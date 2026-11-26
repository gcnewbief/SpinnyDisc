using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Devices;

public sealed record SmartSnapshot
{
    public bool Available { get; init; }
    public string Transport { get; init; } = "none";   // NVMe | ATA | SAT-USB | none
    public string RawHex { get; init; } = "";
    public Dictionary<string, string> Fields { get; init; } = new();
    public string? Failure { get; init; }
}

public static class SmartReader
{
    /// <summary>Try every SMART path that makes sense for this bus. Never throws.</summary>
    public static SmartSnapshot Capture(DiskDescriptor disk, SafeFileHandle h)
    {
        try
        {
            // NVMe log page 0x02 — works on real NVMe and on bridges that pass
            // STORAGE_PROPERTY_QUERY protocol queries through (RTL9210 does).
            if (disk.BusType == StorageBusType.Nvme || disk.BusType == StorageBusType.Usb)
                if (TryNvmeHealth(h) is { } nvme)
                    return nvme;

            if (disk.BusType is StorageBusType.Sata or StorageBusType.Ata or StorageBusType.Raid)
                if (TryAtaSmart(h) is { } ata)
                    return ata with { Transport = "ATA" };

            // USB: SAT ATA pass-through (many SATA bridges support it).
            if (disk.BusType == StorageBusType.Usb)
            {
                if (TryAtaSmart(h) is { } sat)
                    return sat with { Transport = "SAT-USB" };
                if (TryLegacySmart(h) is { } legacy)
                    return legacy with { Transport = "SAT-USB(legacy)" };
            }

            // Last resort for anything else.
            if (TryAtaSmart(h) is { } any)
                return any with { Transport = "ATA" };

            return new SmartSnapshot { Available = false, Failure = "no transport succeeded" };
        }
        catch (Exception ex)
        {
            return new SmartSnapshot { Available = false, Failure = ex.Message };
        }
    }

    // ---------------- NVMe ----------------

    private static unsafe SmartSnapshot? TryNvmeHealth(SafeFileHandle h)
    {
        // STORAGE_PROPERTY_QUERY + STORAGE_PROTOCOL_SPECIFIC_DATA
        var input = new byte[8 + Marshal.SizeOf<StorageProtocolSpecificData>()];
        BitConverter.GetBytes(Ioctl.StorageDeviceProtocolSpecificProperty).CopyTo(input, 0);
        BitConverter.GetBytes(Ioctl.PropertyStandardQuery).CopyTo(input, 4);
        var spec = new StorageProtocolSpecificData
        {
            ProtocolType = Ioctl.ProtocolTypeNvme,
            DataType = Ioctl.NVMeDataTypeLogPage,
            ProtocolSpecificRequestValue = Ioctl.NVMeLogPageSmartHealth,
            ProtocolSpecificRequestValue2 = 0,
        };
        MemoryMarshal.Write(input.AsSpan(8), in spec);

        var output = new byte[8 + 24 + 12 + 512];
        if (!DiskIoctl.Send(h, Ioctl.IOCTL_STORAGE_QUERY_PROPERTY, input, output, out int ret, out int err)
            || ret < 48)
            return null;

        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size, ProtocolSpecific(24),
        // ProtocolDataOffset, ProtocolDataSize, ProtocolDataLength
        int off = (int)BitConverter.ToUInt32(output, 32);
        int len = (int)BitConverter.ToUInt32(output, 36);
        if (off <= 0 || len < 512 || off + 512 > output.Length)
            return null;

        var log = output.AsSpan(off, 512);
        var fields = new Dictionary<string, string>
        {
            ["CriticalWarning"] = $"0x{log[0]:X2}",
            ["TemperatureC"] = $"{BitConverter.ToUInt16(log[1..]) - 273}",
            ["AvailableSparePct"] = $"{log[3]}",
            ["SpareThresholdPct"] = $"{log[4]}",
            ["PercentageUsed"] = $"{log[5]}",
            ["DataUnitsRead"] = $"{Read128(log, 32)}",
            ["DataUnitsWritten"] = $"{Read128(log, 48)}",
            ["PowerCycles"] = $"{Read128(log, 112)}",
            ["PowerOnHours"] = $"{Read128(log, 128)}",
            ["UnsafeShutdowns"] = $"{Read128(log, 144)}",
            ["MediaErrors"] = $"{Read128(log, 160)}",
            ["ErrorLogEntries"] = $"{Read128(log, 176)}",
        };
        return new SmartSnapshot
        {
            Available = true,
            Transport = "NVMe",
            RawHex = Convert.ToHexString(log),
            Fields = fields,
        };
    }

    private static UInt128 Read128(ReadOnlySpan<byte> s, int off) =>
        new(BitConverter.ToUInt64(s.Slice(off + 8, 8)), BitConverter.ToUInt64(s.Slice(off, 8)));

    // ---------------- ATA SMART via ATA_PASS_THROUGH_EX (direct or SAT) ----------------

    private static unsafe SmartSnapshot? TryAtaSmart(SafeFileHandle h)
    {
        var raw = AtaPassThrough(h, feature: Ioctl.SMART_READ_ATTRIBUTES, command: Ioctl.ATA_CMD_SMART, dataLen: 512);
        if (raw is null)
        {
            // SMART may be disabled — try enabling, then re-read.
            if (AtaPassThrough(h, Ioctl.SMART_ENABLE_OPERATIONS, Ioctl.ATA_CMD_SMART, 0) is not null)
                raw = AtaPassThrough(h, Ioctl.SMART_READ_ATTRIBUTES, Ioctl.ATA_CMD_SMART, 512);
        }
        if (raw is null) return null;

        var fields = ParseAtaAttributes(raw);
        return new SmartSnapshot
        {
            Available = fields.Count > 0,
            RawHex = Convert.ToHexString(raw),
            Fields = fields,
        };
    }

    /// <summary>IDENTIFY DEVICE (0xEC) — model/serial straight from the drive, behind lying bridges.</summary>
    public static unsafe (string model, string serial, string firmware)? TryIdentify(SafeFileHandle h)
    {
        var raw = AtaPassThrough(h, feature: 0, command: Ioctl.ATA_CMD_IDENTIFY_DEVICE, dataLen: 512,
            lbaMid: 0, lbaHigh: 0);
        if (raw is null || raw.Length < 512) return null;
        string serial = ReadSwappedAscii(raw, 20, 20);
        string firmware = ReadSwappedAscii(raw, 46, 8);
        string model = ReadSwappedAscii(raw, 54, 40);
        return string.IsNullOrWhiteSpace(model) ? null : (model, serial, firmware);
    }

    /// <summary>ATA DEVICE RESET (0x08) — best-effort, mostly a no-op on AHCI/SAT.</summary>
    public static unsafe bool TryAtaDeviceReset(SafeFileHandle h)
        => AtaPassThrough(h, feature: 0, command: Ioctl.ATA_CMD_DEVICE_RESET, dataLen: 0,
            lbaMid: 0, lbaHigh: 0) is not null;

    /// <summary>CHECK POWER MODE (0xE5) — cheap liveness probe.</summary>
    public static unsafe byte? CheckPowerMode(SafeFileHandle h)
    {
        var raw = AtaPassThrough(h, feature: 0, command: Ioctl.ATA_CMD_CHECK_POWER_MODE, dataLen: 0,
            lbaMid: 0, lbaHigh: 0);
        // Sector-count register = CurrentTaskFile[1] = struct offset 36+1.
        return raw is { Length: > 37 } ? raw[37] : null;
    }

    private static unsafe byte[]? AtaPassThrough(SafeFileHandle h, byte feature, byte command,
        uint dataLen, byte lbaMid = Ioctl.SMART_CYL_LOW, byte lbaHigh = Ioctl.SMART_CYL_HIGH,
        byte sectorCount = 1, byte device = 0xA0)
    {
        int bufLen = Ioctl.AtaPassThroughExSize + (int)Math.Max(dataLen, 1);
        var io = new byte[bufLen];
        ref var ap = ref MemoryMarshal.AsRef<AtaPassThroughEx>(io.AsSpan());
        ap.Length = (ushort)Ioctl.AtaPassThroughExSize;
        ap.AtaFlags = dataLen > 0 ? Ioctl.ATA_FLAGS_DATA_IN : (ushort)0;
        ap.DataTransferLength = dataLen;
        ap.TimeOutValue = 20;
        ap.DataBufferOffset = (nuint)Ioctl.AtaPassThroughExSize;
        ap.CurrentTaskFile[0] = feature;
        ap.CurrentTaskFile[1] = sectorCount;
        ap.CurrentTaskFile[3] = lbaMid;
        ap.CurrentTaskFile[4] = lbaHigh;
        ap.CurrentTaskFile[5] = device;
        ap.CurrentTaskFile[6] = command;

        var outBuf = new byte[bufLen];
        if (!DiskIoctl.Send(h, Ioctl.IOCTL_ATA_PASS_THROUGH_EX, io, outBuf, out int ret, out _) || ret < Ioctl.AtaPassThroughExSize)
            return null;
        return dataLen > 0 ? outBuf[Ioctl.AtaPassThroughExSize..(Ioctl.AtaPassThroughExSize + (int)dataLen)] : outBuf;
    }

    private static string ReadSwappedAscii(byte[] raw, int off, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i += 2)
        {
            if (i + 1 < len) { sb.Append((char)raw[off + i + 1]); sb.Append((char)raw[off + i]); }
        }
        return sb.ToString().Trim('\0', ' ');
    }

    private static Dictionary<string, string> ParseAtaAttributes(byte[] page)
    {
        var fields = new Dictionary<string, string>();
        // 512-byte page: 30 attribute entries of 12 bytes starting at offset 2.
        for (int i = 0; i < 30; i++)
        {
            int off = 2 + i * 12;
            byte id = page[off];
            if (id == 0) continue;
            byte cur = page[off + 3], worst = page[off + 4];
            long rawVal = BitConverter.ToInt64(new byte[] {
                page[off + 5], page[off + 6], page[off + 7], page[off + 8],
                page[off + 9], page[off + 10], 0, 0 });
            string name = AttrName(id);
            fields[$"{id:D3}_{name}"] = $"norm={cur} worst={worst} raw={rawVal}";
        }
        return fields;
    }

    private static string AttrName(byte id) => id switch
    {
        5 => "ReallocatedSectors", 9 => "PowerOnHours", 12 => "PowerCycles",
        171 => "ProgramFailCount", 172 => "EraseFailCount", 173 => "WearLeveling",
        174 => "UnexpectedPowerLoss", 177 => "WearRangeDelta", 181 => "ProgramFailTotal",
        182 => "EraseFailTotal", 187 => "ReportedUncorrect", 188 => "CommandTimeout",
        190 => "AirflowTemp", 194 => "Temperature", 195 => "HardwareEccRecovered",
        196 => "ReallocEvents", 197 => "PendingSectors", 198 => "OfflineUncorrectable",
        199 => "UdmaCrcErrors", 231 => "SsdLifeLeft", 233 => "MediaWearout",
        241 => "TotalLbasWritten", 242 => "TotalLbasRead",
        _ => $"Attr{id}",
    };

    // ---------------- Legacy SMART_RCV_DRIVE_DATA ----------------

    private static unsafe SmartSnapshot? TryLegacySmart(SafeFileHandle h)
    {
        var input = new byte[Marshal.SizeOf<SendCmdInParams>()];
        ref var sc = ref MemoryMarshal.AsRef<SendCmdInParams>(input.AsSpan());
        sc.cBufferSize = 512;
        sc.irDriveRegs.Features = Ioctl.SMART_READ_ATTRIBUTES;
        sc.irDriveRegs.SectorCount = 1;
        sc.irDriveRegs.CylLow = Ioctl.SMART_CYL_LOW;
        sc.irDriveRegs.CylHigh = Ioctl.SMART_CYL_HIGH;
        sc.irDriveRegs.Command = Ioctl.ATA_CMD_SMART;

        var output = new byte[Marshal.SizeOf<SendCmdInParams>() + 16 + 512];
        if (!DiskIoctl.Send(h, Ioctl.SMART_RCV_DRIVE_DATA, input, output, out int ret, out _)
            || ret < output.Length - 512)
            return null;

        // SENDCMDOUTPARAMS: cBufferSize(4) + DRIVERSTATUS(8: bDriverError,bIDEStatus,res[2],dwReserved[2]) then data
        var raw = output[^512..];
        var fields = ParseAtaAttributes(raw);
        if (fields.Count == 0) return null;
        return new SmartSnapshot { Available = true, RawHex = Convert.ToHexString(raw), Fields = fields };
    }
}
