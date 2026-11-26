# SpinnyDisc — build & test notes

In-house SSD reliability triage. Probes drives for the failures SMART can't see:
command hangs, multi-second stalls, I/O errors, silent corruption. Resets the
drive and carries on; emits JSONL + HTML reports.

## Layout
- `src/SpinnyDisc.Core` — engine: enumeration, overlapped raw I/O w/ per-command
  timeout, bus-aware reset (USB port reset / NVMe protocol reset / ATA device reset /
  SetupAPI devnode restart), test phases, reporting.
- `src/SpinnyDisc` — Avalonia GUI (MVVM, CommunityToolkit).
- `src/SpinnyDisc.Cli` — headless runner (`list`, `test <n>`, `pnp` diagnostic).
- `tests/SpinnyDisc.Core.Tests` — xunit; struct-layout asserts catch interop drift.

## Build / test / publish
```bash
dotnet build
dotnet test tests/SpinnyDisc.Core.Tests
dotnet publish src/SpinnyDisc     -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
dotnet publish src/SpinnyDisc.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/cli-win-x64
```
Both exes have `requireAdministrator` manifests — raw `\\.\PhysicalDriveN` needs it.

## Elevated dev loop (this machine)
Devin's shell is NOT elevated. `.elev/daemon.ps1` runs as a persistent elevated
job runner: drop `.elev/job.ps1` → daemon executes it → writes `.elev/job.done`.
Started via `Start-Process -Verb RunAs` (one UAC click per session). Kill it by
creating `.elev/stop`. `elevated-run.cmd` is a one-shot equivalent.
Output redirected by PowerShell `*>` is UTF-16 — read with `iconv -f UTF-16LE`.

## Interop gotchas hit so far
- WoW64 blocks most storage IOCTLs → PlatformTarget=x64 everywhere (Directory.Build.props).
- `SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize` = 8 on x64 (not 6!).
- PDO names aren't always `\Device\HarddiskN` — map interface→disk via
  `IOCTL_STORAGE_GET_DEVICE_NUMBER` on the interface path.
- `ATA_PASS_THROUGH_EX` = 48 bytes on x64 (nuint alignment), not 44.
- `STORAGE_PROTOCOL_COMMAND` = 96 bytes (18 DWORDs + ProtocolSpecific).
- CsWin32 generates mixed friendly/raw overloads — check compile errors for which
  one resolved (SafeHandle vs HDEVINFO, ref vs pointer).
- RTL9210 USB bridge (VID_0BDA&PID_9210): presents disk as SCSI, does NOT forward
  SMART via ATA_PT or NVMe STORAGE_PROPERTY_QUERY to usermode → "SMART unavailable".
- Timed-out overlapped requests: OVERLAPPED+buffer stay kernel-owned → leak them
  (bounded by MaxHangs) until a reset flushes IRPs.

## Safety model
- Read tests: any disk, always allowed.
- Write tests: need `--destructive`/`AllowWrites`; volumes dismounted first;
  REFUSED on the disk hosting the system volume unless `AllowSystemDisk`.
- `IsSystemDisk` follows the boot volume, not disk number — on Windows To Go the
  internal test drive can be disk 0 while the To Go stick is protected.

## Known limits / future work
- SMART over USB is bridge-lottery (RTL9210 = unavailable in usermode).
- QD1 I/O only; a queue-depth stress phase would pressure firmware harder.
- SATA AHCI port reset has no clean usermode path — falls back to devnode restart.
- DiskEnumerator itself can block on a truly wedged device (Windows' Get-Disk
  hangs too). WaitForReturnAsync bounds its probes; initial enumerate doesn't —
  possible future fix is per-disk probe isolation with abandon-on-timeout.

## Session notes — 2026-11-26 (sign-off)

### Field results — broken 1 TB drive in ANYOYO EC-SP02 / RTL9210 (VID_0BDA&PID_9210)
- Drive hung on first/early sweep reads repeatedly (~every 1-8 MiB). Hang watchdog
  (15s) fired reliably; devnode restart brought it back in ~60-100s; post-reset
  probe of the hung offset SUCCEEDED both times → transient state hangs, not
  mapped dead regions.
- At reset #4 the devnode vanished mid-reset (self-eject, 0xE000020B) and the
  drive NEVER came back — bridge now sits disabled/error in PnP. Needs physical
  replug. Enable-PnpDevice on the tombstone doesn't revive it.
- Verdict on that run: Failed — "Hung and could not be reset" (correct call;
  with the self-eject fix it would've been "Lost after reset").
- Takeaway for tuning: this drive hung roughly every 1-8 MiB. A full sweep is
  pointless on it — hang-limit abort is the designed outcome. Consider a
  "hang-rate" early-verdict (e.g. N hangs in first X MiB → early FAILED) to
  save 30+ min per brick.

### Repo / release
- Repo: https://github.com/gcnewbief/SpinnyDisc (renamed from spinnydisc —
  origin URL updated; old URL redirects).
- gh token is a fine-grained PAT — repo CREATION is denied; pushes/releases
  work only after the repo is added to the token's scope.
- v0.1.0 released 2026-11-26 with SpinnyDisc.exe + SpinnyDisc.Cli.exe assets.
- Rebase gotcha: `git rebase` without -c identity stalls mid-pick leaving an
  interactive-rebase state with staged changes — commit manually then
  `git rebase --quit && git branch -f main HEAD && git checkout main`.

### GUI state (all live in publish/win-x64/SpinnyDisc.exe)
- VoltCheck dark-green theme, mascot icon (exe + window + header), card layout.
- NumericUpDown: decimal bindings + ShowButtonSpinner=False (spinners ate text).
- Progress bar is a custom Border track+fill (32px ≈ ¼ of Start button height)
  because Fluent2 ProgressBar ignores Height.
- Intensity ComboBox (gentle/normal/heavy) drives SweepStride + idle params +
  seeds the RandomReadOps box.
- Tooltips on all options; elapsed mm:ss in run row; log pane clipped + scrolls
  horizontally; output path ellipsizes.

