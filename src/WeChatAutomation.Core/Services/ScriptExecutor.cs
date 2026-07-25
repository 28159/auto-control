using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.Core.Services
{
    public class ScriptExecutor : IScriptExecutor, IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private readonly string _scriptsDir;
        private ActionPlayer _player;
        private readonly SemaphoreSlim _executionLock = new(1, 1);
        private CancellationTokenSource _currentCts;

        public bool IsExecuting => _player?.IsPlaying == true;
        public event EventHandler<ExecuteResult> ExecutionCompleted;
        public event EventHandler<string> LogMessage;

        public ScriptExecutor()
        {
            _scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
            Directory.CreateDirectory(_scriptsDir);
            _player = new ActionPlayer();
            _player.LogMessage += (s, msg) => LogMessage?.Invoke(this, msg);
            _player.PlayCompleted += (s, e) =>
            {
                var result = new ExecuteResult
                {
                    Success = true,
                    Message = "执行完成",
                    ExecutedAt = DateTime.Now,
                    ReadResults = _player.ReadResults.ToList(),
                    VisionResults = _player.VisionResults.ToList()
                };
                ExecutionCompleted?.Invoke(this, result);
            };
            _player.PlayError += (s, msg) =>
            {
                var result = new ExecuteResult
                {
                    Success = false,
                    Message = msg,
                    ExecutedAt = DateTime.Now
                };
                ExecutionCompleted?.Invoke(this, result);
            };
            // 订阅子脚本请求：If-TargetScript 条件成立时执行子脚本（继承当前参数）
            _player.SubScriptRequested += (s, scriptName) =>
            {
                bool ok = ExecuteSubScriptSync(scriptName, _player.CurrentParameters);
                _player.ReportSubScriptResult(ok);
            };
        }

        /// <summary>
        /// 同步执行子脚本（供 If-TargetScript 调用，不走信号量，复用主 player 的上下文）。
        /// 用临时 ActionPlayer 执行，避免与主脚本 Play 冲突。
        /// </summary>
        private bool ExecuteSubScriptSync(string scriptName, Dictionary<string, string>? parameters)
        {
            try
            {
                var filePath = Path.Combine(_scriptsDir, $"{scriptName}.json");
                if (!File.Exists(filePath))
                {
                    _logger.Warn("Executor", $"子脚本不存在: {scriptName}");
                    LogMessage?.Invoke(this, $"子脚本不存在: {scriptName}");
                    return false;
                }

                var recordingFile = ActionRecorder.LoadFromFile(filePath);
                if (recordingFile?.Actions == null || recordingFile.Actions.Count == 0)
                {
                    _logger.Warn("Executor", $"子脚本为空: {scriptName}");
                    return false;
                }

                // 参数解析（继承主脚本参数）
                var subParams = parameters;
                if (subParams != null && subParams.Count > 0 && recordingFile.Parameters?.Count > 0)
                    subParams = ResolveParameterIds(subParams, recordingFile.Parameters);

                var actions = recordingFile.Actions.ToList();
                if (subParams != null && subParams.Count > 0)
                    actions = ReplaceParameters(actions, subParams);

                _logger.Info("Executor", $"开始执行子脚本: {scriptName}");
                LogMessage?.Invoke(this, $"── 执行子脚本: {scriptName} ──");

                // 用临时 player 执行子脚本，避免与主 player 的 _isPlaying 冲突
                using var subPlayer = new ActionPlayer();
                subPlayer.LogMessage += (s2, msg) => LogMessage?.Invoke(this, msg);
                subPlayer.CurrentParameters = subParams;

                // 模板匹配模式下每个视觉步骤自带模板，无需全局模型加载

                // 复制主脚本的阅读目标到子脚本（若主脚本设置了 PickTargetWindow）
                // 子脚本通常应自行指定窗口，这里不复制以保持隔离

                // 在线程池线程执行子脚本，避免在 UI 线程上 .GetAwaiter().GetResult() 阻塞导致死锁：
                // 主脚本 If 步骤在 UI 线程触发 SubScriptRequested -> 本方法同步阻塞 UI 线程等 subPlayer.Play；
                // 若直接在 UI 线程跑 Play，其内部 await Task.Delay 的 continuation 要回到 UI 同步上下文，
                // 而 UI 线程正被阻塞 -> 死锁（症状：子脚本"定位成功"后卡住不动）。
                // Task.Run 让 Play 在无 UI SynchronizationContext 的线程池线程上运行，continuation 回线程池，死锁解除。
                Task.Run(() => subPlayer.Play(actions, null)).GetAwaiter().GetResult();

                // 合并子脚本的读取/视觉结果到主结果
                foreach (var r in subPlayer.ReadResults) _player.AppendReadResult(r);
                foreach (var v in subPlayer.VisionResults) _player.AppendVisionResult(v);

                _logger.Info("Executor", $"子脚本执行完成: {scriptName}");
                LogMessage?.Invoke(this, $"── 子脚本完成: {scriptName} ──");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("Executor", $"执行子脚本失败: {scriptName}", ex);
                LogMessage?.Invoke(this, $"子脚本执行失败: {ex.Message}");
                return false;
            }
        }

        public async Task<ExecuteResult> ExecuteScript(string scriptName, Dictionary<string, string> parameters = null)
        {
            if (!await _executionLock.WaitAsync(0))
            {
                return new ExecuteResult
                {
                    Success = false,
                    Message = "有其他脚本正在执行中",
                    ScriptName = scriptName,
                    ExecutedAt = DateTime.Now
                };
            }

            try
            {
                var filePath = Path.Combine(_scriptsDir, $"{scriptName}.json");
                if (!File.Exists(filePath))
                {
                    return new ExecuteResult
                    {
                        Success = false,
                        Message = $"脚本不存在: {scriptName}",
                        ScriptName = scriptName,
                        ExecutedAt = DateTime.Now
                    };
                }

                _logger.Info("Executor", $"开始执行脚本: {scriptName}");
                LogMessage?.Invoke(this, $"开始执行脚本: {scriptName}");

                var recordingFile = ActionRecorder.LoadFromFile(filePath);
                if (recordingFile == null || recordingFile.Actions == null || recordingFile.Actions.Count == 0)
                {
                    return new ExecuteResult
                    {
                        Success = false,
                        Message = $"脚本为空或格式错误: {scriptName}",
                        ScriptName = scriptName,
                        ExecutedAt = DateTime.Now
                    };
                }

                if (parameters != null && parameters.Count > 0 && recordingFile.Parameters?.Count > 0)
                {
                    parameters = ResolveParameterIds(parameters, recordingFile.Parameters);
                }

                var actions = recordingFile.Actions.ToList();
                if (parameters != null && parameters.Count > 0)
                {
                    actions = ReplaceParameters(actions, parameters);
                }

                // 模板匹配模式下每个视觉步骤自带模板，无需全局模型加载

                // 设置当前参数供 If-TargetScript 子脚本继承
                _player.CurrentParameters = parameters;

                _currentCts = new CancellationTokenSource();
                await _player.Play(actions, null);

                return new ExecuteResult
                {
                    Success = true,
                    Message = "执行完成",
                    ScriptName = scriptName,
                    ExecutedAt = DateTime.Now,
                    ReadResults = _player.ReadResults.ToList(),
                    VisionResults = _player.VisionResults.ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.Error("Executor", $"执行脚本失败: {scriptName}", ex);
                return new ExecuteResult
                {
                    Success = false,
                    Message = $"执行失败: {ex.Message}",
                    ScriptName = scriptName,
                    ExecutedAt = DateTime.Now
                };
            }
            finally
            {
                _executionLock.Release();
                _currentCts?.Dispose();
                _currentCts = null;
            }
        }

        public List<ScriptInfo> GetAvailableScripts()
        {
            var scripts = new List<ScriptInfo>();
            if (!Directory.Exists(_scriptsDir)) return scripts;

            foreach (var file in Directory.GetFiles(_scriptsDir, "*.json"))
            {
                try
                {
                    var rec = ActionRecorder.LoadFromFile(file);
                    if (rec != null)
                    {
                        scripts.Add(new ScriptInfo
                        {
                            Name = rec.Name ?? Path.GetFileNameWithoutExtension(file),
                            FilePath = file,
                            StepCount = rec.Actions?.Count ?? 0,
                            LastModified = File.GetLastWriteTime(file),
                            CreatedAt = rec.CreatedAt
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("Executor", $"加载脚本失败 {file}: {ex.Message}");
                }
            }
            return scripts.OrderBy(s => s.Name).ToList();
        }

        public bool IsScriptExist(string scriptName)
        {
            var filePath = Path.Combine(_scriptsDir, $"{scriptName}.json");
            return File.Exists(filePath);
        }

        public RecordingFile GetScriptInfo(string scriptName)
        {
            var filePath = Path.Combine(_scriptsDir, $"{scriptName}.json");
            if (!File.Exists(filePath)) return null;
            return ActionRecorder.LoadFromFile(filePath);
        }

        public void StopExecution()
        {
            _player?.Stop();
        }

        private Dictionary<string, string> ResolveParameterIds(Dictionary<string, string> inputParams, List<ScriptParameter> definedParams)
        {
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in inputParams)
            {
                var match = definedParams.FirstOrDefault(p =>
                    string.Equals(p.Id, kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Name, kvp.Key, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    resolved[match.Name] = kvp.Value;
                else
                    resolved[kvp.Key] = kvp.Value;
            }

            foreach (var def in definedParams)
            {
                if (!resolved.ContainsKey(def.Name) && !string.IsNullOrEmpty(def.DefaultValue))
                    resolved[def.Name] = def.DefaultValue;
            }

            return resolved;
        }

        private List<RecordedAction> ReplaceParameters(List<RecordedAction> actions, Dictionary<string, string> parameters)
        {
            var result = new List<RecordedAction>();
            foreach (var action in actions)
            {
                var newAction = new RecordedAction
                {
                    NodeId = action.NodeId,
                    Order = action.Order,
                    ActionType = action.ActionType,
                    Name = action.Name,
                    DisplayName = action.DisplayName,
                    ClassName = action.ClassName,
                    ElementName = action.ElementName,
                    AutomationId = action.AutomationId,
                    ControlType = action.ControlType,
                    WindowTitle = action.WindowTitle,
                    X = action.X,
                    Y = action.Y,
                    ClickMode = action.ClickMode,
                    VisionLabel = action.VisionLabel,
                    VisionConfThreshold = action.VisionConfThreshold,
                    TemplateImage = action.TemplateImage,
                    XPath = action.XPath,
                    SiblingIndex = action.SiblingIndex,
                    RuntimeId = action.RuntimeId,
                    Parameter = action.Parameter,
                    DelayMs = action.DelayMs,
                    ScrollAmount = action.ScrollAmount,
                    ParameterName = action.ParameterName,
                    DefaultValue = action.DefaultValue,
                    IsRequired = action.IsRequired,
                    CopyToClipboard = action.CopyToClipboard,
                    RegexPattern = action.RegexPattern,
                    RegexGroup = action.RegexGroup,
                    OutputParamName = action.OutputParamName,
                    ConditionExpression = action.ConditionExpression,
                    GotoNodeId = action.GotoNodeId,
                    TrueGotoNodeId = action.TrueGotoNodeId,
                    TargetScript = action.TargetScript,
                    SwitchAll = action.SwitchAll,
                    IsEnabled = action.IsEnabled,
                    CreatedAt = action.CreatedAt,
                    Remark = action.Remark,
                    TrueActions = action.TrueActions != null ? ReplaceParameters(action.TrueActions, parameters) : null,
                    FalseActions = action.FalseActions != null ? ReplaceParameters(action.FalseActions, parameters) : null,
                    TrueBranch = action.TrueBranch,
                    TrueBranchScript = action.TrueBranchScript,
                    FalseBranch = action.FalseBranch,
                    FalseBranchScript = action.FalseBranchScript,
                    MaxLoopCount = action.MaxLoopCount,
                    LoopCount = action.LoopCount,
                    WaitKey = action.WaitKey,
                    WaitTimeoutMs = action.WaitTimeoutMs,
                    HttpUrl = action.HttpUrl,
                    HttpMethod = action.HttpMethod,
                    HttpHeaders = action.HttpHeaders,
                    HttpBody = action.HttpBody,
                    ResponseVarName = action.ResponseVarName,
                    // 步骤后随机行为：必须显式拷贝，否则重建对象会丢失勾选状态（默认 false），导致带参数回放时随机等待/鼠标移动不生效
                    RandomWaitEnabled = action.RandomWaitEnabled,
                    RandomWaitMinSec = action.RandomWaitMinSec,
                    RandomWaitMaxSec = action.RandomWaitMaxSec,
                    RandomMouseMoveEnabled = action.RandomMouseMoveEnabled,
                    RandomMoveMinOffset = action.RandomMoveMinOffset,
                    RandomMoveMaxOffset = action.RandomMoveMaxOffset
                };

                foreach (var kvp in parameters)
                {
                    newAction.Parameter = newAction.Parameter.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.Name = newAction.Name.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.WindowTitle = newAction.WindowTitle?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.ConditionExpression = newAction.ConditionExpression?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.TargetScript = newAction.TargetScript?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.TrueBranchScript = newAction.TrueBranchScript?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.FalseBranchScript = newAction.FalseBranchScript?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.HttpUrl = newAction.HttpUrl?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.HttpHeaders = newAction.HttpHeaders?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.HttpBody = newAction.HttpBody?.Replace($"{{{kvp.Key}}}", kvp.Value);
                }

                if (newAction.ActionType == ActionType.InputParam && newAction.CopyToClipboard)
                {
                    foreach (var kvp in parameters)
                    {
                        if (kvp.Key == newAction.ParameterName)
                        {
                            newAction.Parameter = kvp.Value;
                            break;
                        }
                    }
                }

                result.Add(newAction);
            }
            return result;
        }

        public void Dispose()
        {
            _player?.Dispose();
            _currentCts?.Dispose();
            _executionLock?.Dispose();
        }
    }
}
