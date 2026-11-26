// Doorbell: the click action of a toast, compiled once on the user's machine into
// %LOCALAPPDATA%\claude-code-doorbell\Doorbell.Focus.<hash>.exe and registered as the
// handler for doorbell: links. It selects the session's tab in Windows Terminal by its
// title, then brings the terminal window to the front. It only focuses windows; it
// never runs anything taken from the link.
//
//   doorbell:focus?hwnd=<terminal window>&title=<session title>&n=<toast id>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Doorbell
{
    public static class FocusApp
    {
        static readonly string Log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "claude-code-doorbell", "notify.log");

        static void Write(string message)
        {
            try { File.AppendAllText(Log, DateTime.Now.ToString("s") + " [focus] " + message + Environment.NewLine); }
            catch { }
        }

        static string Normalize(string s)
        {
            return Regex.Replace(s ?? "", @"\s+", " ").Trim().ToLowerInvariant();
        }

        // Selects the tab whose title is the session title. Claude Code titles a tab with
        // a status glyph and the session title, e.g. "* Fix the login page".
        static bool SelectTab(long hwnd, string title)
        {
            string want = Normalize(title);
            if (want.Length < 3) return false;
            AutomationElement window = AutomationElement.FromHandle(new IntPtr(hwnd));
            var isTab = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            AutomationElement match = null;
            foreach (AutomationElement tab in window.FindAll(TreeScope.Descendants, isTab))
            {
                string name = Normalize(Regex.Replace(tab.Current.Name ?? "", @"^[^\p{L}\p{N}]+\s+", ""));
                if (name == want) { match = tab; break; }
                if (match == null && name.Length >= 6 && (name.Contains(want) || want.Contains(name))) match = tab;
            }
            if (match == null) return false;
            ((SelectionItemPattern)match.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            return true;
        }

        // Native.Focus's ways, with UI Automation's own focus request tried second: UIA is
        // sometimes allowed to activate a window when a plain process is not.
        static string BringToFront(long hwnd)
        {
            Native.Restore(hwnd);
            if (Native.TrySetForeground(hwnd)) return "SetForegroundWindow";
            try
            {
                AutomationElement.FromHandle(new IntPtr(hwnd)).SetFocus();
                if (Native.IsForeground(hwnd)) return "UI Automation";
            }
            catch { }
            if (Native.TrySwitchTo(hwnd)) return "SwitchToThisWindow";
            if (Native.TryAttachInput(hwnd)) return "AttachThreadInput";
            if (Native.TryAltTap(hwnd)) return "Alt tap";
            if (Native.TryMinimizeRestore(hwnd)) return "minimize and restore";
            return null;
        }

        // As a clicked notification closes, Windows can hand focus back to the app that had it,
        // undoing the activation above. Watch for two seconds and take focus back if so.
        static string KeepInFront(long hwnd)
        {
            var report = new System.Text.StringBuilder();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int retakes = 0;
            while (clock.ElapsedMilliseconds < 2000)
            {
                System.Threading.Thread.Sleep(50);
                if (Native.IsForegroundNow(hwnd)) continue;
                report.Append(", lost to " + Native.ForegroundDescription() + " after " + clock.ElapsedMilliseconds + " ms");
                if (retakes++ == 3) { report.Append(", gave up"); break; }
                string how = Native.TryAltTap(hwnd) ? "Alt tap"
                           : Native.TrySwitchTo(hwnd) ? "SwitchToThisWindow"
                           : Native.TryAttachInput(hwnd) ? "AttachThreadInput"
                           : Native.TryMinimizeRestore(hwnd) ? "minimize and restore"
                           : null;
                report.Append(", taken back: " + (how ?? "no"));
                if (how == null) break;
            }
            if (report.Length == 0) report.Append(", stayed in front");
            return report.ToString();
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Native.PumpMessages();
            try
            {
                string uri = args.Length > 0 ? args[0] : "";
                Match m = Regex.Match(uri, @"^doorbell:/*focus/?\?(.*)$", RegexOptions.IgnoreCase);
                if (!m.Success) { Write("not a focus link: " + uri); return; }

                var query = new Dictionary<string, string>();
                foreach (string pair in m.Groups[1].Value.Split('&'))
                {
                    string[] kv = pair.Split(new[] { '=' }, 2);
                    if (kv.Length == 2 && kv[0].Length > 0) query[kv[0]] = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                }
                string id = query.ContainsKey("n") ? query["n"] : "";
                long hwnd;
                if (!query.ContainsKey("hwnd") || !long.TryParse(query["hwnd"], out hwnd) || hwnd <= 0)
                {
                    Write("clicked " + id + ", but the pop-up has no terminal window to go to");
                    return;
                }
                if (!Native.IsLiveWindow(hwnd)) { Write("clicked " + id + ", but window " + hwnd + " is gone"); return; }

                var clock = System.Diagnostics.Stopwatch.StartNew();
                long startup = -1;
                try { startup = (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds; } catch { }

                // Select the tab on another thread (UI Automation can take a moment) while
                // waiting for the clicked notification to close: it keeps the foreground
                // until then, and Windows refuses to activate anything else (1.5 s at most).
                bool tab = false;
                long tabAt = -1;
                string title = query.ContainsKey("title") ? query["title"] : "";
                var tabThread = new System.Threading.Thread(() =>
                {
                    try { tab = SelectTab(hwnd, title); }
                    catch (Exception e) { Write("tab: " + e.Message); }
                    tabAt = clock.ElapsedMilliseconds;
                });
                tabThread.SetApartmentState(System.Threading.ApartmentState.MTA);
                tabThread.Start();
                // Keep the main thread checking its message queue while waiting, so Windows
                // sees the app as ready and lets the clicked notification give up the focus.
                Native.PumpMessages();
                while (Native.IsNotificationShellInFront() && clock.ElapsedMilliseconds < 1500)
                {
                    Native.PumpMessages();
                    System.Threading.Thread.Sleep(10);
                }
                long closedAt = clock.ElapsedMilliseconds;
                string before = Native.ForegroundDescription() + (Native.IsNotificationShellInFront() ? ", notification still in front" : "");
                tabThread.Join(200);   // a short head start, so the window comes up on its tab
                string how = BringToFront(hwnd);
                long frontAt = clock.ElapsedMilliseconds;
                string after = how == null ? "" : KeepInFront(hwnd);
                tabThread.Join(3000);
                Write("clicked " + id + ": window " + hwnd + ", tab selected: " + tab + ", in front before: " + before +
                      ", brought to front: " + (how ?? "no, still " + Native.ForegroundDescription()) + after +
                      " | timings: app started " + startup + " ms after launch, notification closed at " + closedAt +
                      " ms, tab selected at " + tabAt + " ms, window in front at " + frontAt + " ms");
            }
            catch (Exception e) { Write(e.ToString()); }
        }
    }
}
