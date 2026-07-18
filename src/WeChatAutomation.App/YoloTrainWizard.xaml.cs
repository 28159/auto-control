using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Recording;
using WeChatAutomation.Core.Services;
using Microsoft.Win32;

namespace WeChatAutomation.App
{
    public partial class YoloTrainWizard : Window
    {
        private static readonly Logger _logger = Logger.Instance;
        private readonly YoloTrainer _trainer;
        private readonly ActionPlayer _player;
        private int _currentStep = 1;
        private bool _labelDone;
        private bool _trainDone;
        private bool _exportDone;
        private bool _deployDone;

        public bool DeployCompleted { get; private set; }

        public YoloTrainWizard(YoloTrainer trainer, ActionPlayer player)
        {
            _logger.Info("Wizard", "向导构造开始");
            InitializeComponent();
            _trainer = trainer;
            _player = player;

            _trainer.ProgressChanged += OnProgressChanged;
            _trainer.LogMessage += OnLogMessage;
            _trainer.Completed += OnCompleted;

            RefreshCapturesInfo();
            CheckExistingLabels();
            UpdateStepUI();
            _logger.Info("Wizard", "向导构造完成，等待用户点击「打开标注工具」");
        }

        private void CheckExistingLabels()
        {
            string capturesDir = _trainer.CapturesDir;
            if (!Directory.Exists(capturesDir)) return;

            int labelCount = Directory.GetFiles(capturesDir, "*.txt", SearchOption.AllDirectories)
                .Count(f => new FileInfo(f).Length > 0);

            if (labelCount > 0)
            {
                _labelDone = true;
                LabelStatus.Text = $"已有 {labelCount} 个标注文件，可直接进入下一步，或重新标注修正。";
            }
        }

        private void OnProgressChanged(object s, TrainProgress p)
        {
            Dispatcher.BeginInvoke(() =>
            {
                TrainProgress.Value = p.Value * 100;
                TrainStatus.Text = p.Message;
                WizardLog.Text = p.Message;
            });
        }

        private void OnLogMessage(object s, string msg)
        {
            Dispatcher.BeginInvoke(() => { WizardLog.Text = msg; });
        }

        private void OnCompleted(object s, TrainResult r)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (r.Success)
                {
                    if (_currentStep == 1)
                    {
                        _labelDone = true;
                        LabelStatus.Text = "标注完成！可以进入下一步。";
                        NextBtn.IsEnabled = true;
                    }
                    else if (_currentStep == 2)
                    {
                        _trainDone = true;
                        TrainStatus.Text = "训练完成";
                        NextBtn.IsEnabled = true;
                    }
                    else if (_currentStep == 3)
                    {
                        _exportDone = true;
                        ExportStatus.Text = "导出完成";
                        NextBtn.IsEnabled = true;
                    }
                    else if (_currentStep == 4)
                    {
                        _deployDone = true;
                        DeployStatus.Text = "部署完成";
                        NextBtn.IsEnabled = true;
                    }
                }
                else
                {
                    WizardLog.Text = $"失败: {r.Message}";
                    NextBtn.IsEnabled = true;
                }
                CancelTrainBtn.Visibility = Visibility.Collapsed;
            });
        }

        private void RefreshCapturesInfo()
        {
            string capturesDir = _trainer.CapturesDir;
            bool exists = Directory.Exists(capturesDir);
            int imgCount = exists ? Directory.GetFiles(capturesDir, "*.png", SearchOption.AllDirectories).Length : 0;
            _logger.Info("Wizard", $"RefreshCapturesInfo: capturesDir={capturesDir} exists={exists} png={imgCount}");

            if (!exists)
            {
                CapturesInfo.Text = $"未找到采集数据目录：\n{capturesDir}\n请先录制（勾选「采集训练数据」）";
                StartLabelBtn.IsEnabled = false;
                _logger.Warn("Wizard", $"StartLabelBtn 禁用（目录不存在）");
                return;
            }

            int txtCount = Directory.GetFiles(capturesDir, "*.txt", SearchOption.AllDirectories).Length;
            var subDirs = Directory.GetDirectories(capturesDir);

            if (imgCount > 0)
            {
                CapturesInfo.Text = $"采集数据：{subDirs.Length} 次录制，共 {imgCount} 张截图，{txtCount} 个标注";
                StartLabelBtn.IsEnabled = true;
                _logger.Info("Wizard", $"StartLabelBtn 启用（{imgCount} 张图）");
            }
            else
            {
                CapturesInfo.Text = $"未找到采集的截图数据：\n{capturesDir}\n请先录制并勾选「采集训练数据」";
                StartLabelBtn.IsEnabled = false;
                _logger.Warn("Wizard", $"StartLabelBtn 禁用（无图片）");
            }
        }

        private void RefreshModelInfo()
        {
            string modelPath = _trainer.FindBestModel();
            if (modelPath != null)
            {
                var fi = new FileInfo(modelPath);
                ModelInfo.Text = $"模型文件: {modelPath}\n大小: {fi.Length / 1024 / 1024:F1} MB\n修改时间: {fi.LastWriteTime:yyyy-MM-dd HH:mm}";
                _trainDone = true;
            }
            else
            {
                ModelInfo.Text = "未找到训练好的模型 (.pt)，请先完成训练";
            }
        }

        private void RefreshOnnxInfo()
        {
            string onnxPath = _trainer.DefaultOnnxPath;
            if (File.Exists(onnxPath))
            {
                var fi = new FileInfo(onnxPath);
                OnnxInfo.Text = $"ONNX 文件: {onnxPath}\n大小: {fi.Length / 1024 / 1024:F1} MB\n修改时间: {fi.LastWriteTime:yyyy-MM-dd HH:mm}";
                _exportDone = true;
            }
            else
            {
                OnnxInfo.Text = "未找到 ONNX 模型，请先完成导出";
            }
        }

        // ═══ 步骤导航 ═══

        private void UpdateStepUI()
        {
            Panel1.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            Panel2.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            Panel3.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
            Panel4.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;
            PanelDone.Visibility = _currentStep == 5 ? Visibility.Visible : Visibility.Collapsed;

            var dots = new[] { Step1Dot, Step2Dot, Step3Dot, Step4Dot };
            var nums = new[] { Step1Num, Step2Num, Step3Num, Step4Num };
            var texts = new[] { Step1Text, Step2Text, Step3Text, Step4Text };
            var labels = new[] { "1.标注", "2.训练", "3.导出", "4.部署" };

            for (int i = 0; i < 4; i++)
            {
                int step = i + 1;
                if (step < _currentStep)
                {
                    dots[i].Background = new SolidColorBrush(Colors.White);
                    nums[i].Text = "✓";
                    nums[i].Foreground = new SolidColorBrush(Color.FromRgb(21, 101, 192));
                    texts[i].Text = labels[i];
                    texts[i].Foreground = new SolidColorBrush(Colors.White);
                }
                else if (step == _currentStep)
                {
                    dots[i].Background = new SolidColorBrush(Colors.White);
                    nums[i].Text = (i + 1).ToString();
                    nums[i].Foreground = new SolidColorBrush(Color.FromRgb(21, 101, 192));
                    texts[i].Text = labels[i];
                    texts[i].Foreground = new SolidColorBrush(Colors.White);
                    texts[i].FontWeight = FontWeights.Bold;
                }
                else
                {
                    dots[i].Background = new SolidColorBrush(Color.FromRgb(66, 165, 245));
                    nums[i].Text = (i + 1).ToString();
                    nums[i].Foreground = new SolidColorBrush(Colors.White);
                    texts[i].Text = labels[i];
                    texts[i].Foreground = new SolidColorBrush(Color.FromRgb(187, 222, 251));
                    texts[i].FontWeight = FontWeights.Normal;
                }
            }

            PrevBtn.Visibility = _currentStep > 1 && _currentStep < 5 ? Visibility.Visible : Visibility.Collapsed;

            if (_currentStep == 5)
            {
                NextBtn.Content = "完成";
                NextBtn.IsEnabled = true;
                DoneSummary.Text = $"✓ 人工标注完成\n✓ 模型训练完成\n✓ ONNX 导出完成\n✓ 模型部署并加载完成";
            }
            else
            {
                NextBtn.Content = "下一步 ▶";
                NextBtn.IsEnabled = CanProceed();
            }

            if (_currentStep == 3) RefreshModelInfo();
            if (_currentStep == 4) RefreshOnnxInfo();
        }

        private bool CanProceed()
        {
            return _currentStep switch
            {
                1 => _labelDone,
                2 => _trainDone,
                3 => _exportDone,
                4 => _deployDone,
                _ => true
            };
        }

        private void NextBtn_Click(object s, RoutedEventArgs e)
        {
            if (_currentStep >= 5)
            {
                DeployCompleted = _deployDone;
                Close();
                return;
            }
            _currentStep++;
            UpdateStepUI();
        }

        private void PrevBtn_Click(object s, RoutedEventArgs e)
        {
            if (_currentStep > 1) _currentStep--;
            UpdateStepUI();
        }

        // ═══ Step 1: 标注 ═══

        private void StartLabel_Click(object s, RoutedEventArgs e)
        {
            _logger.Info("Wizard", "StartLabel_Click 被触发");
            WizardLog.Text = "正在打开标注工具…";
            try
            {
                string capturesDir = _trainer.CapturesDir;
                _logger.Info("Wizard", $"CapturesDir={capturesDir} exists={Directory.Exists(capturesDir)}");
                if (!Directory.Exists(capturesDir))
                {
                    _logger.Warn("Wizard", "captures 目录不存在，放弃");
                    MessageBox.Show(
                        $"未找到采集数据目录：\n{capturesDir}\n\n" +
                        "请先在主界面勾选「采集训练数据」后再录制操作，" +
                        "每次点击会自动截图并保存到该目录。",
                        "无法启动标注", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 选择最近一次“直接包含截图”的录制目录（LabelWindow 非递归读取，跳过空目录）
                string imagesDir = null;
                foreach (var d in Directory.GetDirectories(capturesDir)
                            .OrderByDescending(d => Directory.GetLastWriteTime(d)))
                {
                    bool hasImg = new[] { "*.png", "*.jpg", "*.bmp" }
                        .Any(ext => Directory.GetFiles(d, ext).Length > 0);
                    if (hasImg) { imagesDir = d; break; }
                }

                // 子目录都没有图片时，退回 capturesDir 本身
                if (imagesDir == null &&
                    new[] { "*.png", "*.jpg", "*.bmp" }
                        .Any(ext => Directory.GetFiles(capturesDir, ext).Length > 0))
                {
                    imagesDir = capturesDir;
                }

                if (imagesDir == null)
                {
                    _logger.Warn("Wizard", "captures 下没有可标注图片，放弃");
                    MessageBox.Show(
                        $"采集目录中没有可标注的截图：\n{capturesDir}\n\n" +
                        "请确认录制时勾选了「采集训练数据」，并实际点击了目标窗口中的元素。",
                        "无法启动标注", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _logger.Info("Wizard", $"imagesDir={imagesDir}，准备构造 LabelWindow");
                WizardLog.Text = $"标注目录: {imagesDir}";

                var labelWin = new LabelWindow(imagesDir, imagesDir) { Owner = this };
                _logger.Info("Wizard", "LabelWindow 构造完成，调用 ShowDialog");
                labelWin.ShowDialog();
                _logger.Info("Wizard", $"ShowDialog 返回，Saved={labelWin.Saved}");

                if (labelWin.Saved)
                {
                    _labelDone = true;
                    LabelStatus.Text = "标注完成！可以进入下一步。";
                    NextBtn.IsEnabled = true;
                    UpdateStepUI();
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Wizard", "StartLabel_Click 异常", ex);
                MessageBox.Show(
                    $"启动标注工具时出错：\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "标注工具错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ═══ Step 1: 手动选择目录标注 ═══

        private void StartLabelManual_Click(object s, RoutedEventArgs e)
        {
            _logger.Info("Wizard", "StartLabelManual_Click 被触发");
            WizardLog.Text = "请选择要标注的目录…";
            try
            {
                var dlg = new OpenFolderDialog
                {
                    Title = "选择要标注的截图目录（含 png/jpg/bmp）",
                };

                if (dlg.ShowDialog() != true)
                {
                    _logger.Info("Wizard", "用户取消了目录选择");
                    return;
                }

                string imagesDir = dlg.FolderName;
                _logger.Info("Wizard", $"手动选择目录: {imagesDir}");
                int imgCount = new[] { "*.png", "*.jpg", "*.bmp" }
                    .Sum(ext => Directory.GetFiles(imagesDir, ext).Length);

                if (imgCount == 0)
                {
                    _logger.Warn("Wizard", $"所选目录无图片: {imagesDir}");
                    MessageBox.Show(
                        $"该目录下没有可标注的图片（png/jpg/bmp）：\n{imagesDir}",
                        "无法启动标注", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                CapturesInfo.Text = $"手动选择目录：{imagesDir}（{imgCount} 张图片）";
                WizardLog.Text = $"标注目录: {imagesDir}";

                _logger.Info("Wizard", $"构造 LabelWindow (手动), imagesDir={imagesDir}");
                var labelWin = new LabelWindow(imagesDir, imagesDir) { Owner = this };
                _logger.Info("Wizard", "LabelWindow 构造完成，调用 ShowDialog (手动)");
                labelWin.ShowDialog();
                _logger.Info("Wizard", $"ShowDialog 返回 (手动)，Saved={labelWin.Saved}");

                if (labelWin.Saved)
                {
                    _labelDone = true;
                    LabelStatus.Text = "标注完成！可以进入下一步。";
                    NextBtn.IsEnabled = true;
                    UpdateStepUI();
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Wizard", "StartLabelManual_Click 异常", ex);
                MessageBox.Show(
                    $"启动标注工具时出错：\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "标注工具错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ═══ Step 2: 训练 ═══

        private async void StartTrain_Click(object s, RoutedEventArgs e)
        {
            if (_trainer.IsRunning) return;

            // 始终重新整理数据集：用户可能回第 1 步改过标注，旧 datasets/ 与 .cache 会用过期数据。
            WizardLog.Text = "整理数据集中（含最新标注）...";
            try
            {
                await _trainer.CollectDataAsync();
                // 删除旧缓存，强制 YOLO 重新扫描标签
                foreach (var cache in System.IO.Directory.GetFiles(
                    System.IO.Path.Combine(_trainer.DatasetsDir, "labels"), "*.cache",
                    System.IO.SearchOption.AllDirectories))
                {
                    try { System.IO.File.Delete(cache); } catch { }
                }
            }
            catch (Exception ex)
            {
                WizardLog.Text = $"整理数据失败: {ex.Message}";
                return;
            }

            // 校验：训练集是否有非空标签
            if (!HasLabeledData())
            {
                WizardLog.Text = "数据集无有效标注，请先回第 1 步标注图片";
                TrainStatus.Text = "无标注数据";
                return;
            }

            if (!int.TryParse(EpochsBox.Text, out int epochs)) epochs = 100;
            if (!int.TryParse(BatchBox.Text, out int batch)) batch = 8;
            if (!int.TryParse(PatienceBox.Text, out int patience)) patience = 20;
            if (!double.TryParse(LrBox.Text, out double lr0)) lr0 = 0.01;

            string device = DeviceBox.SelectedIndex switch
            {
                1 => "cpu",
                2 => "0",
                _ => ""
            };

            StartTrainBtn.IsEnabled = false;
            CancelTrainBtn.Visibility = Visibility.Visible;
            NextBtn.IsEnabled = false;
            TrainProgress.Value = 0;
            TrainStatus.Text = "训练中...";

            try
            {
                await _trainer.TrainAsync(_trainer.DatasetYaml, epochs, batch, device, patience: patience, lr0: lr0);
            }
            catch (Exception ex)
            {
                TrainStatus.Text = $"训练异常: {ex.Message}";
                StartTrainBtn.IsEnabled = true;
                CancelTrainBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void CancelTrain_Click(object s, RoutedEventArgs e)
        {
            _trainer.Cancel();
            StartTrainBtn.IsEnabled = true;
            CancelTrainBtn.Visibility = Visibility.Collapsed;
            TrainStatus.Text = "已取消";
            NextBtn.IsEnabled = true;
        }

        /// <summary>检查整理后的数据集是否含非空标签文件。</summary>
        private bool HasLabeledData()
        {
            try
            {
                string lblDir = System.IO.Path.Combine(_trainer.DatasetsDir, "labels", "train");
                if (!System.IO.Directory.Exists(lblDir)) return false;
                return System.IO.Directory.GetFiles(lblDir, "*.txt")
                    .Any(f => new System.IO.FileInfo(f).Length > 0);
            }
            catch { return false; }
        }

        // ═══ Step 3: 导出 ═══

        private async void StartExport_Click(object s, RoutedEventArgs e)
        {
            string modelPath = _trainer.FindBestModel();
            if (modelPath == null)
            {
                ExportStatus.Text = "未找到训练好的模型";
                return;
            }

            StartExportBtn.IsEnabled = false;
            ExportStatus.Text = "导出中...";

            try
            {
                await _trainer.ExportOnnxAsync(modelPath);
                _exportDone = true;
                ExportStatus.Text = "导出完成！可以进入下一步。";
                NextBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ExportStatus.Text = $"导出异常: {ex.Message}";
            }
            finally
            {
                StartExportBtn.IsEnabled = true;
            }
        }

        // ═══ Step 4: 部署 ═══

        private async void StartDeploy_Click(object s, RoutedEventArgs e)
        {
            string onnxPath = _trainer.DefaultOnnxPath;
            if (!File.Exists(onnxPath))
            {
                DeployStatus.Text = "ONNX 文件不存在";
                return;
            }

            string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");

            StartDeployBtn.IsEnabled = false;
            DeployStatus.Text = "部署中...";

            try
            {
                await _trainer.DeployAsync(onnxPath, targetDir);
                _deployDone = true;
                DeployStatus.Text = "部署完成！";

                string deployedModel = Path.Combine(targetDir, "yolov8n-ui.onnx");
                if (File.Exists(deployedModel) && _player != null)
                {
                    _player.InitVisionDetector(deployedModel);
                    DeployStatus.Text = "部署完成，视觉检测器已加载模型！";
                }

                NextBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                DeployStatus.Text = $"部署异常: {ex.Message}";
            }
            finally
            {
                StartDeployBtn.IsEnabled = true;
            }
        }
    }
}
