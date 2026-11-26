using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpinnyDisc.Core.Interop;

/// <summary>OVERLAPPED with a plain event handle — we drive waits manually, no IOCP.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WinOverlapped
{
    public UIntPtr Internal;
    public UIntPtr InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public IntPtr hEvent;
}

internal static unsafe class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;

    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;

    public const uint OPEN_EXISTING = 3;

    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint FILE_FLAG_NORMAL = 0x00000080;

    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_IO_INCOMPLETE = 996;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_SEM_TIMEOUT = 121;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const int ERROR_BAD_COMMAND = 22;
    public const int ERROR_CRC = 23;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_WRITE_FAULT = 29;
    public const int ERROR_READ_FAULT = 30;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_REQUEST_ABORTED = 1235;

    public static WinOverlapped MakeOverlapped(long offset, IntPtr hEvent) => new()
    {
        Offset = unchecked((uint)offset),
        OffsetHigh = unchecked((uint)(offset >> 32)),
        hEvent = hEvent,
    };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        void* lpBuffer,
        uint nNumberOfBytesToRead,
        uint* lpNumberOfBytesRead,
        WinOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        SafeFileHandle hFile,
        void* lpBuffer,
        uint nNumberOfBytesToWrite,
        uint* lpNumberOfBytesWritten,
        WinOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(
        SafeFileHandle hFile,
        WinOverlapped* lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle hFile, WinOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle hHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
