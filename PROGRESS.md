# DreamForge 开发进度基准

## 当前基线

版本日期：2026-09-29；工程基准版本：`0.1.0-dev`；历史记录始于 2026-09-25。

当前结论以本节、目标 0–8 和 [架构](02_Architecture.md) 为准。下方逐轮记录保留当时日期、测试结果与判断，仅用于追溯；旧记录中的“项目级设定库”“只读 Web”“自动同步”“AutoStage 重复执行”等不能覆盖当前结论。

- 主线为企划 → 章节 → 分镜 → 成品。角色、道具、场景不建常驻画布节点，分镜通过 `References`/`entityTargets` 引用，按需临时展开；旧类别和入口仍在，尚未完成旧数据迁移。
- `ParentNodeId` 是画布布局关系，`WorkTreeItemId` 是叙事锚点，`References` 是视觉引用；字段存在不等于同步已实现。
- 工作树和资源库都保留；当前 `Entities` 与 `WorkTree` 随画布保存。项目级资源库迁移必须先完成稳定 ID、引用扫描、删除保护、回收站和版本锁定验证。
- 桌面为 WinForms 自绘主端；Web 创作面仅有镜像及引用版本回写，另有作业服务。TS/React 使用 div 卡片，tldraw 依赖已清理，Canvas 与 Mcp 不在解决方案中。
- 宿主协议 v1 有 17 种消息；桌面 Agent 使用独立的 13 种 action 协议。基础文本/图像链路已有，完整 AI 企划到成品尚未验收；视频、Web 独立创作和多端协同尚未实现。

## 更新规则

1. 每轮实现、修正或验证完成后，必须更新本文件。
2. 每条记录包含日期、完成内容、验证结果、遗留问题和下一步。
3. 未通过的构建或测试不得标记为完成；必须记录真实错误和处理计划。
4. 协议、权限、Job、撤销语义发生变化时，同时更新 `protocol/PROTOCOL.md`、测试夹具和相关实现。
5. 协作撤销仍然禁止使用快照覆盖他人 CRDT 改动，除非另有经过验证的 CRDT 语义设计。

## 当前进度入口

当前唯一有效的阶段与状态表是下方《按依赖排序的小目标》。早期阶段表已移除，避免把历史路线和当前目标混成两套进度口径；逐轮记录只用于追溯，不代表当前待办。

## 按依赖排序的小目标

每个目标都必须先满足前置，再以产出和验收判定完成。状态只描述当前代码事实；本轮验证结果已记录在目标 0 下方。

| 编号 | 前置 | 产出 | 验收标准 | 当前状态 |
| --- | --- | --- | --- | --- |
| **0 清理基线** | 盘点源码、文档、协议和历史验证记录 | 删除明显废弃草稿；统一当前基线；记录修复、遗留问题和本轮验证 | `git diff --check` 通过；文档不把历史结论写成现状；源码改动范围可追溯 | **完成（2026-09-29；构建、回归与差异检查通过）** |
| **1 稳定 ID/模型** | 目标 0 完成 | 节点、工作树、实体、变体、版本和资产的稳定 ID 约束；旧数据映射与歧义报告 | 重载项目后 ID 不变；重复导入不重复建资源；缺失/歧义引用被拒绝；旧画布可报告迁移结果 | **实现完成（含第 4 轮 R5–R11 返工），待再次统一复核（2026-09-29）**：只读校验器、保存/重载/复制 ID 生命周期、旧数据迁移与备份恢复、重复导入去重均已接入真实打开/保存/导入入口，见下方《目标 A 交付基准》 |
| **2 同步引擎** | 目标 1 的 ID/模型 | 工作树到章节/节点的双向同步、变更计划、预览、撤销和冲突报告 | 修改工作树条目只更新受影响节点；不复制资源内容；手动覆盖有明确提示；全流程有自动化用例 | **已通过复核（2026-09-29，含第 10、12 轮返工）**：`CanvasWorkTreeSync`（计划/应用/撤销，按 ID 与上下文判定）、`CanvasChapters`（章节身份、显式顺序、诊断）、`CanvasChapterOperations`（建立/改名/排序/移动/拆分/合并/删除，均带冲突报告与引用重接）；桌面入口为工具栏「章节同步」（计划/应用/撤销）与「章节结构」（`CanvasChapterStructureSession` + `ChapterStructureDialog`，覆盖全部结构操作、预览/副本执行、阻断不替换画布、撤销与统一保存，保存回调按统一保存入口的真实结果返回、失败不报成功），并有入口级回归与可重复 UI 冒烟 |
| **3 章节泳道局部布局** | 目标 2 的节点关系和目标 0 的清理 | 企划在泳道外；章节独立泳道；分镜横排；成品挂在分镜下方；按章节局部重排 | 新增/重排一个章节不移动其他章节；用户手动坐标保留；局部操作可预览和撤销；引用展开不生成常驻节点 | **已通过复核（批次 C，2026-09-29）**：`CanvasSwimlaneLayout`（企划区在泳道外、每章一条独立泳道、分镜按工作树显式顺序横排、成品按 `ParentNodeId` 挂分镜下）是唯一权威布局路径，「整理画布」与 Agent 落位都已改走它；`CanvasLayoutSession`（预览/应用/撤销 + 手动坐标保护与「自动布局覆盖」确认 + 阻断不写入）；`ChapterLayoutDialog` 与工具栏「章节布局」「展开引用」入口；引用临时展开与反向定位见 `CanvasReferenceExpansion` / `CanvasReferences`（不落盘、不生成常驻节点）；Web 端章节筛选/排序改按稳定 `chapterId`（`src/ChapterView.ts`） |
| **4 引用交互/资源保护** | 目标 1 的引用模型和目标 3 的布局 | `References` 展开/收起、反向定位、版本锁定、删除保护、回收站和恢复 | 可从分镜定位资源及从资源列出引用；锁定版本缺失时阻止使用；仍有引用的实体/变体不能删除；资产清除前复扫 | **已通过复核（2026-09-29，含两轮返工）**：`CanvasReferenceScanner`（扫描当前画布 + 画布库 + 草稿，按范围列出命中，打不开的来源如实记录）、`CanvasDeletionGuard`（其它画布/草稿仍引用时硬阻断删除；本画布引用可显式解除，且只改内存不改写任何画布文件；删除按「先写回收站快照、成功后才解除引用并移除」执行，写盘失败整体取消）、`CanvasRecycleBin`（实体/变体移入回收站并可按稳定 ID 还原，还原后引用重新生效）、`CanvasReferenceVersions`（跟随最新/锁定版本写入策略 + 锁定版本缺失判定为阻断）、`ReferenceDialog`（工具栏与设定库的「引用与回收站」入口）；Agent 的 `delete_entity` 也走同一条链路（需显式 `removeReferences` 才连带解除本画布引用，预览试算只在副本上生效且不写回收站）；锁定版本缺失不再静默降级为当前内容；Web 端 `src/ReferenceView.ts` 同口径显示版本阻断 |
| **5 AI 全流程** | 目标 2–4 | 企划 → 工作树 → 视觉设定 → 分镜 → 引用出图 → 成品与历史的单机流程 | 一句创意可生成完整链路；角色/道具/场景只作为引用；审批和自动模式各自只提交一次；端到端用例通过 | **已实现（目标 5，待复核）**：①一句创意的完整批次（企划 + 章节 + 角色/场景 + 三个挂引用的分镜）有端到端回归，断言视觉设定只作为引用（角色/场景不占常驻画布节点）、分镜归属到章节、提示词包含引用、保存重开后一致；②`AgentCommitLedger` + `PendingChanges.BatchId` 保证同一批动作只提交一次（审批与自动模式共用入口 `ApplyAgentBatch`，双击/重复确认被拒且画布不变，内容相同的新提案换新批次号仍可提交）；③`JobRetryPolicy` + `SingleMachineExecutionService.RetryAsync`（只有失败/已取消可重试、默认上限 3 次尝试、新 Job 记录 `Attempt`/`RetryOfJobId`/`RootJobId` 并持久化到 SQLite，`GetAttemptChain` 可追溯整条尝试链，宿主重启的未完成任务改为可重试）；④任务详情新增「重试任务」入口与尝试/血缘显示；⑤`host/job.update` 与 Web 端 `src/JobView.ts` 同口径显示尝试链（跨端一致），两端状态语义与可重试判定一致 |
| **附 接口智能导入（用户追加）** | 与目标 5 同步 | ComfyUI 与「画图 / 画视频」接口的接入、智能导入窗口，以及「说明网页 → 自动建技能 → 输密钥 → 最小测试」的导入向导 | 从文档片段/curl/JSON/env 识别类型与字段；给一个接口说明网页就能建出 api 生图 / api 生视频技能与池子子技能；识别不到不猜；导入前预览改动；密钥加密落盘；未涉及配置项不被清空；最小测试真的调接口并返图、测不了如实说明；真实聚合站文档（anysyaiapi）跑通且池子模型与档位和文档定价表一致 | **已实现（待复核，含真实文档回归修复）**：①`ProviderImporter`（识别 ComfyUI / 画图 / 画视频 / 文本四类，含地址归一化、密钥/模型/checkpoint 抽取、判据与风险提示）+ `ProviderImportDialog`（粘贴 → 智能识别 → 核对 → 测试连接 → 导入并保存）+ 设置里新增画视频字段（`VideoEndpoint`/`VideoModel`/参考帧上限/默认时长）与「智能导入」入口，并修掉设置对话框保存时重建对象会静默重置未列字段的缺陷；②`ApiDocAnalyzer`（OpenAPI/HTML/纯文本解析）+ `ApiSkillFactory`（一条父技能 + 按「模型 × 尺寸」派生的池子子技能，如「生图池1 1K」「生图池2 2K」，只覆盖自己写出的文件）+ `ApiImportWizardDialog`（`ApiDocFetcher` 抓网页 → 建技能 → 输密钥加密落盘 → 问是否最小测试 → 选「是」真调一次出图并返图、选「否」保存完成且不谎报测试结果）+ 设置里「接口向导」入口；③`SkillStep.Model` 与 `ImageGenerationRequest.Model` 让池子逐步骤指定模型，`SkillRunner` 同时接受出图与出视频能力（`IVideoProvider`，当前只有 `UnconfiguredVideoProvider`，未接入的实现如实报出、不伪造视频）；④**用真实聚合站文档（video.anyaiapi.top/about）回归后修掉四处**：模型名保留「(池6)」这类后缀（截断就是另一个模型）、`/v1/videos/{id}` 这类查询 / 下载详情端点不再被当成生成接口（它们还会抢走旁边模型表的归属）、池子改从文档「可用模型」表按每个模型自己的类型与档位建（按行距离猜会把图像模型算到出视频接口头上）、前端渲染的文档站只抓到页面外壳时直接引导改用粘贴而不是报「文档里没写接口」；顺带把池子按「模型 × 档位」展开后限流（每类最多 12 条，超出如实提示），避免几十个组合一次刷满技能目录；⑤**规则解析不出接口时可请大模型分析修正**（`ApiDocRepair` + `IAiJsonCompleter`：约束模型只输出固定结构的 JSON，逐条校验能力名/方法/路径，非法条目与占位符详情端点丢掉并计数，报告明确标注来源是「大模型整理」并要求核对；未接入大模型时按钮显示「未接入」而不是给一个点了必然失败的入口）；⑥**窗口右上角显示余额与上线模型**（`ApiAccountProbe` 查 `GET {base}/user/balance` 与 `GET {base}/models`，保存密钥后自动查、每次最小测试后再查一次并算出本次消耗；文档写了但接口没上线的模型会列出来并给「按接口实际模型重建技能」一键收敛）；⑦另外修掉两处真实缺陷：基础地址必须保留版本前缀（文档 `Base URL https://host/v1`，只取域名会打到 `https://host/images/generations`，实测 404），以及保存生成图片时按文件头决定扩展名（接口返回 JPEG 时不再存成 .png）；⑧**第 15 轮独立复核的六项返工（R1–R6）全部完成**：R1 重试上限改为按整条尝试链判定并在同一把锁里原子分配尝试号（反复重试历史源、并发重试、同根已成功、重启后都绕不过）；R2 批次提交拆成「账本准入 → 动作应用 → 保存 → 账本落定」四阶段，单动作失败即回退该动作的修改、只有全部成功且保存成功才算完成；R3 保存失败不再误报（面板显示失败原因）、「撤销本批」真的回退画布与文件、保存失败时关窗与切标签一律中止且不清空待处理内容；R4 技能自带执行配置（地址/路径/方法/鉴权/模型，运行与最小测试共用），不支持的协议在创建前阻断、异步视频技能标为**规划态**并在运行时明确拒绝；R5 技能按来源命名空间隔离（`api-{来源}-{类型}-pool-N-尺寸.json`），A/B 同尺寸不覆盖、同源重导幂等、清理只动自己名下的文件、改名前遗留文件被认领；R6 大模型修正结果逐条与原文核对，虚构路径/模型、非法方法与类型一律拒绝（不静默补默认），确认不了的留成待确认项且不建技能；⑨**第二轮返工 S1–S6 全部完成**：S1 自动模式把保存结果（含失败原因）经面板回调传给界面，不再无条件报「已应用并保存」；S2 账本区分「动作没落画」与「落了画没存下」，普通保存会**自动续走只重存**且只重存不重拍快照、不覆盖撤销记录；S3 同一路径只认最早快照（原始→A→B 不再停在 A）、被移入系统回收站的资产登记进撤销记录、恢复失败保留记录并阻断关窗/切项目/切标签；S4 池子绑定**产出它的那条接口**、执行路由统一（导入技能不被 ComfyUI 抢走，运行与最小测试同一入口）；S5 来源身份改为完整主机名（含顶级域与端口，不再撞名）、替换前校验文件里的 `sourceId` 归属（不是自己的文件整体拒绝）、无法证明归属的旧文件改为保守保留；S6 大模型修正与原文逐条核对（完整词匹配模型名、方法冲突拦下、原文没写的档位去掉、地址无依据则不写进执行配置）；⑩**第三轮复核 U1–U6 与 S6 补证全部完成**：U1 资产移出改为应用管理的可逆移动（`AssetRecycle`），资产恢复纳入撤销完成条件，不还原就算失败并保留记录；U2 账本新增「待恢复 / 已回滚」状态，部分回滚后禁止按原保存失败只重存；U3 关闭标签 / 新建 / 激活 / 切项目统一走同一个守卫，撤销记录绑定画布身份（跨画布撤销直接拒绝）；U4 池子按「接口身份 + 模型 + 档位」建，无归属的池子标记不可执行、导入技能一律走 API 链路不回退 ComfyUI；U5 来源身份用完整规范化来源 + 摘要式命名空间（分隔符碰撞不再可能），覆盖前逐字核对归属；U6 技能文件写入改为「先备份再替换」，任一失败整批回滚不留混合批次；S6 补证把模型与档位限定在接口所在段落（跨段落列待确认、不写进该接口），方法核对扫描路径的全部出现处 |
| **6 项目级资源库** | 目标 1、2、4、5 | 从画布级 `Entities` 迁移到项目级资源库；旧画布映射、备份回滚、跨章节共享 | 多画布共享同一稳定实体；引用扫描覆盖画布、草稿、历史、任务；删除保护/回收站/版本锁定通过迁移回归 | **待实现** |
| **7 Web 创作** | 目标 2–6 | Web 编辑节点、选择回传、资产端点、技能/任务入口和构建接入 | 浏览器能完成主要创作动作；桌面与 Web 的权限重新鉴权；镜像与回写之外的写入都有协议和错误反馈 | **当前只有镜像与引用版本回写** |
| **8 多端协同** | 目标 7 | 会话身份、权限、在线状态、双向实时同步、冲突和撤销语义 | Web 与 Desktop 同时编辑不互相覆盖；冲突可解释；协作撤销不覆盖他人 CRDT 改动；断线可恢复 | **未开始** |



## 本轮最终验证（2026-09-29）

- `dotnet build DreamForge.slnx`：通过，6 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：通过，22 项测试全部通过（19 项基线 + 目标 5 的任务重试 3 项）。
- `dotnet run --project DreamForge.Agent.Tests/DreamForge.Agent.Tests.csproj --no-build`：通过，152 项测试全部通过（目标 5 新增 3 项、接口智能导入 3 项、接口导入向导 5 项、第 15 轮返工 R1–R6 / S1–S6 / U1–U6 新增 10 项用例，第 16 轮 V1–V6 各 1 项并扩展 U6 用例，第 17 轮 R16-1/R16-4 各 1 项，第 18 轮 R17-2 提交入口 1 项，<strong>目标 6 新增 6 项、复核返工 G6-R1～R3 各 1 项、G6-S2/S3 各 1 项、G6-T1/T2 各 1 项</strong>；含 U4 歧义绑定、V4 同模型两接口按档位绑定、控件自别名与项目级资源库的固定夹具）。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：通过，23 项测试全部通过（含返工 R1 的重试上限回归）。
- `npm.cmd --prefix DreamForge.Canvas test -- --run`：通过，6 个测试文件、40 项测试全部通过。
- `npm.cmd --prefix DreamForge.Canvas run build`：通过，`tsc --noEmit` 与 `vite build` 均通过。
- `git diff --check`：通过，无空白错误（仅 LF/CRLF 行尾提示）。
- 可重复 UI 冒烟（仓库外临时工程，真实对话框与真实画布控件）：通过，26 段，含引用与回收站、章节结构、章节布局、任务重试、接口智能导入窗口、接口导入向导四步（读网页 → 建技能 → 输密钥加密落盘 → 最小测试返图 / 选「否」保存完成），以及真实宿主入口十段：第 17 段 `RollbackLastCommit`（同画布撤销后资产原样回位且字节一致、画布节点数恢复；跨画布撤销被拒、画布未改动、**不**进入待恢复、不拦离开、记录保留），第 18 段资产清理（`asset://` 引用先解析成本机路径再移出、撤销后内容逐字恢复；解析不到的引用如实报未移动），第 19 段真实标签生命周期（`A(49 节点)→B(1 节点)→A→B` 内容互不串味、关闭当前标签切回该标签内容、关闭非当前标签不动当前未保存编辑），第 20 段撤销上下文与恢复收敛（换文档后身份变化且旧上下文作废；待恢复时重试入口可达、只重试失败项、画布只回退一次、账本退出待恢复），第 21 段待恢复期间禁新批（预览不换批、面板保存给「先恢复」说明、直接提交被拒且画布不变；恢复完成后新批放行），第 22 段复制画布接守卫（待恢复时真实复制入口不创建副本、不切标签；干净现场副本状态独立；恢复后复制恢复正常），第 23 段项目级资源闭环（真实迁移入口 → 资源入库并标记项目级 → 实体列表显示「项目级」→ 改库后本画布看到新内容 → 其它画布引用时删除被硬阻断 → 删掉那份画布后允许删除），第 24 段复核返工 G6-R1/G6-R3（真实发布入口写回项目库后重开仍保留编辑；漏带项目库时真实打开入口即标记缺失并阻断解析），第 25 段复核返工 G6-S1/G6-S2（出图前阻断并注入计数桩：缺库时提供方调用为 0、库放回后判定层恢复可执行；真实 `KeepPersistedCommits`：取消不撤销已确认并落库的提交），第 26 段复核返工 G6-T1/T2/T3（打开之后才删库时执行前重新核验、提供方仍为 0 调用；本地实体发布结果为「成功但未持久化」不谎报已保存；补偿失败的待恢复记录、写库守卫与重试恢复后库与内存一致）。

## 目标 A 交付基准（2026-09-29）

目标 A「稳定 ID 与数据可靠性」的实现事实。四项返工（R1–R4）与 1.1–1.4 均已落地，并接进真实的打开、保存、导入入口。

| 主题 | 事实 |
| --- | --- |
| 只读校验 | `Canvas/CanvasIdentityValidator.cs`：`CanvasIdentityValidator.Validate(state, assetAccessible)` 返回结构化报告；42 个稳定错误代码（`CanvasIdentityCodes`）。归属判定只用候选集合：唯一候选才算确定，多个候选报 `*_AMBIGUOUS`（警告），只有「全部候选都不属于所写上下文」才报 `*_OWNER_MISMATCH`（错误）。绝不取首条数据推断归属。 |
| ID 作用域 | 节点/连线/工作树条目/实体/变体为画布级唯一；版本为所属变体内唯一；附件、布局元素、生成历史的 ID 不参与查找。校验器与索引按同一套作用域工作。 |
| 资产标识 | 按 `AssetStore.Resolve` 的真实读取规则判形状：`asset://` 只取文件名，带目录的写法属「可用但非规范」（`ASSET_REFERENCE_NON_CANONICAL`，警告）；只剩目录名、文件名含 Windows 非法字符（`* ? " < > |`）、路径片段非法、疑似 URI 无法解析才报 `ASSET_REFERENCE_MALFORMED`（错误）；取不到文件报 `ASSET_UNREACHABLE`（警告，仅传入解析委托时判定）。 |
| 格式版本 | `RecentCanvasState.FormatVersion`（`CanvasFormat.Legacy = 0` / `CanvasFormat.Current = 1`），缺省 0 表示未标注格式的旧文件；保存时标注为当前版本。字段只追加，旧读取器忽略未知字段即可继续打开。**高于当前支持的版本（例如文件里是 99）不做迁移、不降级标注**：可以只读查看并给出可读诊断，但覆盖保存与导入会被拒绝，避免丢失未知数据。 |
| 迁移 | `Canvas/CanvasMigration.cs`：旧单引用与旧图片路径搬进 `References`/`Attachments`，空 GUID 补 ID，以上都在内存完成；打开阶段不写盘。补出来的 ID 由「迁移前文档指纹 + 对象类型 + 字段路径」确定生成，因此同一份未修改的来源连续迁移得到同一批 ID，导入身份稳定、不随迁移漂移。指向不存在对象、空值语义不唯一（如空 GUID 锁定版本）一律留成歧义项，提供显式处理入口 `DropUnresolvableReferences` / `TreatEmptyLockedVersionAsFollowCurrent`，不猜绑定。重复迁移无额外改动。 |
| 备份与写入 | `Canvas/CanvasStorage.cs`：`CanvasBackup`（画布库 `backups/` 子目录，每个画布保留最新 10 份，支持列出与恢复）与 `CanvasFileWriter`（先写临时文件再替换，失败删除临时文件并保留源文件）。**目标文件已存在时先备份，备份失败会中止保存并抛 `CanvasSaveAbortedException`（不覆盖原文件）**；首次保存（目标不存在）不需要备份。恢复前也要先给当前文件留一份备份，这一步失败同样中止恢复、不覆盖当前文件。 |
| 保存入口统一 | `CanvasLibrary.Save`（标签切换/关闭写回/库保存）与 `CanvasLibrary.Rename`（重命名，含覆盖目标）都只是 `CanvasSaveService.Save` 的薄封装：深拷贝 → 迁移 → 校验 → 写前备份（失败即中止）→ 临时文件原子替换。`CanvasCommandService.Save` 复用 `CanvasLibrary.Save`。手动保存、复制落盘、命令服务、标签切换、关闭写回、重命名六条入口共用同一策略，不存在旁路写盘；重命名在新文件写成功后才删除源文件，删除失败会明确报出并保留两侧文件。导出包的 `canvas.json` 与“最近画布草稿”槽位同样走 `CanvasFileWriter` 原子写入（导出会先在副本上迁移，旧字段引用的资产一并打包；草稿是滚动自动保存槽，不做备份）。 |
| 打开/保存入口 | `CanvasOpenService.TryOpen`（读取→迁移→校验，任何校验错误都不阻止打开；更高格式版本只读查看）与 `CanvasSaveService.Save`（先深拷贝→迁移→校验→写前备份→原子替换）。**保存只作用于副本**：迁移、备份或写入失败时调用方对象图（ID、旧字段、空 ID）保持原样；写出的文件在写入前就已完成迁移并标注格式版本。 |
| 复制 | `Canvas/CanvasDuplication.cs`：复制画布时全部对象换新 ID、集合内关系按「源画布唯一候选 + 明确所属上下文」重写、集合外引用按共享保留；版本一律按所属变体解析，同一 GUID 出现在不同变体互不影响。旧 ID 重复或与所写上下文不符时不猜归属：不登记映射、保留原引用并在报告里列为歧义；`DuplicateNodes` 遇到重复 ID 直接拒绝该 ID（不复制、不取首条）。UI 入口：画布库“复制画布”、画布右键与工具栏“复制节点”。 |
| 重复导入 | `Canvas/CanvasImportMerge.cs`：按 ID 与作用域判定身份，同名不参与判定；已导入且内容一致→跳过，同 ID 不同内容→冲突并保留目标内容，新 ID→追加，变体 ID 被别的实体占用→整条实体不合并。内容指纹不包含附件/布局/历史 ID 与附件添加时间，避免迁移生成的随机 ID 或时间戳造成假冲突。含空 ID 的旧来源因补全 ID 稳定，连续导入与目标保存重开后再次导入都零增量。资产在包内容副本上**先迁移再收集**，旧字段引用的资产同样会被复制，缺失资产真实计数。UI 导入入口提供“替换 / 合并 / 取消”明确选择。 |
| 诊断展示 | 画布库“检查画布”与打开/保存/导入后的提示入口：显示迁移报告、只读校验报告、备份列表与只读查看限制，并提供丢弃无法解析的旧引用、空锁定版本改为跟随当前、恢复备份三个显式操作（只读查看时前两个被禁用）。 |
| 未做 | 自动批量迁移用户现有项目、项目级资源库迁移、工作树自动同步、章节布局、Web 新功能；视频仍未实现。 |

## 实现现状基准（2026-09-29）

本节是"代码里到底有什么"的入口，细节见 `02_Architecture.md`。下表只列当前仍有价值的事实和遗留项。

| 主题 | 事实 |
| --- | --- |
| 解决方案 | `DreamForge.slnx` 含 6 个项目；`DreamForge.Mcp` 与 `DreamForge.Canvas` 都不在其中 |
| 引用关系 | Core←Host←{Desktop,Web}；Desktop 不引用 Web/Mcp，两者只走 HTTP；`Core.Tests` 因校验桌面状态已引用 **Desktop**（`net10.0-windows`） |
| 桌面画布 | `WorkflowCanvasControl`（GDI+ 自绘）；WebView2 与 postMessage 已移除；自动排版为**按章节分块**（`ChapterKeyOf` / `ChapterBounds` / `ArrangeChapter`） |
| 前端画布 | `DreamForge.Canvas` 由 `DreamForge.Web` 托管（静态 dist + `/ws/canvas` + `/api/canvas/*`），浏览器走 WebSocket；仍是 div 卡片，tldraw 未被使用，不在 slnx |
| 桌面 ↔ Web | 桌面端 `POST /api/canvas/scene` 推全量投影 + 每 500ms 轮询 `GET /api/canvas/resource-replace/next`；Web 广播 `host/scene.reset`，并回投 `host/resource.replace.result` |
| 数据模型 | 画布 JSON = `Nodes` + `Edges` + `Entities`（视觉轴）+ `WorkTree`（叙事轴）+ 可选的 `FormatVersion`（数据格式版本，缺省 0 表示旧文件）；`NodeCategory` 追加 `StoryPlan=6 / StoryOutline=7 / Chapter=8` |
| 三套锚点 | `ParentNodeId`（排版）/ `WorkTreeItemId`（叙事轴）/ `References[]`（视觉轴，可被 `entityTarget`/`entityTargets` 写入）；只读校验器按这三套关系分别检查，归属歧义与错绑分开报告 |
| ID 生命周期 | 打开时迁移并校验（不写盘），保存时迁移副本 + 写前备份 + 原子替换；复制画布/节点重发新 ID 并重映射集合内关系；导入按 ID 合并去重，同名不参与判定 |
| Agent | Ask / AutoStage / ReadOnly 三种授权；13 种 action（新增 `entityTargets`）；虚影预览→勾选→保存；撤销依赖提交前快照；**角色/场景/道具不建画布节点** |
| 生成能力 | 文本与图像已实现（OpenAI 兼容 / Anthropic / ComfyUI）；视频只有链路契约与技能（`IVideoProvider` 目前只有「未接入」实现），**出视频执行方未实现** |
| 密钥 | DPAPI（当前用户）+ 密文前缀 `dpapi:`；配置文件在程序目录 |
| 未实现 | 协作、账号/审批/配额/审计、Web 端独立创作、视频生成（接口说明与技能可建、执行方未接入） |
| 当前遗留项 | 工作树自动同步、章节局部布局/手动位置保护、完整引用删除保护、项目级资源库、资源替换协议夹具与跨语言校验、Web 资产端点及选择回传仍未完成；稳定 ID 全链路与旧数据迁移已实现但待统一复核，细节见目标 1–8、目标 A 交付基准和 `02_Architecture.md` |

## 历史轮次记录

以下内容按当时状态保留，用于追溯实现与验证；其中的“当前”“遗留问题”“下一步”均指对应记录日期，不代表 2026-09-29 的现状。现行结论以上方当前基线、目标 0–8 和 `02_Architecture.md` 为准。

### 2026-09-25 第一轮基准

### 已完成

- 确认并采用 C# + TypeScript 双语言边界。
- 建立 `protocol/PROTOCOL.md`，定义版本 1 信封、消息目录、方向校验、能力位覆盖、fail-closed 错误码和单人撤销边界。
- 建立 `protocol/fixtures/` 跨语言一致性夹具及 `manifest.json`。
- 初始化 `DreamForge.Canvas` npm 包配置，锁定 tldraw `5.4.2`。
- 建立 `DreamForge.Canvas/src/Protocol/VersionedMessages.ts`、`CanvasMessageCodec.ts`、`Capabilities.ts`。
- 建立 `CanvasBridge.ts`、`CanvasStore.ts`、tldraw 入口 `CanvasApp.tsx` 和 Dream 节点类型。
- 建立协议一致性、Bridge 和 CanvasStore 测试。

### 本轮验证

- `npm.cmd install`：通过；依赖审计无漏洞。
- `npm.cmd run build`：通过；`tsc --noEmit` 与 `vite build` 均通过。
- `npm.cmd test`：通过；3 个测试文件、24 个测试全部通过。
- tldraw：`5.4.2`。
- TypeScript 构建产生大于 500 kB 的单个生产 chunk 警告，当前不阻断构建，后续进入性能门槛处理。

## 2026-09-25 第二轮更新：C# Core

### 已完成

- 建立 `DreamForge.slnx`，纳入 `DreamForge.Core` 与 `DreamForge.Core.Tests`。
- 两个项目统一使用 `net10.0`。
- 将 `05_Core_Contracts.cs` 的核心领域建模落入可构建的 `DreamForge.Core` 类库。
- 实现 `SessionContext`、`Invocation`、`ExecutionResult`、`Channel`、`Skill`、`Tool`、`TypedReference` 和单人 `OperationRecord`。
- 实现版本化 JSON 协议解码：版本、消息类型、方向、必填字段、能力声明和 fail-closed 错误码。
- 实现 `AccessPolicy`：权限只读取服务端 `ServerClaims`，不信任客户端角色字段进行提权。
- 实现 `ReferenceGraph`：递归 Skill/Tool/Channel 解析、循环检测、递归深度上限和未知引用拒绝。
- 实现 Job 状态机：排队、运行、取消中、成功、失败、取消；支持进度、错误信息、输出和幂等结果注册。
- 实现本地单人 Undo 栈；没有实现协作快照回滚。
- 建立 6 项无外部测试框架依赖的 Core 回归测试。

### 本轮验证

- `dotnet restore DreamForge.slnx`：通过。
- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，6 项测试全部通过。
- 初次编译发现 `System.TypedReference` 与项目 `TypedReference` 同名歧义，已在测试中显式限定项目类型并复验通过。

### 遗留问题

- C# 协议测试目前覆盖核心规则，尚未逐个读取 `protocol/fixtures/manifest.json` 做完整跨语言夹具回放。
- `ReferenceGraph` 当前以仓储传入字典作为授权后的可见集合；服务端查询层仍需实现实际数据权限过滤。
- `DreamForge.Core` 尚未接入 ASP.NET Core、SignalR、SQLite 或 Avalonia。
- 解决方案当前仍没有桌面端和 Web 宿主项目。

### 下一步

1. 建立 `DreamForge.Desktop` 的 Avalonia + WebView 宿主壳。
2. 建立 `DreamForge.Web` 的浏览器宿主适配层。
3. 实现 C# `CanvasBridge` 适配器，与 TypeScript `CanvasBridge` 完成握手、场景初始化和 Job 更新回传。
4. 为协议夹具建立 C# 自动回放测试。

## 2026-09-25 第三轮更新：桌面端与 Web 宿主壳

### 已完成

- 建立 `DreamForge.Host` 共享宿主桥接项目，目标框架为 `net10.0`。
- 实现 `HostBridge`：会话初始化、服务端声明能力位、场景初始化、Job 更新、错误回传和 Canvas 消息接收。
- 实现 `DesktopCanvasTransport`：为 Avalonia WebView 提供 JSON 入站/出站边界。
- 实现 `WebCanvasTransport`：为浏览器 `postMessage` 提供 JSON 入站/出站边界。
- 建立 `DreamForge.Desktop` 与 `DreamForge.Web` 两个 `net10.0` 启动壳，均引用共享宿主桥接层。
- 统一默认场景序列化为 `{ revision, snapshot: { records: [] } }`，避免 `JsonElement` 与匿名类型混用。
- 保持 Core 内部强类型 `ReferenceKind`，跨语言协议使用字符串类型；后续夹具回放需继续校验转换规则。

### 本轮验证

- `dotnet restore DreamForge.slnx`：通过。
- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，6 项测试全部通过。
- `dotnet run --project DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，输出协议版本 1 的 `host/init` 消息。
- `dotnet run --project DreamForge.Web/DreamForge.Web.csproj --no-restore`：通过，输出协议版本 1 的 `host/init` 消息。
- `npm.cmd run build`：通过；TypeScript 编译和 Vite 构建均通过。

### 当前边界

- 当前 `DreamForge.Desktop` 是宿主通信壳，不包含 Avalonia UI 包和真实 WebView 控件；真实控件接入应在桌面 UI 层完成。
- 当前 `DreamForge.Web` 是浏览器互操作边界，不包含 Blazor WebAssembly 页面；页面层接入时不得把 tldraw 重新实现为 Razor 画布。
- 宿主启动示例使用控制台输出验证协议，尚未执行真实浏览器渲染或 Avalonia WebView 输入法/焦点/性能验证。

### 下一步

1. 建立单机端到端执行服务，把 `canvas/invoke.request` 接到 `Job` 和 `host/job.update`。
2. 为 `HostBridge` 增加 C# 协议夹具自动回放测试。
3. 再按技术验证门槛接入真实 Avalonia WebView 和浏览器页面宿主。

## 2026-09-25 第五轮更新：WinForms 原生工作流画布

### 已完成

- 在 `DreamForge.Desktop` 新增原生 WinForms 工作流画布控件，支持节点新增、删除、选择、拖动、输入输出端口连线、连线删除、滚轮缩放、中键平移、网格和箭头绘制。
- 将六节点剧情流程实现为可选模板，不作为固定初始内容。
- 增加选中节点属性面板，可编辑标题、类型和内容。
- 最近画布文件扩展为完整保存节点和边数据，同时保留任务执行、取消、状态与任务记录。
- 移除 Desktop 的 Microsoft.Web.WebView2 包引用、Canvas dist 输出复制、DesktopCanvasTransport 和 WebView 初始化；未修改 `DreamForge.Canvas` 工程。

### 本轮验证

- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj`：通过，16 项测试全部通过。
- 修正连线交互后再次执行 Desktop 构建：通过，0 个警告，0 个错误。

### 遗留问题

- 尚未在当前环境进行人工 WinForms 交互验收；编译和 Core 回归测试均通过。

### 下一步

1. 进行桌面端人工交互验收，重点检查高 DPI 下的端口命中和画布平移体验。
2. 后续按发布门槛补充桌面 UI 自动化测试。

## 2026-09-25 第四轮更新：单机端到端执行服务

### 已完成

- 在 `DreamForge.Host` 实现 `IInvocationExecutor` 抽象和 `InMemoryInvocationExecutor`，为后续 ComfyUI/Channel 执行器保留替换边界。
- 实现 `SingleMachineExecutionService`：服务端权限校验、Job 创建、状态推进、进度发布、成功/失败/取消结果和幂等复用。
- 将 `canvas/invoke.request` 接入 `HostBridge`，解析 `Invocation` 与 `idempotencyKey`，拒绝未握手或无 `skill.invoke` 权限的请求。
- 将 `canvas/job.cancel.request` 接入 Job 取消入口，并要求服务端会话具备 `job.cancel` 声明。
- 将 Job 更新通过既有 `host/job.update` 消息回传 Canvas。
- 为 `IdempotencyRegistry` 增加锁，避免并发访问破坏结果表。
- 新增宿主集成回归测试：模拟 Canvas 握手、提交 Invoke、等待成功 Job 更新，并验证重复幂等键复用同一个 JobId。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，7 项测试全部通过。
- 端到端测试已确认 `Queued → Running → Succeeded` 更新链路、100% 进度回传和幂等 JobId 复用。

### 当前边界

- 当前执行器是内存模拟执行器，只返回本地 Asset 引用，不执行真实模型或 ComfyUI 任务。
- 当前 Job 存储和幂等表是进程内存结构，尚未接入 SQLite/PostgreSQL。
- 当前异步请求通过宿主事件触发，下一步需要补充真实 UI 生命周期下的取消令牌传播和断线恢复策略。

### 下一步

1. 实现真实 Channel/ComfyUI 执行器适配，保留 `IInvocationExecutor` 接口。
2. 接入 SQLite Job 持久化和启动恢复策略。
3. 为协议夹具建立 C# 自动回放测试，并覆盖取消、失败和未授权请求。

## 2026-09-25 第五轮更新：Canvas 协议解析与 Job 生命周期

### 已完成

- 收紧 `DreamForgeProtocol.Decode`：信封 `id` 必须为 GUID，`ts` 必须为整数，未知消息类型、版本、方向和缺失字段继续 fail-closed。
- 增加 `host/job.update` 负载校验：Job/Invocation 标识、状态枚举和 `0..100` 进度必须合法。
- 强化 `Job` 为线程安全状态机：进度不可倒退，成功/失败/取消终态不可再次变更，输出集合复制后再暴露。
- 修正 Job 取消语义：排队 Job 直接进入 `Cancelled`，运行中 Job 进入 `Cancelling` 并向执行器传播 `CancellationToken`。
- 修正并发幂等：相同用户和幂等键的并发请求共享同一个进行中 Task，不会创建重复 Job。
- 增加 `GetJobs(userId)` 只读查询，供宿主生命周期和后续恢复逻辑使用。
- 保持失败统一进入 `Failed` 终态并写入 `JOB_EXECUTION_FAILED`，不再把执行器异常冒泡成未观察任务。
- 增加慢执行器取消、失败执行器和严格协议字段测试。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，9 项测试全部通过。
- 已串行验证构建和测试，避免并行写入 Core 输出目录造成文件锁误报。

### 当前边界

- Job 和幂等状态仍为进程内存，进程重启后不能恢复。
- 取消令牌已经传播到 `IInvocationExecutor`，但真实 Channel/ComfyUI 执行器尚未实现其外部请求取消。
- Canvas 协议目前是 C# 运行时严格解析，尚未逐个回放 TypeScript `protocol/fixtures/` 全部夹具。

### 下一步

1. 接入真实 Channel/ComfyUI 执行器并实现外部任务取消。
2. 接入 SQLite Job、幂等键和输出 Asset 持久化。
3. 实现 C# 与 TypeScript 协议夹具双向自动回放。

## 2026-09-25 第七轮更新：外部任务 ID 持久化

### 已完成

- 新增 `ExecutionOutput`，执行器可同时返回外部任务 ID 和输出 Asset。
- `Job` 新增 `ExternalTaskId`，支持运行中绑定、快照恢复和结果回传。
- 外部任务 ID 只能绑定一次；终态 Job 不允许新增绑定，冲突 ID 会拒绝。
- `SingleMachineExecutionService` 在执行器返回外部 ID 后立即发布并持久化 Job 更新，不必等任务完成。
- `ExecutionResult` 和 `host/job.update` 增加可选 `externalTaskId` 字段。
- C# 与 TypeScript 协议解析器校验外部 ID 必须是字符串或 null；本地任务无外部 ID 时仍合法。
- SQLite `jobs` 表新增 `external_task_id` 列；初始化时对旧数据库执行兼容性列迁移。
- SQLite 加载、Upsert、Job 恢复和幂等结果均保留外部任务 ID。
- 默认内存执行器生成本地外部 ID，模拟 ComfyUI 等外部队列关联。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，11 项测试全部通过。
- 新增测试覆盖：外部 ID 执行器返回、SQLite 写入、服务重启恢复、Job 外部 ID 冲突拒绝。

### 当前边界

- 当前外部 ID 只负责关联和持久化，尚未提供按外部 ID 查询第三方队列状态的轮询器。
- 取消操作已向执行器传播 `CancellationToken`，但真实第三方 API 的取消请求仍需由具体 Channel 实现。
- 外部任务 ID 当前为单字符串；如果不同供应商需要复合标识，后续应增加 `Provider` 或 `ExternalSystem` 字段。

### 下一步

1. 接入真实 Channel/ComfyUI 执行器，保存 provider 和外部队列信息。
2. 增加外部任务状态轮询、回调和断线恢复。
3. 增加 Job 查询、分页、日志和人工重试协议。

## 2026-09-25 第八轮更新：外部任务状态轮询和回调

### 已完成

- Core 新增 `ExternalTaskState`：`Unknown`、`Queued`、`Running`、`Succeeded`、`Failed`、`Cancelled`。
- 新增 `ExternalTaskUpdate`，统一承载外部任务 ID、状态、进度、错误和输出 Asset。
- `Job.ApplyExternalUpdate` 实现供应商无关的状态映射：进度只前进，成功/失败/取消为终态，重复相同终态通知幂等。
- 外部任务 ID 必须匹配当前用户的 Job；不存在、跨用户或不匹配的任务不会被应用。
- `ExecutionOutput` 增加 `AwaitExternalCompletion`；真实外部队列任务提交成功后保持本地 Job 为 `Running`，由轮询或回调推进终态。
- 新增 `IExternalTaskProvider`，定义供应商状态查询接口。
- 新增 `ExternalTaskPoller`：后台定时查询所有未完成外部 Job，支持启动、停止、异常隔离和 CancellationToken。
- 新增 `ExternalTaskCallbackReceiver`：接收供应商回调并复用同一套 Job 状态映射与 SQLite 持久化路径。
- 外部状态更新会继续通过 `Updated` 事件转成 `host/job.update`，因此 Canvas 无需区分轮询和回调来源。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，13 项测试全部通过。
- 新增测试覆盖：回调状态映射、终态重复回调、跨用户拒绝、进度不回退、轮询自动完成和停止释放。

### 当前边界

- 当前轮询器为宿主内存后台服务，生产环境需要按 Provider 分组、退避策略、超时、限流和可观测日志。
- 回调接收器已经提供应用层入口，HTTP 签名校验、时间戳防重放和供应商密钥管理仍应在 Web API 层实现。
- 外部执行器必须显式设置 `AwaitExternalCompletion = true`；否则仍按同步执行器处理并立即完成本地 Job。

### 下一步

1. 接入真实 ComfyUI/Channel Provider，实现状态查询、回调签名校验和外部取消。
2. 增加轮询退避、失败次数、最后查询时间和 Provider 限流配置。
3. 增加 Job 查询、分页、日志和人工重试协议。

## 2026-09-25 第六轮更新：SQLite Job 状态持久化

### 已完成

- 为 `DreamForge.Host` 引入 `Microsoft.Data.Sqlite 9.0.9`，目标框架保持 `net10.0`。
- 新增 `IJobStore` 和 `SqliteJobStore`，自动创建 `jobs` 表及用户/更新时间索引。
- 持久化 Job 核心快照：JobId、UserId、InvocationId、幂等键、状态、进度、错误信息和输出 Asset JSON。
- Job 写入使用 `ON CONFLICT(job_id) DO UPDATE`，状态更新与幂等键恢复可重复执行。
- `ExecutionResult` 增加 `UserId`，`Job.Restore` 使用显式 JobId 恢复，未使用反射修改只读标识。
- `SingleMachineExecutionService` 支持注入 JobStore：启动初始化数据库、加载快照、恢复终态幂等结果。
- 宿主重启时，`Queued/Running/Cancelling` 快照统一转为 `Failed/HOST_RESTARTED` 并写回数据库，避免未经确认的自动重复执行。
- 关闭 SQLite 连接池，确保桌面进程释放数据库文件句柄。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，10 项测试全部通过。
- SQLite 测试已覆盖：成功 Job 写入、重新创建服务实例恢复、恢复后的幂等键复用、未完成 Job 的宿主重启失败标记。

### 当前边界

- 当前 SQLite 仓储同步执行 SQL，适合单机 MVP；高并发 Web 场景仍需异步队列或独立数据库访问层。
- 当前只持久化 Job 快照，Invocation 输入原文和执行器外部任务 ID 尚未单独建列；后续接入真实 Channel 时需要增加审计与重试字段。
- 宿主重启默认不自动重试未完成 Job，后续可在权限和幂等策略明确后增加人工恢复入口。

### 下一步

1. 接入真实 Channel/ComfyUI 执行器和外部任务 ID 持久化。

## 2026-09-25 第十二轮更新：ComfyUI 队列、WebSocket 进度与取消

### 已完成

- `ComfyUiProvider` 正式接入 ComfyUI 原生 `GET /queue`，能区分运行中任务和排队任务，并在 history 尚无记录时回退查询队列。
- 接入 ComfyUI 原生 WebSocket `/ws`，按 `clientId` 建立连接，过滤目标 `prompt_id`，将 `progress`、`execution_success` 和 `execution_error` 映射为统一外部任务更新。
- 接入 ComfyUI 原生 `POST /interrupt`，Job 取消时同时触发本地取消和远端中断；远端失败不会阻塞本地取消，后续由轮询收敛。
- 外部异步任务不再提交后固定写入 90%，真实进度从 0% 单调推进，避免 WebSocket 低于 90% 的进度被拒绝。
- 轮询器停止时等待所有 WebSocket 监听任务退出，避免后台监听任务泄漏。
- 增加队列、取消、文件下载和可控进度源测试；未知 history 响应使用合法 JSON，覆盖 history 到 queue 的回退路径。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 新增验证覆盖：外部回调真实进度、队列状态映射、WebSocket 进度写回、监听任务停止释放、ComfyUI interrupt 请求和输出文件下载。
- 当前 Provider 支持可配置 WebSocket 接收超时、最大重连次数和初始重连间隔，断线采用指数退避并受上限约束。

### 当前边界

- WebSocket 已支持有限次数重连、指数退避和单次接收超时；达到上限后由下一轮 HTTP 轮询继续获取状态，当前尚未提供结构化日志接口。
- 资产下载仍未加入响应大小上限、MIME 白名单和内容哈希校验。
- 轮询器尚未记录失败次数、最后查询时间和可观测日志。

### 下一步

1. 增加 WebSocket 断线重连、退避、超时和结构化日志。
2. 为 ComfyUI 资产下载增加大小限制、MIME 校验和哈希校验。
3. 增加自定义 workflow、img2img、LoRA、ControlNet 及外部任务重试策略。

## 2026-09-25 第九轮更新：ComfyUI Provider 与 HTTP 回调签名

### 已完成

- 新增 `ComfyUiExecutor`：向 ComfyUI `POST /prompt` 提交工作流，解析 `prompt_id`，并以外部异步任务返回。
- 新增 `ComfyUiProvider`：查询 `GET /history/{prompt_id}`，映射排队、运行、失败和成功状态，并将图片输出转换为 `comfyui://` Asset 引用。
- 新增 `HmacCallbackVerifier`：使用 HMAC-SHA256 校验 `timestamp.Base64(rawBody)` 签名，支持 `sha256=` 前缀、固定时间比较、时间窗口和 nonce 防重放。
- 新增 `SignedExternalCallbackHandler`：验签后使用字符串枚举兼容配置反序列化 `ExternalTaskUpdate`，并交给统一回调接收器。
- `DreamForge.Web` 改为 ASP.NET Core Web SDK，接入 `/callbacks/comfyui/{userId}` HTTP 路由和 `/health` 健康检查；回调缺少签名、签名无效、重放、过期、无效 JSON 和未知 Job 分别返回对应 HTTP 结果。
- 测试夹具补充 HTTP Handler、ComfyUI 提交/历史映射、HMAC 签名、防重放、过期拒绝和签名回调完成 Job 的完整链路。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，15 项测试全部通过。
- 已确认 ComfyUI `prompt_id` 能绑定到本地 Job，历史输出能转为 Asset，合法签名能推进 Job 到成功，错误签名、重复 nonce 和过期时间会被拒绝。

### 当前边界

- ComfyUI 当前未实现输出文件下载、队列精确进度、外部取消、认证、重试和超时；`comfyui://` 只是统一 Asset 引用，不是本地文件缓存。
- Web 路由当前从配置读取 Base64 编码回调密钥；生产环境仍需将执行服务、Provider、SQLite 仓储和密钥配置统一注册为应用级依赖，避免示例宿主与真实任务提交端分离。
- nonce 当前为进程内内存缓存，单机可用；多实例部署需要共享存储和密钥轮换策略。

### 下一步

1. 将 ComfyUI 地址、认证、执行服务和 SQLite 仓储接入 Web/桌面正式依赖注入。
2. 实现输出文件下载、队列进度、外部取消、重试、超时和 Provider 限流。
3. 为 HTTP 路由增加集成测试，并将 nonce 缓存替换为可配置持久化实现。

## 2026-09-25 第十轮更新：正式接入 SQLite Job Store 与 ComfyUI Provider

### 已完成

- `DreamForge.Web` 改为应用级依赖注入：`SqliteJobStore`、`SingleMachineExecutionService`、`ComfyUiExecutor`、`ComfyUiProvider`、回调接收器和 HMAC 验证器均由容器管理。
- Web 配置支持 `DreamForge:JobDatabasePath`、`ComfyUI:BaseUrl`、`ComfyUI:ClientId`、`ComfyUI:WorkflowJson`、`ComfyUI:HttpTimeout`、`ComfyUI:PollingInterval` 和 `ComfyUI:CallbackSecret`。
- Web 的画布宿主、HTTP 回调和外部任务轮询共享同一个 `SingleMachineExecutionService`，因此 Job 创建、SQLite 恢复、轮询更新和签名回调使用同一份状态。
- 新增 `ExternalTaskPollingHostedService`，将外部任务轮询器接入 ASP.NET Core 启停生命周期，并修正启动/停止取消令牌竞态。
- `DreamForge.Host` 增加 `Microsoft.AspNetCore.App` FrameworkReference，以承载 Web 生命周期抽象。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，15 项测试全部通过。
- 已验证既有 SQLite 恢复、ComfyUI Provider、外部轮询、HMAC 回调和 Job 生命周期回归测试没有退化。

### 当前边界

- `ComfyUI:BaseUrl` 是必需配置；当前 `WorkflowJson` 使用固定工作流 JSON，尚未根据 Invocation 输入动态生成 ComfyUI 节点图。
- Web 默认数据库文件位于应用目录下；生产部署应显式配置 `DreamForge:JobDatabasePath` 到可写、备份策略明确的持久化目录。
- 回调密钥要求 Base64 编码；nonce 仍为单进程内存缓存，多实例部署前需要共享存储。
- ComfyUI 输出仍以 `comfyui://` 引用暴露，尚未实现文件下载、本地缓存、外部取消、精确队列进度、重试和限流。

### 下一步

1. 增加 Web 路由集成测试，覆盖真实 HTTP 状态码和共享 Job 状态。
2. 将 WorkflowJson 替换为基于 Invocation 的安全工作流构造器，并增加 ComfyUI 认证配置。
3. 实现输出文件下载、外部取消、重试/超时和 Provider 限流。

## 2026-09-25 第十一轮更新：动态工作流生成与 ComfyUI 文件下载

### 已完成

- 新增 `ComfyUiWorkflowFactory`，根据 `Invocation.Inputs` 生成受控 txt2img 工作流。
- 当前支持：`prompt`、`negativePrompt`、`width`、`height`、`steps`、`cfg`、`seed` 和 `checkpoint`。
- 对尺寸、步数、CFG 和 seed 执行服务端范围限制，避免客户端传入无限资源参数。
- ComfyUI Provider 解析历史输出中的 `filename`、`subfolder` 和 `type`，调用 `/view` 下载真实文件。
- 支持配置 `DreamForge:AssetDirectory`，文件写入本地 Asset 目录并返回 `asset://<generated-name>` 引用。
- 未配置 Asset 目录时保留 `comfyui://<filename>` 兼容行为。
- Web 宿主使用动态工作流工厂，不再读取固定 `WorkflowJson`；新增 `ComfyUI:Checkpoint` 配置。
- 下载文件使用随机本地文件名，避免路径穿越和输出文件名冲突；远程文件名只允许作为 ComfyUI `/view` 参数，不直接拼接本地路径。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，15 项测试全部通过。
- 新增测试覆盖动态 prompt 映射、参数边界裁剪、ComfyUI `/prompt`、`/history`、`/view` 和本地文件落盘。

### 当前边界

- 当前工作流模板是标准 txt2img，尚未支持 img2img、ControlNet、LoRA、视频和自定义节点图。
- `ComfyUI:Checkpoint` 需要与 ComfyUI 实例中的模型文件名一致；未配置 checkpoint 时，首次执行会失败并记录 Job 错误。
- Asset 文件当前写入本地目录，但还没有独立 Asset 元数据表、访问权限校验、生命周期清理和 HTTP 下载路由。
- ComfyUI `/view` 下载尚未增加内容大小上限、媒体类型白名单和哈希校验；生产环境仍需补充这些资源保护措施。

### 下一步

1. 增加 Asset 元数据持久化、用户隔离和受控下载路由。
2. 扩展工作流模板到 img2img、LoRA、ControlNet 和视频任务。
3. 增加 ComfyUI 输出大小限制、MIME 校验、哈希校验、重试和超时策略。

## 2026-09-25 第十二轮更新：桌面窗口 WinForms MVP

### 已完成

- 将 `DreamForge.Desktop` 从控制台宿主切换为 `net10.0-windows` WinForms GUI 应用。
- 新增 `MainForm` 三栏窗口：左侧导航、中央图像任务配置区、右侧状态与任务列表区。
- 接入工作流类型、提示词、反向提示词、尺寸、采样步数、CFG、种子等基础参数控件。
- 接入任务提交、Job 状态与进度更新、任务列表刷新和当前任务取消按钮。
- 保留 `DesktopCanvasTransport` 作为后续 WebView/Canvas 通信边界，窗口逻辑通过 `SingleMachineExecutionService` 工作。
- 修复任务列表对不存在的 `Job.UpdatedAt` 属性依赖，并清理桌面项目可空引用警告。

### 本轮验证

- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。

### 当前边界

- 本轮桌面窗口采用 WinForms 作为可运行 MVP；原架构文档约定的 Avalonia WebView 尚未接入，后续需要在正式 UI 架构决策后迁移或替换。
- `MainForm` 当前默认使用 `InMemoryInvocationExecutor`，尚未共享 Web 端的 SQLite Job Store、ComfyUI Provider、WebSocket 进度监听和远端取消器。
- 右侧队列数字当前是本地 Job 数量，不是 ComfyUI `/queue` 的真实队列位置。
- 当前未提供输出图片预览，种子仍按文本输入传递，窗口异步回调的关闭竞态和 UI 自动化测试尚待处理。

### 下一步

1. 确认 WinForms MVP 是否迁移为 Avalonia WebView 正式桌面架构。
2. 为桌面 Host 注入 SQLite Job Store、ComfyUI Executor、Provider 和取消器，并复用外部任务监控。
3. 增加输出图片预览、真实队列位置、种子类型校验和窗口集成测试。

## 2026-09-25 第十三轮更新：新建画布入口

### 已完成

- 在 `DreamForge.Desktop/MainForm.cs` 左侧导航增加“新建画布”入口。
- 增加当前画布标题和修订号显示，创建新画布后生成新的时间标识并递增修订号。
- 新建画布操作增加确认提示，确认后清空当前提示词、反向提示词、尺寸、采样、CFG、种子和当前任务状态。
- 新建画布不会删除任务历史，仅重置当前桌面编辑上下文。

### 本轮验证

- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。

### 当前边界

- 当前“新建画布”是 WinForms MVP 的编辑上下文重置流程，还没有实例化真实 tldraw 画布或发送 `host/scene.reset` 协议消息。
- 后续接入 Avalonia WebView 和 CanvasApp 后，应把这里的修订号、画布名称和空场景重置统一映射到 Canvas 协议。

## 2026-09-25 第十四轮更新：启动恢复最近画布

### 已完成

- 首次启动时默认进入干净的“新建画布”状态，提示词和反向提示词不再预填示例内容。
- 窗口关闭时将最近画布保存到用户本地应用数据目录，记录画布名称、修订号、提示词、反向提示词、尺寸、采样步数、CFG 和种子。
- 下次启动时自动读取最近画布；没有历史文件、文件损坏或参数超出范围时回退到新建画布默认状态。
- 点击“新建画布”后立即更新并保存新的空白画布状态，任务历史仍然保留。

### 本轮验证

- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。

### 当前边界

- 当前恢复的是 WinForms MVP 的画布元数据和任务配置，不是 tldraw 的真实 records 场景快照。
- 后续接入真实 CanvasApp 后，应将最近画布状态迁移到版本化场景存储，并通过 `host/scene.reset` 恢复完整画布。

## 2026-09-25 第十五轮更新：桌面画布工作区

### 已完成

- 将 WinForms 中央区域调整为画布优先布局：顶部画布工具栏、中部画布工作区、底部图像任务参数。
- 增加网格画布背景和空白画布提示。
- 支持点击画布放置临时“创作节点”，用于验证画布交互区域和后续节点扩展位置。
- 点击“新建画布”时清理临时节点、恢复空白提示并重置任务编辑状态。
- 保留宽度、高度、采样步数、CFG、种子等图像任务参数。

### 本轮验证

- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。

### 当前边界

- 当前画布工作区是 WinForms 交互占位层，不是最终 tldraw 实例。
- 下一步应接入 Avalonia WebView 或等价 WebView 控件，加载 `DreamForge.Canvas` 的 `CanvasApp`，并将新建画布映射到 `host/scene.reset`。

## 2026-09-25 第十六轮更新：接入真实 tldraw Canvas

### 已完成

- 为 `DreamForge.Desktop` 接入 `Microsoft.Web.WebView2`，使用 WebView2 承载 `DreamForge.Canvas/dist` 的真实 React/tldraw 页面。
- Canvas 入口同时支持浏览器 `postMessage` 和 WebView2 `window.chrome.webview` 消息通道。
- 桌面端通过 `DesktopCanvasTransport` 和 `HostBridge` 完成 Canvas 握手、`host/init` 和 Job 消息转发。
- 新增 `HostBridge.SendSceneReset()`，点击“新建画布”时向 tldraw 发送 `host/scene.reset` 并清空当前场景。
- WebView2 初始化失败或 Canvas 构建产物缺失时显示明确降级提示。
- 将 Canvas 静态资源复制到桌面输出目录，支持从构建输出目录直接启动。

### 本轮验证

- `npm run build`：通过，TypeScript 检查和 Vite 构建均成功。
- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。

### 当前边界

- 真实 tldraw 已嵌入桌面端，但场景 records 的持久化还未接入最近画布 JSON，当前 `host/scene.reset` 使用空场景重置。
- WebView2 Runtime 需要用户系统已安装 Microsoft Edge WebView2 Runtime；未安装时会显示降级提示。
- 后续应将 tldraw 场景快照接入版本化存储，并把 Canvas 节点与图像任务参数建立正式映射。

## 2026-09-25 第十七轮更新：原生 WinForms 工作流画布编辑器

### 已完成

- 新增 `WorkflowCanvasControl`，使用 WinForms/GDI+ 原生绘制网格、节点、端口和箭头连线。
- 节点支持自由创建、选择、拖动、删除和属性编辑；连线支持从输出端口拖到输入端口创建，并可从输入端口拖回删除。
- 支持鼠标滚轮缩放和中键平移画布。
- 六节点剧情流程改为可选模板，不再作为固定画布内容；新建画布默认为空。
- 最近画布 JSON 现在保存完整节点、连线、标题、修订号和任务参数。
- 移除桌面端 WebView2、Canvas 静态资源复制和 `DesktopCanvasTransport`，未修改 Web Canvas 工程。

### 本轮验证

- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 修正输入端口拖出删除连线逻辑，并阻止重复连线。
- 尚未进行人工 WinForms UI 操作验收；需要重点验证节点拖动、端口连线、缩放、平移和重启恢复。

### 当前边界

- 原生画布当前使用 GDI+ 自绘，尚未提供框选、多选、撤销/重做、端口类型校验和连线选中删除。
- 工作流节点尚未正式映射到 `Invocation` 执行输入；现有图像任务仍通过底部任务参数区提交。
- 桌面端默认仍使用内存执行器，尚未复用 Web 端的 ComfyUI、SQLite 和外部任务监控依赖注入。

### 下一步

1. 进行 WinForms 人工交互验收并修正画布手势细节。
2. 增加节点执行与 `Invocation` 的正式映射。
3. 按需增加框选、多选、撤销/重做和连线校验。

## 2026-09-25 第十八轮更新：节点内容来源、AI 生成与主动提问闭环

### 已完成

- 节点模型补充内容来源、执行状态与生成历史：
  - `ContentSource`（User / Ai / Api）表示内容由用户、AI 大模型或外部 API 提供。
  - `NodeExecutionStatus`（Draft / WaitingForUser / Generating / Completed / Failed / NeedsReview）。
  - `GenerationHistory` 记录输入、指令、输出、Provider、Model、提问、回答、状态与是否被采纳。
  - `Question` / `Answer` 支持 AI 主动向用户提问并用回答继续生成。
  - `ParentNodeId` / `GenerationId` 预留“由哪个节点、哪次生成创建”的溯源信息。
- 复用工作区已有的 `AiProviders.cs` 契约（`IAiProvider` / `AiGenerationRequest` / `AiGenerationResult` / `LocalAiProvider`），未重复定义第二套接口。
- 画布工具栏新增节点类型选择器，可直接创建 Text、AiRewrite、ChapterAnalysis、Storyboard、Character、Scene、ImageGeneration、ImageAsset 节点。
- 属性面板新增节点类型、内容来源、执行状态展示，以及“回答 AI 提问”输入框。
- 属性面板新增“AI 生成 / 继续”和“接受最新结果”操作；生成结果先进入待确认状态，只有用户采纳后才写回节点内容。
- 画布节点新增类型名、执行状态徽标和生成次数绘制。
- 示例模板改为产品链路：企划文本 → 第一章 → 分镜 → 人物 / 场景 → 人物参考图，并写入 `ParentNodeId`，示例仍是可选模板。

### 本轮验证

- `dotnet build DreamForge.Desktop/DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 默认 Provider 仍是 `LocalAiProvider`，输出为**本地模拟文本**（内容自带“本地模拟”字样），不是真实大模型结果；尚未接入真实大模型或图像 API 凭据与配置界面。
- AI 结果尚未自动创建下游节点：章节分析不会自动生成分镜、人物、场景节点，仍需用户手动建节点。
- 图像生成节点尚不产出图片资产，`AssetPaths` 未写入，画布也不显示缩略图。
- 生成历史已记录但界面还不能查看历史和回滚到旧版本。
- 主动提问仅支持单轮问答，没有多轮追问与取消。

### 下一步

1. 接入真实大模型 API Provider（可配置 endpoint、密钥、模型名），保留本地模拟 Provider 作为离线兜底。
2. 让章节分析结果解析为结构化分镜，并按用户确认自动创建分镜 / 人物 / 场景节点。
3. 接入图像 API，把生成图片写回节点 `AssetPaths` 并在画布显示缩略图。
4. 增加生成历史查看、版本回滚与重新生成。

## 2026-09-25 第十九轮更新：真实大模型 Provider 与下游节点自动创建

### 已完成

- 新增 `AiProviderSettings`：AI 配置保存在 `%LocalAppData%\DreamForge\ai-config.json`，并支持 `DREAMFORGE_AI_ENDPOINT`、`DREAMFORGE_AI_MODEL`、`DREAMFORGE_AI_KEY` 环境变量覆盖。
- 新增 `OpenAiCompatibleProvider`：调用 OpenAI 兼容的 `/chat/completions` 接口；提示词要求模型只返回 JSON（`output`、`question`、`proposals`）；返回内容会剥离 Markdown 代码块后再解析；模型未按约定返回 JSON 时，原文作为内容返回，不猜测结构。
- 新增 `AiProviderFactory`：已配置 endpoint 与 model 时使用真实 Provider，否则回落到 `LocalAiProvider`。
- 新增 `AiNodeProposal`，`AiGenerationResult` 增加 `Proposals`，生成结果可以携带下游节点建议。
- `LocalAiProvider` 现在按节点类型给出结构化建议：文本 → 章节分析；章节分析 → 分镜；分镜 → 人物 / 场景；人物 / 场景 → 图像生成提示词。
- 画布新增 `AddGeneratedNodes`：按建议创建下游节点并自动连线，写入 `ParentNodeId`，初始状态为草稿、来源为 AI。
- “接受最新结果”在采纳内容后会询问是否创建下游节点，只有用户确认后才创建。
- 侧边栏“设置”打开 AI Provider 配置对话框（接口地址、模型、密钥），密钥以掩码显示且仅保存在本机配置文件。
- 顶栏显示当前 AI 来源：已配置时显示模型名，未配置时显示“本地模拟（未配置）”。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未使用真实模型凭据做端到端调用验证；`OpenAiCompatibleProvider` 的请求与 JSON 解析尚未经过真实服务或单元测试覆盖。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 真实 Provider 只覆盖文本生成；图像生成仍未接入图像 API，`AssetPaths` 仍为空，画布不显示缩略图。
- 提示词仅面向文本节点，尚未针对不同模型厂商做参数差异适配（如厂商特有的 JSON 模式开关）。
- 自动创建的下游节点位置固定在父节点右侧，未做与既有节点的避让排版。
- 生成历史已记录但界面仍不能查看历史与回滚；主动提问仍只支持单轮问答。
- 真实模型调用失败时只把错误写入节点提问字段，没有重试、超时提示与密钥校验。

### 下一步

1. 接入图像 API，把生成的参考图写入节点 `AssetPaths` 并在画布显示缩略图。
2. 增加生成历史查看、重新生成与版本回滚。
3. 为下游节点自动创建增加布局避让。
4. 对真实 Provider 增加请求失败重试、超时提示与配置校验。

## 2026-09-25 第二十轮更新：图像生成与节点缩略图

### 已完成

- 新增 [ImageGeneration.cs](DreamForge.Desktop/ImageGeneration.cs)：
  - `IImageProvider`、`ImageGenerationResult`、`ImageGenerationStatus`。
  - `OpenAiCompatibleImageProvider` 调用 OpenAI 兼容 `/images/generations`，支持 `b64_json` 与 `url` 两种返回，图片保存到 `%LocalAppData%\DreamForge\assets`。
  - `UnconfiguredImageProvider` 在未配置时明确返回“未配置”，**不生成占位图片**，也不写入节点。
  - `ImageProviderFactory` 按配置选择实现。
- `AiProviderConfig` 增加 `ImageEndpoint`、`ImageModel`、`ImageSize`，并提供 `IsImageConfigured`；新增环境变量 `DREAMFORGE_IMAGE_ENDPOINT`、`DREAMFORGE_IMAGE_MODEL`。
- 设置对话框增加图像模型、图像接口地址（留空复用文本接口）、图像尺寸三项。
- 属性面板新增“生成参考图”按钮：以节点内容（空则用标题）作为提示词，成功后把文件路径写入 `AssetPaths`，记录 `imagePrompt` / `imageProvider` / `imageModel` 参数，来源标记为 API，状态置为已完成。
- 画布支持图像节点缩略图：
  - 含图片资产的节点高度自动增大，并在卡片内绘制缩略图。
  - 缩略图带缓存，控件释放时统一释放图片资源。
  - 图片文件缺失时绘制“图片不可用”占位框，不静默失败。
- 顶栏同时显示文本与图像来源状态。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未配置真实图像模型，因此未做端到端出图验证；`OpenAiCompatibleImageProvider` 未经过真实服务或单元测试覆盖。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 图像尺寸与风格按接口默认，未做节点级独立图像参数面板。
- `AssetPaths` 保存的是本机绝对路径；画布跨机器打开时会显示“图片不可用”，尚未做资产复制或相对路径管理。
- 未接入 ComfyUI 既有图像执行链路；桌面“创建图像任务”区域仍走原有的内存执行器，与节点出图是两条独立路径。
- 生成历史仍无法在界面查看与回滚。

### 下一步

1. 增加生成历史查看、重新生成与版本回滚。
2. 为下游节点自动创建增加布局避让。
3. 统一节点出图与 `SingleMachineExecutionService` / ComfyUI 链路。
4. 规划资产目录与相对路径，支持画布迁移。

## 2026-09-25 第二十一轮更新：生成历史、重新生成与版本回滚

### 已完成

- 属性面板新增“生成历史”按钮，打开节点生成历史对话框。
- 历史对话框展示每条记录的：产生时间、类型（文本生成 / 图像生成）、提供方、模型、状态、是否已采纳、输出预览。
- 选中记录后显示详情：输入、提问、回答与输出内容。
- 支持“打开资产”：当记录输出指向存在的本地图片时，可用系统默认程序打开。
- 支持“回滚到此版本”：
  - 文本记录回滚会恢复节点内容，并标记该记录为已采纳、其余记录取消采纳。
  - 图像记录回滚会把该版本资产置为节点当前预览资产，来源标记为 API。
  - 回滚后节点状态置为已完成并清除待补充提问。
- 支持“重新生成”：直接基于当前节点内容重新调用 Provider。
- 画布节点新增图像资产后会自动增高并绘制缩略图，回滚切换资产后可立即看到对应版本。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；历史对话框、回滚与资产打开均未手工验证。

### 当前边界

- 回滚只恢复节点内容或预览资产，不会回滚由该次生成创建的下游节点。
- 图像记录与文本记录通过“输出是否为本机已存在文件”区分，属于启发式判断；若文本内容恰好等于存在的文件路径会被误判。
- 历史记录仅保存在画布 JSON 中，没有独立的历史存储与容量控制，记录会持续累积。
- 缩略图按文件路径缓存，不感知文件内容被外部替换。
- 节点级图像参数（尺寸、风格、负向提示词）仍没有独立面板。

### 下一步

1. 为下游节点自动创建增加布局避让，避免覆盖既有节点。
2. 统一节点出图与 `SingleMachineExecutionService` / ComfyUI 链路。
3. 规划资产目录与相对路径，支持画布跨机器迁移。
4. 增加节点级图像参数面板与历史容量控制。

## 2026-09-25 第二十二轮更新：节点自动布局避让

### 已完成

- 新增 `FindFreeSlot`：从给定起点按行向下寻找空位，同一列放不下时切换到下一列。
- 新增 `IsFree`：按节点实际高度（含图像节点加高）判断候选位置是否与既有节点重叠。
- `AddGeneratedNodes` 改为先找空位再落位，自动创建的下游节点不再覆盖既有节点，也考虑了同一批次内新节点的互相避让。
- `AddNode`（手动新建节点）同样改用空位查找，取代原先按节点数量累加偏移的方式。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；避让效果未手工确认。

### 当前边界

- 布局是简单的“先同列向下、再换列”的贪心扫描，未做连线交叉最小化或母子节点对齐美化。
- 节点尺寸仍固定为 190 宽；长标题或长内容不会撑开节点。
- 手动拖动节点后不会重新排版，用户自定义位置会被保留（符合预期，但也没有自动整理入口）。
- 画布未提供“自动整理全部节点”操作。

### 下一步

1. 统一节点出图与 `SingleMachineExecutionService` / ComfyUI 链路。
2. 规划资产目录与相对路径，支持画布跨机器迁移。
3. 增加节点级图像参数面板与历史容量控制。
4. 增加“自动整理画布”操作。

## 2026-09-25 第二十三轮更新：桌面执行链路与 ComfyUI 统一

### 已完成

- 新增 `DesktopExecutionHost`：桌面端唯一的执行服务宿主。
  - 配置了 ComfyUI（地址 + checkpoint）时，使用 `ComfyUiExecutor` + `ComfyUiProvider` + `ExternalTaskPoller` 组成真实执行链路，并启动外部任务轮询。
  - 未配置时退回 `InMemoryInvocationExecutor`，行为与之前一致。
  - 任务区与节点出图共用同一个 `SingleMachineExecutionService` 实例，Job 记录、进度和取消入口一致。
- 新增 `ComfyUiImageProvider`：节点出图走共享执行服务提交 `text-to-image` 任务，复用 Host 的 `ComfyUiWorkflowFactory`，等待 Job 终态后把 `asset://` 输出解析为本地资产路径；超时会取消任务。
- 图像 Provider 选择顺序调整为：ComfyUI（已配置且执行服务可用）→ OpenAI 兼容图像接口 → 未配置。
- 桌面 Session 用户 ID 改为固定的 `DesktopUserId`，保证任务提交与 Job 列表过滤一致。
- `AiProviderConfig` 增加 `ComfyUiBaseUrl`、`ComfyUiCheckpoint`、`ComfyUiClientId` 与 `IsComfyUiConfigured`，支持 `DREAMFORGE_COMFYUI_BASEURL`、`DREAMFORGE_COMFYUI_CHECKPOINT` 环境变量。
- 设置对话框增加 ComfyUI 地址与 checkpoint；保存后若 ComfyUI 启用状态发生变化，会重建执行服务并切换 Job 列表。
- 顶栏状态改为显示文本模型与出图后端（ComfyUI checkpoint 或图像模型）。
- 表单关闭时释放执行宿主与后台轮询。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未连接真实 ComfyUI 实例，节点出图与任务区提交均未端到端验证。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 桌面端执行服务未接入 `SqliteJobStore`，Job 不持久化，重启后不恢复；Web 端仍使用 SQLite。
- 桌面端未接入回调签名校验（`HmacCallbackVerifier`）与 HTTP 回调入口，依赖轮询与 WebSocket 收敛状态。
- 节点出图尚未把 `steps`、`cfg`、`seed` 暴露到节点参数，走 WorkflowFactory 默认值。
- 重建执行服务时不会迁移正在运行的 Job，运行中任务会随旧宿主一起失效。
- 配置变更后仍需重新打开设置对话框保存才会生效，没有即时生效提示。

### 下一步

1. 为桌面端接入 Job 持久化，使任务记录跨重启保留。
2. 节点级图像参数（尺寸、步数、CFG、种子、负向提示词）写入 `Parameters` 并传递给工作流。
3. 规划资产目录与相对路径，支持画布跨机器迁移。
4. 增加“自动整理画布”操作。

## 2026-09-25 第二十四轮更新：节点级图像参数

### 已完成

- 新增 `ImageGenerationRequest`，把出图参数从零散字符串改为结构化请求：提示词、负向提示词、宽高、步数、CFG、种子。
- `IImageProvider.GenerateAsync` 改为接收 `ImageGenerationRequest`，三个实现同步更新。
- `ComfyUiImageProvider` 现在把 `steps`、`cfg`、`seed` 一并写入 Invocation 输入，交由 `ComfyUiWorkflowFactory` 使用；未填写的参数不传，走工作流默认值。
- `OpenAiCompatibleImageProvider` 使用请求中的宽高作为 `size`。
- 属性面板新增“图像参数”按钮，可设置：负向提示词、宽度、高度、采样步数、CFG、种子。
  - 宽高范围 64–2048；步数、CFG、种子留空表示使用服务默认或每次随机。
  - 输入非数字时提示且不保存。
- 参数保存到节点 `Parameters`（`negativePrompt`、`imageWidth`、`imageHeight`、`steps`、`cfg`、`seed`），随画布 JSON 一起持久化。
- 修复此前的读取断点：`GenerateSelectedImageAsync` 会读取这些参数，之前没有任何界面能写入它们。
- “设置”中的图像尺寸改为“节点未单独设置时的默认尺寸”，未设置尺寸的节点会回退到该值（默认 1024x1024）。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未连接真实 ComfyUI 或图像接口，参数是否真正生效未端到端验证。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 参数按节点保存，尚未支持“应用到同类节点”或项目级默认参数。
- 步数、CFG、种子只对 ComfyUI 链路生效；OpenAI 兼容图像接口只使用提示词与尺寸，界面已注明。
- 未做采样器、调度器等更细粒度的 ComfyUI 参数暴露。
- 尺寸范围被限制在 64–2048，超出会被夹紧且不提示。

### 下一步

1. 为桌面端接入 Job 持久化，使任务记录跨重启保留。
2. 规划资产目录与相对路径，支持画布跨机器迁移。
3. 增加“自动整理画布”操作。
4. 支持把图像参数应用到同类节点或设为项目默认值。

## 2026-09-25 第二十五轮更新：桌面端 Job 持久化

### 已完成

- `DesktopExecutionHost` 接入 `SqliteJobStore`，默认数据库位置为 `%LocalAppData%\DreamForge\jobs.db`。
  - 内存执行器与 ComfyUI 执行链路都会持久化 Job，任务记录可跨重启恢复。
  - 数据库初始化失败时回退为不持久化，并在任务区显示“任务记录未持久化（原因）”，不再静默失败。
- 新增 `DREAMFORGE_JOB_DB` 环境变量覆盖数据库路径，便于便携部署或受限环境。
- 宿主释放时一并释放 SQLite 连接（`IJobStore.Dispose`）。
- 重启恢复遵循 Host 既有语义：未完成的 Job 会被标记为 `Failed`（`HOST_RESTARTED`），不会静默重试。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- **实际启动桌面程序验证持久化**：把 `DREAMFORGE_JOB_DB` 指向可写路径后启动程序，`jobs.db` 被成功创建（20480 字节），验证后已删除该临时文件。
- 默认的 `%LocalAppData%` 路径在本次开发沙箱中被禁止写入，SQLite 返回“unable to open database file”；已确认这是环境限制而非代码缺陷，且回退路径工作正常、程序未崩溃。
- 未提交真实任务做“重启后任务记录仍在”的端到端验证（需要人工操作界面）。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 只持久化 Job 快照，不保存 Invocation 原始输入，无法从数据库重放某次出图。
- 数据库无容量控制与清理入口，历史 Job 会持续累积。
- 数据库路径若不可写会静默降级为内存模式（界面有提示，但没有引导用户改路径）。
- ComfyUI 重启后仍在执行的外部任务不会被重新接管。

### 下一步

1. 规划资产目录与相对路径，支持画布跨机器迁移。
2. 增加“自动整理画布”操作。
3. 支持把图像参数应用到同类节点或设为项目默认值。
4. 增加 Job 历史清理与容量控制。

## 2026-09-25 第二十六轮更新：资产可移植引用

### 已完成

- 新增 `AssetStore`：画布中保存可移植引用（`asset://文件名`），运行时解析为本机绝对路径。
  - `ToReference`：资产目录内的绝对路径转成引用；非本机根路径或目录外路径原样保留。
  - `Resolve` / `Exists`：把引用解析回本机路径，文件不存在时返回不可用。
  - `Normalize`：加载画布时把旧版本保存的绝对路径批量规范化为引用。
- 节点出图成功后写入的是引用而非绝对路径，生成历史中的图像输出同样保存引用。
- 画布缩略图按引用解析并缓存，换机器后只要资产文件在新机器的资产目录内即可正常显示。
- 生成历史对话框的“打开资产”与“回滚到此版本”改为按引用解析。
- 回滚空输出（例如失败的图像记录）时给出提示，不再把节点内容清空。
- 资产目录内的绝对路径与引用可双向还原，旧画布首次加载即完成迁移。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未在真实资产图片上验证跨机器迁移；本次沙箱禁止写入默认的 `%LocalData%`，无法端到端演练出图与画布保存。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 只保存引用，不会复制或打包资产文件；换机器仍需自行同步资产目录。
- 资产目录固定在 `%LocalAppData%\DreamForge\assets`，没有随画布迁移或自定义位置的入口。

## 2026-09-26 第二十七轮更新：按现有功能收敛 Canvas UI

### 已完成

- 核对桌面端现有真实功能：通用节点、节点端口与连线、附件、设定引用、AI 生成与生成历史、节点锁定、Agent 预览/提交/撤销、任务与出图、画布库和最近画布恢复。
- 移除 React Canvas 中自行设计的固定剧情节点、静态连线、虚构节点类型和未接通的“运行此节点”入口。
- React Canvas 改为协议驱动展示层：消费 `host/init.scene`、`host/scene.reset` 和 `host/op.batch`，展示真实记录、修订号和宿主能力状态；宿主未声明画布编辑能力时，节点详情保持只读。
- 扩展 `CanvasBridge` 的初始化、场景重置和宿主批次事件回调，不改变现有协议消息方向。

### 本轮验证

- `npm.cmd run build`：通过，TypeScript 检查和 Vite 构建均通过。
- `dotnet build DreamForge.Desktop\DreamForge.Desktop.csproj --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Agent.Tests\DreamForge.Agent.Tests.csproj --no-restore`：通过，38 项测试全部通过。

### 当前边界

- `DreamForge.Desktop` 当前使用 WinForms `WorkflowCanvasControl` 作为实际可编辑画布，React Canvas 没有接入桌面项目。
- `HostBridge` 当前仍未处理 `canvas/op.batch`，React Canvas 因此只读展示协议场景，不自行伪造编辑同步。
- React 展示层当前按协议记录的通用字段读取标题、内容和坐标；复杂的 C# 节点附件、引用和生成历史仍由桌面端属性面板展示。

### 下一步

1. 进行 WinForms 人工 UI 验收，重点核对现有节点画布、属性面板、画布库、Agent 和任务面板。
2. 若要让 React Canvas 成为实际编辑器，先补齐 Host 的 `canvas/op.batch` 与 C# `WorkflowCanvasState` 的双向映射，再接入编辑控件。
3. 不在协议和现有模型确认前增加固定剧情节点类型或新的业务操作。
- 文件名冲突或人工改名后引用会失效，界面显示“图片不可用”。
- 生成历史与 `AssetPaths` 中的引用不做去重，同一张图可能重复记录。
- 文本记录与图像记录仍靠“输出能否解析成本机文件”区分，属启发式判断。

### 下一步

1. 支持自定义资产目录，并提供随画布导出的资产打包。
2. 增加“自动整理画布”操作。
3. 支持把图像参数应用到同类节点或设为项目默认值。
4. 增加 Job 历史清理与容量控制。

## 2026-09-25 第二十七轮更新：自定义资产目录与画布导出/导入

### 已完成

- 资产目录统一收敛到 `AssetStore.Directory`：
  - 默认仍为 `%LocalAppData%\DreamForge\assets`。
  - 可在“设置”中指定，或用 `DREAMFORGE_ASSET_DIR` 环境变量覆盖。
  - `ImageProviderFactory.AssetsDirectory` 已移除，出图保存、ComfyUI 下载目录、缩略图解析统一使用该目录。
- 新增 `CanvasPackage`：把画布 JSON 与引用的图片资产一起导出/导入。
  - 导出：写入 `canvas.json` 与 `assets/`，画布内是 `asset://` 引用，包可直接拷贝到其他机器。
  - 导入：读取包内 `canvas.json`，把缺失的资产补入本机资产目录，再载入画布。
  - 导出/导入都会报告成功数量与缺失的资产数量，不做静默跳过。
- 画布工具栏新增“导出画布”“导入画布”。
- `RecentCanvasState` 提升为顶层类型，最近画布与资产包共用同一结构；`LoadRecentCanvas`/`SaveRecentCanvas` 改为复用 `ApplyCanvasState`/`BuildCanvasState`，去掉重复逻辑。
- 资产目录变化时会重建执行链路并清空缩略图缓存，避免继续显示旧目录的图片。
- `DesktopExecutionHost` 记录创建时的资产目录，用于判断配置是否需要重建。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- **实际启动程序验证路径覆盖**：设置 `DREAMFORGE_ASSET_DIR` 与 `DREAMFORGE_JOB_DB` 指向工作区路径后启动，`assets` 目录与 `jobs.db`（20480 字节）均被创建，验证后已删除该临时目录。
- 导出/导入需要人工选择目录，未做端到端演练。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 导出只复制画布引用的资产；被删除的历史资产不会被收集，包内可能缺少旧版本图片。
- 导入按文件名匹配资产，同名不同内容的文件会被本机已有文件占用（覆盖策略为“不覆盖”）。
- 导入会整体替换当前画布，没有多画布管理或撤销。
- 没有“最近画布列表”，仍只自动保存一个最近画布。
- 资产目录不做去重与清理，长期使用会持续膨胀。

### 下一步

1. 增加最近画布列表与多画布管理。
2. 增加“自动整理画布”操作。
3. 支持把图像参数应用到同类节点或设为项目默认值。
4. 增加 Job 历史与资产目录的清理入口。

## 2026-09-25 第二十八轮更新：画布自动整理

### 已完成

- `WorkflowCanvasControl` 新增 `AutoArrange`：按连线层级重新排布全部节点。
  - 列号取节点到起点的最长路径深度，列间距 60，行间距 40。
  - 同列节点按原纵向位置排序，纵向相同再按标题排序，结果稳定可复现。
  - 含图片资产的节点按加高后的实际高度留出行距，避免缩略图互相压住。
- 新增 `ComputeDepths`：以边为输入做有界松弛（最多 50 轮），存在环时也会终止，不会死循环。
- 画布工具栏新增“整理画布”。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；整理后的实际观感与示例模板的排布效果未人工确认。

### 当前边界

- 只按层级分列，没有做连线交叉最小化、节点居中或分支对称等美化。
- 无连线的孤立节点全部落在第一列，可能堆叠较长。
- 存在环状连线时深度松弛会推高列号，布局可能偏宽（不会崩溃，但没有专门处理环的可读性）。
- 整理是全画布操作，不能只整理选中节点或其子树。
- 整理会覆盖用户手工拖出的位置，没有撤销。

### 下一步

1. 增加最近画布列表与多画布管理。
2. 支持把图像参数应用到同类节点或设为项目默认值。
3. 增加 Job 历史与资产目录的清理入口。
4. 为整理操作增加撤销，或只整理选中子树。

## 2026-09-25 第二十九轮更新：AI 自动生成轮数

### 已完成

- 新增配置 `AutoGenerationRounds`：AI 自动展开下游节点的轮数，**默认 2 轮，设为 0 表示不限制**。
  - 可在“设置”中调整（0–20），或用 `DREAMFORGE_AUTO_ROUNDS` 环境变量覆盖。
- 属性面板新增“自动生成下游”：从选中节点开始逐轮展开。
  - 每轮对当前层每个节点调用 AI，采纳结果写入内容与生成历史，并按建议创建下游节点。
  - 下一轮以本轮新建的节点为输入，直到用尽轮数或不再有新的下游建议。
  - AI 主动提问或未返回内容的分支会被跳过并保留提问，等待人工补充，不会继续展开。
  - 同一父节点下已存在的「类型 + 标题」不会被重复创建，避免同一分支被反复生成。
  - 执行期间状态栏显示轮次与进度，并阻止并发重复触发。
  - 轮数设为 0 时，确认框会明确提示“会持续调用 AI 直到没有新的下游建议，可能产生较多调用与费用”。
- 轮数为 0 时结束条件依赖“不再有新建议”，本地模拟 Provider 会自然收敛（分镜 → 人物/场景 → 图像提示词后结束）。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；自动展开的真实节点数量与轮数行为未人工确认。
- 未使用真实大模型验证多轮展开；不限制轮数在真实模型下的收敛性取决于模型是否持续给出新建议。

### 当前边界

- “自动生成下游”是显式按钮，不会在“接受最新结果”时自动触发，避免未经确认连续调用 AI。
- 未限制轮数时没有节点总量上限，只有“同一父节点不重复创建”的去重保护。
- 展开过程只有状态栏文字反馈，没有进度条百分比，也不能中途取消。
- 自动采纳的结果会直接覆盖节点内容与状态，未逐条弹窗确认。
- 轮数是全局设置，不能按节点或按分支单独指定。

### 下一步

1. 为自动展开增加取消入口与总量上限提示。
2. 增加最近画布列表与多画布管理。
3. 支持把图像参数应用到同类节点或设为项目默认值。
4. 增加 Job 历史与资产目录的清理入口。

## 2026-09-25 第三十轮更新：自动展开的取消与上限保护

### 已完成

- “自动生成下游”按钮在运行期间切换为“停止自动生成”，点击即取消当前展开。
- 展开过程使用 `CancellationTokenSource`：每个节点开始前检查取消状态，进行中的 AI 调用也会收到取消令牌。
- 新增单次展开节点上限 `MaxAutoGenerationNodes`（500）：
  - 达到上限即停止，并在结果提示中说明“已达到单次上限”。
  - 创建子节点时按剩余额度截断，不会超限。
  - 确认框中提前说明该上限，不限制轮数时也会一并提示。
- 结果提示区分三种结束原因：用户停止、达到上限、正常完成，并统一附带已创建节点数。
- 展开期间禁止“AI 生成 / 继续”和“生成参考图”，避免状态互相干扰。
- 状态栏在展开期间显示“第 N 轮 x/y · 已创建 M”。
- 关闭窗口时会取消进行中的展开；窗口已释放时不再回写界面或弹提示。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；取消按钮、上限截断与关闭窗口时的取消行为均未人工确认。

### 当前边界

- 500 的上限是硬编码常量，界面上不可配置。
- 取消只对“尚未开始的节点”和“正在进行的 AI 调用”生效，已采纳的内容与已创建的节点会保留，不做回滚。
- 展开期间界面仍可操作画布与其它面板，只有两个 AI 入口被拦截。
- 没有进度百分比，也没有逐轮日志，结束原因只在最后弹一次提示。
- 上限与轮数是全局的，不能按节点或分支单独设置。

### 下一步

1. 把单次节点上限改为可配置项。
2. 增加最近画布列表与多画布管理。
3. 支持把图像参数应用到同类节点或设为默认值。
4. 增加 Job 历史与资产目录的清理入口。

## 2026-09-25 第三十一轮更新：画布库与多画布管理

### 已完成

- 新增 `CanvasLibrary`：画布以 JSON 文件存放在本机目录，支持列出、保存、载入、删除。
  - 目录默认 `%LocalAppData%\DreamForge\canvases`，可用 `DREAMFORGE_CANVAS_DIR` 覆盖。
  - 文件名由画布标题生成并做非法字符替换与长度截断。
  - 列出时读取每个文件的标题与修订号，按修改时间倒序返回。
- 侧边栏改造成画布库面板：
  - 列表显示画布标题与修订号，双击直接打开。
  - 按钮：新建、保存、打开、删除画布。
  - 其余入口（新建任务、任务队列、历史记录、图片资产、工作流模板、设置）保留。
- 保存/打开/删除行为：
  - 保存：把当前画布写入库；首次保存按标题命名，之后覆盖同一文件。
  - 打开：载入库中画布并标记为当前画布。
  - 删除：删除画布文件（不会删除图片资产），若删除的是当前画布会解除绑定。
- 当前画布与文件绑定：
  - 新建画布、导入资产包会解除绑定，避免误覆盖已有文件。
  - 关闭窗口时若已绑定文件，会把编辑结果写回该文件。
  - 未绑定文件的新画布仍只保留在此前的“最近画布草稿”中，行为与之前一致。
- 列表在保存、打开、删除、新建后都会刷新；当前画布对应项会被选中。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- **实际启动程序验证**：设置 `DREAMFORGE_CANVAS_DIR`、`DREAMFORGE_JOB_DB`、`DREAMFORGE_ASSET_DIR` 指向工作区后启动，程序正常运行且无异常输出，说明侧边栏改造与画布库初始化不会导致启动失败；验证后已清理临时目录。
- 保存、打开、删除画布这些需要点击界面的流程未端到端验证。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 没有重命名入口；改名只能通过新建画布后再保存，或直接改文件名。
- 画布标题目前不能在界面上编辑，首次保存的文件名由自动标题决定。
- 保存是整文件覆盖，没有版本历史与冲突检测；外部改动同一文件不会提示。
- 画布库与“最近画布草稿”是两套存储，未保存过的新画布不会出现在库里。
- 删除画布不会清理它引用的图片资产，资产可能残留。
- 列表为空时没有引导文案，只会显示空列表。

### 下一步

1. 增加画布重命名与标题编辑入口。
2. 支持把图像参数应用到同类节点或设为默认值。
3. 增加 Job 历史与资产目录的清理入口。
4. 让画布库与草稿统一，并为列表空状态增加引导。

## 2026-09-25 第三十二轮更新：引用感知的资产清理与存储管理

### 已完成

- 资产删除改为“引用感知 + 回收站”：
  - 有其它引用时不删文件：统计引用时同时覆盖当前画布、画布库全部画布与最近画布草稿，节点预览与生成历史都算引用。
  - 只有最后一个引用消失时才弹窗询问是否清理，并列出文件名。
  - 删除一律走 `Microsoft.VisualBasic.FileIO.FileSystem` 的 `RecycleOption.SendToRecycleBin`，移入回收站由用户自行清空，不做永久删除。
- 触发点：
  - 删除节点（`WorkflowCanvasControl.NodeDeleted` 事件）后检查该节点的图片资产。
  - 从画布库删除画布后检查该画布引用的图片资产。
- 新增 `StorageMaintenance`：统计各目录占用，并按引用清理资产。
  - `CountReferences` / `FindUnreferenced`：按文件名统计引用，跨画布生效。
  - `RecycleUnreferencedAssets`：只处理图片扩展名（png/jpg/jpeg/webp），非图片文件不动。
- 侧边栏“图片资产”改为“存储与清理”，打开存储对话框：
  - 列出画布库、图片资产、任务记录、最近画布草稿、配置文件的位置与占用（未创建的目录会标注）。
  - 可打开所选行所在目录。
  - “清理无引用图片”：二次确认后把无引用图片移入回收站，并报告数量与释放空间。
  - “清空任务记录”：通过新增的 `IJobStore.Clear()` 清空任务表，不影响画布与图片。
- Host 侧新增 `IJobStore.Clear()` 与 `SqliteJobStore.Clear()`（Web 端未受影响）。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；删除节点/删除画布后的询问、回收站移动、存储对话框清空任务记录均未人工验证。

### 当前边界

- 移入回收站依赖系统回收站可用；卷上不可回收时 Windows 可能直接删除，这是系统行为，程序无法完全保证。
- 引用统计以文件名为准，同名不同内容的文件会被视为同一资产。
- 只有“删除节点”和“删除画布”两个触发点；回滚版本、覆盖生成、导入画布等操作不会触发提醒，需要手动用“清理无引用图片”。
- “清空任务记录”只清数据库，已加载到内存的历史 Job 需重启后才从列表消失。
- 存储对话框不显示画布文件内部的资产引用关系，也不支持按单个文件选择删除。
- 引用统计在每次删除时都会读取全部画布文件，画布很多时会有额外的磁盘读取。

### 下一步

1. 增加画布重命名与标题编辑入口。
2. 支持把图像参数应用到同类节点或设为默认值。
3. 让存储对话框支持查看单个资产被哪些画布引用。
4. 让画布库与草稿统一，并为列表空状态增加引导。

## 2026-09-25 第三十三轮更新：画布重命名与标题编辑

### 已完成

- `CanvasLibrary.Rename`：修改画布标题并把文件同步重命名为标题，返回新路径；旧文件在改名成功后删除。
- 侧边栏新增“重命名”按钮（与“删除画布”同排）。
- 顶栏画布标题改为可双击重命名，光标提示可点击。
- 重命名行为分三种情况：
  - 未选中库项：只改当前画布标题（未保存画布仍写入最近画布草稿）。
  - 选中且是当前画布：改标题后先保存当前编辑内容，再重命名文件，并更新当前画布路径。
  - 选中但不是当前画布：读取该画布、改标题、写入新文件名并删除旧文件，当前编辑内容不受影响。
- 新增简易文本输入对话框（`ShowTextInput`），取消或留空视为放弃。
- 文件名继续经 `SanitizeFileName` 过滤非法字符，标题与文件名保持一致。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；重命名三种分支、文件改名与旧文件清理均未人工验证。

### 当前边界

- 改名用的标题与文件名一一对应：同名画布会被视为同一个文件，重名时不提示冲突，可能覆盖已有同名画布。
- 未选中库项时会修改当前画布标题，若此时当前画布是库中某画布，行为与“选中当前画布”一致；但如果选中的是别的画布，就只能改那个画布，无法只改当前标题。
- 旧文件删除发生在写入新文件之后，若删除失败会同时留下新旧两个文件（下次改名会再次尝试）。
- 没有重命名撤销。
- 画布标题改动不会同步到已导出的资产包。

### 下一步

1. 重命名时检测同名冲突并提示合并或覆盖。
2. 支持把图像参数应用到同类节点或设为默认值。
3. 让存储对话框支持查看单个资产被哪些画布引用。
4. 让画布库与草稿统一，并为列表空状态增加引导。

## 2026-09-25 第三十四轮更新：同名画布冲突保护

### 已完成

- `CanvasLibrary.FindByTitle`：按标题计算目标文件名并返回已存在的冲突画布路径，可用 `excludePath` 排除自身。
- `CanvasLibrary.Rename` 新增 `overwrite` 参数：目标已存在同名画布且未允许覆盖时抛出带明确提示的 `IOException`，不再静默覆盖。
- 重命名流程改为先检测冲突：冲突时弹窗说明“继续会覆盖该画布”，用户选择否则直接放弃，选择是才允许覆盖。
- 保存新画布（尚未绑定库文件）时同样检测同名冲突，避免按标题直接覆盖已有画布。
- 判定逻辑区分“同一个文件改名”与“覆盖另一个文件”：目标就是自身文件时不触发冲突提示。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；冲突提示与覆盖分支未人工验证。

### 当前边界

- 只提供“覆盖或放弃”两种选择，没有自动改名（如追加序号）或合并画布。
- 冲突判定以“标题生成的文件名”为准，标题不同但过滤非法字符后同名（例如仅特殊字符不同）仍会被视为冲突。
- 覆盖会直接替换目标画布文件内容，且没有备份或撤销。
- 同名检测与写入之间没有加锁，多实例同时保存同一文件仍可能互相覆盖。
- “导入画布”不写入画布库，因此不涉及该冲突检测。

### 下一步

1. 支持把图像参数应用到同类节点或设为默认值。
2. 让存储对话框支持查看单个资产被哪些画布引用。
3. 让画布库与草稿统一，并为列表空状态增加引导。
4. 为同名冲突提供自动改名选项。

## 2026-09-25 第三十五轮更新：图像参数批量应用与默认值

### 已完成

- “图像参数”对话框底部改为三个动作：
  - 保存到此节点：只写入当前节点。
  - 应用到同类节点：写入当前节点，并写入所有 Kind 相同的节点；执行前显示将影响多少个节点，无同类节点时给出提示。
  - 设为默认：把负向提示词、尺寸、步数、CFG 写入配置作为默认值，供未单独设置参数的节点使用。
- 配置新增 `DefaultNegativePrompt`、`DefaultImageSteps`、`DefaultImageCfg`；默认尺寸沿用已有的 `ImageSize`，避免重复字段。
- `BuildImageRequest` 的取值顺序明确为：节点参数 → 默认值 → 服务默认/内置兜底。
- 打开图像参数对话框时，未设置过的字段会显示默认值，便于在此基础上调整。
- 修复一处实际缺陷：在“设置”里保存会重建 `AiProviderConfig`，原先会清空默认图像参数；现在会保留这三项。
- 参数写入统一走 `ApplyImageParameterValues`，留空表示移除该参数并回落到默认值。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；批量应用、设为默认与设置对话框的字段保留均未人工验证。

### 当前边界

- “设为默认”不会批量回填到既有节点，已设置过参数的节点保持原值。
- 同类节点按 `Kind` 判定，无法只作用于选中的一部分节点（没有多选）。
- 批量应用没有撤销；覆盖后再想恢复需要重新设置。
- 默认值只覆盖负向提示词、尺寸、步数、CFG；种子仍按节点或随机，不做默认。
- 默认值保存在本机 `ai-config.json`，不随画布或资产包迁移。

### 下一步

1. 让存储对话框支持查看单个资产被哪些画布引用。
2. 让画布库与草稿统一，并为列表空状态增加引导。
3. 为同名冲突提供自动改名选项。
4. 支持节点多选，以便批量应用只作用于选中节点。

## 2026-09-25 第三十六轮更新：资产引用视图

### 已完成

- 存储对话框改为两个页签：
  - “存储位置”：原有各处路径与占用统计，以及打开目录、清理无引用图片、清空任务记录。
  - “图片资产”：逐张列出资产，含文件名、大小、引用数与“被哪些画布引用”。
- 新增 `StorageMaintenance.ListAssets` 与 `AssetReference`：扫描资产目录中的图片，按文件名统计每个画布的引用次数，并记录引用它的画布名称。
- 新增 `CollectNamedCanvases`：把画布库、最近画布草稿与当前画布带上名称一起参与统计，便于显示“被哪些画布引用”。
- 图片资产页支持：刷新、打开选中图片、把选中图片移入回收站。
  - 移入回收站前会按当前引用数给出不同提示：仍有引用时提示相关节点会显示图片不可用；无引用时说明可直接移入。
- 修正常见误判：统计单张图片引用数时原先会漏掉当前未保存画布，现已把当前画布一并纳入。
- 存储位置页的“清理无引用图片”与资产页共用同一套引用统计口径。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；资产列表内容、引用来源显示与单张回收站操作均未人工验证。

### 当前边界

- 资产列表每次打开或刷新都会读取全部画布文件，画布与资产很多时刷新偏慢，且没有缓存。
- 引用关系按文件名统计，同名不同内容会被算作同一资产。
- 资产页只能逐张移入回收站，没有多选批量操作，也没有按引用数排序之外的筛选。
- “被哪些画布引用”显示画布标题，标题相同的画布无法区分。
- 不显示资产被哪些具体节点引用，需要回画布中自行查找。

### 下一步

1. 让画布库与草稿统一，并为列表空状态增加引导。
2. 为同名冲突提供自动改名选项。
3. 支持节点多选，以便批量应用与批量操作。
4. 为资产列表增加缓存与按引用筛选。

## 2026-09-25 第三十七轮更新：画布绑定持久化与空状态引导

### 已完成

- 画布库与草稿的关系明确化，不再是两套互不相干的状态：
  - 画布只有“已绑定画布库文件”和“未保存”两种状态，顶栏标题在未绑定时会显示“（未保存）”。
  - 新增 `SetCurrentCanvasPath`，所有绑定变化都会同步写入工作区记录。
- 新增 `workspace.json`（`%LocalAppData%\DreamForge`）：记录当前画布路径，使绑定跨重启保留。
  - `CanvasLibrary.LoadCurrentCanvasPath` 会校验文件仍存在，文件被删除时返回 null。
- 启动恢复顺序明确为：绑定的画布库文件 → 最近画布草稿 → 画布库中最近修改的画布 → 空白画布。
- 画布库为空时显示引导文案（“点击新建开始，再用保存存入画布库”），列表与引导互斥显示，不再是一片空白。
- 标题更新统一走 `UpdateCanvasTitleLabel`，避免各处重复拼标题导致状态显示不一致。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- **实际启动程序验证**：把 `DREAMFORGE_CANVAS_DIR`、`DREAMFORGE_JOB_DB`、`DREAMFORGE_ASSET_DIR` 指向工作区后启动，程序正常运行、无异常输出，覆盖了启动恢复、画布库扫描与空状态分支；验证后已清理临时目录。
- 绑定持久化在本次沙箱中无法验证：`workspace.json` 位于 `%LocalAppData%`，该路径在当前环境被禁止写入，写入失败会被静默忽略。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 绑定与草稿仍是两个文件；草稿不记录绑定信息，绑定信息单独放在 `workspace.json`，三者不一致时以绑定文件为准（启动顺序已定义，但没有冲突提示）。
- `workspace.json` 写入失败时静默降级，用户不会知道绑定未被保存。
- 未保存画布仍只在关闭时写入草稿，运行中崩溃会丢失未保存改动。
- 空状态引导只在画布库为空时显示，没有提供“导入示例模板”等快捷入口。
- 画布标题与文件名强绑定，改标题即改文件名，不支持“标题与文件名分开”。

### 下一步

1. 为同名冲突提供自动改名选项。
2. 支持节点多选，以便批量应用与批量操作。
3. 为资产列表增加缓存与按引用筛选。
4. 把绑定信息并入画布草稿，减少一处状态文件。

## 2026-09-25 第三十八轮更新：任务详情视图

### 已完成

- 新增任务详情对话框，双击任务记录或点击侧边栏“任务详情”打开：
  - 展示状态、进度、外部任务 ID、幂等键、错误码与错误信息。
  - 列出该任务的输出资产（角色 + 输出引用），可打开本机可解析的产出图片。
  - 未完成的任务可在对话框中直接取消。
- 任务列表增加悬停提示：失败任务会显示错误信息，无需打开详情。
- 侧边栏原先“新建任务”这一无效占位入口改为“任务详情”，减少一处点开只会弹“后续版本开放”的死入口（新建任务的入口本来就是底部任务参数区）。
- 输出引用的解析复用 `AssetStore.Resolve`：`asset://` 指向本机资产可打开，本地模拟任务的 `local://` 引用会明确提示不可打开，不做静默失败。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过。
- 未进行人工 WinForms UI 操作验收；任务详情的打开产出、取消任务与悬停提示均未人工验证。
- 未提交真实任务做端到端验证；当前环境下提交任务需要人工点击界面。

### 当前边界

- 详情对话框不显示 Invocation 原始输入，因此看不出该任务当时用的提示词与参数（任务持久化只保存结果快照）。
- 输出列表只按角色与引用展示，没有缩略图预览。
- 取消按钮只对排队中或运行中的任务可用，已结束任务不提供重跑。
- 侧边栏“任务队列”“历史记录”仍是占位项，未与任务列表联动。
- 任务记录仍无清理单条记录的入口，只能整体清空。

### 下一步

1. 在任务详情中展示 Invocation 输入参数（需要同时扩展任务持久化）。
2. 为同名冲突提供自动改名选项。
3. 支持节点多选，以便批量应用与批量操作。
4. 让侧边栏“任务队列”“历史记录”与任务列表联动或移除。

## 2026-09-25 第三十九轮更新：任务输入参数持久化与展示

### 已完成

- 任务快照新增输入参数：`ExecutionResult.Inputs` 与 `Job.Inputs`，记录本次执行的 Invocation 输入。
- `SingleMachineExecutionService.StartAsync` 在创建 Job 时通过 `Job.AttachInputs` 写入输入快照；`ToResult` 与 `Job.Restore` 均带上该字段。
- SQLite 任务库新增 `inputs_json` 列：
  - 新建表包含该列，旧库通过既有的 `AddColumnIfMissing` 自动补列（默认 `{}`），不需要手工迁移。
  - 保存与读取都包含输入参数，重启后仍可查看。
- 桌面任务详情对话框新增“输入参数”列表，逐项显示参数名与值；参数值按类型转为可读文本（字符串原样、数字/布尔直接显示、对象取原始 JSON），过长值会截断。
- 没有记录输入的任务（旧记录或本地模拟任务）会明确显示“该任务没有记录输入参数”，不留空白。
- 该改动同时让 Web 端的任务记录也具备输入参数（Web 使用同一套 `IJobStore`）。
- 协议层未受影响：`HostBridge.SendJobUpdate` 仍只挑选固定字段构造消息，新增字段不会进入协议载荷。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，16 项测试全部通过（含 SQLite 持久化与恢复用例，旧库补列路径未被破坏）。
- **实际启动程序验证**：把 `DREAMFORGE_JOB_DB` 指向工作区后启动，`jobs.db`（20480 字节）成功建立，说明包含 `inputs_json` 的建表语句与补列逻辑正常执行；验证后已清理临时文件。
- 未提交真实任务验证输入参数的写入与回读（提交需要人工点击界面）。
- 未进行人工 WinForms UI 操作验收。

### 当前边界

- 输入参数是执行时刻的快照，之后修改节点参数不会回溯影响历史任务记录。
- 详情对话框只做只读展示，不能从历史任务的输入“重新发起一次”相同任务。
- 输入里的图片类参数（如参考图路径）只显示引用文本，没有预览。
- 旧数据库中被补列的记录输入为空，无法追溯。
- 输入快照会随任务记录增长，数据库体积相应变大，且仍无单条清理入口。

### 下一步

1. 支持从历史任务详情“按相同参数重新发起”。
2. 为同名冲突提供自动改名选项。
3. 支持节点多选，以便批量应用与批量操作。
4. 让侧边栏“任务队列”“历史记录”与任务列表联动或移除。

## 2026-09-25 第四十轮更新：按相同参数重新发起

### 已完成

- 任务快照补齐调用信息：`ExecutionResult` 与 `Job` 新增 `Tool`、`Capability`、`Channel`，与上一轮的 `Inputs` 一起构成可复现的调用快照。
- `Job.AttachInputs` 演化为 `Job.AttachInvocation`，一次性记录工具、能力、通道与输入参数；`StartAsync` 在入队前写入，`ToResult` 与 `Job.Restore` 均带上。
- SQLite 新增 `tool`、`capability`、`channel` 三列，建表包含且旧库自动补列（默认空值/0），已有记录仍可读取。
- 任务详情对话框：
  - 摘要行显示工具、能力与通道。
  - 新增“按相同参数重新发起”按钮，按记录中的工具/能力/通道与输入参数重新提交。
  - 重新发起使用**全新幂等键**，避免被幂等注册表判为重复而直接返回旧结果。
  - 旧记录缺少调用信息时按钮禁用，并给出明确说明。
- 新增回归测试“任务快照的调用信息与输入参数持久化”：写入含中文提示词与数值参数的 Invocation，换个 `SqliteJobStore` 实例重启恢复后逐项校验工具、能力、通道、参数数量与参数值。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，5 个项目，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-restore`：通过，**17 项测试全部通过**（新增用例真实覆盖了工具/能力/通道/输入参数的写入与回读）。
- 未进行人工 WinForms UI 操作验收；“按相同参数重新发起”按钮本身未端到端点击验证。
- 未连接真实 ComfyUI，重新发起后的执行结果未验证。

### 当前边界

- 重新发起是“照搬参数重跑”，没有对话框让用户在提交前修改参数。
- 重新发起不做环境校验：若 checkpoint、资产目录或 ComfyUI 地址已变化，仍按旧参数提交，失败信息由后端返回。
- 输入参数只保留值，不记录任务属于哪个节点，因此无法把重跑结果自动回写到原节点。
- 任务快照字段继续增多，数据库体积随之增长，仍无单条清理入口。
- 旧数据库中被补列的记录没有调用信息，无法重新发起。

### 下一步

1. 支持在重新发起前编辑参数，或把重跑结果回写到对应节点。
2. 为同名冲突提供自动改名选项。
3. 支持节点多选，以便批量应用与批量操作。
4. 让侧边栏“任务队列”“历史记录”与任务列表联动或移除。

## 2026-09-26 第四十一轮更新：节点去类型化与多附件

### 已完成

- 节点模型去类型化：删除 `NodeKind` 枚举与 `WorkflowNode.Kind` / `WorkflowNode.Type`，节点统一为「通用节点」，任何节点都可同时承载文本与任意数量的附件。
- 新增附件模型：`AttachmentKind { Image, Video, Audio, Other }` 与 `WorkflowAttachment`（`Id` / `Kind` / `Reference` / `Name` / `Source` / `AddedAt`），按扩展名自动判定媒体种类。
- 旧画布迁移：`WorkflowNode.LegacyAssetPaths`（JSON 名仍为 `AssetPaths`）在 `LoadState` 时一次性迁移为附件并清空，旧画布不丢图。
- 画布绘制：节点高度按是否含附件区分；缩略图优先取第一个图片附件，无图片时只标注媒体类型与文件名（不伪造视频/音频预览），多附件显示「共 N 个附件」；状态行显示附件摘要，例如「图片 2 · 视频 1」。
- 画布工作区工具栏新增「添加附件」：多选文件后复制进本机资产目录，以 `asset://` 引用挂到选中节点，失败项逐条报告。
- 节点检查器改造：移除「显示类型」输入框与节点类型下拉框，改为附件列表（类型 + 名称）与「添加附件 / 移除附件」按钮；双击列表项用系统默认程序打开附件。
- 移除附件走引用感知清理：文件不立即删除，只有不再被任何画布引用时才询问是否移入回收站。
- AI 侧同步去类型化：`AiGenerationRequest` 新增 `Instruction`，`AiNodeProposal` 只保留 `Title` / `Content`；本地启发式 Provider 改为「指令 + 内容」关键词判定，OpenAI 兼容 Provider 的提示词与 JSON 解析加入附件摘要且不再要求 kind。
- 引用收集统一改为遍历 `Attachments`：`AssetStore.Normalize`、`CanvasPackage.CollectReferences`（存储管理与资产引用视图因此自动覆盖视频/音频附件）。
- 「应用到同类节点」因失去类型语义，改为「应用到全部节点」（写入前确认节点数量）。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误（去类型化改造后首次编译通过）。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常；未进行人工 WinForms UI 点击验收。

### 当前边界

- 附件只是挂载与预览，尚未参与 AI 生成或执行参数（例如把附件作为参考图送进 img2img）。
- 视频与音频没有内嵌播放器，只能在系统默认程序中打开。
- 附件列表不可拖拽排序，"第一个图片附件"即缩略图来源。
- 移除附件不校验同节点内重复引用同一文件的情况，同一文件被两个节点引用时计数正常，但同节点重复添加会产生两条记录。
- `LegacyAssetPaths` 仅保留兼容读取，新画布不会再写入该字段。

### 下一步

1. 支持把附件作为图像生成的参考图（img2img / 参考图条目）。
2. 附件列表支持拖拽排序与设为缩略图。
3. 为同名冲突提供自动改名选项。
4. 支持节点多选，以便批量应用与批量操作。

## 2026-09-26 第四十二轮更新：图标栏 + 展开式抽屉的极简主界面

### 已完成

- 主界面去固定化：移除左侧 190px 画布库固定栏、右侧 260px 检查器固定栏、下方 250px 创建图像任务固定区，三处常驻面板全部取消。
- 新增左侧图标栏（56px，深色）：`库 / 节 / 任` 三个图标加底部 `设`，图标为单字配 ToolTip，选中态用底色与左侧高亮标识。
- 新增展开式抽屉：点击图标在画布左侧展开 356px 面板（再次点击同一图标或点面板右上角 `✕` 收起），展开时才占用画布空间，不展开时完全不占位。
- 面板重组为三个抽屉页：
  - **画布库**：画布列表 + 新建/保存/打开/重命名/删除/导入包/导出包/存储与清理。
  - **节点属性**：选中节点、附件列表与管理、内容来源、节点文本、AI 生成/接受结果/生成参考图/图像参数/生成历史/自动生成下游。
  - **任务与出图**：任务状态与取消、创建图像任务表单、任务记录列表；超长内容可滚动。
- 顶栏压缩为一行 44px 状态条（品牌、画布标题、修订号、Provider 状态），Provider 状态右对齐。
- 画布工作区工具栏精简为单行：`+ 节点 / 添加附件 / 示例模板 / 删除节点 / 整理画布` 与缩放比例；导入/导出画布并入画布库。
- 清理死代码：删除仅被旧固定布局使用的 `AddField` 与 `ConfigureNumber` 辅助方法。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**，本轮只改界面层，未触及 Core 与 Host。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常；未进行人工 WinForms 点击验收。

### 当前边界

- 抽屉为挤占式而非浮层覆盖，展开时画布会变窄，画布缩放与滚动位置不随宽度变化自动调整。
- 一次只能展开一个抽屉；选择节点不会自动展开"节点属性"，需要手动点图标。
- 图标栏使用单字占位，没有真正的矢量图标资源。
- 图像任务的 `txt2img / img2img` 类型下拉框在重组时移除（原下拉框不参与提交参数，仅作展示），提交仍固定走 `text-to-image`。
- 抽屉宽度固定 356px，不可拖拽调整。

### 下一步

1. 把附件作为图像生成的参考图（img2img / 参考图条目）。
2. 选中节点时自动展开"节点属性"（可选开关）。
3. 抽屉支持拖拽调宽并记住宽度。
4. 为图标栏换成真正的矢量图标资源。

## 2026-09-26 第四十三轮更新：项目级设定库与场景空间布局

### 已完成

- 新增设定库数据模型（`WorkflowEntities.cs`）：
  - `WorkflowEntity`：项目级实体，含 `Kind`（角色 / 场景 / 道具）、`Name`、`Aliases`、`Core`（跨章节不变的核心设定）与 `Variants`。
  - `WorkflowEntityVariant`：实体的一种表现（角色外观 / 场景状态 / 道具形制），含 `Name`、`Description`（相对核心的差异）、`Layout`、`Attachments`（该变体自己的参考图）。
  - 引用与出图的单位是「实体 + 变体」，同一实体可并存多种表现。
- **场景空间布局字段**（本轮重点）：
  - `SceneDirection` 枚举覆盖两套参照系：中 / 前 / 后 / 左 / 右（相对方位）与 东 / 南 / 西 / 北 / 四隅（绝对方位），另有上方 / 下方 / 外围 / 地下 / 其他，共 18 项。
  - `SceneLayoutItem`：`Direction` + `DirectionNote`（方位细化，如「右侧偏后」）+ `Element`（如「茅草屋」）+ `Note`（如「距大殿约五十步」）+ 可选的 `EntityId`（该元素本身是独立实体时记录引用）。
  - `SceneLayout`：`Overview` 整体布局概述 + 按方位排列的 `Items`，提供 `IsEmpty` 与 `ToPromptText()`。
  - `ToPromptText()` 拼装规则：概述成「整体布局：……」一行，元素逐条输出为「· 右侧（偏后）：茅草屋，距大殿约五十步」，空元素行自动跳过，避免出现残缺描述。空元素行不进入提示词。
- 挂载与持久化：`WorkflowCanvasState` 新增 `Entities`，随画布 JSON 一起保存与读取；`LoadState` 同步实体，`Clear()` 清空设定库，`ApplyTemplate()` 改为只清节点与连线（`ClearGraph`），避免示例模板误删设定库。
- 资产链路同步：`AssetStore.Normalize` 与 `CanvasPackage.CollectReferences` 覆盖变体附件，因此存储管理、资产引用统计与导出包都能识别变体参考图。
- 界面：图标栏新增「定」（设定库），抽屉页含实体列表与变体列表；变体列表的「空间布局」列显示「未填写」或「N 处元素」。
  - 新建角色 / 场景 / 道具，编辑实体（种类 / 名称 / 别名 / 核心设定）。
  - 新建 / 编辑 / 删除变体；每个实体至少保留一个变体。
  - 场景变体的编辑对话框提供「空间布局概述」与「布局元素」表格（方位 / 方位细化 / 元素 / 补充说明），支持添加、编辑、删除、上移、下移，并实时显示提示词预览。
  - 布局元素使用独立的编辑对话框，方位用下拉选择，可选方位细化与补充说明。
- 设定库任何改动都会触发画布 `NotifyContentChanged()`，修订号自增并标记待保存。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**（Core 与 Host 未改动）。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常；设定库抽屉在启动时即完成构建，说明控件树与列定义无误。
- 未尽验证：`DreamForge.Core.Tests` 目标框架为 `net10.0`，无法引用 WinForms 的 `DreamForge.Desktop`，因此场景布局的提示词拼装与画布 JSON 往返**未纳入自动化测试**，仅经编译与启动验证。

### 当前边界

- 设定库尚未与画布节点打通：节点还没有「引用实体 + 变体」的能力，设定库目前是独立的资料库。
- 实体没有版本历史，改动会直接覆盖当前内容；上一轮讨论的「显式提交版本 + 锁定引用 + 定稿冻结」尚未实现。
- 变体的参考图字段已在模型与资产链路中就绪，但界面还没有添加 / 生成参考图的入口。
- 布局元素的 `EntityId` 字段已预留，界面暂未提供把它指向另一个实体的操作。
- `SceneDirection` 等枚举按数值序列化（与既有 `ContentSource` / `NodeExecutionStatus` 一致），画布 JSON 里不是可读字面量。
- 把实体从角色改为场景时会为其全部变体补上空布局，但从场景改回角色不会清除已有布局数据。

### 下一步

1. 让画布节点可以引用「实体 + 变体」，并在节点卡片上显示名称与参考图。
2. 为实体引入版本历史：显式提交版本、引用锁定版本、章节定稿冻结与一键升级。
3. 给变体接上参考图的添加与图像生成入口。
4. 章节折叠分镜 + 子节点网格布局 + 镜号。

## 2026-09-26 第四十四轮更新：节点引用设定（实体 + 变体）

### 已完成

- 节点模型扩展：`WorkflowNode` 新增 `EntityId` 与 `VariantId`，节点可以指向设定库里的某个实体的某个变体；两者均为可空，未引用时行为与之前完全一致。
- 引用解析集中在 `WorkflowCanvasState`：
  - `ResolveReference(node)` 返回「实体 + 变体」；变体被删除时退回实体的第一个变体，实体也被删除时返回 null，节点按未引用处理，不会抛异常。
  - `ReferenceLabel(node)` 生成卡片标签，例如「小明 · 少年黑衣」。
  - `DescribeReferenceForPrompt(node)` 拼出提示词片段：种类与名称、核心设定、当前表现，场景变体再附空间布局。
- 画布绘制：
  - 节点卡片第二行优先显示引用（`◆ 小明 · 少年黑衣`），再补附件概况；无引用时维持原样（附件摘要或「纯文本节点」）。
  - 缩略图改为**优先使用引用变体的参考图**，节点自带附件作为兜底；来自设定库的图会标注「设定参考图」。
  - 节点高度改为按「自带附件或引用变体有参考图」判断，引用带图时卡片自动加高。
- 节点属性面板新增「引用设定」区块：显示当前引用，提供「引用设定」（实体与变体两级选择对话框，默认定位到已引用项）与「清除引用」。清除只断开引用，节点的文本与附件保留。
- 设定库面板的变体按钮新增「引用到画布」：把选中变体直接变成一个引用节点，便于第二章复制第一章的人物。
- 删除实体会提示有多少节点正在引用它；删除后这些节点失去引用但自身内容保留。
- 提示词链路带上设定：
  - `AiGenerationRequest` 新增 `Reference`，`OpenAiCompatibleProvider` 在提示词中加入「该节点引用的设定（必须遵守，不要改动其中的设定细节）」，逐行缩进陈述。
  - 出图提示词统一由 `ComposeNodePrompt` 生成：节点文本（或标题）+ 引用设定描述；`BuildImageRequest` 与「生成参考图」的提示词一致。
  - 出图记录（`Parameters["imagePrompt"]` 与生成历史的 `Input`）改为记录真正发给接口的完整提示词，便于复现。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**（本轮未触及 Core 与 Host）。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常。
- 未尽验证：引用选择对话框、引用后卡片变化与提示词拼接均为人工点击路径，未做自动化测试，也未做人工点击验收。

### 当前边界

- 节点引用的是「当前内容」，没有版本概念：改设定库里的人物描述，所有引用节点会立刻跟着变，包括已完成章节里的节点。
- 设定库的参考图还没有上传或生成入口，因此「引用变体后在卡片上看到参考图」目前只在变体已有图片时成立。
- 「生成参考图」的出图结果仍写入**节点**的附件，不会回写到变体的参考图；引用同一变体的多个节点会各自持有一份图。
- 引用节点不会自动带上实体核心与变体差异去参与 AI 生成的**节点建议**（proposals），只影响当前节点内容与出图。
- 一个节点只能引用一个「实体 + 变体」，一个镜头里出现多个角色时需要建多个节点；尚无用一张图同时约束多角色的能力。
- 引用不参与画布自动整理的分组逻辑，引用节点与普通节点在布局上没有区别。

### 下一步

1. 给变体接上参考图的添加与图像生成入口，并支持把节点出图结果写回变体。
2. 为实体引入版本历史：显式提交版本、引用锁定版本、章节定稿冻结与一键升级。
3. 支持一个节点引用多个「实体 + 变体」（一个镜头多角色），并在出图时合并各自参考图。
4. 章节折叠分镜 + 子节点网格布局 + 镜号。

## 2026-09-26 第四十五轮更新：版本层、变体参考图、图生图、技能与插件

本轮把「版本层、变体参考图入口、ImageToImage、技能层、插件宿主与三视图用例」一次性做完，
数据模型一次收敛到位，避免画布格式反复变更。

### 一、版本层（实体版本历史与引用锁定）

- 模型：新增 `EntityVariantVersion`（版本号、提交说明、差异描述、空间布局、该版本自己的参考图、提交时间）。
- `WorkflowEntityVariant` 改为「当前内容 + 版本历史」：
  - 当前内容仍是 `Description` / `Layout` / `Attachments`，可自由编辑；
  - `CurrentVersion` 记录最后一次提交的版本号，`Versions` 保存不可变快照；
  - `Fingerprint()` 与 `CommittedFingerprint` 用于判断是否存在未提交改动；
  - `Commit(note)` 深拷贝当前内容生成新版本（避免后续编辑污染历史），`EnsureInitialVersion()` 保证旧数据与新建变体都有 v1 落点。
- 引用：`WorkflowNode` 新增 `VariantVersionId`——留空表示跟随变体当前内容，非空表示锁定到该版本快照。
- 解析：`WorkflowCanvasState.ResolveReference` 统一返回 `ReferenceContent`（实体、变体、版本、生效的描述/布局/参考图），并派生 `IsLocked` 与 `VersionLabel`。版本被删除时回落到变体当前内容，实体被删除时按未引用处理。
- 节点保护：`WorkflowNode.IsLocked` 用于「定稿」——锁定后应用节点属性、改引用、清引用、加减附件、AI 生成、出图一律被拦下并提示先解锁。
- 界面：
  - 变体编辑改为「内容 / 参考图 / 版本」三页签；版本页显示当前版本与是否有未提交改动，提供「提交新版本」（可填说明）与版本历史列表（版本号 / 时间 / 说明 / 参考图数）+ 版本内容详情。
  - 引用对话框扩展为三列「实体 / 变体 / 版本」，版本列表首项是「跟随当前内容」，其余为按版本号倒序的已提交版本；新建引用默认锁定到最新版本。
  - 节点属性面板显示 `◆ 小明 · 少年黑衣 · v3`，提供「引用设定 / 清除引用 / 升级到最新」；升级前展示目标版本的说明与内容摘要。
  - 设定库变体列表新增「版本」列（有未提交改动时显示「v3 有改动」）；「引用到画布」创建的节点默认锁定最新版本。
- 资产链路：`EntityAssets.AllAttachments` 统一枚举变体当前内容与**所有历史版本**的附件，`AssetStore.Normalize` 与 `CanvasPackage.CollectReferences` 都改用它——历史版本的参考图不会被误清理。

### 二、变体参考图入口

- 变体编辑新增「参考图」页：添加图片（多选，复制进资产目录）、移除、打开；提示提交版本后才会固化。
- 节点出图成功后，若节点引用了设定，会询问是否把这张图一并加入该变体的当前参考图（提示需提交版本固化）。

### 三、ImageToImage 与图生图

- `Capability` 新增 `ImageToImage = 103`。
- `ImageGenerationRequest` 新增 `ReferenceImagePath` 与 `Denoise`。
- Host：`ComfyUiWorkflowFactory` 在带 `referenceImage` 时生成图生图工作流（`LoadImage` → `VAEEncode` → `KSampler`，`denoise` 可调，默认 0.6），否则保持原文生图结构；`ComfyUiExecutor` 会先把本机参考图上传到 ComfyUI 的 `upload/image`，再把 `referenceImage` 输入替换为 ComfyUI 侧文件名。
- Desktop：ComfyUI 链路按是否有参考图选择 `image-to-image` / `text-to-image` 与对应能力；OpenAI 兼容链路在有参考图时走 `/images/edits`（multipart），否则维持 `/images/generations`。
- 节点级控制：「图像参数」新增「有参考图时走图生图」开关与「重绘强度」，写入 `useReference` / `denoise` 参数。
- 出图链路统一：节点出图提示词由 `ComposeNodePrompt` 生成（节点文本 + 引用设定描述），图生图底图取引用变体生效版本的参考图。

### 四、技能层（只用大模型产出新资源）

- 模型：`SkillDefinition`（id / 名称 / 说明 / 版本 / 适用实体种类 / 产出目标 / 步骤）与 `SkillStep`（能力、提示词模板、负向提示词、参考图来源、重绘强度、尺寸、产出名）。
- 装载：`SkillLibrary` 从技能目录读取 JSON，逐文件容错（缺 id、无步骤、JSON 非法都只记错误并跳过）；目录为空时写入内置「人物一键三视图」示例。
- 执行：`SkillRunner` 按步骤顺序执行，任一步失败即中止且**不写回任何产出**，避免半成品混进参考图；`variant` 作参考图来源时取变体当前参考图，其余按前置步骤 id 取该步骤产出；提示词占位符 `{kind} {name} {variant} {core} {description} {layout}` 由 `SkillTemplates` 替换，并折掉因占位符为空产生的空行。
- 界面：设定库变体按钮新增「运行技能」，技能选择对话框展示技能说明与逐步骤的能力/底图来源；运行时有进度窗，结束后统一提示，成功后刷新画布与设定库。

### 五、插件宿主与三个扩展点

- 契约：`IDreamForgePlugin`（Id / Name / Version / Register）与 `IPluginHost`（右键菜单、左侧图标、注册窗口与打开窗口、运行技能、只读访问当前画布、请求刷新）。
- 边界：插件只挂界面入口，内容生产一律通过 `RunSkillAsync` 交给技能；插件不直接改写画布数据。
- 装载：`PluginLoader` 扫描插件目录下的一级子目录，读取 `plugin.json` + 程序集，用可卸载的 `AssemblyLoadContext` 加载，宿主与框架程序集回落到默认上下文以保证接口类型一致；`apiVersion` 不匹配直接拒绝加载。
- 失败隔离：单个插件的清单错误、加载异常、注册异常都只记录到该插件条目，宿主与其它插件继续运行；插件面板会显示错误原因。
- 扩展点：
  - 右键菜单：`ContextTarget` 覆盖画布节点 / 画布空白 / 实体 / 变体 / 变体参考图；实体列表、变体列表、画布都接了菜单，变体列表额外提供「参考图 → 插件项 → 具体图片」两级子菜单。
  - 左侧图标栏：插件注册的图标在启动时并入图标栏，面板按需创建（插件代码在宿主就绪后才执行）。
  - 窗口：插件可注册窗口，由插件面板的「打开窗口」入口打开。
  - 画布新增 `HitTestNode`，右键会先选中光标下的节点，保证菜单作用于正确目标。
- 界面：图标栏新增「插」，插件面板列出插件、版本、状态、插件/技能目录、已注册窗口与菜单，并提供打开目录、重新加载（右键菜单即时生效，图标栏需重启）、打开窗口。

### 六、三视图用例

- 内置插件 `ThreeViewMenuPlugin` 把「人物一键三视图」挂到变体与变体参考图的右键菜单上（`用这张图生成三视图`）。
- 内置技能 `character-three-view`：正面立绘文生图 → 侧面、背面各以正面图为底图的图生图（denoise 0.55），三步全部成功才写回变体参考图。
- 目录覆盖：技能与插件目录支持 `DREAMFORGE_SKILL_DIR` / `DREAMFORGE_PLUGIN_DIR`，与既有的画布/资产/任务库覆盖保持一致。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**（含改动过的 ComfyUI 工作流用例）。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常；把技能目录指向该临时目录后，确认 `character-three-view.json`（1608 字节）被正确生成，说明技能装载与目录创建链路可用。
- 发现的环境限制：本机沙箱禁止写入 `%LocalAppData%`，因此默认技能/插件目录在沙箱内不可用；程序对此已做容错（目录不可写只是没有技能），并补齐了环境变量覆盖。
- 未尽验证：技能出图、图生图、版本提交与引用升级、插件右键菜单均为人工点击路径，**未做端到端点击验收**；图生图需要真实 ComfyUI 或支持 `/images/edits` 的服务，当前环境无法验证。

### 当前边界

- 版本只能提交与查看，不能删除或回滚当前内容到历史版本；也没有版本之间的差异对比视图。
- 「章节定稿」目前是节点级的 `IsLocked`，没有章节实体，也没有「整章一键锁定/冻结引用」的批量操作。
- 升级引用只展示目标版本的说明与描述摘要，不做逐条差异比对，也不支持批量把多个节点一起升级。
- 技能只能调用出图能力（TextToImage / ImageToImage）；文本类能力、视频类能力、`ImageUpscale` 尚未接入执行器。
- 技能步骤之间只能传递图片（底图），不能传递文本；没有条件分支、循环或失败重试。
- 技能产出只会写进变体当前内容或触发节点附件，写回后仍需用户手动提交版本。
- 插件是进程内任意代码，没有沙箱；只应安装可信插件。宿主只提供受控 API，但插件仍可通过 `System.Windows.Forms` 直接改界面。
- 插件注册的左侧图标需要重启才能出现；「重新加载」只对右键菜单与窗口生效。
- 内置三视图插件是代码内置的（非 dll），用于演示扩展点用法；外部 dll 插件机制已实现但**未做真实 dll 的加载验证**。
- `DREAMFORGE_SKILL_DIR` / `DREAMFORGE_PLUGIN_DIR` 是本轮新增，其它文档尚未同步。

### 下一步

1. 版本对比与回滚：展示两个版本的差异，支持把当前内容回滚到指定版本。
2. 章节级定稿：引入章节实体或分组，支持整章一键锁定与批量升级引用。
3. 一个节点引用多个「实体 + 变体」，出图时合并多张参考图（一个镜头多角色）。
4. 技能能力扩展：接入文本类能力（分镜拆分、角色抽取）与视频类能力。
5. 章节折叠分镜 + 子节点网格布局 + 镜号。

## 2026-09-26 第四十六轮更新：版本对比与回滚、一个节点引用多个设定

### 一、版本对比与回滚

- 新增 `VersionDifference` 与 `VersionDiff.CompareToCurrent(version, current)`：把已提交版本与变体当前内容逐项对比，覆盖差异描述、布局概述、布局元素（按「方位 + 元素」为键判断新增/移除，键相同则比较补充说明）、参考图（按文件名增删）。
- `WorkflowEntityVariant.RollbackTo(version)`：把当前内容替换为该版本快照（深拷贝），不改动版本历史；回滚后当前内容会显示为「有未提交改动」，需要时再提交新版本。
- 界面（变体编辑的「版本」页）：
  - 选中任一版本，详情区除版本内容外，直接列出**该版本与当前内容的逐项差异**（超过 12 项折叠提示）。
  - 新增「回滚到此版本」按钮：回滚前弹确认框展示差异摘要；当前内容与最后一次提交一致时会提示无需回滚；回滚后内容页控件同步刷新。

### 二、一个节点引用多个「实体 + 变体」

- 模型：新增 `NodeReference`（实体 + 变体 + 可选版本），`WorkflowNode.References` 改为引用列表；原先的三个单引用字段改为 `Legacy*` 并用 `JsonPropertyName` 映射旧名，`LoadState` 时一次性迁移进列表后清空，旧画布不丢引用。
- 解析：`WorkflowCanvasState` 新增 `ResolveReferences`（跳过失效引用）与 `ResolveReferencePairs`（保留与原始引用项的对应关系，用于展示「哪一条引用失效了」）。
- 提示词：`DescribeReferenceForPrompt` 会依次拼接每一条引用的种类与名称、核心设定、生效版本的差异描述与场景布局，因此一个镜头可以同时带多个角色与场景的设定。
- 节点属性面板：引用区改为多行列出全部引用；按钮改为「添加引用」（三列实体/变体/版本选择，默认最新版本，重复引用会被拦下）与「管理引用」。
- 引用管理对话框：逐条显示引用、版本与状态（跟随当前内容 / 可升级到 vN / 已是最新 / 实体或变体已删除），可查看单条引用的生效内容，支持「升级到最新」（选中项，未选中则全部可升级项）、「移除选中」与「全部清除」。
- 出图写回：出图后如果节点引用了设定，单条引用直接询问是否写入，多条引用则列出让用户选择目标变体，避免把图写到错误的角色上。
- 图生图底图：取第一条带图片的引用；**多条引用只使用第一张**，多角色合成参考图需要在参考图合成链路里另行处理。
- 设定库删除实体时的提示改为按引用条数统计，文案改为「这些引用会失效」。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常。
- 未尽验证：版本对比与回滚、多引用添加与管理均为人工点击路径，未做端到端点击验收；旧画布的单引用迁移逻辑经过编译与启动验证，但**未用真实旧画布文件验证迁移结果**。

### 当前边界

- 版本差异比较是逐字段的，不做行内字符级 diff；布局元素以「方位 + 元素」为键，改名会被识别成「移除 + 新增」而不是「修改」。
- 回滚后必须手动提交新版本才能固化，也不会自动把引用升级到新版本。
- 引用升级不展示差异，只按状态判断；「升级到最新」不区分「只想升其中一个」的场景（可通过选中行来限定）。
- 多引用只影响提示词与卡片显示：图生图仍只用第一张参考图，没有多角色参考图合成。
- 节点卡片上的引用摘要超过一条时只显示首条并标注总数，完整列表要看节点属性面板。
- 引用顺序不可调整（按添加顺序），也没有「哪条是主角色」的概念。

### 下一步

1. 多角色参考图合成：把多条引用的参考图一起送进图生图链路（IPAdapter 或多图输入）。
2. 章节级定稿：引入章节实体或分组，支持整章一键锁定与批量升级引用。
3. 引用排序与主角标记。
4. 技能能力扩展：接入文本类能力（分镜拆分、角色抽取）与视频类能力。
5. 章节折叠分镜 + 子节点网格布局 + 镜号。

## 2026-09-26 第四十七轮更新：修正参考图的分层错误（画布不再限制图片数量）

### 问题

上一轮把「图生图只支持单张底图」这个**执行能力限制写进了画布层**：`ImageGenerationRequest`
只有一个 `ReferenceImagePath` 字符串，画布在收集参考图时就 `.FirstOrDefault()` 截断成一张，
并把「多条引用只使用第一张」写成了画布边界。这是分层错误——能收几张图是 ComfyUI 工作流
或图像接口的能力问题，不该由画布决定。更严重的是，将来接入 IPAdapter 之类的多图工作流时，
又得回头改画布与请求层。

### 修正后的分层

| 层 | 职责 |
|---|---|
| 画布 / 领域 | 这个镜头引用了哪些设定、哪个版本；`References` 本来就是列表，不设上限 |
| 提示词 | 把每条引用转成文字，无数量限制 |
| 请求（`ImageGenerationRequest`） | 携带**全部**参考图路径，不设上限 |
| 执行方（ComfyUI 工作流 / 图像接口） | 决定实际用几张、怎么用；用不了的数量必须**显式回报**，不得静默丢弃 |

### 具体改动

- `ImageGenerationRequest.ReferenceImagePath`（单个字符串）改为 `ReferenceImages`（路径列表），
  新增 `HasReferenceImages`；参考图数量彻底离开画布层的决策范围。
- `ImageGenerationResult` 新增 `ReferenceNote`：执行方对参考图的实际使用说明。用不了全部参考图时
  必须在这里说明，而不是静默丢弃。
- 画布层：`ResolveReferenceImagePath` 改为 `ResolveReferenceImagePaths`，按引用顺序收集
  **全部**参考图（去重），不再截断。
- ComfyUI 链路：
  - `ComfyUiImageProvider` 把整个列表放进 `referenceImages`，并额外传 `referenceMode`（当前固定 `img2img`）；
    返回值里附带使用说明（多图时说明「只用了第 1 张」）。
  - `ComfyUiExecutor` 改为上传**全部**参考图到 `upload/image`，再把列表整体替换成 ComfyUI 侧文件名；
    仍兼容旧的 `referenceImage` 单图输入。
  - `ComfyUiWorkflowFactory` 改为读列表（`GetStringList`，兼容单图字符串），并按 `referenceMode` 分支：
    当前只实现 `img2img`（取列表第一张），未知模式**直接报错**而不是静默降级成文生图。
    注释里标明了新增多图模板（IPAdapter 等）时只需在此加分支，**画布、请求与执行器都不用改**。
- OpenAI 兼容链路：多张参考图按 `image[]` 重复提交，并在结果里说明「能否全部生效取决于所用模型」。
- 技能层：`SkillStep.ReferenceFrom` 支持逗号分隔的多个来源（例如 `"front,variant"`），
  `SkillRunner` 收集成列表交给请求层，不再只取一张。
- 记录与提示：节点参数新增 `imageReferenceCount`（本次用了几张）与 `imageReferenceNote`（执行方的使用说明）；
  出图成功但有使用说明时会弹提示，避免用户以为多张图都生效了。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**
  （ComfyUI 工作流用例覆盖了单图与文生图路径）。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常。
- 未尽验证：多图路径（ComfyUI 多图上传、`/images/edits` 的 `image[]`）**没有真实后端可验证**；
  多图时「只用了第 1 张」的回报逻辑也未经端到端点击。

### 当前边界

- ComfyUI 侧目前只有「单张底图」的 `img2img` 模板，因此**多角色合成仍然做不到**；
  差别在于现在这是**工作流模板的缺口**，补模板时画布、请求、执行器、技能都不需要动。
- 多图参考的最终效果取决于后端：ComfyUI 需要新模板，OpenAI 兼容接口取决于所选模型。
- 参考图顺序仍然是引用顺序，没有「哪张是主底图」的显式指定；img2img 模板固定取第一张。

### 下一步

1. 增加 ComfyUI 多图参考工作流模板（IPAdapter 或多 ControlNet 输入），复用现有 `referenceImages` 列表。
2. 参考图顺序与主底图标记（让用户指定哪张作为 img2img 底图）。
3. 章节级定稿：引入章节实体或分组，支持整章一键锁定与批量升级引用。
4. 技能能力扩展：接入文本类能力（分镜拆分、角色抽取）与视频类能力。
5. 章节折叠分镜 + 子节点网格布局 + 镜号。

## 2026-09-26 第四十八轮更新：参考图能力声明与分步合成（含 AI 规划）

### 一、把「能吃几张」变成可声明的能力

- 新增 `ReferenceCapacity(MaxImages, Description)`：`MaxImages` 为 0 表示不限，`CanUseMultiple` 表示是否具备分步合成的前提（至少能同时吃两张）。
- `IImageProvider` 新增 `ReferenceCapacity`：
  - ComfyUI 由 **工作流模板** 决定，新增 `ComfyUiReferenceModes` 目录声明（当前只有 `img2img`，上限 1 张）。
  - OpenAI 兼容接口由配置声明，新增 `ImageMaxReferenceImages`（默认 1，可在设置里调，也支持 `DREAMFORGE_IMAGE_MAX_REFS`），因为能吃几张取决于所用模型，无法自动探测。
- 新增模板时只需在 `ComfyUiReferenceModes` 加一条声明 + 在 `ComfyUiWorkflowFactory` 加一个 `referenceMode` 分支，**画布、请求、执行器、技能都不用改**。

### 二、分步合成：把 N 张参考图逐步并成一张底图

- 技能引用来源扩展为三类（可用逗号组合）：`variant`、**`ref:N`（目标上下文第 N 张参考图）**、前置步骤 id。`ref:N` 的下标按「引用顺序展开每条引用的图片」计算，一条引用有多张图就占多个下标。
- `SkillDefinition.OutputTarget` 扩展为 `variant` / `node` / **`node-base`**：`node-base` 会把最后一步的产出写入节点附件并标记 `WorkflowAttachment.SourceComposition`（合成底图）。
- 出图时 `ResolveReferenceImagePaths` 优先返回合成底图（此时其余参考图已并入其中），否则按引用顺序返回全部参考图。
- 技能作用域支持节点：`SkillTarget` 增加 `Canvas` 并把 `Entity`/`Variant` 改为可空，节点级技能不再要求变体。
- 新内置技能 `reference-merge-two.json`（`outputTarget: node-base`，`referenceFrom: "ref:0,ref:1"`），演示最小两两合成；技能目录已有文件时只补缺失的内置示例，不动用户文件。

### 三、超限预检（提交前告知，不静默）

- 出图前比对「引用带来的参考图数量」与链路能力，超限时弹出三选一：**先分步合成 / 只用前 N 张 / 取消**。
- 当链路一次只能吃 1 张时，明确告知「分步合成也用不上——第二张图传不进去，要合成请先换成支持多图的工作流模板」，而不是给一个假的选择。

### 四、AI 规划 + 可选择的规划弹窗

- 新增 `ICompositionPlanner.PlanCompositionAsync`：规划只产出**编排**（步骤、输入、提示词、备选策略），不产出内容。`OpenAiCompatibleProvider` 让大模型按 `ref:N` 约定返回 JSON；`LocalAiProvider` 与 `LocalCompositionPlanner` 提供离线兜底（按引用顺序两两收敛），模型返回不可用时也会退回兜底而不是卡住。
- 规划请求会把「一次最多几张」作为硬性约束告诉模型，并要求每步不超过该上限、只表达一个并入关系。
- 规划弹窗：展示方案说明与逐步的输入/提示词；可选**备选策略**或**直接输入要求**后重新规划；也可逐条修改步骤提示词；确认后执行。
- 执行复用 `SkillRunner`（把方案转成一个临时技能），沿用「任一步失败不写回任何产出」的规则。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行；确认技能目录里新增了 `reference-merge-two.json`（750 字节），且原有的 `character-three-view.json` 未被覆盖，说明「只补缺失内置示例」的逻辑生效。
- 离线兜底规划的输出经手工推演符合预期：3 张参考图、上限 2 张 → 步骤 s1 = `ref:0,ref:1`，步骤 s2 = `s1,ref:2`。
- 未尽验证：分步合成的实际出图、超限预检弹窗、AI 规划弹窗均为人工点击路径，**未做端到端点击验收**；AI 规划需要真实大模型接口才能验证 JSON 解析质量。

### 当前边界

- 当前唯一的 ComfyUI 模板上限是 1 张，`CanUseMultiple` 为 false，因此**分步合成在这条链路上实际不可用**；接入支持多图的模板后无需改代码即可启用。
- `ref:N` 的下标是「按引用顺序展开的图片序号」，不是「引用序号」；一条引用有多张图时会占用多个下标，界面上没有直观展示这个序号。
- 合成中间图会全部保留在节点附件里（只有最后一步标记为底图），长期使用会累积文件，暂时没有自动清理。
- 引用顺序仍不可调整（按添加顺序），因此「优先保留哪张」只能靠添加顺序控制。
- 规划弹窗只支持改步骤提示词，不能改步骤的输入组成（要改输入得靠「直接输入要求」重新规划）。
- 合成方案不保存：本次规划的结果不会写入画布，下次出图需要重新规划。
- 分步合成每次都要过一遍模型，步数多时耗时与额度消耗明显高于单次出图。

### 下一步

1. ComfyUI 多图参考模板（IPAdapter 等），让 `CanUseMultiple` 变成 true，真正启用分步合成。
2. 引用排序与主底图标记，让「截断」和「优先并入」可控。
3. 合成方案随节点保存，支持复用与再次编辑。
4. 章节级定稿：引入章节实体或分组，支持整章一键锁定与批量升级引用。
5. 章节折叠分镜 + 子节点网格布局 + 镜号。

## 2026-09-26 第四十九轮更新：提交方式探测与链路自检

### 为什么先做这个

上一轮把「多图模板」列成了下一步，但直接写模板是错的顺序：
**ComfyUI 的多图参考不是一种固定协议**，取决于这台机器装了哪些自定义节点
（IPAdapter / ControlNet / 图像合并），而且每个节点的类名与必填输入名在不同版本间会变；
视频链路更是另一类提交方式（几乎都是「提交任务 + 轮询」而非同步返回）。
照文档猜节点名写出来的工作流 JSON，很可能在真实 ComfyUI 上直接跑不起来。

所以本轮先解决「怎么确定提交方式」，而不是猜着写模板。

### 一、把提交方式变成显式声明

- 新增 `SubmissionKind`（`SyncImage` / `AsyncImage` / `AsyncVideo`）与 `SubmissionProfile`
  （Id、名称、能力、提交种类、参考图上限、是否需要先上传、说明）。
- 原先的 `ComfyUiReferenceMode` 升级为 `SubmissionProfile`：现在声明里同时表达了
  「能吃几张」与「同步还是异步」，视频模板将来直接复用同一套执行链路（Host 的
  外部任务轮询机制本来就在用）。
- 当前唯一已实现的声明仍是 `img2img`（异步提交、上限 1 张、参考图需先上传）。
  新增模板时的动作固定为两步：在 `ComfyUiSubmissionProfiles` 加声明 +
  在 `ComfyUiWorkflowFactory` 加同 Id 分支，画布 / 请求 / 执行器 / 技能都不动。

### 二、链路自检（真正去问后端，而不是猜）

- 新增 `BackendProbe.cs`：
  - `ComfyUiProbe` 调用 **`/object_info`**，这是「ComfyUI 支持接受什么」的唯一权威来源。
    它会报告基础出图节点、单图底图节点、IPAdapter 系列、ControlNet 系列、图像合并节点、
    视频编码与视频生成节点是否安装，并对**已安装的节点列出必填输入名**——这些输入名就是
    写模板时要填的提交字段。
  - `OpenAiCompatibleProbe` 调用 `/models` 列出可用模型，并按关键字标出疑似图像 / 视频模型；
    同时回显当前声明的参考图上限。说明里明确指出「参考图字段名随实现而异
    （`image` / `image[]` / `images`），能吃几张取决于模型、无法探测」。
- 界面：设置对话框新增「链路自检（探测提交方式）」按钮，结果窗口按「后端 · 分类 / 项目 / 状态 /
  说明·提交字段」列出全部探测项，并在下方汇总当前已实现的提交方式声明与下一步动作，
  支持一键复制结论，便于据此编写模板。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常。
- **未验证（重要）**：本环境既没有 ComfyUI 也没有可用的图像/视频接口，因此
  `/object_info` 与 `/models` 的解析逻辑**没有对真实后端跑过**；只能保证不可达时的降级路径
  （返回「无法连接 + 原因」而不是崩溃）符合预期。
- 结论：**多图模板与视频模板本轮没有编写**，这是刻意的——按本轮结论，应当先拿到
  真实后端的探测结果，再按实际节点名与输入名写模板。

### 当前边界

- 探测结果不会保存，每次自检都要重新联网查询。
- ComfyUI 探测只检查预置的节点候选名单，装了名单之外的多图/视频节点（例如新版 Flux Kontext、
  Qwen-Image-Edit 类节点）不会被识别为「多图参考」。
- `/object_info` 只给出节点的输入**schema**，不告诉你「哪种组合能真正做出多图参考」——
  那仍然需要人来判断与试跑。
- OpenAI 兼容探测只能列出模型名，无法确认多图参考是否真的生效，也无法判断视频接口的
  具体轮询协议。
- 视频链路仍然完全没有实现：`Capability.TextToVideo` / `ImageToVideo` 至今没有执行侧实现，
  `SubmissionKind.AsyncVideo` 只是声明位。

### 下一步

1. **需要真实后端信息才能继续**：确认目标是本地 ComfyUI（装了哪些自定义节点）还是某个视频/图像 API，
   跑一次「链路自检」拿到真实节点名与输入名。
2. 按探测结果编写第一个多图参考模板（IPAdapter 或 ControlNet 分支），让 `CanUseMultiple` 为 true。
3. 按探测结果编写第一个视频提交方式（`AsyncVideo`），复用现有外部任务轮询链路。
4. 引用排序与主底图标记。
5. 章节折叠分镜 + 子节点网格布局 + 镜号。

## 2026-09-26 第五十轮更新：接入大模型引导与 Agent 对话框

### 问题

此前未配置模型时会**静默降级**成 `LocalAiProvider`（关键词启发式），
界面上只有顶栏一行淡灰小字提示，用户很容易以为自己在用大模型，实际拿到的是占位结果。
这一轮把「接入大模型」变成启动时必须做的一次明确选择。

### 一、启动接入引导（不允许静默降级）

- `AiProviderConfig` 新增两个字段：
  - `ProviderChoiceMade`：用户是否做过选择；未做过时启动弹出接入引导。
  - `UseLocalProvider`：显式选择本地模拟；为 true 时即使填了地址也不走真实接口。
- `AiProviderFactory.Create` 尊重 `UseLocalProvider`。
- `MainForm` 在 `Shown` 时检查：没做过选择就弹 `AiSetupDialog`（首次运行版文案）；
  用户直接关掉窗口时明确提示「AI 功能会使用本地模拟的占位结果，随时可以重新接入」并给出两个入口。
- 顶栏的模型状态改为可点击（提示「点击重新接入大模型」），设置对话框底部也加了「接入引导」按钮。
- 顶栏状态文案改为「AI：本地模拟（未接入大模型，点击接入）」，不再让占位状态看起来像正常状态。

### 二、服务商预设与测试连接

- 新增 `ProviderPreset`：DeepSeek / 月之暗面 / 通义千问 / 智谱 / 硅基流动 / OpenAI / 本地 Ollama / 自定义，以及「本地模拟」。
  选中预设自动回填接口地址与推荐模型，用户只需补 API 密钥；所有字段仍可手改（模型名会随服务商更新）。
- 新增 `OpenAiCompatibleProvider.TestConnectionAsync`：用一次极小的 `ping` 请求验证地址、密钥与模型，
  接入引导里点「测试连接」即可确认，避免保存了一份填错的配置却以为接好了。
- 接入引导本身写「保存并继续」或「先用本地模拟」，后者要二次确认并说明后果。

### 三、Agent 对话框（多轮对话）

- 新增 `IAiChatProvider`：与 `IAiProvider.GenerateAsync` 的区别是**不强制 JSON 输出**，
  允许自然语言回答，用于讨论与打磨；结构化生成仍走 `GenerateAsync`（配对不同的系统提示）。
- `OpenAiCompatibleProvider.ChatAsync` 直接把消息转发给 `/chat/completions`；
  `LocalAiProvider.ChatAsync` **不伪装**，明确回答「当前是本地模拟，无法真正回答」并指出接入入口。
- 新增 `AgentDialog`：对话记录区（区分角色配色）、可勾选「带上当前画布上下文」（选中节点内容 + 设定库摘要）、
  多轮历史、Ctrl+Enter 发送、「采纳到选中节点」（写入节点内容并记入生成历史）、清空对话。
- 图标栏新增「聊」入口打开该对话框。

### 顺带修掉的真实缺陷

- **`AiProviderSettings.Save` 之前不处理写入失败**：目录不可写时会抛出未捕获异常，
  点「保存」直接崩掉。现在返回 bool 并在接入引导里明确告知「本次选择只在当前会话生效」。
- **配置重建丢字段**：图像参数的「设为默认」会重建 `AiProviderConfig`，
  此前漏掉了 `ImageMaxReferenceImages`（上一轮新增的字段），本轮补上，同时保留新增的两个接入字段。
  这是同一类坑第二次出现（第一次是丢失默认图像参数），因此这次把新字段一起列进去核对。
- 配置文件路径新增 `DREAMFORGE_CONFIG` 覆盖，与其它 `DREAMFORGE_*` 保持一致。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：先写入一份 `ProviderChoiceMade: true` 的配置并指向 `DREAMFORGE_CONFIG`，
  启动后程序正常运行，且配置文件未被程序改写——说明配置路径覆盖与读取链路生效、跳过引导的分支不会崩。
- **未验证**：接入引导的实际弹出与「测试连接」、Agent 对话的收发、采纳到节点均为人工点击路径，
  **未做端到端点击验收**；对话功能需要真实大模型接口才能验证返回质量。

### 当前边界

- 服务商预设里的地址与模型名会随服务商更新而过期；模型名填错时「测试连接」会立刻报错，但需要用户自己判断。
- 接入引导只覆盖文本模型；图像接口与 ComfyUI 仍在「设置」里单独维护，两处入口没有统一。
- 「测试连接」只验证 `/chat/completions` 可用，不验证图像接口或 ComfyUI。
- Agent 对话的上下文只有「选中节点内容 + 设定库名称摘要」，不含画布结构、生成历史与任务记录。
- 对话历史只存在内存里，关闭对话框即丢失，也不能导出。
- 「采纳到节点」是整段覆盖节点内容，没有「追加 / 插入 / 只采纳片段」的选项。
- Agent 对话框没有流式输出，长回答要等完整返回；也没有取消按钮。

### 下一步

1. Agent 对话支持流式输出与取消。
2. 上下文扩展：画布结构、生成历史、任务记录。
3. 对话历史持久化与导出；采纳时支持追加/插入。
4. 统一接入入口（文本模型 + 图像接口 + ComfyUI 一处配置）。
5. ComfyUI 多图参考模板（需要先跑「链路自检」拿到真实节点名）。

## 2026-09-26 第五十一轮更新：Agent 改为内嵌面板，子窗体层级统一

### 一、Agent 从独立窗口改为右侧内嵌面板

- 上一轮把 Agent 做成了独立 `Form`（`ShowDialog`）。改成内嵌面板（与画布并排的侧栏形态）：
  删除 `AgentDialog`，新增 `AgentPane`（`Panel` 子类），放进主窗口右侧新增的 `agentDrawer`
  （`Dock = DockStyle.Right`，宽 400）。
- 布局顺序：`workspace(Fill) → agentDrawer(Right) → drawer(Left) → header(Top)`。
  **与画布并排而不是覆盖在画布上**，因此不存在「被画布遮挡」或「挡住画布」的问题；
  左右两个抽屉可以同时打开（左侧设定/属性，右侧 Agent）。
- 交互与其他图标一致：图标栏「聊」改为切换面板（带选中态），面板右上角 `✕` 收起。
- Provider 改为**按需获取**（`Func<IAiChatProvider?>`）：用户中途重新接入模型后，
  面板不必重建就能用上新模型；状态行会跟着刷新。
- 面板内容做了窄栏适配：对话区、工具行（带上下文 / 采纳到节点 / 清空，可换行）、
  输入框、状态行、发送按钮纵向排列。

### 二、子窗体层级统一（不再被画布或主窗口遮挡）

- 新增两个统一入口：
  - `ShowOwnedForm(form)`：非模态子窗体统一 `Show(this)` + `BringToFront()` + `Activate()`。
  - `ShowWaitingForm(form, owner)`：等待/进度小窗同样显式前置。
- 应用到此前会被主窗口盖住的调用点：插件窗口 `OpenWindow`、「链路自检」的探测等待窗、
  技能运行的进度窗。
- 模态设置对话框维持 `ShowDialog(owner)`，owner 一律为当前活动窗体（主窗口或发起它的对话框），
  如「链路自检」从设置对话框打开时以设置对话框为 owner。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常。
- **未验证**：Agent 面板的展开/收起、发送与采纳、以及各设置窗口的实际层级表现均为人工点击路径，
  **未做端到端点击验收**。

### 当前边界

- Agent 面板宽度固定 400，不可拖拽调整；窗口变窄时与左侧抽屉同时打开会把画布压得很小。
- 面板的展开状态不持久化，重启后默认收起。
- Agent 面板与左侧抽屉是两套容器，两边的选中态各自独立，没有「同时只开一个」的约束。
- 内嵌面板的层级问题在 WinForms 里靠 Dock 并排解决（不重叠）；如果将来某个面板改成覆盖式浮层，
  仍需显式 `BringToFront()`。
- Agent 仍无流式输出与取消，上下文仍只有选中节点 + 设定库摘要。

### 下一步

1. Agent 面板支持拖拽调宽并记住宽度与展开状态。
2. Agent 对话支持流式输出与取消。
3. 上下文扩展：画布结构、生成历史、任务记录。
4. 统一接入入口（文本模型 + 图像接口 + ComfyUI 一处配置）。
5. ComfyUI 多图参考模板（需要先跑「链路自检」拿到真实节点名）。

## 2026-09-26 第五十二轮更新：Agent 审批机制与工作文件夹

### 一、Agent 面板默认展开

启动时直接展开右侧 Agent 面板（不再藏在图标后面）。面板的展开/收起仍由图标栏「聊」切换。

### 二、审批机制：模型不能直接改画布

这是本轮的核心。Agent 现在可以改动画布，但**只能提议，不能直接执行**：

- 新增 `AgentAction`（操作提议）与 `AgentReply`（自然语言文本 + 提议列表）；
  支持的操作：`create_node` / `update_node` / `delete_node` /
  `create_entity` / `update_entity` / `delete_entity` / `write_file`。
- 新增 `AgentActionParser`：从模型回复末尾解析 `{"actions":[...]}` JSON 块，
  **解析成功后把该块从展示文本里剥离**；解析失败则原样展示，不猜测结构。
- 新增 `AgentActionExecutor`：只负责把**已批准**的操作应用到数据层。
  节点匹配支持短 id（Guid 前 8 位）或标题；**同名时视为歧义并拒绝**，而不是随便挑一个。
- 审批 UI：操作以复选框列表出现在对话区下方（操作 / 理由两列），
  支持「应用选中」「全部应用」「忽略」；应用后把结果（成功数、失败原因）写回对话区。
- **操作协议放在上下文里而不是 Provider 的系统提示里**：协议依赖画布语义
  （节点短 id、实体种类），放进 Provider 会让它不再是通用组件。

### 三、审批前预检：不盲批

新增 `AgentActionExecutor.Precheck`（只读，不修改任何数据），把后果并进审批列表的理由列：

- 写文件时目标**已存在** → 「该文件已存在，应用后会覆盖原内容」
- 目标是**已锁定节点** → 「节点已锁定，应用会被拒绝」
- 删除节点带附件 → 「删除「X」会同时移除 N 个附件」
- 删除实体带参考图、新建同名实体、找不到目标、路径越界 → 各自给出提示

### 四、工作文件夹

- `AiProviderConfig.AgentWorkspace` 持久化；面板顶部一行显示路径并可点击选择（`FolderBrowserDialog`）。
- 目录内的**顶层文件清单会作为上下文**提供给模型，让它知道有哪些参考资料。
- **路径越界一律拒绝**：`write_file` 的相对路径经 `Path.GetFullPath` 规范化后
  必须落在工作文件夹之下，否则直接失败——防止模型用 `..` 逃逸到任意位置写文件。

### 五、锁定保护对 Agent 生效（一致性修复）

节点锁定（定稿保护）此前只在界面入口做检查。若 Agent 走执行器改节点，
就绕过了这道保护。现在 `update_node` / `delete_node` 在执行器里同样检查 `IsLocked`，
**锁定由数据层强制**，不只是 UI 层提示。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
  （过程中修掉一个真实警告：`AgentPane.Refresh()` 隐藏了 `Control.Refresh()`，已改名 `RefreshState`。）
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常。
- **未验证**：动作解析、审批勾选与应用、工作文件夹选择、文件写入与越界拒绝
  全部是**依赖真实大模型的交互路径**，本轮未做端到端点击验收。
  解析器与执行器的逻辑没有单元测试覆盖。

### 当前边界

- **解析依赖模型遵守格式**：模型不按约定输出 JSON 时不会产生提议，且用户看不出"本可以提议但没提"，
  属于静默失败。需要真实模型验证提示词是否稳定生效。
- **Agent 操作不可撤销**：没有撤销栈。删除节点会走既有的引用感知回收（询问是否回收资产），
  但"改错内容"只能手动改回。
- 文件写入**没有大小上限**，覆盖已有文件只靠预检提示，用户确认后直接覆盖，不生成备份。
- 工作文件夹是**软约束**：只限制 `write_file` 的落点；模型本身没有文件系统访问能力，
  所以不存在"读别的目录"的问题，但也就无法让 Agent 读取工作文件夹里的文件内容（只给了文件名清单）。
- 一次回复里的提议全部走同一张审批表，没有"记住这个选择"（每次都问）。
- 实体删除不检查"被节点引用"的情况，删除后引用会显示为失效引用（既有行为，非本轮引入）。

### 下一步

1. 给 `AgentActionParser` 与 `AgentActionExecutor` 补单元测试（不依赖模型，可离线验证）。
2. Agent 操作的撤销（至少保留最近一次批次的逆向操作）。
3. 让 Agent 能读取工作文件夹里的文件内容（带大小上限）。
4. Agent 面板支持拖拽调宽并记住宽度与展开状态。
5. Agent 对话支持流式输出与取消。

## 2026-09-26 第五十三轮更新：待提交变更集、审批两段式与撤销

本轮把第五十二轮的「审批即执行」改成更严格的一层：**批准 ≠ 生效**。思路是「先决定、再执行」
（批准前先看 diff），但比它更保守——**用户显式保存之前根本不进画布**。

完整链路：`提议 → 预检 → 审批（逐条勾选）→ 待提交变更集 → 画布虚影预览 → 提交并保存 → 撤销`。

### 一、待提交变更集（新增 `PendingChanges.cs`）

- `PendingChanges`：已批准、尚未生效的操作列表。**清空它等于丢弃这批改动，不留痕迹**
  （因为它们从未写入画布）。支持整批加入、单条移除、全部丢弃。
- `CanvasPreview`：待提交改动在当前画布上的**差异投影**——
  将新增节点、将删除节点 id、将修改节点 id、新增连线、将删除连线。
  它只是差异描述，**不参与命中测试，也不改变任何数据**。
- `CanvasPreviewBuilder.Build`：把操作试算到一份**画布副本**（JSON 往返深拷贝）上，
  再与当前画布逐节点/逐连线比对得出差异。试算用 `FileWriteMode.Skip`，
  **既不触碰真实画布，也不写任何文件**。
- `AgentCommitRecord`：一次提交的记录，用于撤销。

### 二、写文件改为可控（`AgentActions.cs`）

`AgentActionExecutor.Apply` 增加 `FileWriteMode`（`Apply` / `Skip`）与文件快照收集：

- `Skip`：只做路径校验，不落盘——供预览试算使用。
- `Apply`：**写入前先记 `FileSnapshot`（路径 + 原内容，文件原本不存在则记 null）**，供撤销还原。

### 三、画布虚影（`WorkflowCanvasControl.cs`）

新增 `SetPreview(CanvasPreview?)`，在正常绘制之后叠加一层虚线投影：

- 将删除：覆白 + 红色虚线 + 「将删除」
- 将修改：强调色虚线 + 「将修改」
- 将新增：半透明虚影 + 「将新增」，新增连线用虚线箭头

### 四、审批与待提交两段式（`AgentPane.cs`）

面板从单一审批模式改为两个阶段（`PaneMode.Approve` / `PaneMode.Pending`）：

- **审批阶段**：勾选列表 + 「加入待提交」/「忽略」，提示文案明确写「批准后进入待提交，保存画布时才生效」。
- **待提交阶段**：「提交并保存」/「全部丢弃」/「移除选中」，提示「画布上是虚影，保存时才写入；提交后可在画布工具栏撤销」。
- 上下文里新增「待提交改动」一节，让模型知道**哪些改动已被批准但尚未生效**，避免重复提议。
- `ClearConversation` 不再影响待提交——**待提交属于画布状态而非对话状态**。

### 五、撤销：整体快照而不是逆操作

- 提交前记录 `SnapshotBefore`（含标题、修订号、出图参数与整张画布）+ 被覆盖文件的原内容。
- **前提条件是 `canvasRevision == RevisionAtCommit + 1`**：提交之后画布若又被改动过，
  回滚会连带回退用户自己的编辑，因此**直接停用撤销**并给出原因（宁可不给，也不给会误伤的回退）。
- 撤销后清空 `lastAgentCommit`，不提供二次撤销。

### 六、未保存不丢改动与入口可达性

- **工具栏常驻待提交指示**：`● N 项 Agent 改动待提交（保存画布后生效）`，点击直接打开 Agent 面板。
- **撤销入口放在常驻工具栏**，而不是 Agent 面板的待提交视图里：
  待提交列表在提交后会清空，若把撤销放在那里，**提交完成的那一刻撤销反而消失了**。
  工具栏按钮按可用性置灰并用悬浮提示说明原因（无提交记录 / 提交后画布又有改动）。
- **关闭窗口保护**：有待提交改动时，`FormClosing` 三选一——「提交并保存」/「丢弃」/「取消」，
  不允许悄悄关掉丢掉一整批改动。
- 用户点「保存画布」时会先提交待提交集，并在提示里说明「含 N 条 Agent 改动」。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：指向 `%TEMP%\dreamforge-demo` 启动桌面端，程序正常运行无异常。
- **未验证**：审批勾选 → 待提交 → 虚影 → 提交 → 撤销这条链路是**依赖真实大模型的交互路径**
  （本地模拟 Provider 不产出 `actions`），本轮未做端到端点击验收。
  `CanvasPreviewBuilder` 与 `PendingChanges` 位于 WinForms 项目（`net10.0-windows`），
  现有 `net10.0` 测试工程无法引用，**暂无单元测试覆盖**。

### 当前边界

- 虚影是**投影而非真实数据**：命中测试、框选、删除都看不到它；
  用户此时手动改画布，虚影会按旧布局显示（提交时以实际数据为准，失配的操作会被执行器拒绝并报错）。
- 撤销只有**一级**，且一旦提交后画布再有其它改动就停用——这是刻意保守，不是缺功能。
- `write_file` 的撤销只能恢复**文件内容**：若 Agent 新建的是目录，撤销不会删除空目录。
- 待提交集**只存在于内存**：程序崩溃或强杀进程会丢掉这批改动（画布本身不受影响）。

### 下一步

1. 给 `AgentActionParser`、`AgentActionExecutor`、`CanvasPreviewBuilder` 补单元测试
   （需先把纯逻辑从 WinForms 项目里分出来，或让测试工程能引用它）。
2. Agent 操作的撤销扩展到多级（保留最近若干次提交的逆向能力）。
3. 让 Agent 能读取工作文件夹里的文件内容（带大小上限）。
4. Agent 面板支持拖拽调宽并记住宽度与展开状态。
5. Agent 对话支持流式输出与取消。

## 2026-09-26 第五十四轮更新：预设模型版本、自定义接口设置与上下文窗口

### 起因：预设里的模型 ID 已经全部过期

核对各家官方文档后发现，第五十二轮留下的预设**没有一条还能用**：

| 预设项 | 现状 |
| --- | --- |
| `deepseek-chat` / `deepseek-reasoner` | 2026-07-24 已停用，现为 `deepseek-flash`（V4.1-Flash）与 `deepseek-v4-pro` |
| `moonshot-v1-8k` | `moonshot-v1` 系列与 `kimi-k2.5` 已于 2026-08-31 下线，现为 `kimi-k3` / `kimi-k2.7-code` / `kimi-k2.6` |
| `glm-4-flash` | 非当前主力，现为 `glm-5.3`（1M 上下文 / 128K 输出） |
| `qwen-plus` | 现为 `qwen3.8-max` / `qwen3.7-plus` / `qwen3.8-flash` 等 |

**修掉一个真 bug**：Kimi 系列把 `temperature` / `top_p` **固定在服务端，显式传入会直接报错**，
而上一轮每次请求都带 `temperature` —— Kimi 预设从接上那天起就用不了。
新增 `SendSamplingParameters` 开关，Kimi 预设默认关闭。

### 一、预设服务商带模型版本列表（`AiChat.cs`）

- 新增 `ProviderModel(Id, Note, ContextWindow, MaxOutputTokens)`：一个预设模型版本。
- `ProviderPreset` 增加 `Format`（接口格式）与 `Models`（版本列表），并标明是否发送采样参数。
- **上下文与最大输出只填已核实的官方数值，拿不到就留 0 表示未声明**——
  宁可让用户按文档自己填，也不要写一个看起来权威的错数字。
- **模型 ID 始终允许手填**：预设列表只是起点，服务商下线旧模型后不必等程序更新。
  硅基流动与 OpenAI 因此只给出已核实/仅作参考的少量条目，并在提示里说明原因。

### 二、配置项扩展（`AiProviderSettings.cs`）

新增 `ApiFormat`、`UseFullUrl`、`DisplayName`、`ContextWindow`、`MaxOutputTokens`、`SendSamplingParameters`，
并提供 `RequestUrl`：基础地址按格式补 `/chat/completions` 或 `/v1/messages`，打开「完整 URL」则原样使用。

自定义配置取这套组合：**API 格式 + 完整 URL 开关 + 模型 ID + 模型展示名称 + 上下文窗口**。

### 三、Provider 支持两种协议（`OpenAiCompatibleProvider.cs`）

- 把原先散在四处、几乎重复的请求代码合并为一个 `SendAsync`：
  报文、鉴权头与响应解析都在这里按格式分支，生成 / 对话 / 规划 / 连接测试共用。
- **Anthropic Messages**：`x-api-key` + `anthropic-version: 2023-06-01`；
  `system` 提到顶层（Anthropic 的 `messages` 不接受 system 角色）；`max_tokens` 是必填项，未配置时给 4096。
- `ExtractText` 同时识别 `choices[0].message.content` 与 Anthropic 的 `content` 文本块（跳过思考块）。
- 「测试连接」从静态方法改为实例方法，并回报**实际 POST 的地址**，拼错路径时一眼能看出来。

### 四、上下文窗口真正生效（`AgentPane.cs`）

`ContextCharacterBudget = 窗口 × 1.5 ÷ 4`（上限 30 万字符）：按中文「1 token ≈ 1.5 字符」粗估，
并只把窗口的四分之一留给画布上下文，其余留给用户输入、历史轮次与模型回复。
**只截画布上下文，操作协议永远保留**——协议被截掉比上下文不全严重得多。窗口留 0（未声明）时不截断。

### 五、接入引导界面

`服务商 → 模型版本（可选可手填）→ 接口地址 + 「完整 URL」开关 + 实际请求地址预览 → API 密钥
→ 高级配置（接口格式 / 模型展示名称 / 上下文窗口 / 最大输出 / 发送采样参数）→ 测试连接`。
第一次打开默认选中列表第一家，不会停在一个空的「自定义」表单上。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `dotnet run --project DreamForge.Core.Tests/DreamForge.Core.Tests.csproj --no-build`：**17 项测试全部通过**。
- **实际启动程序验证**：接入引导弹出正常，预设与模型列表按预期回填。
- **未验证**：两种协议的请求报文**都没有用真实密钥跑过**——
  OpenAI Chat 分支是既有行为的延续，Anthropic 分支完全按文档实现，未经验证。

### 当前边界

- **只实现两种接口格式**。第三方客户端高级配置里的「模型系列」「工具调用轮次」「支持多模态」没有照搬：
  本项目目前没有工具调用与图片输入，做了就是填了不生效的死开关。
- **OpenAI 的型号名来自第三方客户端的内置模型列表推断**，未经 OpenAI 官方文档核实，因此标注为仅供参考。
- **硅基流动只预置 1 条已核实 ID**（`zai-org/GLM-5.3`），其余需手填。
- **上下文预算是粗估**（按字符数换算），不是真分词；用途是防超窗，不是精确计费。
- **服务商与模型列表写死在代码里**：服务商再次下线模型时仍需改代码才能更新预设。

### 下一步

1. 用真实密钥各跑一次 OpenAI Chat 与 Anthropic Messages，确认报文与响应解析都对。
2. 把「加过的自定义模型」持久化为用户自己的模型列表，而不是每次重新手填。
3. 预设列表改为可编辑/可更新，避免又出现整批过期。

## 2026-09-26 第五十五轮更新：Agent 连线操作与操作层离线校验

### 一、补上连线操作（`create_edge` / `delete_edge`）

这是前几轮最影响可用性的缺口：Agent 能建节点、能删节点，却**不能把节点连起来**，
建出来的是孤岛。现在 `source` + `target`（短 id 或标题）即可建立/断开连线。

关键设计：**同一次提议里新建的节点，可以在后续动作里用它的标题作为端点**。
执行器按顺序作用在同一份画布上，所以「先建 A、再建 B、再把 A 连到 B」一次就能提交完成——
否则模型必须先建节点、等用户保存、拿到短 id，下一轮才能连线，等于要用户点两遍。

拒绝规则：起点与终点相同、两个节点之间已有同向连线、端点找不到。
`delete_edge` 只删指定方向（A→B 与 B→A 是两条独立的边）。
与界面拖拽保持一致：**只加边、不维护 `ParentNodeId`**（界面拖拽也不维护——这是既有的不一致，
本轮没有顺手改，因为它会改变「AI 自动展开下游节点」的行为）。

### 二、虚影补上「将被删除的连线」

上一轮 `CanvasPreview.RemovedEdgeIds` 只收集、不绘制，
于是「删掉一条连线」和「删节点连带删边」在保存前**没有任何视觉提示**。
现在用浅红虚线画在原位置。

### 三、操作层离线校验：23 项，全部通过

这几轮的「未验证」有个具体障碍：解析器、执行器、预览都在 WinForms 项目（`net10.0-windows`），
而 `DreamForge.Core.Tests` 是 `net10.0`，引用不到——所以操作层一直没有测试。

本轮用一个**临时校验工程**（放在 `%TEMP%\df-agent-check`，**不进仓库**）通过 `ProjectReference`
直接调用操作层，覆盖：

- 解析器：剥离 JSON 块、正确读到 `source`
- 同批 `create_node` + `create_edge` 用标题连上，且方向没反
- 重复连线 / 自环 / 端点不存在 → 拒绝
- `delete_edge` 只删指定方向；删不存在的边**失败而不是静默成功**
- 删节点连带删边
- 锁定节点拒改拒删，且预检提前告知
- 预览**不动真实画布**；正确报告新增节点 / 新增连线 / 将删除的连线；无差异的操作不报假差异
- 文件操作：`Skip` 不落盘、`../` 越界被拒绝、真正提交时才落盘并记下可撤销快照

结果：**23 项全部通过**。这是本项目第一次对 Agent 操作层拿到真实运行结果，不再是纸面推断。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- **操作层离线校验：23 项通过**（临时工程，见上）。
- 实际启动程序：正常，无异常。
- **仍未验证**：真实模型是否按协议输出 `actions`（提示词遵约率），
  以及界面上「勾选 → 虚影 → 提交」的整体观感——这两件都必须接真实模型才能看。

### 当前边界

- **不校验拓扑**：可以连成环、可以一对多/多对一。画布本身也不拦，所以这里保持一致而不是单独加规则。
- **建边不选端口**：统一用端口 0（与示例模板一致），不支持「第 2 个输出口连到第 1 个输入口」。
- **不维护 `ParentNodeId`**：见上，Agent 建的边不参与「自动展开下游节点」。
- **临时校验工程不在仓库里**：换机器就没了，也不会跟着 CI 跑。

### 下一步

1. 把 `%TEMP%\df-agent-check` 提升为仓库内的正式测试工程（建议 `DreamForge.Agent.Tests`），
   让这 23 项进入回归——它是目前唯一能覆盖 Agent 操作层的路径。
2. 真实模型实测提示词遵约率（这是最后一个没被验证的核心假设）。
3. 给 Agent 补上「加附件 / 加设定引用」等其余画布能力。

## 2026-09-26 第五十六轮更新：Agent 测试工程进入仓库

### 一、新增 `DreamForge.Agent.Tests`

上一轮的校验工程在 `%TEMP%` 里，换机器就没了。本轮把它搬进仓库并纳入 `DreamForge.slnx`：

- 目标框架 `net10.0-windows`（必须与 `DreamForge.Desktop` 一致），`ProjectReference` 指向桌面端。
- 沿用与 `DreamForge.Core.Tests` 相同的控制台测试风格：`(名称, 动作)` 表 + `Expect` 断言，
  失败打印 `FAIL` 并置 `Environment.ExitCode = 1`，**不带入新的测试框架依赖**。
- 共 **14 项测试（内部 23 个断言）**，全部离线，不访问网络、不需要模型：
  提议解析、同批建节点+连线、重复连线与自环拒绝、预检提示、按方向删边、
  删不存在的边报错、删节点连带删边、锁定保护、预览不动真实画布、
  预览投影新增/删除、文件写入的预览与提交差异、路径越界拒绝。

### 二、验证了「测试真的会失败」

测试套件最大的陷阱是永远绿。本轮特意做了反向验证：
把一处断言临时改成必失败，确认输出 `FAIL ...` 且退出码为 1，再改回。

### 本轮验证

- `dotnet build DreamForge.slnx`：通过，0 个警告，0 个错误。
- `DreamForge.Agent.Tests`：**14 项全部通过**，退出码 0。
- `DreamForge.Core.Tests`：**17 项全部通过**，退出码 0。
- 反向验证：故意造一个失败断言 → `FAIL` + 退出码 1，确认失败会被捕获。
- 新增工程首次构建需要先 `dotnet restore`（`--no-restore` 会报 `NETSDK1004` 缺 assets 文件）。

### 当前边界

- 测试只覆盖**操作层**（解析 / 执行 / 预检 / 预览 / 文件写入）。
  **审批勾选、虚影绘制、提交与撤销的界面路径仍无自动化覆盖**——那部分要真实模型或人工点击。
- 没有接 CI：仓库里目前没有任何流水线脚本，测试靠手工 `dotnet run`。

### 下一步

1. 真实模型实测提示词遵约率（最后一个未验证的核心假设）。
2. 把两个测试工程串成一条命令（或加个脚本），避免漏跑。
3. 给 Agent 补上「加附件 / 加设定引用」等其余画布能力。

## 2026-09-26 第五十七轮更新：Agent 多模态支持（图片与文件上传）

### 一、起因：模型支持 ≠ 你用得上

DeepSeek 的图像理解**只跟 `deepseek-flash` 走**，`deepseek-v4-pro` 是纯文本，传图会直接 400。
而我们的对话消息一直是纯文本（`AiChatMessage` 只有 Role + Content），根本没有图片通道。本轮补上。

### 二、附件走两条完全不同的路（新增 `AgentAttachments.cs`）

- **图片** → 转成 data URL，走模型的多模态图片块。**需要模型支持图像理解**。
- **文本文件**（md / txt / json / csv / 代码等）→ 直接读成文本拼进消息正文。
  这条路**任何模型都能用**，不依赖服务商的任何文件接口。
- **二进制文件**（PDF / Word / 压缩包）→ **如实拒绝并说明怎么解决**，不假装上传成功——
  DeepSeek 明确不支持文件输入，假装成功比直接拒绝更糟。
- 上限：图片 8 MB/张（base64 后约 1.34 倍，再大请求体不合适）、文本 20 万字符。
  文本超限**截断并在提示词里标注「已截断」**，否则模型会以为读到了全文。

### 三、两种协议的图片报文（`OpenAiCompatibleProvider.cs`）

- **OpenAI**：带图片时 `content` 从字符串变成内容块数组
  `[{type:"text"},{type:"image_url",image_url:{url}}]`；没有图片时仍用字符串（兼容性最好）。
- **Anthropic**：`{type:"image",source:{type:"base64",media_type,data}}`，
  data URL 必须拆成 `media_type` + `data` 两部分，直接塞 data URL 会被拒。
- **`system` 消息永远保持纯文本**：DeepSeek 对 system 里的图片直接返回 400。
  Anthropic 协议天然满足（system 在顶层），OpenAI 协议靠组装顺序保证。
- 未开启图片输入就带图 → **提前拦住**，报错写明怎么改（勾选开关，或改用
  deepseek-flash / kimi-k3 / qwen3.8-max），而不是把服务端的 400 原样抛出来。

### 四、能力声明落进数据

- `ProviderModel.SupportsVision` + 预设数据（只填已核实的）：
  `deepseek-flash` ✓、`deepseek-v4-pro` ✗、Kimi 四款 ✓、`qwen3.8-max/3.7-plus/3.8-flash` ✓、
  `glm-5.3` ✗（官方明确「仅支持文本模态」）、OpenAI 三款 ✓。
- `AiProviderConfig.SupportsImageInput`：高级配置里可手动勾选，供自定义/手填模型使用。

### 五、界面

对话工具栏新增「添加图片 / 添加文件 / 移除附件」；附件区**只在有附件时展开**，不占对话区。
发送后附件**保留**（同一张图可以连续追问多轮），「清空」对话时一并清掉。
未开启图片输入时「添加图片」直接置灰，并在提示里说明原因。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- `DreamForge.Agent.Tests`：**23 项通过**（新增 9 项）：
  图片转 data URL 且能还原原始字节、超限图片拒绝、文本读取与截断、二进制如实拒绝、
  文本拼正文与图片收集、OpenAI 报文图片块形状、Anthropic 报文图片块与 system 分离、
  system 消息绝不带图片、未开启图片输入时的可读报错。
  **报文形状用桩 `HttpClient` 拦下请求体检查，完全离线、不需要模型。**
- 实际启动程序：正常，无异常。
- **未验证**：真实模型收到图片后的表现——DeepSeek 对图片尺寸/数量的实际限制、
  单张图的 token 消耗，都没实测过。

### 当前边界

- **图片不压缩**：超大图直接拒绝，而不是自动降采样。
- **文本附件只认扩展名**：一个改了名的 `.pdf` 仍会被当文本读，结果是乱码。
- **附件只在内存里**：重启程序要重新添加。
- **不能从画布直接发图**：节点的图片附件、实体的参考图都得先导出到磁盘再上传，
  这一步目前是手工的——而「让 Agent 看这张角色图」正是最该顺手的场景。

### 下一步

1. 从节点附件 / 实体变体参考图一键「发给模型看」，省掉导出再上传。
2. 图片自动降采样到合理尺寸（现在超限只拒绝）。
3. 真实模型实测提示词遵约率（仍未做）。

## 2026-09-26 第五十八轮更新：真实模型端到端实测（首次）

### 一、实测环境

用户接入 DeepSeek `deepseek-v4-flash`（1M 上下文，图像理解开）后，
用临时探针（`%TEMP%\df-live-check`，走 **App 的同一条链路**：同一个 `AiProviderFactory`、
同一段 `ActionProtocol`、同一个 `AgentContext`）做了端到端实测。

### 二、结果：核心假设成立

- **连接测试**：通过。
- **协议遵约：3 次独立请求全部按要求输出 `actions`**（每次 3 个建节点 + 3 条连线，提交成功 6/6）。
  模型**自发使用了「同批新建节点后用标题连线」这一设计**（这是本轮为连线专门设计的用法，
  提示词里只写了一句规则，它读懂了）；并明确说「建议按下面的方式建节点并串联，等待你确认后再提交生效」——
  协议里「不要声称已完成」这条也遵守了。
- **图片通道：通过**。256×256 PNG 经 `AgentAttachmentLoader` → 图片块 → 模型答出「红色」。

**样本量说明**：3 次成功不能证明「稳定」；但那个一直被标为「未验证」的核心假设，
现在至少从「从未验证」变成「已验证成立」。

### 三、实测暴露并修掉一个真 bug：预检把同批连线误报成「找不到端点」

模型一次提议「建 3 个分镜 + 连 3 条线」时，逐条预检把 **3 条连线全部标成**
「找不到终点节点 / 找不到起点节点」，而实际提交 6 条**全部成功**。
用户看到那串 ⚠ 会以为坏了，很可能把正确的操作取消勾选——**批准环节的误报比不报更糟**。

原因：预检是对**原画布**逐条跑的，看不到同一批里前面刚建的节点。
修复：新增 `AgentActionExecutor.PrecheckBatch`——在画布副本上**按顺序试算**，
后面的操作能看到前面操作的效果；试算跳过文件写入，所以预检仍然是只读的。
`AgentPane` 改为整批预检。

### 四、另一个发现：1×1 的图会被服务端判为无效图

首次图片测试用 1×1 PNG，返回 400 `unsupported image`，
而报错内容提示的却是格式列表（webp / png / jpeg / gif）——**格式明明是对的，报错在误导**。
换成 256×256 后正常。结论：**退化尺寸的图会被 DeepSeek 拒绝**，与我们的报文无关。

### 本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- `DreamForge.Agent.Tests`：**24 项通过**。新增「整批预检能看见同批新建的节点」，
  它同时断言四件事：旧行为确实会误报（留作回归证据）、整批预检不误报、
  真实警告（节点锁定）不被整批预检洗掉、预检不修改真实画布。
- 真实模型 3 次请求：协议遵约 3/3，图片通道 1/1。

### 当前边界

- **退化尺寸图片会拿到服务端误导性的 400**：我们不做图片尺寸预检，也不改写服务端报错，
  用户会看到「请确认格式是 png/jpeg/webp/gif」而手里的图正是 png。
- **协议遵约只在单轮、单任务上验证**：多轮追问、更复杂的指令下表现未知。
- **上下文会随轮次累积**：历史里带着图片块与附件正文，多轮后 token 增长较快，界面没有提示。
- **没有流式输出**：本次回复约 1000+ 字自然语言 + 6 条操作，界面上是干等。

### 下一步

1. 多轮追问下的协议遵约与上下文累积（现在只验了单轮）。
2. 退化图片的报错改写：把「格式不对」翻译成「尺寸过小或文件损坏」。
3. 流式输出与取消。

## 2026-09-26 第五十九轮更新：密钥加密落盘（DPAPI）与只写化提示

### 一、先查清主流客户端的两种密钥存法

| | 做法 | 关键事实 |
| --- | --- | --- |
| 加密数据库方案 | 对话与用量放在 **SQLCipher 4 加密**的数据库里（AES-256-CBC / PBKDF2-HMAC-SHA512 / 25.6 万次迭代），库密钥首次启动随机生成、只存在进程内存 | 社区逆向工具证明：**同机同用户只要进程活着，扫内存 0.2 秒就能拿到那把密钥** |
| 配置文件方案 | 密钥写 `$HOME/.credentials.yaml`（**本身不加密**），`settings.yaml` 只存**引用**（`apiKeyEnv`）；界面 **write-only**，保存后无法回读明文 | 官方主推环境变量；明确要求「不要把 key 贴进 prompt、仓库文件、截图或提交的插件配置」 |
| 我们（改前） | 明文 JSON，且**界面会把密钥明文回显** | 已支持 `DREAMFORGE_AI_KEY`，但没有 UI 引导 |

**结论**：加密挡的是「文件被拷走」，**挡不住同机进程**——这条不该被当成能解决的问题。
真正该修的是另外三件：**界面回读**、**文件被抄走/误提交**、**旧明文一直躺着**。

（ACL 实测：配置文件对 当前用户 / Administrators / SYSTEM 可读写；`%LocalAppData%` 本身已保证其它普通用户读不到。所以进一步收紧权限位收益很小，本轮没做。）

### 二、实现

1. **新增 `SecretProtector`**：Windows DPAPI（`CurrentUser` 作用域 + 附加熵），密文带 `dpapi:` 前缀。
   解密失败返回 `null` 而不是抛异常；`Describe()` 只露首尾各 4 个字符用于辨认。
2. **`AiProviderSettings` 加解密与迁移**：
   - `Save`：落盘前把密钥换成密文，`finally` 还原内存明文——**内存始终明文（请求要带它），文件始终密文**。
   - `Load`：解密；解不开 → 标记 `ApiKeyUnreadable` 且密钥置空，**绝不让密文被当成密钥发出去**。
   - **明文自动迁移**：读到旧版明文配置时**就地加密落盘**，不等用户下次打开设置；只尝试一次，
     避免配置目录不可写时每次读取都重复失败。
   - `ApiKeyUnreadable` / `ApiKeyWasPlaintext` 都标了 `[JsonIgnore]`：它们是读取结果，不是配置项。
3. **界面（`AiSetupDialog`）**：
   - **密钥不再回显明文**（只写不回读），只显示脱敏描述符 `sk-c…e7ec`；**留空＝不修改**。
   - 说明行写清：当前用的是哪把 / 怎么保护 / 文件在哪 / 拷到其它账户或机器解不开。
   - 解不开时红字要求重填，并且**不允许空着保存**。
   - 「测试连接」用输入框里的新密钥或已保存的那把——否则留空时会误报「鉴权失败」。
4. **顶栏**：密钥不可用时显示「密钥无法解密（点击重新接入）」，不让用户以为是模型或网络出了问题。

### 三、验证（含在真实配置上实测）

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- `DreamForge.Agent.Tests`：**28 项通过**（新增 4 项）：
  加密往返、拒绝二次加密、脱敏不泄露完整密钥、非法 base64 返回 null；
  **落盘是密文 / 读回是明文 / 内存仍是明文**；损坏密文降级为空密钥并标记、且不影响其它配置项；
  旧明文配置在读取时就地加密。
- **真实配置实测**：迁移前文件里**有明文密钥、无密文**；跑一次连接测试后 →
  文件里**无明文、有 `dpapi:` 密文**，且**连接仍然成功**——证明解密出来的密钥可用，
  加密没有把用户的接入弄坏。

### 当前边界

- **DPAPI 绑定当前 Windows 账户**：换账户或换机器配置就解不开，需要重新填密钥。
  这是刻意的取舍（挡住拷走），界面已明确写出。
- **同机同账户的进程仍可解密**：与加密数据库方案被内存扫描的结论一致，不是本方案能解决的问题。
- **未做「环境变量模式」**：`DREAMFORGE_AI_KEY` 已支持，但没有 UI 引导，
  配置里也没有「只存变量名、密钥完全不落盘」的选项——这是配置文件方案主推的环境变量模式，列为下一步。
- **其它字段仍是明文**（ComfyUI 地址、工作文件夹等）：它们不是密钥，本轮的加密只覆盖 `ApiKey`。

### 下一步

1. 环境变量模式：配置里只存 `DREAMFORGE_AI_KEY` 这个变量名，密钥完全不落盘。
2. 多轮追问下的协议遵约与上下文累积。
3. 流式输出与取消。

## 2026-09-26 第六十轮更新：流式输出与取消

### 一、为什么这是门槛而不是锦上添花

`deepseek-v4-flash` 默认开思考模式，之前的实现是**整段返回**：请求发出去之后界面完全静止，
用户无法判断是在思考、卡死还是失败，也不能中止。这是「能不能天天用」的问题。

### 二、接口层（`AiChat.cs`）

- 新增 `AiStreamSink(OnText, OnThinking?)`。**思考信号是单独的回调**：
  思考型模型会先想一会儿再吐正文，这期间必须有东西告诉用户「还在动」。
- `IAiChatProvider` 增加 `ChatStreamAsync`，并给**默认实现**：退回非流式、一次性把全文回调出去。
  这样不支持流式的实现（本地模拟）不必写额外代码——代价是没有逐字体感，语义完全一致。

### 三、Provider 流式（`OpenAiCompatibleProvider.cs`）

- 两种协议都走 SSE，逐行读 `data:` 负载。用 `HttpCompletionOption.ResponseHeadersRead`
  **必须**：否则 HttpClient 会等整个响应读完才返回，流式就白做了。
- OpenAI 兼容：增量在 `choices[0].delta.content`，思考在 `delta.reasoning_content`
  ——**思考单独返回，绝不混进正文**。
- Anthropic：增量在 `content_block_delta`，`text_delta` 是正文、`thinking_delta` 是思考。
- 失败路径照常读 JSON 报错；**事件流里一行正文都没有时直接抛错**，不静默返回空回复
  （某些网关会 200 但不支持 stream）。
- 顺手把 `EnsureImageInputAllowed` / `ApplyAuthHeaders` 抽出来，去掉流式与非流式的重复。

### 四、界面（`AgentPane.cs`）

- **节流刷新**：增量先进队列，由 60ms 定时器统一写入对话区。
  实测一次回复有 51 段增量，长回复会更多——逐段投递会把 UI 线程打满。
- **停止按钮**：只在请求进行中显示；取消后**半截回复不进对话历史**
  （它不是完整回答，留着会污染后续上下文）。
- **操作块不进对话区**：显示只取操作块之前的部分，定位复用解析器自己的
  `FindActionsBlockStart`（含 ```json 围栏），避免显示与解析两边判断不一致；
  完成后改为提示「已生成 N 条改动建议，见下方待审批列表」。
- 状态行会显示「模型正在思考…（思考完成后逐字出现）」——只报一次，不刷屏。

### 五、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- `DreamForge.Agent.Tests`：**32 项通过**（新增 4 项）：
  OpenAI 格式增量分次回调且思考不混入正文、请求体带 `stream:true`；
  Anthropic 格式 `text_delta` / `thinking_delta` 分流；空事件流报错；
  操作块与围栏不进显示、普通回复不被误截。
- **真实模型实测**（DeepSeek `deepseek-v4-flash`）：思考信号**有**；
  **首个增量 1.21 秒到达**，总耗时 1.42 秒，**51 段增量**，回复内容正常
  —— 确认是真正的流式，不是回落。

### 当前边界与遗留

- **配置仍在 `%TEMP%`**：沙箱不允许我写 `%LocalAppData%`，需要用户自己执行一次复制命令
  （密钥已 DPAPI 加密，同机同账户复制后仍可解密）。
- **取消路径没有自动化测试**：`OperationCanceledException` 分支只在代码里，未在真实请求中按过停止。
- **思考内容不显示**：只用来提示「正在思考」，正文里不含思考过程（要不要展示是可配置项，暂未做）。
- **操作块之后不再显示任何内容**：一旦进入操作块，后续文本一律不显示；
  若模型在块后还写正文，那部分只在解析结果里，界面上看不到。

### 下一步

1. 环境变量模式：配置里只存 `DREAMFORGE_AI_KEY` 这个变量名，密钥完全不落盘。
2. 多轮追问下的协议遵约与上下文累积（含图片留在历史里的 token 增长）。
3. 从节点附件 / 实体变体参考图一键「发给模型看」。

## 2026-09-26 第六十一轮更新：参照两种主流客户端做法重构流式设计

### 一、先查清两家的设计

**客户端 A（事件流）**：模型看到的一切都记入**只追加（append-only）的会话事件流**——system prompt、
**reasoning**、tool calls 与结果、子 agent 调度、每一次上下文注入；另有按来源查看的时间线视图，
把 `user input, model replies, **collapsed thinking**, and grouped tool calls` 渲染成一条流
——**思考默认折叠但可展开**。

**客户端 B（状态机）**：流式有明确的状态机
`idle / connecting / streaming / reconnecting / completed / failed / cancelled`，两条原则最关键：

- **cancelled ≠ failed**：取消是用户主动改意，失败是系统没完成——UI、日志、重试策略都要分开；
- **取消后保留已生成的半截内容**，标记「已停止」，并给「重新生成」入口。

另外 DeepSeek 官方文档给了硬约束：**不带 `tools` 时 `reasoning_content` 不需要回传，
传了也会被忽略**——所以思考**本来就不该进对话历史**。

### 二、对照出的差距与本轮改动

| 项 | 改前 | 改后 |
| --- | --- | --- |
| 思考过程 | **直接丢弃**，只在状态行提示一次 | 灰色小字流式显示、限长 1200 字，正文一到写「思考完毕」收束 |
| 取消 | 保留半截显示、不进历史（已对） | 补**「重新生成」**入口，提示文案改为「已停止生成」 |
| 失败 vs 取消 | 已分开（保持） | 保持 |
| 思考进历史 | 已不进（保持，且有官方文档背书） | 保持 |

实现要点：

- `AiStreamSink.OnThinking` 从**无参信号**改为**带增量文本**——要显示思考就必须拿到内容，
  这也正好对齐客户端 A「reasoning 与 reply 分开记」的做法。
- Provider 的 `ReadOpenAiDelta` / `ReadAnthropicDelta` 返回 `(正文, 思考)` 两条流；
  同一块里既给思考又给正文时**只认正文**，避免混流。
- 界面把思考与正文放进**两个独立队列**，由同一个 60ms 定时器刷新；正文一开始，思考阶段即收束。
- 第 33 项测试：**只收到思考、没有正文也判失败**——否则界面会显示一个空回答。

### 三、真实模型实测（DeepSeek `deepseek-v4-flash`）

| 指标 | 结果 |
| --- | --- |
| 思考增量 | **442 字符**，首个思考片段 **0.38 秒**到达 |
| 首个正文增量 | **1.04 秒** |
| 总耗时 | 1.23 秒　正文 52 段 / 78 字符 |
| 思考是否混入正文 | **没有**（两条流分离正确） |

**一个意外发现**：思考内容是**英文**的（`The user wants a one-sentence definition of "分镜"…`），
而回答是中文。也就是说思考过程通常是模型的**内部工作语言**，不一定与回答同语言。
这反过来支持「弱化展示」的选择：灰色小字 + 限长，而不是把它当成正式内容铺开。

### 四、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- `DreamForge.Agent.Tests`：**33 项通过**（新增「只收到思考没有正文也算失败」，
  并把思考断言从「信号次数」改为「思考增量文本」）。
- 真实模型实测：见上表。

### 当前边界与取舍

- **没有做真正的折叠**：RichTextBox 没有原生折叠能力，硬做要靠 RTF 隐藏文本，代价大于收益。
  折中成「灰字 + 收束标记 + 限长」，保住「思考可见」与「思考完就收起来」两个要点，
  但**不能像客户端 B 那样点开回看被折叠的思考**。
- **自动滚动是「始终跟随」**：用户往上翻看历史时会被新内容拽回底部。
  正确做法是「用户手动滚过就停止跟随」，但 RichTextBox 没有可靠的「是否在底部」信号，
  要么不可靠、要么得 P/Invoke。**本轮没做**，宁可留着也不加一个会误判的启发式。
- **思考不显示在状态行**了：状态行改回「正在连接… / 就绪」，思考本身在对话区里。
- **取消路径仍然只有人工可验**：没有自动化测试能模拟点「停止」。

### 下一步

1. 环境变量模式：配置里只存 `DREAMFORGE_AI_KEY` 这个变量名，密钥完全不落盘。
2. 多轮追问下的协议遵约与上下文累积（含图片留在历史里的 token 增长）。
3. 从节点附件 / 实体变体参考图一键「发给模型看」。

## 2026-09-26 第六十二轮更新：思考可回看、重跑前可编辑

### 一、思考过程做成「可点开回看」

上一轮思考只能看到限长的 1200 字，超出部分永久丢失。本轮：

- 思考全文**留档**（`thinkingFull`），不再随显示截断而丢弃。
- 收束时在对话区留一行可点击入口：**`▸ 回看完整思考（N 字，点击打开）`**，
  悬停变手型，点开是**只读窗口**（等宽字体、可滚动、带「复制全文」）。
- 点按命中用 `GetCharIndexFromPosition` 与记录的 `[Start, Start+Length)` 区间比对；
  对话区**只追加不删除**，所以这些索引天然稳定；「清空对话」时一并清掉。
- 被取消/出错而中断的思考也会留入口，标注「思考被打断」。

### 二、为什么不是 inline 折叠

期望的效果是**在对话流里原地展开/收起**。RichTextBox **没有原生折叠能力**：要做只能靠 RTF 隐藏文本
（`\v`），或者把对话区改成「结构化消息 + 每次重绘」——后者会牵动整个渲染主路径，
还会连带影响那个尚未解决的自动滚动问题。

本轮选择**独立只读窗口**：保住「点开回看」这个结果，不动主渲染路径。
顺带一个好处：长思考在独立窗口里比 inline 展开更好读（不把回答挤到屏幕外），还能整段复制。
**取舍说清楚**：没能做成对话流内的原地折叠，这里是它的等价物。

### 三、重跑前可编辑

上一轮的「重新生成」是**原样重跑**。本轮改成 **「编辑重跑」**：

1. 把上一条提问**放回输入框**，光标置尾，可以改文字；
2. 同时把那一轮从对话历史里**移除**——否则同一句话会以两条 user 消息进入上下文；
3. 改完点「发送」，就是**替代**那一轮，而不是追加新的一轮。

「参数」也可改：**附件**（本来就没被清掉，可顺手增删）与**「带上画布上下文」开关**都保持原状。
按钮只在**取消或失败之后**出现——两种情况都属于「这一轮没完成」，失败态同样带重试入口；
但文案分开：取消说「已停止生成」，失败照常报错，不把失败说成「已停止」。

### 四、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- `DreamForge.Agent.Tests`：33 项通过（本轮改动集中在界面层，未新增自动化断言）。
- 实际启动程序：正常，无异常。

### 当前边界

- **界面层仍无自动化覆盖**：点按命中、回看窗口、「编辑重跑」的替换语义都只能人工点。
  这是本项目一贯的缺口（WinForms 界面路径没有可用的自动化手段）。
- **回看入口的位置索引依赖「对话区只追加」**：若将来加入「删除某条消息」之类的操作，
  这套索引会失效，需要一并改成结构化消息模型。
- **只有一层思考留档**：`thinkingFull` 每轮清空，回看窗口只对应那一轮的思考，不跨轮汇总。
- **自动滚动仍是「始终跟随」**：这条从第六十一轮遗留，仍未解决（缺可靠的「是否在底部」信号）。

### 下一步

1. 环境变量模式：配置里只存 `DREAMFORGE_AI_KEY` 这个变量名，密钥完全不落盘。
2. 多轮追问下的协议遵约与上下文累积（含图片留在历史里的 token 增长）。
3. 从节点附件 / 实体变体参考图一键「发给模型看」。

## 2026-09-26 第六十三轮更新：排查「Agent 加不了节点」

### 一、先证明链路没坏

用户反馈「没法用 agent 加画布节点」。关键怀疑点是：
**之前验证协议遵约走的是非流式 `ChatAsync`，而 App 在第六十轮之后走的是 `ChatStreamAsync`**
—— 流式这条路从来没验证过「操作块是否完整回来」。

用与 App 完全一致的调用（同一个 Provider、同一个 `AgentContext`、同一段协议）实测：

| 指标 | 结果 |
| --- | --- |
| 流式累计正文 vs 增量回调累计 | 411 / 411 字符，**一致** |
| 解析出的操作 | **2 条**（新建「第一章 分镜」+ 连线） |
| 预检 | 无警告 |
| 提交 | **成功 2 / 失败 0**，画布 1 节点 → 2 节点 + 1 连线 |

**结论：链路没坏，操作块在流式路径下完整保留。**

### 二、查到的真实原因（环境层面）

`%LocalAppData%\DreamForge\` 里**没有 `ai-config.json`** ——
用户的密钥一直在 `%TEMP%\dreamforge-demo\`（我为验证启动时用 `DREAMFORGE_CONFIG` 指过去的）。
所以**用户自己双击启动程序时读不到任何模型配置**，会走「本地模拟」；
而本地模拟**只会回占位文本，永远不会产出改动提议**，表现就是「有回复但没有审批列表」。

### 三、顺手修掉的两个「不诚实」缺陷

1. **本地模拟下谎称能改画布**：面板开场白原本写着「我可以在你批准后新建/修改/删除画布节点」，
   而本地模拟根本没有这个能力。现在明确说：「我只能回占位示例文本，不会提出任何画布改动，
   所以改不了节点与设定」，并指向接入入口。
2. **零提议时毫无反馈**：模型只回文字、没输出操作块时，界面什么都不说，
   用户无法区分「模型觉得不用改」和「Agent 坏了」。现在状态行会写明
   「本轮没有改动提议（模型只回答了文字）」并给出更明确的说法示例。
   （为此加了 `statusNote`：状态行的一次性提示，不能被 `finally` 里的 `RefreshStatus()` 冲掉。）

### 四、强化提示词

协议里补一句硬约束：**用户要求改动时，不要用文字描述改动方案来代替操作块——只写文字等于什么都没做。**
这是针对「模型用自然语言描述方案而不产出可批准改动」这类不遵约行为的直接对策。

### 五、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：33 项通过。
- 真实模型实测：流式路径下操作块完整保留、提交成功（见上表）。

### 当前边界

- **配置仍在 `%TEMP%`**：用户必须自己执行一次复制命令才能让正常启动读到密钥
  （沙箱不允许本 Agent 写 `%LocalAppData%`）。密钥是 DPAPI 加密的，同机同账户复制后仍可解密。
- **提示词遵约率仍非 100%**：本轮强化了约束，但没有量化验证；「模型偶发只回文字」依然可能发生，
  现在至少状态行会明确告知，而不是让用户猜。

### 下一步

1. 环境变量模式：配置里只存 `DREAMFORGE_AI_KEY` 这个变量名，密钥完全不落盘。
2. 多轮追问下的协议遵约与上下文累积（含图片留在历史里的 token 增长）。
3. 从节点附件 / 实体变体参考图一键「发给模型看」。

## 2026-09-26 第六十四轮更新：修掉「模型只写故事、不提议改动」

### 一、症状与假设

用户反馈：「我说加节点，他一直在给我编写刚刚的故事」——模型把 Agent 面板当成创作聊天，
继续写剧情，而不是产出可批准的改动。

按线索查系统提示，抓到一条**直接矛盾**的指令：

```
ChatSystemPrompt: 「…用简体中文回答，直接给出可用的内容或建议，不要输出 JSON…」
AgentContext.ActionProtocol: 「…在回复的最后附上一个 JSON 代码块…」
```

**「不要输出 JSON」是 Agent 协议出现之前（第五十二轮之前）的遗留，一直没改。**
它恰好否定了 Agent 功能所要求的那一种输出。叠加画布里的剧情正文与历史里的写作模式，
模型自然顺着写故事。

### 二、复现（先证明，再修）

| 场景 | 修复前 |
| --- | --- |
| 含糊请求「给第一章加个分镜节点」 | 2 条操作（**偶然通过**，说明这条矛盾不是每次都发作） |
| 带故事历史再要求加节点 | 修复前未单独跑；修复后见下表 |

### 三、三处修改

1. **`ChatSystemPrompt`**：删掉「不要输出 JSON」，改为
   「讨论创作时直接给出内容；**当用户要求改动画布或写入文件时，必须按上下文中的操作协议输出改动块**，
   这类要求绝不能只用文字描述或正文内容代替」。
2. **`AgentContext.Describe` 开头**加一句角色定位：
   「你在 DreamForge 的画布助手里：既要讨论创作，也要把改动作为提议交出来；
   用户要求改画布时，只写正文或只描述方案都不算完成。」
3. **`ActionProtocol`** 补一个具体反例：
   「例如用户说「给第一章加个分镜节点」：你应当输出 `create_node` 提议，
   **而不是把分镜内容写进回复正文**——写进正文它不会变成节点。」

### 四、修复后实测

| 场景 | 结果 |
| --- | --- |
| 含糊请求「给第一章加个分镜节点」 | **2 条操作**（新建节点 + 连线） |
| **带故事历史**再要求加节点 | **2 条操作**，正文从长篇故事缩到 585 字符，直接给提议 |

### 五、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：33 项通过。
- 真实模型实测：见上表。

### 当前边界

- **提示词遵约仍是概率性的**：本轮把「矛盾指令」这个确定性缺陷修掉了，
  但模型偶尔不遵约依然可能发生——现在状态行会明确告知「本轮没有改动提议」，不会让用户猜。
- **历史里的写作模式有惯性**：如果前几轮都在纯写作，后面切到「改画布」可能需要更明确的说法；
  这是上下文的固有效应，不完全是提示词能解决的。
- **这条缺陷存在了两个轮次才被发现**：此前验证协议遵约时，构造的都是一句话点明
  「新建节点」的明确请求，**没有覆盖含糊说法与带历史的反例**。
  教训记下：验证提示词时，反例比正例更重要。

### 下一步

1. 环境变量模式：配置里只存 `DREAMFORGE_AI_KEY` 这个变量名，密钥完全不落盘。
2. 多轮追问下的协议遵约与上下文累积（含图片留在历史里的 token 增长）。
3. 从节点附件 / 实体变体参考图一键「发给模型看」。

## 2026-09-26 第六十五轮更新：模型反问改成弹窗（ask 协议）

### 一、起因：模型把问题写成了正文

用户反馈：Agent 在正文里列出「你要加哪个画布？节点类型是？内容写什么？」并让用户自己组织语言回一遍，
并指出**这些应该是弹窗让用户选择或自己输入**。

这是真实的设计缺口：**协议里只有 `actions`，模型没有表达「我需要用户补一个信息」的通道**，
所以它只能用文字问。补上这个通道。

### 二、协议新增 `ask` 块

```json
{"ask":{"question":"要加哪种节点？","options":["角色节点","场景节点","分镜节点"]}}
```

- `options` 可以为空数组（纯自由输入）；`ask` 可与 `actions` 同批出现（先问清再改）。
- 提示词明确写：**信息不足时就问，不要猜**，也不要把问题写成一大段正文。

### 三、新增 `AgentAskDialog`

- 顶部是问题（加粗、自动换行）；中间是**单选选项**；下面是**自由输入框**，手输优先于点选；
  底部「确定 / 跳过」。跳过返回 null，面板会如实说明「已跳过这个问题」。
- 窗口高度按内容计算（选项数量随模型提问变化），并限制在 240–640 之间。
- 模态打开（owner 是主窗体），保证在画布之上不被遮挡。

### 四、面板流程

`RunTurnWithAsksAsync`：跑一轮 → 若模型用 `ask` 反问 → 弹窗 → 用户答 →
**把答案作为新的 user 消息自动继续下一轮**。最多连续 3 轮：模型反复追问会把用户拖住，那不是帮助。

### 五、顺手修掉一个真实解析缺陷

原来「从标记往回找最近的花括号」的定位方式，在 **`ask` 写在 `actions` 前面**时会锚到 ask 对象自己身上，
整个块被切错、什么都解析不出来。改成**逐个试候选对象**：取第一个配对成功、且确实含
`actions`/`ask` 键的对象——正文里偶然出现的花括号也不会再被误判。

（这个缺陷是**写测试的时候发现的**：`AskCoexistsWithActions` 直接失败了。）

### 六、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。
- `DreamForge.Agent.Tests`：**37 项通过**（新增 4 项：反问解析出问题与选项、与 actions 共存、
  只有 ask 也算有效回复、ask 块同样不进对话区）。
- **真实模型实测**：用户只说「帮我加个节点」时，模型给出

```
问题：要加的是什么类型的节点？（顺带说下标题和大致的剧情内容，以及是否要连到「第一章 剧情」）
选项：剧情节点 / 角色节点 / 场景节点 / 分镜节点 / 设定/世界观节点
自然语言部分仅 141 字符
```

  —— 不再是长篇追问，改由弹窗承载。**模型用上了这个新通道。**

### 当前边界

- **弹窗本身只有编译验证**：点按、选项互斥、跳过分支都只能人工点（界面层一贯的缺口）。
- **弹窗是模态的**：回答期间会挡住主窗口。对「必须补信息才能继续」的流程这是合理的，
  但如果模型频繁追问会显得强势——3 轮上限是缓解，不是根治。
- **答案以新 user 消息追加进历史**（不替换上一条），因此「问—答」会留在上下文里；
  这是刻意的，让模型知道用户是怎么回答的。

### 下一步

1. 环境变量模式：配置里只存 `DREAMFORGE_AI_KEY` 这个变量名，密钥完全不落盘。
2. 多轮追问下的协议遵约与上下文累积（含图片留在历史里的 token 增长）。
3. 从节点附件 / 实体变体参考图一键「发给模型看」。

## 2026-09-26 第六十六轮更新：按三段式骨架重做界面（可切换主题，默认深色）

### 一、先诊断「丑」在哪

不是某个颜色不好看，而是**没有统一语言**：

- 颜色散落在十几个文件里，同类控件在不同面板里深浅不一；
- 左侧栏拿**汉字**当图标（库 / 定 / 节 / 任 / 聊 / 插 / 设）；
- 没有标题栏、没有状态栏，画布与面板之间没有层次；
- 列表用系统表头（浅色），一旦深色就成一块补丁。

### 二、目标骨架（本轮照此重排）

```
┌─────────────────────────────────────────────┐
│ 标题栏：品牌 · 画布名 ……… 主题切换 · 模型状态 │  34px，整宽
├────┬──────────────────────────────┬─────────┤
│活动│ 左抽屉面板(可收起) │ 画布区    │ Agent   │
│栏  │                    │ 工具条+画布│ 对话面板 │
│48px│                    │           │ 400px    │
├────┴──────────────────────────────┴─────────┤
│ 状态栏：运行状态 ……………………………………… 修订号 │  24px，整宽
└─────────────────────────────────────────────┘
```

### 三、新增主题层（`Theme.cs`）

- **一份调色板**：外壳 / 面板 / 编辑区 / 文字三级 / 线框 / 交互 / 语义色 / 画布专用色，全部集中。
- **一份字体表**：正文用「Microsoft YaHei UI」，避免 Segoe UI 回退时中英混排尺寸跳变。
- **可切换，默认深色**；标题栏一键切换，写入配置，重启保持。
- `Theme.Apply(控件树)` 统一套用；画布自己绘制，被显式跳过。
- **字面色映射表**：把项目里真正用过的几种字面颜色（近黑正文、灰提示、强调蓝、危险红、警告黄、成功绿）
  映射到主题角色——这样历史代码里那些 `Color.FromArgb(75,63,227)` 之类的调用**不必逐处改写**
  也能随主题变化，且映射是幂等的（重复套用不漂移）。
- **深色下自绘列表表头**：系统表头永远是浅色，不处理就是一块补丁。

### 四、手绘图标（`RailIcon.cs`）

活动栏图标不再用汉字，改为 GDI+ 手绘的七种图形（库 / 角色 / 节点卡 / 任务清单 / 对话气泡 / 拼图块 / 滑杆）。
**为什么不用图标字体**：Segoe MDL2 的字形码点无法离线核实，写错就是一个空方块，比汉字更糟；
手绘图形是确定的。选中态用左侧一条强调色指示条。

### 五、画布配色（`WorkflowCanvasControl`）

网格、节点底/边框/选中态、连线、端口、状态徽标、缩略图占位与「图片不可用」提示、
待提交虚影（将新增/将修改/将删除）全部改走主题；深色下虚影的白色蒙版改成画布底色蒙版。

### 六、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- 实际启动程序：正常，无异常。
- **界面观感未经我亲自验收**：我看不到渲染结果，需要用户实际看一眼。

### 当前边界与未做

- **对话框仍是浅色**：本轮范围是「主窗口 + 画布 + Agent 面板 + 左侧面板」，
  实体编辑、接入设置、提问弹窗这些模态窗没跟着改，深浅混用会有割裂感。
- **面板宽度不可拖拽**：实现拖拽需要 SplitContainer，本轮没做。
- **自绘列表只覆盖表头**：行内容仍走系统绘制，深色下可用但不是像素级统一。
- **主题切换是「重新套色」而非重建控件**：已写入的富文本颜色不会变，
  只有新写入的文本才用新主题；切主题后旧对话内容保持原色。
- **画布靠 `Invalidate()` 重绘**：这条依赖 `OnPaint` 全部读 Theme，新加绘制代码时要守住这个约定。

### 下一步

1. 把对话框也纳入主题（深浅混用会割裂）。
2. 侧栏与 Agent 面板支持拖拽调宽（SplitContainer）。
3. 前几轮欠账：环境变量模式 / 多轮追问验证 / 从画布一键发图。

## 2026-09-26 第六十七轮更新：用户提的 5 条界面问题

用户原话（逐条对应）：

1. 排版有问题，选项的字显示不全
2. 画布做成浏览器那种，点画布标签后的 `+` 可以直接新建画布
3. Agent 框与画布框要显示成两个模块，要有分界
4. 左侧功能栏不要做的一模一样，那种隐藏式的小小的不要整条都是
5. Agent 消息框重做（详见下）

### 一、提问弹窗的字被裁掉（问题 1）

`AgentAskDialog` 里的选项用 `RadioButton` 且设了 `Width`，但同时也设了 `AutoSize = true`——
**`AutoSize` 会覆盖 `Width`**，长选项于是直接超出去被面板横向裁掉。
改成 `AutoSize + MaximumSize = new Size(426, 0)`，长文本会**换行**而不是被裁。

### 二、Agent 输入区重做（问题 5）

去掉了「发送」两个字，整条输入区改成图标 + 两行：

- **工具栏**：附件（回形针）/ 授权模式（盾牌）/ 模型（芯片）三个图标按钮，各带一行小字说明当前值；
- **上下文杯**：竖着的杯子，液面 = 上下文占用比例（>70% 转警告色，>90% 转危险色），
  悬停给出「约 X / Y tokens」；模型没声明上下文窗口时如实说明「无法估算」；
- **缓存箱**：箱体按命中比例分成蓝色（命中）与黄色（未命中），直观表示 Token 命中视图；
- **统计行**：`第 N 轮 · X tok/s · 输入 · 输出 · 缓存命中率`；
- **发送**：纸飞机图标，停止是它左边的方块图标。

**授权模式**（权限预设）：`每次询问` / `自动进待提交` / `只读不提议`。
关键边界：**这三种模式都越不过「保存画布」**——自动进待提交也只是把它放进待提交区，
仍要用户点「提交并保存」才写入画布。只读模式会如实回一句「已忽略模型提出的 N 条改动」。

**用量统计是真的**：`OpenAiCompatibleProvider` 流式请求加了 `stream_options.include_usage`，
新增 `TryReadUsage` 兼容 OpenAI（`prompt_tokens` / `completion_tokens` / `prompt_cache_hit_tokens`）
与 Anthropic（`input_tokens` / `output_tokens` / `cache_read_input_tokens`）两套字段；
**服务商不返回用量时就如实为 0**，不编数字。窗口里没有模型声明，也照样直说。

### 三、画布区与 Agent 面板的分界（问题 3）

- Agent 面板左侧加 1px 分界线。
- 顺带修掉一个既有缺陷：`Theme.Apply` 会把所有 `Panel` 的底色刷成面板色，
  于是**面板标题下那条分隔线在切主题后就消失了**。新增 `Theme.Line(...)`，
  用 `Tag` 标记「这是线不是面」，套色时还原成线色。

### 四、左侧功能栏（问题 4）

用户的要求：**图标 + 小字标签、垂直居中、鼠标过去滑出、不悬停时只露一点点**。
按此重做：

- 活动栏改成**悬浮滑出条**（不再是 48px 的常驻图标列）：
  收起时只留 10px 窄边，中间画三道短横提示「这里能滑出」；鼠标靠过去滑出成 128px 的完整一列。
- 每一项是**图标 + 小字标签**（画布库 / 设定库 / 节点属性 / 任务与出图 / Agent 对话 / 插件 / 设置），
  按组分隔（创作 · 协作 · 扩展 · 设置），不再是一整条长得一样的图标。
- 它是**悬浮层而非布局的一部分**：展开时压在画布左边，画布不会被挤来挤去。
- 滑出用计时器逐帧改宽度实现；每帧顺便判一次光标是否还在条上，决定展开还是收回（动画与悬停判定二合一）。

### 五、画布标签栏（问题 2）

画布工作区顶部加一条标签栏：当前画布是一个标签（双击重命名，
未保存到画布库时标「未保存」），标签后一个 `+` 直接新建画布——
之前新建画布只能绕进「画布库」面板，入口太深。`+` 与面板里的「新建」共用同一个入口。

### 六、本轮踩到的两个崩溃（都已修）

两个都是**上一轮写进去、但从未真正启动过**的代码：

1. `CupGauge` / `CacheBox` 继承的是普通 `Control`，默认**不支持透明底色**，
   构造里设 `BackColor = Color.Transparent` 直接抛 `ArgumentException`。
   修法：先 `SetStyle(ControlStyles.SupportsTransparentBackColor, true)` 再设色。
   —— 这说明上一轮的「界面重做」其实没跑起来过，验证不能只看编译通过。
2. `RefreshAttachments()` 会改 `layout.RowStyles[4]`，但构造期间 `BuildAttachmentMenu`
   先于 `RowStyles` 的填充被调用 → 索引越界。修法：把「附件能力」刷新单独抽出来，
   并给行高赋值加上存在性判断。

### 七、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- 实际启动程序并保持运行 20 秒以上：无异常（前两次启动分别暴露了上面两个崩溃）。
- **界面观感未经我亲自验收**：我看不到渲染结果，需要用户实际看一眼。

### 当前边界与未做

- **滑出条是悬浮层**：展开时会压住画布左侧 128px；未做「固定展开」的开关。
- **滑出条不受用户控制**：没有做「锁定展开 / 关闭滑出」的偏好设置。
- **对话框仍是浅色**（沿用上一轮欠账）；提问弹窗的本轮改动只解决了裁字。
- **`AgentAuthMode` 只影响「提议如何落地」**，不约束只读类工具调用（目前也没有这类工具）。
- **上下文占用是字符粗估**（1 token ≈ 1.5 字符），不是分词结果，只用于看趋势；
  真实用量以服务商返回的 usage 为准。

### 下一步

1. 把对话框纳入主题；给滑出条加「固定展开」选项。
2. 侧栏与 Agent 面板支持拖拽调宽（SplitContainer）。
3. 前几轮欠账：环境变量模式 / 多轮追问验证 / 从画布一键发图。

## 2026-09-26 第六十八轮更新：修「几乎每个面板的文字都显示不全」

用户反馈：**基本所有的文字排版都显示不全**，且接入弹窗里**点不到「测试连接」下面的按钮**。

### 一、先量，不猜

看不到渲染结果，所以先做了一件事：写一个临时的界面自检（`UiAudit`，修完已删），
把每个控件的 `ClientSize` 和它自己文字的实测尺寸比一遍——
能换行的按实际宽度算换行后高度，不能换行的按单行宽高算。
自绘按钮（`IconButton` / `RailNavButton`）的文字不走 `Control.Text`，用反射取出来一起量。

第一轮量出来的结果指向一个很不像"个别按钮没调好"的现象：
**被标出的控件，需要的高度几乎都是 24–25px，而实际只有 20–22px**——每个都差一点点。

### 二、根因：150% 缩放下，字体按 DPI 放大，像素布局没放大

自检把字体事实和 DPI 打了出来：

```
DPI: 窗体 DeviceDpi=144 … 放大比例约=150%
FONT DefaultFont = Microsoft YaHei UI 9pt | Font.Height=23   ← 96 DPI 下应是 15
FONT Theme.UiFont = Microsoft YaHei UI 9.5pt | Font.Height=25
```

本项目界面**全部用代码构建**，尺寸写的是字面像素（`Width = 128`、行高 22…）。
点是物理单位：`DeviceDpi = 144` 时字体会自动放大 1.5 倍，而那些字面像素不会。
于是**每个容器都比文字需要的尺寸小 1.5 倍**——字体越大切得越狠。
这不是某个按钮没调好，而是全局差一个倍数，所以"基本所有排版都有问题"。

顺带纠正一个我先前的误判：`HKCU\...\WindowMetrics\AppliedDPI` 报的是 96，
但那只反映 100% 的传统设置；真实缩放要看 `DeviceDpi`（这里是 144）。

### 三、为什么不能用 WinForms 自带的 AutoScaleMode

先按标准做法给所有窗体加了 `AutoScaleMode.Dpi` + `AutoScaleDimensions = (96,96)`。
量出来的结果是**没修**：写了一个最小探针（一个 100x30 的按钮 + 一条 30px 的绝对行高）：

```
[AutoScaleMode.Dpi]  按钮={100, 30}  行高=30   窗体Client={400, 300}
[AutoScaleMode.Font] 按钮={100, 30}  行高=30   窗体Client={733, 554}
[手工 Scale 1.5]     按钮={150, 45}  行高=45   窗体Client={600, 450}
```

`AutoScaleMode` 把**窗体自身**缩放了（甚至基准值都被推进到 144），
**子控件和表格行高一个都没动**——它依赖的是设计器那套 `InitializeComponent` 时机，
对纯代码构建的界面等于没设。
所以改用 `Control.Scale(SizeF)` 显式缩放，探针证明它是有效的那个机制。

### 四、实际改动

- 新增 `ScaledForm`：所有窗体的基类。`AutoScaleMode = None`，
  在 `OnLoad`（句柄已建、`DeviceDpi` 为真值、窗口还没显示）里调 `Scale(DPI/96)` 缩放整棵控件树。
- `MainForm`、`AiSetupDialog`、`AgentAskDialog` 继承它；`MainForm` 里 20 处 `new Form { … }`
  弹窗全部换成 `new ScaledForm { … }`（含 Agent 面板的思考回看窗口）。
- **运行时才算的像素值必须手动折算**（它们不在构造期，不会跟着缩放走）：
  新增 `Dpi.Scale(control, value)`，用于活动栏的收起/展开宽度与动画步长、
  活动栏的垂直定位、Agent 面板附件区的行高。
- **ListView 列宽 WinForms 不缩放**（实测：其他都缩放了，列宽仍是原值），
  `ScaledForm` 里补一遍 `ScaleListColumns`。列宽不缩放就会截断单元格文字。
- 两个自撑尺寸的弹窗（接入引导、反问弹窗）改成**只在缩放后**算尺寸：
  缩放前 `DeviceDpi` 还不是真值，算出来的宽度会被缩放再乘一次。

### 五、正文里那几处真实的裁剪（与 DPI 无关，单独修）

- **接入弹窗点不到页脚按钮**：17 行内容装进写死的 620px 高度里装不下，
  多出来的部分把「保存并继续 / 先用本地模拟」顶出可视区。
  现在行高一律按内容算、窗口按内容定尺寸，外面再套一层滚动壳兜底。
- **说明类标签的第二行被切**：`modelNote` / `urlPreview` / `keyHint` / `hint` / `testResult`
  是「宽度撑满 + 高度只够一行」的写法，文案一换行就吃掉第二行。
  改成宽度钉住、高度自适应。
- **Agent 输入区溢出**：附件+授权+模型+杯子+箱子需要约 414px，
  而面板只有 400px，流式布局会**直接裁掉尾部**（杯子和箱子整个看不见）。
  改成表格布局，模型列自己挤、挤不下给省略号；统计与状态提示拆回两行。
  附件列表三列之和 510px 也超过面板宽度，「来源」列被切掉，已改窄。
- 长文本补省略号收尾（模型名、附件说明、画布标签）。

### 六、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- **界面自检：0 处文字被切**。三个窗口尺寸（1360x820 / 1100x700+抽屉 / 1920x1000）
  以及接入弹窗（840x1332）逐控件比对，无一条超出。
- 实际启动程序：正常，无异常。
- **界面观感未经我亲自验收**：我看不到渲染结果，需要用户实际看一眼。

### 当前边界与未做

- **弹窗的字体仍是 96 DPI 名义值**（`new Font("Segoe UI", 10F)`）：
  按 DPI 缩放的是布局像素，字体按点是物理单位，两者现在是一致的；但弹窗没有跟随主题。
- **`AutoScaleMode = None` 是有意的**：如果将来有人把窗体交给设计器编辑，
  需要重新评估这条（设计器会自己写 `AutoScaleDimensions`）。
- **Popup 菜单（ToolStrip）不参与缩放**：它们的字体来自系统而不是窗体，
  所以菜单文字比界面其余部分小一号，不裁切但风格不统一。
- **自检工具已删除**：它是本轮定位问题的手段（`DREAMFORGE_UI_AUDIT`），
  不是产品功能。需要复查时可按本文档的"一、先量，不猜"重新接上。
- **ComboBox 编辑区、RichTextBox 正文、ToolStrip 菜单文字**：自检覆盖不到这三类，
  只能说没有看到问题，不能说已验证。

### 下一步

1. 把弹窗纳入主题（沿用上一轮欠账）。
2. 给滑出条加「固定展开」；ToolStrip 菜单跟随窗体字体。
3. 侧栏与 Agent 面板支持拖拽调宽（SplitContainer）。

## 2026-09-26 第六十九轮更新：所有配置文件改存到项目根文件夹

用户要求：**所有配置文件全部存在项目根文件夹**（原来散在 `%LocalAppData%\DreamForge`）。

### 一、改之前散落在哪

同一个目录被 9 处各自拼了一遍路径，其中 `last-canvas.json` 还拼了两遍（`MainForm` 与 `StorageMaintenance` 各有一份定义）：

| 内容 | 原路径 |
| --- | --- |
| 大模型配置 | `%LocalAppData%\DreamForge\ai-config.json` |
| 任务库 | `…\jobs.db` |
| 当前画布绑定 | `…\workspace.json` |
| 未保存的草稿画布 | `…\last-canvas.json`（两处重复定义） |
| 画布库 | `…\canvases\` |
| 资产 | `…\assets\` |
| 技能 / 插件 | `…\skills\`、`…\plugins\` |

### 二、新增 `AppPaths`：位置只有一个出口

- `AppPaths.Root`：**从可执行文件所在目录逐级向上找 `DreamForge.slnx`** 来定位项目根。
  找不到时（例如把程序拷出去当绿色包用）退回可执行文件所在目录，程序照样能跑。
- `AppPaths.Combine(name)`：项目根下的文件或目录。
- 上面 9 处全部改为走它，顺带消掉了 `last-canvas.json` 的重复定义。
- `DREAMFORGE_CONFIG` / `DREAMFORGE_JOB_DB` / `DREAMFORGE_CANVAS_DIR` / `DREAMFORGE_ASSET_DIR`
  / `DREAMFORGE_SKILL_DIR` / `DREAMFORGE_PLUGIN_DIR` 这些覆盖**保持不变**，各自仍然优先。

### 三、一次性搬迁，避免"东西没了"

`AppPaths.MigrateFromLegacyLocation()` 在启动最早期执行（`Program.cs`）：
把旧目录里的 4 个 JSON 与 4 个目录搬到项目根，**只在目标不存在时搬，绝不覆盖**；
搬完一次之后就是空操作。搬不动（权限 / 文件被占用）就当作没有旧数据，程序不受影响。

加它的理由很直接：改路径这件事本身不该让用户丢东西——
草稿画布、已有的画布与资产如果留在旧目录，用户会以为"东西没了"。

### 四、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- **实际启动程序后核对文件落点**（启动前项目根只有源码与文档）：
  启动后项目根出现 `ai-config.json`、`jobs.db`（20480 字节，SQLite 已建表）、
  `last-canvas.json`（202 字节，**与旧目录里那份字节数一致，说明搬迁成功**）、
  `plugins/`、`skills/`。
- **密钥安全未被削弱**：`ai-config.json` 里的密钥仍是 `dpapi:` 密文，磁盘上没有明文。

### 当前边界与未做

- **改动本身可能让用户"看起来丢了配置"**：新位置没有 `ai-config.json` 时，
  接入引导会重新弹一次（因为配置里没有 `ProviderChoiceMade`）。
  本轮启动时就遇到了这个情况：用户在弹窗里重新接入后，
  项目根下生成了新的 `ai-config.json`（密钥是 dpapi 密文）。
- **旧目录没有主动删除**：搬迁是复制不是移动，`%LocalAppData%\DreamForge`
  与更早的 `%TEMP%\dreamforge-demo\` 都还留着，需要用户自己清理。
- **项目根不是 git 仓库、也没有 `.gitignore`**：所以本轮不存在"密钥被提交"的风险。
  将来若把项目纳入版本控制，`ai-config.json`、`jobs.db`、`assets/`、`canvases/`
  必须先进 `.gitignore`。
- **`assets/` 可能与源码混在一起**：生成图会直接堆在项目根下的 `assets/`，
  量大时建议在「设置」里把资产目录指到别处（该入口一直有，也支持 `DREAMFORGE_ASSET_DIR`）。
- **项目根不可写时**：配置保存失败会走既有提示路径（"本次只在当前会话生效"），不会崩。

## 2026-09-26 第七十轮更新：修「Agent 对话黑底黑字」+ 对话区改自绘消息列表

用户反馈：**Agent 对话文字是黑色、底色也是黑色，看不见字**；
并要求**重构对话区、别用自带的文本框**。

### 一、黑底黑字的根因：懒创建的面板从来没被套过主题

启动顺序是「先把已建好的界面统一套色 → 再创建 Agent 面板」，而 `Theme.Apply` 只走当时
已经存在的控件树。于是 Agent 面板里的 `RichTextBox` 从未被套色，
`ForeColor` 停在系统默认的**黑色**，压在深色的 `FieldBg` 上——正好黑底黑字。

顺带发现插件面板有同一个问题（也是按需创建）。

修法：按需创建之后自己补一遍 `Theme.Apply(shell)`。
但这只是止血——真正让这类问题不可能再发生的，是第二节的重做：
**自绘控件每帧都从 `Theme` 取色，没有"存在自己身上的颜色"可以过期**。

### 二、对话区重做成自绘的消息列表（`ChatView.cs`）

上一轮的代码里其实已经写着答案：
> 「RichTextBox 没有原生折叠能力，真要 inline 展开得把对话区改成「结构化消息 + 整体重绘」」

这次就照它做了。删掉 `RichTextBox` 与那套"按字符下标反查点击位置"的脆弱做法，换成：

- **结构化条目** `ChatEntry`：发言者 / 强调色 / 角色（系统·用户·助手·错误）/ 正文 /
  思考全文 / 流式状态 / 收尾说明 / 附加提示。正文与思考分字段存。
- **卡片式绘制**：圆角卡片 + 左侧强调条（一眼看出谁在说话）+ 标题行（发言者 + 右侧"生成中…"）。
  用户、助手、错误用不同的卡片底色。
- **思考就地折叠**：折叠时是一行 `▸ 思考过程（N 字）`，点开在卡片里展开全文——
  不再有"限长截断"，也不再需要那个独立回看窗口。展开后按等宽字体画在嵌套的浅色框里。
- **自绘滚动条** + 滚轮 + 拖动滑块；流式追加时只有"本来就在底部"才自动跟到底，
  用户翻上去看历史时不会被拽回来。
- **右键菜单**：复制这条 / 复制全部对话 / 滚到底部——取代了原来能选中文本的 RichTextBox。
- **增量量测**：流式时只重量最后一条；卡片宽度一变（面板缩放、滚动条出现）才整体重量。
  否则每秒十几次的刷新会把没动过的几十条也量一遍。

### 三、输入区改成圆角 composer

- 外框 `ComposerPanel` 自绘圆角底 + 1px 描边，**聚焦时描边变强调色**。
- 里面的 `TextBox` 去掉边框、底色与外框一致，看起来不再是"系统自带的灰白方框"。
- **工具行嵌进同一个框里**：附件 · 授权模式 · 模型 · 上下文杯 · 缓存箱 · 停止 · 发送。
  原来这些是框外独立一行，现在合成一个整体（一体化 composer 形态）。
- **输入时自动长高**：1～5 行，超出滚动。行高按当前 DPI 折算。

为什么还留着 `TextBox`：中文输入法、选区、粘贴、Ctrl+Enter 是系统文本框几十年磨出来的行为，
自己实现一个编辑器只会更差。做法是"换掉系统控件的皮"，而不是重写编辑器。

### 四、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- **界面自检（临时工具，用完已删）新增了一项"对比度"检查**——把文字色和它实际压着的
  底色比一遍，距离太近就报出来。这正是"黑底黑字"这类问题的量化判据。
  结果：**0 处文字被切、0 处对比度不足**；对话区塞 4 条示例后量得内容高 727 / 可视高 630，
  说明量测与滚动都正常。
- 实际启动程序：正常，无异常。
- **界面观感未经我亲自验收**：我看不到渲染结果，需要用户实际看一眼。

### 当前边界与未做

- **对话区不支持鼠标选中文本**：自绘控件的代价。用右键「复制这条 / 复制全部对话」替代。
  如果确实需要划选，得自己实现选区与命中，不是小改动。
- **滚动条是自己的**：只有拖动与滚轮，没有"点空白翻页"这种细节。
- **`AgentAskDialog`（模型反问弹窗）仍是系统控件**：本轮的"框美化"只覆盖 Agent 面板本身。
- **`ChatView` 的字体在构造时缓存**：主题切换只换颜色不换字体，所以缓存是安全的；
  但将来若做"字号可调"，这里要跟着改。
- **`Theme.Apply` 仍是"启动时套一次"的模型**：本轮靠"按需创建的补一次"补上了窟窿，
  长期看更稳的是让新控件自己注册——没有做，因为改动面更大。
- **旧目录仍未清理**：`%LocalAppData%\DreamForge` 与 `%TEMP%\dreamforge-demo\` 需要用户自己删。

### 下一步

1. 弹窗（含反问弹窗）纳入主题与圆角风格。
2. 对话区支持划选复制。
3. 侧栏与 Agent 面板支持拖拽调宽（SplitContainer）。

## 2026-09-26 第七十一轮更新：修「马赛克 / 字叠字」

用户反馈：**左侧滑出的选项字体是无效重叠的字**，
**Agent 下面那排状态也是黑色马赛克和乱出的字**，**鼠标放上去会变好**。

### 一、「鼠标放上去会变好」把范围收窄了

悬停会触发该控件的重绘，重绘后就正常——说明**内容本身是对的，是画的过程有问题**，
不是字写错了。这类现象在 WinForms 自绘里只有两个成因：

1. **透明子控件压在自绘父容器上**。透明底不是真的透明，而是"请父控件把背景画到我身上"。
   父控件若用 `OnPaint` 画圆角与边框，那段绘制会以**父控件的坐标**落在**子控件的裁剪区**里——
   于是圆角弧、边框片段散落在小控件里，看起来就是"黑色的马赛克块"。
   输入框工具行里那排图标（附件 / 授权 / 杯子 / 箱子）正好是这个结构。
2. **重绘前没有清底**。自绘控件若不自己铺底，就依赖"父控件代画背景"，
   而重绘时旧内容不会被清掉，旧字留在下面就是"字叠字"。
   左侧滑出条每帧都在改宽度，裁剪区每帧都不一样，最容易出这个。

### 二、改法

- 新增 `Theme.Surface(panel, SurfaceRole)`：把一个容器标成"自绘容器里的填充块"，
  声明它该跟哪一层底色一致（编辑区 / 活动栏 / 面板）。套色时按声明的角色取色，
  不会被一律刷成面板底色。
- **圆角输入框内部的每一层都改成了不透明实心块**（`composerLayout`、`toolsRow` → 编辑区底色），
  活动栏那一列同理（→ 活动栏底色）。层级里不再有"透明底 + 自绘父容器"的组合。
- 新增 `RailNavButton.FillBack(...)`：自绘控件在 `OnPaint` 开头**先用父容器底色铺满自己**，
  等价于"不透明版的透明底"。`RailNavButton` / `IconButton` / `CupGauge` / `CacheBox` /
  `RailStrip` 都补上了——不再依赖父控件代画背景，旧内容也就不会被留在下面。
- **自绘控件统一补 `ControlStyles.ResizeRedraw`**：`RailStrip` 每帧改宽度、
  其余控件在 DPI 缩放那一遍会被改尺寸，不重绘整块就会留残影。
- `SetRailWidth` 里加 `railStrip.Invalidate(true)`：连子控件一起重绘，
  而不是只让条自己重画。

### 三、验证：给自检加了一条结构性判据

「马赛克」是渲染结果，量不到；但**成因是结构**，可以量：
自检新增一条检查——沿透明链往上找第一个不透明祖先，**如果它是自绘容器（ChatView /
ComposerPanel / RailStrip / 画布）就报出来**。这条判据能区分改前改后：
改前 `toolsRow` 是透明的，会一路找到自绘的 `ComposerPanel` 从而命中；改后停在
不透明的 `toolsRow` 上，不再命中。

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- 自检结果：**0 处"透明底压在自绘父容器上"、0 处对比度不足、0 处文字被切**。
- 实际启动程序：正常，无异常。
- **界面观感未经我亲自验收**：我看不到渲染结果，需要用户实际看一眼。

### 当前边界与未做

- **`Theme.Apply` 仍是"启动套一次 + 按需创建的补一次"**：自绘控件不受影响（每帧取色），
  但新加的系统控件如果忘了套色，还是会把颜色停在系统默认值上。
- **`SurfaceRole` 需要手动标注**：新写自绘容器时，里面每一层都要记得标，
  否则又会退回"透明底 + 自绘父容器"的组合。这条约束靠注释和自检工具兜着，没有编译期保障。
- **自绘控件的铺底是"读父容器 BackColor"**：如果哪一层父容器是透明的，
  `FillBack` 会退回面板底色，颜色可能不接。目前所有层级都是不透明的，所以不会触发。

## 2026-09-26 第七十二轮更新：入口线程 MTA（复制报错的真因）+ 五条界面问题

用户报了 5 条：输入框看不见、按钮没框没居中、Agent 不弹窗反问、不能选字复制、右键复制报错。

### 一、「右键复制报错」的真因：**主线程跑在 MTA 上**

这条本来最像小毛病，结果是最严重的一个。给自检加了一条环境探针，第一次跑就出结果：

```
[线程] ApartmentState=MTA（剪贴板与 OLE 对话框要求 STA）
[剪贴板] 写入失败：ThreadStateException: 必须将当前线程设置为单线程单元(STA)模式。
请确保您的 Main 函数带有 STAThreadAttribute 标记。
```

根因：`Program.cs` 用的是**顶层语句**，而顶层语句生成的 `Main` **不带 `[STAThread]`**。
界面本身还能起来（很多 WinForms 功能不检查单元状态），但凡是走 OLE 的东西都会抛异常：
**剪贴板（复制）、打开/保存文件对话框、拖放**——全部受影响。
换句话说，这个问题从第一天就在，只是直到用户点"复制"才暴露。

改法：`Program.cs` 换成显式 `Main` 并标注 `[STAThread]`。改完探针复测：

```
[线程] ApartmentState=STA
[剪贴板] 写入成功
```

### 二、对话区支持选字复制（自绘折行）

之前明确说过"自绘控件不支持划选"——用户要，那就补上。做法：

- **折行改由自己算**（`Wrap`）：贪心 + 二分找出一行最多放得下多少字，
  断在英文单词中间时回退到空格，中文按字断。
  为什么要自己算：只有知道每一行从哪个字符开始，才能把"点在某处"换算成"第几个字"，
  选中高亮也才能和绘制严格对齐（用 `TextRenderer` 的 WordBreak 画，是拿不到这个映射的）。
- **选区用（条目, 文本块, 字符下标）**表示，正文与思考各算一块；
  比较按"条目序号 → 块序号 → 块内下标"，绘制与复制共用同一份区间换算（`BlockSelection`），
  所以高亮范围和复制内容是同一份结果，不会出现"看到的和复制的不一样"。
- 交互：拖选、点击清空、`Ctrl+C` 复制选中、`Ctrl+A` 全选；
  拖到卡片外时光标吸附到最近的一条，选区不断。
- 右键菜单按有没有选区动态给出「复制选中 / 复制这条 / 复制全部对话 / 滚到底部」。
- 剪贴板写入改成 `SetDataObject` + 重试，真失败会明确报出来（而不是静默无反应）。

### 三、输入框与按钮（第 1、2 条）

- **输入框看不见**：两个原因——
  1. 描边用的是"线框色"（`#2E2E35`），在深色底上几乎不可见；改成 `TextDim`，聚焦时换强调色。
  2. 占位提示用的是系统 `PlaceholderText`，它的灰是给浅色底设计的，深色底上等于看不见；
     改成**外框自己画**（未聚焦且为空时画 `TextDim` 的提示）。
- **控件之间要有线条**：输入区与工具行之间加了一条分界线；对话卡片本来就有描边。
- **按钮没有框、内容没居中**：`IconButton` 重画——每个按钮一个圆角框（悬停提亮描边），
  **图标与文字作为一整组居中**，不再是"图标贴左、文字跟在后面"。
- **缓存图标要在缓存显示前面**：把缓存命中箱从输入框那一行**移回统计行**，
  放在"缓存 x%"文字之前（原本用户最初的要求就是箱子在下面那一行，上一轮我放错了地方）。

### 四、反问弹窗（第 3 条）

- 弹窗**始终带一个「其他（在下面自己写）」**选项：模型给的选项再全，也总有它没想到的答案；
  选中它光标自动跳到下面的输入框，选了却没写会明确提示。
- 协议里"信息不足必须反问"这条写得更硬，并点明**只回一句文字问句等于没做**：
  用户看不到弹窗、也没有可选项。例如"新建一个节点"这种没给全信息的请求，
  必须输出 ask 块。

（说明：弹窗本身一直是通的——`RunTurnWithAsksAsync` 在拿到 `parsed.Ask` 时会弹。
这次没弹是因为**模型回了文字而不是 ask 块**，属于遵约问题，所以从协议上收紧。）

### 五、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- 自检：**线程 STA ✓、剪贴板写入成功 ✓、0 处透明底压自绘父容器、0 处对比度不足、
  0 处文字被切**；对话区 4 条示例量得内容高 514 / 可视高 630。
- 实际启动程序：正常，无异常。
- **界面观感未经我亲自验收**：我看不到渲染结果，需要用户实际看一眼。

### 当前边界与未做

- **折行是自己实现的**：CJK 按字断、拉丁文回退到空格，但没有连字符、禁则处理
  （比如行首不能是标点）这类排版规则。
- **选区不支持跨"折叠着的思考块"**：折叠时思考不在布局里，自然选不中；
  展开后可以正常选。
- **`[STAThread]` 这条是补历史欠账**：之前所有需要 OLE 的功能
  （文件对话框、拖放）都在 MTA 下"能跑但不合规"，现在才真正合规。
- **`Program.cs` 不能再改回顶层语句**：改回去 MTA 就会回来，注释里写明了原因。

## 2026-09-26 第七十三轮更新：输入框结构修错 + 协议修复重试

用户五条反馈：还没弹窗、输入框有问题（文字区大小）、没输入时没有提示、
输入框没有线条圈起来、几个图标的比例与位置。

### 一、输入框的三个缺陷（都是上一轮我引入的）

自检这次被逼着又加强了一步——**必须先把窗体 Show 出来**：
控件没显示时 `Visible` 一律为 false，而"跳过不可见控件"的过滤会让整份检查**静默失效**。
上一版就是这样：报告里干干净净，其实一条都没检查。修正后立刻抓到三处：

1. **输入区只有 150x45**。上一轮我把 `input.Dock = DockStyle.Fill` 那句连同旧代码一起删了，
   输入框退化成默认大小的小方块浮在左上角——用户说的"文字区大小有问题"就是这个。
   修完实测：`输入区 554x37`（字体行高 25，余量 12）。
2. **提示文字看不见**。原因比上一轮判断的更根本：提示**画在外框上**，
   而外框被里面那层不透明的容器整块盖住，画上去也永远看不到
   （输入框本身也是不透明的，把提示盖在它下面同样无效）。
   改成真正的子控件：输入框上面盖一个只读提示标签，空输入时显示、点它等于点输入框。
3. **外框的线条看不见**。同一个根因：内层容器 `Dock=Fill` 把圆角与描边整块盖掉了。
   改成外框留内边距（`Padding = 12,9,12,9`），内层被内缩，圆角与描边才露出来。

顺带修掉两处自检报出的真实裁剪：**模型名放不下**（挤成省略号，上限收到 10 字符）、
**画布标签文字超宽折行被切**（标签宽度 176 → 210）。

### 二、图标与比例（第 5 条）

- **水桶移到"缓存 x%"文字之后**：上一轮我把它放在了输入框那一行，位置本来就不对；
  现在回到统计行，且在文字右边。
- **上下文杯子太小**：26px → 34x30，并且**按控件尺寸等比绘制**（原来是写死像素，
  控件放大了图标还是那么大）。水桶同理。
- **发送改成画笔图标**：新增 `RailIcon.Brush`，并给 `IconButton` 加 `Highlight`
  强调态（实心强调色 + 白图标），一排描边按钮里一眼能认出主要动作。
- **授权/模型按钮**：`IconButton` 现在每个都画圆角框，**图标与文字作为一整组居中**。

### 三、还是不弹窗（第 1 条）

先排除了我这边的问题：`AgentActionParser` 能正确识别纯 ask 块（`{"ask":{...}}` 单块、
与 actions 共存都能解析），`RunTurnWithAsksAsync` 拿到 `parsed.Ask` 就会弹窗。
所以是**模型没按协议输出 ask 块，而是把问句写进了正文**。

这个我从模型侧改不动，但可以补一层**协议修复重试**：
如果模型这一轮既没有改动块、也没有 ask 块，而用户明显是在要求改动
（命中"新建/删除/修改/连到/生成"这类动词），就**自动请它按协议重来一次**，
并在对话区里明说补了什么。只补一次，避免来回空转。
这比让用户自己再问一遍有用，也不需要我猜用户的意图。

### 四、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Core.Tests`：17 项通过。`DreamForge.Agent.Tests`：37 项通过。
- 自检：线程 STA ✓、剪贴板写入成功 ✓、输入区 554x37 ✓、提示可见且放得下（301/554）✓、
  0 处文字被切、0 处对比度不足、0 处透明底压自绘容器。
- 实际启动程序：正常，无异常。
- **界面观感未经我亲自验收**：我看不到渲染结果，需要用户实际看一眼。

### 当前边界与未做

- **协议修复重试只做一层**：模型若第二次仍不按协议输出，就不会再补，
  只在状态行说明"本轮没有改动提议"。
- **协议修复靠动词表判断**（`LooksLikeChangeRequest`）：用户用别的说法要求改动时不会触发；
  反过来也可能在闲聊里被"生成"这类词误触发一次空跑。
- **自检工具每次都删**：它已经连续四轮抓到关键问题（DPI、MTA、马赛克结构、输入框退化）。
  建议把它正式留下来（`DREAMFORGE_UI_AUDIT` 环境变量开关），但用户没要求，所以仍然删掉了。
- **提示标签盖在输入框上**：点击会先落到标签再转交焦点，光标位置由 TextBox 自己决定，
  点到文字中间不会把光标放到那一位（系统文本框的能力，自绘标签代替不了）。

## 2026-09-26 第七十四轮更新：查出「Agent 加的节点没加上」的真因

用户贴来了真实对话全文（让 Agent 建角色节点，两次弹窗都答了，最后给了改动块，但节点没出现）。

### 一、真因：改动块里的引号没转义，整块 JSON 非法

用户贴的样本里，模型最后输出的是：

```
{"actions":[{"kind":"create_entity",...,"content":"…他是唯一能"读"封印档案的人；…不靠"有用"来换取留下。…"}]}
```

content 里直接写了**英文双引号**（中文文案里很自然），整块 JSON 因此不是合法 JSON。
而 `AgentActionParser` 当时的逻辑是"解析不了就当没有协议块"，
于是两个后果同时发生：**整块 JSON 被当成正文糊在对话区里**（用户看到的那些 JSON），
**改动全部作废**（用户说的"没有加上"）。

这是把"格式错"误当成"没写"。归到根上是我这边的容错不够：
模型写中文文案时随手敲英文引号是常态，不该由它自己去发现。

### 二、改法

1. **解析容错**（`EscapeInnerQuotes`）：先按原样解析，失败就把"内容里的裸引号"转义后再解析一次。
   判据是——字符串内部遇到的引号，只有它后面第一个非空白字符是 , } ] : 或到了结尾时，
   才有资格当结束引号；否则只能是内容里的引号。`IsProtocolBlock`（定位块用）走同一套容错，
   否则"块写坏了"仍会被误判成"没写块"。
2. **区分两种失败**：`AgentReply.ProtocolBroken` —— 出现了协议标记但整块不可解析。
   这和"压根没写协议块"是两回事，提示语与重试理由都分开说。
3. **协议修复重试的触发条件**从"猜用户是不是在要求改动"换成优先看 `ProtocolBroken`
   （确定性判据），只有没有坏块时才回退到动词表启发式。
4. **协议本身**补一句硬要求：字符串内容里的引号必须用中文引号或转义，换行写 `\n`——
   并把"否则你的改动会全部作废、用户会说'没加上'"写进去，让模型知道代价。
5. **审批两步写进提示语**：改动块解析成功后，对话区明说
   「点『加入待提交』→ 再点『提交并保存』，改动才真正写入画布」。
   原来只写"见下方待审批列表"，用户未必知道还要再点两下。

### 三、用真实样本做回归测试

把用户贴的那段原文（含未转义的引号）原样加进 `DreamForge.Agent.Tests`：
`协议块：内容里的裸引号仍能解析（真实样本）`。这条测试在改之前必然失败，
改之后验证：1 条 `create_entity`、标题与内容完整、内容里的引号原样保留、协议块从展示文本里剥离。

**测试数 37 → 38**。

### 四、本轮验证

- `dotnet build DreamForge.slnx --no-restore`：通过，0 个警告，0 个错误。
- `DreamForge.Agent.Tests`：38 项通过（含新增的真实样本回归）。`DreamForge.Core.Tests`：17 项通过。
- 实际启动程序：正常，无异常。

### 当前边界与未做

- **引号修复是启发式的**：极端的嵌套/转义写法仍可能修不出来，那时会走
  `ProtocolBroken` → 自动请模型重来一次这条路。
- **`FindMatchingBrace` 仍是严格扫描**：样本里内容中的引号是成对的，所以括号配对没受影响；
  若模型写出不成对的裸引号，会定位失败并判为坏块（然后重试），不会静默错位。
- **改动仍要用户点两下才生效**：这是既定设计（模型不能直接改用户画布），
  本轮只是把"要点两下"写进了提示语，没有改成自动提交。


## 2026-09-27 第七十五轮更新：设定库接上工作树（叙事轴引入）

> 本轮为**事后补记**：当时未更新本文件，下列事实依据文件创建/修改时间与当日画布存档还原（`WorkTree.cs` 创建于 09-27 03:00，`WorkflowEntities.cs` 同日 05:31 修改；画布存档 09-27 08:45）。

### 已完成

- 新建 `WorkTree.cs`：`WorkTreeKind`（Project / Character / Skill / Prop / Scene / Version）与 `WorkTreeItem`（`ParentId`、`Kind`、`Name`、`Chapter`、`Version`、`Prompt`、`SourceEntityId`、`SourceVariantId`、`SourceVersionId`、`SupersedesVersionId`、`Attachments`）。
- `WorkflowEntities.cs` 同期补上互指字段，并保留设定库自身的变体/版本机制（`Commit` / `VersionDiff` / `RollbackTo`）。
- 桌面端新增"工作树"面板（树视图 + 叙事设定详情 + 素材列表）与版本继承显示（"v1 → v2"）。
- Agent 新增 `create_work_item` / `update_work_item` / `delete_work_item`、`create_entity_version` 等动作。

### 本轮验证

- 编译与运行通过；当日画布存档为 **48 节点 / 35 连线 / 10 个设定库实体 / 工作树 0 条**。

### 遗留问题

1. **工作树的语义没有写进任何文档**：01–04（09-25 写）仍在讲"六层架构 + Avalonia WebView + 房间服务"，本文件也停在第七十四轮。
2. **"技能"一词同时指两件事**：工作树里的 `Skill`（角色能力）与 `skills\*.json`（生成技能）同名，提示词里同段出现，模型必然混用。
3. **重复存内容**：工作树条目上的 `Prompt` / `Attachments` / `Version` 与设定库的变体/版本各存一份，没有边界说明。
4. **节点与工作树之间没有键**：节点只有 `Chapter` 字符串，工作树也没有指回节点的字段。

### 下一步

确立两轴分工、给节点与工作树补稳定关联（即第二天第七十六轮）。


## 2026-09-28 第七十六轮更新：两轴分工 · 命名消歧 · 节点↔工作树关联 · 生成流水线

### 已完成

1. **命名消歧**（`WorkTree.cs`、`AgentActions.cs`、`MainForm.cs`、`AgentPane.cs`）
   - `WorkTreeKind.Skill` → `WorkTreeKind.Ability`，**枚举位置不变（值仍为 2）**，画布按数字落盘，旧数据无需迁移。
   - 新增 `WorkTreeItem.KindName`（项目/角色/能力/道具/场景/版本）与 `ParseKind`（继续接受旧协议的 `"Skill"`/`"技能"`）。
   - 内置技能清单改称"可调用的**生成技能**清单"，并注明与工作树里的角色能力无关。
   - 审批列表显示中文种类（如"工作树新增能力「御剑术」"）；工作树面板与给模型的摘要把"提示词"改为"叙事设定"。
2. **章节锚**：新增 `WorkTreeKind.Chapter`（**追加在末尾，值为 6**，不动既有数值）。
3. **节点↔工作树补键**
   - `WorkflowNode.WorkTreeItemId`（`[JsonIgnore(WhenWritingNull)]`，旧画布兼容）。
   - `AgentAction.WorkTreeTarget` + 协议字段 `workTreeTarget`；`create_node` / `update_node` 落盘时写锚，节点章节文本缺失时用条目补齐。
   - `Precheck` 增 `create_node` 分支与 `update_node` 校验：要关联的工作树条目不存在时提醒"先把章节/能力写进工作树，再建节点"。
   - `PendingChanges.NodeChanged` 纳入 `WorkTreeItemId`，只改锚也会出现在待提交列表。
   - 给模型的工作树摘要带上短 id，使 `workTreeTarget`/`parentTarget` 可精确定位。
4. **生成流水线写进提示词**（`AgentPane.cs`）：①确认剧情（**不改画布也不改工作树**，等用户确认）→②建工作树（Chapter→Character→Ability→Version）→③建视觉骨架（设定库实体）→④建节点（`parentTarget` + `workTreeTarget` + `entityTarget`）；②④**分两批提议**；同步**单向**（工作树变了才更新节点）；两套载体不互相抄；画布上的角色/场景/道具节点只做锚点。

### 本轮验证

- `dotnet build DreamForge.Desktop\DreamForge.Desktop.csproj` → **成功，0 警告 0 错误**。
- 数据结构改动向后兼容：`WorkTreeKind` 只改成员名与追加成员，`WorkTreeItemId` 可空且不写 null。

### 遗留问题

- **没有自动同步引擎**："工作树改了以后一键重排/更新对应章节簇节点"尚未实现，目前只有键与流程约束。
- `PlaceNewNode` 仍未按 `Category` 分层（资源节点与主线节点共用同一套落位规则）。
- `AutoStage` 授权路径会把同一批 actions 应用两次（`ApplyActions` 后 `SaveApplied` 又执行一次）。
- `MarkVersionAdopted` 在 `ReadActions` 中未解析，模型无法设置。

### 下一步

按实际代码校正全部设计文档（即第七十七轮）。


## 2026-09-28 第七十七轮更新：按实际代码校正全部设计文档

### 已完成

- 复核 `DreamForge.Core` / `Host` / `Web` / `Mcp` / `Desktop` 与 `DreamForge.Canvas` 的实际代码，重写四份设计文档：
  - `01_Project_Plan.md`（v6）：产品决策逐条标注实现状态；技术选型改为 WinForms 自绘画布；六层架构替换为实际分层；资产体系改为实际载体；AI 助手改为"一个面板 + 三种授权"；风险与待办按现状改写。
  - `02_Architecture.md`（v2）：项目清单与真实引用关系、进程拓扑、**两套协议**（宿主协议 vs Agent 协议）、执行链路（Job 状态机/单机执行服务/SQLite/ComfyUI/回调签名）、数据模型（字段级 + 枚举数值顺序 + 旧字段迁移）、文件布局、AI 与生成、安全边界，以及**文档与实现仍不一致的 9 项清单**。
  - `03_Skill_System.md`（v2）：先分清三个"技能"（生成技能 / 内置技能 / 角色能力），再写生成技能的数据模型、装载、执行流程与 `outputTarget`、插件体系、版本现状，并列出未实现项。
  - `04_Architecture_Mindmap_Mermaid.md`（v2）：Mermaid 与列表版全部按现状重画。
- 本文件：修正头部基准与《总体进度》阶段表，新增《实现现状基准（2026-09-28 校正）》，补记第七十五～七十七轮。
- **产品名统一**：文档中的旧产品名全部改为 **YEEYEEYEE**（寓意 **YES 工程师 · YES 艺术家**）；工程标识 `DreamForge`（程序集/解决方案/协议名）保持不变。

### 本轮验证

- 文档中每条事实都能在代码里找到依据（文件与行号见 `02_Architecture.md` 各节）；未使用任何"注释里的设想"作为事实。
- 关键校正结论：桌面端已移除 WebView2 且未接入 TS 画布；`DreamForge.Web` 无画布；**视频生成未实现**；`DreamForge.Mcp` 未接入 Desktop 且不在 slnx；根目录 `05_Core_Contracts.cs` 未参与编译且与 Core 不兼容；协作/账号/审批/配额/审计均未开始。
  > 其中"未接入 TS 画布 / Web 无画布"已被**第七十八轮**推翻：Web 现在托管 TS 画布并转发桌面端推送的投影；本行保留为该轮的历史结论。

### 遗留问题

- 文档已一致，但代码里的 8 项不一致（见 `02_Architecture.md` 第十节）仍需逐个处理，其中优先：删除或归档 `05_Core_Contracts.cs`、修 AutoStage 重复应用、统一界面文案大小写（`YeeYeeYee` → `YEEYEEYEE`）。

### 下一步

实现"节点↔工作树"的自动同步引擎；`PlaceNewNode` 按 `Category` 分层排版。


## 2026-09-28 第七十八轮更新：Web 画布通道 · 章节分块排版 · 资源引用新写法

> 本轮为**事后核对补记**：这批代码改动先于文档落地，本轮把 01–04、README、`protocol/PROTOCOL.md` 与本节一起补齐。

### 已完成

1. **桌面 ↔ Web 画布通道**（新增 `DreamForge.Desktop\Canvas\NodeProjection.cs`；改 `MainForm.cs`、`DreamForge.Web\Program.cs`、`WebCanvasTransport.cs`、`HostBridge.cs`）
   - `NodeProjection.ProjectRecords`：`WorkflowNode` → 协议 records（`recordType` 按 `NodeCategory` 分层映射，`record` 带 `title/content/x/y/chapter/status/parentId/references[]`）；工作树里没有对应节点的 `Chapter` 条目投影成 `wt-<id>` 的 L3 record。
   - Desktop：新增 `PushCanvasToWebAsync`（画布变更 / 载入 / 切章 / 替换版本时 `POST /api/canvas/scene`）与 500ms 计时器里的 `PollResourceReplaceRequests`（`GET /api/canvas/resource-replace/next`）；地址是常量 `http://localhost:5000`，失败静默。
   - Web：`UseStaticFiles` 托管 `..\DreamForge.Canvas\dist`、`MapGet("/")`、`/ws/canvas` 与 `/api/canvas/scene|nodes|resource-replace[/next|/result]`；`WebCanvasTransport` 从 `Console.WriteLine` 改为 **WebSocket 广播**（新连接补发最近一帧 `host/scene.reset`）；新增 `appsettings.json`（Kestrel `http://localhost:5000`）。
2. **协议新增两条消息**（`Core\Protocol.cs`、`Host\HostBridge.cs` + TS 侧同构）
   - `canvas/resource.replace.request`（`recordId`/`entityId`/`variantId`/可空 `variantVersionId`，uuid 与字段严格校验）与 `host/resource.replace.result`（`requestId`/`ok`/`message`/`revision`）；消息总数 15 → **17**。
   - `HostBridge` 新增 `ResourceReplaceRequested` 事件、`SendScene`、`SendNodeUpdates`、`SendResourceReplaceResult`，以及 `RESOURCE_REPLACE_UNAVAILABLE`/`RESOURCE_REPLACE_FAILED` 两条错误回传。
   - `WorkflowCanvasState.ReplaceReferenceVersion`：节点不存在/已锁定、引用不存在、版本不属于变体一律拒绝；成功只改 `NodeReference.VariantVersionId`（`null` = 跟随最新）。
3. **画布自动排版改按章节分块**（`WorkflowCanvasControl.cs`）
   - 新增 `ChapterKeyOf` / `GroupByChapter` / `ChapterBounds` / `ArrangeChapter` / `ChapterBlockWidth` / `NodeHeightFor` / `IsResourceCategory` / `DrawChapterBlocks`。
   - `AutoArrange` 由"按连线深度分列"改为"每章一个区块、块内 3 列网格、主线在前资源在后、区块每行 3 块"，并绘制块底与虚线框；右键菜单改为"整理画布（按章节分块）"。
   - Agent `PlaceNewNode` 复用同一套函数：新节点落进所属章节区块并整块重排，不再用固定 190×100 硬避让（这替代了原待办里的"按 Category 分层排版"）。
4. **节点分类与引用写法**（`WorkflowCanvasControl.cs`、`AgentActions.cs`、`MainForm.cs`、`AgentPane.cs`）
   - `NodeCategory` 追加 `StoryPlan=6 / StoryOutline=7 / Chapter=8`（旧数据不受影响）；`ParseNodeCategory` 改为关键词包含判定（人物/主角/镜头/成片…），界面下拉加"剧情/企划/章节"。
   - `AgentAction.EntityTargets` + `ReadStringArray` + `ApplyReferences`：`entityTargets` 一组设定名各跟随首个变体与当前版本（重复去重），需要锁变体/版本时才用单条 `entityTarget`；action 种类 12 → **13**。
   - **提示词改写**：角色/场景/道具**不再建画布节点**（只存在于设定库，节点用 `entityTargets` 引用）；分批规则改为"默认先树后节点，但用户明确要求生成节点时，当次回复必须给出 `create_node` 批次"。
5. **Web 画布 UI**（`CanvasApp.tsx`、`VersionedMessages.ts`、`main.tsx`、`workflow.css`）
   - 五层分带（L1 剧情 / L2 企划 / L3 章节 / L4 分镜头 / L5 成品）+ 章节过滤 + 概览视图 + 视口状态存 `localStorage`。
   - 节点卡片内引用条：缩略图、种类标签、折叠、版本下拉（跟随最新 / 锁定某版）→ 发 `canvas/resource.replace.request`；右侧检查器显示引用详情与"定位资源库"。
   - `main.tsx` 增加 WebSocket 传输（非 WebView 环境），WebView 分支保留。
6. **设定三个进度目标**：目标 1「AI 自动全套流程跑通」、目标 2「Web 端完成」、目标 3「Web / Desktop 多端协同」，含验收条件与现状（见本文件《进度目标》与 README 同名小节）。

### 本轮验证

- `dotnet build DreamForge.slnx` → **成功，0 警告 0 错误**（6 个项目）。
- `dotnet run --project DreamForge.Core.Tests` → **19 项全部通过**（含新增"资源版本替换协议""资源版本替换状态"）。
- `dotnet run --project DreamForge.Agent.Tests` → **46 项全部通过**。
- TS 侧未跑 `npm run build` / `npm test`（本轮只核对代码，未构建前端）；`dist` 未生成，Web 端静态托管需先本地构建。
- 文档侧：01（v6.2）、02、03、04（v2.1）、README、`protocol/PROTOCOL.md`、本文件已按上述改动同步。

### 遗留问题

- 新增两条协议消息**没有 `protocol/fixtures` 夹具**，违反本文件更新规则第 4 条，需补夹具与 manifest 条目。
- Web 画布的 `thumbnailRef` 仍是 `asset://文件名`，Web 没有资产 HTTP 端点，浏览器加载不出引用缩略图。
- `POST /api/canvas/nodes`（`HostBridge.SendNodeUpdates`）没有调用方；浏览器回传的 `canvas/selection.changed`（含 `entityId`/`openResourceLibrary`）在 `HostBridge.Receive` 里没有分支，"定位资源库"未生效。
- 仍然存在：`05_Core_Contracts.cs` 未参与编译、`AutoStage` 重复应用同一批 actions、`MarkVersionAdopted` 未解析。
- Web 画布的数据全部来自桌面端推送（桌面端未启动时浏览器画布为空）；Web 端唯一能回写的操作是替换/锁定引用版本，节点编辑仍在桌面端。
- 仍缺"节点↔工作树"的自动同步引擎。

### 下一步

按《进度目标》推进：目标 1 先做"节点↔工作树"自动同步引擎与端到端跑通用例；目标 2 补 Web 画布的资产 HTTP 端点、`selection.changed` 回传与 `dist` 构建流程；同时给资源替换两条消息补协议夹具。








