// Doorbell: small Win32 helpers, compiled once into the cache folder by native.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Doorbell
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        const uint TH32CS_SNAPPROCESS = 0x2;
        const uint GA_ROOTOWNER = 3;
        const int SW_RESTORE = 9;

        // Process id -> (parent id, exe name), from one snapshot.
        static Dictionary<uint, KeyValuePair<uint, string>> Processes()
        {
            var map = new Dictionary<uint, KeyValuePair<uint, string>>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32FirstW(snap, ref entry))
                {
                    do { map[entry.th32ProcessID] = new KeyValuePair<uint, string>(entry.th32ParentProcessID, entry.szExeFile); }
                    while (Process32NextW(snap, ref entry));
                }
            }
            finally { CloseHandle(snap); }
            return map;
        }

        // The nearest ancestor of pid named claude*.exe; else pid's direct parent; else 0.
        public static uint FindClaudeAncestor(uint pid)
        {
            var map = Processes();
            KeyValuePair<uint, string> self;
            if (!map.TryGetValue(pid, out self)) return 0;
            uint parent = self.Key;
            uint current = parent;
            for (int depth = 0; depth < 8 && current != 0; depth++)
            {
                KeyValuePair<uint, string> info;
                if (!map.TryGetValue(current, out info)) break;
                if (info.Value.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return current;
                current = info.Key;
            }
            return parent;
        }

        // The top-level window showing pid's console: the Windows Terminal window that hosts
        // its tab (the ConPTY pseudo-console window is owned by it), or a classic console
        // window. Otherwise the nearest ancestor with a main window (VS Code, other hosts).
        public static long HostWindow(uint pid)
        {
            IntPtr found = IntPtr.Zero;
            FreeConsole();
            if (AttachConsole(pid))
            {
                try
                {
                    IntPtr console = GetConsoleWindow();
                    if (console != IntPtr.Zero)
                    {
                        IntPtr owner = GetAncestor(console, GA_ROOTOWNER);
                        if (owner != IntPtr.Zero && owner != console) found = owner;
                        else
                        {
                            var cls = new StringBuilder(256);
                            GetClassName(console, cls, cls.Capacity);
                            if (cls.ToString() != "PseudoConsoleWindow" && IsWindowVisible(console)) found = console;
                        }
                    }
                }
                finally { FreeConsole(); }
            }
            if (found != IntPtr.Zero) return found.ToInt64();

            var map = Processes();
            uint current = pid;
            for (int depth = 0; depth < 8 && current != 0; depth++)
            {
                try
                {
                    IntPtr main = Process.GetProcessById((int)current).MainWindowHandle;
                    if (main != IntPtr.Zero) return main.ToInt64();
                }
                catch { }
                KeyValuePair<uint, string> info;
                if (!map.TryGetValue(current, out info)) break;
                current = info.Key;
            }
            return 0;
        }

        public static bool IsLiveWindow(long hwnd)
        {
            return hwnd > 0 && IsWindow(new IntPtr(hwnd));
        }

        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

        const int SW_MINIMIZE = 6;

        // Windows applies activation asynchronously; give it a moment before checking.
        public static bool IsForeground(long hwnd)
        {
            IntPtr target = new IntPtr(hwnd);
            for (int i = 0; i < 6; i++)
            {
                if (GetForegroundWindow() == target) return true;
                System.Threading.Thread.Sleep(25);
            }
            return false;
        }

        // True while the foreground window belongs to the Windows shell process that draws
        // notifications and the notification center. Windows refuses to hand the
        // foreground to another app while it has it.
        public static bool IsNotificationShellInFront()
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            try { return Process.GetProcessById((int)pid).ProcessName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

        // Checks this thread's message queue. Windows treats an app that launched from a
        // click as busy until its main thread does this, and the clicked notification keeps
        // the focus until then.
        public static void PumpMessages()
        {
            MSG msg;
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1)) { }
        }

        public static bool IsForegroundNow(long hwnd)
        {
            return GetForegroundWindow() == new IntPtr(hwnd);
        }

        public static string ForegroundDescription()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return "nothing";
            var cls = new StringBuilder(256);
            GetClassName(fg, cls, cls.Capacity);
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            string name = "?";
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
            return name + "/" + cls;
        }

        public static void Restore(long hwnd)
        {
            IntPtr target = new IntPtr(hwnd);
            if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
        }

        public static bool TrySetForeground(long hwnd)
        {
            SetForegroundWindow(new IntPtr(hwnd));
            return IsForeground(hwnd);
        }

        // The call Alt+Tab uses; often allowed when SetForegroundWindow is refused.
        public static bool TrySwitchTo(long hwnd)
        {
            SwitchToThisWindow(new IntPtr(hwnd), true);
            return IsForeground(hwnd);
        }

        // Borrows the input queue of the thread that owns the current foreground window.
        public static bool TryAttachInput(long hwnd)
        {
            IntPtr target = new IntPtr(hwnd);
            uint ignored;
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
            uint thisThread = GetCurrentThreadId();
            if (foregroundThread == 0 || foregroundThread == thisThread) return false;
            if (!AttachThreadInput(thisThread, foregroundThread, true)) return false;
            try { BringWindowToTop(target); SetForegroundWindow(target); }
            finally { AttachThreadInput(thisThread, foregroundThread, false); }
            return IsForeground(hwnd);
        }

        // A short Alt tap counts as user input, which lifts the foreground lock.
        public static bool TryAltTap(long hwnd)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(new IntPtr(hwnd));
            return IsForeground(hwnd);
        }

        // Last resort: restoring a minimized window activates it.
        public static bool TryMinimizeRestore(long hwnd)
        {
            IntPtr target = new IntPtr(hwnd);
            ShowWindow(target, SW_MINIMIZE);
            ShowWindow(target, SW_RESTORE);
            SetForegroundWindow(target);
            return IsForeground(hwnd);
        }

        // Restores the window if minimized and brings it to the front, trying each way in
        // turn because Windows only lets a process take the foreground in some situations.
        // Returns the way that worked, or null.
        public static string Focus(long hwnd)
        {
            if (!IsWindow(new IntPtr(hwnd))) return null;
            Restore(hwnd);
            if (TrySetForeground(hwnd)) return "SetForegroundWindow";
            if (TrySwitchTo(hwnd)) return "SwitchToThisWindow";
            if (TryAttachInput(hwnd)) return "AttachThreadInput";
            if (TryAltTap(hwnd)) return "Alt tap";
            if (TryMinimizeRestore(hwnd)) return "minimize and restore";
            return null;
        }
    }
}
