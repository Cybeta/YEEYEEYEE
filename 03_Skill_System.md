# YEEYEEYEE 技能体系（按实际代码校正）

> 版本：v2 · 2026-09-28
> 上一版（v1，2026-09-25）写的"Skill = 原子/组合/模板三层资产 + 来源判定 + 可见性 + 云端迁移"是**尚未实现的设想**，本文按代码重写。
> 配套：[02_Architecture.md](02_Architecture.md)

---

## 一、先分清三个"技能"

代码里同时存在三个都用"技能"称呼、但**互不共用类型**的东西。文档和提示词混用它们，是之前协作混乱的主因。

| 名称 | 代码载体 | 是什么 | 会不会出图 | 存在哪 |
|---|---|---|---|---|
| **生成技能** | `SkillDefinition` + `skills\*.json` | 可执行的出图流程：若干模型调用步骤组成 | **会**（产出图片） | 项目根 `skills\` 目录 |
| **内置技能** | `BuiltInSkills`（9 条硬编码记录） | 给模型看的**提示词/流程说明**，不执行任何代码 | 不会 | 代码里，注入到 Agent 上下文 |
| **角色能力** | `WorkTreeKind.Ability` + `WorkTreeItem` | **剧情设定**：这个角色会什么、第几章升级 | 不会 | 画布 `WorkTree` 集合（叙事轴） |

命名注意：`WorkTreeKind.Ability` 在 2026-09-28 之前叫 `Skill`，与"生成技能"同名，导致模型把两者混用。现在枚举已改名 `Ability`，`WorkTreeItem.ParseKind` 仍接受旧协议里的 `"Skill"`/`"技能"` 并映射到 `Ability`（数据按数字落盘，旧画布无需迁移）。

---

## 二、生成技能（文件型）

### 2.1 数据模型

`Skills.cs`：

```
SkillDefinition
├─ Id / Name / Description
├─ Version           普通字符串（如 "1.0.0"），本机清单字段，无云端版本机制
├─ TargetKind        Character | Scene | Prop | Any   ← 适用性校验
├─ OutputTarget      variant | node | node-base       ← 产出写回哪里
└─ Steps[]           SkillStep[]

SkillStep
├─ Id / Name
├─ Capability        TextToImage | ImageToImage（其余能力会被拒绝）
├─ Prompt / NegativePrompt
├─ ReferenceFrom     variant | ref:N | 前置步骤 Id
├─ Denoise / Width / Height
└─ OutputName
```

### 2.2 装载与目录

- 目录：`DREAMFORGE_SKILL_DIR` → 否则**项目根** `skills\`（`SkillLibrary.Directory`）。
- `SkillLibrary.Load()` 读取目录下全部 `*.json`；**单个文件出错只记录该条错误，不影响其它技能**（错误汇总交界面显示）。
- `EnsureDefaultSkills()` 首次运行写入两个内置示例：`character-three-view.json`（人物一键三视图）、`reference-merge-two.json`（参考图两两合成）。
- `SkillDefinition.Validate(entity)`：步骤为空 → 拒绝；`TargetKind` 与实体种类不符 → 拒绝（`Any` 放行）。

### 2.3 执行流程（`SkillRunner.RunAsync`）

1. 必须已配置图像 Provider，否则直接失败。
2. 按 `TargetKind` 校验目标实体。
3. 逐步执行：解析 `Capability` → 渲染提示词（`SkillTemplates.Render`，占位符 `{kind}{name}{variant}{core}{description}{layout}`）→ 解析参考来源（`variant` / `ref:N` / 前置步骤 Id）→ 调用图像 Provider。
4. **全部步骤成功后**才按 `OutputTarget` 写回：
   - `variant`（默认）→ 写入设定库变体的附件；
   - `node` → 写入触发节点的附件；
   - `node-base` → 写入节点附件，并把**最后一步产出**标记 `WorkflowAttachment.Source = "合成底图"`。
5. 任一步失败则**不留半成品**。

`SkillTarget` 描述运行目标（画布 / 实体 / 变体 / 节点），供节点级技能解析引用。

### 2.4 入口

- 设定库面板：「运行技能」，作用于选中实体+变体。
- 节点级：由插件通过 `IPluginHost.RunSkillAsync` 触发。
- Agent 侧：合成规划会把计划转成 `SkillDefinition` 再执行。

### 2.5 明确未实现（不要写进产品说明）

云端技能、团队/公开可见性、`location/scope` 双轴、技能迁移（上传/下载）、typed refs 依赖图与循环检测执行、技能编辑器、配额/审计、破坏性变更版本规则 —— **代码里都不存在**。`Capability`/`SkillLocation`/`SkillScope`/`SkillType`/`ReferenceGraph` 等仅在 `DreamForge.Core` 里有类型与判定实现，桌面端的技能执行链路没有接入它们。

---

## 三、内置技能（提示词清单）

`BuiltInSkills.cs` 共 9 条记录，字段 `Id/Name/Description/OutputFormat/Keywords`：

`node-operations`、`chapter-decomposition`、`image-generation`、`video-generation`、`story-generation`、`storyboard-generation`、`character-generation`、`scene-generation`、`prop-generation`。

- **作用**：作为"可调用的生成技能清单"注入 Agent 上下文（`AgentPane.cs`），让模型知道自己有哪些流程可用；`BuiltInSkills.Resolve(text)` 按关键词命中数打分（同分按 Id 排序）用于在界面里选中技能并填入调用模板。
- **不做任何执行**：它们没有任何对应代码；真正出图的是第二节的"生成技能"。
- **注意**：`video-generation` 只描述"未配置视频 Provider 时只生成任务规格、不伪造结果"——视频生成链路本身未实现（见 [02_Architecture.md](02_Architecture.md) 第七节）。

---

## 四、角色能力（叙事轴，**不是**生成技能）

载体是工作树条目（`WorkTreeItem`，`WorkTreeKind.Ability`），与设定库的"实体/变体/版本"是两条轴：

| | 工作树（叙事轴） | 设定库（视觉轴） |
|---|---|---|
| 回答 | 他会什么、第几章有什么变化、在剧情里起什么作用 | 他长什么样、几套造型、空间布局、参考图 |
| 版本含义 | 剧情进度版本（带 `Chapter`） | 视觉快照（供引用锁定，已定稿章节不被动变化） |
| 消费者 | Agent 上下文（写剧情/分镜时读） | 节点引用→出图、技能运行目标、插件入口 |
| 产出 | 不出图，只出约束 | 出图的单位 |

**约定**（已写入 Agent 提示词）：

1. 结构：项目 → 章节（`Chapter`）→ 角色（`Character`，挂在所属章节下）→ 能力（`Ability`，挂在角色下）→ 能力版本（`Version`，挂在能力下，带 `chapter` 与 `version`）。
2. 单向：工作树变了才更新节点；节点的内容与附件**不回写**工作树。
3. 边界：工作树只写剧情向内容；外观、服装、形态、空间布局、参考图一律进设定库；**能力自带素材（特效/形态参考图、演示视频）算叙事侧资料，可以挂在工作树条目上**。
4. 节点关联：节点用 `workTreeTarget` 指向章节或能力条目，落到 `WorkflowNode.WorkTreeItemId`。

---

## 五、插件体系（程序集驱动）

- **目录**：`DREAMFORGE_PLUGIN_DIR` → 否则项目根 `plugins\`；每个插件一个一级子目录，含 `plugin.json`（`PluginManifest`，`ApiVersion` 必须为 1）+ 程序集（默认 `plugin.dll`，可用 `EntryType` 指定入口）。
- **加载**：`PluginLoader.LoadAll` 用可卸载的 `AssemblyLoadContext` 逐个加载，入口须实现 `IDreamForgePlugin`；加载失败只影响该插件。
- **宿主能力**（`IPluginHost`）：`AddContextMenuItem`、`AddRailItem`、`RegisterWindow`、`OpenWindow`、`RunSkillAsync`、`CurrentCanvas`、`RefreshCanvas`。
- **右键挂载点**（`ContextTarget`，5 个）：`CanvasNode`（节点）、`CanvasBlank`（画布空白）、`Entity`（设定库实体）、`Variant`（设定库变体）、`VariantImage`（变体参考图，可针对单张图执行）。
- **内置示例**：`ThreeViewMenuPlugin` 把"人物一键三视图"挂到 `Variant` 与 `VariantImage` 上，点击后调 `host.RunSkillAsync("character-three-view", …)`——插件只挂入口，内容生产统一交给技能。

---

## 六、与 Agent（AI 助手）的关系

- Agent 不是技能：它是对话 + 提议生成器，输出的是 `actions`（12 种 kind），经"审批/自动"模式落到画布，见 [02_Architecture.md](02_Architecture.md) 与 `AgentActions.cs`。
- Agent 可以**间接**调用生成技能：合成规划会构造 `SkillDefinition` 并执行。
- 权限与撤销：Ask 模式下改动先出虚影预览，用户确认后才应用；"撤销上次提交"依赖提交前整张画布快照，且要求其间没有其它画布编辑。
- 图片/视频约束：**视频没有生成链路**；图片必须说明参考图、画幅与当前模型能力。

---

## 七、版本管理（现状）

- 生成技能：`skills\*.json` 里一个普通 `Version` 字符串，**没有**版本对比、回滚、锁定、破坏性变更规则。
- 设定库：有真正的版本机制——`WorkflowEntityVariant.Commit` 提交不可变快照，`VersionDiff` 做差异比较，`RollbackTo` 回滚当前内容，`NodeReference.VariantVersionId` 可把引用锁定到具体版本。
- 角色能力：用 `Version` 字符串 + `SupersedesVersionId` 记"继承自上一版"，配 `Chapter` 表示来源章节；历史条目不覆盖，新增版本条目。

> 结论：真正的版本能力只在**设定库**里；技能与能力只有版本字符串。
