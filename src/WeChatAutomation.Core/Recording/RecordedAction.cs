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
        RegexMatch,
        If,
        Goto,
        SwitchToWindow,
        /// <summary>条件循环：条件成立时重复执行 TrueActions（循环体）。</summary>
        While,
        /// <summary>固定次数循环：执行 LoopCount 次 TrueActions（循环体）。</summary>
        Loop,
        /// <summary>容错：执行 TrueActions（Try 体），出错时执行 FalseActions（Catch 体）。</summary>
        Try,
        /// <summary>跳出当前循环（While/Loop）。</summary>
        Break,
        /// <summary>进入当前循环下一轮。</summary>
        Continue,
        /// <summary>等待外部 HTTP 触发：脚本暂停，直到带匹配 WaitKey 的 HTTP 请求到达（可传参写入变量）。</summary>
        HttpWait,
        /// <summary>主动调用外部 HTTP API：发起请求并把响应写入 ResponseVarName 变量。</summary>
        HttpCall
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ClickMode
    {
        Coordinate,
        UIAPath,
        Vision
    }

    /// <summary>
    /// If 分支（成立/不成立）的行为类型。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum IfBranchAction
    {
        /// <summary>继续执行主流程后续步骤（默认）。</summary>
        Continue,
        /// <summary>执行指定子脚本后结束当前脚本。</summary>
        RunScript,
        /// <summary>执行分支子动作（TrueActions/FalseActions）后结束当前脚本。</summary>
        RunActions,
        /// <summary>直接结束当前脚本执行。</summary>
        Stop
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
        /// <summary>用户自定义名称（可自由填写，默认空；与自动生成的 Name/Summary 独立）。流程图和列表优先显示它。</summary>
        public string? DisplayName { get; set; }

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
        public float VisionConfThreshold { get; set; } = 0.7f;
        /// <summary>
        /// 视觉模式模板图片路径（绝对路径）。录制视觉点击时自动截取点击位置周围区域保存。
        /// 回放时用 OpenCV 模板匹配在该窗口截图中定位。
        /// </summary>
        public string? TemplateImage { get; set; }

        // UIA 路径标识
        public string? XPath { get; set; }          // 祖先链 XPath，如 /Window[@Name='微信']/Pane/Button[@Name='发送']
        public int SiblingIndex { get; set; }       // 同级索引（0-based），XPath 中 [n] 消歧
        public string? RuntimeId { get; set; }      // RuntimeId 逗号分隔字符串，辅助标识

        // 参数
        public string Parameter { get; set; } = "";
        public int DelayMs { get; set; } = 300;
        public int ScrollAmount { get; set; } = 3; // 滚动行数

        // ── 步骤后随机等待（所有步骤类型可用） ──
        /// <summary>步骤执行完成后是否随机等待。默认 false。</summary>
        public bool RandomWaitEnabled { get; set; }
        /// <summary>随机等待最小秒数。默认 3。范围 1~60。</summary>
        public int RandomWaitMinSec { get; set; } = 3;
        /// <summary>随机等待最大秒数。默认 16。范围 1~60。</summary>
        public int RandomWaitMaxSec { get; set; } = 16;

        // ── 步骤后鼠标随机移动（所有步骤类型可用） ──
        /// <summary>步骤执行完成后是否随机移动鼠标。默认 false。</summary>
        public bool RandomMouseMoveEnabled { get; set; }
        /// <summary>鼠标随机移动偏移像素范围（最小）。默认 30。</summary>
        public int RandomMoveMinOffset { get; set; } = 30;
        /// <summary>鼠标随机移动偏移像素范围（最大）。默认 120。</summary>
        public int RandomMoveMaxOffset { get; set; } = 120;

        // 动态输入参数
        public string? ParameterName { get; set; }
        public string? DefaultValue { get; set; }
        public bool IsRequired { get; set; } = true;
        public bool CopyToClipboard { get; set; }

        public string? RegexPattern { get; set; }
        public string? RegexGroup { get; set; }
        public string? OutputParamName { get; set; }

        // 分支/控制流
        public string? ConditionExpression { get; set; }   // 条件表达式，如 "{found} == true"、"{count} > 0"
        public string? TrueGotoNodeId { get; set; }         // If 为 true 时跳转的目标 NodeId
        public string? GotoNodeId { get; set; }             // Goto 的目标 / If 为 false 时跳转的目标 NodeId
        /// <summary>
        /// If 条件成立时执行的子步骤（树形分支模式）。与 TrueGotoNodeId 互斥，优先使用。
        /// </summary>
        public List<RecordedAction>? TrueActions { get; set; }
        /// <summary>
        /// If 条件不成立时执行的子步骤（树形分支模式）。与 GotoNodeId 互斥，优先使用。
        /// </summary>
        public List<RecordedAction>? FalseActions { get; set; }
        /// <summary>
        /// If 步骤条件成立时执行的子脚本名（执行完停止当前脚本，不执行后续步骤）。
        /// 设置后 If 的行为变为：成立->执行该脚本并停止；不成立->停止当前脚本。
        /// </summary>
        public string? TargetScript { get; set; }

        // ── 新版 If 分支行为（成立/不成立各自独立配置） ──
        // 与上方旧字段并存：若 TrueBranch/FalseBranch 为默认值 Continue，则回退到旧字段逻辑以兼容旧脚本。

        /// <summary>条件成立时的行为。Continue(默认) 时回退到旧逻辑（TargetScript/TrueActions/TrueGotoNodeId）。</summary>
        public IfBranchAction TrueBranch { get; set; } = IfBranchAction.Continue;
        /// <summary>条件成立且 TrueBranch=RunScript 时执行的子脚本名。</summary>
        public string? TrueBranchScript { get; set; }
        /// <summary>条件不成立时的行为。Continue(默认) 时回退到旧逻辑（FalseActions/GotoNodeId）。</summary>
        public IfBranchAction FalseBranch { get; set; } = IfBranchAction.Continue;
        /// <summary>条件不成立且 FalseBranch=RunScript 时执行的子脚本名。</summary>
        public string? FalseBranchScript { get; set; }

        // ── 循环/容错控制流 ──
        // While/Loop：TrueActions 用作循环体；Try：TrueActions=Try体，FalseActions=Catch体。
        /// <summary>While 循环最大迭代次数（防死循环），默认 1000。</summary>
        public int MaxLoopCount { get; set; } = 1000;
        /// <summary>Loop 固定循环次数。</summary>
        public int LoopCount { get; set; } = 3;

        // ── HTTP 等待/调用 ──
        /// <summary>HttpWait：等待唤醒用的唯一 key（外部 HTTP 请求需带此 key）。</summary>
        public string? WaitKey { get; set; }
        /// <summary>HttpWait 超时毫秒（0=无限等待）。</summary>
        public int WaitTimeoutMs { get; set; }
        /// <summary>HttpCall：请求 URL。</summary>
        public string? HttpUrl { get; set; }
        /// <summary>HttpCall：请求方法（GET/POST/PUT/DELETE）。</summary>
        public string? HttpMethod { get; set; } = "GET";
        /// <summary>HttpCall：请求头（每行 Key: Value）。</summary>
        public string? HttpHeaders { get; set; }
        /// <summary>HttpCall：请求体（支持 {变量} 占位）。</summary>
        public string? HttpBody { get; set; }
        /// <summary>HttpWait 传入参数 / HttpCall 响应内容，写入的变量名。</summary>
        public string? ResponseVarName { get; set; }

        // 切窗选项：true=打开该进程名的所有窗口（全部恢复显示并置顶），false=仅切换主窗口
        public bool SwitchAll { get; set; }

        // 元数据
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 步骤备注/说明，供用户标注步骤用途，不影响执行。
        /// </summary>
        public string? Remark { get; set; }

        public string Summary => ActionType switch
        {
            ActionType.Click => ClickMode == ClickMode.Coordinate
                ? $"坐标({X:F0},{Y:F0}){(string.IsNullOrEmpty(WindowTitle) ? "" : $" 窗口:{Trunc(WindowTitle, 15)}")}"
                : ClickMode == ClickMode.Vision
                    ? $"视觉 {VisionLabel ?? "模板"}{(string.IsNullOrEmpty(TemplateImage) ? "(无模板)" : "")} @({X:F0},{Y:F0})"
                    : !string.IsNullOrEmpty(XPath)
                        ? $"{Trunc(XPath, 50)}{(string.IsNullOrEmpty(ElementName) ? "" : $" [{ElementName}]")} @({X:F0},{Y:F0})"
                        : $"{ElementName ?? ClassName ?? AutomationId ?? "未知"}{(string.IsNullOrEmpty(ControlType) ? "" : $" <{ControlType}>")} @({X:F0},{Y:F0})",
            ActionType.TypeText => $"输入 \"{Trunc(Parameter, 30)}\"",
            ActionType.SendKeys => $"按键 {Parameter}",
            ActionType.Wait => RandomWaitEnabled
                ? $"随机等待 {RandomWaitMinSec}~{RandomWaitMaxSec}秒"
                : $"等待 {Parameter}ms",
            ActionType.Copy => "复制 (Ctrl+C)",
            ActionType.Paste => "粘贴 (Ctrl+V)",
            ActionType.InsertText => $"插入 \"{Trunc(Parameter, 30)}\"",
            ActionType.Screenshot => "截图",
            ActionType.OpenApp => $"打开 {Trunc(Parameter, 40)}",
            ActionType.WaitForApp => $"等待应用 {Trunc(Parameter, 25)} ({DelayMs}ms超时)",
            ActionType.Scroll => $"滚动 {ScrollAmount} 行",
            ActionType.ReadContent => "阅读窗口内容" + (!string.IsNullOrEmpty(OutputParamName) ? $" ->{{{OutputParamName}}}" : ""),
            ActionType.ScrollRead => $"滚动阅读 {ScrollAmount} 行" + (!string.IsNullOrEmpty(OutputParamName) ? $" ->{{{OutputParamName}}}" : ""),
            ActionType.InputParam => $"输入参数 [{ParameterName ?? "未命名"}]{(CopyToClipboard ? " →剪切板" : "")}",
            ActionType.RegexMatch => $"正则识别 {Trunc(RegexPattern ?? "", 20)}{(CopyToClipboard ? " →剪切板" : "")}{(!string.IsNullOrEmpty(OutputParamName) ? $" →{{{OutputParamName}}}" : "")}",
            ActionType.If => "判断 " + (!string.IsNullOrEmpty(ConditionExpression) ? Trunc(ConditionExpression, 20) : (!string.IsNullOrEmpty(OutputParamName) ? $"{{{OutputParamName}}} 有值" : "(未配置)"))
                + " [✓" + BranchActionDesc(TrueBranch, TrueBranchScript, true) + " ✗" + BranchActionDesc(FalseBranch, FalseBranchScript, false) + "]"
                + BranchCountSuffix(),
            ActionType.Goto => $"跳转 → {GotoNodeId}",
            ActionType.SwitchToWindow => $"切窗 {WindowTitle ?? Parameter}{(SwitchAll ? " (全部)" : "")}",
            ActionType.While => "循环当 " + (!string.IsNullOrEmpty(ConditionExpression) ? Trunc(ConditionExpression, 20) : (!string.IsNullOrEmpty(OutputParamName) ? $"{{{OutputParamName}}} 有值" : "(未配置)"))
                + $" (体:{TrueActions?.Count ?? 0}步, 上限{MaxLoopCount})",
            ActionType.Loop => $"循环 {LoopCount} 次 (体:{TrueActions?.Count ?? 0}步)",
            ActionType.Try => $"容错 (Try:{TrueActions?.Count ?? 0} Catch:{FalseActions?.Count ?? 0})",
            ActionType.Break => "跳出循环",
            ActionType.Continue => "进入下一轮",
            ActionType.HttpWait => $"等待HTTP [{WaitKey ?? "?"}]" + (WaitTimeoutMs > 0 ? $" (超时{WaitTimeoutMs}ms)" : "") + (!string.IsNullOrEmpty(ResponseVarName) ? $" ->{{{ResponseVarName}}}" : ""),
            ActionType.HttpCall => $"调用{HttpMethod} {Trunc(HttpUrl ?? "", 28)}" + (!string.IsNullOrEmpty(ResponseVarName) ? $" ->{{{ResponseVarName}}}" : ""),
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

        /// <summary>树形分支模式下的子步骤计数后缀，如 [T:2 F:1]，无子步骤返回空。</summary>
        private string BranchCountSuffix()
        {
            int t = TrueActions?.Count ?? 0;
            int f = FalseActions?.Count ?? 0;
            return (t > 0 || f > 0) ? $" (T:{t} F:{f})" : "";
        }

        /// <summary>
        /// 分支行为简短描述（用于 Summary 显示）。
        /// isTrue 分支且行为为 Continue 时，回退到旧字段（TargetScript/TrueActions/TrueGotoNodeId）以兼容旧脚本；
        /// isFalse 同理回退到 FalseActions/GotoNodeId。
        /// </summary>
        private string BranchActionDesc(IfBranchAction action, string? script, bool isTrue)
        {
            // 旧脚本兼容：行为为默认 Continue 时，根据旧字段推断实际行为
            if (action == IfBranchAction.Continue)
            {
                if (isTrue)
                {
                    if (!string.IsNullOrEmpty(TargetScript)) return $"脚本:{Trunc(TargetScript, 12)}";
                    if (TrueActions?.Count > 0) return $"动作x{TrueActions.Count}";
                    if (!string.IsNullOrEmpty(TrueGotoNodeId)) return "跳转";
                    return "继续";
                }
                else
                {
                    if (FalseActions?.Count > 0) return $"动作x{FalseActions.Count}";
                    if (!string.IsNullOrEmpty(GotoNodeId)) return "跳转";
                    return "继续";
                }
            }

            return action switch
            {
                IfBranchAction.Continue => "继续",
                IfBranchAction.RunScript => !string.IsNullOrEmpty(script) ? $"脚本:{Trunc(script, 12)}" : "脚本?",
                IfBranchAction.RunActions => $"{(isTrue ? TrueActions?.Count : FalseActions?.Count) ?? 0}动作",
                IfBranchAction.Stop => "结束",
                _ => action.ToString()
            };
        }

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
        /// [已废弃] 视觉模式不再使用全局 ONNX 模型，改用每步骤的模板图片（TemplateImage）。
        /// 字段保留仅为读取旧脚本不报错，不再有任何作用。
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

        /// <summary>
        /// 输出参数名（ReadContent/ScrollRead/RegexMatch 步骤设置的 OutputParamName）。
        /// 有值时，该阅读内容可通过变量名供后续步骤引用，也用于向服务器报告结构化结果。
        /// </summary>
        public string? OutputParamName { get; set; }
    }

    public class RegexMatchItem
    {
        public string Value { get; set; } = "";
        public int Index { get; set; }
        public Dictionary<string, string> Groups { get; set; } = new();
    }

    /// <summary>
    /// 视觉检测结果（YOLO 检测到的 UI 元素）
    /// </summary>
    public class VisionDetectionResult
    {
        public string WindowTitle { get; set; } = "";
        public string VisionLabel { get; set; } = "";
        public List<DetectionInfo> Detections { get; set; } = new();
        public DateTime CapturedAt { get; set; }
        public string Source { get; set; } = "VisionClick";
    }

    /// <summary>
    /// 单个视觉检测项
    /// </summary>
    public class DetectionInfo
    {
        public string Label { get; set; } = "";
        public float Confidence { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
