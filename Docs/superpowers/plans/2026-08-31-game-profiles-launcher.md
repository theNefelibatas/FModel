# 游戏档案 + 启动器（Game Profiles & Launcher）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 FModel 改为「启动器 + 一游戏一进程」模型：每个游戏一份档案（目录/UE 版本/AES + 全部可手动设置字段），双击 exe 进入启动器，选游戏即开新进程，切换永不重启、AES 永不丢失。

**Architecture:** 单 exe 按参数分派（`--game=<Id>` / `--cli` / 默认启动器）；档案 = 一游戏一个 JSON（`%AppData%\FModel\Profiles\`），运行时仍装在现有全局 `UserSettings.Default` + `CurrentDir` 上（改加载/保存层，不改视图层）；启动器单实例（命名互斥量 + 广播激活），同游戏单实例（PID 锁文件）。

**Tech Stack:** C# / .NET 10 / WPF (AdonisUI) / Newtonsoft.Json / xUnit（新增测试工程）。全部在 FModel 主仓库内，不触碰 CUE4Parse submodule。

**Spec:** `Docs/superpowers/specs/2026-08-31-game-profiles-launcher-design.md`

## Global Constraints

- **提交必须经用户确认**（AGENTS.md 十：禁止自动提交）。各任务末尾的「提交」步骤统一改为：只 `git add` 列出的文件，然后**停下来问用户是否可以提交**；用户点头才 `git commit`。提交信息不加 `Co-Authored-By`。
- **无硬编码**：路径一律 `Environment.SpecialFolder` / `Path.Combine` 组装；测试与示例不写盘符、不写真实游戏名（用 `示例游戏` / `<游戏目录>`）。
- **路径比较前归一化**（分隔符 + 大小写，AGENTS 五 / 不变式 I3）。
- **跨进程传参不传路径**（AGENTS 六）：`--game=<Id>` 只传纯 token。
- **命名宽度=职责宽度**（AGENTS 四）：`GameProfile` / `ProfileStore` / `ProfileLock` / `LauncherWindow` / `LauncherViewModel`。
- upstream 文件只改必要处，沿用相邻代码风格，diff 最小（AGENTS 11）；fork 新增代码注释用中文。
- 测试要点：xUnit 测试不弹任何 UI、不写 `%AppData%` 真实目录（ProfileStore 的根目录允许测试注入口：`ProfileStore.ProfilesDirectory` 可被测试替换，见 T2）。
- CliExporter 文档注释（文件头）必须与最终行为一致，改行为必须同步改注释。
- 参数形式以本计划为准：`--game=<Id>`（等号形式，与既有 `-ExportXxx=` 风格一致；spec 中的空格写法以此为准）。

### 最终公开接口一览（跨任务引用，改签名必须同步这里）

```csharp
// T1: FModel/Settings/GameProfile.cs
public sealed class GameProfile
{
    public static GameProfile FromCurrent(string id, DirectorySettings game);
    public static GameProfile FromSettings(string id, DirectorySettings game, UserSettings settings);
    public void ApplyToCurrent();   // UserSettings.Default = DeepClone(Settings); UserSettings.Default.CurrentDir = DeepClone(Game)
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
    public DirectorySettings Game { get; set; }
    public UserSettings Settings { get; set; }
}

// T2: FModel/Settings/ProfileStore.cs
public static class ProfileStore
{
    public static string ProfilesDirectory { get; }
    public static string NormalizeDirectory(string gameDirectory);
    public static string ComputeId(string normalizedDirectory);
    public static string BuildFileName(string gameName, string id);
    public static string FilePathById(string id);                            // 找不到返回 null
    public static bool ExistsById(string id);
    public static bool TryLoad(string id, out GameProfile profile, out string error);
    public static GameProfile Load(string id);                              // 失败抛异常（调用方接）
    public static IReadOnlyList<GameProfile> Enumerate(out IReadOnlyList<string> corruptFiles);
    public static GameProfile Create(GameProfile profile);                  // I3：同目录已有 → 返回既有档案
    public static void Save(GameProfile profile);                           // I2：空 AES 不覆盖
    public static void Delete(string id);
    public static IReadOnlyList<GameProfile> MigrateLegacy(string legacyJsonPath);
}

// T3: FModel/Settings/ProfileLock.cs
public sealed class ProfileLock : IDisposable
{
    public static ProfileLock TryAcquire(string profileId);                 // 失败返回 null
    public static bool IsHeldByAliveProcess(string profileId);
}

// T5
public static class ProfileStore
{
    public static IReadOnlyList<GameProfile> MigrateLegacy(string legacyJsonPath); // 返回迁移出的档案；后端改名备份
}

// T6: UserSettings 新增
[JsonIgnore] public string CurrentProfileId { get; set; }

// T9: ApplicationViewModel
public void BackToLauncher();
public static void LaunchProfileProcess(string profileId);
public DirectorySettings AddGameDirectory(string directory);                 // 新语义：建/复用档案 + 起进程，不再弹窗

// T12: CliRequest 新增
public string GameId;
```

---

### Task 1：GameProfile 模型 + 测试工程脚手架

**Files:**
- Create: `FModel.Tests/FModel.Tests.csproj`
- Create: `FModel.Tests/GameProfileTests.cs`
- Create: `FModel/Settings/GameProfile.cs`

**Interfaces:**
- Consumes: `DirectorySettings`（`FModel/Settings/DirectorySettings.cs`）、`UserSettings`（`FModel/Settings/UserSettings.cs`）、`FModel.Framework.JsonNetSerializer.SerializerSettings`。
- Produces: `GameProfile`（见上表）。

- [ ] **Step 1: 建测试工程**

`FModel.Tests/FModel.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PlatformTarget>x64</PlatformTarget>
    <IsPackable>false</IsPackable>
    <Nullable>disable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FModel\FModel.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 写失败测试**

`FModel.Tests/GameProfileTests.cs`：

```csharp
using System;
using FModel.Framework;
using FModel.Settings;
using Xunit;

namespace FModel.Tests;

public class GameProfileTests
{
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

        Assert.NotNull(UserSettings.Default);
        Assert.Equal(77, UserSettings.Default.TextureQuality);
        Assert.Equal("<游戏目录>", UserSettings.Default.CurrentDir.GameDirectory);
        // 深拷贝：再改 profile 不影响已装载的
        profile.Game.GameDirectory = "<另一目录>";
        Assert.Equal("<游戏目录>", UserSettings.Default.CurrentDir.GameDirectory);
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj -v m`
Expected: 编译失败（GameProfile 不存在）。

- [ ] **Step 4: 写 GameProfile**

`FModel/Settings/GameProfile.cs`：

```csharp
using System;
using FModel.Extensions;
using FModel.Framework;
using Newtonsoft.Json;

namespace FModel.Settings;

/* 游戏档案：一个游戏一个文件。Settings 是 UserSettings 的全量快照（不含全局静态/运行时字段），
 * Game 是目录级字段快照。加载/保存经过 JSON 序列化——天然深拷贝，上游浅拷贝共享引用的问题从根上杜绝。 */
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

    /* JSON 往返深拷贝：杜绝上游浅拷贝/共享引用引发的相互覆盖（I5）。 */
    public static T DeepClone<T>(T obj)
        => obj == null ? default : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(obj, JsonNetSerializer.SerializerSettings), JsonNetSerializer.SerializerSettings);
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj -v m`
Expected: PASS（2 个用例）。同时确认主工程仍能编译：`dotnet build FModel/FModel.csproj`。

- [ ] **Step 6: 请求用户确认后提交**

`git add FModel.Tests/FModel.Tests.csproj FModel.Tests/GameProfileTests.cs FModel/Settings/GameProfile.cs`
Commit message: `feat: add game profile model and test scaffold`

---

### Task 2：ProfileStore 核心（路径/Id/枚举/读/写/删）

**Files:**
- Create: `FModel/Settings/ProfileStore.cs`
- Create: `FModel.Tests/ProfileStoreTests.cs`

**Interfaces:**
- Consumes: `GameProfile`（T1）、`DirectorySettings`。
- Produces: `ProfileStore` 静态类（见上表）；`ProfilesDirectory` 属性。

关键设计：
- `ProfilesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "Profiles")`。
- Id = `NormalizeDirectory` 后 UTF8 SHA-1 的前 16 位十六进制小写。
- 文件名 = `BuildFileName`：sanitize 名称 + `_` + Id；`FilePathById` 用通配 `*_<id>.json`。
- 测试注入：`ProfilesDirectory` 为 `{ get; }` 属性，默认值上面那样；测试用 `public static string OverrideProfilesDirectoryForTests`（静态字段，测试用例里设置后恢复）。

- [ ] **Step 1: 写失败测试**

`FModel.Tests/ProfileStoreTests.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using FModel.Framework;
using FModel.Settings;
using Xunit;

namespace FModel.Tests;

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
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj --filter ProfileStoreTests -v m`
Expected: 编译失败（ProfileStore 不存在）。

- [ ] **Step 3: 实现 ProfileStore 核心**

`FModel/Settings/ProfileStore.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FModel.Framework;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Settings;

/* 游戏档案存取。一个游戏一个文件（Profiles/<名称>_<Id>.json）。
 * Id 由规范化目录哈希而来：同目录 = 同档案（I3）；目录变了自然产生新档案，旧档案手动删除。
 * 只允许写「当前进程负责」的档案（由调用方保证，App 层强制）。 */
public static class ProfileStore
{
    public static string ProfilesDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "Profiles");

    /* I3：跨进程比较路径前归一化（分隔符 + 大小写）。 */
    public static string NormalizeDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return string.Empty;
        return Path.GetFullPath(gameDirectory.TrimEnd('\\', '/').ToLowerInvariant());
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
        var exact = Path.Combine(ProfilesDirectory, $"{id}.json");
        if (File.Exists(exact)) return exact; // 空名回退文件（BuildFileName 回退分支），否则通配永不命中
        try
        {
            return Directory.GetFiles(ProfilesDirectory, $"*_{id}.json").FirstOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null; // 目录尚未创建：等价于"档案不存在"（TryLoad false / Delete 无操作 / Save 重建）
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
            /* I3：同目录 → 复用既有档案，只更新元数据，不动已存设置（AES 等） */
            existing.LastUsedAt = DateTime.Now;
            Save(existing);
            return existing;
        }

        Save(profile);
        return profile;
    }

    /* 只允许当前档案写入；空密钥不得覆盖既有密钥（I2）。 */
    public static void Save(GameProfile profile)
    {
        if (string.IsNullOrEmpty(profile.Id))
            throw new InvalidOperationException("profile must have an id before saving");

        Directory.CreateDirectory(ProfilesDirectory); // 首次使用时目录尚不存在
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
        if (path != null) File.Delete(path);
    }

    private static bool IsAesEmpty(AesResponse aes)
        => aes == null || string.IsNullOrWhiteSpace(aes.MainKey) && !aes.HasDynamicKeys;
}
```

（顶部补引用：`using FModel.ViewModels.ApiEndpoints.Models;` 因为 `AesResponse` 在该命名空间。）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj --filter ProfileStoreTests -v m`
Expected: PASS（5 个用例）。

- [ ] **Step 5: 请求用户确认后提交**

`git add FModel/Settings/ProfileStore.cs FModel.Tests/ProfileStoreTests.cs`
Commit message: `feat: add profile store with directory-normalized ids and corrupt handling`

---

### Task 3：ProfileLock（同游戏单实例）

**Files:**
- Create: `FModel/Settings/ProfileLock.cs`
- Create: `FModel.Tests/ProfileLockTests.cs`

**Interfaces:**
- Consumes: `ProfileStore.ProfilesDirectory`。
- Produces: `ProfileLock`（见上表）。锁文件：`Profiles/<id>.lock`，内容 = 进程 PID；干净退出删除，崩溃残留靠 PID 存活判断自愈。

- [ ] **Step 1: 写失败测试**

`FModel.Tests/ProfileLockTests.cs`：

```csharp
using System;
using System.Diagnostics;
using System.IO;
using FModel.Settings;
using Xunit;

namespace FModel.Tests;

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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj --filter ProfileLockTests -v m`
Expected: 编译失败（ProfileLock 不存在）。

- [ ] **Step 3: 实现 ProfileLock**

`FModel/Settings/ProfileLock.cs`：

```csharp
using System;
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
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj --filter ProfileLockTests -v m`
Expected: PASS（2 个用例）。

- [ ] **Step 5: 请求用户确认后提交**

`git add FModel/Settings/ProfileLock.cs FModel.Tests/ProfileLockTests.cs`
Commit message: `feat: add per-profile process lock`

---

### Task 4：AES 不丢保底（I2）单测补充

**Files:**
- Modify: `FModel.Tests/ProfileStoreTests.cs`（追加用例）

**Interfaces:**
- Consumes: `ProfileStore.Save`（T2，I2 已实现）。
- Produces: 无新接口；锁定 I2 行为。

- [ ] **Step 1: 追加测试用例**

在 `ProfileStoreTests` 内追加：

```csharp
    [Fact]
    public void Save_EmptyAesDoesNotOverwriteExistingKeys()
    {
        var p = NewProfile("示例游戏", "D:\\Games\\G_Aes");
        p.Game.AesKeys = new FModel.ViewModels.ApiEndpoints.Models.AesResponse
        {
            MainKey = "0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
        };
        ProfileStore.Save(p);

        // 模拟一次"空密钥"保存（例如远端 API 返回空集后）
        p.Game.AesKeys = new FModel.ViewModels.ApiEndpoints.Models.AesResponse();
        ProfileStore.Save(p);

        ProfileStore.TryLoad(p.Id, out var loaded, out _);
        Assert.Equal("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", loaded.Game.AesKeys.MainKey);
    }
```

- [ ] **Step 2: 跑测试**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj --filter "Save_EmptyAesDoesNotOverwriteExistingKeys" -v m`
Expected: PASS（若失败，说明 T2 的 I2 有 bug，先修再交）。

- [ ] **Step 3: 请求用户确认后提交**

`git add FModel.Tests/ProfileStoreTests.cs`
Commit message: `test: assert empty AES never overwrites stored keys`

---

### Task 5：旧 AppSettings.json 迁移

**Files:**
- Modify: `FModel/Settings/ProfileStore.cs`（追加 `MigrateLegacy`）
- Create: `FModel.Tests/MigrationTests.cs`

**Interfaces:**
- Consumes: `GameProfile.FromSettings`、`Save`、`FilePathById`、`ComputeId`、`NormalizeDirectory`。
- Produces: `public static IReadOnlyList<GameProfile> MigrateLegacy(string legacyJsonPath)`（见上表）。

关键设计：用 `JObject` 先抽出 `PerDirectory` 与 `GameDirectory`，**剩余字段**（全局 UserSettings）再 `ToObject<UserSettings>` —— 这样即使后续 UserSettings 加了 `[JsonIgnore]`（T6），迁移依然能读全。备份：同名 `_Legacy-<yyyyMMddHHmmss>.json`；每个 PerDirectory 条目 → 一个档案；`GameDirectory` 对应条目的 `LastUsedAt` 置为最新。

- [ ] **Step 1: 写失败测试**

`FModel.Tests/MigrationTests.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using FModel.Framework;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FModel.Tests;

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
        var path = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.json");
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
        Assert.Single(Directory.GetFiles(Path.GetTempPath(), $"{legacyName}_Legacy-*.json")); // 备份成功
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void MigrateLegacy_NoPerDirectory_ReturnsEmpty()
    {
        var legacy = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(legacy, new JObject { ["GameDirectory"] = "D:\\Games\\示例游戏" }.ToString());

        var profiles = ProfileStore.MigrateLegacy(legacy);

        Assert.Empty(profiles);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj --filter MigrationTests -v m`
Expected: 编译失败（MigrateLegacy 不存在）。

- [ ] **Step 3: 实现 MigrateLegacy**

在 `ProfileStore.cs` 追加（文件顶部加 `using Newtonsoft.Json.Linq;`）：

```csharp
    /* 一次性迁移旧 AppSettings.json：每个 PerDirectory 条目 → 一个档案；
     * 旧全局值作为档案 Settings 初始值；源文件改名备份后不再读取。
     * 用 JObject 先剥掉 PerDirectory/GameDirectory 再 ToObject<UserSettings>：
     * 即使 UserSettings 对这两属性标了 [JsonIgnore]（T6），迁移依旧能读。
     * 注意：JObject.ToObject 只接受 JsonSerializer 重载（JsonConvert 才有 settings 重载），
     * 这里统一包裹一次 JsonSerializer.Create，勿直接传 settings（不存在该重载）。 */
    public static IReadOnlyList<GameProfile> MigrateLegacy(string legacyJsonPath)
    {
        if (!File.Exists(legacyJsonPath)) return Array.Empty<GameProfile>();

        var root = JObject.Parse(File.ReadAllText(legacyJsonPath));
        var perDirectory = root["PerDirectory"] as JObject;
        if (perDirectory == null) return Array.Empty<GameProfile>();

        var gameDirectory = root["GameDirectory"]?.ToString() ?? string.Empty;
        root.Remove("PerDirectory");
        root.Remove("GameDirectory");
        var serializer = JsonSerializer.Create(JsonNetSerializer.SerializerSettings);
        var global = root.ToObject<UserSettings>(serializer) ?? new UserSettings();

        var profiles = new List<GameProfile>();
        foreach (var property in perDirectory.Properties())
        {
            var game = property.Value.ToObject<DirectorySettings>(serializer);
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
        Log.Information("Migrated {Count} game profile(s) from legacy settings, backup at {Backup}", profiles.Count, backup);
        return profiles;
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj --filter MigrationTests -v m`
Expected: PASS（2 个用例）。

- [ ] **Step 5: 请求用户确认后提交**

`git add FModel/Settings/ProfileStore.cs FModel.Tests/MigrationTests.cs`
Commit message: `feat: migrate legacy app settings into per-game profiles`

---

### Task 6：UserSettings 改线（Save/Delete → 档案；PerDirectory/GameDirectory 退役）

**Files:**
- Modify: `FModel/Settings/UserSettings.cs`

**Interfaces:**
- Consumes: `GameProfile.FromCurrent`、`ProfileStore.Save`、`ProfileStore.Delete`。
- Produces: `UserSettings.CurrentProfileId`（`[JsonIgnore]`，运行时当前档案 Id）；`PerDirectory`/`GameDirectory`/`ManualGames` 移除持久化（`[JsonIgnore]`/删除），属性仍在（周边代码 T7-T10 才移除引用，本任务保持编译）。

注意 step 3 的 `File.WriteAllText` 依赖：本文件已有 `using System.IO;`。`Save()` 改后 `ManualGames`（"TO DELETEEEEEE" 属性，仅本文件引用）直接删除属性本身（grep 确认过无其他引用）。

- [ ] **Step 1: 改 Save/Delete**

`UserSettings.cs`：

```csharp
    /* 退出/变更时保存：写当前档案（ProfileStore 内做 I2 保底），不再序列化全局设置文件。 */
    public static void Save()
    {
        if (!_bSave || Default == null) return;
        if (string.IsNullOrEmpty(Default.CurrentProfileId))
            return;
        try
        {
            var profile = GameProfile.FromCurrent(Default.CurrentProfileId, Default.CurrentDir);
            ProfileStore.Save(profile);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to save profile {Id}", Default.CurrentProfileId);
        }
    }

    /* Reset Settings 等：只删当前档案文件。_bSave 守卫保持原语义：
     * 删除后进程内退出（AppExit→Save）不得用内存状态重建刚删的档案。 */
    public static void Delete()
    {
        _bSave = false;
        if (!string.IsNullOrEmpty(Default?.CurrentProfileId))
            ProfileStore.Delete(Default.CurrentProfileId);
    }
```

（保留 `_bSave` 字段的既有用法。）

- [ ] **Step 2: 序列化标记与运行时字段**

在 `UserSettings` 类内：

```csharp
    [JsonIgnore]
    public string CurrentProfileId { get; set; }
```

把 `PerDirectory` 属性加 `[JsonIgnore]`（内存中保留属性，周边代码后续任务再移除引用）：

```csharp
    [JsonIgnore]
    public IDictionary<string, DirectorySettings> PerDirectory { get; set; } = new Dictionary<string, DirectorySettings>();
```

`GameDirectory` 同样加 `[JsonIgnore]`（值由档案的 `Game.GameDirectory` 承载）：

```csharp
    [JsonIgnore]
    public string GameDirectory { get; set; } = string.Empty;
```

删除 `ManualGames` 属性（连同其注释）。

- [ ] **Step 3: 编译确认**

Run: `dotnet build FModel/FModel.csproj`
Expected: BUILD SUCCESS（周边引用 PerDirectory/GameDirectory 的代码仍能编译——属性还在，只是不再序列化）。

- [ ] **Step 4: 跑全部测试**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj -v m`
Expected: PASS。

- [ ] **Step 5: 请求用户确认后提交**

`git add FModel/Settings/UserSettings.cs`
Commit message: `feat: route settings save/delete through the current game profile`

---

### Task 7：GameSelectorViewModel / DirectorySelector 改造成「添加档案」对话

**Files:**
- Modify: `FModel/ViewModels/GameSelectorViewModel.cs`
- Modify: `FModel/Views/DirectorySelector.xaml.cs`
- Modify: `FModel/Views/DirectorySelector.xaml`（仅标题文字：Directory Selector → Game Selector？——不改，diff 最小化，标题维持即可）

**Interfaces:**
- Consumes: `ProfileStore.Enumerate`、`ProfileStore.Delete`、`ProfileStore.ExistsById`、`ProfileStore.ComputeId`/`NormalizeDirectory`、`Helper`、`CUE4Parse.UE4.Versions.EGame`。
- Produces: 改后的 `GameSelectorViewModel`：不再读写 `UserSettings.Default.PerDirectory`；`DetectedDirectories` = 自动检测游戏 + 已存档案（`DirectorySettings` 视图）；`DeleteSelectedGame` = 有档案则删档案文件，无档案仅从列表移除；`AddUndetectedDir` 只加入本地列表，不再顺手写 PerDirectory。

- [ ] **Step 1: 改构造函数与删除**

`GameSelectorViewModel.cs` 构造函数（原第 49-67 行）：把「foreach PerDirectory 手动游戏克隆加入」改为「from ProfileStore.Enumerate()」：

```csharp
    public GameSelectorViewModel(string gameDirectory)
    {
        _detectedDirectories = new ObservableCollection<DirectorySettings>(EnumerateDetectedGames().Where(x => x != null));
        /* 去重用归一化目录推导的档案 Id（I3；AGENTS 5.2：跨来源路径比较必须先归一化） */
        foreach (var profile in ProfileStore.Enumerate(out _))
        {
            var id = ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(profile.Game.GameDirectory));
            if (_detectedDirectories.All(x => ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(x.GameDirectory)) != id))
                _detectedDirectories.Add((DirectorySettings) profile.Game.Clone());
        }

        DetectedDirectories = new ReadOnlyObservableCollection<DirectorySettings>(_detectedDirectories);

        if (DetectedDirectories.FirstOrDefault(x => x.GameDirectory == gameDirectory) is { } detectedGame)
            SelectedDirectory = detectedGame;
        else if (!string.IsNullOrEmpty(gameDirectory))
            AddUndetectedDir(gameDirectory);
        else
            SelectedDirectory = DetectedDirectories.FirstOrDefault();

        UeGames = new ReadOnlyObservableCollection<EGame>(new ObservableCollection<EGame>(EnumerateUeGames()));
    }
```

`AddUndetectedDir`（原 70-81 行）：删除 `UserSettings.Default.PerDirectory[gameDirectory] = setting;` 一行（只加入本地列表）：

```csharp
    public void AddUndetectedDir(string gameName, string gameDirectory)
    {
        if (TryDetectUeVersion(gameDirectory, out var ueVersion, out _))
        {
            // 检测到版本即用它；目录保持用户所选，规范化在档案层做（I3）
        }

        var setting = DirectorySettings.Default(gameName, gameDirectory, true, ueVersion);
        _detectedDirectories.Add(setting);
        SelectedDirectory = DetectedDirectories.Last();
    }
```

`DeleteSelectedGame`（原 185-190 行）：

```csharp
    public void DeleteSelectedGame()
    {
        if (SelectedDirectory == null) return;
        var id = ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(SelectedDirectory.GameDirectory));
        if (ProfileStore.ExistsById(id))
            ProfileStore.Delete(id); // 命中档案才删；自动检测的游戏不删文件
        _detectedDirectories.Remove(SelectedDirectory);
        SelectedDirectory = DetectedDirectories.LastOrDefault();
    }
```

- [ ] **Step 2: 编译确认**

Run: `dotnet build FModel/FModel.csproj`
Expected: SUCCESS（`DirectorySelector.xaml.cs` 无需改动——它调用的是这几个方法的原签名）。

- [ ] **Step 3: 请求用户确认后提交**

`git add FModel/ViewModels/GameSelectorViewModel.cs`
Commit message: `feat: game selector lists saved profiles instead of writing per-directory settings`

---

### Task 8：LauncherWindow + LauncherViewModel

**Files:**
- Create: `FModel/ViewModels/LauncherViewModel.cs`
- Create: `FModel/Views/LauncherWindow.xaml`
- Create: `FModel/Views/LauncherWindow.xaml.cs`

**Interfaces:**
- Consumes: `ProfileStore.Enumerate/TryLoad/Delete`、`ProfileLock.IsHeldByAliveProcess`、`ApplicationViewModel.LaunchProfileProcess`（T9 提供，本任务先以 `Process.Start(exe, "--game=<id>")` 本地实现，T9 统一收口）、`DirectorySelector`（T7 后语义 = 添加游戏对话）、`Ookii.Dialogs.Wpf.VistaFolderBrowserDialog` 不需要。
- Produces: `LauncherWindow`（XAML + code-behind）+ `LauncherViewModel`；启动器持久化 `LauncherSettings` —— **v1 不做**（Ruling: 窗口居中+固定尺寸足够，窗口位置持久化按 YAGNI 剔除；若后续需要，数据仍放 `%AppData%\FModel\Launcher.json`）。

LauncherViewModel 要点：
- `ObservableCollection<ProfileViewModel>` 行（Id、GameName、GameDirectory、UeVersion、LastUsedAt、IsRunning、IsCorrupt）。
- `Refresh()` 重建列表（排序按 LastUsedAt 降序；损坏文件单独行显示为「损坏文件」姓名 + 可删除）。
- 命令：`OpenSelectedCommand`（运行中 → MessageBox 提示；否则 `ApplicationViewModel.LaunchProfileProcess(id)`）、`AddGameCommand`（弹 DirectorySelector；OK 后 `ProfileStore.Create` 并在 `GameProfile.FromSettings` 基础上补齐；随即 `LaunchProfileProcess`）、`DeleteSelectedCommand`、`OpenProfilesFolderCommand`、`RefreshCommand`。
- 窗口关闭 = 进程退出（默认 OnLastWindowClose，启动器是 UI 主窗口）。

- [ ] **Step 1: LauncherViewModel**

`FModel/ViewModels/LauncherViewModel.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FModel.Framework;
using FModel.Settings;
using FModel.Views;
using MessageBox = AdonisUI.Controls.MessageBox;

namespace FModel.ViewModels;

/* 启动器（第一层）：列出所有游戏档案，开/删/新建档案。
 * 关掉本窗口不影响任何已启动的游戏进程（它们各自独立进程）。 */
public class LauncherViewModel : ViewModel
{
    public sealed class ProfileViewModel
    {
        public string Id { get; init; }
        public string GameName { get; init; }
        public string GameDirectory { get; init; }
        public string UeVersion { get; init; }
        public DateTime LastUsedAt { get; init; }
        public bool IsRunning { get; set; }
        public bool IsCorrupt { get; init; }
        public string LastUsedLabel => LastUsedAt == DateTime.MinValue ? "" : $"Last used: {LastUsedAt:yyyy-MM-dd HH:mm}";
    }

    private readonly ObservableCollection<ProfileViewModel> _profiles = new();
    public ObservableCollection<ProfileViewModel> Profiles => _profiles;

    private ProfileViewModel _selectedProfile;
    public ProfileViewModel SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    public ICommand OpenSelectedCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenProfilesFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    public LauncherViewModel()
    {
        OpenSelectedCommand = new DelegateCommand(OpenSelected);
        AddGameCommand = new DelegateCommand(AddGame);
        DeleteCommand = new DelegateCommand(DeleteSelected);
        OpenProfilesFolderCommand = new DelegateCommand(OpenProfilesFolder);
        RefreshCommand = new DelegateCommand(Refresh);

        Refresh();
    }

    public void Refresh()
    {
        var corrupt = new List<string>();
        var profiles = ProfileStore.Enumerate(out corrupt)
            .Select(p => new ProfileViewModel
            {
                Id = p.Id,
                GameName = p.Game.GameName,
                GameDirectory = p.Game.GameDirectory,
                UeVersion = p.Game.UeVersion.ToString(),
                LastUsedAt = p.LastUsedAt,
                IsRunning = ProfileLock.IsHeldByAliveProcess(p.Id)
            })
            .OrderByDescending(p => p.LastUsedAt)
            .ToList();

        /* 损坏档案：仍列出（标注），可以删除；id 从文件名解析 */
        foreach (var file in corrupt)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var pos = id.LastIndexOf('_');
            if (pos > 0) id = id[(pos + 1)..];
            profiles.Add(new ProfileViewModel
            {
                Id = id,
                GameName = $"（损坏档案）{Path.GetFileName(file)}",
                GameDirectory = "unreadable",
                UeVersion = "",
                LastUsedAt = DateTime.MinValue,
                IsCorrupt = true
            });
        }

        _profiles.Clear();
        foreach (var p in profiles) _profiles.Add(p);

        if (SelectedProfile == null && _profiles.Count > 0)
            SelectedProfile = _profiles[0];
    }

    private void OpenSelected()
    {
        if (SelectedProfile == null || SelectedProfile.IsCorrupt) return;
        if (SelectedProfile.IsRunning)
        {
            MessageBox.Show($"{SelectedProfile.GameName} is already running.", "Game already running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ApplicationViewModel.LaunchProfileProcess(SelectedProfile.Id);
    }

    private void AddGame()
    {
        var vm = new GameSelectorViewModel(string.Empty);
        var dialog = new DirectorySelector(vm);
        if (!(dialog.ShowDialog() ?? false)) return;
        var selected = vm.SelectedDirectory;
        if (selected == null) return;

        var profile = GameProfile.FromSettings(
            ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(selected.GameDirectory)),
            selected, new UserSettings());
        profile = ProfileStore.Create(profile); // I3：同目录复用既有设置
        Refresh();
        SelectedProfile = _profiles.FirstOrDefault(x => x.Id == profile.Id);
        ApplicationViewModel.LaunchProfileProcess(profile.Id);
    }

    private void DeleteSelected()
    {
        if (SelectedProfile == null) return;
        var confirm = MessageBox.Show(
            $"Delete game '{SelectedProfile.GameName}'?\nIts settings and AES keys will be removed.",
            "Delete game", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (SelectedProfile.IsCorrupt)
        {
            foreach (var f in Directory.GetFiles(ProfileStore.ProfilesDirectory, "*_" + SelectedProfile.Id + ".json"))
                File.Delete(f);
        }
        else
        {
            ProfileStore.Delete(SelectedProfile.Id);
        }
        Refresh();
    }

    private void OpenProfilesFolder()
    {
        Directory.CreateDirectory(ProfileStore.ProfilesDirectory);
        Process.Start(new ProcessStartInfo { FileName = ProfileStore.ProfilesDirectory, UseShellExecute = true });
    }

    /* 本项目 ViewModelCommand 是带上下文抽象类（绑定只需 ICommand），这里用最小实现 */
    private sealed class DelegateCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public DelegateCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
}
```

- [ ] **Step 2: LauncherWindow.xaml**

`FModel/Views/LauncherWindow.xaml`（对照 `DirectorySelector.xaml` 的 AdonisUI 写法）：

```xml
<adonisControls:AdonisWindow x:Class="FModel.Views.LauncherWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:adonisUi="clr-namespace:AdonisUI;assembly=AdonisUI"
        xmlns:adonisControls="clr-namespace:AdonisUI.Controls;assembly=AdonisUI"
        xmlns:adonisExtensions="clr-namespace:AdonisUI.Extensions;assembly=AdonisUI"
        Width="720" Height="480" MinWidth="520" MinHeight="320"
        WindowStartupLocation="CenterScreen" Activated="OnActivated">
    <adonisControls:AdonisWindow.Style>
        <Style TargetType="adonisControls:AdonisWindow" BasedOn="{StaticResource {x:Type adonisControls:AdonisWindow}}">
            <Setter Property="Title" Value="FModel" />
        </Style>
    </adonisControls:AdonisWindow.Style>
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Border Grid.Row="0" Background="{DynamicResource {x:Static adonisUi:Brushes.Layer2BackgroundBrush}}"
                adonisExtensions:LayerExtension.IncreaseLayer="True">
            <TextBlock Margin="16" FontSize="20" FontWeight="SemiBold" Text="Games" />
        </Border>

        <ListBox Grid.Row="1" Margin="8" ItemsSource="{Binding Profiles}" SelectedItem="{Binding SelectedProfile, Mode=TwoWay}">
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <StackPanel Orientation="Horizontal" Margin="4">
                        <StackPanel Orientation="Vertical" Width="360">
                            <TextBlock Text="{Binding GameName}" FontWeight="SemiBold"/>
                            <TextBlock Text="{Binding GameDirectory}" TextTrimming="CharacterEllipsis" Opacity="0.7"/>
                            <TextBlock Text="{Binding UeVersion}" FontSize="11" Opacity="0.6"/>
                            <TextBlock Text="{Binding LastUsedLabel}" FontSize="11" Opacity="0.6"/>
                        </StackPanel>
                        <TextBlock Text="RUNNING" Foreground="Orange" VerticalAlignment="Center" Margin="8,0,0,0">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding IsRunning}" Value="True">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                    </StackPanel>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>

        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="8">
            <Button MinWidth="90" Style="{DynamicResource {x:Static adonisUi:Styles.AccentButton}}" Margin="0,0,8,0"
                    Content="Open" Command="{Binding OpenSelectedCommand}" IsDefault="True"/>
            <Button MinWidth="90" Margin="0,0,8,0" Content="Add Game" Command="{Binding AddGameCommand}"/>
            <Button MinWidth="90" Margin="0,0,8,0" Content="Delete" Command="{Binding DeleteCommand}"/>
            <Button MinWidth="90" Margin="0,0,8,0" Content="Refresh" Command="{Binding RefreshCommand}" />
            <Button MinWidth="90" Margin="0,0,8,0" Content="Open Folder" Command="{Binding OpenProfilesFolderCommand}"/>
        </StackPanel>
    </Grid>
</adonisControls:AdonisWindow>
```

（Refresh 命令：给 `LauncherViewModel` 补一个简单 `public ViewModelCommand RefreshCommand { get; }` 初始化 `= new ViewModelCommand(() => Refresh());`，`ViewModelCommand` 构造需要看 `FModel/Framework/ViewModelCommand.cs` 实际签名，加无参 Action 重载不行就改成 lambda + `new ViewModelCommand(Refresh)` 传 Action。）

- [ ] **Step 3: LauncherWindow.xaml.cs**

`FModel/Views/LauncherWindow.xaml.cs`：

```csharp
using System.ComponentModel;
using System.Windows;
using FModel.ViewModels;

namespace FModel.Views;

/* 第一层窗口：启动器。本身不承载游戏逻辑，关掉不影响游戏进程。 */
public partial class LauncherWindow
{
    public LauncherWindow()
    {
        DataContext = new LauncherViewModel();
        InitializeComponent();
    }

    /* 窗口每次获得焦点时刷新列表（顺带刷新「运行中」状态，成本 = 枚举目录 + PID 检查） */
    private void OnActivated(object sender, System.EventArgs e)
    {
        ((LauncherViewModel) DataContext).Refresh();
    }
}
```

- [ ] **Step 4: 编译确认**

Run: `dotnet build FModel/FModel.csproj`
Expected: SUCCESS（`ApplicationViewModel.LaunchProfileProcess` 尚不存在，本任务先临时加一个最小静态方法在 ApplicationViewModel 里，见下一行；T9 再定稿）。

在 `ApplicationViewModel.cs` 内先加临时实现（真实实现 T9 已有 `Restart()` 的启动逻辑可复用）：

```csharp
    /* 启动一个游戏进程（--game=<Id>）；exe/dll 判定与 Restart 相同 */
    public static void LaunchProfileProcess(string profileId)
    {
        var path = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        if (path.EndsWith(".dll"))
            startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{path}\" --game={profileId}",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
        else if (path.EndsWith(".exe"))
            startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                Arguments = $"--game={profileId}",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

        System.Diagnostics.Process.Start(startInfo);
    }
```

（如果 `ApplicationViewModel` 编译报错说找不到 `LaunchProfileProcess`，就把这段作为 Step 4 的一部分先落盘再编译。）

- [ ] **Step 5: 请求用户确认后提交**

`git add FModel/ViewModels/LauncherViewModel.cs FModel/Views/LauncherWindow.xaml FModel/Views/LauncherWindow.xaml.cs FModel/ViewModels/ApplicationViewModel.cs`
Commit message: `feat: add launcher window with profile list, open/add/delete`

---

### Task 9：ApplicationViewModel 收口（移除弹窗路径 + BackToLauncher + AddGameDirectory 新语义）

**Files:**
- Modify: `FModel/ViewModels/ApplicationViewModel.cs`

**Interfaces:**
- Consumes: `ProfileStore.Create`、`GameProfile`、`LaunchProfileProcess`（T8）、`Helper.GetWindow`、`GameSelectorViewModel`（T7）。
- Produces: `AddGameDirectory(directory)` 新语义：建/复用档案 + 起进程、返回 null；`BackToLauncher()`；删除 `AvoidEmptyGameDirectory`。

- [ ] **Step 1: 构造函数与 AvoidEmptyGameDirectory 替换**

`ApplicationViewModel` 构造（原第 105-111 行）：

```csharp
        /* 档案已在 App 层装载（--game）；此处只做安全兜底：没有 CurrentDir 直接硬退出（与旧一致） */
        if (UserSettings.Default.CurrentDir is null)
            Environment.Exit(0);
```

删除整个 `AvoidEmptyGameDirectory` 方法（原 139-159 行）。

- [ ] **Step 2: AddGameDirectory 新语义**

```csharp
    /* 新语义：拖入目录 → 建/复用档案 → 启动新游戏进程；当前窗口不动（与启动器一致） */
    public DirectorySettings AddGameDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        var vm = new GameSelectorViewModel(directory);
        vm.AddUndetectedDir(directory);
        var selected = vm.SelectedDirectory;
        if (selected is null) return null;

        var profile = GameProfile.FromSettings(
            ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(selected.GameDirectory)),
            selected, new UserSettings());
        profile = ProfileStore.Create(profile);
        LaunchProfileProcess(profile.Id);
        return selected;
    }
```

- [ ] **Step 3: BackToLauncher + LaunchProfileProcess 定稿**

```csharp
    /* 返回启动器：保存当前档案 → 退出本进程，启动器在则激活、不在则拉起 */
    public void BackToLauncher()
    {
        UserSettings.Save();
        var launcherExe = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        var startInfo = launcherExe.EndsWith(".dll")
            ? new ProcessStartInfo { FileName = "dotnet", Arguments = $"\"{launcherExe}\" --launcher", UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo { FileName = launcherExe, Arguments = "--launcher", UseShellExecute = false, CreateNoWindow = true };
        Process.Start(startInfo);
        Application.Current.Shutdown();
    }
```

`LaunchProfileProcess` 定稿（替换 T8 的临时实现，内容一致——T8 步骤 4 的即终稿）。

- [ ] **Step 4: 编译确认 + 手动检查**

Run: `dotnet build FModel/FModel.csproj`
Expected: SUCCESS。手动核对：`MainWindow` 没有其它 `AvoidEmptyGameDirectory` 引用残留（`grep -n "AvoidEmptyGameDirectory" FModel/` 应为空；`AddGameDirectory` 仍被 `DropOverlay.xaml.cs:97` 引用——T11 处理）。

- [ ] **Step 5: 请求用户确认后提交**

`git add FModel/ViewModels/ApplicationViewModel.cs`
Commit message: `feat: add back-to-launcher and new-add flow, drop selector dialog from main window`

---

### Task 10：App.xaml.cs 分派（launcher 单实例 / --game 直开 / 迁移触发 / CLI 档案化）

**Files:**
- Modify: `FModel/App.xaml.cs`

**Interfaces:**
- Consumes: `ProfileStore.TryLoad/Enumerate/MigrateLegacy`、`ProfileLock.TryAcquire`、`GameProfile.ApplyToCurrent`、`LauncherWindow`、`CliExporter`、`UserSettings.FilePath`。
- Produces: `App.OnStartup` 分派逻辑；静态字段 `_currentProfileLock`（进程级持有锁到 AppExit）。

- [ ] **Step 1: 参数解析与启动模型**

`App.xaml.cs` 顶部（`OnStartup` 内，CLI 分支判断前）加：

```csharp
        /* 启动模型分派（优先级）：CLI → --game=<Id> → 启动器（单实例） */
        if (!CliExporter.HasCliArguments)
        {
            var profileId = GetProfileIdArg();
            if (profileId != null)
            {
                ShowGameWindow(profileId);
                return;
            }
            ShowLauncherWindow();
            return;
        }
```

```csharp
    private static string GetProfileIdArg()
        => Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--game=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--game=".Length).Trim('"');
```

- [ ] **Step 2: ShowLauncherWindow（单实例互斥量 + 广播激活 + 迁移）**

```csharp
    private void ShowLauncherWindow()
    {
        bool createdNew;
        // 单实例：第二个启动器实例只广播激活并退出（AGENTS：外部约定，不可改名）
        _ = new Mutex(true, @"Local\FModel-Launcher-Mutex", out createdNew);
        if (!createdNew)
        {
            NativeMethods.BroadcastLauncherActivate();
            Environment.Exit(0);
            return;
        }

        base.OnStartup(new StartupEventArgs());
        InitializeSettings();
        MigrateLegacySettingsIfNeeded();
        new LauncherWindow().Show();
    }
```

其中：

```csharp
    private void MigrateLegacySettingsIfNeeded()
    {
        if (!File.Exists(UserSettings.FilePath)) return;
        try { ProfileStore.MigrateLegacy(UserSettings.FilePath); }
        catch (Exception ex) { Log.Error(ex, "Legacy settings migration failed"); }
    }
```

- [ ] **Step 3: ShowGameWindow**

```csharp
    private void ShowGameWindow(string profileId)
    {
        base.OnStartup(new StartupEventArgs());
        InitializeSettings();

        if (!ProfileStore.TryLoad(profileId, out var profile, out var error))
        {
            EarlyFail($"profile failed to load: {error}", 1);
            return;
        }

        var profileLock = ProfileLock.TryAcquire(profile.Id);
        if (profileLock == null)
        {
            EarlyFail($"profile '{profile.Id}' is already running", 1);
            return;
        }
        _currentProfileLock = profileLock;

        profile.ApplyToCurrent();
        profile.LastUsedAt = DateTime.Now;
        try { ProfileStore.Save(profile); } catch (Exception ex) { Log.Warning(ex, "Could not update LastUsedAt"); }

        Log.Information("Opened game profile {Id} ({Name})", profile.Id, profile.Game.GameName);
        new MainWindow().Show();
    }

    private static ProfileLock _currentProfileLock;

    private void EarlyFail(string message, int exitCode)
    {
        Serilog.Log.Error("{Message}", message);
        try { AdonisUI.Controls.MessageBox.Show(message, "FModel", MessageBoxButton.OK, MessageBoxImage.Error); }
        catch { /* 无人可点（无桌面）时静默 */ }
        Environment.Exit(exitCode);
    }
```

注意：`InitializeSettings` 后、`MainWindow` 构造前，`profile.ApplyToCurrent()` 必须先执行（MainWindow 构造依赖 CurrentDir），确认顺序如上。

- [ ] **Step 4: AppExit 释放锁**

`AppExit` 里（`UserSettings.Save();` 之后）：

```csharp
        _currentProfileLock?.Dispose();
```

- [ ] **Step 5: 原生激活消息（NativeMethods/RuntimeInterop）**

在 `App.xaml.cs` 加 P/Invoke：

```csharp
    private static class NativeMethods
    {
        private const int WM_BROADCAST = 0xFFFF;
        private static readonly int MsgId = RegisterWindowMessage("FModel-Activate-Launcher");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern int PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void BroadcastLauncherActivate()
            => PostMessage(new IntPtr(WM_BROADCAST), MsgId, IntPtr.Zero, IntPtr.Zero);
    }
```

并在 `LauncherWindow.xaml.cs` 里挂钩（`OnSourceInitialized` 用 `HwndSource` + `HwndSourceHook` 监听同一消息然后 `Dispatcher` 上 `Activate()` 置顶）：

`LauncherWindow.xaml.cs` 追加：

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

private const int WM_ACTIVATE_LAUNCHER = 0; // 运行时注册的消息 Id 缓存在字段
private static int _launcherActivateMsgId = -1;

protected override void OnSourceInitialized(EventArgs e)
{
    base.OnSourceInitialized(e);
    if (_launcherActivateMsgId == -1)
        _launcherActivateMsgId = (int) RegisterWindowMessage("FModel-Activate-Launcher");
    var source = (HwndSource) PresentationSource.FromVisual(this);
    source?.AddHook(LauncherHook);
}

private IntPtr LauncherHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
{
    if (msg == _launcherActivateMsgId)
    {
        Dispatcher.InvokeAsync(() => { Activate(); Topmost = true; Topmost = false; });
        handled = true;
    }
    return IntPtr.Zero;
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
private static extern uint RegisterWindowMessage(string message);
```

（`LauncherWindow` 部分类里已有 `using FModel.ViewModels;`，补 `System.Windows.Interop`、`System.Runtime.InteropServices`、`System.ComponentModel` 按需。）

- [ ] **Step 6: CLI 分支档案化（该步在 T12 细做，本步仅保持编译）**

`App.xaml.cs` 里 `RunHeadless` 前的 `HasCliArguments` 分支里，把原先「GameDirectory/PerDirectory 校验」（原 119-126 行）替换为：

```csharp
            /* CLI：解析档案（--game=<Id> 或最近使用），无档案 → 明确报错（AGENTS 1.3） */
            var profile = ResolveCliProfile(cliRequest.GameId);
            if (profile == null)
            {
                throw new InvalidOperationException(
                    $"CLI: no game profile found. Pick one in the launcher first, or pass --game=<profileId>.");
            }
            var cliLock = ProfileLock.TryAcquire(profile.Id);
            if (cliLock == null)
            {
                throw new InvalidOperationException($"CLI: profile '{profile.Id}' is already running (probably the GUI).");
            }
            profile.ApplyToCurrent();
            _currentProfileLock = cliLock;
```

并加：

```csharp
    private static GameProfile ResolveCliProfile(string gameId)
    {
        if (!string.IsNullOrEmpty(gameId))
            return ProfileStore.TryLoad(gameId, out var p, out _) ? p : null;
        return ProfileStore.Enumerate(out _)
            .OrderByDescending(p => p.LastUsedAt)
            .FirstOrDefault();
    }
```

（`CliRequest.GameId` 在 T12 加——本步先编译：T12 会在同一次合并前落，若 T10 先提交则此处用 `string.Empty` 占位会导致编译失败。**调整顺序：T10 与 T12 都必须合并后才编译通过**，所以本任务的 Step 6 放到 T12 一起交。提交时注明顺序：先 T12（CliExporter 加 GameId），再 T10 编译。）

- [ ] **Step 7: 编译确认**

Run: `dotnet build FModel/FModel.csproj`
Expected: 若提示 `CliRequest.GameId` 不存在，先实施 T12 Step 1-2，再回此步骤。

- [ ] **Step 8: 请求用户确认后提交**

`git add FModel/App.xaml.cs FModel/Views/LauncherWindow.xaml.cs`
Commit message: `feat: dispatch app startup to launcher (single instance) or game window by --game`

---

### Task 11：入口收编（菜单 + DropOverlay）

**Files:**
- Modify: `FModel/ViewModels/Commands/MenuCommand.cs:25-26`
- Modify: `FModel/Views/Resources/Controls/DropOverlay.xaml.cs:97`
- Modify: `FModel/MainWindow.xaml:83-84`（仅菜单文字）

**Interfaces:**
- Consumes: `ApplicationViewModel.BackToLauncher`、`AddGameDirectory`（T9）。

- [ ] **Step 1: 菜单**

`MenuCommand.cs` 的 `case "Directory_Selector":` 改为：

```csharp
            case "Directory_Selector":
                contextViewModel.BackToLauncher();
                break;
```

`MainWindow.xaml`（第 84 行）：`<MenuItem Header="Selector" ...>` 文字改为 `Header="Back to Launcher"`（Keep CommandParameter="Directory_Selector" 不动，最小 diff）。

- [ ] **Step 2: DropOverlay —— 无需改动（只验证编译）**

`DropOverlay.xaml.cs:97` 现为：

```csharp
await Dispatcher.InvokeAsync(() => _applicationView.AddGameDirectory(_path));
```

`AddGameDirectory` 在 T9 变更为新语义（建/复用档案 + 启动游戏进程），**签名与 void 式调用场景不变**（返回值仍为 `DirectorySettings`，此处不使用）。本步只做：

Run: `dotnet build FModel/FModel.csproj`
Expected: SUCCESS。

- [ ] **Step 3: 编译确认 + 手动检查**

Run: `dotnet build FModel/FModel.csproj`
Expected: SUCCESS。核对 `MainWindow` 菜单 HTML 预览不需要。grep `AntiEmptyGameDirectory` 为 0。

- [ ] **Step 4: 请求用户确认后提交**

`git add FModel/ViewModels/Commands/MenuCommand.cs FModel/MainWindow.xaml FModel/Views/Resources/Controls/DropOverlay.xaml.cs`
Commit message: `feat: back-to-launcher menu entry and drag-drop creates profiles`

---

### Task 12：CLI `--game=<Id>` + 注释同步

**Files:**
- Modify: `FModel/CliExporter.cs`

**Interfaces:**
- Consumes: `ProfileStore`（T2/T5）。
- Produces: `CliRequest.GameId`（string）；`--game=<Id>` 解析；文件头注释更新。

- [ ] **Step 1: 解析**

`CliRequest` 加：

```csharp
        public string GameId;
```

`ParseRequest()` 循环里加：

```csharp
            else if (arg.StartsWith("--game=", StringComparison.OrdinalIgnoreCase))
                gameId = arg["--game=".Length..].Trim('"');
```

并在 `return new CliRequest { ..., GameId = gameId };` 返回。

（变量 `string gameId = null;` 加在声明处。）

- [ ] **Step 2: 文件头注释**

`CliExporter.cs` 顶部注释补一段：

```
 *  --game=<Id>：可选，指定要用的游戏档案（Id 见启动器列表或 Profiles 目录文件名）。
 *  不传时使用档案中 LastUsedAt 最新的一个；无任何档案时失败退出（码 1）。
```

- [ ] **Step 3: 编译确认（配合 T10 Step 6）**

Run: `dotnet build FModel/FModel.csproj`
Expected: SUCCESS。然后实施 T10 Step 6 代码、再编译一次。

- [ ] **Step 4: CLI 冒烟（用既有测试方法：Start-Process -Wait -PassThru 拿 ExitCode）**

在 PowerShell 里模拟四项（测试前先确认 `%APPDATA%\FModel\Profiles\` 有档案，或临时用迁移出的档案）：

```powershell
$exe = "E:\Workspace\UE\FModel\FModel\bin\Debug\net10.0-windows\win-x64\FModel.exe"
# 1) --game 指定档案
$p = Start-Process -FilePath $exe -ArgumentList "--cli","--game=<某档案Id>","-ExportFolder=<内容根>/<目录>","-ExportType=Properties" -Wait -PassThru -WindowStyle Hidden
$p.ExitCode  # 期望 0
# 2) 缺省（最近使用档案）
$p = Start-Process -FilePath $exe -ArgumentList "--cli","-ExportFolder=<内容根>/<目录>","-ExportType=Properties" -Wait -PassThru -WindowStyle Hidden
$p.ExitCode  # 期望 0
# 3) 无档案可解析
$p = Start-Process -FilePath $exe -ArgumentList "--cli","-ExportFolder=<内容根>/<目录>","-ExportType=Properties" -Wait -PassThru -WindowStyle Hidden
$p.ExitCode  # 期望 1（先把 Profiles 目录移到旁边再跑，跑完还原）
# 4) 锁冲突：先开一个 --cli 占锁（长任务），再开第二个同档案 --cli
$p = Start-Process -FilePath $exe -ArgumentList "--cli","--game=<同档案Id>","-ExportFile=<内容根>/<目录>/<资产>.uasset","-ExportType=Properties" -Wait -PassThru -WindowStyle Hidden
$p.ExitCode  # 期望 1
```

Expected: 四种情况分别 0 / 0 / 1 / 1。第 3/4 项的失败信息落在程序日志。

- [ ] **Step 5: 请求用户确认后提交**

`git add FModel/CliExporter.cs FModel/App.xaml.cs`
Commit message: `feat: CLI --game profile resolution and lock conflict handling`

---

### Task 13：端到端手工验收 + 记忆更新

**Files:**
- Modify: （无代码；验收清单与记忆文件）
- Update: `C:\Users\luziao01\.claude\projects\E--Workspace-UE-FModel\memory\cli-exporter-testing.md`（测试前置从「AppSettings.json 的 GameDirectory/PerDirectory」改为「启动器里选过游戏 / Profiles 目录有档案」）

- [ ] **Step 1: 全量测试与两种配置构建**

Run: `dotnet test FModel.Tests/FModel.Tests.csproj -v m` → ALL PASS
Run: `dotnet build FModel/FModel.csproj` 与 `dotnet build FModel/FModel.csproj -c Release` → SUCCESS

- [ ] **Step 2: 手工验收清单（用户配合 GUI）**

1. 有旧 AppSettings.json 的环境：双击 exe → 启动器出现；Profiles 目录生成各游戏档案；旧文件成 `AppSettings_Legacy-*.json`；列表与原来一致（AES 都在）。
2. 打开一个游戏 → 主窗口直接进入（无目录选择框）；改 AES + 导出设置 → 关闭 → 重开一致。
3. 再开另一游戏 → 与第一个游戏的设置独立（打开哪个是哪个）。
4. 同一游戏再开一次 → 提示「已在运行」。
5. 返回启动器菜单：启动器还在 → 置顶；已关 → 拉起。
6. 启动器关闭后游戏窗口不受影响。
7. 双击 exe（启动器已开）→ 不出现第二个启动器，已有置顶。
8. 新增游戏（检测的手动都可）→ 自动开进程；同目录重复加 → 复用既有档案（AES 保留）。
9. 删除档案 → 列表消失、目录文件消失。

- [ ] **Step 3: 更新记忆**

按上面 Files 所述改写 `cli-exporter-testing.md` 的前置条件部分（并更新 MEMORY.md 索引行描述如必要）。

- [ ] **Step 4: 请求用户确认后提交**

`git add -A`
Commit message: `feat: game profiles and launcher (end-to-end)`

---

## 自审记录（写计划时完成的检查）

- **Spec 覆盖**：§1 目标①②③（档案=全部可手动设置、自动记录、可删）→ T1/T2/T6/T8；§3 进程模型（启动器单实例/同游戏单实例/解耦）→ T3/T8/T10/T11；§4 档案模型/同目录=同档案 → T1/T2；§5 组件清单 → T1-T12 逐项；§6 数据流（打开/添加/保存/返回/最近使用/拖入）→ T8/T9/T10/T11；§7 迁移 → T5/T10；§8 CLI → T10/T12；§9 不变式 I1-I5 → T2（I2/I3/I4）、T3（同游戏单实例）、T1（I5 深拷贝）、T6/I1（只写当前档案）；§10 错误处理 → T2/T3/T10；§11 测试 → T1-T5/T12/T13；§12 AGENTS 合规 → Global Constraints 逐条；§13 决策表 → 全部落在上述任务。
- **占位符扫描**：无 TODO/TBD；所有 "…" 均为测试样例占位（`<内容根>` 等按 AGENTS 1.4 属必要占位符）或既有代码引用。
- **类型一致性**：`GameProfile.FromCurrent/FromSettings/ApplyToCurrent`、`ProfileStore.*`、`ProfileLock.TryAcquire/IsHeldByAliveProcess`、`UserSettings.CurrentProfileId`、`CliRequest.GameId`、`ApplicationViewModel.LaunchProfileProcess/BackToLauncher/AddGameDirectory` 在跨任务引用处签名一致。
- **已知风险（已核实，无需执行中再查）**：`ViewModelCommand` 是带上下文的抽象类（`FModel/Framework/ViewModelCommand.cs`），T8 改用自带的私有 `DelegateCommand : ICommand`；`DropOverlay.xaml.cs:97` 的 `AddGameDirectory` 调用不使用返回值，签名保持即可（已读第 90-105 行确认）；T10/T12 相互依赖（先 T12 加 `CliRequest.GameId`，或一步完成再编译）。
