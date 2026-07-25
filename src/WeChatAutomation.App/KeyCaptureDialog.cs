using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WeChatAutomation.App
{
    /// <summary>
    /// 按键学习对话框：按下键盘即捕获为按键字符串（如 Ctrl+A、F5、Enter），
    /// 同时保留手动输入文本框以编辑/输入复杂组合。结果通过 <see cref="Keys"/> 返回。
    /// </summary>
    public class KeyCaptureDialog : Window
    {
        private readonly TextBox _keysBox = new();
        private readonly TextBlock _capturedText = new();

        /// <summary>用户确认的按键字符串（手动编辑后的最终值）。</summary>
        public string Keys => _keysBox.Text;

        public KeyCaptureDialog(string? initial = null)
        {
            Title = "按键学习";
            Width = 380;
            Height = 250;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var sp = new StackPanel { Margin = new Thickness(14) };

            sp.Children.Add(new TextBlock
            {
                Text = "请按下要录制的按键（如 Enter、Ctrl+A、F5、Shift+Down）：",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            _capturedText.Text = string.IsNullOrEmpty(initial) ? "（等待按键…）" : "✅ " + initial;
            _capturedText.FontSize = 16;
            _capturedText.FontWeight = FontWeights.Bold;
            _capturedText.Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
            _capturedText.Margin = new Thickness(0, 0, 0, 10);
            sp.Children.Add(_capturedText);

            sp.Children.Add(new TextBlock { Text = "按键（可手动编辑）:", Margin = new Thickness(0, 0, 0, 3) });
            _keysBox.Text = initial ?? "";
            _keysBox.FontSize = 13;
            sp.Children.Add(_keysBox);

            sp.Children.Add(new TextBlock
            {
                Text = "支持 Enter/F5/Ctrl+A/方向键等；复杂组合如 Ctrl+A+Delete 可手动输入",
                FontSize = 10,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 10)
            });

            var bp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var clear = new Button { Content = "清空", Padding = new Thickness(12, 4, 12, 4) };
            clear.Click += (_, _) =>
            {
                _keysBox.Text = "";
                _capturedText.Text = "（等待按键…）";
                _keysBox.Focus();
            };
            bp.Children.Add(clear);

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(6, 0, 0, 0) };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(6, 0, 0, 0) };
            ok.Click += (_, _) => DialogResult = true;
            right.Children.Add(ok);
            right.Children.Add(cancel);

            var bpWrap = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            bpWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bpWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(clear, 0);
            Grid.SetColumn(right, 1);
            bpWrap.Children.Add(clear);
            bpWrap.Children.Add(right);
            sp.Children.Add(bpWrap);

            Content = sp;

            PreviewKeyDown += Window_PreviewKeyDown;
            Loaded += (_, _) => { _keysBox.Focus(); _keysBox.CaretIndex = _keysBox.Text.Length; };
        }

        // 捕获按键：格式化为 "Ctrl+A" 风格，写入文本框与提示。e.Handled 阻止 Enter/Esc 触发默认按钮。
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            // Alt 组合时 e.Key 为 System，真正键在 SystemKey
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            string? formatted = FormatKey(key, Keyboard.Modifiers);
            if (formatted == null) return; // 纯修饰键，等待下一个非修饰键

            _keysBox.Text = formatted;
            _capturedText.Text = "✅ " + formatted;
            _keysBox.Focus();
            _keysBox.CaretIndex = _keysBox.Text.Length;
        }

        private static string? FormatKey(Key key, ModifierKeys mods)
        {
            string? main = KeyToName(key);
            if (main == null) return null;

            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            parts.Add(main);
            return string.Join("+", parts);
        }

        // WPF Key -> SendKeys 解析器能识别的名字；纯修饰键/不支持的返回 null。
        private static string? KeyToName(Key key)
        {
            switch (key)
            {
                case Key.LeftCtrl: case Key.RightCtrl:
                case Key.LeftShift: case Key.RightShift:
                case Key.LeftAlt: case Key.RightAlt:
                case Key.LWin: case Key.RWin:
                    return null;
                case Key.Enter: return "Enter";
                case Key.Escape: return "Escape";
                case Key.Tab: return "Tab";
                case Key.Space: return "Space";
                case Key.Back: return "Backspace";
                case Key.Delete: return "Delete";
                case Key.Insert: return "Insert";
                case Key.Home: return "Home";
                case Key.End: return "End";
                case Key.PageUp: return "PageUp";
                case Key.PageDown: return "PageDown";
                case Key.Up: return "Up";
                case Key.Down: return "Down";
                case Key.Left: return "Left";
                case Key.Right: return "Right";
            }

            string s = key.ToString();
            // 数字键 D0..D9 -> "0".."9"
            if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1].ToString();
            // 字母键 A..Z
            if (s.Length == 1 && char.IsLetter(s[0])) return s;
            // 功能键 F1..F12（Key 枚举名即为 "F1".."F12"）
            if (s.Length >= 2 && s[0] == 'F' && int.TryParse(s[1..], out int f) && f >= 1 && f <= 12) return s;
            return null;
        }
    }
}
