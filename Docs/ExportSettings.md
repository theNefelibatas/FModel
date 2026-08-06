# FModel 导出设置文档

## 1. 导出设置入口与持久化

- **UI 入口**：FModel 顶部菜单 Settings → 导出相关设置面板
- **UI 定义**：`FModel/Views/Resources/Controls/ExportOptionsControl.xaml`
- **UI 逻辑**：`FModel/ViewModels/ExportOptionsViewModel.cs`
- **配置持久化**：`%APPDATA%\FModel\AppSettings.json`（`FModel/Settings/UserSettings.cs`）
- **导出时装配**：`UserSettings.GetExportOptions()` → `ExportSessionViewModel.cs:203` → `Session.Options`

## 2. 导出设置完整清单

| # | UI 名称 | 配置字段 | 选项（= UI 显示） | 含义 |
|---|---|---|---|---|
| 1 | Output Directory | `OutputDirectory` | 路径 | 导出根目录 |
| 2 | Export Immediately | `ExportImmediately` | Enabled / Disabled | 导出后立即打开目录 |
| 3 | Mesh Format | `MeshExportFormat` | ActorX (psk / pskx) / glTF 2.0 (binary) / UEFormat (uemodel) / Universal Scene Description (usda) | 网格导出格式；UI 会按所选格式显示导入器链接（UEFormat → h4lfheart/UEFormat；ActorX → DarklightGames/io_scene_psk_psa） |
| 4 | Mesh Quality | `MeshQuality` | Highest Quality Only / Lowest Quality Only / All Qualities (Individual Files) | 导出哪些 LOD |
| 5 | Socket Format | `SocketExportFormat` | Export Bone Sockets as a Custom Chunk / Export Bone Sockets as Bones / Don't Export Bone Sockets | Socket 导出方式（仅 ActorX 时启用） |
| 6 | Compression Format | `CompressionFormat` | Uncompressed / Gzip Compression / ZStandard Compression | uemodel 文件压缩（仅 UEFormat 时启用） |
| 7 | Nanite Format | `NaniteMeshExportFormat` | Only Nanite LOD / Only Normal LODs / Nanite LOD first, then Normal LODs / Normal LODs first, then Nanite LOD | Nanite LOD 与普通 LOD 的取舍 |
| 8 | Texture Format | `TextureExportFormat` | PNG / JPEG / TGA / WebP | 纹理编码格式 |
| 9 | Texture Platform | `TexturePlatform` | Desktop / Mobile / Xbox One/Series / Playstation 4 / Nintendo Switch / Playstation 5 | 纹理解码平台 |
| 10 | Texture Quality | `TextureQuality` | 1-100（Slider） | JPEG/WebP 编码质量 |
| 11 | Save HDR as .hdr | `SaveHdrTexturesAsHdr` | Enabled / Disabled | HDR 纹理是否保持 .hdr 格式 |
| 12 | Export All Mips | `ExportAllTextureMips` | Enabled / Disabled | 开启时每个 mip 单独导出为独立文件（`_MIP0/1/...` 后缀）；关闭时只导最高分辨率 mip |
| 13 | Material Depth | `MaterialExportFormat` | Top Layer Only / All Layers (Without Referenced Textures) / All Layers (With All Referenced Textures) | 材质参数提取深度 |
| 14 | Auto-Export Attached Materials | `SaveEmbeddedMaterials` | Enabled / Disabled | 网格导出时连带导出引用材质与纹理 |
| 15 | Material JSON Format | `MaterialJsonFormat` | Properties JSON (WriteJson) / Compact JSON (parameter view) | 连带导出的材质 JSON 格式：Properties 与 "Export Folder → Properties" 链路输出一致；Compact 为参数视图 |
| 16 | Export Morph Targets | `SaveMorphTargets` | Enabled / Disabled | 是否导出 Morph Targets |

## 3. 导出链路与设置的影响

### 3.1 链路 A：右键文件夹 → Export Folder → Properties (.json)

```
FolderContextMenu → RightClickMenuCommand("Save_Properties")
  → (EAction.Export, EBulkType.Properties) → 输出目录 = PropertiesDirectory
  → CUE4ParseViewModel.ExtractFolder → BulkFolder
  → 每个资产 Extract(entry, Properties)
      → uasset: Provider.LoadPackage → UObject.Properties（属性 tag 原样）
      → GetDisplayData(saveProperties) → JsonConvert → WriteJson
      → JSON（列表格式）
```

**行为**：对 uasset 属性流的一比一还原，不受导出设置影响（输出目录除外）。**这是资产数据的权威视图**——例如网格资产的 `SkeletalMaterials[i].MaterialSlotName`（材质槽短名）、骨架资产的 `ReferenceSkeleton.FinalRefBoneInfo`（骨骼名+父索引）都在此完整可见。

### 3.2 链路 B：右键文件夹 → Export Folder → Models

```
FolderContextMenu → RightClickMenuCommand("Save_Models")
  → (EAction.Export, EBulkType.Meshes) → 输出目录 = ModelDirectory
  → CUE4ParseViewModel.ExtractFolder → BulkFolder
  → 每个资产 Extract(entry, Meshes)
      → uasset: Provider.LoadPackage（与链路 A 同一份解析）
      → CheckExport → USkeletalMesh → SaveExport → ExportSession
      → SkeletalMeshExporter（BuildFiles）
          ├─ SkeletalMeshDto（类型化转换）
          ├─ EnqueueMaterials → 材质/纹理连带导出（受设置控制）
          └─ IMeshExportFormat.BuildSkeletalMesh → 写入导出文件
```

**设置对链路 B 的分支影响**（同一份 uasset 解析，后续行为由设置决定）：

| 设置 | 分支行为 |
|---|---|
| **Mesh Format** | UEFormat → 单个 `.uemodel` 文件（所有 LOD 在同一文件内）；ActorX → 每 LOD 一个 `.psk`；glTF → `.glb`；USD → `.usda` |
| **Mesh Quality** | Highest/Lowest → 只导出 1 个 LOD；All → 导出全部 LOD（uemodel 仍为单文件） |
| **Nanite Format** | 决定导出普通 LOD、Nanite LOD 还是两者（含顺序） |
| **Auto-Export Attached Materials** | 开启 → 网格引用的每个材质走 `MaterialExporter`：输出材质 JSON（格式由 **Material JSON Format** 决定：Properties = 与 Properties 链路一致的 WriteJson 格式；Compact = 参数视图）；按 **Material Depth** 决定引用纹理的导出范围（纹理加入导出队列）；关闭 → 只导网格 |
| **Material Depth** | Top Layer Only → 只提取最外层 MIC 参数；All Layers (Without Referenced Textures) → 全层级参数；All Layers (With All Referenced Textures) → 全层级参数 + 引用纹理全部导出 |
| **Export Morph Targets** | 开启 → `.uemodel` 写入 MORPHTARGETS chunk |
| **Compression Format** | UEFormat 下：None → 不压缩；Gzip/ZStandard → 头部 IsCompressed=true 并按对应算法压缩 |
| **Socket Format** | 仅 ActorX 时生效（Custom Chunk / Bones / 不导出）；UEFormat 格式固定写入 SOCKETS chunk |
| **Export All Mips / Texture Format / Platform / Quality / HDR** | 影响纹理导出文件：All Mips 开启时每个 mip 一个文件（`_MIP` 后缀），否则每纹理一个文件；格式/平台/质量决定编码 |

### 3.3 链路 C：uemodel 导入（UE 插件）

```
UEFModelReader.cpp（读 chunks）→ UEFModelFactory.cpp（建资产）
  BONES → RefSkeleton | MATERIALS.name → MaterialSlotName | SOCKETS → Socket | LOD0 数据 → 网格
```

**行为**：忠实还原 `.uemodel` 文件内容——文件里有什么就建什么（LOD 数量、材质槽名、Socket、Morph 均来自文件本身）。

### 3.4 链路关系

- 链路 A 与链路 B 共享 `LoadPackage`（同一份 uasset 解析），差异发生在 B 的类型化转换与格式写入阶段
- 链路 C 的产出完全取决于链路 B 生成的 `.uemodel` 文件内容
- 材质 JSON：默认与 Properties 链路格式一致（Material JSON Format = Properties）；可选 Compact 参数视图

## 4. 使用建议（UE 导入管线）

| 设置 | 建议值 | 说明 |
|---|---|---|
| Mesh Format | UEFormat (uemodel) | 单文件、插件直导 |
| Mesh Quality | All Qualities | 全部 LOD 进一个网格 |
| Export All Mips | 关 | 避免纹理拆成多个文件；UE 导入时勾 Generate Mip Maps |
| Material Depth | All Layers (With All Referenced Textures) | 材质参数 + 引用纹理全集 |
| Auto-Export Attached Materials | 按需 | 连带 MIC JSON（与 Properties 链路同格式）+ 纹理 |
| Export Morph Targets | 开 | Morph 数据完整 |
| Compression Format | Uncompressed | 兼容性最好 |
| Save HDR as .hdr | 开 | HDR 保真 |
| Texture Format / Platform / Quality | PNG / Windows / 100 | 常规 |

## 5. 关键源码位置索引

| 功能 | 位置 |
|---|---|
| 导出设置 UI | `FModel/Views/Resources/Controls/ExportOptionsControl.xaml` |
| 导出设置逻辑 | `FModel/ViewModels/ExportOptionsViewModel.cs` |
| 配置持久化 | `FModel/Settings/UserSettings.cs` |
| 右键命令派发 | `FModel/ViewModels/Commands/RightClickMenuCommand.cs` |
| 文件夹菜单定义 | `FModel/Views/Resources/Controls/ContextMenus/FolderContextMenu.xaml` |
| 资产提取入口 | `FModel/ViewModels/CUE4ParseViewModel.cs`（Extract / CheckExport） |
| 导出会话 | `CUE4Parse-Conversion/ExportSession.cs` |
| 网格导出器 | `CUE4Parse-Conversion/Exporters/SkeletalMeshExporter.cs` |
| 材质导出器 | `CUE4Parse-Conversion/Exporters/MaterialExporter.cs` |
| 纹理导出器 | `CUE4Parse-Conversion/Exporters/TextureExporter.cs` |
| 网格 DTO | `CUE4Parse-Conversion/Dto/MeshDto.cs`、`MeshMaterialDto.cs` |
| 格式枚举 | `CUE4Parse-Conversion/Options/`（EMeshFormat、EMeshQuality、ENaniteMeshFormat、ESocketFormat、ETextureFormat） |
| 材质深度枚举 | `CUE4Parse/UE4/Assets/Exports/Material/EMaterialFormat.cs` |
| 压缩枚举 | `CUE4Parse-Conversion/Writers/UEFormat/Enums/EFileCompressionFormat.cs` |
| uemodel 写入 | `CUE4Parse-Conversion/Writers/UEFormat/UEModel.cs` |
| uemodel 读取（插件） | `UEFormat/plugins/unreal/UEFormat/Source/UEFormat/Private/Readers/UEFModelReader.cpp` |
| 资产创建（插件） | `UEFormat/plugins/unreal/UEFormat/Source/UEFormat/Private/Factories/UEFModelFactory.cpp` |
