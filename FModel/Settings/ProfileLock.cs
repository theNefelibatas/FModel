using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace FModel.Settings;

/* 同游戏单实例（每档案一个锁）。锁文件内容为进程 PID，干净退出删除；
 * 崩溃残留期间锁文件仍在，通过 PID 存活判断自愈。 */
public sealed class ProfileLock : IDisposable
{
    private readonly string _path;

    private ProfileLock(string path) => _path = path;

    public static ProfileLock TryAcquire(string profileId)
    {
        Directory.CreateDirectory(ProfileStore.ProfilesDirectory);
        var path = Path.Combine(ProfileStore.ProfilesDirectory, $"{profileId}.lock");
        if (File.Exists(path))
        {
            var pid = ReadPid(path);
            if (pid > 0 && ProcessAlive(pid))
            {
                Log.Information("Profile {Id} is already locked by process {Pid}", profileId, pid);
                return null;
            }
            // 残留锁：清理后继续
            try { File.Delete(path); } catch (Exception ex) { Log.Warning(ex, "Could not delete stale lock {Path}", path); }
        }

        File.WriteAllText(path, Environment.ProcessId.ToString());
        return new ProfileLock(path);
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
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { Log.Warning(ex, "Could not delete lock {Path}", _path); }
    }

    private static int ReadPid(string path)
    {
        try { return int.Parse(File.ReadAllText(path).Trim()); }
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
