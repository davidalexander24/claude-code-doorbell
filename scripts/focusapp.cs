// Doorbell: what clicking a pop-up does. Compiled once on the user's machine into
// %LOCALAPPDATA%\claude-code-doorbell\Doorbell.Focus.exe and registered as the pop-ups'
// COM activator (CustomActivator of the ClaudeCodeDoorbell app identity). When a pop-up
// is clicked, Windows starts this app, calls Activate with the pop-up's arguments and
// lets it bring a window to the front. It selects the session's tab, found through the
// session's console (in Windows Terminal, and in VS Code where it also puts the cursor
// in that terminal), then brings the session's window to the front. It only focuses
// windows and tabs and marks the session's console title for a moment; it never runs
// anything taken from the pop-up.
//
//   focus?hwnd=<window>&pid=<claude process>&title=<session title>&n=<pop-up id>
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Doorbell
{
    [StructLayout(LayoutKind.Sequential)]
    public struct NotificationUserInput
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Key;
        [MarshalAs(UnmanagedType.LPWStr)] public string Value;
    }

    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationActivationCallback
    {
        void Activate(
            [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [In, MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] NotificationUserInput[] data,
            [In, MarshalAs(UnmanagedType.U4)] uint count);
    }

    // Keep the GUID in sync with $activatorClsid in notify.ps1.
    [ComVisible(true), Guid("1C6EDB03-608D-455E-847C-2FE3331B1016"), ClassInterface(ClassInterfaceType.None)]
    public class PopupActivator : INotificationActivationCallback
    {
        public void Activate(string appUserModelId, string invokedArgs, NotificationUserInput[] data, uint count)
        {
            FocusApp.OnClick(invokedArgs);
        }
    }

    public static class FocusApp
    {
        static readonly string Log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "claude-code-doorbell", "notify.log");

        static readonly ManualResetEvent Clicked = new ManualResetEvent(false);
        static readonly System.Diagnostics.Stopwatch Clock = new System.Diagnostics.Stopwatch();
        static string report = "";
        static long hwnd;

        static void Write(string message)
        {
            try { File.AppendAllText(Log, DateTime.Now.ToString("s") + " [click] " + message + Environment.NewLine); }
            catch { }
        }

        static string Normalize(string s)
        {
            return Regex.Replace(s ?? "", @"\s+", " ").Trim().ToLowerInvariant();
        }

        // A tab's title as Claude Code set it, without the status glyph in front ("* Fix
        // the login page") or VS Code's ", Editor Group 2" after it.
        static string TabTitle(string name)
        {
            name = Regex.Replace(name ?? "", @"^[^\p{L}\p{N}]+\s+", "");
            return Normalize(Regex.Replace(name, @",\s*Editor Group \d+$", ""));
        }

        // Invisible characters added to the session's console title for a moment, so its
        // Windows Terminal tab can be told apart from tabs with the same title (every
        // session Claude Code hasn't named yet is called "Claude Code").
        const string Marker = "\u200B\u2060\u200B";

        // The window's tabs, without VS Code's activity bar (also tabs to UI Automation, one
        // of them called "Claude Code"). Windows Terminal answers with none for a moment
        // when it hasn't been asked in a while, so ask again until some show up.
        static List<AutomationElement> Tabs(AutomationElement root, int timeoutMs)
        {
            var isTab = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var tabs = new List<AutomationElement>();
            while (true)
            {
                foreach (AutomationElement tab in root.FindAll(TreeScope.Descendants, isTab))
                    if (!(tab.Current.ClassName ?? "").StartsWith("action-item")) tabs.Add(tab);
                if (tabs.Count > 0 || clock.ElapsedMilliseconds > timeoutMs) return tabs;
                Thread.Sleep(30);
            }
        }

        // The tab whose title is the given title; with several, none, since picking the
        // wrong one is worse than leaving the window as it was.
        static AutomationElement TabByTitle(List<AutomationElement> tabs, string title)
        {
            string want = TabTitle(title);
            if (want.Length < 3) return null;
            var exact = tabs.FindAll(t => TabTitle(t.Current.Name) == want);
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) return null;
            var close = tabs.FindAll(t =>
            {
                string name = TabTitle(t.Current.Name);
                return name.Length >= 6 && (name.Contains(want) || want.Contains(name));
            });
            return close.Count == 1 ? close[0] : null;
        }

        // Shows the session's tab: in Windows Terminal, and in VS Code, which runs Claude Code
        // in terminal tabs. Other windows are left alone. The tab is found through the
        // session's console, whose title is what its tab shows right now; the title the hook
        // saw (which a new session doesn't have yet) is the fallback.
        static string ShowTab(long window, uint pid, string title)
        {
            bool terminal = Native.IsWindowsTerminal(window), editor = Native.IsChromium(window);
            if (!(terminal || editor)) return "no";
            AutomationElement root = AutomationElement.FromHandle(new IntPtr(window));
            string current = Native.AttachToConsole(pid);
            bool marked = false;
            try
            {
                AutomationElement match = null;
                string how = "";
                if (terminal && !string.IsNullOrEmpty(current))
                {
                    Native.SetTitle(current + Marker);
                    marked = true;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (match == null && clock.ElapsedMilliseconds < 1000)
                    {
                        match = Tabs(root, 1000).Find(t => (t.Current.Name ?? "").Contains(Marker));
                        if (match == null) Thread.Sleep(30);
                    }
                    if (match != null) how = " (by console)";
                }
                if (match == null)
                {
                    var tabs = Tabs(root, 1000);
                    match = TabByTitle(tabs, current) ?? TabByTitle(tabs, title);
                    if (match != null) how = " (by title)";
                }

                string shown = "no";
                bool selected = false;
                if (match != null)
                {
                    var item = (SelectionItemPattern)match.GetCurrentPattern(SelectionItemPattern.Pattern);
                    shown = (item.Current.IsSelected ? "already shown" : "yes") + how;
                    if (!item.Current.IsSelected) { item.Select(); selected = true; }
                }
                // Also covers a session in VS Code's terminal panel, which has no tab of its own.
                if (editor) shown += FocusTerminal(root, new[] { current, title }, selected ? 10 : 1);
                return shown;
            }
            finally
            {
                // Put the title back, unless Claude Code has already set a new one.
                if (marked && Native.ConsoleTitle() == current + Marker) Native.SetTitle(current);
                if (current != null) Native.LeaveConsole();
            }
        }

        // In VS Code, selecting a tab that is already showing doesn't make its editor group
        // active. Focusing the terminal's input does, and puts the cursor in Claude Code's
        // prompt. The input, named after the terminal's title, appears a moment after its
        // tab is selected.
        static string FocusTerminal(AutomationElement root, string[] titles, int attempts)
        {
            var isInput = new PropertyCondition(AutomationElement.ClassNameProperty, "xterm-helper-textarea");
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var inputs = new List<AutomationElement>();
                foreach (AutomationElement input in root.FindAll(TreeScope.Descendants, isInput)) inputs.Add(input);
                foreach (string title in titles)
                {
                    string want = TabTitle(title);
                    if (want.Length < 3) continue;
                    var named = inputs.FindAll(i => TabTitle(i.Current.Name).Contains(want));
                    if (named.Count != 1) continue;
                    named[0].SetFocus();
                    return ", terminal focused";
                }
                Thread.Sleep(50);
            }
            return ", terminal not found";
        }

        static Dictionary<string, string> ParseArgs(string args)
        {
            var query = new Dictionary<string, string>();
            Match m = Regex.Match(args ?? "", @"^focus\?(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) return query;
            foreach (string pair in m.Groups[1].Value.Split('&'))
            {
                string[] kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && kv[0].Length > 0) query[kv[0]] = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }
            return query;
        }

        // Called by Windows when a pop-up is clicked. Windows allows this app to set the
        // foreground window only around this call, so the window is brought up before
        // returning; the rest (logging, checking it stayed in front) happens after.
        public static void OnClick(string args)
        {
            if (Clicked.WaitOne(0)) return;
            try
            {
                Clock.Start();
                if (!Regex.IsMatch(args ?? "", @"^focus\?", RegexOptions.IgnoreCase))
                {
                    report = "clicked a pop-up from Doorbell 0.2.0 or older, which doesn't say which session it is from";
                    return;
                }
                var query = ParseArgs(args);
                string id = query.ContainsKey("n") ? query["n"] : "";
                string before = Native.ForegroundDescription();
                if (!query.ContainsKey("hwnd") || !long.TryParse(query["hwnd"], out hwnd) || hwnd <= 0)
                {
                    report = "clicked " + id + ", but the pop-up has no window to go to";
                    hwnd = 0;
                    return;
                }
                if (!Native.IsLiveWindow(hwnd)) { report = "clicked " + id + ", but window " + hwnd + " is gone"; hwnd = 0; return; }

                // Show the tab first (on another thread; UI Automation can take a moment),
                // with a short head start so the window comes up on the right tab.
                string tab = "no";
                long tabAt = -1;
                string title = query.ContainsKey("title") ? query["title"] : "";
                uint pid;
                if (!query.ContainsKey("pid") || !uint.TryParse(query["pid"], out pid)) pid = 0;
                var tabThread = new Thread(() =>
                {
                    try { tab = ShowTab(hwnd, pid, title); }
                    catch (Exception e) { tab = "failed: " + e.Message; }
                    tabAt = Clock.ElapsedMilliseconds;
                });
                tabThread.SetApartmentState(ApartmentState.MTA);
                tabThread.Start();
                tabThread.Join(250);

                bool front = Native.BringToFront(hwnd);
                long frontAt = Clock.ElapsedMilliseconds;
                tabThread.Join(4000);
                report = "clicked " + id + ": window " + hwnd + " (" + Native.WindowDescription(hwnd) + "), in front before: " + before +
                         ", brought to front: " + (front ? "yes" : "no, still " + Native.ForegroundDescription()) +
                         ", tab: " + tab + " | at " + frontAt + " ms, tab at " + tabAt + " ms";
            }
            catch (Exception e) { report = e.ToString(); hwnd = 0; }
            finally { Clicked.Set(); }
        }

        // As a clicked pop-up closes, Windows could hand the focus back to where it was.
        // Watch for a second and report what happened.
        static string Watch()
        {
            if (hwnd == 0) return "";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1000)
            {
                Thread.Sleep(50);
                if (!Native.IsForegroundNow(hwnd))
                {
                    string lost = ", lost to " + Native.ForegroundDescription() + " " + watch.ElapsedMilliseconds + " ms after";
                    return lost + (Native.BringToFront(hwnd) ? ", taken back" : ", not taken back");
                }
            }
            return ", stayed in front";
        }

        [MTAThread]
        public static void Main(string[] args)
        {
            var services = new RegistrationServices();
            int cookie = services.RegisterTypeForComClients(typeof(PopupActivator),
                RegistrationClassContext.LocalServer, RegistrationConnectionType.SingleUse);
            try
            {
                // Windows calls Activate right after starting this app; leave if it never does.
                if (!Clicked.WaitOne(15000)) { Write("started, but no click arrived"); return; }
                Write(report + Watch());
            }
            finally
            {
                // Give the reply to Windows a moment to go out before the process ends.
                Thread.Sleep(200);
                services.UnregisterTypeForComClients(cookie);
            }
        }
    }
}
