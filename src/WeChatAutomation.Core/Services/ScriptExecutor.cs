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

                // 参数替换（如果有的话）
                var actions = recordingFile.Actions.ToList();
                if (parameters != null && parameters.Count > 0)
                {
                    actions = ReplaceParameters(actions, parameters);
                }

                _currentCts = new CancellationTokenSource();
                await _player.Play(actions);

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
                    Parameter = action.Parameter,
                    DelayMs = action.DelayMs,
                    ScrollAmount = action.ScrollAmount,
                    IsEnabled = action.IsEnabled,
                    CreatedAt = action.CreatedAt
                };

                // 替换参数中的占位符
                foreach (var kvp in parameters)
                {
                    newAction.Parameter = newAction.Parameter.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.Name = newAction.Name.Replace($"{{{kvp.Key}}}", kvp.Value);
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
