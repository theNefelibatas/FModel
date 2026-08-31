using System;
using FModel.Framework;
using FModel.Settings;
using Xunit;

namespace FModel.Tests;

public class GameProfileTests : IDisposable
{
    private readonly UserSettings _origDefault;

    public GameProfileTests()
    {
        /* ApplyToCurrent 会整体替换 UserSettings.Default：每例测完复位，避免污染并行集合 */
        _origDefault = UserSettings.Default;
    }

    public void Dispose()
    {
        UserSettings.Default = _origDefault;
    }

    [Fact]
    public void FromSettings_CapturesGameAndSettings()
    {
        var settings = new UserSettings { TextureQuality = 55, OutputDirectory = "<导出目录占位>" };
        var game = new DirectorySettings { GameName = "示例游戏", GameDirectory = "<游戏目录>", UeVersion = CUE4Parse.UE4.Versions.EGame.GAME_UE4_26 };
        game.AesKeys = new FModel.ViewModels.ApiEndpoints.Models.AesResponse { MainKey = "0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" };
        var era = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

        var profile = GameProfile.FromSettings("a1b2c3", game, settings);
        profile.CreatedAt = era;
        profile.LastUsedAt = era;

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(profile, JsonNetSerializer.SerializerSettings);
        var round = Newtonsoft.Json.JsonConvert.DeserializeObject<GameProfile>(json, JsonNetSerializer.SerializerSettings);

        Assert.Equal("a1b2c3", round.Id);
        Assert.Equal(era, round.LastUsedAt);
        Assert.Equal("示例游戏", round.Game.GameName);
        Assert.Equal(game.AesKeys.MainKey, round.Game.AesKeys.MainKey);
        Assert.Equal(55, round.Settings.TextureQuality);
        Assert.Equal("<导出目录占位>", round.Settings.OutputDirectory);
    }

    [Fact]
    public void ApplyToCurrent_SetsDefaultAndCurrentDir()
    {
        var settings = new UserSettings { TextureQuality = 77 };
        var game = new DirectorySettings { GameName = "示例游戏", GameDirectory = "<游戏目录>" };
        var profile = GameProfile.FromSettings("a1b2c3", game, settings);

        profile.ApplyToCurrent();

        // 深拷贝：再改 profile 不影响已装载的
        profile.Game.GameDirectory = "<另一目录>";
        Assert.Equal("<游戏目录>", UserSettings.Default.CurrentDir.GameDirectory);
        // 设置路径的深拷贝验证：验证后再改 profile 不影响已装载的
        profile.Settings.TextureQuality = 88;
        Assert.Equal(77, UserSettings.Default.TextureQuality);
    }
}
