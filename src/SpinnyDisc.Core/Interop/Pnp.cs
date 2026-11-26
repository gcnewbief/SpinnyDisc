using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;

namespace SpinnyDisc.Core.Interop;

/// <summary>
/// Plug-and-play helpers built on the CsWin32-generated SetupAPI / CfgMgr32 bindings.
/// Used for disk-number ↔ devnode mapping, USB ancestor info (bridge VID/PID),
/// and device reset via disable/enable.
/// </summary>
internal static unsafe class Pnp
{
    public static readonly Guid GuidDevInterfaceDisk = new("{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}");
    public static readonly Guid GuidDevInterfaceVolume = new("{53F5630D-B6BF-11D0-94F2-00A0C91EFB8B}");

    public sealed record DiskDevNode(
        uint DevInst,
        string DevicePath,          // \\?\IDE#DISK...#{guid} interface path
        string DeviceInstanceId,    // e.g. USBSTOR\DISK&VEN_...
        IReadOnlyList<string> AncestorIds, // devnode ids walking up: usbstor devnode, usb device (VID/PID), hub...
        string PhysicalObjectName); // \Device\HarddiskN\DRn -> disk number

    /// <summary>Last failure detail for diagnostics.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Enumerate all present disk devnodes, keyed by physical disk number.</summary>
    public static Dictionary<int, DiskDevNode> EnumerateDiskDevNodes()
    {
        LastError = null;
        var result = new Dictionary<int, DiskDevNode>();
        using var devs = PInvoke.SetupDiGetClassDevs(
            GuidDevInterfaceDisk, null, default,
            SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE);

        if (devs.IsInvalid)
        {
            LastError = $"GetClassDevs: {Marshal.GetLastPInvokeErrorMessage()}";
            return result;
        }

        var hraw = new HDEVINFO(devs.DangerousGetHandle());
        var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
        for (uint i = 0; ; i++)
        {
            if (!PInvoke.SetupDiEnumDeviceInterfaces(devs, null, GuidDevInterfaceDisk, i, ref ifData))
            {
                if (i == 0) LastError = $"EnumDeviceInterfaces[0]: {Marshal.GetLastPInvokeErrorMessage()}";
                break;
            }
            ifData.cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA); // re-arm each iteration

            // First call gets the required detail buffer size.
            uint required = 0;
            PInvoke.SetupDiGetDeviceInterfaceDetail(hraw, &ifData, null, 0, &required, null);
            if (required == 0)
            {
                LastError ??= $"GetInterfaceDetail probe[{i}]: req=0 {Marshal.GetLastPInvokeErrorMessage()}";
                continue;
            }
            if (required == 0 || required > 1 << 20)
                continue;

            var buf = new byte[required];
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            fixed (byte* p = buf)
            {
                // cbSize quirk: the native struct is DWORD+WCHAR[] but x64 expects 8, x86 expects 6.
                *(uint*)p = (uint)(IntPtr.Size == 8 ? 8 : 4 + sizeof(char));
                if (!PInvoke.SetupDiGetDeviceInterfaceDetail(hraw, &ifData, (SP_DEVICE_INTERFACE_DETAIL_DATA_W*)p, required, &required, &devInfo))
                {
                    LastError ??= $"GetInterfaceDetail[{i}]: {Marshal.GetLastPInvokeErrorMessage()}";
                    continue;
                }
                string path = Marshal.PtrToStringUni((IntPtr)(p + sizeof(uint))) ?? "";
                string pdo = GetRegPropertyString(hraw, &devInfo, SETUP_DI_REGISTRY_PROPERTY.SPDRP_PHYSICAL_DEVICE_OBJECT_NAME) ?? "";

                // PDO names aren't guaranteed to be \Device\HarddiskN — query the number directly.
                int diskNum = GetDiskNumberFromPath(path);
                if (diskNum < 0) diskNum = ParseDiskNumber(pdo);
                if (diskNum < 0)
                {
                    LastError ??= $"disk number unknown: pdo='{pdo}' path='{path}'";
                    continue;
                }

                result[diskNum] = new DiskDevNode(
                    devInfo.DevInst,
                    path,
                    GetDeviceId(devInfo.DevInst),
                    GetAncestorIds(devInfo.DevInst),
                    pdo);
            }
        }
        return result;
    }

    /// <summary>Open a device-interface path and ask it for its STORAGE_DEVICE_NUMBER.</summary>
    private static int GetDiskNumberFromPath(string path)
    {
        var h = Kernel32.CreateFile(path, Kernel32.GENERIC_READ,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return -1;
        using (h)
        {
            Span<byte> output = stackalloc byte[12];
            fixed (byte* po = output)
            {
                if (!Kernel32.DeviceIoControl(h, Ioctl.IOCTL_STORAGE_GET_DEVICE_NUMBER,
                        null, 0, po, (uint)output.Length, out _, IntPtr.Zero))
                    return -1;
            }
            return (int)BitConverter.ToUInt32(output[4..]);
        }
    }

    /// <summary>Parse "\Device\Harddisk5\DR9" -> 5.</summary>
    public static int ParseDiskNumber(string physicalObjectName)
    {
        const string marker = "Harddisk";
        int i = physicalObjectName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        i += marker.Length;
        int j = i;
        while (j < physicalObjectName.Length && char.IsDigit(physicalObjectName[j])) j++;
        return j > i && int.TryParse(physicalObjectName.AsSpan(i, j - i), out int n) ? n : -1;
    }

    public static string GetDeviceId(uint devInst, int maxChars = 512)
    {
        Span<char> buf = stackalloc char[maxChars];
        fixed (char* p = buf)
        {
            var cr = PInvoke.CM_Get_Device_IDW(devInst, new PWSTR(p), (uint)buf.Length, 0);
            if (cr != CONFIGRET.CR_SUCCESS) return "";
        }
        int len = buf.IndexOf('\0');
        return len < 0 ? new string(buf) : new string(buf[..len]);
    }

    /// <summary>Devnode ids of all ancestors, nearest parent first.</summary>
    public static List<string> GetAncestorIds(uint devInst)
    {
        var ids = new List<string>();
        uint cur = devInst;
        for (int depth = 0; depth < 8; depth++)
        {
            var cr = PInvoke.CM_Get_Parent(out uint parent, cur, 0);
            if (cr != CONFIGRET.CR_SUCCESS || parent == 0 || parent == cur)
                break;
            ids.Add(GetDeviceId(parent));
            cur = parent;
        }
        return ids;
    }

    /// <summary>Best-effort "VID_xxxx&PID_xxxx" extracted from a USB ancestor id.</summary>
    public static string? FindUsbVidPid(IEnumerable<string> ancestorIds)
    {
        foreach (var id in ancestorIds)
        {
            var m = Regex.Match(id, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
            if (m.Success) return m.Value;
        }
        return null;
    }

    /// <summary>Get a single-string registry property (hardware IDs come back joined with ';').</summary>
    public static string? GetRegPropertyString(HDEVINFO devs, SP_DEVINFO_DATA* devInfo, SETUP_DI_REGISTRY_PROPERTY prop)
    {
        var vals = GetRegPropertyMulti(devs, devInfo, prop);
        return vals is null ? null : string.Join(";", vals);
    }

    public static string[]? GetRegPropertyMulti(HDEVINFO devs, SP_DEVINFO_DATA* devInfo, SETUP_DI_REGISTRY_PROPERTY prop)
    {
        uint required = 0, regType = 0;
        PInvoke.SetupDiGetDeviceRegistryProperty(devs, devInfo, prop, &regType, null, 0, &required);
        if (required == 0 || required > 1 << 20)
            return null;
        var buf = new byte[required];
        fixed (byte* p = buf)
        {
            if (!PInvoke.SetupDiGetDeviceRegistryProperty(devs, devInfo, prop, &regType, p, required, &required))
                return null;
        }
        var s = Marshal.PtrToStringUni((IntPtr)Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0), buf.Length / 2)!;
        return s.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Locate a devinst by device instance id ("USBSTOR\DISK&VEN_...").</summary>
    public static uint LocateDevInst(string deviceId)
    {
        fixed (char* p = deviceId)
        {
            var cr = PInvoke.CM_Locate_DevNode(out uint devInst, new PWSTR(p), 0);
            return cr == CONFIGRET.CR_SUCCESS ? devInst : 0;
        }
    }

    /// <summary>
    /// Disable then re-enable a devnode — the classic devcon-style reset.
    /// Used to recover USBSTOR children and NVMe/SATA controllers when lower-level
    /// resets aren't available. Requires admin.
    /// </summary>
    public static bool RestartDevNode(uint targetDevInst, out string error)
    {
        error = "";
        var devs = PInvoke.SetupDiGetClassDevs(
            (Guid?)null, null, default, SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT);
        if (devs.IsInvalid)
        {
            error = $"SetupDiGetClassDevs failed: {Marshal.GetLastPInvokeErrorMessage()}";
            return false;
        }
        using (devs)
        {
            var hraw = new HDEVINFO(devs.DangerousGetHandle());
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            for (uint i = 0; PInvoke.SetupDiEnumDeviceInfo(hraw, i, &devInfo); i++)
            {
                if (devInfo.DevInst != targetDevInst)
                    continue;

                // Found the devnode inside this devinfo set — set the class install
                // params to disable, apply, then enable.
                var hdr = new SP_PROPCHANGE_PARAMS
                {
                    ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                    {
                        cbSize = (uint)sizeof(SP_CLASSINSTALL_HEADER),
                        InstallFunction = (DI_FUNCTION)0x3, // DICS_PROPCHANGE
                    },
                    StateChange = (SETUP_DI_STATE_CHANGE)0x2,         // DICS_DISABLE
                    Scope = (SETUP_DI_PROPERTY_CHANGE_SCOPE)0x2,      // DICS_FLAG_CONFIGSPECIFIC
                    HwProfile = 0,
                };

                if (!PInvoke.SetupDiSetClassInstallParams(hraw, &devInfo, (SP_CLASSINSTALL_HEADER*)&hdr, (uint)sizeof(SP_PROPCHANGE_PARAMS)) ||
                    !PInvoke.SetupDiCallClassInstaller((DI_FUNCTION)0x3, devs, devInfo))
                {
                    error = $"Disable failed: {Marshal.GetLastPInvokeErrorMessage()}";
                    return false;
                }

                Thread.Sleep(500);
                hdr.StateChange = (SETUP_DI_STATE_CHANGE)0x1; // DICS_ENABLE

                if (!PInvoke.SetupDiSetClassInstallParams(hraw, &devInfo, (SP_CLASSINSTALL_HEADER*)&hdr, (uint)sizeof(SP_PROPCHANGE_PARAMS)) ||
                    !PInvoke.SetupDiCallClassInstaller((DI_FUNCTION)0x3, devs, devInfo))
                {
                    error = $"Enable failed: {Marshal.GetLastPInvokeErrorMessage()}";
                    return false;
                }
                return true;
            }
        }
        error = "devnode not found in device info set";
        return false;
    }
}
