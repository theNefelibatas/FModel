using System;
using System.IO;
using System.Linq;
using FModel.Settings;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FModel.Tests;

[Collection("ProfileStoreTmp")]
public class MigrationTests : IDisposable
{
    private readonly string _origDir;

    public MigrationTests()
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

    private static string WriteLegacy(string gameDirectory)
    {
        var legacy = new JObject
        {
            ["GameDirectory"] = gameDirectory,
            ["TextureQuality"] = 88,
            ["PerDirectory"] = new JObject
            {
                [gameDirectory] = new JObject
                {
                    ["GameName"] = "示例游戏",
                    ["GameDirectory"] = gameDirectory,
                    ["IsManual"] = true,
                    ["UeVersion"] = (int) CUE4Parse.UE4.Versions.EGame.GAME_UE4_26,
                    ["AesKeys"] = new JObject
                    {
                        ["MainKey"] = "0x1111111111111111111111111111111111111111111111111111111111111111"
                    }
                }
            }
        };
        /* 临时文件建在本实例自己的子目录里（与 ProfilesDirectory 同目录），不再污染 GetTempPath() 根 */
        var path = Path.Combine(ProfileStore.ProfilesDirectory, $"legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, legacy.ToString());
        return path;
    }

    [Fact]
    public void MigrateLegacy_CreatesProfilePerDirectoryAndBacksUp()
    {
        var dir = "D:\\Games\\示例游戏\\Content\\Paks";
        var legacy = WriteLegacy(dir);
        var legacyName = Path.GetFileNameWithoutExtension(legacy);

        var profiles = ProfileStore.MigrateLegacy(legacy);

        Assert.True(profiles.Count >= 1);
        var p = profiles.Single();
        Assert.Equal("示例游戏", p.Game.GameName);
        Assert.Equal(dir, p.Game.GameDirectory);
        Assert.Equal("0x1111111111111111111111111111111111111111111111111111111111111111", p.Game.AesKeys.MainKey);
        Assert.Equal(88, p.Settings.TextureQuality); // 旧全局值进档案
        Assert.True(p.LastUsedAt > DateTime.Now.AddMinutes(-5)); // 旧"当前游戏"归档为最近使用
        Assert.Single(Directory.GetFiles(ProfileStore.ProfilesDirectory, $"{legacyName}_Legacy-*.json")); // 备份成功
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void MigrateLegacy_NoPerDirectory_ReturnsEmpty()
    {
        var legacy = Path.Combine(ProfileStore.ProfilesDirectory, $"legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(legacy, new JObject { ["GameDirectory"] = "D:\\Games\\示例游戏" }.ToString());

        var profiles = ProfileStore.MigrateLegacy(legacy);

        Assert.Empty(profiles);
        Assert.True(File.Exists(legacy)); // 无 PerDirectory：不迁移也不备份
        Assert.Empty(Directory.GetFiles(ProfileStore.ProfilesDirectory, $"{Path.GetFileNameWithoutExtension(legacy)}_Legacy-*.json"));
    }
}
