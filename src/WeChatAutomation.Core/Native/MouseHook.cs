using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.UIA3;
using WeChatAutomation.Core.Logging;
using WeChatAutomation.Core.Recording;

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
        public string? XPath { get; set; }
        public int SiblingIndex { get; set; }
        public string? RuntimeId { get; set; }
    }

    /// <summary>
    /// 全局鼠标钩子 - 捕获任意应用的点击（排除自身）
    /// </summary>
    public class MouseHook : IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;
        private static readonly UIA3Automation _automation = new();
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
                        var rawElement = _automation.FromPoint(new System.Drawing.Point(capturedX, capturedY));
                        if (rawElement != null)
                        {
                            // 将非交互式元素（Text、Image、Pane、Group）提升到最近的交互式祖先
                            var element = PromoteToInteractive(rawElement);
                            info.ClassName = element.ClassName ?? "";
                            info.ElementName = element.Name ?? "";
                            info.AutomationId = element.AutomationId ?? "";
                            info.ControlType = element.ControlType.ToString();

                            // 尝试获取元素所在的顶层窗口名称
                            if (string.IsNullOrEmpty(info.WindowTitle))
                            {
                                try
                                {
                                    var parent = element.Parent;
                                    while (parent != null)
                                    {
                                        if (parent.ControlType == FlaUI.Core.Definitions.ControlType.Window)
                                        {
                                            info.WindowTitle = parent.Name;
                                            break;
                                        }
                                        parent = parent.Parent;
                                    }
                                }
                                catch { /* 父级遍历失败则忽略 */ }
                            }

                            // 构建 XPath 路径（使用提升后的元素）
                            try
                            {
                                info.XPath = XPathBuilder.BuildXPath(element);
                                info.SiblingIndex = XPathBuilder.ComputeSiblingIndex(element);
                                info.RuntimeId = XPathBuilder.ExtractRuntimeId(element);
                            }
                            catch (Exception ex) { _logger.Warn("MouseHook", $"XPath 构建失败: {ex.Message}"); }
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

        /// <summary>
        /// 将非交互式元素（Text、Image、Pane、Group）提升到最近的交互式祖先。
        /// FromPoint 返回最深层叶子，但用户实际点击的是交互式父元素（如 Button）。
        /// </summary>
        private static FlaUI.Core.AutomationElements.AutomationElement PromoteToInteractive(
            FlaUI.Core.AutomationElements.AutomationElement element)
        {
            if (element == null) return element;

            // 非交互式 ControlType：这些元素通常不可直接点击，是交互式控件的子元素
            var nonInteractiveTypes = new HashSet<FlaUI.Core.Definitions.ControlType>
            {
                FlaUI.Core.Definitions.ControlType.Text,
                FlaUI.Core.Definitions.ControlType.Image,
                FlaUI.Core.Definitions.ControlType.Pane,
                FlaUI.Core.Definitions.ControlType.Group,
                FlaUI.Core.Definitions.ControlType.Header,
                FlaUI.Core.Definitions.ControlType.HeaderItem,
                FlaUI.Core.Definitions.ControlType.Separator,
            };

            // 不应作为提升终点的 ControlType：Window/TitleBar/Custom/Thumb 等
            // 这些虽不在 nonInteractiveTypes 中，但作为提升终点会丢失控件信息（捕获到窗口级元数据）
            var stopBarrierTypes = new HashSet<FlaUI.Core.Definitions.ControlType>
            {
                FlaUI.Core.Definitions.ControlType.Window,
                FlaUI.Core.Definitions.ControlType.TitleBar,
                FlaUI.Core.Definitions.ControlType.Custom,
                FlaUI.Core.Definitions.ControlType.Thumb,
            };

            var current = element;
            int maxSteps = 5;
            bool promoted = false;

            while (current != null && maxSteps-- > 0)
            {
                // 遇到不应作为提升终点的类型（如 Window），停止且不提升到此元素
                if (stopBarrierTypes.Contains(current.ControlType))
                    break;

                // 当前元素是交互式类型且不在屏障集合中，停止提升
                if (!nonInteractiveTypes.Contains(current.ControlType))
                {
                    promoted = true;
                    break;
                }

                // 当前元素支持 InvokePattern（可点击），停止提升
                try
                {
                    var invokePattern = current.Patterns.Invoke.PatternOrDefault;
                    if (invokePattern != null) { promoted = true; break; }
                }
                catch { }

                // 向上提升到父元素
                try
                {
                    var parent = current.Parent;
                    if (parent == null) break; // 已到达根节点
                    current = parent;
                }
                catch
                {
                    break; // 无法获取父元素
                }
            }

            // 若提升命中了屏障类型（Window 等）或未找到合适终点，回退到原始元素，
            // 避免捕获窗口级元数据（ClassName=窗口类、Name=窗口标题）导致回放定位错误
            if (!promoted)
                current = element;

            // 日志记录提升情况
            if (current != null && !ReferenceEquals(element, current))
            {
                _logger.Info("MouseHook",
                    $"元素提升: {element.ControlType}[@Name='{element.Name}'] -> {current.ControlType}[@Name='{current.Name}']");
            }

            return current ?? element;
        }

        public void Dispose() => StopCapture();
    }
}
