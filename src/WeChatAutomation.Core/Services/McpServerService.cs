using System;
using System.Collections.Generic;
using System.IO;
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
    /// MCP (Model Context Protocol) Server 服务
    /// 通过标准输入输出通信，供 OpenClaw 等工具调用
    /// </summary>
    public class McpServerService : IHostedService, IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private readonly IScriptExecutor _executor;
        private readonly IConfiguration _configuration;
        private readonly CancellationTokenSource _cts = new();
        private Task _readLoop;
        private bool _initialized;

        public McpServerService(IScriptExecutor executor, IConfiguration configuration)
        {
            _executor = executor;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var mcpEnabled = _configuration.GetValue("Mcp:Enabled", false);
                if (!mcpEnabled)
                {
                    _logger.Info("Mcp", "MCP Server 已禁用");
                    return Task.CompletedTask;
                }

                _logger.Info("Mcp", "MCP Server 已启动，等待标准输入...");
                _readLoop = ReadInputLoop(_cts.Token);
            }
            catch (Exception ex)
            {
                _logger.Error("Mcp", "启动 MCP Server 失败", ex);
            }

            return Task.CompletedTask;
        }

        private async Task ReadInputLoop(CancellationToken ct)
        {
            var stdin = Console.In;
            var stdout = Console.Out;

            // 重定向标准输出以避免干扰
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await stdin.ReadLineAsync(ct);
                    if (line == null) break;

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var request = JsonSerializer.Deserialize<McpRequest>(line);
                        if (request == null) continue;

                        var response = await HandleRequest(request);
                        var responseJson = JsonSerializer.Serialize(response, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                        });

                        await stdout.WriteLineAsync(responseJson);
                        await stdout.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Mcp", $"处理请求失败: {ex.Message}", ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error("Mcp", "读取循环异常", ex);
            }
        }

        private async Task<McpResponse> HandleRequest(McpRequest request)
        {
            switch (request.Method)
            {
                case "initialize":
                    _initialized = true;
                    return new McpResponse
                    {
                        Id = request.Id,
                        Result = new McpInitializeResult
                        {
                            ProtocolVersion = "2024-11-05",
                            Capabilities = new McpCapabilities
                            {
                                Tools = new McpToolsCapability
                                {
                                    ListChanged = false
                                }
                            },
                            ServerInfo = new McpServerInfo
                            {
                                Name = "WeChatAutomation",
                                Version = "1.0.0"
                            }
                        }
                    };

                case "notifications/initialized":
                    // 客户端确认初始化完成
                    return null; // 不需要响应

                case "tools/list":
                    return new McpResponse
                    {
                        Id = request.Id,
                        Result = new McpToolsListResult
                        {
                            Tools = GetToolDefinitions()
                        }
                    };

                case "tools/call":
                    return await HandleToolCall(request);

                case "ping":
                    return new McpResponse
                    {
                        Id = request.Id,
                        Result = new { }
                    };

                default:
                    return new McpResponse
                    {
                        Id = request.Id,
                        Error = new McpError
                        {
                            Code = -32601,
                            Message = $"未知方法: {request.Method}"
                        }
                    };
            }
        }

        private List<McpTool> GetToolDefinitions()
        {
            return new List<McpTool>
            {
                new McpTool
                {
                    Name = "list_scripts",
                    Description = "列出所有可用的自动化脚本",
                    InputSchema = new McpInputSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, McpSchemaProperty>()
                    }
                },
                new McpTool
                {
                    Name = "get_script_info",
                    Description = "获取指定脚本的详细信息",
                    InputSchema = new McpInputSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, McpSchemaProperty>
                        {
                            ["script_name"] = new McpSchemaProperty
                            {
                                Type = "string",
                                Description = "脚本名称"
                            }
                        },
                        Required = new List<string> { "script_name" }
                    }
                },
                new McpTool
                {
                    Name = "execute_script",
                    Description = "执行指定的自动化脚本",
                    InputSchema = new McpInputSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, McpSchemaProperty>
                        {
                            ["script_name"] = new McpSchemaProperty
                            {
                                Type = "string",
                                Description = "脚本名称"
                            },
                            ["parameters"] = new McpSchemaProperty
                            {
                                Type = "object",
                                Description = "参数字典，key 可使用参数 Id 或参数名称，value 为参数值。如 {\"abc123\": \"值\"} 或 {\"message\": \"你好\"}"
                            }
                        },
                        Required = new List<string> { "script_name" }
                    }
                },
                new McpTool
                {
                    Name = "check_script_exists",
                    Description = "检查指定脚本是否存在",
                    InputSchema = new McpInputSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, McpSchemaProperty>
                        {
                            ["script_name"] = new McpSchemaProperty
                            {
                                Type = "string",
                                Description = "脚本名称"
                            }
                        },
                        Required = new List<string> { "script_name" }
                    }
                },
                new McpTool
                {
                    Name = "parse_command",
                    Description = "将自然语言命令解析为自动化步骤（如'点击发送按钮'、'在搜索框输入你好'）",
                    InputSchema = new McpInputSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, McpSchemaProperty>
                        {
                            ["command"] = new McpSchemaProperty
                            {
                                Type = "string",
                                Description = "自然语言命令，如 '点击发送按钮' 或 '在搜索框输入你好'"
                            }
                        },
                        Required = new List<string> { "command" }
                    }
                }
            };
        }

        private async Task<McpResponse> HandleToolCall(McpRequest request)
        {
            var toolCall = request.Params as JsonElement?;
            if (toolCall == null)
            {
                return new McpResponse
                {
                    Id = request.Id,
                    Error = new McpError { Code = -32602, Message = "无效的工具调用参数" }
                };
            }

            try
            {
                var paramsJson = toolCall.Value.GetRawText();
                var toolParams = JsonSerializer.Deserialize<McpToolCallParams>(paramsJson);

                switch (toolParams.Name)
                {
                    case "list_scripts":
                        return HandleListScripts(request);

                    case "get_script_info":
                        return HandleGetScriptInfo(request, toolParams);

                    case "execute_script":
                        return await HandleExecuteScript(request, toolParams);

                    case "check_script_exists":
                        return HandleCheckScriptExists(request, toolParams);

                    case "parse_command":
                        return HandleParseCommand(request, toolParams);

                    default:
                        return new McpResponse
                        {
                            Id = request.Id,
                            Error = new McpError { Code = -32601, Message = $"未知工具: {toolParams.Name}" }
                        };
                }
            }
            catch (Exception ex)
            {
                return new McpResponse
                {
                    Id = request.Id,
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent
                        {
                            Type = "text",
                            Text = $"工具执行失败: {ex.Message}"
                        }
                    }
                };
            }
        }

        private McpResponse HandleListScripts(McpRequest request)
        {
            var scripts = _executor.GetAvailableScripts();
            var text = scripts.Count > 0
                ? string.Join("\n", scripts.Select(s => $"- {s.Name} ({s.StepCount} 步)"))
                : "没有可用的脚本";

            return new McpResponse
            {
                Id = request.Id,
                Result = new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = text }
                    }
                }
            };
        }

        private McpResponse HandleGetScriptInfo(McpRequest request, McpToolCallParams toolParams)
        {
            var scriptName = toolParams.Arguments?.GetValueOrDefault("script_name")?.ToString();
            if (string.IsNullOrEmpty(scriptName))
            {
                return new McpResponse
                {
                    Id = request.Id,
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = "缺少 script_name 参数" }
                    }
                };
            }

            var info = _executor.GetScriptInfo(scriptName);
            if (info == null)
            {
                return new McpResponse
                {
                    Id = request.Id,
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = $"脚本不存在: {scriptName}" }
                    }
                };
            }

            var text = $"脚本名称: {info.Name}\n" +
                       $"创建时间: {info.CreatedAt}\n" +
                       $"步骤数量: {info.Actions?.Count ?? 0}\n" +
                       $"参数列表:\n" +
                       string.Join("\n", info.Parameters?.Select(p => $"  [{p.Id}] {p.Name} ({p.DisplayName ?? p.Name}) = \"{p.DefaultValue}\"{(p.CopyToClipboard ? " [→剪切板]" : "")}{(p.IsRequired ? " 必填" : " 选填")}") ?? Array.Empty<string>()) +
                       (info.Parameters?.Count > 0 ? "\n" : "") +
                       $"步骤列表:\n" +
                       string.Join("\n", info.Actions?.Select(a => $"  {a.Order}. {a.Summary}") ?? Array.Empty<string>());

            return new McpResponse
            {
                Id = request.Id,
                Result = new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = text }
                    }
                }
            };
        }

        private async Task<McpResponse> HandleExecuteScript(McpRequest request, McpToolCallParams toolParams)
        {
            var scriptName = toolParams.Arguments?.GetValueOrDefault("script_name")?.ToString();
            if (string.IsNullOrEmpty(scriptName))
            {
                return new McpResponse
                {
                    Id = request.Id,
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = "缺少 script_name 参数" }
                    }
                };
            }

            Dictionary<string, string> parameters = null;
            if (toolParams.Arguments != null && toolParams.Arguments.TryGetValue("parameters", out var paramsValue))
            {
                var paramsJson = paramsValue.ToString();
                if (!string.IsNullOrEmpty(paramsJson) && paramsJson != "{}")
                {
                    parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(paramsJson);
                }
            }

            var result = await _executor.ExecuteScript(scriptName, parameters);

            var text = result.Success
                ? $"脚本 {scriptName} 执行成功\n执行时间: {result.ExecutedAt}"
                : $"脚本 {scriptName} 执行失败\n错误信息: {result.Message}";

            return new McpResponse
            {
                Id = request.Id,
                IsError = !result.Success,
                Result = new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = text }
                    }
                }
            };
        }

        private McpResponse HandleCheckScriptExists(McpRequest request, McpToolCallParams toolParams)
        {
            var scriptName = toolParams.Arguments?.GetValueOrDefault("script_name")?.ToString();
            if (string.IsNullOrEmpty(scriptName))
            {
                return new McpResponse
                {
                    Id = request.Id,
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = "缺少 script_name 参数" }
                    }
                };
            }

            var exists = _executor.IsScriptExist(scriptName);
            return new McpResponse
            {
                Id = request.Id,
                Result = new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new McpContent
                        {
                            Type = "text",
                            Text = exists ? $"脚本 {scriptName} 存在" : $"脚本 {scriptName} 不存在"
                        }
                    }
                }
            };
        }

        private McpResponse HandleParseCommand(McpRequest request, McpToolCallParams toolParams)
        {
            var command = toolParams.Arguments?.GetValueOrDefault("command")?.ToString();
            if (string.IsNullOrEmpty(command))
            {
                return new McpResponse
                {
                    Id = request.Id,
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = "缺少 command 参数" }
                    }
                };
            }

            try
            {
                var parser = new LlmCommandParser();
                var actions = parser.ParseCommand(command);
                var result = actions.Count > 0
                    ? string.Join("\n", actions.Select(a => a.Summary))
                    : "无法解析该命令";

                return new McpResponse
                {
                    Id = request.Id,
                    Result = new McpToolResult
                    {
                        Content = new List<McpContent>
                        {
                            new McpContent { Type = "text", Text = result }
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return new McpResponse
                {
                    Id = request.Id,
                    IsError = true,
                    Content = new List<McpContent>
                    {
                        new McpContent { Type = "text", Text = $"命令解析失败: {ex.Message}" }
                    }
                };
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            _logger.Info("Mcp", "MCP Server 已停止");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cts?.Dispose();
        }
    }

    // MCP 协议数据模型
    public class McpRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public object Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("params")]
        public object Params { get; set; }
    }

    public class McpResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public object Id { get; set; }

        [JsonPropertyName("result")]
        public object Result { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpError Error { get; set; }

        [JsonPropertyName("isError")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsError { get; set; }

        [JsonPropertyName("content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<McpContent> Content { get; set; }
    }

    public class McpError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }

    public class McpInitializeResult
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; }

        [JsonPropertyName("capabilities")]
        public McpCapabilities Capabilities { get; set; }

        [JsonPropertyName("serverInfo")]
        public McpServerInfo ServerInfo { get; set; }
    }

    public class McpCapabilities
    {
        [JsonPropertyName("tools")]
        public McpToolsCapability Tools { get; set; }
    }

    public class McpToolsCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class McpServerInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }
    }

    public class McpToolsListResult
    {
        [JsonPropertyName("tools")]
        public List<McpTool> Tools { get; set; }
    }

    public class McpTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("inputSchema")]
        public McpInputSchema InputSchema { get; set; }
    }

    public class McpInputSchema
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("properties")]
        public Dictionary<string, McpSchemaProperty> Properties { get; set; }

        [JsonPropertyName("required")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> Required { get; set; }
    }

    public class McpSchemaProperty
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }
    }

    public class McpToolResult
    {
        [JsonPropertyName("content")]
        public List<McpContent> Content { get; set; }
    }

    public class McpContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; }
    }

    public class McpToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("arguments")]
        public Dictionary<string, object> Arguments { get; set; }
    }
}
