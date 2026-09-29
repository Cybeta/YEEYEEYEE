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

画布 JSON 当前保存 `Nodes`、`Edges`、`Entities`、`WorkTree`，并在保存时标注 `FormatVersion`（缺省 0 表示旧文件）。`Entities` 与 `WorkTree` 仍是画布级数据，项目级共享资源库属于后续迁移。

| 字段 | 职责 | 约束 |
|---|---|---|
| `ParentNodeId` / `parentTarget` | 画布父子关系和布局归属 | 不表示叙事状态，也不表示资源来源 |
| `WorkTreeItemId` / `workTreeTarget` | 工作树章节或能力的叙事锚点 | 关联不存在时拒绝创建/更新；自动同步待实现 |
| `References[]` / `entityTargets` | 实体、变体、可选视觉版本 | 一个分镜可引用多个实体；持久化使用稳定 ID |
| `SourceEntityId` 等 | 工作树条目的视觉来源 | 只表达来源，不触发自动复制或同步 |

产品规则与当前 Agent 提示词要求角色、场景、道具不创建常驻资源画布节点，由分镜通过 `References`/`entityTargets` 引用，按需临时展开可视化。旧 `Character`、`Scene`、`Prop` 枚举及创建入口仍保留，旧节点尚未迁移，不能声称代码已全面禁止资源节点。工作树写剧情向设定；资源库写跨章节视觉设定；节点保留自身文本、位置、附件、生成历史和引用。

模型已有 GUID，Agent 仍支持名称或短 ID 解析。稳定 ID 的校验、保存重载保真、复制重映射、旧数据迁移与重复导入去重已实现（见下一节）；`WorkflowCanvasState.Entities` 的“项目级设定库”注释与实际画布级持久化仍不符（项目级资源库属后续批次），`WorkTreeItemId` 的“跟随项目树更新”注释也不代表同步引擎已实现。

## ID 生命周期、迁移与导入

`DreamForge.Desktop/Canvas` 下的五个文件构成一条只读校验 → 迁移 → 备份写入 → 复制 → 导入的链路，UI 只调用这一层的用例方法，不在界面里做数据判定。

| 文件 | 职责 |
|---|---|
| `CanvasIdentityValidator.cs` | 只读校验：ID 空/重复、连线端点、父链与循环、工作树锚点与来源、实体/变体/版本引用与归属、资产标识形状。42 个稳定错误代码；只用候选集合判归属，多候选报歧义，全部候选都不属于所写上下文才报错绑 |
| `CanvasMigration.cs` | `CanvasFormat` 与迁移：旧单引用/旧图片路径搬运、空 GUID 补稳定 ID（迁移前文档指纹 + 对象类型 + 字段路径）、歧义收集；高于当前支持的格式版本不做迁移也不降级标注；`DropUnresolvableReferences` 与 `TreatEmptyLockedVersionAsFollowCurrent` 是仅由用户触发的显式处理入口 |
| `CanvasStorage.cs` | `CanvasCloner`（深拷贝）、`CanvasBackup`（画布库 `backups/`，每画布保留最新 10 份，可列出/恢复）、`CanvasFileWriter`（临时文件 + 替换）、`CanvasOpenService.TryOpen`（读取→迁移→校验，不写盘；未知高版本只读查看）、`CanvasSaveService.Save`（副本→迁移→校验→写前备份→原子替换；备份失败抛 `CanvasSaveAbortedException` 中止，不覆盖原文件） |
| `CanvasDuplication.cs` | 复制画布/节点：新 ID、集合内关系按「源画布唯一候选 + 所属上下文」重写（版本按所属变体解析）、集合外引用按共享保留，`VariantVersionId = null` 的跟随语义原样保留；重复 ID 或上下文不符时不猜归属，保留原引用并在报告里列为歧义 |
| `CanvasImportMerge.cs` | 导入合并：按 ID 与作用域判定身份（同名不参与），内容一致跳过、同 ID 不同内容报冲突且不覆盖、变体 ID 被占用时整条实体不合并；内容指纹排除附件/布局/历史 ID 与附件时间；`CanvasImportService.TryImport` 支持替换/合并两种模式，未知高版本拒绝导入 |

数据格式版本记录在 `RecentCanvasState.FormatVersion`（0 = 未标注的旧文件，1 = 当前）。打开旧文件只在内存迁移并提示问题，保存时才写回并标注版本；写前自动备份，**备份失败会中止保存**，写入采用临时文件替换，失败保留源文件；保存与复制都在深拷贝上进行，失败不改动调用方对象图。迁移不猜测绑定：指向不存在对象或空值语义不唯一的引用留成歧义项，由用户在“检查画布”里显式处理。高于当前支持的格式版本只读查看，覆盖保存与导入都被拒绝。

保存入口只有一条链路：`CanvasLibrary.Save`（库保存、标签切换、关闭写回）与 `CanvasLibrary.Rename`（重命名，含覆盖目标）都是 `CanvasSaveService.Save` 的薄封装，`CanvasCommandService.Save` 复用 `CanvasLibrary.Save`。因此深拷贝、迁移、校验、写前备份与原子替换对全部入口一致生效；重命名在新文件写成功后才删除源文件。以后新增保存路径时必须调用 `CanvasSaveService.Save`，不要直接写盘。

ID 作用域：节点、连线、工作树条目、实体、变体为画布级唯一；版本为所属变体内唯一；附件、布局元素、生成历史的 ID 不参与查找。资产标识（`WorkflowAttachment.Reference`）是路径或 URI，按 `AssetStore.Resolve` 的真实读取规则判形状，可用但非规范的写法只警告。

## 企划、章节泳道与成品下挂

目标模型为全局企划 → 章节 → 分镜 → 成品。全局企划是单份全局约束，位于章节泳道之外；章节泳道承载本章章节节点、分镜和成品；分镜横向排列，成品按分镜分组横向排列并显示在分镜下方。资源引用条属于节点内部的临时展开区域，不是独立资源节点。

当前 `WorkflowCanvasControl` 有 `ChapterKeyOf`、`ChapterBounds`、`ArrangeChapter`、节点坐标和拖动；`AgentActions.PlaceNewNode` 会对章节区块调用排版。现有实现仍按章节块和节点分类计算位置，尚未持久化“自动位置/手动位置”来源，也没有专门的分镜成品下挂布局。新增或局部排版必须在后续实现中限制于目标章节，保留其他章节和用户手动坐标。

## 章节身份与工作树同步

章节的权威身份是工作树里 `WorkTreeKind.Chapter` 条目的 ID（`WorkTreeItem.Id`），父子关系用 `ParentId`，排序用显式字段 `WorkTreeItem.Order`（0 表示未指定，由 `CanvasChapters.EnsureExplicitOrder` 按「第N章」数字再按条目出现顺序确定性补齐，幂等）。节点的归属靠稳定锚点 `WorkflowNode.WorkTreeItemId` 或父节点链解析（`CanvasChapters.ResolveChapterId`）；`WorkflowNode.Chapter` 只是显示文本，**任何绑定都不按名称判定**，同名章节会报 `CHAPTER_NAME_AMBIGUOUS` 并停止自动绑定。

| 文件 | 职责与边界 |
|---|---|
| `CanvasChapters.cs` | 章节身份层：`List`（按显式顺序）、`ChapterOfAnchor` / `ResolveChapterId`（按 ID 与父链解析归属）、`EnsureExplicitOrder`（顺序补齐）、`Diagnose`（缺锚点、悬空锚点、同名歧义、顺序重复/缺失、父链循环） |
| `CanvasChapterOperations.cs` | 章节结构操作：建立/改名/排序/移动/拆分/合并/删除与顺序归一化。全部在副本上执行，有阻断冲突时不做任何改动；移动成环、删除有引用（未选择改挂）会阻断；拆分/合并/删除按 ID 重接节点锚点与子章节父链，并同步显示文本 |
| `CanvasWorkTreeSync.cs` | 双向同步：`Plan`（只读预览，四遍判定：锚点 → 新建章节 → 父链上下文传播 → 文本兜底）、`Apply`（副本上执行，存在阻断冲突整批拒绝，`RequiresConfirmation` 项需显式确认）、`CanvasSyncSession`（计划 + 应用 + 内存快照撤销）。同一变体/章节的对齐依据是 ID，不是名称 |
| `CanvasChapterStructureSession.cs` | 桌面结构操作的唯一逻辑路径：包住 `CanvasChapterOperations`，成功改动的状态入内存撤销栈（JSON 快照），阻断操作不改动状态（`LastBlocked`），`Undo` 恢复上一状态，`SaveCurrent` 通过注入的保存委托走统一保存入口并把真实成功/失败结果记到 `LastSaveSucceeded`（失败时不刷新已保存快照，`HasUnsavedChanges` 仍为 true）。UI 与入口级回归都驱动它 |
| `ChapterStructureDialog.cs` | 桌面「章节结构」入口：工具栏按钮触发，提供新建/改名/上移/下移/移动到/按顺序排序/拆分/合并到目标/删除/删除并改挂上级/顺序归一化/撤销/保存；每次操作先经会话在副本上执行并显示改动与冲突，阻断时明确提示「画布未改动」；只读画布禁用全部结构、撤销与保存按钮并给出可见提示 |

判定顺序与冲突语义：锚点与父链是**确定依据**，可直接应用；只靠唯一同名条目匹配标记为**待确认**（不会静默绑定）；同名多条目、悬空锚点、重复章节 ID、跨章节错绑都进入冲突报告。工作树是叙事轴事实源，因此章节名与章节节点标题不一致时，提议把名称同步到节点标题（待确认），不反向改写工作树。同步产生的画布变更由界面调用 `CanvasSaveService.Save` 落盘，备份、失败不覆盖与原子替换继续生效；`CanvasSyncSession.Undo` 只回滚内存状态，磁盘文件不变。

`CanvasMigration` 在迁移时会为章节补齐显式顺序并记录 `NormalizedChapterOrder`（幂等，第二次零改动）；`CanvasPackage` 导入后会用 `CanvasChapterImportSummary` 报告章节数、章节诊断与同步冲突，章节元数据与锚点随画布文件进出，无需单独迁移。

## 章节泳道布局与引用展开

批次 C 把画布从「按章节名分块」升级为「按稳定章节身份分泳道」。`CanvasSwimlaneLayout` 是纯计算引擎：企划节点（`StoryPlan` / `StoryOutline`）放在章节泳道之外的一条企划区，`CanvasChapters.List` 的显式顺序决定章节泳道顺序，分镜在泳道内横向排列（先按工作树锚点的 `Order`，再按当前阅读顺序），成品按 `ParentNodeId` 挂在对应分镜正下方；资源类节点（角色/场景/道具）只作锚点、不进章节泳道。归属一律走 `CanvasChapters.ResolveChapterId`，只有章节名文本的节点进「未分章」泳道，不会被按名字猜进某章。引擎只产出 `CanvasLayoutChange` 位置改动，`Apply` 在副本上写 X/Y，**不新增/删除节点，也不改锚点、引用和父子关系**。

`CanvasLayoutSession` 承载预览与撤销：`PreviewAll` / `PreviewChapter` 只算不改（`PreviewChapter` 把泳道锚定在当前左上角，所以局部重排不动其他章节）；`Apply` 有阻断冲突时整批拒绝、有手动摆放节点时要求显式确认「自动布局覆盖」（确认后按同一范围以 `OverrideManual` 重新计算）、计划过期也拒绝；成功后压入 JSON 快照，`Undo` 只回滚内存，保存继续走统一保存入口。桌面入口是 `ChapterLayoutDialog`（工具栏「章节布局」）与工具栏「展开引用」。

泳道引擎是**唯一**权威布局路径：工具栏「整理画布」/右键「整理画布（按章节泳道）」改走 `PlanAll` 并按同一套手动坐标保护写回（`WorkflowCanvasControl.AutoArrange` 返回计划，`MainForm.ArrangeCanvasBySwimlanes` 如实提示「保留了几个手动节点 / 因重叠取消」），Agent 新建节点的落位也改走 `PlanLane`（先按加节点之前的状态锁定泳道锚点，再用稳定章节 ID 判定归属）。`ChapterKeyOf` / `GroupByChapter` / `ChapterBounds` / `ArrangeChapter` 是批次 C 之前的按名称分块实现，现在没有调用方，保留仅为兼容旧插件，新代码不要再用。

节点的 `ManualPosition` 是追加落盘字段：用户在画布上拖动过位置就置 true，自动布局默认保留它并让其他节点绕开；选择「自动布局覆盖」后写回时会清除该标记。两个手动节点互相重叠报 `LAYOUT_MANUAL_OVERLAP` 阻断，不能靠覆盖绕过。

`CanvasReferenceExpansionState` 是纯内存的引用卡展开状态，它完全不认识 `WorkflowCanvasState`，因此展开/收起引用不可能改动节点、锚点、引用或落盘字段，也不会生成常驻画布节点；`CanvasReferences` 提供按稳定实体 ID 的反向定位（`NodesReferencing`）与锁定版本缺失检查（`MissingLockedVersions`），锁定版本缺失的引用在画布上以红色徽标标出。`NodeProjection` 现在给每个节点带 `chapterId`（稳定章节 ID），章节记录另带 `order`，Web 端据此筛选与排序，不再按章节名文本绑定。

## 模型与媒体接口接入

模型接入分三类，配置项都在全局配置文件（`ai-config.json`，可用 `DREAMFORGE_CONFIG` 覆盖）：文本接口（OpenAI 兼容 `/chat/completions` 或 Anthropic `/v1/messages`）、画图接口（`ImageEndpoint`/`ImageModel`，走 OpenAI 兼容 `/images/generations`）、画视频接口（`VideoEndpoint`/`VideoModel`，异步任务：提交后轮询取结果）。三者都可留空地址表示复用主接口地址（`EffectiveImageEndpoint`/`EffectiveVideoEndpoint`），并有对应的 env 覆盖（`DREAMFORGE_IMAGE_*`、`DREAMFORGE_VIDEO_*`）。ComfyUI 是另一条链路：`ComfyUiBaseUrl` + `ComfyUiCheckpoint` 都填齐才启用，缺一不可，启用后优先于画图接口。**未配置画视频时只生成任务规格，不伪造视频结果**。

密钥只写在 `AiProviderConfig.ApiKey` 一处，落盘经 `SecretProtector` 加密（DPAPI CurrentUser 作用域），界面回显一律走 `Describe` 的脱敏形式，绝不回显完整密钥。设置对话框就地修改已加载的配置对象、只覆盖自己负责的字段——**不能 new 一个新对象再保存**，否则主题、上下文窗口、Agent 工作目录、采样开关这些未列字段会被静默重置。

`ProviderImporter` 是接口智能导入的识别核心（用户追加需求）：输入一段任意文本（文档片段、curl、JSON、env 键值对），输出「识别到的类型 + 基础地址 + 模型 + checkpoint + 密钥 + 命中判据 + 需确认项」。判定规则按判据强弱排序：ComfyUI 判据（`/object_info`、`:8188`、`comfyui` 等）优先，其次画视频，再次画图，最后文本；画图与画视频判据同时命中时返回「未能判断」并让用户手选，不替用户猜。地址一律归一化为基础地址（剥掉 `/object_info`、`/images/generations` 这类具体路径，保留 `/v1` 版本前缀），与「地址栏填基础地址、程序按协议补路径」的既有约定一致。识别阶段零副作用；`DescribeChanges` 给出「导入后会写入哪些字段」的预览；`Apply` 只在用户确认后写入，且**只写识别到的字段**，未识别到的一律不动。

`ProviderImportDialog` 是这个模块的窗口：粘贴 → 智能识别 → 核对字段与判据 → 测试连接（复用 `ComfyUiProbe` / `OpenAiCompatibleProbe`）→ 导入并保存。保存失败（配置文件不可写、磁盘只读）必须如实报出并说明本次导入未生效，绝不显示成功。

### 接口导入向导（网页 → 技能 → 密钥 → 最小测试）

`ApiImportWizardDialog` 把上面这套「识别 + 写入」包成一条按顺序推进的向导，四步：给一个接口说明网页 → 自动建 api 生图 / api 生视频技能 → 输密钥（加密落盘）→ 询问是否做一次最小测试。

- **抓取**：`ApiDocFetcher` 只做一次 GET，不执行页面脚本。抓不到、返回非 2xx、页面为空都如实说明并让用户改贴文档文字。**前端渲染的文档站会被单独识别出来**：`/about` 这类 SPA 抓回来只有几百字节的容器外壳，剥完标签几乎没有可读文字，此时直接提示「请在浏览器里打开该文档，把接口说明复制到输入框」，而不是让后面的解析报一句「文档里没写接口」——真实站点（video.anyaiapi.top/about）就是这个形态。
- **解析**：`ApiDocAnalyzer.Analyze` 是纯文本解析（不联网，便于离线回归），判定顺序是 OpenAPI/Swagger JSON → HTML（先剥标签）→ 纯文本。产出 `ApiOpCandidate`（能力、方法、路径、模型、尺寸、是否异步、鉴权方式、命中判据）。四条硬边界：只报文档里写了的内容（解析不出模型/尺寸就留空，不套默认值）；**判不出用途的路径不放进清单**，只记 warning，且**判用途以路径为准、正文只做兜底**（正文里的「图生视频」常是在描述某个可选参数，拿它给通用的 `/v1/videos` 定性会判错）；**路径后紧跟 `{id}` 这类占位符的查询 / 下载端点不算生成接口**（`/v1/videos/{id}`、`/v1/videos/{id}/content`），否则会凭空多出接口并抢走旁边模型表的归属；**上下文按最近的「路径出现处」归属**（同一端点常出现多次：端点清单 + 参数章节），这样参数章节能并回同一条接口。
- **可用模型表**：`ApiModelEntry` 与 `ParseModelTable` 单独解析文档里的「可用模型」表（表头必须含 model / 模型列，**且表头只认第一行**——放宽到任意行会把参数表里那行 `| model | string | 必填 | … |` 当成表头，于是 prompt / seconds / input_reference 这些参数名会被当成模型名建出池子）。模型名的括号后缀与尾注是名字的一部分，必须保留（`gpt-image-2.5-sunburst(池6)`、`gpt-image-2(号池8)原生`，截断就是另一个模型）；类型列给出图像 / 视频；**该模型支持的档位取自它自己那一行**。表行不参与接口上下文归属，避免出图接口读到出视频的模型。
- **建技能**：`ApiSkillFactory.Build` 产出「一条父技能 + 若干池子子技能」。父技能 `api-image` / `api-video` 为文档里的每条接口出一步（跑一次等于把文档链路走一遍）；子技能按「模型 × 尺寸」派生，命名就是用户要的那种「生图池1 1K」「生图池2 2K」，尺寸沿用文档写法（`1k→1K`、`720p` 保留分辨率叫法）。**池子优先从可用模型表建**（表里每个模型自带类型与档位，最准），没有表时才退回「按接口段落里的模型名分组」的兜底做法并提示用户核对（这条在真实文档上会串味）。池子按「模型 × 档位」展开后限流 `MaxPoolsPerKind = 12`（聚合站常有几十个组合，一次全建会把技能目录刷满），超出部分如实提示而不是静默丢弃。`Write` 只覆盖自己写出的文件名（`api-image-pool-1-1k.json` 这种由技能 Id 派生的名字），重复导入同一份文档不新增文件，目录不可写时逐个文件报错而不是整体静默失败。
- **密钥**：`SaveKey` 复用 `ProviderImporter.Apply`（按识别出的类型分别写画图 / 画视频的地址与模型）+ `AiProviderSettings.Save`（DPAPI 加密落盘）。保存失败显示失败，界面与状态区都不回显完整密钥。
- **最小测试**：`ApiMinimalTest.Plan` 优先挑**不需要参考图**的接口（文生图 → 文生视频），只有图生图 / 图生视频的文档明确说明「最小测试需要一张参考图，请在画布节点上运行技能」，不硬跑一次注定失败的调用。测试真的调一次 `IImageProvider`，成功就把返回的图片显示在窗口里并给出真实文件路径；选「否」则跳过测试且**不谎报测试结果**（`LastTestSucceeded` 保持 null，与「测了但失败」区分开）。
- **出视频链路**：`SkillStep.Model` 与 `ImageGenerationRequest.Model` 让「池子」能逐步骤指定模型（留空则用设置里的默认模型）；`SkillRunner` 现在同时接受出图与出视频能力，出视频走 `IVideoProvider`。**出视频执行方目前只有 `UnconfiguredVideoProvider` 一个实现**：接口说明与技能可以先建好、可保存，但运行时会如实报「出视频链路尚未接入」，不拿别的文件冒充视频。纯出视频技能不再因为「没配出图模型」被拦住（`SkillDefinition.NeedsImageProvider` / `NeedsVideoProvider`）。
- **余额与上线模型**：窗口右上角显示账号余额（`ApiAccountProbe` 查 `GET {基础地址}/user/balance` + `GET {基础地址}/models`，带 Bearer 鉴权）。保存密钥后自动查一次，每次最小测试后再查一次并算出**本次实际消耗**（真实站点实测 1K 出图扣 2 积分，与文档定价表一致）；点余额那一行可手动重查。余额字段按站点习惯取 `balance`（其次 `credits`），取不到就显示原因，**不拿 0 冒充余额**。文档写了但接口当前没上线的模型会被列出来（这些名字调用一定 404），并提供「按接口实际模型重建技能」：`ApiSkillFactory.Build(report, liveModels)` 按线上列表收敛池子，父技能里失效的模型名退回空串（改用设置里的默认模型），不把死名字留在技能里。
- **大模型修正解析**：本地规则解析不出接口时，可点「让大模型分析」请已接入的文本模型把正文整理成结构化 JSON（`ApiDocRepair` + `IAiJsonCompleter`）。`IAiJsonCompleter` 与对话用的 `IAiChatProvider` **分开**：对话模式的系统提示故意不要求 JSON，混用会让模型的既有协议（改动块 / 反问块）与本次要求打架。提示词逐条写死只收录文档里确实有的内容、只收生成接口、模型名原样抄；返回的 JSON 逐条校验（能力名白名单、方法白名单、路径必须以 `/` 开头、带 `{id}` 的详情端点丢掉、模型必须有名字），不合格的条目丢掉并计数；**未接入大模型时按钮显示「未接入」并禁用**，不给一个点下去必然失败的入口。基础地址缺失或丢版本前缀时按接口路径补上。
- **图片扩展名按文件头**：`ImageFormatSniffer` 依 PNG/JPEG/GIF/BMP/WEBP 的文件头决定落盘扩展名。真实站点实测返回的是 JPEG，原先一律存成 `.png`，扩展名言不顺（缩略图、外部打开、后续转码都可能判错格式）。

### 第 15 轮返工：提交语义、执行契约与来源隔离（R1–R6）

- **重试上限按整条尝试链判定（R1）**：`JobRetryPolicy.PlanRetry(chain, source, limit, out next, out reason)` 用「同根尝试链上已用过的最大尝试号」决定下一次，而不是 `source.Attempt + 1`——后者会被「反复重试同一个失败的历史源」绕过（每次都只产生第 2 次尝试）。另外两条：同根已有成功尝试时拒绝（重复执行同一次输入），已有进行中的尝试时拒绝（同一次输入同时跑两份）。`SingleMachineExecutionService.RetryAsync` 把「查链 → 定号 → 建作业」整段放在同一把锁里，避免并发重试拿到同一个号；尝试号与血缘落 SQLite，宿主重启后规则不变。界面用 `CanRetryJob` 预检并给出原因。
- **批次提交分阶段（R2/R3）**：`AgentBatchCommitter` 是唯一提交入口，四阶段——账本准入（`AgentCommitLedger` 记 `Pending / Succeeded / Failed`，不再是「消费过」二值）→ 动作应用 → 保存 → 账本落定。只有「动作全部成功 **且** 保存成功」才算提交完成；部分应用、保存失败都不报成功，并保留恢复手段：保存失败可用同一批次号走「只重存」的续跑路径（`saveOnly`，不再重复应用动作），动作失败则回退该动作的修改。`AgentActionExecutor.Apply` 现在对每个动作留快照，**只有真的改动了才**回退（纯校验失败不回退，避免把调用方持有的节点实例换成反序列化新对象）。界面对齐：面板按返回值显示「已保存 / 保存未完成」；「撤销本批」在批次已落画时真的回退画布与文件；保存失败时关窗、切项目、切标签一律中止且不清空待处理内容。文件副作用靠 `FileSnapshot`（含新建文件的删除语义，`FileSnapshot.RestoreAll` 一份实现供界面与测试共用）。
- **技能执行契约与来源隔离（R4/R5）**：`SkillEndpoint`（地址 / 路径 / 方法 / 鉴权）挂在 `SkillStep` 上，导入时写入，运行与最小测试共用同一份（`ImageGenerationRequest.BaseUrl/EndpointPath/Method/AuthStyle` → `OpenAiCompatibleImageProvider` 按它发请求，含 `x-api-key` 与 `api_key` 查询串两种鉴权，以及 base 与路径都带版本段时去重的 `AvoidDuplicatedPrefix`）。不支持的协议（非 POST/PUT/PATCH）在创建前阻断；异步视频技能标 `IsPlannedOnly` + `PlannedReason`，`SkillRunner` 直接拒绝并说明原因。技能 Id 与文件名带来源命名空间（`SourceId` = 去掉协议的可读前缀 + 来源完整身份的 SHA-256 前 8 位摘要，见第 15 轮 U5），同源重导幂等覆盖，减池只清理「本来源上次写过、本次不再产出」的文件（靠 `OwnedFiles` 更新清单），手工技能与其它来源一律不动；改名前遗留的 `api-image.json` 这类没有来源标识的文件**保守保留**并记进结果说明。写入先全部落临时文件、全部就绪后再逐个「先备份再替换」，失败整批回滚（第 16 轮 U6 边界补上残留清理与恢复失败的如实报告）。
- **大模型修正与原文核对（R6）**：`ApiDocRepair.FromModelJson` 在拿到模型返回的 JSON 后**逐条与原文核对**：原文里找不到的路径或模型名按虚构丢弃；方法 / 能力 / 类型不在支持范围内直接拒绝，不静默补默认；确认不了的（方法缺失、类型判不出）留成 `ApiDocReport.PendingItems`，界面单列「待确认（不会建进技能）」。没有原文时如实说明「无法核对」，不假装核对过。

### 第二轮返工：提交恢复语义、执行路由与来源身份（S1–S6）

- **自动模式的结论必须传出去（S1）**：`AgentPaneHost.AutoStage` 返回 `AgentAutoStageResult(Actions, Failure)`，面板据 `Failure` 显示「已应用并保存」或「自动应用未完成 + 原因」。旧实现直接把 `SaveApplied()` 的返回值丢掉，上层于是无论成败都报成功。
- **重存与原始快照（S2）**：账本新增 `AgentBatchFailure`（Apply / Save）记失败发生在哪一段。`AgentBatchCommitter` 遇到「动作已落画、上次只是保存失败」时，把**普通保存自动改走只重存**（`saveOnly`），用户拒绝一次即时重存后不会被 Failed 状态卡死。`MainForm.ApplyAgentBatch` 用 `appliedThisCall` 判定，**只有真的应用了动作才登记撤销记录**——只重存不再重拍快照，也不把 `AppliedCount` 覆盖成 0（否则撤销会退到错误状态或干脆不回退）。
- **外部副作用的恢复（S3，第 16 轮 U1/V1 修正）**：同一路径可能被同一批动作写过多次（原始 → A → B），`FileSnapshot.RestoreAll` **按路径只认最早快照**（正序回写会停在中间态 A）；资产移出改为**应用管理的可逆移动**（`AssetRecycle` 移入资产目录下的 `_recycle` 并记原路径，撤销时移回），不再交给系统回收站——系统回收站拿不到目标路径也没有随撤销自动回来，界面却会报「已撤销」；`RollbackLastCommit` **只在全部恢复成功时**清记录，失败保留记录并返回未恢复项；`DiscardPendingChanges` 返回失败清单且失败时不清待处理内容；`UndoPendingForPane` 把失败说明交回面板，关窗 / 切项目 / 切标签在撤销失败时中止。
- **导入执行一致性（S4）**：`ImageProviderFactory.CreateFor(skill, execution)` 是统一的执行路由——技能步骤带 `Endpoint`（即导入技能）时走 `OpenAiCompatibleImageProvider` 打它记下来的地址与模型，**不会被「已配置 ComfyUI」抢走**；没有执行配置的技能保持 ComfyUI 优先。导入向导的最小测试走同一条链路，保证「测过的就是会跑的」。池子的执行配置绑到**产出它的那条接口**（`PoolSpec.Op` → `ResolvePoolOp`）：先看哪条接口明确挂着这个模型，再看同类能力是否只有一条，有歧义就不绑（回落设置里的地址并提示），不猜——猜错会让池子打到别人的路径上。
- **来源身份与所有权（S5）**：`SourceIdOf` 改为**完整主机名（含顶级域）+ 端口**做规范化（`api.example.com` → `api-example-com`），旧实现去掉顶级域会让 `.com` 与 `.net` 撞成同一个命名空间、两个来源互相覆盖。`ApiSkillFactory.Write` 在替换前读取目标文件里的 `sourceId` 校验归属：不是本来源写出的（手工技能、别的来源）**整体拒绝本次写入**，绝不覆盖；清理只删「同一来源且本次不再产出」的文件；无法证明归属的疑似旧版本文件（`api-*.json` 且没有 `sourceId`）改为**保守保留**并在说明里列出，交给用户决定。
- **原文语义依据（S6）**：模型名与档位改用**完整词**命中（`flux-1` 不再因为出现在 `flux-1-dev` 里算命中）；`ApiDocAnalyzer.DeclaredMethodFor` 取出原文对某条路径声明的方法，与模型给的不一致时**列为待确认并拒绝**（原文 POST 而模型给 PUT 属于冲突）；原文没写的档位直接去掉；`ResolveGroundedBaseUrl` 要求地址主机等于来源主机或出现在原文里，否则改用来源推导地址并如实说明——地址会写进技能的执行配置，不能凭模型一句话；「已逐条核对」的说明按实际核对范围表述。

### 第三轮返工：资产引用、画布身份与归属依据（V1–V6）

- **资产移出必须先解析引用（V1）**：无引用扫描（`StorageMaintenance.FindUnreferenced`）与节点删除给出的都是**可移植引用**（`asset://文件名`），不是文件路径。`AssetRecycle.MoveReference(reference)` 统一经 `AssetStore.Resolve` 解析成本机路径后再移动；`MainForm` 把「询问用户」与「真正移出」拆成两段（`RecycleOrphanedAssets` 不弹窗），移不动的文件如实列出且**不计入撤销记录**——没有可恢复的东西就不该让撤销以为有。
- **离开画布的统一守卫与画布身份（V2/V3）**：`AgentCommitLedger.HasPendingRecovery(out batchId)` 能**脱离任何清单**扫出待恢复批次（提交成功后 `pendingChanges` 已清空，只看清单会漏）。`ConfirmPendingBeforeLeaving()` 是唯一的离开守卫，接在关闭标签、新建、切标签、切项目、关窗、从画布库打开与导入画布上。画布身份 `CurrentCanvasKey()` 取**标签的稳定 Id**（不是文件路径：未命名画布没有路径、另存为会换路径）；`CanvasKey` 同时记进账本，`TryBegin`/`TryResume` 都校验——**跨画布提交与跨画布重存一律拒绝**，避免「A 保存失败 → 打开 B → 在 B 上把 A 记成成功」。
- **池子归属按模型 + 档位判定（V4）**：`ResolvePoolOp(ops, spec, size)` 逐条「模型 × 档位」组合判定归属：先取声明该模型的接口，不止一条时再用这条池子的档位消歧，唯一才绑；仍判不出即**不可执行**（`IsPlannedOnly`），绝不回退到默认执行方或 ComfyUI。
- **完整来源身份（V5）**：`OwnedBy` 要求文件里的 `SourceId` 与 `SourceIdentity` **都有值且逐字相等**才算本来源的文件；覆盖前校验，清理时对每个待删文件**再读一次身份**核对，对不上就保守保留并写进说明。只比 `SourceId` 会让「命名空间相同、身份指向别的来源」的旧池子被误删。
- **模型表档位也要有同处依据（V6）**：`ApiDocRepair` 顶层 `models` 的档位改用 `SizeBelongsToModel`——必须与模型名**同一行**，或同一段里带尺寸标签的那一行；全文命中不再算数（否则 B 接口段落里的 4K 会被挂到只支持 1K 的模型上，工厂又优先用模型表，于是重新造出打不通的池子）。同时**已核实的接口限制优先**：模型表给出的档位会被同模型接口实际声明的档位收敛（无原文时不套用）。
- **替换失败要如实报告与清理（U6 边界）**：`ApiSkillFactory.Write` 在替换失败时检查备份放回的结果（失败就说明该文件可能仍是新版本），并统一清理剩余临时文件——`RollbackReplaced` 只声明「成功回滚」还是「回滚没有完全成功」，不把未验证的情况写成任意失败都能整批还原。

### 第四轮返工：标签生命周期与恢复收敛（R16）

- **标签快照必须与活动画布分离（R16-1）**：`BuildCanvasState()` 的 `Canvas` 就是 `canvas.State` 本身，直接当标签快照会让「快照」与「当前画布」是同一个对象：切标签时 `LoadState` **原地**改写这份对象（被切走的标签的快照被写成新标签的内容），切回来时 `LoadState` 又把自身当来源加载（先 `Clear` 再 `AddRange`，从刚清空的集合里取）→ 节点清零。现在 `BuildCanvasSnapshot()`/`CloneCanvasSnapshot()` 一律给独立副本，`WorkflowCanvasControl.LoadState` 也直接拒绝自身别名；`CloseCanvasTab` 只在关闭**当前**标签时才装载快照（关别的标签重装当前快照会丢掉未保存编辑）。
- **画布身份要含「第几份文档」（R16-2）**：标签 Id 只说明「哪一格标签」，说明不了「现在装的是哪份文档」。`CanvasTabState.DocumentGeneration` 在同标签被从画布库打开或导入替换时递增，`CurrentCanvasKey()` = 标签 Id + 世代；`BeginNewDocumentOnCurrentTab()` 递增世代并作废旧的提交/恢复上下文（旧快照属于上一份文档，而不同文档的修订号相同是常态，撤销会把上一份文档套到新文档上，随后保存还会顺着新文档的路径写下去）。
- **待恢复状态必须可退出（R16-3）**：撤销中途失败进入 `NeedsRecovery` 后，画布已回退（修订号回到提交前），原来「修订号必须等于提交时 +1」的撤销预检会把唯一的恢复入口也关掉，而所有离开入口又被守卫拦着 → 用户被锁死。现在 `UndoAvailability` 在 `NeedsRecovery` 时放行（专用重试入口），`AgentCommitRecord.CanvasRestored` 让画布回退只做一次（重试只补剩余副作用，不二次回退画布）；**纯跨画布拒绝不再标 `NeedsRecovery`**——那只是「现在不能撤销」，记录保留、切回原画布即可撤销。
- **恢复要能收敛（R16-4）**：`AssetRecycle.RestoreAll` 每次重放整份记录，已成功移回的项因回收目录里已无文件而被报成「找不到该文件」，记录永远清不掉。`AssetMove.Restored` 在成功移回的那一刻置位，重试时跳过已完成的项，只处理真正未完成的——恢复因此可收敛到 `RolledBack`。

### 第五轮返工：恢复期间的入口与批次隔离（R17）

- **离开当前画布的入口一个都不能漏（R17-1）**：`DuplicateCurrentCanvas` 会**落盘一份新画布并切到新标签**，本质是离开当前画布，因此第一行接 `ConfirmPendingBeforeLeaving()`（同时覆盖"落盘"与"切换"两段）。守卫与结果提示都支持 `quiet` 参数：自动化可以用同一判定无弹窗驱动真实入口（判定与副作用完全一致），而真实点击路径照旧弹窗、照旧询问。
- **待恢复期间不许开新批（R17-2）**：`PreviewAgentBatch` 换批号、`ApplyAgentBatch` 成功时覆盖 `lastAgentCommit`——只要旧批还在 `NeedsRecovery`，新批一旦落画就会把旧批的恢复记录变成不可达的死记录，而账本仍停在待恢复、所有离开入口又被拦住。三层一起挡：`AgentCommitLedger.TryBegin` 在任何提交（含整批回滚后的重新提交）前检查 `HasPendingRecovery`；预览阶段不换批号、不建虚影；统一提交入口 `ApplyAgentBatch` 与面板共用的 `SavePendingForPane` 返回同一句可操作说明。恢复完成后旧批变 `RolledBack`，新批自然放行。

## 项目级资源库（目标 6）

实体原先内嵌在每份画布文件里：同一角色在两个画布上就是两份互不相干的副本。目标 6 把它提到**项目**这一层，身份仍是稳定 ID（`WorkflowEntity.Id` / `VariantId` / `VersionId`），内容只有一份。

- **存储与权威（G6-1）**：`ProjectLibrary`（`project/entities.json`）是权威来源，原子写 + 每次覆盖前备份到 `project/backups`，失败清理临时文件。实体新增 `ManagedByProject` 标记（旧文件缺省 false，兼容旧数据）。画布文件里的托管实体只是**快照**：`CanvasSaveService.Save` 写盘前调 `ProjectEntityScope.RefreshSnapshots` 从库刷新，`MainForm.ApplyCanvasState` 打开后调 `MergeInto` 以库为准刷新、并补入「被引用但本画布没有」的库实体；库里已不存在的托管实体如实列进缺失，交给既有的引用/锁定版本缺失提示。两个接入点各自覆盖全部读写路径。
- **跨画布索引（G6-1/G6-3）**：`ProjectEntityIndex` 复用 `CanvasReferenceScanner`（当前画布 + 画布库 + 草稿）得出「资源 → 引用来源」，不另造一套遍历口径；`ProjectEntityUsage.HasForeignReference` 直接回答「除本画布外还有谁在用」。
- **迁移（G6-2）**：`ProjectEntityMigration.Preview/Apply`。预览列出逐项处置与**同名冲突**（同名不同 ID 各自保留，绝不按名字合并）；Apply 先备份库文件、再对每份受影响的画布 `CanvasBackup` 备份后写入，任一步失败**整批回滚**（库与已写入的画布都恢复原状）。同一实体出现在多份画布时**每份都要标记**（否则迁移完另一份还会被再迁一次，看起来不幂等）；重复执行直接跳过已共享项。
- **删除保护与回收（G6-3）**：`ProjectEntityDeletion` 在其它画布/草稿仍引用时**硬阻断**；只本画布引用时允许删除——先备份库、把实体按既有 `CanvasRecycleBin` 存入回收站、再从库移除，`TryRestoreToLibrary` 把回收站记录还原**回项目库**（不是还原成某份画布的本地资源），原引用按稳定 ID 重新生效。主界面按「项目级 / 本画布」分流删除路径，实体列表新增「来源」列。
- **未覆盖**：把共享资源退回成某份画布的本地资源、项目库专用浏览/编辑界面、大项目索引性能，均未做（详见归档 `round-19-goal6` 的边界清单）。

### 复核返工 G6-R1～R3（权威写入、迁移事务、缺失阻断）

- **权威写入路径（G6-R1）**：项目库是共享资源内容的权威，所以**编辑动作必须写回库**——只改画布对象会在保存时被"从库刷新快照"覆盖掉。`ProjectEntityScope.TryPublish` 负责把托管实体写回库（未托管的本地实体直接放行），`RestoreInto` 负责整体还原；`MainForm.PublishSharedEntity` 统一接入编辑与变体新建·编辑·删除四个入口：编辑前留原样、编辑后发布、**发布失败即整体撤回并如实报错**。保存链的"写盘前从库刷新快照"保持不变，此时库里已含编辑。
- **迁移事务（G6-R2）**：`ProjectEntityMigration.Apply` 的回滚要还原三样：① 项目库——原本存在则从备份恢复，**原本不存在则删掉本次新建的库文件**；② 已写入的画布文件——按各自备份逐份恢复；③ 内存里的托管标记——按迁移前快照逐条还原。备份失败分支与写入失败分支走同一个 `Rollback`。
- **权威缺失即阻断（G6-R3）**：库文件漏拷或被删时，画布里的托管快照只是"恢复参考"，不能当有效资源。实体带运行时标记 `ProjectMissingReason`（`JsonIgnore`，不落盘）：打开时由 `MergeInto` 打标/清标，`ResolveReference` 见标即返回未解析，`CanvasReferenceVersions.IsBlocked`（含 `IsAuthoritativeMissing`）驱动 `IsNodeBlocked`、`UsableContents` 与 `DescribeBlock` 分别说明"锁定版本缺失"与"项目级资源缺失"。库放回后缺失清零、解析即刻恢复。

### 复核返工 G6-S1～S3（请求前阻断、提交即落库、删除顺序）

- **出图前阻断（S1）**：只要有引用不可用（锁定版本缺失，或项目级资源已不在项目库），就在**请求构造与节点状态置位之前**拒绝——`GenerateSelectedImageAsync` 先看 `CanvasReferenceVersions.DescribeBlock`，非空即返回并写诊断，绝不用旧快照消耗额度。方法带 `quiet` 供自动化驱动；自动化用注入的计数桩（`ImageProviderFactory.Override`，`internal`，生产不设置）证明"被阻断时提供方调用为 0"。
- **提交即落库（S2）**：版本提交是用户已确认的动作，`CommitVersion` 在 `variant.Commit` 之后**立即** `TryPublish`：写库失败就撤回这次提交并如实报错。编辑器用 `out bool persisted` 把"对话框内已有确认落库的提交"告知外层，外层取消只丢弃未持久化的编辑（`KeepPersistedCommits` 用库内容对齐内存），不会撤销已确认的提交。
- **删除顺序（S3）**：删除变体（以及同类"先解除引用再落库"的操作）必须先**发布**、后动本画布：先把"删除之后"的实体写进项目库，成功后才解除引用/写回收站/移除变体；本地删除失败时把库回滚。这样失败时实体、引用、回收记录三者一致，不会出现"变体回来了、引用没了"。
- **回滚如实反馈（R2 补证）**：迁移回滚把每一步的失败都收集起来（库原本存在却无备份、恢复库失败、删新库失败、画布恢复失败），汇总成"已回滚"或"回滚未完全成功，请人工检查"两种结论，并在 `ProjectMigrationOutcome.RollbackComplete` 里给出机器可判定的结果——恢复失败绝不能报告成成功。

### 复核返工 G6-T1～T3（执行前核验、发布语义、补偿可恢复）

- **执行前重新核验（T1）**：打开时算出的缺失标记会过期——库可能在打开之后被删除或漏拷。`ProjectEntityScope.RefreshAuthority` 只重新核验"托管资源是否还在库里"、**只更新标记不碰内容**（不会覆盖未保存编辑），出图在阻断检查之前先调它，因此"打开后才缺库"也拦得住；库放回后核验即放行。
- **发布语义要区分"真落盘"（T2）**：`ProjectEntityScope.Publish` 返回 `PublishOutcome(Succeeded, Persisted, Error)`——托管实体写库成功才算已持久化；本地（未迁移）实体是"成功但未持久化"。提交入口据此分两种文案：托管说"已提交并保存"，本地说"已提交（本画布本地版本），保存画布后才落盘"且**不**计入已持久化（外层取消可回滚，语义一致）。
- **补偿失败要可恢复（T3）**：本地删除失败后的库补偿也可能失败，此时记下 `ProjectCompensation`（实体、删除前内容、画布身份、原因）作为待恢复记录，如实提示（不再说"均未改动"），并用 `EnsureNoProjectCompensation` 在所有写库入口拒绝继续写库；面板「恢复项目库」调 `RetryProjectCompensation`：成功即清记录并用库内容对齐内存，再失败则保留记录并更新原因。

## AI 全流程闭环

目标 5 把「提案 → 审批 → 出图 → 成品」串成可回归的一条链路。**一句创意**先变成一批 `AgentAction`（企划 + 章节 + 角色/场景 + 分镜），其中视觉设定只以 `EntityTargets` 形式挂到分镜的 `References[]` 上，**不建常驻画布节点**；分镜经工作树锚点归属到章节。这批动作先经 `CanvasPreviewBuilder` 在副本上试算（预览零副作用），再经 `AgentActionExecutor.Apply` 落在真实画布上，最后统一走 `CanvasSaveService.Save` 落盘。

提交的一次性由 `AgentCommitLedger` 保证：每批动作在产生时拿到一个 `BatchId`，提交必须消费它，**同一批只允许成功一次**——双击提交、窗口切换重复确认、审批与自动模式同时命中同一批，第二次都会被拒绝且画布不变。用批次号而不是内容指纹做键，所以内容相同的新提案（新批次号）仍可提交。提案 → 审批（`PendingChanges` 虚影）→ 提交/撤销是唯一路径。

出图走 `SingleMachineExecutionService`，任务状态持久化在 SQLite（`jobs.db`）。**失败重试**由 `JobRetryPolicy` 与 `RetryAsync` 收口：只有 `Failed`/`Cancelled` 两个终态可重试，同一次输入的反复尝试默认上限 3 次（超限只能手工重新发起），新 Job 沿用首次的工具/能力/通道/输入参数并记录 `Attempt`、`RetryOfJobId`、`RootJobId`，`GetAttemptChain` 给出完整尝试链；宿主重启时未完成的任务标记为失败并提示可重试，而不是就此终局。取消按声明 `job.cancel` 鉴权，排队与运行中都能取消并收敛到 `Cancelled`。

跨端一致：`host/job.update` 除状态与进度外还推送 `attempt`/`retryOfJobId`/`rootJobId`，Web 端 `src/JobView.ts` 用与桌面 `JobRetryPolicy` 相同的口径渲染「第 N 次尝试 / 重试自哪个任务」，两端对「是否可重试」的判定一致。密钥侧沿用 `SecretProtector`（DPAPI CurrentUser 作用域 + 只写不回读的脱敏显示），权限侧沿用 `AccessPolicy` 的会话声明校验（`skill.invoke` / `job.cancel` 等），越权与跨用户访问一律拒绝。

## 引用交互与资源保护

目标 4 把「引用」从画布内的可视关系升级为可扫描、可保护、可恢复的数据关系。`CanvasReferenceScanner` 只读扫描三类来源——当前画布、画布库里的其它画布、草稿画布（`last-canvas.json`）——产出 `ReferenceHit`（范围、来源、节点、实体/变体/版本、是否锁定版本缺失）；草稿不存在不算「打不开」，存在却读不了才如实记入 `Skipped`。

`CanvasDeletionGuard` 是删除前的闸门：其它画布/草稿仍在引用时**硬阻断**（本画布不能替别的文件改写引用），只有本画布引用时可以删除，但必须显式确认并逐条解除引用；`RemoveReferencesIn` 只改传入的画布对象，**从不写盘**，磁盘变化仍只发生在统一保存入口。删除不是抹掉内容：`CanvasRecycleBin` 把实体/变体的完整快照（含 ID）记到 `recycle-bin.json`，还原时 ID 不变，因此原有引用按稳定 ID 自动重新生效；实体已存在时只补齐缺失变体，不覆盖现有内容。

删除的顺序是**先落盘、后删除**：`TryStashEntity` / `TryStashVariant` 只有快照真正写进回收站文件才返回 true，`CanvasDeletionGuard.TryDeleteEntity` / `TryDeleteVariant` 在此之后才解除引用并移除对象；写盘失败（权限、IO、路径被占）一律返回 false 并带回原因，**画布与磁盘都保持原样**，绝不出现「快照没落盘但原数据已删」的不可恢复状态。还原同理：先把回收站记录落盘成功，才改动画布。回收站写盘绝不吞异常，调用方（`ReferenceDialog` 与设定库删除入口）必须依据返回值取消操作并把原因显示出来。

这条边界对**所有入口**都成立，包括 Agent：`AgentActionExecutor` 的 `delete_entity` 走的是同一套「跨画布/草稿扫描 → 其它来源引用硬阻断 → 回收站快照成功后删除」，并且需要动作显式声明 `removeReferences` 才允许连带解除本画布引用（未声明就拒绝，预检也会提前提示）。删除实体的动作在 `FileWriteMode.Skip`（预览试算）下只在传入的副本上移除实体、不写回收站——预览的调用方一律传副本，所以预览既如实反映删除又不产生任何磁盘副作用。扫描在未打开项目时退化为「只扫当前画布」，不抛异常；回收站写不进去时被判定为写入失败并取消删除。

版本语义在 `CanvasReferenceVersions` 收口：`TrySetVersion` 只在版本真实属于该变体时写入（锁定节点拒绝改写），锁定不存在的版本一律拒绝。**锁定版本缺失不再静默降级**：`ResolveReference` 仍会退回变体当前内容以便画面能显示，但会置 `ReferenceContent.VersionMissing`，`VersionLabel` 显示「版本缺失」、提示词写入 `[阻断]` 行、出图参考图经 `UsableContents` 过滤掉这一条——宁可这一条不给图，也不静默用错版本。桌面入口是工具栏与设定库的「引用与回收站」（`ReferenceDialog`），Web 端由 `src/ReferenceView.ts` 用同一口径显示版本阻断。

资产清理沿用既有复扫：`StorageMaintenance` 的引用统计覆盖画布库 + 草稿 + 当前画布，只有最后一个引用消失才把图片移入回收站；删除节点后还会再看生成历史与其它画布。

## 多视图与选择联动

TS 画布当前有 `chapter` / `overview` 两种显示模式、章节筛选、引用折叠和浏览器选择状态；`NodeProjection` 把节点投影为 `recordType`、位置、章节、父节点和引用缩略图，并将未出现在画布的工作树章节投影为章节记录。当前布局由前端 `computeLayout` 统一计算，场景初始化或重置会清空选择和折叠状态。

章节身份在 Web 端由 `src/ChapterView.ts` 统一裁决（C-4）：`chapterIdOf` 只认投影写入的 `chapterId`（工作树章节条目 ID），章节名文本只用于显示，所以同名不同 ID 的章节是两条、不会被合并；`chapterEntries` 按 `order` 排序，`filterByChapter` 按 ID 过滤并保留企划层，`sortWithinChapters` 按（章节 ID、显式顺序、坐标、标题）给出稳定渲染顺序。视图偏好仍存 `localStorage`，但键名从章节名改为 `chapterFilterId`：旧值（章节名）不会被当作身份，读到即回到「全部章节」。节点卡片与检查器都显示章节名 + 稳定 ID 前缀，并提供「定位到该章节」。

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
