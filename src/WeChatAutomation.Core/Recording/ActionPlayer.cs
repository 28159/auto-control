using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Native;

namespace WeChatAutomation.Core.Recording
{
    /// <summary>
    /// 通用回放器
    /// </summary>
    public class ActionPlayer : IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private CancellationTokenSource _cts;
        private bool _isPlaying;
        private IntPtr _targetWindow = IntPtr.Zero;

        public bool IsPlaying => _isPlaying;
        private readonly List<ReadContentResult> _readResults = new();
        public IReadOnlyList<ReadContentResult> ReadResults => _readResults;

        /// <summary>
        /// 设置目标窗口句柄（用于阅读功能）
        /// </summary>
        public void SetTargetWindow(IntPtr hwnd) => _targetWindow = hwnd;
        public event EventHandler<string> LogMessage;
        public event EventHandler PlayCompleted;
        public event EventHandler<string> PlayError;

        public async Task Play(List<RecordedAction> nodes)
        {
            if (_isPlaying) return;
            _isPlaying = true;
            _cts = new CancellationTokenSource();
            OnLog($"开始回放，共 {nodes.Count} 步");

            try
            {
                foreach (var node in nodes)
                {
                    if (_cts.IsCancellationRequested) break;
                    if (!node.IsEnabled) continue;
                    if (node.DelayMs > 0) await Task.Delay(node.DelayMs, _cts.Token);
                    await ExecuteNode(node);
                }
                if (!_cts.IsCancellationRequested) { OnLog("回放完成"); PlayCompleted?.Invoke(this, EventArgs.Empty); }
            }
            catch (OperationCanceledException) { OnLog("回放已取消"); }
            catch (Exception ex) { OnLog($"回放异常: {ex.Message}"); PlayError?.Invoke(this, ex.Message); }
            finally { _isPlaying = false; _cts?.Dispose(); _cts = null; }
        }

        private async Task ExecuteNode(RecordedAction node)
        {
            switch (node.ActionType)
            {
                case ActionType.Click:
                    await DoClickAsync(node);
                    OnLog($"点击 {node.ElementName ?? node.ClassName ?? $"({node.X:F0},{node.Y:F0})"}");
                    break;

                case ActionType.TypeText:
                    await DoTypeTextAsync(node.Parameter);
                    OnLog($"输入 \"{Trunc(node.Parameter, 20)}\"");
                    break;

                case ActionType.SendKeys:
                    await DoSendKeysAsync(node.Parameter);
                    OnLog($"按键 {node.Parameter}");
                    break;

                case ActionType.Wait:
                    if (int.TryParse(node.Parameter, out int ms)) { OnLog($"等待 {ms}ms"); await Task.Delay(ms); }
                    break;

                case ActionType.Copy:
                    await DoSendKeysAsync("Ctrl+C");
                    OnLog("复制");
                    break;

                case ActionType.Paste:
                    await DoSendKeysAsync("Ctrl+V");
                    OnLog("粘贴");
                    break;

                case ActionType.InsertText:
                    await DoTypeTextAsync(node.Parameter);
                    OnLog($"插入 \"{Trunc(node.Parameter, 20)}\"");
                    break;

                case ActionType.Screenshot:
                    DoScreenshot();
                    OnLog("截图");
                    break;

                case ActionType.OpenApp:
                    DoOpenApp(node.Parameter);
                    OnLog($"打开 {node.Parameter}");
                    break;

                case ActionType.WaitForApp:
                    await DoWaitForApp(node.Parameter, node.DelayMs);
                    break;

                case ActionType.ReadContent:
                    DoReadContent();
                    break;

                case ActionType.ScrollRead:
                    await DoScrollReadAsync(node.ScrollAmount);
                    break;

                case ActionType.Scroll:
                    DoScroll(node.ScrollAmount);
                    OnLog($"滚动 {node.ScrollAmount} 行");
                    break;

                case ActionType.InputParam:
                    // 参数步骤 - 只是标记，实际替换由 ScriptExecutor 处理
                    OnLog($"参数 [{node.ParameterName}]: {Trunc(node.Parameter, 20)}");
                    break;

                default:
                    OnLog($"未知操作类型: {node.ActionType}");
                    break;
            }
        }

        private async Task DoClickAsync(RecordedAction node)
        {
            // UIA 定位元素，获取当前位置
            var (found, hwnd, rect) = TryLocateElement(node);

            if (found && !rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
            {
                // 激活目标窗口
                User32.SetForegroundWindow(hwnd);
                await Task.Delay(100);

                // 点击元素中心
                int clickX = (int)(rect.Left + rect.Width / 2);
                int clickY = (int)(rect.Top + rect.Height / 2);
                User32.SetCursorPos(clickX, clickY);
                await Task.Delay(50);
                User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                await Task.Delay(50);
                User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                OnLog($"点击 ({clickX},{clickY}) {node.ElementName ?? node.ClassName ?? ""}");
                return;
            }

            OnLog($"定位失败: {node.ElementName ?? node.ClassName ?? node.AutomationId ?? "未知"}");
        }

        /// <summary>
        /// 通过 UIA 属性定位元素，返回 (是否找到, 窗口句柄, 元素边界框)
        /// </summary>
        private (bool found, IntPtr hwnd, System.Windows.Rect rect) TryLocateElement(RecordedAction node)
        {
            try
            {
                // 1. 定位目标窗口
                IntPtr hwnd = FindTargetWindow(node.WindowTitle);
                if (hwnd == IntPtr.Zero)
                {
                    OnLog($"未找到窗口: \"{node.WindowTitle}\"");
                    return (false, IntPtr.Zero, default);
                }

                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                if (root == null)
                {
                    OnLog("无法获取窗口 UIA 根元素");
                    return (false, hwnd, default);
                }

                // 2. 按优先级逐步放宽条件搜索元素
                System.Windows.Automation.AutomationElement element = null;
                string matchMethod = "";

                // 策略1: AutomationId 精确匹配
                if (element == null && !string.IsNullOrEmpty(node.AutomationId))
                {
                    element = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(
                            System.Windows.Automation.AutomationElement.AutomationIdProperty, node.AutomationId));
                    if (element != null) matchMethod = $"AutomationId={node.AutomationId}";
                }

                // 策略2: Name 精确匹配
                if (element == null && !string.IsNullOrEmpty(node.ElementName))
                {
                    element = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(
                            System.Windows.Automation.AutomationElement.NameProperty, node.ElementName));
                    if (element != null) matchMethod = $"Name={node.ElementName}";
                }

                // 策略3: ClassName + ControlType 组合
                if (element == null && !string.IsNullOrEmpty(node.ClassName) && !string.IsNullOrEmpty(node.ControlType))
                {
                    var ct = System.Windows.Automation.ControlType.LookupById(GetControlTypeId(node.ControlType));
                    if (ct != null)
                    {
                        var andCond = new System.Windows.Automation.AndCondition(
                            new System.Windows.Automation.PropertyCondition(
                                System.Windows.Automation.AutomationElement.ClassNameProperty, node.ClassName),
                            new System.Windows.Automation.PropertyCondition(
                                System.Windows.Automation.AutomationElement.ControlTypeProperty, ct));
                        element = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, andCond);
                        if (element != null) matchMethod = $"ClassName={node.ClassName}+ControlType={node.ControlType}";
                    }
                }

                // 策略4: 单独 ClassName
                if (element == null && !string.IsNullOrEmpty(node.ClassName))
                {
                    element = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(
                            System.Windows.Automation.AutomationElement.ClassNameProperty, node.ClassName));
                    if (element != null) matchMethod = $"ClassName={node.ClassName}";
                }

                if (element == null)
                {
                    OnLog($"UIA 未找到: Name=\"{node.ElementName}\" Class=\"{node.ClassName}\" AutoId=\"{node.AutomationId}\"");
                    return (false, hwnd, default);
                }

                var rect = element.Current.BoundingRectangle;
                OnLog($"定位成功 ({matchMethod}) 坐标({rect.Left:F0},{rect.Top:F0})");
                return (true, hwnd, rect);
            }
            catch (Exception ex)
            {
                OnLog($"UIA 异常: {ex.Message}");
                return (false, IntPtr.Zero, default);
            }
        }

        /// <summary>
        /// 根据窗口标题查找窗口句柄
        /// </summary>
        private IntPtr FindTargetWindow(string? windowTitle)
        {
            // 如果窗口标题为空，返回前台窗口
            if (string.IsNullOrEmpty(windowTitle))
            {
                IntPtr fg = User32.GetForegroundWindow();
                OnLog($"窗口标题为空，使用前台窗口: {fg}");
                return fg;
            }

            IntPtr found = IntPtr.Zero;
            // 保持回调委托引用，防止 GC 回收
            User32.EnumWindowsProc callback = (hwnd, _) =>
            {
                if (!User32.IsWindowVisible(hwnd)) return true;
                int len = User32.GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new System.Text.StringBuilder(len + 1);
                User32.GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            };
            User32.EnumWindows(callback, IntPtr.Zero);

            // 如果精确匹配失败，尝试匹配进程名
            if (found == IntPtr.Zero)
            {
                OnLog($"未找到窗口 \"{windowTitle}\"，尝试按进程名搜索");
                found = FindWindowByProcessName(windowTitle);
            }

            return found;
        }

        /// <summary>
        /// 通过进程名查找窗口
        /// </summary>
        private IntPtr FindWindowByProcessName(string name)
        {
            IntPtr found = IntPtr.Zero;
            User32.EnumWindowsProc callback = (hwnd, _) =>
            {
                if (!User32.IsWindowVisible(hwnd)) return true;
                User32.GetWindowThreadProcessId(hwnd, out int pid);
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (proc.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hwnd;
                        return false;
                    }
                }
                catch { }
                return true;
            };
            User32.EnumWindows(callback, IntPtr.Zero);
            return found;
        }

        private static int GetControlTypeId(string controlType) => controlType switch
        {
            "Button" => 50000,
            "Calendar" => 50001,
            "CheckBox" => 50002,
            "ComboBox" => 50003,
            "Edit" => 50004,
            "Hyperlink" => 50005,
            "Image" => 50006,
            "ListItem" => 50007,
            "List" => 50008,
            "Menu" => 50009,
            "MenuBar" => 50010,
            "MenuItem" => 50011,
            "ProgressBar" => 50012,
            "RadioButton" => 50013,
            "ScrollBar" => 50014,
            "Slider" => 50015,
            "Spinner" => 50016,
            "StatusBar" => 50017,
            "Tab" => 50018,
            "TabItem" => 50019,
            "Text" => 50020,
            "ToolBar" => 50021,
            "ToolTip" => 50022,
            "Tree" => 50023,
            "TreeItem" => 50024,
            "DataGrid" => 50028,
            "DataItem" => 50029,
            "Document" => 50030,
            "SplitButton" => 50031,
            "Window" => 50032,
            "Pane" => 50033,
            "Header" => 50034,
            "HeaderItem" => 50035,
            "Table" => 50036,
            "Thumbnail" => 50050,
            "Graphic" => 50051,
            "StaticText" => 50052,
            "Unknown" => 50053,
            "OutlookBar" => 50054,
            "SemanticZoom" => 50055,
            "AppBar" => 50056,
            _ => 50000 // 默认 Button
        };

        private async Task DoTypeTextAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            UIAutomationHelper.SetClipboardText(text);
            await UIAutomationHelper.SimulateKeyComboAsync(User32.VK_CONTROL, User32.VK_V);
        }

        private async Task DoSendKeysAsync(string keys)
        {
            if (string.IsNullOrEmpty(keys)) return;
            var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries);
            var mods = new List<byte>();
            var keyList = new List<byte>();

            foreach (var p in parts)
            {
                switch (p.Trim().ToLower())
                {
                    case "ctrl": case "control": mods.Add(User32.VK_CONTROL); break;
                    case "shift": mods.Add(User32.VK_SHIFT); break;
                    case "alt": mods.Add(User32.VK_MENU); break;
                    case "enter": case "return": keyList.Add(User32.VK_RETURN); break;
                    case "escape": case "esc": keyList.Add(User32.VK_ESCAPE); break;
                    case "delete": case "del": keyList.Add(User32.VK_DELETE); break;
                    case "backspace": case "bs": keyList.Add(User32.VK_BACK); break;
                    case "tab": keyList.Add(User32.VK_TAB); break;
                    case "space": keyList.Add(User32.VK_SPACE); break;
                    case "home": keyList.Add(User32.VK_HOME); break;
                    case "end": keyList.Add(User32.VK_END); break;
                    case "pageup": keyList.Add(User32.VK_PRIOR); break;
                    case "pagedown": keyList.Add(User32.VK_NEXT); break;
                    case "up": keyList.Add(User32.VK_UP); break;
                    case "down": keyList.Add(User32.VK_DOWN); break;
                    case "left": keyList.Add(User32.VK_LEFT); break;
                    case "right": keyList.Add(User32.VK_RIGHT); break;
                    case "a": keyList.Add(User32.VK_A); break;
                    case "c": keyList.Add(User32.VK_C); break;
                    case "v": keyList.Add(User32.VK_V); break;
                    case "x": keyList.Add(User32.VK_X); break;
                    case "z": keyList.Add(User32.VK_Z); break;
                    case "s": keyList.Add(User32.VK_S); break;
                    default:
                        if (p.Trim().Length == 1) keyList.Add((byte)char.ToUpper(p.Trim()[0]));
                        else if (byte.TryParse(p.Trim(), out byte vk)) keyList.Add(vk);
                        break;
                }
            }

            foreach (var m in mods) User32.keybd_event(m, 0, 0, 0);
            foreach (var k in keyList)
            {
                User32.keybd_event(k, 0, 0, 0);
                await Task.Delay(10);
                User32.keybd_event(k, 0, User32.KEYEVENTF_KEYUP, 0);
                await Task.Delay(10);
            }
            foreach (var m in mods) User32.keybd_event(m, 0, User32.KEYEVENTF_KEYUP, 0);
        }

        private void DoScreenshot()
        {
            try
            {
                var bounds = System.Windows.SystemParameters.WorkArea;
                int w = (int)bounds.Width, h = (int)bounds.Height;
                int x = (int)bounds.Left, y = (int)bounds.Top;
                using var bmp = new System.Drawing.Bitmap(w, h);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "screenshots",
                    $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                OnLog($"截图保存: {path}");
            }
            catch (Exception ex) { OnLog($"截图失败: {ex.Message}"); }
        }

        private void DoOpenApp(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { OnLog($"打开失败: {ex.Message}"); }
        }

        /// <summary>
        /// 循环等待应用打开（每500ms检查一次，直到进程存在或超时）
        /// Parameter: 进程名（如 notepad, chrome）
        /// DelayMs: 超时时间（毫秒）
        /// </summary>
        private async Task DoWaitForApp(string processName, int timeoutMs)
        {
            if (string.IsNullOrEmpty(processName)) return;
            if (timeoutMs <= 0) timeoutMs = 30000; // 默认30秒超时

            string name = System.IO.Path.GetFileNameWithoutExtension(processName).ToLower();
            OnLog($"等待应用 \"{name}\" 启动 (超时 {timeoutMs / 1000}s)");

            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                if (_cts.IsCancellationRequested) return;

                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    OnLog($"应用 \"{name}\" 已启动 (PID: {procs[0].Id})");
                    return;
                }

                await Task.Delay(500, _cts.Token);
                elapsed += 500;
            }

            OnLog($"等待超时: 应用 \"{name}\" 未在 {timeoutMs / 1000}s 内启动");
        }

        /// <summary>
        /// 读取指定窗口的 UIA 文本内容
        /// </summary>
        private void DoReadContent()
        {
            try
            {
                IntPtr hwnd = _targetWindow;
                if (hwnd == IntPtr.Zero) hwnd = User32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) { OnLog("无法获取目标窗口"); return; }

                int len = User32.GetWindowTextLength(hwnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    User32.GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                var element = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                string content = ExtractAllText(element, 0, 5);

                var result = new ReadContentResult
                {
                    WindowTitle = title,
                    Content = content,
                    CapturedAt = DateTime.Now,
                    Source = "ReadContent"
                };
                _readResults.Add(result);

                OnLog($"已读取窗口: {title} ({content.Length} 字符)");
            }
            catch (Exception ex) { OnLog($"读取失败: {ex.Message}"); }
        }

        /// <summary>
        /// 滚动并阅读窗口内容
        /// </summary>
        private async Task DoScrollReadAsync(int scrollLines)
        {
            try
            {
                IntPtr hwnd = _targetWindow;
                if (hwnd == IntPtr.Zero) hwnd = User32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) { OnLog("无法获取目标窗口"); return; }

                int len = User32.GetWindowTextLength(hwnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    User32.GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                // 滚动
                User32.mouse_event(0x0800, 0, 0, scrollLines * -120, 0);
                await Task.Delay(500); // 等待滚动完成

                // 读取内容
                var element = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                string content = ExtractAllText(element, 0, 5);

                var result = new ReadContentResult
                {
                    WindowTitle = title,
                    Content = content,
                    CapturedAt = DateTime.Now,
                    Source = "ScrollRead"
                };
                _readResults.Add(result);

                OnLog($"滚动阅读: {title} ({content.Length} 字符)");
            }
            catch (Exception ex) { OnLog($"滚动阅读失败: {ex.Message}"); }
        }

        /// <summary>
        /// 递归提取 UIA 元素的所有文本
        /// </summary>
        private string ExtractAllText(System.Windows.Automation.AutomationElement element, int depth, int maxDepth)
        {
            if (depth > maxDepth || element == null) return "";

            var texts = new List<string>();
            try
            {
                // 获取当前元素的名称（通常是文本内容）
                string name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    texts.Add(name);

                // 获取 ValuePattern 的值
                if (element.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out object patternObj))
                {
                    var vp = (System.Windows.Automation.ValuePattern)patternObj;
                    string val = vp.Current.Value;
                    if (!string.IsNullOrWhiteSpace(val) && val != name)
                        texts.Add(val);
                }

                // 递归子元素
                var children = element.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                foreach (System.Windows.Automation.AutomationElement child in children)
                {
                    string childText = ExtractAllText(child, depth + 1, maxDepth);
                    if (!string.IsNullOrWhiteSpace(childText))
                        texts.Add(childText);
                }
            }
            catch (Exception ex) { _logger.Warn("Player", $"UIA 遍历异常: {ex.Message}"); }

            return string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        private void DoScroll(int lines)
        {
            User32.mouse_event(0x0800, 0, 0, lines * -120, 0); // MOUSEEVENTF_WHEEL
        }

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

        public void Stop() => _cts?.Cancel();
        private void OnLog(string msg) { _logger.Info("Player", msg); LogMessage?.Invoke(this, msg); }
        public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); }
    }

    internal static class UIAutomationHelper
    {
        public static void SetClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            User32.OpenClipboard(IntPtr.Zero);
            try
            {
                User32.EmptyClipboard();
                int bytes = (text.Length + 1) * 2; // UTF-16: 2 bytes per char + null terminator
                IntPtr h = User32.GlobalAlloc(User32.GMEM_MOVEABLE, bytes);
                if (h == IntPtr.Zero) return;
                IntPtr locked = User32.GlobalLock(h);
                if (locked == IntPtr.Zero) { User32.GlobalUnlock(h); return; }
                System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                // Null-terminate
                System.Runtime.InteropServices.Marshal.WriteInt16(locked + text.Length * 2, 0);
                User32.GlobalUnlock(h);
                User32.SetClipboardData(User32.CF_UNICODETEXT, h);
            }
            finally
            {
                User32.CloseClipboard();
            }
        }

        public static async System.Threading.Tasks.Task SimulateKeyComboAsync(byte modifier, byte key)
        {
            User32.keybd_event(modifier, 0, 0, 0);
            await System.Threading.Tasks.Task.Delay(10);
            User32.keybd_event(key, 0, 0, 0);
            await System.Threading.Tasks.Task.Delay(10);
            User32.keybd_event(key, 0, User32.KEYEVENTF_KEYUP, 0);
            await System.Threading.Tasks.Task.Delay(10);
            User32.keybd_event(modifier, 0, User32.KEYEVENTF_KEYUP, 0);
        }
    }
}
