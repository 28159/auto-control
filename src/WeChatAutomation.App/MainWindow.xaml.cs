using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WeChatAutomation.Core.Native;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.App
{
    public partial class MainWindow : Window
    {
        private readonly ActionRecorder _recorder = new();
        private readonly ActionPlayer _player = new();
        private readonly ObservableCollection<RecordedAction> _steps = new();
        private readonly ObservableCollection<ScriptInfo> _scripts = new();
        private readonly KeyboardHook _hotkeyHook = new();
        private string _scriptsDir;
        private ScriptInfo _currentScript;

        public MainWindow()
        {
            InitializeComponent();
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
            });
            _recorder.LogMessage += (s, msg) => AppendLog(msg);

            _player.LogMessage += (s, msg) => Dispatcher.BeginInvoke(() => AppendLog(msg));
            _player.PlayCompleted += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                StopPlayBtn.IsEnabled = false; PlayBtn.IsEnabled = _steps.Count > 0;
                var last = _player.ReadResults.LastOrDefault();
                if (last != null) ReadContentText.Text = $"[{last.CapturedAt:HH:mm:ss}] {last.WindowTitle}\n\n{last.Content}";
                AppendLog("回放完成");
            });

            _hotkeyHook.HotKeyPressed += (s, vk) => Dispatcher.BeginInvoke(() => OnHotKey(vk));
            _hotkeyHook.StartCapture();

            LoadScriptsList();
            AppendLog("F9录制 F10确认 F11回放 | 双击步骤可编辑");
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
            _recorder.Start(RecordMode.Continuous);
        }
        private void StopRecording() { if (_recorder.IsRecording) _recorder.Stop(); }

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

        private void AddOrRun(ActionType type, string parameter = "", string name = "", int delayMs = -1)
        {
            _recorder.AddManual(type, parameter, name, delayMs);
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
            var w = new Window
            {
                Title = $"编辑步骤 #{node.Order}",
                Width = 400, Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(15) };

            // 类型
            sp.Children.Add(new TextBlock { Text = "类型:", Margin = new Thickness(0, 0, 0, 3) });
            var typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
            foreach (ActionType t in Enum.GetValues(typeof(ActionType)))
                typeCombo.Items.Add(t.ToString());
            typeCombo.SelectedItem = node.ActionType.ToString();
            sp.Children.Add(typeCombo);

            // 名称
            sp.Children.Add(new TextBlock { Text = "名称:", Margin = new Thickness(0, 0, 0, 3) });
            var nameBox = new TextBox { Text = node.Name ?? "", Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(nameBox);

            // 参数
            sp.Children.Add(new TextBlock { Text = "参数:", Margin = new Thickness(0, 0, 0, 3) });
            var paramBox = new TextBox { Text = node.Parameter ?? "", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 80 };
            sp.Children.Add(paramBox);

            // 延时
            sp.Children.Add(new TextBlock { Text = "延时(毫秒):", Margin = new Thickness(0, 0, 0, 3) });
            var delayBox = new TextBox { Text = node.DelayMs.ToString(), Margin = new Thickness(0, 0, 0, 8), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(delayBox);

            // 坐标（点击类型才显示）
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
                // 应用修改
                if (Enum.TryParse<ActionType>(typeCombo.SelectedItem?.ToString(), out var newType))
                    node.ActionType = newType;
                node.Name = nameBox.Text;
                node.Parameter = paramBox.Text;
                if (int.TryParse(delayBox.Text, out int d)) node.DelayMs = d;
                if (double.TryParse(xBox.Text, out double x)) node.X = x;
                if (double.TryParse(yBox.Text, out double y)) node.Y = y;
                w.DialogResult = true;
            };

            if (w.ShowDialog() == true)
            {
                RefreshGrid();
                AppendLog($"编辑步骤 #{node.Order}: {node.ActionType} {node.Name}");
            }
        }

        private void Delete_Click(object s, RoutedEventArgs e)
        {
            if (StepsGrid.SelectedItem is RecordedAction n)
            { _recorder.RemoveNode(n.NodeId); RefreshGrid(); PlayBtn.IsEnabled = _steps.Count > 0; }
        }

        private void BatchDelete_Click(object s, RoutedEventArgs e)
        {
            var selected = StepsGrid.SelectedItems.Cast<RecordedAction>().ToList();
            if (selected.Count == 0) { MessageBox.Show("请先选择要删除的步骤"); return; }
            if (MessageBox.Show($"确定删除 {selected.Count} 个步骤吗？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var n in selected) _recorder.RemoveNode(n.NodeId);
            RefreshGrid(); PlayBtn.IsEnabled = _steps.Count > 0;
            AppendLog($"已删除 {selected.Count} 个步骤");
        }

        private void RefreshGrid()
        {
            _steps.Clear();
            foreach (var n in _recorder.Nodes) _steps.Add(n);
            StepCountText.Text = _steps.Count.ToString();
        }

        // ═══ 回放 ═══
        private async void PlayBtn_Click(object s, RoutedEventArgs e) => await PlayAll();
        private void StopPlayBtn_Click(object s, RoutedEventArgs e) => _player.Stop();
        private async System.Threading.Tasks.Task PlayAll()
        {
            if (_steps.Count == 0) return;
            PlayBtn.IsEnabled = false; StopPlayBtn.IsEnabled = true;
            await _player.Play(_steps.ToList());
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
                    PlayBtn.IsEnabled = _steps.Count > 0; StepCountText.Text = _steps.Count.ToString();
                    AppendLog($"已加载: {script.Name} ({_steps.Count} 步)");
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
            if (_currentScript != null)
            {
                var rec = new RecordingFile { Name = _currentScript.Name, CreatedAt = DateTime.Now, Actions = _steps.ToList() };
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
                var rec = new RecordingFile { Name = name, CreatedAt = DateTime.Now, Actions = _steps.ToList() };
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

        // ═══ 阅读保存 ═══
        private void SaveReadContent_Click(object s, RoutedEventArgs e)
        {
            var content = _player.ReadResults.LastOrDefault()?.Content;
            if (string.IsNullOrEmpty(content)) { MessageBox.Show("没有可保存的内容"); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "保存", Filter = "文本|*.txt|所有|*.*", FileName = $"阅读_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
            if (dlg.ShowDialog() == true) { File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8); AppendLog($"已保存: {dlg.FileName}"); }
        }

        private void CopyReadContent_Click(object s, RoutedEventArgs e)
        {
            var content = _player.ReadResults.LastOrDefault()?.Content;
            if (string.IsNullOrEmpty(content)) { MessageBox.Show("没有可复制的内容"); return; }
            Clipboard.SetText(content); AppendLog("已复制");
        }

        protected override void OnClosed(EventArgs e) { _recorder?.Dispose(); _player?.Dispose(); _hotkeyHook?.Dispose(); base.OnClosed(e); }
    }

    public class ScriptInfo { public string FilePath { get; set; } public string Name { get; set; } public int StepCount { get; set; } public DateTime LastModified { get; set; } }
}
