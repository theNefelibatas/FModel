# 游戏档案 + 启动器（Game Profiles & Launcher）设计

- 日期：2026-08-31
- 状态：设计稿（待评审）
- 范围：FModel 主仓库（custom/dev 线），不涉及 CUE4Parse submodule

## 1. 背景与问题

当前实现（均已核实）：

- 单进程单窗口。启动链：`App.OnStartup` → 新建 `MainWindow` → 构造 `ApplicationViewModel`（`ApplicationViewModel.cs:105` 调 `AvoidEmptyGameDirectory(false)`）→ `PerDirectory[GameDirectory]` 命中则直接进入，否则弹 `DirectorySelector` 对话框。
- 设置几乎全部是全局一份（`UserSettings`），只有少数目录级字段（AES、UE 版本、纹理平台、版本标记、端点、自定义目录等，见 `DirectorySettings`）按目录存进 `PerDirectory` 字典（键 = 目录路径字符串）。
- 切换游戏需要重启：第二次打开 `DirectorySelector` 选了不同游戏后走 `RestartWithWarning`（`ApplicationViewModel.cs:152-157`、`184-186`；菜单入口 `MenuCommand.cs:26` 的 `Directory_Selector`）。

用户反馈的三个问题：

1. **设置不按游戏区分**。换个游戏后导出的格式、质量、材质选项等全部串台；期望"打开哪个游戏就是哪个游戏的全套设置"。
2. **切换需要重启**且流程阻隔（先弹"需要重启"提示）。
3. **AES 密钥切换会丢**。已核实代码里的薄弱点：
   - `GameSelectorViewModel` 对已记录游戏使用 `dir.Clone()`（`GameSelectorViewModel.cs:54`），`MemberwiseClone` 是浅拷贝，`AesKeys` 对象与存储中的条目共享引用；
   - `DirectorySettings.Default`（`DirectorySettings.cs:21`）对已有条目直接复用 `old.AesKeys` 引用而不是拷贝，后续任何替换/清空都会作用到已存数据上；
   - `RefreshAes` / `UpdateProvider` 会用 API 结果整体替换 `CurrentDir.AesKeys`（`CUE4ParseViewModel.cs:418`、`AesManagerViewModel.cs:92`），接口返回空集时即"清空"；
   - `UserSettings.Save()`（`UserSettings.cs:38`）每次退出把 `CurrentDir` 重新序列化回 `PerDirectory[CurrentDir.GameDirectory]`，配合上面的共享引用，切换路径上存在"新条目携带空值覆盖旧条目"的丢失窗口。

## 2. 目标与非目标

### 目标

- 一游戏一**档案**：游戏目录、UE 版本、AES，以及 FModel 界面**所有可手动设置**的字段，全部归入该游戏的档案；打开哪个游戏就是哪个游戏的设置。
- 档案自动记录（进入游戏主界面后新增/变更即保存），可手动删除（启动器里或直接删文件）。
- 双层窗口：第一层**启动器**（常驻，建/选/开/删档案）；第二层**游戏主窗口**（每游戏一个独立进程），与启动器生命周期解耦。
- 同游戏单实例；不同游戏可同时多开。
- 切换到另一个游戏 = 直接开一个新的游戏进程，**永不重启任何已有进程**。
- 兼容既有 CLI 无头导出模式。

### 非目标（v1 明确不做）

- 同进程多主窗口（`ApplicationService` 是静态单例，同进程多窗口需大面积重构）。
- 档案改名、档案目录迁移（目录变了 = 新档案；旧档案留在列表里手动删）。
- 跨档案复制/合并设置（每个档案从默认值初始化，各自独立设置）。
- 主窗口内直接换游戏（换游戏入口 = 返回启动器）。
- 启动器单实例之外的其它 IPC（如远程控制游戏窗口）。

## 3. 终端进程模型

三个入口，同一 exe 按参数分派；分派在 `App.OnStartup` 里先于现有 CLI/GUI 分支判定执行：

| 参数 | 模式 |
|---|---|
| 无参数 / `--launcher` | 启动器（第一层） |
| `--game <档案Id>` | 直接进该档案的游戏主窗口（第二层） |
| `--cli ...` | 无头导出；`--game <档案Id>` 可选，缺省用最近使用的档案 |

- **启动器单实例**：启动器进程持有命名互斥量 `Local\FModel-LauncherMutex`。重复双击 exe 时：互斥量已被持有 → 通过 `RegisterWindowMessage("FModel-Activate-Launcher")` + `HWND_BROADCAST` 通知已有启动器窗口 `SetForegroundWindow`/激活置顶，新进程立即退出。`--game` / `--cli` 入口不获取该互斥量。
- **游戏进程单实例**：游戏进程启动时创建 `Profiles\<Id>.lock`（内容写自身 PID），持有到进程退出，退出时删除。第二个实例尝试打开同一游戏：
  - 启动器预检：锁文件存在且 PID 存活 → 列表该档案标记「运行中」，双击为提示「该游戏已在运行」；
  - 进程级兜底：锁已存在且 PID 存活 → 弹提示后退出（CLI 场景改为记录日志并以退出码 1 退出）。
  - 崩溃残留：锁文件存在但 PID 已无存活进程 → 视为未锁定，允许打开（打开时覆盖旧锁文件）。
- **生命周期解耦**：关闭启动器不影响任何游戏进程。游戏窗口菜单「Directory_Selector」改为「返回启动器」：保存并退出本进程；启动器仍在 → 激活置顶，不在 → 拉起新的启动器进程（复用现有 `Restart()` 的 exe/dll 启动判定逻辑）。

## 4. 档案模型

### 4.1 存储

- 目录：`%AppData%\FModel\Profiles\`（`Environment.SpecialFolder.ApplicationData` 拼接，与现有 AppSettings 文件同根，不写死路径）。
- 文件名：`<净化后的显示名>_<Id>.json`（显示名去除 Windows 非法文件名字符替换为 `_`，便于文件系统直接辨认）。
- `Id`：规范化目录（去尾部分隔符 + `Path.GetFullPath` + 忽略大小写）的短哈希，SHA-1 前 16 位十六进制；冲突时追加序号。**同目录 = 同档案** 由 Id 定义保证；目录变了自然产生新 Id → 新档案（旧档案保留，手动删）。

### 4.2 结构

```json
{
  "SchemaVersion": 1,
  "Id": "<档案Id>",
  "CreatedAt": "<当次运行时间>",
  "LastUsedAt": "<当次运行时间>",
  "Game": {
    "_DirectorySettings 全部字段（含 GameName / GameDirectory / UeVersion / AesKeys 等）_": {}
  },
  "Settings": {
    "_UserSettings 全部可序列化字段（PerDirectory / CurrentDir 除外）_": {}
  }
}
```

- 序列化直接复用现有 `UserSettings` / `DirectorySettings` 对象图：`Settings` = 反序列化出来的 `UserSettings` 快照，`Game` = `DirectorySettings` 快照。**字段清单无需维护**：以后手动设置项新增字段时自动进入档案（这正是"所有可手动设置字段入档案"的实现方式）。
- 加载：反序列化两个快照分别装入 `UserSettings.Default` 与 `UserSettings.Default.CurrentDir`（JSON 往返天然深拷贝，杜绝浅拷贝共享引用——问题 3 的根因之一）。其余运行时代码**零改动**。
- 保存：从 `UserSettings.Default` + `CurrentDir` 序列化回当前档案文件。
- `PerDirectory` 字典与 `CurrentDir` 序列化形式：加载/保存流程中转为上述结构后，`PerDirectory` 在内存中保留为空字典（`[JsonIgnore]` 或恒空），不再作为持久化结构。

### 4.3 运行时的「当前档案」标识

加载档案时记录当前档案 Id 至 `UserSettings.Default`（`[JsonIgnore]` 运行字段）。游戏进程写入文件的唯一资格 = 启动参数指定的档案；任何写盘路径先校验当前档案 Id，不匹配即拒绝并记录日志（不变式 I1）。

## 5. 组件清单

| 组件 | 类型 | 职责 |
|---|---|---|
| `Settings/GameProfile.cs` | 新增 | 档案模型（4.2 结构、元数据、加载/序列化辅助） |
| `Settings/ProfileStore.cs` | 新增 | 档案的创建/读取/写入/枚举/删除；Id 与文件名；规范化；迁移（第 7 节）；锁文件（第 3 节）；AES 不变式（I2） |
| `Settings/LauncherSettings.cs` | **v1 不做**（实施裁决：窗口居中+固定尺寸足够，窗口位置持久化按 YAGNI 剔除；如需补，数据放 `%AppData%\FModel\Launcher.json`） |
| `Views/LauncherWindow.xaml(.cs)` | 新增 | 第一层窗口：档案列表（名称/目录/版本/最后使用/运行中标记）、双击/回车/«打开»、«+ 添加新游戏»、«删除»、«打开档案目录»、«打开上次游戏» |
| `ViewModels/LauncherViewModel.cs` | 新增 | 启动器数据与命令 |
| `App.xaml.cs` | 修改 | 入口分派（第 3 节表）；`--game` 路径加载档案后走现有 MainWindow 流程；启动器单实例互斥量与激活消息 |
| `Settings/UserSettings.cs` | 修改 | `Save()`/`Delete()` 改走 `ProfileStore`（写当前档案）；`PerDirectory` 退役；`CurrentDir` 序列化变动 |
| `ViewModels/ApplicationViewModel.cs` | 修改 | 移除 `AvoidEmptyGameDirectory` 弹窗路径（改为由 `--game` 驱动）；`Restart()` 拆出「启动子进程」与「返回启动器」 |
| `ViewModels/GameSelectorViewModel.cs` + `Views/DirectorySelector.xaml` | 修改 | 保持自动检测/手动添加逻辑，改造为启动器的「添加新游戏」对话框；「删除」行为改为删除档案 |
| `ViewModels/Commands/MenuCommand.cs` | 修改 | `Directory_Selector` 菜单项 →「返回启动器」 |
| `CliExporter.cs` | 修改 | 解析 `--game <档案Id>`；缺省用最近使用档案；锁冲突按退出码 1 处理 |

修改既有 upstream 文件时：新代码沿用相邻代码风格，diff 最小化（AGENTS 11）。

## 6. 数据流

1. **打开游戏**（启动器 → 游戏进程）：双击/回车/«打开» → `Process.Start(exe, "--game <Id>")`（复用现有 exe/dll 启动判定）。子进程：`InitializeSettings` → 加载档案 → 建 `MainWindow`（`OnLoaded` 既有初始化序列全部保留）。`ApplicationViewModel` 构造不再出现目录选择弹窗。
2. **添加新游戏**：启动器«+ 添加新游戏» → 复用现有 `DirectorySelector` + `GameSelectorViewModel`（自动检测 Epic/Steam/Riot/注册表 + 浏览器手动添加全部保留）→ `ProfileStore.Create`：同目录档案已存在 → 复用其设置只更新元数据；否则以**代码默认值**初始化 → 立即打开该游戏进程。
3. **保存**：保存点 = 现有全部 `UserSettings.Save()` 调用点 + 游戏窗口 `OnClosing` + `AppExit`；全部写**当前档案**，防抖不必要（单文件小写），直接写。启动器与游戏进程不写对方的文件。
4. **返回启动器**：游戏窗口菜单 → 保存当前档案 → 退出本进程 → 激活/拉起启动器。
5. **最近使用**：`LastUsedAt` 在档案内记录，由游戏进程在成功加载档案并**进入主界面时**写入（即需求"进入主界面后自动记录"）。启动器列表按各档案 `LastUsedAt` 排序；CLI 缺省档 = `LastUsedAt` 最大者。单一数据源，不跨进程共享"上次用哪个"指针，避免写冲突。
6. **切换游戏**：关闭当前游戏窗口 → 启动器（始终保持可用）→ 选另一个 → 新进程自动带该档案设置，全程无重启。
7. **主窗口拖入目录**（既有 `DropOverlay` 行为）：创建/复用同目录档案，并启动新的游戏进程打开它；当前窗口不动。同一游戏拖入（已有进程在跑）→ 锁冲突提示。

## 7. 迁移（一次性）

- 触发：启动器启动时检测旧 `AppSettings.json`（按当前构建分别对应 `AppSettings.json` / `AppSettings_Debug.json`）存在且含 `PerDirectory` 记录。
- 步骤：
  1. `PerDirectory` 每个条目 → 一个档案：目录级字段（含 AES）原样搬入 `Game`；`Settings` 快照初始化为**旧全局设置值**（保留用户现有导出选项等，不用代码默认值）。
  2. 旧 `GameDirectory/CurrentDir` 对应档案置为 `LastUsedAt` 最新（成为 CLI 缺省档与排序首位）。
  3. 旧文件改名备份为 `AppSettings_Legacy-<yyyyMMdd>.json`，之后不再读取。
  4. 记录迁移日志（数量、成功/失败条目、备份路径）。
- 边界：迁移只按当前构建对应的文件名进行；损坏条目跳过并计入日志，不拖垮整体；迁移后新建的**全新**档案才用代码默认值初始化。

## 8. CLI 兼容

- `--cli` 解析增加 `--game <档案Id>`（纯安全 token，不含路径；跨进程传参路径规则见 AGENTS 六，v1 不接收路径形式参数，若将来需要按路径打开其它机器拷贝的档案文件，通过环境变量传递）。
- 无 `--game` 时解析最近使用档案（`LastUsedAt` 最大）。
- 解析不到档案 → 明确错误信息 + 退出码 1（与现状一致），不静默回退默认值（AGENTS 1.3）。
- 锁检查同 GUI：目标游戏正被 GUI 使用 → 提示冲突（日志 + 退出码 1）。

## 9. 不变式

- **I1 只写自己的档案**：写入前校验当前档案 Id 与启动参数一致。
- **I2 AES 保底**：保存时若 `AesKeys`（主键 + 动态键）全空且磁盘上原档案非空 → 用磁盘原值，记警告日志。手动键入的 AES 不可能被空值覆盖。
- **I3 同目录 = 同档案**：创建前按规范化目录查 Id；命中即复用已存设置。
- **I4 外读校验**：`SchemaVersion` 未知或 JSON 解析失败 → 该档案标记损坏（启动器「损坏」+ 可删除；游戏进程加载失败 → 错误提示 + 退出码 1）。
- **I5 引用安全**：加载一律经 JSON 反序列化深拷贝；禁止把内存中 `AesKeys` 等对象引用直接存入档案缓存。

## 10. 错误处理

| 场景 | 行为 |
|---|---|
| 档案损坏 | 启动器标「损坏」可删除，不崩；游戏进程加载失败 → 提示 + 退出 |
| `--game` 指向不存在的档案 | GUI：提示 + 退出；CLI：退出码 1 |
| 同一游戏二次打开 | 锁冲突提示「该游戏已在运行」；CLI 退出码 1 |
| 游戏目录不存在 | 列表黄标提醒；打开后行为同现状（FModel 自身报错） |
| 锁文件残留（崩溃留尸） | PID 无存活进程 → 视为未锁定，可打开 |

## 11. 测试

- **ProfileStore 单元测试**：创建/枚举/重读 round-trip；同名目录规范化命中；损坏 JSON / 未知 SchemaVersion；删除；锁文件创建/冲突/残留（假 PID）/释放；迁移样本（含含 AES 的目录级字段 + 旧全局值快照）；不变式 I2 样例（空 AES 不覆盖）。
- **CLI 冒烟**（沿用现有 `Start-Process -Wait` 取退出码的测试脚本方式）：`--game` 指定 / 缺省最近使用 / 无档案可解析 / 锁冲突，四类场景断言退出码。
- **手工流程**：旧设置迁移 → 开游戏 → 改 AES + 导出设置 → 关（自动保存）→ 重开验证一致；双游戏并存各自设置独立；同游戏二次打开提示；关闭启动器后「返回启动器」自动拉起；加密游戏 AES 在多次开关切换后仍在（问题 3 回归）。

## 12. 合规要点（AGENTS.md 落地）

- 路径：一律 `Environment.SpecialFolder` / `Path.Combine` 组装；文档与实现不落盘符、不落具体游戏名（示例用占位符）。
- 跨进程比较/匹配路径先归一化（分隔符 + 大小写）——I3 的规范化即此规则。
- 跨进程传参不传路径，只传 Id（AGENTS 六）。
- 注释中文（fork 自有代码，向 upstream 提交则英文从简）。
- 命名宽度 = 职责宽度：`GameProfile` / `ProfileStore` / `LauncherWindow` / `LauncherViewModel` / `LauncherSettings`。
- upstream 既有文件改动保持原风格、diff 最小；涉及 upstream 同步时档案与启动器代码不受影响（不触碰 CUE4Parse）。

## 13. 已确认决策（与用户对齐记录）

| 决策 | 结论 |
|---|---|
| 第二层窗口进程模型 | 独立进程，一游戏一进程 |
| 双击 exe 入口 | 总是先显示启动器 |
| 档案存储位置 | 用户数据目录 `%AppData%\FModel\Profiles\`，一档案一文件 |
| 设置划分 | 所有 FModel 内可手动设置的字段都入档案；新档案初始为代码默认值；迁移档案初始为旧全局值 |
| 启动器实例数 | 单实例（重复点击 exe 激活已有启动器） |
| 启动器与游戏窗口 | 生命周期解耦；启动器可随时关闭；同游戏单实例，不同游戏可同时多开 |
