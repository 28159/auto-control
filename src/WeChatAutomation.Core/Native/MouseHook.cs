using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Native
{
    public class MouseClickInfo
    {
        public int X { get; set; }
        public int Y { get; set; }
        public IntPtr WindowHandle { get; set; }
        public string? WindowTitle { get; set; }
        public string? ClassName { get; set; }
        public string? ElementName { get; set; }
        public string? AutomationId { get; set; }
        public string? ControlType { get; set; }
    }

    /// <summary>
    /// 全局鼠标钩子 - 捕获任意应用的点击（排除自身）
    /// </summary>
    public class MouseHook : IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private User32.LowLevelMouseProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _isCapturing;
        private IntPtr _targetWindow = IntPtr.Zero;
        private readonly int _selfPid; // 录制工具自身的进程ID

        public event EventHandler<MouseClickInfo> ClickCaptured;
        public bool IsCapturing => _isCapturing;

        public MouseHook()
        {
            _proc = HookCallback;
            _selfPid = Process.GetCurrentProcess().Id; // 排除自身进程
        }

        public void StartCapture(IntPtr targetWindow = 0)
        {
            if (_isCapturing) return;
            _targetWindow = targetWindow;
            using var cur = Process.GetCurrentProcess();
            using var mod = cur.MainModule;
            _hookId = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _proc,
                User32.GetModuleHandle(mod!.ModuleName), 0);
            _isCapturing = true;
        }

        public void StopCapture()
        {
            if (!_isCapturing) return;
            User32.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _isCapturing = false;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (int)wParam == User32.WM_LBUTTONDOWN)
            {
                var st = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);
                int x = st.pt.x, y = st.pt.y;

                // 获取点击位置的窗口
                IntPtr hwnd = User32.WindowFromPoint(new User32.POINT { x = x, y = y });

                // 排除自身窗口的点击
                if (hwnd != IntPtr.Zero && IsSelfWindow(hwnd))
                    goto next;

                // 检查是否在目标窗口内（如果指定了）
                if (_targetWindow != IntPtr.Zero)
                {
                    User32.GetWindowRect(_targetWindow, out RECT r);
                    if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom)
                        goto next;
                }

                // 在后台线程获取 UIA 信息 + 触发事件（不阻塞钩子线程）
                int capturedX = x, capturedY = y;
                IntPtr capturedHwnd = hwnd;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    var info = new MouseClickInfo { X = capturedX, Y = capturedY, WindowHandle = capturedHwnd };

                    // 获取顶层窗口句柄（Chrome 等应用有子窗口）
                    IntPtr topLevelHwnd = User32.GetAncestor(capturedHwnd, User32.GA_ROOT);
                    if (topLevelHwnd == IntPtr.Zero) topLevelHwnd = capturedHwnd;

                    // 获取顶层窗口标题
                    int titleLen = User32.GetWindowTextLength(topLevelHwnd);
                    if (titleLen > 0)
                    {
                        var sb = new System.Text.StringBuilder(titleLen + 1);
                        User32.GetWindowText(topLevelHwnd, sb, sb.Capacity);
                        info.WindowTitle = sb.ToString();
                    }

                    try
                    {
                        var element = AutomationElement.FromPoint(new System.Windows.Point(capturedX, capturedY));
                        if (element != null)
                        {
                            info.ClassName = element.Current.ClassName;
                            info.ElementName = element.Current.Name;
                            info.AutomationId = element.Current.AutomationId;
                            info.ControlType = element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "";

                            // 尝试获取元素所在的顶层窗口名称
                            if (string.IsNullOrEmpty(info.WindowTitle))
                            {
                                var parentWindow = element.FindFirst(TreeScope.Ancestors,
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
                                if (parentWindow != null)
                                {
                                    info.WindowTitle = parentWindow.Current.Name;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { _logger.Warn("MouseHook", $"UIA 元素捕获失败: {ex.Message}"); }
                    ClickCaptured?.Invoke(this, info);
                });
            }
        next:
            return User32.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        /// <summary>
        /// 判断窗口是否属于录制工具自身
        /// </summary>
        private bool IsSelfWindow(IntPtr hwnd)
        {
            try
            {
                User32.GetWindowThreadProcessId(hwnd, out int pid);
                return pid == _selfPid;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose() => StopCapture();
    }
}
