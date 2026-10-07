# MVZ2 Mod Manager

> 《Minecraft vs Zombies 2》的**启动前模组管理器**：开游戏之前决定启用哪些 mod，
> 并提前告诉你"这么关会出什么事"。
>
> ⚠️ 非官方工具，与游戏原作者无关。仅适配 **0.7.x**（Windows **x86** / IL2CPP / BepInEx 6）。

---

## 为什么需要它

BepInEx 的插件发现就是一句 `Directory.GetFiles(plugins, "*.dll", AllDirectories)` ——
没有开关、白名单或配置项，所以"启用哪些 mod"本质上只能是**启动前给文件改名**。

而通用的改名工具不懂 MVZ2 的两件事：

1. **硬依赖会静默失效** —— 关掉 DSHCore，5 个内容 mod 会一起无声消失，表现为"功能莫名不见了"。
2. **存档按 `命名空间@版本` 逐个比对** —— 禁用某个 mod，会让用到它的关卡存档**直接读不进去**。

本工具就是围绕这两点做的。

---

## 功能


| 功能                | 说明                                                                                                                                                                                                                  |
| ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **启用 / 禁用 mod** | 给插件 DLL 改名（`a.dll` ↔ `a.dll.disabled`），**不删任何文件**，随手可逆。                                                                                                                                          |
| **依赖拦截**        | 直接读 DLL 的 PE 元数据（**不加载程序集** —— 这些 dll 引用 IL2CPP interop）拿到 `[BepInPlugin]` / `[BepInDependency]` / `[BepInIncompatibility]` / `[BepInProcess]`；禁用前先算出会连带弄坏哪些 mod（含传递依赖）。 |
| **存档风险拦截**    | 解开关卡存档（gzip + JSON）读它需要哪些`命名空间@版本`，列出这次禁用会让哪几份存档读不进去。可在设置页整体关掉。                                                                                                      |
| **快照点**          | 把"当前哪些 mod 开着"存成快照，随时切回去 —— 连`BepInEx/config` 一起快照、一起还原。                                                                                                                                |
| **模组包**          | 把插件 + 配置 + 资源整包导出成`.mvz2pack`，双击导入。只覆盖模组自己的目录，**绝不碰 `core/` `interop/` `patchers/`**。                                                                                                |
| **日志 / 问题报告** | 实时跟随 BepInEx 日志、归档最近 10 次、从崩溃堆栈里猜元凶、一键导出 zip 报告。                                                                                                                                        |
| **总开关**          | 设置页一键把`winhttp.dll` 改名 —— 唯一"全部关掉且完全可逆"的手段。                                                                                                                                                  |

界面为中文，**零第三方依赖**（只用 .NET 8 自带的东西）。

---

## 如何使用

- 点列表里的复选框开关 mod。**改开关前请先关掉游戏**（插件 DLL 被锁住）。
- 禁用 / 卸载前会弹确认框，列出"会被连带弄坏的插件"和"会读不进去的存档（含文件名）"。
- 直接把 `.dll` 拖进窗口就能装；卸载时单独问一次要不要连 `StreamingAssets/Mods/<命名空间>/` 一起清掉。
- 首次运行会有一段底部教练卡带你走一遍六个标签页。

六个标签页：**已安装 / 快照点 / 模组包 / 配置 / 日志 / 设置**。

---

## 构建 / 运行

```powershell
.\build.ps1                              # 构建（Debug）
.\build.ps1 -Release -Run                # Release 并启动
.\build.ps1 -Exe                         # 裸单个 exe → ..\dist\（0.4MB，需 .NET 8 Desktop Runtime）
.\build.ps1 -Exe -SelfContained          # 免安装裸 exe → ..\dist\（约 165MB，目标机器不用装 .NET）
.\build.ps1 -Pack                        # 与 -Exe 叠加再套一层 zip（附带 README / LICENSE，便于分发）
.\build.ps1 -SelfTest -ApplyRoundtrip    # 核心自检（元数据 / 依赖 / 存档 / 模组包）
.\build.ps1 -UiSelfTest                  # 界面自检（真的构造所有窗体并切一遍标签页）
.\build.ps1 -Screenshot                  # 每个标签页一张 PNG + 控件树，核对版面用
```

> **产物本身就是单个 exe。** publish 用的是 `PublishSingleFile`，所以那一个文件拷到哪里都能双击运行，
> 不需要 README / LICENSE 或者其它 dll 陪着；zip 只是为了把说明与许可一起分发才额外打的。

> 直接跑 `.ps1` 被 ExecutionPolicy 挡住时，用
> `powershell -ExecutionPolicy Bypass -File .\build.ps1 ...`

需要 **.NET SDK 8+**（目标框架 `net8.0-windows`，WinForms）。不需要游戏目录、不需要 `runtime\`，
也不引用 DSHCore —— 游戏目录只在**运行时**由程序自己找：

1. 设置页里选过并记住的路径（`%APPDATA%\MVZ2ModManager\state.json`）
2. 环境变量 `MVZ2_GAME_DIR`
3. 都落空 → 弹目录选择器

选定后会校验目录里有没有 `MinecraftVSZombies2_Data`，选错一层会被挡住。

---

## 数据放哪


| 位置                                            | 内容                                           |
| ----------------------------------------------- | ---------------------------------------------- |
| `%APPDATA%\MVZ2ModManager\state.json`           | 本程序的偏好：游戏路径、主题、是否提醒存档风险 |
| `%APPDATA%\MVZ2ModManager\Modpacks\mvz2\`       | 自建模组包（`.mvz2pack`）                      |
| `<游戏目录>\BepInEx\MVZ2ModManager\loadouts\`   | 快照点 +`config_snapshots\<n>\`                |
| `<游戏目录>\BepInEx\MVZ2ModManager\LogHistory\` | 最近 10 次日志归档                             |

快照点放进游戏目录是刻意的：换安装目录时"哪套 mod 开着"跟着走。
模组包放进 `%APPDATA%` 也是刻意的：它本来就是要**带到别的安装目录去用**的。

---

## 来源与许可

派生自 [Sev's Mod Manager](https://github.com/sevvy-wevvy/sevs-mod-manager) ——
复用了它的自绘控件、主题引擎与整体版面，并在此基础上做了三件事：砍掉绑定其站点的部分、
把加载器抽象收敛成 BepInEx、加上 MVZ2 特有的依赖与存档分析。

按 **GPL-3.0** 发布（同本仓库）。原始许可证文本见 [LICENSE.txt](LICENSE.txt)。

---

## AI 代码声明

**本项目的代码绝大部分由 AI 生成**（GitHub Copilot 等大语言模型）。
人类负责的是提出需求、审查结果、决定取舍，以及在真机上验证。

请按这个前提对待它：

- 已通过构建（0 警告 0 错误）、核心自检、界面自检，以及**打包后真机冒烟**；
  但**没有人工逐行审阅过**，不保证没有缺陷。
- 验证最薄弱的是"必须靠人手点击才能覆盖"的交互路径（例如列表列宽的拖动手势）。
- 报问题时建议附上 `--selftest` 生成的报告（含环境、模组元数据、存档扫描结果），
  能省掉大半来回。
