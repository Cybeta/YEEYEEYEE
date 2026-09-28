# YEEYEEYEE 架构

> 当前基线：2026-09-29

工程、程序集与协议标识为 `DreamForge`。本文以源码为事实来源；产品方案见 [01](01_Project_Plan.md)，小目标与验收见 [PROGRESS](PROGRESS.md)，消息契约见 [protocol/PROTOCOL.md](protocol/PROTOCOL.md)。

## 解决方案与运行方式

| 项目 | 框架与职责 |
|---|---|
| `DreamForge.Core` | `net10.0`；协议、Job 状态机、能力与权限判定、引用图 |
| `DreamForge.Host` | `net10.0`；执行编排、SQLite Job、ComfyUI、轮询与回调签名 |
| `DreamForge.Desktop` | `net10.0-windows`；WinForms 自绘画布、Agent、设定库、工作树、技能与插件，当前主端 |
| `DreamForge.Web` | ASP.NET Core；作业服务、回调、静态前端托管及 HTTP/WebSocket 桥接 |
| `DreamForge.Canvas` | 独立 React/Vite 工程；当前为 div 卡片画布，`dist` 单独构建，不在解决方案内 |
| `DreamForge.Core.Tests` / `DreamForge.Agent.Tests` | 控制台断言式测试 |

`DreamForge.Mcp` 是独立只读 stdio 服务，未接入桌面端，也不在 `DreamForge.slnx`。依赖关系为 Host → Core，Desktop/Web → Host；Desktop 不引用 Web 或 Mcp，不使用 WebView2。桌面端是权威数据源，Web 当前是镜像和引用版本回写端。

## 领域数据与三种关联

画布 JSON 当前保存 `Nodes`、`Edges`、`Entities`、`WorkTree`。`Entities` 与 `WorkTree` 仍是画布级数据，项目级共享资源库属于后续迁移。

| 字段 | 职责 | 约束 |
|---|---|---|
| `ParentNodeId` / `parentTarget` | 画布父子关系和布局归属 | 不表示叙事状态，也不表示资源来源 |
| `WorkTreeItemId` / `workTreeTarget` | 工作树章节或能力的叙事锚点 | 关联不存在时拒绝创建/更新；自动同步待实现 |
| `References[]` / `entityTargets` | 实体、变体、可选视觉版本 | 一个分镜可引用多个实体；持久化使用稳定 ID |
| `SourceEntityId` 等 | 工作树条目的视觉来源 | 只表达来源，不触发自动复制或同步 |

产品规则与当前 Agent 提示词要求角色、场景、道具不创建常驻资源画布节点，由分镜通过 `References`/`entityTargets` 引用，按需临时展开可视化。旧 `Character`、`Scene`、`Prop` 枚举及创建入口仍保留，旧节点尚未迁移，不能声称代码已全面禁止资源节点。工作树写剧情向设定；资源库写跨章节视觉设定；节点保留自身文本、位置、附件、生成历史和引用。

模型已有 GUID，但 Agent 仍支持名称或短 ID 解析，跨画布合并、旧 ID 映射和歧义拒绝尚需统一。`WorkflowCanvasState.Entities` 的“项目级设定库”源码注释与实际画布级持久化不符；`WorkTreeItemId` 的“跟随项目树更新”注释也不代表同步引擎已实现。本轮仅记录差异，不改源码。

## 企划、章节泳道与成品下挂

目标模型为全局企划 → 章节 → 分镜 → 成品。全局企划是单份全局约束，位于章节泳道之外；章节泳道承载本章章节节点、分镜和成品；分镜横向排列，成品按分镜分组横向排列并显示在分镜下方。资源引用条属于节点内部的临时展开区域，不是独立资源节点。

当前 `WorkflowCanvasControl` 有 `ChapterKeyOf`、`ChapterBounds`、`ArrangeChapter`、节点坐标和拖动；`AgentActions.PlaceNewNode` 会对章节区块调用排版。现有实现仍按章节块和节点分类计算位置，尚未持久化“自动位置/手动位置”来源，也没有专门的分镜成品下挂布局。新增或局部排版必须在后续实现中限制于目标章节，保留其他章节和用户手动坐标。

## 多视图与选择联动

TS 画布当前有 `chapter` / `overview` 两种显示模式、章节筛选、引用折叠和浏览器选择状态；`NodeProjection` 把节点投影为 `recordType`、位置、章节、父节点和引用缩略图，并将未出现在画布的工作树章节投影为章节记录。当前布局由前端 `computeLayout` 统一计算，场景初始化或重置会清空选择和折叠状态。

目标多视图共享记录 ID：章节视图过滤章节并保留全局企划，总览视图显示跨章节关系，工作树和资源库视图提供反向定位，成品视图按分镜显示下挂产出。选择工作树条目应定位 `WorkTreeItemId`，选择实体/变体/版本应定位 `References[]`，选择节点应显示叙事锚点、引用和版本。`canvas/selection.changed` 只传递上下文，宿主必须重新鉴权。

## 桌面与 Web 数据通路

```text
Desktop 状态 → NodeProjection → POST /api/canvas/scene
             → Web HostBridge → WebSocket /ws/canvas → host/scene.reset
浏览器换/锁引用版本 → canvas/resource.replace.request → Web 队列
Desktop 每 500ms GET /api/canvas/resource-replace/next
             → 校验并替换引用 → 保存、revision++、重新推送 scene
             → POST /api/canvas/resource-replace/result → host/resource.replace.result
```

桌面地址硬编码为 `http://localhost:5000`；Web 未启动时推送失败不阻断桌面使用。当前缺口是 `asset://` 缩略图没有浏览器资产 HTTP 端点，`POST /api/canvas/nodes` 没有桌面调用方，`canvas/selection.changed` 尚无 HostBridge 业务分支，Web 不能独立完成创作。

## Agent、执行与技能

桌面 Agent 使用独立 JSON actions 协议，提供 Ask、AutoStage、ReadOnly 三种模式。Ask 先预览再确认，AutoStage 准备动作、生成暂存预览并通过统一保存入口提交，ReadOnly 不提交。`markVersionAdopted` 仅 JSON 布尔 `true` 生效；提交前快照只支持单机且要求修订号未变化。

Job 按 `Queued → Running/Cancelled`、`Running → Cancelling/Succeeded/Failed`、`Cancelling → Cancelled` 转换。文本支持 OpenAI 兼容、Anthropic Messages 和本地草稿兜底；图像支持 OpenAI 兼容与 ComfyUI，任务和产物写入 SQLite；视频未实现。生成技能、内置技能、角色能力和插件的边界见 [03](03_Skill_System.md)。

## 删除与存储边界

当前实体删除逻辑会统计当前画布中的引用并弹出警告，但仍允许删除实体，引用会失效；变体删除只限制每个实体至少保留一个变体。`StorageMaintenance` 能跨保存画布和当前画布统计 `asset://` 图片引用，并把未引用图片移入回收站。完整实体/变体删除保护还必须覆盖画布标签、草稿、历史版本、任务输入和任务产物，并支持迁移、恢复和最终清除前复验。

项目根保存 `project.json`、`canvases/`、`assets/`、`skills/`、`plugins/`、`jobs.db`、`last-canvas.json`、`canvas-tabs.json`、`workspace.json`；程序目录保存 `ai-config.json`、`recent-projects.json` 和诊断日志。模型密钥以当前用户 DPAPI 密文保存。显式保存、标签切换/关闭、窗口关闭和项目切换触发落盘，没有定时自动保存。

## 协议边界

`DreamForgeProtocol v1` 用于 Host/Web/TS 与测试，共 17 种消息，校验版本、方向、UUID、时间戳和能力声明；协议细节和实现差异保留在 [协议文档](protocol/PROTOCOL.md)。WinForms 画布不直接使用该信封，而是通过 HTTP 推送投影并轮询引用替换请求。

当前 HostBridge 初始化时主动发送 `host/init`；收到 `canvas/hello` 只设置 ready，尚未按握手载荷版本重新初始化。接收分支处理 hello、invoke、resource.replace、job.cancel；op.batch、undo/redo、selection 和 diagnostic 尚无业务处理。资源替换已在 C# 与 TS 两侧实现，但共享夹具尚未补齐；TS 的 UUID 等细粒度校验也未完全对齐 C#。

## 项目级资源迁移与版本保护

工作树与资源库继续共存；迁移目标只把视觉资源提升为项目级共享事实，不把叙事状态并入资源库。迁移前依次完成稳定 ID/模型、引用扫描、删除保护、回收站和版本锁定验证。扫描范围包括保存画布、打开标签、草稿、历史版本、任务输入与产物；迁移需提供旧 ID 映射、备份、失败回滚及重复运行不重复建资源的保证。

`ReplaceReferenceVersion` 已校验节点锁定、实体/变体存在及版本归属，`VariantVersionId=null` 跟随当前内容。另一路 `ResolveReference` 在变体缺失时退回首个变体，锁定版本缺失时退回当前内容；这是现存宽松解析，与目标“锁定版本不漂移”有差距。后续应提示失效并阻止误用，而不是把已有版本字段当作完整保护。

## 方案与现状的边界

待完成的是稳定 ID 的全链路约束、工作树单向同步、章节泳道和局部布局保护、完整多视图联动、实体删除保护、项目级资源迁移、Web 独立创作及身份协同。桌面 `HighlightWorkTreeItem` 已能按工作树版本的来源字段高亮引用节点，但尚不是按 `WorkTreeItemId` 对章节/能力的完整定位。

架构图保留在 [04](04_Architecture_Mindmap_Mermaid.md)，与本文分担图示和事实说明；故障记录合入 [README](README.md#故障案例-新建项目被拒绝访问)。推进顺序与验收统一见 [PROGRESS](PROGRESS.md) 的目标 0–8。
