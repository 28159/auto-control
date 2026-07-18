using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Services
{
    public class YoloTrainer
    {
        private static readonly Logger _logger = Logger.Instance;
        private Process _process;
        private bool _isRunning;
        private string _pythonExe;

        public bool IsRunning => _isRunning;
        public event EventHandler<TrainProgress> ProgressChanged;
        public event EventHandler<string> LogMessage;
        public event EventHandler<TrainResult> Completed;

        public YoloTrainer()
        {
            _pythonExe = FindPython();
        }

        private static string FindPython()
        {
            string[] candidates = { "python", "python3", "py" };
            foreach (var cmd in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0) return cmd;
                }
                catch { continue; }
            }
            return "python";
        }

        public string ScriptDir
        {
            get
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var dir = new DirectoryInfo(baseDir);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, "yolo_train");
                    if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "label_and_train.py")))
                        return candidate;
                    dir = dir.Parent;
                }
                string fallback = Path.Combine(baseDir, "yolo_train");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        public string CapturesDir => AppPaths.CapturesDir;
        public string DatasetsDir => Path.Combine(ScriptDir, "datasets");
        public string DatasetYaml => Path.Combine(ScriptDir, "dataset.yaml");
        public string ModelsDir => Path.Combine(ScriptDir, "models");
        public string DefaultOnnxPath => Path.Combine(ModelsDir, "yolov8n-ui.onnx");

        public string FindBestModel()
        {
            string weightDir = Path.Combine(ScriptDir, "runs", "detect", "train", "weights");
            string best = Path.Combine(weightDir, "best.pt");
            if (File.Exists(best)) return best;
            if (!Directory.Exists(Path.Combine(ScriptDir, "runs"))) return null;
            var dirs = Directory.GetDirectories(Path.Combine(ScriptDir, "runs"), "train*", SearchOption.AllDirectories);
            foreach (var d in dirs.OrderByDescending(d => d))
            {
                best = Path.Combine(d, "weights", "best.pt");
                if (File.Exists(best)) return best;
            }
            return null;
        }

        public async Task CollectDataAsync(string capturesDir = null, string outputDir = null, double valRatio = 0.2)
        {
            capturesDir ??= CapturesDir;
            outputDir ??= DatasetsDir;
            string script = Path.Combine(ScriptDir, "label_and_train.py");
            string args = $"\"{script}\" collect --captures-dir \"{capturesDir}\" --output-dir \"{outputDir}\" --val-ratio {valRatio}";
            await RunPythonAsync(args);
        }

        public async Task LabelAsync(string imagesDir, string outputDir = null)
        {
            outputDir ??= imagesDir;
            string script = Path.Combine(ScriptDir, "label_and_train.py");
            string args = $"\"{script}\" label --images-dir \"{imagesDir}\" --output-dir \"{outputDir}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _pythonExe,
                Arguments = args,
                WorkingDirectory = ScriptDir,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            OnLog($"执行: {_pythonExe} {args}");

            try
            {
                _isRunning = true;
                var process = Process.Start(psi);
                if (process == null)
                {
                    _isRunning = false;
                    OnLog("启动标注工具失败");
                    Completed?.Invoke(this, new TrainResult { Success = false, Message = "启动标注工具失败" });
                    return;
                }

                await Task.Run(() => process.WaitForExit());
                _isRunning = false;

                string marker = Path.Combine(outputDir, "annotations.json");
                bool hasLabels = File.Exists(marker);
                OnLog(hasLabels ? "标注工具已退出，标注已保存" : "标注工具已退出");
                Completed?.Invoke(this, new TrainResult { Success = true, Data = hasLabels ? "labeled" : "" });
            }
            catch (Exception ex)
            {
                _isRunning = false;
                OnLog($"标注工具启动失败: {ex.Message}");
                Completed?.Invoke(this, new TrainResult { Success = false, Message = ex.Message });
            }
        }

        public async Task TrainAsync(string dataYaml = null, int epochs = 100, int batch = 8,
            string device = "", string modelVariant = "yolov8n", int patience = 20, double lr0 = 0.01)
        {
            dataYaml ??= DatasetYaml;
            string script = Path.Combine(ScriptDir, "label_and_train.py");
            string args = $"\"{script}\" train --data \"{dataYaml}\" --epochs {epochs} --batch {batch}" +
                          $" --model-variant {modelVariant} --patience {patience} --lr0 {lr0}" +
                          $" --project \"{Path.Combine(ScriptDir, "runs")}\" --name train" +
                          (string.IsNullOrEmpty(device) ? "" : $" --device {device}");
            await RunPythonAsync(args);
        }

        public async Task ExportOnnxAsync(string modelPath = null, string outputPath = null)
        {
            modelPath ??= FindBestModel();
            outputPath ??= DefaultOnnxPath;
            if (modelPath == null || !File.Exists(modelPath))
            {
                OnLog("未找到训练好的模型 (.pt)");
                Completed?.Invoke(this, new TrainResult { Success = false, Message = "未找到训练好的模型" });
                return;
            }
            string script = Path.Combine(ScriptDir, "label_and_train.py");
            string args = $"\"{script}\" export --model \"{modelPath}\" --output \"{outputPath}\"";
            await RunPythonAsync(args);
        }

        public async Task DeployAsync(string onnxPath = null, string targetDir = null)
        {
            onnxPath ??= DefaultOnnxPath;
            targetDir ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
            if (!File.Exists(onnxPath))
            {
                OnLog("ONNX 文件不存在，请先导出");
                Completed?.Invoke(this, new TrainResult { Success = false, Message = "ONNX 文件不存在" });
                return;
            }
            string script = Path.Combine(ScriptDir, "label_and_train.py");
            string args = $"\"{script}\" deploy --onnx \"{onnxPath}\" --target \"{targetDir}\"";
            await RunPythonAsync(args);
        }

        public void Cancel()
        {
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        private async Task RunPythonAsync(string arguments)
        {
            if (_isRunning)
            {
                OnLog("训练任务正在执行中");
                return;
            }

            _isRunning = true;
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _pythonExe,
                    Arguments = arguments,
                    WorkingDirectory = ScriptDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                OnLog($"执行: {_pythonExe} {arguments}");

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.Exited += (s, e) =>
                {
                    _isRunning = false;
                    tcs.TrySetResult(true);
                };

                _process.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    ParseJsonLine(e.Data);
                };

                _process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    OnLog($"[stderr] {e.Data}");
                };

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                await tcs.Task;
            }
            catch (Exception ex)
            {
                _isRunning = false;
                OnLog($"执行失败: {ex.Message}");
                Completed?.Invoke(this, new TrainResult { Success = false, Message = ex.Message });
            }
        }

        private void ParseJsonLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // 训练输出量大，tqdm 进度条等可能和 JSON 混在同一行；
            // 从第一个 '{' 到最后一个 '}' 截取 JSON 再解析，提高鲁棒性。
            string trimmed = line.Trim();
            int start = trimmed.IndexOf('{');
            if (start < 0) { OnLog(line); return; }
            int end = trimmed.LastIndexOf('}');
            if (end <= start) { OnLog(line); return; }
            string json = trimmed.Substring(start, end - start + 1);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl))
                {
                    OnLog(line);
                    return;
                }
                string type = typeEl.GetString();

                switch (type)
                {
                    case "progress":
                        double value = root.GetProperty("value").GetDouble();
                        string msg = root.TryGetProperty("message", out var m) ? m.GetString() : "";
                        ProgressChanged?.Invoke(this, new TrainProgress { Value = value, Message = msg ?? "" });
                        OnLog(msg ?? $"进度: {value:P0}");
                        break;

                    case "log":
                        string logMsg = root.GetProperty("message").GetString() ?? "";
                        OnLog(logMsg);
                        break;

                    case "result":
                        bool success = root.GetProperty("success").GetBoolean();
                        var result = new TrainResult { Success = success };
                        if (root.TryGetProperty("data", out var data))
                            result.Data = data.GetRawText();
                        if (root.TryGetProperty("message", out var rm))
                            result.Message = rm.GetString() ?? "";
                        Completed?.Invoke(this, result);
                        OnLog(success ? "操作完成" : $"操作失败: {result.Message}");
                        break;

                    case "error":
                        string errMsg = root.GetProperty("message").GetString() ?? "未知错误";
                        OnLog($"错误: {errMsg}");
                        Completed?.Invoke(this, new TrainResult { Success = false, Message = errMsg });
                        break;

                    default:
                        OnLog(line);
                        break;
                }
            }
            catch
            {
                OnLog(line);
            }
        }

        private void OnLog(string message)
        {
            LogMessage?.Invoke(this, message);
            _logger.Info("YoloTrainer", message);
        }
    }

    public class TrainProgress
    {
        public double Value { get; set; }
        public string Message { get; set; } = "";
    }

    public class TrainResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string Data { get; set; } = "";
    }
}
