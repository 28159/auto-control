using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SWA = System.Windows.Automation;

namespace WeChatAutomation.Core.Native
{
    /// <summary>
    /// 微信 4.x 等"无障碍客户端检测"应用的 UIA 树激活器。
    ///
    /// 微信 4.x 仅当检测到有 UIAutomation 客户端主动遍历窗口时才暴露完整可交互 UIA 树，
    /// 否则只暴露 Window + 空壳，导致 FlaUI 的 FindByXPath/FindFirstDescendants 定位失败
    /// （症状：UIA 点击"定位失败找不到"，但开启 Windows 讲述人后正常——讲述人是系统辅助功能客户端，附着后系统标记有 AT 客户端，微信随之暴露树）。
    ///
    /// 修复：用 System.Windows.Automation（UIAutomationClient.dll，即 SWA）的
    /// AutomationElement.FromHandle + TreeWalker.ControlViewWalker 主动遍历子控件触发检测。
    /// FlaUI 的 COM 封装（IUIAutomation）不触发此检测，必须用 SWA。
    /// 项目 UseWPF=true 自动包含 UIAutomationClient.dll。
    /// </summary>
    public static class UIATreeActivator
    {
        /// <summary>已激活的进程 PID 集合（同进程窗口树一起暴露，避免重复激活开销）。</summary>
        private static readonly HashSet<int> _activatedPids = new();
        private static readonly object _lock = new();

        /// <summary>
        /// 激活指定窗口的 UIA 树：SWA FromHandle + ControlViewWalker 遍历触发微信暴露完整树。
        /// 按 PID 缓存，同进程只激活一次。激活失败静默忽略（不影响主流程）。
        /// </summary>
        public static void Activate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                User32.GetWindowThreadProcessId(hwnd, out int pid);

                // 按 PID 缓存，避免同进程窗口重复激活
                lock (_lock)
                {
                    if (pid != 0 && _activatedPids.Contains(pid)) return;
                }

                var elt = SWA.AutomationElement.FromHandle(hwnd);
                if (elt == null) return;

                var walker = SWA.TreeWalker.ControlViewWalker;
                // 主动遍历子控件，触发微信无障碍客户端检测，迫使其构建完整 UIA 树
                var child = walker.GetFirstChild(elt);
                if (child != null)
                {
                    // 再向下走一层，确保深层控件也被暴露
                    walker.GetFirstChild(child);
                    walker.GetNextSibling(child);
                }

                // 等待微信构建 UIA 树
                Thread.Sleep(300);

                if (pid != 0)
                {
                    lock (_lock) { _activatedPids.Add(pid); }
                }
            }
            catch
            {
                // 激活失败不影响主流程
            }
        }

        /// <summary>清除激活缓存（微信切换账号/窗口重建后调用，强制下次重新激活）。</summary>
        public static void ClearCache()
        {
            lock (_lock) { _activatedPids.Clear(); }
        }

        /// <summary>
        /// 查找并激活微信主窗口的 UIA 树。供程序启动时预激活，避免首次 UIA 操作定位失败。
        /// 兼容微信4.x(Weixin)与旧版(WeChat)进程名。微信未运行时静默跳过（后续按需激活仍会触发）。
        /// </summary>
        public static void ActivateWeChat()
        {
            try
            {
                // 微信4.x 进程名为 Weixin，旧版为 WeChat，两个都尝试
                var procs = Process.GetProcessesByName("Weixin")
                    .Concat(Process.GetProcessesByName("WeChat"))
                    .ToList();
                if (procs.Count == 0) return;

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            Activate(p.MainWindowHandle);
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch
            {
                // 启动预激活失败不影响程序运行，后续操作会按需激活
            }
        }
    }
}
