using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpinnyDisc.Core.Interop;

namespace SpinnyDisc.Core.Devices;

public enum IoOutcomeKind { Completed, TimedOut, Failed }

public readonly record struct IoResult(
    IoOutcomeKind Kind,
    long Offset,
    uint Length,
    double LatencyMs,
    uint BytesTransferred,
    int Win32Error);

/// <summary>
/// Overlapped raw-sector I/O against \\.\PhysicalDriveN with a per-command timeout.
///
/// Hang semantics: when a command exceeds its timeout we return TimedOut and keep the
/// request's OVERLAPPED + event alive in a zombie list. The kernel still owns them via
/// the pending IRP — freeing early is a memory-corruption bug waiting to happen. The
/// engine resets the device (which flushes pending IRPs) and zombies are only dropped
/// on Dispose; the leak is bounded by the configured hang cap.
///
/// Caller must supply sector-aligned offsets/lengths and aligned buffers — we open the
/// handle with FILE_FLAG_NO_BUFFERING so reads actually hit the media.
/// </summary>
public sealed unsafe class RawDiskHandle : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly List<(nint ovl, EventWaitHandle evt)> _zombies = [];
    private bool _disposed;

    private RawDiskHandle(SafeFileHandle handle, bool canWrite)
    {
        _handle = handle;
        CanWrite = canWrite;
    }

    public bool CanWrite { get; }
    public SafeFileHandle Handle => _handle;
    public int ZombieCount => _zombies.Count;

    public static RawDiskHandle Open(string path, bool write)
    {
        uint access = Kernel32.GENERIC_READ | (write ? Kernel32.GENERIC_WRITE : 0);
        uint flags = Kernel32.FILE_FLAG_OVERLAPPED | Kernel32.FILE_FLAG_NO_BUFFERING;
        if (write) flags |= Kernel32.FILE_FLAG_WRITE_THROUGH;
        var h = Kernel32.CreateFile(path, access,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            IntPtr.Zero, Kernel32.OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"CreateFile({path}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
        return new RawDiskHandle(h, write);
    }

    /// <summary>Overlapped ReadFile at byte offset with a timeout in ms.</summary>
    public IoResult ReadAt(long offset, void* buf, uint len, int timeoutMs)
        => RunIo(true, offset, buf, len, timeoutMs);

    public IoResult WriteAt(long offset, void* buf, uint len, int timeoutMs)
        => RunIo(false, offset, buf, len, timeoutMs);

    public bool Flush() => Kernel32.FlushFileBuffers(_handle);

    private IoResult RunIo(bool isRead, long offset, void* buf, uint len, int timeoutMs)
    {
        var ovl = (WinOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(WinOverlapped));
        var evt = new EventWaitHandle(false, EventResetMode.ManualReset);
        *ovl = Kernel32.MakeOverlapped(offset, evt.SafeWaitHandle.DangerousGetHandle());

        var sw = Stopwatch.StartNew();
        bool issued = isRead
            ? Kernel32.ReadFile(_handle, buf, len, null, ovl)
            : Kernel32.WriteFile(_handle, buf, len, null, ovl);

        if (!issued)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err != Kernel32.ERROR_IO_PENDING)
            {
                FreeRequest(ovl, evt);
                return new IoResult(IoOutcomeKind.Failed, offset, len, sw.Elapsed.TotalMilliseconds, 0, err);
            }
        }

        if (!evt.WaitOne(timeoutMs))
        {
            // Device did not complete the command in time — this is the failure mode
            // the tool exists to catch. Best-effort cancel; the IRP may still be
            // owned by the storage stack, so the request context goes to zombies.
            Kernel32.CancelIoEx(_handle, ovl);
            _zombies.Add(((nint)ovl, evt));
            return new IoResult(IoOutcomeKind.TimedOut, offset, len, sw.Elapsed.TotalMilliseconds, 0, 0);
        }

        bool ok = Kernel32.GetOverlappedResult(_handle, ovl, out uint transferred, false);
        int err2 = ok ? 0 : Marshal.GetLastPInvokeError();
        FreeRequest(ovl, evt);
        return ok
            ? new IoResult(IoOutcomeKind.Completed, offset, len, sw.Elapsed.TotalMilliseconds, transferred, 0)
            : new IoResult(IoOutcomeKind.Failed, offset, len, sw.Elapsed.TotalMilliseconds, transferred, err2);
    }

    /// <summary>Drop zombie contexts — call only after a device reset has flushed pending IRPs.</summary>
    public void ReleaseZombies()
    {
        foreach (var (ovl, evt) in _zombies)
        {
            evt.Dispose();
            if (ovl != 0) NativeMemory.Free((void*)ovl);
        }
        _zombies.Clear();
    }

    private static void FreeRequest(WinOverlapped* ovl, EventWaitHandle evt)
    {
        evt.Dispose();
        NativeMemory.Free(ovl);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseZombies();
        _handle.Dispose();
    }
}
