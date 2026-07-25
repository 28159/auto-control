using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Native;
using WeChatAutomation.Core.Services;
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
        private static readonly Random _rng = new();
        private CancellationTokenSource _cts;
        private bool _isPlaying;
        private IntPtr _targetWindow = IntPtr.Zero;
        private AutomationElement _targetElement = null; // 用户选择的具体 UIA 元素（用于阅读）
        private readonly HumanInputSimulator _simulator = new();
        private VisionDetector _visionDetector;
        private static readonly System.Net.Http.HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

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

        public bool InitVisionDetector(string templatePath, string labelsPath = null)
        {
            _visionDetector?.Dispose();
            _visionDetector = new VisionDetector();
            return _visionDetector.LoadModel(templatePath, labelsPath);
        }

        /// <summary>直接从 Bitmap 加载模板到检测器（录制后即时回放单步用）。</summary>
        public bool InitVisionDetectorFromBitmap(Bitmap template, string label)
        {
            _visionDetector?.Dispose();
            _visionDetector = new VisionDetector();
            return _visionDetector.LoadTemplateFromBitmap(template, label);
        }

        public bool IsVisionReady => _visionDetector?.IsLoaded == true;

        /// <summary>当前视觉检测器（UI 可读取模板元数据；勿 Dispose）。</summary>
        public Vision.VisionDetector VisionDetector => _visionDetector;

        /// <summary>
        /// 当前已加载模板的文件名（便于判断是否需要切换）。
        /// </summary>
        public string LoadedModelFileName =>
            _visionDetector?.IsLoaded == true && !string.IsNullOrEmpty(_visionDetector.TemplatePath)
                ? System.IO.Path.GetFileName(_visionDetector.TemplatePath)
                : null;

        /// <summary>
        /// 确保视觉检测器就绪。模板匹配模式下无需全局模型，仅做轻量检查。
        /// 保留方法名以减少上层调用改动；实际模板在每个 Vision 步骤执行时按需加载。
        /// </summary>
        public bool EnsureVisionModel(string modelFileName)
        {
            // 模板匹配模式：检测器在 DoClickByVisionAsync 中按步骤加载，此处仅返回 true
            return true;
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

        /// <summary>循环控制信号：Break 置位让当前循环体执行列表立即退出并结束循环。</summary>
        private bool _loopBreak;
        /// <summary>循环控制信号：Continue 置位让当前循环体执行列表立即退出但进入下一轮。</summary>
        private bool _loopContinue;

        /// <summary>子脚本执行结果（SubScriptRequested 处理方回填），null=未执行/执行失败</summary>
        private bool? _subScriptSuccess;

        /// <summary>
        /// 定位重试期间静默 UIA 诊断日志（段修剪/未找到等）。
        /// 回放串行执行，该标志仅在单次 TryLocateElement 调用期间置位，不会影响并发。
        /// </summary>
        private bool _mutedLocateLog;

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
            _loopBreak = false;
            _loopContinue = false;
            _subScriptSuccess = null;
            _totalIterations = 0;
            OnLog($"开始回放，共 {nodes.Count} 步" +
                  (string.IsNullOrEmpty(visionModelFileName) ? "" : $"（视觉模型: {visionModelFileName}）"));

            try
            {
                await ExecuteActionList(nodes);

                if (_stopRequested)
                    OnLog(_subScriptSuccess == true ? "已执行子脚本并停止当前脚本" : "已停止当前脚本后续步骤");
                else if (_totalIterations >= MaxTotalIterations)
                    OnLog($"回放达到最大迭代次数 ({MaxTotalIterations})，可能存在死循环，已中止");

                if (!_cts.IsCancellationRequested && !_stopRequested && _totalIterations < MaxTotalIterations)
                { OnLog("回放完成"); PlayCompleted?.Invoke(this, EventArgs.Empty); }
            }
            catch (OperationCanceledException) { OnLog("回放已取消"); }
            catch (Exception ex) { OnLog($"回放异常: {ex.Message}"); PlayError?.Invoke(this, ex.Message); }
            finally { _isPlaying = false; _cts?.Dispose(); _cts = null; }
        }

        /// <summary>执行一组步骤列表（主流程或 If 分支子步骤），支持递归调用。</summary>
        private async Task ExecuteActionList(List<RecordedAction> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;

            // 每个列表构建自己的 NodeId→索引映射（Goto 仅在当前列表范围内跳转）
            var nodeIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; i++)
                if (!string.IsNullOrEmpty(nodes[i].NodeId))
                    nodeIndexMap[nodes[i].NodeId] = i;

            int currentIndex = 0;
            while (currentIndex < nodes.Count && _totalIterations++ < MaxTotalIterations)
            {
                if (_cts.IsCancellationRequested) break;
                if (_stopRequested) break;
                // 循环控制信号：Break/Continue 让当前执行列表（可能是循环体）立即退出
                if (_loopBreak || _loopContinue) break;
                var node = nodes[currentIndex];
                if (!node.IsEnabled) { currentIndex++; continue; }
                if (node.DelayMs > 0) await Task.Delay(node.DelayMs, _cts.Token);

                int? jumpTo = await ExecuteNode(node, nodeIndexMap);

                // 步骤执行完成后：按步骤配置执行随机等待和鼠标移动
                if (node.RandomWaitEnabled)
                {
                    int minSec = Math.Clamp(node.RandomWaitMinSec, 1, 60);
                    int maxSec = Math.Clamp(node.RandomWaitMaxSec, 1, 60);
                    if (maxSec < minSec) maxSec = minSec;
                    int sec = _rng.Next(minSec, maxSec + 1);
                    OnLog($"步骤后随机等待 {minSec}~{maxSec}秒，本次 {sec}秒");
                    await Task.Delay(sec * 1000, _cts.Token);
                }
                if (node.RandomMouseMoveEnabled)
                {
                    await RandomMouseWander(node.RandomMoveMinOffset, node.RandomMoveMaxOffset);
                }
                currentIndex = jumpTo.HasValue ? jumpTo.Value : currentIndex + 1;
            }
        }

        /// <summary>跨递归调用的总迭代计数，防止死循环。</summary>
        private int _totalIterations;
        private const int MaxTotalIterations = 100000;

        /// <summary>
        /// 随机鼠标漂移：模拟人类在操作间隙的鼠标无意识移动。
        /// 从当前位置随机偏移 minOffset~maxOffset 像素，使用贝塞尔曲线移动。
        /// </summary>
        private async Task RandomMouseWander(int minOffset = 30, int maxOffset = 120)
        {
            try
            {
                User32.GetCursorPos(out User32.POINT current);
                // 随机偏移方向和距离
                int offset = _rng.Next(minOffset, maxOffset + 1);
                double angle = _rng.NextDouble() * Math.PI * 2;
                int targetX = current.x + (int)(offset * Math.Cos(angle));
                int targetY = current.y + (int)(offset * Math.Sin(angle));
                // 限制在屏幕范围内
                int screenW = User32.GetSystemMetrics(User32.SM_CXSCREEN);
                int screenH = User32.GetSystemMetrics(User32.SM_CYSCREEN);
                targetX = Math.Clamp(targetX, 0, screenW - 1);
                targetY = Math.Clamp(targetY, 0, screenH - 1);
                await _simulator.MoveToAsync(targetX, targetY);
            }
            catch
            {
                // 鼠标漂移失败不影响主流程
            }
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
                    {
                        // 随机等待模式
                        if (resolvedNode.RandomWaitEnabled)
                        {
                            int minSec = Math.Clamp(resolvedNode.RandomWaitMinSec, 1, 60);
                            int maxSec = Math.Clamp(resolvedNode.RandomWaitMaxSec, 1, 60);
                            if (maxSec < minSec) maxSec = minSec;
                            int sec = _rng.Next(minSec, maxSec + 1);
                            OnLog($"随机等待 {minSec}~{maxSec}秒，本次 {sec}秒");
                            await Task.Delay(sec * 1000, _cts.Token);
                        }
                        else if (int.TryParse(resolvedNode.Parameter, out int ms))
                        {
                            OnLog($"等待 {ms}ms");
                            await Task.Delay(ms, _cts.Token);
                        }
                        break;
                    }

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

                        // 新版分支行为：成立走 TrueBranch，不成立走 FalseBranch，各自 4 种行为。
                        // 行为为 Continue(默认) 时回退旧字段逻辑，兼容旧脚本。
                        IfBranchAction branchAction = condResult ? resolvedNode.TrueBranch : resolvedNode.FalseBranch;
                        string? branchScript = condResult ? resolvedNode.TrueBranchScript : resolvedNode.FalseBranchScript;
                        var branchActions = condResult ? resolvedNode.TrueActions : resolvedNode.FalseActions;
                        string branchName = condResult ? "成立" : "不成立";

                        // 旧脚本兼容：行为为 Continue 时按旧字段推断
                        if (branchAction == IfBranchAction.Continue)
                        {
                            // 旧 TargetScript 模式：成立->执行子脚本并停止；不成立->停止
                            if (condResult && !string.IsNullOrEmpty(resolvedNode.TargetScript))
                            {
                                OnLog($"  -> 条件成立，执行子脚本: {resolvedNode.TargetScript}（执行完停止当前脚本）");
                                _subScriptSuccess = ExecuteSubScript(resolvedNode.TargetScript);
                                _stopRequested = true;
                                return null;
                            }
                            if (!condResult && !string.IsNullOrEmpty(resolvedNode.TargetScript))
                            {
                                OnLog($"  -> 条件不成立，停止当前脚本后续步骤");
                                _stopRequested = true;
                                return null;
                            }

                            // 旧树形分支模式：有子步骤时递归执行对应分支，执行完继续主流程
                            if (branchActions != null && branchActions.Count > 0)
                            {
                                OnLog($"  -> 执行{(condResult ? "True" : "False")}分支子步骤 ({branchActions.Count} 步)");
                                await ExecuteActionList(branchActions);
                                break; // 子步骤执行完继续主流程下一步
                            }

                            // 旧跳转模式
                            string? gotoId = condResult ? node.TrueGotoNodeId : node.GotoNodeId;
                            if (!string.IsNullOrEmpty(gotoId) && nodeIndexMap.TryGetValue(gotoId, out int gotoIdx))
                            {
                                OnLog($"  -> 跳转到 {(condResult ? "true" : "false")} 分支: {gotoId}");
                                return gotoIdx;
                            }
                            // 无任何配置则继续下一步
                            break;
                        }

                        // 新版显式行为分派
                        switch (branchAction)
                        {
                            case IfBranchAction.RunScript:
                                OnLog($"  -> 条件{branchName}，执行子脚本: {branchScript}（执行完停止当前脚本）");
                                _subScriptSuccess = ExecuteSubScript(branchScript ?? "");
                                _stopRequested = true;
                                return null;

                            case IfBranchAction.RunActions:
                                if (branchActions != null && branchActions.Count > 0)
                                {
                                    OnLog($"  -> 条件{branchName}，执行分支子动作 ({branchActions.Count} 步) 后停止当前脚本");
                                    await ExecuteActionList(branchActions);
                                }
                                else
                                {
                                    OnLog($"  -> 条件{branchName}，分支无子动作，停止当前脚本");
                                }
                                _stopRequested = true;
                                return null;

                            case IfBranchAction.Stop:
                                OnLog($"  -> 条件{branchName}，停止当前脚本执行");
                                _stopRequested = true;
                                return null;

                            default: // Continue：继续主流程下一步
                                OnLog($"  -> 条件{branchName}，继续主流程");
                                break;
                        }
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

                case ActionType.While:
                    {
                        // 条件循环：条件成立时重复执行 TrueActions（循环体）
                        int max = resolvedNode.MaxLoopCount > 0 ? resolvedNode.MaxLoopCount : 1000;
                        int iter = 0;
                        while (iter < max && !_cts.IsCancellationRequested && !_stopRequested)
                        {
                            // 求值循环条件（复用 If 的便捷模式：空表达式+OutputParamName 则判断是否有值）
                            bool keep;
                            if (string.IsNullOrWhiteSpace(resolvedNode.ConditionExpression) && !string.IsNullOrEmpty(resolvedNode.OutputParamName))
                            {
                                string val = GetVariable(resolvedNode.OutputParamName) ?? "";
                                keep = !string.IsNullOrWhiteSpace(val)
                                    && !val.Equals("0", StringComparison.OrdinalIgnoreCase)
                                    && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                            }
                            else
                            {
                                keep = EvaluateCondition(resolvedNode.ConditionExpression);
                            }
                            if (!keep) { OnLog($"  循环结束（条件不成立），共执行 {iter} 轮"); break; }

                            iter++;
                            OnLog($"  循环第 {iter} 轮（上限 {max}）");
                            await ExecuteActionList(resolvedNode.TrueActions ?? new List<RecordedAction>());

                            // Break：结束循环
                            if (_loopBreak) { _loopBreak = false; _loopContinue = false; OnLog($"  循环被 Break 跳出，共执行 {iter} 轮"); break; }
                            // Continue：清除信号，进入下一轮
                            if (_loopContinue) { _loopContinue = false; }
                        }
                        if (iter >= max && !_stopRequested)
                            OnLog($"  循环达到上限 {max}，已中止（可能死循环）");
                        break;
                    }

                case ActionType.Loop:
                    {
                        // 固定次数循环
                        int count = resolvedNode.LoopCount > 0 ? resolvedNode.LoopCount : 0;
                        for (int i = 0; i < count; i++)
                        {
                            if (_cts.IsCancellationRequested || _stopRequested) break;
                            OnLog($"  固定循环第 {i + 1}/{count} 轮");
                            await ExecuteActionList(resolvedNode.TrueActions ?? new List<RecordedAction>());
                            if (_loopBreak) { _loopBreak = false; _loopContinue = false; OnLog($"  循环被 Break 跳出，已执行 {i + 1} 轮"); break; }
                            if (_loopContinue) { _loopContinue = false; }
                        }
                        break;
                    }

                case ActionType.Try:
                    {
                        // 容错：执行 Try 体，异常时执行 Catch 体
                        try
                        {
                            OnLog("  Try 执行体");
                            await ExecuteActionList(resolvedNode.TrueActions ?? new List<RecordedAction>());
                        }
                        catch (Exception ex)
                        {
                            OnLog($"  Try 体异常: {ex.Message} -> 执行 Catch 体");
                            try { await ExecuteActionList(resolvedNode.FalseActions ?? new List<RecordedAction>()); }
                            catch (Exception ex2) { OnLog($"  Catch 体异常: {ex2.Message}"); }
                        }
                        break;
                    }

                case ActionType.Break:
                    OnLog("  Break：跳出当前循环");
                    _loopBreak = true;
                    break;

                case ActionType.Continue:
                    OnLog("  Continue：进入下一轮");
                    _loopContinue = true;
                    break;

                case ActionType.HttpWait:
                    {
                        string key = resolvedNode.WaitKey ?? "";
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            OnLog("等待HTTP失败: 未设置 key");
                            break;
                        }
                        int timeout = resolvedNode.WaitTimeoutMs;
                        OnLog($"等待外部 HTTP 触发: key={key}" + (timeout > 0 ? $"（超时 {timeout}ms）" : "（无限等待）") +
                              $"，可 POST /api/wait/{key} 唤醒");
                        try
                        {
                            string? payload = await WaitSignalHub.WaitAsync(key, timeout, _cts.Token);
                            OnLog($"已收到 HTTP 触发: key={key}" + (payload != null ? $"，内容长度 {payload.Length}" : ""));
                            if (!string.IsNullOrEmpty(resolvedNode.ResponseVarName) && payload != null)
                                SetVariable(resolvedNode.ResponseVarName, payload);
                        }
                        catch (TimeoutException)
                        {
                            OnLog($"等待HTTP超时: key={key}");
                        }
                        catch (OperationCanceledException)
                        {
                            OnLog($"等待HTTP被取消: key={key}");
                            throw;
                        }
                        break;
                    }

                case ActionType.HttpCall:
                    {
                        try
                        {
                            string url = resolvedNode.HttpUrl ?? "";
                            if (string.IsNullOrWhiteSpace(url)) { OnLog("调用HTTP失败: 未设置 URL"); break; }
                            var method = new System.Net.Http.HttpMethod(
                                string.IsNullOrWhiteSpace(resolvedNode.HttpMethod) ? "GET" : resolvedNode.HttpMethod.ToUpperInvariant());
                            using var reqMsg = new System.Net.Http.HttpRequestMessage(method, url);

                            // 请求头（每行 Key: Value）
                            if (!string.IsNullOrWhiteSpace(resolvedNode.HttpHeaders))
                            {
                                foreach (var line in resolvedNode.HttpHeaders.Split('\n'))
                                {
                                    var t = line.Trim();
                                    int colon = t.IndexOf(':');
                                    if (colon > 0)
                                    {
                                        string hk = t[..colon].Trim();
                                        string hv = t[(colon + 1)..].Trim();
                                        if (hk.Length > 0) reqMsg.Headers.TryAddWithoutValidation(hk, hv);
                                    }
                                }
                            }

                            // 请求体
                            if (!string.IsNullOrEmpty(resolvedNode.HttpBody) && method != System.Net.Http.HttpMethod.Get)
                                reqMsg.Content = new System.Net.Http.StringContent(resolvedNode.HttpBody,
                                    System.Text.Encoding.UTF8, "application/json");

                            OnLog($"调用 HTTP {method} {url}");
                            using var resp = await _httpClient.SendAsync(reqMsg, _cts.Token);
                            string respBody = await resp.Content.ReadAsStringAsync(_cts.Token);
                            OnLog($"HTTP 响应 {(int)resp.StatusCode}，内容长度 {respBody.Length}");
                            if (!string.IsNullOrEmpty(resolvedNode.ResponseVarName))
                                SetVariable(resolvedNode.ResponseVarName, respBody);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { OnLog($"调用HTTP失败: {ex.Message}"); }
                        break;
                    }

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
            // 账号切换/窗口刚激活后，主界面可能尚未加载完成，UIA 树里还没有目标元素
            // （如微信切换账号后主界面延迟出现）。定位失败时短暂重试等待界面就绪，
            // 避免立即报"UIA 未找到"误判。
            const int locateRetryTimeoutMs = 8000;
            const int locateRetryIntervalMs = 400;

            // 第一次尝试：正常输出诊断日志（段修剪/未找到等），便于排查
            var (found, hwnd, rect) = TryLocateElement(node);

            // 失败则静默重试，等待目标元素出现
            int retriedMs = 0;
            while (!found && retriedMs < locateRetryTimeoutMs)
            {
                if (retriedMs == 0)
                    OnLog($"界面可能未就绪，等待重试定位（最多 {locateRetryTimeoutMs / 1000}s）...");
                // 未激活的窗口 UIA 树可能不完整，重试前确保窗口在前台
                if (hwnd != IntPtr.Zero) EnsureWindowForeground(hwnd);

                int delay = Math.Min(locateRetryIntervalMs, locateRetryTimeoutMs - retriedMs);
                try { await Task.Delay(delay, _cts.Token); }
                catch (OperationCanceledException) { return false; }
                retriedMs += delay;

                // 重试期间静默诊断日志，避免段修剪信息刷屏
                _mutedLocateLog = true;
                try { (found, hwnd, rect) = TryLocateElement(node); }
                finally { _mutedLocateLog = false; }
            }
            if (found && retriedMs > 0)
                OnLog($"重试定位成功（等待约 {retriedMs}ms 后元素出现）");

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
            // 模板匹配模式：每个视觉步骤绑定自己的模板图片，按需加载
            string templatePath = node.TemplateImage;
            if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
            {
                OnLog($"视觉模式失败: 模板图片不存在 {(string.IsNullOrEmpty(templatePath) ? "(未设置)" : templatePath)}");
                return false;
            }

            // 加载该步骤的模板（与已加载不同时重新加载）
            if (!IsVisionReady || !string.Equals(_visionDetector?.TemplatePath, templatePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!InitVisionDetector(templatePath))
                {
                    OnLog("视觉模式失败: 模板加载失败");
                    return false;
                }
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

            string label = node.VisionLabel ?? "template";
            // 模板匹配阈值默认 0.7（原 YOLO 的 0.3 不适用于归一化互相关）
            float confThreshold = node.VisionConfThreshold > 0 ? node.VisionConfThreshold : 0.7f;

            // 同一窗口常有多个相似元素。优先点离录制坐标最近的匹配，避免点错。
            // node.X/Y 是录制时的屏幕坐标，转成窗口局部坐标后参与择近。
            Detection detection;
            bool hasRef = node.X > 0 || node.Y > 0;
            if (hasRef)
            {
                int localRefX = (int)(node.X - winX);
                int localRefY = (int)(node.Y - winY);
                int tolerance = (int)(Math.Min(winW, winH) * 0.45);
                detection = _visionDetector.FindNearest(screenshot, label, localRefX, localRefY, tolerance, confThreshold);
            }
            else
            {
                detection = _visionDetector.FindBest(screenshot, label, confThreshold);
            }

            if (detection == null)
            {
                OnLog($"视觉模式失败: 未匹配到模板 \"{label}\" (匹配度阈值: {confThreshold})");
                return false;
            }

            // 捕获视觉检测结果，供回传到服务器
            try
            {
                var allDetections = _visionDetector.Detect(screenshot, null, confThreshold);
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

            OnLog($"视觉点击: {detection.Label} (匹配度:{detection.Confidence:P0}) @ 屏幕({screenX},{screenY}) 框({detection.X},{detection.Y} {detection.Width}x{detection.Height})" +
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

                // 判断录制的 ClassName 是否为窗口根元素的 ClassName。
                // 若相等（如 Qt 应用内部容器复用窗口类名 Qt51514QWindowIcon），
                // 则纯 ClassName 策略无法区分控件，应跳过，避免匹配到任意内部容器。
                bool classNameIsWindowRoot = !string.IsNullOrEmpty(node.ClassName)
                    && !string.IsNullOrEmpty(root.ClassName)
                    && string.Equals(node.ClassName, root.ClassName, StringComparison.Ordinal);

                // 策略4: ClassName + ControlType
                if (element == null && !string.IsNullOrEmpty(node.ClassName) && !classNameIsWindowRoot)
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
                            // ControlType 无法解析时，纯 ClassName 查找过于宽松，跳过
                            _logger.Warn("Player", $"跳过纯 ClassName 策略: ControlType=\"{node.ControlType}\" 无法解析");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Player", $"ClassName 查找异常: {ex.Message}");
                    }
                }

                // 策略5: 非交互式 ControlType 提升回退
                // 如果录制的 ControlType 是非交互式（Text、Image、Pane、Group），
                // 尝试用交互式父类型 + 同名 Name 查找
                if (element == null && !string.IsNullOrEmpty(node.ElementName) && !string.IsNullOrEmpty(node.ControlType))
                {
                    try
                    {
                        var promotedType = PromoteControlType(node.ControlType);
                        if (promotedType != null)
                        {
                            var allMatches = root.FindAllDescendants(
                                _cf.ByName(node.ElementName).And(_cf.ByControlType(promotedType.Value)));
                            if (allMatches.Length > 0)
                            {
                                int idx = Math.Min(node.SiblingIndex, allMatches.Length - 1);
                                element = allMatches[idx];
                                matchMethod = $"Name={node.ElementName}+PromotedControlType={promotedType}[{idx}]";
                                OnLog($"非交互式类型提升: {node.ControlType} -> {promotedType} + Name={node.ElementName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Player", $"ControlType 提升查找异常: {ex.Message}");
                    }
                }

                // 策略6: ClassName + 提升后 ControlType
                if (element == null && !string.IsNullOrEmpty(node.ClassName) && !string.IsNullOrEmpty(node.ControlType) && !classNameIsWindowRoot)
                {
                    try
                    {
                        var promotedType = PromoteControlType(node.ControlType);
                        if (promotedType != null)
                        {
                            element = root.FindFirstDescendant(
                                _cf.ByClassName(node.ClassName).And(_cf.ByControlType(promotedType.Value)));
                            if (element != null)
                                matchMethod = $"ClassName={node.ClassName}+PromotedControlType={promotedType}";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Player", $"ClassName+提升ControlType 查找异常: {ex.Message}");
                    }
                }

                // 策略7: 窗口级目标回退
                // 若录制的目标是窗口本身（Qt/Electron 应用 UIA 树暴露差，FromPoint 常返回窗口元素），
                // 且所有后代查找均失败，直接返回窗口根元素，由 DoClickByUIAPathAsync 使用录制坐标点击。
                if (element == null)
                {
                    bool targetIsWindow =
                        string.Equals(node.ControlType, "Window", StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(node.WindowTitle)
                            && !string.IsNullOrEmpty(node.ElementName)
                            && string.Equals(node.ElementName, node.WindowTitle, StringComparison.Ordinal))
                        || classNameIsWindowRoot;
                    if (targetIsWindow)
                    {
                        element = root;
                        matchMethod = "WindowRoot(窗口级目标)";
                        OnLog($"窗口级目标回退: 录制目标为窗口本身，使用窗口根元素 + 录制坐标({node.X:F0},{node.Y:F0})");
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
                    // 根匹配，从第二级开始逐级查找（支持段修剪）
                    var result = FindByPathWithPruning(current, segments, 1);
                    if (result != null) return result;
                }
                else
                {
                    // 根不精确匹配，尝试模糊匹配根
                    if (FuzzyMatchesSegment(current, firstSeg, out var score) && score.TotalScore > 20)
                    {
                        var result = FindByPathWithPruning(current, segments, 1);
                        if (result != null) return result;
                    }
                }
            }

            // // 开头或根不匹配：在任意深度查找匹配段序列的元素
            var deepResult = FindByXPathDeep(root, segments);
            if (deepResult != null) return deepResult;

            // 所有路径匹配策略失败，尝试叶子回退：
            // 如果最后一段是非交互式类型（Text、Image、Pane、Group），
            // 尝试匹配倒数第二段（交互式父元素）并返回它
            if (segments.Count >= 2)
            {
                var leafSeg = segments[^1];
                var nonInteractiveTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Text", "Image", "Pane", "Group", "Header", "HeaderItem", "Separator"
                };

                // 判断叶子段是否缺少可靠标识：无 Name 且无 AutomationId（仅靠 ControlType/ClassName）
                // 这种叶子（如 Button[@ClassName='mmui::XImage'] 图标子元素）定位不稳定，应回退到父段
                bool leafLacksReliableId = !leafSeg.HasReliableId();

                if (nonInteractiveTypes.Contains(leafSeg.ControlType) || leafLacksReliableId)
                {
                    string reason = nonInteractiveTypes.Contains(leafSeg.ControlType)
                        ? "是非交互式"
                        : "缺少可靠标识(无Name/AutoId)";
                    OnLog($"XPath 叶子回退: 最后一段 {leafSeg.ControlType} {reason}，尝试匹配父段");
                    var parentResult = FindByXPathUpTo(root, segments, segments.Count - 1, isDeep);
                    if (parentResult != null)
                    {
                        OnLog($"XPath 叶子回退成功: 返回 {parentResult.ControlType}[@Name='{parentResult.Name}']");
                        return parentResult;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 在子树中递归查找匹配 XPath 段序列的元素
        /// </summary>
        private AutomationElement FindByXPathDeep(AutomationElement root, List<XPathSegment> segments)
        {
            if (segments.Count == 0) return null;

            // 在所有后代中查找匹配第一段的元素
            var candidates = FindAllMatchingDescendants(root, segments[0]);
            // 如果精确匹配无结果，尝试模糊匹配
            if (candidates.Count == 0)
            {
                var fuzzyCandidates = FindAllMatchingDescendantsFuzzy(root, segments[0]);
                candidates = fuzzyCandidates.Select(c => c.elem).ToList();
            }

            foreach (var candidate in candidates)
            {
                if (segments.Count == 1) return candidate;

                // 递归匹配后续段（支持段修剪）
                var result = FindByPathWithPruning(candidate, segments, 1);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// 从指定父元素开始，按 XPath 段逐级查找。
        /// 支持段修剪：当某个段匹配失败时，跳过该段继续尝试后续段。
        /// 最多允许跳过 MaxSkippableSegments 个段。
        /// </summary>
        private const int MaxSkippableSegments = 3;

        private AutomationElement FindByPathWithPruning(AutomationElement parent, List<XPathSegment> segments, int startIndex)
        {
            // 先尝试严格路径匹配（不跳过任何段）
            var strictResult = FindByPathStrict(parent, segments, startIndex);
            if (strictResult != null) return strictResult;

            // 严格匹配失败，尝试修剪（跳过中间段）
            OnLog($"XPath 严格匹配失败，尝试段修剪（最多跳过 {MaxSkippableSegments} 段）");
            return FindByPathWithSkip(parent, segments, startIndex, 0);
        }

        /// <summary>
        /// 严格路径匹配：不跳过任何段
        /// </summary>
        private AutomationElement FindByPathStrict(AutomationElement parent, List<XPathSegment> segments, int startIndex)
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
        /// 带段跳过的路径匹配。
        /// skipped: 已跳过的段数
        /// </summary>
        private AutomationElement FindByPathWithSkip(AutomationElement parent, List<XPathSegment> segments, int startIndex, int skipped)
        {
            if (startIndex >= segments.Count) return parent; // 所有段已处理
            if (skipped > MaxSkippableSegments) return null; // 跳过太多段

            // 尝试匹配当前段
            var child = FindDescendantBySegment(parent, segments[startIndex]);
            if (child != null)
            {
                // 当前段匹配成功，继续匹配后续段
                var result = FindByPathWithSkip(child, segments, startIndex + 1, skipped);
                if (result != null) return result;
            }

            // 当前段匹配失败（或后续段失败），尝试跳过当前段
            // 限制：不跳过最后一段（叶子段必须匹配）；不跳过带可靠标识（Name/AutomationId）的段，
            // 这些是定位锚点，跳过会匹配到错误元素。只允许跳过无标识的中间容器段。
            if (startIndex + 1 < segments.Count && !segments[startIndex].HasReliableId())
            {
                string segName = segments[startIndex].Attributes.TryGetValue("Name", out var n) ? n : "";
                OnLog($"XPath 段修剪: 跳过段 {segments[startIndex].ControlType}[@Name='{segName}']");
                var skipResult = FindByPathWithSkip(parent, segments, startIndex + 1, skipped + 1);
                if (skipResult != null) return skipResult;
            }

            return null;
        }

        /// <summary>
        /// 查找 XPath 路径中到指定深度为止的元素（忽略后续段）。
        /// 用于叶子回退：当最后一段是非交互式类型时，只匹配到倒数第二段。
        /// </summary>
        private AutomationElement FindByXPathUpTo(AutomationElement root, List<XPathSegment> segments, int upToIndex, bool isDeep)
        {
            if (upToIndex <= 0 || upToIndex > segments.Count) return null;

            var truncatedSegments = segments.Take(upToIndex).ToList();

            AutomationElement current = root;
            if (!isDeep && truncatedSegments.Count > 0)
            {
                if (MatchesSegment(current, truncatedSegments[0]) ||
                    (FuzzyMatchesSegment(current, truncatedSegments[0], out var s) && s.TotalScore > 20))
                {
                    var result = FindByPathWithPruning(current, truncatedSegments, 1);
                    if (result != null) return result;
                }
            }

            return FindByXPathDeep(root, truncatedSegments);
        }

        /// <summary>
        /// 在后代元素中查找匹配指定段的元素（先查直接子元素，再查更深层后代）。
        /// 这比仅查直接子元素更健壮，因为 UI 树中常有中间容器元素。
        /// </summary>
        private AutomationElement FindDescendantBySegment(AutomationElement parent, XPathSegment segment)
        {
            try
            {
                // 索引可靠性判断：仅当段无 AutomationId 且无 Name（纯靠 ControlType/ClassName 标识）时，
                // 兄弟索引才用于消歧。带 AutomationId 或 Name 的段，索引跨账号/跨次启动不可靠（兄弟顺序变化），
                // 应优先按属性匹配，忽略索引。
                bool useIndex = segment.Index > 0 && ShouldUseIndex(segment);

                // 1. 先在直接子元素中精确查找（最精确，遵循 XPath 路径）
                var children = parent.FindAllChildren();
                var exactMatches = new List<AutomationElement>();

                foreach (var child in children)
                {
                    if (MatchesSegment(child, segment))
                        exactMatches.Add(child);
                }

                if (exactMatches.Count > 0)
                {
                    if (useIndex && exactMatches.Count > 1)
                    {
                        int idx = Math.Min(segment.Index - 1, exactMatches.Count - 1);
                        return exactMatches[idx];
                    }
                    // 无索引或仅一个匹配，取首个（属性已足够定位）
                    return exactMatches[0];
                }

                // 2. 直接子元素精确匹配失败，尝试子元素模糊匹配
                var fuzzyCandidates = new List<(AutomationElement elem, MatchScore score)>();
                foreach (var child in children)
                {
                    if (FuzzyMatchesSegment(child, segment, out var score))
                        fuzzyCandidates.Add((child, score));
                }

                if (fuzzyCandidates.Count > 0)
                {
                    fuzzyCandidates.Sort((a, b) => b.score.TotalScore.CompareTo(a.score.TotalScore));
                    AutomationElement best;
                    if (useIndex && fuzzyCandidates.Count > 1)
                    {
                        int idx = Math.Min(segment.Index - 1, fuzzyCandidates.Count - 1);
                        best = fuzzyCandidates[idx].elem;
                    }
                    else
                    {
                        best = fuzzyCandidates[0].elem;
                    }
                    string segName = segment.Attributes.TryGetValue("Name", out var n) ? n : "";
                    OnLog($"XPath 模糊匹配: {segment.ControlType}[@Name='{segName}'] -> " +
                          $"{best.ControlType}[@Name='{best.Name}'] 评分={fuzzyCandidates[0].score.TotalScore}");
                    return best;
                }

                // 3. 在更深层后代中精确查找（跳过中间容器）
                var descendantMatches = FindAllMatchingDescendants(parent, segment);
                if (descendantMatches.Count > 0)
                {
                    AutomationElement pick;
                    if (useIndex && descendantMatches.Count > 1)
                    {
                        int idx = Math.Min(segment.Index - 1, descendantMatches.Count - 1);
                        OnLog($"XPath: 在后代中找到 {descendantMatches.Count} 个匹配（跳过中间容器）[{idx}]");
                        pick = descendantMatches[idx];
                    }
                    else
                    {
                        OnLog($"XPath: 在后代中找到 {descendantMatches.Count} 个匹配（跳过中间容器）");
                        pick = descendantMatches[0];
                    }
                    return pick;
                }

                // 4. 在更深层后代中模糊查找
                var fuzzyDescendants = FindAllMatchingDescendantsFuzzy(parent, segment);
                if (fuzzyDescendants.Count > 0)
                {
                    fuzzyDescendants.Sort((a, b) => b.score.TotalScore.CompareTo(a.score.TotalScore));
                    AutomationElement best;
                    if (useIndex && fuzzyDescendants.Count > 1)
                    {
                        int idx = Math.Min(segment.Index - 1, fuzzyDescendants.Count - 1);
                        best = fuzzyDescendants[idx].elem;
                    }
                    else
                    {
                        best = fuzzyDescendants[0].elem;
                    }
                    OnLog($"XPath 后代模糊匹配: {segment.ControlType} -> {best.ControlType}[@Name='{best.Name}'] 评分={fuzzyDescendants[0].score.TotalScore}");
                    return best;
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 判断 XPath 段是否应使用兄弟索引消歧。
        /// 仅当段无 AutomationId 且无 Name（纯靠 ControlType/ClassName 标识）时索引才有意义；
        /// 带 AutomationId 或 Name 的段，索引跨账号不可靠，应忽略。
        /// </summary>
        private static bool ShouldUseIndex(XPathSegment segment)
        {
            if (segment.Attributes == null || segment.Attributes.Count == 0) return true;
            foreach (var key in segment.Attributes.Keys)
            {
                string k = key.ToLower();
                if (k == "automationid" || k == "name") return false;
            }
            return true;
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
        /// 在所有后代中模糊查找匹配指定段的元素，返回带评分的候选列表
        /// </summary>
        private List<(AutomationElement elem, MatchScore score)> FindAllMatchingDescendantsFuzzy(
            AutomationElement root, XPathSegment segment)
        {
            var results = new List<(AutomationElement elem, MatchScore score)>();
            try
            {
                var controlType = ParseControlType(segment.ControlType);
                if (controlType != null)
                {
                    // 使用 FlaUI 的 ControlType 条件缩小搜索范围
                    var found = root.FindAllDescendants(_cf.ByControlType(controlType.Value));
                    foreach (var elem in found)
                    {
                        if (FuzzyMatchesSegment(elem, segment, out var score))
                            results.Add((elem, score));
                    }
                }
                else
                {
                    // 无法解析 ControlType，遍历所有后代
                    var descendants = root.FindAllDescendants();
                    foreach (var desc in descendants)
                    {
                        if (FuzzyMatchesSegment(desc, segment, out var score))
                            results.Add((desc, score));
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
        /// 模糊比较属性值，用于评分式匹配。
        /// 返回匹配级别：0=不匹配, 1=精确, 2=大小写不敏感, 3=包含匹配
        /// </summary>
        private static int TryGetPropertyValueFuzzy(string actualValue, string expectedValue)
        {
            try
            {
                if (actualValue == expectedValue) return 1;
                if (string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase)) return 2;
                if (!string.IsNullOrEmpty(actualValue) && !string.IsNullOrEmpty(expectedValue))
                {
                    if (actualValue.Contains(expectedValue, StringComparison.OrdinalIgnoreCase)) return 3;
                    if (expectedValue.Contains(actualValue, StringComparison.OrdinalIgnoreCase)) return 3;
                }
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// XPath 段匹配评分结果
        /// </summary>
        private struct MatchScore
        {
            public int TotalScore;
            public bool ControlTypeMatched;
            public bool AutomationIdExact;
            public bool NameExact;
            public bool NamePartial;
            public bool ClassNameExact;
            public bool ClassNamePartial;
        }

        /// <summary>
        /// 计算元素与 XPath 段的匹配评分。
        /// ControlType 必须匹配（硬性要求），其他属性按可靠性加权。
        /// </summary>
        private MatchScore ComputeMatchScore(AutomationElement element, XPathSegment segment)
        {
            var score = new MatchScore();
            try
            {
                // ControlType 是硬性要求
                string ctName = element.ControlType.ToString();
                if (!string.Equals(ctName, segment.ControlType, StringComparison.OrdinalIgnoreCase))
                    return score; // ControlType 不匹配，评分=0

                score.ControlTypeMatched = true;
                score.TotalScore = 10; // ControlType 匹配基础分

                if (segment.Attributes.Count == 0) return score;

                foreach (var attr in segment.Attributes)
                {
                    string key = attr.Key.ToLower();
                    string expected = attr.Value;

                    switch (key)
                    {
                        case "automationid":
                            {
                                string actual = element.AutomationId ?? "";
                                int level = TryGetPropertyValueFuzzy(actual, expected);
                                if (level == 1) { score.AutomationIdExact = true; score.TotalScore += 50; }
                                else if (level == 2) { score.TotalScore += 40; }
                                break;
                            }
                        case "name":
                            {
                                string actual = element.Name ?? "";
                                int level = TryGetPropertyValueFuzzy(actual, expected);
                                if (level == 1) { score.NameExact = true; score.TotalScore += 30; }
                                else if (level == 2) { score.NamePartial = true; score.TotalScore += 20; }
                                else if (level == 3) { score.NamePartial = true; score.TotalScore += 10; }
                                break;
                            }
                        case "classname":
                            {
                                string actual = element.ClassName ?? "";
                                int level = TryGetPropertyValueFuzzy(actual, expected);
                                if (level == 1) { score.ClassNameExact = true; score.TotalScore += 15; }
                                else if (level == 2) { score.ClassNamePartial = true; score.TotalScore += 10; }
                                else if (level == 3) { score.ClassNamePartial = true; score.TotalScore += 5; }
                                break;
                            }
                    }
                }
            }
            catch { }

            return score;
        }

        /// <summary>
        /// 模糊匹配：计算评分，返回是否达到最低匹配阈值。
        /// 最低阈值：ControlType 匹配 + 至少一个属性精确匹配，或 ControlType + 多个属性部分匹配。
        /// </summary>
        private bool FuzzyMatchesSegment(AutomationElement element, XPathSegment segment, out MatchScore score)
        {
            score = ComputeMatchScore(element, segment);
            if (!score.ControlTypeMatched) return false;
            // 最低阈值：ControlType 匹配（10分）+ 至少一个属性贡献额外分数
            return score.TotalScore > 10;
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

            /// <summary>
            /// 是否有可靠标识（AutomationId 或 Name）。
            /// 缺少这两者的段（仅靠 ControlType/ClassName）定位不稳定。
            /// </summary>
            public bool HasReliableId()
            {
                if (Attributes == null || Attributes.Count == 0) return false;
                foreach (var key in Attributes.Keys)
                {
                    string k = key.ToLower();
                    if (k == "automationid" || k == "name") return true;
                }
                return false;
            }
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
        /// 将非交互式 ControlType 提升为交互式 ControlType。
        /// 例如：Text -> Button, Image -> Button, Group -> Button
        /// 用于回退策略：当录制的元素是非交互式子元素时，尝试查找其交互式父元素。
        /// </summary>
        private static FlaUI.Core.Definitions.ControlType? PromoteControlType(string controlType)
        {
            if (string.IsNullOrEmpty(controlType)) return null;

            return controlType.ToLower() switch
            {
                "text" => FlaUI.Core.Definitions.ControlType.Button,
                "image" => FlaUI.Core.Definitions.ControlType.Button,
                "group" => FlaUI.Core.Definitions.ControlType.Button,
                "pane" => null, // Pane 太泛化，不提升
                _ => null
            };
        }
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
                DisplayName = node.DisplayName,
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
                TemplateImage = node.TemplateImage,   // 模板路径不做变量替换
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
                RuntimeId = node.RuntimeId,
                TrueActions = node.TrueActions?.Select(ResolveVariables).ToList(),
                FalseActions = node.FalseActions?.Select(ResolveVariables).ToList(),
                TrueBranch = node.TrueBranch,
                TrueBranchScript = ResolveStr(node.TrueBranchScript),
                FalseBranch = node.FalseBranch,
                FalseBranchScript = ResolveStr(node.FalseBranchScript),
                MaxLoopCount = node.MaxLoopCount,
                LoopCount = node.LoopCount,
                WaitKey = node.WaitKey,
                WaitTimeoutMs = node.WaitTimeoutMs,
                HttpUrl = ResolveStr(node.HttpUrl),
                HttpMethod = node.HttpMethod,
                HttpHeaders = ResolveStr(node.HttpHeaders),
                HttpBody = ResolveStr(node.HttpBody),
                ResponseVarName = node.ResponseVarName
            };
            return resolved;
        }

        public void Stop() => _cts?.Cancel();
        private void OnLog(string msg) { if (_mutedLocateLog) return; _logger.Info("Player", msg); LogMessage?.Invoke(this, msg); }
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
