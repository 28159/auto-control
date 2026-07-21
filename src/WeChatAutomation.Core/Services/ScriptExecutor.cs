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
                    ReadResults = _player.ReadResults.ToList()
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

                if (actions.Any(a => a.ClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision))
                {
                    // 使用脚本绑定的视觉模型（无头回放/HTTP/MCP 调用同样复用）
                    _player.EnsureVisionModel(recordingFile.VisionModel);
                }

                _currentCts = new CancellationTokenSource();
                await _player.Play(actions, recordingFile.VisionModel);

                return new ExecuteResult
                {
                    Success = true,
                    Message = "执行完成",
                    ScriptName = scriptName,
                    ExecutedAt = DateTime.Now,
                    ReadResults = _player.ReadResults.ToList()
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
                    IsEnabled = action.IsEnabled,
                    CreatedAt = action.CreatedAt
                };

                foreach (var kvp in parameters)
                {
                    newAction.Parameter = newAction.Parameter.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.Name = newAction.Name.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.WindowTitle = newAction.WindowTitle?.Replace($"{{{kvp.Key}}}", kvp.Value);
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
