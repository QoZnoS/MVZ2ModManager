# MVZ2 Mod Manager

> 《Minecraft vs Zombies 2》的**启动前模组管理器**：在开游戏之前决定启用哪些 mod，
> 并提前告诉你"这么关会出什么事"。

> ⚠️ 非官方工具，与游戏原作者无关。仅适配 **0.7.x test-\***（Windows **x86** / IL2CPP / BepInEx 6 be.788）。

---

## 1. 它解决什么问题

BepInEx 的插件发现就是一句话：

```csharp
Directory.GetFiles(Path.Combine(BepInExRoot, "plugins"), "*.dll", SearchOption.AllDirectories)
```

**没有任何开关、白名单或配置项**（v5 / v6 都是；`.dll.disabled` 也不是官方约定，
只是这个 glob 只认 `.dll` 的副作用）。所以"启用哪些 mod"本质上只能是启动前改文件名。

能改名的工具其实有现成的（[Sevs Mod Manager](https://github.com/sevvy-wevvy/sevs-mod-manager) 等），
但**没有一个知道 MVZ2 的两件要命事**：

1. **依赖是声明式的、失败是静默的。** 这套仓库里 5 个内容 mod 全都
   `[BepInDependency(DSHCore, HardDependency)]`。关掉 DSHCore，BepInEx 不会崩，
   只是**把这 5 个插件一起丢掉** —— 表现为"功能莫名消失"，极难倒查。
2. **存档按命名空间逐个比对版本。** 关卡存档头里记着
   `[{"spaceName":"mvz2_lab","dataVersion":4}, ...]`，读档时按命名空间比对。
   禁用某个 mod 会让**用到它的存档直接读不进去**（数据没删，但进不去）。

本工具就是围绕这两点做的。

---

## 2. 三个核心能力

| 能力 | 怎么做的 | 在哪看 |
| --- | --- | --- |
| **插件元数据** | 用 `System.Reflection.Metadata` **直接读 PE 元数据**，不加载程序集（这些 dll 引用 IL2CPP interop，加载会出问题）→ 拿到 `[BepInPlugin]` / `[BepInDependency]` / `[BepInIncompatibility]` / `[BepInProcess]` | 已安装页的 `Plugin GUID` 列 |
| **依赖分析** | 缺硬依赖 / 硬依赖被禁用 / 重复 GUID / 声明互斥 / 进程名不匹配；禁用前还会算出**连带弄坏谁**（含传递依赖） | 顶部警告条 + `⚠ Issues` |
| **存档风险** | 解开 `.lvl`（gzip）取第一个 JSON 对象的 `identifiers`，得到"这份存档需要哪些 `命名空间@版本`"，再和当前启用集合比对 | 顶部警告条 + `⚠ Issues` + 禁用确认框 |

关于存档格式与判定规则的细节写在 [SaveCompatibility.cs](Core/SaveCompatibility.cs) 的类注释里。

---

## 3. 用起来

- **开关一个 mod**：点列表里的复选框（或选中后按空格）。
- **禁用前的拦截**：会弹一个框，列出"会被连带弄坏的插件"和"会读不进去的存档（含文件名）"。
- **总开关**：设置页的 `Enable mods`。它把 `winhttp.dll` 改名 —— 这是唯一一个
  "全部关掉且完全可逆"的手段（**不删任何 dll**）。
- **存档点（Loadouts）**：把当前"哪些开着"存成一份快照，随时切回去；
  每份快照还会**连 `BepInEx/config` 一起快照**，切回去时配置也一起还原。
- **日志页**：BepInEx 从**游戏启动第一行**就开始写 `LogOutput.log`，所以这里
  既是日志查看器，也顺带是加载进度看板（启动游戏会自动跳过来）。

游戏运行中插件 DLL 是被锁住的，所以**改开关前请关掉游戏**；
工具栏和状态栏都会提示这一点。

---

## 4. 构建 / 运行

```powershell
.\build.ps1                 # 构建（Debug）
.\build.ps1 -Release -Run   # Release 并启动
.\build.ps1 -Pack           # 框架依赖单文件 → ..\dist\  (~2MB，需 .NET 8 Desktop Runtime)
.\build.ps1 -Pack -SelfContained   # 免安装单文件 → ..\dist\  (~63MB，换机器就能跑)
.\build.ps1 -SelfTest       # 无界面自检，写 _selftest.txt
```

> 直接跑 `.ps1` 若被 ExecutionPolicy 挡住（仓库根的 `build.ps1` 也一样）：
> `powershell -ExecutionPolicy Bypass -File .\build.ps1 -Pack`

需要 **.NET SDK 8+**（目标框架 `net8.0-windows`，WinForms）。
**不需要**游戏目录、不需要 `runtime\`、不引用 DSHCore —— 游戏目录只在运行时由程序自己找。

### 游戏目录怎么定位

1. 设置页里选过并记住的路径（`%APPDATA%\MVZ2ModManager\state.json`）
2. 环境变量 **`MVZ2_GAME_DIR`**（沿用仓库既有约定）
3. 都落空 → 弹目录选择器

选定后会校验目录里有没有 `MinecraftVSZombies2_Data`，选错一层会被挡住。

---

## 5. 自检

```powershell
.\build.ps1 -SelfTest
```

不开窗，把**核心逻辑**跑一遍并落盘（退出码 0 = 全过）：

- 游戏目录识别、BepInEx 状态
- 每个 dll 的 PE 元数据是否读出 `[BepInPlugin]` GUID
- **`DependencyFlags` 的取值是否从这套 BepInEx 的 `BepInEx.Core.dll` 里读出来**
  （实测这套是 `HardDependency=1, SoftDependency=2`，不是从 0 开始 —— 猜错会导致
  把硬依赖当软依赖，所以宁可去读也不猜）
- 硬依赖是否解码正确、级联禁用是否算得出受影响者
- 所有 `.lvl` 头是否都能解析（gzip + `SerializableLevelControllerHeader` 假设是否还成立）
- 存档里出现的命名空间是否都能归属到某个已装 mod 或原版

游戏改版后这份自检会最先红，比在游戏里试出来快得多。

---

## 6. 数据放哪

| 位置 | 内容 |
| --- | --- |
| `%APPDATA%\MVZ2ModManager\state.json` | 本程序的偏好：游戏路径、主题 |
| `<游戏目录>\BepInEx\MVZ2ModManager\loadouts\` | 存档点 + `config_snapshots\<n>\` |
| `<游戏目录>\BepInEx\MVZ2ModManager\LogHistory\` | 最近 10 次日志归档 |

存档点放在游戏目录里是刻意的：换机器/换安装目录时"哪套 mod 开着"跟着走。

---

## 7. 目录结构

```
Core/                        无 UI 的业务逻辑
  AppState.cs                游戏路径与所有派生路径、设置读写
  Mvz2Catalog.cs             GUID ↔ 原生命名空间 对照表（新增 mod 在这里补一行）
  ModCatalog.cs              扫盘 + 读元数据 + 补命名空间（唯一入口）
  ModMetadata.cs             PE 元数据读 BepInEx 特性（不加载程序集）
  DependencyChecker.cs       依赖/互斥/重复/级联分析（纯函数）
  SaveCompatibility.cs       .lvl 存档头解析与风险判定
  ModInstaller.cs            装/卸/开关（改名实现）
  BepInExManager.cs          环境探测 + 总开关（**不含任何安装/更新逻辑**）
  BepInExConfig.cs           BepInEx .cfg 解析/写回
  SelfTest.cs                无界面自检
UI/
  MainForm.cs                自绘外壳：标题栏 + 标签条 + Play + 状态栏
  GamePickerForm.cs          选游戏目录
  Panels/                    Installed / Loadouts / Config / Logs / Settings
  Controls/                  自绘控件（按钮/下拉/输入框/滚动条/圆角）
Theme/ThemeEngine.cs         4 套配色 + 递归上色
```

---

## 8. 还没做的

按重要性排：

- [ ] **在线浏览/下载 mod**（上游的 Mods 页，依赖其站点，已删）
- [ ] **模组包导入导出**（上游的 Modpacks 页 + `.mvz2pack` 文件关联，已删；文件仍在
      `_sevs-mod-manager-main/` 快照里可参考）
- [ ] **UI 文案中文化**（目前沿用上游的英文文案，与仓库其它部分不一致）
- [ ] **首次使用引导**（上游的教程覆盖层绑在已删的 Mods 页上）
- [ ] 卸载时一并处理 `StreamingAssets/Mods/<命名空间>/` 资源目录
      （目前只处理 `BepInEx/plugins`，命名空间对照表已具备，接上即可）

---

## 9. 来源与许可

本工具**派生自 [Sev's Mod Manager](https://github.com/sevvy-wevvy/sevs-mod-manager)**
（MIT/GPL-3.0 作者 Cade Ayres 的作品）—— 复用了它的自绘控件、主题引擎与整体版面，
并在此基础上做了三件事：砍掉绑定其站点的部分、把加载器抽象收敛成 BepInEx、
加上 MVZ2 特有的依赖与存档分析。

按 GPL-3.0 发布（同本仓库）。原始许可证文本见 [LICENSE.txt](LICENSE.txt)，
上游源码快照留在 `_sevs-mod-manager-main/`（不参与构建）。
