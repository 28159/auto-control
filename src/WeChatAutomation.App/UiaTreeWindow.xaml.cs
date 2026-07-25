using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FlaUI.UIA3;
using WeChatAutomation.Core.Native;
using WeChatAutomation.Core.Recording;
using AutomationElement = FlaUI.Core.AutomationElements.AutomationElement;

namespace WeChatAutomation.App
{
    /// <summary>
    /// 交互式 UIA 树查看器（调试）：左侧 TreeView 浏览元素窗口的 UIA 树（懒加载任意深度），
    /// 右侧显示选中节点属性与 XPath，可复制。UIA3Automation 实例由本窗口持有，关闭时释放。
    /// </summary>
    public class UiaTreeWindow : Window
    {
        private readonly UIA3Automation _uia;
        private readonly IntPtr _hwnd;          // 窗口根句柄，供刷新用
        private readonly TreeView _tree = new();
        private readonly TextBox _propBox = new();
        private UiaTreeNode? _root;

        public UiaTreeWindow(UIA3Automation uia, AutomationElement root, IntPtr hwnd)
        {
            _uia = uia;
            _hwnd = hwnd;
            Title = "UIA 树查看器（调试）";
            Width = 900; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current.MainWindow;

            var grid = new Grid { Margin = new Thickness(8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 顶部工具条
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var refreshBtn = new Button { Content = "🔄 刷新根节点", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "重新从窗口句柄加载 UIA 树" };
            refreshBtn.Click += (_, _) => RefreshRoot();
            toolbar.Children.Add(refreshBtn);
            var copyXpBtn = new Button { Content = "复制 XPath", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
            copyXpBtn.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(_lastXPath)) { try { Clipboard.SetText(_lastXPath); } catch { } }
            };
            toolbar.Children.Add(copyXpBtn);
            var copyAllBtn = new Button { Content = "复制全部属性", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
            copyAllBtn.Click += (_, _) => { try { Clipboard.SetText(_propBox.Text); } catch { } };
            toolbar.Children.Add(copyAllBtn);
            var hint = new TextBlock { Text = "  提示：展开节点懒加载子层；选中节点看属性/XPath", Foreground = Brushes.Gray, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            toolbar.Children.Add(hint);
            Grid.SetRow(toolbar, 0); Grid.SetColumnSpan(toolbar, 2);
            grid.Children.Add(toolbar);

            // 左：TreeView
            BuildTreeTemplate();
            _tree.BorderThickness = new Thickness(1);
            _tree.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            _tree.Background = Brushes.White;
            _tree.SelectedItemChanged += Tree_SelectedItemChanged;
            Grid.SetRow(_tree, 1); Grid.SetColumn(_tree, 0);
            grid.Children.Add(_tree);

            // 右：属性面板
            var rightSp = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            rightSp.Children.Add(new TextBlock { Text = "选中节点属性:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            _propBox.IsReadOnly = true;
            _propBox.TextWrapping = TextWrapping.Wrap;
            _propBox.FontFamily = new FontFamily("Consolas");
            _propBox.FontSize = 11;
            _propBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _propBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _propBox.Height = 480;
            _propBox.Text = "（选中左侧节点查看属性）";
            rightSp.Children.Add(_propBox);
            Grid.SetRow(rightSp, 1); Grid.SetColumn(rightSp, 1);
            grid.Children.Add(rightSp);

            Content = grid;

            // 初始化根
            _root = new UiaTreeNode(root);
            _root.LoadChildren();   // 预加载首层
            _tree.Items.Add(_root);
            _tree.Loaded += (_, _) =>
            {
                if (_tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem rootItem)
                    rootItem.IsExpanded = true;
            };

            Closed += (_, _) => { try { _uia?.Dispose(); } catch { } };
        }

        private string _lastXPath = "";

        // 用代码构建 HierarchicalDataTemplate（ItemsSource=Children，文本绑 DisplayText）
        private void BuildTreeTemplate()
        {
            var hdt = new HierarchicalDataTemplate(typeof(UiaTreeNode));
            hdt.ItemsSource = new Binding(nameof(UiaTreeNode.Children));
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding(nameof(UiaTreeNode.DisplayText)));
            tb.SetValue(TextBlock.FontSizeProperty, 12.0);
            tb.SetValue(TextBlock.MarginProperty, new Thickness(2, 1, 2, 1));
            hdt.VisualTree = tb;
            _tree.Resources.Add(new DataTemplateKey(typeof(UiaTreeNode)), hdt);

            // 展开事件触发懒加载
            _tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnItemExpanded));
        }

        private static void OnItemExpanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is UiaTreeNode node)
                node.LoadChildren();
        }

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is UiaTreeNode node) UpdatePropertyPanel(node);
        }

        private void UpdatePropertyPanel(UiaTreeNode node)
        {
            var sb = new StringBuilder();
            string ct = "", name = "", cls = "", autoId = "", rect = "", hwndStr = "";
            try { ct = node.Element?.ControlType.ToString() ?? ""; } catch { }
            try { name = node.Element?.Name ?? ""; } catch { }
            try { cls = node.Element?.ClassName ?? ""; } catch { }
            try { autoId = node.Element?.AutomationId ?? ""; } catch { }
            try
            {
                var r = node.Element?.BoundingRectangle;
                if (r.HasValue) rect = $"X={r.Value.X:F0} Y={r.Value.Y:F0} W={r.Value.Width:F0} H={r.Value.Height:F0}";
            } catch { }
            try
            {
                var nh = node.Element?.Properties.NativeWindowHandle.Value;
                if (nh.HasValue) hwndStr = $"0x{nh.Value:X}";
            } catch { }

            _lastXPath = "";
            try { _lastXPath = XPathBuilder.BuildXPath(node.Element); } catch { }

            sb.AppendLine($"ControlType: {ct}");
            sb.AppendLine($"Name: {name}");
            sb.AppendLine($"ClassName: {cls}");
            sb.AppendLine($"AutomationId: {autoId}");
            sb.AppendLine($"BoundingRect: {rect}");
            sb.AppendLine($"NativeWindowHandle: {hwndStr}");
            sb.AppendLine($"XPath:");
            sb.AppendLine(_lastXPath ?? "(无)");
            _propBox.Text = sb.ToString();
        }

        // 重新从窗口句柄加载根（窗口内容变化/激活失效时用）
        private void RefreshRoot()
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                UIATreeActivator.Activate(_hwnd);
                var root = _uia.FromHandle(_hwnd);
                if (root == null) { _propBox.Text = "刷新失败：FromHandle 返回空"; return; }
                _tree.Items.Clear();
                _root = new UiaTreeNode(root);
                _root.LoadChildren();
                _tree.Items.Add(_root);
                if (_tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem rootItem)
                    rootItem.IsExpanded = true;
                _propBox.Text = "（已刷新，选中节点查看属性）";
            }
            catch (Exception ex) { _propBox.Text = "刷新异常: " + ex.Message; }
        }

        /// <summary>UIA 树节点（懒加载子节点）。</summary>
        private sealed class UiaTreeNode : INotifyPropertyChanged
        {
            public AutomationElement? Element { get; }
            public ObservableCollection<UiaTreeNode> Children { get; } = new();

            private string _displayText = "";
            public string DisplayText { get => _displayText; private set { _displayText = value; OnProp(); } }

            private bool _loaded;        // 子节点已加载
            private readonly bool _isDummy;

            public UiaTreeNode(AutomationElement? element, bool isDummy = false)
            {
                Element = element;
                _isDummy = isDummy;
                if (isDummy) { DisplayText = "  加载中…"; _loaded = true; return; }
                ReadDisplay();
                // 占位子节点：让展开箭头显示，展开时 LoadChildren 替换
                Children.Add(new UiaTreeNode(null, isDummy: true));
            }

            private void ReadDisplay()
            {
                string ct = "", name = "";
                try { ct = Element?.ControlType.ToString() ?? ""; } catch { }
                try { name = Element?.Name ?? ""; } catch { }
                if (name.Length > 40) name = name[..37] + "…";
                DisplayText = string.IsNullOrEmpty(name) ? ct : $"{ct}  Name='{name}'";
                if (string.IsNullOrEmpty(DisplayText)) DisplayText = "(空元素)";
            }

            // 展开（TreeViewItem.Expanded 事件）时调用：加载真实子节点
            public void LoadChildren()
            {
                if (_loaded || _isDummy || Element == null) return;
                _loaded = true;
                Children.Clear();
                try
                {
                    foreach (var k in Element.FindAllChildren())
                        Children.Add(new UiaTreeNode(k));
                }
                catch { }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnProp([CallerMemberName] string n = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}
