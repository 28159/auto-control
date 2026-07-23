using System;
using System.Collections.Generic;
using System.Windows;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.App
{
    /// <summary>
    /// 流程图自动布局引擎。
    /// 采用树形布局：主流程自上而下纵向排列，If 节点的 True/False 分支向左右展开成子树。
    /// 支持无限纵向滚动，鼠标滚轮上下滑动浏览。
    /// </summary>
    public static class FlowLayoutEngine
    {
        /// <summary>
        /// 计算每个步骤节点的 Canvas 坐标（树形布局）
        /// </summary>
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
                double col = 0;

                if (action.ActionType == ActionType.If)
                {
                    // True 分支目标偏左，False 分支目标偏右
                    if (!string.IsNullOrEmpty(action.TrueGotoNodeId) && nodeIdToIndex.TryGetValue(action.TrueGotoNodeId, out int trueIdx) && trueIdx > i)
                        columnOffsets[trueIdx] = -1; // 左
                    if (!string.IsNullOrEmpty(action.GotoNodeId) && nodeIdToIndex.TryGetValue(action.GotoNodeId, out int falseIdx) && falseIdx > i)
                        columnOffsets[falseIdx] = 1;  // 右
                }
                else if (action.ActionType == ActionType.Goto)
                {
                    // Goto 回跳目标不做列偏移（回边用虚线表示）
                }

                _ = col; // 主流程节点列固定为 0（居中）
            }

            // 计算每个节点的实际 X：基准居中 + 累积列偏移
            // 简化策略：分支目标按其 columnOffsets 值水平偏移一个 stepX 单位
            double stepX = nodeWidth + horizontalGap;
            double centerX = 0; // 基准 X，渲染时居中由 Canvas 宽度决定，这里先用 0，后面统一平移

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
    }
}
