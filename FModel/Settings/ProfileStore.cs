using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FModel.Framework;
using FModel.ViewModels.ApiEndpoints.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FModel.Settings;

/* 游戏档案存取。一个游戏一个文件（Profiles/<名称>_<Id>.json）。
 * Id 由规范化目录哈希而来：同目录 = 同档案（I3）；目录变了自然产生新档案，旧档案手动删除。
 * 只允许写「当前进程负责」的档案（由调用方保证，App 层强制）。 */
public static class ProfileStore
{
    public static string ProfilesDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "Profiles");

    /* 路径比较前归一化（分隔符 + 大小写）：同一路径的不同写法映射到同一 Id。 */
    public static string NormalizeDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return string.Empty;
        var trimmed = gameDirectory.TrimEnd('\\', '/').ToLowerInvariant();
        /* 盘符根："D:\" 裁剪后剩 "D:"，GetFullPath 会按盘符当前目录解析 → 补回 '\'，同一盘根总得到同一路径。 */
        if (trimmed.Length == 2 && trimmed[1] == ':') trimmed += '\\';
        return Path.GetFullPath(trimmed);
    }

    public static string ComputeId(string normalizedDirectory)
    {
        byte[] hash;
        using (var sha = SHA1.Create())
            hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedDirectory));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static readonly Regex InvalidFileNameChars = new Regex(@"[<>:""/\\|?*\x00-\x1F]");

    public static string SanitizeFileName(string name)
        => InvalidFileNameChars.Replace(name ?? "", "_").Trim();

    public static string BuildFileName(string gameName, string id)
    {
        var name = SanitizeFileName(gameName);
        return string.IsNullOrEmpty(name) ? $"{id}.json" : $"{name}_{id}.json";
    }

    public static string FilePathById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        /* 空名回退分支写入的是裸 <id>.json，先精确匹配；目录缺失视同"无此档案"。 */
        var exact = Path.Combine(ProfilesDirectory, $"{id}.json");
        if (File.Exists(exact)) return exact;
        try
        {
            return Directory.GetFiles(ProfilesDirectory, $"*_{id}.json").FirstOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static bool ExistsById(string id) => FilePathById(id) != null;

    public static bool TryLoad(string id, out GameProfile profile, out string error)
    {
        profile = null;
        error = null;
        var path = FilePathById(id);
        if (path == null)
        {
            error = $"profile '{id}' not found in {ProfilesDirectory}";
            return false;
        }

        try
        {
            profile = JsonConvert.DeserializeObject<GameProfile>(File.ReadAllText(path), JsonNetSerializer.SerializerSettings);
            if (profile == null || profile.SchemaVersion != 1)
            {
                error = $"profile '{path}' has unsupported schema version";
                profile = null;
                return false;
            }
            if (profile.Game == null || profile.Settings == null)
            {
                error = $"profile '{path}' is missing Game/Settings";
                profile = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"profile '{path}' could not be read: {ex.Message}";
            return false;
        }
    }

    public static GameProfile Load(string id)
    {
        if (!TryLoad(id, out var profile, out var error))
            throw new InvalidOperationException(error);
        return profile;
    }

    public static IReadOnlyList<GameProfile> Enumerate(out IReadOnlyList<string> corruptFiles)
    {
        Directory.CreateDirectory(ProfilesDirectory);
        var profiles = new List<GameProfile>();
        var corrupt = new List<string>();
        foreach (var file in Directory.GetFiles(ProfilesDirectory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var pos = id.LastIndexOf('_');
            if (pos > 0) id = id[(pos + 1)..];
            if (TryLoad(id, out var profile, out _))
                profiles.Add(profile);
            else
                corrupt.Add(file);
        }
        corruptFiles = corrupt;
        return profiles;
    }

    public static GameProfile Create(GameProfile profile)
    {
        var normalized = NormalizeDirectory(profile.Game.GameDirectory);
        if (string.IsNullOrEmpty(normalized))
            throw new InvalidOperationException("profile has no game directory");
        var existing = Enumerate(out _).FirstOrDefault(p => NormalizeDirectory(p.Game.GameDirectory) == normalized);
        if (existing != null)
        {
            /* 同目录复用既有档案：只更新元数据，不动已存设置（AES 等）。
             * 运行中的游戏档案由游戏进程锁持有，此处不重写。 */
            if (!ProfileLock.IsHeldByAliveProcess(existing.Id))
            {
                existing.LastUsedAt = DateTime.Now;
                Save(existing);
            }
            return existing;
        }

        Save(profile);
        return profile;
    }

    /* 只允许当前档案写入；空密钥不得覆盖既有密钥。 */
    public static void Save(GameProfile profile)
    {
        if (string.IsNullOrEmpty(profile.Id))
            throw new InvalidOperationException("profile must have an id before saving");

        /* 目录缺失视同"首次创建"：先建目录，FilePathById/读盘才安全。 */
        Directory.CreateDirectory(ProfilesDirectory);

        var path = FilePathById(profile.Id);
        if (path != null && IsAesEmpty(profile.Game.AesKeys))
        {
            try
            {
                var onDisk = JsonConvert.DeserializeObject<GameProfile>(File.ReadAllText(path), JsonNetSerializer.SerializerSettings);
                if (onDisk?.Game.AesKeys is { } diskAes && !IsAesEmpty(diskAes))
                {
                    Log.Warning("Save: profile {Id} has empty AES keys, keeping existing keys", profile.Id);
                    profile.Game.AesKeys = diskAes;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Save: could not read existing profile {Id}, overwriting", profile.Id);
            }
        }

        var target = path ?? Path.Combine(ProfilesDirectory, BuildFileName(profile.Game.GameName, profile.Id));
        File.WriteAllText(target, JsonConvert.SerializeObject(profile, Formatting.Indented, JsonNetSerializer.SerializerSettings));
    }

    public static void Delete(string id)
    {
        var path = FilePathById(id);
        if (path == null) return;
        Log.Information("Deleted game profile {Id} ({File})", id, path);
        File.Delete(path);
    }

    private static bool IsAesEmpty(AesResponse aes)
        => aes == null || string.IsNullOrWhiteSpace(aes.MainKey) && !aes.HasDynamicKeys;

    /* 一次性迁移旧 AppSettings.json：每个 PerDirectory 条目 → 一个档案；
     * 旧全局值作为档案 Settings 初始值；源文件改名备份后不再读取。
     * 用 JObject 先剥掉 PerDirectory/GameDirectory 再 ToObject<UserSettings>：
     * 即使 UserSettings 对这两属性标了 [JsonIgnore]（T6），迁移依旧能读。 */
    public static IReadOnlyList<GameProfile> MigrateLegacy(string legacyJsonPath)
    {
        if (!File.Exists(legacyJsonPath)) return Array.Empty<GameProfile>();

        var root = JObject.Parse(File.ReadAllText(legacyJsonPath));
        var perDirectory = root["PerDirectory"] as JObject;
        if (perDirectory == null) return Array.Empty<GameProfile>();

        var gameDirectory = root["GameDirectory"]?.ToString() ?? string.Empty;
        root.Remove("PerDirectory");
        root.Remove("GameDirectory");
        var legacySerializer = JsonSerializer.Create(JsonNetSerializer.SerializerSettings);
        var global = root.ToObject<UserSettings>(legacySerializer) ?? new UserSettings();

        var profiles = new List<GameProfile>();
        var skipped = 0;
        foreach (var property in perDirectory.Properties())
        {
            if (property.Value is not JObject entryObj)
            {
                skipped++;
                continue;
            }

            DirectorySettings game;
            try
            {
                game = entryObj.ToObject<DirectorySettings>(legacySerializer);
            }
            catch (JsonSerializationException ex)
            {
                skipped++;
                Log.Warning(ex, "Migration: skipping corrupt legacy entry {Key}", property.Name);
                continue;
            }
            if (game == null || string.IsNullOrEmpty(game.GameDirectory)) continue;

            var id = ComputeId(NormalizeDirectory(game.GameDirectory));
            if (ExistsById(id)) continue; // 已迁移过

            var profile = GameProfile.FromSettings(id, game, global);
            if (!string.IsNullOrEmpty(gameDirectory) &&
                NormalizeDirectory(game.GameDirectory) == NormalizeDirectory(gameDirectory))
                profile.LastUsedAt = DateTime.Now; // 旧"当前游戏"归档为最近使用（CLI 缺省档）

            Save(profile);
            profiles.Add(profile);
        }

        var backup = Path.Combine(
            Path.GetDirectoryName(legacyJsonPath) ?? ".",
            $"{Path.GetFileNameWithoutExtension(legacyJsonPath)}_Legacy-{DateTime.Now:yyyyMMddHHmmss}.json");
        File.Move(legacyJsonPath, backup);
        Log.Information("Migrated {Count} game profile(s), skipped {Skipped}, backup at {Backup}", profiles.Count, skipped, backup);
        return profiles;
    }
}
