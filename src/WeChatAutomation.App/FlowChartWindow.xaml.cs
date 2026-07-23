using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.App
{
    /// <summary>
    /// FlowChartWindow - visual script workflow editor with snake layout.
    /// Nodes flow top-to-bottom in columns; when a column fills the visible
    /// window height, the next column continues to the right.
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

        // Compact node dimensions
        private const double NodeWidth = 150;
        private const double NodeHeight = 36;
        private const double VerticalGap = 30;   // 行间距（换行时留出连接线空间）
        private const double HorizontalGap = 40; // 同行节点间距（留出箭头和标签空间）

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
        };

        public FlowChartWindow(ActionRecorder recorder, ActionPlayer player, WeChatAutomation.Core.Recording.ClickMode clickMode, string visionModel)
        {
            InitializeComponent();
            _recorder = recorder;
            _player = player;
            _currentClickMode = clickMode;
            _currentVisionModel = visionModel;
            _actions = new List<RecordedAction>(recorder.Nodes);
            // 录制时新节点实时同步到流程图
            _recorder.NodeRecorded += OnNodeRecorded;
            _recorder.RecordingStopped += OnRecordingStopped;
            Loaded += (_, _) => RenderFlowChart();
            SizeChanged += (_, _) => RenderFlowChart();
            Closed += (_, _) => { _recorder.NodeRecorded -= OnNodeRecorded; _recorder.RecordingStopped -= OnRecordingStopped; if (_recorder.IsRecording) _recorder.Stop(); };
        }

        /// <summary>
        /// 录制时新节点加入 _actions 并实时刷新流程图
        /// </summary>
        private void OnNodeRecorded(object? sender, RecordedAction node)
        {
            // 同步 recorder 的最新节点列表（避免副本遗漏）
            _actions = new List<RecordedAction>(_recorder.Nodes);
            // 在 UI 线程刷新（录制回调可能在钩子线程）
            Dispatcher.Invoke(() => RenderFlowChart());
        }

        /// <summary>
        /// 录制停止时恢复窗口并刷新
        /// </summary>
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

        /// <summary>
        /// 切换录制状态
        /// </summary>
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
                    // 录制开始时最小化窗口，避免遮挡操作
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

        // === Layout helpers ===

        // 树形布局：所有节点纵向排列，可无限向下滚动，无需计算每行节点数。

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
                StatusText.Text = "0 steps";
                return;
            }

            // 树形布局：节点纵向排列，分支向左右展开
            var positions = FlowLayoutEngine.Layout(_actions, NodeWidth, NodeHeight, VerticalGap, HorizontalGap, 0);

            // 计算内容尺寸并设置 Canvas（纵向可无限滚动）
            var contentSize = FlowLayoutEngine.MeasureContent(positions, NodeWidth, NodeHeight, 20);
            canvas.Width = Math.Max(contentSize.Width * _zoom, 100);
            canvas.Height = Math.Max(contentSize.Height * _zoom, 100);

            // 计算水平居中偏移：让整棵树居中显示在画布可视宽度内
            double availableWidth = Math.Max(ActualWidth - 220 - 40, 200); // 减去属性面板
            double contentMinX = double.MaxValue, contentMaxX = double.MinValue;
            foreach (var kvp in positions)
            {
                if (kvp.Value.X < contentMinX) contentMinX = kvp.Value.X;
                if (kvp.Value.X > contentMaxX) contentMaxX = kvp.Value.X;
            }
            double contentWidth = (contentMaxX - contentMinX + NodeWidth) * _zoom;
            // 居中：把内容中心对齐到可视区域中心
            // contentCenterX = (contentMinX + NodeWidth/2) * zoom + centerOffset 应等于 availableWidth/2
            double contentCenterX = (contentMinX + contentMaxX) / 2 * _zoom + NodeWidth / 2 * _zoom;
            double centerOffset = availableWidth / 2 - contentCenterX;
            // Canvas 宽度至少要容纳居中后的内容
            canvas.Width = Math.Max(contentWidth, availableWidth);

            // 渲染连接线（在节点下方）
            RenderConnections(canvas, positions, centerOffset);

            // 渲染节点
            foreach (var action in _actions)
            {
                if (!positions.TryGetValue(action.NodeId, out var pos)) continue;
                var nodeControl = CreateNodeControl(action, pos, centerOffset);
                canvas.Children.Add(nodeControl);
            }

            StatusText.Text = $"{_actions.Count} steps | zoom {_zoom:P0}";
        }

        // === Connection lines ===

        private void RenderConnections(Canvas canvas, Dictionary<string, Point> positions, double centerOffset)
        {
            for (int i = 0; i < _actions.Count; i++)
            {
                var action = _actions[i];
                if (!positions.TryGetValue(action.NodeId, out var fromPos)) continue;

                // 顺序连接到下一步
                if (i < _actions.Count - 1)
                {
                    var nextAction = _actions[i + 1];
                    if (positions.TryGetValue(nextAction.NodeId, out var toPos))
                    {
                        bool hasBranch = action.ActionType == ActionType.If
                            && (!string.IsNullOrEmpty(action.TrueGotoNodeId) || !string.IsNullOrEmpty(action.GotoNodeId));
                        bool hasGoto = action.ActionType == ActionType.Goto && !string.IsNullOrEmpty(action.GotoNodeId);

                        if (!hasBranch && !hasGoto)
                            DrawConnection(canvas, fromPos, toPos, Brushes.Gray, false, null, centerOffset);
                    }
                }

                // If 分支连接
                if (action.ActionType == ActionType.If)
                {
                    if (!string.IsNullOrEmpty(action.TrueGotoNodeId) && positions.TryGetValue(action.TrueGotoNodeId, out var truePos))
                        DrawConnection(canvas, fromPos, truePos, new SolidColorBrush(Color.FromRgb(76, 175, 80)), false, "T", centerOffset);
                    if (!string.IsNullOrEmpty(action.GotoNodeId) && positions.TryGetValue(action.GotoNodeId, out var falsePos))
                        DrawConnection(canvas, fromPos, falsePos, new SolidColorBrush(Color.FromRgb(244, 67, 54)), false, "F", centerOffset);

                    if (string.IsNullOrEmpty(action.TrueGotoNodeId) && string.IsNullOrEmpty(action.GotoNodeId) && i < _actions.Count - 1)
                    {
                        if (positions.TryGetValue(_actions[i + 1].NodeId, out var nextPos))
                            DrawConnection(canvas, fromPos, nextPos, Brushes.Gray, false, null, centerOffset);
                    }
                }

                // Goto 连接
                if (action.ActionType == ActionType.Goto && !string.IsNullOrEmpty(action.GotoNodeId))
                {
                    if (positions.TryGetValue(action.GotoNodeId, out var gotoPos))
                        DrawConnection(canvas, fromPos, gotoPos, new SolidColorBrush(Color.FromRgb(0, 188, 212)), true, null, centerOffset);
                }
            }
        }

        /// <summary>
        /// 树形纵向连接：从节点底部中心 -> 下一节点顶部中心。
        /// 同列直线；跨列（分支偏移）用折线绕行。
        /// </summary>
        private void DrawConnection(Canvas canvas, Point from, Point to, Brush stroke, bool dashed, string? label, double centerOffset)
        {
            double fromCX = (from.X + NodeWidth / 2) * _zoom + centerOffset;
            double fromBottomY = (from.Y + NodeHeight) * _zoom;
            double toCX = (to.X + NodeWidth / 2) * _zoom + centerOffset;
            double toTopY = to.Y * _zoom;

            bool crossColumn = Math.Abs(fromCX - toCX) > 2;

            if (!crossColumn)
            {
                // 同列：垂直直线
                AddLine(canvas, fromCX, fromBottomY, toCX, toTopY, stroke, dashed);
            }
            else
            {
                // 跨列：折线 - 底部向下 -> 水平到目标列 -> 向上到目标顶部
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
            }

            // 箭头（向下指）
            var arrow = new Polygon { Fill = stroke };
            arrow.Points.Add(new Point(toCX, toTopY));
            arrow.Points.Add(new Point(toCX - 4, toTopY - 6));
            arrow.Points.Add(new Point(toCX + 4, toTopY - 6));
            canvas.Children.Add(arrow);

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

        private FrameworkElement CreateNodeControl(RecordedAction action, Point pos, double centerOffset)
        {
            Brush color = TypeColors.TryGetValue(action.ActionType, out var c) ? c : Brushes.Gray;

            double w = NodeWidth * _zoom;
            double h = NodeHeight * _zoom;

            var border = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(4),
                BorderBrush = color,
                BorderThickness = new Thickness(1.5),
                Background = action.ActionType == ActionType.If
                    ? new SolidColorBrush(Color.FromRgb(255, 248, 225))
                    : Brushes.White,
                Cursor = Cursors.Hand,
                Tag = action.NodeId,
                ToolTip = $"#{action.Order} [{action.NodeId}] {action.Summary}"
            };

            // Highlight selected node
            if (action.NodeId == _selectedNodeId)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                border.BorderThickness = new Thickness(2.5);
            }

            // Inner layout: left color bar + text
            var dock = new DockPanel();

            // Left color bar (4px)
            var colorBar = new Border
            {
                Width = 4,
                Background = color,
                CornerRadius = new CornerRadius(4, 0, 0, 4)
            };
            DockPanel.SetDock(colorBar, Dock.Left);
            dock.Children.Add(colorBar);

            // Text area
            var textPanel = new StackPanel { Margin = new Thickness(6, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };

            // Line 1: icon + type
            var line1 = new TextBlock
            {
                Text = $"{GetActionIcon(action.ActionType)} {action.ActionType}",
                FontSize = 9 * Math.Max(_zoom, 0.7),
                Foreground = color,
                FontWeight = FontWeights.Bold
            };
            textPanel.Children.Add(line1);

            // Line 2: summary
            var line2 = new TextBlock
            {
                Text = Truncate(action.Summary, 20),
                FontSize = 10 * Math.Max(_zoom, 0.7),
                Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            textPanel.Children.Add(line2);

            dock.Children.Add(textPanel);
            border.Child = dock;

            Canvas.SetLeft(border, pos.X * _zoom + centerOffset);
            Canvas.SetTop(border, pos.Y * _zoom);

            border.MouseLeftButtonDown += Node_MouseDown;

            return border;
        }

        // === Node interaction ===

        private void Node_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var element = sender as FrameworkElement;
            while (element != null && element.Tag == null)
                element = VisualTreeHelper.GetParent(element) as FrameworkElement;
            if (element?.Tag is string nodeId)
            {
                _selectedNodeId = nodeId;
                UpdatePropertiesPanel();
                RenderFlowChart();

                // Double-click to edit
                if (e.ClickCount == 2)
                {
                    EditSelectedStep();
                }
            }
        }

        // === Properties panel ===

        private void UpdatePropertiesPanel()
        {
            var propsPanel = PropsPanel;
            if (propsPanel == null) return;
            propsPanel.Children.Clear();

            var action = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (action == null)
            {
                propsPanel.Children.Add(new TextBlock { Text = "Select a node to view properties", Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                return;
            }

            propsPanel.Children.Add(new TextBlock { Text = $"Step #{action.Order}", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            propsPanel.Children.Add(new TextBlock { Text = $"Type: {action.ActionType}", Margin = new Thickness(0, 0, 0, 3) });
            propsPanel.Children.Add(new TextBlock { Text = $"Name: {action.Name}", Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
            propsPanel.Children.Add(new TextBlock { Text = $"NodeId: {action.NodeId}", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) });

            if (!string.IsNullOrEmpty(action.Parameter))
                propsPanel.Children.Add(new TextBlock { Text = $"Param: {Truncate(action.Parameter, 40)}", Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(action.WindowTitle))
                propsPanel.Children.Add(new TextBlock { Text = $"Window: {action.WindowTitle}", Margin = new Thickness(0, 0, 0, 3) });

            if (action.ActionType == ActionType.If)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "Branch config:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(new TextBlock { Text = $"Condition: {action.ConditionExpression}", Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
                propsPanel.Children.Add(new TextBlock { Text = $"True -> {(action.TrueGotoNodeId ?? "next step")}", Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)), Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(new TextBlock { Text = $"False -> {(action.GotoNodeId ?? "next step")}", Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)), Margin = new Thickness(0, 0, 0, 3) });
            }

            if (action.ActionType == ActionType.Goto)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = $"Goto target: {action.GotoNodeId}", Margin = new Thickness(0, 0, 0, 3) });
            }

            propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });
            var editBtn = new Button { Content = "Edit", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 3) };
            editBtn.Click += (_, _) => EditSelectedStep();
            propsPanel.Children.Add(editBtn);
            var deleteBtn = new Button { Content = "Delete", Padding = new Thickness(10, 4, 10, 4), Foreground = Brushes.Red };
            deleteBtn.Click += (_, _) => DeleteSelectedStep();
            propsPanel.Children.Add(deleteBtn);
        }

        // === Edit / Delete ===

        private void EditSelectedStep()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;
            var action = _actions.Find(a => a.NodeId == _selectedNodeId);
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

            // OK / Cancel buttons
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
            var action = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (action == null) return;
            if (MessageBox.Show($"Delete step #{action.Order}?\n{action.Summary}", "Delete", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _actions.Remove(action);
            // Clean up references to deleted node
            foreach (var a in _actions)
            {
                if (a.TrueGotoNodeId == _selectedNodeId) a.TrueGotoNodeId = null;
                if (a.GotoNodeId == _selectedNodeId) a.GotoNodeId = null;
            }
            // Re-number
            for (int i = 0; i < _actions.Count; i++) _actions[i].Order = i + 1;
            _selectedNodeId = null;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
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
            var newNode = new RecordedAction { Order = _actions.Count + 1, ClickMode = _currentClickMode, ActionType = ActionType.Click, Name = "Click" };

            if (typeTag == "Click") { newNode.ActionType = ActionType.Click; newNode.Name = "Click"; }
            else if (typeTag == "TypeText") { newNode.ActionType = ActionType.TypeText; newNode.Name = "TypeText"; }
            else if (typeTag == "Wait") { newNode.ActionType = ActionType.Wait; newNode.Parameter = "1000"; newNode.Name = "Wait 1000ms"; }
            else if (typeTag == "Screenshot") { newNode.ActionType = ActionType.Screenshot; newNode.Name = "Screenshot"; }
            else if (typeTag == "ReadContent") { newNode.ActionType = ActionType.ReadContent; newNode.Name = "ReadContent"; }
            else if (typeTag == "RegexMatch") { newNode.ActionType = ActionType.RegexMatch; newNode.Name = "RegexMatch"; }
            else if (typeTag == "SwitchToWindow") { newNode.ActionType = ActionType.SwitchToWindow; newNode.Name = "SwitchToWindow"; }
            else if (typeTag == "If") { newNode.ActionType = ActionType.If; newNode.Name = "If"; newNode.ConditionExpression = ""; }
            else if (typeTag == "Goto") { newNode.ActionType = ActionType.Goto; newNode.Name = "Goto"; if (_actions.Count > 0) newNode.GotoNodeId = _actions[0].NodeId; }

            _actions.Add(newNode);
            _selectedNodeId = newNode.NodeId;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
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
            var positions = FlowLayoutEngine.Layout(_actions, NodeWidth, NodeHeight, VerticalGap, HorizontalGap, 0);
            var contentSize = FlowLayoutEngine.MeasureContent(positions, NodeWidth, NodeHeight, 20);

            // 计算最佳缩放：使内容高度适应可视区域（树形纵向布局，宽度通常较窄）
            double availableWidth = ActualWidth - 220 - 40;
            double availableHeight = ActualHeight - 110;
            double scaleX = availableWidth > 0 && contentSize.Width > 0 ? availableWidth / contentSize.Width : 1.0;
            double scaleY = availableHeight > 0 && contentSize.Height > 0 ? availableHeight / contentSize.Height : 1.0;
            _zoom = Math.Clamp(Math.Min(scaleX, scaleY), 0.3, 2.0);
            RenderFlowChart();
        }

        // === Helpers ===

        private static string GetActionIcon(ActionType type) => type switch
        {
            ActionType.Click => ">",
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
            _ => "*"
        };

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;
    }
}
