using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Services
{
    /// <summary>
    /// 任务轮询服务 — 从远程服务器拉取任务，本地执行，回传结果。
    /// 遵循 API_DOC.md 定义的 /wxauto-api/* 接口规范。
    /// </summary>
    public class TaskPollingService : IHostedService, IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly IScriptExecutor _executor;
        private readonly IConfiguration _configuration;
        private readonly CancellationTokenSource _cts = new();

        // 后台循环任务
        private Task _pollingLoop;
        private Task _heartbeatLoop;
        private Task _cacheRetryLoop;

        // 配置
        private string _serverUrl;
        private int _pollIntervalSeconds = 10;
        private int _heartbeatIntervalSeconds = 30;

        // 状态
        private volatile bool _isRunning;
        private volatile bool _isExecuting;
        private RemoteTask _currentTask;
        private string _clientId;

        // 结果缓存（回传失败时本地缓存，最多 50 条）
        private readonly ConcurrentQueue<CachedResult> _resultCache = new();
        private const int MaxCacheSize = 50;

        // 任务历史记录（所有已执行的任务，最多保留 200 条）
        private readonly ConcurrentQueue<TaskHistoryItem> _taskHistory = new();
        private const int MaxHistorySize = 200;

        // 任务类型映射
        private Dictionary<string, string> _taskTypeMapping = new();

        public bool IsRunning => _isRunning;
        public bool IsExecuting => _isExecuting;
        public string ClientId => _clientId;
        public string ServerUrl => _serverUrl;
        public RemoteTask CurrentTask => _currentTask;
        public int CachedResultCount => _resultCache.Count;
        public IReadOnlyList<TaskHistoryItem> TaskHistory => _taskHistory.ToList().AsReadOnly();
        public int TotalCompleted => _taskHistory.Count(h => h.Status == "completed");
        public int TotalFailed => _taskHistory.Count(h => h.Status == "failed");

        public TaskPollingService(IScriptExecutor executor, IConfiguration configuration)
        {
            _executor = executor;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return StartInternal(cancellationToken, checkConfig: true);
        }

        /// <summary>
        /// 手动启动（UI 点击"开启"时调用），跳过 Enabled 配置检查
        /// </summary>
        public Task StartManual(CancellationToken cancellationToken)
        {
            return StartInternal(cancellationToken, checkConfig: false);
        }

        private Task StartInternal(CancellationToken cancellationToken, bool checkConfig)
        {
            if (_isRunning) return Task.CompletedTask;

            // 自动启动时检查 AutoStart 配置，手动启动时跳过
            if (checkConfig)
            {
                var autoStart = _configuration.GetValue("TaskPolling:AutoStart", false);
                if (!autoStart)
                {
                    _logger.Info("TaskPolling", "任务轮询服务未设置自动启动");
                    return Task.CompletedTask;
                }
            }

            _serverUrl = _configuration.GetValue("TaskPolling:ServerUrl", "http://localhost:6621").TrimEnd('/');
            _pollIntervalSeconds = _configuration.GetValue("TaskPolling:PollIntervalSeconds", 10);
            _heartbeatIntervalSeconds = _configuration.GetValue("TaskPolling:HeartbeatIntervalSeconds", 30);

            // 加载或生成 Client ID
            _clientId = _configuration.GetValue("TaskPolling:ClientId", "");
            if (string.IsNullOrWhiteSpace(_clientId))
                _clientId = LoadOrGenerateClientId();

            // 加载任务类型映射
            LoadTaskTypeMapping();

            _isRunning = true;

            // 启动三个后台循环
            _pollingLoop = Task.Run(() => PollingLoop(_cts.Token));
            _heartbeatLoop = Task.Run(() => HeartbeatLoop(_cts.Token));
            _cacheRetryLoop = Task.Run(() => CacheRetryLoop(_cts.Token));

            _logger.Info("TaskPolling", $"任务轮询服务已启动 → {_serverUrl} (Client: {_clientId})");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_isRunning) return;
            _isRunning = false;

            _cts.Cancel();

            // 等待循环退出（最多 5 秒）
            var tasks = new[] { _pollingLoop, _heartbeatLoop, _cacheRetryLoop }
                .Where(t => t != null).ToArray();
            if (tasks.Length > 0)
            {
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
                catch { }
            }

            _logger.Info("TaskPolling", "任务轮询服务已停止");
        }

        // ═══ 轮询循环 ═══

        private async Task PollingLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), ct);
                    await PollAndExecuteTask();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Warn("TaskPolling", $"轮询异常: {ex.Message}");
                }
            }
        }

        private async Task HeartbeatLoop(CancellationToken ct)
        {
            // 首次立即发送一次心跳
            try { await SendHeartbeat(); } catch { }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSeconds), ct);
                    await SendHeartbeat();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Warn("TaskPolling", $"心跳异常: {ex.Message}");
                }
            }
        }

        private async Task CacheRetryLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                    await FlushCachedResults();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Warn("TaskPolling", $"缓存重试异常: {ex.Message}");
                }
            }
        }

        // ═══ 任务执行管道 ═══

        public async Task<RemoteTaskExecutionResult> TriggerPollCycle()
        {
            if (!_isRunning) return new RemoteTaskExecutionResult { Success = false, Message = "服务未运行" };
            return await PollAndExecuteTask();
        }

        private async Task<RemoteTaskExecutionResult> PollAndExecuteTask()
        {
            // 如果有脚本正在执行，跳过
            if (_executor is ScriptExecutor se && se.IsExecuting)
                return null;

            try
            {
                // 1. 拉取待执行任务
                var url = $"{_serverUrl}/wxauto-api/tasks?status=pending&client_id={Uri.EscapeDataString(_clientId)}&limit=1";
                var response = await SendWithRetry(HttpMethod.Get, url, null, 3, new[] { 1000, 2000, 3000 });
                if (response == null || !response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                var apiResp = JsonSerializer.Deserialize<ApiTaskListResponse>(body, _jsonOptions);
                if (apiResp?.Data?.Items == null || apiResp.Data.Items.Count == 0)
                    return null; // 无待执行任务

                var task = apiResp.Data.Items[0];

                // 2. 认领任务
                var claimed = await ClaimTask(task.TaskId);
                if (!claimed)
                {
                    _logger.Warn("TaskPolling", $"认领任务失败: {task.TaskId}");
                    return null;
                }

                _logger.Info("TaskPolling", $"已认领任务: {task.TaskId} ({task.TaskType})");

                // 3. 执行任务
                var result = await ExecuteTask(task);

                // 4. 回传结果
                await ReportTaskResult(task.TaskId, result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Warn("TaskPolling", $"轮询执行异常: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> ClaimTask(string taskId)
        {
            try
            {
                var url = $"{_serverUrl}/wxauto-api/tasks/{Uri.EscapeDataString(taskId)}/claim";
                var body = new { client_id = _clientId };
                var response = await SendWithRetry(HttpMethod.Put, url, body, 3, new[] { 1000, 2000, 3000 });
                if (response == null) return false;

                if (!response.IsSuccessStatusCode) return false;

                var respBody = await response.Content.ReadAsStringAsync();
                var apiResp = JsonSerializer.Deserialize<ApiResponse>(respBody, _jsonOptions);
                return apiResp?.Success == true;
            }
            catch (Exception ex)
            {
                _logger.Warn("TaskPolling", $"认领任务 HTTP 失败: {ex.Message}");
                return false;
            }
        }

        private async Task<RemoteTaskExecutionResult> ExecuteTask(RemoteTask task)
        {
            _currentTask = task;
            _isExecuting = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 记录到历史（开始执行）
            var historyItem = new TaskHistoryItem
            {
                TaskId = task.TaskId,
                TaskType = task.TaskType,
                Description = task.Description,
                Params = task.Params,
                Status = "running",
                StartedAt = DateTime.Now
            };
            AddHistory(historyItem);

            try
            {
                // 映射 task_type 到脚本名
                var scriptName = MapTaskTypeToScript(task.TaskType);

                // 检查脚本是否存在
                if (!_executor.IsScriptExist(scriptName))
                {
                    _logger.Warn("TaskPolling", $"脚本不存在: {scriptName} (任务类型: {task.TaskType})");
                    return new RemoteTaskExecutionResult
                    {
                        Success = false,
                        Message = $"脚本不存在: {scriptName}",
                        Duration = sw.Elapsed.TotalSeconds,
                        Data = null
                    };
                }

                // 转换参数
                var parameters = ConvertParams(task.Params);

                _logger.Info("TaskPolling", $"执行任务: {task.TaskId} → 脚本 {scriptName} ({parameters.Count} 个参数)");

                // 执行脚本
                var result = await _executor.ExecuteScript(scriptName, parameters);

                sw.Stop();

                // 构建结果数据
                object resultData = null;
                if (result.ReadResults?.Count > 0 || result.VisionResults?.Count > 0)
                {
                    // 从阅读结果中提取 OutputParamName -> 内容映射，格式: {items:[{参数名: 阅读内容}]}
                    var items = result.ReadResults?
                        .Where(r => !string.IsNullOrEmpty(r.OutputParamName))
                        .Select(r => new Dictionary<string, string>
                        {
                            { r.OutputParamName, r.Source == "RegexMatch" ? (r.MatchedValue ?? "") : r.Content }
                        })
                        .ToList();

                    resultData = new
                    {
                        readResults = result.ReadResults?.Select(r => new
                        {
                            r.WindowTitle,
                            r.Content,
                            r.Source,
                            Matches = r.Matches?.Select(m => new { m.Value, m.Index, m.Groups }).ToList(),
                            r.MatchedValue,
                            r.OutputParamName
                        }).ToList(),
                        visionResults = result.VisionResults?.Select(v => new
                        {
                            v.WindowTitle,
                            v.VisionLabel,
                            v.Source,
                            Detections = v.Detections?.Select(d => new
                            {
                                d.Label,
                                d.Confidence,
                                d.X,
                                d.Y,
                                d.Width,
                                d.Height
                            }).ToList()
                        }).ToList(),
                        items
                    };
                }

                _logger.Info("TaskPolling", $"任务完成: {task.TaskId} ({(result.Success ? "成功" : "失败")}, {sw.Elapsed.TotalSeconds:F1}s)");

                // 更新历史记录
                historyItem.Status = result.Success ? "completed" : "failed";
                historyItem.Message = result.Message;
                historyItem.Duration = sw.Elapsed.TotalSeconds;
                historyItem.FinishedAt = DateTime.Now;
                UpdateHistory(historyItem);

                return new RemoteTaskExecutionResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    Duration = sw.Elapsed.TotalSeconds,
                    Data = resultData
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.Error("TaskPolling", $"执行任务异常: {task.TaskId}", ex);

                historyItem.Status = "failed";
                historyItem.Message = $"执行异常: {ex.Message}";
                historyItem.Duration = sw.Elapsed.TotalSeconds;
                historyItem.FinishedAt = DateTime.Now;
                UpdateHistory(historyItem);

                return new RemoteTaskExecutionResult
                {
                    Success = false,
                    Message = $"执行异常: {ex.Message}",
                    Duration = sw.Elapsed.TotalSeconds,
                    Data = null
                };
            }
            finally
            {
                _currentTask = null;
                _isExecuting = false;
            }
        }

        private async Task ReportTaskResult(string taskId, RemoteTaskExecutionResult result)
        {
            if (result == null) return;

            try
            {
                var url = $"{_serverUrl}/wxauto-api/tasks/{Uri.EscapeDataString(taskId)}/result";
                var body = new
                {
                    client_id = _clientId,
                    status = result.Success ? "completed" : "failed",
                    duration = Math.Round(result.Duration, 2),
                    result = new
                    {
                        success = result.Success,
                        message = result.Message,
                        data = result.Data
                    }
                };
                var response = await SendWithRetry(HttpMethod.Put, url, body, 3, new[] { 2000, 4000, 6000 });

                if (response == null || !response.IsSuccessStatusCode)
                {
                    // 回传失败，本地缓存
                    CacheResult(taskId, result);
                    _logger.Warn("TaskPolling", $"任务结果回传失败，已缓存: {taskId}");
                }
            }
            catch (Exception ex)
            {
                CacheResult(taskId, result);
                _logger.Warn("TaskPolling", $"任务结果回传异常，已缓存: {taskId} - {ex.Message}");
            }
        }

        // ═══ 心跳 ═══

        private async Task SendHeartbeat()
        {
            try
            {
                var url = $"{_serverUrl}/wxauto-api/heartbeat";
                var body = new
                {
                    client_id = _clientId,
                    info = new
                    {
                        wx_online = true,
                        executing = _isExecuting
                    }
                };
                await SendWithRetry(HttpMethod.Post, url, body, 3, new[] { 1000, 2000, 3000 });
            }
            catch (Exception ex)
            {
                _logger.Warn("TaskPolling", $"心跳发送失败: {ex.Message}");
            }
        }

        // ═══ 结果缓存 ═══

        private void CacheResult(string taskId, RemoteTaskExecutionResult result)
        {
            _resultCache.Enqueue(new CachedResult
            {
                TaskId = taskId,
                Result = result,
                CachedAt = DateTime.Now,
                RetryCount = 0
            });

            // 超过最大缓存数，移除最旧的
            while (_resultCache.Count > MaxCacheSize)
                _resultCache.TryDequeue(out _);
        }

        public async Task<int> FlushCachedResults()
        {
            var flushed = 0;
            var failed = new List<CachedResult>();

            while (_resultCache.TryDequeue(out var cached))
            {
                try
                {
                    var url = $"{_serverUrl}/wxauto-api/tasks/{Uri.EscapeDataString(cached.TaskId)}/result";
                    var body = new
                    {
                        client_id = _clientId,
                        status = cached.Result.Success ? "completed" : "failed",
                        duration = Math.Round(cached.Result.Duration, 2),
                        result = new
                        {
                            success = cached.Result.Success,
                            message = cached.Result.Message,
                            data = cached.Result.Data
                        }
                    };
                    var response = await SendWithRetry(HttpMethod.Put, url, body, 3, new[] { 2000, 4000, 6000 });

                    if (response != null && response.IsSuccessStatusCode)
                    {
                        flushed++;
                        _logger.Info("TaskPolling", $"缓存结果已回传: {cached.TaskId}");
                    }
                    else
                    {
                        cached.RetryCount++;
                        failed.Add(cached);
                    }
                }
                catch
                {
                    cached.RetryCount++;
                    failed.Add(cached);
                }
            }

            // 重新入队失败的结果
            foreach (var item in failed)
                _resultCache.Enqueue(item);

            return flushed;
        }

        public List<CachedResult> GetCachedResults()
        {
            return _resultCache.ToList();
        }

        // ═══ 健康检查 ═══

        public async Task<bool> CheckServerHealth()
        {
            try
            {
                var url = $"{_serverUrl}/wxauto-api/health";
                var response = await _httpClient.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // ═══ HTTP 重试 ═══

        private async Task<HttpResponseMessage> SendWithRetry(HttpMethod method, string url, object body, int maxRetries, int[] backoffMs)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(method, url);
                    if (body != null)
                    {
                        var json = JsonSerializer.Serialize(body, _jsonOptions);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }
                    var response = await _httpClient.SendAsync(request, _cts.Token);
                    if (response.IsSuccessStatusCode) return response;

                    if (attempt >= maxRetries) return response;
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex)
                {
                    if (attempt >= maxRetries) return null;
                    _logger.Warn("TaskPolling", $"HTTP 请求失败 (尝试 {attempt + 1}): {ex.Message}");
                }

                if (attempt < maxRetries)
                {
                    try { await Task.Delay(backoffMs[Math.Min(attempt, backoffMs.Length - 1)], _cts.Token); }
                    catch (OperationCanceledException) { throw; }
                }
            }
            return null;
        }

        // ═══ Client ID 管理 ═══

        private string LoadOrGenerateClientId()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client_id.txt");
            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(existing)) return existing;
                }
            }
            catch { }

            var id = $"wxauto-{Guid.NewGuid().ToString("N")[..12]}";
            try { File.WriteAllText(path, id); } catch { }
            return id;
        }

        // ═══ 任务类型映射 ═══

        private void LoadTaskTypeMapping()
        {
            var section = _configuration.GetSection("TaskPolling:TaskTypeMapping");
            if (section.Exists())
            {
                _taskTypeMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in section.GetChildren())
                {
                    _taskTypeMapping[child.Key] = child.Value;
                }
            }
        }

        private string MapTaskTypeToScript(string taskType)
        {
            if (_taskTypeMapping.TryGetValue(taskType, out var scriptName))
                return scriptName;
            return taskType; // 默认 1:1 映射
        }

        // ═══ 参数转换 ═══

        private void AddHistory(TaskHistoryItem item)
        {
            _taskHistory.Enqueue(item);
            while (_taskHistory.Count > MaxHistorySize)
                _taskHistory.TryDequeue(out _);
        }

        private void UpdateHistory(TaskHistoryItem updated)
        {
            // 找到并更新历史记录中的条目
            var list = _taskHistory.ToList();
            var idx = list.FindIndex(h => h.TaskId == updated.TaskId && h.StartedAt == updated.StartedAt);
            if (idx >= 0)
            {
                list[idx] = updated;
                // 重建队列
                while (_taskHistory.TryDequeue(out _)) { }
                foreach (var item in list) _taskHistory.Enqueue(item);
            }
        }

        public void ClearHistory()
        {
            while (_taskHistory.TryDequeue(out _)) { }
        }

        // ═══ 参数转换 ═══

        private Dictionary<string, string> ConvertParams(Dictionary<string, object> taskParams)
        {
            if (taskParams == null) return new Dictionary<string, string>();

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in taskParams)
            {
                if (kvp.Value == null) continue;
                // 简单类型直接 ToString，复杂类型 JSON 序列化
                if (kvp.Value is string s)
                    result[kvp.Key] = s;
                else if (kvp.Value is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.String)
                        result[kvp.Key] = je.GetString() ?? "";
                    else if (je.ValueKind == JsonValueKind.Number)
                        result[kvp.Key] = je.ToString();
                    else if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                        result[kvp.Key] = je.GetBoolean().ToString().ToLower();
                    else
                        result[kvp.Key] = je.ToString();
                }
                else
                    result[kvp.Key] = kvp.Value.ToString();
            }
            return result;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }

    // ═══ 数据模型 ═══

    public class RemoteTask
    {
        [JsonPropertyName("task_id")]
        public string TaskId { get; set; }

        [JsonPropertyName("task_type")]
        public string TaskType { get; set; }

        [JsonPropertyName("params")]
        public Dictionary<string, object> Params { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; }

        [JsonPropertyName("scheduled_at")]
        public string ScheduledAt { get; set; }
    }

    public class RemoteTaskExecutionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public double Duration { get; set; }
        public object Data { get; set; }
    }

    public class CachedResult
    {
        public string TaskId { get; set; }
        public RemoteTaskExecutionResult Result { get; set; }
        public DateTime CachedAt { get; set; }
        public int RetryCount { get; set; }
    }

    public class TaskHistoryItem
    {
        public string TaskId { get; set; }
        public string TaskType { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Params { get; set; }
        public string Status { get; set; } // pending / running / completed / failed
        public string Message { get; set; }
        public double Duration { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    // ═══ 远程 API 响应模型 ═══

    public class ApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }

    public class ApiTaskListResponse : ApiResponse
    {
        [JsonPropertyName("data")]
        public ApiTaskListData Data { get; set; }
    }

    public class ApiTaskListData
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("items")]
        public List<RemoteTask> Items { get; set; }
    }
}
