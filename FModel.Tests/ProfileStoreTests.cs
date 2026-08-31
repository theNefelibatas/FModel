using System;
using System.IO;
using FModel.Settings;
using Xunit;

namespace FModel.Tests;

[Collection("ProfileStoreTmp")]
public class ProfileStoreTests : IDisposable
{
    private readonly string _origDir;

    public ProfileStoreTests()
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

    private static GameProfile NewProfile(string name, string dir)
    {
        var g = new DirectorySettings { GameName = name, GameDirectory = dir };
        return GameProfile.FromSettings(ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(dir)), g, new UserSettings());
    }

    [Fact]
    public void ComputeId_NormalizesSeparatorsAndCase()
    {
        var id1 = ProfileStore.ComputeId(ProfileStore.NormalizeDirectory("D:\\Games\\示例游戏\\Content\\Paks\\"));
        var id2 = ProfileStore.ComputeId(ProfileStore.NormalizeDirectory("d:/games/示例游戏/content/paks"));
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var p = NewProfile("示例游戏", "<游戏目录>");
        p.Settings.TextureQuality = 42;
        ProfileStore.Save(p);

        Assert.True(ProfileStore.ExistsById(p.Id));
        Assert.True(ProfileStore.TryLoad(p.Id, out var loaded, out var err));
        Assert.Null(err);
        Assert.Equal("示例游戏", loaded.Game.GameName);
        Assert.Equal(42, loaded.Settings.TextureQuality);
    }

    [Fact]
    public void Create_SameNormalizedDir_ReusesExistingProfile()
    {
        var p1 = NewProfile("示例游戏", "D:\\Games\\G1\\Content\\Paks");
        var saved = ProfileStore.Create(p1);

        var p2 = NewProfile("示例游戏", "d:/games/g1/content/paks/");
        var reuse = ProfileStore.Create(p2);

        Assert.Equal(saved.Id, reuse.Id);
        Assert.Equal(p1.Game.GameDirectory, reuse.Game.GameDirectory);
        // 计数：目录里只有一个档案文件
        Assert.Single(Directory.GetFiles(ProfileStore.ProfilesDirectory, "*.json"));
    }

    [Fact]
    public void Enumerate_SkipsCorruptFilesAndReportsThem()
    {
        var p = NewProfile("示例游戏", "D:\\Games\\G1");
        ProfileStore.Save(p);
        File.WriteAllText(ProfileStore.FilePathById(p.Id), "{ not json !!");

        var profiles = ProfileStore.Enumerate(out var corrupt);

        Assert.Empty(profiles);
        Assert.Single(corrupt);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var p = NewProfile("示例游戏", "D:\\Games\\G1");
        ProfileStore.Save(p);
        ProfileStore.Delete(p.Id);
        Assert.False(ProfileStore.ExistsById(p.Id));
    }

    [Fact]
    public void Save_WorksWhenProfilesDirectoryMissing()
    {
        Directory.Delete(ProfileStore.ProfilesDirectory, true);
        var p = NewProfile("示例游戏", "D:\\Games\\G_Missing");
        ProfileStore.Save(p); // 不得抛异常：目录缺失视同"首次创建"
        Assert.True(ProfileStore.ExistsById(p.Id));
        Assert.True(ProfileStore.TryLoad(p.Id, out _, out _));
        ProfileStore.Delete(p.Id); // 同样不得抛
    }

    [Fact]
    public void Save_EmptyGameName_StillLoadable()
    {
        var p = NewProfile("", "D:\\Games\\G_NoName"); // 空名触发 BuildFileName 回退分支
        ProfileStore.Save(p);
        Assert.True(ProfileStore.TryLoad(p.Id, out var loaded, out _));
        Assert.Empty(loaded.Game.GameName);
        Assert.Equal("D:\\Games\\G_NoName", loaded.Game.GameDirectory);
    }

    [Fact]
    public void Save_EmptyAesDoesNotOverwriteExistingKeys()
    {
        var p = NewProfile("示例游戏", "D:\\Games\\G_Aes");
        p.Game.AesKeys = new FModel.ViewModels.ApiEndpoints.Models.AesResponse
        {
            MainKey = "0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
            DynamicKeys = new System.Collections.Generic.List<FModel.ViewModels.ApiEndpoints.Models.DynamicKey>
            {
                new() { Key = "0x1111111111111111111111111111111111111111111111111111111111111111", Name = "动态占位", Guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
            }
        };
        ProfileStore.Save(p);
        /* 保存后（第一次）：主键 + 动态键都在 */
        ProfileStore.TryLoad(p.Id, out var saved, out _);
        Assert.Equal("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", saved.Game.AesKeys.MainKey);
        Assert.NotNull(saved.Game.AesKeys.DynamicKeys);
        Assert.Single(saved.Game.AesKeys.DynamicKeys);
        Assert.Equal("0x1111111111111111111111111111111111111111111111111111111111111111", saved.Game.AesKeys.DynamicKeys[0].Key);
        Assert.Equal("动态占位", saved.Game.AesKeys.DynamicKeys[0].Name);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", saved.Game.AesKeys.DynamicKeys[0].Guid);

        // 模拟一次"空密钥"保存（例如远端 API 返回空集后）
        p.Game.AesKeys = new FModel.ViewModels.ApiEndpoints.Models.AesResponse();
        ProfileStore.Save(p);

        /* 空密钥覆盖路径：主键与动态键都不得被清掉 */
        ProfileStore.TryLoad(p.Id, out var loaded, out _);
        Assert.Equal("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", loaded.Game.AesKeys.MainKey);
        Assert.NotNull(loaded.Game.AesKeys.DynamicKeys);
        Assert.Single(loaded.Game.AesKeys.DynamicKeys);
        Assert.Equal("0x1111111111111111111111111111111111111111111111111111111111111111", loaded.Game.AesKeys.DynamicKeys[0].Key);
    }
}
