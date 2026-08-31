using System;
using System.IO;
using System.Linq;
using FModel.Settings;
using FModel.ViewModels;
using Xunit;

namespace FModel.Tests;

/* 与 ProfileStoreTests/ProfileLockTests/MigrationTests 同一集合：三类都互换
 * ProfileStore.ProfilesDirectory 静态，xUnit 默认并行会互相踩。 */
[Collection("ProfileStoreTmp")]
public class LauncherViewModelTests : IDisposable
{
    private readonly string _origDir;

    public LauncherViewModelTests()
    {
        _origDir = ProfileStore.ProfilesDirectory;
        ProfileStore.ProfilesDirectory = Path.Combine(Path.GetTempPath(), "FModelTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(ProfileStore.ProfilesDirectory, true); } catch { }
        ProfileStore.ProfilesDirectory = _origDir;
    }

    private static GameProfile NewProfile(string name, string dir)
        => GameProfile.FromSettings(
            ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(dir)),
            new DirectorySettings { GameName = name, GameDirectory = dir },
            new UserSettings());

    [Fact]
    public void Refresh_FlagsMissingDirectoriesAndIgnoresLiveTriggers()
    {
        var realDir = Path.Combine(ProfileStore.ProfilesDirectory, "exists");
        Directory.CreateDirectory(realDir);
        var goneDir = Path.Combine(ProfileStore.ProfilesDirectory, "gone");
        const string liveTrigger = "valorant-live.manifest"; // 非绝对路径，LIVE 触发串

        ProfileStore.Save(NewProfile("示例游戏", realDir));
        ProfileStore.Save(NewProfile("示例游戏", goneDir));
        ProfileStore.Save(NewProfile("Valorant [LIVE]", liveTrigger));

        var vm = new LauncherViewModel();
        var byId = vm.Profiles.ToDictionary(x => x.Id);

        Assert.False(byId[ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(realDir))].IsDirectoryMissing);
        Assert.True(byId[ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(goneDir))].IsDirectoryMissing);
        Assert.False(byId[ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(liveTrigger))].IsDirectoryMissing);
    }
}
