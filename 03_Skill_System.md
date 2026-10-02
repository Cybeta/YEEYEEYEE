# YEEYEEYEE 技能体系

更新日期：2026-09-28。本文按源码区分生成技能、内置技能、角色能力和程序集插件；产品边界见 [01](01_Project_Plan.md)，运行架构见 [02](02_Architecture.md)。

## 三类技能（外加一类不是技能的东西：站点）

| 名称 | 代码载体 | 作用 | 是否出图 | 存储位置 |
|---|---|---|---|---|
| 生成技能 | `SkillDefinition`、`skills/*.json` | 可执行的多步骤图像流程 | 是 | 项目根 `skills/` |
| 内置技能 | `BuiltInSkills` | 注入 Agent 的提示词和流程说明 | 否 | 代码内 |
| 角色能力 | `WorkTreeKind.Ability`、`WorkTreeItem` | 角色在叙事中的能力和章节变化 | 否 | 画布 `WorkTree` |
| **站点**（不是技能） | `SiteProfile`、`skills/sites/*.json` | 一家接口站有哪些能用的池子 | 否（是调用参数） | 项目根 `skills/sites/` |

## 站点与池子

一家接口站（例如 `api.example.com`）在叙事与画布之外还带来一个独立的问题：**它下面有哪些能用的接口**。
把它建成技能是错的——那家站有 20 个生图模型 × 3 档 = 60 多个池子，逐个建文件只会把技能目录刷满，
而且真正要回答的是「这家有哪些能用的」，那是一个**站点**的问题，不是六十个技能的问题。

所以：**站点是一级实体，池子是它下面的选项**。

- `SiteProfile`：站点标识（由主机名派生，`video.example.com` → `example`）、显示名（可改）、基础地址、
  各类接口路径（文生图 / 图生图 / 出视频）、鉴权方式、池子清单、以及**清单是从哪个地址探到的**。
- `SitePool`：模型 × 档位，外加画幅 / 时长 / 价格 / 是否吃参考图 / 清单里是否标为下线。
  一个池子就是一次可执行的出图配置；名字里的「(池6)」这类后缀是名字的一部分，必须原样保留。
- 一个站点一个文件，放在 `skills/sites/` 子目录里——**不和技能混一层**：
  技能是「一步一步的流程」，站点是「一家有哪些能用的」，混在一起，装载器与技能管理页都得靠文件名前缀去猜。

**池子清单从哪来**（`SitePoolProbe`，全程只读、不发任何生成请求）：按顺序试几个常见来源
（站点后台的清单接口 → OpenAI 兼容的 `{base}/models`），**拉到的内容必须能被解析成池子才认**；
一个都认不出来就退回文档里的「可用模型」表，并把实际用上的来源记进站点文件。
解析器刻意写得宽容：模型名取 `alias/id/name/model/key/label` 里第一个非空的，档位取
`resolutions/sizes/tiers` 或 `durations`（逗号分隔的一行也拆），价格按档位取。
**取不到档位就留空，不猜一档**；一条都认不出来时返回空清单，由调用方退回文档那一份。

**调用前先问一句用哪个池子**：同一句话用 1K 还是 4K、用哪个池，出的钱能差好几倍，
替用户挑一个「默认的」是不正当的。选择器做成三级（站点 → 模型 → 档位）而不是把几十个池子平铺——
平铺的列表里，同一个小区的池子除了「(池6)/(池1)」这一个后缀之外没有任何区别，翻起来只会让人放弃选择。
**上一次选过的池子会被记住并预选**（站点标识 + 模型 + 档位存在配置里）：每次都要重新翻一遍三级列表，
用户最后还是会随便挑一个，那比替他从上次的选择继续更糟。找不到上次那个池子时**不退回第一个**，
而是从空白开始选 —— 悄悄换成别家的池子会直接变成「出的图不对，钱也花了」。
**出视频的池子**在出视频执行方接入之前选了只会得到一句明确拒绝，不会偷偷起一个花钱的异步任务。

**导入一个站点的产物是一个站点文件，不是一串技能文件**（旧 WinForms 端曾保持另一套行为，该端已在第 128 轮删除）。

`WorkTreeKind.Ability` 曾使用 `Skill` 名称。`WorkTreeItem.ParseKind` 仍接受旧协议中的 `Skill`/`技能` 并映射到 `Ability`；枚举按数字落盘，只能追加，不能重排。角色能力与生成技能不共用类型。

## 生成技能

`SkillDefinition` 包含 `Id`、`Name`、`Description`、普通字符串 `Version`、`TargetKind`、`OutputTarget` 和 `Steps[]`。`SkillStep` 包含能力类型、提示词、负面提示词、参考来源、去噪、尺寸和输出名。当前支持 `TextToImage` 与 `ImageToImage`；参考来源可为变体、前置步骤或 `ref:N`。

技能目录由 `YEEYEEYEE_SKILL_DIR` 覆盖，否则使用项目根 `skills/`。`SkillLibrary.Load()` 逐个读取 JSON，单文件错误只记录该文件，不阻断其他技能；首次运行会写入人物三视图和参考图两两合成两个示例。校验拒绝空步骤和不匹配的 `TargetKind`，`Any` 放行。装载时会给每条技能补上 `FilePath`（不写进 JSON），启停与删除都要靠它。

**启停与删除（设置 → 技能管理）**：`SkillDefinition.Enabled` 默认 true，停用的技能保留配置与文件、只是不再被调用。`SkillLibrary.TrySetEnabled` 用 `JsonNode` **只改 `Enabled` 这一个键**——技能文件里可能有这一版不认识的字段，整体反序列化再写回会把它们抹掉，那是静悄悄的数据损失；写回时显式放宽 `Encoder`，否则中文全变成 `\uXXXX`，技能名与提示词从此没法读也没法 diff。`SkillLibrary.TryDelete` **只删这一个文件**：同一来源导入的其它文件（`OwnedFiles`）可能被同来源的其它技能共用，顺手删掉会把它们一起弄坏。界面上删除要先确认，因为技能文件删掉后没法从界面找回。

`SkillRunner.RunAsync` 检查图像 Provider 和输出目标，按步骤渲染模板并解析参考图，调用图像 Provider；所有步骤成功后才按 `OutputTarget` 写回变体或节点附件。任一步失败不写回附件列表，但此前已生成的磁盘文件没有在此统一回滚。实体种类校验由 `SkillDefinition.Validate` 提供，不能把它误写成运行器内部的必经校验。`SkillTarget` 描述画布、实体、变体和节点等运行目标。

入口有三处：设定库面板对选中实体和变体运行；插件通过 `IPluginHost.RunSkillAsync` 运行；Agent 的合成规划将计划转换为 `SkillDefinition` 后执行。技能的 `Version` 只是本机清单字段，当前没有云端版本、可见性、迁移、编辑器、配额、审计或破坏性变更规则。Core 中部分能力类型不代表桌面端已接入这些功能。

## 内置技能

`BuiltInSkills.cs` 当前有 9 条记录：`node-operations`、`chapter-decomposition`、`image-generation`、`video-generation`、`story-generation`、`storyboard-generation`、`character-generation`、`scene-generation`、`prop-generation`。

它们只向 Agent 提供名称、说明、输出格式和关键词。`Resolve(text, disabled)` 按关键词命中数选择记录，同分按 ID 排序，用于界面选择和调用模板填充；`DescribeForAgent(disabled)` 把它们摊成提示词里的「可调用的生成技能清单」。两个入口都接受一份**停用名单**（`AiProviderConfig.DisabledBuiltInSkills`）：内置技能是编译进程序的静态表，删不掉（删了下次启动又回来），停用才是它真正的开关，而停用必须**同时**作用于「关键词命中」与「摊给 Agent 的清单」两处——只在命中处生效的话，提示词里还写着它，模型就会照着去调。全部停用时清单给一句「内置生成技能当前全部处于停用状态」，而不是留一段空白让模型以为没有技能。`video-generation` 只描述未配置视频 Provider 时生成任务规格的行为，视频生成链路本身未实现。

## 角色能力

角色能力属于叙事轴，由工作树保存项目 → 章节 → 角色 → 能力 → 能力版本。能力版本带 `chapter`、`version` 和可选的 `SupersedesVersionId`，历史条目不覆盖旧内容。

| | 工作树能力 | 设定库视觉资源 |
|---|---|---|
| 回答的问题 | 角色会什么、哪一章发生什么变化 | 角色长什么样、有哪些变体和版本 |
| 消费者 | Agent 的剧情和分镜上下文 | 节点引用、技能运行和插件入口 |
| 产出 | 叙事约束和叙事侧资料 | 图像和视觉版本 |

工作树只写剧情向内容；外观、服装、形态、空间布局和参考图进入设定库。能力自带的特效、形态参考图和演示视频属于叙事侧资料，可以挂在工作树条目上。节点用 `workTreeTarget` 指向章节或能力条目；角色、场景、道具不建画布节点，由剧情或分镜节点通过 `entityTargets` 引用设定库实体。工作树变化可作为节点更新来源，节点内容和附件不会反向写回工作树。

## 插件体系

插件目录由 `YEEYEEYEE_PLUGIN_DIR` 覆盖，否则使用项目根 `plugins/`。每个插件是一个一级子目录，包含 `plugin.json`；`ApiVersion` 必须为 1，程序集默认为 `plugin.dll`，也可由 `EntryType` 指定入口。

`PluginLoader.LoadAll` 使用可卸载的 `AssemblyLoadContext` 逐个加载，入口必须实现 `IYEEYEEYEEPlugin`，单个插件失败不影响其他插件。`IPluginHost` 提供右键菜单、侧栏项目、窗口注册和打开、运行技能、当前画布及刷新画布能力。右键挂载点包括 `CanvasNode`、`CanvasBlank`、`Entity`、`Variant`、`VariantImage`。示例 `ThreeViewMenuPlugin` 在变体或参考图上挂载人物三视图入口，再调用 `RunSkillAsync`。

## Agent、版本与范围

Agent 是对话与提议生成器，不是技能。它输出 13 种 `actions` 或 `ask`，由 Ask、AutoStage、ReadOnly 模式决定是否预览、提交或拒绝改动；Agent 可以间接构造并运行生成技能。

设定库有真正的视觉版本机制：`WorkflowEntityVariant.Commit` 生成不可变快照，`VersionDiff` 比较差异，`RollbackTo` 回滚当前内容，`NodeReference.VariantVersionId` 可锁定引用版本。生成技能只有普通版本字符串，角色能力以版本字符串、章节和继承 ID 表示。

云端技能、团队或公开可见性、技能上传下载、依赖图循环检测、技能编辑器、配额审计和视频生成均属于计划能力。完成状态和后续顺序统一维护于 [PROGRESS](PROGRESS.md)。
