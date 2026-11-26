using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Devices;

public static class DiskEnumerator
{
    /// <summary>
    /// Probe \\.\PhysicalDrive0..63 and build descriptors, merging in
    /// volume-mount info and PnP ancestry (USB bridge detection).
    /// </summary>
    public static List<DiskDescriptor> Enumerate()
    {
        var devNodes = Pnp.EnumerateDiskDevNodes();
        var diskVolumes = MapVolumesToDisks(out var systemDisks);
        var list = new List<DiskDescriptor>();

        for (int n = 0; n < 64; n++)
        {
            var d = TryProbe(n);
            if (d is null) continue;

            if (diskVolumes.TryGetValue(n, out var vols))
                d = d with { MountedVolumes = vols };
            if (systemDisks.Contains(n))
                d = d with { IsSystemDisk = true };
            if (devNodes.TryGetValue(n, out var node))
                d = d with
                {
                    DevInst = node.DevInst,
                    DeviceInstanceId = node.DeviceInstanceId,
                    AncestorIds = node.AncestorIds,
                    UsbVidPid = Pnp.FindUsbVidPid(node.AncestorIds),
                };

            list.Add(d);
        }
        return list;
    }

    private static DiskDescriptor? TryProbe(int n)
    {
        var h = Kernel32.CreateFile(
            $"\\\\.\\PhysicalDrive{n}",
            Kernel32.GENERIC_READ,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        using (h)
        {
            // Confirm the number — don't trust the path after re-enumeration.
            if (!DiskIoctl.GetDeviceNumber(h, out uint actual) || actual != n)
                return null;

            string vendor = "", product = "", revision = "", serial = "";
            var bus = StorageBusType.Unknown;
            var adapterBus = StorageBusType.Unknown;

            if (DiskIoctl.QueryStorageProperty(h, Ioctl.StorageDeviceProperty) is { } devDesc)
            {
                if (DiskIoctl.ParseDeviceDescriptor(devDesc) is { } p)
                {
                    vendor = p.vendor; product = p.product; revision = p.revision;
                    serial = p.serial; bus = (StorageBusType)p.busType;
                }
            }
            if (DiskIoctl.QueryStorageProperty(h, Ioctl.StorageAdapterProperty) is { } adDesc
                && adDesc.Length >= Marshal.SizeOf<StorageAdapterDescriptor>())
            {
                var ad = MemoryMarshal.Read<StorageAdapterDescriptor>(adDesc);
                adapterBus = (StorageBusType)ad.BusType;
            }

            if (!DiskIoctl.GetGeometryEx(h, out long size, out uint sectorSize) || size <= 0)
                return null;
            if (sectorSize == 0) sectorSize = 512;

            return new DiskDescriptor
            {
                Number = n,
                Model = string.IsNullOrWhiteSpace(product) ? vendor : $"{vendor} {product}".Trim(),
                Serial = serial,
                Firmware = revision,
                BusType = bus,
                AdapterBusType = adapterBus,
                SizeBytes = size,
                BytesPerSector = sectorSize,
                IncursSeekPenalty = DiskIoctl.QuerySeekPenalty(h),
                SupportsTrim = DiskIoctl.QueryTrimEnabled(h),
            };
        }
    }

    /// <summary>Map disk number → mounted volume roots; also find which disks host the system volume.</summary>
    private static Dictionary<int, List<string>> MapVolumesToDisks(out HashSet<int> systemDisks)
    {
        var map = new Dictionary<int, List<string>>();
        systemDisks = [];

        var sysRoot = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        char sysLetter = char.ToUpperInvariant(sysRoot[0]);

        for (char c = 'A'; c <= 'Z'; c++)
        {
            string vol = $"\\\\.\\{c}:";
            var h = Kernel32.CreateFile(vol, Kernel32.GENERIC_READ,
                Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
                IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            using (h)
            {
                foreach (int dn in DiskIoctl.GetVolumeDiskExtents(h))
                {
                    if (!map.TryGetValue(dn, out var l)) map[dn] = l = [];
                    l.Add($"{c}:");
                    if (c == sysLetter) systemDisks.Add(dn);
                }
            }
        }
        return map;
    }
}
