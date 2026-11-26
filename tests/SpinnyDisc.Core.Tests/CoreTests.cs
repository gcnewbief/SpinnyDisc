using SpinnyDisc.Core.Engine;
using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Tests;

public class BadLbaMapTests
{
    [Fact]
    public void Add_MergesOverlapping()
    {
        var m = new BadLbaMap();
        m.Add(100, 200, BadRegionKind.Hang);
        m.Add(150, 300, BadRegionKind.Hang);
        var r = Assert.Single(m.Regions);
        Assert.Equal(100, r.StartByte);
        Assert.Equal(300, r.EndByte);
        Assert.Equal(2, r.EventCount);
    }

    [Fact]
    public void Add_MergesAcrossGap()
    {
        var m = new BadLbaMap();
        m.Add(0, 100, BadRegionKind.Hang, gap: 50);
        m.Add(140, 200, BadRegionKind.Hang, gap: 50); // within 50 of first → merge
        Assert.Single(m.Regions);
    }

    [Fact]
    public void Touches_RespectsMargin()
    {
        var m = new BadLbaMap();
        m.Add(1000, 2000, BadRegionKind.Hang);
        Assert.True(m.Touches(900, 50, margin: 200));
        Assert.False(m.Touches(500, 100, margin: 200));
    }

    [Fact]
    public void SkipRanges_Inflated()
    {
        var m = new BadLbaMap();
        m.Add(1000, 2000, BadRegionKind.Hang);
        var r = Assert.Single(m.SkipRanges(500));
        Assert.Equal(500, r.start);
        Assert.Equal(2500, r.end);
    }
}

public class PatternTests
{
    [Fact]
    public unsafe void FillVerify_Roundtrip()
    {
        uint len = 4096;
        var buf = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(len, 4096);
        try
        {
            TestRunner.FillPattern(buf, len, 0x51D1EC01, 0);
            Assert.True(TestRunner.VerifyPattern(buf, len, 0x51D1EC01, 0, out int bad));
            buf[123] ^= 0xFF;
            Assert.False(TestRunner.VerifyPattern(buf, len, 0x51D1EC01, 0, out bad));
            Assert.InRange(bad, 120, 127);
        }
        finally { System.Runtime.InteropServices.NativeMemory.AlignedFree(buf); }
    }

    [Fact]
    public unsafe void Fill_IsOffsetDependent()
    {
        uint len = 512;
        var a = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(len, 4096);
        var b = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(len, 4096);
        try
        {
            TestRunner.FillPattern(a, len, 1, 0);
            TestRunner.FillPattern(b, len, 1, 1 << 20);
            Assert.False(new ReadOnlySpan<byte>(a, (int)len).SequenceEqual(new ReadOnlySpan<byte>(b, (int)len)));
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree(a);
            System.Runtime.InteropServices.NativeMemory.AlignedFree(b);
        }
    }
}

public class CtlCodeTests
{
    [Fact]
    public void KnownValues()
    {
        Assert.Equal(0x2D1400u, Ioctl.IOCTL_STORAGE_QUERY_PROPERTY);
        Assert.Equal(0x2D1080u, Ioctl.IOCTL_STORAGE_GET_DEVICE_NUMBER);
        Assert.Equal(0x4D02Cu, Ioctl.IOCTL_ATA_PASS_THROUGH);
        Assert.Equal(0x700A0u, Ioctl.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX);
        Assert.Equal(0x560000u, Ioctl.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS);
        Assert.Equal(0x90018u, Ioctl.FSCTL_LOCK_VOLUME);
        Assert.Equal(0x7C088u, Ioctl.SMART_RCV_DRIVE_DATA);
        Assert.Equal(0x2DD3C0u, Ioctl.IOCTL_STORAGE_PROTOCOL_COMMAND);
    }
}

public class StructLayoutTests
{
    [Fact]
    public void SizesMatchKernel()
    {
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf<AtaPassThroughEx>()); // pad to 8-align DataBufferOffset on x64
        Assert.Equal(36, System.Runtime.InteropServices.Marshal.SizeOf<StorageDeviceDescriptorHeader>());
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.SizeOf<StorageAdapterDescriptor>());
        Assert.Equal(96, System.Runtime.InteropServices.Marshal.SizeOf<StorageProtocolCommand>());
        Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<StorageProtocolSpecificData>());
        Assert.Equal(12, System.Runtime.InteropServices.Marshal.SizeOf<StorageDeviceNumber>());
    }
}
