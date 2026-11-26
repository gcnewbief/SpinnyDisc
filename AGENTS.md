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
