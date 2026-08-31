using System;
using FModel.Framework;
using Newtonsoft.Json;

namespace FModel.Settings;

/* 游戏档案：一个游戏一个文件。Settings 是 UserSettings 的全量快照（不含全局静态/运行时字段），
 * Game 是目录级字段快照。加载/保存经 JSON 序列化（深拷贝）：两快照互不共享对象引用。 */
[JsonObject(MemberSerialization.OptOut)]
public sealed class GameProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
    public DirectorySettings Game { get; set; } = new DirectorySettings();
    public UserSettings Settings { get; set; } = new UserSettings();

    public static GameProfile FromCurrent(string id, DirectorySettings game)
        => FromSettings(id, game, UserSettings.Default);

    public static GameProfile FromSettings(string id, DirectorySettings game, UserSettings settings)
        => new GameProfile
        {
            Id = id,
            CreatedAt = DateTime.Now,
            LastUsedAt = DateTime.Now,
            Game = DeepClone(game),
            Settings = DeepClone(settings)
        };

    /* 装载进运行时全局：UserSettings.Default + CurrentDir。 */
    public void ApplyToCurrent()
    {
        UserSettings.Default = DeepClone(Settings);
        UserSettings.Default.CurrentDir = DeepClone(Game);
    }

    /* JSON 往返深拷贝：两个快照与运行时状态互不共享对象引用。 */
    public static T DeepClone<T>(T obj)
        => obj == null ? default : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(obj, JsonNetSerializer.SerializerSettings), JsonNetSerializer.SerializerSettings);
}
