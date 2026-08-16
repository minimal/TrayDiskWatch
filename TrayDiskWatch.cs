// TrayDiskWatch - live disk-space tray gauge for Windows
//
// Shows a colored dot in the system tray that reflects free space on all
// fixed drives (the WORST drive wins), with a dark wedge = used space.
//   green  >= WarnYellow % free (big drives: >= WarnGreenGB  free)
//   yellow >= WarnOrange % free (big drives: >= WarnYellowGB free)
//   orange >= WarnRed    % free (big drives: >= WarnOrangeGB free)
//   red    below that (blinks when critical)
// Each boundary uses the SMALLER requirement (percent-of-drive vs GB floor),
// so a 2 TB drive with 5 GB free is yellow, not red. Tier changes need a
// HysteresisGB margin beyond the boundary, so a drive hovering near one
// can't flap and re-alert constantly.
// Tooltip lists every fixed drive as "C:5.1G(2.6%)", refreshed every
// RefreshSeconds. A balloon notification fires the moment a drive drops
// into a worse tier (and re-arms when it recovers). While a drive stays
// red (< WarnRed), the alert repeats every RedRepeatMinutes.
// Set IncludeDrives (below) to whitelist the drives you care about; leave
// empty to monitor every fixed drive.
//
// To recompile after tweaking the config below, double-click build.bat
// (or run: csc /nologo /target:winexe /out:TrayDiskWatch.exe
//        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll TrayDiskWatch.cs)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

static class Program
{
    // ---- CONFIG ----
    static readonly int    RefreshSeconds = 30;
    static readonly double WarnYellow     = 10.0;   // >= 10% free = green
    static readonly double WarnOrange     = 5.0;    // >= 5% free = yellow
    static readonly double WarnRed        = 2.0;    // >= 2% free = orange, below = red
    static readonly double WarnGreenGB    = 10.0;   // big drives: green once >= 10 GB free
    static readonly double WarnYellowGB   = 5.0;    // big drives: yellow once >= 5 GB free
    static readonly double WarnOrangeGB   = 2.0;    // big drives: orange once >= 2 GB free
    static readonly double HysteresisGB   = 0.5;    // tier changes need this much margin
    static readonly double RedRepeatMinutes = 10.0; // while red, re-alert every N minutes
    static readonly string[] IncludeDrives = new string[] { "C:", "D:", "F:", "O:" };  // empty = all fixed drives
    //                                                        // e.g. { "C:", "D:" } = only these
    const string Aumid = "TrayDiskWatch";
    const string ToastGroup = "TrayDiskWatch";
    // ----------------

    class Drive
    {
        public string Letter;              // "C:"
        public ulong  Free;                // bytes
        public ulong  Total;
        public double FreePct;
        public int    Tier;                // -1 unknown, 0 ok, 1 yellow, 2 orange, 3 red
        public long   LastRedAlert;        // unix seconds of last red alert (0 = none)
    }

    static NotifyIcon ni;
    static System.Windows.Forms.Timer timer;
    static System.Windows.Forms.Timer blinkTimer;
    static double currentWorst = 100.0;
    static int currentWorstTier = 0;
    static bool firstRefresh = true;
    static List<Drive> drives = new List<Drive>();
    static Icon currentIcon = null;
    static IntPtr currentHicon = IntPtr.Zero;
    static string LogPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "TrayDiskWatch.log");

    static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine); } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetDiskFreeSpaceEx(string dir,
        out ulong freeAvail, out ulong totalBytes, out ulong totalFree);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    [STAThread]
    static void Main()
    {
        bool createdNew;
        using (var mutex = new Mutex(true, "TrayDiskWatch_SingleInstance", out createdNew))
        {
            if (!createdNew) { Log("second instance - exiting"); return; } // already running elsewhere

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("CRASH: " + e.ExceptionObject);
            Application.ThreadException += (s, e) => Log("THREAD EXCEPTION: " + e.Exception);
            Log("started");
            RegisterAumid();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            ni = new NotifyIcon();
            ni.Visible = true;
            ni.ContextMenuStrip = BuildMenu();

            timer = new System.Windows.Forms.Timer { Interval = RefreshSeconds * 1000 };
            timer.Tick += (s, e) => Refresh();
            timer.Start();

            blinkTimer = new System.Windows.Forms.Timer { Interval = 500 };
            blinkTimer.Tick += (s, e) => UpdateIcon(currentWorst, currentWorstTier);
            blinkTimer.Enabled = false;

            Refresh();                               // paint immediately on start
            Application.Run();
        }
    }

    static ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Refresh now", null, (s, e) => Refresh());
        m.Items.Add("Open C: in Explorer", null, (s, e) => Process.Start("explorer.exe", "C:\\"));
        m.Items.Add("Storage settings", null, (s, e) => Process.Start("ms-settings:storagesense"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit", null, (s, e) =>
        {
            ni.Visible = false;
            ni.Dispose();
            if (currentHicon != IntPtr.Zero) { DestroyIcon(currentHicon); currentHicon = IntPtr.Zero; }
            Application.Exit();
        });
        return m;
    }

    static void Refresh()
    {
        try
        {
            DoRefresh();
        }
        catch (Exception ex)
        {
            Log("Refresh error: " + ex);
        }
    }

    static double TierBoundaryGB(double totalGB, int tier)
    {
        // free space required to be IN the given tier
        switch (tier)
        {
            case 0: return Math.Min(WarnYellow * totalGB / 100.0, WarnGreenGB);   // green
            case 1: return Math.Min(WarnOrange * totalGB / 100.0, WarnYellowGB);  // yellow
            case 2: return Math.Min(WarnRed    * totalGB / 100.0, WarnOrangeGB);  // orange
            default: return 0.0;
        }
    }

    static void DoRefresh()
    {
        // Re-enumerate drive letters (cheap) but PRESERVE Drive objects (and their
        // tier state) across refreshes, so alerts fire once per crossing, not per tick
        var byLetter = new Dictionary<string, Drive>();
        foreach (var d in drives) byLetter[d.Letter] = d;

        var fresh = new List<Drive>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                string letter = d.Name.Substring(0, 2);
                if (IncludeDrives.Length > 0)
                {
                    bool allowed = false;
                    foreach (var l in IncludeDrives)
                        if (string.Equals(l, letter, StringComparison.OrdinalIgnoreCase)) { allowed = true; break; }
                    if (!allowed) continue;          // not whitelisted -> skip
                }
                Drive drv;
                if (!byLetter.TryGetValue(letter, out drv))
                    drv = new Drive { Letter = letter, Tier = -1 };
                fresh.Add(drv);
            }
        }
        catch { }
        if (fresh.Count > 0) drives = fresh;

        double worst = 100.0;
        int worstTier = 0;
        foreach (var drv in drives)
        {
            ulong free, total, avail;
            if (!GetDiskFreeSpaceEx(drv.Letter + "\\", out avail, out total, out free)) continue;
            drv.Free = free;
            drv.Total = total;
            drv.FreePct = total > 0 ? free * 100.0 / total : 0.0;

            double freeGB = drv.Free / 1073741824.0;
            double totalGB = drv.Total / 1073741824.0;
            int rawTier = freeGB >= TierBoundaryGB(totalGB, 0) ? 0
                        : freeGB >= TierBoundaryGB(totalGB, 1) ? 1
                        : freeGB >= TierBoundaryGB(totalGB, 2) ? 2 : 3;

            // hysteresis: a tier change needs a margin beyond the boundary, so a
            // drive hovering near a boundary can't flap and re-alert constantly
            int newTier = rawTier;
            if (drv.Tier >= 0)
            {
                if (rawTier < drv.Tier && freeGB < TierBoundaryGB(totalGB, rawTier) + HysteresisGB)
                    newTier = drv.Tier;                     // improvement not big enough yet
                else if (rawTier > drv.Tier && freeGB >= TierBoundaryGB(totalGB, drv.Tier) - HysteresisGB)
                    newTier = drv.Tier;                     // drop not deep enough yet
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (drv.Tier == -1)
            {
                // first look: only shout if already critical
                if (newTier == 3) { Alert(drv, 3, false); drv.LastRedAlert = now; }
            }
            else if (newTier > drv.Tier)
            {
                Alert(drv, drv.Tier, false);        // got worse -> notify once per tier
                if (newTier == 3) drv.LastRedAlert = now;
            }
            else if (newTier == 3 && drv.Tier == 3)
            {
                // still red: re-alert every RedRepeatMinutes
                if (now - drv.LastRedAlert >= RedRepeatMinutes * 60)
                {
                    Alert(drv, 3, true);
                    drv.LastRedAlert = now;
                }
            }
            if (newTier < 3) drv.LastRedAlert = 0;  // left red -> reset repeat cycle
            if (newTier < drv.Tier)
            {
                // improved: clear any lingering notification for this drive
                try { ToastNotificationManager.History.Remove(drv.Letter, ToastGroup, Aumid); } catch { }
            }
            drv.Tier = newTier;

            if (drv.FreePct < worst) worst = drv.FreePct;
            if (newTier > worstTier) worstTier = newTier;
        }

        UpdateIcon(worst, worstTier);
        currentWorst = worst;
        currentWorstTier = worstTier;
        blinkTimer.Enabled = worstTier == 3;   // blink while critical

        if (firstRefresh)
        {
            // one-time startup summary so you can see what the app decided
            var sb = new StringBuilder("tiers: ");
            foreach (var d in drives)
                sb.AppendFormat("{0}={1:F1}G/{2:F1}% tier{3}  ", d.Letter, d.Free / 1073741824.0, d.FreePct, d.Tier);
            Log(sb.ToString());
            firstRefresh = false;
        }

        UpdateTooltip();
    }

    static void RegisterAumid()
    {
        // Lets Windows show a friendly name + icon for our toasts in the
        // notification center (unpackaged desktop app pattern)
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
            {
                k.SetValue("DisplayName", "Tray Disk Watch");
                k.SetValue("IconUri", Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "icon.ico"));
            }
            Log("AUMID registered");
        }
        catch (Exception ex) { Log("AUMID registration failed: " + ex.Message); }
    }

    static void Alert(Drive d, int oldTier, bool repeat)
    {
        string title = oldTier == 3 ? "CRITICAL: disk space - " + d.Letter
                     : oldTier == 2 ? "Warning: disk space - " + d.Letter
                     :                "Low disk space - " + d.Letter;
        string text = string.Format("{0} {1:F1} GB free ({2:F1}%). Free up space before you can't save files!",
            d.Letter, d.Free / 1073741824.0, d.FreePct);
        Log((repeat ? "REPEAT ALERT" : "ALERT") + " tier " + oldTier + " - " + d.Letter + " " + text);
        try
        {
            // Windows toast: pops briefly, then persists in the notification
            // center until dismissed. Same Tag/Group replaces the old one.
            var doc = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var nodes = doc.GetElementsByTagName("text");
            nodes.Item(0).AppendChild(doc.CreateTextNode(title));
            nodes.Item(1).AppendChild(doc.CreateTextNode(text));
            var toast = new ToastNotification(doc);
            toast.Tag = d.Letter;
            toast.Group = ToastGroup;
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
            Log("TOAST " + d.Letter);
        }
        catch (Exception ex)
        {
            // fallback: legacy balloon
            Log("toast failed, balloon fallback: " + ex.Message);
            ni.BalloonTipTitle = title;
            ni.BalloonTipText = text;
            ni.BalloonTipIcon = ToolTipIcon.Warning;
            ni.ShowBalloonTip(10000);
        }
    }

    static void UpdateIcon(double worstFreePct, int worstTier)
    {
        // Blink when critical: alternate with a grey dot
        bool blink = worstTier == 3 && ((Environment.TickCount / 500) % 2 == 0);

        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color c = worstTier == 0 ? Color.FromArgb(46, 204, 113)
                        : worstTier == 1 ? Color.FromArgb(241, 196, 15)
                        : worstTier == 2 ? Color.FromArgb(230, 126, 34)
                        :                 Color.FromArgb(231, 76, 60);
                if (blink) c = Color.FromArgb(170, 170, 170);

                using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);

                // dark wedge = used portion (colored remainder = free)
                double used = 1.0 - worstFreePct / 100.0;
                if (used < 0.0) used = 0.0;
                if (used > 1.0) used = 1.0;
                if (used > 0.02)
                {
                    using (var b2 = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                        g.FillPie(b2, 1, 1, 14, 14, -90, (float)(360.0 * used));
                }

                using (var p = new Pen(Color.FromArgb(80, 0, 0, 0))) g.DrawEllipse(p, 1, 1, 14, 14);
            }

            IntPtr h = bmp.GetHicon();
            var icon = Icon.FromHandle(h);
            if (currentIcon != null) currentIcon.Dispose();
            if (currentHicon != IntPtr.Zero) DestroyIcon(currentHicon);
            currentIcon = icon;
            currentHicon = h;
            ni.Icon = icon;
        }
    }

    static void UpdateTooltip()
    {
        // Keep under NotifyIcon's 63-char limit: percent-only, space-separated
        var sb = new StringBuilder();
        foreach (var d in drives)
        {
            if (sb.Length > 0) sb.Append(" ");
            sb.AppendFormat("{0}{1:F1}G({2:F1}%)", d.Letter, d.Free / 1073741824.0, d.FreePct);
        }
        string text = sb.ToString();
        if (text.Length > 63) text = text.Substring(0, 60) + "...";
        ni.Text = "";                                // force refresh even if text is unchanged
        ni.Text = text;
    }
}
