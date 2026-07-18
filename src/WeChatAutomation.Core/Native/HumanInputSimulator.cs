using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Native
{
    public class HumanInputSimulator
    {
        private static readonly Logger _logger = Logger.Instance;
        private static readonly Random _rng = new Random();

        private int _moveStepsMin = 30;
        private int _moveStepsMax = 60;
        private int _stepDelayMinMs = 2;
        private int _stepDelayMaxMs = 6;
        private int _clickDownDelayMinMs = 30;
        private int _clickDownDelayMaxMs = 80;
        private double _jitterAmplitude = 1.5;

        public async Task MoveToAsync(int targetX, int targetY)
        {
            User32.GetCursorPos(out User32.POINT current);
            int startX = current.x;
            int startY = current.y;

            if (startX == targetX && startY == targetY) return;

            double distance = Math.Sqrt(Math.Pow(targetX - startX, 2) + Math.Pow(targetY - startY, 2));
            int steps = (int)Math.Clamp(distance / 3, _moveStepsMin, _moveStepsMax);

            double cp1x = startX + (targetX - startX) * _rng.Next(20, 40) / 100.0 + (_rng.NextDouble() - 0.5) * distance * 0.15;
            double cp1y = startY + (targetY - startY) * _rng.Next(20, 40) / 100.0 + (_rng.NextDouble() - 0.5) * distance * 0.15;
            double cp2x = startX + (targetX - startX) * _rng.Next(60, 80) / 100.0 + (_rng.NextDouble() - 0.5) * distance * 0.15;
            double cp2y = startY + (targetY - startY) * _rng.Next(60, 80) / 100.0 + (_rng.NextDouble() - 0.5) * distance * 0.15;

            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                double eased = EaseInOutCubic(t);

                double bx = CubicBezier(eased, startX, cp1x, cp2x, targetX);
                double by = CubicBezier(eased, startY, cp1y, cp2y, targetY);

                double jitterX = (_rng.NextDouble() - 0.5) * 2 * _jitterAmplitude;
                double jitterY = (_rng.NextDouble() - 0.5) * 2 * _jitterAmplitude;

                int px = (int)Math.Round(bx + jitterX);
                int py = (int)Math.Round(by + jitterY);

                SendMouseAbsoluteMove(px, py);

                int delay = _rng.Next(_stepDelayMinMs, _stepDelayMaxMs + 1);
                await Task.Delay(delay);
            }

            SendMouseAbsoluteMove(targetX, targetY);
        }

        public async Task ClickAsync(int x, int y)
        {
            await MoveToAsync(x, y);

            int preDelay = _rng.Next(20, 60);
            await Task.Delay(preDelay);

            SendMouseClick(x, y, User32.MOUSEEVENTF_LEFTDOWN);
            int holdDelay = _rng.Next(_clickDownDelayMinMs, _clickDownDelayMaxMs + 1);
            await Task.Delay(holdDelay);
            SendMouseClick(x, y, User32.MOUSEEVENTF_LEFTUP);

            int postDelay = _rng.Next(10, 40);
            await Task.Delay(postDelay);
        }

        public async Task DoubleClickAsync(int x, int y)
        {
            await ClickAsync(x, y);
            int interDelay = _rng.Next(50, 120);
            await Task.Delay(interDelay);
            await ClickAsync(x, y);
        }

        public async Task RightClickAsync(int x, int y)
        {
            await MoveToAsync(x, y);

            int preDelay = _rng.Next(20, 60);
            await Task.Delay(preDelay);

            SendMouseClick(x, y, User32.MOUSEEVENTF_RIGHTDOWN);
            int holdDelay = _rng.Next(_clickDownDelayMinMs, _clickDownDelayMaxMs + 1);
            await Task.Delay(holdDelay);
            SendMouseClick(x, y, User32.MOUSEEVENTF_RIGHTUP);
        }

        public async Task TypeTextAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            foreach (char c in text)
            {
                SendChar(c);
                int delay = _rng.Next(30, 100);
                await Task.Delay(delay);
            }
        }

        public async Task KeyComboAsync(params byte[] keys)
        {
            if (keys == null || keys.Length == 0) return;

            var inputs = new List<User32.INPUT>();

            foreach (byte vk in keys)
            {
                inputs.Add(CreateKeyboardInput(vk, 0));
                await Task.Delay(_rng.Next(10, 30));
            }

            SendInputs(inputs.ToArray());

            await Task.Delay(_rng.Next(20, 50));

            for (int i = keys.Length - 1; i >= 0; i--)
            {
                inputs.Add(CreateKeyboardInput(keys[i], User32.KEYEVENTF_KEYUP));
                await Task.Delay(_rng.Next(10, 30));
            }

            SendInputs(inputs.GetRange(keys.Length, keys.Length).ToArray());
        }

        public async Task SendKeysAsync(string keys)
        {
            if (string.IsNullOrEmpty(keys)) return;
            var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries);
            var mods = new List<byte>();
            var keyList = new List<byte>();

            foreach (var p in parts)
            {
                switch (p.Trim().ToLower())
                {
                    case "ctrl": case "control": mods.Add(User32.VK_CONTROL); break;
                    case "shift": mods.Add(User32.VK_SHIFT); break;
                    case "alt": mods.Add(User32.VK_MENU); break;
                    case "enter": case "return": keyList.Add(User32.VK_RETURN); break;
                    case "escape": case "esc": keyList.Add(User32.VK_ESCAPE); break;
                    case "delete": case "del": keyList.Add(User32.VK_DELETE); break;
                    case "backspace": case "bs": keyList.Add(User32.VK_BACK); break;
                    case "tab": keyList.Add(User32.VK_TAB); break;
                    case "space": keyList.Add(User32.VK_SPACE); break;
                    case "home": keyList.Add(User32.VK_HOME); break;
                    case "end": keyList.Add(User32.VK_END); break;
                    case "pageup": keyList.Add(User32.VK_PRIOR); break;
                    case "pagedown": keyList.Add(User32.VK_NEXT); break;
                    case "up": keyList.Add(User32.VK_UP); break;
                    case "down": keyList.Add(User32.VK_DOWN); break;
                    case "left": keyList.Add(User32.VK_LEFT); break;
                    case "right": keyList.Add(User32.VK_RIGHT); break;
                    case "a": keyList.Add(User32.VK_A); break;
                    case "c": keyList.Add(User32.VK_C); break;
                    case "v": keyList.Add(User32.VK_V); break;
                    case "x": keyList.Add(User32.VK_X); break;
                    case "z": keyList.Add(User32.VK_Z); break;
                    case "s": keyList.Add(User32.VK_S); break;
                    default:
                        if (p.Trim().Length == 1) keyList.Add((byte)char.ToUpper(p.Trim()[0]));
                        else if (byte.TryParse(p.Trim(), out byte vk)) keyList.Add(vk);
                        break;
                }
            }

            var allKeys = new List<byte>(mods);
            allKeys.AddRange(keyList);
            await KeyComboAsync(allKeys.ToArray());
        }

        private static double EaseInOutCubic(double t)
        {
            return t < 0.5
                ? 4 * t * t * t
                : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        private static double CubicBezier(double t, double p0, double p1, double p2, double p3)
        {
            double mt = 1 - t;
            return mt * mt * mt * p0 + 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t * p3;
        }

        private static void SendMouseAbsoluteMove(int x, int y)
        {
            var input = new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u = new User32.InputUnion
                {
                    mi = new User32.MOUSEINPUT
                    {
                        dx = User32.CalculateAbsoluteX(x),
                        dy = User32.CalculateAbsoluteY(y),
                        dwFlags = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInputs(new[] { input });
        }

        private static void SendMouseClick(int x, int y, uint flag)
        {
            var input = new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u = new User32.InputUnion
                {
                    mi = new User32.MOUSEINPUT
                    {
                        dx = User32.CalculateAbsoluteX(x),
                        dy = User32.CalculateAbsoluteY(y),
                        dwFlags = flag | User32.MOUSEEVENTF_ABSOLUTE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInputs(new[] { input });
        }

        private static void SendChar(char c)
        {
            var inputs = new User32.INPUT[2];

            inputs[0] = new User32.INPUT
            {
                type = User32.INPUT_KEYBOARD,
                u = new User32.InputUnion
                {
                    ki = new User32.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)c,
                        dwFlags = User32.KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            inputs[1] = new User32.INPUT
            {
                type = User32.INPUT_KEYBOARD,
                u = new User32.InputUnion
                {
                    ki = new User32.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)c,
                        dwFlags = User32.KEYEVENTF_UNICODE | User32.KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInputs(inputs);
        }

        private static User32.INPUT CreateKeyboardInput(byte vk, uint flags)
        {
            return new User32.INPUT
            {
                type = User32.INPUT_KEYBOARD,
                u = new User32.InputUnion
                {
                    ki = new User32.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private static void SendInputs(User32.INPUT[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return;
            User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<User32.INPUT>());
        }
    }
}
