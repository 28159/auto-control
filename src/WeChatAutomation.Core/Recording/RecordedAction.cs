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
        ScrollRead
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

        // 参数
        public string Parameter { get; set; } = "";
        public int DelayMs { get; set; } = 300;
        public int ScrollAmount { get; set; } = 3; // 滚动行数

        // 元数据
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string Summary => ActionType switch
        {
            ActionType.Click => $"点击 {ElementName ?? ClassName ?? $"({X:F0},{Y:F0})"}",
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
    }

    public class ReadContentResult
    {
        public string WindowTitle { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CapturedAt { get; set; }
        public string Source { get; set; } = ""; // "ReadContent" 或 "ScrollRead"
    }
}
