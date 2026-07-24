using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCvSharp;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Vision
{
    /// <summary>
    /// 基于 OpenCV 模板匹配的视觉检测器。
    /// 替代原 YOLO/ONNX 方案：录制时截取目标元素图片作为模板，回放时用 Cv2.MatchTemplate
    /// 在窗口截图中定位最相似位置。无需训练，零外部依赖（仅 OpenCvSharp4）。
    /// 保留 FindBest/FindNearest/Detect 方法签名，便于上层 ActionPlayer 最小改动。
    /// </summary>
    public class VisionDetector : IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private Mat _template;
        private Mat _templateGray;
        private int _templateWidth;
        private int _templateHeight;
        private string _templateLabel = "template";
        private bool _disposed;

        public bool IsLoaded => _template != null;
        public string TemplatePath { get; private set; }

        /// <summary>模板标签（用于日志和结果回传，不参与匹配逻辑）。</summary>
        public string Label => _templateLabel;
        /// <summary>模板尺寸。</summary>
        public (int Width, int Height) InputSize => (_templateWidth, _templateHeight);

        public VisionDetector() { }

        /// <summary>
        /// 加载模板图片。兼容旧接口：modelPath 指向模板图片文件（png/bmp/jpg）。
        /// labelsPath 忽略（模板匹配无需类别文件）。
        /// </summary>
        public bool LoadModel(string modelPath, string labelsPath = null)
        {
            try
            {
                if (!File.Exists(modelPath))
                {
                    _logger.Error("VisionDetector", $"模板文件不存在: {modelPath}");
                    return false;
                }

                _template?.Dispose();
                _templateGray?.Dispose();
                _template = Cv2.ImRead(modelPath, ImreadModes.Color);
                if (_template == null || _template.Empty())
                {
                    _logger.Error("VisionDetector", $"模板图片读取失败: {modelPath}");
                    _template = null;
                    return false;
                }

                _templateGray = new Mat();
                Cv2.CvtColor(_template, _templateGray, ColorConversionCodes.BGR2GRAY);
                _templateWidth = _template.Width;
                _templateHeight = _template.Height;
                TemplatePath = modelPath;
                _templateLabel = Path.GetFileNameWithoutExtension(modelPath);

                _logger.Info("VisionDetector", $"模板加载成功: {modelPath}, 尺寸: {_templateWidth}x{_templateHeight}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("VisionDetector", $"模板加载失败: {ex.Message}");
                _template?.Dispose();
                _templateGray?.Dispose();
                _template = null;
                _templateGray = null;
                return false;
            }
        }

        /// <summary>
        /// 直接从 Bitmap 加载模板（录制时截取后内存加载，无需落盘再读）。
        /// </summary>
        public bool LoadTemplateFromBitmap(Bitmap bitmap, string label = "template")
        {
            try
            {
                if (bitmap == null) return false;
                _template?.Dispose();
                _templateGray?.Dispose();
                _template = BitmapToMat(bitmap);
                _templateGray = new Mat();
                Cv2.CvtColor(_template, _templateGray, ColorConversionCodes.BGR2GRAY);
                _templateWidth = _template.Width;
                _templateHeight = _template.Height;
                _templateLabel = label;
                TemplatePath = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("VisionDetector", $"从 Bitmap 加载模板失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 在截图中匹配当前模板，返回所有超过阈值的位置。
        /// filterLabels 在模板匹配中无实际作用（只有单一模板），仅保留以兼容签名。
        /// confThreshold 解释为匹配度阈值（0~1，默认 0.7），iouThreshold 用于对多重叠命中做 NMS。
        /// </summary>
        public List<Detection> Detect(Bitmap screenshot, string[] filterLabels = null, float confThreshold = 0.7f, float iouThreshold = 0.45f)
        {
            if (_templateGray == null)
            {
                _logger.Warn("VisionDetector", "模板未加载");
                return new List<Detection>();
            }

            try
            {
                using var sceneGray = BitmapToGrayMat(screenshot);
                if (sceneGray == null || sceneGray.Empty()) return new List<Detection>();

                // 模板不能大于场景
                if (_templateWidth > sceneGray.Width || _templateHeight > sceneGray.Height)
                {
                    _logger.Warn("VisionDetector", $"模板({_templateWidth}x{_templateHeight})大于截图({sceneGray.Width}x{sceneGray.Height})");
                    return new List<Detection>();
                }

                using var result = new Mat();
                Cv2.MatchTemplate(sceneGray, _templateGray, result, TemplateMatchModes.CCoeffNormed);

                // 收集所有超过阈值的位置：通过归一化结果矩阵逐点扫描
                var rawDetections = new List<Detection>();
                result.GetArray(out float[] matchVals);
                int resW = result.Width;
                int resH = result.Height;
                float thr = Math.Clamp(confThreshold, 0f, 1f);
                for (int y = 0; y < resH; y++)
                {
                    for (int x = 0; x < resW; x++)
                    {
                        float val = matchVals[y * resW + x];
                        if (val >= thr)
                        {
                            rawDetections.Add(new Detection
                            {
                                Label = _templateLabel,
                                Confidence = val,
                                X = x,
                                Y = y,
                                Width = _templateWidth,
                                Height = _templateHeight
                            });
                        }
                    }
                }

                // 邻近点会形成大量重叠命中，用 NMS 合并
                return NMS(rawDetections, iouThreshold);
            }
            catch (Exception ex)
            {
                _logger.Error("VisionDetector", $"模板匹配失败: {ex.Message}");
                return new List<Detection>();
            }
        }

        /// <summary>匹配度最高的位置。</summary>
        public Detection FindBest(Bitmap screenshot, string label, float confThreshold = 0.7f)
        {
            var results = Detect(screenshot, null, confThreshold);
            return results.OrderByDescending(d => d.Confidence).FirstOrDefault();
        }

        public Detection FindBestInRegion(Bitmap screenshot, string label, int regionX, int regionY, int regionW, int regionH, float confThreshold = 0.7f)
        {
            var results = Detect(screenshot, null, confThreshold);
            return results
                .Where(d => d.CenterX >= regionX && d.CenterX <= regionX + regionW
                         && d.CenterY >= regionY && d.CenterY <= regionY + regionH)
                .OrderByDescending(d => d.Confidence)
                .FirstOrDefault();
        }

        /// <summary>
        /// 在所有匹配位置里，优先返回中心点离参考点 (refX, refY)（窗口局部坐标）最近的那个；
        /// 若没有任何匹配落在 tolerance 半径内，回退到全局匹配度最高的位置。
        /// 用于视觉点击：同一窗口常有多个相似元素，按录制坐标点准目标。
        /// </summary>
        public Detection FindNearest(Bitmap screenshot, string label, int refX, int refY,
            int tolerance, float confThreshold = 0.7f)
        {
            var results = Detect(screenshot, null, confThreshold);
            if (results.Count == 0) return null;

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
            return best ?? results.OrderByDescending(d => d.Confidence).FirstOrDefault();
        }

        // ── Bitmap <-> Mat 转换 ──

        private static Mat BitmapToMat(Bitmap bitmap)
        {
            // 统一转为 24bppBgr，避免 32bppArgb 通道顺序问题
            using var bmp = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.DrawImage(bitmap, new Rectangle(0, 0, bmp.Width, bmp.Height));
            }
            var bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                // FromPixelData 与源内存共享，需克隆脱离 LockBits 生命周期
                using var mat = Mat.FromPixelData(bmp.Height, bmp.Width, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride);
                return mat.Clone();
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        private static Mat BitmapToGrayMat(Bitmap bitmap)
        {
            using var bgr = BitmapToMat(bitmap);
            var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }

        private static List<Detection> NMS(List<Detection> detections, double iouThreshold)
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
                _template?.Dispose();
                _templateGray?.Dispose();
                _template = null;
                _templateGray = null;
                _disposed = true;
            }
        }
    }
}
