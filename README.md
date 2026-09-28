# YEEYEEYEE

> 寓意：**YES 工程师 · YES 艺术家**（YEEYEEYEE = YES YES YES）
> 单机无限画布 AI 创作工具：把剧情拆成「叙事设定 → 视觉设定 → 生成动作」，用大模型驱动画布节点生产。
> 桌面端（WinForms 自绘无限画布）为唯一交付端；本仓库同时保留作业服务（并托管一份由桌面端推送的只读 Web 画布）、只读 MCP 服务与前端画布工程。
> 工程标识为 `DreamForge`（程序集 / 解决方案 / 协议名），**改名只针对产品名，代码结构不动**。

---

## 一、当前状态一览

| 能力 | 状态 |
|---|---|
| 无限画布（节点/连线/缩放平移/**按章节分块排版**/预览虚影） | ✅ 已实现（GDI+ 自绘，无浏览器依赖） |
| 剧情 → 工作树 → 节点 的 Agent 生成流水线 | ✅ 已实现（提示词 + 数据键，缺自动同步引擎） |
| Agent 提议 → 审批/自动 → 保存 → 回滚 | ✅ 已实现（Ask / AutoStage / ReadOnly 三种授权） |
| 设定库（角色/场景/道具：变体 + 不可变版本 + 参考图） | ✅ 已实现 |
| 工作树（叙事轴：章节 → 角色 → 能力 → 能力版本） | ✅ 已实现 |
| 文本与多模态调用（OpenAI 兼容 / Anthropic Messages） | ✅ 已实现（流式 + 取消 + 图片输入） |
| 图像生成（OpenAI 兼容 /images、ComfyUI） | ✅ 已实现（含参考图合成规划与本地兜底） |
| 技能（`skills/*.json` 出图流程）与插件（`plugins/`） | ✅ 已实现 |
| 密钥安全存储（Windows DPAPI） | ✅ 已实现 |
| 任务持久化（SQLite）+ ComfyUI 进度/取消/回调签名 | ✅ 已实现 |
| **Web 画布（TS 画布镜像）** | ✅ 镜像展示 + 回写引用版本：`DreamForge.Web` 托管 `DreamForge.Canvas\dist`，桌面端推投影、Web 广播；浏览器可换/锁引用版本（走 `canvas/resource.replace.request`） |
| **TS 画布（`DreamForge.Canvas`）接入** | ⚠️ 部分接入：由 Web 托管、走 `/ws/canvas`，但仍是 div 卡片（tldraw 未使用），也不在 slnx |
| **视频生成** | ❌ 未实现（仅枚举与提示词条目） |
| **协作（房间 / Yjs / SignalR）** | ❌ 未开始 |
| **账号 / 邀请码 / 审批 / 配额 / 审计** | ❌ 未开始 |

### 进度目标（2026-09-28 设定）

| # | 目标 | 现状 |
|---|---|---|
| **目标 1** | **AI 自动全套流程跑通** —— 一句创意 → 工作树 → 设定库 → 画布节点 → 按引用出图，中途不需要手动补数据 | 部分（四步流水线与出图链路已就绪；缺"节点↔工作树"自动同步引擎与端到端用例） |
| **目标 2** | **Web 端完成** —— 浏览器里就能完成桌面端的主要创作动作（画布、引用版本、节点编辑、技能/出图、任务），并补齐资产端点与 `dist` 构建 | 起步（已能托管画布并回写引用版本） |
| **目标 3** | **Web / Desktop 多端协同** —— 同一项目多端同时打开，画布双向实时同步、互不覆盖 | 未开始（当前只有桌面端单向推送） |

> 验收条件与阶段归属见 [PROGRESS.md](PROGRESS.md) 的《进度目标》。

---

## 二、快速开始

```powershell
# 构建（解决方案 6 个项目）
dotnet build DreamForge.Desktop\DreamForge.Desktop.csproj
dotnet build DreamForge.slnx

# 运行：直接双击 exe，或
.\DreamForge.Desktop\bin\Debug\net10.0-windows\DreamForge.Desktop.exe
```

首次启动：新建项目 → 选**可写目录** → 在「设置」里配置模型（服务商预设或自定义 Endpoint/Model/ApiKey）→ 在「设定库」建角色/场景/道具 → 打开右侧 Agent 面板开始生成。

想看 Web 画布（可选）：先 `cd DreamForge.Canvas; npm install; npm run build` 生成 `dist`，再 `dotnet run --project DreamForge.Web`（监听 `http://localhost:5000`），然后打开桌面端——画布内容会通过 `POST /api/canvas/scene` 推送到浏览器。桌面端地址是常量 `http://localhost:5000`，Web 没起时所有推送静默失败，不影响桌面端使用。

> ⚠️ **不要在受限沙箱里启动**（例如直接从 IDE 的沙箱终端拉起）。沙箱会拦截工作区之外的写入，表现为 `项目创建失败：Access to the path '...' is denied.`，这不是应用缺陷。用资源管理器双击启动，或把项目放在允许目录内即可。

---

## 三、架构（简版）

```
Core        ← 无引用                         协议 / Job 状态机 / 能力枚举 / 权限 / 引用图
Host        → Core                           单机执行服务 / SQLite Job / ComfyUI(HTTP+WS) / 回调签名
Web         → Host                           作业服务 + ComfyUI 回调 + 托管 TS 画布（静态 + /ws/canvas + /api/canvas/*）
Desktop     → Host                           唯一交付端：自绘画布 + Agent + 设定库 + 工作树 + 技能/插件
Core.Tests  → Core + Host + Desktop          协议 / Job / 持久化 / ComfyUI / 回调签名 / 资源版本替换
Agent.Tests → Desktop                        Agent 协议 / 批量试算 / 引用版本 / 附件 / DPAPI / 流式
Mcp         → 无引用（不在 slnx）              stdio 只读工具服务（4 个工具），未接入桌面端
Canvas      → TS/Vite（不在 slnx）            前端画布工程，由 Web 托管，浏览器走 WebSocket
```

Desktop 与 Web **没有程序集引用**，只有 HTTP 联动：Desktop 每 500ms 轮询 `GET /api/canvas/resource-replace/next`，并把节点投影推给 `POST /api/canvas/scene`；Web 广播给浏览器画布，再把结果回投 `POST /api/canvas/resource-replace/result`。

**两套协议并存，别混用**：

- `DreamForgeProtocol v1`（Core）：信封 `v/id/type/ts/payload`，9 条 `canvas/*` + 8 条 `host/*`，方向与字段严格校验。用于 Host / Web（含浏览器画布的 WebSocket 通道）/ TS 前端 / 测试。
- **Agent JSON 协议**（Desktop）：模型在回复末尾给 `{"actions":[…]}` 或 `{"ask":{…}}` 块，13 种 action。这是当前**唯一真正驱动画布改动**的通道。

细节见 [02_Architecture.md](02_Architecture.md)。

---

## 四、三条轴与生成流水线

同一批创作元素在系统里分三个载体，职责不可混用：

| 载体 | 回答的问题 | 存在哪 | 锚点字段 |
|---|---|---|---|
| **画布节点**（动作层） | 这一章/这一镜要做什么 | 画布 `Nodes` | `ParentNodeId`（决定排版） |
| **设定库**（视觉轴） | 他长什么样、几套造型、空间布局 | 画布 `Entities` + `assets/` | `References[]`（实体/变体/版本） |
| **工作树**（叙事轴） | 他会什么、第几章有什么变化 | 画布 `WorkTree` | `WorkTreeItemId` |

**Agent 固定四步（提示词里写死）**：

1. **确认剧情** —— 只给章节切分/人物/能力/道具/场景清单，**不改画布也不改工作树**，等用户确认。
2. **建工作树** —— `第1章(Chapter)` → `角色(Character)` → `能力(Ability)` → `能力版本(Version)`。
3. **建视觉骨架** —— 为角色/场景/道具建或更新设定库实体，只写外观，剧情里没有的留空。
4. **建节点（只建主线）** —— 章节/剧情/分镜/成品各建一个节点：`parentTarget` 挂上游 + `workTreeTarget` 关联章节/能力；**角色/场景/道具不建画布节点**，改用 `entityTargets` 把设定名挂到剧情/分镜节点上（卡片显示 ◆ 引用标签与参考图缩略图）。

**分批规则**：只有"按剧情从零搭项目结构"这类请求才分两批（先工作树、再节点，让用户先看清结构）；用户明确说"生成节点 / 自动生成节点 / 铺开画布节点"时，**当次回复必须给出 `create_node` 批次**，否则画布上什么都不会有。

**边界**：工作树只写剧情向内容；外观/服装/布局/参考图一律进设定库，角色/场景/道具**只存在于设定库**（不再有"资源节点做锚点"这一层；能力自带的特效/形态素材仍算叙事资料，可挂在工作树条目上）；只有需要锁定某个变体或版本时才用单条 `entityTarget` / `variantTarget` / `variantVersion`；同步方向单向（工作树变了才更新节点）。

> 命名注意：工作树里的 **能力 `Ability`**（角色会什么，剧情设定）与本程序的 **生成技能 `SkillDefinition`**（可执行出图流程）是两回事；`BuiltInSkills` 是给模型看的提示词清单，不执行代码。

---

## 五、数据与存储

画布 JSON = `Nodes` + `Edges` + `Entities` + `WorkTree`（按枚举数字落盘，**枚举只能追加不能插入**）。

**项目根**：`project.json`、`canvases/`、`assets/`、`skills/`、`plugins/`、`jobs.db`、`last-canvas.json`、`canvas-tabs.json`、`workspace.json`

**程序目录**：`ai-config.json`（密钥为 DPAPI 密文）、`recent-projects.json`、`startup-media.txt`、`agent-diagnostics.log`

环境变量覆盖：`DREAMFORGE_CANVAS_DIR` / `ASSET_DIR` / `SKILL_DIR` / `PLUGIN_DIR` / `JOB_DB` / `CONFIG`

---

## 六、文档索引

| 文档 | 内容 |
|---|---|
| [01_Project_Plan.md](01_Project_Plan.md) | 产品决策逐条标注实现状态、技术选型、资产体系、AI 助手形态、进度、风险、待办 |
| [02_Architecture.md](02_Architecture.md) | 项目与引用关系、进程拓扑、两套协议、执行链路、字段级数据模型、文件布局、安全边界、**文档/实现不一致清单** |
| [03_Skill_System.md](03_Skill_System.md) | 三个"技能"的区分、生成技能的数据模型与执行流程、插件体系、版本现状 |
| [04_Architecture_Mindmap_Mermaid.md](04_Architecture_Mindmap_Mermaid.md) | 架构思维导图（Mermaid + XMind 列表版） |
| [protocol/PROTOCOL.md](protocol/PROTOCOL.md) | 画布 ⇄ 宿主消息协议规范（v1）+ `protocol/fixtures/` 跨语言夹具 |
| [PROGRESS.md](PROGRESS.md) | 开发进度基准（含《实现现状基准》与逐轮记录） |
| [debug-new-project-failure.md](debug-new-project-failure.md) | "项目创建失败：Access to the path ... is denied" 的排查记录（根因：沙箱拦截，非应用缺陷） |

---

## 七、已知问题与下一步

**代码里的已知不一致**

1. 根目录 `05_Core_Contracts.cs` 未参与编译、与 `Core/Contracts.cs` 重名不兼容 —— 应删除或归档。
2. `AutoStage` 授权路径会把同一批 actions 应用两次 —— 应修。
3. `AgentAction.MarkVersionAdopted` 未在协议解析中读取，模型无法设置。
4. 界面文案与资源文件名仍用大小写变体 `YeeYeeYee`（产品名已定为 `YEEYEEYEE`），可选统一。
5. 资源替换的两条新协议消息（`canvas/resource.replace.request` / `host/resource.replace.result`）**没有 `protocol/fixtures` 夹具**，违反"先夹具后实现"的自定规则。
6. Web 画布两处断点：引用缩略图用 `asset://`，Web 没有资产 HTTP 端点（浏览器加载不出图）；`POST /api/canvas/nodes` 与 `HostBridge.SendNodeUpdates` 没有调用方。
7. 浏览器画布回传的 `canvas/selection.changed`（带 `entityId`/`openResourceLibrary`）在 `HostBridge` 里没有分支处理，"定位资源库"等联动尚未生效。
8. `DreamForge.Canvas` 现在由 Web 托管，但它仍是 div 卡片（tldraw 未被使用）、不在 slnx、`dist` 需本地构建 —— 路线仍未定。

**待实现**

- 节点 ↔ 工作树的自动同步引擎（当前只有键与流程约束，缺"按本章重新同步节点"）。
- 浏览器画布 ↔ 桌面端的交互回传（选中引用 → 打开设定库对应实体等）。
- 视频生成链路。

---

## 八、仓库结构（顶层）

```
DreamForge.Desktop/      桌面端（WinForms 自绘画布 + 各面板 + Agent）
DreamForge.Core/         领域契约（协议 / Job / 枚举 / 权限 / 引用图）
DreamForge.Host/         执行与适配（单机执行服务 / SQLite / ComfyUI / 回调）
DreamForge.Web/          作业服务 + ComfyUI 回调 + 托管 TS 画布
DreamForge.Mcp/          stdio 只读 MCP 服务（未接入桌面端）
DreamForge.Canvas/       前端画布工程（TS/Vite，由 Web 托管；不在 slnx）
DreamForge.Core.Tests/   领域与宿主用例
DreamForge.Agent.Tests/  Agent 与桌面端用例
0x_*.md                  设计与架构文档（见上表）
```
