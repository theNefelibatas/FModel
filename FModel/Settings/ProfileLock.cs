using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Serilog;

namespace FModel.Settings;

/* 写句柄保证同档案互斥。兼容只记录 PID 的旧锁时，进程必须在锁写入前就已启动。 */
public sealed class ProfileLock : IDisposable
{
    private readonly string _path;
    private readonly FileStream _lease;
    private int _disposed;

    private ProfileLock(string path, FileStream lease)
    {
        _path = path;
        _lease = lease;
    }

    public static ProfileLock TryAcquire(string profileId)
    {
        Directory.CreateDirectory(ProfileStore.ProfilesDirectory);
        var path = Path.Combine(ProfileStore.ProfilesDirectory, $"{profileId}.lock");
        FileStream lease;
        try
        {
            // Keep an exclusive writer handle for the session lifetime. A PID file alone races during startup.
            lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException) { return null; }

        try
        {
            var pid = ReadOwnerPid(lease);
            if (pid > 0)
            {
                lease.Dispose();
                Log.Information("Profile {Id} is already locked by process {Pid}", profileId, pid);
                return null;
            }
            lease.SetLength(0);
            lease.Position = 0;
            using var writer = new StreamWriter(lease, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            // Windows can defer the last-write timestamp until the writer closes.
            File.SetLastWriteTimeUtc(lease.SafeFileHandle, DateTime.UtcNow);
            return new ProfileLock(path, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public static bool IsHeldByAliveProcess(string profileId)
    {
        var path = Path.Combine(ProfileStore.ProfilesDirectory, $"{profileId}.lock");
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadOwnerPid(stream) > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lease.Dispose();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { Log.Warning(ex, "Could not delete lock {Path}", _path); }
    }

    private static int ReadOwnerPid(FileStream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        if (!int.TryParse(reader.ReadToEnd().Trim(), out var pid) || pid <= 0) return 0;
        var writtenAt = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() <= writtenAt ? pid : 0;
        }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }
        catch (Win32Exception) { return 0; }
    }
}
