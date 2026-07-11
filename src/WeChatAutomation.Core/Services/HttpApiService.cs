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
        private WebApplication _app;
        private Task _appTask;
        private readonly CancellationTokenSource _cts = new();

        public bool IsRunning => _app != null;
        public int Port { get; private set; }

        public HttpApiService(IScriptExecutor executor, IConfiguration configuration)
        {
            _executor = executor;
            _configuration = configuration;
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
                builder.Services.AddEndpointsApiExplorer();
                builder.Logging.ClearProviders();

                builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");

                _app = builder.Build();

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

                _app.MapGet("/api/health", () => Results.Ok("OK"));

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
}
