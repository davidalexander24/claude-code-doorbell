// Doorbell: small Win32 helpers, compiled once into the cache folder by native.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

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

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        const uint TH32CS_SNAPPROCESS = 0x2;
        const uint GA_ROOTOWNER = 3;
        const uint GW_OWNER = 4;
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

        static string ClassOf(IntPtr hwnd)
        {
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            return cls.ToString();
        }

        static string TitleOf(IntPtr hwnd)
        {
            var text = new StringBuilder(512);
            GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }

        // The visible, titled, unowned top-level windows of a process: what a user would
        // call its windows.
        static List<IntPtr> WindowsOf(uint pid)
        {
            var found = new List<IntPtr>();
            EnumWindows((hwnd, param) =>
            {
                uint owner;
                GetWindowThreadProcessId(hwnd, out owner);
                if (owner == pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero && TitleOf(hwnd).Length > 0)
                    found.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Of several windows of one app (VS Code keeps all of its windows in one process),
        // the one whose title names the session's folder or the nearest folder above it,
        // as VS Code's "file - folder - Visual Studio Code" does; else the first.
        static IntPtr PickByFolder(List<IntPtr> windows, string folder)
        {
            for (string dir = folder; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (name.Length == 0) break;
                var named = new Regex(@"(^|[^\p{L}\p{N}_.-])" + Regex.Escape(name) + @"($|[^\p{L}\p{N}_.-])", RegexOptions.IgnoreCase);
                foreach (IntPtr hwnd in windows)
                    if (named.IsMatch(TitleOf(hwnd))) return hwnd;
            }
            return windows[0];
        }

        // The top-level window showing pid's session: the Windows Terminal or VS Code window
        // that hosts its terminal (each owns the ConPTY pseudo-console window of the
        // terminals it hosts), or a classic console window. Without a console (as in the VS
        // Code extension's own panel), the nearest ancestor with a window, choosing by folder
        // name when it has several.
        public static long HostWindow(uint pid, string folder)
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
                        else if (ClassOf(console) != "PseudoConsoleWindow" && IsWindowVisible(console)) found = console;
                    }
                }
                finally { FreeConsole(); }
            }
            if (found != IntPtr.Zero) return found.ToInt64();

            var map = Processes();
            uint current = pid;
            for (int depth = 0; depth < 8 && current != 0; depth++)
            {
                List<IntPtr> windows = WindowsOf(current);
                if (windows.Count == 1) return windows[0].ToInt64();
                if (windows.Count > 1) return PickByFolder(windows, folder).ToInt64();
                KeyValuePair<uint, string> info;
                if (!map.TryGetValue(current, out info)) break;
                current = info.Key;
            }
            return 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetConsoleTitle(StringBuilder title, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetConsoleTitle(string title);

        // Attaches this process to the console of pid (a running claude process) and
        // returns that console's title, which is what its terminal tab shows; null if it
        // can't. Call LeaveConsole when done.
        public static string AttachToConsole(uint pid)
        {
            try
            {
                if (pid == 0 || !Process.GetProcessById((int)pid).ProcessName.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return null;
            }
            catch { return null; }
            FreeConsole();
            if (!AttachConsole(pid)) return null;
            return ConsoleTitle();
        }

        public static string ConsoleTitle()
        {
            var title = new StringBuilder(1024);
            GetConsoleTitle(title, (uint)title.Capacity);
            return title.ToString();
        }

        public static void SetTitle(string title) { SetConsoleTitle(title); }

        public static void LeaveConsole() { FreeConsole(); }

        public static bool IsLiveWindow(long hwnd)
        {
            return hwnd > 0 && IsWindow(new IntPtr(hwnd));
        }

        public static bool IsWindowsTerminal(long hwnd)
        {
            return ClassOf(new IntPtr(hwnd)) == "CASCADIA_HOSTING_WINDOW_CLASS";
        }

        // VS Code and the other Electron apps.
        public static bool IsChromium(long hwnd)
        {
            return ClassOf(new IntPtr(hwnd)) == "Chrome_WidgetWin_1";
        }

        public static bool IsForegroundNow(long hwnd)
        {
            return GetForegroundWindow() == new IntPtr(hwnd);
        }

        // Restores the window if minimized and makes it the foreground window. Windows
        // allows this only while the app may set the foreground, as a clicked pop-up's
        // activator may; activation is applied asynchronously, so give it a moment.
        public static bool BringToFront(long hwnd)
        {
            IntPtr target = new IntPtr(hwnd);
            if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
            SetForegroundWindow(target);
            for (int i = 0; i < 8; i++)
            {
                if (GetForegroundWindow() == target) return true;
                System.Threading.Thread.Sleep(25);
            }
            return false;
        }

        public static string WindowDescription(long hwnd)
        {
            IntPtr h = new IntPtr(hwnd);
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            string name = "?";
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
            return name + "/" + ClassOf(h);
        }

        public static string ForegroundDescription()
        {
            IntPtr fg = GetForegroundWindow();
            return fg == IntPtr.Zero ? "nothing" : WindowDescription(fg.ToInt64());
        }
    }
}
