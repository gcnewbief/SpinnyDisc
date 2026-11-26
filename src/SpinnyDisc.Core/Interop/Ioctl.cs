using System.Runtime.InteropServices;

namespace SpinnyDisc.Core.Interop;

/// <summary>
/// Storage IOCTL constants and structures, transcribed from ntddstor.h / ntddscsi.h /
/// ntdddisk.h / usbioctl.h. Struct layouts must match the kernel exactly — do not "tidy" them.
/// </summary>
internal static class Ioctl
{
    // Device types
    public const uint FILE_DEVICE_DISK = 0x0007;
    public const uint FILE_DEVICE_FILE_SYSTEM = 0x0009;
    public const uint FILE_DEVICE_USB = 0x0022;
    public const uint IOCTL_SCSI_BASE = 0x0004;          // FILE_DEVICE_CONTROLLER
    public const uint IOCTL_STORAGE_BASE = FILE_DEVICE_MASS_STORAGE;
    public const uint FILE_DEVICE_MASS_STORAGE = 0x002d;
    public const uint IOCTL_VOLUME_BASE = 0x56;          // 'V'

    public const uint METHOD_BUFFERED = 0;
    public const uint METHOD_IN_DIRECT = 1;
    public const uint METHOD_OUT_DIRECT = 2;
    public const uint METHOD_NEITHER = 3;

    public const uint FILE_ANY_ACCESS = 0;
    public const uint FILE_READ_ACCESS = 1;
    public const uint FILE_WRITE_ACCESS = 2;
    public const uint FILE_SPECIAL_ACCESS = 0;

    public static uint CtlCode(uint device, uint function, uint method, uint access)
        => (device << 16) | (access << 14) | (function << 2) | method;

    // ntddstor.h
    public static readonly uint IOCTL_STORAGE_QUERY_PROPERTY = CtlCode(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_STORAGE_GET_DEVICE_NUMBER = CtlCode(IOCTL_STORAGE_BASE, 0x0420, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_STORAGE_RESET_DEVICE = CtlCode(IOCTL_STORAGE_BASE, 0x0400, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_RESET_BUS = CtlCode(IOCTL_STORAGE_BASE, 0x0401, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_BREAK_RESERVATION = CtlCode(IOCTL_STORAGE_BASE, 0x0405, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_PROTOCOL_COMMAND = CtlCode(IOCTL_STORAGE_BASE, 0x04F0, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS);

    // ntdddisk.h
    public static readonly uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = CtlCode(FILE_DEVICE_DISK, 0x0028, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_DISK_GET_LENGTH_INFO = CtlCode(FILE_DEVICE_DISK, 0x0017, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_DISK_UPDATE_PROPERTIES = CtlCode(FILE_DEVICE_DISK, 0x0050, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint SMART_RCV_DRIVE_DATA = CtlCode(FILE_DEVICE_DISK, 0x0022, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS);

    // ntddscsi.h
    public static readonly uint IOCTL_ATA_PASS_THROUGH = CtlCode(IOCTL_SCSI_BASE, 0x040b, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS);
    public static readonly uint IOCTL_ATA_PASS_THROUGH_EX = CtlCode(IOCTL_SCSI_BASE, 0x040d, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS);
    public static readonly uint IOCTL_SCSI_PASS_THROUGH_DIRECT = CtlCode(IOCTL_SCSI_BASE, 0x0405, METHOD_IN_DIRECT | METHOD_OUT_DIRECT, FILE_READ_ACCESS | FILE_WRITE_ACCESS);
    public static readonly uint IOCTL_SCSI_GET_ADDRESS = CtlCode(IOCTL_SCSI_BASE, 0x0406, METHOD_BUFFERED, FILE_ANY_ACCESS);

    // ntddvol.h / winioctl.h
    public static readonly uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = CtlCode(IOCTL_VOLUME_BASE, 0x0000, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint FSCTL_LOCK_VOLUME = CtlCode(FILE_DEVICE_FILE_SYSTEM, 6, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint FSCTL_UNLOCK_VOLUME = CtlCode(FILE_DEVICE_FILE_SYSTEM, 7, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint FSCTL_DISMOUNT_VOLUME = CtlCode(FILE_DEVICE_FILE_SYSTEM, 8, METHOD_BUFFERED, FILE_ANY_ACCESS);

    // usbioctl.h
    public static readonly uint IOCTL_USB_HUB_CYCLE_PORT = CtlCode(FILE_DEVICE_USB, 0x0111, METHOD_BUFFERED, FILE_ANY_ACCESS);

    // STORAGE_PROPERTY_ID
    public const int StorageDeviceProperty = 0;
    public const int StorageAdapterProperty = 1;
    public const int StorageDeviceIdProperty = 2;
    public const int StorageDeviceUniqueIdProperty = 3;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int StorageDeviceTrimProperty = 8;
    public const int StorageDeviceMediumProductType = 15;
    public const int StorageAdapterProtocolSpecificProperty = 49;
    public const int StorageDeviceProtocolSpecificProperty = 50;
    public const int StorageAdapterTemperatureProperty = 51;
    public const int StorageDeviceTemperatureProperty = 52;

    // STORAGE_QUERY_TYPE
    public const int PropertyStandardQuery = 0;
    public const int PropertyExistsQuery = 1;

    // STORAGE_PROTOCOL_TYPE
    public const int ProtocolTypeUnknown = 0x00;
    public const int ProtocolTypeScsi = 0x01;
    public const int ProtocolTypeAta = 0x02;
    public const int ProtocolTypeNvme = 0x03;
    public const int ProtocolTypeSd = 0x04;

    // STORAGE_PROTOCOL_NVME_DATA_TYPE
    public const int NVMeDataTypeUnknown = 0;
    public const int NVMeDataTypeIdentify = 1;
    public const int NVMeDataTypeLogPage = 2;
    public const int NVMeDataTypeFeature = 3;

    // NVMe log page ids
    public const byte NVMeLogPageErrorInfo = 0x01;
    public const byte NVMeLogPageSmartHealth = 0x02;

    // STORAGE_PROTOCOL_COMMAND flags
    public const uint STORAGE_PROTOCOL_COMMAND_FLAG_ADAPTER_REQUEST = 0x80000000;
    public const uint STORAGE_PROTOCOL_COMMAND_FLAG_NVME_RESET = 0x00000001;

    // ATA flags (ntddscsi.h)
    public const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
    public const ushort ATA_FLAGS_DATA_IN = 0x02;
    public const ushort ATA_FLAGS_DATA_OUT = 0x04;
    public const ushort ATA_FLAGS_48BIT_COMMAND = 0x08;
    public const ushort ATA_FLAGS_USE_DMA = 0x10;
    public const ushort ATA_FLAGS_NO_MULTIPLE = 0x20;

    // ATA commands
    public const byte ATA_CMD_IDENTIFY_DEVICE = 0xEC;
    public const byte ATA_CMD_DEVICE_RESET = 0x08;
    public const byte ATA_CMD_SMART = 0xB0;
    public const byte ATA_CMD_CHECK_POWER_MODE = 0xE5;
    public const byte ATA_CMD_STANDBY_IMMEDIATE = 0xE0;

    // SMART feature registers
    public const byte SMART_READ_ATTRIBUTES = 0xD0;
    public const byte SMART_READ_THRESHOLDS = 0xD1;
    public const byte SMART_ENABLE_OPERATIONS = 0xD8;
    public const byte SMART_RETURN_STATUS = 0xDA;
    public const byte SMART_READ_LOG = 0xD5;
    public const byte SMART_WRITE_LOG = 0xD6;
    public const byte SMART_EXECUTE_OFFLINE = 0xD4;
    public const byte SMART_CYL_LOW = 0x4F;
    public const byte SMART_CYL_HIGH = 0xC2;

    // Native sizes include alignment padding — derive from the managed layouts.
    public static readonly int StorageProtocolCommandSize = Marshal.SizeOf<StorageProtocolCommand>();
    public static readonly int AtaPassThroughExSize = Marshal.SizeOf<AtaPassThroughEx>();
}

public enum StorageBusType : byte
{
    Unknown = 0x00,
    Scsi = 0x01,
    Atapi = 0x02,
    Ata = 0x03,
    Ieee1394 = 0x04,
    Ssa = 0x05,
    Fibre = 0x06,
    Usb = 0x07,
    Raid = 0x08,
    IScsi = 0x09,
    Sas = 0x0A,
    Sata = 0x0B,
    Sd = 0x0C,
    Mmc = 0x0D,
    Virtual = 0x0E,
    FileBackedVirtual = 0x0F,
    Spaces = 0x10,
    Nvme = 0x11,
    Scm = 0x12,
    Ufs = 0x13,
    Max = 0x14,
}

[StructLayout(LayoutKind.Sequential)]
internal struct StoragePropertyQuery
{
    public int PropertyId;
    public int QueryType;
    // AdditionalParameters[] follows
}

/// <summary>Header of STORAGE_DEVICE_DESCRIPTOR — variable strings live after it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StorageDeviceDescriptorHeader
{
    public uint Version;
    public uint Size;
    public byte DeviceType;
    public byte DeviceTypeModifier;
    public byte RemovableMedia;
    public byte CommandQueueing;
    public int VendorIdOffset;
    public int ProductIdOffset;
    public int ProductRevisionOffset;
    public int SerialNumberOffset;
    public int BusType;          // STORAGE_BUS_TYPE (int in device descriptor)
    public uint RawPropertiesLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageAdapterDescriptor
{
    public uint Version;
    public uint Size;
    public uint MaximumTransferLength;
    public uint MaximumPhysicalPages;
    public uint AlignmentMask;
    public byte AdapterUsesPio;
    public byte AdapterScansDown;
    public byte CommandQueueing;
    public byte AcceleratedTransfer;
    public byte BusType;         // STORAGE_BUS_TYPE as a byte here
    public ushort BusMajorVersion;
    public ushort BusMinorVersion;
    public byte SrbType;
    public byte AddressType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageDeviceIdDescriptorHeader
{
    public uint Version;
    public uint Size;
    public uint NumberOfIdentifiers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageIdentifier
{
    public byte CodeSet;
    public byte Type;
    public ushort IdentifierSize;
    public ushort NextIdOffset;
    public byte Association;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageDeviceNumber
{
    public uint DeviceType;
    public uint DeviceNumber;
    public uint PartitionNumber;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DiskGeometry
{
    public long Cylinders;
    public uint MediaType;       // 12 = FixedMedia
    public uint TracksPerCylinder;
    public uint SectorsPerTrack;
    public uint BytesPerSector;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DiskExtent
{
    public uint DiskNumber;
    public long StartingOffset;
    public long ExtentLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageProtocolSpecificData
{
    public int ProtocolType;
    public int DataType;
    public uint ProtocolSpecificRequestValue;
    public uint ProtocolSpecificRequestValue2;
    public uint ProtocolSpecificRequestValue3;
    public uint ProtocolSpecificRequestValue4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageProtocolDataDescriptor
{
    public uint Version;
    public uint Size;
    public StorageProtocolSpecificData ProtocolSpecific;
    public uint ProtocolDataOffset;
    public uint ProtocolDataSize;
    public uint ProtocolDataLength;
    // byte ProtocolData[] follows
}

/// <summary>STORAGE_PROTOCOL_COMMAND — command buffer follows the header.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StorageProtocolCommand
{
    public uint Version;                  // sizeof(STORAGE_PROTOCOL_COMMAND)
    public uint Length;                   // header + command bytes
    public int ProtocolType;
    public uint Flags;
    public uint ReturnStatus;
    public uint ErrorCode;
    public uint CommandLength;
    public uint ErrorInfoLength;
    public uint DataToDeviceTransferLength;
    public uint DataFromDeviceTransferLength;
    public uint TimeOutValue;
    public uint ErrorInfoOffset;
    public uint DataToDeviceBufferOffset;
    public uint DataFromDeviceBufferOffset;
    public uint CommandSpecific;
    public uint Reserved0;
    public uint FixedProtocolReturnData;
    public uint ReturnDataLength;
    public StorageProtocolSpecificData ProtocolSpecific;
    // byte Command[] follows
}

/// <summary>ATA_PASS_THROUGH_EX — data buffer sits at DataBufferOffset inside the same allocation.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AtaPassThroughEx
{
    public ushort Length;               // sizeof
    public ushort AtaFlags;
    public byte PathId;
    public byte TargetId;
    public byte Lun;
    public byte ReservedAsUchar;
    public uint DataTransferLength;
    public uint TimeOutValue;           // seconds
    public uint ReservedAsUlong;
    public nuint DataBufferOffset;
    public fixed byte PreviousTaskFile[8];
    public fixed byte CurrentTaskFile[8];
}

/// <summary>IDEREGS as used by SMART_RCV_DRIVE_DATA (old pre-W8 SMART path).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IdeRegs
{
    public byte Features;
    public byte SectorCount;
    public byte SectorNumber;   // LBA low
    public byte CylLow;         // LBA mid
    public byte CylHigh;        // LBA high
    public byte DriveHead;
    public byte Command;
    public byte Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SendCmdInParams
{
    public uint cBufferSize;
    public IdeRegs irDriveRegs;
    public byte bDriveNumber;
    public byte Reserved0;
    public byte Reserved1;
    public byte Reserved2;
    public uint dwReserved0;
    public uint dwReserved1;
    public uint dwReserved2;
    public uint dwReserved3;
    // byte bBuffer[] follows
}
