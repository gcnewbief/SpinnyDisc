using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Devices;

/// <summary>Everything we know about one physical disk after probing.</summary>
public sealed record DiskDescriptor
{
    public required int Number { get; init; }
    public string Path => $"\\\\.\\PhysicalDrive{Number}";

    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Firmware { get; init; } = "";
    public StorageBusType BusType { get; init; } = StorageBusType.Unknown;
    public StorageBusType AdapterBusType { get; init; } = StorageBusType.Unknown;

    public long SizeBytes { get; init; }
    public uint BytesPerSector { get; init; }
    public long TotalSectors => BytesPerSector > 0 ? SizeBytes / BytesPerSector : 0;

    /// <summary>null = unknown.</summary>
    public bool? IncursSeekPenalty { get; init; }
    public bool? SupportsTrim { get; init; }
    public bool IsLikelySsd => BusType == StorageBusType.Nvme || IncursSeekPenalty == false;

    public bool IsSystemDisk { get; init; }
    public IReadOnlyList<string> MountedVolumes { get; init; } = [];

    /// <summary>USB bridge "VID_xxxx&PID_xxxx" when attached over USB.</summary>
    public string? UsbVidPid { get; init; }
    public IReadOnlyList<string> AncestorIds { get; init; } = [];
    public uint DevInst { get; init; }
    public string DeviceInstanceId { get; init; } = "";

    /// <summary>
    /// Identity key used to re-find the same device after a reset/re-enumeration.
    /// Serial can be blank/all-zero behind cheap USB bridges, so model+size is folded in.
    /// </summary>
    public string IdentityKey =>
        $"{Model.Trim()}|{Serial.Trim()}|{SizeBytes}|{DeviceInstanceId}";

    public string DisplayName =>
        $"Disk {Number} — {Model.Trim()} ({FormatSize(SizeBytes)}, {BusType})";

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }
}
