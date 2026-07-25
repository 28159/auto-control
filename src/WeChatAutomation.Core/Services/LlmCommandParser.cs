using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.Core.Services
{
    /// <summary>
    /// 自然语言命令解析服务 - 将中文命令解析为结构化的 RecordedAction 列表
    /// 支持两种模式：正则快速解析（离线）和 LLM API 解析（在线）
    /// </summary>
    public class LlmCommandParser
    {
        private static readonly Logger _logger = Logger.Instance;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "gpt-4o-mini";
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// 正则快速解析（无需网络）
        /// </summary>
        public List<RecordedAction> ParseCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return new List<RecordedAction>();

            command = command.Trim();

            // 点击XX按钮/链接/图标
            var clickMatch = Regex.Match(command, @"点击(.+?)(按钮|链接|图标|标签|菜单|复选框|单选框|下拉框|输入框|搜索框|发送按钮|关闭按钮)");
            if (clickMatch.Success)
            {
                string elementDesc = clickMatch.Groups[1].Value + clickMatch.Groups[2].Value;
                string visionLabel = MapElementToVisionLabel(clickMatch.Groups[2].Value, clickMatch.Groups[1].Value);
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Click,
                        ClickMode = ClickMode.Vision,
                        VisionLabel = visionLabel,
                        Name = $"视觉点击 {visionLabel}",
                        ElementName = elementDesc,
                        DelayMs = 300
                    }
                };
            }

            // 点击坐标(x,y)
            var coordMatch = Regex.Match(command, @"点击坐标?\s*[\(（](\d+)\s*[,，]\s*(\d+)[\)）]");
            if (coordMatch.Success)
            {
                double x = double.Parse(coordMatch.Groups[1].Value);
                double y = double.Parse(coordMatch.Groups[2].Value);
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Click,
                        ClickMode = ClickMode.Coordinate,
                        X = x, Y = y,
                        Name = $"点击坐标({x:F0},{y:F0})",
                        DelayMs = 300
                    }
                };
            }

            // 在XX框输入YY / 输入YY到XX框 / 输入YY
            var typeMatch = Regex.Match(command, @"在(.+?)(框|输入框|搜索框|文本框|编辑框)输入(.+)");
            if (typeMatch.Success)
            {
                string elementDesc = typeMatch.Groups[1].Value + typeMatch.Groups[2].Value;
                string text = typeMatch.Groups[3].Value.Trim().Trim('"', '\'', '「', '」', '"', '"');
                string visionLabel = MapElementToVisionLabel(typeMatch.Groups[2].Value, typeMatch.Groups[1].Value);
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Click,
                        ClickMode = ClickMode.Vision,
                        VisionLabel = visionLabel,
                        Name = $"视觉点击 {visionLabel}",
                        ElementName = elementDesc,
                        DelayMs = 300
                    },
                    new()
                    {
                        ActionType = ActionType.TypeText,
                        Parameter = text,
                        Name = $"输入: {(text.Length > 20 ? text[..20] + "..." : text)}",
                        DelayMs = 300
                    }
                };
            }

            // 直接输入
            var directTypeMatch = Regex.Match(command, @"^输入\s*['「“‘](.+?)['」”’]$");
            if (directTypeMatch.Success)
            {
                string text = directTypeMatch.Groups[1].Value;
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.TypeText,
                        Parameter = text,
                        Name = $"输入: {(text.Length > 20 ? text[..20] + "..." : text)}",
                        DelayMs = 300
                    }
                };
            }

            // 随机等待N~M秒
            var waitRangeMatch = Regex.Match(command, @"随机等待\s*(\d+)\s*[-~到]\s*(\d+)\s*(秒|s)");
            if (waitRangeMatch.Success)
            {
                int minSec = Math.Clamp(int.Parse(waitRangeMatch.Groups[1].Value), 1, 60);
                int maxSec = Math.Clamp(int.Parse(waitRangeMatch.Groups[2].Value), 1, 60);
                if (maxSec < minSec) maxSec = minSec;
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Wait,
                        Parameter = "",
                        RandomWaitEnabled = true,
                        RandomWaitMinSec = minSec,
                        RandomWaitMaxSec = maxSec,
                        Name = $"随机等待{minSec}~{maxSec}秒",
                        DelayMs = 0
                    }
                };
            }

            // 等待N秒/毫秒（兼容旧格式，转为随机区间：N秒 → N~N*1.5秒，毫秒 → 固定毫秒）
            var waitMatch = Regex.Match(command, @"等待\s*(\d+)\s*(秒|毫秒|ms|s)");
            if (waitMatch.Success)
            {
                int value = int.Parse(waitMatch.Groups[1].Value);
                string unit = waitMatch.Groups[2].Value;
                if (unit is "秒" or "s")
                {
                    // 秒数转为随机区间：N → N~N*1.5秒
                    int minSec = Math.Clamp(value, 1, 60);
                    int maxSec = Math.Clamp((int)Math.Round(value * 1.5), minSec, 60);
                    return new List<RecordedAction>
                    {
                        new()
                        {
                            ActionType = ActionType.Wait,
                            Parameter = "",
                            RandomWaitEnabled = true,
                            RandomWaitMinSec = minSec,
                            RandomWaitMaxSec = maxSec,
                            Name = $"随机等待{minSec}~{maxSec}秒",
                            DelayMs = 0
                        }
                    };
                }
                else
                {
                    // 毫秒保持旧模式
                    return new List<RecordedAction>
                    {
                        new()
                        {
                            ActionType = ActionType.Wait,
                            Parameter = value.ToString(),
                            Name = $"等待 {value}ms",
                            DelayMs = 0
                        }
                    };
                }
            }

            // 按XX键
            var keyMatch = Regex.Match(command, @"按\s*(.+?)\s*键");
            if (keyMatch.Success)
            {
                string key = keyMatch.Groups[1].Value.Trim();
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.SendKeys,
                        Parameter = key,
                        Name = key,
                        DelayMs = 300
                    }
                };
            }

            // 截图
            if (command.Contains("截图"))
            {
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Screenshot,
                        Name = "截图",
                        DelayMs = 0
                    }
                };
            }

            // 打开XX
            var openMatch = Regex.Match(command, @"打开\s*(.+)");
            if (openMatch.Success)
            {
                string app = openMatch.Groups[1].Value.Trim();
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.OpenApp,
                        Parameter = app,
                        Name = $"打开 {app}",
                        DelayMs = 1000
                    }
                };
            }

            // 滚动N行
            var scrollMatch = Regex.Match(command, @"(向下|向上)?\s*滚动\s*(\d+)\s*行");
            if (scrollMatch.Success)
            {
                int lines = int.Parse(scrollMatch.Groups[2].Value);
                if (scrollMatch.Groups[1].Value == "向上") lines = -lines;
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Scroll,
                        ScrollAmount = lines,
                        Name = $"滚动 {lines} 行",
                        DelayMs = 300
                    }
                };
            }

            // 阅读/读取窗口内容
            if (command.Contains("阅读") || command.Contains("读取"))
            {
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.ReadContent,
                        Name = "阅读窗口内容",
                        DelayMs = 300
                    }
                };
            }

            // 复制
            if (command.Contains("复制"))
            {
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Copy,
                        Name = "复制 (Ctrl+C)",
                        DelayMs = 300
                    }
                };
            }

            // 粘贴
            if (command.Contains("粘贴"))
            {
                return new List<RecordedAction>
                {
                    new()
                    {
                        ActionType = ActionType.Paste,
                        Name = "粘贴 (Ctrl+V)",
                        DelayMs = 300
                    }
                };
            }

            _logger.Warn("LlmParser", $"正则解析失败，无法识别命令: {command}");
            return new List<RecordedAction>();
        }

        /// <summary>
        /// LLM API 解析（需配置 API Key）
        /// </summary>
        public async Task<List<RecordedAction>> ParseCommandWithLlmAsync(string command)
        {
            if (!Enabled || string.IsNullOrEmpty(ApiKey))
            {
                _logger.Warn("LlmParser", "LLM 解析未启用或未配置 API Key");
                return ParseCommand(command);
            }

            try
            {
                var requestBody = BuildLlmRequestBody(command);
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");

                var response = await _httpClient.PostAsync(Endpoint, content);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                return ParseLlmResponse(responseBody);
            }
            catch (Exception ex)
            {
                _logger.Warn("LlmParser", $"LLM API 调用失败: {ex.Message}，回退到正则解析");
                return ParseCommand(command);
            }
        }

        /// <summary>
        /// 映射元素描述到 VisionLabel
        /// </summary>
        private static string MapElementToVisionLabel(string elementType, string elementName)
        {
            string lower = elementType.ToLower();

            if (lower.Contains("发送")) return "send_button";
            if (lower.Contains("关闭")) return "close_button";
            if (lower.Contains("最小化")) return "minimize_button";
            if (lower.Contains("最大化")) return "maximize_button";
            if (lower.Contains("搜索")) return "search_box";
            if (lower.Contains("输入") || lower.Contains("编辑") || lower.Contains("文本")) return "input";
            if (lower.Contains("按钮")) return "button";
            if (lower.Contains("链接")) return "link";
            if (lower.Contains("图标")) return "icon";
            if (lower.Contains("菜单")) return "menu_item";
            if (lower.Contains("复选")) return "checkbox";
            if (lower.Contains("单选")) return "radio";
            if (lower.Contains("下拉")) return "dropdown";
            if (lower.Contains("标签")) return "tab";

            // 通过元素名称推断
            string nameLower = elementName.ToLower();
            if (nameLower.Contains("发送") || nameLower.Contains("send")) return "send_button";
            if (nameLower.Contains("关闭") || nameLower.Contains("close")) return "close_button";
            if (nameLower.Contains("搜索") || nameLower.Contains("search")) return "search_box";

            return "button";
        }

        /// <summary>
        /// 构建 LLM API 请求体
        /// </summary>
        private string BuildLlmRequestBody(string command)
        {
            string systemPrompt = @"你是一个Windows桌面自动化命令解析器。将用户的自然语言指令解析为结构化的自动化动作。

可用的动作类型（ActionType）:
- Click: 点击元素
- TypeText: 输入文本
- SendKeys: 按键（如 Enter, Tab, Ctrl+C）
- Wait: 等待（参数为毫秒数）
- Copy: 复制（Ctrl+C）
- Paste: 粘贴（Ctrl+V）
- Screenshot: 截图
- OpenApp: 打开应用
- Scroll: 滚动

点击模式（ClickMode）:
- Coordinate: 坐标模式（需要X,Y）
- UIAPath: 路径模式（通过UIA属性定位）
- Vision: 视觉模式（通过YOLO识别）

视觉标签（VisionLabel）:
- button, input, search_box, send_button, close_button, minimize_button, maximize_button
- checkbox, radio, dropdown, tab, menu_item, icon, link, text_field, scrollbar, slider, toggle, tooltip, image

规则：
1. '点击XX' 类命令 → Click + Vision模式
2. '在XX框输入YY' → Click(定位输入框) + TypeText(输入文本)
3. '等待N秒' → Wait(参数=N*1000毫秒)
4. 返回JSON数组，每个元素包含: ActionType, ClickMode, VisionLabel, Parameter, X, Y, ElementName, ScrollAmount

只返回JSON数组，不要其他文本。";

            var request = new
            {
                model = Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = command }
                },
                temperature = 0.1,
                max_tokens = 1024
            };

            return JsonSerializer.Serialize(request);
        }

        /// <summary>
        /// 解析 LLM API 响应
        /// </summary>
        private List<RecordedAction> ParseLlmResponse(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // 提取 assistant 消息内容
                string content = root
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                // 提取 JSON 数组（可能被 markdown 代码块包裹）
                content = content.Trim();
                if (content.StartsWith("```"))
                {
                    int start = content.IndexOf('[');
                    int end = content.LastIndexOf(']');
                    if (start >= 0 && end > start)
                        content = content[start..(end + 1)];
                }

                var actions = JsonSerializer.Deserialize<List<LlmParsedAction>>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (actions == null) return new List<RecordedAction>();

                return actions.Select((a, i) => new RecordedAction
                {
                    Order = i + 1,
                    ActionType = a.ActionType ?? ActionType.Click,
                    ClickMode = a.ClickMode ?? ClickMode.Vision,
                    VisionLabel = a.VisionLabel,
                    Parameter = a.Parameter ?? "",
                    X = a.X ?? 0,
                    Y = a.Y ?? 0,
                    ElementName = a.ElementName,
                    ScrollAmount = a.ScrollAmount ?? 3,
                    Name = BuildActionName(a),
                    DelayMs = 300
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.Warn("LlmParser", $"LLM 响应解析失败: {ex.Message}");
                return new List<RecordedAction>();
            }
        }

        private static string BuildActionName(LlmParsedAction a)
        {
            return (a.ActionType ?? ActionType.Click) switch
            {
                ActionType.Click => a.ClickMode == ClickMode.Coordinate
                    ? $"点击坐标({a.X ?? 0:F0},{a.Y ?? 0:F0})"
                    : a.ClickMode == ClickMode.Vision
                        ? $"视觉点击 {a.VisionLabel ?? "未知"}"
                        : $"点击路径 {a.ElementName ?? "未知"}",
                ActionType.TypeText => $"输入: {(a.Parameter?.Length > 20 ? a.Parameter[..20] + "..." : a.Parameter ?? "")}",
                ActionType.SendKeys => a.Parameter ?? "",
                ActionType.Wait => $"等待 {a.Parameter}ms",
                ActionType.Copy => "复制 (Ctrl+C)",
                ActionType.Paste => "粘贴 (Ctrl+V)",
                ActionType.Screenshot => "截图",
                ActionType.OpenApp => $"打开 {a.Parameter}",
                ActionType.Scroll => $"滚动 {a.ScrollAmount ?? 3} 行",
                _ => a.ActionType?.ToString() ?? "未知"
            };
        }

        /// <summary>
        /// LLM 解析结果模型
        /// </summary>
        private class LlmParsedAction
        {
            public ActionType? ActionType { get; set; }
            public ClickMode? ClickMode { get; set; }
            public string? VisionLabel { get; set; }
            public string? Parameter { get; set; }
            public double? X { get; set; }
            public double? Y { get; set; }
            public string? ElementName { get; set; }
            public int? ScrollAmount { get; set; }
        }
    }
}
