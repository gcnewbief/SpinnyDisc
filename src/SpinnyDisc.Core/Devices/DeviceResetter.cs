using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Devices;

public enum ResetMethod
{
    None,
    StorageResetDevice,   // IOCTL_STORAGE_RESET_DEVICE — usbstor port reset
    NvmeProtocolReset,    // STORAGE_PROTOCOL_COMMAND + NVME_RESET flag
    AtaDeviceReset,       // ATA 0x08 via pass-through
    DevnodeRestart,       // SetupAPI disable/enable of a parent devnode
}

public sealed record ResetResult(bool Success, ResetMethod Method, string Detail);

/// <summary>
/// Recover a hung drive. Strategy by bus:
///   USB  → IOCTL_STORAGE_RESET_DEVICE, then devnode restart of the USBSTOR parent.
///   NVMe → protocol-command controller reset, then adapter devnode restart.
///   SATA → ATA DEVICE RESET, then adapter devnode restart.
/// The caller closes/reopens its own handle; we only trigger the reset + wait for return.
/// </summary>
public static class DeviceResetter
{
    public static async Task<ResetResult> ResetAsync(
        DiskDescriptor disk, SafeFileHandle? dyingHandle, CancellationToken ct)
    {
        // --- Step 1: in-band resets on the existing (possibly hung) handle ---
        if (dyingHandle is { IsInvalid: false })
        {
            var tried = disk.BusType switch
            {
                StorageBusType.Usb => TryStorageReset(dyingHandle)
                    ? ResetMethod.StorageResetDevice : ResetMethod.None,
                StorageBusType.Nvme => TryNvmeReset(dyingHandle)
                    ? ResetMethod.NvmeProtocolReset : ResetMethod.None,
                StorageBusType.Sata or StorageBusType.Ata => SmartReader.TryAtaDeviceReset(dyingHandle)
                    ? ResetMethod.AtaDeviceReset : ResetMethod.None,
                _ => TryStorageReset(dyingHandle) ? ResetMethod.StorageResetDevice : ResetMethod.None,
            };
            if (tried != ResetMethod.None)
                return new ResetResult(true, tried, "in-band reset accepted");
        }

        // --- Step 2: devnode restart ---
        // For USB, restart the USBSTOR parent (first ancestor). For internal drives,
        // restart the first ancestor (adapter/controller) — heavier but effective.
        // Fall back to restarting the disk devnode itself.
        uint target = 0;
        foreach (var anc in disk.AncestorIds)
        {
            if (anc.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) ||
                anc.StartsWith("SCSI\\", StringComparison.OrdinalIgnoreCase) ||
                anc.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) && disk.BusType == StorageBusType.Usb)
            {
                target = Pnp.LocateDevInst(anc);
                if (target != 0) break;
            }
        }
        if (target == 0 && disk.AncestorIds.Count > 0)
            target = Pnp.LocateDevInst(disk.AncestorIds[0]);
        if (target == 0 && disk.DevInst != 0)
            target = disk.DevInst;

        if (target == 0)
            // Nothing to restart — the wedged device already dropped off the bus.
            return new ResetResult(true, ResetMethod.DevnodeRestart,
                "devnode gone — device self-ejected, waiting for re-enumeration");

        if (!Pnp.RestartDevNode(target, out string err))
        {
            // 0xE000020B (no such devinst): devnode vanished mid-reset — same self-eject case.
            if (err.Contains("0xE000020B", StringComparison.OrdinalIgnoreCase))
                return new ResetResult(true, ResetMethod.DevnodeRestart,
                    "devnode vanished mid-reset — device self-ejected");
            return new ResetResult(false, ResetMethod.DevnodeRestart, err);
        }

        return new ResetResult(true, ResetMethod.DevnodeRestart, "devnode disable/enable issued");
    }

    /// <summary>Wait for the disk to re-enumerate and confirm identity.</summary>
    public static async Task<DiskDescriptor?> WaitForReturnAsync(
        DiskDescriptor original, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct).ConfigureAwait(false);

            // A wedged drive can hang enumeration itself — bound each probe and
            // abandon it on timeout rather than stall the whole wait loop.
            var probe = Task.Run(() => DiskEnumerator.Enumerate(), ct);
            if (await Task.WhenAny(probe, Task.Delay(10_000, ct)) != probe)
                continue;
            List<DiskDescriptor> disks;
            try { disks = probe.Result; }
            catch { continue; }

            foreach (var d in disks)
            {
                if (d.IdentityKey == original.IdentityKey)
                    return d;
            }
            // Serial-less bridges: relax the match — same model+size+bus.
            foreach (var d in disks)
            {
                if (d.Model == original.Model && d.SizeBytes == original.SizeBytes
                    && d.BusType == original.BusType && d.Serial == original.Serial)
                    return d;
            }
        }
        return null;
    }

    /// <summary>IOCTL_STORAGE_RESET_DEVICE on an open disk handle (usbstor supports it).</summary>
    private static unsafe bool TryStorageReset(SafeFileHandle h)
    {
        return DiskIoctl.Send(h, Ioctl.IOCTL_STORAGE_RESET_DEVICE, default, default, out _, out _);
    }

    /// <summary>NVMe controller reset via STORAGE_PROTOCOL_COMMAND + NVME_RESET flag.</summary>
    private static unsafe bool TryNvmeReset(SafeFileHandle h)
    {
        var io = new byte[Ioctl.StorageProtocolCommandSize];
        ref var cmd = ref MemoryMarshal.AsRef<StorageProtocolCommand>(io.AsSpan());
        cmd.Version = (uint)Ioctl.StorageProtocolCommandSize;
        cmd.Length = (uint)io.Length;
        cmd.ProtocolType = Ioctl.ProtocolTypeNvme;
        cmd.Flags = Ioctl.STORAGE_PROTOCOL_COMMAND_FLAG_NVME_RESET;
        cmd.TimeOutValue = 10;
        cmd.ProtocolSpecific.ProtocolType = Ioctl.ProtocolTypeNvme;

        return DiskIoctl.Send(h, Ioctl.IOCTL_STORAGE_PROTOCOL_COMMAND, io, io, out _, out _);
    }
}
