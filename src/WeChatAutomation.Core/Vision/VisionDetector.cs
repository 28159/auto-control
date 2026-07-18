using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Vision
{
    public class VisionDetector : IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private InferenceSession _session;
        private string[] _labels;
        private int _inputWidth = 640;
        private int _inputHeight = 640;
        private bool _disposed;

        public bool IsLoaded => _session != null;
        public string ModelPath { get; private set; }
        /// <summary>模型类别名（无 labels 文件时为内置 20 类）。</summary>
        public IReadOnlyList<string> Labels => _labels;
        /// <summary>模型输入尺寸。</summary>
        public (int Width, int Height) InputSize => (_inputWidth, _inputHeight);

        public VisionDetector() { }

        public bool LoadModel(string modelPath, string labelsPath = null)
        {
            try
            {
                if (!File.Exists(modelPath))
                {
                    _logger.Error("VisionDetector", $"模型文件不存在: {modelPath}");
                    return false;
                }

                var sessionOptions = new SessionOptions();
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                _session = new InferenceSession(modelPath, sessionOptions);
                ModelPath = modelPath;

                var inputMeta = _session.InputMetadata;
                foreach (var kvp in inputMeta)
                {
                    var shape = kvp.Value.Dimensions;
                    if (shape.Length >= 3)
                    {
                        _inputHeight = shape[2] > 0 ? shape[2] : 640;
                        _inputWidth = shape[3] > 0 ? shape[3] : 640;
                    }
                    break;
                }

                if (labelsPath != null && File.Exists(labelsPath))
                {
                    _labels = File.ReadAllLines(labelsPath)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();
                }
                else
                {
                    var outputMeta = _session.OutputMetadata;
                    foreach (var kvp in outputMeta)
                    {
                        var shape = kvp.Value.Dimensions;
                        if (shape.Length >= 3)
                        {
                            int dim1 = shape[1] > 0 ? shape[1] : 0;
                            int dim2 = shape[2] > 0 ? shape[2] : 0;
                            bool transposed = dim1 < dim2;
                            int numClasses = transposed ? dim1 - 4 : dim2 - 4;
                            if (numClasses > 0)
                            {
                                _labels = Enumerable.Range(0, numClasses)
                                    .Select(i => $"class_{i}")
                                    .ToArray();
                            }
                        }
                        break;
                    }

                    if (_labels == null || _labels.Length == 0)
                    {
                        _labels = new[] { "button", "input", "checkbox", "radio", "dropdown",
                            "tab", "menu_item", "icon", "link", "text_field",
                            "search_box", "send_button", "close_button", "minimize_button",
                            "maximize_button", "scrollbar", "slider", "toggle", "tooltip", "image" };
                    }
                }

                _logger.Info("VisionDetector", $"模型加载成功: {modelPath}, 输入尺寸: {_inputWidth}x{_inputHeight}, 类别数: {_labels.Length}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("VisionDetector", $"模型加载失败: {ex.Message}");
                _session?.Dispose();
                _session = null;
                return false;
            }
        }

        public List<Detection> Detect(Bitmap screenshot, string[] filterLabels = null, float confThreshold = 0.5f, float iouThreshold = 0.45f)
        {
            if (_session == null)
            {
                _logger.Warn("VisionDetector", "模型未加载");
                return new List<Detection>();
            }

            try
            {
                int origWidth = screenshot.Width;
                int origHeight = screenshot.Height;

                float scaleX = (float)origWidth / _inputWidth;
                float scaleY = (float)origHeight / _inputHeight;

                var tensor = Preprocess(screenshot);

                var inputName = _session.InputMetadata.Keys.First();
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                using var results = _session.Run(inputs);
                var outputTensor = results.First().AsTensor<float>();
                var rawDetections = Postprocess(outputTensor, scaleX, scaleY, confThreshold);

                var nmsResults = NMS(rawDetections, iouThreshold);

                if (filterLabels != null && filterLabels.Length > 0)
                {
                    var labelSet = new HashSet<string>(filterLabels, StringComparer.OrdinalIgnoreCase);
                    nmsResults = nmsResults.Where(d => labelSet.Contains(d.Label)).ToList();
                }

                return nmsResults;
            }
            catch (Exception ex)
            {
                _logger.Error("VisionDetector", $"推理失败: {ex.Message}");
                return new List<Detection>();
            }
        }

        public Detection FindBest(Bitmap screenshot, string label, float confThreshold = 0.5f)
        {
            var results = Detect(screenshot, new[] { label }, confThreshold);
            return results.OrderByDescending(d => d.Confidence).FirstOrDefault();
        }

        public Detection FindBestInRegion(Bitmap screenshot, string label, int regionX, int regionY, int regionW, int regionH, float confThreshold = 0.5f)
        {
            var results = Detect(screenshot, new[] { label }, confThreshold);
            return results
                .Where(d => d.CenterX >= regionX && d.CenterX <= regionX + regionW
                         && d.CenterY >= regionY && d.CenterY <= regionY + regionH)
                .OrderByDescending(d => d.Confidence)
                .FirstOrDefault();
        }

        /// <summary>
        /// 在所有匹配 label 的检测里，优先返回中心点离参考点 (refX, refY)（窗口局部坐标）最近的那个；
        /// 若没有任何检测落在 tolerance 半径内，回退到全局置信度最高的检测。
        /// 用于视觉点击：同一窗口常有多个同类按钮，按录制坐标点准目标。
        /// </summary>
        public Detection FindNearest(Bitmap screenshot, string label, int refX, int refY,
            int tolerance, float confThreshold = 0.5f)
        {
            var results = Detect(screenshot, new[] { label }, confThreshold);
            if (results.Count == 0) return null;

            // 容差为 0/负 视作不限制距离（退化为最高置信度）
            if (tolerance <= 0)
                return results.OrderByDescending(d => d.Confidence).FirstOrDefault();

            Detection best = null;
            double bestDist = double.MaxValue;
            foreach (var d in results)
            {
                double dx = d.CenterX - refX;
                double dy = d.CenterY - refY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= tolerance && dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            // 附近没有 -> 回退到最高置信度，避免完全点不动
            return best ?? results.OrderByDescending(d => d.Confidence).FirstOrDefault();
        }

        private DenseTensor<float> Preprocess(Bitmap image)
        {
            using var resized = new Bitmap(image, new Size(_inputWidth, _inputHeight));

            var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });
            var bmpData = resized.LockBits(new Rectangle(0, 0, _inputWidth, _inputHeight),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            try
            {
                int stride = bmpData.Stride;
                int bytes = Math.Abs(stride) * _inputHeight;
                byte[] rgbValues = new byte[bytes];
                Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytes);

                for (int y = 0; y < _inputHeight; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = 0; x < _inputWidth; x++)
                    {
                        int offset = rowOffset + x * 3;
                        float b = rgbValues[offset] / 255.0f;
                        float g = rgbValues[offset + 1] / 255.0f;
                        float r = rgbValues[offset + 2] / 255.0f;

                        tensor[0, 0, y, x] = r;
                        tensor[0, 1, y, x] = g;
                        tensor[0, 2, y, x] = b;
                    }
                }
            }
            finally
            {
                resized.UnlockBits(bmpData);
            }

            return tensor;
        }

        private List<Detection> Postprocess(Tensor<float> output, float scaleX, float scaleY, float confThreshold)
        {
            var detections = new List<Detection>();
            var dimensions = output.Dimensions.ToArray();

            if (dimensions.Length == 3)
            {
                int dim1 = dimensions[1];
                int dim2 = dimensions[2];

                bool transposed = dim1 < dim2;

                int numDetections = transposed ? dim2 : dim1;
                int numValues = transposed ? dim1 : dim2;
                int numClasses = numValues - 4;

                for (int i = 0; i < numDetections; i++)
                {
                    float cx, cy, w, h;
                    int bestClass = -1;
                    float bestConf = 0;

                    if (transposed)
                    {
                        cx = output[0, 0, i] * scaleX;
                        cy = output[0, 1, i] * scaleY;
                        w = output[0, 2, i] * scaleX;
                        h = output[0, 3, i] * scaleY;

                        for (int c = 0; c < numClasses; c++)
                        {
                            float conf = output[0, 4 + c, i];
                            if (conf > bestConf)
                            {
                                bestConf = conf;
                                bestClass = c;
                            }
                        }
                    }
                    else
                    {
                        cx = output[0, i, 0] * scaleX;
                        cy = output[0, i, 1] * scaleY;
                        w = output[0, i, 2] * scaleX;
                        h = output[0, i, 3] * scaleY;

                        for (int c = 4; c < numValues; c++)
                        {
                            float conf = output[0, i, c];
                            if (conf > bestConf)
                            {
                                bestConf = conf;
                                bestClass = c - 4;
                            }
                        }
                    }

                    int x = (int)(cx - w / 2);
                    int y = (int)(cy - h / 2);

                    if (bestConf < confThreshold || bestClass < 0 || bestClass >= _labels.Length)
                        continue;

                    detections.Add(new Detection
                    {
                        Label = _labels[bestClass],
                        Confidence = bestConf,
                        X = Math.Max(0, x),
                        Y = Math.Max(0, y),
                        Width = (int)w,
                        Height = (int)h
                    });
                }
            }
            else if (dimensions.Length == 2)
            {
                int numDetections = dimensions[0];
                int numValues = dimensions[1];

                for (int i = 0; i < numDetections; i++)
                {
                    float cx = output[i, 0] * scaleX;
                    float cy = output[i, 1] * scaleY;
                    float w = output[i, 2] * scaleX;
                    float h = output[i, 3] * scaleY;

                    int x = (int)(cx - w / 2);
                    int y = (int)(cy - h / 2);

                    int classStartIdx = 4;
                    int bestClass = -1;
                    float bestConf = 0;

                    for (int c = classStartIdx; c < Math.Min(numValues, _labels.Length + classStartIdx); c++)
                    {
                        float conf = output[i, c];
                        if (conf > bestConf)
                        {
                            bestConf = conf;
                            bestClass = c - classStartIdx;
                        }
                    }

                    if (bestConf < confThreshold || bestClass < 0 || bestClass >= _labels.Length)
                        continue;

                    detections.Add(new Detection
                    {
                        Label = _labels[bestClass],
                        Confidence = bestConf,
                        X = Math.Max(0, x),
                        Y = Math.Max(0, y),
                        Width = (int)w,
                        Height = (int)h
                    });
                }
            }

            return detections;
        }

        private static List<Detection> NMS(List<Detection> detections, float iouThreshold)
        {
            var results = new List<Detection>();
            var sorted = detections.OrderByDescending(d => d.Confidence).ToList();

            while (sorted.Count > 0)
            {
                var best = sorted[0];
                results.Add(best);
                sorted.RemoveAt(0);

                sorted.RemoveAll(d => best.IoU(d) > iouThreshold);
            }

            return results;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _session?.Dispose();
                _session = null;
                _disposed = true;
            }
        }
    }
}
