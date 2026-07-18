using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeChatAutomation.Core;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Services;

namespace WeChatAutomation.App;

public partial class App : Application
{
    private IHost _host;
    private static readonly Logger _logger = Logger.Instance;

    public static IScriptExecutor ScriptExecutor { get; private set; }
    public static HttpApiService HttpApi { get; private set; }
    public static MqttService Mqtt { get; private set; }
    public static McpServerService Mcp { get; set; }
    public static IConfiguration Configuration { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.Sources.Clear();
                config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IScriptExecutor, ScriptExecutor>();
                services.AddSingleton<HttpApiService>();
                services.AddSingleton<MqttService>();
                services.AddSingleton<McpServerService>();
            })
            .Build();

        // 保存配置实例
        Configuration = _host.Services.GetRequiredService<IConfiguration>();

        // 启动服务
        _host.Start();

        // 获取服务实例
        ScriptExecutor = _host.Services.GetRequiredService<IScriptExecutor>();
        HttpApi = _host.Services.GetRequiredService<HttpApiService>();
        Mqtt = _host.Services.GetRequiredService<MqttService>();

        // 根据配置决定是否启动各服务
        var httpEnabled = Configuration.GetValue("HttpApi:Enabled", false);
        var mqttEnabled = Configuration.GetValue("Mqtt:Enabled", false);
        var mcpEnabled = Configuration.GetValue("Mcp:Enabled", false);

        if (httpEnabled)
        {
            _ = HttpApi.StartAsync(CancellationToken.None);
            _logger.Info("App", "HTTP API 服务已启动");
        }

        if (mqttEnabled)
        {
            _ = Mqtt.StartAsync(CancellationToken.None);
            _logger.Info("App", "MQTT 服务已启动");
        }

        if (mcpEnabled)
        {
            Mcp = _host.Services.GetRequiredService<McpServerService>();
            _ = Mcp.StartAsync(CancellationToken.None);
            _logger.Info("App", "MCP Server 已启动");
        }

        _logger.Info("App", "应用已启动");

        // 诊断：把日志写到项目根 wizard.log，便于排查向导/标注问题
        try
        {
            string logPath = System.IO.Path.Combine(AppPaths.ProjectRoot, "wizard.log");
            Logger.Instance.AddSink(new FileSink(logPath));
            _logger.Info("App", $"日志文件: {logPath}");
        }
        catch (Exception ex)
        {
            _logger.Warn("App", $"无法创建日志文件: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger.Info("App", "应用正在关闭...");

        try
        {
            HttpApi?.StopAsync(CancellationToken.None).Wait(5000);
            Mqtt?.StopAsync(CancellationToken.None).Wait(5000);
            Mcp?.StopAsync(CancellationToken.None).Wait(5000);

            HttpApi?.Dispose();
            Mqtt?.Dispose();
            Mcp?.Dispose();
            (ScriptExecutor as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Error("App", "关闭服务失败", ex);
        }

        _host?.Dispose();
        base.OnExit(e);
    }
}
