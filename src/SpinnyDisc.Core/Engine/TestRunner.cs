using System.Diagnostics;
using System.Runtime.InteropServices;
using SpinnyDisc.Core.Devices;
using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Engine;

/// <summary>
/// Runs a test plan against one disk. One command in flight at a time (QD1) —
/// deterministic, and a hung command unambiguously identifies the failing LBA.
/// On a hang the device is reset and the sweep continues past the fault zone.
/// </summary>
public sealed unsafe class TestRunner
{
    private readonly ITestSink _sink;
    private TestReport _report = null!;
    private DiskDescriptor _disk = null!;
    private TestPlan _plan = null!;
    private RawDiskHandle? _handle;
    private byte* _buf;
    private uint _bufLen;
    private uint _align;
    private CancellationToken _ct;
    private bool _abort;
    private readonly Dictionary<long, int> _errorOffsets = new();

    public TestRunner(ITestSink? sink = null) => _sink = sink ?? new NullSink();

    public Task<TestReport> RunAsync(DiskDescriptor disk, TestPlan plan, CancellationToken ct)
        => Task.Run(() => RunCore(disk, plan, ct), ct);

    private TestReport RunCore(DiskDescriptor disk, TestPlan plan, CancellationToken ct)
    {
        _disk = disk; _plan = plan; _ct = ct;
        _report = new TestReport { Disk = disk, Plan = plan, StartedAt = DateTimeOffset.UtcNow };

        Emit(EventKind.TestStart, detail: $"plan: writes={plan.AllowWrites} chunk={plan.ChunkBytes}B hangTimeout={plan.HangTimeoutMs}ms");

        try
        {
            if (!ValidateSafety()) return Finish();

            OpenHandle();

            foreach (var phase in plan.Phases)
            {
                if (_abort) break;
                _ct.ThrowIfCancellationRequested();
                RunPhase(phase);
            }
        }
        catch (OperationCanceledException)
        {
            Emit(EventKind.Aborted, detail: "cancelled by user");
            _report.Verdict = Verdict.Aborted;
            _report.VerdictReason = "Cancelled";
        }
        catch (Exception ex)
        {
            Emit(EventKind.Aborted, detail: $"engine fault: {ex.Message}");
            _report.Verdict = Verdict.Aborted;
            _report.VerdictReason = $"Internal error: {ex.Message}";
        }
        return Finish();
    }

    // ------------------------------------------------------------------ setup

    private bool ValidateSafety()
    {
        if (!_plan.AllowWrites) return true;
        if (_disk.IsSystemDisk && !_plan.AllowSystemDisk)
        {
            Emit(EventKind.Aborted, detail: "refused: destructive test on the disk hosting the system volume (enable AllowSystemDisk to override)");
            _report.Verdict = Verdict.Aborted;
            _report.VerdictReason = "Refused to write to the system disk";
            return false;
        }
        if (_disk.IsSystemDisk && _plan.AllowSystemDisk)
            Emit(EventKind.Note, detail: "WARNING: destructive test explicitly allowed on the SYSTEM disk");
        if (_disk.BusType == StorageBusType.Unknown && _disk.SizeBytes == 0)
        {
            Emit(EventKind.Aborted, detail: "drive reports zero size");
            _report.Verdict = Verdict.Aborted;
            _report.VerdictReason = "Drive unusable";
            return false;
        }
        return true;
    }

    private void OpenHandle()
    {
        if (_plan.AllowWrites && _disk.MountedVolumes.Count > 0)
        {
            if (!DismountVolumes())
                throw new IOException("Could not dismount volumes — destructive test refused");
        }
        _handle = RawDiskHandle.Open(_disk.Path, _plan.AllowWrites);
        uint sector = Math.Max(_disk.BytesPerSector, 512);
        _align = Math.Max(sector, 4096);
        _bufLen = (uint)_plan.ChunkBytes;
        _buf = (byte*)NativeMemory.AlignedAlloc(_bufLen, _align);
    }

    private bool DismountVolumes()
    {
        foreach (var vol in _disk.MountedVolumes)
        {
            string path = $"\\\\.\\{vol.TrimEnd(':')}:";
            var h = Kernel32.CreateFile(path, Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
                IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { Emit(EventKind.Note, detail: $"cannot open {vol} for dismount"); continue; }
            using (h)
            {
                if (!DiskIoctl.Send(h, Ioctl.FSCTL_LOCK_VOLUME, default, default, out _, out int e1))
                {
                    Emit(EventKind.Note, detail: $"FSCTL_LOCK_VOLUME {vol} failed: win32={e1}");
                    return false;
                }
                DiskIoctl.Send(h, Ioctl.FSCTL_DISMOUNT_VOLUME, default, default, out _, out int e2);
                Emit(EventKind.Note, detail: $"dismounted {vol} (win32={e2})");
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ phases

    private void RunPhase(TestPhaseKind phase)
    {
        Emit(EventKind.PhaseStart, phase: phase.ToString());
        try
        {
            switch (phase)
            {
                case TestPhaseKind.Identify: PhaseIdentify(); break;
                case TestPhaseKind.SmartBaseline: PhaseSmartBaseline(); break;
                case TestPhaseKind.ReadSweep: PhaseReadSweep(); break;
                case TestPhaseKind.RandomRead: PhaseRandomRead(); break;
                case TestPhaseKind.IdleBurst: PhaseIdleBurst(); break;
                case TestPhaseKind.WriteVerify: PhaseWriteVerify(); break;
                case TestPhaseKind.HotSpot: PhaseHotSpot(); break;
                case TestPhaseKind.SmartDelta: PhaseSmartDelta(); break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Emit(EventKind.IoError, phase: phase.ToString(), detail: $"phase fault: {ex.Message}");
        }
        Emit(EventKind.PhaseEnd, phase: phase.ToString());
    }

    private void PhaseIdentify()
    {
        var extra = new Dictionary<string, object>
        {
            ["model"] = _disk.Model, ["serial"] = _disk.Serial,
            ["firmware"] = _disk.Firmware, ["bus"] = _disk.BusType.ToString(),
            ["sizeBytes"] = _disk.SizeBytes, ["sectorSize"] = _disk.BytesPerSector,
            ["usbBridge"] = _disk.UsbVidPid ?? "",
            ["ancestors"] = string.Join(" <- ", _disk.AncestorIds),
        };
        // ATA identify reaches past lying USB bridges.
        if (_handle is not null && SmartReader.TryIdentify(_handle.Handle) is { } id)
        {
            extra["driveModel"] = id.model;
            extra["driveSerial"] = id.serial;
            extra["driveFirmware"] = id.firmware;
        }
        Emit(EventKind.IdentifyInfo, phase: "Identify", extra: extra);
    }

    private void PhaseSmartBaseline()
    {
        _report.SmartBefore = _handle is null ? null : SmartReader.Capture(_disk, _handle.Handle);
        Emit(EventKind.SmartSnapshot, phase: "SmartBaseline",
            detail: _report.SmartBefore?.Transport ?? "unavailable",
            extra: _report.SmartBefore?.Fields?.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
    }

    private void PhaseSmartDelta()
    {
        if (_handle is null) return;
        _report.SmartAfter = SmartReader.Capture(_disk, _handle.Handle);
        Emit(EventKind.SmartSnapshot, phase: "SmartDelta",
            detail: _report.SmartAfter.Transport,
            extra: _report.SmartAfter.Fields?.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
    }

    private void PhaseReadSweep()
    {
        long size = _disk.SizeBytes;
        if (_plan.SweepStride > 1)
            Emit(EventKind.Note, phase: "ReadSweep",
                detail: $"sparse sweep: every {_plan.SweepStride}th chunk (~{100 / _plan.SweepStride}% coverage)");
        long pos = 0;
        var lastProgress = Stopwatch.StartNew();
        while (pos < size && !_abort)
        {
            _ct.ThrowIfCancellationRequested();
            uint len = (uint)Math.Min(_bufLen, size - pos);
            len = AlignDown(len);
            if (len == 0) break;

            if (_report.BadMap.Touches(pos, len, _plan.SkipRadiusBytes))
            {
                _report.Stats.SkippedBytes += len;
                pos += len;
                continue;
            }

            if (!IssueIo(pos, len, isWrite: false, "ReadSweep")) break;
            pos += len * _plan.SweepStride;   // stride>1 = sparse sampling sweep
            _report.Stats.BytesRead += len;

            if (lastProgress.ElapsedMilliseconds > 250)
            {
                Progress("ReadSweep", (double)pos / size, pos, size, "sweeping");
                lastProgress.Restart();
            }
        }
        Progress("ReadSweep", 1, pos, size, "done");
    }

    private void PhaseRandomRead()
    {
        var rng = new Random(unchecked((int)(_plan.WriteSeed ^ 0x9E3779B9)));
        int block = Math.Min(_plan.RandomBlockBytes, (int)_bufLen);
        block = (int)AlignDown((uint)block);
        if (block <= 0) return;
        long maxOff = Math.Max(0, _disk.SizeBytes - block);

        for (int i = 0; i < _plan.RandomReadOps && !_abort; i++)
        {
            _ct.ThrowIfCancellationRequested();
            long off = ((long)(rng.NextDouble() * maxOff)) & ~(_align - 1);
            if (!IssueIo(off, (uint)block, false, "RandomRead")) break;
            _report.Stats.BytesRead += block;
            if (i % 32 == 0)
                Progress("RandomRead", (double)i / _plan.RandomReadOps, i, _plan.RandomReadOps, $"{i}/{_plan.RandomReadOps}");
        }
        Progress("RandomRead", 1, _plan.RandomReadOps, _plan.RandomReadOps, "done");
    }

    private void PhaseIdleBurst()
    {
        var rng = new Random(unchecked((int)_plan.WriteSeed + 77));
        for (int round = 0; round < _plan.IdleRounds && !_abort; round++)
        {
            Emit(EventKind.Note, phase: "IdleBurst", detail: $"idling {_plan.IdleSeconds}s (round {round + 1}/{_plan.IdleRounds})");
            Progress("IdleBurst", (double)round / _plan.IdleRounds, round, _plan.IdleRounds, $"idle {round + 1}/{_plan.IdleRounds}");

            // Interruptible idle — check cancel + early failure (a dead drive often
            // fails the first I/O after sleep, but if it's gone entirely we bail).
            for (int s = 0; s < _plan.IdleSeconds * 10; s++)
            {
                _ct.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }

            for (int i = 0; i < _plan.IdleBurstOps && !_abort; i++)
            {
                long off = ((long)(rng.NextDouble() * Math.Max(0, _disk.SizeBytes - 4096))) & ~4095L;
                if (!IssueIo(off, 4096, false, "IdleBurst")) return;
            }
        }
        Progress("IdleBurst", 1, _plan.IdleRounds, _plan.IdleRounds, "done");
    }

    private void PhaseWriteVerify()
    {
        if (!_plan.AllowWrites || _handle is null) return;
        int windowBlocks = Math.Max(1, (64 << 20) / _plan.ChunkBytes); // verify every ~64 MiB
        long size = _disk.SizeBytes;
        long pos = 0;
        var lastProgress = Stopwatch.StartNew();

        while (pos < size && !_abort)
        {
            // --- write window ---
            long winStart = pos;
            for (int b = 0; b < windowBlocks && pos < size && !_abort; b++)
            {
                uint len = AlignDown((uint)Math.Min(_bufLen, size - pos));
                if (len == 0) break;
                if (_report.BadMap.Touches(pos, len, _plan.SkipRadiusBytes)) { pos += len; continue; }

                FillPattern(_buf, len, _plan.WriteSeed, pos);
                if (!IssueIo(pos, len, isWrite: true, "WriteVerify")) return;
                _report.Stats.BytesWritten += len;
                pos += len;
            }
            _handle.Flush();

            // --- verify window ---
            for (long voff = winStart; voff < pos && !_abort;)
            {
                uint len = AlignDown((uint)Math.Min(_bufLen, pos - voff));
                if (len == 0) break;
                if (_report.BadMap.Touches(voff, len, _plan.SkipRadiusBytes)) { voff += len; continue; }

                var r = ReadRaw(voff, len);
                if (r.Kind == IoOutcomeKind.Completed)
                {
                    _report.Stats.SampleLatency(r.LatencyMs);
                    if (!VerifyPattern(_buf, len, _plan.WriteSeed, voff, out int firstBad))
                    {
                        _report.Stats.Mismatches++;
                        long absBad = voff + firstBad;
                        Emit(EventKind.VerifyMismatch, "WriteVerify", absBad, len, r.LatencyMs,
                            detail: $"first mismatch at +{firstBad} bytes");
                        _report.BadMap.Add(absBad, absBad + _align, BadRegionKind.VerifyMismatch);
                    }
                }
                else if (!HandleBadIo(r, "WriteVerify")) return;
                voff += len;
            }

            if (lastProgress.ElapsedMilliseconds > 250)
            {
                Progress("WriteVerify", (double)pos / size, pos, size, $"write+verify {DiskDescriptor.FormatSize(pos)}/{DiskDescriptor.FormatSize(size)}");
                lastProgress.Restart();
            }
        }
        Progress("WriteVerify", 1, pos, size, "done");
    }

    private void PhaseHotSpot()
    {
        var regions = _report.BadMap.Regions.Where(r => r.Kind == BadRegionKind.Hang).ToList();
        if (regions.Count == 0)
        {
            Emit(EventKind.Note, phase: "HotSpot", detail: "no hang regions to characterize");
            return;
        }
        foreach (var region in regions)
        {
            long from = Math.Max(0, region.StartByte - (long)_plan.HotSpotRadiusBlocks * _plan.ChunkBytes);
            long to = Math.Min(_disk.SizeBytes, region.EndByte + (long)_plan.HotSpotRadiusBlocks * _plan.ChunkBytes);
            int probe = 64 * 1024; // 64 KiB probing granularity
            for (long off = from; off < to && !_abort; off += probe)
            {
                _ct.ThrowIfCancellationRequested();
                if (!IssueIo(off, (uint)Math.Min(probe, to - off), false, "HotSpot")) return;
            }
        }
        Progress("HotSpot", 1, 1, 1, "done");
    }

    // ------------------------------------------------------------------ I/O + recovery

    /// <summary>Issue one I/O and drive the hang→reset→retry state machine. False = abort test.</summary>
    private bool IssueIo(long offset, uint len, bool isWrite, string phase)
    {
        var r = isWrite ? _handle!.WriteAt(offset, _buf, len, _plan.HangTimeoutMs)
                        : _handle!.ReadAt(offset, _buf, len, _plan.HangTimeoutMs);
        if (r.Kind == IoOutcomeKind.Completed)
        {
            _report.Stats.OpsCompleted++;
            _report.Stats.SampleLatency(r.LatencyMs);
            if (r.LatencyMs >= _plan.StallThresholdMs)
            {
                _report.Stats.Stalls++;
                Emit(EventKind.Stall, phase, offset, len, r.LatencyMs);
            }
            return true;
        }
        return HandleBadIo(r, phase);
    }

    /// <summary>Handle a Failed/TimedOut I/O result. False = abort.</summary>
    private bool HandleBadIo(IoResult r, string phase)
    {
        if (r.Kind == IoOutcomeKind.Failed && !IsDeviceGone(r.Win32Error))
        {
            // A real I/O error (media error, CRC, etc.) — the drive answered. Record,
            // mark region after a repeat at the same offset, keep going.
            _report.Stats.IoErrors++;
            int seen = _errorOffsets.TryGetValue(r.Offset, out int c) ? c + 1 : 1;
            _errorOffsets[r.Offset] = seen;
            Emit(EventKind.IoError, phase, r.Offset, r.Length, r.LatencyMs, r.Win32Error,
                detail: $"win32={r.Win32Error} (seen x{seen})");
            if (seen >= 2)
                _report.BadMap.Add(r.Offset, r.Offset + _align, BadRegionKind.Hang);
            return true;
        }

        // TimedOut, or the device disappeared mid-command — hang path.
        _report.Stats.Hangs++;
        Emit(EventKind.Hang, phase, r.Offset, r.Length, r.LatencyMs,
            detail: r.Kind == IoOutcomeKind.TimedOut ? $"timeout {_plan.HangTimeoutMs}ms" : $"device lost (win32={r.Win32Error})");

        if (_report.Stats.Hangs > _plan.MaxHangs)
        {
            Emit(EventKind.Aborted, phase, detail: $"hang limit ({_plan.MaxHangs}) exceeded");
            _report.Verdict = Verdict.Failed;
            _report.VerdictReason = $"{_report.Stats.Hangs} hangs — drive condemned";
            _abort = true;
            return false;
        }
        return ResetAndRetry(r, phase);
    }

    private bool ResetAndRetry(IoResult r, string phase)
    {
        if (_report.Stats.Resets >= _plan.MaxResets)
        {
            Emit(EventKind.Aborted, phase, detail: $"reset limit ({_plan.MaxResets}) exceeded");
            _report.Verdict = Verdict.Failed;
            _report.VerdictReason = "Exceeded reset budget — drive condemned";
            _abort = true;
            return false;
        }
        _report.Stats.Resets++;
        Emit(EventKind.ResetAttempt, phase, r.Offset, r.Length, detail: $"reset #{_report.Stats.Resets}");

        var dyingHandle = _handle;
        var method = DeviceResetter.ResetAsync(_disk, dyingHandle?.Handle, _ct)
            .GetAwaiter().GetResult();
        _handle?.Dispose();      // safe: pending IRPs keep the file object alive; zombies leak till exit
        _handle = null;

        if (!method.Success)
        {
            Emit(EventKind.ResetFailed, phase, detail: $"no reset path worked ({method.Detail})");
            _report.Verdict = Verdict.Failed;
            _report.VerdictReason = "Hung and could not be reset";
            _abort = true;
            return false;
        }

        var back = DeviceResetter.WaitForReturnAsync(_disk, _plan.ResetWaitMs, _ct)
            .GetAwaiter().GetResult();
        if (back is null)
        {
            Emit(EventKind.ResetFailed, phase, detail: $"drive did not re-enumerate in {_plan.ResetWaitMs}ms");
            _report.Verdict = Verdict.Failed;
            _report.VerdictReason = "Lost after reset";
            _abort = true;
            return false;
        }

        _disk = back; // disk number may have changed after re-enumeration
        Emit(EventKind.ResetOk, phase, r.Offset, r.Length,
            detail: $"back as {_disk.Path} via {method.Method}");
        _handle = RawDiskHandle.Open(_disk.Path, _plan.AllowWrites);

        // Re-probe the hung offset once: same fault → confirmed dead region; clean → transient.
        var probe = ReadRaw(r.Offset, Math.Min(r.Length, _bufLen));
        if (probe.Kind == IoOutcomeKind.Completed)
        {
            _report.Stats.RetriedOk++;
            _report.Stats.SampleLatency(probe.LatencyMs);
            Emit(EventKind.Note, phase, r.Offset, detail: "post-reset probe of hung offset succeeded (transient hang)");
            return true;
        }
        if (probe.Kind == IoOutcomeKind.TimedOut || IsDeviceGone(probe.Win32Error))
        {
            // Confirmed location-specific fault — map it and let sweeps skip the area.
            _report.BadMap.Add(r.Offset, r.Offset + r.Length, BadRegionKind.Hang);
            Emit(EventKind.Note, phase, r.Offset, detail: "re-hung at same offset — region mapped as bad");
            // The drive is hung again; one more reset before continuing.
            if (_report.Stats.Hangs > _plan.MaxHangs) { _abort = true; return false; }
            return ResetAndContinue(phase);
        }
        Emit(EventKind.Note, phase, r.Offset, detail: $"post-reset probe errored win32={probe.Win32Error}");
        _report.BadMap.Add(r.Offset, r.Offset + _align, BadRegionKind.Hang);
        return true;
    }

    /// <summary>Reset without retrying the same offset (drive re-hung on the probe).</summary>
    private bool ResetAndContinue(string phase)
    {
        if (_report.Stats.Resets >= _plan.MaxResets)
        {
            Emit(EventKind.Aborted, phase, detail: $"reset limit ({_plan.MaxResets}) exceeded");
            _report.Verdict = Verdict.Failed;
            _report.VerdictReason = "Exceeded reset budget — drive condemned";
            _abort = true;
            return false;
        }
        _report.Stats.Resets++;
        Emit(EventKind.ResetAttempt, phase, detail: $"reset #{_report.Stats.Resets} (post-probe)");
        var dying = _handle;
        var method = DeviceResetter.ResetAsync(_disk, dying?.Handle, _ct).GetAwaiter().GetResult();
        _handle?.Dispose(); _handle = null;
        if (!method.Success) { _abort = true; _report.Verdict = Verdict.Failed; _report.VerdictReason = "Could not reset"; return false; }
        var back = DeviceResetter.WaitForReturnAsync(_disk, _plan.ResetWaitMs, _ct).GetAwaiter().GetResult();
        if (back is null) { _abort = true; _report.Verdict = Verdict.Failed; _report.VerdictReason = "Lost after reset"; return false; }
        _disk = back;
        Emit(EventKind.ResetOk, phase, detail: $"back as {_disk.Path} via {method.Method}");
        _handle = RawDiskHandle.Open(_disk.Path, _plan.AllowWrites);
        return true;
    }

    private IoResult ReadRaw(long offset, uint len)
        => _handle!.ReadAt(offset, _buf, len, _plan.HangTimeoutMs);

    private static bool IsDeviceGone(int err) => err is
        Kernel32.ERROR_DEVICE_NOT_CONNECTED or Kernel32.ERROR_FILE_NOT_FOUND
        or Kernel32.ERROR_PATH_NOT_FOUND or Kernel32.ERROR_INVALID_HANDLE
        or Kernel32.ERROR_NOT_READY or Kernel32.ERROR_GEN_FAILURE
        or Kernel32.ERROR_REQUEST_ABORTED or Kernel32.ERROR_BAD_COMMAND;

    private uint AlignDown(uint len) => len & ~(_align - 1);

    // ------------------------------------------------------------------ patterns

    /// <summary>Deterministic fill: xorshift32 seeded by (seed ^ offset) so verify can regenerate.</summary>
    internal static void FillPattern(byte* buf, uint len, uint seed, long offset)
    {
        uint x = seed ^ (uint)(offset >> 12) ^ 0x9E3779B9;
        var p = (uint*)buf;
        for (uint i = 0; i < len / 4; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            p[i] = x;
        }
        for (uint i = len & ~3u; i < len; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            buf[i] = (byte)x;
        }
    }

    internal static bool VerifyPattern(byte* buf, uint len, uint seed, long offset, out int firstBad)
    {
        firstBad = -1;
        uint x = seed ^ (uint)(offset >> 12) ^ 0x9E3779B9;
        var p = (uint*)buf;
        for (uint i = 0; i < len / 4; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            if (p[i] != x) { firstBad = (int)(i * 4); return false; }
        }
        return true;
    }

    // ------------------------------------------------------------------ finish

    private void Emit(EventKind kind, string phase = "", long offset = -1, long length = 0,
        double latency = 0, int win32err = 0, string detail = "", Dictionary<string, object>? extra = null)
    {
        var e = new TestEvent
        {
            Kind = kind, Phase = phase, Offset = offset, Length = length,
            LatencyMs = latency, Win32Error = win32err, Detail = detail, Extra = extra,
        };
        _report.Events.Add(e);
        _sink.Emit(e);
    }

    private void Progress(string phase, double frac, long done, long total, string status)
        => _sink.Progress(new PhaseProgress(Enum.Parse<TestPhaseKind>(phase), frac, done, total, status));

    private TestReport Finish()
    {
        if (_report.Verdict == Verdict.Inconclusive)
        {
            var s = _report.Stats;
            if (s.Mismatches > 0 || s.Hangs > 0)
            {
                _report.Verdict = Verdict.Failed;
                _report.VerdictReason = s.Mismatches > 0
                    ? $"{s.Mismatches} verify mismatch(es) — silent corruption"
                    : $"{s.Hangs} hang(s) requiring {_report.Stats.Resets} reset(s)";
            }
            else if (s.Stalls > 0 || s.IoErrors > 0)
            {
                _report.Verdict = Verdict.Degraded;
                _report.VerdictReason = $"{s.Stalls} stall(s), {s.IoErrors} I/O error(s) — matches OS-hang symptom profile";
            }
            else
            {
                _report.Verdict = Verdict.Pass;
                _report.VerdictReason = "no hangs, stalls, errors or corruption detected";
            }
        }
        _report.FinishedAt = DateTimeOffset.UtcNow;
        Emit(EventKind.TestEnd, detail: $"verdict={_report.Verdict}: {_report.VerdictReason}");
        _handle?.Dispose();
        if (_buf != null) { NativeMemory.AlignedFree(_buf); _buf = null; }
        return _report;
    }
}
