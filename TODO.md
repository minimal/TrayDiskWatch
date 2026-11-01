# Remaining work from the two-axis review

Everything the Standards and Spec reviews turned up that is **not yet fixed**, with
enough detail to act on without re-reading the review. Current as of commit
`3ca6fed` (line numbers will drift — search by the quoted names instead).

## Where the code stands

| | |
|---|---|
| App | `TrayDiskWatch.cs`, 495 lines, single file, no dependencies |
| Tests | `TrayDiskWatch.Tests.cs`, 259 lines, framework-free, 53 checks, all green |
| Run tests | `test.bat` — exits 0, prints `PASS  53 checks` |
| Build app | `build.bat` (or the `csc` line in README, which is **wrong** — see item 11) |
| Compiler | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, **C# 5 only** |

Already done, do not redo:

- `db8d020` test suite + seams (`RawTier`, `ApplyHysteresis`, `ReduceWorst`,
  `AlertTitle`, `AlertTierAfterTransition`, `BuildTooltipText`, `UsedFraction`)
- `3ca6fed` `enum Tier` + one `TierTable` (boundary, colour, toast headline);
  `ReduceWorst` picks one winning drive so colour and wedge cannot disagree

### Constraints to respect while working here

- **C# 5.** No string interpolation, no `nameof`, no expression-bodied members, no
  null-conditional, no auto-property initialisers. This compiler is the .NET
  Framework 4.0 one and there is no Roslyn/dotnet on this machine.
- **The shipped app must stay one file** (README: "Single-file C# app, no
  dependencies"). Tests live in a second file that is only ever compiled by
  `test.bat` under `/define:TESTS`; the app's `Main` is wrapped in `#if !TESTS`.
- **Do not rebuild while the app is running** — a live `TrayDiskWatch.exe` from the
  tray holds the output file and `csc` fails with `CS0016 ... being used by another
  process`. Either exit the tray app, or build to a scratch name:
  `csc /out:TrayDiskWatch.verify.exe ... TrayDiskWatch.cs`
- Thresholds belong in the `// ---- CONFIG ----` block (README promises it); the
  `TierTable` is built from them and must not hard-code them.

---

## P1 — spec gaps a user would notice

### [ ] 1. Default drive whitelist contradicts "all fixed drives"

**Where** `TrayDiskWatch.cs:50` — `IncludeDrives = { "C:", "D:", "F:", "O:" }`
**Spec** README: *"A coloured dot reflects free space on all fixed drives (the worst drive wins)"*.
The comment on the same line says `// empty = all fixed drives`, but empty is not
what ships. Out of the box E:, G:, anything else is invisible, and nothing in the
UI says so.

**Fix** Pick one and be explicit: default to `new string[0]` (all fixed drives, as
the README claims) and let the whitelist be opt-in; or keep the whitelist and
change the README to say the defaults are C:, D:, F:, O:. Whichever, make the
tooltip or menu surface which drives are being ignored.

**Test** `BuildTooltipText` already takes a drive list; a test over an empty
whitelist belongs in `TrayDiskWatch.Tests.cs`.

### [ ] 2. `build.bat` pins one Windows SDK version and fails everywhere else

**Where** `build.bat:7` —
`/r:"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd"`
and `build.bat:2` pins `...\Framework64\v4.0.30319\csc.exe`. Neither is checked.

**Spec** README: *"Requires the Windows SDK for toast support"* — one version, not one specific build.

**Fix** Glob the newest `UnionMetadata\10.*` directory (`for /d %%i in (...)` sorted
descending), and fail with a readable message naming what is missing rather than a
raw `CS0006`. Same fix is needed in `test.bat:13` (the refs are duplicated — see item 13).

### [ ] 3. `AlertTierAfterTransition` can announce a *better* tier than reality

**Where** `TrayDiskWatch.cs:360` — `return newTier > previousTier ? newTier : previousTier;`

Safe today only because `Alert` is called exclusively on worsening, first-look and
repeat paths (`TrayDiskWatch.cs:290-313`). If anyone ever calls `Alert` on an
improvement, the toast will describe the drive as worse than it is. Either assert
the precondition in a comment, or drop the `max` and pass the entered tier
explicitly.

---

## P2 — standards / smell work

### [ ] 4. `Info()` is a mysterious name (introduced by the refactor)

**Where** `TrayDiskWatch.cs:196` — `static TierInfo Info(Tier tier)`

`Info(...)` says nothing. Rename to `InfoFor` / `TierInfoFor` / `Table(Tier)`.
Called from `TierBoundaryGB`, `RawTier`, `AlertTitle`, `UpdateIcon`.

### [ ] 5. Threshold constants are named for the tier you *leave*

**Where** `TrayDiskWatch.cs:42-47`

```csharp
static readonly double WarnYellow = 10.0;  // >= 10% free = green
```

`WarnYellow` is the boundary you must clear to *be* green — it names the wrong
tier. The `TierTable` at `:85-91` now reads `Tier.Green, WarnYellow, WarnGreenGB`,
which is exactly the confusion. Every name needs the comment beside it to be
readable.

**Fix** Rename by what they gate: `GreenMinPercent`, `GreenMinGB`,
`YellowMinPercent`, `YellowMinGB`, `OrangeMinPercent`, `OrangeMinGB`. Update the
file header comment at `TrayDiskWatch.cs:5-7` too, since it names them.

**Risk** Low: only the config block, the table and the header comment.

### [ ] 6. Three bare `catch { }` blocks swallow real failures

**Where**

| line | what is silenced |
|---|---|
| `121` | every `Log()` write (fine — logging must never throw) |
| `274` | `DriveInfo.GetDrives()` enumeration *and* the whitelist filter |
| `313` | `ToastNotificationManager.History.Remove` |

Line 121 is defensible. Lines 274 and 313 are not: a failed enumeration leaves the
previous `drives` list in place and the tray silently keeps showing stale numbers;
a failed toast-cleanup leaves a stale notification in the Action Center with no
trace.

**Fix** `catch (Exception ex) { Log("drive enum failed: " + ex.Message); }` and the
same for the toast cleanup. Keep them non-fatal, just visible.

### [ ] 7. `GetDiskFreeSpaceEx` result is half-discarded

**Where** `TrayDiskWatch.cs:279-280`

```csharp
ulong free, total, avail;
if (!GetDiskFreeSpaceEx(drv.Letter + "\\", out avail, out total, out free)) continue;
```

`avail` is the free space **available to this user** (quota-aware); `free` is total
free bytes. `avail` is captured and never read — and it is what Windows Explorer
shows. Either use it (and document which figure the dot reflects) or drop the
parameter into a discard. Also note the `continue` leaves that drive's previous
`Free`/`FreePct` in place, so it keeps contributing stale numbers to the icon.

### [ ] 8. `(freeGB, totalGB)` still travel together everywhere (Data Clumps)

**Where** `TierBoundaryGB` (:203), `RawTier` (:208), `ApplyHysteresis` (:216)

Three functions pass the same pair. A small `DriveSpace`/`Capacity` struct owning
`FreeGB`/`TotalGB` (and the boundary maths) would collapse them and give
`BoundaryGB` a home. Judgement call — the current form is readable, so only worth
it if these grow.

### [ ] 9. `Alert` and `BuildTooltipText` only ever read `Drive` fields (Feature Envy)

**Where** `TrayDiskWatch.cs:368`, `:453`

Both reach into `d.Letter`, `d.FreeGB`, `d.FreePct` and nothing else. `Drive` has
already gained `FreeGB`/`TotalGB`; the natural next step is
`Drive.TooltipEntry()` / `Drive.AlertText()`, leaving the functions to join and
format. Low value while there are only two call sites.

### [ ] 10. `Divergent Change` — one file is now config, tier domain, P/Invoke, model, GDI drawing, toasts and scheduling

**Where** all 495 lines of `TrayDiskWatch.cs`

Still suppressed by the documented "single-file" rule, which is why the refactor
stopped at data, not files. If it keeps growing, the honest move is to split into
`Tier.cs` / `Drive.cs` / `Tray.cs` **and update the README line that says
single-file** — do not quietly contradict a documented standard.

---

## P3 — docs and tooling

### [ ] 11. README's `csc` one-liner cannot compile

**Where** README.md, Build section:

```
csc /nologo /target:winexe /win32icon:icon.ico /out:TrayDiskWatch.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll TrayDiskWatch.cs
```

Missing `/r:System.Runtime.WindowsRuntime.dll`,
`/r:System.Runtime.InteropServices.WindowsRuntime.dll` and the `Windows.winmd`
reference, which `Windows.Data.Xml.Dom` and `Windows.UI.Notifications` need. Copy
the reference list from `build.bat` (which is correct).

### [ ] 12. The file header comment has drifted from reality

**Where** `TrayDiskWatch.cs:1-23`

- `:18` says *"A balloon notification fires"* — toasts are primary, the balloon is
  a fallback (`:389-398`).
- `:21-23` gives the same broken `csc` line as README (no `/win32icon`, no winmd).
- `:5-7` and `:16` use the confusing `Warn*` names (see item 5).
- It duplicates the tier table that now lives in `TierTable` — two copies to drift.

**Fix** Point at the config block and the table instead of restating thresholds;
fix the compile line to match `build.bat`.

### [ ] 13. `build.bat` and `test.bat` duplicate the reference list

Two copies of seven `/r:` arguments and the SDK path. A new SDK, a new reference or
a moved `Windows.winmd` has to be changed in both (this is the Shotgun Surgery the
review flagged). Extract to one `refs.bat` or one `set REFS=...` both `call`.

### [ ] 14. No CI

No `.github/`. `test.bat` is already a clean entry point with a proper exit code,
so a `windows-latest` job running it is cheap — but it will need item 2's SDK
discovery first, because `10.0.26100.0` will not exist on every runner.

### [ ] 15. Untracked agent-tooling directories

`git status` shows `.pi/` and `.unipi/` (both contain only tool settings). Decide:
add to `.gitignore`, or track them deliberately. They are currently noise that
invites an accidental `git add`.

---

## Test coverage — what is *not* covered

`test.bat` covers the pure logic only. Untested, by design rather than by accident:

- [ ] **`DoRefresh`'s alert state machine** — the "once per crossing, then repeat
  every `RedRepeatMinutes`" behaviour (`:290-313`) depends on `drv.Tier` and
  `drv.LastRedAlert` surviving across refreshes. Only the *wording* decision is
  tested (`AlertTierAfterTransition`), not the timing. Testable by extracting a
  pure `ShouldAlert(previousTier, newTier, lastRedAlert, nowSeconds)`.
- [ ] **`UpdateIcon` rendering** — colour choice and pie geometry; only
  `UsedFraction` is tested. Testable as a pure `WedgeAngleDegrees(freePct)`.
- [ ] **Drive enumeration and whitelist filtering** (`:253-276`) — needs a seam
  taking `DriveInfo[]`.
- [ ] **`RegisterAumid`, the single-instance mutex, the context menu** — integration
  territory, needs a desktop session.
- [ ] **Tooltip against the real `NotifyIcon`** — `TooltipMaxLength = 63` is
  enforced by our own code now, but nothing verifies WinForms accepts the string.

### How to add a test

In `TrayDiskWatch.Tests.cs`: add a `static void Something()` method, call it from
`Main`, and use the existing helpers — `Ok(name, bool)`, `OkClose(name, actual,
expected)`, `OkEqual(name, int, int)`, `OkText(name, actual, expected)`, and
`MakeDrive(letter, freeGB, totalGB, Tier.Green)` to build fixtures. Everything is in
`partial class Program`, so private members are directly reachable — no visibility
changes needed, and none should be made just for tests.

---

## Deliberate decisions — do not "fix" these

| Behaviour | Why it is intentional |
|---|---|
| Wedge skipped below 2% used (`WedgeMinimumFraction`, `:405`) | A 0.5% wedge is a hairline artefact, not information. Characterised by a test so a change is a decision, not an accident. |
| Tooltip degrades percent → decimal → drives (`:453-477`) | The 63-char `NotifyIcon` cap is real. Dropping whole drives silently (the old `Substring`) was the bug; showing fewer digits is not. |
| `HysteresisGB = 0.5` holds a tier change until the margin clears | Prevents flap/re-alert storms near a boundary. Direction-specific (improvement vs deterioration) and both directions are tested. |
| Alert wording takes the worse of old and new tier | Guarantees a drop into red always says CRITICAL, including a two-tier jump and the first look. |
| `Drive` objects are preserved across refreshes | `drv.Tier` / `drv.LastRedAlert` carry the alert state, so alerts fire once per crossing rather than every 30 s tick. |
| Tier thresholds live in the `// ---- CONFIG ----` block | README documents that block as the place to tune; `TierTable` is derived from it. |
