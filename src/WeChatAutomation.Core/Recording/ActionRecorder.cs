using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Native;
using WeChatAutomation.Core.Vision;

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

        // 训练数据采集
        private bool _captureTrainingData;
        private string _capturesDir;
        private int _captureIndex;
        private static readonly Dictionary<string, int> ControlTypeToClassId = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Button"] = 0,
            ["Edit"] = 1,
            ["CheckBox"] = 2,
            ["RadioButton"] = 3,
            ["ComboBox"] = 4,
            ["TabItem"] = 5,
            ["MenuItem"] = 6,
            ["Image"] = 7,
            ["Hyperlink"] = 8,
            ["Text"] = 9,
            ["ToolBar"] = 10,
        };

        private static readonly string[] YoloClassNames = new[]
        {
            "button", "input", "checkbox", "radio", "dropdown",
            "tab", "menu_item", "icon", "link", "text_field",
            "search_box", "send_button", "close_button", "minimize_button",
            "maximize_button", "scrollbar", "slider", "toggle", "tooltip", "image"
        };

        /// <summary>
        /// 扩展的 ControlType → VisionLabel 映射表
        /// </summary>
        private static readonly Dictionary<string, string> ExtendedControlTypeMapping = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Button"] = "button",
            ["SplitButton"] = "button",
            ["Edit"] = "input",
            ["Text"] = "text_field",
            ["CheckBox"] = "checkbox",
            ["RadioButton"] = "radio",
            ["ComboBox"] = "dropdown",
            ["TabItem"] = "tab",
            ["Tab"] = "tab",
            ["MenuItem"] = "menu_item",
            ["Menu"] = "menu_item",
            ["MenuBar"] = "menu_item",
            ["Hyperlink"] = "link",
            ["Image"] = "image",
            ["ToolBar"] = "search_box",
            ["ScrollBar"] = "scrollbar",
            ["Slider"] = "slider",
            ["ToolTip"] = "tooltip",
            ["ListItem"] = "button",
            ["DataItem"] = "button",
            ["TreeItem"] = "button",
            ["DataGrid"] = "input",
            ["Document"] = "text_field",
            ["Spinner"] = "input",
            ["StatusBar"] = "text_field",
            ["Header"] = "text_field",
            ["HeaderItem"] = "button",
            ["Table"] = "text_field",
            ["Pane"] = "text_field",
            ["Window"] = "text_field",
        };

        private static string ControlTypeToVisionLabel(string controlType)
        {
            if (string.IsNullOrEmpty(controlType)) return "button";

            // 1. 精确映射
            if (ExtendedControlTypeMapping.TryGetValue(controlType, out string label))
                return label;

            // 2. 原有映射表（兼容旧逻辑）
            if (ControlTypeToClassId.TryGetValue(controlType, out int id) && id < YoloClassNames.Length)
                return YoloClassNames[id];

            // 3. 模糊匹配
            string lower = controlType.ToLower();
            if (lower.Contains("button") || lower.Contains("btn")) return "button";
            if (lower.Contains("edit") || lower.Contains("input") || lower.Contains("text")) return "input";
            if (lower.Contains("check")) return "checkbox";
            if (lower.Contains("radio")) return "radio";
            if (lower.Contains("combo") || lower.Contains("dropdown") || lower.Contains("select")) return "dropdown";
            if (lower.Contains("tab")) return "tab";
            if (lower.Contains("menu")) return "menu_item";
            if (lower.Contains("link") || lower.Contains("hyper")) return "link";
            if (lower.Contains("image") || lower.Contains("picture")) return "image";
            if (lower.Contains("scroll")) return "scrollbar";
            if (lower.Contains("slide")) return "slider";
            if (lower.Contains("toggle")) return "toggle";
            if (lower.Contains("tip")) return "tooltip";
            if (lower.Contains("search")) return "search_box";

            // 4. 最终回退：记录警告
            _logger.Warn("Recorder", $"未识别的 ControlType '{controlType}'，回退到 'button'");
            return "button";
        }

        public bool IsRecording => _isRecording;
        public RecordMode Mode => _mode;
        public IReadOnlyList<RecordedAction> Nodes => _nodes.AsReadOnly();
        public ClickMode CurrentClickMode { get; set; } = ClickMode.Coordinate;
        public bool CaptureTrainingData
        {
            get => _captureTrainingData;
            set => _captureTrainingData = value;
        }
        public string CapturesDir => _capturesDir;

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
            _captureIndex = 0;

            if (_captureTrainingData)
            {
                _capturesDir = Path.Combine(
                    AppPaths.CapturesDir,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(_capturesDir);
                OnLog($"训练数据采集目录: {_capturesDir}");
            }

            _mouseHook.StartCapture();
            _keyHook.StartCapture();

            OnLog($"开始录制 (F9停止){(_captureTrainingData ? " [训练数据采集开启]" : "")}");
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
            string visionLabel = ControlTypeToVisionLabel(info.ControlType);
            string name = CurrentClickMode == ClickMode.Coordinate
                ? $"点击坐标({info.X},{info.Y})"
                : CurrentClickMode == ClickMode.Vision
                    ? $"视觉点击 {visionLabel}"
                    : !string.IsNullOrEmpty(info.XPath)
                        ? $"路径点击 {(info.XPath.Length > 40 ? info.XPath[..40] + "..." : info.XPath)}"
                        : $"点击路径 {desc}";

            _nodes.Add(new RecordedAction
            {
                Order = _nodes.Count + 1,
                ActionType = ActionType.Click,
                Name = name,
                ClassName = info.ClassName,
                ElementName = info.ElementName,
                AutomationId = info.AutomationId,
                ControlType = info.ControlType,
                WindowTitle = info.WindowTitle,
                X = info.X,
                Y = info.Y,
                DelayMs = delay,
                ClickMode = CurrentClickMode,
                VisionLabel = CurrentClickMode == ClickMode.Vision ? visionLabel : null,
                TemplateImage = CurrentClickMode == ClickMode.Vision ? SaveVisionTemplate(info, visionLabel) : null,
                XPath = info.XPath,
                SiblingIndex = info.SiblingIndex,
                RuntimeId = info.RuntimeId
            });

            if (_captureTrainingData && info.WindowHandle != IntPtr.Zero)
            {
                SaveTrainingCapture(info);
            }

            OnLog($"录制点击 #{_nodes.Count}: {desc} ({CurrentClickMode})" +
                  (!string.IsNullOrEmpty(info.XPath) ? $" XPath={info.XPath}" : ""));
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

        /// <summary>
        /// 在指定 nodeId 之后插入动作；若 nodeId 为 null 或找不到则追加到末尾。
        /// </summary>
        public void InsertAfter(string? afterNodeId, RecordedAction node)
        {
            if (_isRecording) FlushTextBuffer();

            int idx = -1;
            if (!string.IsNullOrEmpty(afterNodeId))
            {
                var target = _nodes.Find(n => n.NodeId == afterNodeId);
                if (target != null) idx = _nodes.IndexOf(target);
            }

            if (idx < 0)
            {
                node.Order = _nodes.Count + 1;
                _nodes.Add(node);
            }
            else
            {
                _nodes.Insert(idx + 1, node);
                for (int i = 0; i < _nodes.Count; i++) _nodes[i].Order = i + 1;
            }
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

        public int RemoveNodes(IEnumerable<string> nodeIds)
        {
            var idSet = new HashSet<string>(nodeIds);
            var removed = _nodes.RemoveAll(n => idSet.Contains(n.NodeId));
            if (removed > 0)
                for (int i = 0; i < _nodes.Count; i++) _nodes[i].Order = i + 1;
            return removed;
        }

        public void ClearNodes()
        {
            _nodes.Clear();
        }

        public void MoveNode(string nodeId, int newOrder)
        {
            var node = _nodes.Find(n => n.NodeId == nodeId);
            if (node == null) return;
            int old = _nodes.IndexOf(node);
            int dest = Math.Clamp(newOrder - 1, 0, _nodes.Count - 1);
            _nodes.RemoveAt(old);
            _nodes.Insert(dest, node);
            RenumberOrders();
        }

        /// <summary>
        /// 重新编号顶层步骤 Order（1..N）。嵌套步骤的 Order 不参与显示/跳转，不重排。
        /// </summary>
        private void RenumberOrders()
        {
            for (int i = 0; i < _nodes.Count; i++) _nodes[i].Order = i + 1;
        }

        /// <summary>
        /// 递归查找 nodeId 所在位置（覆盖任意嵌套深度）。
        /// 返回节点、所在列表、父容器（顶层为 null）、是否在 True 分支。
        /// </summary>
        public NodeLocation? FindNode(string nodeId) => FindNodeRecursive(_nodes, nodeId, null);

        private static NodeLocation? FindNodeRecursive(List<RecordedAction> list, string nodeId, RecordedAction? parent)
        {
            foreach (var a in list)
            {
                if (a.NodeId == nodeId)
                    return new NodeLocation(a, list, parent, parent != null && ReferenceEquals(list, parent.TrueActions));
            }
            foreach (var a in list)
            {
                if (a.TrueActions != null)
                {
                    var r = FindNodeRecursive(a.TrueActions, nodeId, a);
                    if (r != null) return r;
                }
                if (a.FalseActions != null)
                {
                    var r = FindNodeRecursive(a.FalseActions, nodeId, a);
                    if (r != null) return r;
                }
            }
            return null;
        }

        /// <summary>
        /// 通用移动：把 draggedId 从原位置移除，插入 targetList 的 targetIndex。
        /// targetList 为 null 表示移到顶层 _nodes。同列表且移除点在目标前则 targetIndex 前移一位。最后重排顶层 Order。
        /// </summary>
        public bool MoveNodeTo(string draggedId, List<RecordedAction>? targetList, int targetIndex)
        {
            var loc = FindNode(draggedId);
            if (loc == null) return false;
            var dragged = loc.Node;
            var srcList = loc.List;
            var destList = targetList ?? _nodes;

            int oldIndex = srcList.IndexOf(dragged);
            if (oldIndex < 0) return false;
            srcList.RemoveAt(oldIndex);

            if (ReferenceEquals(srcList, destList) && oldIndex < targetIndex)
                targetIndex--;

            targetIndex = Math.Clamp(targetIndex, 0, destList.Count);
            destList.Insert(targetIndex, dragged);
            RenumberOrders();
            return true;
        }

        /// <summary>
        /// 收集 container 及其所有后代（任意深度）的 NodeId，用于环检测。
        /// </summary>
        public static HashSet<string> CollectSubtreeIds(RecordedAction container)
        {
            var set = new HashSet<string>();
            CollectSubtreeIdsRecursive(container, set);
            return set;
        }

        private static void CollectSubtreeIdsRecursive(RecordedAction node, HashSet<string> set)
        {
            set.Add(node.NodeId);
            if (node.TrueActions != null)
                foreach (var a in node.TrueActions) CollectSubtreeIdsRecursive(a, set);
            if (node.FalseActions != null)
                foreach (var a in node.FalseActions) CollectSubtreeIdsRecursive(a, set);
        }

        /// <summary>节点定位结果：节点、所在列表、父容器、是否 True 分支。</summary>
        public sealed class NodeLocation
        {
            public RecordedAction Node { get; }
            public List<RecordedAction> List { get; }
            public RecordedAction? Parent { get; }
            public bool InTrueBranch { get; }
            public NodeLocation(RecordedAction node, List<RecordedAction> list, RecordedAction? parent, bool inTrueBranch)
            { Node = node; List = list; Parent = parent; InTrueBranch = inTrueBranch; }
        }

        // ── 训练数据采集 ──

        /// <summary>
        /// 视觉模式：截取点击位置周围的窗口局部区域作为模板图片，保存到模板目录。
        /// 返回模板文件绝对路径，供回放时模板匹配使用。
        /// </summary>
        private string SaveVisionTemplate(MouseClickInfo info, string label)
        {
            try
            {
                if (info.WindowHandle == IntPtr.Zero) return null;

                IntPtr topLevelHwnd = User32.GetAncestor(info.WindowHandle, User32.GA_ROOT);
                if (topLevelHwnd == IntPtr.Zero) topLevelHwnd = info.WindowHandle;

                User32.GetWindowRect(topLevelHwnd, out RECT winRect);
                if (winRect.Width <= 0 || winRect.Height <= 0) return null;

                using var screenshot = WindowCapturer.CaptureWindow(topLevelHwnd);
                if (screenshot == null) return null;

                // 点击点在窗口局部坐标
                int localX = info.X - winRect.Left;
                int localY = info.Y - winRect.Top;

                // 模板尺寸：基于元素类型估算，最小 32x32，不超过窗口
                int w = EstimateElementWidth(info);
                int h = EstimateElementHeight(info);
                w = Math.Clamp(w, 32, winRect.Width);
                h = Math.Clamp(h, 32, winRect.Height);

                // 以点击点为中心截取，边界裁剪到窗口内
                int tx = localX - w / 2;
                int ty = localY - h / 2;
                if (tx < 0) tx = 0;
                if (ty < 0) ty = 0;
                if (tx + w > screenshot.Width) w = screenshot.Width - tx;
                if (ty + h > screenshot.Height) h = screenshot.Height - ty;
                if (w < 16 || h < 16) return null;

                using var template = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(template))
                {
                    g.DrawImage(screenshot, new Rectangle(0, 0, w, h),
                        new Rectangle(tx, ty, w, h), GraphicsUnit.Pixel);
                }

                Directory.CreateDirectory(AppPaths.TemplatesDir);
                string fileName = $"{label ?? "tpl"}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{info.X}_{info.Y}.png";
                string path = Path.Combine(AppPaths.TemplatesDir, fileName);
                template.Save(path, ImageFormat.Png);

                OnLog($"视觉模板已截取: {fileName} ({w}x{h})");
                return path;
            }
            catch (Exception ex)
            {
                _logger.Warn("Recorder", $"视觉模板截取失败: {ex.Message}");
                return null;
            }
        }

        private void SaveTrainingCapture(MouseClickInfo info)
        {
            try
            {
                IntPtr topLevelHwnd = User32.GetAncestor(info.WindowHandle, User32.GA_ROOT);
                if (topLevelHwnd == IntPtr.Zero) topLevelHwnd = info.WindowHandle;

                User32.GetWindowRect(topLevelHwnd, out RECT winRect);
                if (winRect.Width <= 0 || winRect.Height <= 0) return;

                using var screenshot = WindowCapturer.CaptureWindow(topLevelHwnd);
                if (screenshot == null) return;

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string imgPath = Path.Combine(_capturesDir, $"{timestamp}.png");
                screenshot.Save(imgPath, ImageFormat.Png);

                int classId = ResolveClassId(info);
                if (classId < 0) return;

                double cx = (double)(info.X - winRect.Left) / winRect.Width;
                double cy = (double)(info.Y - winRect.Top) / winRect.Height;

                double estW = EstimateElementWidth(info) / (double)winRect.Width;
                double estH = EstimateElementHeight(info) / (double)winRect.Height;

                cx = Math.Clamp(cx, 0, 1);
                cy = Math.Clamp(cy, 0, 1);
                estW = Math.Clamp(estW, 0.01, 1);
                estH = Math.Clamp(estH, 0.01, 1);

                string labelPath = Path.Combine(_capturesDir, $"{timestamp}.txt");
                string labelLine = $"{classId} {cx:F6} {cy:F6} {estW:F6} {estH:F6}";
                File.WriteAllText(labelPath, labelLine);

                _captureIndex++;
                OnLog($"训练数据 #{_captureIndex}: class={classId} ({info.ControlType ?? "unknown"}) @ ({cx:F3},{cy:F3})");
            }
            catch (Exception ex)
            {
                _logger.Warn("Recorder", $"训练数据采集失败: {ex.Message}");
            }
        }

        private static int ResolveClassId(MouseClickInfo info)
        {
            if (!string.IsNullOrEmpty(info.ControlType) &&
                ControlTypeToClassId.TryGetValue(info.ControlType, out int id))
            {
                return id;
            }

            if (!string.IsNullOrEmpty(info.ClassName))
            {
                string cn = info.ClassName.ToLower();
                if (cn.Contains("button") || cn.Contains("btn")) return 0;
                if (cn.Contains("edit") || cn.Contains("input") || cn.Contains("text")) return 1;
                if (cn.Contains("check")) return 2;
                if (cn.Contains("radio")) return 3;
                if (cn.Contains("combo") || cn.Contains("dropdown") || cn.Contains("select")) return 4;
                if (cn.Contains("tab")) return 5;
                if (cn.Contains("menu")) return 6;
                if (cn.Contains("image") || cn.Contains("picture") || cn.Contains("icon")) return 7;
                if (cn.Contains("link") || cn.Contains("hyperlink")) return 8;
            }

            if (!string.IsNullOrEmpty(info.ElementName))
            {
                string en = info.ElementName.ToLower();
                if (en.Contains("发送") || en.Contains("send")) return 11;
                if (en.Contains("关闭") || en.Contains("close")) return 12;
                if (en.Contains("最小化") || en.Contains("minimize")) return 13;
                if (en.Contains("最大化") || en.Contains("maximize")) return 14;
                if (en.Contains("搜索") || en.Contains("search")) return 10;
            }

            return 0;
        }

        private static int EstimateElementWidth(MouseClickInfo info)
        {
            if (!string.IsNullOrEmpty(info.ControlType))
            {
                return info.ControlType switch
                {
                    "Button" => 80,
                    "Edit" => 150,
                    "CheckBox" => 16,
                    "RadioButton" => 16,
                    "ComboBox" => 120,
                    "TabItem" => 80,
                    "MenuItem" => 100,
                    "Image" => 32,
                    "Hyperlink" => 60,
                    "Text" => 100,
                    "ToolBar" => 200,
                    _ => 40
                };
            }
            return 40;
        }

        private static int EstimateElementHeight(MouseClickInfo info)
        {
            if (!string.IsNullOrEmpty(info.ControlType))
            {
                return info.ControlType switch
                {
                    "Button" => 28,
                    "Edit" => 24,
                    "CheckBox" => 16,
                    "RadioButton" => 16,
                    "ComboBox" => 24,
                    "TabItem" => 28,
                    "MenuItem" => 24,
                    "Image" => 32,
                    "Hyperlink" => 20,
                    "Text" => 20,
                    "ToolBar" => 28,
                    _ => 24
                };
            }
            return 24;
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
