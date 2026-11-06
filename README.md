# TrayDiskWatch

A system tray icon for Windows that shows disk space at a glance. A coloured square reflects free space on all fixed drives (the worst drive wins): used space fills the square from the bottom in the tier colour, free space is the same colour tinted pale, so a nearly full drive stays a solid block of its tier colour instead of fading to black. Hover for per-drive free space (the percent is dropped before any drive is, if the tooltip would overflow); a toast notification fires when a drive drops into a worse tier.

## Tiers

| Color  | Condition                                  |
|--------|--------------------------------------------|
| Green  | >= 10% free (or >= 10 GB on big drives)    |
| Yellow | >= 5% free (or >= 5 GB on big drives)      |
| Orange | >= 2% free (or >= 2 GB on big drives)      |
| Red    | below that (blinks, repeats alert)         |

Each boundary uses the smaller of the two requirements (percent vs. GB floor), so a 2 TB drive with 5 GB free reads yellow, not red. Tier changes need a 0.5 GB hysteresis margin to prevent flapping.

## Build

Single-file C# app, no dependencies. Edit the config block at the top of `TrayDiskWatch.cs` (refresh interval, thresholds, drive whitelist), then run `build.bat`, or:

```
csc /nologo /target:winexe /win32icon:icon.ico /out:TrayDiskWatch.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll TrayDiskWatch.cs
```

Requires the Windows SDK for toast support (`Windows.winmd`, referenced in `build.bat`).

## Test

```
test.bat
```

Compiles `TrayDiskWatch.cs` together with `TrayDiskWatch.Tests.cs` under `/define:TESTS` (which swaps the tray entry point for the test runner), runs the suite, and exits non-zero if any check fails. Plain `csc`, no test framework: the checks cover the tier boundaries, the hysteresis margin, which drive the icon describes, the alert wording, and the tooltip layout. The shipped build is unaffected — `TrayDiskWatch.cs` on its own is still the whole program.

## Run

Launch `TrayDiskWatch.exe`. It stays in the tray; right-click for menu (refresh, open C:, storage settings, exit). Logs to `TrayDiskWatch.log` next to the exe.

## License

MIT. See [LICENSE](LICENSE).
