using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Blish_HUD;
using Blish_HUD.Controls.Extern;
using Blish_HUD.Controls.Intern;

namespace VoxTyria {

    internal sealed class ChatInjector {

        private const uint WM_CHAR         = 0x0102;
        private const uint WM_KEYDOWN      = 0x0100;
        private const uint WM_KEYUP        = 0x0101;
        private const uint VK_RETURN       = 0x0D;
        private static readonly IntPtr RETURN_DOWN = (IntPtr)(1 | (0x1C << 16));
        private static readonly IntPtr RETURN_UP   = (IntPtr)(1 | (0x1C << 16) | (1 << 30) | (1 << 31));

        private const uint INPUT_KEYBOARD  = 1;
        private const uint MAPVK_VK_TO_VSC = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT {
            public ushort wVk;
            public ushort wScan;
            public uint   dwFlags;
            public uint   time;
            public IntPtr dwExtraInfo;
        }

        // Win32 INPUT on 64-bit: type(4) + padding(4) + union(32) = 40 bytes.
        // Use Explicit layout with Size=40 so Marshal.SizeOf returns exactly 40
        // regardless of CLR padding heuristics.
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct INPUT {
            [FieldOffset(0)] public uint       type;
            [FieldOffset(8)] public KEYBDINPUT ki;   // union starts at offset 8 on 64-bit
        }

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private static List<byte> SnapshotHeldKeys() {
            var held = new List<byte>();
            for (int vk = 0x01; vk <= 0xFE; vk++)
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                    held.Add((byte)vk);
            return held;
        }

        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static uint ResumeKeys(List<byte> keys) {
            if (keys.Count == 0) return 0;
            // Send KEYUP then KEYDOWN for each key.
            // KEYUP resets GW2's internal "key is held" state so the following
            // KEYDOWN is treated as a fresh press and movement resumes.
            var inputs = new INPUT[keys.Count * 2];
            for (int i = 0; i < keys.Count; i++) {
                ushort scan = (ushort)MapVirtualKey(keys[i], MAPVK_VK_TO_VSC);
                inputs[i * 2].type          = INPUT_KEYBOARD;
                inputs[i * 2].ki.wVk        = keys[i];
                inputs[i * 2].ki.wScan      = scan;
                inputs[i * 2].ki.dwFlags    = KEYEVENTF_KEYUP;
                inputs[i * 2 + 1].type      = INPUT_KEYBOARD;
                inputs[i * 2 + 1].ki.wVk    = keys[i];
                inputs[i * 2 + 1].ki.wScan  = scan;
                inputs[i * 2 + 1].ki.dwFlags = 0;
            }
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // ── Low-level keyboard suppressor ─────────────────────────────────────
        // Installs a WH_KEYBOARD_LL hook on a dedicated thread with a message
        // loop (required for LL hooks). All keydown events are swallowed for the
        // lifetime of the suppressor so key-repeats cannot interleave with our
        // injection. No admin rights required.
        private sealed class KeyboardSuppressor : IDisposable {

            private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);

            [DllImport("user32.dll")]
            private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint tid);
            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr h);
            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
            [DllImport("user32.dll")]
            private static extern int GetMessage(out MSG m, IntPtr hwnd, uint min, uint max);
            [DllImport("user32.dll")]
            private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();

            [StructLayout(LayoutKind.Sequential)]
            private struct MSG {
                public IntPtr hwnd; public uint message;
                public IntPtr wParam; public IntPtr lParam;
                public uint time; public long pt;
            }

            private const int  WH_KEYBOARD_LL = 13;
            private const uint WM_QUIT        = 0x0012;
            private const int  WM_KEYDOWN_VAL = 0x100;
            private const int  WM_SYSKEYDOWN  = 0x104;

            private readonly HookProc _proc;
            private uint  _tid;
            private IntPtr _hook;
            private bool  _disposed;
            private readonly ManualResetEventSlim _hookReady   = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _hookRemoved = new ManualResetEventSlim(false);

            public KeyboardSuppressor() {
                _proc = SuppressProc;

                new Thread(() => {
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
                    _tid  = GetCurrentThreadId();
                    _hookReady.Set();
                    MSG m;
                    while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                    UnhookWindowsHookEx(_hook);
                    _hookRemoved.Set(); // signal that the hook is fully gone
                }) { IsBackground = true }.Start();

                _hookReady.Wait();
            }

            private IntPtr SuppressProc(int code, IntPtr w, IntPtr l) {
                if (code >= 0 && ((int)w == WM_KEYDOWN_VAL || (int)w == WM_SYSKEYDOWN))
                    return (IntPtr)1;
                return CallNextHookEx(_hook, code, w, l);
            }

            public void Dispose() {
                if (_disposed) return;
                _disposed = true;
                PostThreadMessage(_tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _hookRemoved.Wait(); // block until hook thread has called UnhookWindowsHookEx
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        public async Task InjectMessageAsync(string message) {
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Message must not be empty.", nameof(message));

            var gw2 = Process.GetProcessesByName("Gw2-64").FirstOrDefault();
            if (gw2 == null) return;
            IntPtr hwnd = gw2.MainWindowHandle;

            // Suppress all physical keystrokes while chat opens so no key-repeats
            // can interleave with our WM_CHAR burst. Hook removed after injection.
            using (new KeyboardSuppressor()) {
                // Open chat and wait for GW2 to activate the input widget.
                PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, RETURN_DOWN);
                PostMessage(hwnd, WM_KEYUP,   (IntPtr)VK_RETURN, RETURN_UP);
                await Task.Delay(150);

                // Burst all chars then Enter — no physical keys can arrive during
                // this window because the hook is still active.
                foreach (char c in message)
                    PostMessage(hwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);

                PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, RETURN_DOWN);
                PostMessage(hwnd, WM_KEYUP,   (IntPtr)VK_RETURN, RETURN_UP);
            } // hook removed here

            // Re-inject held movement keys once GW2 is back in game mode.
            await Task.Delay(50);
            ResumeKeys(SnapshotHeldKeys());
        }
    }
}
