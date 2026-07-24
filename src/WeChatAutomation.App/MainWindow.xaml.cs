using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using FlaUIAutomation = FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using WeChatAutomation.Core.Native;
using WeChatAutomation.Core.Recording;
using WeChatAutomation.Core.Services;

namespace WeChatAutomation.App
{
    public partial class MainWindow : Window
    {
        // ═══ 缓存画刷（避免每次状态更新创建新实例） ═══
        private static readonly SolidColorBrush _statusOnBrush = new(Color.FromRgb(76, 175, 80));
        private static readonly SolidColorBrush _statusOffBrush = new(Color.FromRgb(204, 204, 204));
        private static readonly SolidColorBrush _toggleOnBrush = new(Color.FromRgb(229, 57, 53));
        private static readonly SolidColorBrush _toggleOffBrush = new(Color.FromRgb(76, 175, 80));
        private static readonly SolidColorBrush _branchTrueBrush = new(Color.FromRgb(76, 175, 80));
        private static readonly SolidColorBrush _branchFalseBrush = new(Color.FromRgb(244, 67, 54));
        private static readonly SolidColorBrush _inspectActiveBrush = new(Color.FromRgb(0x4A, 0x14, 0x8C));
        private static readonly SolidColorBrush _inspectInactiveBrush = new(Color.FromRgb(0x7B, 0x1F, 0xA2));
        private static readonly SolidColorBrush _ifTrueBgBrush = new(Color.FromRgb(232, 245, 233));
        private static readonly SolidColorBrush _ifFalseBgBrush = new(Color.FromRgb(253, 233, 233));

        private readonly ActionRecorder _recorder = new();
        private readonly ActionPlayer _player = new();
        private readonly ObservableCollection<RecordedAction> _steps = new();
        private readonly ObservableCollection<ScriptInfo> _scripts = new();
        private readonly KeyboardHook _hotkeyHook = new();
        private string _scriptsDir;
        private ScriptInfo _currentScript;
        private WeChatAutomation.Core.Recording.ClickMode _currentClickMode = WeChatAutomation.Core.Recording.ClickMode.Coordinate;

        public MainWindow()
        {
            InitializeComponent();

            // XAML 初始化期间 ClickModeRadioButton_Changed 可能在控件未完全创建时触发，
            // 这里确保所有控件就绪后正确设置可见性
            UpdateClickModeUI();

            StepsTree.ItemsSource = BuildStepTree();
            SelectAllCheckBox.Checked += SelectAllCheckBox_Changed;
            SelectAllCheckBox.Unchecked += SelectAllCheckBox_Changed;
            ScriptsListBox.ItemsSource = _scripts;

            try { User32.SetProcessDpiAwareness(2); } catch { try { User32.SetProcessDPIAware(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DPI 设置失败: {ex.Message}"); } }

            _scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
            Directory.CreateDirectory(_scriptsDir);

            _recorder.NodeRecorded += (s, n) => Dispatcher.BeginInvoke(RefreshStepTree);
            _recorder.RecordingStarted += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                StartBtn.IsEnabled = false; StopBtn.IsEnabled = true;
                StatusDot.Fill = Brushes.Red;
                StatusText.Text = "录制中";
            });
            _recorder.RecordingStopped += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                StartBtn.IsEnabled = true; StopBtn.IsEnabled = false;
                PlayBtn.IsEnabled = _steps.Count > 0;
                StatusDot.Fill = Brushes.Gray; StatusText.Text = "就绪";

            });
            _recorder.LogMessage += (s, msg) => AppendLog(msg);

            _player.LogMessage += (s, msg) => Dispatcher.BeginInvoke(() => AppendLog(msg));
            _player.PlayCompleted += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                StopPlayBtn.IsEnabled = false; PlayBtn.IsEnabled = _steps.Count > 0;
                UpdateReadContentDisplay();
                AppendLog("回放完成");
            });
            _player.PlayError += (s, msg) => Dispatcher.BeginInvoke(() =>
            {
                StopPlayBtn.IsEnabled = false; PlayBtn.IsEnabled = _steps.Count > 0;
                AppendLog($"回放错误: {msg}");
            });

            _hotkeyHook.HotKeyPressed += (s, vk) => Dispatcher.BeginInvoke(() => OnHotKey(vk));
            _hotkeyHook.StartCapture();

            LoadScriptsList();
            AppendLog("F9录制 F10确认 F11回放 F12检查 | 双击步骤可编辑");
            UpdateClickModeUI();

            // 初始化服务状态显示
            Dispatcher.BeginInvoke(() => UpdateServiceStatus(), System.Windows.Threading.DispatcherPriority.Background);

            // 任务列表定时刷新
            var taskRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            taskRefreshTimer.Tick += (_, _) => RefreshTaskHistory();
            taskRefreshTimer.Start();
        }

        // ═══ 快捷键 ═══
        private bool _isInspecting = false; // 窗口检查模式状态
        private void OnHotKey(int vk)
        {
            if (vk == User32.VK_F9) { if (_recorder.IsRecording) StopRecording(); else StartRecording(); }
            else if (vk == User32.VK_F10) _recorder.ConfirmStep();
            else if (vk == User32.VK_F11) { if (_player.IsPlaying) _player.Stop(); else _ = PlayAll(); }
            else if (vk == User32.VK_F12) { if (_isInspecting) StopInspect(); else StartInspect(); }
            else if (vk == User32.VK_F8) { if (_isTreeCapturing) StopTreeCapture(); else StartTreeCapture(); }
        }

        // ═══ 录制 ═══
        private void StartBtn_Click(object s, RoutedEventArgs e) => StartRecording();
        private void StopBtn_Click(object s, RoutedEventArgs e) => StopRecording();
        private void StartRecording()
        {
            if (_recorder.IsRecording) return;
            _player.SetTargetWindow(IntPtr.Zero); // 开始录制时清除之前的阅读目标
            _recorder.CurrentClickMode = _currentClickMode;
            _recorder.Start(RecordMode.Continuous);
        }
        private void StopRecording() { if (_recorder.IsRecording) _recorder.Stop(); }

        // ═══ 点击模式 ═══
        private void ClickModeRadioButton_Changed(object s, RoutedEventArgs e)
        {
            if (ClickModeCoordinate == null) return;
            if (ClickModeCoordinate.IsChecked == true)
                _currentClickMode = WeChatAutomation.Core.Recording.ClickMode.Coordinate;
            else if (ClickModeUIAPath.IsChecked == true)
                _currentClickMode = WeChatAutomation.Core.Recording.ClickMode.UIAPath;
            else if (ClickModeVision.IsChecked == true)
                _currentClickMode = WeChatAutomation.Core.Recording.ClickMode.Vision;
            UpdateClickModeUI();
        }

        private void UpdateClickModeUI()
        {
            if (ClickModeCoordinate == null) return;
            switch (_currentClickMode)
            {
                case WeChatAutomation.Core.Recording.ClickMode.Coordinate:
                    ClickModeCoordinate.IsChecked = true;
                    break;
                case WeChatAutomation.Core.Recording.ClickMode.UIAPath:
                    ClickModeUIAPath.IsChecked = true;
                    break;
                case WeChatAutomation.Core.Recording.ClickMode.Vision:
                    ClickModeVision.IsChecked = true;
                    break;
            }
        }


        private void ToggleClickMode_Click(object s, RoutedEventArgs e)
        {
            if (GetSelectedAction() is not RecordedAction n) { MessageBox.Show("请先选择步骤"); return; }
            if (n.ActionType != ActionType.Click) { MessageBox.Show("只能切换点击步骤的模式"); return; }
            n.ClickMode = n.ClickMode switch
            {
                WeChatAutomation.Core.Recording.ClickMode.Coordinate => WeChatAutomation.Core.Recording.ClickMode.UIAPath,
                WeChatAutomation.Core.Recording.ClickMode.UIAPath => WeChatAutomation.Core.Recording.ClickMode.Vision,
                _ => WeChatAutomation.Core.Recording.ClickMode.Coordinate
            };
            n.Name = n.ClickMode switch
            {
                WeChatAutomation.Core.Recording.ClickMode.Coordinate => $"点击坐标({n.X:F0},{n.Y:F0})",
                WeChatAutomation.Core.Recording.ClickMode.Vision => $"视觉点击 {n.VisionLabel ?? "模板"}",
                _ => $"点击路径 {n.ElementName ?? n.ClassName ?? n.AutomationId ?? "未知"}"
            };
            RefreshStepTree();
            AppendLog($"步骤 #{n.Order} 切换为 {n.ClickMode} 模式");
        }
        // ═══ 新增步骤 ═══
        private void AddClick_Click(object s, RoutedEventArgs e) => AddOrRun(ActionType.Click, name: "点击");
        private void AddText_Click(object s, RoutedEventArgs e)
        {
            var t = ShowInput("输入文本", "内容:"); if (t != null) AddOrRun(ActionType.TypeText, t, t.Length > 15 ? t[..15] + "..." : t);
        }
        private void AddKeys_Click(object s, RoutedEventArgs e)
        {
            var k = ShowInput("按键", "如 Enter, Ctrl+A:"); if (k != null) AddOrRun(ActionType.SendKeys, k, k);
        }
        private void AddCopy_Click(object s, RoutedEventArgs e) => AddOrRun(ActionType.Copy, name: "复制");
        private void AddPaste_Click(object s, RoutedEventArgs e) => AddOrRun(ActionType.Paste, name: "粘贴");

        // ═══ 新增步骤（任意可用） ═══
        private void AddWait_Click(object s, RoutedEventArgs e)
        {
            var ms = ShowInput("等待", "毫秒:", "1000");
            if (int.TryParse(ms, out int v)) AddOrRun(ActionType.Wait, v.ToString(), $"等待{v}ms");
        }
        private void AddInsert_Click(object s, RoutedEventArgs e)
        {
            var t = ShowInput("插入文本", "内容:"); if (t != null) AddOrRun(ActionType.InsertText, t, t.Length > 15 ? t[..15] + "..." : t);
        }
        private void AddScreenshot_Click(object s, RoutedEventArgs e) => AddOrRun(ActionType.Screenshot, name: "截图");
        private void AddOpenApp_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择程序", Filter = "可执行文件|*.exe;*.bat;*.cmd;*.lnk|所有文件|*.*" };
            if (dlg.ShowDialog() == true) AddOrRun(ActionType.OpenApp, dlg.FileName, $"打开 {Path.GetFileNameWithoutExtension(dlg.FileName)}");
        }
        private void AddWaitForApp_Click(object s, RoutedEventArgs e)
        {
            var name = ShowProcessPicker();
            if (string.IsNullOrEmpty(name)) return;
            var timeout = ShowInput("超时(毫秒)", "超时:", "30000");
            if (int.TryParse(timeout, out int ms)) AddOrRun(ActionType.WaitForApp, name, $"等待 {name}", delayMs: ms);
        }

        private string ShowProcessPicker()
        {
            var w = new Window
            {
                Title = "选择等待的应用", Width = 380, Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(12) };

            sp.Children.Add(new TextBlock { Text = "选择已运行的进程，或手动输入进程名:", Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.Bold });

            // 进程下拉框
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), DisplayMemberPath = "Display" };
            var processes = System.Diagnostics.Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle) || p.ProcessName is not ("Idle" or "System" or "csrss" or "smss" or "lsass" or "wininit" or "services" or "svchost" or "dwm" or "conhost"))
                .OrderBy(p => p.ProcessName)
                .Select(p => new { Display = $"{p.ProcessName} - {p.MainWindowTitle}", Name = p.ProcessName })
                .DistinctBy(p => p.Name)
                .ToList();
            combo.ItemsSource = processes;
            if (processes.Count > 0) combo.SelectedIndex = 0;
            sp.Children.Add(combo);

            sp.Children.Add(new TextBlock { Text = "或手动输入进程名:", Margin = new Thickness(0, 0, 0, 3) });
            var manualBox = new TextBox { Margin = new Thickness(0, 0, 0, 8), ToolTip = "如 notepad, chrome, WeChat" };
            sp.Children.Add(manualBox);

            // 按钮
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

            w.Content = sp;

            string result = null;
            ok.Click += (_, _) =>
            {
                // 优先使用手动输入
                if (!string.IsNullOrWhiteSpace(manualBox.Text))
                    result = manualBox.Text.Trim();
                else if (combo.SelectedItem != null)
                    result = ((dynamic)combo.SelectedItem).Name;
                w.DialogResult = true;
            };

            w.ShowDialog();
            return result;
        }
        private void AddReadContent_Click(object s, RoutedEventArgs e)
        {
            if (!PickTargetWindow("请切换到目标窗口（3秒后读取）")) return;
            AddOrRun(ActionType.ReadContent, name: "阅读窗口");
        }
        private void AddScrollRead_Click(object s, RoutedEventArgs e)
        {
            var n = ShowInput("滚动阅读", "行数:", "5");
            if (!int.TryParse(n, out int v)) return;
            if (!PickTargetWindow("请切换到目标窗口（3秒后读取）")) return;
            AddOrRun(ActionType.ScrollRead, v.ToString(), $"滚动阅读{v}行");
        }
        private void AddScroll_Click(object s, RoutedEventArgs e)
        {
            var n = ShowInput("滚动", "行数(正=下 负=上):", "3");
            if (int.TryParse(n, out int v)) AddOrRun(ActionType.Scroll, v.ToString(), $"滚动{v}行");
        }

        private void AddRegexMatch_Click(object s, RoutedEventArgs e)
        {
            var node = ShowRegexMatchDialog();
            if (node == null) return;
            InsertAfterSelected(node);
            AppendLog($"已添加正则识别步骤: {node.RegexPattern}");
        }

        private RecordedAction ShowRegexMatchDialog(RecordedAction existing = null)
        {
            var w = new Window
            {
                Title = existing != null ? "编辑正则识别" : "添加正则识别",
                Width = 480,
                Height = 490,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };

            sp.Children.Add(new TextBlock { Text = "目标窗口标题 (留空=当前窗口):", Margin = new Thickness(0, 0, 0, 3) });
            var windowTitleBox = new TextBox { Text = existing?.WindowTitle ?? "", Margin = new Thickness(0, 0, 0, 8), ToolTip = "如: 微信, 记事本" };
            sp.Children.Add(windowTitleBox);

            sp.Children.Add(new TextBlock { Text = "正则表达式:", Margin = new Thickness(0, 0, 0, 3), FontWeight = FontWeights.Bold });
            var patternBox = new TextBox
            {
                Text = existing?.RegexPattern ?? "",
                Margin = new Thickness(0, 0, 0, 3),
                ToolTip = "如: 金额[：:]\\s*([\\d.]+) 或 \\d{4}-\\d{2}-\\d{2}"
            };
            sp.Children.Add(patternBox);

            sp.Children.Add(new TextBlock { Text = "提取分组 (留空=整条匹配, 数字=第N组, 字符串=命名组):", Margin = new Thickness(0, 0, 0, 3) });
            var groupBox = new TextBox { Text = existing?.RegexGroup ?? "", Margin = new Thickness(0, 0, 0, 8), ToolTip = "如: 1 或 amount" };
            sp.Children.Add(groupBox);

            var templatePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            var templateLabel = new TextBlock { Text = "常用:", VerticalAlignment = VerticalAlignment.Center, FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 4, 0) };
            templatePanel.Children.Add(templateLabel);

            var templates = new (string Label, string Pattern, string Group)[]
            {
                ("金额", @"金额[：:]\s*([\d.]+)", "1"),
                ("日期", @"(\d{4}[-/]\d{2}[-/]\d{2})", "1"),
                ("手机号", @"(1[3-9]\d{9})", "1"),
                ("邮箱", @"([\w.+-]+@[\w-]+\.[\w.]+)", "1"),
                ("姓名", @"姓名[：:]\s*(\S+)", "1"),
                ("数字", @"([\d.]+)", "1"),
                ("身份证", @"(\d{17}[\dXx])", "1"),
            };

            foreach (var t in templates)
            {
                var btn = new Button
                {
                    Content = t.Label,
                    Padding = new Thickness(4, 1, 4, 1),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 2, 0),
                    Tag = (t.Pattern, t.Group)
                };
                btn.Click += (_, _) =>
                {
                    var (p, g) = ((string, string))btn.Tag;
                    patternBox.Text = p;
                    groupBox.Text = g;
                };
                templatePanel.Children.Add(btn);
            }
            sp.Children.Add(templatePanel);

            sp.Children.Add(new TextBlock { Text = "输出变量名 (匹配值存入变量，后续步骤用 {变量名} 引用):", Margin = new Thickness(0, 0, 0, 3) });
            var outputBox = new TextBox { Text = existing?.OutputParamName ?? "", Margin = new Thickness(0, 0, 0, 8), ToolTip = "如: price, date" };
            sp.Children.Add(outputBox);

            var copyToClipboardBox = new CheckBox
            {
                Content = "复制匹配值到剪切板",
                IsChecked = existing?.CopyToClipboard ?? false,
                Margin = new Thickness(0, 0, 0, 8)
            };
            sp.Children.Add(copyToClipboardBox);

            var testBtn = new Button { Content = "测试匹配", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(testBtn);

            var testResult = new TextBlock
            {
                Text = "",
                Foreground = Brushes.DarkGreen,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 60
            };
            sp.Children.Add(testResult);

            testBtn.Click += (_, _) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(patternBox.Text))
                    {
                        testResult.Text = "请输入正则表达式";
                        testResult.Foreground = Brushes.Red;
                        return;
                    }

                    IntPtr hwnd = IntPtr.Zero;
                    if (!string.IsNullOrEmpty(windowTitleBox.Text))
                    {
                        hwnd = User32.FindWindowByTitle(windowTitleBox.Text);
                    }
                    if (hwnd == IntPtr.Zero) hwnd = User32.GetForegroundWindow();
                    if (hwnd == IntPtr.Zero)
                    {
                        testResult.Text = "无法获取目标窗口";
                        testResult.Foreground = Brushes.Red;
                        return;
                    }

                    var uia = new UIA3Automation();
                    var element = uia.FromHandle(hwnd);
                    var content = ExtractTestContent(element);

                    var regex = new System.Text.RegularExpressions.Regex(
                        patternBox.Text,
                        System.Text.RegularExpressions.RegexOptions.Multiline |
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var matches = regex.Matches(content);

                    if (matches.Count == 0)
                    {
                        testResult.Text = $"未匹配 (内容前100字: {(content.Length > 100 ? content[..100] : content)})";
                        testResult.Foreground = Brushes.Orange;
                    }
                    else
                    {
                        string value;
                        if (!string.IsNullOrEmpty(groupBox.Text))
                        {
                            if (int.TryParse(groupBox.Text, out int gi) && gi < matches[0].Groups.Count)
                                value = matches[0].Groups[gi].Value;
                            else
                                value = matches[0].Groups[groupBox.Text]?.Value ?? matches[0].Value;
                        }
                        else
                        {
                            value = matches.Count == 1 ? matches[0].Value : $"{matches.Count}处匹配: {matches[0].Value} ...";
                        }
                        testResult.Text = $"匹配 {matches.Count} 处: {value}";
                        testResult.Foreground = Brushes.DarkGreen;
                    }
                }
                catch (Exception ex)
                {
                    testResult.Text = $"错误: {ex.Message}";
                    testResult.Foreground = Brushes.Red;
                }
            };

            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok);
            bp.Children.Add(cancel);
            sp.Children.Add(bp);

            w.Content = sp;

            RecordedAction result = null;
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(patternBox.Text))
                {
                    MessageBox.Show("请输入正则表达式");
                    return;
                }

                try
                {
                    _ = new System.Text.RegularExpressions.Regex(patternBox.Text);
                }
                catch (System.Text.RegularExpressions.RegexParseException ex)
                {
                    MessageBox.Show($"正则表达式语法错误:\n{ex.Message}");
                    return;
                }

                result = new RecordedAction
                {
                    ActionType = ActionType.RegexMatch,
                    Name = $"正则识别 {patternBox.Text}",
                    WindowTitle = windowTitleBox.Text.Trim(),
                    RegexPattern = patternBox.Text.Trim(),
                    RegexGroup = groupBox.Text.Trim(),
                    OutputParamName = outputBox.Text.Trim(),
                    CopyToClipboard = copyToClipboardBox.IsChecked == true,
                    DelayMs = 300
                };
                w.DialogResult = true;
            };

            w.ShowDialog();
            return result;
        }

        private string ExtractTestContent(FlaUIAutomation.AutomationElement element, int depth = 0, int maxDepth = 10)
        {
            if (depth > maxDepth || element == null) return "";
            var texts = new List<string>();
            try
            {
                string name = "";
                try { name = element.Name ?? ""; } catch { }
                if (!string.IsNullOrWhiteSpace(name)) texts.Add(name);
                try
                {
                    var valuePattern = element.Patterns.Value.PatternOrDefault;
                    if (valuePattern != null)
                    {
                        string val = valuePattern.Value.Value ?? "";
                        if (!string.IsNullOrWhiteSpace(val) && val != name) texts.Add(val);
                    }
                }
                catch { }
                // TextPattern 用于富文本控件（聊天消息等）
                try
                {
                    var textPattern = element.Patterns.Text.PatternOrDefault;
                    if (textPattern != null)
                    {
                        var documentRange = textPattern.DocumentRange;
                        if (documentRange != null)
                        {
                            string text = documentRange.GetText(int.MaxValue);
                            if (!string.IsNullOrWhiteSpace(text) && text != name) texts.Add(text);
                        }
                    }
                }
                catch { }
                var children = element.FindAllChildren();
                foreach (var child in children)
                {
                    string childText = ExtractTestContent(child, depth + 1, maxDepth);
                    if (!string.IsNullOrWhiteSpace(childText)) texts.Add(childText);
                }
            }
            catch { }
            return string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        private void AddOrRun(ActionType type, string parameter = "", string name = "", int delayMs = -1)
        {
            RecordedAction node;
            if (type == ActionType.Click)
            {
                node = new RecordedAction
                {
                    Order = _steps.Count + 1,
                    ActionType = ActionType.Click,
                    Name = name,
                    Parameter = parameter,
                    DelayMs = delayMs >= 0 ? delayMs : 0,
                    ClickMode = _currentClickMode
                };
            }
            else
            {
                node = new RecordedAction
                {
                    Order = _steps.Count + 1,
                    ActionType = type,
                    Name = name,
                    Parameter = parameter,
                    ParameterName = type == ActionType.InputParam ? parameter : null,
                    DelayMs = delayMs >= 0 ? delayMs : 0
                };
            }
            InsertAfterSelected(node);
        }

        /// <summary>
        /// 将新动作插入到当前选中步骤之后；若未选中则追加到末尾。
        /// 插入后选中新节点，便于连续在尾部追加。
        /// </summary>
        private void InsertAfterSelected(RecordedAction node)
        {
            string? afterId = GetSelectedAction()?.NodeId;
            _recorder.InsertAfter(afterId, node);
            RefreshStepTree();
            PlayBtn.IsEnabled = _steps.Count > 0;
            if (afterId != null) SelectTreeNodeByActionId(node.NodeId);
        }

        // ═══ 步骤操作 ═══
        private void MoveUp_Click(object s, RoutedEventArgs e)
        {
            if (GetSelectedAction() is RecordedAction n && n.Order > 1)
            { _recorder.MoveNode(n.NodeId, n.Order - 1); RefreshStepTree(); }
        }
        private void MoveDown_Click(object s, RoutedEventArgs e)
        {
            if (GetSelectedAction() is RecordedAction n && n.Order < _steps.Count)
            { _recorder.MoveNode(n.NodeId, n.Order + 1); RefreshStepTree(); }
        }

        // 可内联编辑的列：双击进入单元格编辑；其他列双击打开完整编辑对话框
        private static readonly HashSet<string> EditableHeaders = new() { "名称", "参数", "正则", "延时" };

        // 双击编辑
        private void StepsTree_MouseDoubleClick(object s, MouseButtonEventArgs e)
        {
            EditStep();
        }

        private void Edit_Click(object s, RoutedEventArgs e) => EditStep();

        private void EditStep()
        {
            if (GetSelectedAction() is not RecordedAction n) return;
            ShowEditDialog(n);
        }

        private void ShowEditDialog(RecordedAction node)
        {
            if (node.ActionType == ActionType.InputParam)
            {
                ShowEditInputParamDialog(node);
                return;
            }

            if (node.ActionType == ActionType.RegexMatch)
            {
                ShowEditRegexMatchDialog(node);
                return;
            }

            if (node.ActionType == ActionType.If)
            {
                ShowEditIfDialog(node);
                return;
            }

            if (node.ActionType == ActionType.Goto)
            {
                ShowEditGotoDialog(node);
                return;
            }

            var w = new Window
            {
                Title = $"编辑步骤 #{node.Order}",
                Width = 400, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };

            sp.Children.Add(new TextBlock { Text = "类型:", Margin = new Thickness(0, 0, 0, 3) });
            var typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            foreach (ActionType t in Enum.GetValues(typeof(ActionType)))
                typeCombo.Items.Add(t.ToString());
            typeCombo.SelectedItem = node.ActionType.ToString();
            sp.Children.Add(typeCombo);

            sp.Children.Add(new TextBlock { Text = "自定义名称 (可自由填写，留空显示自动说明):", Margin = new Thickness(0, 0, 0, 3) });
            var nameBox = new TextBox { Text = node.DisplayName ?? "", Margin = new Thickness(0, 0, 0, 8), ToolTip = "你自己给步骤起的名字，流程图和列表显示它" };
            sp.Children.Add(nameBox);

            // 目标窗口标题（按类型显隐）
            var winTitlePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            winTitlePanel.Children.Add(new TextBlock { Text = "目标窗口标题:", Margin = new Thickness(0, 0, 0, 3) });
            var windowTitleBox = new TextBox { Text = node.WindowTitle ?? "", ToolTip = "留空=当前前台窗口；切窗步骤填进程名或窗口标题" };
            winTitlePanel.Children.Add(windowTitleBox);
            sp.Children.Add(winTitlePanel);

            // 参数（按类型显隐）
            var paramPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            paramPanel.Children.Add(new TextBlock { Text = "参数:", Margin = new Thickness(0, 0, 0, 3) });
            var paramBox = new TextBox { Text = node.Parameter ?? "", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 80 };
            paramPanel.Children.Add(paramBox);
            sp.Children.Add(paramPanel);

            // 滚动行数（Scroll/ScrollRead）
            var scrollPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            scrollPanel.Children.Add(new TextBlock { Text = "滚动行数(正=下 负=上):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var scrollBox = new TextBox { Text = node.ScrollAmount.ToString(), Width = 80 };
            scrollPanel.Children.Add(scrollBox);
            sp.Children.Add(scrollPanel);

            // 输出变量名（阅读/滚动阅读/正则识别步骤存内容；点击步骤存点击是否成功 true/false）
            var outputVarPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            outputVarPanel.Children.Add(new TextBlock { Text = "输出变量名 (阅读/正则存内容，点击存成功状态 true/false；留空=不存变量):", Margin = new Thickness(0, 0, 0, 3), FontSize = 11 });
            var outputVarBox = new TextBox { Text = node.OutputParamName ?? "", ToolTip = "阅读/正则: 内容/匹配值存入此变量；点击: 成功存 true 失败存 false。后续判断步骤可用 {变量名} 引用。点击结果同时写入固定变量 {last_click_success}" };
            outputVarPanel.Children.Add(outputVarBox);
            sp.Children.Add(outputVarPanel);

            // 打开全部同名窗口（仅切窗步骤用）
            var switchAllCheck = new CheckBox
            {
                Content = "打开全部同名窗口（切窗步骤用，恢复显示所有匹配窗口）",
                IsChecked = node.SwitchAll,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "勾选后切窗会把该进程名的所有窗口都恢复显示并置顶；不勾选只切换主窗口"
            };
            sp.Children.Add(switchAllCheck);

            // 延时（按类型显隐）
            var delayPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            delayPanel.Children.Add(new TextBlock { Text = "延时(毫秒):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var delayBox = new TextBox { Text = node.DelayMs.ToString(), Width = 100 };
            delayPanel.Children.Add(delayBox);
            sp.Children.Add(delayPanel);

            // 点击模式（仅点击步骤）
            var clickModePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            clickModePanel.Children.Add(new TextBlock { Text = "点击模式:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var clickModeCombo = new ComboBox { Width = 120 };
            clickModeCombo.Items.Add("Coordinate");
            clickModeCombo.Items.Add("UIAPath");
            clickModeCombo.Items.Add("Vision");
            clickModeCombo.SelectedItem = node.ClickMode.ToString();
            clickModePanel.Children.Add(clickModeCombo);
            sp.Children.Add(clickModePanel);

            // 视觉标签+匹配度阈值（点击+视觉模式）
            var visionPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            visionPanel.Children.Add(new TextBlock { Text = "标签:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var visionLabelBox = new TextBox { Text = node.VisionLabel ?? "", Width = 120, ToolTip = "视觉步骤标签名（仅用于显示/日志，不影响匹配）" };
            visionPanel.Children.Add(visionLabelBox);
            visionPanel.Children.Add(new TextBlock { Text = "匹配阈值:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) });
            var visionConfBox = new TextBox { Text = node.VisionConfThreshold > 0 ? node.VisionConfThreshold.ToString() : "0.7", Width = 50, ToolTip = "模板匹配度阈值 0~1，默认 0.7，越低越宽松" };
            visionPanel.Children.Add(visionConfBox);
            sp.Children.Add(visionPanel);

            // 模板图片路径（视觉模式）
            var tplPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            tplPanel.Children.Add(new TextBlock { Text = "模板:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var tplBox = new TextBox { Text = node.TemplateImage ?? "", Width = 260, ToolTip = "模板图片路径（录制时自动截取，可手动替换）" };
            tplPanel.Children.Add(tplBox);
            var tplBrowseBtn = new Button { Content = "浏览...", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
            tplBrowseBtn.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择模板图片", Filter = "图片文件|*.png;*.bmp;*.jpg;*.jpeg" };
                if (dlg.ShowDialog() == true) tplBox.Text = dlg.FileName;
            };
            tplPanel.Children.Add(tplBrowseBtn);
            sp.Children.Add(tplPanel);

            // 坐标 X/Y（点击+非视觉模式）
            var coordPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            coordPanel.Children.Add(new TextBlock { Text = "坐标 X:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var xBox = new TextBox { Text = node.X.ToString("F0"), Width = 60, Margin = new Thickness(0, 0, 10, 0) };
            coordPanel.Children.Add(xBox);
            coordPanel.Children.Add(new TextBlock { Text = "Y:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var yBox = new TextBox { Text = node.Y.ToString("F0"), Width = 60 };
            coordPanel.Children.Add(yBox);
            sp.Children.Add(coordPanel);

            // 循环条件（While）
            var whileCondPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            whileCondPanel.Children.Add(new TextBlock { Text = "循环条件 (While，留空按变量有值):", Margin = new Thickness(0, 0, 0, 3) });
            var whileCondBox = new TextBox { Text = node.ConditionExpression ?? "", ToolTip = "如 {count} > 0；条件成立时重复执行循环体" };
            whileCondPanel.Children.Add(whileCondBox);
            sp.Children.Add(whileCondPanel);

            // 最大迭代次数（While）
            var maxLoopPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            maxLoopPanel.Children.Add(new TextBlock { Text = "最大迭代次数 (防死循环):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var maxLoopBox = new TextBox { Text = node.MaxLoopCount.ToString(), Width = 100 };
            maxLoopPanel.Children.Add(maxLoopBox);
            sp.Children.Add(maxLoopPanel);

            // 循环次数（Loop）
            var loopCountPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            loopCountPanel.Children.Add(new TextBlock { Text = "循环次数 (Loop):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var loopCountBox = new TextBox { Text = node.LoopCount.ToString(), Width = 100 };
            loopCountPanel.Children.Add(loopCountBox);
            sp.Children.Add(loopCountPanel);

            // 等待 key + 超时（HttpWait）
            var waitKeyPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            waitKeyPanel.Children.Add(new TextBlock { Text = "等待唯一 key (外部 POST /api/wait/{key} 唤醒):", Margin = new Thickness(0, 0, 0, 3) });
            var waitKeyBox = new TextBox { Text = node.WaitKey ?? "", ToolTip = "外部 HTTP 请求需带此 key 唤醒该步骤" };
            waitKeyPanel.Children.Add(waitKeyBox);
            sp.Children.Add(waitKeyPanel);

            var waitTimeoutPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            waitTimeoutPanel.Children.Add(new TextBlock { Text = "等待超时(ms, 0=无限):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var waitTimeoutBox = new TextBox { Text = node.WaitTimeoutMs.ToString(), Width = 100 };
            waitTimeoutPanel.Children.Add(waitTimeoutBox);
            sp.Children.Add(waitTimeoutPanel);

            // HTTP 调用字段（HttpCall）
            var httpUrlPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            httpUrlPanel.Children.Add(new TextBlock { Text = "URL (支持 {变量} 占位):", Margin = new Thickness(0, 0, 0, 3) });
            var httpUrlBox = new TextBox { Text = node.HttpUrl ?? "" };
            httpUrlPanel.Children.Add(httpUrlBox);
            sp.Children.Add(httpUrlPanel);

            var httpMethodPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            httpMethodPanel.Children.Add(new TextBlock { Text = "方法:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var httpMethodCombo = new ComboBox { Width = 120 };
            httpMethodCombo.Items.Add("GET"); httpMethodCombo.Items.Add("POST"); httpMethodCombo.Items.Add("PUT"); httpMethodCombo.Items.Add("DELETE");
            httpMethodCombo.SelectedItem = string.IsNullOrEmpty(node.HttpMethod) ? "GET" : node.HttpMethod;
            httpMethodPanel.Children.Add(httpMethodCombo);
            sp.Children.Add(httpMethodPanel);

            var httpHeadersPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            httpHeadersPanel.Children.Add(new TextBlock { Text = "请求头 (每行 Key: Value):", Margin = new Thickness(0, 0, 0, 3) });
            var httpHeadersBox = new TextBox { Text = node.HttpHeaders ?? "", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 60 };
            httpHeadersPanel.Children.Add(httpHeadersBox);
            sp.Children.Add(httpHeadersPanel);

            var httpBodyPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            httpBodyPanel.Children.Add(new TextBlock { Text = "请求体 (支持 {变量} 占位):", Margin = new Thickness(0, 0, 0, 3) });
            var httpBodyBox = new TextBox { Text = node.HttpBody ?? "", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 60 };
            httpBodyPanel.Children.Add(httpBodyBox);
            sp.Children.Add(httpBodyPanel);

            // 响应/传入写入变量名（HttpWait/HttpCall）
            var respVarPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            respVarPanel.Children.Add(new TextBlock { Text = "写入变量名 (HttpWait 传入 / HttpCall 响应):", Margin = new Thickness(0, 0, 0, 3) });
            var respVarBox = new TextBox { Text = node.ResponseVarName ?? "", ToolTip = "内容写入该变量，供后续步骤 {变量名} 引用" };
            respVarPanel.Children.Add(respVarBox);
            sp.Children.Add(respVarPanel);

            // 按类型/点击模式显隐字段
            void ApplyFieldVisibility()
            {
                if (!Enum.TryParse<ActionType>(typeCombo.SelectedItem?.ToString(), out var t))
                    t = node.ActionType;
                Enum.TryParse<WeChatAutomation.Core.Recording.ClickMode>(clickModeCombo.SelectedItem?.ToString(), out var cm);

                bool isClick = t == ActionType.Click;
                winTitlePanel.Visibility = (isClick || t == ActionType.ReadContent || t == ActionType.ScrollRead
                    || t == ActionType.RegexMatch || t == ActionType.SwitchToWindow) ? Visibility.Visible : Visibility.Collapsed;
                paramPanel.Visibility = (t == ActionType.TypeText || t == ActionType.SendKeys || t == ActionType.Wait
                    || t == ActionType.InsertText || t == ActionType.OpenApp || t == ActionType.WaitForApp) ? Visibility.Visible : Visibility.Collapsed;
                scrollPanel.Visibility = (t == ActionType.Scroll || t == ActionType.ScrollRead) ? Visibility.Visible : Visibility.Collapsed;
                outputVarPanel.Visibility = (isClick || t == ActionType.ReadContent || t == ActionType.ScrollRead || t == ActionType.RegexMatch) ? Visibility.Visible : Visibility.Collapsed;
                switchAllCheck.Visibility = t == ActionType.SwitchToWindow ? Visibility.Visible : Visibility.Collapsed;
                delayPanel.Visibility = (isClick || t == ActionType.WaitForApp || t == ActionType.OpenApp
                    || t == ActionType.TypeText || t == ActionType.SendKeys || t == ActionType.InsertText
                    || t == ActionType.Scroll || t == ActionType.ScrollRead) ? Visibility.Visible : Visibility.Collapsed;
                clickModePanel.Visibility = isClick ? Visibility.Visible : Visibility.Collapsed;
                visionPanel.Visibility = (isClick && cm == WeChatAutomation.Core.Recording.ClickMode.Vision) ? Visibility.Visible : Visibility.Collapsed;
                coordPanel.Visibility = (isClick && cm != WeChatAutomation.Core.Recording.ClickMode.Vision) ? Visibility.Visible : Visibility.Collapsed;

                // 控制流 / HTTP 专属字段
                whileCondPanel.Visibility = t == ActionType.While ? Visibility.Visible : Visibility.Collapsed;
                maxLoopPanel.Visibility = t == ActionType.While ? Visibility.Visible : Visibility.Collapsed;
                loopCountPanel.Visibility = t == ActionType.Loop ? Visibility.Visible : Visibility.Collapsed;
                waitKeyPanel.Visibility = t == ActionType.HttpWait ? Visibility.Visible : Visibility.Collapsed;
                waitTimeoutPanel.Visibility = t == ActionType.HttpWait ? Visibility.Visible : Visibility.Collapsed;
                httpUrlPanel.Visibility = t == ActionType.HttpCall ? Visibility.Visible : Visibility.Collapsed;
                httpMethodPanel.Visibility = t == ActionType.HttpCall ? Visibility.Visible : Visibility.Collapsed;
                httpHeadersPanel.Visibility = t == ActionType.HttpCall ? Visibility.Visible : Visibility.Collapsed;
                httpBodyPanel.Visibility = t == ActionType.HttpCall ? Visibility.Visible : Visibility.Collapsed;
                respVarPanel.Visibility = (t == ActionType.HttpWait || t == ActionType.HttpCall) ? Visibility.Visible : Visibility.Collapsed;
                // 备注框也隐藏 tplPanel（模板仅点击视觉用），其余保持
                tplPanel.Visibility = (isClick && cm == WeChatAutomation.Core.Recording.ClickMode.Vision) ? Visibility.Visible : Visibility.Collapsed;
            }

            typeCombo.SelectionChanged += (_, _) => ApplyFieldVisibility();
            clickModeCombo.SelectionChanged += (_, _) => ApplyFieldVisibility();
            ApplyFieldVisibility();

            // 备注（始终显示）
            sp.Children.Add(new TextBlock { Text = "备注:", Margin = new Thickness(0, 0, 0, 3) });
            var remarkBox = new TextBox { Text = node.Remark ?? "", Margin = new Thickness(0, 0, 0, 8), ToolTip = "步骤说明/备注，不影响执行" };
            sp.Children.Add(remarkBox);

            // 按钮
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

            w.Content = sp;

            ok.Click += (_, _) =>
            {
                if (Enum.TryParse<ActionType>(typeCombo.SelectedItem?.ToString(), out var newType))
                    node.ActionType = newType;
                node.DisplayName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim();
                node.WindowTitle = windowTitleBox.Text.Trim();
                node.Parameter = paramBox.Text;
                if (int.TryParse(delayBox.Text, out int d)) node.DelayMs = d;
                if (int.TryParse(scrollBox.Text, out int sa)) node.ScrollAmount = sa;
                if (double.TryParse(xBox.Text, out double x)) node.X = x;
                if (double.TryParse(yBox.Text, out double y)) node.Y = y;
                if (Enum.TryParse<WeChatAutomation.Core.Recording.ClickMode>(clickModeCombo.SelectedItem?.ToString(), out var cm)) node.ClickMode = cm;
                node.VisionLabel = visionLabelBox.Text.Trim();
                if (float.TryParse(visionConfBox.Text, out float vc) && vc > 0) node.VisionConfThreshold = vc;
                node.TemplateImage = string.IsNullOrWhiteSpace(tplBox.Text) ? null : tplBox.Text.Trim();
                node.OutputParamName = string.IsNullOrWhiteSpace(outputVarBox.Text) ? null : outputVarBox.Text.Trim();
                node.SwitchAll = switchAllCheck.IsChecked == true;
                // 控制流 / HTTP 字段
                node.ConditionExpression = string.IsNullOrWhiteSpace(whileCondBox.Text) ? node.ConditionExpression : whileCondBox.Text.Trim();
                if (node.ActionType == ActionType.While && string.IsNullOrWhiteSpace(whileCondBox.Text)) node.ConditionExpression = null;
                if (int.TryParse(maxLoopBox.Text, out int ml) && ml > 0) node.MaxLoopCount = ml;
                if (int.TryParse(loopCountBox.Text, out int lc) && lc > 0) node.LoopCount = lc;
                node.WaitKey = string.IsNullOrWhiteSpace(waitKeyBox.Text) ? null : waitKeyBox.Text.Trim();
                if (int.TryParse(waitTimeoutBox.Text, out int wt)) node.WaitTimeoutMs = wt;
                node.HttpUrl = string.IsNullOrWhiteSpace(httpUrlBox.Text) ? null : httpUrlBox.Text.Trim();
                node.HttpMethod = httpMethodCombo.SelectedItem?.ToString() ?? "GET";
                node.HttpHeaders = string.IsNullOrWhiteSpace(httpHeadersBox.Text) ? null : httpHeadersBox.Text;
                node.HttpBody = string.IsNullOrWhiteSpace(httpBodyBox.Text) ? null : httpBodyBox.Text;
                node.ResponseVarName = string.IsNullOrWhiteSpace(respVarBox.Text) ? null : respVarBox.Text.Trim();
                node.Remark = string.IsNullOrWhiteSpace(remarkBox.Text) ? null : remarkBox.Text.Trim();
                w.DialogResult = true;
            };

            if (w.ShowDialog() == true)
            {
                RefreshStepTree();
                AppendLog($"编辑步骤 #{node.Order}: {node.ActionType} {node.Name}");
            }
        }

        private void ShowEditIfDialog(RecordedAction node)
        {
            if (ShowIfDialogCore(node, isNew: false))
            {
                RefreshStepTree();
                AppendLog($"编辑判断步骤 #{node.Order}");
            }
        }

        /// <summary>
        /// 构建单个分支（成立/不成立）的行为配置面板：行为下拉 + 脚本下拉（仅 RunScript 时可见）。
        /// </summary>
        private StackPanel BuildBranchConfigPanel(string title, RecordedAction node, bool isTrue,
            out ComboBox actionCombo, out ComboBox scriptCombo)
        {
            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 0, 0, 4)
            });

            // 行为下拉
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
            combo.Items.Add("继续主流程");
            combo.Items.Add("执行脚本 (后结束)");
            combo.Items.Add("执行动作 (后结束)");
            combo.Items.Add("结束脚本执行");
            actionCombo = combo;

            // 脚本下拉（RunScript 时显示）
            var scCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 0), IsEditable = true, Visibility = Visibility.Collapsed };
            scCombo.Items.Add("(无)");
            foreach (var sc in _scripts)
                scCombo.Items.Add(sc.Name);
            scriptCombo = scCombo;

            // 旧数据转换：若新字段为默认 Continue，根据旧字段推断当前应显示的行为
            IfBranchAction current = isTrue ? node.TrueBranch : node.FalseBranch;
            string? currentScript = isTrue ? node.TrueBranchScript : node.FalseBranchScript;
            if (current == IfBranchAction.Continue)
            {
                if (isTrue)
                {
                    // 旧 TargetScript 模式：成立->执行子脚本；不成立->停止
                    if (!string.IsNullOrEmpty(node.TargetScript))
                    {
                        current = IfBranchAction.RunScript;
                        currentScript = node.TargetScript;
                    }
                    else if (node.TrueActions?.Count > 0)
                    {
                        current = IfBranchAction.RunActions;
                    }
                    else if (!string.IsNullOrEmpty(node.TrueGotoNodeId))
                    {
                        current = IfBranchAction.Continue; // 旧跳转由 Continue 回退处理
                    }
                }
                else
                {
                    // 旧 TargetScript 模式下不成立->停止
                    if (!string.IsNullOrEmpty(node.TargetScript))
                    {
                        current = IfBranchAction.Stop;
                    }
                    else if (node.FalseActions?.Count > 0)
                    {
                        current = IfBranchAction.RunActions;
                    }
                    else if (!string.IsNullOrEmpty(node.GotoNodeId))
                    {
                        current = IfBranchAction.Continue; // 旧跳转由 Continue 回退处理
                    }
                }
            }

            combo.SelectedIndex = current switch
            {
                IfBranchAction.Continue => 0,
                IfBranchAction.RunScript => 1,
                IfBranchAction.RunActions => 2,
                IfBranchAction.Stop => 3,
                _ => 0
            };

            // 选中脚本
            int scSel = 0;
            if (!string.IsNullOrEmpty(currentScript))
            {
                for (int i = 0; i < _scripts.Count; i++)
                {
                    if (_scripts[i].Name == currentScript) { scSel = i + 1; break; }
                }
                if (scSel == 0) { scCombo.Items.Add(currentScript); scSel = scCombo.Items.Count - 1; }
            }
            scCombo.SelectedIndex = scSel;

            // 执行动作时的提示
            var actionHint = new TextBlock
            {
                Text = "选择'执行动作'后，可在【流程图】窗口中编辑本分支的子动作",
                FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            // 行为切换时联动：脚本下拉仅 RunScript 可见，提示仅 RunActions 可见
            void UpdateVisibility()
            {
                scCombo.Visibility = combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                actionHint.Visibility = combo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            }
            combo.SelectionChanged += (_, _) => UpdateVisibility();
            UpdateVisibility();

            panel.Children.Add(combo);
            panel.Children.Add(actionHint);
            panel.Children.Add(scCombo);

            return panel;
        }

        /// <summary>
        /// 从分支配置面板读取行为与脚本，写回 node 的 TrueBranch/TrueBranchScript 或 FalseBranch/FalseBranchScript。
        /// </summary>
        private static void ReadBranchConfig(ComboBox actionCombo, ComboBox scriptCombo, RecordedAction node, bool isTrue)
        {
            IfBranchAction action = actionCombo.SelectedIndex switch
            {
                1 => IfBranchAction.RunScript,
                2 => IfBranchAction.RunActions,
                3 => IfBranchAction.Stop,
                _ => IfBranchAction.Continue
            };

            string? script = null;
            if (action == IfBranchAction.RunScript)
            {
                script = scriptCombo.SelectedIndex > 0
                    ? (scriptCombo.SelectedItem as string ?? scriptCombo.Text.Trim())
                    : scriptCombo.Text.Trim();
                if (script == "(无)" || string.IsNullOrEmpty(script)) script = null;
            }

            if (isTrue)
            {
                node.TrueBranch = action;
                node.TrueBranchScript = script;
            }
            else
            {
                node.FalseBranch = action;
                node.FalseBranchScript = script;
            }
        }

        private void ShowEditGotoDialog(RecordedAction node)
        {
            if (_steps.Count == 0) { AppendLog("没有步骤可跳转"); return; }

            var w = new Window
            {
                Title = $"编辑跳转步骤 #{node.Order}",
                Width = 350, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };

            sp.Children.Add(new TextBlock { Text = "跳转目标:", Margin = new Thickness(0, 0, 0, 3) });
            var targetCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            int sel = 0;
            for (int i = 0; i < _steps.Count; i++)
            {
                targetCombo.Items.Add($"#{_steps[i].Order} [{_steps[i].NodeId}] {_steps[i].Summary}");
                if (_steps[i].NodeId == node.GotoNodeId) sel = i;
            }
            targetCombo.SelectedIndex = sel;
            sp.Children.Add(targetCombo);

            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

            w.Content = sp;

            ok.Click += (_, _) =>
            {
                var target = _steps[targetCombo.SelectedIndex];
                node.GotoNodeId = target.NodeId;
                node.Name = $"跳转 -> #{target.Order} {target.Summary}";
                w.DialogResult = true;
            };

            if (w.ShowDialog() == true) { RefreshStepTree(); AppendLog($"编辑跳转步骤 #{node.Order}"); }
        }

        private void ShowEditInputParamDialog(RecordedAction node)
        {
            var param = new ScriptParameter
            {
                Id = node.ParameterName ?? Guid.NewGuid().ToString("N")[..8],
                Name = node.ParameterName ?? "",
                DisplayName = node.Name?.Replace("参数: ", "") ?? "",
                DefaultValue = node.DefaultValue ?? "",
                IsRequired = node.IsRequired,
                Type = ParameterType.Text,
                CopyToClipboard = node.CopyToClipboard
            };

            var edited = ShowAddParamDialog(param);
            if (edited != null)
            {
                node.ParameterName = edited.Name;
                node.Name = $"参数: {edited.DisplayName ?? edited.Name}";
                node.DefaultValue = edited.DefaultValue;
                node.IsRequired = edited.IsRequired;
                node.CopyToClipboard = edited.CopyToClipboard;
                node.Parameter = $"{{{edited.Name}}}";
                RefreshStepTree();
                AppendLog($"编辑参数步骤: {edited.Name}");
            }
        }

        private void ShowEditRegexMatchDialog(RecordedAction node)
        {
            var edited = ShowRegexMatchDialog(node);
            if (edited != null)
            {
                node.Name = edited.Name;
                node.WindowTitle = edited.WindowTitle;
                node.RegexPattern = edited.RegexPattern;
                node.RegexGroup = edited.RegexGroup;
                node.OutputParamName = edited.OutputParamName;
                node.CopyToClipboard = edited.CopyToClipboard;
                RefreshStepTree();
                AppendLog($"编辑正则识别步骤: {edited.RegexPattern}");
            }
        }

        private void Delete_Click(object s, RoutedEventArgs e)
        {
            if (GetSelectedAction() is not RecordedAction n) return;
            string nodeId = n.NodeId;
            bool removed = _recorder.RemoveNode(nodeId);
            if (!removed)
            {
                _steps.Remove(n);
                SyncStepsToRecorder();
            }
            RefreshStepTree();
            PlayBtn.IsEnabled = _steps.Count > 0;
        }

        private void BatchDelete_Click(object s, RoutedEventArgs e)
        {
            // 收集所有 IsRowSelected=true 的步骤
            var selectedIds = CollectSelectedNodeIds();
            if (selectedIds.Count == 0) { MessageBox.Show("请先勾选要删除的步骤"); return; }
            if (MessageBox.Show($"确定删除选中的 {selectedIds.Count} 个步骤吗？", "确认批量删除", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            int removed = _recorder.RemoveNodes(selectedIds);
            if (removed == 0)
            {
                var toRemove = _steps.Where(a => selectedIds.Contains(a.NodeId)).ToList();
                foreach (var a in toRemove) _steps.Remove(a);
                SyncStepsToRecorder();
            }
            RefreshStepTree();
            PlayBtn.IsEnabled = _steps.Count > 0;
            AppendLog($"已批量删除 {removed} 个步骤");
        }

        /// <summary>
        /// 递归收集所有 IsRowSelected=true 的步骤 NodeId。
        /// </summary>
        private List<string> CollectSelectedNodeIds()
        {
            var ids = new List<string>();
            if (StepsTree.ItemsSource is ObservableCollection<StepTreeNode> roots)
            {
                foreach (var root in roots)
                    CollectSelectedRecursive(root, ids);
            }
            return ids;
        }

        private static void CollectSelectedRecursive(StepTreeNode node, List<string> ids)
        {
            if (!node.IsBranchHeader && node.IsRowSelected)
                ids.Add(node.Action.NodeId);
            foreach (var child in node.Children)
                CollectSelectedRecursive(child, ids);
        }

        private void SelectAllCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool isChecked = SelectAllCheckBox.IsChecked == true;
            if (StepsTree.ItemsSource is ObservableCollection<StepTreeNode> roots)
            {
                foreach (var root in roots)
                    SetRowSelectedRecursive(root, isChecked);
            }
        }

        private static void SetRowSelectedRecursive(StepTreeNode node, bool selected)
        {
            if (!node.IsBranchHeader)
                node.IsRowSelected = selected;
            foreach (var child in node.Children)
                SetRowSelectedRecursive(child, selected);
        }

        private void RefreshStepTree()
        {
            // 记录当前选中项
            string? selectedId = GetSelectedAction()?.NodeId;

            _steps.Clear();
            foreach (var n in _recorder.Nodes) _steps.Add(n);
            StepCountText.Text = _steps.Count.ToString();

            // 重建树
            StepsTree.ItemsSource = BuildStepTree();

            // 重置全选复选框
            SelectAllCheckBox.IsChecked = false;

            // 恢复选中
            if (selectedId != null)
                SelectTreeNodeByActionId(selectedId);
        }

        /// <summary>
        /// 从 _recorder.Nodes 递归构建 StepTreeNode 树。
        /// </summary>
        private ObservableCollection<StepTreeNode> BuildStepTree()
        {
            var nodes = new ObservableCollection<StepTreeNode>();
            int order = 0;
            foreach (var action in _recorder.Nodes)
            {
                order++;
                var treeNode = new StepTreeNode(action, null, null, null)
                {
                    DisplayOrder = order.ToString()
                };

                bool isContainer = action.ActionType == ActionType.If
                    || action.ActionType == ActionType.While
                    || action.ActionType == ActionType.Loop
                    || action.ActionType == ActionType.Try;
                if (isContainer)
                {
                    // 分支标签按类型：If=True/False，While/Loop=循环体，Try=Try/Catch
                    bool isLoop = action.ActionType == ActionType.While || action.ActionType == ActionType.Loop;
                    bool isTry = action.ActionType == ActionType.Try;
                    string trueLabel = isLoop ? "🔄 循环体" : isTry ? "🟢 Try" : "✓ True";
                    string falseLabel = isTry ? "🔴 Catch" : "✗ False";

                    // True 分支 / 循环体 / Try 体
                    if (action.TrueActions != null)
                    {
                        var trueHeader = new StepTreeNode(trueLabel, _branchTrueBrush, action.NodeId, true);
                        int subOrder = 0;
                        foreach (var ta in action.TrueActions)
                        {
                            subOrder++;
                            trueHeader.Children.Add(new StepTreeNode(ta, "True", _branchTrueBrush, action.NodeId, true, false)
                            {
                                DisplayOrder = $"{order}.T{subOrder}"
                            });
                        }
                        treeNode.Children.Add(trueHeader);
                    }

                    // False 分支 / Catch 体（循环无 False 分支，不显示）
                    if (!isLoop && action.FalseActions != null)
                    {
                        var falseHeader = new StepTreeNode(falseLabel, _branchFalseBrush, action.NodeId, false);
                        int subOrder = 0;
                        foreach (var fa in action.FalseActions)
                        {
                            subOrder++;
                            falseHeader.Children.Add(new StepTreeNode(fa, "False", _branchFalseBrush, action.NodeId, false, true)
                            {
                                DisplayOrder = $"{order}.F{subOrder}"
                            });
                        }
                        treeNode.Children.Add(falseHeader);
                    }
                }

                nodes.Add(treeNode);
            }
            return nodes;
        }

        /// <summary>
        /// 获取 TreeView 当前选中的 RecordedAction。
        /// </summary>
        private RecordedAction? GetSelectedAction()
        {
            if (StepsTree.SelectedItem is StepTreeNode node && !node.IsBranchHeader)
                return node.Action;
            return null;
        }

        /// <summary>
        /// 按 NodeId 选中 TreeView 中的节点。
        /// </summary>
        private void SelectTreeNodeByActionId(string nodeId)
        {
            var item = FindTreeViewItem(StepsTree, nodeId);
            if (item != null)
            {
                item.IsSelected = true;
                item.BringIntoView();
            }
        }

        private static TreeViewItem? FindTreeViewItem(ItemsControl parent, string nodeId)
        {
            for (int i = 0; i < parent.Items.Count; i++)
            {
                var item = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                if (item == null) continue;

                if (item.DataContext is StepTreeNode node && node.Action?.NodeId == nodeId)
                    return item;

                var found = FindTreeViewItem(item, nodeId);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// TreeView 中删除按钮点击。
        /// </summary>
        private void DeleteSingleTreeNode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not StepTreeNode treeNode || treeNode.IsBranchHeader) return;
            var action = treeNode.Action;
            if (action == null) return;

            // 检查是否是嵌套步骤
            if (treeNode.ParentIfNodeId != null)
            {
                var parentIf = _steps.FirstOrDefault(a => a.NodeId == treeNode.ParentIfNodeId);
                if (parentIf != null)
                {
                    string branchName = treeNode.IsInTrueBranch ? "True" : "False";
                    if (MessageBox.Show($"确定从 {branchName} 分支删除步骤？\n{action.Summary}", "删除分支步骤", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

                    if (treeNode.IsInTrueBranch)
                        parentIf.TrueActions?.Remove(action);
                    else
                        parentIf.FalseActions?.Remove(action);

                    SyncStepsToRecorder();
                    RefreshStepTree();
                    PlayBtn.IsEnabled = _steps.Count > 0;
                    return;
                }
            }

            // 顶层步骤
            string nodeId = action.NodeId;
            bool removed = _recorder.RemoveNode(nodeId);
            if (!removed)
            {
                _steps.Remove(action);
                SyncStepsToRecorder();
            }
            RefreshStepTree();
            PlayBtn.IsEnabled = _steps.Count > 0;
            AppendLog($"已删除步骤 #{action.Order}: {action.Name}");
        }

        private void SyncStepsToRecorder()
        {
            _recorder.ClearNodes();
            foreach (var s in _steps) _recorder.AddManual(s);
        }

        // ═══ 拖拽排序（TreeView 简化版：暂不支持拖放，使用上移/下移按钮） ═══

        private async void RunSingleAction_Click(object sender, RoutedEventArgs e)
        {
            RecordedAction? action = null;
            if (sender is Button btn)
            {
                // TreeView 中 Tag 直接绑定 RecordedAction
                if (btn.Tag is RecordedAction a)
                    action = a;
            }
            if (action == null) return;
            if (_player.IsPlaying) { AppendLog("正在回放中，请先停止"); return; }

            AppendLog($"单独执行步骤 #{action.Order}: {action.Name}");
            try
            {
                PlayBtn.IsEnabled = false; StopPlayBtn.IsEnabled = true;
                await _player.Play(new List<RecordedAction> { action }, null);
                StopPlayBtn.IsEnabled = false; PlayBtn.IsEnabled = _steps.Count > 0;
                UpdateReadContentDisplay();
            }
            catch (Exception ex) { AppendLog($"执行失败: {ex.Message}"); StopPlayBtn.IsEnabled = false; PlayBtn.IsEnabled = _steps.Count > 0; }
        }

        private void DeleteSingleAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not RecordedAction action) return;
            string nodeId = action.NodeId;
            bool removed = _recorder.RemoveNode(nodeId);
            if (!removed)
            {
                _steps.Remove(action);
                SyncStepsToRecorder();
            }
            RefreshStepTree();
            PlayBtn.IsEnabled = _steps.Count > 0;
            AppendLog($"已删除步骤 #{action.Order}: {action.Name}");
        }

        // ═══ 回放 ═══
        private async void PlayBtn_Click(object s, RoutedEventArgs e) => await PlayAllWithParams();
        private void StopPlayBtn_Click(object s, RoutedEventArgs e) => _player.Stop();
        private async System.Threading.Tasks.Task PlayAll()
        {
            if (_steps.Count == 0) return;

            PlayBtn.IsEnabled = false; StopPlayBtn.IsEnabled = true;
            await _player.Play(_steps.ToList(), null);
            PlayBtn.IsEnabled = _steps.Count > 0; StopPlayBtn.IsEnabled = false;
        }

        // ═══ 脚本管理 ═══
        private void LoadScriptsList()
        {
            _scripts.Clear();
            foreach (var f in Directory.GetFiles(_scriptsDir, "*.json"))
            {
                try
                {
                    var rec = ActionRecorder.LoadFromFile(f);
                    _scripts.Add(new ScriptInfo { FilePath = f, Name = rec.Name ?? Path.GetFileNameWithoutExtension(f), StepCount = rec.Actions.Count, LastModified = File.GetLastWriteTime(f) });
                } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"加载脚本失败 {f}: {ex.Message}"); }
            }
            ScriptCountText.Text = $"共 {_scripts.Count} 个脚本";
        }

        private void ScriptsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScriptsListBox.SelectedItem is ScriptInfo script)
            {
                _currentScript = script; CurrentScriptText.Text = script.Name;
                try
                {
                    var rec = ActionRecorder.LoadFromFile(script.FilePath);
                    _steps.Clear(); foreach (var a in rec.Actions) _steps.Add(a);
                    SyncStepsToRecorder();
                    _currentClickMode = rec.DefaultClickMode;
                    UpdateClickModeUI();
                    PlayBtn.IsEnabled = _steps.Count > 0; StepCountText.Text = _steps.Count.ToString();
                    StepsTree.ItemsSource = BuildStepTree();
                    AppendLog($"已加载: {script.Name} ({_steps.Count} 步, {_currentClickMode}模式)" +
                              "");
                } catch (Exception ex) { AppendLog($"加载失败: {ex.Message}"); }
            }
        }

        private void NewScript_Click(object s, RoutedEventArgs e)
        {
            var name = ShowInput("新建脚本", "名称:", $"脚本_{DateTime.Now:MMdd_HHmmss}");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!IsValidScriptName(name)) { MessageBox.Show("名称包含非法字符"); return; }
            var path = Path.Combine(_scriptsDir, $"{name}.json");
            var rec = new RecordingFile { Name = name, CreatedAt = DateTime.Now, Actions = new() };
            var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(rec, opt));
            LoadScriptsList(); ScriptsListBox.SelectedItem = _scripts.FirstOrDefault(x => x.Name == name);
        }

        private void EditScriptInfo_Click(object s, RoutedEventArgs e)
        {
            if (ScriptsListBox.SelectedItem is not ScriptInfo script) { MessageBox.Show("请先选择脚本"); return; }

            try
            {
                var rec = ActionRecorder.LoadFromFile(script.FilePath);
                if (rec == null) { MessageBox.Show("无法加载脚本"); return; }

                var w = new Window
                {
                    Title = $"编辑脚本 - {rec.Name}",
                    Width = 450, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this, ResizeMode = ResizeMode.NoResize
                };

                var sp = new StackPanel { Margin = new Thickness(15) };

                // 名称
                sp.Children.Add(new TextBlock { Text = "名称:", Margin = new Thickness(0, 0, 0, 3) });
                var nameBox = new TextBox { Text = rec.Name, Margin = new Thickness(0, 0, 0, 8) };
                sp.Children.Add(nameBox);

                // 描述
                sp.Children.Add(new TextBlock { Text = "描述:", Margin = new Thickness(0, 0, 0, 3) });
                var descBox = new TextBox
                {
                    Text = rec.Description ?? "",
                    Margin = new Thickness(0, 0, 0, 8),
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    MaxHeight = 80,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                sp.Children.Add(descBox);

                // 默认点击模式
                var clickModePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                clickModePanel.Children.Add(new TextBlock { Text = "默认点击模式:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                var clickModeCombo = new ComboBox { Width = 120 };
                clickModeCombo.Items.Add("Coordinate");
                clickModeCombo.Items.Add("UIAPath");
                clickModeCombo.Items.Add("Vision");
                clickModeCombo.SelectedItem = rec.DefaultClickMode.ToString();
                clickModePanel.Children.Add(clickModeCombo);
                sp.Children.Add(clickModePanel);

                // 统计信息（只读）
                sp.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 8) });
                var infoText = new TextBlock
                {
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 8),
                    Text = $"步骤数: {rec.Actions?.Count ?? 0}  |  参数数: {rec.Parameters?.Count ?? 0}  |  创建: {rec.CreatedAt:yyyy-MM-dd HH:mm}"
                };
                sp.Children.Add(infoText);

                // 按钮
                var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var ok = new Button { Content = "保存", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
                var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
                bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

                w.Content = sp;

                ok.Click += (_, _) =>
                {
                    var newName = nameBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(newName)) { MessageBox.Show("名称不能为空"); return; }
                    if (!IsValidScriptName(newName)) { MessageBox.Show("名称包含非法字符"); return; }

                    // 检查重名（排除自身）
                    if (newName != rec.Name)
                    {
                        var newPath = Path.Combine(_scriptsDir, $"{newName}.json");
                        if (File.Exists(newPath)) { MessageBox.Show("已存在同名脚本"); return; }
                    }

                    rec.Name = newName;
                    rec.Description = descBox.Text;
                    if (Enum.TryParse<WeChatAutomation.Core.Recording.ClickMode>(clickModeCombo.SelectedItem?.ToString(), out var cm))
                        rec.DefaultClickMode = cm;

                    // 如果改名了，需要移动文件
                    if (newName != script.Name)
                    {
                        var oldPath = script.FilePath;
                        var newPath = Path.Combine(_scriptsDir, $"{newName}.json");
                        var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                        File.WriteAllText(newPath, System.Text.Json.JsonSerializer.Serialize(rec, opt));
                        if (oldPath != newPath) File.Delete(oldPath);
                    }
                    else
                    {
                        var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                        File.WriteAllText(script.FilePath, System.Text.Json.JsonSerializer.Serialize(rec, opt));
                    }

                    w.DialogResult = true;
                };

                if (w.ShowDialog() == true)
                {
                    LoadScriptsList();
                    AppendLog($"已编辑脚本: {nameBox.Text.Trim()}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"编辑失败: {ex.Message}");
            }
        }

        private void DeleteScript_Click(object s, RoutedEventArgs e)
        {
            if (ScriptsListBox.SelectedItem is not ScriptInfo script) { MessageBox.Show("请先选择脚本"); return; }
            if (MessageBox.Show($"确定删除 \"{script.Name}\" 吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            File.Delete(script.FilePath);
            if (_currentScript == script) { _currentScript = null; CurrentScriptText.Text = "(未命名)"; _steps.Clear(); StepCountText.Text = "0"; StepsTree.ItemsSource = BuildStepTree(); }
            LoadScriptsList();
        }

        private void CopyScript_Click(object s, RoutedEventArgs e)
        {
            if (ScriptsListBox.SelectedItem is not ScriptInfo script) { MessageBox.Show("请先选择脚本"); return; }

            try
            {
                var rec = ActionRecorder.LoadFromFile(script.FilePath);
                if (rec == null) { MessageBox.Show("无法加载脚本"); return; }

                string defaultName = rec.Name + "_副本";
                var name = ShowInput("复制脚本", "新脚本名称:", defaultName);
                if (string.IsNullOrWhiteSpace(name)) return;
                if (!IsValidScriptName(name)) { MessageBox.Show("名称包含非法字符"); return; }

                // 重名时自动加时间戳后缀（与 ImportScript_Click 一致）
                var destPath = Path.Combine(_scriptsDir, $"{name}.json");
                if (File.Exists(destPath))
                {
                    name = $"{name}_{DateTime.Now:MMdd_HHmmss}";
                    destPath = Path.Combine(_scriptsDir, $"{name}.json");
                }

                var opt = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                };
                rec.Name = name;
                File.WriteAllText(destPath, System.Text.Json.JsonSerializer.Serialize(rec, opt));

                LoadScriptsList();
                ScriptsListBox.SelectedItem = _scripts.FirstOrDefault(x => x.Name == name);
                AppendLog($"已复制脚本: {script.Name} -> {name}");
            }
            catch (Exception ex)
            {
                AppendLog($"复制脚本失败: {ex.Message}");
            }
        }

        private void ExportScript_Click(object s, RoutedEventArgs e)
        {
            if (ScriptsListBox.SelectedItem is not ScriptInfo script) { MessageBox.Show("请先选择要导出的脚本"); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出脚本",
                Filter = "JSON 脚本|*.json|所有文件|*.*",
                FileName = $"{script.Name}.json"
            };
            if (dlg.ShowDialog() != true) return;
            File.Copy(script.FilePath, dlg.FileName, overwrite: true);
            AppendLog($"已导出: {dlg.FileName}");
            MessageBox.Show($"脚本已导出到:\n{dlg.FileName}", "导出成功");
        }

        private void ImportScript_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入脚本",
                Filter = "JSON 脚本|*.json|所有文件|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;

            int imported = 0;
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    var rec = ActionRecorder.LoadFromFile(file);
                    if (rec == null || rec.Actions == null) { AppendLog($"跳过无效文件: {Path.GetFileName(file)}"); continue; }

                    // 避免重名：如果已存在同名脚本则加后缀
                    string name = rec.Name ?? Path.GetFileNameWithoutExtension(file);
                    string destPath = Path.Combine(_scriptsDir, $"{name}.json");
                    if (File.Exists(destPath))
                    {
                        name = $"{name}_{DateTime.Now:MMdd_HHmmss}";
                        destPath = Path.Combine(_scriptsDir, $"{name}.json");
                    }

                    var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                    rec.Name = name;
                    File.WriteAllText(destPath, System.Text.Json.JsonSerializer.Serialize(rec, opt));
                    imported++;
                }
                catch (Exception ex) { AppendLog($"导入失败 {Path.GetFileName(file)}: {ex.Message}"); }
            }

            LoadScriptsList();
            if (imported > 0) AppendLog($"已导入 {imported} 个脚本");
            MessageBox.Show($"成功导入 {imported} 个脚本", "导入完成");
        }

        private void SaveBtn_Click(object s, RoutedEventArgs e)
        {
            if (_steps.Count == 0) { MessageBox.Show("没有步骤可保存"); return; }

            // 获取现有参数与创建时间（如果有）
            List<ScriptParameter> existingParams = null;
            RecordingFile existingRec = null;
            if (_currentScript != null)
            {
                try
                {
                    existingRec = ActionRecorder.LoadFromFile(_currentScript.FilePath);
                    existingParams = existingRec.Parameters;
                }
                catch { }
            }

            if (_currentScript != null)
            {
                var rec = new RecordingFile
                {
                    Name = _currentScript.Name,
                    CreatedAt = existingRec?.CreatedAt ?? DateTime.Now,
                    Actions = _steps.ToList(),
                    Parameters = existingParams ?? new List<ScriptParameter>(),
                    DefaultClickMode = _currentClickMode,
                    VisionModel = null
                };
                var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                File.WriteAllText(_currentScript.FilePath, System.Text.Json.JsonSerializer.Serialize(rec, opt));
                AppendLog($"已保存: {_currentScript.Name}"); LoadScriptsList();
            }
            else
            {
                var name = ShowInput("保存为新脚本", "名称:", $"脚本_{DateTime.Now:MMdd_HHmmss}");
                if (string.IsNullOrWhiteSpace(name)) return;
                if (!IsValidScriptName(name)) { MessageBox.Show("名称包含非法字符"); return; }
                var path = Path.Combine(_scriptsDir, $"{name}.json");
                var rec = new RecordingFile
                {
                    Name = name,
                    CreatedAt = DateTime.Now,
                    Actions = _steps.ToList(),
                    Parameters = existingParams ?? new List<ScriptParameter>(),
                    DefaultClickMode = _currentClickMode,
                    VisionModel = null
                };
                var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(rec, opt));
                LoadScriptsList(); _currentScript = _scripts.FirstOrDefault(x => x.Name == name); CurrentScriptText.Text = name;
            }
        }

        // ═══ 选择窗口（支持悬停预览） ═══
        private bool PickTargetWindow(string message)
        {
            // 创建浮动提示标签（跟随鼠标显示元素信息）
            var hoverTip = new Window
            {
                Title = "", Width = 380, Height = Double.NaN, // Height auto
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                Foreground = Brushes.White, ShowInTaskbar = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                IsHitTestVisible = false, Focusable = false,
                Padding = new Thickness(0)
            };
            var tipText = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 11, FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(8, 5, 8, 5), TextWrapping = TextWrapping.Wrap,
                Text = "移动鼠标到目标区域，点击选中\n按 Esc 取消"
            };
            hoverTip.Content = tipText;

            // 创建顶部提示条
            var topBar = new Window
            {
                Title = "选择目标区域", Width = 500, Height = 50,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(240, 33, 150, 243)),
                Foreground = Brushes.White, ShowInTaskbar = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                IsHitTestVisible = false, Focusable = false
            };
            topBar.Content = new TextBlock
            {
                Text = "🔍 移动鼠标查看元素信息，点击选中目标区域，Esc 取消",
                FontSize = 13, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };

            this.WindowState = WindowState.Minimized;

            IntPtr capturedHwnd = IntPtr.Zero;
            FlaUI.Core.AutomationElements.AutomationElement capturedElement = null;
            bool clickCaptured = false;
            var uia = new FlaUI.UIA3.UIA3Automation();

            // 使用 DispatcherFrame 嵌套消息循环，避免阻塞 UI 线程
            var frame = new DispatcherFrame();

            // 全局鼠标钩子 — 同时监听移动和点击
            IntPtr hookId = IntPtr.Zero;
            User32.LowLevelMouseProc hookProc = (int nCode, IntPtr wParam, IntPtr lParam) =>
            {
                if (nCode >= 0)
                {
                    int msg = (int)wParam;
                    var st = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);
                    int x = st.pt.x, y = st.pt.y;

                    if (msg == User32.WM_MOUSEMOVE && !clickCaptured)
                    {
                        // 悬停预览：获取鼠标位置的 UIA 元素信息
                        try
                        {
                            var element = uia.FromPoint(new System.Drawing.Point(x, y));
                            if (element != null)
                            {
                                string ct = "";
                                try { ct = element.ControlType.ToString(); } catch { }
                                string name = "";
                                try { name = element.Name ?? ""; } catch { }
                                string cls = "";
                                try { cls = element.ClassName ?? ""; } catch { }
                                string autoId = "";
                                try { autoId = element.AutomationId ?? ""; } catch { }

                                var sb = new System.Text.StringBuilder();
                                sb.AppendLine($"类型: {ct}");
                                if (!string.IsNullOrEmpty(name))
                                    sb.AppendLine($"名称: {(name.Length > 40 ? name[..40] + "..." : name)}");
                                if (!string.IsNullOrEmpty(cls))
                                    sb.AppendLine($"类名: {cls}");
                                if (!string.IsNullOrEmpty(autoId))
                                    sb.AppendLine($"ID: {autoId}");

                                // 在 UI 线程更新悬停提示
                                string tipContent = sb.ToString().TrimEnd();
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    tipText.Text = tipContent;
                                    // 定位提示窗口在鼠标右下方
                                    hoverTip.Left = x + 20;
                                    hoverTip.Top = y + 20;
                                    // 防止超出屏幕
                                    if (hoverTip.Left + 400 > SystemParameters.PrimaryScreenWidth)
                                        hoverTip.Left = x - 400;
                                    if (hoverTip.Top + 150 > SystemParameters.PrimaryScreenHeight)
                                        hoverTip.Top = y - 150;
                                }));
                            }
                        }
                        catch { }
                    }
                    else if (msg == User32.WM_LBUTTONDOWN && !clickCaptured)
                    {
                        // 点击选中
                        try
                        {
                            var element = uia.FromPoint(new System.Drawing.Point(x, y));
                            if (element == null) goto next;

                            // 排除自身窗口
                            IntPtr hwnd = User32.WindowFromPoint(new User32.POINT { x = x, y = y });
                            User32.GetWindowThreadProcessId(hwnd, out int pid);
                            if (pid == System.Diagnostics.Process.GetCurrentProcess().Id) goto next;

                            IntPtr topLevelHwnd = User32.GetAncestor(hwnd, User32.GA_ROOT);
                            if (topLevelHwnd == IntPtr.Zero) topLevelHwnd = hwnd;

                            capturedHwnd = topLevelHwnd;
                            capturedElement = element;
                            clickCaptured = true;

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (hookId != IntPtr.Zero) { User32.UnhookWindowsHookEx(hookId); hookId = IntPtr.Zero; }
                                hoverTip.Close();
                                topBar.Close();
                            }));
                        }
                        catch { }
                    next:;
                    }
                }
                return User32.CallNextHookEx(hookId, nCode, wParam, lParam);
            };

            // 安装全局鼠标钩子
            using (var cur = System.Diagnostics.Process.GetCurrentProcess())
            using (var mod = cur.MainModule)
            {
                hookId = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, hookProc,
                    User32.GetModuleHandle(mod!.ModuleName), 0);
            }

            // Esc 键取消
            var escHandler = new KeyEventHandler((s, e) =>
            {
                if (e.Key == Key.Escape && !clickCaptured)
                {
                    clickCaptured = true;
                    if (hookId != IntPtr.Zero) { User32.UnhookWindowsHookEx(hookId); hookId = IntPtr.Zero; }
                    hoverTip.Close();
                    topBar.Close();
                }
            });
            this.KeyDown += escHandler;

            // 显示悬停提示和顶部提示
            hoverTip.Show();
            topBar.Show();

            // 15 秒超时
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!clickCaptured)
                {
                    clickCaptured = true;
                    if (hookId != IntPtr.Zero) { User32.UnhookWindowsHookEx(hookId); hookId = IntPtr.Zero; }
                    hoverTip.Close();
                    topBar.Close();
                }
            };
            timer.Start();

            // 窗口关闭时退出嵌套消息循环
            hoverTip.Closed += (_, _) =>
            {
                timer.Stop();
                this.KeyDown -= escHandler;
                frame.Continue = false; // 退出 Dispatcher.Push 嵌套循环
            };

            // 进入嵌套消息循环（不阻塞 UI 线程，仍可处理 Dispatcher 回调）
            Dispatcher.PushFrame(frame);

            this.WindowState = WindowState.Normal; this.Activate();

            if (capturedHwnd == IntPtr.Zero || capturedElement == null)
            {
                AppendLog("未捕获到目标区域");
                return false;
            }

            // 设置目标元素
            _player.SetTargetElement(capturedHwnd, capturedElement);

            string elementDesc = "";
            try { elementDesc = $"{capturedElement.ControlType}"; } catch { }
            try { if (!string.IsNullOrEmpty(capturedElement.Name)) elementDesc += $" '{TruncateStr(capturedElement.Name, 30)}'"; } catch { }

            int len2 = User32.GetWindowTextLength(capturedHwnd);
            if (len2 > 0)
            {
                var sb2 = new System.Text.StringBuilder(len2 + 1);
                User32.GetWindowText(capturedHwnd, sb2, sb2.Capacity);
                AppendLog($"已选择: {sb2} → {elementDesc}");
            }
            else
            {
                AppendLog($"已选择区域: {elementDesc}");
            }
            return true;
        }

        private static string TruncateStr(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

        // ═══ 窗口检查模式（F12） ═══
        private Window _inspectTip;       // 悬停提示窗口
        private Window _inspectTopBar;    // 顶部状态条
        private IntPtr _inspectHookId = IntPtr.Zero;
        private User32.LowLevelMouseProc _inspectHookProc;
        private UIA3Automation _inspectUia;
        private KeyEventHandler _inspectEscHandler;

        private void InspectBtn_Click(object s, RoutedEventArgs e)
        {
            if (_isInspecting) StopInspect(); else StartInspect();
        }

        private void StartInspect()
        {
            if (_isInspecting) return;
            _isInspecting = true;
            InspectBtn.Background = _inspectActiveBrush;

            // 创建浮动提示标签
            _inspectTip = new Window
            {
                Title = "", Width = 420, Height = Double.NaN,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(235, 20, 20, 20)),
                Foreground = Brushes.White, ShowInTaskbar = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                IsHitTestVisible = false, Focusable = false,
                Padding = new Thickness(0)
            };
            var tipText = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 11, FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(8, 5, 8, 5), TextWrapping = TextWrapping.Wrap,
                Text = "移动鼠标查看窗口信息\nF12 或 Esc 退出检查模式"
            };
            _inspectTip.Content = tipText;

            // 创建顶部状态条
            _inspectTopBar = new Window
            {
                Title = "窗口检查模式", Width = 500, Height = 40,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(240, 123, 31, 162)),
                Foreground = Brushes.White, ShowInTaskbar = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                IsHitTestVisible = false, Focusable = false
            };
            _inspectTopBar.Content = new TextBlock
            {
                Text = "🔍 窗口检查模式 — 移动鼠标查看窗口/元素信息 | F12 或 Esc 退出",
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };

            _inspectUia = new UIA3Automation();

            // 全局鼠标钩子 — 只监听移动
            _inspectHookProc = (int nCode, IntPtr wParam, IntPtr lParam) =>
            {
                if (nCode >= 0)
                {
                    int msg = (int)wParam;
                    if (msg == User32.WM_MOUSEMOVE)
                    {
                        var st = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);
                        int x = st.pt.x, y = st.pt.y;

                        try
                        {
                            // 获取鼠标位置的窗口句柄
                            IntPtr hwnd = User32.WindowFromPoint(new User32.POINT { x = x, y = y });
                            IntPtr topHwnd = User32.GetAncestor(hwnd, User32.GA_ROOT);
                            if (topHwnd == IntPtr.Zero) topHwnd = hwnd;

                            // 排除自身窗口
                            User32.GetWindowThreadProcessId(topHwnd, out int pid);
                            int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                            bool isSelf = (pid == myPid);

                            // 获取窗口信息
                            var sb = new System.Text.StringBuilder();

                            // 窗口标题
                            int titleLen = User32.GetWindowTextLength(topHwnd);
                            string windowTitle = "";
                            if (titleLen > 0)
                            {
                                var titleSb = new System.Text.StringBuilder(titleLen + 1);
                                User32.GetWindowText(topHwnd, titleSb, titleSb.Capacity);
                                windowTitle = titleSb.ToString();
                            }

                            // 窗口类名
                            var clsSb = new System.Text.StringBuilder(256);
                            User32.GetClassName(topHwnd, clsSb, clsSb.Capacity);
                            string windowClass = clsSb.ToString();

                            // 进程信息
                            string processName = "";
                            try
                            {
                                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                                processName = proc.ProcessName;
                            }
                            catch { }

                            sb.AppendLine($"══ 窗口信息 ══");
                            sb.AppendLine($"标题: {(windowTitle.Length > 50 ? windowTitle[..50] + "..." : windowTitle)}");
                            sb.AppendLine($"句柄: 0x{topHwnd.ToInt64():X8} ({topHwnd.ToInt64()})");
                            sb.AppendLine($"类名: {windowClass}");
                            sb.AppendLine($"进程: {processName} (PID: {pid})");
                            if (isSelf) sb.AppendLine($"⚠ 自身窗口");

                            // 获取 UIA 元素信息
                            try
                            {
                                var element = _inspectUia.FromPoint(new System.Drawing.Point(x, y));
                                if (element != null)
                                {
                                    sb.AppendLine($"══ 元素信息 ══");
                                    string ct = "";
                                    try { ct = element.ControlType.ToString(); } catch { }
                                    if (!string.IsNullOrEmpty(ct)) sb.AppendLine($"类型: {ct}");

                                    string name = "";
                                    try { name = element.Name ?? ""; } catch { }
                                    if (!string.IsNullOrEmpty(name))
                                        sb.AppendLine($"名称: {(name.Length > 50 ? name[..50] + "..." : name)}");

                                    string cls = "";
                                    try { cls = element.ClassName ?? ""; } catch { }
                                    if (!string.IsNullOrEmpty(cls)) sb.AppendLine($"类名: {cls}");

                                    string autoId = "";
                                    try { autoId = element.AutomationId ?? ""; } catch { }
                                    if (!string.IsNullOrEmpty(autoId)) sb.AppendLine($"ID: {autoId}");

                                    // BoundingRectangle
                                    try
                                    {
                                        var rect = element.BoundingRectangle;
                                        sb.AppendLine($"位置: ({(int)rect.X}, {(int)rect.Y}) {((int)rect.Width)}×{((int)rect.Height)}");
                                    }
                                    catch { }
                                }
                            }
                            catch { }

                            string tipContent = sb.ToString().TrimEnd();
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                tipText.Text = tipContent;
                                // 定位提示窗口在鼠标右下方
                                _inspectTip.Left = x + 20;
                                _inspectTip.Top = y + 20;
                                // 防止超出屏幕
                                if (_inspectTip.Left + 440 > SystemParameters.PrimaryScreenWidth)
                                    _inspectTip.Left = x - 440;
                                if (_inspectTip.Top + 200 > SystemParameters.PrimaryScreenHeight)
                                    _inspectTip.Top = y - 200;
                            }));
                        }
                        catch { }
                    }
                }
                return User32.CallNextHookEx(_inspectHookId, nCode, wParam, lParam);
            };

            // 安装全局鼠标钩子
            using (var cur = System.Diagnostics.Process.GetCurrentProcess())
            using (var mod = cur.MainModule)
            {
                _inspectHookId = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _inspectHookProc,
                    User32.GetModuleHandle(mod!.ModuleName), 0);
            }

            // Esc 键退出
            _inspectEscHandler = new KeyEventHandler((s, e) =>
            {
                if (e.Key == Key.Escape && _isInspecting)
                    StopInspect();
            });
            this.KeyDown += _inspectEscHandler;

            _inspectTip.Show();
            _inspectTopBar.Show();
            AppendLog("🔍 窗口检查模式已开启 — 移动鼠标查看窗口信息，F12/Esc 退出");
        }

        private void StopInspect()
        {
            if (!_isInspecting) return;
            _isInspecting = false;

            // 卸载钩子
            if (_inspectHookId != IntPtr.Zero)
            {
                User32.UnhookWindowsHookEx(_inspectHookId);
                _inspectHookId = IntPtr.Zero;
            }

            // 移除 Esc 监听
            if (_inspectEscHandler != null)
            {
                this.KeyDown -= _inspectEscHandler;
                _inspectEscHandler = null;
            }

            // 关闭提示窗口
            try { _inspectTip?.Close(); } catch { }
            try { _inspectTopBar?.Close(); } catch { }
            _inspectTip = null;
            _inspectTopBar = null;

            // 释放 UIA
            try { _inspectUia?.Dispose(); } catch { }
            _inspectUia = null;

            InspectBtn.Background = _inspectInactiveBrush;
            AppendLog("🔍 窗口检查模式已关闭");
        }

        // ═══ 路径树抓取（F8）══
        // 按 F8 进入抓取模式 → 鼠标点击目标元素 → 弹出该元素所属窗口根下的 UIA 子树（可复制）。
        // 用于诊断：A 脚本能用而 B 不能用时，对比 B 下点击元素的路径树。
        private bool _isTreeCapturing = false;
        private bool _treeCaptured = false;   // 已抓取一次，忽略后续点击（保证只抓一个）
        private IntPtr _treeHookId = IntPtr.Zero;
        private User32.LowLevelMouseProc _treeHookProc;
        private UIA3Automation _treeUia;
        private Window _treeTopBar;
        private KeyEventHandler _treeEscHandler;

        private void StartTreeCapture()
        {
            if (_isTreeCapturing) return;
            _isTreeCapturing = true;
            _treeCaptured = false;

            _treeUia = new UIA3Automation();

            // 顶部状态条
            _treeTopBar = new Window
            {
                Title = "路径树抓取", Width = 560, Height = 40,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(240, 0, 96, 100)),
                Foreground = Brushes.White, ShowInTaskbar = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                IsHitTestVisible = false, Focusable = false
            };
            _treeTopBar.Content = new TextBlock
            {
                Text = "🌳 路径树抓取 - 点击一个目标元素生成 UIA 路径树（点击后自动结束）| F8 或 Esc 取消",
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            _treeTopBar.Show();

            // 全局鼠标钩子 - 只在左键按下时抓取
            _treeHookProc = (int nCode, IntPtr wParam, IntPtr lParam) =>
            {
                if (nCode >= 0 && (int)wParam == User32.WM_LBUTTONDOWN && !_treeCaptured)
                {
                    // 立即标记已抓取并卸载钩子，保证只能抓取一个元素（后续点击不再触发）
                    _treeCaptured = true;
                    if (_treeHookId != IntPtr.Zero)
                    {
                        User32.UnhookWindowsHookEx(_treeHookId);
                        _treeHookId = IntPtr.Zero;
                    }

                    var st = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);
                    int x = st.pt.x, y = st.pt.y;
                    // 在后台线程抓树避免阻塞钩子链
                    System.Threading.Tasks.Task.Run(() => CaptureAndShowTree(x, y));
                }
                return User32.CallNextHookEx(_treeHookId, nCode, wParam, lParam);
            };

            using (var cur = System.Diagnostics.Process.GetCurrentProcess())
            using (var mod = cur.MainModule)
            {
                _treeHookId = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _treeHookProc,
                    User32.GetModuleHandle(mod!.ModuleName), 0);
            }

            _treeEscHandler = new KeyEventHandler((s, e) =>
            {
                if (e.Key == Key.Escape && _isTreeCapturing) StopTreeCapture();
            });
            this.KeyDown += _treeEscHandler;

            AppendLog("🌳 路径树抓取已开启 - 点击一个目标元素生成路径树，点击后自动结束（F8/Esc 取消）");
        }

        private void StopTreeCapture()
        {
            if (!_isTreeCapturing) return;
            _isTreeCapturing = false;

            if (_treeHookId != IntPtr.Zero)
            {
                User32.UnhookWindowsHookEx(_treeHookId);
                _treeHookId = IntPtr.Zero;
            }
            if (_treeEscHandler != null)
            {
                this.KeyDown -= _treeEscHandler;
                _treeEscHandler = null;
            }
            try { _treeTopBar?.Close(); } catch { }
            _treeTopBar = null;
            try { _treeUia?.Dispose(); } catch { }
            _treeUia = null;

            AppendLog("🌳 路径树抓取已关闭");
        }

        /// <summary>
        /// 抓取点击位置元素的 UIA 路径树并弹窗展示。
        /// 向上找到窗口根，再向下递归生成子树（限制深度和宽度避免超大树）。
        /// </summary>
        private void CaptureAndShowTree(int x, int y)
        {
            string treeText;
            try
            {
                var uia = _treeUia;
                if (uia == null)
                {
                    Dispatcher.BeginInvoke(new Action(() => StopTreeCapture()));
                    return;
                }
                var element = uia.FromPoint(new System.Drawing.Point(x, y));
                if (element == null)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AppendLog("🌳 未获取到点击位置元素");
                        StopTreeCapture();
                    }));
                    return;
                }

                // 向上找到窗口根
                var root = element;
                for (int i = 0; i < 64; i++)
                {
                    var parent = root.Parent;
                    if (parent == null) break;
                    root = parent;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"══ 点击位置 ({x},{y}) 的路径树 ══");
                sb.AppendLine();
                BuildTreeText(root, sb, 0, maxDepth: 8, maxChildrenPerNode: 30);
                treeText = sb.ToString();
            }
            catch (Exception ex)
            {
                treeText = $"抓取路径树异常: {ex.Message}";
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopTreeCapture(); // 抓取一次后自动退出
                ShowTreeDialog(treeText);
            }));
        }

        /// <summary>递归生成 UIA 树文本（带缩进）。</summary>
        private void BuildTreeText(FlaUI.Core.AutomationElements.AutomationElement node,
            System.Text.StringBuilder sb, int depth, int maxDepth, int maxChildrenPerNode)
        {
            if (node == null || depth > maxDepth) return;

            string indent = new string(' ', depth * 2);
            string ct = "", name = "", cls = "", autoId = "";
            try { ct = node.ControlType.ToString(); } catch { }
            try { name = node.Name ?? ""; } catch { }
            try { cls = node.ClassName ?? ""; } catch { }
            try { autoId = node.AutomationId ?? ""; } catch { }

            // 拼成可读行：Type Name='..' Class='..' AutoId='..'
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ct)) parts.Add(ct);
            if (!string.IsNullOrEmpty(name)) parts.Add($"Name='{TruncS(name, 30)}'");
            if (!string.IsNullOrEmpty(cls)) parts.Add($"Class='{cls}'");
            if (!string.IsNullOrEmpty(autoId)) parts.Add($"AutoId='{autoId}'");
            sb.AppendLine($"{indent}{(parts.Count > 0 ? string.Join(" ", parts) : "(空元素)")}");

            if (depth == maxDepth) return;

            FlaUI.Core.AutomationElements.AutomationElement[] children;
            try { children = node.FindAllChildren(); }
            catch { return; }
            if (children == null || children.Length == 0) return;

            bool truncated = children.Length > maxChildrenPerNode;
            int showCount = Math.Min(children.Length, maxChildrenPerNode);
            for (int i = 0; i < showCount; i++)
                BuildTreeText(children[i], sb, depth + 1, maxDepth, maxChildrenPerNode);

            if (truncated)
                sb.AppendLine($"{new string(' ', (depth + 1) * 2)}... (还有 {children.Length - showCount} 个兄弟节点已省略)");
        }

        /// <summary>弹窗展示路径树文本，带"复制到剪切板"按钮。</summary>
        private void ShowTreeDialog(string text)
        {
            var w = new Window
            {
                Title = "UIA 路径树（可复制）", Width = 720, Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this
            };
            var sp = new StackPanel { Margin = new Thickness(8) };
            var tb = new TextBox
            {
                Text = text, IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                Height = 470
            };
            sp.Children.Add(tb);
            var bp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var copyBtn = new Button { Content = "复制全部", Padding = new Thickness(14, 5, 14, 5) };
            var closeBtn = new Button { Content = "关闭", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(5, 0, 0, 0), IsCancel = true };
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(text); AppendLog("🌳 路径树已复制到剪切板"); } catch { }
            };
            bp.Children.Add(copyBtn); bp.Children.Add(closeBtn);
            sp.Children.Add(bp);
            w.Content = sp;
            w.Show();
        }

        private static string TruncS(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length > max ? s[..max] + "..." : s;

        // ═══ 工具 ═══
        private string ShowInput(string title, string msg, string def = "")
        {
            var w = new Window { Title = title, Width = 320, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
            var sp = new StackPanel { Margin = new Thickness(10) };
            sp.Children.Add(new TextBlock { Text = msg, Margin = new Thickness(0, 0, 0, 5) });
            var tb = new TextBox { Text = def }; sp.Children.Add(tb);
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(12, 4, 12, 4) };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(5, 0, 0, 0) };
            bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);
            w.Content = sp; ok.Click += (_, _) => w.DialogResult = true;
            return w.ShowDialog() == true ? tb.Text : null;
        }

        private bool IsValidScriptName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
        }

        private void AppendLog(string msg) => Dispatcher.BeginInvoke(() => LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        private void ClearLog_Click(object s, RoutedEventArgs e) => LogText.Text = "";

        private void UpdateReadContentDisplay()
        {
            var last = _player.ReadResults.LastOrDefault();
            if (last == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{last.CapturedAt:HH:mm:ss}] {last.WindowTitle}");

            if (last.Source == "RegexMatch")
            {
                sb.AppendLine("来源: 正则识别");
                if (!string.IsNullOrEmpty(last.MatchedValue))
                    sb.AppendLine($"匹配值: {last.MatchedValue}");
                if (last.Matches.Count > 0)
                {
                    sb.AppendLine($"匹配数: {last.Matches.Count}");
                    for (int i = 0; i < last.Matches.Count; i++)
                    {
                        var m = last.Matches[i];
                        sb.AppendLine($"  [{i + 1}] {m.Value}");
                        foreach (var g in m.Groups)
                            sb.AppendLine($"      {g.Key}: {g.Value}");
                    }
                }
                sb.AppendLine($"\n--- 原始内容 ---\n{last.Content}");
            }
            else
            {
                sb.AppendLine($"\n{last.Content}");
            }

            ReadContentText.Text = sb.ToString();
        }

        // ═══ 阅读保存 ═══
        private void SaveReadContent_Click(object s, RoutedEventArgs e)
        {
            var last = _player.ReadResults.LastOrDefault();
            if (last == null || string.IsNullOrEmpty(last.Content)) { MessageBox.Show("没有可保存的内容"); return; }

            var content = last.Source == "RegexMatch" && !string.IsNullOrEmpty(last.MatchedValue)
                ? $"匹配值:\n{last.MatchedValue}\n\n--- 原始内容 ---\n{last.Content}"
                : last.Content;

            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "保存", Filter = "文本|*.txt|所有|*.*", FileName = $"阅读_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
            if (dlg.ShowDialog() == true) { File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8); AppendLog($"已保存: {dlg.FileName}"); }
        }

        private void CopyReadContent_Click(object s, RoutedEventArgs e)
        {
            var last = _player.ReadResults.LastOrDefault();
            if (last == null) { MessageBox.Show("没有可复制的内容"); return; }

            var content = last.Source == "RegexMatch" && !string.IsNullOrEmpty(last.MatchedValue)
                ? last.MatchedValue
                : last.Content;

            if (string.IsNullOrEmpty(content)) { MessageBox.Show("没有可复制的内容"); return; }
            Clipboard.SetText(content); AppendLog("已复制");
        }

        protected override void OnClosed(EventArgs e) { _recorder?.Dispose(); _player?.Dispose(); _hotkeyHook?.Dispose(); base.OnClosed(e); }

        // ═══ 服务状态 ═══
        private void RefreshStatus_Click(object s, RoutedEventArgs e) => UpdateServiceStatus();

        private void UpdateServiceStatus()
        {
            try
            {
                // HTTP 状态
                if (App.HttpApi?.IsRunning == true)
                {
                    AnimateStatusDot(HttpStatusDot, _statusOnBrush);
                    HttpPortText.Text = $":{App.HttpApi.Port}";
                    HttpToggleBtn.Content = "关闭";
                    HttpToggleBtn.Background = _toggleOnBrush;
                    HttpToggleBtn.Foreground = Brushes.White;
                }
                else
                {
                    AnimateStatusDot(HttpStatusDot, _statusOffBrush);
                    HttpPortText.Text = "(未启动)";
                    HttpToggleBtn.Content = "开启";
                    HttpToggleBtn.Background = _toggleOffBrush;
                    HttpToggleBtn.Foreground = Brushes.White;
                }

                // MQTT 状态
                if (App.Mqtt?.IsConnected == true)
                {
                    AnimateStatusDot(MqttStatusDot, _statusOnBrush);
                    MqttStatusText.Text = $":{App.Mqtt.BrokerPort}";
                    MqttToggleBtn.Content = "关闭";
                    MqttToggleBtn.Background = _toggleOnBrush;
                    MqttToggleBtn.Foreground = Brushes.White;
                }
                else
                {
                    AnimateStatusDot(MqttStatusDot, _statusOffBrush);
                    MqttStatusText.Text = "(未连接)";
                    MqttToggleBtn.Content = "开启";
                    MqttToggleBtn.Background = _toggleOffBrush;
                    MqttToggleBtn.Foreground = Brushes.White;
                }

                // MCP 状态
                if (App.Mcp != null)
                {
                    AnimateStatusDot(McpStatusDot, _statusOnBrush);
                    McpToggleBtn.Content = "关闭";
                    McpToggleBtn.Background = _toggleOnBrush;
                    McpToggleBtn.Foreground = Brushes.White;
                }
                else
                {
                    AnimateStatusDot(McpStatusDot, _statusOffBrush);
                    McpToggleBtn.Content = "开启";
                    McpToggleBtn.Background = _toggleOffBrush;
                    McpToggleBtn.Foreground = Brushes.White;
                }

                // 任务轮询状态
                if (App.TaskPolling?.IsRunning == true)
                {
                    AnimateStatusDot(TaskPollingStatusDot, _statusOnBrush);
                    TaskPollingStatusText.Text = App.TaskPolling.IsExecuting ? "执行中" : "空闲";
                    TaskPollingToggleBtn.Content = "关闭";
                    TaskPollingToggleBtn.Background = _toggleOnBrush;
                    TaskPollingToggleBtn.Foreground = Brushes.White;
                }
                else
                {
                    AnimateStatusDot(TaskPollingStatusDot, _statusOffBrush);
                    TaskPollingStatusText.Text = "(未启动)";
                    TaskPollingToggleBtn.Content = "开启";
                    TaskPollingToggleBtn.Background = _toggleOffBrush;
                    TaskPollingToggleBtn.Foreground = Brushes.White;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"更新服务状态失败: {ex.Message}");
            }
        }

        /// <summary>状态点颜色切换时做短暂闪烁动画</summary>
        private void AnimateStatusDot(System.Windows.Shapes.Ellipse dot, SolidColorBrush newBrush)
        {
            if (dot.Fill == newBrush) return;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
            anim.AutoReverse = true;
            dot.BeginAnimation(System.Windows.Shapes.Ellipse.OpacityProperty, anim);
            dot.Fill = newBrush;
        }

        private async void HttpToggle_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (App.HttpApi?.IsRunning == true)
                {
                    await App.HttpApi.StopAsync(CancellationToken.None);
                    AppendLog("HTTP API 已关闭");
                }
                else
                {
                    await App.HttpApi.StartAsync(CancellationToken.None);
                    AppendLog("HTTP API 已开启");
                }
                UpdateServiceStatus();
            }
            catch (Exception ex)
            {
                AppendLog($"HTTP API 切换失败: {ex.Message}");
            }
        }

        private async void MqttToggle_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (App.Mqtt?.IsConnected == true)
                {
                    await App.Mqtt.StopAsync(CancellationToken.None);
                    AppendLog("MQTT 服务已关闭");
                }
                else
                {
                    await App.Mqtt.StartAsync(CancellationToken.None);
                    AppendLog("MQTT 服务已开启");
                }
                UpdateServiceStatus();
            }
            catch (Exception ex)
            {
                AppendLog($"MQTT 服务切换失败: {ex.Message}");
            }
        }

        private void McpToggle_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (App.Mcp != null)
                {
                    App.Mcp.StopAsync(CancellationToken.None).Wait();
                    App.Mcp.Dispose();
                    AppendLog("MCP Server 已关闭");
                }
                else
                {
                    var config = App.Configuration;
                    App.Mcp = new WeChatAutomation.Core.Services.McpServerService(App.ScriptExecutor, config);
                    App.Mcp.StartAsync(CancellationToken.None);
                    AppendLog("MCP Server 已开启");
                }
                UpdateServiceStatus();
            }
            catch (Exception ex)
            {
                AppendLog($"MCP Server 切换失败: {ex.Message}");
            }
        }

        private async void TaskPollingToggle_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (App.TaskPolling?.IsRunning == true)
                {
                    await App.TaskPolling.StopAsync(CancellationToken.None);
                    AppendLog("任务轮询服务已关闭");
                }
                else
                {
                    await App.TaskPolling.StartManual(CancellationToken.None);
                    if (App.TaskPolling.IsRunning)
                        AppendLog("任务轮询服务已开启");
                    else
                        AppendLog("任务轮询服务启动失败，请检查 ServerUrl 配置");
                }
                UpdateServiceStatus();
            }
            catch (Exception ex)
            {
                AppendLog($"任务轮询服务切换失败: {ex.Message}");
            }
        }

        private void TaskPollingConfig_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var config = App.Configuration;
                var currentUrl = config.GetValue("TaskPolling:ServerUrl", "http://localhost:6621");
                var currentPoll = config.GetValue("TaskPolling:PollIntervalSeconds", 10);
                var currentHeartbeat = config.GetValue("TaskPolling:HeartbeatIntervalSeconds", 30);
                var currentClientId = App.TaskPolling?.ClientId ?? config.GetValue("TaskPolling:ClientId", "");
                var currentAutoStart = config.GetValue("TaskPolling:AutoStart", false);

                var w = new Window
                {
                    Title = "任务轮询配置",
                    Width = 420, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this, ResizeMode = ResizeMode.NoResize
                };

                var sp = new StackPanel { Margin = new Thickness(15) };

                // 服务器地址
                sp.Children.Add(new TextBlock { Text = "远程服务器地址:", Margin = new Thickness(0, 0, 0, 3) });
                var serverUrlBox = new TextBox { Text = currentUrl, Margin = new Thickness(0, 0, 0, 8), ToolTip = "远程任务服务器 Base URL" };
                sp.Children.Add(serverUrlBox);

                // 轮询间隔
                var pollPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                pollPanel.Children.Add(new TextBlock { Text = "轮询间隔(秒):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                var pollBox = new TextBox { Text = currentPoll.ToString(), Width = 60 };
                pollPanel.Children.Add(pollBox);
                pollPanel.Children.Add(new TextBlock { Text = "心跳间隔(秒):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 5, 0) });
                var heartbeatBox = new TextBox { Text = currentHeartbeat.ToString(), Width = 60 };
                pollPanel.Children.Add(heartbeatBox);
                sp.Children.Add(pollPanel);

                // Client ID
                sp.Children.Add(new TextBlock { Text = "Client ID:", Margin = new Thickness(0, 0, 0, 3) });
                var clientIdBox = new TextBox { Text = currentClientId, Margin = new Thickness(0, 0, 0, 3), ToolTip = "客户端唯一标识，留空自动生成" };
                sp.Children.Add(clientIdBox);
                sp.Children.Add(new TextBlock { Text = "留空自动生成并持久化到 client_id.txt", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) });

                // 自动开启
                var autoStartCheckBox = new CheckBox { Content = "自动开启轮询（应用启动后自动运行）", IsChecked = currentAutoStart, Margin = new Thickness(0, 0, 0, 8) };
                sp.Children.Add(autoStartCheckBox);

                // 当前状态
                if (App.TaskPolling?.IsRunning == true)
                {
                    sp.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 8) });
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"当前状态: 运行中 → {App.TaskPolling.ServerUrl}\n已执行: ✅{App.TaskPolling.TotalCompleted} ❌{App.TaskPolling.TotalFailed}  缓存: {App.TaskPolling.CachedResultCount}条",
                        FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8)
                    });
                }

                // 按钮
                var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var ok = new Button { Content = "保存并重启", IsDefault = true, Padding = new Thickness(12, 5, 12, 5), FontWeight = FontWeights.Bold };
                var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
                bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

                w.Content = sp;

                ok.Click += async (_, _) =>
                {
                    var newUrl = serverUrlBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(newUrl)) { MessageBox.Show("服务器地址不能为空"); return; }

                    // 写入 appsettings.json
                    try
                    {
                        var configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                        string json;
                        if (System.IO.File.Exists(configPath))
                            json = System.IO.File.ReadAllText(configPath);
                        else
                            json = "{}";

                        var doc = System.Text.Json.JsonDocument.Parse(json);
                        using var stream = new System.IO.MemoryStream();
                        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });

                        writer.WriteStartObject();
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "TaskPolling")
                            {
                                writer.WritePropertyName("TaskPolling");
                                writer.WriteStartObject();
                                writer.WriteBoolean("AutoStart", autoStartCheckBox.IsChecked == true);
                                writer.WriteString("ServerUrl", newUrl);
                                if (int.TryParse(pollBox.Text, out int pi)) writer.WriteNumber("PollIntervalSeconds", pi);
                                if (int.TryParse(heartbeatBox.Text, out int hi)) writer.WriteNumber("HeartbeatIntervalSeconds", hi);
                                var cid = clientIdBox.Text.Trim();
                                if (!string.IsNullOrEmpty(cid)) writer.WriteString("ClientId", cid);
                                // 保留 TaskTypeMapping
                                if (prop.Value.TryGetProperty("TaskTypeMapping", out var mapping))
                                {
                                    writer.WritePropertyName("TaskTypeMapping");
                                    mapping.WriteTo(writer);
                                }
                                writer.WriteEndObject();
                            }
                            else
                            {
                                prop.WriteTo(writer);
                            }
                        }
                        // 如果原来没有 TaskPolling 节
                        if (!doc.RootElement.TryGetProperty("TaskPolling", out _))
                        {
                            writer.WritePropertyName("TaskPolling");
                            writer.WriteStartObject();
                            writer.WriteBoolean("AutoStart", autoStartCheckBox.IsChecked == true);
                            writer.WriteString("ServerUrl", newUrl);
                            if (int.TryParse(pollBox.Text, out int pi)) writer.WriteNumber("PollIntervalSeconds", pi);
                            if (int.TryParse(heartbeatBox.Text, out int hi)) writer.WriteNumber("HeartbeatIntervalSeconds", hi);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                        writer.Flush();

                        json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                        System.IO.File.WriteAllText(configPath, json);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存配置失败: {ex.Message}");
                        return;
                    }

                    // 重启轮询服务
                    if (App.TaskPolling?.IsRunning == true)
                    {
                        await App.TaskPolling.StopAsync(CancellationToken.None);
                    }

                    // 重新加载配置
                    ((IConfigurationRoot)App.Configuration).Reload();
                    await App.TaskPolling.StartManual(CancellationToken.None);

                    w.DialogResult = true;
                };

                if (w.ShowDialog() == true)
                {
                    UpdateServiceStatus();
                    AppendLog($"任务轮询配置已更新 → {serverUrlBox.Text.Trim()}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"配置编辑失败: {ex.Message}");
            }
        }

        // ═══ 任务列表 ═══

        private readonly ObservableCollection<TaskHistoryDisplayItem> _taskHistoryItems = new();

        private void RefreshTaskHistory_Click(object s, RoutedEventArgs e) => RefreshTaskHistory();

        private void ClearTaskHistory_Click(object s, RoutedEventArgs e)
        {
            try
            {
                App.TaskPolling?.ClearHistory();
                _taskHistoryItems.Clear();
                TaskPollingCountText.Text = "";
                AppendLog("任务历史已清除");
            }
            catch (Exception ex) { AppendLog($"清除任务历史失败: {ex.Message}"); }
        }

        private void TaskHistoryList_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (TaskHistoryList.SelectedItem is TaskHistoryDisplayItem item)
                ShowTaskDetail(item);
        }

        public void RefreshTaskHistory()
        {
            try
            {
                var polling = App.TaskPolling;
                if (polling == null) return;

                var history = polling.TaskHistory.ToList();
                _taskHistoryItems.Clear();

                // 按时间倒序（最新的在最上面）
                foreach (var h in history.OrderByDescending(h => h.StartedAt))
                {
                    _taskHistoryItems.Add(new TaskHistoryDisplayItem
                    {
                        TaskId = h.TaskId,
                        TaskType = h.TaskType,
                        Description = h.Description ?? h.TaskType,
                        Status = h.Status,
                        Message = h.Message ?? "",
                        Duration = h.Duration,
                        StartedAt = h.StartedAt,
                        FinishedAt = h.FinishedAt,
                        Params = h.Params,
                        StatusIcon = h.Status switch
                        {
                            "running" => "🔄",
                            "completed" => "✅",
                            "failed" => "❌",
                            _ => "⏳"
                        },
                        DisplayText = !string.IsNullOrEmpty(h.Description) ? h.Description : $"{h.TaskType} ({(h.TaskId != null && h.TaskId.Length >= 8 ? h.TaskId[..8] : h.TaskId ?? "")})",
                        DetailText = h.Status == "running" ? "执行中..." :
                                     h.FinishedAt.HasValue ? $"{h.Status} · {h.StartedAt:HH:mm:ss}" :
                                     $"{h.Status} · {h.StartedAt:HH:mm:ss}",
                        DurationText = h.Duration > 0 ? $"{h.Duration:F1}s" : ""
                    });
                }

                TaskHistoryList.ItemsSource = _taskHistoryItems;
                var completed = polling.TotalCompleted;
                var failed = polling.TotalFailed;
                TaskPollingCountText.Text = history.Count > 0 ? $"✅{completed} ❌{failed}" : "";
            }
            catch { }
        }

        private void ShowTaskDetail(TaskHistoryDisplayItem item)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"任务ID: {item.TaskId}");
            sb.AppendLine($"类型: {item.TaskType}");
            sb.AppendLine($"状态: {item.Status}");
            if (!string.IsNullOrEmpty(item.Description))
                sb.AppendLine($"描述: {item.Description}");
            sb.AppendLine($"开始: {item.StartedAt:yyyy-MM-dd HH:mm:ss}");
            if (item.FinishedAt.HasValue)
                sb.AppendLine($"完成: {item.FinishedAt:yyyy-MM-dd HH:mm:ss}");
            if (item.Duration > 0)
                sb.AppendLine($"耗时: {item.Duration:F2}s");
            if (!string.IsNullOrEmpty(item.Message))
                sb.AppendLine($"消息: {item.Message}");
            if (item.Params?.Count > 0)
            {
                sb.AppendLine("参数:");
                foreach (var kvp in item.Params)
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
            }

            var w = new Window
            {
                Title = $"任务详情 - {item.TaskId}",
                Width = 400, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };
            var sp = new StackPanel { Margin = new Thickness(15) };
            sp.Children.Add(new TextBlock
            {
                Text = sb.ToString().TrimEnd(),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            var closeBtn = new Button
            {
                Content = "关闭",
                IsCancel = true,
                Padding = new Thickness(15, 5, 15, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            closeBtn.Click += (_, _) => w.Close();
            sp.Children.Add(closeBtn);
            w.Content = sp;
            w.ShowDialog();
        }

        // ═══ 动态参数管理 ═══
        private void AddInputParam_Click(object s, RoutedEventArgs e)
        {
            var param = ShowAddParamDialog();
            if (param != null)
            {
                var node = new RecordedAction
                {
                    Order = _steps.Count + 1,
                    ActionType = ActionType.InputParam,
                    Name = $"参数: {param.DisplayName ?? param.Name}",
                    ParameterName = param.Name,
                    DefaultValue = param.DefaultValue,
                    IsRequired = param.IsRequired,
                    CopyToClipboard = param.CopyToClipboard,
                    Parameter = $"{{{param.Name}}}"
                };
                InsertAfterSelected(node);
                AppendLog($"已添加参数步骤: {param.Name}");
            }
        }

        private void AddIf_Click(object s, RoutedEventArgs e)
        {
            try
            {
                // 创建临时 node 复用编辑对话框逻辑（isNew 标识添加 vs 编辑）
                var node = new RecordedAction
                {
                    Order = _steps.Count + 1,
                    ActionType = ActionType.If,
                    Name = "判断",
                    TrueActions = new List<RecordedAction>(),
                    FalseActions = new List<RecordedAction>()
                };

                if (ShowIfDialogCore(node, isNew: true))
                {
                    InsertAfterSelected(node);
                    AppendLog($"已添加判断步骤: {node.Name?.Replace("判断: ", "")}");
                }
            }
            catch (Exception ex) { AppendLog($"添加判断步骤失败: {ex.Message}"); }
        }

        /// <summary>
        /// 判断步骤添加/编辑共用对话框。返回 true 表示用户确认。
        /// </summary>
        private bool ShowIfDialogCore(RecordedAction node, bool isNew)
        {
            var w = new Window
            {
                Title = isNew ? "添加判断步骤" : $"编辑判断步骤 #{node.Order}",
                Width = 460, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };

            // 收集可用变量
            var availableVars = new List<string>();
            foreach (var step in _steps)
            {
                if (!string.IsNullOrEmpty(step.OutputParamName) && !availableVars.Contains(step.OutputParamName))
                    availableVars.Add(step.OutputParamName);
            }
            if (_steps.Any(s => s.ActionType == ActionType.Click))
                availableVars.Add("last_click_success");

            // 判断来源变量
            sp.Children.Add(new TextBlock { Text = "判断来源变量 (可选):", Margin = new Thickness(0, 0, 0, 3) });
            sp.Children.Add(new TextBlock
            {
                Text = "选择阅读/正则步骤输出的变量，或点击步骤的 last_click_success。留空条件表达式时，判断该变量是否有值；填了条件表达式时，用 {变量名} 引用。",
                FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap
            });
            var sourceVarCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), IsEditable = true };
            sourceVarCombo.Items.Add("(无)");
            int srcSel = 0;
            for (int i = 0; i < availableVars.Count; i++)
            {
                sourceVarCombo.Items.Add(availableVars[i]);
                if (!isNew && availableVars[i] == node.OutputParamName) srcSel = i + 1;
            }
            sourceVarCombo.SelectedIndex = srcSel;
            sp.Children.Add(sourceVarCombo);

            // 条件表达式
            sp.Children.Add(new TextBlock { Text = "条件表达式 (可选):", Margin = new Thickness(0, 0, 0, 3) });
            sp.Children.Add(new TextBlock
            {
                Text = "示例: {last_click_success} == true   {found} == true   {count} > 0   {content} contains '已添加'   {text} matches '\\d+'",
                FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap
            });
            var exprBox = new TextBox { Text = isNew ? "" : node.ConditionExpression ?? "", Margin = new Thickness(0, 0, 0, 10), ToolTip = "判断条件，支持 == != > < >= <= contains matches。留空则按上方'判断来源变量'是否有值判断" };
            sp.Children.Add(exprBox);

            // ── 条件成立时行为 ──
            sp.Children.Add(new Border
            {
                Background = _ifTrueBgBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 5, 8, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = BuildBranchConfigPanel("条件成立时 (✓ True)", node, true, out var trueCombo, out var trueScriptCombo)
            });

            // ── 条件不成立时行为 ──
            sp.Children.Add(new Border
            {
                Background = _ifFalseBgBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 5, 8, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = BuildBranchConfigPanel("条件不成立时 (✗ False)", node, false, out var falseCombo, out var falseScriptCombo)
            });

            // 按钮
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = isNew ? "添加" : "确定", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

            w.Content = sp;

            bool result = false;
            ok.Click += (_, _) =>
            {
                string expr = exprBox.Text.Trim();
                string sourceVar = sourceVarCombo.SelectedIndex > 0
                    ? (sourceVarCombo.SelectedItem as string ?? sourceVarCombo.Text.Trim())
                    : sourceVarCombo.Text.Trim();
                if (sourceVar == "(无)" || string.IsNullOrEmpty(sourceVar)) sourceVar = null;

                if (string.IsNullOrEmpty(expr) && string.IsNullOrEmpty(sourceVar))
                {
                    MessageBox.Show("请填写条件表达式或选择判断来源变量");
                    return;
                }

                ReadBranchConfig(trueCombo, trueScriptCombo, node, true);
                ReadBranchConfig(falseCombo, falseScriptCombo, node, false);

                node.ConditionExpression = string.IsNullOrEmpty(expr) ? null : expr;
                node.OutputParamName = sourceVar;
                node.TrueActions ??= new List<RecordedAction>();
                node.FalseActions ??= new List<RecordedAction>();
                string dispName = !string.IsNullOrEmpty(expr) ? expr : (!string.IsNullOrEmpty(sourceVar) ? $"变量 {{{sourceVar}}} 是否有值" : "判断");
                node.Name = $"判断: {dispName}";
                result = true;
                w.DialogResult = true;
            };

            w.ShowDialog();
            return result;
        }

        private void AddGoto_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (_steps.Count == 0) { AppendLog("没有步骤可跳转"); return; }

                var w = new Window
                {
                    Title = "添加跳转步骤",
                    Width = 350, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this, ResizeMode = ResizeMode.NoResize
                };

                var sp = new StackPanel { Margin = new Thickness(15) };

                sp.Children.Add(new TextBlock { Text = "跳转目标:", Margin = new Thickness(0, 0, 0, 3) });
                var targetCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var step in _steps)
                    targetCombo.Items.Add($"#{step.Order} [{step.NodeId}] {step.Summary}");
                targetCombo.SelectedIndex = 0;
                sp.Children.Add(targetCombo);

                var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var ok = new Button { Content = "添加", IsDefault = true, Padding = new Thickness(12, 5, 12, 5) };
                var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
                bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

                w.Content = sp;

                ok.Click += (_, _) =>
                {
                    var targetStep = _steps[targetCombo.SelectedIndex];
                    var node = new RecordedAction
                    {
                        Order = _steps.Count + 1,
                        ActionType = ActionType.Goto,
                        Name = $"跳转 → #{targetStep.Order} {targetStep.Summary}",
                        GotoNodeId = targetStep.NodeId
                    };
                    InsertAfterSelected(node);
                    AppendLog($"已添加跳转步骤 -> #{targetStep.Order}");
                    w.DialogResult = true;
                };

                w.ShowDialog();
            }
            catch (Exception ex) { AppendLog($"添加跳转步骤失败: {ex.Message}"); }
        }

        private void AddSwitchToWindow_Click(object s, RoutedEventArgs e)
        {
            try
            {
                // 切窗专用选择器：带"打开全部同名窗口"开关
                var (processName, switchAll) = ShowSwitchWindowPicker();
                if (string.IsNullOrEmpty(processName)) return;

                var node = new RecordedAction
                {
                    Order = _steps.Count + 1,
                    ActionType = ActionType.SwitchToWindow,
                    Name = switchAll ? $"打开全部窗口: {processName}" : $"切换窗口: {processName}",
                    WindowTitle = processName,
                    Parameter = processName,
                    SwitchAll = switchAll
                };
                InsertAfterSelected(node);
                AppendLog($"已添加切窗步骤: {processName}{(switchAll ? " (打开全部)" : "")}");
            }
            catch (Exception ex) { AppendLog($"添加切窗步骤失败: {ex.Message}"); }
        }

        // ═══ 循环 / 容错 / HTTP 控制流步骤 ═══

        private void AddWhile_Click(object s, RoutedEventArgs e)
        {
            var expr = ShowInput("While 循环", "循环条件 (如 {found} == true，留空按变量有值):", "");
            var node = new RecordedAction
            {
                Order = _steps.Count + 1,
                ActionType = ActionType.While,
                Name = "While循环",
                ConditionExpression = string.IsNullOrWhiteSpace(expr) ? null : expr.Trim(),
                TrueActions = new List<RecordedAction>()
            };
            InsertAfterSelected(node);
            AppendLog("已添加 While 循环（循环体请在流程图窗口编辑）");
        }

        private void AddLoop_Click(object s, RoutedEventArgs e)
        {
            var n = ShowInput("循环 N 次", "次数:", "3");
            if (!int.TryParse(n, out int count) || count <= 0) return;
            var node = new RecordedAction
            {
                Order = _steps.Count + 1,
                ActionType = ActionType.Loop,
                Name = $"循环{count}次",
                LoopCount = count,
                TrueActions = new List<RecordedAction>()
            };
            InsertAfterSelected(node);
            AppendLog($"已添加循环 {count} 次（循环体请在流程图窗口编辑）");
        }

        private void AddTry_Click(object s, RoutedEventArgs e)
        {
            var node = new RecordedAction
            {
                Order = _steps.Count + 1,
                ActionType = ActionType.Try,
                Name = "容错Try",
                TrueActions = new List<RecordedAction>(),
                FalseActions = new List<RecordedAction>()
            };
            InsertAfterSelected(node);
            AppendLog("已添加容错 Try（Try/Catch 体请在流程图窗口编辑）");
        }

        private void AddBreak_Click(object s, RoutedEventArgs e)
        {
            var node = new RecordedAction { Order = _steps.Count + 1, ActionType = ActionType.Break, Name = "跳出循环" };
            InsertAfterSelected(node);
            AppendLog("已添加 Break（跳出循环）");
        }

        private void AddContinue_Click(object s, RoutedEventArgs e)
        {
            var node = new RecordedAction { Order = _steps.Count + 1, ActionType = ActionType.Continue, Name = "下一轮" };
            InsertAfterSelected(node);
            AppendLog("已添加 Continue（进入下一轮）");
        }

        private void AddHttpWait_Click(object s, RoutedEventArgs e)
        {
            var key = ShowInput("等待 HTTP 触发", "唯一 key（外部 POST /api/wait/{key} 唤醒）:", "wait1");
            if (string.IsNullOrWhiteSpace(key)) return;
            var varName = ShowInput("等待 HTTP 触发", "传入内容写入变量名（可留空）:", "");
            var node = new RecordedAction
            {
                Order = _steps.Count + 1,
                ActionType = ActionType.HttpWait,
                Name = $"等待HTTP[{key.Trim()}]",
                WaitKey = key.Trim(),
                ResponseVarName = string.IsNullOrWhiteSpace(varName) ? null : varName.Trim()
            };
            InsertAfterSelected(node);
            AppendLog($"已添加等待HTTP步骤 key={key.Trim()}");
        }

        private void AddHttpCall_Click(object s, RoutedEventArgs e)
        {
            var url = ShowInput("调用 HTTP", "URL:", "https://");
            if (string.IsNullOrWhiteSpace(url)) return;
            var varName = ShowInput("调用 HTTP", "响应写入变量名（可留空）:", "");
            var node = new RecordedAction
            {
                Order = _steps.Count + 1,
                ActionType = ActionType.HttpCall,
                Name = $"调用 {url.Trim()}",
                HttpUrl = url.Trim(),
                HttpMethod = "GET",
                ResponseVarName = string.IsNullOrWhiteSpace(varName) ? null : varName.Trim()
            };
            InsertAfterSelected(node);
            AppendLog($"已添加调用HTTP步骤 {url.Trim()}");
        }

        /// <summary>
        /// 切窗步骤专用窗口选择器，返回 (进程名, 是否打开全部同名窗口)
        /// </summary>
        private (string, bool) ShowSwitchWindowPicker()
        {
            var w = new Window
            {
                Title = "切换窗口", Width = 380, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(12) };

            sp.Children.Add(new TextBlock { Text = "选择已运行的进程，或手动输入进程名:", Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.Bold });

            // 进程下拉框
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), DisplayMemberPath = "Display" };
            var processes = System.Diagnostics.Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle) || p.ProcessName is not ("Idle" or "System" or "csrss" or "smss" or "lsass" or "wininit" or "services" or "svchost" or "dwm" or "conhost"))
                .OrderBy(p => p.ProcessName)
                .Select(p => new { Display = $"{p.ProcessName} - {p.MainWindowTitle}", Name = p.ProcessName })
                .DistinctBy(p => p.Name)
                .ToList();
            combo.ItemsSource = processes;
            if (processes.Count > 0) combo.SelectedIndex = 0;
            sp.Children.Add(combo);

            sp.Children.Add(new TextBlock { Text = "或手动输入进程名:", Margin = new Thickness(0, 0, 0, 3) });
            var manualBox = new TextBox { Margin = new Thickness(0, 0, 0, 8), ToolTip = "如 notepad, chrome, Weixin" };
            sp.Children.Add(manualBox);

            // 打开全部开关
            var switchAllCheck = new CheckBox
            {
                Content = "打开全部同名窗口（恢复显示所有匹配窗口并置顶）",
                Margin = new Thickness(0, 4, 0, 8),
                ToolTip = "勾选后，会把该进程名的所有窗口都恢复显示并置顶；不勾选则只切换主窗口到前台"
            };
            sp.Children.Add(switchAllCheck);

            // 按钮
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok); bp.Children.Add(cancel); sp.Children.Add(bp);

            w.Content = sp;

            string result = null;
            ok.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(manualBox.Text))
                    result = manualBox.Text.Trim();
                else if (combo.SelectedItem != null)
                    result = ((dynamic)combo.SelectedItem).Name;
                w.DialogResult = true;
            };

            return w.ShowDialog() == true ? (result, switchAllCheck.IsChecked == true) : (null, false);
        }

        private void FlowChartBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var chart = new FlowChartWindow(_recorder, _player, _currentClickMode, null)
                {
                    Owner = this
                };
                chart.ShowDialog();
                RefreshStepTree();
                PlayBtn.IsEnabled = _steps.Count > 0;
            }
            catch (Exception ex) { AppendLog($"打开流程图失败: {ex.Message}"); }
        }

        private void ParseCommand_Click(object s, RoutedEventArgs e)
        {
            string command = CommandInput?.Text?.Trim();
            if (string.IsNullOrEmpty(command))
            {
                AppendLog("请输入自然语言命令");
                return;
            }

            try
            {
                var parser = new LlmCommandParser();
                var actions = parser.ParseCommand(command);
                if (actions.Count == 0)
                {
                    AppendLog($"无法解析命令: {command}");
                    return;
                }

                // 解析出的多个动作作为一组，依次插入选中步骤之后（保持顺序）
                string? afterId = GetSelectedAction()?.NodeId;
                foreach (var action in actions)
                {
                    _recorder.InsertAfter(afterId, action);
                    afterId = action.NodeId;
                }
                RefreshStepTree();
                PlayBtn.IsEnabled = _steps.Count > 0;
                AppendLog($"已解析 {actions.Count} 步: {string.Join(", ", actions.Select(a => a.Summary))}");
                CommandInput.Text = "";
            }
            catch (Exception ex)
            {
                AppendLog($"命令解析失败: {ex.Message}");
            }
        }

        private ScriptParameter ShowAddParamDialog(ScriptParameter existing = null)
        {
            var w = new Window
            {
                Title = existing != null ? "编辑参数" : "添加输入参数",
                Width = 400,
                Height = 430,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };

            sp.Children.Add(new TextBlock { Text = "参数 ID (用于 HTTP/MQTT/MCP 传入):", Margin = new Thickness(0, 0, 0, 3) });
            var idBox = new TextBox { Text = existing?.Id ?? Guid.NewGuid().ToString("N")[..8], Margin = new Thickness(0, 0, 0, 8), ToolTip = "自动生成，可通过此 ID 从外部传入参数值" };
            sp.Children.Add(idBox);

            sp.Children.Add(new TextBlock { Text = "参数名称 (英文，用于占位符):", Margin = new Thickness(0, 0, 0, 3) });
            var nameBox = new TextBox { Text = existing?.Name ?? "", Margin = new Thickness(0, 0, 0, 8), ToolTip = "如: message, username" };
            sp.Children.Add(nameBox);

            sp.Children.Add(new TextBlock { Text = "显示名称:", Margin = new Thickness(0, 0, 0, 3) });
            var displayNameBox = new TextBox { Text = existing?.DisplayName ?? "", Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(displayNameBox);

            sp.Children.Add(new TextBlock { Text = "参数类型:", Margin = new Thickness(0, 0, 0, 3) });
            var typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            typeCombo.Items.Add("Text - 文本");
            typeCombo.Items.Add("Number - 数字");
            typeCombo.Items.Add("Password - 密码");
            typeCombo.Items.Add("MultiLine - 多行文本");
            typeCombo.SelectedIndex = existing?.Type switch
            {
                ParameterType.Number => 1,
                ParameterType.Password => 2,
                ParameterType.MultiLine => 3,
                _ => 0
            };
            sp.Children.Add(typeCombo);

            sp.Children.Add(new TextBlock { Text = "默认值:", Margin = new Thickness(0, 0, 0, 3) });
            var defaultBox = new TextBox { Text = existing?.DefaultValue ?? "", Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(defaultBox);

            var requiredBox = new CheckBox { Content = "必填", IsChecked = existing?.IsRequired ?? true, Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(requiredBox);

            var copyToClipboardBox = new CheckBox { Content = "复制到剪切板（执行时将参数值自动复制到剪切板）", IsChecked = existing?.CopyToClipboard ?? false, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(copyToClipboardBox);

            sp.Children.Add(new TextBlock { Text = "说明:", Margin = new Thickness(0, 0, 0, 3) });
            var descBox = new TextBox { Text = existing?.Description ?? "", Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(descBox);

            sp.Children.Add(new TextBlock
            {
                Text = "提示: 在步骤参数中使用 {参数名} 作为占位符\n外部调用可通过参数 ID 或名称传入值",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 5)
            });

            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok);
            bp.Children.Add(cancel);
            sp.Children.Add(bp);

            w.Content = sp;

            ScriptParameter result = null;
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    MessageBox.Show("请输入参数名称");
                    return;
                }

                var paramName = nameBox.Text.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(paramName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                {
                    MessageBox.Show("参数名称只能包含英文字母、数字和下划线，且不能以数字开头");
                    return;
                }

                var paramId = idBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(paramId))
                {
                    paramId = Guid.NewGuid().ToString("N")[..8];
                }

                var typeStr = typeCombo.SelectedItem?.ToString() ?? "Text - 文本";
                var paramType = typeStr switch
                {
                    var s when s.StartsWith("Number") => ParameterType.Number,
                    var s when s.StartsWith("Password") => ParameterType.Password,
                    var s when s.StartsWith("MultiLine") => ParameterType.MultiLine,
                    _ => ParameterType.Text
                };

                result = new ScriptParameter
                {
                    Id = paramId,
                    Name = paramName,
                    DisplayName = displayNameBox.Text.Trim(),
                    DefaultValue = defaultBox.Text,
                    IsRequired = requiredBox.IsChecked == true,
                    CopyToClipboard = copyToClipboardBox.IsChecked == true,
                    Description = descBox.Text,
                    Type = paramType
                };
                w.DialogResult = true;
            };

            w.ShowDialog();
            return result;
        }

        private void ManageParams_Click(object s, RoutedEventArgs e)
        {
            if (_currentScript == null)
            {
                MessageBox.Show("请先选择或保存脚本");
                return;
            }

            try
            {
                var rec = ActionRecorder.LoadFromFile(_currentScript.FilePath);
                ShowManageParamsDialog(rec);
            }
            catch (Exception ex)
            {
                AppendLog($"加载脚本参数失败: {ex.Message}");
            }
        }

        private void ShowManageParamsDialog(RecordingFile recording)
        {
            var w = new Window
            {
                Title = $"管理参数 - {recording.Name}",
                Width = 550,
                Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var mainSp = new StackPanel { Margin = new Thickness(10) };

            // 说明
            mainSp.Children.Add(new TextBlock
            {
                Text = "在脚本步骤中使用 {参数名} 作为占位符，执行时会自动替换",
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // 参数列表
            var paramsList = recording.Parameters ?? new List<ScriptParameter>();
            var listbox = new ListBox { Margin = new Thickness(0, 0, 0, 10), MinHeight = 150 };
            RefreshParamsList(listbox, paramsList);
            mainSp.Children.Add(listbox);

            // 当前脚本中的参数占位符
            var placeholders = new List<string>();
            foreach (var action in recording.Actions)
            {
                if (!string.IsNullOrEmpty(action.Parameter))
                {
                    var matches = System.Text.RegularExpressions.Regex.Matches(action.Parameter, @"\{(\w+)\}");
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        if (!placeholders.Contains(m.Groups[1].Value))
                            placeholders.Add(m.Groups[1].Value);
                    }
                }
                if (!string.IsNullOrEmpty(action.OutputParamName) && !placeholders.Contains(action.OutputParamName))
                {
                    placeholders.Add(action.OutputParamName);
                }
                if (!string.IsNullOrEmpty(action.WindowTitle))
                {
                    var matches = System.Text.RegularExpressions.Regex.Matches(action.WindowTitle, @"\{(\w+)\}");
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        if (!placeholders.Contains(m.Groups[1].Value))
                            placeholders.Add(m.Groups[1].Value);
                    }
                }
            }

            if (placeholders.Count > 0)
            {
                mainSp.Children.Add(new TextBlock
                {
                    Text = $"检测到的占位符: {string.Join(", ", placeholders)}",
                    Foreground = Brushes.Blue,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 10)
                });
            }

            // 按钮
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var addBtn = new Button { Content = "添加", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 5, 0) };
            var editBtn = new Button { Content = "编辑", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 5, 0) };
            var deleteBtn = new Button { Content = "删除", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 5, 0), Foreground = Brushes.Red };
            var closeBtn = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
            bp.Children.Add(addBtn);
            bp.Children.Add(editBtn);
            bp.Children.Add(deleteBtn);
            bp.Children.Add(closeBtn);
            mainSp.Children.Add(bp);

            Action saveAction = () =>
            {
                recording.Parameters = paramsList;
                var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                File.WriteAllText(_currentScript.FilePath, System.Text.Json.JsonSerializer.Serialize(recording, opt));
            };

            addBtn.Click += (_, _) =>
            {
                var param = ShowAddParamDialog();
                if (param != null)
                {
                    paramsList.Add(param);
                    RefreshParamsList(listbox, paramsList);
                    saveAction();
                    AppendLog($"已添加参数: {param.Name}");
                }
            };

            editBtn.Click += (_, _) =>
            {
                if (listbox.SelectedIndex < 0)
                {
                    MessageBox.Show("请先选择要编辑的参数");
                    return;
                }
                var param = paramsList[listbox.SelectedIndex];
                var edited = ShowAddParamDialog(param);
                if (edited != null)
                {
                    paramsList[listbox.SelectedIndex] = edited;
                    RefreshParamsList(listbox, paramsList);
                    saveAction();
                    AppendLog($"已编辑参数: {edited.Name}");
                }
            };

            deleteBtn.Click += (_, _) =>
            {
                if (listbox.SelectedIndex < 0)
                {
                    MessageBox.Show("请先选择要删除的参数");
                    return;
                }
                var paramName = paramsList[listbox.SelectedIndex].Name;
                if (MessageBox.Show($"确定删除参数 \"{paramName}\" 吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    paramsList.RemoveAt(listbox.SelectedIndex);
                    RefreshParamsList(listbox, paramsList);
                    saveAction();
                    AppendLog($"已删除参数: {paramName}");
                }
            };

            w.Content = mainSp;
            w.ShowDialog();
        }

        private void RefreshParamsList(ListBox listbox, List<ScriptParameter> paramsList)
        {
            listbox.Items.Clear();
            foreach (var p in paramsList)
            {
                var typeStr = p.Type switch
                {
                    ParameterType.Number => "数字",
                    ParameterType.Password => "密码",
                    ParameterType.MultiLine => "多行",
                    _ => "文本"
                };
                var required = p.IsRequired ? "必填" : "选填";
                var clipboard = p.CopyToClipboard ? " [→剪切板]" : "";
                listbox.Items.Add($"[{p.Id}] [{typeStr}] {p.Name} ({p.DisplayName ?? p.Name}) = \"{p.DefaultValue}\" ({required}){clipboard}");
            }
        }

        // ═══ 执行时参数输入对话框 ═══
        private Dictionary<string, string> ShowParameterInputDialog(List<ScriptParameter> parameters)
        {
            if (parameters == null || parameters.Count == 0)
                return null;

            var w = new Window
            {
                Title = "输入脚本参数",
                Width = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };
            var inputs = new Dictionary<string, object>(); // TextBox 或 PasswordBox

            foreach (var param in parameters)
            {
                // 标签
                sp.Children.Add(new TextBlock
                {
                    Text = $"{param.DisplayName ?? param.Name}{(param.IsRequired ? " *" : "")}{(param.CopyToClipboard ? " [→剪切板]" : "")}:",
                    Margin = new Thickness(0, 0, 0, 3),
                    FontWeight = param.IsRequired ? FontWeights.Bold : FontWeights.Normal
                });

                // 说明
                if (!string.IsNullOrEmpty(param.Description))
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = param.Description,
                        Foreground = Brushes.Gray,
                        FontSize = 10,
                        Margin = new Thickness(0, 0, 0, 3)
                    });
                }

                // 根据类型创建输入控件
                if (param.Type == ParameterType.Password)
                {
                    var pwdBox = new PasswordBox
                    {
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    if (!string.IsNullOrEmpty(param.DefaultValue))
                        pwdBox.Password = param.DefaultValue;
                    inputs[param.Name] = pwdBox;
                    sp.Children.Add(pwdBox);
                }
                else if (param.Type == ParameterType.MultiLine)
                {
                    var textBox = new TextBox
                    {
                        Text = param.DefaultValue ?? "",
                        Margin = new Thickness(0, 0, 0, 8),
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        MinHeight = 80,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    };
                    inputs[param.Name] = textBox;
                    sp.Children.Add(textBox);
                }
                else
                {
                    var textBox = new TextBox
                    {
                        Text = param.DefaultValue ?? "",
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    inputs[param.Name] = textBox;
                    sp.Children.Add(textBox);
                }
            }

            // 提示信息
            sp.Children.Add(new TextBlock
            {
                Text = "* 表示必填项",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 5, 0, 0)
            });

            // 按钮
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = "确定执行", IsDefault = true, Padding = new Thickness(15, 5, 15, 5), FontWeight = FontWeights.Bold };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(8, 0, 0, 0) };
            bp.Children.Add(ok);
            bp.Children.Add(cancel);
            sp.Children.Add(bp);

            w.Content = sp;

            // 计算窗口高度
            w.SizeToContent = SizeToContent.Height;

            Dictionary<string, string> result = null;
            ok.Click += (_, _) =>
            {
                // 验证必填项
                foreach (var param in parameters)
                {
                    var input = inputs[param.Name];
                    string value = input switch
                    {
                        TextBox tb => tb.Text,
                        PasswordBox pb => pb.Password,
                        _ => ""
                    };

                    if (param.IsRequired && string.IsNullOrWhiteSpace(value))
                    {
                        MessageBox.Show($"请填写必填参数: {param.DisplayName ?? param.Name}");
                        return;
                    }
                }

                // 收集结果
                result = new Dictionary<string, string>();
                foreach (var kvp in inputs)
                {
                    result[kvp.Key] = kvp.Value switch
                    {
                        TextBox tb => tb.Text,
                        PasswordBox pb => pb.Password,
                        _ => ""
                    };
                }
                w.DialogResult = true;
            };

            w.ShowDialog();
            return result;
        }

        // 修改 PlayAll 以支持参数输入
        private async System.Threading.Tasks.Task PlayAllWithParams()
        {
            if (_steps.Count == 0) return;

            // 收集所有需要用户输入的参数
            Dictionary<string, string> parameters = null;

            // 1. 从已保存的脚本文件中获取参数定义
            List<ScriptParameter> paramDefs = null;
            if (_currentScript != null)
            {
                try
                {
                    var rec = ActionRecorder.LoadFromFile(_currentScript.FilePath);
                    if (rec.Parameters != null && rec.Parameters.Count > 0)
                        paramDefs = rec.Parameters;
                }
                catch { }
            }

            // 2. 从步骤列表中的 InputParam 步骤提取参数（覆盖未保存的参数）
            var stepParams = _steps
                .Where(s => s.ActionType == ActionType.InputParam && !string.IsNullOrEmpty(s.ParameterName))
                .Select(s => new ScriptParameter
                {
                    Name = s.ParameterName,
                    DisplayName = s.Name?.Replace("参数: ", "").Replace("输入参数: ", "") ?? s.ParameterName,
                    DefaultValue = s.DefaultValue ?? s.Parameter?.TrimStart('{').TrimEnd('}'),
                    IsRequired = s.IsRequired,
                    Type = ParameterType.Text,
                    CopyToClipboard = s.CopyToClipboard,
                    Description = ""
                })
                .ToList();

            // 合并：步骤中的参数优先（因为可能包含未保存的更改）
            if (stepParams.Count > 0)
            {
                paramDefs ??= new List<ScriptParameter>();
                foreach (var sp in stepParams)
                {
                    // 如果参数定义中已有同名参数，用步骤中的覆盖
                    var existingIdx = paramDefs.FindIndex(p => p.Name == sp.Name);
                    if (existingIdx >= 0)
                        paramDefs[existingIdx] = sp;
                    else
                        paramDefs.Add(sp);
                }
            }

            // 3. 如果有任何参数定义，弹出输入对话框
            if (paramDefs != null && paramDefs.Count > 0)
            {
                parameters = ShowParameterInputDialog(paramDefs);
                if (parameters == null) return; // 用户取消
            }

            PlayBtn.IsEnabled = false;
            StopPlayBtn.IsEnabled = true;

            if (parameters != null && parameters.Count > 0)
            {
                // 有参数时，使用 ScriptExecutor 执行（支持参数替换）
                if (_currentScript != null)
                {
                    var result = await App.ScriptExecutor.ExecuteScript(
                        Path.GetFileNameWithoutExtension(_currentScript.FilePath),
                        parameters);
                    AppendLog(result.Success ? "执行完成" : $"执行失败: {result.Message}");
                }
                else
                {
                    // 未保存的脚本，手动替换参数后直接用 ActionPlayer 执行

                    var resolvedSteps = ReplaceStepParameters(_steps.ToList(), parameters);
                    await _player.Play(resolvedSteps, null);
                }
            }
            else
            {
                await _player.Play(_steps.ToList(), null);
            }

            PlayBtn.IsEnabled = _steps.Count > 0;
            StopPlayBtn.IsEnabled = false;
        }

        /// <summary>
        /// 手动替换步骤中的参数占位符（用于未保存的脚本）
        /// </summary>
        private List<RecordedAction> ReplaceStepParameters(List<RecordedAction> steps, Dictionary<string, string> parameters)
        {
            var result = new List<RecordedAction>();
            foreach (var action in steps)
            {
                var newAction = new RecordedAction
                {
                    NodeId = action.NodeId,
                    Order = action.Order,
                    ActionType = action.ActionType,
                    Name = action.Name ?? "",
                    ClassName = action.ClassName,
                    ElementName = action.ElementName,
                    AutomationId = action.AutomationId,
                    ControlType = action.ControlType,
                    WindowTitle = action.WindowTitle,
                    X = action.X,
                    Y = action.Y,
                    ClickMode = action.ClickMode,
                    VisionLabel = action.VisionLabel,
                    VisionConfThreshold = action.VisionConfThreshold,
                    XPath = action.XPath,
                    SiblingIndex = action.SiblingIndex,
                    RuntimeId = action.RuntimeId,
                    Parameter = action.Parameter ?? "",
                    DelayMs = action.DelayMs,
                    ScrollAmount = action.ScrollAmount,
                    ParameterName = action.ParameterName,
                    DefaultValue = action.DefaultValue,
                    IsRequired = action.IsRequired,
                    CopyToClipboard = action.CopyToClipboard,
                    RegexPattern = action.RegexPattern,
                    RegexGroup = action.RegexGroup,
                    OutputParamName = action.OutputParamName,
                    IsEnabled = action.IsEnabled,
                    CreatedAt = action.CreatedAt
                };

                foreach (var kvp in parameters)
                {
                    newAction.Parameter = newAction.Parameter.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.Name = newAction.Name.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.WindowTitle = newAction.WindowTitle?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.ElementName = newAction.ElementName?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.XPath = newAction.XPath?.Replace($"{{{kvp.Key}}}", kvp.Value);
                    newAction.VisionLabel = newAction.VisionLabel?.Replace($"{{{kvp.Key}}}", kvp.Value);
                }

                // InputParam 步骤：直接使用参数值
                if (newAction.ActionType == ActionType.InputParam && newAction.CopyToClipboard)
                {
                    foreach (var kvp in parameters)
                    {
                        if (kvp.Key == newAction.ParameterName)
                        {
                            newAction.Parameter = kvp.Value;
                            break;
                        }
                    }
                }

                result.Add(newAction);
            }
            return result;
        }

        // ═══ YOLO 训练向导（已移除：视觉模式改用 OpenCV 模板匹配，无需训练） ═══
    }

    public class ScriptInfo { public string FilePath { get; set; } public string Name { get; set; } public int StepCount { get; set; } public DateTime LastModified { get; set; } }

    public class TaskHistoryDisplayItem
    {
        public string TaskId { get; set; }
        public string TaskType { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public double Duration { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public Dictionary<string, object> Params { get; set; }
        public string StatusIcon { get; set; }
        public string DisplayText { get; set; }
        public string DetailText { get; set; }
        public string DurationText { get; set; }
    }
}
