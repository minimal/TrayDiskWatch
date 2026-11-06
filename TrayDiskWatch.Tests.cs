// TrayDiskWatch.Tests.cs - tests for TrayDiskWatch's decision logic.
//
// Not part of the app build. test.bat compiles this together with
// TrayDiskWatch.cs under /define:TESTS, which drops the tray entry point and
// keeps the runner below instead. No test framework and no packages, so the
// "single-file C# app, no dependencies" rule still holds for the shipped exe.
//
//   test.bat        builds and runs the suite, exits non-zero on failure
//
// Everything under test here is pure (no WinForms, no WinRT, no disks): the
// tier boundaries, the hysteresis, which drive the icon describes, the toast
// wording, the tooltip layout, and the pixels RenderIcon paints. That is also
// why those pieces are split out of DoRefresh/UpdateIcon/UpdateTooltip into
// named functions.

#if TESTS
using System;
using System.Collections.Generic;
using System.Drawing;

static partial class Program
{
    const double GB = 1073741824.0;

    static int checks;
    static int failures;

    static int Main()
    {
        Console.WriteLine("TrayDiskWatch tests");

        TierBoundaries();
        TierTableMatchesReadme();
        RawTierSelection();
        HysteresisHoldsImprovement();
        HysteresisHoldsDeterioration();
        HysteresisSkipsFirstLook();
        WorstDriveDrivesColourAndFill();
        AlertWordsTheTierBeingEntered();
        TooltipKeepsEveryDrive();
        UsedFractionClamps();
        UsedPixelsTracksFill();
        IconMatchesTraySize();
        IconKeepsTierColourWhenNearlyFull();
        IconBlinkFrameIsGrey();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "PASS  " + checks + " checks"
            : "FAIL  " + failures + " of " + checks + " checks");
        return failures == 0 ? 0 : 1;
    }

    // ---------------- tests ----------------

    static void TierBoundaries()
    {
        Console.WriteLine();
        Console.WriteLine("tier boundaries: the smaller of percent-of-drive and GB floor");

        OkClose("100 GB, green boundary is 10% = 10 GB", TierBoundaryGB(100.0, Tier.Green), 10.0);
        OkClose("100 GB, yellow boundary is 5% = 5 GB", TierBoundaryGB(100.0, Tier.Yellow), 5.0);
        OkClose("100 GB, orange boundary is 2% = 2 GB", TierBoundaryGB(100.0, Tier.Orange), 2.0);
        OkClose("2 TB, green floor caps the percent at 10 GB", TierBoundaryGB(2048.0, Tier.Green), 10.0);
        OkClose("2 TB, orange floor caps the percent at 2 GB", TierBoundaryGB(2048.0, Tier.Orange), 2.0);
        OkClose("20 GB, green boundary is percent-bound at 2 GB", TierBoundaryGB(20.0, Tier.Green), 2.0);
        OkClose("20 GB, orange boundary is percent-bound at 0.4 GB", TierBoundaryGB(20.0, Tier.Orange), 0.4);
    }

    static void TierTableMatchesReadme()
    {
        Console.WriteLine();
        Console.WriteLine("the tier table still matches the README");

        Ok("four tiers, green first and red last",
           TierTable.Length == 4
           && TierTable[0].Tier == Tier.Green
           && TierTable[3].Tier == Tier.Red);

        OkClose("green wants 10% free", TierTable[0].MinPercentFree, 10.0);
        OkClose("green floor is 10 GB", TierTable[0].MinGBFree, 10.0);
        OkClose("yellow wants 5% free", TierTable[1].MinPercentFree, 5.0);
        OkClose("yellow floor is 5 GB", TierTable[1].MinGBFree, 5.0);
        OkClose("orange wants 2% free", TierTable[2].MinPercentFree, 2.0);
        OkClose("orange floor is 2 GB", TierTable[2].MinGBFree, 2.0);

        Ok("every tier has its own colour",
           TierTable[0].Rgb != TierTable[1].Rgb
           && TierTable[1].Rgb != TierTable[2].Rgb
           && TierTable[2].Rgb != TierTable[3].Rgb);
        Ok("only red is critical", Info(Tier.Red).AlertTitle.Contains("CRITICAL"));
        Ok("orange warns", Info(Tier.Orange).AlertTitle.Contains("Warning"));
        Ok("an unknown tier falls back to red, not green",
           Info(Tier.Unknown).Tier == Tier.Red);
    }

    static void RawTierSelection()
    {
        Console.WriteLine();
        Console.WriteLine("tier selection from free space");

        OkEqual("100 GB with 15 GB free is green", (int)RawTier(15.0, 100.0), 0);
        OkEqual("100 GB with 7 GB free is yellow", (int)RawTier(7.0, 100.0), 1);
        OkEqual("100 GB with 3 GB free is orange", (int)RawTier(3.0, 100.0), 2);
        OkEqual("100 GB with 1 GB free is red", (int)RawTier(1.0, 100.0), 3);
        OkEqual("100 GB with exactly 10 GB free is green, boundary inclusive",
                (int)RawTier(10.0, 100.0), 0);
        OkEqual("2 TB with 5 GB free is yellow, not red", (int)RawTier(5.0, 2048.0), 1);
        OkEqual("2 TB with 11 GB free is green, the GB floor saves it",
                (int)RawTier(11.0, 2048.0), 0);
        OkEqual("20 GB with 2.5 GB free is green, percent binds on small drives",
                (int)RawTier(2.5, 20.0), 0);
    }

    static void HysteresisHoldsImprovement()
    {
        Console.WriteLine();
        Console.WriteLine("hysteresis holds an improvement until the margin is cleared");

        OkEqual("red at 2.3 GB free, only 0.3 over the orange boundary, stays red",
                (int)ApplyHysteresis(RawTier(2.3, 100.0), Tier.Red, 2.3, 100.0), 3);
        OkEqual("red at 2.6 GB free has cleared the margin, so it is orange",
                (int)ApplyHysteresis(RawTier(2.6, 100.0), Tier.Red, 2.6, 100.0), 2);
    }

    static void HysteresisHoldsDeterioration()
    {
        Console.WriteLine();
        Console.WriteLine("hysteresis holds a deterioration until the margin is crossed");

        OkEqual("yellow at 4.6 GB free, only 0.4 under the boundary, stays yellow",
                (int)ApplyHysteresis(RawTier(4.6, 100.0), Tier.Yellow, 4.6, 100.0), 1);
        OkEqual("yellow at 4.4 GB free has crossed the margin, so it is orange",
                (int)ApplyHysteresis(RawTier(4.4, 100.0), Tier.Yellow, 4.4, 100.0), 2);
    }

    static void HysteresisSkipsFirstLook()
    {
        Console.WriteLine();
        Console.WriteLine("hysteresis does not hold anything on the first look");

        OkEqual("unknown previous tier passes the raw tier straight through",
                (int)ApplyHysteresis(RawTier(3.0, 100.0), Tier.Unknown, 3.0, 100.0), 2);
        OkEqual("unknown previous tier passes red straight through",
                (int)ApplyHysteresis(RawTier(1.0, 100.0), Tier.Unknown, 1.0, 100.0), 3);
    }

    static void WorstDriveDrivesColourAndFill()
    {
        Console.WriteLine();
        Console.WriteLine("the icon's colour and its fill must describe the same drive");

        // The big drive is rescued by the GB floor (11 GB free reads green) but
        // has the lowest free PERCENT. The small drive is orange. So the lowest
        // percent and the worst tier belong to different drives.
        Drive small = MakeDrive("C:", 0.5, 20.0, RawTier(0.5, 20.0));
        Drive big = MakeDrive("D:", 11.0, 2048.0, RawTier(11.0, 2048.0));

        OkEqual("the big drive is green via the GB floor", (int)big.Tier, 0);
        OkEqual("the small drive is orange", (int)small.Tier, 2);

        double worstPct;
        Tier worstTier;
        ReduceWorst(new List<Drive> { big, small }, out worstPct, out worstTier);

        OkEqual("the icon takes the colour of the orange drive", (int)worstTier, 2);
        OkClose("the fill takes the used portion of that same drive", worstPct, small.FreePct);
    }

    static void AlertWordsTheTierBeingEntered()
    {
        Console.WriteLine();
        Console.WriteLine("the toast describes the tier being entered");

        OkText("red is critical", AlertTitle(Tier.Red), "CRITICAL: disk space");
        OkText("orange is a warning", AlertTitle(Tier.Orange), "Warning: disk space");
        OkEqual("orange to red is worded for red",
                (int)AlertTierAfterTransition(Tier.Orange, Tier.Red), 3);
        OkEqual("green to orange is worded for orange",
                (int)AlertTierAfterTransition(Tier.Green, Tier.Orange), 2);
        OkEqual("a first look that finds red is worded for red",
                (int)AlertTierAfterTransition(Tier.Unknown, Tier.Red), 3);
    }

    static void TooltipKeepsEveryDrive()
    {
        Console.WriteLine();
        Console.WriteLine("the tooltip reports every drive inside the 63-char limit");

        string one = BuildTooltipText(new List<Drive> { MakeDrive("C:", 123.4, 1000.0, Tier.Green) });
        Ok("a lone drive shows free space and percent", one.Contains("(12.3%)"));

        var four = new List<Drive>
        {
            MakeDrive("C:", 1234.5, 9999.0, Tier.Green),
            MakeDrive("D:", 1234.5, 9999.0, Tier.Green),
            MakeDrive("F:", 1234.5, 9999.0, Tier.Green),
            MakeDrive("O:", 1234.5, 9999.0, Tier.Green),
        };
        string text = BuildTooltipText(four);

        Ok("four drives stay inside the 63-char limit", text.Length <= TooltipMaxLength);
        foreach (var d in four) Ok("the tooltip mentions " + d.Letter, text.Contains(d.Letter));
        Ok("no drive is cut off mid-entry", text.EndsWith(")") || text.EndsWith("G"));

        // More drives than can ever fit: dropping detail is allowed, silently
        // mismatching the limit is not.
        var many = new List<Drive>();
        for (int i = 0; i < 12; i++)
            many.Add(MakeDrive(((char)('C' + i)).ToString() + ":", 1234.5, 9999.0, Tier.Green));
        Ok("even twelve drives stay inside the 63-char limit",
           BuildTooltipText(many).Length <= TooltipMaxLength);
    }

    static void UsedFractionClamps()
    {
        Console.WriteLine();
        Console.WriteLine("the used fraction that drives the filled band");

        OkClose("40% free is 60% used", UsedFraction(40.0), 0.6);
        OkClose("150% free clamps to 0% used", UsedFraction(150.0), 0.0);
        OkClose("negative free clamps to 100% used", UsedFraction(-25.0), 1.0);
        Ok("a nearly-empty drive draws no filled band, deliberate so it is not a hairline",
           UsedFraction(99.9) < BandMinimumFraction);
    }

    static void UsedPixelsTracksFill()
    {
        Console.WriteLine();
        Console.WriteLine("the filled rows of the square track used space");

        OkEqual("100% free fills no rows", UsedPixels(100.0, IconInner), 0);
        OkEqual("50% free fills half the square", UsedPixels(50.0, IconInner), IconInner / 2);
        OkEqual("0% free fills every row", UsedPixels(0.0, IconInner), IconInner);
        OkEqual("0.5% free still fills every row - the shape of the user's C:",
                UsedPixels(0.5, IconInner), IconInner);
        OkEqual("99.9% free draws no band, so no hairline", UsedPixels(99.9, IconInner), 0);
    }

    static void IconMatchesTraySize()
    {
        Console.WriteLine();
        Console.WriteLine("the icon is drawn at the size the tray asks for");

        // Windows 11's notification area draws 24px icons at 100% scaling. A 16px
        // bitmap gets upscaled 1.5x, which smears the frame and the fill edge -
        // that is why the icon looked soft in the tray.
        OkEqual("the icon is 24px square", IconSize, 24);
        OkEqual("the frame scales with it", IconFrameWidth, 2);

        using (var bmp = RenderIcon(50.0, Tier.Green, false))
        {
            OkEqual("the canvas matches IconSize", bmp.Width, IconSize);
            OkText("the corner is the dark frame", HexAt(bmp, 0, 0), Hex(IconFrame));
            OkText("the first inner row is the free band",
                   HexAt(bmp, IconSize / 2, IconFrameWidth),
                   Hex(Tint(TierColor(Info(Tier.Green).Rgb), FreeTint)));
        }
    }

    static void IconKeepsTierColourWhenNearlyFull()
    {
        Console.WriteLine();
        Console.WriteLine("the icon stays in its tier colour however full the drive is");

        // Regression: the old dark-wedge design left a near-black blob at high
        // fill, so a critical drive was the least legible state of all.
        int[] fills = { 0, 1, 5, 20 };
        foreach (int pct in fills)
        {
            using (var bmp = RenderIcon(pct, Tier.Red, false))
                OkText(pct + "% free: the used area is the tier colour",
                       HexAt(bmp, IconSize / 2, UsedRow()), Hex(TierColor(Info(Tier.Red).Rgb)));
        }

        foreach (var info in TierTable)
        {
            using (var bmp = RenderIcon(0.5, info.Tier, false))
                OkText("a nearly full " + info.Tier + " drive stays " + info.Tier,
                       HexAt(bmp, IconSize / 2, UsedRow()), Hex(TierColor(info.Rgb)));
        }

        // with room to spare the square shows both bands, pale free above full-strength used
        using (var roomy = RenderIcon(20.0, Tier.Red, false))
        {
            OkText("20% free paints the free band as a pale tint",
                   HexAt(roomy, IconSize / 2, IconFrameWidth),
                   Hex(Tint(TierColor(Info(Tier.Red).Rgb), FreeTint)));
            Ok("the free band and the used band differ",
               HexAt(roomy, IconSize / 2, IconFrameWidth) != HexAt(roomy, IconSize / 2, UsedRow()));
        }
    }

    static void IconBlinkFrameIsGrey()
    {
        Console.WriteLine();
        Console.WriteLine("the blink frame alternates to grey");

        using (var bmp = RenderIcon(0.5, Tier.Red, true))
            OkText("the blinking used area is grey", HexAt(bmp, IconSize / 2, UsedRow()), Hex(BlinkGrey));
        using (var bmp = RenderIcon(0.5, Tier.Red, false))
            Ok("the same icon is red when not blinking",
               HexAt(bmp, IconSize / 2, UsedRow()) != Hex(BlinkGrey));
    }

    // ---------------- harness ----------------

    static int UsedRow()
    {
        // last row of fill: inside the square, whatever the icon size is
        return IconSize - IconFrameWidth - 1;
    }

    static string Hex(Color c)
    {
        return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
    }

    static string HexAt(Bitmap bmp, int x, int y)
    {
        return Hex(bmp.GetPixel(x, y));
    }

    static Drive MakeDrive(string letter, double freeGB, double totalGB, Tier tier)
    {
        var d = new Drive();
        d.Letter = letter;
        d.Total = (ulong)(totalGB * GB);
        d.Free = (ulong)(freeGB * GB);
        d.FreePct = d.Total > 0 ? d.Free * 100.0 / d.Total : 0.0;
        d.Tier = tier;
        return d;
    }

    static void Ok(string name, bool condition)
    {
        checks++;
        if (condition) { Console.WriteLine("  ok    " + name); return; }
        failures++;
        Console.WriteLine("  FAIL  " + name);
    }

    static void OkClose(string name, double actual, double expected)
    {
        Ok(name + " [expected " + expected.ToString("F3") + ", got " + actual.ToString("F3") + "]",
           Math.Abs(actual - expected) < 0.0001);
    }

    static void OkEqual(string name, int actual, int expected)
    {
        Ok(name + " [expected " + expected + ", got " + actual + "]", actual == expected);
    }

    static void OkText(string name, string actual, string expected)
    {
        Ok(name + " [expected \"" + expected + "\", got \"" + actual + "\"]",
           actual == expected);
    }
}
#endif
