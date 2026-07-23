using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;
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
        private static readonly UIA3Automation _automation = new();
        private static readonly ConditionFactory _cf = _automation.ConditionFactory;
        private CancellationTokenSource _cts;
        private bool _isPlaying;
        private IntPtr _targetWindow = IntPtr.Zero;
        private AutomationElement _targetElement = null; // 用户选择的具体 UIA 元素（用于阅读）
        private readonly HumanInputSimulator _simulator = new();
        private VisionDetector _visionDetector;

        public bool IsPlaying => _isPlaying;
        private readonly List<ReadContentResult> _readResults = new();
        public IReadOnlyList<ReadContentResult> ReadResults => _readResults;
        private readonly List<VisionDetectionResult> _visionResults = new();
        public IReadOnlyList<VisionDetectionResult> VisionResults => _visionResults;

        /// <summary>
        /// 设置目标窗口句柄（用于阅读功能）
        /// </summary>
        public void SetTargetWindow(IntPtr hwnd)
        {
            _targetWindow = hwnd;
            _targetElement = null; // 清除旧的元素引用
        }

        /// <summary>
        /// 设置目标 UIA 元素（用于阅读具体区域内容）
        /// </summary>
        public void SetTargetElement(IntPtr hwnd, AutomationElement element)
        {
            _targetWindow = hwnd;
            _targetElement = element;
        }

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
        /// If 步骤条件成立且设置了 TargetScript 时触发，ScriptExecutor 订阅执行子脚本。
        /// 参数为子脚本名。子脚本继承当前参数（通过 ScriptExecutor.ExecuteScript 传入）。
        /// </summary>
        public event EventHandler<string>? SubScriptRequested;

        /// <summary>
        /// 当前回放脚本收到的参数（由 ScriptExecutor 设置，供子脚本继承）。
        /// </summary>
        public Dictionary<string, string>? CurrentParameters { get; set; }

        /// <summary>请求停止当前脚本后续步骤（由 If-TargetScript 逻辑设置）</summary>
        private bool _stopRequested;

        /// <summary>子脚本执行结果（SubScriptRequested 处理方回填），null=未执行/执行失败</summary>
        private bool? _subScriptSuccess;

        /// <summary>
        /// 执行子脚本：触发 SubScriptRequested 事件，由 ScriptExecutor 订阅执行。
        /// 子脚本继承当前参数（CurrentParameters）。返回子脚本是否成功。
        /// </summary>
        private bool ExecuteSubScript(string scriptName)
        {
            try
            {
                _subScriptSuccess = null;
                SubScriptRequested?.Invoke(this, scriptName);
                return _subScriptSuccess == true;
            }
            catch (Exception ex)
            {
                OnLog($"执行子脚本异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 供 SubScriptRequested 订阅方回填子脚本执行结果。
        /// </summary>
        public void ReportSubScriptResult(bool success) => _subScriptSuccess = success;

        /// <summary>供 ScriptExecutor 合并子脚本的阅读结果到主结果</summary>
        public void AppendReadResult(ReadContentResult r) => _readResults.Add(r);

        /// <summary>供 ScriptExecutor 合并子脚本的视觉检测结果到主结果</summary>
        public void AppendVisionResult(VisionDetectionResult v) => _visionResults.Add(v);

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
            _visionResults.Clear();
            _stopRequested = false;
            _subScriptSuccess = null;
            // 注意：不清除 _targetWindow 和 _targetElement，它们是用户通过 PickTargetWindow 选择的阅读目标
            OnLog($"开始回放，共 {nodes.Count} 步" +
                  (string.IsNullOrEmpty(visionModelFileName) ? "" : $"（视觉模型: {visionModelFileName}）"));

            // 构建 NodeId → 索引映射，供 If/Goto 跳转使用
            var nodeIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; i++)
                if (!string.IsNullOrEmpty(nodes[i].NodeId))
                    nodeIndexMap[nodes[i].NodeId] = i;

            try
            {
                int currentIndex = 0;
                int iterations = 0;
                const int maxIterations = 10000; // 防止死循环

                while (currentIndex < nodes.Count && iterations++ < maxIterations)
                {
                    if (_cts.IsCancellationRequested) break;
                    if (_stopRequested) break; // If-TargetScript 请求停止后续步骤
                    var node = nodes[currentIndex];
                    if (!node.IsEnabled) { currentIndex++; continue; }
                    if (node.DelayMs > 0) await Task.Delay(node.DelayMs, _cts.Token);

                    int? jumpTo = await ExecuteNode(node, nodeIndexMap);
                    currentIndex = jumpTo.HasValue ? jumpTo.Value : currentIndex + 1;
                }

                if (_stopRequested)
                    OnLog(_subScriptSuccess == true ? "已执行子脚本并停止当前脚本" : "已停止当前脚本后续步骤");
                else if (iterations >= maxIterations)
                    OnLog($"回放达到最大迭代次数 ({maxIterations})，可能存在死循环，已中止");

                if (!_cts.IsCancellationRequested && !_stopRequested && iterations < maxIterations)
                { OnLog("回放完成"); PlayCompleted?.Invoke(this, EventArgs.Empty); }
            }
            catch (OperationCanceledException) { OnLog("回放已取消"); }
            catch (Exception ex) { OnLog($"回放异常: {ex.Message}"); PlayError?.Invoke(this, ex.Message); }
            finally { _isPlaying = false; _cts?.Dispose(); _cts = null; }
        }

        private async Task<int?> ExecuteNode(RecordedAction node, Dictionary<string, int> nodeIndexMap)
        {
            var resolvedNode = ResolveVariables(node);

            switch (resolvedNode.ActionType)
            {
                case ActionType.Click:
                    {
                        bool clickOk = await DoClickAsync(resolvedNode);
                        RecordClickResult(resolvedNode, clickOk);
                        OnLog($"点击 {resolvedNode.ElementName ?? resolvedNode.ClassName ?? $"({resolvedNode.X:F0},{resolvedNode.Y:F0})"} -> {(clickOk ? "成功" : "失败")}");
                        break;
                    }

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
                    DoReadContent(resolvedNode);
                    break;

                case ActionType.ScrollRead:
                    await DoScrollReadAsync(resolvedNode.ScrollAmount, resolvedNode.WindowTitle, resolvedNode.OutputParamName);
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

                case ActionType.If:
                    {
                        // 便捷模式：条件表达式为空但设置了 OutputParamName，直接判断该变量（阅读/正则内容）是否有值
                        string? ifExpr = resolvedNode.ConditionExpression;
                        bool condResult;
                        if (string.IsNullOrWhiteSpace(ifExpr) && !string.IsNullOrEmpty(resolvedNode.OutputParamName))
                        {
                            string val = GetVariable(resolvedNode.OutputParamName) ?? "";
                            OnLog($"判断变量 {{{resolvedNode.OutputParamName}}} 是否有值: [{Trunc(val, 30)}]");
                            condResult = !string.IsNullOrWhiteSpace(val)
                                && !val.Equals("0", StringComparison.OrdinalIgnoreCase)
                                && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            condResult = EvaluateCondition(ifExpr);
                            OnLog($"判断: {Trunc(ifExpr ?? "", 40)} -> {(condResult ? "true" : "false")}");
                        }

                        // 新模式：设置了 TargetScript 时，成立->执行子脚本并停止；不成立->直接停止
                        if (!string.IsNullOrEmpty(resolvedNode.TargetScript))
                        {
                            if (condResult)
                            {
                                OnLog($"  -> 条件成立，执行子脚本: {resolvedNode.TargetScript}（执行完停止当前脚本）");
                                _subScriptSuccess = ExecuteSubScript(resolvedNode.TargetScript);
                                _stopRequested = true;
                            }
                            else
                            {
                                OnLog($"  -> 条件不成立，停止当前脚本后续步骤");
                                _stopRequested = true;
                            }
                            return null;
                        }

                        // 旧模式：跳转目标
                        if (condResult && !string.IsNullOrEmpty(node.TrueGotoNodeId)
                            && nodeIndexMap.TryGetValue(node.TrueGotoNodeId, out int trueIdx))
                        {
                            OnLog($"  -> 跳转到 true 分支: {node.TrueGotoNodeId}");
                            return trueIdx;
                        }
                        else if (!condResult && !string.IsNullOrEmpty(node.GotoNodeId)
                            && nodeIndexMap.TryGetValue(node.GotoNodeId, out int falseIdx))
                        {
                            OnLog($"  -> 跳转到 false 分支: {node.GotoNodeId}");
                            return falseIdx;
                        }
                        // 无跳转目标则继续下一步
                        break;
                    }

                case ActionType.Goto:
                    if (!string.IsNullOrEmpty(node.GotoNodeId) && nodeIndexMap.TryGetValue(node.GotoNodeId, out int targetIdx))
                    {
                        OnLog($"跳转 → {node.GotoNodeId} (索引 {targetIdx})");
                        return targetIdx;
                    }
                    OnLog($"跳转失败: 未找到目标节点 {node.GotoNodeId}");
                    break;

                case ActionType.SwitchToWindow:
                    DoSwitchToWindow(resolvedNode);
                    break;

                default:
                    OnLog($"未知操作类型: {resolvedNode.ActionType}");
                    break;
            }

            return null; // 继续下一步
        }

        private async Task<bool> DoClickAsync(RecordedAction node)
        {
            if (node.ClickMode == ClickMode.Vision)
            {
                return await DoClickByVisionAsync(node);
            }

            if (node.ClickMode == ClickMode.Coordinate)
            {
                return await DoClickByCoordinateAsync(node);
            }

            return await DoClickByUIAPathAsync(node);
        }

        private async Task<bool> DoClickByCoordinateAsync(RecordedAction node)
        {
            if (node.X <= 0 && node.Y <= 0)
            {
                OnLog($"坐标模式点击失败: 无有效坐标");
                return false;
            }

            if (!string.IsNullOrEmpty(node.WindowTitle))
            {
                IntPtr hwnd = FindTargetWindow(node.WindowTitle);
                if (hwnd != IntPtr.Zero)
                {
                    EnsureWindowForeground(hwnd);
                    await Task.Delay(100);
                }
            }

            int clickX = (int)node.X;
            int clickY = (int)node.Y;
            OnLog($"坐标点击 ({clickX},{clickY})");
            await _simulator.ClickAsync(clickX, clickY);
            return true;
        }

        private async Task<bool> DoClickByUIAPathAsync(RecordedAction node)
        {
            var (found, hwnd, rect) = TryLocateElement(node);

            if (found && !rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
            {
                EnsureWindowForeground(hwnd);
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
                return true;
            }

            // 路径模式下禁止回退到坐标点击——直接报告失败
            OnLog($"UIA路径定位失败: {node.ElementName ?? node.ClassName ?? node.AutomationId ?? "未知"}" +
                  (!string.IsNullOrEmpty(node.XPath) ? $" (XPath={Trunc(node.XPath, 40)})" : ""));
            return false;
        }

        private async Task<bool> DoClickByVisionAsync(RecordedAction node)
        {
            // 按脚本绑定的模型加载（_currentVisionModel），失败则中止该步
            if (!EnsureVisionModel(_currentVisionModel))
            {
                OnLog("视觉模式失败: 模型未就绪");
                return false;
            }

            IntPtr hwnd = FindTargetWindow(node.WindowTitle);
            if (hwnd == IntPtr.Zero)
            {
                OnLog($"视觉模式失败: 未找到窗口 \"{node.WindowTitle}\"");
                return false;
            }

            EnsureWindowForeground(hwnd);
            await Task.Delay(200);

            var (winX, winY, winW, winH) = WindowCapturer.GetWindowRect(hwnd);
            using var screenshot = WindowCapturer.CaptureWindow(hwnd);
            if (screenshot == null)
            {
                OnLog("视觉模式失败: 窗口截图失败");
                return false;
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

            // 特定标签检测失败时，尝试降级到更通用的 "button" 标签
            if (detection == null && label != "button")
            {
                OnLog($"视觉模式: '{label}' 未检测到，尝试降级到 'button'");
                if (hasRef)
                {
                    int localRefX = (int)(node.X - winX);
                    int localRefY = (int)(node.Y - winY);
                    int tolerance = (int)(Math.Min(winW, winH) * 0.45);
                    detection = _visionDetector.FindNearest(screenshot, "button", localRefX, localRefY, tolerance, confThreshold);
                }
                else
                {
                    detection = _visionDetector.FindBest(screenshot, "button", confThreshold);
                }
                if (detection != null)
                    OnLog($"视觉模式降级检测成功: button ({detection.Confidence:P0})");
            }

            if (detection == null)
            {
                OnLog($"视觉模式失败: 未检测到 \"{label}\" (置信度阈值: {confThreshold})");
                return false;
            }

            // 捕获视觉检测结果，供回传到服务器
            try
            {
                var allDetections = _visionDetector.Detect(screenshot, new[] { label }, confThreshold);
                _visionResults.Add(new VisionDetectionResult
                {
                    WindowTitle = node.WindowTitle ?? "",
                    VisionLabel = label,
                    CapturedAt = DateTime.Now,
                    Source = "VisionClick",
                    Detections = allDetections.Select(d => new DetectionInfo
                    {
                        Label = d.Label,
                        Confidence = d.Confidence,
                        X = d.X,
                        Y = d.Y,
                        Width = d.Width,
                        Height = d.Height
                    }).ToList()
                });
            }
            catch { /* 捕获结果不影响主流程 */ }

            int screenX = detection.CenterX + winX;
            int screenY = detection.CenterY + winY;

            OnLog($"视觉点击: {detection.Label} ({detection.Confidence:P0}) @ 屏幕({screenX},{screenY}) 框({detection.X},{detection.Y} {detection.Width}x{detection.Height})" +
                  (hasRef ? $" (按录制坐标择近)" : ""));
            await _simulator.ClickAsync(screenX, screenY);
            return true;
        }

        /// <summary>
        /// 通过 UIA 属性定位元素，5 策略级联（XPath 优先）
        /// 返回 (是否找到, 窗口句柄, 元素边界框)
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

                var root = _automation.FromHandle(hwnd);
                if (root == null)
                {
                    OnLog("无法获取窗口 UIA 根元素");
                    return (false, hwnd, default);
                }

                AutomationElement element = null;
                string matchMethod = "";

                // 策略1: XPath 查找（最精确）
                if (element == null && !string.IsNullOrEmpty(node.XPath))
                {
                    try
                    {
                        element = FindByXPath(root, node.XPath);
                        if (element != null)
                        {
                            matchMethod = $"XPath={Trunc(node.XPath, 60)}";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Player", $"XPath 查找异常: {ex.Message}");
                    }
                }

                // 策略2: AutomationId 精确匹配
                if (element == null && !string.IsNullOrEmpty(node.AutomationId))
                {
                    element = root.FindFirstDescendant(_cf.ByAutomationId(node.AutomationId));
                    if (element != null) matchMethod = $"AutomationId={node.AutomationId}";
                }

                // 策略3: Name + ControlType + SiblingIndex
                if (element == null && !string.IsNullOrEmpty(node.ElementName))
                {
                    try
                    {
                        var controlType = ParseControlType(node.ControlType);
                        if (controlType != null)
                        {
                            var allMatches = root.FindAllDescendants(
                                _cf.ByName(node.ElementName).And(_cf.ByControlType(controlType.Value)));
                            if (allMatches.Length > 0)
                            {
                                int idx = Math.Min(node.SiblingIndex, allMatches.Length - 1);
                                element = allMatches[idx];
                                matchMethod = $"Name={node.ElementName}+ControlType={node.ControlType}[{idx}]";
                            }
                        }
                        else
                        {
                            // 仅 Name 匹配
                            var allMatches = root.FindAllDescendants(_cf.ByName(node.ElementName));
                            if (allMatches.Length > 0)
                            {
                                int idx = Math.Min(node.SiblingIndex, allMatches.Length - 1);
                                element = allMatches[idx];
                                matchMethod = $"Name={node.ElementName}[{idx}]";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Player", $"Name+ControlType 查找异常: {ex.Message}");
                    }
                }

                // 策略4: ClassName + ControlType
                if (element == null && !string.IsNullOrEmpty(node.ClassName))
                {
                    try
                    {
                        var controlType = ParseControlType(node.ControlType);
                        if (controlType != null)
                        {
                            element = root.FindFirstDescendant(
                                _cf.ByClassName(node.ClassName).And(_cf.ByControlType(controlType.Value)));
                            if (element != null) matchMethod = $"ClassName={node.ClassName}+ControlType={node.ControlType}";
                        }
                        else
                        {
                            element = root.FindFirstDescendant(_cf.ByClassName(node.ClassName));
                            if (element != null) matchMethod = $"ClassName={node.ClassName}";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Player", $"ClassName 查找异常: {ex.Message}");
                    }
                }

                if (element == null)
                {
                    OnLog($"UIA 未找到: Name=\"{node.ElementName}\" Class=\"{node.ClassName}\" AutoId=\"{node.AutomationId}\"" +
                          (!string.IsNullOrEmpty(node.XPath) ? $" XPath=\"{node.XPath}\"" : ""));
                    return (false, hwnd, default);
                }

                var rect = element.BoundingRectangle;
                OnLog($"定位成功 ({matchMethod}) 坐标({rect.Left:F0},{rect.Top:F0})");
                return (true, hwnd, new System.Windows.Rect(rect.X, rect.Y, rect.Width, rect.Height));
            }
            catch (Exception ex)
            {
                OnLog($"UIA 异常: {ex.Message}");
                return (false, IntPtr.Zero, default);
            }
        }

        /// <summary>
        /// 通过自定义 XPath 字符串查找元素。
        /// 解析 XPath 路径（如 /Window[@Name='微信']/Pane/Button[@Name='发送'][2]），
        /// 从根元素逐级向下查找，支持跳过中间容器元素。
        /// </summary>
        private AutomationElement FindByXPath(AutomationElement root, string xpath)
        {
            if (string.IsNullOrEmpty(xpath)) return null;

            // 移除前导 / 或 //
            string path = xpath.TrimStart('/');
            bool isDeep = xpath.StartsWith("//");

            var segments = ParseXPathSegments(path);
            if (segments.Count == 0) return null;

            AutomationElement current = root;

            // 第一级：检查根元素是否匹配第一个段
            if (!isDeep && segments.Count > 0)
            {
                var firstSeg = segments[0];
                if (MatchesSegment(current, firstSeg))
                {
                    // 根匹配，从第二级开始逐级查找
                    for (int i = 1; i < segments.Count; i++)
                    {
                        var child = FindDescendantBySegment(current, segments[i]);
                        if (child == null) return null;
                        current = child;
                    }
                    return current;
                }
            }

            // // 开头或根不匹配：在任意深度查找匹配段序列的元素
            return FindByXPathDeep(root, segments);
        }

        /// <summary>
        /// 在子树中递归查找匹配 XPath 段序列的元素
        /// </summary>
        private AutomationElement FindByXPathDeep(AutomationElement root, List<XPathSegment> segments)
        {
            if (segments.Count == 0) return null;

            // 在所有后代中查找匹配第一段的元素
            var candidates = FindAllMatchingDescendants(root, segments[0]);
            foreach (var candidate in candidates)
            {
                if (segments.Count == 1) return candidate;

                // 递归匹配后续段
                var result = FindByPathFromParent(candidate, segments, 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// 从指定父元素开始，按 XPath 段逐级查找（允许跳过中间容器）
        /// </summary>
        private AutomationElement FindByPathFromParent(AutomationElement parent, List<XPathSegment> segments, int startIndex)
        {
            AutomationElement current = parent;
            for (int i = startIndex; i < segments.Count; i++)
            {
                var child = FindDescendantBySegment(current, segments[i]);
                if (child == null) return null;
                current = child;
            }
            return current;
        }

        /// <summary>
        /// 在后代元素中查找匹配指定段的元素（先查直接子元素，再查更深层后代）。
        /// 这比仅查直接子元素更健壮，因为 UI 树中常有中间容器元素。
        /// </summary>
        private AutomationElement FindDescendantBySegment(AutomationElement parent, XPathSegment segment)
        {
            try
            {
                // 1. 先在直接子元素中查找（最精确，遵循 XPath 路径）
                var children = parent.FindAllChildren();
                var matches = new List<AutomationElement>();

                foreach (var child in children)
                {
                    if (MatchesSegment(child, segment))
                        matches.Add(child);
                }

                if (matches.Count > 0)
                {
                    int idx = segment.Index > 0 ? Math.Min(segment.Index - 1, matches.Count - 1) : 0;
                    return matches[idx];
                }

                // 2. 直接子元素未找到，在更深层后代中查找（跳过中间容器）
                // 使用 FlaUI 的 FindAllDescendants + 属性条件提高效率
                var descendantMatches = FindAllMatchingDescendants(parent, segment);
                if (descendantMatches.Count > 0)
                {
                    int idx = segment.Index > 0 ? Math.Min(segment.Index - 1, descendantMatches.Count - 1) : 0;
                    OnLog($"XPath: 在后代中找到 {descendantMatches.Count} 个匹配（跳过中间容器）");
                    return descendantMatches[idx];
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 在所有后代中查找匹配指定段的元素
        /// </summary>
        private List<AutomationElement> FindAllMatchingDescendants(AutomationElement root, XPathSegment segment)
        {
            var results = new List<AutomationElement>();
            try
            {
                // 优先使用 FlaUI 条件查找（更高效）
                var controlType = ParseControlType(segment.ControlType);
                if (controlType != null)
                {
                    // 如果有 Name 属性，使用 ControlType + Name 组合条件
                    if (segment.Attributes.TryGetValue("Name", out string name) && !string.IsNullOrEmpty(name))
                    {
                        var found = root.FindAllDescendants(
                            _cf.ByControlType(controlType.Value).And(_cf.ByName(name)));
                        foreach (var elem in found)
                        {
                            if (MatchesSegment(elem, segment))
                                results.Add(elem);
                        }
                    }
                    else
                    {
                        // 仅 ControlType 条件
                        var found = root.FindAllDescendants(_cf.ByControlType(controlType.Value));
                        foreach (var elem in found)
                        {
                            if (MatchesSegment(elem, segment))
                                results.Add(elem);
                        }
                    }
                }
                else
                {
                    // 无法解析 ControlType，遍历所有后代
                    var descendants = root.FindAllDescendants();
                    foreach (var desc in descendants)
                    {
                        if (MatchesSegment(desc, segment))
                            results.Add(desc);
                    }
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// 检查元素是否匹配 XPath 段
        /// </summary>
        private bool MatchesSegment(AutomationElement element, XPathSegment segment)
        {
            try
            {
                // ControlType 匹配
                string ctName = element.ControlType.ToString();
                if (!string.Equals(ctName, segment.ControlType, StringComparison.OrdinalIgnoreCase))
                    return false;

                // 属性匹配
                if (segment.Attributes.Count == 0) return true;

                foreach (var attr in segment.Attributes)
                {
                    bool attrMatch = attr.Key.ToLower() switch
                    {
                        "automationid" => TryGetPropertyValue(element.AutomationId, attr.Value),
                        "name" => TryGetPropertyValue(element.Name, attr.Value),
                        "classname" => TryGetPropertyValue(element.ClassName, attr.Value),
                        _ => true // 未知属性不阻止匹配
                    };
                    if (!attrMatch) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 尝试比较元素属性值与期望值。
        /// 如果属性无法获取（异常），视为不匹配。
        /// </summary>
        private static bool TryGetPropertyValue(string actualValue, string expectedValue)
        {
            try
            {
                return actualValue == expectedValue;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析 XPath 字符串为段列表
        /// </summary>
        private List<XPathSegment> ParseXPathSegments(string xpath)
        {
            var segments = new List<XPathSegment>();
            if (string.IsNullOrEmpty(xpath)) return segments;

            // 按 / 分割（排除转义的 /）
            var parts = xpath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var seg = new XPathSegment();
                string remaining = part.Trim();

                // 提取最后的索引 [n]（仅数字索引，非属性谓词）
                var idxMatch = System.Text.RegularExpressions.Regex.Match(remaining, @"\[(\d+)\]$");
                if (idxMatch.Success)
                {
                    seg.Index = int.Parse(idxMatch.Groups[1].Value);
                    remaining = remaining[..idxMatch.Index].Trim();
                }

                // 提取 ControlType（谓词前的部分）和属性谓词
                var predMatch = System.Text.RegularExpressions.Regex.Match(remaining, @"^(\w+)(\[.+\])?$");
                if (predMatch.Success)
                {
                    seg.ControlType = predMatch.Groups[1].Value;

                    if (predMatch.Groups[2].Success)
                    {
                        string predicates = predMatch.Groups[2].Value.Trim('[', ']');
                        // 解析 @Attr='Value' 对
                        foreach (System.Text.RegularExpressions.Match m in
                            System.Text.RegularExpressions.Regex.Matches(predicates, @"@(\w+)='([^']*)'"))
                        {
                            seg.Attributes[m.Groups[1].Value] = m.Groups[2].Value;
                        }
                    }
                }
                else
                {
                    seg.ControlType = remaining;
                }

                if (!string.IsNullOrEmpty(seg.ControlType))
                    segments.Add(seg);
            }

            return segments;
        }

        /// <summary>
        /// XPath 段数据结构
        /// </summary>
        private class XPathSegment
        {
            public string ControlType { get; set; } = "";
            public Dictionary<string, string> Attributes { get; set; } = new();
            public int Index { get; set; } // 0 = 无索引, 1+ = 1-based
        }

        /// <summary>
        /// 将字符串 ControlType 解析为 FlaUI ControlType 枚举
        /// </summary>
        private static FlaUI.Core.Definitions.ControlType? ParseControlType(string controlType)
        {
            if (string.IsNullOrEmpty(controlType)) return null;

            return controlType switch
            {
                "Button" => FlaUI.Core.Definitions.ControlType.Button,
                "Calendar" => FlaUI.Core.Definitions.ControlType.Calendar,
                "CheckBox" => FlaUI.Core.Definitions.ControlType.CheckBox,
                "ComboBox" => FlaUI.Core.Definitions.ControlType.ComboBox,
                "Edit" => FlaUI.Core.Definitions.ControlType.Edit,
                "Hyperlink" => FlaUI.Core.Definitions.ControlType.Hyperlink,
                "Image" => FlaUI.Core.Definitions.ControlType.Image,
                "ListItem" => FlaUI.Core.Definitions.ControlType.ListItem,
                "List" => FlaUI.Core.Definitions.ControlType.List,
                "Menu" => FlaUI.Core.Definitions.ControlType.Menu,
                "MenuBar" => FlaUI.Core.Definitions.ControlType.MenuBar,
                "MenuItem" => FlaUI.Core.Definitions.ControlType.MenuItem,
                "ProgressBar" => FlaUI.Core.Definitions.ControlType.ProgressBar,
                "RadioButton" => FlaUI.Core.Definitions.ControlType.RadioButton,
                "ScrollBar" => FlaUI.Core.Definitions.ControlType.ScrollBar,
                "Slider" => FlaUI.Core.Definitions.ControlType.Slider,
                "Spinner" => FlaUI.Core.Definitions.ControlType.Spinner,
                "StatusBar" => FlaUI.Core.Definitions.ControlType.StatusBar,
                "Tab" => FlaUI.Core.Definitions.ControlType.Tab,
                "TabItem" => FlaUI.Core.Definitions.ControlType.TabItem,
                "Text" => FlaUI.Core.Definitions.ControlType.Text,
                "ToolBar" => FlaUI.Core.Definitions.ControlType.ToolBar,
                "ToolTip" => FlaUI.Core.Definitions.ControlType.ToolTip,
                "Tree" => FlaUI.Core.Definitions.ControlType.Tree,
                "TreeItem" => FlaUI.Core.Definitions.ControlType.TreeItem,
                "DataGrid" => FlaUI.Core.Definitions.ControlType.DataGrid,
                "DataItem" => FlaUI.Core.Definitions.ControlType.DataItem,
                "Document" => FlaUI.Core.Definitions.ControlType.Document,
                "SplitButton" => FlaUI.Core.Definitions.ControlType.SplitButton,
                "Window" => FlaUI.Core.Definitions.ControlType.Window,
                "Pane" => FlaUI.Core.Definitions.ControlType.Pane,
                "Header" => FlaUI.Core.Definitions.ControlType.Header,
                "HeaderItem" => FlaUI.Core.Definitions.ControlType.HeaderItem,
                "Table" => FlaUI.Core.Definitions.ControlType.Table,
                "Thumb" => FlaUI.Core.Definitions.ControlType.Thumb,
                "Group" => FlaUI.Core.Definitions.ControlType.Group,
                "Custom" => FlaUI.Core.Definitions.ControlType.Custom,
                _ => null
            };
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
                // 不依赖 IsWindowVisible：微信等 Electron 应用窗口无 WS_VISIBLE 位但实际可见
                if (!IsWindowActuallyVisible(hwnd)) return true;
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

            // 如果标题匹配失败，尝试 FindWindow 精确匹配（对微信"微信"标题有效）
            if (found == IntPtr.Zero)
            {
                IntPtr fw = User32.FindWindow(null, windowTitle);
                if (fw != IntPtr.Zero && IsWindowActuallyVisible(fw))
                    found = fw;
            }

            // 如果精确匹配失败，尝试匹配进程名
            if (found == IntPtr.Zero)
            {
                OnLog($"未找到窗口 \"{windowTitle}\"，尝试按进程名搜索");
                found = FindWindowByProcessName(windowTitle);
            }

            return found;
        }

        /// <summary>
        /// 通过进程名查找窗口。
        /// 同一进程可能有多个顶层窗口（如微信），优先选面积最大的主窗口。
        /// </summary>
        private IntPtr FindWindowByProcessName(string name)
        {
            var all = FindAllWindowsByProcessName(name);
            if (all.Count == 0) return IntPtr.Zero;

            // 选面积最大的作为主窗口
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            foreach (var (hwnd, _, area) in all)
            {
                if (area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }
            }
            return best;
        }

        /// <summary>
        /// 通过进程名查找所有实际可见的顶层窗口（排除辅助窗口）。
        /// 返回 (窗口句柄, 标题, 面积) 列表。用于"打开全部"同名进程窗口。
        /// </summary>
        private List<(IntPtr hwnd, string title, long area)> FindAllWindowsByProcessName(string name)
        {
            var result = new List<(IntPtr hwnd, string title, long area)>();
            var seen = new HashSet<IntPtr>();

            User32.EnumWindowsProc callback = (hwnd, _) =>
            {
                // 不用 IsWindowVisible：微信等 Electron 应用窗口无 WS_VISIBLE 位但实际可见
                if (!IsWindowActuallyVisible(hwnd)) return true;
                if (!seen.Add(hwnd)) return true; // 去重
                User32.GetWindowThreadProcessId(hwnd, out int pid);
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (proc.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        int len = User32.GetWindowTextLength(hwnd);
                        if (len == 0) return true;
                        var sb = new System.Text.StringBuilder(len + 1);
                        User32.GetWindowText(hwnd, sb, sb.Capacity);
                        string title = sb.ToString();

                        // 排除辅助窗口（托盘消息、IME、默认 IME 等）
                        if (title.IndexOf("MessageWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("IME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
                            title.IndexOf("Tray", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;

                        User32.GetWindowRect(hwnd, out var r);
                        int width = r.Right - r.Left;
                        int height = r.Bottom - r.Top;
                        if (width <= 0 || height <= 0) return true;
                        long area = (long)width * height;
                        result.Add((hwnd, title, area));
                    }
                }
                catch { }
                return true;
            };
            User32.EnumWindows(callback, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// 判断窗口是否"实际可见"。
        /// 不依赖 IsWindowVisible（WS_VISIBLE 位）：微信等 Electron 应用的窗口
        /// 实际显示在屏幕上但没有 WS_VISIBLE 样式位。这里用：有标题 + 矩形宽高>0 判断。
        /// </summary>
        private bool IsWindowActuallyVisible(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            // 必须有标题（过滤 IME、消息窗口等辅助窗口）
            if (User32.GetWindowTextLength(hwnd) == 0) return false;
            // 矩形宽高必须 > 0
            if (!User32.GetWindowRect(hwnd, out var r)) return false;
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return false;
            return true;
        }

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
        /// 读取指定窗口的 UIA 文本内容。若设置了 OutputParamName，内容会存入变量供后续判断引用。
        /// </summary>
        private void DoReadContent(RecordedAction node)
        {
            string windowTitle = node.WindowTitle;
            try
            {
                AutomationElement readElement = null;
                IntPtr hwnd;

                if (_targetElement != null && _targetWindow != IntPtr.Zero)
                {
                    // 用户通过 PickTargetWindow 选中了具体元素，优先用该元素读取
                    hwnd = _targetWindow;
                    readElement = _targetElement;
                    OnLog("阅读: 使用用户选择的目标元素");
                }
                else if (_targetWindow != IntPtr.Zero)
                {
                    // 仅有窗口句柄，从窗口根元素出发查找内容区域
                    hwnd = _targetWindow;
                    var windowElement = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(windowElement);
                    readElement = contentElement ?? windowElement;
                    OnLog("阅读: 使用用户选择的目标窗口");
                }
                else
                {
                    hwnd = ResolveTargetWindow(windowTitle);
                    if (hwnd == IntPtr.Zero) { OnLog("无法获取目标窗口"); return; }

                    var element = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(element);
                    readElement = contentElement ?? element;
                }

                int len = User32.GetWindowTextLength(hwnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    User32.GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                string content = ExtractAllText(readElement, 0, 10);

                var result = new ReadContentResult
                {
                    WindowTitle = title,
                    Content = content,
                    CapturedAt = DateTime.Now,
                    Source = "ReadContent",
                    OutputParamName = node.OutputParamName
                };
                _readResults.Add(result);

                // 如果设置了输出变量名，把读取到的内容存入变量，供后续 If 判断引用
                if (!string.IsNullOrEmpty(node.OutputParamName))
                {
                    SetVariable(node.OutputParamName, content);
                    OnLog($"已读取窗口: {title} ({content.Length} 字符) -> 变量 {{{node.OutputParamName}}}");
                }
                else
                {
                    OnLog($"已读取窗口: {title} ({content.Length} 字符)");
                }
            }
            catch (Exception ex) { OnLog($"读取失败: {ex.Message}"); }
        }

        private async Task DoScrollReadAsync(int scrollLines, string windowTitle = null, string outputParamName = null)
        {
            try
            {
                AutomationElement readElement = null;
                IntPtr hwnd;

                if (_targetElement != null && _targetWindow != IntPtr.Zero)
                {
                    hwnd = _targetWindow;
                    readElement = _targetElement;
                }
                else if (_targetWindow != IntPtr.Zero)
                {
                    hwnd = _targetWindow;
                    var windowElement = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(windowElement);
                    readElement = contentElement ?? windowElement;
                }
                else
                {
                    hwnd = ResolveTargetWindow(windowTitle);
                    if (hwnd == IntPtr.Zero) { OnLog("无法获取目标窗口"); return; }

                    var element = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(element);
                    readElement = contentElement ?? element;
                }

                int len = User32.GetWindowTextLength(hwnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    User32.GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

                EnsureWindowForeground(hwnd);
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

                // 滚动后重新获取元素内容
                string content;
                if (_targetElement != null && _targetWindow != IntPtr.Zero)
                {
                    // 用户选择了具体元素，从该元素重新读取
                    content = ExtractAllText(readElement, 0, 10);
                }
                else if (_targetWindow != IntPtr.Zero)
                {
                    var windowElement = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(windowElement);
                    content = ExtractAllText(contentElement ?? windowElement, 0, 10);
                }
                else
                {
                    var element = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(element);
                    content = ExtractAllText(contentElement ?? element, 0, 10);
                }

                var result = new ReadContentResult
                {
                    WindowTitle = title,
                    Content = content,
                    CapturedAt = DateTime.Now,
                    Source = "ScrollRead",
                    OutputParamName = outputParamName
                };
                _readResults.Add(result);

                if (!string.IsNullOrEmpty(outputParamName))
                {
                    SetVariable(outputParamName, content);
                    OnLog($"滚动阅读: {title} ({content.Length} 字符) -> 变量 {{{outputParamName}}}");
                }
                else
                {
                    OnLog($"滚动阅读: {title} ({content.Length} 字符)");
                }
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
        /// 在窗口 UIA 树中查找主要内容区域元素。
        /// 跳过标题栏、菜单栏、工具栏、状态栏等外围元素，
        /// 找到包含实际内容的 Document 或 Pane 子元素。
        /// </summary>
        private AutomationElement FindContentElement(AutomationElement windowElement)
        {
            if (windowElement == null) return null;

            try
            {
                // 1. 优先查找 Document 类型元素（如浏览器、聊天窗口的内容区）
                var doc = windowElement.FindFirstDescendant(_cf.ByControlType(FlaUI.Core.Definitions.ControlType.Document));
                if (doc != null)
                {
                    OnLog("阅读: 找到 Document 内容区域");
                    return doc;
                }

                // 2. 查找具有大量文本子元素的 Pane（通常是主内容区）
                var children = windowElement.FindAllChildren();
                if (children.Length > 0)
                {
                    // 寻找子元素最多的 Pane（通常是内容区域而非工具栏）
                    AutomationElement bestPane = null;
                    int bestChildCount = 0;

                    foreach (var child in children)
                    {
                        try
                        {
                            // 跳过标题栏、菜单栏、工具栏、状态栏
                            if (child.ControlType == FlaUI.Core.Definitions.ControlType.TitleBar ||
                                child.ControlType == FlaUI.Core.Definitions.ControlType.MenuBar ||
                                child.ControlType == FlaUI.Core.Definitions.ControlType.ToolBar ||
                                child.ControlType == FlaUI.Core.Definitions.ControlType.StatusBar)
                                continue;

                            // 统计该子元素的后代数量（作为内容丰富度的指标）
                            var descendants = child.FindAllDescendants();
                            if (descendants.Length > bestChildCount)
                            {
                                bestChildCount = descendants.Length;
                                bestPane = child;
                            }
                        }
                        catch { }
                    }

                    if (bestPane != null && bestChildCount > 5)
                    {
                        OnLog($"阅读: 找到内容区域 (ControlType={bestPane.ControlType}, {bestChildCount} 个后代元素)");
                        return bestPane;
                    }
                }

                // 3. 没有找到更好的内容区域，返回整个窗口
                OnLog("阅读: 使用整个窗口元素");
                return windowElement;
            }
            catch (Exception ex)
            {
                _logger.Warn("Player", $"查找内容区域失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 递归提取 UIA 元素的所有文本（FlaUI 版本）。
        /// 支持 Name 属性、ValuePattern、TextPattern、RangePattern。
        /// </summary>
        private string ExtractAllText(AutomationElement element, int depth, int maxDepth)
        {
            if (depth > maxDepth || element == null) return "";

            var texts = new List<string>();
            try
            {
                // 1. 获取当前元素的 Name 属性
                string name = "";
                try { name = element.Name ?? ""; } catch { }
                if (!string.IsNullOrWhiteSpace(name))
                    texts.Add(name);

                // 2. 获取 ValuePattern 的值（适用于 Edit、TextBox 等输入控件）
                try
                {
                    var valuePattern = element.Patterns.Value.PatternOrDefault;
                    if (valuePattern != null)
                    {
                        string val = valuePattern.Value.Value ?? "";
                        if (!string.IsNullOrWhiteSpace(val) && val != name)
                            texts.Add(val);
                    }
                }
                catch { /* ValuePattern 不可用则忽略 */ }

                // 3. 获取 TextPattern 的值（适用于富文本、聊天消息等）
                try
                {
                    var textPattern = element.Patterns.Text.PatternOrDefault;
                    if (textPattern != null)
                    {
                        var documentRange = textPattern.DocumentRange;
                        if (documentRange != null)
                        {
                            string text = documentRange.GetText(int.MaxValue);
                            if (!string.IsNullOrWhiteSpace(text) && text != name)
                                texts.Add(text);
                        }
                    }
                }
                catch { /* TextPattern 不可用则忽略 */ }

                // 4. 递归子元素
                var children = element.FindAllChildren();
                foreach (var child in children)
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

                IntPtr hwnd;
                string content;

                if (_targetElement != null && _targetWindow != IntPtr.Zero)
                {
                    hwnd = _targetWindow;
                    content = ExtractAllText(_targetElement, 0, 10);
                }
                else if (_targetWindow != IntPtr.Zero)
                {
                    hwnd = _targetWindow;
                    var windowElement = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(windowElement);
                    content = ExtractAllText(contentElement ?? windowElement, 0, 10);
                }
                else
                {
                    hwnd = ResolveTargetWindow(node.WindowTitle);
                    if (hwnd == IntPtr.Zero) { OnLog("正则识别: 无法获取目标窗口"); return; }

                    var element = _automation.FromHandle(hwnd);
                    var contentElement = FindContentElement(element);
                    content = ExtractAllText(contentElement ?? element, 0, 10);
                }

                int len = User32.GetWindowTextLength(hwnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    User32.GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();
                }

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
                        MatchedValue = "",
                        OutputParamName = node.OutputParamName
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
                    Matches = matchItems,
                    OutputParamName = node.OutputParamName
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

        /// <summary>
        /// 记录点击步骤的成功/失败结果到变量，供后续 If 判断引用。
        /// 始终写入固定变量 last_click_success；若点击步骤设置了 OutputParamName，也写入该变量。
        /// </summary>
        private void RecordClickResult(RecordedAction node, bool success)
        {
            string val = success ? "true" : "false";
            SetVariable("last_click_success", val);
            if (!string.IsNullOrEmpty(node.OutputParamName))
                SetVariable(node.OutputParamName, val);
        }

        /// <summary>
        /// 确保窗口在前台，如果最小化则先恢复。
        /// 使用 ALT 键欺骗 + AttachThreadInput + ShowWindow 组合绕过 Windows 前台锁定限制。
        /// 注意：若目标应用以管理员权限运行而本工具非管理员，受 UIPI 限制无法切换。
        /// </summary>
        private void EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            // 取真正的顶层 owner 窗口（EnumWindows 可能返回子窗口/工具窗口）
            IntPtr root = User32.GetAncestor(hwnd, User32.GA_ROOTOWNER);
            if (root == IntPtr.Zero) root = User32.GetAncestor(hwnd, User32.GA_ROOT);
            if (root != IntPtr.Zero) hwnd = root;

            // 最小化则先恢复（SW_RESTORE=9）
            if (User32.IsIconic(hwnd))
            {
                User32.ShowWindow(hwnd, 9);
                OnLog("窗口从最小化恢复");
            }

            // 已经是前台窗口则无需操作
            if (User32.GetForegroundWindow() == hwnd) return;

            try
            {
                uint curThread = User32.GetCurrentThreadId();

                // 尝试最多 3 次，每次用不同的手段，直到目标窗口成为前台
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr foreHwnd = User32.GetForegroundWindow();
                    // 注意：GetWindowThreadProcessId 必须带 out 参数，否则 native 会往空指针写 PID 导致崩溃
                    uint foreThread = 0;
                    if (foreHwnd != IntPtr.Zero)
                    {
                        int forePid;
                        foreThread = (uint)User32.GetWindowThreadProcessId(foreHwnd, out forePid);
                    }

                    // AttachThreadInput 绑定当前前台线程输入到本线程
                    bool attached = false;
                    if (foreThread != 0 && foreThread != curThread)
                    {
                        attached = User32.AttachThreadInput(curThread, foreThread, true);
                    }

                    // ALT 键欺骗：按下并释放 ALT，解除前台锁定
                    User32.keybd_event(User32.VK_MENU, 0, 0, 0);
                    User32.keybd_event(User32.VK_MENU, 0, 0x0002, 0);

                    // 组合调用：ShowWindow 激活 + BringWindowToTop 置顶 + SetForegroundWindow 设前台
                    User32.ShowWindow(hwnd, 5);  // SW_SHOW
                    User32.ShowWindow(hwnd, 9);  // SW_RESTORE
                    User32.BringWindowToTop(hwnd);
                    bool ok = User32.SetForegroundWindow(hwnd);

                    if (attached)
                        User32.AttachThreadInput(curThread, foreThread, false);

                    // 短暂等待窗口响应
                    System.Threading.Thread.Sleep(80);

                    // 验证是否成功切到前台
                    if (User32.GetForegroundWindow() == hwnd)
                        return;

                    if (!ok && attempt == 0)
                        OnLog($"SetForegroundWindow 返回 false (尝试 {attempt + 1}/3)，可能被前台锁定");
                }

                // 最后手段：强制置顶（不抢占前台但提到 Z 序顶部）
                OnLog("常规前置失败，尝试强制置顶");
                User32.BringWindowToTop(hwnd);
            }
            catch (Exception ex)
            {
                OnLog($"切窗前置异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换窗口到前台。
        /// SwitchAll=false（默认）：仅切换匹配的主窗口到前台。
        /// SwitchAll=true：把该进程名的所有窗口都恢复显示并置顶，最后让主窗口成为前台。
        /// </summary>
        private void DoSwitchToWindow(RecordedAction node)
        {
            string title = node.WindowTitle ?? node.Parameter;
            if (string.IsNullOrEmpty(title))
            {
                OnLog("切窗: 未指定窗口标题");
                return;
            }

            // SwitchAll=true：打开该进程名的全部窗口
            if (node.SwitchAll)
            {
                SwitchAllWindows(title);
                return;
            }

            // SwitchAll=false：仅切换单个窗口
            SwitchSingleWindow(title);
        }

        /// <summary>
        /// 仅切换单个匹配窗口到前台（优先标题匹配，回退进程名主窗口）
        /// </summary>
        private void SwitchSingleWindow(string title)
        {
            IntPtr hwnd = FindTargetWindow(title);
            if (hwnd == IntPtr.Zero)
            {
                OnLog($"切窗失败: 未找到窗口 \"{title}\"（按标题和进程名均未匹配）");
                return;
            }

            int len = User32.GetWindowTextLength(hwnd);
            string foundTitle = "";
            if (len > 0)
            {
                var sb = new System.Text.StringBuilder(len + 1);
                User32.GetWindowText(hwnd, sb, sb.Capacity);
                foundTitle = sb.ToString();
            }
            User32.GetWindowThreadProcessId(hwnd, out int pid);
            string procName = "";
            try { procName = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { }

            EnsureWindowForeground(hwnd);

            bool success = User32.GetForegroundWindow() == hwnd;
            if (success)
                OnLog($"已切换到窗口: {foundTitle} (进程: {procName}, PID: {pid})");
            else
                OnLog($"切窗未完全生效: 目标=\"{foundTitle}\" (进程: {procName})，可能目标应用以管理员权限运行（UIPI 限制）或窗口拒绝激活");
        }

        /// <summary>
        /// 打开该进程名的所有窗口：全部恢复显示并置顶，最后主窗口置前
        /// </summary>
        private void SwitchAllWindows(string title)
        {
            var allWindows = FindAllWindowsByProcessName(title);
            if (allWindows.Count == 0)
            {
                // 没有按进程名匹配到，回退到单个窗口切换（可能是按标题输入的）
                OnLog($"按进程名 \"{title}\" 未找到窗口，尝试按标题切换单个窗口");
                SwitchSingleWindow(title);
                return;
            }

            OnLog($"按进程名 \"{title}\" 找到 {allWindows.Count} 个窗口，全部恢复显示并置顶");

            // 按面积从小到大排序：先恢复小窗口，最后把最大的主窗口设为前台
            allWindows.Sort((a, b) => a.area.CompareTo(b.area));
            IntPtr mainWindow = allWindows[^1].hwnd; // 最大的作为主窗口

            foreach (var (wh, wtitle, _) in allWindows)
            {
                try
                {
                    // 最小化的先恢复
                    if (User32.IsIconic(wh))
                        User32.ShowWindow(wh, 9); // SW_RESTORE
                    // 显示并置顶
                    User32.ShowWindow(wh, 5); // SW_SHOW
                    User32.BringWindowToTop(wh);
                }
                catch { }
            }

            // 最后把主窗口设为前台焦点
            EnsureWindowForeground(mainWindow);

            // 日志汇总
            foreach (var (wh, wtitle, area) in allWindows)
            {
                User32.GetWindowThreadProcessId(wh, out int pid);
                string pn = "";
                try { pn = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { }
                OnLog($"  已恢复窗口: \"{wtitle}\" (进程: {pn}, PID: {pid}, {area / 1000}k px²)");
            }

            bool ok = User32.GetForegroundWindow() == mainWindow;
            OnLog(ok ? "全部窗口已打开，主窗口已置前" : "全部窗口已恢复显示，但主窗口未能抢占前台（可能受 UIPI 限制）");
        }

        /// <summary>
        /// 条件表达式求值器。
        /// 支持: ==, !=, >, <, >=, <=, contains, matches
        /// 变量引用 {varName} 已在 ResolveVariables 中解析
        /// </summary>
        private bool EvaluateCondition(string? expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return false;

            string expr = expression.Trim();

            // 解析 {varName} 变量引用
            foreach (var kvp in _variables)
                expr = expr.Replace($"{{{kvp.Key}}}", kvp.Value);

            // contains 运算符: A contains B
            var containsMatch = System.Text.RegularExpressions.Regex.Match(expr, @"^(.+?)\s+contains\s+(.+)$");
            if (containsMatch.Success)
                return containsMatch.Groups[1].Value.Trim().Contains(containsMatch.Groups[2].Value.Trim());

            // matches 运算符: A matches B (正则)
            var matchesMatch = System.Text.RegularExpressions.Regex.Match(expr, @"^(.+?)\s+matches\s+(.+)$");
            if (matchesMatch.Success)
            {
                try { return System.Text.RegularExpressions.Regex.IsMatch(matchesMatch.Groups[1].Value.Trim(), matchesMatch.Groups[2].Value.Trim()); }
                catch { return false; }
            }

            // 数值比较: >=, <=, >, <
            var numCmpMatch = System.Text.RegularExpressions.Regex.Match(expr, @"^(.+?)\s*(>=|<=|>|<)\s*(.+)$");
            if (numCmpMatch.Success
                && double.TryParse(numCmpMatch.Groups[1].Value.Trim(), out double left)
                && double.TryParse(numCmpMatch.Groups[3].Value.Trim(), out double right))
            {
                return numCmpMatch.Groups[2].Value switch
                {
                    ">=" => left >= right,
                    "<=" => left <= right,
                    ">" => left > right,
                    "<" => left < right,
                    _ => false
                };
            }

            // 字符串比较: ==, !=
            var strCmpMatch = System.Text.RegularExpressions.Regex.Match(expr, @"^(.+?)\s*(==|!=)\s*(.+)$");
            if (strCmpMatch.Success)
            {
                string lhs = strCmpMatch.Groups[1].Value.Trim();
                string rhs = strCmpMatch.Groups[3].Value.Trim();
                // 去除引号
                if ((lhs.StartsWith('"') && lhs.EndsWith('"')) || (lhs.StartsWith('\'') && lhs.EndsWith('\'')))
                    lhs = lhs[1..^1];
                if ((rhs.StartsWith('"') && rhs.EndsWith('"')) || (rhs.StartsWith('\'') && rhs.EndsWith('\'')))
                    rhs = rhs[1..^1];
                return strCmpMatch.Groups[2].Value == "==" ? lhs == rhs : lhs != rhs;
            }

            // Truthy 检查: 非空、非 "0"、非 "false" 视为 true
            return !string.IsNullOrWhiteSpace(expr)
                && !expr.Equals("0", StringComparison.OrdinalIgnoreCase)
                && !expr.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

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
                ConditionExpression = ResolveStr(node.ConditionExpression),
                GotoNodeId = node.GotoNodeId,           // NodeId 不做变量替换
                TrueGotoNodeId = node.TrueGotoNodeId,
                TargetScript = ResolveStr(node.TargetScript),
                SwitchAll = node.SwitchAll,
                IsEnabled = node.IsEnabled,
                CreatedAt = node.CreatedAt,
                XPath = ResolveStr(node.XPath),
                SiblingIndex = node.SiblingIndex,
                RuntimeId = node.RuntimeId
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
