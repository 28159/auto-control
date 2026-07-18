using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Native;
using WeChatAutomation.Core.Vision;

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
        private readonly HumanInputSimulator _simulator = new();
        private VisionDetector _visionDetector;

        public bool IsPlaying => _isPlaying;
        private readonly List<ReadContentResult> _readResults = new();
        public IReadOnlyList<ReadContentResult> ReadResults => _readResults;

        /// <summary>
        /// 设置目标窗口句柄（用于阅读功能）
        /// </summary>
        public void SetTargetWindow(IntPtr hwnd) => _targetWindow = hwnd;

        public bool InitVisionDetector(string modelPath, string labelsPath = null)
        {
            _visionDetector?.Dispose();
            _visionDetector = new VisionDetector();
            return _visionDetector.LoadModel(modelPath, labelsPath);
        }

        public bool IsVisionReady => _visionDetector?.IsLoaded == true;

        /// <summary>当前视觉检测器（UI 可读取模型元数据；勿 Dispose）。</summary>
        public Vision.VisionDetector VisionDetector => _visionDetector;

        /// <summary>
        /// 当前已加载模型的文件名（便于判断是否需要切换）。
        /// </summary>
        public string LoadedModelFileName =>
            _visionDetector?.IsLoaded == true && !string.IsNullOrEmpty(_visionDetector.ModelPath)
                ? System.IO.Path.GetFileName(_visionDetector.ModelPath)
                : null;

        /// <summary>
        /// 按文件名（位于运行目录 models/ 下）确保视觉模型已加载；若已是该模型则跳过。
        /// modelFileName 为空或不存在时回退到默认 yolov8n-ui.onnx。
        /// 返回是否就绪。
        /// </summary>
        public bool EnsureVisionModel(string modelFileName)
        {
            if (!string.IsNullOrEmpty(LoadedModelFileName) &&
                string.Equals(LoadedModelFileName, modelFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true; // 已是同一模型，复用
            }

            string modelsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
            string fileName = !string.IsNullOrWhiteSpace(modelFileName) ? modelFileName : "yolov8n-ui.onnx";
            string modelPath = System.IO.Path.Combine(modelsDir, fileName);

            if (System.IO.File.Exists(modelPath))
            {
                bool ok = InitVisionDetector(modelPath);
                OnLog(ok ? $"视觉模型已加载: {fileName}" : $"视觉模型加载失败: {modelPath}");
                return ok;
            }

            OnLog($"视觉模型文件不存在: {modelPath}");
            return false;
        }
        public event EventHandler<string> LogMessage;
        public event EventHandler PlayCompleted;
        public event EventHandler<string> PlayError;

        /// <summary>
        /// 当前回放脚本绑定的视觉模型文件名（来自 RecordingFile.VisionModel）。
        /// 为空时 EnsureVisionModel 回退到默认 yolov8n-ui.onnx。
        /// </summary>
        private string _currentVisionModel;

        public Task Play(List<RecordedAction> nodes) => Play(nodes, null);

        public async Task Play(List<RecordedAction> nodes, string visionModelFileName)
        {
            if (_isPlaying) return;
            _isPlaying = true;
            _currentVisionModel = visionModelFileName;
            _cts = new CancellationTokenSource();
            _readResults.Clear();
            _variables.Clear();
            OnLog($"开始回放，共 {nodes.Count} 步" +
                  (string.IsNullOrEmpty(visionModelFileName) ? "" : $"（视觉模型: {visionModelFileName}）"));

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
            var resolvedNode = ResolveVariables(node);

            switch (resolvedNode.ActionType)
            {
                case ActionType.Click:
                    await DoClickAsync(resolvedNode);
                    OnLog($"点击 {resolvedNode.ElementName ?? resolvedNode.ClassName ?? $"({resolvedNode.X:F0},{resolvedNode.Y:F0})"}");
                    break;

                case ActionType.TypeText:
                    await DoTypeTextAsync(resolvedNode.Parameter);
                    OnLog($"输入 \"{Trunc(resolvedNode.Parameter, 20)}\"");
                    break;

                case ActionType.SendKeys:
                    await DoSendKeysAsync(resolvedNode.Parameter);
                    OnLog($"按键 {resolvedNode.Parameter}");
                    break;

                case ActionType.Wait:
                    if (int.TryParse(resolvedNode.Parameter, out int ms)) { OnLog($"等待 {ms}ms"); await Task.Delay(ms); }
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
                    await DoTypeTextAsync(resolvedNode.Parameter);
                    OnLog($"插入 \"{Trunc(resolvedNode.Parameter, 20)}\"");
                    break;

                case ActionType.Screenshot:
                    DoScreenshot();
                    OnLog("截图");
                    break;

                case ActionType.OpenApp:
                    DoOpenApp(resolvedNode.Parameter);
                    OnLog($"打开 {resolvedNode.Parameter}");
                    break;

                case ActionType.WaitForApp:
                    await DoWaitForApp(resolvedNode.Parameter, resolvedNode.DelayMs);
                    break;

                case ActionType.ReadContent:
                    DoReadContent(resolvedNode.WindowTitle);
                    break;

                case ActionType.ScrollRead:
                    await DoScrollReadAsync(resolvedNode.ScrollAmount, resolvedNode.WindowTitle);
                    break;

                case ActionType.Scroll:
                    DoScroll(resolvedNode.ScrollAmount);
                    OnLog($"滚动 {resolvedNode.ScrollAmount} 行");
                    break;

                case ActionType.InputParam:
                    if (resolvedNode.CopyToClipboard && !string.IsNullOrEmpty(resolvedNode.Parameter))
                    {
                        var paramValue = resolvedNode.Parameter;
                        if (paramValue.StartsWith("{") && paramValue.EndsWith("}"))
                            paramValue = paramValue[1..^1];
                        UIAutomationHelper.SetClipboardText(paramValue);
                        OnLog($"参数 [{resolvedNode.ParameterName}]: {Trunc(paramValue, 20)} → 已复制到剪切板");
                    }
                    else
                    {
                        OnLog($"参数 [{resolvedNode.ParameterName}]: {Trunc(resolvedNode.Parameter, 20)}");
                    }
                    break;

                case ActionType.RegexMatch:
                    DoRegexMatch(resolvedNode);
                    break;

                default:
                    OnLog($"未知操作类型: {resolvedNode.ActionType}");
                    break;
            }
        }

        private async Task DoClickAsync(RecordedAction node)
        {
            if (node.ClickMode == ClickMode.Vision)
            {
                await DoClickByVisionAsync(node);
                return;
            }

            if (node.ClickMode == ClickMode.Coordinate)
            {
                await DoClickByCoordinateAsync(node);
                return;
            }

            await DoClickByUIAPathAsync(node);
        }

        private async Task DoClickByCoordinateAsync(RecordedAction node)
        {
            if (node.X <= 0 && node.Y <= 0)
            {
                OnLog($"坐标模式点击失败: 无有效坐标");
                return;
            }

            if (!string.IsNullOrEmpty(node.WindowTitle))
            {
                IntPtr hwnd = FindTargetWindow(node.WindowTitle);
                if (hwnd != IntPtr.Zero)
                {
                    User32.SetForegroundWindow(hwnd);
                    await Task.Delay(100);
                }
            }

            int clickX = (int)node.X;
            int clickY = (int)node.Y;
            OnLog($"坐标点击 ({clickX},{clickY})");
            await _simulator.ClickAsync(clickX, clickY);
        }

        private async Task DoClickByUIAPathAsync(RecordedAction node)
        {
            var (found, hwnd, rect) = TryLocateElement(node);

            if (found && !rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
            {
                User32.SetForegroundWindow(hwnd);
                await Task.Delay(100);

                int clickX, clickY;
                if (node.X > 0 && node.Y > 0
                    && node.X >= rect.Left && node.X <= rect.Right
                    && node.Y >= rect.Top && node.Y <= rect.Bottom)
                {
                    clickX = (int)node.X;
                    clickY = (int)node.Y;
                    OnLog($"路径模式-使用录制坐标 ({clickX},{clickY}) 点击 {node.ElementName ?? node.ClassName ?? ""}");
                }
                else
                {
                    clickX = (int)(rect.Left + rect.Width / 2);
                    clickY = (int)(rect.Top + rect.Height / 2);
                    OnLog($"路径模式-使用元素中心 ({clickX},{clickY}) 点击 {node.ElementName ?? node.ClassName ?? ""}");
                }

                await _simulator.ClickAsync(clickX, clickY);
                return;
            }

            OnLog($"UIA路径定位失败: {node.ElementName ?? node.ClassName ?? node.AutomationId ?? "未知"}");
        }

        private async Task DoClickByVisionAsync(RecordedAction node)
        {
            // 按脚本绑定的模型加载（_currentVisionModel），失败则中止该步
            if (!EnsureVisionModel(_currentVisionModel))
            {
                OnLog("视觉模式失败: 模型未就绪");
                return;
            }

            IntPtr hwnd = FindTargetWindow(node.WindowTitle);
            if (hwnd == IntPtr.Zero)
            {
                OnLog($"视觉模式失败: 未找到窗口 \"{node.WindowTitle}\"");
                return;
            }

            User32.SetForegroundWindow(hwnd);
            await Task.Delay(200);

            var (winX, winY, winW, winH) = WindowCapturer.GetWindowRect(hwnd);
            using var screenshot = WindowCapturer.CaptureWindow(hwnd);
            if (screenshot == null)
            {
                OnLog("视觉模式失败: 窗口截图失败");
                return;
            }

            string label = node.VisionLabel ?? "button";
            float confThreshold = node.VisionConfThreshold > 0 ? node.VisionConfThreshold : 0.3f;

            // 同一窗口常有多个同类按钮（如多个 send_button）。优先点离录制坐标最近的检测，
            // 避免点错。node.X/Y 是录制时的屏幕坐标，转成窗口局部坐标后参与择近。
            Detection detection;
            bool hasRef = node.X > 0 || node.Y > 0;
            if (hasRef)
            {
                int localRefX = (int)(node.X - winX);
                int localRefY = (int)(node.Y - winY);
                // 容差取窗口较短边的 45%：既能命中目标按钮，又能在该范围内唯一确定
                int tolerance = (int)(Math.Min(winW, winH) * 0.45);
                detection = _visionDetector.FindNearest(screenshot, label, localRefX, localRefY, tolerance, confThreshold);
            }
            else
            {
                detection = _visionDetector.FindBest(screenshot, label, confThreshold);
            }

            if (detection == null)
            {
                OnLog($"视觉模式失败: 未检测到 \"{label}\" (置信度阈值: {confThreshold})");
                return;
            }

            int screenX = detection.CenterX + winX;
            int screenY = detection.CenterY + winY;

            OnLog($"视觉点击: {detection.Label} ({detection.Confidence:P0}) @ 屏幕({screenX},{screenY}) 框({detection.X},{detection.Y} {detection.Width}x{detection.Height})" +
                  (hasRef ? $" (按录制坐标择近)" : ""));
            await _simulator.ClickAsync(screenX, screenY);
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
            await _simulator.KeyComboAsync(User32.VK_CONTROL, User32.VK_V);
        }

        private async Task DoSendKeysAsync(string keys)
        {
            if (string.IsNullOrEmpty(keys)) return;
            await _simulator.SendKeysAsync(keys);
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
            if (timeoutMs <= 0) timeoutMs = 30000;

            string name = System.IO.Path.GetFileNameWithoutExtension(processName).ToLower();
            OnLog($"等待应用 \"{name}\" 启动 (超时 {timeoutMs / 1000}s)");

            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                if (_cts.IsCancellationRequested) return;

                try
                {
                    var procs = Process.GetProcessesByName(name);
                    if (procs.Length > 0)
                    {
                        OnLog($"应用 \"{name}\" 已启动 (PID: {procs[0].Id})");
                        foreach (var p in procs) p.Dispose();
                        return;
                    }
                    foreach (var p in procs) p.Dispose();
                }
                catch (Exception ex)
                {
                    OnLog($"检查进程异常: {ex.Message}，继续等待...");
                }

                try
                {
                    await Task.Delay(500, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                elapsed += 500;
            }

            OnLog($"等待超时: 应用 \"{name}\" 未在 {timeoutMs / 1000}s 内启动，继续执行后续步骤");
        }

        /// <summary>
        /// 读取指定窗口的 UIA 文本内容
        /// </summary>
        private void DoReadContent(string windowTitle = null)
        {
            try
            {
                IntPtr hwnd = ResolveTargetWindow(windowTitle);
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

        private async Task DoScrollReadAsync(int scrollLines, string windowTitle = null)
        {
            try
            {
                IntPtr hwnd = ResolveTargetWindow(windowTitle);
                if (hwnd == IntPtr.Zero) { OnLog("无法获取目标窗口"); return; }

                int len = User32.GetWindowTextLength(hwnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    User32.GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                User32.SetForegroundWindow(hwnd);
                await Task.Delay(200);

                var scrollInput = new User32.INPUT
                {
                    type = User32.INPUT_MOUSE,
                    u = new User32.InputUnion
                    {
                        mi = new User32.MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = (uint)(scrollLines * -120),
                            dwFlags = 0x0800,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };
                User32.SendInput(1, new[] { scrollInput }, System.Runtime.InteropServices.Marshal.SizeOf<User32.INPUT>());
                await Task.Delay(500);

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

        private IntPtr ResolveTargetWindow(string windowTitle)
        {
            if (!string.IsNullOrEmpty(windowTitle))
            {
                IntPtr found = FindTargetWindow(windowTitle);
                if (found != IntPtr.Zero)
                {
                    OnLog($"按标题定位窗口: {windowTitle}");
                    return found;
                }
                OnLog($"未找到窗口 \"{windowTitle}\"，使用当前目标窗口");
            }

            if (_targetWindow != IntPtr.Zero) return _targetWindow;
            IntPtr fg = User32.GetForegroundWindow();
            return fg;
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

        private void DoRegexMatch(RecordedAction node)
        {
            try
            {
                if (string.IsNullOrEmpty(node.RegexPattern))
                {
                    OnLog("正则识别: 未设置正则表达式");
                    return;
                }

                IntPtr hwnd = ResolveTargetWindow(node.WindowTitle);
                if (hwnd == IntPtr.Zero) { OnLog("正则识别: 无法获取目标窗口"); return; }

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

                if (string.IsNullOrEmpty(content))
                {
                    OnLog("正则识别: 窗口内容为空");
                    return;
                }

                var regex = new System.Text.RegularExpressions.Regex(
                    node.RegexPattern,
                    System.Text.RegularExpressions.RegexOptions.Multiline |
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var matches = regex.Matches(content);
                if (matches.Count == 0)
                {
                    OnLog($"正则识别: 未匹配到内容 (模式: {Trunc(node.RegexPattern, 30)})");
                    var emptyResult = new ReadContentResult
                    {
                        WindowTitle = title,
                        Content = content,
                        CapturedAt = DateTime.Now,
                        Source = "RegexMatch",
                        MatchedValue = ""
                    };
                    _readResults.Add(emptyResult);
                    return;
                }

                var matchItems = new List<RegexMatchItem>();
                var allValues = new List<string>();

                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var item = new RegexMatchItem
                    {
                        Value = m.Value,
                        Index = m.Index,
                        Groups = new Dictionary<string, string>()
                    };

                    foreach (System.Text.RegularExpressions.Group g in m.Groups)
                    {
                        string groupName = g.Name;
                        if (int.TryParse(g.Name, out _)) continue;
                        if (g.Success) item.Groups[groupName] = g.Value;
                    }

                    for (int i = 1; i < m.Groups.Count; i++)
                    {
                        if (m.Groups[i].Success && !item.Groups.ContainsKey($"g{i}"))
                            item.Groups[$"g{i}"] = m.Groups[i].Value;
                    }

                    matchItems.Add(item);
                    allValues.Add(m.Value);
                }

                string matchedValue;
                if (!string.IsNullOrEmpty(node.RegexGroup))
                {
                    var firstMatch = matches[0];
                    if (int.TryParse(node.RegexGroup, out int groupIdx) && groupIdx < firstMatch.Groups.Count)
                        matchedValue = firstMatch.Groups[groupIdx].Value;
                    else
                        matchedValue = firstMatch.Groups[node.RegexGroup]?.Value ?? firstMatch.Value;
                }
                else
                {
                    matchedValue = allValues.Count == 1 ? allValues[0] : string.Join("\n", allValues);
                }

                var result = new ReadContentResult
                {
                    WindowTitle = title,
                    Content = content,
                    CapturedAt = DateTime.Now,
                    Source = "RegexMatch",
                    MatchedValue = matchedValue,
                    Matches = matchItems
                };
                _readResults.Add(result);

                OnLog($"正则识别: 匹配 {matches.Count} 处，值: {Trunc(matchedValue, 40)}");

                if (node.CopyToClipboard && !string.IsNullOrEmpty(matchedValue))
                {
                    UIAutomationHelper.SetClipboardText(matchedValue);
                    OnLog($"已复制匹配值到剪切板: {Trunc(matchedValue, 30)}");
                }

                if (!string.IsNullOrEmpty(node.OutputParamName))
                {
                    SetVariable(node.OutputParamName, matchedValue);
                    OnLog($"匹配值已存入变量 {{{node.OutputParamName}}}: {Trunc(matchedValue, 30)}");
                }
            }
            catch (System.Text.RegularExpressions.RegexParseException ex)
            {
                OnLog($"正则表达式语法错误: {ex.Message}");
            }
            catch (Exception ex)
            {
                OnLog($"正则识别失败: {ex.Message}");
            }
        }

        private readonly Dictionary<string, string> _variables = new();
        public IReadOnlyDictionary<string, string> Variables => _variables;

        public void SetVariable(string name, string value)
        {
            _variables[name] = value;
        }

        public string GetVariable(string name)
        {
            return _variables.TryGetValue(name, out var val) ? val : null;
        }

        public void ClearVariables() => _variables.Clear();

        private void DoScroll(int lines)
        {
            var input = new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u = new User32.InputUnion
                {
                    mi = new User32.MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = (uint)(lines * -120),
                        dwFlags = 0x0800,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            User32.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<User32.INPUT>());
        }

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

        private RecordedAction ResolveVariables(RecordedAction node)
        {
            if (_variables.Count == 0) return node;

            string ResolveStr(string s)
            {
                if (string.IsNullOrEmpty(s)) return s;
                foreach (var kvp in _variables)
                    s = s.Replace($"{{{kvp.Key}}}", kvp.Value);
                return s;
            }

            var resolved = new RecordedAction
            {
                NodeId = node.NodeId,
                Order = node.Order,
                ActionType = node.ActionType,
                Name = ResolveStr(node.Name),
                ClassName = ResolveStr(node.ClassName),
                ElementName = ResolveStr(node.ElementName),
                AutomationId = ResolveStr(node.AutomationId),
                ControlType = node.ControlType,
                WindowTitle = ResolveStr(node.WindowTitle),
                X = node.X,
                Y = node.Y,
                ClickMode = node.ClickMode,
                VisionLabel = ResolveStr(node.VisionLabel),
                VisionConfThreshold = node.VisionConfThreshold,
                Parameter = ResolveStr(node.Parameter),
                DelayMs = node.DelayMs,
                ScrollAmount = node.ScrollAmount,
                ParameterName = node.ParameterName,
                DefaultValue = node.DefaultValue,
                IsRequired = node.IsRequired,
                CopyToClipboard = node.CopyToClipboard,
                RegexPattern = ResolveStr(node.RegexPattern),
                RegexGroup = node.RegexGroup,
                OutputParamName = node.OutputParamName,
                IsEnabled = node.IsEnabled,
                CreatedAt = node.CreatedAt
            };
            return resolved;
        }

        public void Stop() => _cts?.Cancel();
        private void OnLog(string msg) { _logger.Info("Player", msg); LogMessage?.Invoke(this, msg); }
        public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); _visionDetector?.Dispose(); }
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
                int bytes = (text.Length + 1) * 2;
                IntPtr h = User32.GlobalAlloc(User32.GMEM_MOVEABLE, bytes);
                if (h == IntPtr.Zero) return;
                IntPtr locked = User32.GlobalLock(h);
                if (locked == IntPtr.Zero) { User32.GlobalUnlock(h); return; }
                System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                System.Runtime.InteropServices.Marshal.WriteInt16(locked + text.Length * 2, 0);
                User32.GlobalUnlock(h);
                User32.SetClipboardData(User32.CF_UNICODETEXT, h);
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
    }
}
