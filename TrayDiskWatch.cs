// TrayDiskWatch - live disk-space tray gauge for Windows
//
// Shows a coloured square in the system tray that reflects free space on all
// fixed drives (the WORST drive wins). Used space fills the square from the
// bottom in the tier colour at full strength; free space is the same hue tinted
// pale; a dark frame holds the shape on a light taskbar. A nearly full drive is
// therefore a solid block of its tier colour, which is the state that must not
// be missed.
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

static partial class Program
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

    enum Tier { Unknown = -1, Green = 0, Yellow = 1, Orange = 2, Red = 3 }

    class TierInfo
    {
        public readonly Tier   Tier;
        public readonly double MinPercentFree;   // percent of the drive that must be free
        public readonly double MinGBFree;        // absolute floor; the SMALLER of the two wins
        public readonly int    Rgb;              // tier colour
        public readonly string AlertTitle;       // toast headline

        public TierInfo(Tier tier, double minPercentFree, double minGBFree, int rgb, string alertTitle)
        {
            Tier = tier;
            MinPercentFree = minPercentFree;
            MinGBFree = minGBFree;
            Rgb = rgb;
            AlertTitle = alertTitle;
        }

        public double BoundaryGB(double totalGB)
        {
            // free space required to be IN this tier: the smaller of the two
            // requirements, so a 2 TB drive with 5 GB free reads yellow, not red
            return Math.Min(MinPercentFree * totalGB / 100.0, MinGBFree);
        }
    }

    // One table for the whole tier domain - boundary, colour, toast headline. The
    // thresholds stay in the config block above, so it is still the one place to tune.
    static readonly TierInfo[] TierTable = new TierInfo[]
    {
        new TierInfo(Tier.Green,  WarnYellow, WarnGreenGB,  0x2ECC71, "Low disk space"),
        new TierInfo(Tier.Yellow, WarnOrange, WarnYellowGB, 0xF1C40F, "Low disk space"),
        new TierInfo(Tier.Orange, WarnRed,    WarnOrangeGB, 0xE67E22, "Warning: disk space"),
        new TierInfo(Tier.Red,    0.0,        0.0,          0xE74C3C, "CRITICAL: disk space"),
    };

    const double BytesPerGB = 1073741824.0;

    class Drive
    {
        public string Letter;              // "C:"
        public ulong  Free;                // bytes
        public ulong  Total;
        public double FreePct;             // derived from Free/Total on each refresh
        public Tier   Tier;                // Unknown until the first refresh
        public long   LastRedAlert;        // unix seconds of last red alert (0 = none)

        public double FreeGB  { get { return Free / BytesPerGB; } }
        public double TotalGB { get { return Total / BytesPerGB; } }
    }

    static NotifyIcon ni;
    static System.Windows.Forms.Timer timer;
    static System.Windows.Forms.Timer blinkTimer;
    static double currentWorst = 100.0;
    static Tier currentWorstTier = Tier.Green;
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

#if !TESTS
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
#endif

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

    static TierInfo Info(Tier tier)
    {
        foreach (var info in TierTable)
            if (info.Tier == tier) return info;
        return TierTable[TierTable.Length - 1];     // red: the safe default
    }

    static double TierBoundaryGB(double totalGB, Tier tier)
    {
        return Info(tier).BoundaryGB(totalGB);
    }

    static Tier RawTier(double freeGB, double totalGB)
    {
        // tier the drive is in right now, before hysteresis
        foreach (var info in TierTable)
            if (freeGB >= info.BoundaryGB(totalGB)) return info.Tier;
        return Tier.Red;
    }

    static Tier ApplyHysteresis(Tier rawTier, Tier previousTier, double freeGB, double totalGB)
    {
        // a tier change needs HysteresisGB of margin beyond the boundary, so a
        // drive hovering near one can't flap and re-alert constantly
        if (previousTier == Tier.Unknown) return rawTier;   // no history: nothing to hold
        if (rawTier < previousTier && freeGB < TierBoundaryGB(totalGB, rawTier) + HysteresisGB)
            return previousTier;                        // improvement not big enough yet
        if (rawTier > previousTier && freeGB >= TierBoundaryGB(totalGB, previousTier) - HysteresisGB)
            return previousTier;                        // drop not deep enough yet
        return rawTier;
    }

    static void ReduceWorst(List<Drive> all, out double worstFreePct, out Tier worstTier)
    {
        // ONE drive decides both the tier colour and the fill level, so the two
        // can never describe different drives. Worst tier wins; ties go to whichever
        // of those has the least free space.
        Drive worst = null;
        foreach (var d in all)
        {
            if (worst == null
                || d.Tier > worst.Tier
                || (d.Tier == worst.Tier && d.FreePct < worst.FreePct))
            {
                worst = d;
            }
        }
        worstFreePct = worst == null ? 100.0 : worst.FreePct;
        worstTier = worst == null ? Tier.Green : worst.Tier;
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
                    drv = new Drive { Letter = letter, Tier = Tier.Unknown };
                fresh.Add(drv);
            }
        }
        catch { }
        if (fresh.Count > 0) drives = fresh;

        foreach (var drv in drives)
        {
            ulong free, total, avail;
            if (!GetDiskFreeSpaceEx(drv.Letter + "\\", out avail, out total, out free)) continue;
            drv.Free = free;
            drv.Total = total;
            drv.FreePct = total > 0 ? free * 100.0 / total : 0.0;

            double freeGB = drv.FreeGB;
            double totalGB = drv.TotalGB;
            Tier newTier = ApplyHysteresis(RawTier(freeGB, totalGB), drv.Tier, freeGB, totalGB);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (drv.Tier == Tier.Unknown)
            {
                // first look: only shout if already critical
                if (newTier == Tier.Red) { Alert(drv, drv.Tier, newTier, false); drv.LastRedAlert = now; }
            }
            else if (newTier > drv.Tier)
            {
                Alert(drv, drv.Tier, newTier, false);   // got worse -> notify once per tier
                if (newTier == Tier.Red) drv.LastRedAlert = now;
            }
            else if (newTier == Tier.Red && drv.Tier == Tier.Red)
            {
                // still red: re-alert every RedRepeatMinutes
                if (now - drv.LastRedAlert >= RedRepeatMinutes * 60)
                {
                    Alert(drv, drv.Tier, newTier, true);
                    drv.LastRedAlert = now;
                }
            }
            if (newTier != Tier.Red) drv.LastRedAlert = 0;  // left red -> reset repeat cycle
            if (newTier < drv.Tier)
            {
                // improved: clear any lingering notification for this drive
                try { ToastNotificationManager.History.Remove(drv.Letter, ToastGroup, Aumid); } catch { }
            }
            drv.Tier = newTier;
        }

        double worst;
        Tier worstTier;
        ReduceWorst(drives, out worst, out worstTier);
        UpdateIcon(worst, worstTier);
        currentWorst = worst;
        currentWorstTier = worstTier;
        blinkTimer.Enabled = worstTier == Tier.Red;   // blink while critical

        if (firstRefresh)
        {
            // one-time startup summary so you can see what the app decided
            var sb = new StringBuilder("tiers: ");
            foreach (var d in drives)
                sb.AppendFormat("{0}={1:F1}G/{2:F1}% {3}  ", d.Letter, d.FreeGB, d.FreePct, d.Tier);
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

    static string AlertTitle(Tier tier)
    {
        return Info(tier).AlertTitle;
    }

    static Tier AlertTierAfterTransition(Tier previousTier, Tier newTier)
    {
        // never announce a drive as healthier than it is: on the first look
        // previousTier is Unknown, and a two-tier drop must read as the tier it
        // landed in, not the one it left
        return newTier > previousTier ? newTier : previousTier;
    }

    static void Alert(Drive d, Tier previousTier, Tier newTier, bool repeat)
    {
        Tier titleTier = AlertTierAfterTransition(previousTier, newTier);
        string title = AlertTitle(titleTier) + " - " + d.Letter;
        string text = string.Format("{0} {1:F1} GB free ({2:F1}%). Free up space before you can't save files!",
            d.Letter, d.FreeGB, d.FreePct);
        Log((repeat ? "REPEAT ALERT" : "ALERT") + " tier " + titleTier + " - " + d.Letter + " " + text);
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

    static Color TierColor(int rgb)
    {
        return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    // ---- ICON ----
    // Used space is painted in the tier colour at FULL strength, so the worst a
    // drive can be is a solid block of that colour. The previous design covered
    // the dot with a 75%-opaque dark wedge, which at 0.5% free left a near-black
    // blob: the colour - the whole point of the icon - was gone exactly when it
    // mattered. Painting the *free* part as a pale tint instead keeps the tier
    // colour on screen at every fill level and on both taskbar themes.
    const int    IconSize            = 16;
    const int    IconFrameWidth      = 2;                                 // dark border around the fill
    const int    IconInner           = IconSize - 2 * IconFrameWidth;     // 12px of fill
    const double FreeTint            = 0.45;                              // free band: tier colour mixed toward white
    const double BandMinimumFraction = 0.02;                              // a thinner used band is a hairline: skip it
    static readonly Color IconFrame  = Color.FromArgb(12, 12, 14);
    static readonly Color BlinkGrey  = Color.FromArgb(170, 170, 170);

    static double UsedFraction(double freePct)
    {
        double used = 1.0 - freePct / 100.0;
        if (used < 0.0) return 0.0;
        if (used > 1.0) return 1.0;
        return used;
    }

    static int UsedPixels(double freePct, int height)
    {
        // rows of used space in a square `height` rows tall
        double used = UsedFraction(freePct);
        if (used < BandMinimumFraction) return 0;    // hairline, not information
        int px = (int)(used * height + 0.5);
        return px > height ? height : px;
    }

    static Color Tint(Color c, double towardWhite)
    {
        return Color.FromArgb(
            (int)(c.R + (255 - c.R) * towardWhite),
            (int)(c.G + (255 - c.G) * towardWhite),
            (int)(c.B + (255 - c.B) * towardWhite));
    }

    static Bitmap RenderIcon(double worstFreePct, Tier worstTier, bool blink)
    {
        // pure: no NotifyIcon, no timer, so a test can read the pixels back
        Color c = blink ? BlinkGrey : TierColor(Info(worstTier).Rgb);
        var bmp = new Bitmap(IconSize, IconSize);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;   // crisp pixels: 16px leaves no room for blur

            using (var b = new SolidBrush(Tint(c, FreeTint)))
                g.FillRectangle(b, IconFrameWidth, IconFrameWidth, IconInner, IconInner);

            int used = UsedPixels(worstFreePct, IconInner);
            if (used > 0)
                using (var b = new SolidBrush(c))
                    g.FillRectangle(b, IconFrameWidth, IconFrameWidth + IconInner - used, IconInner, used);

            using (var p = new Pen(IconFrame))
            {
                g.DrawRectangle(p, 0, 0, IconSize - 1, IconSize - 1);
                g.DrawRectangle(p, 1, 1, IconSize - 3, IconSize - 3);
            }
        }
        return bmp;
    }

    static void UpdateIcon(double worstFreePct, Tier worstTier)
    {
        // Blink when critical: alternate with a grey square
        bool blink = worstTier == Tier.Red && ((Environment.TickCount / 500) % 2 == 0);

        using (var bmp = RenderIcon(worstFreePct, worstTier, blink))
        {
            IntPtr h = bmp.GetHicon();
            var icon = Icon.FromHandle(h);
            if (currentIcon != null) currentIcon.Dispose();
            if (currentHicon != IntPtr.Zero) DestroyIcon(currentHicon);
            currentIcon = icon;
            currentHicon = h;
            ni.Icon = icon;
        }
    }

    const int TooltipMaxLength = 63;    // NotifyIcon's limit

    static string BuildTooltipText(List<Drive> all)
    {
        // NotifyIcon caps the tooltip at 63 chars. Degrade in order - drop the
        // percent, then the decimal, then whole drives - so a drive that is named
        // always keeps its free-space figure, and nothing is cut mid-number.
        string text = JoinDrives(all, "{0}{1:F1}G({2:F1}%)");
        if (text.Length <= TooltipMaxLength) return text;

        text = JoinDrives(all, "{0}{1:F1}G");
        if (text.Length <= TooltipMaxLength) return text;

        text = JoinDrives(all, "{0}{1:F0}G");
        if (text.Length <= TooltipMaxLength) return text;

        var kept = new StringBuilder();
        foreach (var d in all)
        {
            string entry = string.Format("{0}{1:F0}G", d.Letter, d.FreeGB);
            int needed = (kept.Length == 0 ? 0 : 1) + entry.Length;
            if (kept.Length + needed > TooltipMaxLength) break;
            if (kept.Length > 0) kept.Append(" ");
            kept.Append(entry);
        }
        return kept.ToString();
    }

    static string JoinDrives(List<Drive> all, string format)
    {
        var sb = new StringBuilder();
        foreach (var d in all)
        {
            if (sb.Length > 0) sb.Append(" ");
            sb.AppendFormat(format, d.Letter, d.FreeGB, d.FreePct);
        }
        return sb.ToString();
    }

    static void UpdateTooltip()
    {
        ni.Text = "";                                // force refresh even if text is unchanged
        ni.Text = BuildTooltipText(drives);
    }
}
