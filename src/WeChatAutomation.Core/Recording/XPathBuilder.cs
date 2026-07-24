using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Recording
{
    /// <summary>
    /// 从 FlaUI AutomationElement 构建祖先链 XPath，用于录制时精确定位元素。
    /// </summary>
    public static class XPathBuilder
    {
        private static readonly Logger _logger = Logger.Instance;
        private const int MaxDepth = 12;

        /// <summary>
        /// 从目标元素向上遍历到窗口根节点，构建 XPath 路径。
        /// 示例: /Window[@Name='微信']/Pane[@ClassName='WeChatMainWnd']/Button[@Name='发送'][2]
        /// </summary>
        public static string BuildXPath(AutomationElement element)
        {
            if (element == null) return "";

            try
            {
                var chain = new List<string>();
                var current = element;
                int depth = 0;

                while (current != null && depth <= MaxDepth)
                {
                    // 叶子节点（链的第一个，即用户实际点击的元素）保留同级索引消歧；
                    // 中间容器不加索引（其同类型兄弟顺序跨账号/跨次启动最不稳定）
                    bool isLeaf = chain.Count == 0;
                    string step = BuildElementStep(current, isLeaf);
                    if (string.IsNullOrEmpty(step)) break;

                    chain.Add(step);

                    // 到达 Window 类型则停止
                    if (current.ControlType == ControlType.Window)
                        break;

                    current = current.Parent;
                    depth++;
                }

                // 反转：从根到目标
                chain.Reverse();

                if (chain.Count == 0) return "";

                // 深度超过限制时用 // 前缀
                string prefix = depth > MaxDepth ? "//" : "/";
                return prefix + string.Join("/", chain);
            }
            catch (Exception ex)
            {
                _logger.Warn("XPathBuilder", $"构建 XPath 失败: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 计算同级索引：在同级中，与目标元素具有相同 ControlType 且相同 Name 的元素里，
        /// 目标元素是第几个（0-based）。基于 ControlType+Name 计数比仅 ControlType 更稳定，
        /// 能在同名按钮间准确消歧，不受无关同类型元素顺序变化影响。
        /// </summary>
        public static int ComputeSiblingIndex(AutomationElement element)
        {
            if (element == null) return 0;

            try
            {
                var parent = element.Parent;
                if (parent == null) return 0;

                string targetName = "";
                try { targetName = element.Name ?? ""; } catch { }

                var siblings = parent.FindAllChildren();
                int index = 0;
                foreach (var child in siblings)
                {
                    if (child.Equals(element)) return index;
                    // 仅统计同 ControlType 且同 Name 的兄弟，使索引在同名元素间稳定
                    if (child.ControlType == element.ControlType)
                    {
                        string childName = "";
                        try { childName = child.Name ?? ""; } catch { }
                        if (string.Equals(childName, targetName, StringComparison.Ordinal))
                            index++;
                    }
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 提取 RuntimeId 作为逗号分隔字符串。
        /// </summary>
        public static string ExtractRuntimeId(AutomationElement element)
        {
            if (element == null) return "";

            try
            {
                var runtimeId = element.Properties.RuntimeId.Value;
                return runtimeId != null ? string.Join(",", runtimeId) : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 构建单个元素的 XPath 步骤，如 Button[@AutomationId='sendBtn' and @Name='发送'][2]
        /// 尽可能组合多个属性，提高定位精度。
        /// 仅叶子节点（isLeaf=true）附加同级索引用于消歧；中间容器不加索引。
        /// </summary>
        private static string BuildElementStep(AutomationElement element, bool isLeaf)
        {
            try
            {
                string controlTypeName = element.ControlType.ToString();
                var predicates = new List<string>();

                // 收集所有可用属性，组合使用以提高精确度
                string automationId = "";
                try { automationId = element.AutomationId ?? ""; } catch { }

                string name = "";
                try { name = element.Name ?? ""; } catch { }

                string className = "";
                try { className = element.ClassName ?? ""; } catch { }

                // AutomationId 最精确，优先级最高
                if (!string.IsNullOrEmpty(automationId))
                {
                    predicates.Add($"@AutomationId='{EscapeXPath(automationId)}'");
                }

                // Name 也很精确，配合使用
                if (!string.IsNullOrEmpty(name))
                {
                    predicates.Add($"@Name='{EscapeXPath(name)}'");
                }

                // ClassName 作为补充，在 Name 不够唯一时提供额外信息
                if (!string.IsNullOrEmpty(className) && predicates.Count < 2)
                {
                    predicates.Add($"@ClassName='{EscapeXPath(className)}'");
                }

                string predicateStr = predicates.Count > 0 ? $"[{string.Join(" and ", predicates)}]" : "";

                // 仅叶子节点保留同级索引消歧；中间容器不加索引（顺序不稳定）
                string indexStr = "";
                if (isLeaf)
                {
                    int siblingIdx = ComputeSiblingIndex(element);
                    indexStr = siblingIdx > 0 ? $"[{siblingIdx + 1}]" : "";
                }

                return $"{controlTypeName}{predicateStr}{indexStr}";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 转义 XPath 字符串中的特殊字符。
        /// </summary>
        private static string EscapeXPath(string value)
        {
            if (value.Contains('\''))
            {
                // 包含单引号时用 concat 函数
                return $"concat('{value.Replace("'", "',\"'\",'")}'')";
            }
            return value;
        }
    }
}
