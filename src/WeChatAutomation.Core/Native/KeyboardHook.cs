using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeChatAutomation.Core.Native
{
    public class KeyInfo
    {
        public int VkCode { get; set; }
        public string KeyName { get; set; }
        public bool IsCtrl { get; set; }
        public bool IsAlt { get; set; }
        public bool IsShift { get; set; }
        public bool IsModifier => IsCtrl || IsAlt || IsShift;
        public bool IsText { get; set; } // 是否是可打印字符
        public string Text { get; set; } // 单个字符
    }

    /// <summary>
    /// 全局键盘钩子 - 捕获快捷键 + 录制键盘操作
    /// </summary>
    public class KeyboardHook : IDisposable
    {
        private User32.LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _isCapturing;

        // 热键事件（F9/F10/F11）
        public event EventHandler<int> HotKeyPressed;

        // 录制键盘事件（用于录制键盘操作）
        public event EventHandler<KeyInfo> KeyRecorded;

        public bool IsCapturing => _isCapturing;

        // 热键 VK 码
        public const int VK_F9 = 0x78;
        public const int VK_F10 = 0x79;
        public const int VK_F11 = 0x7A;

        public KeyboardHook() => _proc = HookCallback;

        public void StartCapture()
        {
            if (_isCapturing) return;
            using var cur = Process.GetCurrentProcess();
            using var mod = cur.MainModule;
            _hookId = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _proc,
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
            if (nCode >= 0)
            {
                var st = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)st.vkCode;
                bool isDown = (int)wParam == User32.WM_KEYDOWN;
                bool isUp = (int)wParam == User32.WM_KEYUP;

                // 热键检测（F9/F10/F11）
                if (isDown && (vk == VK_F9 || vk == VK_F10 || vk == VK_F11))
                {
                    HotKeyPressed?.Invoke(this, vk);
                    goto next;
                }

                // 录制键盘操作
                if (isDown)
                {
                    bool ctrl = (User32.GetKeyState(User32.VK_CONTROL) & 0x8000) != 0;
                    bool alt = (User32.GetKeyState(User32.VK_MENU) & 0x8000) != 0;
                    bool shift = (User32.GetKeyState(User32.VK_SHIFT) & 0x8000) != 0;

                    // 判断是否是可打印字符
                    bool isText = !ctrl && !alt && vk >= 0x20 && vk <= 0x7E;

                    var info = new KeyInfo
                    {
                        VkCode = vk,
                        KeyName = GetKeyName(vk),
                        IsCtrl = ctrl,
                        IsAlt = alt,
                        IsShift = shift,
                        IsText = isText,
                        Text = isText ? ((char)vk).ToString() : null
                    };

                    KeyRecorded?.Invoke(this, info);
                }
            }
        next:
            return User32.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private string GetKeyName(int vk)
        {
            return vk switch
            {
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0D => "Enter",
                0x10 => "Shift",
                0x11 => "Ctrl",
                0x12 => "Alt",
                0x13 => "Pause",
                0x14 => "CapsLock",
                0x1B => "Escape",
                0x20 => "Space",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x23 => "End",
                0x24 => "Home",
                0x25 => "Left",
                0x26 => "Up",
                0x27 => "Right",
                0x28 => "Down",
                0x2C => "PrintScreen",
                0x2D => "Insert",
                0x2E => "Delete",
                >= 0x30 and <= 0x39 => ((char)vk).ToString(),
                >= 0x41 and <= 0x5A => ((char)vk).ToString(),
                >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
                0x6A => "Num*",
                0x6B => "Num+",
                0x6D => "Num-",
                0x6E => "Num.",
                0x6F => "Num/",
                >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
                _ => $"0x{vk:X2}"
            };
        }

        public void Dispose() => StopCapture();
    }
}
