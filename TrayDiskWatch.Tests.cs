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
// wording, and the tooltip layout. That is also why those pieces are split out
// of DoRefresh/UpdateIcon/UpdateTooltip into named functions.

#if TESTS
using System;
using System.Collections.Generic;

static partial class Program
{
    const double GB = 1073741824.0;

    static int checks;
    static int failures;

    static int Main()
    {
        Console.WriteLine("TrayDiskWatch tests");

        TierBoundaries();
        RawTierSelection();
        HysteresisHoldsImprovement();
        HysteresisHoldsDeterioration();
        HysteresisSkipsFirstLook();
        WorstDriveDrivesColourAndWedge();
        AlertWordsTheTierBeingEntered();
        TooltipKeepsEveryDrive();
        UsedFractionClamps();

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

        OkClose("100 GB, green boundary is 10% = 10 GB", TierBoundaryGB(100.0, 0), 10.0);
        OkClose("100 GB, yellow boundary is 5% = 5 GB", TierBoundaryGB(100.0, 1), 5.0);
        OkClose("100 GB, orange boundary is 2% = 2 GB", TierBoundaryGB(100.0, 2), 2.0);
        OkClose("2 TB, green floor caps the percent at 10 GB", TierBoundaryGB(2048.0, 0), 10.0);
        OkClose("2 TB, orange floor caps the percent at 2 GB", TierBoundaryGB(2048.0, 2), 2.0);
        OkClose("20 GB, green boundary is percent-bound at 2 GB", TierBoundaryGB(20.0, 0), 2.0);
        OkClose("20 GB, orange boundary is percent-bound at 0.4 GB", TierBoundaryGB(20.0, 2), 0.4);
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
                (int)ApplyHysteresis(RawTier(2.3, 100.0), 3, 2.3, 100.0), 3);
        OkEqual("red at 2.6 GB free has cleared the margin, so it is orange",
                (int)ApplyHysteresis(RawTier(2.6, 100.0), 3, 2.6, 100.0), 2);
    }

    static void HysteresisHoldsDeterioration()
    {
        Console.WriteLine();
        Console.WriteLine("hysteresis holds a deterioration until the margin is crossed");

        OkEqual("yellow at 4.6 GB free, only 0.4 under the boundary, stays yellow",
                (int)ApplyHysteresis(RawTier(4.6, 100.0), 1, 4.6, 100.0), 1);
        OkEqual("yellow at 4.4 GB free has crossed the margin, so it is orange",
                (int)ApplyHysteresis(RawTier(4.4, 100.0), 1, 4.4, 100.0), 2);
    }

    static void HysteresisSkipsFirstLook()
    {
        Console.WriteLine();
        Console.WriteLine("hysteresis does not hold anything on the first look");

        OkEqual("unknown previous tier passes the raw tier straight through",
                (int)ApplyHysteresis(2, -1, 3.0, 100.0), 2);
        OkEqual("unknown previous tier passes red straight through",
                (int)ApplyHysteresis(3, -1, 1.0, 100.0), 3);
    }

    static void WorstDriveDrivesColourAndWedge()
    {
        Console.WriteLine();
        Console.WriteLine("the icon's colour and its wedge must describe the same drive");

        // The big drive is rescued by the GB floor (11 GB free reads green) but
        // has the lowest free PERCENT. The small drive is orange. So the lowest
        // percent and the worst tier belong to different drives.
        Drive small = MakeDrive("C:", 0.5, 20.0, RawTier(0.5, 20.0));
        Drive big = MakeDrive("D:", 11.0, 2048.0, RawTier(11.0, 2048.0));

        OkEqual("the big drive is green via the GB floor", (int)big.Tier, 0);
        OkEqual("the small drive is orange", (int)small.Tier, 2);

        double worstPct;
        int worstTier;
        ReduceWorst(new List<Drive> { big, small }, out worstPct, out worstTier);

        OkEqual("the dot takes the colour of the orange drive", worstTier, 2);
        OkClose("the wedge takes the used portion of that same drive", worstPct, small.FreePct);
    }

    static void AlertWordsTheTierBeingEntered()
    {
        Console.WriteLine();
        Console.WriteLine("the toast describes the tier being entered");

        OkText("red is critical", AlertTitle(3), "CRITICAL: disk space");
        OkText("orange is a warning", AlertTitle(2), "Warning: disk space");
        OkEqual("orange to red is worded for red",
                (int)AlertTierAfterTransition(2, 3), 3);
        OkEqual("green to orange is worded for orange",
                (int)AlertTierAfterTransition(0, 2), 2);
        OkEqual("a first look that finds red is worded for red",
                (int)AlertTierAfterTransition(-1, 3), 3);
    }

    static void TooltipKeepsEveryDrive()
    {
        Console.WriteLine();
        Console.WriteLine("the tooltip reports every drive inside the 63-char limit");

        string one = BuildTooltipText(new List<Drive> { MakeDrive("C:", 123.4, 1000.0, 0) });
        Ok("a lone drive shows free space and percent", one.Contains("(12.3%)"));

        var four = new List<Drive>
        {
            MakeDrive("C:", 1234.5, 9999.0, 0),
            MakeDrive("D:", 1234.5, 9999.0, 0),
            MakeDrive("F:", 1234.5, 9999.0, 0),
            MakeDrive("O:", 1234.5, 9999.0, 0),
        };
        string text = BuildTooltipText(four);

        Ok("four drives stay inside the 63-char limit", text.Length <= TooltipMaxLength);
        foreach (var d in four) Ok("the tooltip mentions " + d.Letter, text.Contains(d.Letter));
        Ok("no drive is cut off mid-entry", text.EndsWith(")") || text.EndsWith("G"));

        // More drives than can ever fit: dropping detail is allowed, silently
        // mismatching the limit is not.
        var many = new List<Drive>();
        for (int i = 0; i < 12; i++)
            many.Add(MakeDrive(((char)('C' + i)).ToString() + ":", 1234.5, 9999.0, 0));
        Ok("even twelve drives stay inside the 63-char limit",
           BuildTooltipText(many).Length <= TooltipMaxLength);
    }

    static void UsedFractionClamps()
    {
        Console.WriteLine();
        Console.WriteLine("the used fraction that drives the wedge");

        OkClose("40% free is 60% used", UsedFraction(40.0), 0.6);
        OkClose("150% free clamps to 0% used", UsedFraction(150.0), 0.0);
        OkClose("negative free clamps to 100% used", UsedFraction(-25.0), 1.0);
        Ok("a nearly-empty drive draws no wedge, deliberate so it is not a hairline",
           UsedFraction(99.9) < WedgeMinimumFraction);
    }

    // ---------------- harness ----------------

    static Drive MakeDrive(string letter, double freeGB, double totalGB, int tier)
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
