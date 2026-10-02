# YEEYEEYEE

> 版本 0.1.1 · 基线 2026-10-02 · YES 工程师 · YES 艺术家

面向 AI 创作的 Windows 桌面工作区。主线是 **企划 → 章节 → 分镜 → 成品**：先把设定整理成条目，再把分镜挂到章节上，出图之后挑一张收进节点，最后串成成品。Agent 可以把一句创意铺成整条链路，也可以只在某一步上手。

角色、道具、场景不占画布节点——分镜通过引用指向它们，需要看时再临时展开。一份设定改了图，所有引用它的分镜会被标出来。项目就是一个文件夹，画布、资产、技能都在里面；密钥按平台加密后存在用户配置目录，不上传任何地方。

![工作台](docs/screenshots/02-workbench.png)

## 主要能力

| 能力 | 说明 |
|---|---|
| 画布 | 节点、连线、拖动、缩放平移；章节网格与泳道布局、泳道内局部重排；可切到时间轴与剧本视图，读的是同一份数据 |
| Agent | Ask / AutoStage / ReadOnly 三种模式，13 种 action；准备动作 → 预览暂存 → 统一保存；模型选择与用量显示 |
| 出图批次 | 一次出 1–6 张并在点下去之前算出花费；候选图在节点上方实时显示各自进度，挑一张收进节点，其余移入回收站 |
| 出图开奖 | 可选的出图观感。出完不直接摊开缩略图，而是留一排背面朝上的卡，点开走一段全屏揭晓再挑 |
| 出图判档 | 可选的图质量分档（设置 → 生图与生视频 → 出图观感，默认关，**要多花一次模型调用**）：一批图出好后，**视觉模型照着你写出去的提示词与负面提示词给每张打一个 0–10 的分**，分数换成档位（**9 分以上红、7–8 金、5–6 紫、3–4 蓝、2 分以下白；红最好、金第二**）。评审看四件事：贴合度、崩坏（多余手指 / 肢体断裂）、穿帮（多出来的东西 / 光影矛盾）、**角色与物体互相嵌进去**；提示词里还带着负面词原文与**这一步在节点树里的位置**。**踩中负面提示词的那张是裂纹卡**（卡面裂开 + 牌上写命中了哪条），它的分数再高也不计入预兆。本地只做**技术筛查**（读不出 / 纯色 / 尺寸不对 → 客观坏图），**判不了的如实说「不判」而不是猜**；分数不完整或越界一律作废。档位是**模型的判断，不是客观结论**，界面上明写这一点 |
| 技能与站点池子 | 站点是一级实体（一家站有哪些能用的池子），调用前选池子并记住上次；探测池子全程只读 |
| 智能导入 | 给一个接口说明网页，自动建出 api 生图 / 生视频技能与池子，输密钥后跑最小测试并查余额 |
| 应用内更新 | 启动时静默检查最新发行版，可应用内下载升级，退出后由脚本替换程序目录并重启 |

工作树保存叙事条目，设定库保存实体、变体与视觉版本，两边都保留。要注意它们是三份不同的数据：`WorkTreeItemId` 是叙事锚点，`References` 是视觉引用，`ParentNodeId` 只管画布布局，不能互相替代；字段存在也不等于同步已实现。具体边界以[架构](02_Architecture.md)为准。

### 出图开奖

开奖按二游抽卡的分拍走：蓄势（徽标呼吸、光点往中心收）→ 爆发（竖光柱撑开、全屏一闪）→ 卡片从中心飞出 → 依次翻面 → 挑一张。卡片是黑白卡纸加自有的 ◈ 徽记，颜色全在卡背后那团缓缓流动的光晕里；排列按张数定（六张就是上下各三张），卡片固定 2:3 竖长方形。

![出图开奖](docs/screenshots/11-gacha-reveal.png)

开启判图质量档之后，每张卡右下角多一枚档位牌（`红 9/10`），抬头多一行免责声明；许愿那一拍的圆形形象**自带九家的**（`provider-art/` 里是出图接口生成的自创角色），也可以换成你自己的图，或在设置页点一下用当前图像链路重画。判定用的不是这一批里的名次，而是**照着你写的提示词与负面提示词给的绝对分**——名次的问题在于：同一批六张都很差时也一定有一张「红」，而那张牌说的是「这批里最好」，不是「真的好」。分数换成档位后同时决定光点数量（红 12 到白 3）、卡背后光晕的亮度与整批入场的强度；**踩中负面提示词的卡面会裂开**，且不计入预兆。

![判图质量档](docs/screenshots/16-gacha-graded.png)

每一拍的画面都是代码画的，没有引入任何第三方 logo、拟人形象或现成演出素材。逐拍说明与参数取舍见[使用说明](USAGE.md)。

## 快速开始

需要 Windows 与 .NET 10 SDK。在仓库根目录执行：

```powershell
dotnet build YEEYEEYEE.slnx
dotnet run --project YEEYEEYEE.Desktop.Avalonia
```

首次启动停在启动页，选定项目之后才加载画布与设置。进工作台后在设置里填 Endpoint、Model 与 ApiKey 就能出图。密钥按平台加密落盘（Windows 用当前用户 DPAPI，macOS 用钥匙串，都没有时退到本机密钥文件 + AES-GCM），绑定账户与设备，换机器或换系统要重新填一次。

可选的 Web 镜像需要先构建前端再启动 Web：

```powershell
npm.cmd --prefix YEEYEEYEE.Canvas install
npm.cmd --prefix YEEYEEYEE.Canvas run build
dotnet run --project YEEYEEYEE.Web
```

浏览器访问 `http://localhost:5000`。桌面端向 `/api/canvas/scene` 推送全量投影，并每 500ms 轮询引用替换请求；Web 没起来时推送失败不影响桌面使用。

<details>
<summary>从 DreamForge 时期升级</summary>

早期代号 DreamForge 已全量替换为 YEEYEEYEE，但保留了两条回退读取，所以已有密钥与项目不会因为改名而「消失」：

- 目录：若 `%LOCALAPPDATA%\DreamForge`（或 `文档\DreamForge\Projects`）还在、而新目录尚未建立，程序继续用旧目录；把旧目录改名成新名字即完成迁移。
- 环境变量与配置键：`YEEYEEYEE_*` / `YEEYEEYEE:*` 优先，取不到时回退读 `DREAMFORGE_*` / `DreamForge:*`，写在启动脚本或 CI 里的旧覆盖项不会静默失效。
- 配置位置可用 `YEEYEEYEE_CONFIG` 指定到别处，默认在 `%LOCALAPPDATA%\YEEYEEYEE`。

</details>

## 还没做的

- 视频生成只有枚举与提示词条目，执行方未接入；池子可以登记，选了会明确拒绝而不是偷偷起任务。
- Web 能镜像桌面画布、能请求替换引用版本，不能独立完成创作。
- 项目级资源库（多画布共享同一份实体）迁移未完成，`Entities` 仍随每张画布保存。稳定 ID、引用扫描、删除保护、回收站与版本锁定都在，但迁移本身还没走完。
- 账号、房间协同、配额与审计未实现。`YEEYEEYEE.Mcp` 是独立只读服务，未接入桌面端。
- 多端协同（目标 8）未开始。按依赖推进的小目标与验收见 [PROGRESS](PROGRESS.md)。

## 仓库结构

| 目录 | 职责 |
|---|---|
| `YEEYEEYEE.Core` | 作业、协议、引用图、访问策略等核心契约 |
| `YEEYEEYEE.Host` | 单机执行服务、ComfyUI 提供方、SQLite 任务库 |
| `YEEYEEYEE.Desktop.Core` | 画布模型、存储、迁移、项目库等基础设施 |
| `YEEYEEYEE.Desktop.Shared` | 无界面逻辑：Agent、画布计算、AI 提供方、技能 |
| `YEEYEEYEE.Desktop.Avalonia` | 桌面主端，仓库里唯一的界面端 |
| `YEEYEEYEE.Web` | Web 镜像服务与 `/api/web` 创作接口 |
| `YEEYEEYEE.Canvas` | React/Vite 前端，产物单独构建，不在解决方案里 |
| `YEEYEEYEE.Mcp` | 独立只读 MCP 服务，不在解决方案里 |
| `protocol/` | Canvas ⇄ Host v1 消息契约与夹具 |
| `tools/` | 发布脚本 |

依赖是单向的：`Core` → `Host` → `Desktop.Core` → `Desktop.Shared` → `Desktop.Avalonia`，没有任何项目反向引用界面端。两份共享代码（`Desktop.Core` 与 `Desktop.Shared`）是唯一来源，界面只调用它们。详见[架构](02_Architecture.md)。

## 文档

| 文档 | 职责 |
|---|---|
| [使用说明](USAGE.md) | 从启动到出图的完整手工流程，配各页面截图 |
| [01 产品决策](01_Project_Plan.md) | 产品目标、两轴分工与待实现方案 |
| [02 架构](02_Architecture.md) | 源码事实、架构图、数据与迁移边界 |
| [03 技能](03_Skill_System.md) | 生成技能、站点与池子、角色能力与插件 |
| [04 架构导图](04_Architecture_Mindmap_Mermaid.md) | 模块关系的 Mermaid 图 |
| [PROGRESS](PROGRESS.md) | 按依赖推进的小目标、验收与逐轮验证记录 |
| [protocol/PROTOCOL](protocol/PROTOCOL.md) | Canvas ⇄ Host v1 消息契约与实现缺口 |

## 开发与验证

2026-10-02 基线：解决方案构建 0 错误；Agent 202 项、Core 29 项、Migration `8/8`、G6V1、Canvas TS 44 项、Web HTTP 回归全部通过。

```powershell
dotnet run --project YEEYEEYEE.Agent.Tests
dotnet run --project YEEYEEYEE.Core.Tests
dotnet run --project YEEYEEYEE.Migration.Tests
dotnet run --project YEEYEEYEE.G6V1.Tests
dotnet run --project YEEYEEYEE.Web.Tests
npm.cmd --prefix YEEYEEYEE.Canvas test -- --run
```

`YEEYEEYEE.slnx` 只包含 6 个生产项目，测试项目要单独运行——所以「解决方案构建通过」覆盖的是生产代码，测试项目的编译错误不会被它拦到。

发版用 `tools/publish-release.ps1`：按项目文件里的版本号打出一个便携 zip，默认框架依赖（约 12 MB，目标机器需要 .NET 10 运行时），加 `-SelfContained` 则把运行时一起打进去。把 zip 上传到对应的 GitHub 发行版，应用内升级下载的就是它。

## 已知问题

### 新建项目被拒绝访问

现象是 `项目创建失败：Access to the path '<项目父目录>\<项目名>' is denied.`。2026-09-28 的历史排查确认，那次实例由 IDE 沙箱启动，目标目录不在允许范围（进程链为 IDE → shell → 沙箱宿主 → shell → dotnet → Desktop）：同一路径在沙箱内写删均被拒，但工作区内的 `Directory.CreateDirectory` 成功。

代码侧已做两件事：`AppPaths.CanWriteDirectory` 真的建目录、写文件再删掉来探针；启动页在失败时提示可能是权限或安全策略拦截。绕过办法是直接用资源管理器启动、选一个可写的目录，或把目标目录加进 IDE 的允许列表。

同一个错误也可能来自普通目录权限或安全软件，不能把所有拒绝访问都断定为沙箱问题。先确认启动方式与目标目录是否可写，再查 ACL 与系统策略。

<details>
<summary>那次排查留下的现场记录</summary>

目标目录 ACL 允许修改、无 Deny ACE，无只读 / 重解析 / 加密标记；磁盘为 NTFS 且健康，受控文件夹访问关闭。同盘此前有成功创建的项目。沙箱允许清单的 104 条路径都在系统盘，没有目标盘路径。这些是历史现场记录，未在后续轮次重新采集。

</details>
