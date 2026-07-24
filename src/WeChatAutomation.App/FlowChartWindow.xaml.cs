using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.App
{
    /// <summary>
    /// FlowChartWindow - visual script workflow editor with tree layout.
    /// If nodes expand True/False sub-branches below them.
    /// </summary>
    public partial class FlowChartWindow : Window
    {
        private readonly ActionRecorder _recorder;
        private readonly ActionPlayer _player;
        private WeChatAutomation.Core.Recording.ClickMode _currentClickMode;
        private string _currentVisionModel;
        private List<RecordedAction> _actions;
        private string? _selectedNodeId;
        private double _zoom = 1.0;

        // 折叠的 If 节点 NodeId 集合
        private readonly HashSet<string> _collapsedIfNodeIds = new(StringComparer.OrdinalIgnoreCase);

        // 当前布局结果（缓存，供交互使用）
        private LayoutResult? _currentLayout;

        // Compact node dimensions
        private const double NodeWidth = 150;
        private const double NodeHeight = 30;
        private const double VerticalGap = 30;
        private const double HorizontalGap = 40;
        private const double BranchHeaderWidth = 60;

        // Color mapping per action type
        private static readonly Dictionary<ActionType, Brush> TypeColors = new()
        {
            { ActionType.Click, new SolidColorBrush(Color.FromRgb(33, 150, 243)) },
            { ActionType.TypeText, new SolidColorBrush(Color.FromRgb(76, 175, 80)) },
            { ActionType.SendKeys, new SolidColorBrush(Color.FromRgb(76, 175, 80)) },
            { ActionType.Wait, new SolidColorBrush(Color.FromRgb(255, 152, 0)) },
            { ActionType.Copy, new SolidColorBrush(Color.FromRgb(158, 158, 158)) },
            { ActionType.Paste, new SolidColorBrush(Color.FromRgb(158, 158, 158)) },
            { ActionType.InsertText, new SolidColorBrush(Color.FromRgb(76, 175, 80)) },
            { ActionType.Screenshot, new SolidColorBrush(Color.FromRgb(156, 39, 176)) },
            { ActionType.OpenApp, new SolidColorBrush(Color.FromRgb(0, 150, 136)) },
            { ActionType.WaitForApp, new SolidColorBrush(Color.FromRgb(255, 152, 0)) },
            { ActionType.Scroll, new SolidColorBrush(Color.FromRgb(158, 158, 158)) },
            { ActionType.ReadContent, new SolidColorBrush(Color.FromRgb(63, 81, 181)) },
            { ActionType.ScrollRead, new SolidColorBrush(Color.FromRgb(63, 81, 181)) },
            { ActionType.InputParam, new SolidColorBrush(Color.FromRgb(121, 85, 72)) },
            { ActionType.RegexMatch, new SolidColorBrush(Color.FromRgb(233, 30, 99)) },
            { ActionType.If, new SolidColorBrush(Color.FromRgb(255, 193, 7)) },
            { ActionType.Goto, new SolidColorBrush(Color.FromRgb(0, 188, 212)) },
            { ActionType.SwitchToWindow, new SolidColorBrush(Color.FromRgb(103, 58, 183)) },
            { ActionType.While, new SolidColorBrush(Color.FromRgb(255, 87, 34)) },
            { ActionType.Loop, new SolidColorBrush(Color.FromRgb(255, 111, 0)) },
            { ActionType.Try, new SolidColorBrush(Color.FromRgb(121, 85, 72)) },
            { ActionType.Break, new SolidColorBrush(Color.FromRgb(120, 144, 156)) },
            { ActionType.Continue, new SolidColorBrush(Color.FromRgb(120, 144, 156)) },
            { ActionType.HttpWait, new SolidColorBrush(Color.FromRgb(0, 137, 123)) },
            { ActionType.HttpCall, new SolidColorBrush(Color.FromRgb(2, 119, 189)) },
        };

        private static readonly Brush TrueBranchColor = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        private static readonly Brush FalseBranchColor = new SolidColorBrush(Color.FromRgb(244, 67, 54));

        public FlowChartWindow(ActionRecorder recorder, ActionPlayer player, WeChatAutomation.Core.Recording.ClickMode clickMode, string visionModel)
        {
            InitializeComponent();
            _recorder = recorder;
            _player = player;
            _currentClickMode = clickMode;
            _currentVisionModel = visionModel;
            _actions = new List<RecordedAction>(recorder.Nodes);
            _recorder.NodeRecorded += OnNodeRecorded;
            _recorder.RecordingStopped += OnRecordingStopped;
            Loaded += (_, _) => RenderFlowChart();
            SizeChanged += (_, _) => RenderFlowChart();
            Closed += (_, _) => { _recorder.NodeRecorded -= OnNodeRecorded; _recorder.RecordingStopped -= OnRecordingStopped; if (_recorder.IsRecording) _recorder.Stop(); };
        }

        // === Recording ===

        private void OnNodeRecorded(object? sender, RecordedAction node)
        {
            _actions = new List<RecordedAction>(_recorder.Nodes);
            Dispatcher.Invoke(() => RenderFlowChart());
        }

        private void OnRecordingStopped(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _actions = new List<RecordedAction>(_recorder.Nodes);
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                UpdateRecordBtn();
                RenderFlowChart();
                StatusText.Text = $"{_actions.Count} steps | 录制停止 | zoom {_zoom:P0}";
            });
        }

        private void RecordBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_recorder.IsRecording)
                {
                    _recorder.Stop();
                    UpdateRecordBtn();
                    StatusText.Text = $"{_actions.Count} steps | 录制停止 | zoom {_zoom:P0}";
                }
                else
                {
                    _recorder.CurrentClickMode = _currentClickMode;
                    _recorder.Start(RecordMode.Continuous);
                    UpdateRecordBtn();
                    WindowState = WindowState.Minimized;
                    StatusText.Text = "录制中... (F9 停止)";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"录制切换失败: {ex.Message}");
            }
        }

        private void UpdateRecordBtn()
        {
            if (RecordBtn == null) return;
            if (_recorder.IsRecording)
            {
                RecordBtn.Content = "⏹ 停止";
                RecordBtn.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            }
            else
            {
                RecordBtn.Content = "⏺ 录制";
                RecordBtn.Background = new SolidColorBrush(Color.FromRgb(229, 57, 53));
            }
        }

        // === Main render ===

        private void RenderFlowChart()
        {
            var canvas = FlowCanvas;
            if (canvas == null) return;
            canvas.Children.Clear();

            if (_actions.Count == 0)
            {
                canvas.Width = 0;
                canvas.Height = 0;
                _currentLayout = null;
                StatusText.Text = "0 steps";
                return;
            }

            // 树形布局
            var layout = FlowLayoutEngine.LayoutTree(_actions, NodeWidth, NodeHeight, VerticalGap, HorizontalGap, _collapsedIfNodeIds);
            _currentLayout = layout;

            // 计算画布尺寸
            double contentWidth = layout.TotalWidth > 0 ? layout.TotalWidth : 200;
            double contentHeight = layout.TotalHeight > 0 ? layout.TotalHeight : 200;

            // 计算水平居中偏移
            double availableWidth = Math.Max(ActualWidth - 220 - 40, 200);
            double contentMinX = double.MaxValue, contentMaxX = double.MinValue;
            foreach (var kvp in layout.NodePositions)
            {
                if (kvp.Value.X < contentMinX) contentMinX = kvp.Value.X;
                if (kvp.Value.X > contentMaxX) contentMaxX = kvp.Value.X;
            }
            foreach (var kvp in layout.BranchHeaderPositions)
            {
                if (kvp.Value.X < contentMinX) contentMinX = kvp.Value.X;
                if (kvp.Value.X > contentMaxX) contentMaxX = kvp.Value.X;
            }
            double contentCenterX = (contentMinX + contentMaxX) / 2 * _zoom + NodeWidth / 2 * _zoom;
            double centerOffset = availableWidth / 2 - contentCenterX;

            canvas.Width = Math.Max(contentWidth * _zoom, availableWidth);
            canvas.Height = Math.Max(contentHeight * _zoom, 100);

            // 渲染连接线
            RenderConnections(canvas, layout, centerOffset);

            // 渲染分支头标签
            foreach (var kvp in layout.BranchHeaderPositions)
            {
                string key = kvp.Key;
                var pos = kvp.Value;
                bool isTrue = layout.BranchHeaderIsTrue[key];
                var headerControl = CreateBranchHeader(key, isTrue, pos, centerOffset);
                canvas.Children.Add(headerControl);
            }

            // 渲染所有节点（顶层 + 嵌套）
            var allNodes = FlattenAllNodes(_actions);
            foreach (var action in allNodes)
            {
                if (!layout.NodePositions.TryGetValue(action.NodeId, out var pos)) continue;
                bool isInTrueBranch = layout.NodeBranchIsTrue.TryGetValue(action.NodeId, out var isTB) && isTB;
                bool isInFalseBranch = layout.NodeBranchIsTrue.TryGetValue(action.NodeId, out var isFB) && !isFB;
                var nodeControl = CreateNodeControl(action, pos, centerOffset, isInTrueBranch, isInFalseBranch);
                canvas.Children.Add(nodeControl);
            }

            // 统计总步骤数（含嵌套）
            int totalSteps = allNodes.Count;
            StatusText.Text = $"{totalSteps} steps | zoom {_zoom:P0}";
        }

        // === Connection lines ===

        /// <summary>是否为可带 True/False 子体的容器节点（If/While/Loop/Try）。</summary>
        private static bool IsContainerNode(ActionType t) =>
            t == ActionType.If || t == ActionType.While || t == ActionType.Loop || t == ActionType.Try;

        private void RenderConnections(Canvas canvas, LayoutResult layout, double centerOffset)
        {
            // 1. 顶层步骤间的顺序连接
            RenderSequentialConnections(canvas, _actions, layout, centerOffset);

            // 2. If 节点的分支连接（树形模式 + 旧跳转模式）
            RenderIfBranchConnections(canvas, _actions, layout, centerOffset);

            // 3. Goto 跳转连接
            RenderGotoConnections(canvas, _actions, layout, centerOffset);
        }

        /// <summary>
        /// 渲染同一列表中相邻步骤的顺序连接线。
        /// </summary>
        private void RenderSequentialConnections(Canvas canvas, List<RecordedAction> actions, LayoutResult layout, double centerOffset)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (!layout.NodePositions.TryGetValue(action.NodeId, out var fromPos)) continue;

                // 容器节点（If/While/Loop/Try）有树形分支时，不画到下一步的顺序线（分支线代替）
                bool hasTreeBranches = IsContainerNode(action.ActionType)
                    && (action.TrueActions?.Count > 0 || action.FalseActions?.Count > 0)
                    && !_collapsedIfNodeIds.Contains(action.NodeId);

                if (hasTreeBranches) continue;

                // 旧模式 If 有跳转目标时，也不画顺序线
                bool hasJumpBranches = action.ActionType == ActionType.If
                    && (!string.IsNullOrEmpty(action.TrueGotoNodeId) || !string.IsNullOrEmpty(action.GotoNodeId));

                if (hasJumpBranches && !hasTreeBranches)
                {
                    // 旧模式：If 无跳转目标时画顺序线
                    if (string.IsNullOrEmpty(action.TrueGotoNodeId) && string.IsNullOrEmpty(action.GotoNodeId) && i < actions.Count - 1)
                    {
                        if (layout.NodePositions.TryGetValue(actions[i + 1].NodeId, out var nextPos))
                            DrawConnection(canvas, fromPos, nextPos, Brushes.Gray, false, null, centerOffset);
                    }
                    continue;
                }

                // Goto 节点不画顺序线
                if (action.ActionType == ActionType.Goto && !string.IsNullOrEmpty(action.GotoNodeId)) continue;

                // 画到下一步的顺序线
                if (i < actions.Count - 1)
                {
                    if (layout.NodePositions.TryGetValue(actions[i + 1].NodeId, out var nextPos))
                        DrawConnection(canvas, fromPos, nextPos, Brushes.Gray, false, null, centerOffset);
                }
            }
        }

        /// <summary>
        /// 渲染 If 节点的分支连接线（树形模式 + 旧跳转模式）。
        /// </summary>
        private void RenderIfBranchConnections(Canvas canvas, List<RecordedAction> actions, LayoutResult layout, double centerOffset)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (!IsContainerNode(action.ActionType)) continue;
                if (!layout.NodePositions.TryGetValue(action.NodeId, out var ifPos)) continue;

                bool hasTreeBranches = (action.TrueActions?.Count > 0 || action.FalseActions?.Count > 0)
                    && !_collapsedIfNodeIds.Contains(action.NodeId);

                if (hasTreeBranches)
                {
                    // === 树形分支模式 ===

                    // If → True 分支头
                    string trueHeaderKey = $"{action.NodeId}_True";
                    if (layout.BranchHeaderPositions.TryGetValue(trueHeaderKey, out var trueHeaderPos))
                    {
                        DrawConnection(canvas, ifPos, trueHeaderPos, TrueBranchColor, false, "T", centerOffset);
                    }

                    // If → False 分支头
                    string falseHeaderKey = $"{action.NodeId}_False";
                    if (layout.BranchHeaderPositions.TryGetValue(falseHeaderKey, out var falseHeaderPos))
                    {
                        DrawConnection(canvas, ifPos, falseHeaderPos, FalseBranchColor, false, "F", centerOffset);
                    }

                    // True 分支头 → 第一个 True 子步骤
                    if (action.TrueActions?.Count > 0)
                    {
                        RenderBranchInternalConnections(canvas, action.TrueActions, layout, centerOffset, trueHeaderKey, true);
                    }

                    // False 分支头 → 第一个 False 子步骤
                    if (action.FalseActions?.Count > 0)
                    {
                        RenderBranchInternalConnections(canvas, action.FalseActions, layout, centerOffset, falseHeaderKey, false);
                    }

                    // 分支末尾 → If 之后的顶层步骤（回到主线）
                    // 找到 If 之后的顶层步骤
                    if (i < actions.Count - 1 && layout.NodePositions.TryGetValue(actions[i + 1].NodeId, out var afterIfPos))
                    {
                        // True 分支末尾 → 下一步
                        if (action.TrueActions?.Count > 0)
                        {
                            var lastTrue = action.TrueActions[^1];
                            if (layout.NodePositions.TryGetValue(lastTrue.NodeId, out var lastTruePos))
                                DrawConnection(canvas, lastTruePos, afterIfPos, Brushes.Gray, false, null, centerOffset, isReturnLine: true);
                        }
                        // False 分支末尾 → 下一步
                        if (action.FalseActions?.Count > 0)
                        {
                            var lastFalse = action.FalseActions[^1];
                            if (layout.NodePositions.TryGetValue(lastFalse.NodeId, out var lastFalsePos))
                                DrawConnection(canvas, lastFalsePos, afterIfPos, Brushes.Gray, false, null, centerOffset, isReturnLine: true);
                        }
                        // 如果两个分支都为空，If 直接连到下一步
                        if ((action.TrueActions == null || action.TrueActions.Count == 0)
                            && (action.FalseActions == null || action.FalseActions.Count == 0))
                        {
                            DrawConnection(canvas, ifPos, afterIfPos, Brushes.Gray, false, null, centerOffset);
                        }
                    }

                    // While/Loop：循环体末尾 → 循环节点自身（虚线回连，表达重复）
                    if ((action.ActionType == ActionType.While || action.ActionType == ActionType.Loop)
                        && action.TrueActions?.Count > 0)
                    {
                        var lastBody = action.TrueActions[^1];
                        if (layout.NodePositions.TryGetValue(lastBody.NodeId, out var lastBodyPos))
                            DrawConnection(canvas, lastBodyPos, ifPos, new SolidColorBrush(Color.FromRgb(255, 152, 0)), false, "↺", centerOffset, isReturnLine: true);
                    }
                }
                else if (action.ActionType == ActionType.If)
                {
                    // === If 旧跳转模式 ===
                    if (!string.IsNullOrEmpty(action.TrueGotoNodeId) && layout.NodePositions.TryGetValue(action.TrueGotoNodeId, out var truePos))
                        DrawConnection(canvas, ifPos, truePos, TrueBranchColor, false, "T", centerOffset);
                    if (!string.IsNullOrEmpty(action.GotoNodeId) && layout.NodePositions.TryGetValue(action.GotoNodeId, out var falsePos))
                        DrawConnection(canvas, ifPos, falsePos, FalseBranchColor, false, "F", centerOffset);
                }
            }
        }

        /// <summary>
        /// 渲染分支内部的连接线：分支头→第一个子步骤，子步骤间顺序连接，递归处理嵌套 If。
        /// </summary>
        private void RenderBranchInternalConnections(Canvas canvas, List<RecordedAction> branchActions, LayoutResult layout, double centerOffset, string parentHeaderKey, bool isTrueBranch)
        {
            if (branchActions == null || branchActions.Count == 0) return;

            // 分支头 → 第一个子步骤
            if (layout.BranchHeaderPositions.TryGetValue(parentHeaderKey, out var headerPos))
            {
                if (layout.NodePositions.TryGetValue(branchActions[0].NodeId, out var firstPos))
                    DrawConnection(canvas, headerPos, firstPos, isTrueBranch ? TrueBranchColor : FalseBranchColor, false, null, centerOffset);
            }

            // 子步骤间顺序连接
            for (int i = 0; i < branchActions.Count; i++)
            {
                var action = branchActions[i];
                if (!layout.NodePositions.TryGetValue(action.NodeId, out var fromPos)) continue;

                bool hasTreeBranches = IsContainerNode(action.ActionType)
                    && (action.TrueActions?.Count > 0 || action.FalseActions?.Count > 0)
                    && !_collapsedIfNodeIds.Contains(action.NodeId);

                if (hasTreeBranches)
                {
                    // 嵌套容器（If/While/Loop/Try）的分支连接
                    string trueHeaderKey = $"{action.NodeId}_True";
                    string falseHeaderKey = $"{action.NodeId}_False";

                    if (layout.BranchHeaderPositions.TryGetValue(trueHeaderKey, out var trueHeaderPos))
                        DrawConnection(canvas, fromPos, trueHeaderPos, TrueBranchColor, false, "T", centerOffset);
                    if (layout.BranchHeaderPositions.TryGetValue(falseHeaderKey, out var falseHeaderPos))
                        DrawConnection(canvas, fromPos, falseHeaderPos, FalseBranchColor, false, "F", centerOffset);

                    // 递归渲染嵌套分支内部
                    if (action.TrueActions?.Count > 0)
                        RenderBranchInternalConnections(canvas, action.TrueActions, layout, centerOffset, trueHeaderKey, true);
                    if (action.FalseActions?.Count > 0)
                        RenderBranchInternalConnections(canvas, action.FalseActions, layout, centerOffset, falseHeaderKey, false);

                    // 嵌套 If 分支末尾 → 嵌套 If 之后的步骤
                    if (i < branchActions.Count - 1 && layout.NodePositions.TryGetValue(branchActions[i + 1].NodeId, out var afterNestedPos))
                    {
                        if (action.TrueActions?.Count > 0)
                        {
                            var lastTrue = action.TrueActions[^1];
                            if (layout.NodePositions.TryGetValue(lastTrue.NodeId, out var lastTruePos))
                                DrawConnection(canvas, lastTruePos, afterNestedPos, Brushes.Gray, false, null, centerOffset, isReturnLine: true);
                        }
                        if (action.FalseActions?.Count > 0)
                        {
                            var lastFalse = action.FalseActions[^1];
                            if (layout.NodePositions.TryGetValue(lastFalse.NodeId, out var lastFalsePos))
                                DrawConnection(canvas, lastFalsePos, afterNestedPos, Brushes.Gray, false, null, centerOffset, isReturnLine: true);
                        }
                    }
                    continue;
                }

                // 旧模式 If 跳转
                if (action.ActionType == ActionType.If
                    && (!string.IsNullOrEmpty(action.TrueGotoNodeId) || !string.IsNullOrEmpty(action.GotoNodeId)))
                {
                    if (!string.IsNullOrEmpty(action.TrueGotoNodeId) && layout.NodePositions.TryGetValue(action.TrueGotoNodeId, out var truePos))
                        DrawConnection(canvas, fromPos, truePos, TrueBranchColor, false, "T", centerOffset);
                    if (!string.IsNullOrEmpty(action.GotoNodeId) && layout.NodePositions.TryGetValue(action.GotoNodeId, out var falsePos))
                        DrawConnection(canvas, fromPos, falsePos, FalseBranchColor, false, "F", centerOffset);
                    continue;
                }

                // Goto 跳转
                if (action.ActionType == ActionType.Goto && !string.IsNullOrEmpty(action.GotoNodeId))
                {
                    if (layout.NodePositions.TryGetValue(action.GotoNodeId, out var gotoPos))
                        DrawConnection(canvas, fromPos, gotoPos, new SolidColorBrush(Color.FromRgb(0, 188, 212)), true, null, centerOffset);
                    continue;
                }

                // 普通步骤 → 下一步
                if (i < branchActions.Count - 1)
                {
                    if (layout.NodePositions.TryGetValue(branchActions[i + 1].NodeId, out var nextPos))
                        DrawConnection(canvas, fromPos, nextPos, Brushes.Gray, false, null, centerOffset);
                }
            }
        }

        /// <summary>
        /// 渲染 Goto 跳转连接线（仅顶层）。
        /// </summary>
        private void RenderGotoConnections(Canvas canvas, List<RecordedAction> actions, LayoutResult layout, double centerOffset)
        {
            foreach (var action in actions)
            {
                if (action.ActionType != ActionType.Goto) continue;
                if (string.IsNullOrEmpty(action.GotoNodeId)) continue;
                if (!layout.NodePositions.TryGetValue(action.NodeId, out var fromPos)) continue;
                if (!layout.NodePositions.TryGetValue(action.GotoNodeId, out var gotoPos)) continue;
                DrawConnection(canvas, fromPos, gotoPos, new SolidColorBrush(Color.FromRgb(0, 188, 212)), true, null, centerOffset);
            }
        }

        /// <summary>
        /// 绘制连接线。支持同列直线和跨列折线。
        /// </summary>
        private void DrawConnection(Canvas canvas, Point from, Point to, Brush stroke, bool dashed, string? label, double centerOffset, bool isReturnLine = false)
        {
            double fromCX = (from.X + NodeWidth / 2) * _zoom + centerOffset;
            double fromBottomY = (from.Y + NodeHeight) * _zoom;
            double toCX = (to.X + NodeWidth / 2) * _zoom + centerOffset;
            double toTopY = to.Y * _zoom;

            // 分支头的中心 X 使用 BranchHeaderWidth
            if (from.X < to.X - 1 || from.X > to.X + NodeWidth)
            {
                // from 可能是分支头
            }

            bool crossColumn = Math.Abs(fromCX - toCX) > 2;

            if (isReturnLine)
            {
                // 回归线：从分支末尾回到主线，用折线绕行
                // 底部向下 → 水平到主线列 → 向下到目标
                double midY1 = fromBottomY + 10 * _zoom;
                double midY2 = toTopY - 10 * _zoom;

                var points = new PointCollection
                {
                    new Point(fromCX, fromBottomY),
                    new Point(fromCX, midY1),
                    new Point(toCX, midY1),
                    new Point(toCX, toTopY)
                };
                canvas.Children.Add(new Polyline { Points = points, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } });

                // 箭头
                var arrow = new Polygon { Fill = stroke };
                arrow.Points.Add(new Point(toCX, toTopY));
                arrow.Points.Add(new Point(toCX - 4, toTopY - 6));
                arrow.Points.Add(new Point(toCX + 4, toTopY - 6));
                canvas.Children.Add(arrow);
            }
            else if (!crossColumn)
            {
                // 同列：垂直直线
                AddLine(canvas, fromCX, fromBottomY, toCX, toTopY, stroke, dashed);
                // 箭头
                var arrow = new Polygon { Fill = stroke };
                arrow.Points.Add(new Point(toCX, toTopY));
                arrow.Points.Add(new Point(toCX - 4, toTopY - 6));
                arrow.Points.Add(new Point(toCX + 4, toTopY - 6));
                canvas.Children.Add(arrow);
            }
            else
            {
                // 跨列：折线
                double midY = fromBottomY + (toTopY - fromBottomY) / 2;

                if (dashed)
                {
                    AddLine(canvas, fromCX, fromBottomY, fromCX, midY, stroke, true);
                    AddLine(canvas, fromCX, midY, toCX, midY, stroke, true);
                    AddLine(canvas, toCX, midY, toCX, toTopY, stroke, true);
                }
                else
                {
                    var points = new PointCollection
                    {
                        new Point(fromCX, fromBottomY),
                        new Point(fromCX, midY),
                        new Point(toCX, midY),
                        new Point(toCX, toTopY)
                    };
                    canvas.Children.Add(new Polyline { Points = points, Stroke = stroke, StrokeThickness = 1.5 });
                }

                // 箭头
                var arrow = new Polygon { Fill = stroke };
                arrow.Points.Add(new Point(toCX, toTopY));
                arrow.Points.Add(new Point(toCX - 4, toTopY - 6));
                arrow.Points.Add(new Point(toCX + 4, toTopY - 6));
                canvas.Children.Add(arrow);
            }

            // 分支标签
            if (label != null)
            {
                var text = new TextBlock { Text = label, Foreground = stroke, FontSize = 9, FontWeight = FontWeights.Bold };
                if (crossColumn)
                {
                    double midY2 = fromBottomY + (toTopY - fromBottomY) / 2;
                    Canvas.SetLeft(text, (fromCX + toCX) / 2 - 4);
                    Canvas.SetTop(text, midY2 - 8);
                }
                else
                {
                    Canvas.SetLeft(text, fromCX + 6);
                    Canvas.SetTop(text, (fromBottomY + toTopY) / 2 - 8);
                }
                canvas.Children.Add(text);
            }
        }

        private static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, Brush stroke, bool dashed)
        {
            var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = 1.5 };
            if (dashed) line.StrokeDashArray = new DoubleCollection { 4, 2 };
            canvas.Children.Add(line);
        }

        // === Node rendering ===

        private FrameworkElement CreateNodeControl(RecordedAction action, Point pos, double centerOffset, bool isInTrueBranch = false, bool isInFalseBranch = false)
        {
            Brush color = TypeColors.TryGetValue(action.ActionType, out var c) ? c : Brushes.Gray;

            double w = NodeWidth * _zoom;
            double h = NodeHeight * _zoom;

            // 分支内节点的边框颜色微调
            Brush borderColor = color;
            if (isInTrueBranch) borderColor = TrueBranchColor;
            else if (isInFalseBranch) borderColor = FalseBranchColor;

            var border = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(4),
                BorderBrush = borderColor,
                BorderThickness = new Thickness(1.5),
                Background = action.ActionType == ActionType.If
                    ? new SolidColorBrush(Color.FromRgb(255, 248, 225))
                    : Brushes.White,
                Cursor = Cursors.Hand,
                Tag = action.NodeId,
                ToolTip = $"#{action.Order} [{action.NodeId}] {action.Summary}"
            };

            // 选中高亮
            if (action.NodeId == _selectedNodeId)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                border.BorderThickness = new Thickness(2.5);
            }

            // If 节点折叠/展开指示器
            var dock = new DockPanel();

            // 左侧颜色条
            var colorBar = new Border
            {
                Width = 4,
                Background = borderColor,
                CornerRadius = new CornerRadius(4, 0, 0, 4)
            };
            DockPanel.SetDock(colorBar, Dock.Left);
            dock.Children.Add(colorBar);

            // 文本区域
            var textPanel = new StackPanel { Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

            // 行1: 图标 + 类型 + 折叠指示（If/While/Loop/Try 有子体时可折叠）
            string collapseIndicator = "";
            bool isContainerWithBody =
                (action.ActionType == ActionType.If || action.ActionType == ActionType.While
                 || action.ActionType == ActionType.Loop || action.ActionType == ActionType.Try)
                && (action.TrueActions?.Count > 0 || action.FalseActions?.Count > 0);
            if (isContainerWithBody)
            {
                collapseIndicator = _collapsedIfNodeIds.Contains(action.NodeId) ? " ▶" : " ▼";
            }

            // 图标 + 名字（优先用户自定义 DisplayName，其次 Name，最后类型）+ 折叠指示
            string title = !string.IsNullOrWhiteSpace(action.DisplayName) ? action.DisplayName
                : !string.IsNullOrWhiteSpace(action.Name) ? action.Name
                : action.ActionType.ToString();
            var line1 = new TextBlock
            {
                Text = $"{GetActionIcon(action.ActionType)} {title}{collapseIndicator}",
                FontSize = 10 * Math.Max(_zoom, 0.7),
                Foreground = color,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            textPanel.Children.Add(line1);

            dock.Children.Add(textPanel);
            border.Child = dock;

            Canvas.SetLeft(border, pos.X * _zoom + centerOffset);
            Canvas.SetTop(border, pos.Y * _zoom);

            border.MouseLeftButtonDown += Node_MouseDown;

            return border;
        }

        /// <summary>
        /// 创建 True/False 分支头标签。
        /// </summary>
        private FrameworkElement CreateBranchHeader(string key, bool isTrue, Point pos, double centerOffset)
        {
            var brush = isTrue ? TrueBranchColor : FalseBranchColor;
            // 从 key（{parentId}_True/_False）解析父节点，按父节点类型决定标签文案
            string parentLabel = isTrue ? "✓ True" : "✗ False";
            int sep = key.LastIndexOf('_');
            if (sep > 0)
            {
                var parent = FindActionByNodeId(key[..sep], _actions);
                if (parent != null && (parent.ActionType == ActionType.While || parent.ActionType == ActionType.Loop))
                {
                    parentLabel = "🔄 循环体";
                }
                else if (parent != null && parent.ActionType == ActionType.Try)
                {
                    parentLabel = isTrue ? "🟢 Try" : "🔴 Catch";
                }
            }
            string text = parentLabel;

            var border = new Border
            {
                Width = BranchHeaderWidth * _zoom,
                Height = (NodeHeight * 0.7) * _zoom,
                CornerRadius = new CornerRadius(3),
                Background = brush,
                Cursor = Cursors.Hand,
                Tag = $"BRANCH_{key}", // 标记为分支头
                ToolTip = parentLabel + "（点击选中父节点）"
            };

            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 10 * Math.Max(_zoom, 0.7),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            border.Child = textBlock;

            Canvas.SetLeft(border, pos.X * _zoom + centerOffset);
            Canvas.SetTop(border, pos.Y * _zoom);

            // 点击分支头 → 选中对应的 If 节点
            border.MouseLeftButtonDown += BranchHeader_MouseDown;

            return border;
        }

        // === Node interaction ===

        private void Node_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var element = sender as FrameworkElement;
            while (element != null && element.Tag == null)
                element = VisualTreeHelper.GetParent(element) as FrameworkElement;
            if (element?.Tag is string nodeId && !nodeId.StartsWith("BRANCH_"))
            {
                _selectedNodeId = nodeId;
                UpdatePropertiesPanel();
                RenderFlowChart();

                // 双击编辑
                if (e.ClickCount == 2)
                {
                    EditSelectedStep();
                }
            }
        }

        private void BranchHeader_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var element = sender as FrameworkElement;
            while (element != null && element.Tag == null)
                element = VisualTreeHelper.GetParent(element) as FrameworkElement;
            if (element?.Tag is string tag && tag.StartsWith("BRANCH_"))
            {
                // 从分支头 key 提取 If 节点 NodeId
                string key = tag["BRANCH_".Length..];
                // key 格式: "ifNodeId_True" 或 "ifNodeId_False"
                int lastUnderscore = key.LastIndexOf('_');
                if (lastUnderscore > 0)
                {
                    string ifNodeId = key[..lastUnderscore];
                    _selectedNodeId = ifNodeId;
                    UpdatePropertiesPanel();
                    RenderFlowChart();
                }
            }
        }

        // === Properties panel ===

        private void UpdatePropertiesPanel()
        {
            var propsPanel = PropsPanel;
            if (propsPanel == null) return;
            propsPanel.Children.Clear();

            var action = FindActionByNodeId(_selectedNodeId, _actions);
            if (action == null)
            {
                propsPanel.Children.Add(new TextBlock { Text = "选择一个节点查看属性", Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                return;
            }

            // 标题
            propsPanel.Children.Add(new TextBlock
            {
                Text = $"步骤 #{action.Order}  {action.ActionType}",
                FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3)
            });
            propsPanel.Children.Add(new TextBlock
            {
                Text = $"NodeId: {action.NodeId}",
                FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8)
            });

            // 名称（用户自定义，流程图节点显示；留空则显示自动名/类型）
            propsPanel.Children.Add(MakeEditField("名称:", action.DisplayName ?? "", v => { action.DisplayName = string.IsNullOrWhiteSpace(v) ? null : v; }, "自定义名称，流程图节点显示此名字"));
            // 窗口标题
            if (ShowWindowTitle(action.ActionType))
                propsPanel.Children.Add(MakeEditField("窗口标题:", action.WindowTitle ?? "", v => { action.WindowTitle = v; }, "留空=当前前台窗口"));
            // 参数
            if (ShowParameter(action.ActionType))
                propsPanel.Children.Add(MakeEditField("参数:", action.Parameter ?? "", v => { action.Parameter = v; }, multiline: true));
            // 滚动行数
            if (action.ActionType == ActionType.Scroll || action.ActionType == ActionType.ScrollRead)
                propsPanel.Children.Add(MakeEditField("滚动行数:", action.ScrollAmount.ToString(), v => { if (int.TryParse(v, out int n)) action.ScrollAmount = n; }));
            // 输出变量名
            if (ShowOutputVar(action.ActionType))
                propsPanel.Children.Add(MakeEditField("输出变量名:", action.OutputParamName ?? "", v => { action.OutputParamName = string.IsNullOrWhiteSpace(v) ? null : v; }, "阅读/正则存内容，点击存成功状态"));
            // 延时
            if (ShowDelay(action.ActionType))
                propsPanel.Children.Add(MakeEditField("延时(ms):", action.DelayMs.ToString(), v => { if (int.TryParse(v, out int d)) action.DelayMs = d; }));

            // 点击模式
            if (action.ActionType == ActionType.Click)
            {
                var cmPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                cmPanel.Children.Add(new TextBlock { Text = "点击模式:", Margin = new Thickness(0, 0, 0, 3), FontSize = 11 });
                var cmCombo = new ComboBox { Width = 120 };
                cmCombo.Items.Add("Coordinate"); cmCombo.Items.Add("UIAPath"); cmCombo.Items.Add("Vision");
                cmCombo.SelectedItem = action.ClickMode.ToString();
                cmCombo.SelectionChanged += (_, _) =>
                {
                    if (Enum.TryParse<WeChatAutomation.Core.Recording.ClickMode>(cmCombo.SelectedItem?.ToString(), out var cm))
                    { action.ClickMode = cm; SyncToRecorder(); RenderFlowChart(); }
                };
                cmPanel.Children.Add(cmCombo);
                propsPanel.Children.Add(cmPanel);
            }

            // ── 容器/控制流节点配置 ──
            if (action.ActionType == ActionType.If)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "判断分支配置:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(MakeEditField("条件表达式:", action.ConditionExpression ?? "", v => { action.ConditionExpression = v; }, "留空则按输出变量是否有值判断"));
                propsPanel.Children.Add(BuildBranchBehaviorEditor(action, true));
                propsPanel.Children.Add(BuildBranchBehaviorEditor(action, false));

                bool hasTreeBranches = action.TrueActions?.Count > 0 || action.FalseActions?.Count > 0;
                if (hasTreeBranches)
                {
                    AppendBranchStepList(propsPanel, action, true, "✓ True 分支", TrueBranchColor);
                    AppendBranchStepList(propsPanel, action, false, "✗ False 分支", FalseBranchColor);
                    AppendCollapseToggle(propsPanel, action);
                }
                else
                {
                    propsPanel.Children.Add(MakeTargetCombo("True 跳转:", action.TrueGotoNodeId, _actions, true, v => { action.TrueGotoNodeId = v; }));
                    propsPanel.Children.Add(MakeTargetCombo("False 跳转:", action.GotoNodeId, _actions, true, v => { action.GotoNodeId = v; }));
                    var convertBtn = new Button { Content = "切换为树形分支模式", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 4, 0, 4), FontSize = 10, Tag = action, Cursor = Cursors.Hand };
                    convertBtn.Click += ConvertToTreeMode_Click;
                    propsPanel.Children.Add(convertBtn);
                }
            }
            else if (action.ActionType == ActionType.While)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "While 循环配置:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(MakeEditField("循环条件:", action.ConditionExpression ?? "", v => { action.ConditionExpression = v; }, "条件成立时重复执行循环体；留空则按输出变量是否有值"));
                propsPanel.Children.Add(MakeEditField("最大迭代次数:", action.MaxLoopCount.ToString(), v => { if (int.TryParse(v, out int n) && n > 0) action.MaxLoopCount = n; }, "防死循环上限"));
                AppendBranchStepList(propsPanel, action, true, "🔄 循环体", new SolidColorBrush(Color.FromRgb(255, 87, 34)));
                AppendCollapseToggle(propsPanel, action);
            }
            else if (action.ActionType == ActionType.Loop)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "固定循环配置:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(MakeEditField("循环次数:", action.LoopCount.ToString(), v => { if (int.TryParse(v, out int n) && n > 0) action.LoopCount = n; }));
                AppendBranchStepList(propsPanel, action, true, "🔄 循环体", new SolidColorBrush(Color.FromRgb(255, 111, 0)));
                AppendCollapseToggle(propsPanel, action);
            }
            else if (action.ActionType == ActionType.Try)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "容错配置:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                AppendBranchStepList(propsPanel, action, true, "🟢 Try 体", TrueBranchColor);
                AppendBranchStepList(propsPanel, action, false, "🔴 Catch 体", FalseBranchColor);
                AppendCollapseToggle(propsPanel, action);
            }
            else if (action.ActionType == ActionType.HttpWait)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "等待 HTTP 触发:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(MakeEditField("唯一 key:", action.WaitKey ?? "", v => { action.WaitKey = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }, "外部 HTTP 请求需带此 key 唤醒：POST /api/wait/{key}"));
                propsPanel.Children.Add(MakeEditField("超时(ms):", action.WaitTimeoutMs.ToString(), v => { if (int.TryParse(v, out int n)) action.WaitTimeoutMs = n; }, "0=无限等待"));
                propsPanel.Children.Add(MakeEditField("传入写入变量:", action.ResponseVarName ?? "", v => { action.ResponseVarName = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }, "外部请求体内容写入该变量"));
            }
            else if (action.ActionType == ActionType.HttpCall)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "调用 HTTP API:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(MakeEditField("URL:", action.HttpUrl ?? "", v => { action.HttpUrl = v; }, "支持 {变量} 占位"));
                var methodPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                methodPanel.Children.Add(new TextBlock { Text = "方法:", Margin = new Thickness(0, 0, 0, 3), FontSize = 11 });
                var methodCombo = new ComboBox { Width = 120 };
                methodCombo.Items.Add("GET"); methodCombo.Items.Add("POST"); methodCombo.Items.Add("PUT"); methodCombo.Items.Add("DELETE");
                methodCombo.SelectedItem = string.IsNullOrEmpty(action.HttpMethod) ? "GET" : action.HttpMethod;
                methodCombo.SelectionChanged += (_, _) => { action.HttpMethod = methodCombo.SelectedItem?.ToString() ?? "GET"; SyncToRecorder(); RenderFlowChart(); };
                methodPanel.Children.Add(methodCombo);
                propsPanel.Children.Add(methodPanel);
                propsPanel.Children.Add(MakeEditField("请求头:", action.HttpHeaders ?? "", v => { action.HttpHeaders = string.IsNullOrWhiteSpace(v) ? null : v; }, "每行一个 Key: Value", multiline: true));
                propsPanel.Children.Add(MakeEditField("请求体:", action.HttpBody ?? "", v => { action.HttpBody = string.IsNullOrWhiteSpace(v) ? null : v; }, "支持 {变量} 占位", multiline: true));
                propsPanel.Children.Add(MakeEditField("响应写入变量:", action.ResponseVarName ?? "", v => { action.ResponseVarName = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }, "响应内容写入该变量供后续使用"));
            }

            // Goto 目标
            if (action.ActionType == ActionType.Goto)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(MakeTargetCombo("跳转目标:", action.GotoNodeId, _actions, true, v => { action.GotoNodeId = v; }));
            }

            // 备注
            propsPanel.Children.Add(MakeEditField("备注:", action.Remark ?? "", v => { action.Remark = string.IsNullOrWhiteSpace(v) ? null : v; }, "步骤说明/备注"));

            // 操作按钮
            propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });

            // 部分运行：单独执行此节点（含其子体，用于测试）
            propsPanel.Children.Add(MakeBtn("▶ 运行此节点", null, RunSelectedNode, Brushes.Green));

            var btnRow1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            btnRow1.Children.Add(MakeBtn("✏ 编辑", null, EditSelectedStep));
            btnRow1.Children.Add(MakeBtn("📋 复制", new Thickness(5, 0, 0, 0), DuplicateSelectedStep));
            propsPanel.Children.Add(btnRow1);
            var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            btnRow2.Children.Add(MakeBtn("↑ 上移", null, MoveSelectedUp));
            btnRow2.Children.Add(MakeBtn("↓ 下移", new Thickness(5, 0, 0, 0), MoveSelectedDown));
            propsPanel.Children.Add(btnRow2);
            propsPanel.Children.Add(MakeBtn("🗑 删除", null, DeleteSelectedStep, Brushes.Red));
        }

        /// <summary>
        /// 部分运行：单独执行当前选中的节点（含其子分支/循环体），用于测试。
        /// </summary>
        private async void RunSelectedNode()
        {
            var action = FindActionByNodeId(_selectedNodeId, _actions);
            if (action == null) { MessageBox.Show("请先选择一个节点"); return; }
            if (_player.IsPlaying) { MessageBox.Show("正在回放中，请先停止"); return; }

            SyncToRecorder();
            StatusText.Text = $"运行节点 #{action.Order} {action.ActionType} ...";
            try
            {
                await _player.Play(new List<RecordedAction> { action }, null);
                StatusText.Text = $"节点 #{action.Order} 运行完成";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"运行失败: {ex.Message}";
            }
        }

        // === Branch editing ===

        /// <summary>
        /// 添加步骤到 If 分支。
        /// </summary>
        private void AddToBranch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not (RecordedAction ifAction, bool isTrueBranch)) return;

            // 弹出步骤类型选择
            var stepType = ShowStepTypePicker();
            if (stepType == null) return;

            var newStep = CreateStepByType(stepType);
            if (newStep == null) return;

            if (isTrueBranch)
            {
                ifAction.TrueActions ??= new List<RecordedAction>();
                ifAction.TrueActions.Add(newStep);
            }
            else
            {
                ifAction.FalseActions ??= new List<RecordedAction>();
                ifAction.FalseActions.Add(newStep);
            }

            _selectedNodeId = newStep.NodeId;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        /// <summary>
        /// 从 If 分支删除步骤。
        /// </summary>
        private void DeleteBranchStep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not (RecordedAction ifAction, RecordedAction subAction, bool isTrueBranch)) return;

            string msg = $"确定从 {(isTrueBranch ? "True" : "False")} 分支删除步骤？\n{subAction.Summary}";
            if (MessageBox.Show(msg, "删除分支步骤", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            if (isTrueBranch)
                ifAction.TrueActions?.Remove(subAction);
            else
                ifAction.FalseActions?.Remove(subAction);

            if (_selectedNodeId == subAction.NodeId)
                _selectedNodeId = ifAction.NodeId;

            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        /// <summary>
        /// 折叠/展开 If 分支。
        /// </summary>
        private void ToggleBranchCollapse_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string ifNodeId) return;

            if (_collapsedIfNodeIds.Contains(ifNodeId))
                _collapsedIfNodeIds.Remove(ifNodeId);
            else
                _collapsedIfNodeIds.Add(ifNodeId);

            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        /// <summary>
        /// 将 If 步骤从跳转模式切换为树形分支模式。
        /// </summary>
        private void ConvertToTreeMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not RecordedAction ifAction) return;

            ifAction.TrueActions ??= new List<RecordedAction>();
            ifAction.FalseActions ??= new List<RecordedAction>();
            ifAction.TrueGotoNodeId = null;
            ifAction.GotoNodeId = null;

            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        /// <summary>
        /// 弹出步骤类型选择对话框。
        /// </summary>
        private string? ShowStepTypePicker()
        {
            var w = new Window
            {
                Title = "选择步骤类型",
                Width = 300,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };
            sp.Children.Add(new TextBlock { Text = "选择要添加的步骤类型:", Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold });

            var types = new (string Label, string Tag)[]
            {
                ("👆 点击", "Click"),
                ("⌨ 输入文本", "TypeText"),
                ("🔤 按键", "SendKeys"),
                ("📋 复制", "Copy"),
                ("📄 粘贴", "Paste"),
                ("✏ 插入文本", "InsertText"),
                ("⏳ 等待", "Wait"),
                ("📸 截图", "Screenshot"),
                ("📂 打开应用", "OpenApp"),
                ("⏳ 等待应用", "WaitForApp"),
                ("🖱 滚动", "Scroll"),
                ("📖 阅读窗口", "ReadContent"),
                ("📖 滚动阅读", "ScrollRead"),
                ("🔍 正则识别", "RegexMatch"),
                ("📝 输入参数", "InputParam"),
                ("🔀 判断", "If"),
                ("🔁 While循环", "While"),
                ("🔢 循环N次", "Loop"),
                ("🛡 容错Try", "Try"),
                ("⏹ 跳出循环", "Break"),
                ("⏭ 下一轮", "Continue"),
                ("⏸ 等待HTTP", "HttpWait"),
                ("🌐 调用HTTP", "HttpCall"),
                ("↩ 跳转", "Goto"),
                ("🪟 切窗", "SwitchToWindow"),
            };

            string? selectedType = null;
            foreach (var (label, tag) in types)
            {
                var btn = new Button
                {
                    Content = label,
                    Tag = tag,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 1, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Cursor = Cursors.Hand
                };
                btn.Click += (_, _) => { selectedType = tag; w.DialogResult = true; };
                sp.Children.Add(btn);
            }

            var cancelBtn = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 4, 15, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            sp.Children.Add(cancelBtn);
            w.Content = sp;

            w.ShowDialog();
            return selectedType;
        }

        /// <summary>
        /// 根据步骤类型标签创建 RecordedAction。
        /// </summary>
        private RecordedAction? CreateStepByType(string? typeTag)
        {
            if (string.IsNullOrEmpty(typeTag)) return null;

            var newNode = new RecordedAction { Order = 0, ClickMode = _currentClickMode, ActionType = ActionType.Click, Name = "点击" };

            switch (typeTag)
            {
                case "Click": newNode.ActionType = ActionType.Click; newNode.Name = "点击"; break;
                case "TypeText":
                    {
                        var t = ShowInput("输入文本", "内容:");
                        if (t == null) return null;
                        newNode.ActionType = ActionType.TypeText; newNode.Parameter = t;
                        newNode.Name = t.Length > 12 ? "输入\"" + t[..12] + "...\"" : "输入\"" + t + "\"";
                        break;
                    }
                case "SendKeys":
                    {
                        var k = ShowInput("按键", "如 Enter, Ctrl+A:", "Enter");
                        if (k == null) return null;
                        newNode.ActionType = ActionType.SendKeys; newNode.Parameter = k; newNode.Name = "按键 " + k;
                        break;
                    }
                case "Copy": newNode.ActionType = ActionType.Copy; newNode.Name = "复制"; break;
                case "Paste": newNode.ActionType = ActionType.Paste; newNode.Name = "粘贴"; break;
                case "InsertText":
                    {
                        var t = ShowInput("插入文本", "内容:");
                        if (t == null) return null;
                        newNode.ActionType = ActionType.InsertText; newNode.Parameter = t;
                        newNode.Name = t.Length > 12 ? "插入\"" + t[..12] + "...\"" : "插入\"" + t + "\"";
                        break;
                    }
                case "Wait":
                    {
                        var ms = ShowInput("等待", "毫秒:", "1000");
                        if (!int.TryParse(ms, out int v)) return null;
                        newNode.ActionType = ActionType.Wait; newNode.Parameter = v.ToString(); newNode.Name = "等待" + v + "ms";
                        break;
                    }
                case "Screenshot": newNode.ActionType = ActionType.Screenshot; newNode.Name = "截图"; break;
                case "OpenApp": newNode.ActionType = ActionType.OpenApp; newNode.Name = "打开应用"; break;
                case "WaitForApp": newNode.ActionType = ActionType.WaitForApp; newNode.Name = "等待应用"; break;
                case "ReadContent": newNode.ActionType = ActionType.ReadContent; newNode.Name = "阅读窗口"; break;
                case "ScrollRead":
                    {
                        var n = ShowInput("滚动阅读", "行数:", "5");
                        if (!int.TryParse(n, out int v)) return null;
                        newNode.ActionType = ActionType.ScrollRead; newNode.ScrollAmount = v; newNode.Parameter = v.ToString(); newNode.Name = "滚动阅读" + v + "行";
                        break;
                    }
                case "RegexMatch": newNode.ActionType = ActionType.RegexMatch; newNode.Name = "正则识别"; break;
                case "Scroll":
                    {
                        var n = ShowInput("滚动", "行数(正=下 负=上):", "3");
                        if (!int.TryParse(n, out int v)) return null;
                        newNode.ActionType = ActionType.Scroll; newNode.ScrollAmount = v; newNode.Parameter = v.ToString(); newNode.Name = "滚动" + v + "行";
                        break;
                    }
                case "InputParam": newNode.ActionType = ActionType.InputParam; newNode.ParameterName = "参数1"; newNode.Name = "输入参数"; break;
                case "If": newNode.ActionType = ActionType.If; newNode.Name = "判断"; newNode.ConditionExpression = "";
                    newNode.TrueActions = new List<RecordedAction>();
                    newNode.FalseActions = new List<RecordedAction>();
                    break;
                case "While": newNode.ActionType = ActionType.While; newNode.Name = "While循环"; newNode.ConditionExpression = "";
                    newNode.TrueActions = new List<RecordedAction>();
                    break;
                case "Loop":
                    {
                        var n = ShowInput("循环次数", "次数:", "3");
                        if (!int.TryParse(n, out int v) || v <= 0) return null;
                        newNode.ActionType = ActionType.Loop; newNode.LoopCount = v; newNode.Name = $"循环{v}次";
                        newNode.TrueActions = new List<RecordedAction>();
                        break;
                    }
                case "Try": newNode.ActionType = ActionType.Try; newNode.Name = "容错Try";
                    newNode.TrueActions = new List<RecordedAction>();
                    newNode.FalseActions = new List<RecordedAction>();
                    break;
                case "Break": newNode.ActionType = ActionType.Break; newNode.Name = "跳出循环"; break;
                case "Continue": newNode.ActionType = ActionType.Continue; newNode.Name = "下一轮"; break;
                case "HttpWait":
                    {
                        var key = ShowInput("等待HTTP触发", "唯一key(外部请求需带此key):", "wait1");
                        if (string.IsNullOrWhiteSpace(key)) return null;
                        newNode.ActionType = ActionType.HttpWait; newNode.WaitKey = key.Trim();
                        newNode.Name = $"等待HTTP[{key.Trim()}]";
                        break;
                    }
                case "HttpCall":
                    {
                        var url = ShowInput("调用HTTP", "URL:", "https://");
                        if (string.IsNullOrWhiteSpace(url)) return null;
                        newNode.ActionType = ActionType.HttpCall; newNode.HttpUrl = url.Trim(); newNode.HttpMethod = "GET";
                        newNode.Name = $"调用 {url.Trim()}";
                        break;
                    }
                case "Goto": newNode.ActionType = ActionType.Goto; newNode.Name = "跳转"; break;
                case "SwitchToWindow": newNode.ActionType = ActionType.SwitchToWindow; newNode.Name = "切窗"; break;
                default: return null;
            }

            return newNode;
        }

        // === Helper methods ===

        private StackPanel MakeEditField(string label, string value, Action<string> onCommit, string? tip = null, bool multiline = false)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3), FontSize = 11 });
            TextBox box;
            if (multiline)
                box = new TextBox { Text = value, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 70, ToolTip = tip };
            else
                box = new TextBox { Text = value, ToolTip = tip };
            box.LostFocus += (_, _) =>
            {
                onCommit(box.Text);
                SyncToRecorder();
                RenderFlowChart();
            };
            panel.Children.Add(box);
            return panel;
        }

        private StackPanel MakeTargetCombo(string label, string? currentId, List<RecordedAction> actions, bool allowNone, Action<string?> onCommit)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3), FontSize = 11 });
            var combo = new ComboBox();
            int sel = 0;
            if (allowNone) combo.Items.Add("(下一步)");
            for (int i = 0; i < actions.Count; i++)
            {
                combo.Items.Add($"#{actions[i].Order} {Truncate(actions[i].Summary, 24)}");
                if (actions[i].NodeId == currentId) sel = allowNone ? i + 1 : i;
            }
            combo.SelectedIndex = Math.Min(sel, combo.Items.Count - 1);
            combo.SelectionChanged += (_, _) =>
            {
                int idx = combo.SelectedIndex;
                string? targetId = null;
                if (allowNone) { if (idx > 0) targetId = actions[idx - 1].NodeId; }
                else { if (idx >= 0 && idx < actions.Count) targetId = actions[idx].NodeId; }
                onCommit(targetId);
                SyncToRecorder();
                RenderFlowChart();
            };
            panel.Children.Add(combo);
            return panel;
        }

        private static Button MakeBtn(string text, Thickness? margin, Action onClick, Brush? fore = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4) };
            if (margin.HasValue) b.Margin = margin.Value;
            if (fore != null) b.Foreground = fore;
            b.Click += (_, _) => onClick();
            return b;
        }

        private static bool ShowWindowTitle(ActionType t) =>
            t == ActionType.Click || t == ActionType.ReadContent || t == ActionType.ScrollRead
            || t == ActionType.RegexMatch || t == ActionType.SwitchToWindow || t == ActionType.Screenshot;

        private static bool ShowParameter(ActionType t) =>
            t == ActionType.TypeText || t == ActionType.SendKeys || t == ActionType.Wait
            || t == ActionType.InsertText || t == ActionType.OpenApp || t == ActionType.WaitForApp
            || t == ActionType.SwitchToWindow;

        private static bool ShowOutputVar(ActionType t) =>
            t == ActionType.Click || t == ActionType.ReadContent || t == ActionType.ScrollRead || t == ActionType.RegexMatch;

        private static bool ShowDelay(ActionType t) =>
            t == ActionType.Click || t == ActionType.WaitForApp || t == ActionType.OpenApp
            || t == ActionType.TypeText || t == ActionType.SendKeys || t == ActionType.InsertText
            || t == ActionType.Scroll || t == ActionType.ScrollRead;

        /// <summary>
        /// 递归查找指定 NodeId 的 RecordedAction。
        /// </summary>
        private static RecordedAction? FindActionByNodeId(string? nodeId, List<RecordedAction> actions)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            foreach (var action in actions)
            {
                if (action.NodeId == nodeId) return action;
                // 递归搜索 TrueActions/FalseActions
                if (action.TrueActions != null)
                {
                    var found = FindActionByNodeId(nodeId, action.TrueActions);
                    if (found != null) return found;
                }
                if (action.FalseActions != null)
                {
                    var found = FindActionByNodeId(nodeId, action.FalseActions);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 递归展平所有节点（顶层 + 嵌套）。
        /// </summary>
        private static List<RecordedAction> FlattenAllNodes(List<RecordedAction> actions)
        {
            var result = new List<RecordedAction>();
            foreach (var action in actions)
            {
                result.Add(action);
                if (action.TrueActions != null) result.AddRange(FlattenAllNodes(action.TrueActions));
                if (action.FalseActions != null) result.AddRange(FlattenAllNodes(action.FalseActions));
            }
            return result;
        }

        /// <summary>
        /// 查找嵌套节点所属的父 If 节点和分支。
        /// </summary>
        private static (RecordedAction? parentIf, bool isTrueBranch)? FindParentIf(string nodeId, List<RecordedAction> actions)
        {
            foreach (var action in actions)
            {
                if (action.ActionType != ActionType.If) continue;
                if (action.TrueActions != null)
                {
                    foreach (var sub in action.TrueActions)
                    {
                        if (sub.NodeId == nodeId) return (action, true);
                        var nested = FindParentIf(nodeId, action.TrueActions);
                        if (nested != null) return nested;
                    }
                }
                if (action.FalseActions != null)
                {
                    foreach (var sub in action.FalseActions)
                    {
                        if (sub.NodeId == nodeId) return (action, false);
                        var nested = FindParentIf(nodeId, action.FalseActions);
                        if (nested != null) return nested;
                    }
                }
            }
            return null;
        }

        // === Edit / Delete ===

        private void EditSelectedStep()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;
            var action = FindActionByNodeId(_selectedNodeId, _actions);
            if (action == null) return;

            var w = new Window
            {
                Title = $"Edit step #{action.Order}",
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };

            sp.Children.Add(new TextBlock { Text = "Name:", Margin = new Thickness(0, 0, 0, 3) });
            var nameBox = new TextBox { Text = action.Name ?? "", Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(nameBox);

            sp.Children.Add(new TextBlock { Text = "Parameter:", Margin = new Thickness(0, 0, 0, 3) });
            var paramBox = new TextBox { Text = action.Parameter ?? "", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 80 };
            sp.Children.Add(paramBox);

            sp.Children.Add(new TextBlock { Text = "Window title:", Margin = new Thickness(0, 0, 0, 3) });
            var windowTitleBox = new TextBox { Text = action.WindowTitle ?? "", Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(windowTitleBox);

            // If-specific fields
            TextBox? exprBox = null;
            ComboBox? trueCombo = null;
            ComboBox? falseCombo = null;
            if (action.ActionType == ActionType.If)
            {
                sp.Children.Add(new TextBlock { Text = "Condition expression:", Margin = new Thickness(0, 0, 0, 3) });
                exprBox = new TextBox { Text = action.ConditionExpression ?? "", Margin = new Thickness(0, 0, 0, 8) };
                sp.Children.Add(exprBox);

                // 仅在旧跳转模式下显示跳转目标
                bool hasTreeBranches = action.TrueActions?.Count > 0 || action.FalseActions?.Count > 0;
                if (!hasTreeBranches)
                {
                    sp.Children.Add(new TextBlock { Text = "True branch target:", Margin = new Thickness(0, 0, 0, 3) });
                    trueCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
                    trueCombo.Items.Add("(next step)");
                    int trueSel = 0;
                    for (int i = 0; i < _actions.Count; i++)
                    {
                        trueCombo.Items.Add($"#{_actions[i].Order} [{_actions[i].NodeId}] {_actions[i].Summary}");
                        if (_actions[i].NodeId == action.TrueGotoNodeId) trueSel = i + 1;
                    }
                    trueCombo.SelectedIndex = trueSel;
                    sp.Children.Add(trueCombo);

                    sp.Children.Add(new TextBlock { Text = "False branch target:", Margin = new Thickness(0, 0, 0, 3) });
                    falseCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
                    falseCombo.Items.Add("(next step)");
                    int falseSel = 0;
                    for (int i = 0; i < _actions.Count; i++)
                    {
                        falseCombo.Items.Add($"#{_actions[i].Order} [{_actions[i].NodeId}] {_actions[i].Summary}");
                        if (_actions[i].NodeId == action.GotoNodeId) falseSel = i + 1;
                    }
                    falseCombo.SelectedIndex = falseSel;
                    sp.Children.Add(falseCombo);
                }
            }

            // Goto-specific fields
            ComboBox? gotoCombo = null;
            if (action.ActionType == ActionType.Goto)
            {
                sp.Children.Add(new TextBlock { Text = "Goto target:", Margin = new Thickness(0, 0, 0, 3) });
                gotoCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
                int gotoSel = 0;
                for (int i = 0; i < _actions.Count; i++)
                {
                    gotoCombo.Items.Add($"#{_actions[i].Order} [{_actions[i].NodeId}] {_actions[i].Summary}");
                    if (_actions[i].NodeId == action.GotoNodeId) gotoSel = i;
                }
                gotoCombo.SelectedIndex = gotoSel;
                sp.Children.Add(gotoCombo);
            }

            // OK / Cancel
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok);
            bp.Children.Add(cancel);
            sp.Children.Add(bp);
            w.Content = sp;

            ok.Click += (_, _) =>
            {
                action.Name = nameBox.Text;
                action.Parameter = paramBox.Text;
                action.WindowTitle = windowTitleBox.Text.Trim();
                if (exprBox != null) action.ConditionExpression = exprBox.Text.Trim();
                if (trueCombo != null) action.TrueGotoNodeId = trueCombo.SelectedIndex > 0 ? _actions[trueCombo.SelectedIndex - 1].NodeId : null;
                if (falseCombo != null) action.GotoNodeId = falseCombo.SelectedIndex > 0 ? _actions[falseCombo.SelectedIndex - 1].NodeId : null;
                if (gotoCombo != null) action.GotoNodeId = _actions[gotoCombo.SelectedIndex].NodeId;
                SyncToRecorder();
                RenderFlowChart();
                UpdatePropertiesPanel();
                w.DialogResult = true;
            };
            w.ShowDialog();
        }

        private void DeleteSelectedStep()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;

            // 先检查是否是嵌套步骤
            var parentInfo = FindParentIf(_selectedNodeId, _actions);
            if (parentInfo != null)
            {
                var (parentIf, isTrueBranch) = parentInfo.Value;
                var action = FindActionByNodeId(_selectedNodeId, _actions);
                if (action == null || parentIf == null) return;

                string branchName = isTrueBranch ? "True" : "False";
                if (MessageBox.Show($"确定从 {branchName} 分支删除步骤？\n{action.Summary}", "删除分支步骤", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

                if (isTrueBranch)
                    parentIf.TrueActions?.Remove(action);
                else
                    parentIf.FalseActions?.Remove(action);

                _selectedNodeId = parentIf.NodeId;
                SyncToRecorder();
                RenderFlowChart();
                UpdatePropertiesPanel();
                return;
            }

            // 顶层步骤删除
            var topAction = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (topAction == null) return;
            if (MessageBox.Show($"确定删除步骤 #{topAction.Order}？\n{topAction.Summary}", "删除", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _actions.Remove(topAction);
            foreach (var a in _actions)
            {
                if (a.TrueGotoNodeId == _selectedNodeId) a.TrueGotoNodeId = null;
                if (a.GotoNodeId == _selectedNodeId) a.GotoNodeId = null;
            }
            for (int i = 0; i < _actions.Count; i++) _actions[i].Order = i + 1;
            _selectedNodeId = null;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        private void DuplicateSelectedStep()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;

            // 检查是否是嵌套步骤
            var parentInfo = FindParentIf(_selectedNodeId, _actions);
            if (parentInfo != null)
            {
                var (parentIf, isTrueBranch) = parentInfo.Value;
                var action = FindActionByNodeId(_selectedNodeId, _actions);
                if (action == null || parentIf == null) return;

                var copy = CloneAction(action);
                var list = isTrueBranch ? parentIf.TrueActions : parentIf.FalseActions;
                if (list == null) return;

                int idx = list.IndexOf(action);
                list.Insert(idx + 1, copy);
                _selectedNodeId = copy.NodeId;
                SyncToRecorder();
                RenderFlowChart();
                UpdatePropertiesPanel();
                return;
            }

            // 顶层步骤复制
            var topAction = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (topAction == null) return;
            var topCopy = CloneAction(topAction);
            int topIdx = _actions.IndexOf(topAction);
            _actions.Insert(topIdx + 1, topCopy);
            _selectedNodeId = topCopy.NodeId;
            for (int i = 0; i < _actions.Count; i++) _actions[i].Order = i + 1;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        /// <summary>深拷贝 RecordedAction（新 NodeId）。</summary>
        private static RecordedAction CloneAction(RecordedAction action)
        {
            var copy = new RecordedAction
            {
                ActionType = action.ActionType,
                Name = action.Name,
                DisplayName = action.DisplayName,
                ClassName = action.ClassName, ElementName = action.ElementName, AutomationId = action.AutomationId,
                ControlType = action.ControlType, WindowTitle = action.WindowTitle,
                X = action.X, Y = action.Y, ClickMode = action.ClickMode,
                VisionLabel = action.VisionLabel, VisionConfThreshold = action.VisionConfThreshold, TemplateImage = action.TemplateImage,
                XPath = action.XPath, SiblingIndex = action.SiblingIndex, RuntimeId = action.RuntimeId,
                Parameter = action.Parameter, DelayMs = action.DelayMs, ScrollAmount = action.ScrollAmount,
                ParameterName = action.ParameterName, DefaultValue = action.DefaultValue, IsRequired = action.IsRequired,
                CopyToClipboard = action.CopyToClipboard,
                RegexPattern = action.RegexPattern, RegexGroup = action.RegexGroup, OutputParamName = action.OutputParamName,
                ConditionExpression = action.ConditionExpression, GotoNodeId = action.GotoNodeId, TrueGotoNodeId = action.TrueGotoNodeId,
                TargetScript = action.TargetScript, SwitchAll = action.SwitchAll, IsEnabled = action.IsEnabled,
                Remark = action.Remark,
                TrueBranch = action.TrueBranch, TrueBranchScript = action.TrueBranchScript,
                FalseBranch = action.FalseBranch, FalseBranchScript = action.FalseBranchScript,
                MaxLoopCount = action.MaxLoopCount, LoopCount = action.LoopCount,
                WaitKey = action.WaitKey, WaitTimeoutMs = action.WaitTimeoutMs,
                HttpUrl = action.HttpUrl, HttpMethod = action.HttpMethod, HttpHeaders = action.HttpHeaders,
                HttpBody = action.HttpBody, ResponseVarName = action.ResponseVarName,
                // 深拷贝子分支
                TrueActions = action.TrueActions?.Select(CloneAction).ToList(),
                FalseActions = action.FalseActions?.Select(CloneAction).ToList()
            };
            return copy;
        }

        private void MoveSelectedUp()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;

            // 嵌套步骤上移
            var parentInfo = FindParentIf(_selectedNodeId, _actions);
            if (parentInfo != null)
            {
                var (parentIf, isTrueBranch) = parentInfo.Value;
                var list = isTrueBranch ? parentIf.TrueActions : parentIf.FalseActions;
                if (list == null) return;
                var action = list.Find(a => a.NodeId == _selectedNodeId);
                if (action == null) return;
                int idx = list.IndexOf(action);
                if (idx <= 0) return;
                list.RemoveAt(idx);
                list.Insert(idx - 1, action);
                SyncToRecorder();
                RenderFlowChart();
                return;
            }

            // 顶层步骤上移
            var topAction = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (topAction == null) return;
            int topIdx = _actions.IndexOf(topAction);
            if (topIdx <= 0) return;
            _actions.RemoveAt(topIdx);
            _actions.Insert(topIdx - 1, topAction);
            for (int i = 0; i < _actions.Count; i++) _actions[i].Order = i + 1;
            SyncToRecorder();
            RenderFlowChart();
        }

        private void MoveSelectedDown()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;

            // 嵌套步骤下移
            var parentInfo = FindParentIf(_selectedNodeId, _actions);
            if (parentInfo != null)
            {
                var (parentIf, isTrueBranch) = parentInfo.Value;
                var list = isTrueBranch ? parentIf.TrueActions : parentIf.FalseActions;
                if (list == null) return;
                var action = list.Find(a => a.NodeId == _selectedNodeId);
                if (action == null) return;
                int idx = list.IndexOf(action);
                if (idx < 0 || idx >= list.Count - 1) return;
                list.RemoveAt(idx);
                list.Insert(idx + 1, action);
                SyncToRecorder();
                RenderFlowChart();
                return;
            }

            // 顶层步骤下移
            var topAction = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (topAction == null) return;
            int topIdx = _actions.IndexOf(topAction);
            if (topIdx < 0 || topIdx >= _actions.Count - 1) return;
            _actions.RemoveAt(topIdx);
            _actions.Insert(topIdx + 1, topAction);
            for (int i = 0; i < _actions.Count; i++) _actions[i].Order = i + 1;
            SyncToRecorder();
            RenderFlowChart();
        }

        private void SyncToRecorder()
        {
            _recorder.ClearNodes();
            foreach (var action in _actions) _recorder.AddManual(action);
        }

        // === Toolbar event handlers ===

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            SyncToRecorder();
            DialogResult = true;
        }

        private void AddStepBtn_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string? typeTag = button?.Tag as string;
            if (string.IsNullOrEmpty(typeTag)) return;

            var newNode = CreateStepByType(typeTag);
            if (newNode == null) return;

            _actions.Add(newNode);
            _selectedNodeId = newNode.NodeId;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        private string? ShowInput(string title, string prompt, string def = "")
        {
            var w = new Window
            {
                Title = title, Width = 320, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };
            var sp = new StackPanel { Margin = new Thickness(15) };
            sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 5) });
            var box = new TextBox { Text = def, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(box);
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(15, 4, 15, 4), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 4, 15, 4), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);
            w.Content = sp;
            ok.Click += (_, _) => { if (string.IsNullOrWhiteSpace(box.Text) && def == "") { MessageBox.Show("内容不能为空"); return; } w.DialogResult = true; };
            return w.ShowDialog() == true ? box.Text : null;
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Min(_zoom + 0.1, 2.0);
            RenderFlowChart();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Max(_zoom - 0.1, 0.3);
            RenderFlowChart();
        }

        private void FitToWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_actions.Count == 0) return;
            _zoom = 1.0;
            var layout = FlowLayoutEngine.LayoutTree(_actions, NodeWidth, NodeHeight, VerticalGap, HorizontalGap, _collapsedIfNodeIds);

            double availableWidth = ActualWidth - 220 - 40;
            double availableHeight = ActualHeight - 110;
            double scaleX = availableWidth > 0 && layout.TotalWidth > 0 ? availableWidth / layout.TotalWidth : 1.0;
            double scaleY = availableHeight > 0 && layout.TotalHeight > 0 ? availableHeight / layout.TotalHeight : 1.0;
            _zoom = Math.Clamp(Math.Min(scaleX, scaleY), 0.3, 2.0);
            RenderFlowChart();
        }

        private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double delta = e.Delta > 0 ? 0.1 : -0.1;
                _zoom = Math.Clamp(_zoom + delta, 0.3, 2.0);
                RenderFlowChart();
                e.Handled = true;
            }
        }

        // === Static helpers ===

        private static string GetActionIcon(ActionType type) => type switch
        {
            ActionType.Click => "⊕",
            ActionType.TypeText => "T",
            ActionType.SendKeys => "K",
            ActionType.Wait => "W",
            ActionType.Copy => "C",
            ActionType.Paste => "P",
            ActionType.InsertText => "I",
            ActionType.Screenshot => "S",
            ActionType.OpenApp => "O",
            ActionType.WaitForApp => "W",
            ActionType.Scroll => "^",
            ActionType.ReadContent => "R",
            ActionType.ScrollRead => "R",
            ActionType.InputParam => "$",
            ActionType.RegexMatch => "M",
            ActionType.If => "?",
            ActionType.Goto => "G",
            ActionType.SwitchToWindow => "W2",
            ActionType.While => "🔁",
            ActionType.Loop => "🔢",
            ActionType.Try => "🛡",
            ActionType.Break => "⏹",
            ActionType.Continue => "⏭",
            ActionType.HttpWait => "⏸",
            ActionType.HttpCall => "🌐",
            _ => "*"
        };

        private static string GetNodeDetail(RecordedAction a)
        {
            if (!string.IsNullOrEmpty(a.Remark)) return a.Remark;
            return a.ActionType switch
            {
                ActionType.Click => a.ClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision
                    ? $"视觉:{a.VisionLabel ?? "button"}"
                    : a.ClickMode == WeChatAutomation.Core.Recording.ClickMode.UIAPath && !string.IsNullOrEmpty(a.XPath)
                        ? a.ElementName ?? "未知"
                        : a.X > 0 || a.Y > 0 ? $"@({a.X:F0},{a.Y:F0})" : "点击",
                ActionType.TypeText or ActionType.InsertText => !string.IsNullOrEmpty(a.Parameter) ? $"\"{a.Parameter}\"" : a.Name,
                ActionType.SendKeys => !string.IsNullOrEmpty(a.Parameter) ? a.Parameter : "按键",
                ActionType.Wait => !string.IsNullOrEmpty(a.Parameter) ? $"{a.Parameter}ms" : "等待",
                ActionType.Scroll => $"{a.ScrollAmount}行",
                ActionType.ScrollRead => $"滚动{a.ScrollAmount}行{(!string.IsNullOrEmpty(a.OutputParamName) ? $" ->{{{a.OutputParamName}}}" : "")}",
                ActionType.ReadContent => !string.IsNullOrEmpty(a.OutputParamName) ? $"->{a.OutputParamName}" : "阅读",
                ActionType.RegexMatch => !string.IsNullOrEmpty(a.RegexPattern) ? a.RegexPattern : "正则",
                ActionType.If => !string.IsNullOrEmpty(a.ConditionExpression) ? a.ConditionExpression
                    : !string.IsNullOrEmpty(a.OutputParamName) ? $"{{{a.OutputParamName}}}?" : "判断",
                ActionType.Goto => !string.IsNullOrEmpty(a.GotoNodeId) ? $"→{a.GotoNodeId[..Math.Min(8, a.GotoNodeId.Length)]}" : "跳转",
                ActionType.SwitchToWindow => !string.IsNullOrEmpty(a.WindowTitle) ? a.WindowTitle : !string.IsNullOrEmpty(a.Parameter) ? a.Parameter : "切窗",
                ActionType.OpenApp => !string.IsNullOrEmpty(a.Parameter) ? a.Parameter : "打开",
                ActionType.InputParam => !string.IsNullOrEmpty(a.ParameterName) ? $"[{a.ParameterName}]" : "参数",
                ActionType.While => (!string.IsNullOrEmpty(a.ConditionExpression) ? a.ConditionExpression : "循环") + $" ×≤{a.MaxLoopCount}",
                ActionType.Loop => $"{a.LoopCount} 次",
                ActionType.Try => $"Try{a.TrueActions?.Count ?? 0}/Catch{a.FalseActions?.Count ?? 0}",
                ActionType.Break => "跳出循环",
                ActionType.Continue => "下一轮",
                ActionType.HttpWait => $"key={a.WaitKey ?? "?"}" + (a.WaitTimeoutMs > 0 ? $" ⏱{a.WaitTimeoutMs}ms" : ""),
                ActionType.HttpCall => $"{a.HttpMethod} {Truncate(a.HttpUrl ?? "", 18)}",
                _ => a.Name
            };
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

        /// <summary>
        /// 返回 If 分支（成立/不成立）行为的可读描述，用于属性面板摘要。
        /// 与 RecordedAction.Summary 的 BranchActionDesc 逻辑保持一致（含旧字段回退）。
        /// </summary>
        private static string BranchBehaviorText(RecordedAction action, bool isTrue)
        {
            IfBranchAction b = isTrue ? action.TrueBranch : action.FalseBranch;
            string? script = isTrue ? action.TrueBranchScript : action.FalseBranchScript;
            if (b == IfBranchAction.Continue)
            {
                if (isTrue)
                {
                    if (!string.IsNullOrEmpty(action.TargetScript)) return $"执行脚本:{Truncate(action.TargetScript, 14)}";
                    if (action.TrueActions?.Count > 0) return $"执行动作x{action.TrueActions.Count}";
                    if (!string.IsNullOrEmpty(action.TrueGotoNodeId)) return "跳转";
                    return "继续主流程";
                }
                else
                {
                    if (!string.IsNullOrEmpty(action.TargetScript)) return "结束脚本";
                    if (action.FalseActions?.Count > 0) return $"执行动作x{action.FalseActions.Count}";
                    if (!string.IsNullOrEmpty(action.GotoNodeId)) return "跳转";
                    return "继续主流程";
                }
            }
            return b switch
            {
                IfBranchAction.RunScript => !string.IsNullOrEmpty(script) ? $"执行脚本:{Truncate(script, 14)}" : "执行脚本?",
                IfBranchAction.RunActions => $"执行动作x{(isTrue ? action.TrueActions?.Count : action.FalseActions?.Count) ?? 0}",
                IfBranchAction.Stop => "结束脚本",
                _ => "继续主流程"
            };
        }

        /// <summary>
        /// 构建 If 分支（成立/不成立）的行为编辑器：行为下拉 + 脚本框（RunScript 时显示）。
        /// 打开时把旧字段自动映射为对应行为；修改后写回新字段。
        /// </summary>
        private FrameworkElement BuildBranchBehaviorEditor(RecordedAction action, bool isTrue)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var bg = isTrue ? Color.FromRgb(232, 245, 233) : Color.FromRgb(253, 233, 233);
            var border = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4, 6, 6),
                Child = panel
            };

            panel.Children.Add(new TextBlock
            {
                Text = isTrue ? "✓ 条件成立时" : "✗ 条件不成立时",
                FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 0, 0, 3)
            });

            // 当前行为（含旧字段自动映射）
            IfBranchAction current = isTrue ? action.TrueBranch : action.FalseBranch;
            string? curScript = isTrue ? action.TrueBranchScript : action.FalseBranchScript;
            if (current == IfBranchAction.Continue)
            {
                if (isTrue)
                {
                    if (!string.IsNullOrEmpty(action.TargetScript)) { current = IfBranchAction.RunScript; curScript = action.TargetScript; }
                    else if (action.TrueActions?.Count > 0) current = IfBranchAction.RunActions;
                }
                else
                {
                    if (!string.IsNullOrEmpty(action.TargetScript)) current = IfBranchAction.Stop;
                    else if (action.FalseActions?.Count > 0) current = IfBranchAction.RunActions;
                }
            }

            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
            combo.Items.Add("继续主流程");
            combo.Items.Add("执行脚本 (后结束)");
            combo.Items.Add("执行动作 (后结束)");
            combo.Items.Add("结束脚本执行");
            combo.SelectedIndex = current switch
            {
                IfBranchAction.RunScript => 1,
                IfBranchAction.RunActions => 2,
                IfBranchAction.Stop => 3,
                _ => 0
            };

            var scriptBox = new TextBox
            {
                Text = curScript ?? "",
                ToolTip = "子脚本名（执行完结束当前脚本）",
                Visibility = combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed
            };
            var actionHint = new TextBlock
            {
                Text = "在下方“+添加步骤到分支”里编辑子动作",
                FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
                Visibility = combo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed
            };

            void Commit()
            {
                var act = combo.SelectedIndex switch
                {
                    1 => IfBranchAction.RunScript,
                    2 => IfBranchAction.RunActions,
                    3 => IfBranchAction.Stop,
                    _ => IfBranchAction.Continue
                };
                string? scr = act == IfBranchAction.RunScript && !string.IsNullOrWhiteSpace(scriptBox.Text) ? scriptBox.Text.Trim() : null;
                if (isTrue) { action.TrueBranch = act; action.TrueBranchScript = scr; }
                else { action.FalseBranch = act; action.FalseBranchScript = scr; }
                SyncToRecorder();
                RenderFlowChart();
            }

            combo.SelectionChanged += (_, _) =>
            {
                scriptBox.Visibility = combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                actionHint.Visibility = combo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                Commit();
            };
            scriptBox.LostFocus += (_, _) => Commit();

            panel.Children.Add(combo);
            panel.Children.Add(scriptBox);
            panel.Children.Add(actionHint);
            return border;
        }

        /// <summary>
        /// 在属性面板追加某个容器节点（If/While/Loop/Try）的一个分支步骤列表 + 添加按钮。
        /// isTrueBranch=true 用 TrueActions，false 用 FalseActions。
        /// </summary>
        private void AppendBranchStepList(StackPanel propsPanel, RecordedAction container, bool isTrueBranch,
            string headerText, Brush headerColor)
        {
            var list = isTrueBranch ? container.TrueActions : container.FalseActions;

            propsPanel.Children.Add(new TextBlock
            {
                Text = $"{headerText}: {list?.Count ?? 0} 步",
                Foreground = headerColor,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 2)
            });

            if (list?.Count > 0)
            {
                foreach (var subAction in list)
                {
                    var subPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 1, 0, 1) };
                    subPanel.Children.Add(new TextBlock
                    {
                        Text = $"  {subAction.ActionType}: {Truncate(subAction.Summary, 20)}",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    var selectBtn = new Button { Content = "选", Padding = new Thickness(3), FontSize = 9, Margin = new Thickness(4, 0, 0, 0), Tag = subAction.NodeId, Cursor = Cursors.Hand };
                    selectBtn.Click += (_, _) => { _selectedNodeId = subAction.NodeId; UpdatePropertiesPanel(); RenderFlowChart(); };
                    subPanel.Children.Add(selectBtn);
                    var delBtn = new Button { Content = "✕", Padding = new Thickness(3), FontSize = 9, Foreground = Brushes.Red, Margin = new Thickness(2, 0, 0, 0), Tag = (container, subAction, isTrueBranch), Cursor = Cursors.Hand };
                    delBtn.Click += DeleteBranchStep_Click;
                    subPanel.Children.Add(delBtn);
                    propsPanel.Children.Add(subPanel);
                }
            }

            var addBtn = new Button
            {
                Content = $"+ 添加步骤到 {headerText}",
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(10, 2, 0, 6),
                FontSize = 10,
                Tag = (container, isTrueBranch),
                Cursor = Cursors.Hand
            };
            addBtn.Click += AddToBranch_Click;
            propsPanel.Children.Add(addBtn);
        }

        /// <summary>
        /// 在属性面板追加折叠/展开按钮（容器节点用）。
        /// </summary>
        private void AppendCollapseToggle(StackPanel propsPanel, RecordedAction container)
        {
            var toggleBtn = new Button
            {
                Content = _collapsedIfNodeIds.Contains(container.NodeId) ? "▼ 展开分支" : "▶ 折叠分支",
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 4, 0, 4),
                FontSize = 10,
                Tag = container.NodeId,
                Cursor = Cursors.Hand
            };
            toggleBtn.Click += ToggleBranchCollapse_Click;
            propsPanel.Children.Add(toggleBtn);
        }
    }
}
