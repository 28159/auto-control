using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Native;

namespace WeChatAutomation.Core.Vision
{
    public class WindowCapturer
    {
        private static readonly Logger _logger = Logger.Instance;

        public static Bitmap CaptureWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;

            try
            {
                User32.GetWindowRect(hwnd, out RECT rect);
                int width = rect.Width;
                int height = rect.Height;

                if (width <= 0 || height <= 0) return null;

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                        new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                return bmp;
            }
            catch (Exception ex)
            {
                _logger.Warn("WindowCapturer", $"截图失败: {ex.Message}");
                return null;
            }
        }

        public static Bitmap CaptureWindowRegion(IntPtr hwnd, int x, int y, int width, int height)
        {
            if (hwnd == IntPtr.Zero) return null;

            try
            {
                User32.GetWindowRect(hwnd, out RECT rect);

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(rect.Left + x, rect.Top + y, 0, 0,
                        new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                return bmp;
            }
            catch (Exception ex)
            {
                _logger.Warn("WindowCapturer", $"区域截图失败: {ex.Message}");
                return null;
            }
        }

        public static Bitmap CaptureScreen()
        {
            try
            {
                var bounds = System.Windows.SystemParameters.PrimaryScreenWidth;
                int w = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                int h = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }

                return bmp;
            }
            catch (Exception ex)
            {
                _logger.Warn("WindowCapturer", $"全屏截图失败: {ex.Message}");
                return null;
            }
        }

        public static (int X, int Y, int Width, int Height) GetWindowRect(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return (0, 0, 0, 0);
            User32.GetWindowRect(hwnd, out RECT rect);
            return (rect.Left, rect.Top, rect.Width, rect.Height);
        }
    }
}
