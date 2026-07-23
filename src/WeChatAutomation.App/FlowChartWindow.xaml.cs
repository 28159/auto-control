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

            // Line 2: 关键参数（按类型提取）
            var line2 = new TextBlock
            {
                Text = Truncate(GetNodeDetail(action), 22),
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

            // 名称（可编辑）
            propsPanel.Children.Add(MakeEditField("名称:", action.Name ?? "", v => { action.Name = v; }));
            // 窗口标题（按类型显隐）
            if (ShowWindowTitle(action.ActionType))
                propsPanel.Children.Add(MakeEditField("窗口标题:", action.WindowTitle ?? "", v => { action.WindowTitle = v; }, "留空=当前前台窗口"));
            // 参数（按类型显隐）
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

            // 点击模式（仅点击）
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

            // If 分支配置
            if (action.ActionType == ActionType.If)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(new TextBlock { Text = "分支配置:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
                propsPanel.Children.Add(MakeEditField("条件表达式:", action.ConditionExpression ?? "", v => { action.ConditionExpression = v; }, "留空则按输出变量是否有值判断"));
                propsPanel.Children.Add(MakeEditField("成立时执行脚本:", action.TargetScript ?? "", v => { action.TargetScript = string.IsNullOrWhiteSpace(v) ? null : v; }, "条件成立时执行的子脚本名"));
                propsPanel.Children.Add(MakeTargetCombo("True 跳转:", action.TrueGotoNodeId, _actions, true, v => { action.TrueGotoNodeId = v; }));
                propsPanel.Children.Add(MakeTargetCombo("False 跳转:", action.GotoNodeId, _actions, true, v => { action.GotoNodeId = v; }));
            }

            // Goto 目标
            if (action.ActionType == ActionType.Goto)
            {
                propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
                propsPanel.Children.Add(MakeTargetCombo("跳转目标:", action.GotoNodeId, _actions, true, v => { action.GotoNodeId = v; }));
            }

            // 备注（可编辑）
            propsPanel.Children.Add(MakeEditField("备注:", action.Remark ?? "", v => { action.Remark = string.IsNullOrWhiteSpace(v) ? null : v; }, "步骤说明/备注"));

            // 操作按钮
            propsPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });
            var btnRow1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            btnRow1.Children.Add(MakeBtn("✏ 编辑", null, EditSelectedStep));
            btnRow1.Children.Add(MakeBtn("📋 复制", new Thickness(5, 0, 0, 0), DuplicateSelectedStep));
            propsPanel.Children.Add(btnRow1);
            var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            btnRow2.Children.Add(MakeBtn("↑ 上移", null, MoveSelectedUp));
            btnRow2.Children.Add(MakeBtn("↓ 下移", new Thickness(5, 0, 0, 0), MoveSelectedDown));
            propsPanel.Children.Add(btnRow2);
            propsPanel.Children.Add(MakeBtn("🗑 删除", null, DeleteSelectedStep, Brushes.Red));
        }

        // 构建可编辑字段：失焦时写回并同步刷新
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

        // 构建分支/跳转目标下拉：显示"#N 摘要"，第一项"(下一步)"
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
            if (MessageBox.Show($"确定删除步骤 #{action.Order}？\n{action.Summary}", "删除", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
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

        /// <summary>复制选中步骤，插入到其后方（深拷贝，新 NodeId）。</summary>
        private void DuplicateSelectedStep()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;
            var action = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (action == null) return;
            var copy = new RecordedAction
            {
                ActionType = action.ActionType,
                Name = action.Name,
                ClassName = action.ClassName, ElementName = action.ElementName, AutomationId = action.AutomationId,
                ControlType = action.ControlType, WindowTitle = action.WindowTitle,
                X = action.X, Y = action.Y, ClickMode = action.ClickMode,
                VisionLabel = action.VisionLabel, VisionConfThreshold = action.VisionConfThreshold,
                XPath = action.XPath, SiblingIndex = action.SiblingIndex, RuntimeId = action.RuntimeId,
                Parameter = action.Parameter, DelayMs = action.DelayMs, ScrollAmount = action.ScrollAmount,
                ParameterName = action.ParameterName, DefaultValue = action.DefaultValue, IsRequired = action.IsRequired,
                CopyToClipboard = action.CopyToClipboard,
                RegexPattern = action.RegexPattern, RegexGroup = action.RegexGroup, OutputParamName = action.OutputParamName,
                ConditionExpression = action.ConditionExpression, GotoNodeId = action.GotoNodeId, TrueGotoNodeId = action.TrueGotoNodeId,
                TargetScript = action.TargetScript, SwitchAll = action.SwitchAll, IsEnabled = action.IsEnabled,
                Remark = action.Remark
            };
            int idx = _actions.IndexOf(action);
            _actions.Insert(idx + 1, copy);
            _selectedNodeId = copy.NodeId;
            for (int i = 0; i < _actions.Count; i++) _actions[i].Order = i + 1;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        private void MoveSelectedUp()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;
            var action = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (action == null) return;
            int idx = _actions.IndexOf(action);
            if (idx <= 0) return;
            _actions.RemoveAt(idx);
            _actions.Insert(idx - 1, action);
            for (int i = 0; i < _actions.Count; i++) _actions[i].Order = i + 1;
            SyncToRecorder();
            RenderFlowChart();
        }

        private void MoveSelectedDown()
        {
            if (string.IsNullOrEmpty(_selectedNodeId)) return;
            var action = _actions.Find(a => a.NodeId == _selectedNodeId);
            if (action == null) return;
            int idx = _actions.IndexOf(action);
            if (idx < 0 || idx >= _actions.Count - 1) return;
            _actions.RemoveAt(idx);
            _actions.Insert(idx + 1, action);
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

            var newNode = new RecordedAction { Order = _actions.Count + 1, ClickMode = _currentClickMode, ActionType = ActionType.Click, Name = "点击" };

            // 需要即填内容的类型：先弹输入框，取消则不添加
            switch (typeTag)
            {
                case "Click": newNode.ActionType = ActionType.Click; newNode.Name = "点击"; break;
                case "TypeText":
                    {
                        var t = ShowInput("输入文本", "内容:");
                        if (t == null) return;
                        newNode.ActionType = ActionType.TypeText; newNode.Parameter = t;
                        newNode.Name = t.Length > 12 ? "输入\"" + t[..12] + "...\"" : "输入\"" + t + "\"";
                        break;
                    }
                case "SendKeys":
                    {
                        var k = ShowInput("按键", "如 Enter, Ctrl+A:", "Enter");
                        if (k == null) return;
                        newNode.ActionType = ActionType.SendKeys; newNode.Parameter = k; newNode.Name = "按键 " + k;
                        break;
                    }
                case "Copy": newNode.ActionType = ActionType.Copy; newNode.Name = "复制"; break;
                case "Paste": newNode.ActionType = ActionType.Paste; newNode.Name = "粘贴"; break;
                case "InsertText":
                    {
                        var t = ShowInput("插入文本", "内容:");
                        if (t == null) return;
                        newNode.ActionType = ActionType.InsertText; newNode.Parameter = t;
                        newNode.Name = t.Length > 12 ? "插入\"" + t[..12] + "...\"" : "插入\"" + t + "\"";
                        break;
                    }
                case "Wait":
                    {
                        var ms = ShowInput("等待", "毫秒:", "1000");
                        if (!int.TryParse(ms, out int v)) return;
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
                        if (!int.TryParse(n, out int v)) return;
                        newNode.ActionType = ActionType.ScrollRead; newNode.ScrollAmount = v; newNode.Parameter = v.ToString(); newNode.Name = "滚动阅读" + v + "行";
                        break;
                    }
                case "RegexMatch": newNode.ActionType = ActionType.RegexMatch; newNode.Name = "正则识别"; break;
                case "Scroll":
                    {
                        var n = ShowInput("滚动", "行数(正=下 负=上):", "3");
                        if (!int.TryParse(n, out int v)) return;
                        newNode.ActionType = ActionType.Scroll; newNode.ScrollAmount = v; newNode.Parameter = v.ToString(); newNode.Name = "滚动" + v + "行";
                        break;
                    }
                case "InputParam": newNode.ActionType = ActionType.InputParam; newNode.ParameterName = "参数1"; newNode.Name = "输入参数"; break;
                case "If": newNode.ActionType = ActionType.If; newNode.Name = "判断"; newNode.ConditionExpression = ""; break;
                case "Goto": newNode.ActionType = ActionType.Goto; newNode.Name = "跳转"; if (_actions.Count > 0) newNode.GotoNodeId = _actions[0].NodeId; break;
                case "SwitchToWindow": newNode.ActionType = ActionType.SwitchToWindow; newNode.Name = "切窗"; break;
                default: return;
            }

            _actions.Add(newNode);
            _selectedNodeId = newNode.NodeId;
            SyncToRecorder();
            RenderFlowChart();
            UpdatePropertiesPanel();
        }

        /// <summary>轻量单字段输入框，返回输入文本；用户取消返回 null。</summary>
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

        /// <summary>Ctrl+滚轮缩放画布；普通滚轮垂直滚动。</summary>
        private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double delta = e.Delta > 0 ? 0.1 : -0.1;
                _zoom = Math.Clamp(_zoom + delta, 0.3, 2.0);
                RenderFlowChart();
                e.Handled = true;
            }
            // 非 Ctrl 时让 ScrollViewer 正常垂直滚动
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

        /// <summary>按类型提取节点关键参数用于显示。若有备注则优先显示备注。</summary>
        private static string GetNodeDetail(RecordedAction a)
        {
            if (!string.IsNullOrEmpty(a.Remark)) return a.Remark;
            return a.ActionType switch
        {
            ActionType.Click => a.ClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision
                ? $"视觉:{a.VisionLabel ?? "button"}"
                : a.ClickMode == WeChatAutomation.Core.Recording.ClickMode.UIAPath && !string.IsNullOrEmpty(a.XPath)
                    ? $"路径:{a.ElementName ?? "未知"}"
                    : a.X > 0 || a.Y > 0 ? $"@({a.X:F0},{a.Y:F0})" : "点击",
            ActionType.TypeText or ActionType.InsertText => !string.IsNullOrEmpty(a.Parameter) ? $"\"{a.Parameter}\"" : a.Name,
            ActionType.SendKeys => !string.IsNullOrEmpty(a.Parameter) ? a.Parameter : "按键",
            ActionType.Wait => !string.IsNullOrEmpty(a.Parameter) ? $"{a.Parameter}ms" : "等待",
            ActionType.Scroll => $"{a.ScrollAmount}行",
            ActionType.ScrollRead => $"滚动{a.ScrollAmount}行{( !string.IsNullOrEmpty(a.OutputParamName) ? $" ->{{{a.OutputParamName}}}" : "")}",
            ActionType.ReadContent => !string.IsNullOrEmpty(a.OutputParamName) ? $"->{a.OutputParamName}" : "阅读",
            ActionType.RegexMatch => !string.IsNullOrEmpty(a.RegexPattern) ? a.RegexPattern : "正则",
            ActionType.If => !string.IsNullOrEmpty(a.ConditionExpression) ? a.ConditionExpression
                : !string.IsNullOrEmpty(a.OutputParamName) ? $"{{{a.OutputParamName}}}?" : "判断",
            ActionType.Goto => !string.IsNullOrEmpty(a.GotoNodeId) ? $"→{a.GotoNodeId[..Math.Min(8, a.GotoNodeId.Length)]}" : "跳转",
            ActionType.SwitchToWindow => !string.IsNullOrEmpty(a.WindowTitle) ? a.WindowTitle : !string.IsNullOrEmpty(a.Parameter) ? a.Parameter : "切窗",
            ActionType.OpenApp => !string.IsNullOrEmpty(a.Parameter) ? a.Parameter : "打开",
            ActionType.InputParam => !string.IsNullOrEmpty(a.ParameterName) ? $"[{a.ParameterName}]" : "参数",
            _ => a.Name
        };
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;
    }
}
