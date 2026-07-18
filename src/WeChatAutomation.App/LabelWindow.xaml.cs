using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatAutomation.Core.Logging;
using IOPath = System.IO.Path;

namespace WeChatAutomation.App
{
    public partial class LabelWindow : Window
    {
        private static readonly string[] Classes =
        {
            "button", "input", "checkbox", "radio", "dropdown",
            "tab", "menu_item", "icon", "link", "text_field",
            "search_box", "send_button", "close_button", "minimize_button",
            "maximize_button", "scrollbar", "slider", "toggle", "tooltip", "image"
        };

        private static readonly Brush[] ClassBrushes =
        {
            Brushes.Red, Brushes.Blue, Brushes.Green, Brushes.Orange, Brushes.Purple,
            Brushes.Cyan, Brushes.DeepPink, Brushes.Indigo, Brushes.OliveDrab, Brushes.Gold,
            Brushes.Brown, Brushes.Gray, Brushes.Magenta, Brushes.Teal, Brushes.DarkOrange,
            Brushes.MediumPurple, Brushes.DodgerBlue, Brushes.HotPink, Brushes.LimeGreen, Brushes.DarkRed
        };

        private readonly string _imagesDir;
        private readonly string _outputDir;
        private readonly List<string> _imageFiles = new();
        private readonly Dictionary<string, List<Annotation>> _annotations = new();
        private int _currentIdx;
        private int _selectedClass;
        private bool _drawing;
        private Point _drawStart;
        private System.Windows.Shapes.Rectangle _drawRect;
        private double _scaleX;
        private double _scaleY;
        private double _offsetX;
        private double _offsetY;

        public bool Saved { get; private set; }

        public class Annotation
        {
            public int ClassId { get; set; }
            public double Cx { get; set; }
            public double Cy { get; set; }
            public double W { get; set; }
            public double H { get; set; }
        }

        public LabelWindow(string imagesDir, string outputDir)
        {
            Logger.Instance.Info("LabelWindow", $"构造开始: imagesDir={imagesDir}");
            try
            {
            InitializeComponent();
            _imagesDir = imagesDir;
            _outputDir = outputDir;

            Directory.CreateDirectory(outputDir);

            foreach (var ext in new[] { "*.png", "*.jpg", "*.bmp" })
                foreach (var f in Directory.GetFiles(imagesDir, ext))
                    _imageFiles.Add(f);

            Logger.Instance.Info("LabelWindow", $"找到图片 {_imageFiles.Count} 张");
            if (_imageFiles.Count == 0)
            {
                Logger.Instance.Warn("LabelWindow", "未找到图片，提前返回");
                MessageBox.Show("未找到图片文件", "提示");
                return;
            }

            for (int i = 0; i < Classes.Length; i++)
                ClassListBox.Items.Add($"{i}: {Classes[i]}");
            ClassListBox.SelectedIndex = 0;

            LoadAnnotations();
            _currentIdx = 0;
            LoadCurrentImage();

            KeyDown += OnKeyDown;
            Focusable = true;
            Focus();

            // 多显示器 / 小屏幕下 CenterOwner 仍可能把窗口顶出可视区，加载后夹回虚拟屏幕
            Loaded += (s, e) => ClampOntoVirtualScreen();
            Logger.Instance.Info("LabelWindow", "构造完成，等待 ShowDialog 显示");
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("LabelWindow", "构造异常", ex);
                throw;
            }
        }

        private void ClampOntoVirtualScreen()
        {
            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsW = SystemParameters.VirtualScreenWidth;
            double vsH = SystemParameters.VirtualScreenHeight;
            double vsRight = vsLeft + vsW;
            double vsBottom = vsTop + vsH;

            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;

            if (Left < vsLeft) Left = vsLeft;
            if (Top < vsTop) Top = vsTop;
            if (Left + w > vsRight) Left = Math.Max(vsLeft, vsRight - w);
            if (Top + h > vsBottom) Top = Math.Max(vsTop, vsBottom - h);

            Logger.Instance.Info("LabelWindow",
                $"Loaded 定位: left={Left:F0} top={Top:F0} size={w:F0}x{h:F0} " +
                $"virtualScreen=({vsLeft:F0},{vsTop:F0},{vsW:F0}x{vsH:F0}) IsVisible={IsVisible}");
        }

        private void OnKeyDown(object s, KeyEventArgs e)
        {
            // 数字键 0-9：切换类别（最多前 10 类）
            if (e.Key >= Key.D0 && e.Key <= Key.D9)
            {
                int idx = e.Key - Key.D0;
                if (idx < ClassListBox.Items.Count)
                {
                    ClassListBox.SelectedIndex = idx;
                    StatusText.Text = $"类别: {Classes[_selectedClass]}";
                }
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Left:
                case Key.A:
                case Key.P:
                    PrevImage_Click(s, e);
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.D:
                case Key.N:
                    NextImage_Click(s, e);
                    e.Handled = true;
                    break;
                case Key.Delete:
                case Key.Back:
                    DeleteAnnotation_Click(s, e);
                    e.Handled = true;
                    break;
                case Key.S:
                    SaveAll_Click(s, e);
                    e.Handled = true;
                    break;
                case Key.Q:
                case Key.Escape:
                    SaveAnnotations();   // 退出前自动保存，避免丢失
                    Close();
                    e.Handled = true;
                    break;
            }
        }

        private void LoadAnnotations()
        {
            string jsonPath = IOPath.Combine(_outputDir, "annotations.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, List<Annotation>>>(
                        File.ReadAllText(jsonPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (dict != null)
                        foreach (var kvp in dict)
                            _annotations[kvp.Key] = kvp.Value;
                }
                catch { }
            }

            foreach (var imgFile in _imageFiles)
            {
                string name = IOPath.GetFileName(imgFile);
                if (_annotations.ContainsKey(name)) continue;

                string lblPath = IOPath.Combine(_imagesDir, IOPath.GetFileNameWithoutExtension(imgFile) + ".txt");
                var boxes = new List<Annotation>();
                if (File.Exists(lblPath))
                {
                    foreach (var line in File.ReadAllLines(lblPath))
                    {
                        var parts = line.Trim().Split();
                        if (parts.Length == 5 && int.TryParse(parts[0], out int cid)
                            && double.TryParse(parts[1], out double cx)
                            && double.TryParse(parts[2], out double cy)
                            && double.TryParse(parts[3], out double w)
                            && double.TryParse(parts[4], out double h))
                        {
                            boxes.Add(new Annotation { ClassId = cid, Cx = cx, Cy = cy, W = w, H = h });
                        }
                    }
                }
                _annotations[name] = boxes;
            }
        }

        private void ClassListBox_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (ClassListBox.SelectedIndex >= 0)
                _selectedClass = ClassListBox.SelectedIndex;
        }

        private void LoadCurrentImage()
        {
            if (_currentIdx < 0 || _currentIdx >= _imageFiles.Count) return;

            string imgPath = _imageFiles[_currentIdx];
            string imgName = IOPath.GetFileName(imgPath);

            try
            {
                var bmp = new BitmapImage(new Uri(imgPath));
                DisplayImage.Source = bmp;
                DisplayImage.UpdateLayout();
            }
            catch
            {
                StatusText.Text = $"无法加载: {imgName}";
                return;
            }

            ProgressText.Text = $"{_currentIdx + 1} / {_imageFiles.Count}";
            ImageInfoText.Text = $"{imgName}\n标注数: {_annotations.GetValueOrDefault(imgName)?.Count ?? 0}";

            Dispatcher.BeginInvoke(() =>
            {
                ComputeScale();
                RefreshOverlay();
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            StatusText.Text = $"[{_currentIdx + 1}/{_imageFiles.Count}] {imgName} | 类别: {Classes[_selectedClass]}";
        }

        private void ComputeScale()
        {
            if (DisplayImage.Source is not BitmapImage bmp) return;

            double canvasW = ImageCanvas.ActualWidth;
            double canvasH = ImageCanvas.ActualHeight;
            if (canvasW <= 0 || canvasH <= 0) return;

            double imgW = bmp.PixelWidth;
            double imgH = bmp.PixelHeight;
            if (imgW <= 0 || imgH <= 0) return;

            _scaleX = canvasW / imgW;
            _scaleY = canvasH / imgH;
            double scale = Math.Min(_scaleX, _scaleY);
            _scaleX = scale;
            _scaleY = scale;

            double displayW = imgW * scale;
            double displayH = imgH * scale;
            _offsetX = (canvasW - displayW) / 2;
            _offsetY = (canvasH - displayH) / 2;

            Canvas.SetLeft(DisplayImage, _offsetX);
            Canvas.SetTop(DisplayImage, _offsetY);
            DisplayImage.Width = displayW;
            DisplayImage.Height = displayH;

            // 关键：OverlayCanvas 默认 0x0，配合 ClipToBounds 会把画上去的框全裁掉。
            // 让它铺满整个 ImageCanvas，框选框才能显示出来。
            Canvas.SetLeft(OverlayCanvas, 0);
            Canvas.SetTop(OverlayCanvas, 0);
            OverlayCanvas.Width = canvasW;
            OverlayCanvas.Height = canvasH;
        }

        private void RefreshOverlay()
        {
            OverlayCanvas.Children.Clear();

            if (_currentIdx < 0 || _currentIdx >= _imageFiles.Count) return;

            string imgName = IOPath.GetFileName(_imageFiles[_currentIdx]);
            var boxes = _annotations.GetValueOrDefault(imgName);
            if (boxes == null) return;

            AnnotationListBox.Items.Clear();
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                DrawAnnotationBox(b, i == AnnotationListBox.SelectedIndex);

                string cls = b.ClassId < Classes.Length ? Classes[b.ClassId] : $"class_{b.ClassId}";
                AnnotationListBox.Items.Add($"#{i} {cls} ({b.Cx:F2},{b.Cy:F2})");
            }

            ImageInfoText.Text = $"{imgName}\n标注数: {boxes.Count}";
        }

        private void DrawAnnotationBox(Annotation b, bool selected)
        {
            if (DisplayImage.Source is not BitmapImage bmp) return;

            double imgW = bmp.PixelWidth;
            double imgH = bmp.PixelHeight;

            double x1 = (b.Cx - b.W / 2) * imgW * _scaleX + _offsetX;
            double y1 = (b.Cy - b.H / 2) * imgH * _scaleY + _offsetY;
            double w = b.W * imgW * _scaleX;
            double h = b.H * imgH * _scaleY;

            var brush = b.ClassId < ClassBrushes.Length ? ClassBrushes[b.ClassId] : Brushes.White;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, w),
                Height = Math.Max(1, h),
                Stroke = brush,
                StrokeThickness = selected ? 3 : 1.5,
                Fill = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = 0.1 }
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, y1);
            OverlayCanvas.Children.Add(rect);

            string cls = b.ClassId < Classes.Length ? Classes[b.ClassId] : $"class_{b.ClassId}";
            var label = new TextBlock
            {
                Text = cls,
                Foreground = brush,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(label, x1);
            Canvas.SetTop(label, y1 - 14);
            OverlayCanvas.Children.Add(label);
        }

        private void Canvas_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _drawing = true;
            _drawStart = e.GetPosition(ImageCanvas);

            _drawRect = new System.Windows.Shapes.Rectangle
            {
                Stroke = ClassBrushes[_selectedClass % ClassBrushes.Length],
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            Canvas.SetLeft(_drawRect, _drawStart.X);
            Canvas.SetTop(_drawRect, _drawStart.Y);
            OverlayCanvas.Children.Add(_drawRect);
        }

        private void Canvas_MouseMove(object s, MouseEventArgs e)
        {
            if (!_drawing || _drawRect == null) return;

            Point pos = e.GetPosition(ImageCanvas);
            double x = Math.Min(_drawStart.X, pos.X);
            double y = Math.Min(_drawStart.Y, pos.Y);
            double w = Math.Abs(pos.X - _drawStart.X);
            double h = Math.Abs(pos.Y - _drawStart.Y);

            Canvas.SetLeft(_drawRect, x);
            Canvas.SetTop(_drawRect, y);
            _drawRect.Width = Math.Max(1, w);
            _drawRect.Height = Math.Max(1, h);
        }

        private void Canvas_MouseUp(object s, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            _drawing = false;

            if (_drawRect != null)
            {
                OverlayCanvas.Children.Remove(_drawRect);
                _drawRect = null;
            }

            if (DisplayImage.Source is not BitmapImage bmp) return;

            Point pos = e.GetPosition(ImageCanvas);
            double x1 = Math.Min(_drawStart.X, pos.X);
            double y1 = Math.Min(_drawStart.Y, pos.Y);
            double x2 = Math.Max(_drawStart.X, pos.X);
            double y2 = Math.Max(_drawStart.Y, pos.Y);

            double w = x2 - x1;
            double h = y2 - y1;
            if (w < 5 || h < 5) return;

            double imgW = bmp.PixelWidth;
            double imgH = bmp.PixelHeight;

            double normX1 = (x1 - _offsetX) / (_scaleX * imgW);
            double normY1 = (y1 - _offsetY) / (_scaleY * imgH);
            double normX2 = (x2 - _offsetX) / (_scaleX * imgW);
            double normY2 = (y2 - _offsetY) / (_scaleY * imgH);

            normX1 = Math.Max(0, Math.Min(1, normX1));
            normY1 = Math.Max(0, Math.Min(1, normY1));
            normX2 = Math.Max(0, Math.Min(1, normX2));
            normY2 = Math.Max(0, Math.Min(1, normY2));

            double cx = (normX1 + normX2) / 2;
            double cy = (normY1 + normY2) / 2;
            double bw = normX2 - normX1;
            double bh = normY2 - normY1;

            string imgName = IOPath.GetFileName(_imageFiles[_currentIdx]);
            if (!_annotations.ContainsKey(imgName))
                _annotations[imgName] = new List<Annotation>();

            _annotations[imgName].Add(new Annotation
            {
                ClassId = _selectedClass,
                Cx = cx,
                Cy = cy,
                W = bw,
                H = bh
            });

            RefreshOverlay();
            StatusText.Text = $"添加标注: {Classes[_selectedClass]} ({cx:F2},{cy:F2}) | 共 {_annotations[imgName].Count} 个";
        }

        private void PrevImage_Click(object s, RoutedEventArgs e)
        {
            if (_currentIdx <= 0) { StatusText.Text = "已是第一张"; return; }
            _currentIdx--; LoadCurrentImage();
        }

        private void NextImage_Click(object s, RoutedEventArgs e)
        {
            if (_currentIdx >= _imageFiles.Count - 1)
            {
                StatusText.Text = $"已是最后一张（共 {_imageFiles.Count}）— 记得 Ctrl+S 保存";
                return;
            }
            _currentIdx++; LoadCurrentImage();
        }

        private void DeleteAnnotation_Click(object s, RoutedEventArgs e)
        {
            if (_currentIdx < 0 || _currentIdx >= _imageFiles.Count) return;

            string imgName = IOPath.GetFileName(_imageFiles[_currentIdx]);
            var boxes = _annotations.GetValueOrDefault(imgName);
            if (boxes == null || boxes.Count == 0) return;

            // 有选中就删选中；否则删最后一个（避免按 Delete/Backspace 没反应）
            int idx = AnnotationListBox.SelectedIndex;
            if (idx < 0 || idx >= boxes.Count) idx = boxes.Count - 1;
            boxes.RemoveAt(idx);
            RefreshOverlay();
            StatusText.Text = $"删除标注 #{idx + 1}，剩余 {boxes.Count} 个";
        }

        private void AnnotationListBox_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (AnnotationListBox.SelectedIndex >= 0)
                RefreshOverlay();
        }

        private void SaveAll_Click(object s, RoutedEventArgs e)
        {
            SaveAnnotations();
            Saved = true;
            MessageBox.Show($"已保存 {_annotations.Count} 张图片的标注", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveAnnotations()
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            string jsonPath = IOPath.Combine(_outputDir, "annotations.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(_annotations, options));

            foreach (var kvp in _annotations)
            {
                string lblPath = IOPath.Combine(_outputDir, IOPath.GetFileNameWithoutExtension(kvp.Key) + ".txt");
                using var writer = new StreamWriter(lblPath);
                foreach (var b in kvp.Value)
                    writer.WriteLine($"{b.ClassId} {b.Cx:F6} {b.Cy:F6} {b.W:F6} {b.H:F6}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ComputeScale();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ComputeScale();
            RefreshOverlay();
        }
    }
}
