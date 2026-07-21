using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Services
{
    public class HttpApiService : IHostedService, IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private readonly IScriptExecutor _executor;
        private readonly IConfiguration _configuration;
        private readonly TaskPollingService _taskPolling;
        private WebApplication _app;
        private Task _appTask;
        private readonly CancellationTokenSource _cts = new();

        public bool IsRunning => _app != null;
        public int Port { get; private set; }

        public HttpApiService(IScriptExecutor executor, IConfiguration configuration, TaskPollingService taskPolling)
        {
            _executor = executor;
            _configuration = configuration;
            _taskPolling = taskPolling;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var httpEnabled = _configuration.GetValue("HttpApi:Enabled", true);
                if (!httpEnabled)
                {
                    _logger.Info("HttpApi", "HTTP API 已禁用");
                    return;
                }

                Port = _configuration.GetValue("HttpApi:Port", 5000);

                var builder = WebApplication.CreateBuilder();
                builder.Services.AddSingleton(_executor);
                builder.Services.AddCors();
                builder.Services.AddEndpointsApiExplorer();
                builder.Logging.ClearProviders();

                builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");

                _app = builder.Build();

                // CORS — 允许所有来源（本地工具，方便前端调用）
                _app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

                // 配置路由
                _app.MapGet("/api/status", () => Results.Ok(new
                {
                    status = "running",
                    timestamp = DateTime.Now,
                    version = "1.0.0"
                }));

                _app.MapGet("/api/scripts", () =>
                {
                    var scripts = _executor.GetAvailableScripts();
                    return Results.Ok(scripts);
                });

                _app.MapGet("/api/scripts/{name}", (string name) =>
                {
                    var info = _executor.GetScriptInfo(name);
                    if (info == null)
                        return Results.NotFound(new { message = $"脚本不存在: {name}" });
                    return Results.Ok(info);
                });

                _app.MapGet("/api/scripts/{name}/params", (string name) =>
                {
                    var info = _executor.GetScriptInfo(name);
                    if (info == null)
                        return Results.NotFound(new { message = $"脚本不存在: {name}" });
                    return Results.Ok(info.Parameters?.Select(p => new
                    {
                        p.Id,
                        p.Name,
                        DisplayName = p.DisplayName ?? p.Name,
                        p.DefaultValue,
                        p.IsRequired,
                        p.Type,
                        p.CopyToClipboard,
                        p.Description
                    }));
                });

                _app.MapPost("/api/scripts/execute", async (HttpRequest request) =>
                {
                    try
                    {
                        using var reader = new StreamReader(request.Body);
                        var body = await reader.ReadToEndAsync();
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var req = JsonSerializer.Deserialize<ExecuteRequest>(body, options);

                        if (req == null || string.IsNullOrEmpty(req.ScriptName))
                        {
                            return Results.BadRequest(new { message = "缺少 scriptName 参数" });
                        }

                        var result = await _executor.ExecuteScript(req.ScriptName, req.Parameters);
                        return result.Success
                            ? Results.Ok(result)
                            : Results.BadRequest(result);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("HttpApi", "执行请求失败", ex);
                        return Results.BadRequest(new { message = ex.Message });
                    }
                });

                _app.MapPost("/api/scripts/{name}/stop", (string name) =>
                {
                    if (_executor is ScriptExecutor executor)
                    {
                        executor.StopExecution();
                    }
                    return Results.Ok(new { message = "已发送停止信号" });
                });

                _app.MapGet("/api/scripts/{name}/exists", (string name) =>
                {
                    var exists = _executor.IsScriptExist(name);
                    return Results.Ok(new { name, exists });
                });

                _app.MapPost("/api/parse-command", (ParseCommandRequest req) =>
                {
                    try
                    {
                        var parser = new LlmCommandParser();
                        var actions = parser.ParseCommand(req.Command);
                        return Results.Ok(new { command = req.Command, actions, count = actions.Count });
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
                });

                _app.MapGet("/api/health", () => Results.Ok("OK"));

                // ═══ 任务轮询管理接口 ═══

                _app.MapGet("/api/task-polling/status", () =>
                {
                    if (_taskPolling == null)
                        return Results.Ok(new { enabled = false, running = false });
                    return Results.Ok(new
                    {
                        enabled = true,
                        running = _taskPolling.IsRunning,
                        executing = _taskPolling.IsExecuting,
                        client_id = _taskPolling.ClientId,
                        server_url = _taskPolling.ServerUrl,
                        current_task = _taskPolling.CurrentTask != null ? new
                        {
                            task_id = _taskPolling.CurrentTask.TaskId,
                            task_type = _taskPolling.CurrentTask.TaskType,
                            description = _taskPolling.CurrentTask.Description
                        } : null,
                        cached_results = _taskPolling.CachedResultCount
                    });
                });

                _app.MapPost("/api/task-polling/start", async () =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    await _taskPolling.StartManual(CancellationToken.None);
                    return _taskPolling.IsRunning
                        ? Results.Ok(new { message = "任务轮询服务已启动" })
                        : Results.BadRequest(new { message = "启动失败，请检查 ServerUrl 配置" });
                });

                _app.MapPost("/api/task-polling/stop", async () =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    await _taskPolling.StopAsync(CancellationToken.None);
                    return Results.Ok(new { message = "任务轮询服务已停止" });
                });

                _app.MapPost("/api/task-polling/trigger", async () =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    if (!_taskPolling.IsRunning) return Results.BadRequest(new { message = "任务轮询服务未运行，请先启动" });
                    var result = await _taskPolling.TriggerPollCycle();
                    if (result == null) return Results.Ok(new { message = "当前无可执行任务" });
                    return Results.Ok(new { success = result.Success, message = result.Message, duration = result.Duration });
                });

                _app.MapGet("/api/task-polling/health-check", async () =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    var healthy = await _taskPolling.CheckServerHealth();
                    return Results.Ok(new { server_reachable = healthy, server_url = _taskPolling.ServerUrl });
                });

                _app.MapGet("/api/task-polling/cached-results", () =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    var cached = _taskPolling.GetCachedResults();
                    return Results.Ok(new { count = cached.Count, items = cached.Select(c => new
                    {
                        c.TaskId,
                        success = c.Result?.Success,
                        message = c.Result?.Message,
                        c.CachedAt,
                        c.RetryCount
                    })});
                });

                _app.MapPost("/api/task-polling/flush-cache", async () =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    var flushed = await _taskPolling.FlushCachedResults();
                    return Results.Ok(new { message = $"已回传 {flushed} 条缓存结果", flushed });
                });

                // ═══ 任务历史接口 ═══

                _app.MapGet("/api/task-polling/history", () =>
                {
                    if (_taskPolling == null) return Results.Ok(new { items = Array.Empty<object>(), total = 0, completed = 0, failed = 0 });
                    var history = _taskPolling.TaskHistory.ToList();
                    return Results.Ok(new
                    {
                        items = history.Select(h => new
                        {
                            h.TaskId,
                            h.TaskType,
                            h.Description,
                            h.Status,
                            h.Message,
                            duration = Math.Round(h.Duration, 2),
                            h.StartedAt,
                            h.FinishedAt,
                            params_count = h.Params?.Count ?? 0
                        }),
                        total = history.Count,
                        completed = _taskPolling.TotalCompleted,
                        failed = _taskPolling.TotalFailed
                    });
                });

                _app.MapGet("/api/task-polling/history/{taskId}", (string taskId) =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    var item = _taskPolling.TaskHistory.FirstOrDefault(h => h.TaskId == taskId);
                    if (item == null) return Results.NotFound(new { message = $"任务不存在: {taskId}" });
                    return Results.Ok(new
                    {
                        item.TaskId,
                        item.TaskType,
                        item.Description,
                        item.Params,
                        item.Status,
                        item.Message,
                        duration = Math.Round(item.Duration, 2),
                        item.StartedAt,
                        item.FinishedAt
                    });
                });

                _app.MapPost("/api/task-polling/history/clear", () =>
                {
                    if (_taskPolling == null) return Results.BadRequest(new { message = "TaskPolling 服务未注册" });
                    _taskPolling.ClearHistory();
                    return Results.Ok(new { message = "历史记录已清除" });
                });

                // ═══ 执行状态接口 ═══

                _app.MapGet("/api/execution/status", () =>
                {
                    var isExecuting = _executor is ScriptExecutor se && se.IsExecuting;
                    return Results.Ok(new
                    {
                        executing = isExecuting,
                        scripts = _executor.GetAvailableScripts().Select(s => new
                        {
                            s.Name,
                            s.StepCount,
                            s.LastModified
                        })
                    });
                });

                _appTask = _app.StartAsync(_cts.Token);
                _logger.Info("HttpApi", $"HTTP API 服务器已启动: http://localhost:{Port}");
            }
            catch (Exception ex)
            {
                _logger.Error("HttpApi", "启动 HTTP API 失败", ex);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_app != null)
                {
                    _cts.Cancel();
                    await _app.StopAsync(cancellationToken);
                    _logger.Info("HttpApi", "HTTP API 服务器已停止");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("HttpApi", "停止 HTTP API 失败", ex);
            }
        }

        public void Dispose()
        {
            _cts?.Dispose();
        }
    }

    public class ExecuteRequest
    {
        public string ScriptName { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
    }

    public class ParseCommandRequest
    {
        public string Command { get; set; }
    }
}
