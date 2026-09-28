# YEEYEEYEE 技能体系

更新日期：2026-09-28。本文按源码区分生成技能、内置技能、角色能力和程序集插件；产品边界见 [01](01_Project_Plan.md)，运行架构见 [02](02_Architecture.md)。

## 三类技能

| 名称 | 代码载体 | 作用 | 是否出图 | 存储位置 |
|---|---|---|---|---|
| 生成技能 | `SkillDefinition`、`skills/*.json` | 可执行的多步骤图像流程 | 是 | 项目根 `skills/` |
| 内置技能 | `BuiltInSkills` | 注入 Agent 的提示词和流程说明 | 否 | 代码内 |
| 角色能力 | `WorkTreeKind.Ability`、`WorkTreeItem` | 角色在叙事中的能力和章节变化 | 否 | 画布 `WorkTree` |

`WorkTreeKind.Ability` 曾使用 `Skill` 名称。`WorkTreeItem.ParseKind` 仍接受旧协议中的 `Skill`/`技能` 并映射到 `Ability`；枚举按数字落盘，只能追加，不能重排。角色能力与生成技能不共用类型。

## 生成技能

`SkillDefinition` 包含 `Id`、`Name`、`Description`、普通字符串 `Version`、`TargetKind`、`OutputTarget` 和 `Steps[]`。`SkillStep` 包含能力类型、提示词、负面提示词、参考来源、去噪、尺寸和输出名。当前支持 `TextToImage` 与 `ImageToImage`；参考来源可为变体、前置步骤或 `ref:N`。

技能目录由 `DREAMFORGE_SKILL_DIR` 覆盖，否则使用项目根 `skills/`。`SkillLibrary.Load()` 逐个读取 JSON，单文件错误只记录该文件，不阻断其他技能；首次运行会写入人物三视图和参考图两两合成两个示例。校验拒绝空步骤和不匹配的 `TargetKind`，`Any` 放行。

`SkillRunner.RunAsync` 检查图像 Provider 和输出目标，按步骤渲染模板并解析参考图，调用图像 Provider；所有步骤成功后才按 `OutputTarget` 写回变体或节点附件。任一步失败不写回附件列表，但此前已生成的磁盘文件没有在此统一回滚。实体种类校验由 `SkillDefinition.Validate` 提供，不能把它误写成运行器内部的必经校验。`SkillTarget` 描述画布、实体、变体和节点等运行目标。

入口有三处：设定库面板对选中实体和变体运行；插件通过 `IPluginHost.RunSkillAsync` 运行；Agent 的合成规划将计划转换为 `SkillDefinition` 后执行。技能的 `Version` 只是本机清单字段，当前没有云端版本、可见性、迁移、编辑器、配额、审计或破坏性变更规则。Core 中部分能力类型不代表桌面端已接入这些功能。

## 内置技能

`BuiltInSkills.cs` 当前有 9 条记录：`node-operations`、`chapter-decomposition`、`image-generation`、`video-generation`、`story-generation`、`storyboard-generation`、`character-generation`、`scene-generation`、`prop-generation`。

它们只向 Agent 提供名称、说明、输出格式和关键词。`Resolve(text)` 按关键词命中数选择记录，同分按 ID 排序，用于界面选择和调用模板填充。`video-generation` 只描述未配置视频 Provider 时生成任务规格的行为，视频生成链路本身未实现。

## 角色能力

角色能力属于叙事轴，由工作树保存项目 → 章节 → 角色 → 能力 → 能力版本。能力版本带 `chapter`、`version` 和可选的 `SupersedesVersionId`，历史条目不覆盖旧内容。

| | 工作树能力 | 设定库视觉资源 |
|---|---|---|
| 回答的问题 | 角色会什么、哪一章发生什么变化 | 角色长什么样、有哪些变体和版本 |
| 消费者 | Agent 的剧情和分镜上下文 | 节点引用、技能运行和插件入口 |
| 产出 | 叙事约束和叙事侧资料 | 图像和视觉版本 |

工作树只写剧情向内容；外观、服装、形态、空间布局和参考图进入设定库。能力自带的特效、形态参考图和演示视频属于叙事侧资料，可以挂在工作树条目上。节点用 `workTreeTarget` 指向章节或能力条目；角色、场景、道具不建画布节点，由剧情或分镜节点通过 `entityTargets` 引用设定库实体。工作树变化可作为节点更新来源，节点内容和附件不会反向写回工作树。

## 插件体系

插件目录由 `DREAMFORGE_PLUGIN_DIR` 覆盖，否则使用项目根 `plugins/`。每个插件是一个一级子目录，包含 `plugin.json`；`ApiVersion` 必须为 1，程序集默认为 `plugin.dll`，也可由 `EntryType` 指定入口。

`PluginLoader.LoadAll` 使用可卸载的 `AssemblyLoadContext` 逐个加载，入口必须实现 `IDreamForgePlugin`，单个插件失败不影响其他插件。`IPluginHost` 提供右键菜单、侧栏项目、窗口注册和打开、运行技能、当前画布及刷新画布能力。右键挂载点包括 `CanvasNode`、`CanvasBlank`、`Entity`、`Variant`、`VariantImage`。示例 `ThreeViewMenuPlugin` 在变体或参考图上挂载人物三视图入口，再调用 `RunSkillAsync`。

## Agent、版本与范围

Agent 是对话与提议生成器，不是技能。它输出 13 种 `actions` 或 `ask`，由 Ask、AutoStage、ReadOnly 模式决定是否预览、提交或拒绝改动；Agent 可以间接构造并运行生成技能。

设定库有真正的视觉版本机制：`WorkflowEntityVariant.Commit` 生成不可变快照，`VersionDiff` 比较差异，`RollbackTo` 回滚当前内容，`NodeReference.VariantVersionId` 可锁定引用版本。生成技能只有普通版本字符串，角色能力以版本字符串、章节和继承 ID 表示。

云端技能、团队或公开可见性、技能上传下载、依赖图循环检测、技能编辑器、配额审计和视频生成均属于计划能力。完成状态和后续顺序统一维护于 [PROGRESS](PROGRESS.md)。
