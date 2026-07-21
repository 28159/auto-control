using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WeChatAutomation.Core.Recording
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ActionType
    {
        Click,
        TypeText,
        SendKeys,
        Wait,
        Copy,
        Paste,
        InsertText,
        Screenshot,
        OpenApp,
        WaitForApp,
        Scroll,
        ReadContent,
        ScrollRead,
        InputParam,
        RegexMatch
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ClickMode
    {
        Coordinate,
        UIAPath,
        Vision
    }

    /// <summary>
    /// 录制的步骤节点
    /// </summary>
    public class RecordedAction
    {
        public string NodeId { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public int Order { get; set; }
        public ActionType ActionType { get; set; }
        public string Name { get; set; } = "";

        // 目标控件
        public string? ClassName { get; set; }
        public string? ElementName { get; set; }
        public string? AutomationId { get; set; }
        public string? ControlType { get; set; }
        public string? WindowTitle { get; set; } // 目标窗口标题
        public double X { get; set; }
        public double Y { get; set; }
        public ClickMode ClickMode { get; set; } = ClickMode.Coordinate;
        public string? VisionLabel { get; set; }
        public float VisionConfThreshold { get; set; } = 0.3f;

        // UIA 路径标识
        public string? XPath { get; set; }          // 祖先链 XPath，如 /Window[@Name='微信']/Pane/Button[@Name='发送']
        public int SiblingIndex { get; set; }       // 同级索引（0-based），XPath 中 [n] 消歧
        public string? RuntimeId { get; set; }      // RuntimeId 逗号分隔字符串，辅助标识

        // 参数
        public string Parameter { get; set; } = "";
        public int DelayMs { get; set; } = 300;
        public int ScrollAmount { get; set; } = 3; // 滚动行数

        // 动态输入参数
        public string? ParameterName { get; set; }
        public string? DefaultValue { get; set; }
        public bool IsRequired { get; set; } = true;
        public bool CopyToClipboard { get; set; }

        public string? RegexPattern { get; set; }
        public string? RegexGroup { get; set; }
        public string? OutputParamName { get; set; }

        // 元数据
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string Summary => ActionType switch
        {
            ActionType.Click => ClickMode == ClickMode.Coordinate
                ? $"点击坐标({X:F0},{Y:F0})"
                : ClickMode == ClickMode.Vision
                    ? $"视觉点击 {VisionLabel ?? "未知"}"
                    : !string.IsNullOrEmpty(XPath)
                        ? $"路径点击 {Trunc(XPath, 40)}"
                        : $"点击路径 {ElementName ?? ClassName ?? AutomationId ?? "未知"}",
            ActionType.TypeText => $"输入 \"{Trunc(Parameter, 20)}\"",
            ActionType.SendKeys => $"按键 {Parameter}",
            ActionType.Wait => $"等待 {Parameter}ms",
            ActionType.Copy => "复制 (Ctrl+C)",
            ActionType.Paste => "粘贴 (Ctrl+V)",
            ActionType.InsertText => $"插入 \"{Trunc(Parameter, 20)}\"",
            ActionType.Screenshot => "截图",
            ActionType.OpenApp => $"打开 {Trunc(Parameter, 30)}",
            ActionType.WaitForApp => $"等待应用 {Trunc(Parameter, 20)} ({DelayMs}ms超时)",
            ActionType.Scroll => $"滚动 {ScrollAmount} 行",
            ActionType.ReadContent => "阅读窗口内容",
            ActionType.ScrollRead => $"滚动阅读 {ScrollAmount} 行",
            ActionType.InputParam => $"输入参数 [{ParameterName ?? "未命名"}]{(CopyToClipboard ? " →剪切板" : "")}",
            ActionType.RegexMatch => $"正则识别 {Trunc(RegexPattern ?? "", 20)}{(CopyToClipboard ? " →剪切板" : "")}{(!string.IsNullOrEmpty(OutputParamName) ? $" →{{{OutputParamName}}}" : "")}",
            _ => ActionType.ToString()
        };

        public string TargetDesc
        {
            get
            {
                var p = new List<string>();
                if (!string.IsNullOrEmpty(ControlType)) p.Add(ControlType);
                if (!string.IsNullOrEmpty(ElementName)) p.Add(ElementName);
                else if (!string.IsNullOrEmpty(ClassName)) p.Add($"[{ClassName}]");
                if (X > 0 || Y > 0) p.Add($"@({X:F0},{Y:F0})");
                return p.Count > 0 ? string.Join(" ", p) : "-";
            }
        }

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

        public override string ToString() => $"[{Order}] {Summary}";
    }

    public class RecordingFile
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public List<RecordedAction> Actions { get; set; } = new();
        public List<ReadContentResult> ReadResults { get; set; } = new();
        public List<ScriptParameter> Parameters { get; set; } = new();
        public ClickMode DefaultClickMode { get; set; } = ClickMode.Coordinate;

        /// <summary>
        /// 视觉模式使用的 ONNX 模型文件名（位于运行目录 models/ 下，如 yolov8n-ui.onnx）。
        /// 留空时回退到默认 yolov8n-ui.onnx。每个脚本绑定一个模型，保存后回放/调用复用。
        /// </summary>
        public string? VisionModel { get; set; }
    }

    /// <summary>
    /// 脚本参数定义
    /// </summary>
    public class ScriptParameter
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DefaultValue { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsRequired { get; set; } = true;
        public ParameterType Type { get; set; } = ParameterType.Text;
        public bool CopyToClipboard { get; set; } = false;
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ParameterType
    {
        Text,
        Number,
        Password,
        MultiLine
    }

    public class ReadContentResult
    {
        public string WindowTitle { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CapturedAt { get; set; }
        public string Source { get; set; } = "";
        public List<RegexMatchItem> Matches { get; set; } = new();
        public string MatchedValue { get; set; } = "";
    }

    public class RegexMatchItem
    {
        public string Value { get; set; } = "";
        public int Index { get; set; }
        public Dictionary<string, string> Groups { get; set; } = new();
    }
}
