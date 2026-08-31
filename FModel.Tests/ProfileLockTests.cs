using System;
using System.Diagnostics;
using System.IO;
using FModel.Settings;
using Xunit;

namespace FModel.Tests;

[Collection("ProfileStoreTmp")]
public class ProfileLockTests : IDisposable
{
    private readonly string _origDir;

    public ProfileLockTests()
    {
        _origDir = ProfileStore.ProfilesDirectory;
        ProfileStore.ProfilesDirectory = Path.Combine(Path.GetTempPath(), "FModelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ProfileStore.ProfilesDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(ProfileStore.ProfilesDirectory, true); } catch { }
        ProfileStore.ProfilesDirectory = _origDir;
    }

    [Fact]
    public void TryAcquire_SameId_FailsSecondTime_UntilDispose()
    {
        using var first = ProfileLock.TryAcquire("abc");
        Assert.NotNull(first);
        Assert.Null(ProfileLock.TryAcquire("abc"));
        first.Dispose();
        using var second = ProfileLock.TryAcquire("abc");
        Assert.NotNull(second);
    }

    [Fact]
    public void IsHeldByAliveProcess_TrueOnlyWithLivePid()
    {
        using var l = ProfileLock.TryAcquire("abc");
        Assert.True(ProfileLock.IsHeldByAliveProcess("abc"));

        l.Dispose();
        Assert.False(ProfileLock.IsHeldByAliveProcess("abc"));

        // 伪残留锁：填一个必然不存在的 PID
        File.WriteAllText(Path.Combine(ProfileStore.ProfilesDirectory, "abc.lock"), "2147483647");
        Assert.False(ProfileLock.IsHeldByAliveProcess("abc"));
        using var after = ProfileLock.TryAcquire("abc"); // 残留自愈
        Assert.NotNull(after);
    }
}
