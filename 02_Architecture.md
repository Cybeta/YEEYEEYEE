# YEEYEEYEE 架构文档（工程标识 DreamForge）

> 版本：v2.1（按实际代码校正；追加第七十八轮的 Web 画布通道、章节分块排版、资源引用新写法） · 2026-09-28
> 本文只描述**代码里真实存在**的结构、接口与边界；未实现的东西在第十节明确列出。
> 上一版（v1，2026-09-25）描述的是"六层架构 + Avalonia WebView + 房间服务 + Yjs"的设想，与实现不一致，已作废。

---

## 一、解决方案与分层

### 1.1 实际项目清单

`DreamForge.slnx` 只包含 6 个项目（`DreamForge.slnx`）：

| 项目 | 目标框架 | 实际职责 |
|---|---|---|
| `DreamForge.Core` | net10.0 | 纯契约与领域模型：协议常量、Job 状态机、Token/Capability 枚举、权限判定、引用图判定。**无第三方依赖** |
| `DreamForge.Host` | net10.0 | 执行与任务编排：Job 存储（SQLite）、单机执行服务、ComfyUI Provider、外部任务轮询与进度监听、画布桥接抽象、回调签名 |
| `DreamForge.Desktop` | net10.0-windows | 唯一交付给用户的客户端：WinForms 主程序 + 自绘画布 + Agent 面板 + 设定库 + 工作树 + 技能/插件 |
| `DreamForge.Web` | net10.0 (Web SDK) | 作业服务宿主：DI 组装 Core/Host 的执行链路，暴露 ComfyUI 回调端点与 `/health`；**并托管 `DreamForge.Canvas` 的 `dist`（静态文件 + `/ws/canvas` WebSocket + `/api/canvas/*` 推送端点）**，自身不产生画布数据，只做转发与广播 |
| `DreamForge.Core.Tests` | net10.0-windows | 控制台断言式测试：协议、权限、Job、持久化、ComfyUI、回调签名、资源版本替换（19 项）。**因校验桌面端 `WorkflowCanvasState`，已同时引用 Core + Host + Desktop**（`EnableWindowsTargeting`） |
| `DreamForge.Agent.Tests` | net10.0-windows | 控制台断言式测试：Agent 协议解析、批量试算、引用/版本/工作树、附件、DPAPI、流式解析 |

**不在解决方案里**：`DreamForge.Mcp`（能独立编译运行，但未被任何项目引用，见第十节第 6 项）、`DreamForge.Canvas`（TypeScript/Vite 工程，见 4.3）。

### 1.2 真实引用关系

```
Core        ← 无引用
Host        → Core
Web         → Host（传递 Core）
Desktop     → Host（传递 Core）
Core.Tests  → Core + Host + Desktop
Agent.Tests → Desktop
Mcp         → 无引用（自包含）
```

关键结论：

- **Desktop 引用 Host，是在同进程内使用**（`DesktopExecutionHost.cs`），不是"客户端 + 服务端"两个进程。
- **Desktop 不引用 Web、不引用 Mcp**：在 `DreamForge.Desktop\*.cs` 中检索 `DreamForge.Web`、`WebCanvasTransport`、`DreamForge.Mcp` 等标识符为 0 命中。两者的联动只走 HTTP（见 4.4）。
- **Core.Tests 现在引用 Desktop**：新增的 `ResourceReplaceState` 用例直接构造 `WorkflowCanvasState` / `WorkflowNode` / `NodeReference`，因此该项目改为 `net10.0-windows` 并打开 `EnableWindowsTargeting`。

### 1.3 分层（按代码实际形态）

```
┌─ 表现层（Desktop，WinForms 自绘）───────────────────────┐
│ WorkflowCanvasControl（画布）· MainForm（各面板）· AgentPane │
├─ 应用/编排层（Desktop）────────────────────────────────┤
│ AgentActions（提议→审批→应用）· PendingChanges（虚影/快照）  │
│ Skills（技能执行）· Plugins（插件宿主）· CanvasCommandService │
│ NodeProjection（画布 → 协议 records 投影）· Web 推送/轮询     │
│ DesktopExecutionHost（把 Host 拉进本进程）                 │
├─ 领域层（Core）────────────────────────────────────────┤
│ Job 状态机 · Capability/Token 枚举 · AccessPolicy · 协议校验 │
├─ 适配层（Host）────────────────────────────────────────┤
│ SingleMachineExecutionService · SqliteJobStore            │
│ ComfyUiExecutor / ComfyUiProvider（HTTP + WebSocket）      │
│ ExternalTaskPoller · HmacCallbackVerifier                 │
└────────────────────────────────────────────────────────┘
```

---

## 二、协议：两套并存，别混用

代码里有**两套互不相关**的协议，文档必须分开写：

### 2.1 宿主协议 `DreamForgeProtocol`（Core 定义）

`DreamForge.Core\Protocol.cs`：`Version = 1`，信封字段为 `v / id / type / ts / payload`，并**白名单校验方向**（`Protocol.cs:8-12`）：

| 方向 | 消息 |
|---|---|
| canvas → host（9） | `canvas/hello`、`canvas/op.batch`、`canvas/undo.request`、`canvas/redo.request`、`canvas/invoke.request`、`canvas/job.cancel.request`、`canvas/selection.changed`、`canvas/resource.replace.request`、`canvas/diagnostic` |
| host → canvas（8） | `host/init`、`host/op.batch`、`host/scene.reset`、`host/undo.result`、`host/job.update`、`host/capabilities`、`host/resource.replace.result`、`host/error` |

真实校验规则（不是注释）：方向不符抛 `PROTOCOL_DIRECTION_MISMATCH`，版本不符抛 `PROTOCOL_VERSION_MISMATCH`，字段缺失/非法 GUID/非整数时间戳抛 `PROTOCOL_MALFORMED`；`canvas/undo.request` 与 `canvas/redo.request` 必须 `localOnly=true`；`canvas/invoke.request` 必须有非空幂等键；`canvas/resource.replace.request` 的 `recordId`/`entityId`/`variantId` 必须是合法 uuid，`variantVersionId` 允许 `null` 或合法 uuid；`host/capabilities` 不得超出服务端声明集（否则 `PROTOCOL_UNAUTHORIZED`）。

**谁在用**：`DreamForge.Host\HostBridge.cs`、`DreamForge.Web\WebCanvasTransport.cs`（浏览器 WebSocket 双向传输）、`DreamForge.Canvas\src\Protocol\*`（TS 侧同构实现，浏览器里走 `/ws/canvas`，WebView 里仍保留 postMessage 分支）与控制台测试。**Desktop 不使用它**（WinForms 端没有 WebView2，也没有任何 postMessage 通道）。

### 2.2 Agent 协议（Desktop 定义）

Agent 与模型之间用 JSON 代码块交换"改动提议"（`{"actions":[…]} / {"ask":{…}}`，13 种 kind），见 [03_Skill_System.md](03_Skill_System.md) 与 `AgentActions.cs` / `AgentPane.cs`。这是**当前唯一真正驱动画布改动**的协议。本轮新增 `entityTargets`（一组设定名，落到节点引用，用来表达"这一镜出现谁、在哪、用什么"），并在提示词里明确**角色/场景/道具不再建画布节点**。

---

## 三、进程与宿主拓扑（现状）

```
┌──────────────────────────────────────┐
│ DreamForge.Desktop.exe（唯一交付端）    │
│ WinForms / STAThread                  │
│ ├ WorkflowCanvasControl（自绘画布）      │
│ ├ AgentPane（模型对话 + 审批）           │
│ ├ DesktopExecutionHost（进程内执行链路）  │
│ └ jobs.db / project.json / assets …    │
└───────┬──────────────┬───────────────┘
        │ HTTP + WS    │ HTTP（推送投影 + 每 500ms 轮询替换请求）
        ▼              ▼
 本地 ComfyUI      DreamForge.Web（:5000，可选）
 127.0.0.1:8188    ├ SingleMachineExecutionService / SqliteJobStore
        ▲          ├ ExternalTaskPoller
        │          ├ 静态托管 DreamForge.Canvas/dist
 外部模型 API       ├ GET /ws/canvas（HostBridge 广播）
（OpenAI 兼容 /     ├ POST /api/canvas/scene | nodes
  Anthropic）      ├ GET|POST /api/canvas/resource-replace*
                   └ POST /callbacks/comfyui/{userId} · GET /health
                            ▲
                            │ WebSocket /ws/canvas
                      浏览器打开 http://localhost:5000（TS 画布）

另有：DreamForge.Mcp.exe（stdio 只读工具服务，独立运行，未与 Desktop 连接）
      DreamForge.Canvas 既能被 Web 托管为浏览器画布，也能被 WebView 承载（main.tsx 两种传输分支）
```

### 3.1 关键边界（代码事实）

- 桌面端与 Host 执行链路**同进程**：`DesktopExecutionHost` 直接构造 `SqliteJobStore` 与执行服务；配置了 ComfyUI 时会启动 `ExternalTaskPoller` 后台轮询（间隔 2 秒）。
- 桌面端与 Web **没有程序集引用**，只有 HTTP 联动：Desktop 每 500ms 轮询 `GET /api/canvas/resource-replace/next` 取画布改动请求，并把节点投影推给 `POST /api/canvas/scene`；Web 收到后用 `HostBridge` 广播给 WS 上的浏览器画布，并把执行结果回投 `POST /api/canvas/resource-replace/result`（见 4.4）。
- Web 宿主**不产生画布数据**：它只托管前端静态文件、转发/广播消息与跑作业链路；数据来源始终是桌面端推送的 records。
- 桌面端**没有**房间服务、SignalR、Yjs、Blazor、邀请码/审批、配额与审计（第十节列出）。
- 视频生成链路**不存在**（第十节）。

---

## 四、桌面端架构

### 4.1 启动与线程

- 入口 `Program.cs`：显式 `Main` + `[STAThread]`（不能用顶层语句，否则主线程跑在 MTA，剪贴板/文件对话框/拖放会抛 `ThreadStateException`）。
- 顺序：`ApplicationConfiguration.Initialize()` → `ProjectStartupForm.ShowStartup()`（模态选项目）→ `AppPaths.UseProject` → `new MainForm()` → `Application.Run`。`MainForm.RestartForProjectSelection` 为真时回到选项目循环。
- 主窗口构造：建执行宿主 → 建 `SessionContext(ClientType.Desktop)` → 应用主题 → `SkillLibrary.EnsureDefaultSkills` → 注册内置插件 / 加载外部插件 → 建布局 → 加载初始画布 → 订阅执行事件 → 启动 500ms 刷新计时器（WinForms Timer，回调在 UI 线程；每个 tick 同时 `RefreshJobList` 与 `PollResourceReplaceRequests`）。
- 后台线程：`ExternalTaskPoller`（ComfyUI 轮询）与插件 `AssemblyLoadContext`；执行更新经 `BeginInvoke` 回 UI 线程；Web 轮询用独立的 `HttpClient`（3 秒超时），`BeginInvoke` 回 UI 线程执行替换。

### 4.2 主窗口骨架与面板

三段式：标题栏（34） + 内容 + 状态栏（24）；活动栏是可滑出的 `RailStrip`。

| 侧栏标题 | pane key | 职责 |
|---|---|---|
| 画布库 | `library` | 画布列表、新建/保存/打开/重命名/删除、导入导出资产包、存储与清理 |
| 设定库 | `entities` | 角色/场景/道具实体与变体、参考图、运行技能、引用到画布 |
| 工作树 | `worktree` | **叙事轴**条目树（章节/角色/能力/道具/场景/版本）与详情 |
| 节点属性 | `node` | 标题、附件、文本、生成、版本决策、锁定 |
| 任务与出图 | `tasks` | 任务队列与出图 |
| Agent 对话 | `agent` | 右侧抽屉（宽 400），对话 + 审批列表 + 待提交列表 |
| 插件 | `plugins` | 插件清单；插件可追加自定义侧栏入口 |
| 设置 | — | 直接打开模型设置对话框 |

### 4.3 画布的两种实现与现状

- **桌面端画布 = `WorkflowCanvasControl`（WinForms 自绘）**：节点卡片、端口、连线、缩放平移、预览虚影全部自绘，不依赖任何前端。**自动排版已改为按章节分块**（每章一个区块，块内网格排布、主线在前资源在后，区块按"第N章"从左到右、每行 3 块并绘制半透明块底与虚线框）；Agent 新建节点时用同一套 `ChapterKeyOf` / `ChapterBounds` / `ArrangeChapter` 落位，只重排所属章节区块。
- **`DreamForge.Canvas`（React + Vite）现在由 `DreamForge.Web` 托管**：`main.tsx` 有两条传输分支——在 WebView（`chrome.webview` 存在）里走 postMessage，否则连 `ws://<host>/ws/canvas`；Web 在 `dist` 存在时用 `UseStaticFiles` 托管，并把消息接到 `HostBridge`。
- 画布 UI 已改成**五层竖排 + 引用条**：`layerOf()` 把 recordType 归到 L1 剧情 / L2 企划 / L3 章节 / L4 分镜头 / L5 成品，支持"章节视图 / 概览视图"与章节下拉过滤；带引用的节点在卡片下方显示引用缩略图（可折叠），选中引用后可切换/锁定版本，通过 `canvas/resource.replace.request` 提交。
- `tldraw` 依赖仍在 `package.json` 里但**代码未使用**（只有 `tldraw.d.ts` 的 CSS 声明）；`DreamForge.Canvas` 仍**不在 `DreamForge.slnx` 内**，`dist` 需在本地构建。
- 结论：**"双端共用 tldraw 画布"仍不成立**（桌面端是自绘、Web 端是 div 卡片），但两端现在通过 Web + HTTP/WS 有了真实的数据通路。

### 4.4 桌面端与 Web 画布的数据通路（本轮新增）

```
Desktop（权威数据）                         Web（只转发，不落盘）
WorkflowCanvasControl 变更
  └─ NodeProjection.ProjectRecords ──► POST /api/canvas/scene ──► HostBridge.SendScene
                                                                   └─► WS 广播 host/scene.reset
浏览器画布选中引用 / 换版本
  └─ canvas/resource.replace.request ──► WS ──► HostBridge ──► 队列
Desktop 每 500ms 轮询 GET /api/canvas/resource-replace/next ──► 取到请求
  └─ WorkflowCanvasState.ReplaceReferenceVersion（锁定节点/版本不属于变体一律拒绝）
     └─ 成功：canvasRevision++、保存最近画布与标签、再推一次 scene
        └─ POST /api/canvas/resource-replace/result ──► HostBridge.SendResourceReplaceResult
                                                        └─► WS 广播 host/resource.replace.result
```

- 投影字段（`NodeProjection`）：`recordId` / `recordType`（`NodeCategory` → `story-plan|story-outline|chapter|storyboard|character|scene-description|prop|product|general`）/ `record{title,content,x,y,chapter,status,parentId,references[]}`；`references[]` 带 `entityId/name/kind/variantId/variantVersionId/thumbnailRef/variantLabel/versions[]`。
- 工作树里的 `Chapter` 条目若没有对应画布节点，也会被投影成 `wt-<id>` 的 L3 record。
- 待补：`thumbnailRef` 仍是 `asset://文件名`，Web 没有资产 HTTP 端点，浏览器里这张图加载不出来（`<img src="asset://…">` 会失败）；`POST /api/canvas/nodes`（`HostBridge.SendNodeUpdates`）目前没有调用方，桌面端一律走全量 `scene` 推送。
- 地址是**硬编码**的：Desktop 用常量 `http://localhost:5000`，Web 用 `appsettings.json` 的 Kestrel 端点 `http://localhost:5000`；Web 未启动时推送与轮询都静默失败（桌面端功能不受影响）。

---

## 五、执行与任务链路（Core + Host）

### 5.1 Job 状态机（真实合法转换）

`DreamForge.Core\Jobs.cs`：状态 `Queued=1, Running=2, Cancelling=3, Succeeded=4, Failed=5, Cancelled=6`。合法转换只有 6 条：

```
Queued → Running | Cancelled
Running → Cancelling | Succeeded | Failed
Cancelling → Cancelled
```

其余抛 `JobStateException`；进入 `Succeeded` 时进度强制 100；`ReportProgress` 仅允许 `Running/Cancelling` 且 0-100 单调不减；终态不可再绑定外部任务；已绑定不同外部任务 ID 抛异常。`IdempotencyRegistry` 按 `(UserId, Key)` 复用结果；`LocalUndoStack` 仅 `Push/Pop`。

### 5.2 单机执行服务

`DreamForge.Host\SingleMachineExecutionService.cs`：`StartAsync` 校验 `skill.invoke` → 幂等复用/并发同键合并 → 构造 Job 与 TCS → 后台 `RunAsync`（Running→25%→调用执行器→绑定外部任务→非等待外部完成则 90%→Complete）；支持 `ApplyExternalUpdate`、`ApplyExternalProgress`、`Cancel`（校验 `job.cancel` 与属主）；进程重启时把未完成 Job 恢复为 `HOST_RESTARTED` 失败。

### 5.3 持久化

`SqliteJobStore`：表 `jobs`（`job_id` 主键、`UNIQUE(user_id, idempotency_key)`、outputs/inputs/tool/capability/channel 等 JSON 列），带 `AddColumnIfMissing` 向前兼容。DB 路径：`DREAMFORGE_JOB_DB` → 否则项目根 `jobs.db`。

### 5.4 ComfyUI 适配（真实边界）

- `ComfyUiExecutor`：`POST prompt` 提交，返回 `prompt_id` 作为 `ExternalTaskId` 并标记"等待外部完成"；参考图先 `POST upload/image` 再替换输入。
- `ComfyUiWorkflowFactory`：只生成 txt2img 与**单图 img2img** 两种工作流（节点 3/4/5/6/7/8/9/10）；尺寸 64-2048、steps 1-150、cfg 1-30、denoise 0.05-1 夹取；其它参考模式抛异常。
- `ComfyUiProvider`：进度优先 `GET history/{id}`，无记录回退 `GET queue`；有 outputs 时下载产物并写成 `asset://`；`POST interrupt` 取消；`ListenForProgressAsync` 连 `/ws?clientId=`（带重连与 30s 接收超时，`progress` 映射到 0-99，`execution_success`/`execution_error` 映射终态）。
- 回调签名：`HMACSHA256(secret, "{timestamp}.{Base64(rawBody)}")`，输出**大写 Hex**，校验允许 `sha256=` 前缀，用 `FixedTimeEquals` 比较，`nonce` 防重放并做时钟窗口校验；请求头名为 `X-ComfyUI-Timestamp` / `X-ComfyUI-Nonce` / `X-ComfyUI-Signature`（头部名在 `DreamForge.Web\Program.cs`，算法在 `DreamForge.Host\HttpCallbackSecurity.cs`）。

---

## 六、数据模型（实际）

### 6.1 画布 JSON

落盘的状态就是 4 个集合（`WorkflowCanvasState`）：`Nodes`、`Edges`、`Entities`、`WorkTree`。该类还提供引用解析与改写：`ResolveReferences`（跳过失效引用）、`ResolveReferencePairs`（保留"哪条引用解析失败"的对应关系）、`FindEntity`，以及本轮的 `ReplaceReferenceVersion(recordId, entityId, variantId, variantVersionId, out error)`——它要求节点存在且未锁定、节点确实引用了该"实体+变体"，且新版本属于该变体，成功后只改写 `NodeReference.VariantVersionId`（`null` = 跟随最新）。

**节点 `WorkflowNode`**（`WorkflowCanvasControl.cs:78-137`）字段：`Id`、`Title`、`Category`、`ContentSource`、`ExecutionStatus`、`Content`、`Chapter`、`Question`、`Answer`、`ParentNodeId`、`WorkTreeItemId`、`GenerationId`、`IsCollapsed`、`References`、`IsLocked`、`VersionDecision`、`Attachments`、`Parameters`、`GenerationHistory`、`X`、`Y`、`InputCount`、`OutputCount`。

旧字段（仅用于加载时迁移，迁移后清空）：`LegacyEntityId` / `LegacyVariantId` / `LegacyVariantVersionId`（JSON 名 `EntityId` 等）、`LegacyAssetPaths`（JSON 名 `AssetPaths`）。

**三类"资源"的锚点分工**（本轮确立，勿混用）：

| 字段 | 指向 | 作用 |
|---|---|---|
| `ParentNodeId` | 画布节点 | 画布上的父子归属；**决定自动排版位置** |
| `WorkTreeItemId` | 工作树条目 | 叙事轴锚点（章节/能力），节点靠它跟随项目树更新 |
| `References[].EntityId/VariantId/VariantVersionId` | 设定库实体/变体/版本 | 视觉轴引用，出图时合成提示词与参考图 |

**其它类型**：`WorkflowEdge`（`SourceNodeId/SourcePort/TargetNodeId/TargetPort`）；`WorkflowAttachment`（`Kind/Reference(asset://…)/Name/Source/AddedAt`，常量 `SourceComposition="合成底图"`）；`GenerationHistory`（含 `Input/Instruction/Output/Model/Provider/Accepted/Status/Proposals`）。

**设定库**（`WorkflowEntities.cs`）：`WorkflowEntity`（`Kind/Name/Aliases/Core/Variants`）、`WorkflowEntityVariant`（`Name/Description/Layout/Attachments/CurrentVersion/Versions/CommittedFingerprint`，带 `Commit/RollbackTo/EnsureInitialVersion`）、`EntityVariantVersion`（不可变快照：`Number/Note/SupersedesVersionId/Description/Layout/Attachments`）、`SceneLayout`/`SceneLayoutItem`（结构化方位）、`NodeReference`、`ReferenceContent`、`VersionDiff`。

**工作树**（`WorkTree.cs`）：`WorkTreeItem`（`ParentId/Kind/Name/Chapter/Version/Prompt/SourceEntityId/SourceVariantId/SourceVersionId/SupersedesVersionId/Attachments`）。

**枚举数值顺序即落盘兼容性**（只能追加，不能插入）：

| 枚举 | 顺序 |
|---|---|
| `NodeCategory` | General=0, Character=1, Scene=2, Storyboard=3, Prop=4, Product=5, **StoryPlan=6, StoryOutline=7, Chapter=8（追加在末尾，不影响旧数据）** |
| `ContentSource` | User=0, Ai=1, Api=2 |
| `NodeExecutionStatus` | Draft=0, WaitingForUser=1, Generating=2, Completed=3, Failed=4, NeedsReview=5 |
| `AttachmentKind` | Image=0, Video=1, Audio=2, Other=3 |
| `VersionDecision` | None=0, KeepHistorical=1, Adopted=2 |
| `EntityKind` | Character=0, Scene=1, Prop=2 |
| `WorkTreeKind` | Project=0, Character=1, Ability=2, Prop=3, Scene=4, Version=5, Chapter=6 |
| `ProjectType` | Video=0, Image=1, Novel=2, Other=3 |
| `ContextTarget` | CanvasNode=0, CanvasBlank=1, Entity=2, Variant=3, VariantImage=4 |

### 6.2 文件布局

**项目根**（`ProjectContext.Create` 建目录，`EnsureDirectories` 建子目录）：

| 路径 | 内容 | 读写方 |
|---|---|---|
| `project.json` | `ProjectDescriptor`（Id/Name/Type/CreatedAt/LastOpenedAt）；存在与否即"是否本项目" | `ProjectContext` |
| `canvases\*.json` | 画布库，每画布一文件 | `CanvasLibrary` |
| `assets\` | 图片资产（`asset://` 目标） | `AssetStore` |
| `skills\` | 技能清单 JSON | `SkillLibrary`（首次写入 2 个内置示例） |
| `plugins\` | 每插件一个子目录（`plugin.json` + `plugin.dll`） | `PluginLoader` |
| `jobs.db` | SQLite 任务库 | `SqliteJobStore` |
| `last-canvas.json` | 最近画布草稿 | `MainForm.SaveRecentCanvas` / `LoadInitialCanvas` |
| `canvas-tabs.json` | 标签会话（`ActiveTabId` + 各标签路径与快照） | `MainForm.SaveAllCanvasTabs` / `TryLoadCanvasTabs` |
| `workspace.json` | `{ CurrentCanvasPath }` | `CanvasLibrary` |

**程序目录**（`AppContext.BaseDirectory`）：

| 路径 | 内容 |
|---|---|
| `ai-config.json` | 模型/图像/ComfyUI/主题/资产目录/工作文件夹等全部设置；**密钥以 DPAPI 密文存储** |
| `recent-projects.json` | 最近项目（最多 12 条，读时过滤不可访问项并回写） |
| `startup-media.txt` | 启动背景媒体路径 |
| `agent-diagnostics.log` | Agent 诊断日志 |

环境变量覆盖：`DREAMFORGE_CANVAS_DIR`、`DREAMFORGE_ASSET_DIR`、`DREAMFORGE_SKILL_DIR`、`DREAMFORGE_PLUGIN_DIR`、`DREAMFORGE_JOB_DB`、`DREAMFORGE_CONFIG`。

**保存时机**：没有定时自动保存；落盘发生在保存画布、切换/关闭标签、`FormClosed`、切换项目这些显式时机。启动时画布加载优先级：`canvas-tabs.json` → `workspace.json` 绑定画布 → `last-canvas.json` → 画布库最近修改的一张 → 空画布。

---

## 七、AI 与生成

- **Provider 工厂**：`UseLocalProvider` 或未配置 → `LocalAiProvider`（关键词启发式草稿）；否则 `OpenAiCompatibleProvider`。
- **服务商预设**（`AiChat.cs`）：Local、DeepSeek、Moonshot、Qwen、Zhipu、SiliconFlow、OpenAI、Ollama、Custom；模型条目带 `ContextWindow/MaxOutputTokens/SupportsVision`。
- **接口格式**：`AiApiFormat` = `OpenAiChat`（`/chat/completions`）或 `AnthropicMessages`（`/v1/messages`）。
- **上下文预算**：`ContextWindow<=0 ? 0 : min(ContextWindow × 1.5 / 4, 300000)` 字符；只截画布上下文，协议永不截断。
- **多模态**：OpenAI 兼容走 text + `image_url` 块；Anthropic 走 `media_type` + base64/url；未开启图片输入时先拦截报错。附件加载上限：图片 8MB、文本 20 万字符；**视频附件在菜单里置灰**。
- **密钥**：Windows DPAPI（`ProtectedData`，`CurrentUser`），附加熵 `DreamForge.ApiKey.v1`，密文前缀 `dpapi:` + Base64；读取失败标记 `ApiKeyUnreadable`；旧明文自动迁移一次；界面脱敏显示。
- **流式**：SSE 逐行读 `data:`，`ResponseHeadersRead` 边收边显；OpenAI 追加 `stream_options.include_usage`；取消后保留半截并提供"编辑重跑"。
- **图像生成**：三种 Provider —— `UnconfiguredImageProvider`、`OpenAiCompatibleImageProvider`（`/images/generations` 或 `/images/edits` 多图）、`ComfyUiImageProvider`（走 `SingleMachineExecutionService`，超时 10 分钟）。参考图合成规划：`CompositionPlanningRequest`（默认每步最多 2 张）→ 模型规划，失败回退 `LocalCompositionPlanner`（按引用顺序两两收敛）。
- **视频生成：未实现**。`Capability.TextToVideo/ImageToVideo` 只是枚举声明；`SubmissionKind.AsyncVideo` 只有声明没有实现；`ComfyUiSubmissionProfiles` 只有 `SingleBaseImage`；`SkillRunner` 明确拒绝非出图能力；`BuiltInSkills` 里的 `video-generation` 是纯提示词条目；`AttachmentKind.Video` 只表示"可以把视频文件挂在节点上"。

---

## 八、技能与插件

见 [03_Skill_System.md](03_Skill_System.md)。此处只记架构位置：技能是**文件驱动**（`skills\*.json` → `SkillDefinition`），插件是**程序集驱动**（`plugins\<name>\plugin.dll` + `plugin.json`，可卸载 `AssemblyLoadContext`，入口实现 `IDreamForgePlugin`）。

---

## 九、安全与边界（已实现的部分）

1. **Key 不落明文**：DPAPI + 当前用户作用域；配置文件在程序目录。
2. **回调必须签名**：HMACSHA256 + 时间戳 + nonce 防重放；未配置密钥时端点返回 503（code / 头部缺失 400 / 重放或签名错 401）。
3. **路径收口**：Agent 写文件必须落在"工作文件夹"内（`ResolveWorkspacePath` 越界即拒绝）；MCP 只读工具用 `SafePath` 防越界。
4. **AI 不能直接改画布**：Agent 只能提交"提议"，Ask 模式下先出虚影预览，用户确认后才应用；锁定节点（定稿保护）拒绝 AI 修改。
5. **撤销边界**：`AgentCommitRecord` 保存提交前整张画布快照；"撤销上次提交"只在 `canvasRevision == RevisionAtCommit + 1` 时可用（提交后又有编辑则停用）。
6. **运行环境提醒**：若把桌面程序放在受限沙箱里启动（例如从 IDE 的沙箱终端拉起），工作区之外的写入会被拦住并表现为 `Access to the path ... is denied`，这不是应用缺陷。

---

## 十、文档与实现仍不一致的清单（待处理）

| # | 事项 | 代码事实 | 影响 |
|---|---|---|---|
| 1 | 根目录 `05_Core_Contracts.cs` | 未包含进任何 csproj、未被任何代码引用、不参与编译；命名空间 `DreamForge.Core.Contracts` 与 Core 实际类型重复且不兼容（`ExecutionResult` 字段更少，还定义了 Core 里不存在的 `IChannelService`/`ISkillMigration`） | 极易被误当成"真实契约" |
| 2 | 视频生成 | 全链路未实现（只有枚举、提示词条目、诊断探测、附件挂载） | 产品说明需标注"计划中" |
| 3 | 协作（房间/Yjs/SignalR/Blazor） | 完全未实现 | 01/04 已按现状改写 |
| 4 | Web 端画布 | Web 现在托管 TS 画布并转发消息，但**画布数据全部来自桌面端推送**；桌面端未启动时不显示任何内容，Web 端唯一能回写的操作是替换/锁定节点引用版本 | 产品说明里不能写成"Web 端可以独立创作" |
| 5 | `DreamForge.Canvas` | 已能被 Web 托管（静态文件 + WS），但仍是 div 卡片、`tldraw` 未被使用、不在 slnx、`dist` 需本地构建 | 与"tldraw 画布"的历史描述不一致 |
| 6 | `DreamForge.Mcp` | 独立只读 stdio 服务，未与 Desktop 连接，也不在 slnx | 需明确它的定位 |
| 7 | `MarkVersionAdopted` | `AgentAction` 有这个字段，但 `ReadActions` 不解析，模型无法设置，恒为 false | 该能力实际不可用 |
| 8 | AutoStage 路径 | 先 `ApplyActions`，随后 `SaveApplied` 因 pending 非空**再次**应用同一批 actions | 同一批被执行两次，需修 |
| 9 | 配额 / 审计 / 审批 / 邀请码 | 未实现 | 产品计划需降级为"未开始" |
| 10 | 资源替换协议缺夹具 | `canvas/resource.replace.request` / `host/resource.replace.result` 已两端落地并有 C# 用例，但 `protocol/fixtures/manifest.json` 没有对应条目 | 违反"先夹具后实现"的自定规则，TS 侧缺跨语言回放 |
| 11 | Web 画布的两处断点 | `thumbnailRef` 是 `asset://文件名`，Web 没有资产 HTTP 端点（浏览器加载不出这张图）；`POST /api/canvas/nodes` 与 `HostBridge.SendNodeUpdates` 没有调用方 | 引用缩略图在 Web 端不可见；增量推送只有声明没有使用 |
| 12 | 画布交互回传范围 | 浏览器画布的 `canvas/selection.changed`（含 `entityId`/`openResourceLibrary`）在 `HostBridge.Receive` 中没有分支处理 | 桌面端"定位资源库"等联动尚未生效 |
| 13 | 品牌口径 | 界面字符串与资源文件名仍用大小写变体 `YeeYeeYee`，产品名已定为 `YEEYEEYEE` | 可选统一 |

## 附录：协议与外部接口清单（实际）

| 协议/接口 | 用途 | 实现位置 |
|---|---|---|
| `DreamForgeProtocol v1`（17 条消息） | 宿主 ↔ 画布（Host/Web/TS 前端与测试） | `DreamForge.Core\Protocol.cs` |
| Agent JSON 协议（13 种 action） | 模型 ↔ Desktop（当前唯一驱动画布的通道） | `DreamForge.Desktop\AgentActions.cs`、`AgentPane.cs` |
| HTTP 画布推送（`/api/canvas/scene`、`/api/canvas/nodes`、`/api/canvas/resource-replace[/next|/result]`） | Desktop ↔ Web（投影推送、资源替换往返） | `DreamForge.Web\Program.cs`、`DreamForge.Desktop\MainForm.cs` |
| WebSocket `/ws/canvas` | Web ↔ 浏览器画布（协议信封双向） | `DreamForge.Web\WebCanvasTransport.cs`、`DreamForge.Canvas\src\main.tsx` |
| OpenAI 兼容 / Anthropic Messages | 文本与多模态调用、图像生成 | `OpenAiCompatibleProvider.cs`、`ImageGeneration.cs` |
| ComfyUI HTTP + WebSocket | 提交、进度、取消、产物下载 | `DreamForge.Host\ComfyUiProvider.cs` |
| HMAC 回调 | 外部任务状态回传 | `DreamForge.Host\HttpCallbackSecurity.cs` + `DreamForge.Web\Program.cs` |
| MCP stdio（只读 4 工具） | 项目信息、文件清单、画布标签与状态 | `DreamForge.Mcp\Program.cs` |
