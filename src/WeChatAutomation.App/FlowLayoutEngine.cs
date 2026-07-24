using System;
using System.Collections.Generic;
using System.Windows;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.App
{
    /// <summary>
    /// 树形布局结果：包含所有节点位置、分支头位置、父子关系映射和内容尺寸。
    /// </summary>
    public class LayoutResult
    {
        /// <summary>所有步骤节点（顶层 + 嵌套）的 Canvas 坐标，键为 NodeId。</summary>
        public Dictionary<string, Point> NodePositions { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True/False 分支头标签的 Canvas 坐标，键为 "ifNodeId_True" / "ifNodeId_False"。</summary>
        public Dictionary<string, Point> BranchHeaderPositions { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>嵌套节点的 NodeId → 其所属父 If 节点的 NodeId。</summary>
        public Dictionary<string, string> NodeParentIfId { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>嵌套节点的 NodeId → 是否在 True 分支（false = False 分支）。</summary>
        public Dictionary<string, bool> NodeBranchIsTrue { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>分支头标签键 → 其所属 If 节点的 NodeId。</summary>
        public Dictionary<string, string> BranchHeaderParentIfId { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>分支头标签键 → 是否为 True 分支。</summary>
        public Dictionary<string, bool> BranchHeaderIsTrue { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>布局内容总宽度。</summary>
        public double TotalWidth { get; set; }

        /// <summary>布局内容总高度。</summary>
        public double TotalHeight { get; set; }
    }

    /// <summary>
    /// 流程图自动布局引擎。
    /// 采用树形布局：主流程自上而下纵向排列，If 节点的 True/False 分支向左右展开成子树。
    /// 支持无限纵向滚动，鼠标滚轮上下滑动浏览。
    /// </summary>
    public static class FlowLayoutEngine
    {
        /// <summary>
        /// 计算每个步骤节点的 Canvas 坐标（树形布局）—— 旧版扁平布局，仅处理 TrueGotoNodeId/GotoNodeId 跳转。
        /// </summary>
        [Obsolete("请使用 LayoutTree 替代，支持 TrueActions/FalseActions 树形分支布局")]
        public static Dictionary<string, Point> Layout(
            List<RecordedAction> actions,
            double nodeWidth,
            double nodeHeight,
            double verticalGap,
            double horizontalGap,
            int nodesPerRow = 0)
        {
            var positions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            if (actions == null || actions.Count == 0) return positions;

            // NodeId -> action 索引
            var nodeIdToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < actions.Count; i++)
            {
                if (!string.IsNullOrEmpty(actions[i].NodeId))
                    nodeIdToIndex[actions[i].NodeId] = i;
            }

            double stepY = nodeHeight + verticalGap;
            double baseY = 20;

            // 树形布局：按顺序纵向排列，If 节点的分支目标向左右偏移
            // 先确定每个节点的"列"（X 偏移），再按序号分配 Y
            var columnOffsets = new double[actions.Count];
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];

                if (action.ActionType == ActionType.If)
                {
                    // True 分支目标偏左，False 分支目标偏右
                    if (!string.IsNullOrEmpty(action.TrueGotoNodeId) && nodeIdToIndex.TryGetValue(action.TrueGotoNodeId, out int trueIdx) && trueIdx > i)
                        columnOffsets[trueIdx] = -1; // 左
                    if (!string.IsNullOrEmpty(action.GotoNodeId) && nodeIdToIndex.TryGetValue(action.GotoNodeId, out int falseIdx) && falseIdx > i)
                        columnOffsets[falseIdx] = 1;  // 右
                }
            }

            // 计算每个节点的实际 X：基准居中 + 累积列偏移
            double stepX = nodeWidth + horizontalGap;
            double centerX = 0;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                double x = centerX + columnOffsets[i] * stepX;
                double y = baseY + i * stepY;

                if (!string.IsNullOrEmpty(action.NodeId))
                    positions[action.NodeId] = new Point(x, y);
            }

            return positions;
        }

        /// <summary>
        /// 树形布局：支持 If 节点的 TrueActions/FalseActions 子树展开。
        /// If 节点居中，True 分支向左展开，False 分支向右展开，子树递归布局。
        /// </summary>
        /// <param name="actions">顶层步骤列表</param>
        /// <param name="nodeWidth">节点宽度</param>
        /// <param name="nodeHeight">节点高度</param>
        /// <param name="verticalGap">纵向间距</param>
        /// <param name="horizontalGap">横向间距</param>
        /// <param name="collapsedIfNodeIds">折叠的 If 节点 NodeId 集合（不展开子树）</param>
        /// <returns>包含所有节点位置和分支头位置的布局结果</returns>
        public static LayoutResult LayoutTree(
            List<RecordedAction> actions,
            double nodeWidth,
            double nodeHeight,
            double verticalGap,
            double horizontalGap,
            HashSet<string>? collapsedIfNodeIds = null)
        {
            var result = new LayoutResult();
            if (actions == null || actions.Count == 0) return result;

            double baseY = 20;
            double centerX = 0; // 基准 X，渲染时居中由 Canvas 宽度决定

            // 递归布局顶层步骤
            double finalY = LayoutSubTree(
                actions, centerX, baseY,
                nodeWidth, nodeHeight, verticalGap, horizontalGap,
                depth: 0, isTrueBranch: false,
                parentIfNodeId: null,
                collapsedIfNodeIds, result);

            // 计算内容尺寸
            CalculateContentSize(result, nodeWidth, nodeHeight, 20);

            return result;
        }

        /// <summary>
        /// 递归布局子树。返回此子树底部 Y 坐标（最后一个节点底部 + padding）。
        /// </summary>
        private static double LayoutSubTree(
            List<RecordedAction> actions,
            double startX,
            double startY,
            double nodeWidth,
            double nodeHeight,
            double verticalGap,
            double horizontalGap,
            int depth,
            bool isTrueBranch,
            string? parentIfNodeId,
            HashSet<string>? collapsedIfNodeIds,
            LayoutResult result)
        {
            if (actions == null || actions.Count == 0) return startY;

            double currentY = startY;
            double stepY = nodeHeight + verticalGap;
            // 每层嵌套增加横向偏移
            double branchOffset = (nodeWidth + horizontalGap) * (depth + 1);

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (string.IsNullOrEmpty(action.NodeId)) continue;

                // 记录父子关系
                if (parentIfNodeId != null)
                {
                    result.NodeParentIfId[action.NodeId] = parentIfNodeId;
                    result.NodeBranchIsTrue[action.NodeId] = isTrueBranch;
                }

                // If/While/Loop/Try 都可携带 TrueActions/FalseActions 子树，统一按分支布局
                bool isContainerNode = action.ActionType == ActionType.If
                    || action.ActionType == ActionType.While
                    || action.ActionType == ActionType.Loop
                    || action.ActionType == ActionType.Try;
                bool hasTreeBranches = isContainerNode
                    && (action.TrueActions?.Count > 0 || action.FalseActions?.Count > 0);
                bool isCollapsed = collapsedIfNodeIds != null
                    && collapsedIfNodeIds.Contains(action.NodeId);

                if (hasTreeBranches && !isCollapsed)
                {
                    // === 容器节点（If/While/Loop/Try）有子树 ===
                    // While/Loop 只有单个循环体（TrueActions），不放 False 分支头
                    bool singleBody = action.ActionType == ActionType.While || action.ActionType == ActionType.Loop;

                    // 1. 放置容器节点本身
                    result.NodePositions[action.NodeId] = new Point(startX, currentY);
                    currentY += stepY;

                    // 2. 放置 True/False 分支头标签
                    double trueHeaderX = singleBody ? startX : startX - branchOffset;
                    double falseHeaderX = startX + branchOffset;
                    double headerY = currentY;

                    // True 分支头（循环体 / Try 体 / If True）
                    string trueHeaderKey = $"{action.NodeId}_True";
                    result.BranchHeaderPositions[trueHeaderKey] = new Point(trueHeaderX, headerY);
                    result.BranchHeaderParentIfId[trueHeaderKey] = action.NodeId;
                    result.BranchHeaderIsTrue[trueHeaderKey] = true;

                    // False 分支头（If False / Try Catch）；While/Loop 无
                    if (!singleBody)
                    {
                        string falseHeaderKey = $"{action.NodeId}_False";
                        result.BranchHeaderPositions[falseHeaderKey] = new Point(falseHeaderX, headerY);
                        result.BranchHeaderParentIfId[falseHeaderKey] = action.NodeId;
                        result.BranchHeaderIsTrue[falseHeaderKey] = false;
                    }

                    currentY += stepY; // 分支头占一行

                    // 3. 递归布局 True 子树
                    double trueBottom = currentY;
                    if (action.TrueActions?.Count > 0)
                    {
                        trueBottom = LayoutSubTree(
                            action.TrueActions, trueHeaderX, currentY,
                            nodeWidth, nodeHeight, verticalGap, horizontalGap,
                            depth + 1, isTrueBranch: true,
                            parentIfNodeId: action.NodeId,
                            collapsedIfNodeIds, result);
                    }

                    // 4. 递归布局 False 子树（While/Loop 无）
                    double falseBottom = currentY;
                    if (!singleBody && action.FalseActions?.Count > 0)
                    {
                        falseBottom = LayoutSubTree(
                            action.FalseActions, falseHeaderX, currentY,
                            nodeWidth, nodeHeight, verticalGap, horizontalGap,
                            depth + 1, isTrueBranch: false,
                            parentIfNodeId: action.NodeId,
                            collapsedIfNodeIds, result);
                    }

                    // 5. 下一个顶层步骤的 Y = 两个子树中较深的那个底部 + 间距
                    currentY = Math.Max(trueBottom, falseBottom) + verticalGap;
                }
                else if (action.ActionType == ActionType.If
                    && !string.IsNullOrEmpty(action.TrueGotoNodeId)
                    && !hasTreeBranches)
                {
                    // === If 节点使用旧模式 TrueGotoNodeId/GotoNodeId 跳转 ===
                    // 跳转目标在扁平列表中，不在此处递归，仅放置 If 节点本身
                    result.NodePositions[action.NodeId] = new Point(startX, currentY);
                    currentY += stepY;
                }
                else
                {
                    // === 普通步骤 ===
                    result.NodePositions[action.NodeId] = new Point(startX, currentY);
                    currentY += stepY;
                }
            }

            return currentY;
        }

        /// <summary>
        /// 计算布局后的内容总尺寸（用于设置 Canvas 大小）
        /// </summary>
        public static Size MeasureContent(
            Dictionary<string, Point> positions,
            double nodeWidth,
            double nodeHeight,
            double padding = 20)
        {
            if (positions == null || positions.Count == 0)
                return new Size(0, 0);

            double minX = double.MaxValue, maxX = double.MinValue;
            double maxBottom = 0;
            foreach (var kvp in positions)
            {
                if (kvp.Value.X < minX) minX = kvp.Value.X;
                if (kvp.Value.X + nodeWidth > maxX) maxX = kvp.Value.X + nodeWidth;
                double bottom = kvp.Value.Y + nodeHeight + padding;
                if (bottom > maxBottom) maxBottom = bottom;
            }
            double width = (maxX - minX) + padding * 2;
            return new Size(width, maxBottom);
        }

        /// <summary>
        /// 从 LayoutResult 计算内容总尺寸（包含节点和分支头标签）。
        /// </summary>
        private static void CalculateContentSize(LayoutResult result, double nodeWidth, double nodeHeight, double padding)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double maxBottom = 0;

            foreach (var kvp in result.NodePositions)
            {
                UpdateBounds(kvp.Value, nodeWidth, nodeHeight, padding, ref minX, ref maxX, ref maxBottom);
            }
            foreach (var kvp in result.BranchHeaderPositions)
            {
                // 分支头标签宽度较窄（约 60px）
                UpdateBounds(kvp.Value, 60, nodeHeight, padding, ref minX, ref maxX, ref maxBottom);
            }

            if (minX == double.MaxValue)
            {
                result.TotalWidth = 0;
                result.TotalHeight = 0;
            }
            else
            {
                result.TotalWidth = (maxX - minX) + padding * 2;
                result.TotalHeight = maxBottom;
            }
        }

        private static void UpdateBounds(Point pos, double width, double height, double padding,
            ref double minX, ref double maxX, ref double maxBottom)
        {
            if (pos.X < minX) minX = pos.X;
            if (pos.X + width > maxX) maxX = pos.X + width;
            double bottom = pos.Y + height + padding;
            if (bottom > maxBottom) maxBottom = bottom;
        }
    }
}
