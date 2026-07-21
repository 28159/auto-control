using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlaUIAutomation = FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using WeChatAutomation.Core.Native;
using WeChatAutomation.Core.Recording;
using WeChatAutomation.Core.Services;

namespace WeChatAutomation.App
{
    public partial class MainWindow : Window
    {
        private readonly ActionRecorder _recorder = new();
        private readonly ActionPlayer _player = new();
        private readonly YoloTrainer _yoloTrainer = new();
        private readonly ObservableCollection<RecordedAction> _steps = new();
        private readonly ObservableCollection<ScriptInfo> _scripts = new();
        private readonly KeyboardHook _hotkeyHook = new();
        private string _scriptsDir;
        private ScriptInfo _currentScript;
        private WeChatAutomation.Core.Recording.ClickMode _currentClickMode = WeChatAutomation.Core.Recording.ClickMode.Coordinate;
        /// <summary>当前脚本绑定的视觉模型文件名（如 yolov8n-ui.onnx）；空表示用默认。</summary>
        private string _currentVisionModel;

        public MainWindow()
        {
            InitializeComponent();

            // XAML 初始化期间 ClickModeRadioButton_Changed 可能在控件未完全创建时触发，
            // 这里确保所有控件就绪后正确设置可见性
            UpdateClickModeUI();

            StepsGrid.ItemsSource = _steps;
            ScriptsListBox.ItemsSource = _scripts;

            try { User32.SetProcessDpiAwareness(2); } catch { try { User32.SetProcessDPIAware(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DPI 设置失败: {ex.Message}"); } }

            _scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
            Directory.CreateDirectory(_scriptsDir);

            _recorder.NodeRecorded += (s, n) => Dispatcher.BeginInvoke(RefreshGrid);
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

                if (_recorder.CaptureTrainingData)
                {
                    try
                    {
                        string capturesDir = _yoloTrainer.CapturesDir;
                        if (Directory.Exists(capturesDir) && Directory.GetFiles(capturesDir, "*.png", SearchOption.AllDirectories).Length > 0)
                        {
                            var result = MessageBox.Show(
                                "录制完成，已采集训练数据。\n是否立即开始 YOLO 训练向导？\n\n（标注 → 训练 → 导出 → 部署）",
                                "训练向导", MessageBoxButton.YesNo, MessageBoxImage.Question);
                            if (result == MessageBoxResult.Yes)
                                OpenYoloWizard();
                        }
                    }
                    catch { }
                }
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

            _yoloTrainer.ProgressChanged += (s, p) => Dispatcher.BeginInvoke(() =>
            {
                YoloProgress.Value = p.Value * 100;
                YoloProgressText.Text = p.Message;
            });
            _yoloTrainer.LogMessage += (s, msg) => Dispatcher.BeginInvoke(() => AppendLog($"[YOLO] {msg}"));
            _yoloTrainer.Completed += (s, r) => Dispatcher.BeginInvoke(() =>
            {
                YoloProgress.Value = r.Success ? 100 : YoloProgress.Value;
                YoloProgressText.Text = r.Success ? "完成" : "";
            });

            LoadScriptsList();
            AppendLog("F9录制 F10确认 F11回放 | 双击步骤可编辑");
            UpdateClickModeUI();
            UpdateVisionModelUI();

            // 初始化服务状态显示
            Dispatcher.BeginInvoke(() => UpdateServiceStatus(), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ═══ 快捷键 ═══
        private void OnHotKey(int vk)
        {
            if (vk == User32.VK_F9) { if (_recorder.IsRecording) StopRecording(); else StartRecording(); }
            else if (vk == User32.VK_F10) _recorder.ConfirmStep();
            else if (vk == User32.VK_F11) { if (_player.IsPlaying) _player.Stop(); else _ = PlayAll(); }
        }

        // ═══ 录制 ═══
        private void StartBtn_Click(object s, RoutedEventArgs e) => StartRecording();
        private void StopBtn_Click(object s, RoutedEventArgs e) => StopRecording();
        private void StartRecording()
        {
            if (_recorder.IsRecording) return;
            _recorder.CurrentClickMode = _currentClickMode;
            _recorder.CaptureTrainingData = CaptureTrainingCheck.IsChecked == true;
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

            // 视觉专属控件仅在视觉模式下可见（控件可能在 XAML 初始化期间为 null）
            bool isVision = _currentClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision;
            if (CaptureTrainingCheck != null)
                CaptureTrainingCheck.Visibility = isVision ? Visibility.Visible : Visibility.Collapsed;
            if (VisionModelCombo != null)
                VisionModelCombo.Visibility = isVision ? Visibility.Visible : Visibility.Collapsed;
            if (VisionModelLabel != null)
                VisionModelLabel.Visibility = isVision ? Visibility.Visible : Visibility.Collapsed;
        }

        // ═══ 视觉模型选择（每脚本一个，保存复用） ═══

        /// <summary>项目运行目录下 models/*.onnx 文件名列表。</summary>
        private List<string> ListAvailableModels()
        {
            var list = new List<string>();
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
                if (Directory.Exists(dir))
                    list.AddRange(Directory.GetFiles(dir, "*.onnx").Select(Path.GetFileName).OrderBy(n => n));
            }
            catch { }
            return list;
        }

        /// <summary>刷新模型下拉框，保留当前选择。</summary>
        private void UpdateVisionModelUI()
        {
            if (VisionModelCombo == null) return;

            // 暂停 SelectionChanged，避免填充时误触发
            VisionModelCombo.SelectionChanged -= VisionModelCombo_SelectionChanged;
            try
            {
                VisionModelCombo.Items.Clear();
                VisionModelCombo.Items.Add("(默认 yolov8n-ui.onnx)");
                foreach (var m in ListAvailableModels())
                    if (!VisionModelCombo.Items.Contains(m)) VisionModelCombo.Items.Add(m);

                if (string.IsNullOrEmpty(_currentVisionModel))
                    VisionModelCombo.SelectedIndex = 0;
                else
                {
                    int idx = VisionModelCombo.Items.IndexOf(_currentVisionModel);
                    VisionModelCombo.SelectedIndex = idx > 0 ? idx : 0;
                }
            }
            finally { VisionModelCombo.SelectionChanged += VisionModelCombo_SelectionChanged; }
        }

        private void VisionModelCombo_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VisionModelCombo.SelectedIndex <= 0)
                _currentVisionModel = null;
            else
                _currentVisionModel = VisionModelCombo.SelectedItem as string;

            // 切模型后，如果当前是视觉模式，立即预加载新模型
            if (_currentClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision && !string.IsNullOrEmpty(_currentVisionModel))
                _player.EnsureVisionModel(_currentVisionModel);

            // 显示模型元数据（类别数/输入尺寸/类别名），便于判断是否兼容
            string info = DescribeCurrentModel();
            VisionModelCombo.ToolTip = string.IsNullOrEmpty(info)
                ? "选择视觉模式使用的 ONNX 模型（项目 models 目录）"
                : info;

            if (string.IsNullOrEmpty(_currentVisionModel))
            {
                AppendLog("视觉模型: 默认");
            }
            else
            {
                AppendLog($"视觉模型已选择: {_currentVisionModel}（保存脚本后生效）");
                if (!string.IsNullOrEmpty(info)) AppendLog(info);
            }
        }

        /// <summary>
        /// 读取当前已加载模型的元数据，返回多行描述；未加载则尝试加载后读取。
        /// </summary>
        private string DescribeCurrentModel()
        {
            try
            {
                // 若尚未加载则临时加载（仅读取元数据，不阻塞）
                if (!_player.IsVisionReady && !string.IsNullOrEmpty(_currentVisionModel))
                    _player.EnsureVisionModel(_currentVisionModel);

                var det = _player.VisionDetector;
                if (det == null || !det.IsLoaded) return "";

                var labels = det.Labels;
                var (w, h) = det.InputSize;
                string labelNames = labels.Count > 0
                    ? string.Join(", ", labels.Take(8)) + (labels.Count > 8 ? " ..." : "")
                    : "(无)";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"模型: {System.IO.Path.GetFileName(det.ModelPath)}");
                sb.AppendLine($"输入尺寸: {w}x{h}");
                sb.AppendLine($"类别数: {labels.Count}");
                sb.AppendLine($"类别: {labelNames}");
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }


        private void ToggleClickMode_Click(object s, RoutedEventArgs e)
        {
            if (StepsGrid.SelectedItem is not RecordedAction n) { MessageBox.Show("请先选择步骤"); return; }
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
                WeChatAutomation.Core.Recording.ClickMode.Vision => $"视觉点击 {n.VisionLabel ?? "button"}",
                _ => $"点击路径 {n.ElementName ?? n.ClassName ?? n.AutomationId ?? "未知"}"
            };
            RefreshGrid();
            AppendLog($"步骤 #{n.Order} 切换为 {n.ClickMode} 模式");
        }

        private void CaptureTrainingCheck_Changed(object s, RoutedEventArgs e)
        {
            _recorder.CaptureTrainingData = CaptureTrainingCheck.IsChecked == true;
            if (CaptureTrainingCheck.IsChecked == true)
            {
                AppendLog("训练数据采集已开启 - 录制时将自动截图保存到 captures/ 目录");
            }
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
            _recorder.AddManual(node);
            RefreshGrid();
            PlayBtn.IsEnabled = _steps.Count > 0;
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

        private string ExtractTestContent(FlaUIAutomation.AutomationElement element, int depth = 0, int maxDepth = 5)
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
            if (type == ActionType.Click)
            {
                var node = new RecordedAction
                {
                    Order = _steps.Count + 1,
                    ActionType = ActionType.Click,
                    Name = name,
                    Parameter = parameter,
                    DelayMs = delayMs >= 0 ? delayMs : 0,
                    ClickMode = _currentClickMode
                };
                _recorder.AddManual(node);
            }
            else
            {
                _recorder.AddManual(type, parameter, name, delayMs);
            }
            RefreshGrid();
            PlayBtn.IsEnabled = _steps.Count > 0;
        }

        // ═══ 步骤操作 ═══
        private void MoveUp_Click(object s, RoutedEventArgs e)
        {
            if (StepsGrid.SelectedItem is RecordedAction n && n.Order > 1)
            { _recorder.MoveNode(n.NodeId, n.Order - 1); RefreshGrid(); }
        }
        private void MoveDown_Click(object s, RoutedEventArgs e)
        {
            if (StepsGrid.SelectedItem is RecordedAction n && n.Order < _steps.Count)
            { _recorder.MoveNode(n.NodeId, n.Order + 1); RefreshGrid(); }
        }

        // 双击编辑
        private void StepsGrid_MouseDoubleClick(object s, MouseButtonEventArgs e) => EditStep();

        private void Edit_Click(object s, RoutedEventArgs e) => EditStep();

        private void EditStep()
        {
            if (StepsGrid.SelectedItem is not RecordedAction n) return;
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

            var w = new Window
            {
                Title = $"编辑步骤 #{node.Order}",
                Width = 400, Height = 360,
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

            sp.Children.Add(new TextBlock { Text = "名称:", Margin = new Thickness(0, 0, 0, 3) });
            var nameBox = new TextBox { Text = node.Name ?? "", Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(nameBox);

            sp.Children.Add(new TextBlock { Text = "目标窗口标题:", Margin = new Thickness(0, 0, 0, 3) });
            var windowTitleBox = new TextBox { Text = node.WindowTitle ?? "", Margin = new Thickness(0, 0, 0, 8), ToolTip = "留空=当前前台窗口" };
            sp.Children.Add(windowTitleBox);

            sp.Children.Add(new TextBlock { Text = "参数:", Margin = new Thickness(0, 0, 0, 3) });
            var paramBox = new TextBox { Text = node.Parameter ?? "", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 80 };
            sp.Children.Add(paramBox);

            sp.Children.Add(new TextBlock { Text = "延时(毫秒):", Margin = new Thickness(0, 0, 0, 3) });
            var delayBox = new TextBox { Text = node.DelayMs.ToString(), Margin = new Thickness(0, 0, 0, 8), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(delayBox);

            var clickModePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            clickModePanel.Children.Add(new TextBlock { Text = "点击模式:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var clickModeCombo = new ComboBox { Width = 120 };
            clickModeCombo.Items.Add("Coordinate");
            clickModeCombo.Items.Add("UIAPath");
            clickModeCombo.Items.Add("Vision");
            clickModeCombo.SelectedItem = node.ClickMode.ToString();
            clickModePanel.Children.Add(clickModeCombo);
            sp.Children.Add(clickModePanel);

            var visionLabelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            visionLabelPanel.Children.Add(new TextBlock { Text = "视觉标签:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var visionLabelBox = new TextBox { Text = node.VisionLabel ?? "", Width = 120, ToolTip = "YOLO检测目标类别，如: button, send_button, input" };
            visionLabelPanel.Children.Add(visionLabelBox);
            visionLabelPanel.Children.Add(new TextBlock { Text = "置信度:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) });
            var visionConfBox = new TextBox { Text = node.VisionConfThreshold > 0 ? node.VisionConfThreshold.ToString() : "0.5", Width = 50, ToolTip = "检测置信度阈值 (0-1)" };
            visionLabelPanel.Children.Add(visionConfBox);
            sp.Children.Add(visionLabelPanel);

            var coordPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            coordPanel.Children.Add(new TextBlock { Text = "坐标 X:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var xBox = new TextBox { Text = node.X.ToString("F0"), Width = 60, Margin = new Thickness(0, 0, 10, 0) };
            coordPanel.Children.Add(xBox);
            coordPanel.Children.Add(new TextBlock { Text = "Y:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var yBox = new TextBox { Text = node.Y.ToString("F0"), Width = 60 };
            coordPanel.Children.Add(yBox);
            sp.Children.Add(coordPanel);

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
                node.Name = nameBox.Text;
                node.WindowTitle = windowTitleBox.Text.Trim();
                node.Parameter = paramBox.Text;
                if (int.TryParse(delayBox.Text, out int d)) node.DelayMs = d;
                if (double.TryParse(xBox.Text, out double x)) node.X = x;
                if (double.TryParse(yBox.Text, out double y)) node.Y = y;
                if (Enum.TryParse<WeChatAutomation.Core.Recording.ClickMode>(clickModeCombo.SelectedItem?.ToString(), out var cm)) node.ClickMode = cm;
                node.VisionLabel = visionLabelBox.Text.Trim();
                if (float.TryParse(visionConfBox.Text, out float vc) && vc > 0) node.VisionConfThreshold = vc;
                w.DialogResult = true;
            };

            if (w.ShowDialog() == true)
            {
                RefreshGrid();
                AppendLog($"编辑步骤 #{node.Order}: {node.ActionType} {node.Name}");
            }
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
                RefreshGrid();
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
                RefreshGrid();
                AppendLog($"编辑正则识别步骤: {edited.RegexPattern}");
            }
        }

        private void Delete_Click(object s, RoutedEventArgs e)
        {
            if (StepsGrid.SelectedItem is not RecordedAction n) return;
            string nodeId = n.NodeId;
            bool removed = _recorder.RemoveNode(nodeId);
            if (!removed)
            {
                _steps.Remove(n);
                SyncStepsToRecorder();
            }
            RefreshGrid();
            PlayBtn.IsEnabled = _steps.Count > 0;
        }

        private void BatchDelete_Click(object s, RoutedEventArgs e)
        {
            var selected = StepsGrid.SelectedItems.Cast<RecordedAction>().ToList();
            if (selected.Count == 0) { MessageBox.Show("请先选择要删除的步骤"); return; }
            if (MessageBox.Show($"确定删除 {selected.Count} 个步骤吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var nodeIds = selected.Select(n => n.NodeId).ToList();
            int removed = _recorder.RemoveNodes(nodeIds);
            if (removed < selected.Count)
            {
                foreach (var n in selected) _steps.Remove(n);
                SyncStepsToRecorder();
            }
            RefreshGrid();
            PlayBtn.IsEnabled = _steps.Count > 0;
            AppendLog($"已删除 {selected.Count} 个步骤");
        }

        private void RefreshGrid()
        {
            StepsGrid.ItemsSource = null;
            _steps.Clear();
            foreach (var n in _recorder.Nodes) _steps.Add(n);
            StepsGrid.ItemsSource = _steps;
            StepCountText.Text = _steps.Count.ToString();
        }

        private void SyncStepsToRecorder()
        {
            _recorder.ClearNodes();
            foreach (var s in _steps) _recorder.AddManual(s);
        }

        private void StepsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
        }

        private async void RunSingleAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not RecordedAction action) return;
            if (_player.IsPlaying) { AppendLog("正在回放中，请先停止"); return; }

            if (action.ClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision)
            {
                _player.EnsureVisionModel(_currentVisionModel);
            }

            AppendLog($"单独执行步骤 #{action.Order}: {action.Name}");
            try
            {
                PlayBtn.IsEnabled = false; StopPlayBtn.IsEnabled = true;
                await _player.Play(new List<RecordedAction> { action }, _currentVisionModel);
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
            RefreshGrid();
            PlayBtn.IsEnabled = _steps.Count > 0;
            AppendLog($"已删除步骤 #{action.Order}: {action.Name}");
        }

        // ═══ 回放 ═══
        private async void PlayBtn_Click(object s, RoutedEventArgs e) => await PlayAllWithParams();
        private void StopPlayBtn_Click(object s, RoutedEventArgs e) => _player.Stop();
        private async System.Threading.Tasks.Task PlayAll()
        {
            if (_steps.Count == 0) return;

            if (_steps.Any(s => s.ClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision))
            {
                _player.EnsureVisionModel(_currentVisionModel);
            }

            PlayBtn.IsEnabled = false; StopPlayBtn.IsEnabled = true;
            await _player.Play(_steps.ToList(), _currentVisionModel);
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
                    _currentVisionModel = rec.VisionModel;
                    UpdateClickModeUI();
                    UpdateVisionModelUI();
                    PlayBtn.IsEnabled = _steps.Count > 0; StepCountText.Text = _steps.Count.ToString();
                    AppendLog($"已加载: {script.Name} ({_steps.Count} 步, {_currentClickMode}模式)" +
                              (string.IsNullOrEmpty(_currentVisionModel) ? "" : $", 视觉模型: {_currentVisionModel}"));
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

        private void RenameScript_Click(object s, RoutedEventArgs e)
        {
            if (ScriptsListBox.SelectedItem is not ScriptInfo script) { MessageBox.Show("请先选择脚本"); return; }
            var newName = ShowInput("重命名", "新名称:", script.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == script.Name) return;
            if (!IsValidScriptName(newName)) { MessageBox.Show("名称包含非法字符"); return; }
            var newPath = Path.Combine(_scriptsDir, $"{newName}.json");
            if (File.Exists(newPath)) { MessageBox.Show("已存在同名脚本"); return; }
            File.Move(script.FilePath, newPath);
            try { var rec = ActionRecorder.LoadFromFile(newPath); rec.Name = newName;
                var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                File.WriteAllText(newPath, System.Text.Json.JsonSerializer.Serialize(rec, opt)); }
            catch (Exception ex) { AppendLog($"重命名保存失败: {ex.Message}"); }
            LoadScriptsList();
        }

        private void DeleteScript_Click(object s, RoutedEventArgs e)
        {
            if (ScriptsListBox.SelectedItem is not ScriptInfo script) { MessageBox.Show("请先选择脚本"); return; }
            if (MessageBox.Show($"确定删除 \"{script.Name}\" 吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            File.Delete(script.FilePath);
            if (_currentScript == script) { _currentScript = null; CurrentScriptText.Text = "(未命名)"; _steps.Clear(); StepCountText.Text = "0"; }
            LoadScriptsList();
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

            // 获取现有参数（如果有）
            List<ScriptParameter> existingParams = null;
            if (_currentScript != null)
            {
                try
                {
                    var existingRec = ActionRecorder.LoadFromFile(_currentScript.FilePath);
                    existingParams = existingRec.Parameters;
                }
                catch { }
            }

            if (_currentScript != null)
            {
                var rec = new RecordingFile
                {
                    Name = _currentScript.Name,
                    CreatedAt = DateTime.Now,
                    Actions = _steps.ToList(),
                    Parameters = existingParams ?? new List<ScriptParameter>(),
                    DefaultClickMode = _currentClickMode,
                    VisionModel = _currentVisionModel
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
                    VisionModel = _currentVisionModel
                };
                var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(rec, opt));
                LoadScriptsList(); _currentScript = _scripts.FirstOrDefault(x => x.Name == name); CurrentScriptText.Text = name;
            }
        }

        // ═══ 选择窗口 ═══
        private bool PickTargetWindow(string message)
        {
            var tip = new Window
            {
                Title = "选择窗口", Width = 320, Height = 70,
                WindowStyle = WindowStyle.ToolWindow, Topmost = true, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)), Foreground = Brushes.White
            };
            tip.Content = new TextBlock { Text = message, FontSize = 13, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

            this.WindowState = WindowState.Minimized;
            IntPtr captured = IntPtr.Zero;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { timer.Stop(); captured = User32.GetForegroundWindow(); tip.Close(); };
            tip.Loaded += (_, _) => timer.Start();
            tip.ShowDialog();
            this.WindowState = WindowState.Normal; this.Activate();

            if (captured == IntPtr.Zero) { AppendLog("未捕获到窗口"); return false; }
            User32.GetWindowThreadProcessId(captured, out int pid);
            if (pid == System.Diagnostics.Process.GetCurrentProcess().Id) { AppendLog("不能选择自身窗口"); return false; }

            _player.SetTargetWindow(captured);
            int len = User32.GetWindowTextLength(captured);
            if (len > 0) { var sb = new System.Text.StringBuilder(len + 1); User32.GetWindowText(captured, sb, sb.Capacity); AppendLog($"已选择: {sb}"); }
            return true;
        }

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
                    HttpStatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                    HttpPortText.Text = $":{App.HttpApi.Port}";
                    HttpToggleBtn.Content = "关闭";
                    HttpToggleBtn.Background = new SolidColorBrush(Color.FromRgb(229, 57, 53)); // 红色
                    HttpToggleBtn.Foreground = Brushes.White;
                }
                else
                {
                    HttpStatusDot.Fill = new SolidColorBrush(Color.FromRgb(204, 204, 204)); // 灰色
                    HttpPortText.Text = "(未启动)";
                    HttpToggleBtn.Content = "开启";
                    HttpToggleBtn.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                    HttpToggleBtn.Foreground = Brushes.White;
                }

                // MQTT 状态
                if (App.Mqtt?.IsConnected == true)
                {
                    MqttStatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                    MqttStatusText.Text = $":{App.Mqtt.BrokerPort}";
                    MqttToggleBtn.Content = "关闭";
                    MqttToggleBtn.Background = new SolidColorBrush(Color.FromRgb(229, 57, 53)); // 红色
                    MqttToggleBtn.Foreground = Brushes.White;
                }
                else
                {
                    MqttStatusDot.Fill = new SolidColorBrush(Color.FromRgb(204, 204, 204)); // 灰色
                    MqttStatusText.Text = "(未连接)";
                    MqttToggleBtn.Content = "开启";
                    MqttToggleBtn.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                    MqttToggleBtn.Foreground = Brushes.White;
                }

                // MCP 状态
                if (App.Mcp != null)
                {
                    McpStatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                    McpToggleBtn.Content = "关闭";
                    McpToggleBtn.Background = new SolidColorBrush(Color.FromRgb(229, 57, 53)); // 红色
                    McpToggleBtn.Foreground = Brushes.White;
                }
                else
                {
                    McpStatusDot.Fill = new SolidColorBrush(Color.FromRgb(204, 204, 204)); // 灰色
                    McpToggleBtn.Content = "开启";
                    McpToggleBtn.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                    McpToggleBtn.Foreground = Brushes.White;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"更新服务状态失败: {ex.Message}");
            }
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
                _recorder.AddManual(node);
                RefreshGrid();
                PlayBtn.IsEnabled = _steps.Count > 0;
                AppendLog($"已添加参数步骤: {param.Name}");
            }
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

                foreach (var action in actions)
                {
                    _recorder.AddManual(action);
                }
                RefreshGrid();
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

            // 检查是否有参数需要输入
            Dictionary<string, string> parameters = null;
            if (_currentScript != null)
            {
                try
                {
                    var rec = ActionRecorder.LoadFromFile(_currentScript.FilePath);
                    if (rec.Parameters != null && rec.Parameters.Count > 0)
                    {
                        parameters = ShowParameterInputDialog(rec.Parameters);
                        if (parameters == null) return; // 用户取消
                    }
                }
                catch { }
            }

            PlayBtn.IsEnabled = false;
            StopPlayBtn.IsEnabled = true;

            if (parameters != null && _currentScript != null)
            {
                // 使用 ScriptExecutor 执行（支持参数替换）
                var result = await App.ScriptExecutor.ExecuteScript(
                    Path.GetFileNameWithoutExtension(_currentScript.FilePath),
                    parameters);
                AppendLog(result.Success ? "执行完成" : $"执行失败: {result.Message}");
            }
            else
            {
                if (_steps.Any(s => s.ClickMode == WeChatAutomation.Core.Recording.ClickMode.Vision))
                    _player.EnsureVisionModel(_currentVisionModel);
                await _player.Play(_steps.ToList(), _currentVisionModel);
            }

            PlayBtn.IsEnabled = _steps.Count > 0;
            StopPlayBtn.IsEnabled = false;
        }

        // ═══ YOLO 训练向导 ═══

        private void YoloWizardBtn_Click(object s, RoutedEventArgs e) => OpenYoloWizard();

        private void OpenYoloWizard()
        {
            var wizard = new YoloTrainWizard(_yoloTrainer, _player)
            {
                Owner = this
            };
            wizard.ShowDialog();
            if (wizard.DeployCompleted)
            {
                AppendLog("[YOLO] 模型已部署并加载，视觉模式可用");
                YoloProgressText.Text = "已部署";
            }
        }
    }

    public class ScriptInfo { public string FilePath { get; set; } public string Name { get; set; } public int StepCount { get; set; } public DateTime LastModified { get; set; } }
}
