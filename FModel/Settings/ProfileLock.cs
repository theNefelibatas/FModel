using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Serilog;

namespace FModel.Settings;

/* 同游戏单实例（每档案一个锁）。锁文件内容为进程 PID，干净退出删除；
 * 崩溃残留期间锁文件仍在，通过 PID 存活判断自愈。 */
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
            using var reader = new StreamReader(lease, leaveOpen: true);
            if (int.TryParse(reader.ReadToEnd().Trim(), out var pid) && pid > 0 && ProcessAlive(pid))
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
            return new ProfileLock(path, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public static bool IsHeldByAliveProcess(string profileId)
    {
        var path = Path.Combine(ProfileStore.ProfilesDirectory, $"{profileId}.lock");
        if (!File.Exists(path)) return false;
        var pid = ReadPid(path);
        return pid > 0 && ProcessAlive(pid);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lease.Dispose();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { Log.Warning(ex, "Could not delete lock {Path}", _path); }
    }

    private static int ReadPid(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return int.Parse(reader.ReadToEnd().Trim());
        }
        catch { return 0; }
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        /* 跨权限/已消失的 PID：视同不可判活 → 不算存活（锁可被下一实例自愈接管） */
        catch (Win32Exception) { return false; }
    }
}
