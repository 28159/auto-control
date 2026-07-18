using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Services
{
    public class MqttService : IHostedService, IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private readonly IScriptExecutor _executor;
        private readonly IConfiguration _configuration;
        private IMqttClient _mqttClient;
        private MqttClientOptions _options;
        private readonly CancellationTokenSource _cts = new();
        private string _topicPrefix;

        public bool IsConnected => _mqttClient?.IsConnected == true;
        public string BrokerHost { get; private set; }
        public int BrokerPort { get; private set; }

        public MqttService(IScriptExecutor executor, IConfiguration configuration)
        {
            _executor = executor;
            _configuration = configuration;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var mqttEnabled = _configuration.GetValue("Mqtt:Enabled", true);
                if (!mqttEnabled)
                {
                    _logger.Info("Mqtt", "MQTT 服务已禁用");
                    return;
                }

                BrokerHost = _configuration.GetValue("Mqtt:BrokerHost", "localhost");
                BrokerPort = _configuration.GetValue("Mqtt:BrokerPort", 1883);
                _topicPrefix = _configuration.GetValue("Mqtt:TopicPrefix", "wechat-auto");

                var factory = new MqttFactory();
                _mqttClient = factory.CreateMqttClient();

                _options = new MqttClientOptionsBuilder()
                    .WithClientId($"WeChatAutomation_{Guid.NewGuid():N}")
                    .WithTcpServer(BrokerHost, BrokerPort)
                    .WithCleanSession()
                    .Build();

                // 设置事件处理
                _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
                _mqttClient.ConnectedAsync += OnConnectedAsync;
                _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

                // 连接
                var connectResult = await _mqttClient.ConnectAsync(_options, _cts.Token);
                if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                {
                    _logger.Warn("Mqtt", $"MQTT 连接失败: {connectResult.ResultCode}");
                    return;
                }

                _logger.Info("Mqtt", $"MQTT 已连接到 {BrokerHost}:{BrokerPort}");
            }
            catch (Exception ex)
            {
                _logger.Error("Mqtt", "启动 MQTT 服务失败", ex);
            }
        }

        private async Task OnConnectedAsync(MqttClientConnectedEventArgs args)
        {
            _logger.Info("Mqtt", "MQTT 连接成功");

            // 订阅执行命令主题
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic($"{_topicPrefix}/execute/#")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            await _mqttClient.SubscribeAsync(topicFilter, _cts.Token);
            _logger.Info("Mqtt", $"已订阅主题: {_topicPrefix}/execute/#");

            // 发布在线状态
            await PublishStatus("online");
        }

        private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            _logger.Warn("Mqtt", "MQTT 连接断开");

            // 尝试重连
            if (!_cts.IsCancellationRequested)
            {
                await Task.Delay(5000, _cts.Token);
                try
                {
                    await _mqttClient.ConnectAsync(_options, _cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.Error("Mqtt", "MQTT 重连失败", ex);
                }
            }
        }

        private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var topic = args.ApplicationMessage.Topic;
                var payload = args.ApplicationMessage.PayloadSegment.ToArray();
                var payloadStr = System.Text.Encoding.UTF8.GetString(payload);

                _logger.Info("Mqtt", $"收到消息: {topic} -> {payloadStr}");

                // 解析脚本名称
                var prefix = $"{_topicPrefix}/execute/";
                if (!topic.StartsWith(prefix))
                {
                    _logger.Warn("Mqtt", $"未知主题格式: {topic}");
                    return;
                }

                var scriptName = topic[prefix.Length..];
                if (string.IsNullOrEmpty(scriptName))
                {
                    _logger.Warn("Mqtt", "脚本名称为空");
                    return;
                }

                // 发布执行中状态
                await PublishStatus("executing", scriptName);

                // 解析参数
                Dictionary<string, string> parameters = null;
                if (!string.IsNullOrEmpty(payloadStr))
                {
                    try
                    {
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var request = JsonSerializer.Deserialize<ExecuteRequest>(payloadStr, options);
                        parameters = request?.Parameters;
                        if (!string.IsNullOrEmpty(request?.ScriptName))
                        {
                            scriptName = request.ScriptName;
                        }
                    }
                    catch
                    {
                        // 忽略 JSON 解析错误，使用原始脚本名称
                    }
                }

                // 执行脚本
                var result = await _executor.ExecuteScript(scriptName, parameters);

                // 发布执行结果
                await PublishResult(result);

                _logger.Info("Mqtt", $"脚本执行完成: {scriptName} - {(result.Success ? "成功" : "失败")}");
            }
            catch (Exception ex)
            {
                _logger.Error("Mqtt", "处理消息失败", ex);
            }
        }

        private async Task PublishStatus(string status, string scriptName = null)
        {
            if (_mqttClient?.IsConnected != true) return;

            var message = new
            {
                status,
                script = scriptName,
                timestamp = DateTime.Now
            };

            var payload = JsonSerializer.Serialize(message);
            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic($"{_topicPrefix}/status")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            await _mqttClient.PublishAsync(applicationMessage, _cts.Token);
        }

        private async Task PublishResult(ExecuteResult result)
        {
            if (_mqttClient?.IsConnected != true) return;

            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic($"{_topicPrefix}/result")
                .WithPayload(JsonSerializer.Serialize(result))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();

            await _mqttClient.PublishAsync(applicationMessage, _cts.Token);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_mqttClient?.IsConnected == true)
                {
                    await PublishStatus("offline");
                    await _mqttClient.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
                }
                _logger.Info("Mqtt", "MQTT 服务已停止");
            }
            catch (Exception ex)
            {
                _logger.Error("Mqtt", "停止 MQTT 服务失败", ex);
            }
        }

        public void Dispose()
        {
            // 可能被 App.OnExit 和 DI 容器各调用一次；Cancel 对已释放的 CTS 会抛 ObjectDisposedException
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts?.Dispose();
            _mqttClient?.Dispose();
        }
    }
}
