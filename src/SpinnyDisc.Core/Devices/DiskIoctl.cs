using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Devices;

/// <summary>Thin DeviceIoControl helpers over stackalloc'd buffers.</summary>
internal static unsafe class DiskIoctl
{
    public static bool Send(SafeFileHandle h, uint code,
        ReadOnlySpan<byte> input, Span<byte> output,
        out int returned, out int win32Error)
    {
        fixed (byte* pi = input)
        fixed (byte* po = output)
        {
            bool ok = Kernel32.DeviceIoControl(h, code, pi, (uint)input.Length, po, (uint)output.Length, out uint ret, IntPtr.Zero);
            returned = (int)ret;
            win32Error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }

    /// <summary>Issue IOCTL_STORAGE_QUERY_PROPERTY; returns the output buffer or null.</summary>
    public static byte[]? QueryStorageProperty(SafeFileHandle h, int propertyId, int queryType = Ioctl.PropertyStandardQuery, byte[]? additionalParams = null)
    {
        int inLen = 8 + (additionalParams?.Length ?? 0);
        var input = new byte[Math.Max(inLen, 16)];
        BitConverter.GetBytes(propertyId).CopyTo(input, 0);
        BitConverter.GetBytes(queryType).CopyTo(input, 4);
        additionalParams?.CopyTo(input, 8);

        var output = new byte[64 * 1024];
        if (!Send(h, Ioctl.IOCTL_STORAGE_QUERY_PROPERTY, input, output, out int ret, out _) || ret < 8)
            return null;
        return output[..ret];
    }

    /// <summary>Offsets within a STORAGE_DEVICE_DESCRIPTOR for the string fields.</summary>
    public static (string vendor, string product, string revision, string serial, int busType)?
        ParseDeviceDescriptor(byte[] d)
    {
        if (d.Length < 36) return null;
        var h = MemoryMarshal.Read<StorageDeviceDescriptorHeader>(d);
        if (h.Size < 36 || h.Size > d.Length) h.Size = (uint)d.Length;
        return (
            ReadAscii(d, h.VendorIdOffset),
            ReadAscii(d, h.ProductIdOffset),
            ReadAscii(d, h.ProductRevisionOffset),
            ReadAscii(d, h.SerialNumberOffset),
            h.BusType);
    }

    public static string ReadAscii(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    public static bool GetLengthInfo(SafeFileHandle h, out long bytes)
    {
        bytes = 0;
        Span<byte> output = stackalloc byte[8];
        if (!Send(h, Ioctl.IOCTL_DISK_GET_LENGTH_INFO, default, output, out _, out _))
            return false;
        bytes = BitConverter.ToInt64(output);
        return true;
    }

    public static bool GetGeometryEx(SafeFileHandle h, out long diskSize, out uint bytesPerSector)
    {
        diskSize = 0; bytesPerSector = 0;
        var output = new byte[64];
        if (!Send(h, Ioctl.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, default, output, out int ret, out _) || ret < 32)
            return false;
        var g = MemoryMarshal.Read<DiskGeometry>(output);
        bytesPerSector = g.BytesPerSector;
        diskSize = BitConverter.ToInt64(output, 24); // DiskSize after DISK_GEOMETRY(24)
        return true;
    }

    public static bool GetDeviceNumber(SafeFileHandle h, out uint diskNumber)
    {
        diskNumber = uint.MaxValue;
        Span<byte> output = stackalloc byte[12];
        if (!Send(h, Ioctl.IOCTL_STORAGE_GET_DEVICE_NUMBER, default, output, out _, out _))
            return false;
        diskNumber = BitConverter.ToUInt32(output[4..]);
        return true;
    }

    /// <summary>Which disks back a volume like "\\.\C:" → disk numbers.</summary>
    public static int[] GetVolumeDiskExtents(SafeFileHandle volume)
    {
        var output = new byte[1024];
        if (!Send(volume, Ioctl.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, default, output, out int ret, out _) || ret < 8)
            return [];
        uint count = BitConverter.ToUInt32(output, 0);
        var list = new List<int>((int)Math.Min(count, 32));
        int off = 8;
        for (int i = 0; i < count && off + 24 <= ret; i++, off += 24)
            list.Add(BitConverter.ToInt32(output, off));
        return [.. list];
    }

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty (byte at offset 8).</summary>
    public static bool? QuerySeekPenalty(SafeFileHandle h)
    {
        var d = QueryStorageProperty(h, Ioctl.StorageDeviceSeekPenaltyProperty);
        return d is { Length: >= 9 } ? d[8] != 0 : null;
    }

    /// <summary>DEVICE_TRIM_DESCRIPTOR.TrimEnabled (byte at offset 8).</summary>
    public static bool? QueryTrimEnabled(SafeFileHandle h)
    {
        var d = QueryStorageProperty(h, Ioctl.StorageDeviceTrimProperty);
        return d is { Length: >= 9 } ? d[8] != 0 : null;
    }
}
