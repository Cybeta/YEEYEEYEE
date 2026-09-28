# YEEYEEYEE 项目企划（按实际代码校正）

> 版本：v6.1（产品名统一为 YEEYEEYEE） · 2026-09-28
> 产品名：**YEEYEEYEE** · 寓意 **YES 工程师 · YES 艺术家**（工程标识仍为 `DreamForge`，代码结构不动）
> 无限画布 AI 多模态创作平台 · **单机桌面端为主**
> 本文保留产品方向，但**所有技术形态与"已完成/未完成"均按代码实际改写**；未实现的能力统一标注「未开始」。
> 上一版（v5，2026-09-25）描述的"Avalonia WebView + tldraw 双端 + 房间服务 + Yjs 协作"是设想，代码里不存在，已作废。
> 配套：[02_Architecture.md](02_Architecture.md)、[03_Skill_System.md](03_Skill_System.md)

---

## 一、产品形态（当前状态）

| # | 决策 | 实际状态 |
|---|---|---|
| 1 | 核心形态：无限画布节点编排，串起图/文/视频任务 | **已实现**：WinForms 自绘无限画布（`WorkflowCanvasControl`），节点/连线/缩放/自动排版/预览虚影 |
| 2 | 主端：桌面客户端 | **已实现**：`DreamForge.Desktop`（net10.0-windows，WinForms），唯一交付端 |
| 3 | 辅端：Web | **只做了作业服务**：`DreamForge.Web` 组装执行链路并提供 ComfyUI 回调端点与 `/health`，**没有画布** |
| 4 | 不内置厂商清单，只做协议模板 | **部分偏离**：文本/图像走 OpenAI 兼容与 Anthropic 两种格式（可自定义，也有 9 个服务商预设）；本地生成走 ComfyUI |
| 5 | 选择粒度：用户选能力，不选厂商 | **实际是选服务商与模型**（`ai-config.json` 里配 Endpoint/Model/ApiKey），能力枚举尚未作为选择入口 |
| 6 | 注册需邀请码 + 管理员审批；本地创作零账号零联网 | **未开始**：没有账号、服务端与邀请机制；本地创作天然离线（除调用模型/ComfyUI） |
| 7 | 五类权限（可见/引用/执行/修改/分享） | **未开始**：`DreamForge.Core\AccessPolicy.cs` 有按 claims 的判定与测试，但桌面端未接入真实会话鉴权 |
| 8 | 本地通道直连 ComfyUI | **已实现**：桌面进程内直连 ComfyUI（HTTP + WebSocket 进度） |
| 9 | MCP 归入工具注册表，桌面端 stdio 接入 | **部分**：`DreamForge.Mcp` 已是可运行的只读 stdio 服务（4 个工具），但**未被桌面端接入**，也不在解决方案里 |
| 10 | Web 端自填 Key：本期不做 | **未开始**（Web 端没有界面） |

### 1.1 技术选型（实际）

| 层级 | 技术 | 状态 |
|---|---|---|
| 桌面端 | C# / WinForms（net10.0-windows），**自绘画布** | 已实现 |
| 画布渲染 | `WorkflowCanvasControl`（GDI+ 自绘） | 已实现 |
| 前端画布包 | `DreamForge.Canvas`（React + Vite）：**独立工程，未接入任何宿主，tldraw 实际未使用** | 搁置 |
| 领域层 | `DreamForge.Core`（net10.0，无第三方依赖） | 已实现 |
| 执行层 | `DreamForge.Host`（SQLite + ComfyUI 适配 + 轮询/回调） | 已实现 |
| 任务库 | SQLite（项目根 `jobs.db`） | 已实现 |
| 模型接入 | OpenAI 兼容 `/chat/completions`、Anthropic `/v1/messages`、图像 `/images/generations|edits` | 已实现 |
| 密钥 | Windows DPAPI（当前用户），密文落 `程序目录\ai-config.json` | 已实现 |
| 协作（SignalR / Yjs / 房间） | — | **未开始** |
| 服务端数据库（PostgreSQL） | — | **未开始** |

### 1.2 命名规范

| 用途 | 写法 |
|---|---|
| 产品名（对外唯一写法） | **YEEYEEYEE** |
| 寓意 | **YES 工程师 · YES 艺术家**（YEEYEEYEE = YES YES YES） |
| 工程标识（代码 / 程序集 / 解决方案 / 协议） | `DreamForge`（`DreamForge.*`、`DreamForge.slnx`）—— 改名只针对产品名，工程标识与仓库结构不动 |
| 界面文案现状 | 界面字符串与品牌资源文件名仍用大小写变体 `YeeYeeYee`，可选统一为全大写 |
| 错误码 | 协议层用大写下划线码（如 `PROTOCOL_MALFORMED`、`HOST_RESTARTED`），未见 `DRM_` 前缀 |

---

## 二、代码分层（替代原"六层架构"）

原六层架构（L1–L6）是设想；实际分层见 [02_Architecture.md](02_Architecture.md) 第一节，概括为：

```
表现层     Desktop：自绘画布 · 各面板 · Agent 面板
应用编排层 Desktop：Agent 提议/审批 · 画布命令与撤销 · 技能 · 插件 · 进程内执行宿主
领域层     Core：Job 状态机 · 能力/权限枚举 · 协议校验 · 引用图判定
适配层     Host：单机执行服务 · SQLite Job · ComfyUI（HTTP+WS）· 外部任务轮询/回调
```

**仍然成立的铁律**：AI 的任何画布改动必须可预览、可确认、可撤销；画布层不感知具体模型厂商。

---

## 三、资产体系（实际）

设想里的五类资产（Channel / Capability / Tool / Asset / Skill）中，只有 `Capability` 与 `Asset` 以代码形式存在；真正在用的资产是下面这些：

| 资产 | 载体 | 存在哪 | 说明 |
|---|---|---|---|
| **画布** | `WorkflowCanvasState`（Nodes/Edges/Entities/WorkTree） | 项目根 `canvases\*.json` + `last-canvas.json` + `canvas-tabs.json` | 一次创作的主体 |
| **节点与连线** | `WorkflowNode` / `WorkflowEdge` | 画布内 | 动作层：剧情、分镜、成品等 |
| **设定库（视觉轴）** | `WorkflowEntity` → `Variant` → 不可变 `Version` + `SceneLayout` + 参考图 | 画布 `Entities` + 项目根 `assets\` | 角色/场景/道具的外观与空间设定，出图的单位 |
| **工作树（叙事轴）** | `WorkTreeItem`（章节/角色/能力/道具/场景/版本） | 画布 `WorkTree` | 剧情设定与章节归属，只提供约束 |
| **生成技能** | `SkillDefinition`（可执行出图流程） | 项目根 `skills\*.json` | 人物三视图、参考图合成等 |
| **插件** | `IDreamForgePlugin` + `plugin.json` | 项目根 `plugins\<name>\` | 只挂界面入口与右键，不产内容 |
| **图片资产** | `WorkflowAttachment`（`asset://文件名`） | 项目根 `assets\` | 可移植引用，换机器不失效 |
| **任务** | Job（Queued→Running→…→Succeeded/Failed/Cancelled） | 项目根 `jobs.db` | 执行与产物状态 |

> 历史的 `Channel` / `Tool` 抽象没有落地成可配置资产：连什么、用什么，直接写在 `ai-config.json` 与代码里。

---

## 四、AI 助手（实际形态）

原文档写的是"对话框 / 内嵌操控双模式"，实际是**一个 Agent 面板 + 三种授权模式**：

| | 实际实现 |
|---|---|
| 入口 | 主窗口右侧 Agent 抽屉（宽 400），可切换显示；对话区 + 审批列表 + 待提交列表 |
| 授权模式 | `Ask`（默认，逐条批准）· `AutoStage`（直接应用并保存）· `ReadOnly`（只读，丢弃提议） |
| 协议 | 模型在回复末尾给 JSON 块（`actions` / `ask`）；坏块会**自动请求重来一次**；内容里的裸引号会被启发式修复 |
| 预览 | Ask 模式先在画布上画"虚影"（只改预览层，不动真实画布） |
| 确认 | 用户在审批列表勾选 → 「保存画布」落盘；「撤销本批」只丢弃虚影（不还原已应用改动） |
| 回滚 | 独立的「撤销上次提交」：用提交前整张画布快照回滚；要求其间没有其它编辑，否则停用 |
| 反问 | 信息不足时用 `ask` 弹出选项让用户点或自填，最多续跑 3 轮 |
| 上下文 | 选中节点 + 画布节点清单（带短 id）+ 设定库摘要 + 工作树摘要 + 工作文件夹文件清单；按模型上下文窗口截断，**操作协议永不截断** |
| 工作流约束 | 提示词里写死：**先建工作树、再建节点、分两批提议**；两套载体不互相抄；画布资源节点只做锚点 |

已知缺陷（代码事实，待修）：`AutoStage` 路径会把同一批 actions 应用两次（先 `ApplyActions`，随后 `SaveApplied` 因 pending 非空再次应用）。

---

## 五、画布层（现状）

- 桌面端画布是 **WinForms 自绘**，不依赖浏览器；WebView2 与 `postMessage` 通道已从桌面端彻底移除。
- `DreamForge.Canvas`（React/Vite）保留在仓库中但**未接入**：`CanvasApp.tsx` 渲染普通 div，`tldraw` 依赖未被使用，`main.tsx` 仍假设运行在 WebView 内。
- 因此"双端共用同一套画布代码"目前**不成立**；若继续这条路线，需要先决定是复活 TS 画布并重新搭通道，还是彻底放弃该工程。

---

## 六、进度（实际）

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 设定、产品计划、架构与核心契约 | 初稿已完成，**2026-09-28 按代码重新校正** |
| 1 | 版本化宿主协议 + TS 画布基础 | 协议与 TS 侧实现完成；TS 画布**未接入宿主** |
| 2 | Core 领域模型（协议/权限/TypedReference/Job） | 完成并有用例 |
| 3 | 桌面宿主与 Web 宿主壳 | 完成：桌面为 WinForms，Web 为作业服务（无画布） |
| 4 | 单机端到端闭环 | 完成：节点→出图→资产→任务库闭环；**视频未实现** |
| 5 | 设定库 + 工作树（叙事轴/视觉轴） | 完成字段与界面；工作树与节点的关联（`WorkTreeItemId`）2026-09-28 补上 |
| 6 | 协作（房间/Yjs/审批/审计/配额） | **未开始** |
| 7 | 发布打磨（安装包、品牌统一、回归） | **未开始** |

---

## 七、风险校准（按现状改写）

| 风险 | 触发信号 | 应对 |
|---|---|---|
| 文档与代码继续脱节 | 拿旧文档对照代码发现"没这回事" | 本次已校正 01–04；后续改动必须同步 `PROGRESS.md` |
| 根目录 `05_Core_Contracts.cs` 被误当真实契约 | 有人按它写代码 | 它未参与编译且与 Core 不兼容；应删除或移入归档目录 |
| 视频能力被误宣传 | 用户要求"出视频"却没有链路 | 明确标注未实现，只支持图片与文本 |
| AI 改画布失控 | 误改结构、重复建节点 | 已有：审批模式 + 虚影预览 + 锁定节点保护 + 撤销上次提交；仍缺"节点↔工作树"的自动同步引擎 |
| 数据模型语义混用 | 同一角色在画布节点/设定库/工作树三处各写一份 | 已确立两轴边界与"只传 id 不复制文本"的约定，并写进提示词 |
| 受限沙箱里运行 | 出现 `Access to the path ... is denied` | 应用无缺陷；改在沙箱外运行或把项目放在允许目录内 |
| 产品名与工程标识并存（YEEYEEYEE vs DreamForge） | 有人误以为要改程序集名或仓库结构 | 已在《命名规范》写明：改名只针对产品显示名 |

---

## 附录 A：术语表（校正）

| 术语 | 含义 |
|---|---|
| 画布节点 `WorkflowNode` | 一次具体创作动作（剧情/分镜/成品等），带位置、连线、附件、引用 |
| 设定库（视觉轴） | 角色/场景/道具的外观与空间设定，含变体与不可变版本，出图的单位 |
| 工作树（叙事轴） | 章节 → 角色 → 能力 → 能力版本 的剧情设定树，只提供约束 |
| 角色能力 `Ability` | 剧情里"这个角色会什么"，**不是**本程序的生成技能 |
| 生成技能 `SkillDefinition` | 可执行的出图流程（`skills\*.json`） |
| 内置技能 `BuiltInSkills` | 注入给模型的提示词/流程清单，不执行 |
| 插件 | 程序集形式的界面扩展（挂右键/侧栏），不产内容 |
| `parentTarget` | 节点/条目的父子归属，决定画布位置 |
| `workTreeTarget` | 节点关联的工作树锚点（章节/能力） |
| `entityTarget` / `variantTarget` | 节点对设定库实体/变体的引用 |
| 待提交（pending） | Agent 提议已画成虚影但尚未保存到画布文件的状态 |

## 附录 B：待办

- [ ] 可选：把界面文案与品牌资源文件名的大小写变体 `YeeYeeYee` 统一为 `YEEYEEYEE`
- [ ] 处置根目录 `05_Core_Contracts.cs`（未编译的旧契约草稿）
- [ ] 决定 `DreamForge.Canvas`（TS 画布）去留
- [ ] 修 `AutoStage` 重复应用同一批 actions
- [ ] 补 `MarkVersionAdopted` 的协议解析（当前模型无法设置）
- [ ] 实施"节点↔工作树"的自动同步引擎（当前只有键与流程约束）
- [ ] 中国商标网查第 9/42 类「YEEYEEYEE」近似；确定并注册主域名与备份域名；占位同名账号
