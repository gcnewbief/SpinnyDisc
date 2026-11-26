# SpinnyDisc

SSD reliability triage for drives that lie to you.

<img src="src/SpinnyDisc/Assets/mascot-256.png" width="128" align="right">

Some drives fail in a way no SMART report will show: they read fine in
CrystalDiskInfo, every attribute is green, and then the OS hangs for 30
seconds, throws I/O errors, or the drive falls off the bus entirely.
The failure isn't a bad sector — it's the firmware or bridge wedging
under load.

SpinnyDisc doesn't benchmark. It issues real sector-level I/O at queue
depth 1, watches every command, and when a command doesn't come back it
resets the device, re-probes the hung offset to tell a *dead region* from
a *firmware state hang*, skips the fault zone, and keeps going. At the
end you get a verdict, a hang/stall map, and an HTML report.

## What it catches

- **Hangs** — a command that never completes (the classic "everything
  freezes for 30 s" drive). Caught by a per-I/O watchdog, logged with
  the exact offset, then the device is reset and the test continues.
- **Stalls** — commands that complete but far too slowly. The
  "micro-freeze" symptom.
- **I/O errors** — media errors, CRC failures, device-gone events.
- **Silent corruption** — with write+verify enabled, every written byte
  is read back and compared against its generated pattern.
- **Bus resets** — hung USB devices are recovered via bridge devnode
  restart; NVMe/SATA get protocol-level resets where the driver allows.

## Toolchain

| Project | What |
|---|---|
| `src/SpinnyDisc` | Avalonia GUI — pick a disk, set options, watch live |
| `src/SpinnyDisc.Cli` | Headless runner — `list`, `test`, `pnp` |
| `src/SpinnyDisc.Core` | Engine: enumeration, raw I/O, resets, phases, reports |
| `tests/` | Unit tests incl. struct-layout guards for the IOCTL structs |

Windows only (the engine is raw `\\.\PhysicalDriveN` + SetupAPI/CfgMgr32
interop). Requires administrator — raw disk access and device resets
need it; both apps carry `requireAdministrator` manifests.

## Test phases

1. **Identify** — model/serial/firmware, bus type, PnP ancestor chain
   (catches which USB bridge is in the path — a "hang" can be the
   bridge, not the drive).
2. **SMART baseline** — ATA / NVMe / SAT-over-USB attempts.
3. **Read sweep** — sequential across the surface.
4. **Random reads** — stresses firmware lookups, provokes GC.
5. **Idle-burst** — idle, then hammer it. Many failing drives wedge on
   wake from their own power management.
6. **Write + verify** *(destructive, opt-in)* — pattern write, flush,
   read-back compare in ~64 MiB windows.
7. **Hot-spot** — dense re-probing around every confirmed hang region.
8. **SMART delta** — same attributes, after the abuse.

## Hang recovery

QD1 overlapped I/O means a hung command unambiguously names the failing
LBA. On timeout:

1. Record the hang, then reset — `IOCTL_STORAGE_RESET_DEVICE`, NVMe
   protocol reset, ATA `DEVICE RESET`, or SetupAPI disable/enable on
   the devnode (USB bridges get a full re-enumeration this way).
2. Wait for the disk to come back and re-verify identity before
   resuming (serial/model/size match — USB renumbering is expected).
3. Re-probe the hung offset once. Same fault → confirmed bad region,
   added to the LBA map and skipped thereafter. Reads fine → transient
   firmware hang, counted but not mapped.
4. If the devnode vanished mid-reset (win32 `0xE000020B`) that's the
   device self-ejecting — a free reset; just wait for re-enumeration.

Bail-out: `MaxHangs` total hangs or `MaxResets` resets → verdict
FAILED. A drive needing thirty resets is already condemned.

## GUI

- Drive picker with bridge/ancestor detail; system disk flagged.
- **Intensity presets**: `gentle` (~25% surface, quick triage),
  `normal` (full sweep), `heavy` (full sweep + sustained random
  pressure + long idle-wake cycles).
- Live event log (also streamed to JSONL), phase progress, elapsed
  timer, verdict banner.
- `destructive write+verify` is opt-in; `allow disk zero` is a second
  explicit gate required before writes touch the boot-volume disk —
  and `IsSystemDisk` follows the *volume*, not the disk number, so a
  Windows To Go test drive at disk 0 needs no override.

## CLI

```
SpinnyDisc.Cli list                    # enumerate drives + PnP ancestry
SpinnyDisc.Cli test 1                  # read-only test on PhysicalDrive1
    --destructive                      # enable write+verify
    --allow-system-disk                # ...even on the boot disk
    --cap-mb 2048                      # only touch first N MiB (smoke test)
    --hang-ms 15000  --stall-ms 5000  --max-resets 10  --ops 4096
    --out <dir>
SpinnyDisc.Cli pnp                     # raw devnode diagnostics
```

Exit code 0 = PASS, 1 = anything else. Reports land in `--out`
(default `<exe>/reports/`): `*.jsonl` (every event), `*.json`
(structured report), `*.html` (verdict, stats, LBA fault scatter,
SMART delta).

## Build & publish

```powershell
dotnet test                                          # unit tests
dotnet publish src/SpinnyDisc -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o publish/win-x64      # GUI, ~105 MB
dotnet publish src/SpinnyDisc.Cli -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o publish/cli-win-x64  # CLI, ~74 MB
```

Single-file self-contained exes — drop on a Windows To Go bench stick
and go. The icon/mascot is generated by `tools/make-icon.py` (Pillow).

## Known limits

- **USB bridges mask the truth.** RTL9210-class bridges don't forward
  NVMe health queries to usermode, so SMART may report "unavailable"
  over USB. UASP buffering hides micro-stalls; you'll still catch gross
  hangs. The report records the bridge VID/PID so you can blame it.
- **Enumeration itself can hang** on a truly wedged drive — Windows'
  own `Get-Disk` stalls too. Re-enumeration waits inside the engine are
  time-bounded; the initial enumerate isn't, so a catatonic drive may
  need a replug before the tool can see it.
- Timed-out I/O contexts are deliberately leaked (the kernel may still
  own their IRPs) until a confirmed reset releases them — bounded by
  `MaxHangs`.
