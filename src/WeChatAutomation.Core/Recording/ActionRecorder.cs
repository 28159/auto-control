using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Native;

namespace WeChatAutomation.Core.Recording
{
    public enum RecordMode
    {
        Continuous,
        StepByStep
    }

    /// <summary>
    /// 通用录制器 - 鼠标点击 + 键盘输入
    /// </summary>
    public class ActionRecorder : IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;

        private readonly List<RecordedAction> _nodes = new();
        private readonly MouseHook _mouseHook = new();
        private readonly KeyboardHook _keyHook = new();
        private readonly Stopwatch _sw = new();
        private bool _isRecording;
        private RecordMode _mode;

        // 暂存
        private MouseClickInfo _pendingClick;
        private readonly StringBuilder _textBuffer = new(); // 累积文本输入
        private DateTime _lastInputTime = DateTime.MinValue;
        private const int TextFlushDelayMs = 500; // 超过500ms无输入则flush

        // 防抖
        private DateTime _lastClickTime = DateTime.MinValue;

        // 排除自身进程
        private readonly int _selfPid;

        public bool IsRecording => _isRecording;
        public RecordMode Mode => _mode;
        public IReadOnlyList<RecordedAction> Nodes => _nodes.AsReadOnly();

        public event EventHandler<RecordedAction> NodeRecorded;
        public event EventHandler RecordingStarted;
        public event EventHandler RecordingStopped;
        public event EventHandler<string> LogMessage;

        public ActionRecorder()
        {
            _selfPid = Process.GetCurrentProcess().Id;
            _mouseHook.ClickCaptured += OnMouseClick;
            _keyHook.KeyRecorded += OnKeyRecorded;
        }

        public void Start(RecordMode mode)
        {
            if (_isRecording) return;

            _mode = mode;
            _isRecording = true;
            _pendingClick = null;
            _textBuffer.Clear();
            _sw.Restart();
            _lastClickTime = DateTime.MinValue;
            _lastInputTime = DateTime.MinValue;

            _mouseHook.StartCapture();
            _keyHook.StartCapture();

            OnLog($"开始录制 (F9停止)");
            RecordingStarted?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            if (!_isRecording) return;

            FlushTextBuffer(); // flush剩余文本
            _mouseHook.StopCapture();
            _keyHook.StopCapture();
            _isRecording = false;
            _pendingClick = null;
            _sw.Stop();

            OnLog($"录制结束，共 {_nodes.Count} 步");
            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// StepByStep 模式：确认当前操作
        /// </summary>
        public void ConfirmStep()
        {
            if (!_isRecording || _mode != RecordMode.StepByStep) return;

            // flush文本
            FlushTextBuffer();

            // 确认待定的点击
            if (_pendingClick != null)
            {
                RecordClickNode(_pendingClick);
                _pendingClick = null;
            }
        }

        // ── 鼠标点击回调 ──

        private void OnMouseClick(object sender, MouseClickInfo info)
        {
            if (!_isRecording) return;

            // 排除自身窗口
            if (info.WindowHandle != IntPtr.Zero && IsSelfWindow(info.WindowHandle))
                return;

            // 防抖
            var now = DateTime.Now;
            if ((now - _lastClickTime).TotalMilliseconds < 300) return;
            _lastClickTime = now;

            // flush文本
            FlushTextBuffer();

            if (_mode == RecordMode.Continuous)
            {
                RecordClickNode(info);
            }
            else
            {
                _pendingClick = info;
                string desc = info.ElementName ?? info.ClassName ?? $"({info.X},{info.Y})";
                OnLog($"捕获点击: {desc} → F10确认");
            }
        }

        // ── 键盘回调 ──

        private void OnKeyRecorded(object sender, KeyInfo key)
        {
            if (!_isRecording) return;

            // 可打印字符：累积到文本缓冲区
            if (key.IsText)
            {
                _textBuffer.Append(key.Text);
                _lastInputTime = DateTime.Now;
                return;
            }

            // 特殊键/组合键：先flush文本，再记录按键
            FlushTextBuffer();

            // 记录特殊按键
            string keyName = FormatKeyName(key);
            if (string.IsNullOrEmpty(keyName)) return;

            int delay = _nodes.Count > 0 ? (int)_sw.ElapsedMilliseconds : 0;
            _sw.Restart();

            _nodes.Add(new RecordedAction
            {
                Order = _nodes.Count + 1,
                ActionType = ActionType.SendKeys,
                Parameter = keyName,
                Name = keyName,
                DelayMs = delay
            });

            OnLog($"录制按键: {keyName}");
            NodeRecorded?.Invoke(this, _nodes[^1]);
        }

        private string FormatKeyName(KeyInfo key)
        {
            var parts = new List<string>();
            if (key.IsCtrl) parts.Add("Ctrl");
            if (key.IsAlt) parts.Add("Alt");
            if (key.IsShift) parts.Add("Shift");

            string main = key.KeyName;
            // 跳过纯修饰键
            if (main is "Ctrl" or "Alt" or "Shift")
                return null;

            parts.Add(main);
            return string.Join("+", parts);
        }

        // ── 文本缓冲区 ──

        private void FlushTextBuffer()
        {
            if (_textBuffer.Length == 0) return;

            string text = _textBuffer.ToString();
            _textBuffer.Clear();

            int delay = _nodes.Count > 0 ? (int)_sw.ElapsedMilliseconds : 0;
            _sw.Restart();

            string name = text.Length > 20 ? text[..20] + "..." : text;
            _nodes.Add(new RecordedAction
            {
                Order = _nodes.Count + 1,
                ActionType = ActionType.TypeText,
                Parameter = text,
                Name = $"输入: {name}",
                DelayMs = delay
            });

            OnLog($"录制输入: {name}");
            NodeRecorded?.Invoke(this, _nodes[^1]);
        }

        // ── 记录点击节点 ──

        private void RecordClickNode(MouseClickInfo info)
        {
            int delay = _nodes.Count > 0 ? (int)_sw.ElapsedMilliseconds : 0;
            _sw.Restart();

            string desc = info.ElementName ?? info.ClassName ?? "";

            _nodes.Add(new RecordedAction
            {
                Order = _nodes.Count + 1,
                ActionType = ActionType.Click,
                Name = $"点击 {desc}",
                ClassName = info.ClassName,
                ElementName = info.ElementName,
                AutomationId = info.AutomationId,
                ControlType = info.ControlType,
                WindowTitle = info.WindowTitle,
                X = info.X,
                Y = info.Y,
                DelayMs = delay
            });

            OnLog($"录制点击 #{_nodes.Count}: {desc}");
            NodeRecorded?.Invoke(this, _nodes[^1]);
        }

        // ── 手动添加 ──

        public void AddManual(ActionType type, string parameter = "", string name = "", int delayMs = -1)
        {
            if (_isRecording) FlushTextBuffer();
            int delay = delayMs >= 0 ? delayMs : 0;

            var node = new RecordedAction
            {
                Order = _nodes.Count + 1,
                ActionType = type,
                Name = name,
                Parameter = parameter,
                ParameterName = type == ActionType.InputParam ? parameter : null,
                DelayMs = delay
            };

            _nodes.Add(node);
            OnLog($"手动添加 #{node.Order}: {node.Summary}");
            NodeRecorded?.Invoke(this, node);
        }

        public void AddManual(RecordedAction node)
        {
            if (_isRecording) FlushTextBuffer();

            node.Order = _nodes.Count + 1;
            _nodes.Add(node);
            OnLog($"手动添加 #{node.Order}: {node.Summary}");
            NodeRecorded?.Invoke(this, node);
        }

        // ── 节点操作 ──

        public bool RemoveNode(string nodeId)
        {
            var removed = _nodes.RemoveAll(n => n.NodeId == nodeId);
            if (removed > 0)
                for (int i = 0; i < _nodes.Count; i++) _nodes[i].Order = i + 1;
            return removed > 0;
        }

        public void MoveNode(string nodeId, int newOrder)
        {
            var node = _nodes.Find(n => n.NodeId == nodeId);
            if (node == null) return;
            int old = _nodes.IndexOf(node);
            int dest = Math.Clamp(newOrder - 1, 0, _nodes.Count - 1);
            _nodes.RemoveAt(old);
            _nodes.Insert(dest, node);
            for (int i = 0; i < _nodes.Count; i++) _nodes[i].Order = i + 1;
        }

        // ── 自身窗口排除 ──

        private bool IsSelfWindow(IntPtr hwnd)
        {
            try
            {
                User32.GetWindowThreadProcessId(hwnd, out int pid);
                return pid == _selfPid;
            }
            catch { return false; }
        }

        // ── 保存/加载 ──

        public void SaveToFile(string path, string name = null)
        {
            FlushTextBuffer();
            var rec = new RecordingFile
            {
                Name = name ?? Path.GetFileNameWithoutExtension(path),
                CreatedAt = DateTime.Now,
                Actions = new List<RecordedAction>(_nodes)
            };
            var opt = new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(path, JsonSerializer.Serialize(rec, opt));
            OnLog($"已保存: {path}");
        }

        public static RecordingFile LoadFromFile(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"文件不存在: {path}");
            var opt = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            return JsonSerializer.Deserialize<RecordingFile>(File.ReadAllText(path), opt);
        }

        private void OnLog(string msg)
        {
            _logger.Info("Recorder", msg);
            LogMessage?.Invoke(this, msg);
        }

        public void Dispose()
        {
            _mouseHook?.Dispose();
            _keyHook?.Dispose();
        }
    }
}
