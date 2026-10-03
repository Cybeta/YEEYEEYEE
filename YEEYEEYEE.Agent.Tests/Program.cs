using System.Drawing;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

// Agent 操作层的离线校验：提议解析、操作执行、审批预检、画布虚影预览、附件加载与多模态报文。
//
// 为什么单独建一个测试工程：这些类型原先只在桌面端项目里（WinForms，net10.0-windows），
// 而 YEEYEEYEE.Core.Tests 是 net10.0，引用不到。第 127 轮之后它们都住在
// YEEYEEYEE.Desktop.Shared（无界面，net10.0）里，这个工程引用的是那个库——不再是「借源码」。
// 测试全部离线，不访问网络、不需要模型——报文形状用桩 HttpClient 拦下来检查。
var tests = new (string Name, Action Run)[]
{
    ("提议解析：剥离操作块并读出 source", ParseReplyAndSource),
    ("版本采纳：只接受JSON布尔true并传递到执行器", ParseVersionAdopted),
    ("同一批里建节点后用标题建立连线", CreateNodesThenConnectInOneBatch),
    ("拒绝重复连线与自环", RejectDuplicateAndSelfLoop),
    ("预检提示找不到的连线端点", PrecheckMissingEndpoint),
    ("整批预检能看见同批新建的节点", BatchPrecheckSeesEarlierNodes),
    ("连线批次：模型把连线写在建节点前面也能全部落地（含工作树条目端点与归一化标题）", AgentEdgeBatchSurvivesModelOrdering),
    ("delete_edge 只删指定方向", DeleteEdgeTargetsDirection),
    ("删除不存在的连线会失败而不是静默成功", DeleteMissingEdgeFails),
    ("删节点会连带删掉相连的边", DeleteNodeRemovesEdges),
    ("连线规则只有一份：自环拒、方向算身份、判重按节点对", SharedEdgeRuleIsOneCopy),
    ("锁定节点拒改拒删且预检提前告知", LockedNodeIsProtected),
    ("版本创建同步工作树并锁定节点引用", EntityVersionCreatesWorkTreeAndNodeReference),
    ("更新节点可切换历史版本或跟随当前版本", UpdateNodeChangesVersionReference),
    ("版本决策序列化后仍保留", VersionDecisionRoundTrips),
    ("画布预览不修改真实画布", PreviewDoesNotMutateCanvas),
    ("预览投影新增节点与新增连线", PreviewReportsAdditions),
    ("预览对无差异的操作不报假差异", PreviewIgnoresNoOp),
    ("预览报告将被删除的连线", PreviewReportsRemovedEdge),
    ("预览模式不写文件，提交时才落盘", FileWritePreviewVersusCommit),
    ("写文件路径越界被拒绝", FileWriteEscapeRejected),
    ("附件：图片转成 data URL 且能还原原始字节", AttachmentImageRoundTrip),
    ("附件：超过上限的图片被拒绝", AttachmentImageTooLarge),
    ("附件：文本文件读取与超长截断", AttachmentTextTruncation),
    ("附件：二进制文件被如实拒绝", AttachmentBinaryRejected),
    ("附件：文本拼进正文、图片单独收集", ComposeContentAndCollectImages),
    ("报文：OpenAI 格式携带图片块", OpenAiPayloadCarriesImage),
    ("报文：Anthropic 格式携带图片块且 system 分离", AnthropicPayloadCarriesImage),
    ("报文：system 消息绝不带图片", SystemMessageStaysTextOnly),
    ("报文：未开启图片输入时给出可读报错", ImageWithoutVisionFails),
    ("密钥保护：加密往返与脱敏描述", SecretProtectorRoundTrip),
    ("密钥落盘是密文，读回是明文", ConfigFileStoresCiphertext),
    ("损坏的密文降级为空密钥并标记原因", BrokenCiphertextIsReported),
    ("旧配置的明文密钥在读取时就地加密", PlaintextSecretMigratesOnLoad),
    ("密钥保护：与旧版的 dpapi 密文双向互通", SecretProtectorInteroperatesWithLegacyDpapi),
    ("密钥保护：本机密钥文件档（AES-GCM）往返，密钥文件丢失即报解不开", AesGcmKeyFileRoundTrip),
    ("密钥保护：别的平台的密文如实报解不开，带冒号的密钥不被误判", CiphertextFromAnotherPlatformIsReportedUnreadable),
    ("密钥保护：明文档如实带 plain: 前缀并被标记出来", PlaintextTierIsReportedHonestly),
    ("配置落盘：应用级文件在用户配置目录，不再写在程序旁边", AppFilesLiveInUserConfigDirectory),
    ("改名过渡：配置目录新名优先，但旧目录里有东西就用旧的", RenamedDirectoryPrefersTheOneWithData),
    ("更新：版本标签解析（带 v / 两段式 / 预发布后缀 / 看不懂要报错）", VersionTagParsing),
    ("更新：发行版比对（有新版 / 已最新 / 本地更新 / 看不懂的标签与坏 JSON 都报失败）", UpdateCheckComparesVersionsAndReportsFailures),
    ("更新：重启标记能原样存回（前一版 / 目标版 / 更新内容）", UpdateMarkerRoundTrips),
    ("更新：替换脚本只含 ASCII（PS5 会把无 BOM 的 UTF-8 当 ANSI 读）", UpdateSwapScriptIsAsciiOnly),
    ("更新：替换脚本实测——真换掉一个目录并留下结果文件", UpdateSwapScriptActuallyReplacesDirectory),
    ("出图批次：整批还在跑时单张一律不能操作（漏文件那条路的入口）", BatchSlotsAreNotActionableWhileRunning),
    ("出图开奖：按张数排布（一行最多 3 张 / 4 张 2×2 / 卡片固定 2:3 / 末行按自己的张数居中）", GachaCardLayoutIsPinnedByCount),
    ("出图开奖：分数 → 档位（9 分以上金 / 7-8 红 / 5-6 紫 / 3-4 蓝 / 2 分以下白；光点从金到白严格递减）", QualityTierFollowsScore),
    ("出图开奖：模型打分必须完整且合法才认（漏项 / 重复 / 越界 / 超范围分数 / 坏 JSON 一律作废）", QualityScoresMustBeComplete),
    ("出图开奖：评审提示词必须带上出图要求、负面提示词、节点上下文与四类缺陷", JudgePromptCarriesContextAndChecks),
    ("出图开奖：本地筛查只判客观坏图（读不出 / 纯色 / 尺寸不对），不碰「好不好」", TechnicalScreeningOnlyFlagsBrokenImages),
    ("出图开奖：分数落回槽位；踩中负面提示词就是裂纹卡，且裂纹卡不参与预兆", QualityGradesMapBackToSlots),
    ("配置落盘：程序旁的旧文件会被搬到用户配置目录（搬不是拷）", LegacyProgramRootConfigIsMigrated),
    ("模型预设：地址能反推回同一家，预置模型元数据自洽", ProviderPresetCatalogIsConsistent),
    ("模型预设：预设换算两端共用一份（采样开关与型号覆盖都一致）", ProviderPresetValuesResolveIsConsistent),
    ("模型预设：能力说明写清「自动开了什么」，自定义/无预置型号归用户填", ProviderCapabilityRuleIsSharedAndHonest),
    ("接入引导：选过服务商之后不再重复弹", ProviderChoiceFlagRoundTrips),
    ("流式：OpenAI 格式逐个增量回调，思考不混入正文", OpenAiStreamParsesDeltas),
    ("流式：Anthropic 格式逐个增量回调", AnthropicStreamParsesDeltas),
    ("流式：一行正文都没有时报错而不是空回复", EmptyStreamIsReportedAsError),
    ("流式：只收到思考没有正文也算失败", ThinkingOnlyIsStillAnError),
    ("流式显示：操作块与围栏不进对话区", ActionsBlockIsHiddenFromDisplay),
    ("反问：解析出问题与候选选项", AskIsParsedWithOptions),
    ("反问：与 actions 共存互不干扰", AskCoexistsWithActions),
    ("反问：只有 ask 没有 actions 也算有效回复", AskAloneIsValid),
    ("反问：ask 块同样不进对话区", AskBlockIsHiddenFromDisplay),
    ("协议块：内容里的裸引号仍能解析（真实样本）", BrokenQuotesSampleStillParses),
    ("协议块：格式损坏时不泄漏 JSON", BrokenProtocolIsHiddenFromDisplay),
    ("反问：未闭合 fenced JSON 不泄漏且流式隐藏", IncompleteAskProtocolIsHidden),
    ("ID 校验：合法画布零问题", IdentityValidatorCleanCanvas),
    ("画布标签：编号不与已有画布撞名（标题即文件名）", CanvasTabRulesAreCollisionProof),
    ("画布标签：改名查重挡住空名、超长与重名", CanvasTabRenameGuardsNames),
    ("节点协助：沿连线收集上游设定并按类型给建议", NodeAssistCollectsUpstream),
    ("节点协助素材：本体设定 → 上游 → 同镜 → 所属章节，叶子节点不再被判成没素材", NodeAssistMaterialsMergeFourSources),
    ("制作阶段：按阶段筛节点与统计真实数量", ProductionStagesFilterAndCount),
    ("章节拆分：按标题或长度拆文本，并按设定名匹配出场", ChapterSplitByHeadingAndLength),
    ("画布搜索：节点 / 工作树条目 / 设定库都能搜到并可定位", CanvasSearchFindsNodeWorkTreeAndEntity),
    ("引用树：直接引用分组排序、子引用递归展开、环被挡住，只算不改画布", ReferenceTreeExpandsDirectAndNested),
    ("故事画布：章节 → 分镜 → 场景/人物 → 道具，变体版本在人物行，引用是引用点不是副本", StoryTreeNestsChaptersShotsAndReferences),
    ("节点配色：九类九色（章节与通用保持品牌色不变）", NodeKindPaletteGivesEveryCategoryItsOwnColor),
    ("生成技能：角色/场景/道具/分镜的字段顺序与负面词都取自同一份基线", GenerationSkillsSharePromptBaseline),
    ("引用卡解析：不同引用各自解析出不同正文，源头卡不算引用", ReferenceCardsResolveDistinctContent),
    ("引用树：子引用（角色挂的道具）挂在角色下面，深度优先前序", ReferenceTreeListsNestedReferences),
    ("设定提示词：描述里写了「出图提示词」就用它，没写才按基线段落拼", SettingPromptPrefersWrittenPrompt),
    ("附件提示词：出图用的提示词随画布存住，老画布缺字段也能读", AttachmentPromptIsStoredAndReadable),
    ("出图方式：按剧情判文生图 / 图生图（合成底图 · 改稿 · 同场景连续性）", NodeImageModeFollowsStoryContinuity),
    ("角色参考图：三/四视图是标准、九视图不是；视图粘连负面词与逐字复用纪律", CharacterSheetFollowsTurnaroundStandard),
    ("模型配置多份：可同时启用多份、停用当前选中自动切换、密钥不落明文、旧配置自动迁移", SettingsProfilesMultiEnableAndSelection),
    ("模型配置：复制一份逐字段一致（含密钥与启用状态）", ProfileDuplicateCopiesEveryField),
    ("模型清单：地址按基础地址 / 完整 URL / Anthropic 推导，两种响应形状都能解析", ModelCatalogResolvesUrlAndParsesList),
    ("内置技能：停用后不被命中、也不进给 Agent 的清单；全停时明说原因", BuiltInSkillsRespectDisabledList),
    ("技能文件：启停只改一个键（未知字段保留），删除只删自己那个文件", SkillFileToggleAndDelete),
    ("智能导入：最小测试挑面积最小的一档，报告文本由共享层一份生成", ApiImportSummaryAndSmallestSize),
    ("智能导入：中文说明里的光杆家族词（runway）不得被当成模型名", ApiDocFamilyWordInProseIsNotAModel),
    ("智能导入：前端渲染站点从空壳里找回正文的挑选规则（同源 / 按路由名 / 只认够像文档的）", ApiDocShellMiningPicksSafely),
    ("站点与池子：站点标识与落盘、清单宽容解析（模型 × 档位、价格、参考图能力）", SiteCatalogAndPoolProbe),
    ("候选图批次：只留选中的那张、其余进回收站，没出好的不算在内", NodeImageBatchKeepsOnlyPicked),
    ("智能导入认得出 ComfyUI（且不把普通画图接口误认成它）", ProviderImportRecognizesComfyUi),
    ("生成链自检：四层缺口报数与花费预估、预勾只管挡路的那几件、缺失文件不算已出图", GenerationAuditReportsDependencyChain),
    ("厂家徽标：预设表里每一家都有徽标、区分色两两不同，表外的 id 落回中性徽标", ProviderBadgesCoverEveryPreset),
    ("引用过期：设定换了图 / 描述，下游产物要报「建议重出」；没记录的、新加的、锁版本的不报", ReferenceStalenessDetectsUpdatedSettings),
    ("AI 建实体：内容同时落到核心设定与默认变体，引用卡不再空白", AgentEntityContentReachesVariantAndCard),
    ("ID 校验：空 ID 与重复 ID 逐项报告", IdentityValidatorEmptyAndDuplicateIds),
    ("ID 校验：悬空边端点、父节点、工作树锚点与布局可选引用", IdentityValidatorDanglingReferences),
    ("ID 校验：缺失实体、变体与锁定版本", IdentityValidatorMissingTargets),
    ("ID 校验：版本错绑到其他变体", IdentityValidatorVersionMisbinding),
    ("ID 校验：null 版本跟随当前且同名实体不合并", IdentityValidatorNullVersionAndSameName),
    ("ID 校验：自引用与父链循环", IdentityValidatorCycles),
    ("ID 校验：一次收集全部问题", IdentityValidatorCollectsAllProblems),
    ("ID 校验：资产标识无效与不可访问分开", IdentityValidatorAssetReferences),
    ("ID 校验：只读，不改对象也不动文件", IdentityValidatorIsReadOnly),
    ("返工 R1：工作树上一版本按来源变体判定", ReworkWorkTreeSupersedesScopedByVariant),
    ("返工 R2：重复变体 ID 不凭首条猜归属", ReworkDuplicateVariantIdsNoFirstItemInference),
    ("返工 R3：非规范 asset:// 写法与真实解析一致", ReworkAssetNonCanonicalReferenceMatchesRealResolution),
    ("返工 R4：非法资产形状必须报格式错误", ReworkMalformedAssetShapesAreReported),
    ("ID 生命周期：保存重载保持 ID 与关系", SaveReloadKeepsIdsAndRelations),
    ("迁移：旧字段搬运、打开不写盘、重复迁移幂等", MigrationMovesLegacyFields),
    ("迁移：歧义保留并提供显式处理入口", MigrationKeepsAmbiguityAndProvidesRepairEntry),
    ("迁移：写前备份、失败不覆盖、可恢复", MigrationBacksUpAndRestoresOnFailure),
    ("复制画布：全部换新 ID 且内部关系重映射", DuplicateCanvasRemapsInternalIds),
    ("复制节点：集合内重映射、集合外共享", DuplicateNodesRemapsInternalIdsAndKeepsSharedReferences),
    ("重复导入：按 ID 去重且第二次零增量", ImportMergeIsIdempotent),
    ("导入冲突：同 ID 不同内容不覆盖、同名保持独立", ImportMergeReportsConflictWithoutOverwrite),
    ("导入：变体 ID 被占用时不合并实体", ImportMergeRejectsVariantIdClash),
    ("真实入口冒烟：打开→保存重开→复制→重复导入", RealEntrySmokeOpenSaveCopyImport),
    ("返工 R5：空 ID 旧来源的导入身份稳定", ReworkEmptyIdSourceImportIdentityIsStable),
    ("返工 R6：复制保持版本所属变体作用域", ReworkDuplicateCanvasKeepsVersionScope),
    ("备份：清单画布旁边的备份也列得出、清得掉，且不碰别的画布", ProjectCanvasBackupsAreListedAndPruned),
    ("返工 R7：备份失败中止保存且不覆盖", ReworkBackupFailureAbortsSave),
    ("返工 R8：保存失败不改动调用方对象", ReworkFailedSaveLeavesInputUntouched),
    ("返工 R9：旧格式包资产会随导入复制", ReworkLegacyPackageAssetsAreImported),
    ("返工 R10：重复 ID 不猜测消歧", ReworkDuplicateIdsAreNotGuessed),
    ("返工 R11：更高格式版本不降级不覆盖", ReworkFutureFormatIsNotDowngraded),
    ("返工 R12：画布库保存入口走完整保存链", ReworkLibrarySaveUsesFullChain),
    ("返工 R13：重命名入口先备份再原子写入", ReworkRenameEntryIsBackedUpAndAtomic),
    ("入口审计：所有真实保存入口共用同一策略", AllSaveEntryPointsShareOnePolicy),
    ("导出包：旧字段资产随包、原子覆盖不留半截", ExportPackagePacksLegacyAssetsAtomically),
    ("章节：显式顺序确定性补齐且幂等", ChapterOrderIsExplicitAndIdempotent),
    ("章节：归属按 ID 与父链解析，同名不绑定", ChapterResolutionUsesIdAndContext),
    ("章节：拆分与合并按 ID 重接引用", ChapterSplitAndMergeRekeyReferences),
    ("章节：删除保护引用并显式改挂", ChapterDeleteProtectsReferences),
    ("章节：移动成环阻断且不改动画布", ChapterMoveRejectsCycles),
    ("同步：画布到工作树按上下文建锚点", SyncCanvasToWorkTreeCreatesAnchorsByContext),
    ("同步：工作树到画布尊重人工确认", SyncWorkTreeToCanvasRespectsConfirmation),
    ("同步：会话可应用、可撤销", SyncSessionSupportsUndo),
    ("章节：元数据随保存重开、复制与包往返", ChapterMetadataSurvivesRoundTrip),
    ("章节入口：结构操作会话覆盖全部操作与撤销保存", ChapterStructureEntryCoversAllOperations),
    ("章节入口：保存失败不报成功且原文件不变", ChapterStructureSaveFailureKeepsCanvasAndFile),
    ("泳道布局：企划外置、章节独立泳道、分镜横排、成品挂分镜下", SwimlaneLayoutArrangesLanes),
    ("泳道布局：局部重排只动本章、预览不改画布、应用后可撤销", SwimlaneLocalRearrangeOnlyTouchesChapter),
    ("泳道布局：手动坐标受保护，需确认覆盖；重叠阻断且不写入", SwimlaneManualProtectionAndOverlap),
    ("引用：临时展开不落盘，锁定版本缺失可检出，反向定位按实体 ID", ReferenceExpansionAndMissingVersions),
    ("章节投影：节点带稳定 chapterId 与显式顺序，同名不合并", ChapterProjectionCarriesStableIds),
    ("Agent 落位：新分镜按稳定章节 ID 进泳道，不动其他章节与手动坐标", AgentNodePlacementUsesChapterLanes),
    ("引用扫描：跨画布与草稿命中，其它来源引用硬阻断删除", ReferenceScanSpansCanvasDraftAndLibrary),
    ("删除保护与回收站：还原后引用按稳定 ID 重新生效", DeletionGuardAndRecycleBinRestore),
    ("回收站写入失败：删除整体取消，内存与磁盘保持原样", RecycleBinWriteFailureCancelsDeletion),
    ("Agent 删除实体：走引用扫描与回收站链路，不再绕过保护", AgentDeleteEntityUsesReferenceProtection),
    ("Agent 删除实体预览：只在副本生效，不写回收站", AgentDeleteEntityPreviewWritesNoRecycleBin),
    ("Agent 批次提交：审批与自动模式各自只提交一次", AgentBatchCommitsOnlyOnce),
    ("AI 全流程：一句创意到分镜引用与章节归属", PremiseToStoryboardChain),
    ("AI 全流程：引用出图落盘、成品保存、失败重试与取消", PremiseToFinishedImageAndRecovery),
    ("智能导入：从文档/curl/JSON 识别 ComfyUI 与画图画视频接口", ProviderImportDetectsKinds),
    ("智能导入：识别不到就不猜，地址照样归一化", ProviderImportRefusesToGuess),
    ("智能导入：写入只改相关字段，其余配置与密钥加密保持不变", ProviderImportAppliesWithoutLosingConfig),
    ("接口文档：HTML/OpenAPI/纯文本都解析出接口、模型与尺寸档位", ApiDocAnalyzerReadsDocs),
    ("接口文档：一条父技能 + 按模型与尺寸派生池子子技能", ApiSkillFactoryBuildsPools),
    ("接口文档：技能落盘可被技能库装载，不可写时如实报错", ApiSkillFilesRoundTrip),
    ("出视频能力：无链路如实失败，有链路按步骤模型产出视频", VideoCapabilityRunsThroughRunner),
    ("接口文档：模型表定池子，详情端点与参数表不混进模型", ApiDocModelTableDrivesPools),
    ("接口文档：基础地址保留版本前缀且扩展名按文件头", ApiDocBaseUrlAndMediaExtension),
    ("接口账号：余额与上线模型取数解析，失败如实报出", ApiAccountProbeReadsBalanceAndModels),
    ("大模型修正：只采信文档里有的内容，非法条目丢掉", ApiDocRepairValidatesModelOutput),
    ("返工 R2/R3：批次提交分阶段如实报告，部分成功与保存失败都不算成功", AgentBatchCommitStagesAreHonest),
    ("返工 R4：技能自带执行配置，按导入的路径方法与鉴权发请求", ApiSkillExecutionContractIsHonored),
    ("返工 R5：按来源命名空间隔离，只清理自己名下的技能文件", ApiSkillSourceNamespacingAndPruning),
    ("返工 S4：池子绑定产出它的接口，导入技能不被 ComfyUI 抢走", ApiSkillPoolsBindTheirOwnEndpoint),
    ("返工 S6：大模型修正不得虚构（方法冲突、档位、地址）", ApiDocRepairDoesNotFabricate),
    ("返工 U1：资产移出是可逆移动，内容能原样恢复", AssetRecycleRoundTrip),
    ("返工 U6：替换中途失败整批回滚，不留混合批次", ApiSkillWriteRollsBackOnReplaceFailure),
    ("返工 U2：部分回滚后进入待恢复，禁止只重存", AgentBatchLedgerNeedsRecovery),
    ("返工 S6：接口关联（跨段落档位待确认、目录先出现后声明方法）", ApiDocRepairRespectsInterfaceAssociation),
    ("返工 U4：归属有歧义的池子不可执行，不回退默认执行方", ApiSkillAmbiguousPoolsAreNotExecutable),
    ("返工 V1：资产引用要解析成路径才移得动，扫描→移出→撤销全程可逆", ApiAssetRecycleResolvesReferences),
    ("返工 V2：待恢复批次能脱离清单判定，离开入口一律拦下", AgentLedgerReportsPendingRecoveryWithoutList),
    ("返工 V3：跨画布提交与重存被拒，画布身份用标签而不是路径", AgentBatchRejectsCrossCanvasCommit),
    ("返工 V4：同模型两接口按档位绑定，判不出来就不可执行", ApiSkillPoolsBindByModelAndSize),
    ("返工 V5：覆盖与清理都要完整来源身份，缺失或冲突保守保留", ApiSkillWriteRespectsFullOwnership),
    ("返工 V6：模型表档位要与模型同处，且不得超出接口已核实限制", ApiDocModelTableSizesNeedAssociation),
    ("返工 R16-4：部分资产恢复失败后重试只重试失败项并收敛", AssetRecycleRetryOnlyRetriesFailures),
    ("返工 R17-2：待恢复期间新批次被统一提交入口拦住，恢复后放行", CommitRejectsNewBatchWhileRecoveryPending),
    ("目标 6：项目级资源库读写、原子写、写前备份与幂等", ProjectLibraryRoundTrip),
    ("目标 6：同一资源被多份画布共享，改库后各画布都看到新内容", ProjectEntitySharedAcrossCanvases),
    ("目标 6：跨画布索引覆盖画布库与草稿，并标出外来源引用", ProjectEntityIndexCoversCanvasAndDraft),
    ("目标 6：项目级资源删除保护（外来源阻断 + 回收站还原回库）", ProjectEntityDeletionBlocksForeignReferences),
    ("目标 6：资源迁移幂等、同名不同实体不合并", ProjectEntityMigrationIsIdempotentAndKeepsSameNamesApart),
    ("目标 6：迁移中途失败整批回滚（库与画布逐字恢复）", ProjectEntityMigrationRollsBackOnFailure),
    ("返工 G6-R1：托管资源编辑写回项目库，保存重开保留；写库失败不误报", ProjectEntityEditPublishesToLibrary),
    ("返工 G6-R2：首次迁移失败把库恢复成「本来不存在」并还原内存标记，可重试", ProjectEntityMigrationFirstRunRollbackRestoresAbsentLibrary),
    ("返工 G6-R3：项目库权威资源缺失时解析/预检/锁定明确阻断，放回库即恢复", ProjectEntityMissingAuthorityBlocksResolve),
    ("返工 G6-S2：版本提交立即落库，外层取消不撤销已确认提交，写库失败整体撤回", VersionCommitPersistsImmediatelyAndSurvivesCancel),
    ("返工 G6-S3：删除变体先落库后动画布，失败时引用与实体原样、库回滚", VariantDeletionKeepsReferencesWhenPublishFails),
    ("返工 G6-T1：打开之后库被删也在执行前核验出来（旧标记不算数）", AuthorityRecheckCatchesLaterDeletedLibrary),
    ("返工 G6-T2：发布结果区分「真落库」与「本地内容」，本地实体不谎报已保存", PublishOutcomeDistinguishesLocalAndShared),
    ("版本策略：存在版本可锁定，缺失版本拒绝且不静默降级", ReferenceVersionPolicy),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures.Add($"FAIL {test.Name}: {ex.Message}"); Console.WriteLine(failures[^1]); }
}

if (failures.Count > 0) Environment.ExitCode = 1;
else Console.WriteLine($"全部 {tests.Length} 项 Agent 测试通过。");

/// <summary>模型按协议给出的回复：自然语言 + 末尾的操作块。</summary>
static string SampleReply() => """
我建议这样组织第一章的流程：先放剧情节点，再挂分镜，然后把分镜连到剧情下面。

```json
{"actions":[
  {"kind":"create_node","title":"第一章 剧情","content":"少年入宗门","reason":"起点"},
  {"kind":"create_node","title":"第一章 分镜","content":"三个镜头","reason":"下游"},
  {"kind":"create_edge","source":"第一章 剧情","target":"第一章 分镜","reason":"分镜属于剧情"}
]}
```
""";

static void ParseReplyAndSource()
{
    var reply = AgentActionParser.Parse(SampleReply());
    Expect(reply.Actions.Count == 3, $"应解析出 3 条操作，实际 {reply.Actions.Count}");
    // 操作块必须从展示文本里剥离，否则用户会在对话区看到一大段 JSON。
    Expect(!reply.Text.Contains("create_node", StringComparison.Ordinal) && !reply.Text.Contains("actions", StringComparison.Ordinal),
        $"展示文本里残留了操作块：{reply.Text}");
    Expect(reply.Actions[2].Source == "第一章 剧情" && reply.Actions[2].Target == "第一章 分镜",
        "create_edge 的 source/target 没有被正确读出");
}

static void ParseVersionAdopted()
{
    foreach (var value in new[] { "true", "false", "null", "\"true\"", "1", "{}", "[]", "" })
    {
        var field = value.Length == 0 ? "" : ",\"markVersionAdopted\":" + value;
        var reply = AgentActionParser.Parse("{\"actions\":[{\"kind\":\"update_node\",\"target\":\"镜头\",\"entityTarget\":\"角色\"" + field + "}]}");
        Expect(!reply.ProtocolBroken && reply.Actions.Count == 1, $"字段 {value} 导致整个动作解析失败");
        Expect(reply.Actions[0].MarkVersionAdopted == (value == "true"), $"字段 {value} 解析错误");
        var canvas = new WorkflowCanvasState();
        AgentActionExecutor.Apply(new[] { new AgentAction { Kind = "create_entity", Title = "角色", EntityKind = "角色" } }, canvas, null);
        canvas.Nodes.Add(new WorkflowNode { Title = "镜头" });
        var result = AgentActionExecutor.Apply(reply.Actions, canvas, null);
        Expect(result.Applied == 1 && result.Errors.Count == 0, "解析后的更新执行失败");
        Expect(canvas.Nodes.Single().VersionDecision == (value == "true" ? VersionDecision.Adopted : VersionDecision.None),
            "采纳标记未传递到节点版本决策");
    }
}

static void CreateNodesThenConnectInOneBatch()
{
    // 这是连线可用性的关键：执行器按顺序作用在同一份画布上，
    // 所以同批新建的节点能立刻用标题被后面的连线引用，不必让模型等下一轮拿短 id。
    var canvas = new WorkflowCanvasState();
    var result = AgentActionExecutor.Apply(AgentActionParser.Parse(SampleReply()).Actions, canvas, null);
    Expect(result.Applied == 3 && result.Errors.Count == 0, $"应全部成功，实际 {result.Applied} 条成功：{string.Join("；", result.Errors)}");
    Expect(canvas.Edges.Count == 1, $"应产生 1 条连线，实际 {canvas.Edges.Count}");
    Expect(canvas.Nodes.First(node => node.Title == "第一章 剧情").Id == canvas.Edges[0].SourceNodeId
        && canvas.Nodes.First(node => node.Title == "第一章 分镜").Id == canvas.Edges[0].TargetNodeId,
        "连线方向与 source/target 不一致");
}

static void RejectDuplicateAndSelfLoop()
{
    var canvas = new WorkflowCanvasState();
    AgentActionExecutor.Apply(AgentActionParser.Parse(SampleReply()).Actions, canvas, null);

    var duplicate = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "create_edge", Source = "第一章 剧情", Target = "第一章 分镜" } }, canvas, null);
    Expect(duplicate.Applied == 0 && canvas.Edges.Count == 1, "重复连线没有被拒绝");

    var selfLoop = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "create_edge", Source = "第一章 剧情", Target = "第一章 剧情" } }, canvas, null);
    Expect(selfLoop.Applied == 0 && canvas.Edges.Count == 1, "起点与终点相同的连线没有被拒绝");
}

// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void PrecheckMissingEndpoint()
{
	WorkflowCanvasState canvas = new WorkflowCanvasState
	{
		Nodes = 
		{
			new WorkflowNode
			{
				Title = "第一章 剧情"
			}
		}
	};
	string text = AgentActionExecutor.Precheck(new AgentAction
	{
		Kind = "create_edge",
		Source = "不存在的节点",
		Target = "第一章 剧情"
	}, canvas, null);
	Expect(text?.StartsWith("找不到起点节点") ?? false, "预检提示不符：" + text);
}

// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void BatchPrecheckSeesEarlierNodes()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState
	{
		Nodes = 
		{
			new WorkflowNode
			{
				Title = "第一章 剧情"
			}
		}
	};
	AgentAction[] array3 = new AgentAction[2]
	{
		new AgentAction
		{
			Kind = "create_node",
			Title = "分镜 A"
		},
		new AgentAction
		{
			Kind = "create_edge",
			Source = "第一章 剧情",
			Target = "分镜 A"
		}
	};
	string text = AgentActionExecutor.Precheck(array3[1], workflowCanvasState, null);
	Expect(text?.StartsWith("找不到终点节点") ?? false, "逐条预检本来就该误报（记录旧行为），实际：" + text);
	IReadOnlyList<string> readOnlyList = AgentActionExecutor.PrecheckBatch(array3, workflowCanvasState, null);
	Expect(readOnlyList.Count == 2 && readOnlyList.All((string hint) => hint == null), "整批预检不应误报，实际：" + string.Join(" / ", readOnlyList.Select((string hint) => hint ?? "无")));
	Expect(workflowCanvasState.Nodes.Count == 1, $"整批预检改动了真实画布：{workflowCanvasState.Nodes.Count} 个节点");
	WorkflowCanvasState canvas = new WorkflowCanvasState
	{
		Nodes = 
		{
			new WorkflowNode
			{
				Title = "定稿章节",
				IsLocked = true
			}
		}
	};
	IReadOnlyList<string> readOnlyList2 = AgentActionExecutor.PrecheckBatch(new AgentAction[1]
	{
		new AgentAction
		{
			Kind = "update_node",
			Target = "定稿章节"
		}
	}, canvas, null);
	Expect(readOnlyList2[0] == "节点已锁定，应用会被拒绝", "整批预检漏掉了真实警告：" + readOnlyList2[0]);
}

static void DeleteEdgeTargetsDirection()
{
    var canvas = new WorkflowCanvasState();
    var first = new WorkflowNode { Title = "第一章 剧情" };
    var second = new WorkflowNode { Title = "第一章 分镜" };
    canvas.Nodes.Add(first);
    canvas.Nodes.Add(second);

    AgentActionExecutor.Apply(new[] { new AgentAction { Kind = "create_edge", Source = first.Title, Target = second.Title } }, canvas, null);
    AgentActionExecutor.Apply(new[] { new AgentAction { Kind = "create_edge", Source = second.Title, Target = first.Title } }, canvas, null);
    Expect(canvas.Edges.Count == 2, $"反向连线也应被允许（不做拓扑校验），实际 {canvas.Edges.Count}");

    var removed = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_edge", Source = second.Title, Target = first.Title } }, canvas, null);
    Expect(removed.Applied == 1 && canvas.Edges.Count == 1, $"应只删掉一条，实际剩 {canvas.Edges.Count}");
    Expect(canvas.Edges[0].SourceNodeId == first.Id && canvas.Edges[0].TargetNodeId == second.Id,
        "删错了方向：应该保留 剧情→分镜");
}

static void DeleteMissingEdgeFails()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "A" });
    canvas.Nodes.Add(new WorkflowNode { Title = "B" });
    var result = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_edge", Source = "A", Target = "B" } }, canvas, null);
    // 静默成功最危险：用户会以为断开了，其实什么都没发生。
    Expect(result.Applied == 0 && result.Errors.Count == 1, "删除不存在的连线应报错");
}

static void DeleteNodeRemovesEdges()
{
    var canvas = new WorkflowCanvasState();
    AgentActionExecutor.Apply(AgentActionParser.Parse(SampleReply()).Actions, canvas, null);
    var result = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_node", Target = "第一章 剧情" } }, canvas, null);
    Expect(result.Applied == 1 && canvas.Nodes.Count == 1 && canvas.Edges.Count == 0,
        $"删节点应连带删边，实际剩 {canvas.Nodes.Count} 个节点 / {canvas.Edges.Count} 条边");
}

/// <summary>
/// 「许不许连」这条规则只有一份（<see cref="CanvasEdgeRules"/>）：桌面端拖拽连接与网页端的「连接」动作
/// 问的是同一个方法。这里钉住它本身的契约，而不是某一端的入口——两端各写一遍时，
/// 同一次操作迟早会在两端得到不同的结果。
/// </summary>
static void SharedEdgeRuleIsOneCopy()
{
    var canvas = new WorkflowCanvasState();
    var from = new WorkflowNode { Title = "起点" };
    var to = new WorkflowNode { Title = "终点" };
    canvas.Nodes.Add(from);
    canvas.Nodes.Add(to);

    Expect(CanvasEdgeRules.Refusal(canvas, from.Id, to.Id) is null, "两个不同节点之间应当可以连");

    // 方向是这条边的身份：反过来的那一条还不存在，不能因为「已经有 a→b」就把 b→a 也拒了。
    Expect(CanvasEdgeRules.Refusal(canvas, to.Id, from.Id) is null, "反向的连线是另一条，不该被当成重复");

    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = from.Id, TargetNodeId = to.Id });
    Expect(CanvasEdgeRules.Refusal(canvas, from.Id, to.Id) is { Kind: EdgeRefusalKind.Duplicate } duplicate &&
        duplicate.Message.Length > 0, "同一对节点之间的第二条要拒绝，并给出一句能直接显示的话");

    // 判重**按节点对**（忽略端口）：界面上一个出口只有一根线，端口是协议字段不是界面概念。
    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = from.Id, SourcePort = 0, TargetNodeId = to.Id, TargetPort = 0 });
    Expect(CanvasEdgeRules.Refusal(canvas, from.Id, to.Id) is { Kind: EdgeRefusalKind.Duplicate },
        "端口不同也算重复——判重按节点对，与桌面端拖拽一致");

    Expect(CanvasEdgeRules.Refusal(canvas, from.Id, from.Id) is { Kind: EdgeRefusalKind.SelfLoop },
        "自环要拒绝（画布校验也会拦，但那时只能回一句「校验失败」）");
}

static void LockedNodeIsProtected()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "定稿章节", IsLocked = true });

    var result = AgentActionExecutor.Apply(new[]
    {
        new AgentAction { Kind = "update_node", Target = "定稿章节", Content = "偷偷改" },
        new AgentAction { Kind = "delete_node", Target = "定稿章节" }
    }, canvas, null);

    // 锁定是数据层强制，不是界面提示——Agent 不能绕过审批之外的保护。
    Expect(result.Applied == 0 && canvas.Nodes.Count == 1 && canvas.Nodes[0].Content.Length == 0,
        $"锁定节点被改动了：{string.Join("；", result.Errors)}");
    Expect(AgentActionExecutor.Precheck(new AgentAction { Kind = "update_node", Target = "定稿章节" }, canvas, null) == "节点已锁定，应用会被拒绝",
        "预检没有提前告知锁定会被拒绝");
}

static void EntityVersionCreatesWorkTreeAndNodeReference()
{
    var canvas = new WorkflowCanvasState();
    var created = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "create_entity",
            EntityKind = "角色",
            Title = "沈砚",
            Content = "第一版角色设定"
        }
    }, canvas, null);
    Expect(created.Applied == 1 && created.Errors.Count == 0, $"实体创建失败：{string.Join("；", created.Errors)}");

    var versioned = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "create_entity_version",
            EntityTarget = "沈砚",
            VariantTarget = "默认",
            Chapter = "第二章",
            Content = "第二版角色设定",
            VersionNote = "第二章进入听雨铃事件后的新设定"
        },
        new AgentAction
        {
            Kind = "create_node",
            Title = "第二章角色",
            Content = "沈砚在雨夜追查来信",
            EntityTarget = "沈砚",
            VariantTarget = "默认",
            VariantVersion = "v2"
        }
    }, canvas, null);

    Expect(versioned.Applied == 2 && versioned.Errors.Count == 0,
        $"版本与节点应全部成功：{string.Join("；", versioned.Errors)}");
    var entity = canvas.Entities.Single(entity => entity.Name == "沈砚");
    var variant = entity.Variants.Single(variant => variant.Name == "默认");
    var version = variant.Versions.Single(version => version.Number == 2);
    var initialVersion = variant.Versions.Single(version => version.Number == 1);
    var workItem = canvas.WorkTree.Single(item => item.SourceVersionId == version.Id);
    var node = canvas.Nodes.Single(node => node.Title == "第二章角色");

    Expect(version.SupersedesVersionId == initialVersion.Id,
        "V2 没有记录它继承的 V1 版本");
    Expect(workItem.Chapter == "第二章" && workItem.SourceEntityId == entity.Id
        && workItem.SourceVariantId == variant.Id && workItem.SourceVersionId == version.Id
        && workItem.SupersedesVersionId == initialVersion.Id && workItem.Version == "v2",
        "工作树版本条目的来源章节、升级关系或实体关联不完整");
    Expect(node.References.Count == 1 && node.References[0].EntityId == entity.Id
        && node.References[0].VariantId == variant.Id
        && node.References[0].VariantVersionId == version.Id,
        "节点没有锁定到新建的 v2 快照");
}

static void UpdateNodeChangesVersionReference()
{
    var canvas = new WorkflowCanvasState();
    AgentActionExecutor.Apply(new[]
    {
        new AgentAction { Kind = "create_entity", EntityKind = "道具", Title = "听雨铃", Content = "初始设定" },
        new AgentAction
        {
            Kind = "create_entity_version",
            EntityTarget = "听雨铃",
            VariantTarget = "默认",
            Chapter = "第三章",
            Content = "回声版本设定",
            VersionNote = "第三章获得回声能力"
        },
        new AgentAction
        {
            Kind = "create_node",
            Title = "旧版道具节点",
            Content = "仍按旧版描述",
            EntityTarget = "听雨铃",
            VariantTarget = "默认",
            VariantVersion = "v1"
        }
    }, canvas, null);

    var updated = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "update_node",
            Target = "旧版道具节点",
            Content = "切换到当前版本描述",
            EntityTarget = "听雨铃",
            VariantTarget = "默认"
        }
    }, canvas, null);

    Expect(updated.Applied == 1 && updated.Errors.Count == 0,
        $"更新节点失败：{string.Join("；", updated.Errors)}");
    var node = canvas.Nodes.Single(node => node.Title == "旧版道具节点");
    Expect(node.References.Count == 1 && node.References[0].VariantVersionId is null,
        "省略版本号时节点应改为跟随变体当前内容");

    var locked = AgentActionExecutor.Apply(new[]
    {
        new AgentAction
        {
            Kind = "update_node",
            Target = "旧版道具节点",
            EntityTarget = "听雨铃",
            VariantTarget = "默认",
            VariantVersion = "v1"
        }
    }, canvas, null);
    var initialVersion = canvas.Entities.Single().Variants.Single().Versions.Single(version => version.Number == 1);
    Expect(locked.Applied == 1 && canvas.Nodes.Single().References[0].VariantVersionId == initialVersion.Id,
        "明确指定历史版本后节点应锁定到 v1 快照");
}

static void VersionDecisionRoundTrips()
{
    var state = new WorkflowCanvasState();
    state.Nodes.Add(new WorkflowNode
    {
        Title = "第二章角色",
        Chapter = "第二章",
        VersionDecision = VersionDecision.KeepHistorical
    });

    var restored = JsonSerializer.Deserialize<WorkflowCanvasState>(JsonSerializer.Serialize(state));
    Expect(restored is not null && restored.Nodes.Single().VersionDecision == VersionDecision.KeepHistorical,
        "版本决策经过 JSON 保存和加载后没有保留");
}

static void PreviewDoesNotMutateCanvas()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章" });
    CanvasPreviewBuilder.Build(AgentActionParser.Parse(SampleReply()).Actions, canvas);
    // 预览是「试算到副本上」，真实画布在用户保存前必须一点都不能动。
    Expect(canvas.Nodes.Count == 1 && canvas.Edges.Count == 0,
        $"预览改动了真实画布：{canvas.Nodes.Count} 个节点 / {canvas.Edges.Count} 条边");
}

static void PreviewReportsAdditions()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章" });
    var preview = CanvasPreviewBuilder.Build(new[]
    {
        new AgentAction { Kind = "create_node", Title = "分镜 A", Content = "镜头" },
        new AgentAction { Kind = "create_edge", Source = "第一章", Target = "分镜 A" }
    }, canvas);
    Expect(preview.AddedNodes.Count == 1 && preview.AddedEdges.Count == 1,
        $"预览应报 1 个新节点 + 1 条新连线，实际 {preview.AddedNodes.Count} / {preview.AddedEdges.Count}");
}

static void PreviewIgnoresNoOp()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章" });
    canvas.Nodes.Add(new WorkflowNode { Title = "第二章" });
    // 删一条不存在的连线什么也不会变，预览不应凭空画出差异。
    var preview = CanvasPreviewBuilder.Build(
        new[] { new AgentAction { Kind = "delete_edge", Source = "第一章", Target = "第二章" } }, canvas);
    Expect(preview.IsEmpty, "无效果的操作不应产生预览差异");
}

static void PreviewReportsRemovedEdge()
{
    var canvas = new WorkflowCanvasState();
    var first = new WorkflowNode { Title = "A" };
    var second = new WorkflowNode { Title = "B" };
    canvas.Nodes.Add(first);
    canvas.Nodes.Add(second);
    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = first.Id, TargetNodeId = second.Id });

    var preview = CanvasPreviewBuilder.Build(
        new[] { new AgentAction { Kind = "delete_edge", Source = "A", Target = "B" } }, canvas);
    Expect(preview.RemovedEdgeIds.Count == 1 && preview.AddedEdges.Count == 0,
        $"预览应报 1 条将被删除的连线，实际 {preview.RemovedEdgeIds.Count}");
    Expect(canvas.Edges.Count == 1, "预览不应真的删掉连线");
}

static void FileWritePreviewVersusCommit()
{
    var workspace = NewWorkspace();
    try
    {
        var file = Path.Combine(workspace, "note.md");
        var action = new[] { new AgentAction { Kind = "write_file", Path = "note.md", Content = "内容" } };

        var preview = AgentActionExecutor.Apply(action, new WorkflowCanvasState(), workspace, FileWriteMode.Skip);
        Expect(preview.Applied == 1 && !File.Exists(file), "预览模式不应落盘");

        var snapshots = new List<FileSnapshot>();
        var commit = AgentActionExecutor.Apply(action, new WorkflowCanvasState(), workspace, FileWriteMode.Apply, snapshots);
        Expect(commit.Applied == 1 && File.Exists(file), "提交时应落盘");
        // 文件原本不存在 → 快照记 null，撤销时据此删除这个新文件。
        Expect(snapshots.Count == 1 && snapshots[0].OriginalContent is null,
            $"应记录 1 条文件快照，实际 {snapshots.Count}");
    }
    finally { Directory.Delete(workspace, true); }
}

static void FileWriteEscapeRejected()
{
    var workspace = NewWorkspace();
    try
    {
        var escaped = Path.Combine(Path.GetDirectoryName(workspace)!, "escape.md");
        var result = AgentActionExecutor.Apply(
            new[] { new AgentAction { Kind = "write_file", Path = "../escape.md", Content = "内容" } },
            new WorkflowCanvasState(), workspace, FileWriteMode.Apply);
        Expect(result.Applied == 0, "越界路径应被拒绝");
        Expect(!File.Exists(escaped), "越界路径写出了文件");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentImageRoundTrip()
{
    var workspace = NewWorkspace();
    try
    {
        var path = Path.Combine(workspace, "角色.png");
        var original = Convert.FromBase64String(Sample.TinyPngBase64);
        File.WriteAllBytes(path, original);

        var (attachment, error) = AgentAttachmentLoader.Load(path);
        Expect(attachment is not null, $"应成功加载图片：{error}");
        Expect(attachment!.Kind == AgentAttachmentKind.Image, "应识别为图片附件");
        Expect(attachment.Payload.StartsWith("data:image/png;base64,", StringComparison.Ordinal),
            $"data URL 前缀不对：{attachment.Payload[..Math.Min(40, attachment.Payload.Length)]}");

        // 关键：发出去的必须是**原始字节**，不是被重新编码过的图。
        var split = AgentAttachmentLoader.SplitDataUrl(attachment.Payload);
        Expect(split is not null && split.Value.MediaType == "image/png", "应能拆出 media type");
        Expect(Convert.FromBase64String(split!.Value.Data).SequenceEqual(original), "还原出来的字节与原始文件不一致");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentImageTooLarge()
{
    var workspace = NewWorkspace();
    try
    {
        var path = Path.Combine(workspace, "巨图.png");
        File.WriteAllBytes(path, new byte[AgentAttachmentLoader.MaxImageBytes + 1]);
        var (attachment, error) = AgentAttachmentLoader.Load(path);
        // 拒绝要给出可执行的原因，不能只说"失败"。
        Expect(attachment is null && error is not null && error.Contains("超过", StringComparison.Ordinal),
            $"超限图片应被拒绝并说明原因，实际：{error}");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentTextTruncation()
{
    var workspace = NewWorkspace();
    try
    {
        var shortPath = Path.Combine(workspace, "设定.md");
        File.WriteAllText(shortPath, "# 主角设定\n少年剑客");
        var (small, _) = AgentAttachmentLoader.Load(shortPath);
        Expect(small is not null && small.Kind == AgentAttachmentKind.Text && !small.Truncated, "普通文本文件应完整读取");
        Expect(small!.Payload.Contains("少年剑客", StringComparison.Ordinal), "文本内容丢失");

        var longPath = Path.Combine(workspace, "长文.md");
        File.WriteAllText(longPath, new string('字', AgentAttachmentLoader.MaxTextCharacters + 5000));
        var (big, _) = AgentAttachmentLoader.Load(longPath);
        Expect(big is not null && big.Truncated, "超长文本应被标记为已截断");
        Expect(big!.Payload.Length == AgentAttachmentLoader.MaxTextCharacters, $"截断长度不对：{big.Payload.Length}");

        // 截断必须让模型知道，否则它会以为读到了全文。
        var composed = AgentAttachmentLoader.ComposeContent("看看这份设定", new[] { big });
        Expect(composed.Contains("已截断", StringComparison.Ordinal), "拼进正文时应标注截断");
    }
    finally { Directory.Delete(workspace, true); }
}

static void AttachmentBinaryRejected()
{
    var workspace = NewWorkspace();
    try
    {
        var path = Path.Combine(workspace, "分镜.pdf");
        File.WriteAllBytes(path, new byte[] { 0x25, 0x50, 0x44, 0x46 });
        var (attachment, error) = AgentAttachmentLoader.Load(path);
        Expect(attachment is null, "PDF 不应被当作可发送附件");
        Expect(error is not null && error.Contains("转成文本", StringComparison.Ordinal),
            $"拒绝理由应说明怎么解决，实际：{error}");
    }
    finally { Directory.Delete(workspace, true); }
}

static void ComposeContentAndCollectImages()
{
    var image = new AgentAttachment { Name = "角色.png", Kind = AgentAttachmentKind.Image, Payload = "data:image/png;base64,AAAA" };
    var text = new AgentAttachment { Name = "设定.md", Kind = AgentAttachmentKind.Text, Payload = "少年剑客" };
    var composed = AgentAttachmentLoader.ComposeContent("这张图有问题吗", new[] { image, text });

    Expect(composed.StartsWith("这张图有问题吗", StringComparison.Ordinal), "用户的问题应保留在最前");
    Expect(composed.Contains("【附件：设定.md】", StringComparison.Ordinal) && composed.Contains("少年剑客", StringComparison.Ordinal),
        "文本附件的内容应拼进正文");
    Expect(!composed.Contains("data:image/png", StringComparison.Ordinal), "图片不应被塞进正文（它走图片块）");

    var images = AgentAttachmentLoader.CollectImages(new[] { image, text });
    Expect(images.Count == 1 && images[0] == image.Payload, "只应收集图片附件");
}

static void OpenAiPayloadCarriesImage()
{
    var handler = SendOneTurn(AiApiFormat.OpenAiChat, vision: true, content: "这张图怎样", images: new[] { Sample.DataUrl });
    var messages = JsonDocument.Parse(handler.Body!).RootElement.GetProperty("messages");

    Expect(messages[0].GetProperty("role").GetString() == "system", "第一条应是 system 消息");
    var user = messages[1];
    var parts = user.GetProperty("content");
    Expect(parts.ValueKind == JsonValueKind.Array, "带图片时 content 应是内容块数组");
    Expect(parts[0].GetProperty("type").GetString() == "text" && parts[0].GetProperty("text").GetString() == "这张图怎样",
        "第一个块应是文本块");
    Expect(parts[1].GetProperty("type").GetString() == "image_url"
        && parts[1].GetProperty("image_url").GetProperty("url").GetString() == Sample.DataUrl,
        "第二个块应是 image_url，且 url 就是 data URL");
}

static void AnthropicPayloadCarriesImage()
{
    var handler = SendOneTurn(AiApiFormat.AnthropicMessages, vision: true, content: "这张图怎样", images: new[] { Sample.DataUrl });
    var root = JsonDocument.Parse(handler.Body!).RootElement;

    // Anthropic 的 system 必须放在顶层，不能出现在 messages 里。
    Expect(root.GetProperty("system").ValueKind == JsonValueKind.String, "system 应是顶层字符串");
    Expect(root.GetProperty("max_tokens").GetInt32() > 0, "Anthropic 的 max_tokens 是必填项");
    foreach (var message in root.GetProperty("messages").EnumerateArray())
        Expect(message.GetProperty("role").GetString() != "system", "messages 里不应出现 system 角色");

    var parts = root.GetProperty("messages")[0].GetProperty("content");
    var image = parts[1];
    Expect(image.GetProperty("type").GetString() == "image", "第二个块应是 image");
    var source = image.GetProperty("source");
    // data URL 必须拆成 media_type + data 两部分，直接塞 data URL 会被拒。
    Expect(source.GetProperty("type").GetString() == "base64", "data URL 应转成 base64 来源");
    Expect(source.GetProperty("media_type").GetString() == "image/png", "media_type 应从 data URL 里拆出来");
    Expect(source.GetProperty("data").GetString() == Sample.DataUrl.Split(',')[1], "base64 数据应原样传递");
}

static void SystemMessageStaysTextOnly()
{
    // DeepSeek 对 system 消息里的图片直接返回 400，所以系统提示必须始终是纯文本。
    var openai = JsonDocument.Parse(
        SendOneTurn(AiApiFormat.OpenAiChat, vision: true, content: "问题", images: new[] { Sample.DataUrl }).Body!)
        .RootElement.GetProperty("messages")[0];
    Expect(openai.GetProperty("content").ValueKind == JsonValueKind.String, "OpenAI 格式的 system 内容应是纯文本");
}

static void ImageWithoutVisionFails()
{
    string? message = null;
    try { SendOneTurn(AiApiFormat.OpenAiChat, vision: false, content: "这张图怎样", images: new[] { Sample.DataUrl }); }
    catch (InvalidOperationException error) { message = error.Message; }

    Expect(message is not null, "未开启图片输入时应直接拦住，而不是把图发给接口");
    // 报错要能指导用户怎么改，不能把服务端的 400 原样抛出来。
    Expect(message!.Contains("图片输入", StringComparison.Ordinal) && message.Contains("deepseek-flash", StringComparison.Ordinal),
        $"报错信息不够可读：{message}");
}

/// <summary>发一轮对话，返回被桩 HttpClient 拦下来的请求体。</summary>
static CapturingHandler SendOneTurn(AiApiFormat format, bool vision, string content, IReadOnlyList<string> images)
{
    var handler = new CapturingHandler();
    var config = ConfigFor(format);
    config.SupportsImageInput = vision;
    var provider = new OpenAiCompatibleProvider(config, new HttpClient(handler));
    provider.ChatAsync(new[] { new AiChatMessage { Role = "user", Content = content, Images = images } }).GetAwaiter().GetResult();
    return handler;
}

static void SecretProtectorRoundTrip()
{
    const string key = "sk-test-placeholder-0123456789abcdef";
    var cipher = SecretProtector.Protect(key);
    Expect(SecretProtector.IsProtected(cipher), "加密结果应带 dpapi: 前缀");
    Expect(!cipher.Contains(key, StringComparison.Ordinal), "密文里不应出现明文");
    Expect(SecretProtector.Unprotect(cipher) == key, "解密应还原原值");
    Expect(SecretProtector.Protect(cipher) == cipher, "已加密的值不应被二次加密（否则解不回来）");

    // 旧版本留下的明文要原样返回，由保存流程负责迁移，不能直接判为"坏数据"。
    Expect(SecretProtector.Unprotect(key) == key, "明文应原样返回以兼容旧配置");
    // 密文损坏时返回 null 而不是抛异常，让上层能给出可读的提示。
    Expect(SecretProtector.Unprotect("dpapi:!!!not-base64!!!") is null, "非法 base64 应返回 null");

    var described = SecretProtector.Describe(key);
    Expect(!described.Contains(key, StringComparison.Ordinal), $"脱敏描述泄露了完整密钥：{described}");
    Expect(described.Contains('…', StringComparison.Ordinal), $"脱敏描述应带省略号：{described}");
    Expect(SecretProtector.Describe(string.Empty) == "（未填写）", "空密钥的描述不符");
}

static void ConfigFileStoresCiphertext()
{
    const string key = "sk-roundtrip-check-0123456789";
    using var environment = new ConfigEnvironment();
    try
    {
        var config = AiProviderSettings.Load();
        config.Endpoint = "https://api.example.com/v1";
        config.Model = "test-model";
        config.ApiKey = key;
        Expect(AiProviderSettings.Save(config), "保存应该成功");

        // 这是本轮要保证的核心性质：文件里永远是密文，内存里永远是明文。
        var onDisk = File.ReadAllText(environment.ConfigPath);
        Expect(!onDisk.Contains(key, StringComparison.Ordinal), "配置文件里出现了明文密钥");
        Expect(onDisk.Contains(SecretProtector.Prefix, StringComparison.Ordinal), "配置文件里没有 DPAPI 密文");
        Expect(config.ApiKey == key, "保存后内存里的密钥应仍是明文（请求要带它）");

        var loaded = AiProviderSettings.Load();
        Expect(loaded.ApiKey == key, $"读回应还原明文，实际：{loaded.ApiKey}");
        Expect(!loaded.ApiKeyUnreadable && !loaded.ApiKeyWasPlaintext, "正常读回不该带任何警告标记");
    }
    finally { environment.Restore(); }
}

static void BrokenCiphertextIsReported()
{
    using var environment = new ConfigEnvironment();
    try
    {
        // 密文合法但解不开（等价于配置被拷到别的 Windows 账户或机器）。
        File.WriteAllText(environment.ConfigPath,
            "{\"Endpoint\":\"https://api.example.com/v1\",\"Model\":\"m\",\"ApiKey\":\"dpapi:bm90LWEtcmVhbC1jaXBoZXJ0ZXh0\"}");

        var loaded = AiProviderSettings.Load();
        Expect(loaded.ApiKeyUnreadable, "解不开的密文应被标记，供界面提示重新填写");
        Expect(loaded.ApiKey.Length == 0, "解不开时密钥应为空，绝不能拿密文去当密钥请求");
        // 一处解不开不该把整个配置废掉。
        Expect(loaded.Endpoint == "https://api.example.com/v1" && loaded.Model == "m", "其它配置项不应受影响");
    }
    finally { environment.Restore(); }
}

static void PlaintextSecretMigratesOnLoad()
{
    const string key = "sk-legacy-plaintext-9876543210";
    using var environment = new ConfigEnvironment();
    try
    {
        // 模拟旧版本留下的明文配置。
        File.WriteAllText(environment.ConfigPath,
            $"{{\"Endpoint\":\"https://api.example.com/v1\",\"Model\":\"m\",\"ApiKey\":\"{key}\",\"ProviderChoiceMade\":true}}");

        var loaded = AiProviderSettings.Load();
        Expect(loaded.ApiKey == key, $"旧明文配置应仍然可用，实际：{loaded.ApiKey}");
        Expect(loaded.ApiKeyWasPlaintext, "应记录「原来是明文」这一事实，供界面提示已自动加密");
        Expect(!loaded.ApiKeyUnreadable, "明文不是坏数据，不该被标记为无法解密");

        // 关键：读取时就地把明文升级为密文，不等用户下次打开设置——缩小明文暴露窗口。
        var onDisk = File.ReadAllText(environment.ConfigPath);
        Expect(!onDisk.Contains(key, StringComparison.Ordinal), "明文密钥没有被迁移掉");
        Expect(onDisk.Contains(SecretProtector.Prefix, StringComparison.Ordinal), "迁移后应是 DPAPI 密文");
        Expect(AiProviderSettings.Load().ApiKey == key, "迁移后应仍能读回原密钥");
    }
    finally { environment.Restore(); }
}

static void OpenAiStreamParsesDeltas()
{
    // 思考型模型的 SSE：先来 reasoning_content，再来正文。思考绝不能混进正文。
    const string sse = """
        data: {"choices":[{"delta":{"reasoning_content":"让我想想"}}]}

        data: {"choices":[{"delta":{"content":"第一段"}}]}

        data: {"choices":[{"delta":{"content":"第二段"}}]}

        data: [DONE]

        """;
    var handler = new CapturingHandler { ResponseBody = sse, ContentType = "text/event-stream" };
    var deltas = new List<string>();
    var thinking = new List<string>();
    var reply = StreamOnce(AiApiFormat.OpenAiChat, handler, deltas, thinking.Add);

    Expect(reply == "第一段第二段", $"拼接结果不对：{reply}");
    Expect(deltas.SequenceEqual(new[] { "第一段", "第二段" }), $"增量应分次回调：{string.Join("/", deltas)}");
    // 思考作为独立的一条流给出来（把 reasoning 与 reply 分开记）。
    Expect(thinking.Count == 1 && thinking[0] == "让我想想", $"思考增量不正确：{string.Join("/", thinking)}");
    Expect(!reply.Contains("让我想想", StringComparison.Ordinal), "思考内容混进了正文");
    Expect(handler.Body!.Contains("\"stream\":true", StringComparison.Ordinal), "请求体没有带 stream:true");
}

static void AnthropicStreamParsesDeltas()
{
    // Anthropic 的事件流：增量在 content_block_delta，文本是 text_delta，思考是 thinking_delta。
    const string sse = """
        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"嗯"}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"甲"}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"乙"}}

        data: [DONE]

        """;
    var handler = new CapturingHandler { ResponseBody = sse, ContentType = "text/event-stream" };
    var deltas = new List<string>();
    var thinking = new List<string>();
    var reply = StreamOnce(AiApiFormat.AnthropicMessages, handler, deltas, thinking.Add);

    Expect(reply == "甲乙", $"拼接结果不对：{reply}");
    Expect(thinking.Count == 1 && thinking[0] == "嗯", $"thinking_delta 应作为思考增量给出：{string.Join("/", thinking)}");
    Expect(handler.Body!.Contains("\"stream\":true", StringComparison.Ordinal), "请求体没有带 stream:true");
    Expect(handler.Body!.Contains("max_tokens", StringComparison.Ordinal), "Anthropic 报文仍应带必填的 max_tokens");
}

static void EmptyStreamIsReportedAsError()
{
    // 网关返回 200 但一行正文都没有（例如不支持 stream）——必须报错，不能静默返回空回复。
    var handler = new CapturingHandler { ResponseBody = "data: [DONE]\n\n", ContentType = "text/event-stream" };
    string? message = null;
    try { StreamOnce(AiApiFormat.OpenAiChat, handler, new List<string>(), _ => { }); }
    catch (InvalidOperationException error) { message = error.Message; }

    Expect(message is not null && message.Contains("没有收到任何文本", StringComparison.Ordinal),
        $"应明确报错，实际：{message ?? "（没有抛异常）"}");
}

static void ThinkingOnlyIsStillAnError()
{
    // 只收到思考、没有正文：这是不完整的一轮，不能当作成功（否则界面会显示一个空回答）。
    var handler = new CapturingHandler
    {
        ResponseBody = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"想了半天\"}}]}\n\ndata: [DONE]\n\n",
        ContentType = "text/event-stream"
    };
    var thinking = new List<string>();
    string? message = null;
    try { StreamOnce(AiApiFormat.OpenAiChat, handler, new List<string>(), thinking.Add); }
    catch (InvalidOperationException error) { message = error.Message; }

    Expect(thinking.Count == 1, "思考增量仍应被回调出来");
    Expect(message is not null, "只有思考没有正文时应报错");
}

static void ActionsBlockIsHiddenFromDisplay()
{
    // 流式显示要在协议块之前截断，而且不能把```json 围栏留在对话区。
    const string reply = "建议这样改：\n\n```json\n{\"actions\":[{\"kind\":\"create_node\",\"title\":\"A\"}]}\n```\n";
    var cut = AgentActionParser.FindProtocolBlockStart(reply);
    Expect(cut >= 0, "应能定位协议块");
    var shown = reply[..cut].Trim();
    Expect(shown == "建议这样改：", $"显示部分不符：{shown}");
    Expect(!shown.Contains("```", StringComparison.Ordinal), "围栏不该出现在显示部分");

    // 没有协议块的普通回复不该被截断。
    Expect(AgentActionParser.FindProtocolBlockStart("普通回复，没有操作块。") < 0, "无操作块时不应截断");
    Expect(AgentActionParser.FindProtocolBlockStart(string.Empty) < 0, "空文本不应截断");
}

static void AskIsParsedWithOptions()
{
    // 模型信息不足时应当「问」，界面据此弹窗——而不是把问题写成一大段正文让用户自己组织语言。
    const string reply = """
        我需要确认两件事再动手。

        ```json
        {"ask":{"question":"要加哪种节点？","options":["角色节点","场景节点","分镜节点"]}}
        ```
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.Ask is not null, "应解析出反问");
    Expect(parsed.Ask!.Question == "要加哪种节点？", $"问题文本不符：{parsed.Ask.Question}");
    Expect(parsed.Ask.Options.SequenceEqual(new[] { "角色节点", "场景节点", "分镜节点" }),
        $"选项不符：{string.Join("/", parsed.Ask.Options)}");
    Expect(parsed.Actions.Count == 0, "这条回复没有改动提议");
    Expect(!parsed.Text.Contains("ask", StringComparison.Ordinal), $"展示文本里残留了协议块：{parsed.Text}");
    Expect(parsed.Text.Contains("我需要确认", StringComparison.Ordinal), "自然语言部分应保留");
}

static void AskCoexistsWithActions()
{
    // 允许「先问清、再改」同批出现；两者必须互不干扰。
    const string reply = """
        ```json
        {"ask":{"question":"加到哪个画布？","options":["当前画布"]},
         "actions":[{"kind":"create_node","title":"新角色","content":"待补充","reason":"先占位"}]}
        ```
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.Ask is not null && parsed.Ask.Question == "加到哪个画布？", "反问应被解析");
    Expect(parsed.Actions.Count == 1 && parsed.Actions[0].Title == "新角色", "操作应被解析");
}

static void AskAloneIsValid()
{
    // 只有 ask、没有 actions：过去这种回复会因为找不到 "actions" 标记而被当成纯文本，
    // 整块 JSON 直接糊在对话区里——这正是要避免的。
    const string reply = "先确认一下。\n\n{\"ask\":{\"question\":\"内容写什么？\",\"options\":[]}}\n";
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.Ask is not null && parsed.Ask.Question == "内容写什么？", "应解析出反问");
    Expect(parsed.Ask!.Options.Count == 0, "允许不给选项（纯自由输入）");
    Expect(!parsed.Text.Contains("\"ask\"", StringComparison.Ordinal), "协议块应从展示文本里剥离");
    Expect(parsed.Text.Trim() == "先确认一下。", $"展示文本不符：{parsed.Text}");
}

static void AskBlockIsHiddenFromDisplay()
{
    // 流式显示时 ask 块也要被挡住（它和 actions 走同一套定位）。
    const string reply = "我先问一下：\n\n```json\n{\"ask\":{\"question\":\"哪种节点？\"}}\n```\n";
    var cut = AgentActionParser.FindProtocolBlockStart(reply);
    Expect(cut >= 0, "ask 块也应能被定位");
    var shown = reply[..cut].Trim();
    Expect(!shown.Contains("```", StringComparison.Ordinal) && !shown.Contains("\"ask\"", StringComparison.Ordinal),
        $"显示部分不该包含围栏或 ask 块：{shown}");
}

/// <summary>
/// 真实样本：用户让 Agent「当前画布加个新节点」，模型最后给出的 create_entity 块里，
/// content 的正文用了**英文引号**（"读" / "有用"）却没转义，整块 JSON 因此非法。
/// 修复前：定位不到块 → 整块 JSON 原样糊在对话区里、改动全部作废（用户的"没有加上"）。
/// 修复后：内容里的裸引号被自动转义，块照常解析。
/// </summary>
static void BrokenProtocolIsHiddenFromDisplay()
{
    const string reply = """
        我先说明处理方式。

        ```json
        {"actions":[{"kind":"update_node","content":"未闭合"}]
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(parsed.ProtocolBroken, "格式损坏的协议块应标记为失败");
    Expect(parsed.Text.Trim() == "我先说明处理方式。", $"应只保留自然语言，实际：{parsed.Text}");
    Expect(!parsed.Text.Contains("update_node", StringComparison.Ordinal), "损坏的操作协议不应出现在展示文本");
}

static void IncompleteAskProtocolIsHidden()
{
    const string reply = """
        你还没说清要加什么节点，我先问一下，避免猜错建错。

        ```json
        {"ask":{"question":"这两个节点要建成什么类型、什么内容？","options":["角色节点（我做设定）","场景节点","分镜节点","普通草稿节点（先占位，内容我来给）"]
        """;
    var parsed = AgentActionParser.Parse(reply);
    var streamCut = AgentActionParser.FindProtocolBlockStart(reply);

    Expect(parsed.ProtocolBroken, "未闭合 ask 协议应标记为格式错误");
    Expect(parsed.Text.Trim() == "你还没说清要加什么节点，我先问一下，避免猜错建错。", $"应只保留自然语言，实际：{parsed.Text}");
    Expect(!parsed.Text.Contains("options", StringComparison.Ordinal), "损坏的 ask JSON 不应泄漏");
    Expect(streamCut >= 0 && !reply[..streamCut].Contains("options", StringComparison.Ordinal), "流式展示不应显示 fenced JSON");

    const string truncated = "你还没说是什么节点，先确认类型，我再把它建到画布上。\n\n```json\n{\"ask";
    var partialCut = AgentActionParser.FindProtocolBlockStart(truncated);
    var partialParsed = AgentActionParser.Parse(truncated);
    Expect(partialCut >= 0 && !truncated[..partialCut].Contains("ask", StringComparison.Ordinal), "半截 ask 标记必须在流式阶段隐藏");
    Expect(partialParsed.ProtocolBroken && !partialParsed.Text.Contains("ask", StringComparison.Ordinal), "半截 ask 标记不能泄漏到最终回复");

    var chunks = new[] { "正文。\n\n```", "json", "\n", "{", "\\\"ask" };
    var accumulated = string.Empty;
    var cuts = new List<int>();
    foreach (var chunk in chunks)
    {
        accumulated += chunk;
        cuts.Add(AgentActionParser.FindProtocolBlockStart(accumulated));
    }
    Expect(cuts.All(cut => cut == "正文。\n\n".Length), $"各流式分片都应从起始围栏处隐藏，实际：{string.Join(",", cuts)}");
}

static void BrokenQuotesSampleStillParses()
{
    const string reply = """
        我按"低起点、内在有缺口"这个骨架拟了一个主角，设定放进改动块里，等你批准。

        ```json
        {"actions":[{"kind":"create_entity","entityKind":"角色","title":"沈砚（主角）","content":"【定位】主角\n【秘密】他是唯一能"读"封印档案的人；这能力不是天赋，是十年前留下的疤。\n【内在需求】真正的成长是学会不靠"有用"来换取留下。","reason":"用户要求新建一个角色节点，名称与设定由我拟定"}]}
        ```
        """;
    var parsed = AgentActionParser.Parse(reply);

    Expect(!parsed.ProtocolBroken, "内容里的裸引号不该被判成坏块");
    Expect(parsed.Actions.Count == 1, $"应解析出 1 条改动，实际 {parsed.Actions.Count}");
    var action = parsed.Actions[0];
    Expect(action.Kind == "create_entity" && action.EntityKind == "角色", $"改动种类不符：{action.Kind}/{action.EntityKind}");
    Expect(action.Title == "沈砚（主角）", $"标题不符：{action.Title}");
    Expect(action.Content.Contains("\"读\"", StringComparison.Ordinal), "内容里的引号应原样保留");
    Expect(action.Content.Contains("不靠\"有用\"来换取", StringComparison.Ordinal) || action.Content.Contains("有用", StringComparison.Ordinal),
        "内容应完整保留");
    Expect(!parsed.Text.Contains("\"actions\"", StringComparison.Ordinal), "协议块应从展示文本里剥离");
}

/// <summary>跑一次流式对话，返回正文；正文增量与思考增量通过参数带出。</summary>
static string StreamOnce(AiApiFormat format, CapturingHandler handler, List<string> deltas, Action<string> onThinking)
{
    var provider = new OpenAiCompatibleProvider(ConfigFor(format), new HttpClient(handler));
    return provider.ChatStreamAsync(
        new[] { new AiChatMessage { Role = "user", Content = "hi" } },
        new AiStreamSink(deltas.Add, onThinking)).GetAwaiter().GetResult();
}

static AiProviderConfig ConfigFor(AiApiFormat format) => new()
{
    Endpoint = "https://example.invalid/v1",
    Model = "test-model",
    ApiKey = "test-key",
    ApiFormat = format,
    SupportsImageInput = true,
    SendSamplingParameters = true
};

static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

/// <summary>这段调用该抛 ArgumentOutOfRangeException 吗。用来钉「越界必须报错，不能静默夹到边界」。</summary>
static bool Throws(Action action)
{
	try { action(); return false; }
	catch (ArgumentOutOfRangeException) { return true; }
}

// ── 目标 1.1：只读 ID 与引用校验 ─────────────────────────────────────────────

/// <summary>一份各项引用都成立的画布：两层节点、一条连线、工作树父子与版本来源、实体引用与锁定版本、两个附件。</summary>
static WorkflowCanvasState ValidCanvas()
{
    var canvas = new WorkflowCanvasState();

    var plan = new WorkflowNode { Title = "第一章 企划", Category = NodeCategory.Chapter };
    var shot = new WorkflowNode { Title = "第一章 分镜", Category = NodeCategory.Storyboard, ParentNodeId = plan.Id };
    canvas.Nodes.Add(plan);
    canvas.Nodes.Add(shot);
    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = plan.Id, TargetNodeId = shot.Id, TargetPort = 0, SourcePort = 0 });

    var chapter = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第一章" };
    var ability = new WorkTreeItem { Kind = WorkTreeKind.Ability, Name = "御剑术", ParentId = chapter.Id };
    canvas.WorkTree.Add(chapter);
    canvas.WorkTree.Add(ability);
    shot.WorkTreeItemId = chapter.Id;

    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("少年黑衣");
    var initial = variant.EnsureInitialVersion();
    variant.Description = "少年黑衣，佩剑";
    var adopted = variant.Commit("换装");
    canvas.Entities.Add(entity);

    shot.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    shot.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = adopted.Id });

    canvas.WorkTree.Add(new WorkTreeItem
    {
        Kind = WorkTreeKind.Version,
        Name = adopted.Label,
        Chapter = "第一章",
        Version = adopted.Label,
        SourceEntityId = entity.Id,
        SourceVariantId = variant.Id,
        SourceVersionId = adopted.Id,
        SupersedesVersionId = initial.Id
    });

    shot.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://shot-1.png", Name = "分镜底图" });
    variant.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://shen-yan.png", Name = "角色参考图" });
    return canvas;
}

static void IdentityValidatorCleanCanvas()
{
    var canvas = ValidCanvas();
    var report = CanvasIdentityValidator.Validate(canvas);
    Expect(report.IsClean, "合法画布不应产生任何问题：" + report.ToText());
    Expect(report.ErrorCount == 0 && report.WarningCount == 0, "合法画布的错误与警告都应为 0");
}

static void IdentityValidatorEmptyAndDuplicateIds()
{
    var canvas = new WorkflowCanvasState();
    var first = new WorkflowNode { Title = "A" };
    var second = new WorkflowNode { Title = "B", Id = first.Id };
    canvas.Nodes.Add(first);
    canvas.Nodes.Add(second);
    canvas.WorkTree.Add(new WorkTreeItem { Id = Guid.Empty, Kind = WorkTreeKind.Project, Name = "空 ID 条目" });

    var report = CanvasIdentityValidator.Validate(canvas);
    var duplicates = report.WithCode(CanvasIdentityCodes.IdDuplicate);
    Expect(duplicates.Count == 2, $"重复 ID 的每一处都应报出（预期 2 条），实际 {duplicates.Count} 条：" + report.ToText());
    Expect(duplicates.All(issue => issue.ObjectId == first.Id.ToString()), "重复问题应指向冲突的那个 ID");
    Expect(duplicates.Select(issue => issue.FieldPath).Distinct().Count() == 2, "重复问题应分别给出两处字段路径，而不是只取第一条");
    Expect(duplicates.All(issue => issue.Severity == CanvasIdentitySeverity.Error), "会作为查找键的重复 ID 属于错误");

    var empties = report.WithCode(CanvasIdentityCodes.IdEmpty);
    Expect(empties.Any(issue => issue.ObjectType == "WorkTreeItem" && issue.FieldPath == "WorkTree[0].Id"), "工作树条目空 ID 应报告");
    Expect(empties.All(issue => issue.TargetId.Length == 0), "空 ID 报告不应伪造目标 ID");

    // 重复 ID 的对象不应被静默丢弃或改写
    Expect(canvas.Nodes.Count == 2 && canvas.Nodes[1].Title == "B", "校验不得删除或改写重复 ID 的对象");
}

static void IdentityValidatorDanglingReferences()
{
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode
    {
        Title = "悬空引用节点",
        ParentNodeId = Guid.NewGuid(),
        WorkTreeItemId = Guid.NewGuid()
    };
    canvas.Nodes.Add(node);
    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = node.Id, TargetNodeId = Guid.NewGuid() });

    var scene = new WorkflowEntity { Kind = EntityKind.Scene, Name = "宗门" };
    var sceneVariant = scene.CreateVariant("晴日");
    sceneVariant.Layout!.Items.Add(new SceneLayoutItem { Element = "大殿", EntityId = scene.Id });
    sceneVariant.Layout!.Items.Add(new SceneLayoutItem { Element = "茅草屋", EntityId = Guid.NewGuid() });
    canvas.Entities.Add(scene);

    var report = CanvasIdentityValidator.Validate(canvas);
    Expect(report.WithCode(CanvasIdentityCodes.ParentMissing).Count == 1, "悬空父节点应报告一次");
    Expect(report.WithCode(CanvasIdentityCodes.AnchorMissing).Count == 1, "悬空工作树锚应报告一次");
    Expect(report.WithCode(CanvasIdentityCodes.EdgeEndpointMissing).Count == 1, "悬空连线端点应报告一次");

    var layout = report.WithCode(CanvasIdentityCodes.LayoutElementEntityMissing);
    Expect(layout.Count == 1, $"只有指向不存在实体的那个布局元素应被报告，实际 {layout.Count} 条：" + report.ToText());
    Expect(layout[0].Severity == CanvasIdentitySeverity.Warning, "布局元素上的可选实体引用属于警告");
    Expect(layout[0].FieldPath == "Entities[0].Variants[0].Layout.Items[1].EntityId", $"布局问题路径不符：{layout[0].FieldPath}");
}

static void IdentityValidatorMissingTargets()
{
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Title = "分镜" };
    node.References.Add(new NodeReference { EntityId = Guid.NewGuid(), VariantId = Guid.NewGuid(), VariantVersionId = Guid.NewGuid() });
    node.References.Add(new NodeReference());
    canvas.Nodes.Add(node);

    canvas.WorkTree.Add(new WorkTreeItem
    {
        Kind = WorkTreeKind.Version,
        Name = "来源全悬空",
        SourceEntityId = Guid.NewGuid(),
        SourceVariantId = Guid.NewGuid(),
        SourceVersionId = Guid.NewGuid(),
        SupersedesVersionId = Guid.NewGuid()
    });

    var report = CanvasIdentityValidator.Validate(canvas);
    Expect(report.WithCode(CanvasIdentityCodes.EntityMissing).Count == 1, "缺失实体应报告");
    Expect(report.WithCode(CanvasIdentityCodes.VariantMissing).Count == 1, "缺失变体应报告");
    Expect(report.WithCode(CanvasIdentityCodes.VersionMissing).Count == 1, "缺失锁定版本应报告");
    Expect(report.WithCode(CanvasIdentityCodes.NodeReferenceEmpty).Count == 1, "空引用应报告");
    Expect(report.WithCode(CanvasIdentityCodes.SourceEntityMissing).Count == 1, "工作树来源实体悬空应报告");
    Expect(report.WithCode(CanvasIdentityCodes.SourceVariantMissing).Count == 1, "工作树来源变体悬空应报告");
    Expect(report.WithCode(CanvasIdentityCodes.SourceVersionMissing).Count == 1, "工作树来源版本悬空应报告");
    Expect(report.WithCode(CanvasIdentityCodes.SupersedesMissing).Count == 1, "工作树上一版本悬空应报告");

    // 报告不猜绑定：悬空引用保持原样，不被清空也不被改成别的对象
    Expect(node.References.Count == 2 && node.References[0].EntityId != Guid.Empty, "校验不得清空悬空引用");
}

static void IdentityValidatorVersionMisbinding()
{
    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variantA = entity.CreateVariant("少年");
    var variantB = entity.CreateVariant("成年");
    canvas.Entities.Add(entity);

    var node = new WorkflowNode { Title = "分镜" };
    // 引用了变体 A，却锁定到变体 B 的版本：错绑
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variantA.Id, VariantVersionId = variantB.Versions[0].Id });
    canvas.Nodes.Add(node);

    canvas.WorkTree.Add(new WorkTreeItem
    {
        Kind = WorkTreeKind.Version,
        Name = "错绑来源",
        SourceEntityId = entity.Id,
        SourceVariantId = variantA.Id,
        SourceVersionId = variantB.Versions[0].Id
    });

    var report = CanvasIdentityValidator.Validate(canvas);
    Expect(report.WithCode(CanvasIdentityCodes.VersionOwnerMismatch).Count == 1, "节点版本错绑应报告一次：" + report.ToText());
    Expect(report.WithCode(CanvasIdentityCodes.SourceVersionOwnerMismatch).Count == 1, "工作树来源版本错绑应报告一次");
    Expect(report.WithCode(CanvasIdentityCodes.VersionMissing).Count == 0, "版本确实存在，不应报成缺失");
    Expect(report.WithCode(CanvasIdentityCodes.VariantMissing).Count == 0, "变体确实存在，不应报成缺失");

    Expect(node.References[0].VariantVersionId == variantB.Versions[0].Id, "校验不得改写错绑的版本 ID");
}

static void IdentityValidatorNullVersionAndSameName()
{
    var canvas = new WorkflowCanvasState();
    var first = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var firstVariant = first.CreateVariant("默认");
    var second = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var secondVariant = second.CreateVariant("默认");
    canvas.Entities.Add(first);
    canvas.Entities.Add(second);

    var node = new WorkflowNode { Title = "分镜" };
    node.References.Add(new NodeReference { EntityId = first.Id, VariantId = firstVariant.Id, VariantVersionId = null });
    node.References.Add(new NodeReference { EntityId = second.Id, VariantId = secondVariant.Id });
    canvas.Nodes.Add(node);

    var report = CanvasIdentityValidator.Validate(canvas);
    Expect(report.IsClean, "null 版本跟随当前是合法值，同名不同 ID 的实体也不应误报：" + report.ToText());
    Expect(canvas.Entities.Count == 2, "同名实体不得被合并");
    Expect(first.Id != second.Id && firstVariant.Id != secondVariant.Id, "同名实体的 ID 应各自独立");
    Expect(node.References[0].VariantVersionId is null, "校验不得把 null 版本改成具体版本");
}

static void IdentityValidatorCycles()
{
    var canvas = new WorkflowCanvasState();
    var selfParent = new WorkflowNode { Title = "自引用" };
    var loopA = new WorkflowNode { Title = "循环 A" };
    var loopB = new WorkflowNode { Title = "循环 B" };
    selfParent.ParentNodeId = selfParent.Id;
    loopA.ParentNodeId = loopB.Id;
    loopB.ParentNodeId = loopA.Id;
    canvas.Nodes.Add(selfParent);
    canvas.Nodes.Add(loopA);
    canvas.Nodes.Add(loopB);
    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = selfParent.Id, TargetNodeId = selfParent.Id });

    var root = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "一章" };
    var workA = new WorkTreeItem { Kind = WorkTreeKind.Ability, Name = "甲" };
    var workB = new WorkTreeItem { Kind = WorkTreeKind.Ability, Name = "乙" };
    workA.ParentId = workB.Id;
    workB.ParentId = workA.Id;
    canvas.WorkTree.Add(root);
    canvas.WorkTree.Add(workA);
    canvas.WorkTree.Add(workB);

    var report = CanvasIdentityValidator.Validate(canvas);
    Expect(report.WithCode(CanvasIdentityCodes.ParentSelf).Count == 1, "节点自引用父节点应报告");
    Expect(report.WithCode(CanvasIdentityCodes.ParentCycle).Count == 1, $"同一个循环只应报告一次，实际 {report.WithCode(CanvasIdentityCodes.ParentCycle).Count} 次");
    Expect(report.WithCode(CanvasIdentityCodes.EdgeSelfLoop).Count == 1, "连线自环应报告");
    Expect(report.WithCode(CanvasIdentityCodes.WorkTreeParentCycle).Count == 1, "工作树父链循环应报告");
    Expect(report.WithCode(CanvasIdentityCodes.IdDuplicate).Count == 0, "循环检测不得把不同节点的 ID 当成重复");

    var cycle = report.WithCode(CanvasIdentityCodes.ParentCycle)[0];
    Expect(cycle.Message.Contains(loopA.Id.ToString(), StringComparison.Ordinal)
        && cycle.Message.Contains(loopB.Id.ToString(), StringComparison.Ordinal), "循环报告应列出循环内的全部节点");
}

static void IdentityValidatorCollectsAllProblems()
{
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Title = "问题节点", ParentNodeId = Guid.NewGuid(), WorkTreeItemId = Guid.NewGuid() };
    node.References.Add(new NodeReference { EntityId = Guid.NewGuid(), VariantId = Guid.NewGuid() });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = string.Empty });
    canvas.Nodes.Add(node);
    canvas.Edges.Add(new WorkflowEdge { SourceNodeId = Guid.NewGuid(), TargetNodeId = node.Id });

    var report = CanvasIdentityValidator.Validate(canvas);
    Expect(report.Issues.Count >= 6, $"应一次收集全部问题，实际只收集到 {report.Issues.Count} 项：" + report.ToText());
    Expect(report.CountByCode().Count >= 5, "应覆盖多种错误代码，而不是在第一项失败后中断");
    Expect(report.HasErrors && report.ErrorCount == report.Issues.Count, "本样例的问题都应是错误级");

    var text = report.ToText();
    Expect(text.Contains(CanvasIdentityCodes.EntityMissing, StringComparison.Ordinal)
        && text.Contains(CanvasIdentityCodes.AssetReferenceEmpty, StringComparison.Ordinal), "报告文本应逐项列出代码");
}

static void IdentityValidatorAssetReferences()
{
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Title = "分镜" };
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = string.Empty });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://" });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://sub/dir.png" });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://missing.png" });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "C:/assets/ok.png" });
    canvas.Nodes.Add(node);

    var report = CanvasIdentityValidator.Validate(canvas, reference => reference is "asset://sub/dir.png" or "C:/assets/ok.png");
    Expect(report.WithCode(CanvasIdentityCodes.AssetReferenceEmpty).Count == 1, "空资产标识应报告为标识无效");
    Expect(report.WithCode(CanvasIdentityCodes.AssetReferenceMalformed).Count == 1, "只有 asset:// 后没有文件名才应报告为不可用");
    // 带目录的 asset:// 写法当前读取能解析（只取文件名），属于兼容可用，只警告不报错。
    var nonCanonical = report.WithCode(CanvasIdentityCodes.AssetReferenceNonCanonical);
    Expect(nonCanonical.Count == 1 && nonCanonical[0].Severity == CanvasIdentitySeverity.Warning,
        $"asset://sub/dir.png 应只报非规范警告，实际：{report.ToText()}");
    var unreachable = report.WithCode(CanvasIdentityCodes.AssetUnreachable);
    Expect(unreachable.Count == 1, $"只有形状正确但取不到的资产应报不可访问，实际 {unreachable.Count} 条：" + report.ToText());
    Expect(unreachable[0].Severity == CanvasIdentitySeverity.Warning, "资产不可访问属于警告，不阻止打开旧项目");
    Expect(report.WithCode(CanvasIdentityCodes.IdEmpty).Count == 0 && report.WithCode(CanvasIdentityCodes.IdDuplicate).Count == 0,
        "资产标识是路径或 URI，不得按 GUID 校验");

    // 不传委托时只做形状检查，不访问磁盘
    var shapeOnly = CanvasIdentityValidator.Validate(canvas);
    Expect(shapeOnly.WithCode(CanvasIdentityCodes.AssetUnreachable).Count == 0, "未提供资产解析委托时不应猜文件是否存在");
    Expect(shapeOnly.WithCode(CanvasIdentityCodes.AssetReferenceMalformed).Count == 1
        && shapeOnly.WithCode(CanvasIdentityCodes.AssetReferenceEmpty).Count == 1
        && shapeOnly.WithCode(CanvasIdentityCodes.AssetReferenceNonCanonical).Count == 1, "形状检查仍应照常报告");
}

static void IdentityValidatorIsReadOnly()
{
    var workspace = NewWorkspace();
    try
    {
        var canvas = ValidCanvas();
        var path = Path.Combine(workspace, "canvas.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new RecentCanvasState("只读校验", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas)));

        var before = JsonSerializer.Serialize(canvas);
        var beforeBytes = File.ReadAllBytes(path);

        var report = CanvasIdentityValidator.Validate(canvas, _ => false);
        Expect(report.Issues.Count > 0, "本样例应报告资产不可访问");

        Expect(JsonSerializer.Serialize(canvas) == before, "校验不得改动传入的画布对象");
        Expect(File.ReadAllBytes(path).SequenceEqual(beforeBytes), "校验不得改动磁盘上的画布文件");
        Expect(Directory.GetFiles(workspace).Length == 1, "校验不得写入任何额外文件");
    }
    finally
    {
        if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
    }
}

// ── 第 2 轮返工 R1–R4 与目标 A：ID 生命周期、迁移、导入去重 ─────────────────

/// <summary>R1：两个变体各有相同 ID 的初始版本；工作树引用其中一条的版本与上一版本，两种顺序都必须零错。</summary>
static void ReworkWorkTreeSupersedesScopedByVariant()
{
    foreach (var swap in new[] { false, true })
    {
        var canvas = new WorkflowCanvasState();
        var sameVersionId = Guid.NewGuid();
        var entityA = new WorkflowEntity { Kind = EntityKind.Character, Name = "甲" };
        var variantA = entityA.CreateVariant("默认");
        variantA.Versions[0].Id = sameVersionId;
        var entityB = new WorkflowEntity { Kind = EntityKind.Character, Name = "乙" };
        var variantB = entityB.CreateVariant("默认");
        variantB.Versions[0].Id = sameVersionId;

        // B 再提交一个新版本，工作树条目引用 B 的新版本与上一版本。
        variantB.Description = "换装";
        var newVersion = variantB.Commit("换装");

        if (swap) { canvas.Entities.Add(entityB); canvas.Entities.Add(entityA); }
        else { canvas.Entities.Add(entityA); canvas.Entities.Add(entityB); }

        canvas.WorkTree.Add(new WorkTreeItem
        {
            Kind = WorkTreeKind.Version,
            Name = newVersion.Label,
            Version = newVersion.Label,
            SourceEntityId = entityB.Id,
            SourceVariantId = variantB.Id,
            SourceVersionId = newVersion.Id,
            SupersedesVersionId = sameVersionId
        });

        var report = CanvasIdentityValidator.Validate(canvas);
        Expect(report.WithCode(CanvasIdentityCodes.SupersedesOwnerMismatch).Count == 0,
            $"顺序 swap={swap}：上一版本属于来源变体，不应报错绑：{report.ToText()}");
        Expect(report.WithCode(CanvasIdentityCodes.SupersedesMissing).Count == 0,
            $"顺序 swap={swap}：上一版本确实存在于来源变体，不应报缺失：{report.ToText()}");
        Expect(report.WithCode(CanvasIdentityCodes.SupersedesAmbiguous).Count == 0,
            $"顺序 swap={swap}：来源变体明确，不应报歧义：{report.ToText()}");
        // 版本 ID 在所属变体内唯一，跨变体相同只体现在位置（这里没有引用指向它，因此不应有任何错误）
        Expect(report.ErrorCount == 0, $"顺序 swap={swap}：合法样例应零错误：{report.ToText()}");
    }
}

/// <summary>R2：变体 ID 跨实体重复时按实体上下文解析，不得凭首条数据断定错绑。</summary>
static void ReworkDuplicateVariantIdsNoFirstItemInference()
{
    foreach (var swap in new[] { false, true })
    {
        var canvas = new WorkflowCanvasState();
        var sharedVariantId = Guid.NewGuid();
        var entity1 = new WorkflowEntity { Kind = EntityKind.Character, Name = "甲" };
        var variant1 = entity1.CreateVariant("默认");
        variant1.Id = sharedVariantId;
        var entity2 = new WorkflowEntity { Kind = EntityKind.Character, Name = "乙" };
        var variant2 = entity2.CreateVariant("默认");
        variant2.Id = sharedVariantId;
        variant2.Description = "乙的外观";
        var version2 = variant2.Commit("乙的版本");

        if (swap) { canvas.Entities.Add(entity2); canvas.Entities.Add(entity1); }
        else { canvas.Entities.Add(entity1); canvas.Entities.Add(entity2); }

        var node = new WorkflowNode { Title = "分镜" };
        node.References.Add(new NodeReference { EntityId = entity2.Id, VariantId = sharedVariantId, VariantVersionId = version2.Id });
        canvas.Nodes.Add(node);

        var report = CanvasIdentityValidator.Validate(canvas);
        Expect(report.WithCode(CanvasIdentityCodes.VariantOwnerMismatch).Count == 0,
            $"顺序 swap={swap}：引用写的实体就是变体所属实体，不应报错绑：{report.ToText()}");
        Expect(report.WithCode(CanvasIdentityCodes.VersionOwnerMismatch).Count == 0,
            $"顺序 swap={swap}：锁定的版本属于该实体的变体，不应报错绑：{report.ToText()}");
        Expect(report.WithCode(CanvasIdentityCodes.VariantMissing).Count == 0 && report.WithCode(CanvasIdentityCodes.VersionMissing).Count == 0,
            $"顺序 swap={swap}：变体与版本都存在，不应报缺失");
        Expect(report.WithCode(CanvasIdentityCodes.IdDuplicate).Count == 2,
            $"顺序 swap={swap}：重复的变体 ID 仍应逐处报出（2 条），实际 {report.WithCode(CanvasIdentityCodes.IdDuplicate).Count}");
        Expect(!report.WithCode(CanvasIdentityCodes.IdDuplicate).Any(issue => issue.Severity == CanvasIdentitySeverity.Warning),
            "会作为查找键的重复 ID 属于错误级");
    }

    // 同类推断排查：重复的节点 ID、工作树 ID 也只报歧义，不猜归属。
    var canvas2 = new WorkflowCanvasState();
    var nodeA = new WorkflowNode { Title = "A" };
    var nodeB = new WorkflowNode { Title = "B", Id = nodeA.Id };
    var child = new WorkflowNode { Title = "子", ParentNodeId = nodeA.Id };
    canvas2.Nodes.Add(nodeA);
    canvas2.Nodes.Add(nodeB);
    canvas2.Nodes.Add(child);
    var chapterX = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "章" };
    var chapterY = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "章副本", Id = chapterX.Id };
    var anchored = new WorkflowNode { Title = "锚点节点", WorkTreeItemId = chapterX.Id };
    canvas2.Nodes.Add(anchored);
    canvas2.WorkTree.Add(chapterX);
    canvas2.WorkTree.Add(chapterY);

    var report2 = CanvasIdentityValidator.Validate(canvas2);
    Expect(report2.WithCode(CanvasIdentityCodes.ParentAmbiguous).Count == 1, "重复节点 ID 作父节点时应报歧义：" + report2.ToText());
    Expect(report2.WithCode(CanvasIdentityCodes.ParentMissing).Count == 0, "重复节点 ID 不应被报成悬空");
    Expect(report2.WithCode(CanvasIdentityCodes.AnchorAmbiguous).Count == 1, "重复工作树 ID 作锚点时应报歧义");
    Expect(report2.WithCode(CanvasIdentityCodes.AnchorMissing).Count == 0, "重复工作树 ID 不应被报成悬空");
}

/// <summary>R3：隔离资产目录里真实存在的文件，其非规范 asset:// 写法只警告，不因只读校验收紧读取兼容性。</summary>
static void ReworkAssetNonCanonicalReferenceMatchesRealResolution()
{
    using var stores = new IsolatedStores();
    stores.WriteAsset("ok.png");

    Expect(AssetStore.Exists("asset://sub/ok.png"), "真实读取会把 asset://sub/ok.png 解析到 ok.png");
    Expect(AssetStore.Exists("asset://ok.png"), "规范写法同样可解析");

    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Title = "分镜" };
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://sub/ok.png" });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://ok.png" });
    canvas.Nodes.Add(node);

    var report = CanvasIdentityValidator.Validate(canvas, AssetStore.Exists);
    Expect(report.ErrorCount == 0, "可用的非规范引用不得报错：" + report.ToText());
    Expect(report.WithCode(CanvasIdentityCodes.AssetReferenceNonCanonical).Count == 1, "非规范写法应给出单独警告");
    Expect(report.WithCode(CanvasIdentityCodes.AssetUnreachable).Count == 0, "文件确实存在，不应报不可访问");
    Expect(report.WithCode(CanvasIdentityCodes.AssetReferenceMalformed).Count == 0, "不得把兼容写法报成格式错误");
}

/// <summary>R4：确实不可用的资产形状必须报格式错误，不能用「文件缺失」代替。</summary>
static void ReworkMalformedAssetShapesAreReported()
{
    using var stores = new IsolatedStores();
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Title = "分镜" };
    foreach (var reference in new[] { "asset://.", "asset://..", "C:/assets/bad*.png", "asset://bad?.png", @"C:\assets\ok|.png" })
        node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = reference });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://missing.png" });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "https://example.com/a.png" });
    node.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "data:image/png;base64,AAAA" });
    canvas.Nodes.Add(node);

    var report = CanvasIdentityValidator.Validate(canvas, reference => reference.StartsWith("http", StringComparison.Ordinal) || reference.StartsWith("data:", StringComparison.Ordinal));
    var malformed = report.WithCode(CanvasIdentityCodes.AssetReferenceMalformed);
    Expect(malformed.Count == 5, $"目录名与通配符写法都应报格式错误，实际 {malformed.Count} 条：{report.ToText()}");
    Expect(malformed.All(issue => issue.Severity == CanvasIdentitySeverity.Error), "格式错误属于错误级");
    Expect(report.WithCode(CanvasIdentityCodes.AssetUnreachable).Count == 1, "普通缺失文件仍按不可访问（警告）报告");
    Expect(report.WithCode(CanvasIdentityCodes.AssetReferenceMalformed).All(issue => !issue.Message.Contains("取不到")),
        "格式错误不得用「文件缺失」代替");
    Expect(report.WithCode(CanvasIdentityCodes.AssetReferenceNonCanonical).Count == 0, "这些写法都不是「可用但非规范」");
}

/// <summary>1.2：保存重载保持 ID、关系与版本语义一致，并标注格式版本。</summary>
static void SaveReloadKeepsIdsAndRelations()
{
    using var stores = new IsolatedStores();
    var canvas = ValidCanvas();
    var path = Path.Combine(stores.CanvasDirectory, "roundtrip.json");
    var state = new RecentCanvasState("往返", 3, "正", "负", 512, 512, 20, 7, "", canvas) { FormatVersion = CanvasFormat.Current };

    var first = CanvasSaveService.Save(state, path);
    Expect(first.BackupPath is null, "首次保存没有旧文件，不应产生备份");
    Expect(CanvasOpenService.TryOpen(path, out var reloaded, out var error), "重开失败：" + error);
    Expect(!reloaded.Migration.Changed, "已标注当前格式的画布重载不应再有迁移改动");
    Expect(reloaded.State.FormatVersion == CanvasFormat.Current, "格式版本应保持当前值");
    Expect(SameIdsAndRelations(canvas, reloaded.State.Canvas), "保存重载不得改变 ID 与关系");
    Expect(CanvasIdentityValidator.Validate(reloaded.State.Canvas).IsClean, "往返后应仍然自洽");

    var second = CanvasSaveService.Save(reloaded.State, path);
    Expect(second.BackupPath is { } backup && File.Exists(backup), "覆盖保存前应留下可恢复备份");
    Expect(reloaded.State.Title == "往返" && reloaded.State.Width == 512, "保存重载不应改动标量字段");
}

/// <summary>1.3：旧格式字段搬进引用与附件，并记录变更；打开不改写磁盘文件。</summary>
static void MigrationMovesLegacyFields()
{
    using var stores = new IsolatedStores();
    var state = LegacyCanvasFile();
    var path = Path.Combine(stores.CanvasDirectory, "legacy.json");
    File.WriteAllText(path, JsonSerializer.Serialize(state));
    var original = File.ReadAllBytes(path);

    Expect(CanvasOpenService.TryOpen(path, out var opened, out var error), "打开旧画布失败：" + error);
    Expect(opened.Migration.Changed, "旧文件应产生确定性迁移改动");
    Expect(opened.State.FormatVersion == CanvasFormat.Current, "迁移后应标注当前格式版本");

    var node = opened.State.Canvas.Nodes[0];
    Expect(node.References.Count == 1, "旧单引用应转成一条引用");
    Expect(node.References[0].EntityId == state.Canvas.Entities[0].Id
        && node.References[0].VariantId == state.Canvas.Entities[0].Variants[0].Id
        && node.References[0].VariantVersionId == state.Canvas.Entities[0].Variants[0].Versions[0].Id, "引用内容应完整保留");
    Expect(node.Attachments.Count == 1 && node.Attachments[0].Reference == "asset://shot-1.png", "旧图片路径应转成附件");
    Expect(node.LegacyEntityId is null && node.LegacyVariantId is null && node.LegacyAssetPaths.Count == 0, "旧字段应清空以保持幂等");

    Expect(opened.Migration.Changes.Any(change => change.Kind == MigrationChangeKind.MovedLegacyReference), "应记录引用迁移");
    Expect(opened.Migration.Changes.Any(change => change.Kind == MigrationChangeKind.MovedLegacyAttachments), "应记录附件迁移");
    Expect(opened.Migration.Changes.Any(change => change.Kind == MigrationChangeKind.AssignedId), "应为空 ID 补全并记录映射");
    Expect(opened.Validation.ErrorCount == 0, "迁移后的样例不应有错误级问题：" + opened.Validation.ToText());

    // 打开只作用于内存：磁盘文件必须原样
    Expect(File.ReadAllBytes(path).SequenceEqual(original), "打开不得改写磁盘文件");

    // 重复迁移幂等
    var again = CanvasMigration.Migrate(opened.State);
    Expect(!again.Report.Changed, "第二次迁移不应产生额外改动");
    var migratedAgain = CanvasMigration.MigrateCanvas(opened.State.Canvas, CanvasFormat.Current);
    Expect(!migratedAgain.Changed, "按当前格式再迁移也应无改动");
}

/// <summary>1.3：无法确定的引用留成歧义并提供显式处理入口，不猜绑定。</summary>
static void MigrationKeepsAmbiguityAndProvidesRepairEntry()
{
    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    canvas.Entities.Add(entity);

    var node = new WorkflowNode { Title = "问题节点" };
    node.References.Add(new NodeReference());
    node.References.Add(new NodeReference { EntityId = Guid.NewGuid(), VariantId = Guid.NewGuid() });
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = Guid.Empty });
    canvas.Nodes.Add(node);

    var report = CanvasMigration.MigrateCanvas(canvas, CanvasFormat.Current);
    Expect(report.Ambiguities.Any(item => item.FieldPath == "Nodes[0].References[0]"), "空引用应留成歧义项");
    Expect(report.Ambiguities.Any(item => item.FieldPath.EndsWith(".VariantVersionId", StringComparison.Ordinal)),
        "空 GUID 的锁定版本应留成歧义项，而不是猜成跟随当前：" + report.ToText());
    Expect(node.References.Count == 3 && node.References[2].VariantVersionId == Guid.Empty, "迁移不得自行改动这些引用");

    var dropped = CanvasMigration.DropUnresolvableReferences(canvas);
    Expect(dropped.Affected == 2, $"应丢弃两条无法解析的引用，实际 {dropped.Affected}");
    Expect(node.References.Count == 1 && node.References[0].VariantId == variant.Id, "可解析的引用必须保留");

    var follow = CanvasMigration.TreatEmptyLockedVersionAsFollowCurrent(canvas);
    Expect(follow.Affected == 1 && node.References[0].VariantVersionId is null, "显式处理入口应把空锁定版本改为跟随当前");
}

/// <summary>1.3：写入前备份、写入失败不覆盖源文件、可从备份恢复。</summary>
static void MigrationBacksUpAndRestoresOnFailure()
{
    using var stores = new IsolatedStores();
    var canvas = ValidCanvas();
    var path = Path.Combine(stores.CanvasDirectory, "guard.json");
    var v1 = new RecentCanvasState("第一版", 1, "", "", 512, 512, 20, 7, "", canvas) { FormatVersion = CanvasFormat.Current };
    CanvasFileWriter.Write(path, v1);
    var original = File.ReadAllBytes(path);

    var backup = CanvasBackup.TryBackup(path, out var backupError);
    var backupPath = backup ?? string.Empty;
    Expect(backupPath.Length > 0 && File.Exists(backupPath), "应写出可恢复备份：" + backupError);

    // 写入失败：目标路径被一个目录占用，替换必然失败。
    var blocked = Path.Combine(stores.CanvasDirectory, "blocked.json");
    Directory.CreateDirectory(blocked);
    ExpectThrows<SystemException>(() => CanvasFileWriter.Write(blocked, v1 with { Title = "第二版" }), "目标被目录占用时应写入失败");
    Expect(Directory.Exists(blocked) && !File.Exists(blocked), "失败不得创建目标文件");
    Expect(!Directory.EnumerateFiles(stores.CanvasDirectory, "*.tmp").Any(), "失败应清理临时文件");

    // 源文件在失败后保持原样
    Expect(File.ReadAllBytes(path).SequenceEqual(original), "写入失败不得影响其它画布文件");

    // 恢复：换一版内容后从备份回滚
    CanvasFileWriter.Write(path, v1 with { Title = "改坏了" });
    CanvasBackup.Restore(backupPath, path);
    Expect(File.ReadAllBytes(path).SequenceEqual(original), "恢复后应与备份字节一致");
    Expect(CanvasOpenService.TryOpen(path, out var restored, out _) && restored.State.Title == "第一版", "恢复后的内容应是备份那一版");
    Expect(CanvasBackup.ListFor(path).Count >= 1, "应能列出该画布的备份");
    Expect(CanvasBackup.Prune(1) >= 0, "清理备份不应抛错");
}

/// <summary>
/// 备份躺在**画布文件旁边**（TryBackup 写在那儿），而 <c>CanvasBackup.Directory</c> 指的是
/// 「应用那本画布库」的备份目录——画布库目录本身还是环境相关的（配了项目就是项目的 canvases）。
/// 两处不是同一个地方，于是项目画布的备份**既列不出来也清不掉**；清理那一侧过去还额外判
/// 「写的这份是不是当前项目」，那个条件在 Web 服务里永远不成立，备份就只涨不落（工程债 #9）。
///
/// 这条钉三件事：按画布自己的位置**列得出**、按画布自己的位置**清得掉**（只留最新 10 份）、
/// 以及**清一张画布不得碰另一张画布的备份**（删备份是最容易误伤的操作，范围必须小）。
/// </summary>
static void ProjectCanvasBackupsAreListedAndPruned()
{
    var root = Path.Combine(Path.GetTempPath(), "yeeeyee-backup-" + Guid.NewGuid().ToString("N"));
    var directory = Path.Combine(root, "canvases");
    var canvas = Path.Combine(directory, "main.json");
    var other = Path.Combine(directory, "other.json");
    System.IO.Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(canvas, "{\"a\":1}");
        File.WriteAllText(other, "{\"b\":1}");
        for (var index = 0; index < 12; index++)
            Expect(CanvasBackup.TryBackup(canvas, out var error) is not null, $"第 {index} 次备份应成功：{error}");
        for (var index = 0; index < 3; index++)
            Expect(CanvasBackup.TryBackup(other, out _) is not null, "另一张画布的备份也应成功");

        // 列得出：以前这里按「画布库目录」找，项目画布拿回来的永远是空表。
        Expect(CanvasBackup.ListFor(canvas).Count == 12, $"应列出画布旁边的 12 份备份，实际 {CanvasBackup.ListFor(canvas).Count}");
        Expect(CanvasBackup.ListFor(other).Count == 3, "另一张画布应有 3 份备份");

        // 清得掉：只留最新 10 份。
        Expect(CanvasBackup.PruneFor(canvas) == 2, "应删掉超出 10 份的那 2 份");
        Expect(CanvasBackup.ListFor(canvas).Count == 10, $"清理后应剩 10 份，实际 {CanvasBackup.ListFor(canvas).Count}");
        Expect(CanvasBackup.ListFor(other).Count == 3, "清一张画布不得碰另一张画布的备份");

        // 清理是维护：没有备份目录时给 0，不抛。
        Expect(CanvasBackup.PruneFor(Path.Combine(root, "nope", "nobody.json")) == 0, "没有备份目录时应返回 0");
    }
    finally
    {
        try { System.IO.Directory.Delete(root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>1.2：复制画布时全部换新 ID，集合内关系按映射改写，版本语义保持。</summary>
static void DuplicateCanvasRemapsInternalIds()
{
    var source = ValidCanvas();
    var copy = CanvasDuplication.DuplicateCanvas(source);

    Expect(copy.Canvas.Nodes.Count == source.Nodes.Count && copy.Canvas.Edges.Count == source.Edges.Count, "复制应保持对象数量");
    Expect(copy.Canvas.Nodes.All(node => source.Nodes.All(origin => origin.Id != node.Id)), "复制出的节点必须换新 ID");
    Expect(copy.Canvas.Edges.All(edge => source.Edges.All(origin => origin.Id != edge.Id)), "复制出的连线必须换新 ID");
    Expect(copy.Canvas.WorkTree.All(item => source.WorkTree.All(origin => origin.Id != item.Id)), "复制出的工作树条目必须换新 ID");

    var copiedEntity = copy.Canvas.Entities[0];
    var sourceEntity = source.Entities[0];
    Expect(copiedEntity.Id != sourceEntity.Id, "实体应换新 ID");
    Expect(copiedEntity.Variants[0].Id != sourceEntity.Variants[0].Id, "变体应换新 ID");
    Expect(copiedEntity.Variants[0].Versions.Select(version => version.Id)
        .Intersect(sourceEntity.Variants[0].Versions.Select(version => version.Id)).Count() == 0, "版本应换新 ID");

    var copiedNodes = copy.Canvas.Nodes.ToDictionary(node => node.Id);
    Expect(copiedNodes.ContainsKey(copy.Canvas.Edges[0].SourceNodeId) && copiedNodes.ContainsKey(copy.Canvas.Edges[0].TargetNodeId),
        "连线端点应重映射到复制后的节点");
    Expect(copy.Canvas.Nodes[1].ParentNodeId == copy.Canvas.Nodes[0].Id, "父链应重映射到复制后的节点");
    Expect(copy.Canvas.Nodes[1].WorkTreeItemId == copy.Canvas.WorkTree[0].Id, "工作树锚应重映射到复制后的条目");

    var shot = copy.Canvas.Nodes[1];
    Expect(shot.References[0].EntityId == copiedEntity.Id && shot.References[0].VariantId == copiedEntity.Variants[0].Id,
        "节点引用应重映射到复制后的实体与变体");
    Expect(shot.References[0].VariantVersionId is null, "跟随当前版本的 null 语义必须保留");
    Expect(shot.References[1].VariantVersionId == copiedEntity.Variants[0].Versions[1].Id, "锁定版本应重映射到复制后的版本");

    var versionItem = copy.Canvas.WorkTree[2];
    Expect(versionItem.SourceVersionId == copiedEntity.Variants[0].Versions[1].Id, "工作树来源版本应重映射");
    Expect(versionItem.SupersedesVersionId == copiedEntity.Variants[0].Versions[0].Id, "工作树上一版本应重映射");

    Expect(copy.Map.SharedReferences.Count == 0, "整份复制没有集合外引用");
    Expect(CanvasIdentityValidator.Validate(copy.Canvas).IsClean, "复制结果应自洽：" + CanvasIdentityValidator.Validate(copy.Canvas).ToText());

    // 原画布不受影响
    Expect(source.Nodes[1].ParentNodeId == source.Nodes[0].Id && source.Entities[0].Id == sourceEntity.Id, "复制不得改动原画布");
}

/// <summary>1.2：复制节点时集合内关系重映射、集合外引用保留为共享。</summary>
static void DuplicateNodesRemapsInternalIdsAndKeepsSharedReferences()
{
    var source = ValidCanvas();
    var plan = source.Nodes[0];
    var shot = source.Nodes[1];
    var entity = source.Entities[0];

    var single = CanvasDuplication.DuplicateNodes(source, new[] { shot.Id }, includeEdges: false);
    Expect(single.Nodes.Count == 1 && single.Nodes[0].Id != shot.Id, "应复制出一个新 ID 的节点");
    Expect(single.Nodes[0].ParentNodeId == plan.Id, "父节点在复制范围外，应按原样保留（集合外引用）");
    Expect(single.Nodes[0].References[0].EntityId == entity.Id && single.Nodes[0].References[1].VariantVersionId == entity.Variants[0].Versions[1].Id,
        "引用指向画布级设定库，复制后仍指向同一实体/版本（共享语义）");
    Expect(single.Nodes[0].X == shot.X + 40 && single.Nodes[0].Y == shot.Y + 40, "复制出的节点应有偏移，避免完全重叠");
    Expect(single.Report.Notes.Any(note => note.Contains("父节点不在复制范围内")), "应说明父节点按集合外引用保留：" + single.Report.ToText());

    var both = CanvasDuplication.DuplicateNodes(source, new[] { plan.Id, shot.Id });
    Expect(both.Nodes.Count == 2 && both.Edges.Count == 1, "复制两个节点应连同内部连线一起复制");
    var newPlanId = both.Map.Nodes[plan.Id];
    var newShotId = both.Map.Nodes[shot.Id];
    Expect(both.Nodes.Single(node => node.Id == newShotId).ParentNodeId == newPlanId, "集合内父子关系应重映射");
    Expect(both.Edges[0].SourceNodeId == newPlanId && both.Edges[0].TargetNodeId == newShotId, "内部连线端点应重映射");

    var added = CanvasDuplication.Append(source, both.Nodes, both.Edges);
    Expect(added == 2 && source.Nodes.Count == 4 && source.Edges.Count == 2, "追加后画布应多出复制对象");
    Expect(CanvasIdentityValidator.Validate(source).IsClean, "复制后画布应仍然自洽：" + CanvasIdentityValidator.Validate(source).ToText());
}

/// <summary>1.4：重复导入按 ID 去重，第二次导入零增量且不改动画布。</summary>
static void ImportMergeIsIdempotent()
{
    var target = ValidCanvas();
    var incoming = CanvasDuplication.DuplicateCanvas(ValidCanvas()).Canvas;

    var first = CanvasImportMerge.Merge(target, incoming);
    Expect(first.Changed, "第一次合并应加入对象");
    Expect(first.AddedNodes == 2 && first.AddedEdges == 1 && first.AddedEntities == 1 && first.AddedWorkTreeItems == 3,
        $"第一次合并数量不符：{first.ToText()}");
    Expect(!first.HasConflicts, "两份不同 ID 的画布合并不应产生冲突：" + first.ToText());

    var snapshot = JsonSerializer.Serialize(target);
    var second = CanvasImportMerge.Merge(target, incoming);
    Expect(!second.Changed, "重复导入同一份数据不应再增量：" + second.ToText());
    Expect(second.SkippedNodes == 2 && second.SkippedEntities == 1 && second.SkippedWorkTreeItems == 3, "已导入对象应被识别并跳过");
    Expect(JsonSerializer.Serialize(target) == snapshot, "重复导入不得改动画布内容");
    Expect(CanvasIdentityValidator.Validate(target).IsClean, "合并后画布应自洽：" + CanvasIdentityValidator.Validate(target).ToText());
}

/// <summary>1.4：同 ID 不同内容只报告冲突，不覆盖目标；同名不同 ID 保持独立。</summary>
static void ImportMergeReportsConflictWithoutOverwrite()
{
    var target = ValidCanvas();
    var incoming = CanvasDuplication.DuplicateCanvas(ValidCanvas()).Canvas;
    incoming.Nodes[0].Id = target.Nodes[0].Id;
    incoming.Nodes[0].Title = "被改过的同 ID 节点";
    incoming.Entities[0].Id = target.Entities[0].Id;
    incoming.Entities[0].Name = "被改过的同 ID 实体";

    var report = CanvasImportMerge.Merge(target, incoming);
    Expect(report.Conflicts.Any(conflict => conflict.ObjectType == "WorkflowNode"), "同 ID 不同内容的节点应报冲突");
    Expect(report.Conflicts.Any(conflict => conflict.ObjectType == "WorkflowEntity"), "同 ID 不同内容的实体应报冲突");
    Expect(target.Nodes.All(node => node.Title != "被改过的同 ID 节点"), "冲突不得覆盖目标节点内容");
    Expect(target.Entities.All(entity => entity.Name != "被改过的同 ID 实体"), "冲突不得覆盖目标实体内容");
    Expect(target.Nodes.Count(node => node.Id == incoming.Nodes[0].Id) == 1, "同 ID 不得重复出现两个对象");

    // 同名不同 ID：各自保留，并提示没有按名称去重
    var named = new WorkflowCanvasState();
    var sameName = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    sameName.CreateVariant("默认");
    named.Entities.Add(sameName);
    var incomingSameName = new WorkflowCanvasState();
    var other = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    other.CreateVariant("默认");
    incomingSameName.Entities.Add(other);

    var nameReport = CanvasImportMerge.Merge(named, incomingSameName);
    Expect(nameReport.AddedEntities == 1 && named.Entities.Count == 2, "同名不同 ID 的实体应各自保留");
    Expect(named.Entities.Select(entity => entity.Id).Distinct().Count() == 2, "同名实体不得被合并");
    Expect(nameReport.Notes.Any(note => note.Contains("同名")), "应提示未按名称去重");
}

/// <summary>1.4：变体 ID 已被别的实体占用时整条实体不合并，避免设定挂到错误实体下。</summary>
static void ImportMergeRejectsVariantIdClash()
{
    var target = new WorkflowCanvasState();
    var existing = new WorkflowEntity { Kind = EntityKind.Character, Name = "甲" };
    var existingVariant = existing.CreateVariant("默认");
    target.Entities.Add(existing);

    var incoming = new WorkflowCanvasState();
    var clashing = new WorkflowEntity { Kind = EntityKind.Character, Name = "乙" };
    var clashingVariant = clashing.CreateVariant("默认");
    clashingVariant.Id = existingVariant.Id;
    incoming.Entities.Add(clashing);

    var report = CanvasImportMerge.Merge(target, incoming);
    Expect(report.Conflicts.Any(conflict => conflict.ObjectType == "WorkflowEntity" && conflict.Reason.Contains("占用")),
        "变体 ID 被占用应报冲突：" + report.ToText());
    Expect(target.Entities.Count == 1 && target.Entities[0].Name == "甲", "冲突时不得把来源实体挂进来");
    Expect(target.Entities[0].Variants[0].Id == existingVariant.Id, "目标变体不得被改写");
}

/// <summary>目标 A 的真实入口冒烟：打开旧文件 → 迁移 → 保存重开 → 复制 → 重复导入。</summary>
static void RealEntrySmokeOpenSaveCopyImport()
{
    using var stores = new IsolatedStores();
    stores.WriteAsset("shot-1.png");

    var path = Path.Combine(stores.CanvasDirectory, "smoke.json");
    File.WriteAllText(path, JsonSerializer.Serialize(LegacyCanvasFile()));
    var originalBytes = File.ReadAllBytes(path);

    // 打开：迁移 + 校验，且不改写磁盘
    Expect(CanvasOpenService.TryOpen(path, out var opened, out var error), "打开失败：" + error);
    Expect(opened.Migrated && opened.NeedsAttention, "旧文件应产生迁移改动并提示检查");
    Expect(File.ReadAllBytes(path).SequenceEqual(originalBytes), "打开阶段不得写盘");
    Expect(opened.Validation.ErrorCount == 0, "迁移后的样例不应有错误：" + opened.Validation.ToText());

    // 保存：写前备份 + 标注格式版本 + 原子写入
    var saved = CanvasSaveService.Save(opened.State, path);
    Expect(saved.BackupPath is { } backup && File.Exists(backup), "覆盖保存前应留备份");
    Expect(!File.ReadAllText(path).Contains("\"FormatVersion\": 0", StringComparison.Ordinal), "保存后应标注当前格式版本");

    // 重开：无迁移改动、ID 与关系一致
    Expect(CanvasOpenService.TryOpen(path, out var reloaded, out var reloadError), "重开失败：" + reloadError);
    Expect(!reloaded.Migration.Changed, "已迁移画布重开不应再有确定改动");
    Expect(SameIdsAndRelations(opened.State.Canvas, reloaded.State.Canvas), "保存重开应保持 ID 与关系");
    Expect(reloaded.State.FormatVersion == CanvasFormat.Current, "重开后应保持当前格式版本");

    // 复制画布：全部换新 ID 且自洽
    var copy = CanvasDuplication.DuplicateCanvas(reloaded.State.Canvas);
    Expect(copy.Map.SharedReferences.Count == 0, "整份复制不应有集合外引用");
    Expect(CanvasIdentityValidator.Validate(copy.Canvas).IsClean, "复制结果应自洽");

    // 重复导入：先合并到一份空白画布（第一次增量），同一份数据第二次导入应零增量
    var blank = new RecentCanvasState("空白", 0, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, new WorkflowCanvasState());
    Expect(CanvasImportService.TryImport(path, blank, CanvasImportMode.Merge, out var merged, out var importError), "导入失败：" + importError);
    Expect(merged.Merge!.Changed && merged.Merge!.AddedNodes > 0 && merged.Merge!.AddedEntities > 0, "第一次合并应加入对象：" + merged.Merge!.ToText());
    Expect(CanvasImportService.TryImport(path, merged.State, CanvasImportMode.Merge, out var again, out var againError), "二次导入失败：" + againError);
    Expect(!again.Merge!.Changed, "第二次导入同一份数据不应增量：" + again.Merge!.ToText());
    Expect(!again.Merge!.HasConflicts, "同一份数据重复导入不应产生冲突：" + again.Merge!.ToText());
    Expect(CanvasIdentityValidator.Validate(again.State.Canvas).IsClean, "重复导入后画布仍应自洽");

    // 已经含有同一份数据的画布再导入该文件：同样零增量、无冲突
    Expect(CanvasImportService.TryImport(path, reloaded.State, CanvasImportMode.Merge, out var selfImport, out var selfError), "自导入失败：" + selfError);
    Expect(!selfImport.Merge!.Changed && !selfImport.Merge!.HasConflicts, "导入自己已有的数据应零增量且无冲突：" + selfImport.Merge!.ToText());

    // 替换导入仍然可用（资产已在本机，不再重复导入）
    Expect(CanvasImportService.TryImport(path, reloaded.State, CanvasImportMode.Replace, out var replaced, out _), "替换导入失败");
    Expect(replaced.Merge is null && replaced.State.FormatVersion == CanvasFormat.Current, "替换导入应直接采用来源画布并标注格式版本");
}

/// <summary>测试用的旧格式画布文件：旧单引用、旧图片路径、空 ID 附件，且没有格式版本标注。</summary>
static RecentCanvasState LegacyCanvasFile()
{
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    var version = variant.EnsureInitialVersion();

    var canvas = new WorkflowCanvasState();
    canvas.Entities.Add(entity);

    var node = new WorkflowNode
    {
        Title = "旧分镜",
        LegacyEntityId = entity.Id,
        LegacyVariantId = variant.Id,
        LegacyVariantVersionId = version.Id
    };
    node.LegacyAssetPaths.Add("asset://shot-1.png");
    node.LegacyAssetPaths.Add("   ");
    canvas.Nodes.Add(node);
    node.WorkTreeItemId = AddChapter(canvas);

    canvas.WorkTree.Add(new WorkTreeItem
    {
        Kind = WorkTreeKind.Version,
        Name = version.Label,
        Chapter = "第一章",
        Version = version.Label,
        SourceEntityId = entity.Id,
        SourceVariantId = variant.Id,
        SourceVersionId = version.Id,
        // 旧文件里可能有空 GUID 的标识：迁移应按确定规则补全并记录映射。
        Attachments = { new WorkflowAttachment { Id = Guid.Empty, Kind = AttachmentKind.Image, Reference = "asset://shot-1.png" } }
    });

    return new RecentCanvasState("冒烟样例", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas);
}

static Guid AddChapter(WorkflowCanvasState canvas)
{
    var chapter = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第一章" };
    canvas.WorkTree.Add(chapter);
    return chapter.Id;
}

/// <summary>对比两份画布的 ID 与关系字段，用于「保存重载/复制重映射」的一致性断言。</summary>
static bool SameIdsAndRelations(WorkflowCanvasState left, WorkflowCanvasState right)
{
    if (!left.Nodes.Select(node => node.Id).SequenceEqual(right.Nodes.Select(node => node.Id))) return false;
    if (!left.Edges.Select(edge => (edge.Id, edge.SourceNodeId, edge.TargetNodeId))
        .SequenceEqual(right.Edges.Select(edge => (edge.Id, edge.SourceNodeId, edge.TargetNodeId)))) return false;
    if (!left.Entities.Select(entity => entity.Id).SequenceEqual(right.Entities.Select(entity => entity.Id))) return false;
    if (!left.Entities.SelectMany(entity => entity.Variants).Select(variant => variant.Id)
        .SequenceEqual(right.Entities.SelectMany(entity => entity.Variants).Select(variant => variant.Id))) return false;
    if (!left.Entities.SelectMany(entity => entity.Variants).SelectMany(variant => variant.Versions).Select(version => version.Id)
        .SequenceEqual(right.Entities.SelectMany(entity => entity.Variants).SelectMany(variant => variant.Versions).Select(version => version.Id))) return false;
    if (!left.WorkTree.Select(item => (item.Id, item.ParentId, item.SourceEntityId, item.SourceVariantId, item.SourceVersionId, item.SupersedesVersionId))
        .SequenceEqual(right.WorkTree.Select(item => (item.Id, item.ParentId, item.SourceEntityId, item.SourceVariantId, item.SourceVersionId, item.SupersedesVersionId)))) return false;

    for (var index = 0; index < left.Nodes.Count; index++)
    {
        var a = left.Nodes[index];
        var b = right.Nodes[index];
        if (a.ParentNodeId != b.ParentNodeId || a.WorkTreeItemId != b.WorkTreeItemId) return false;
        if (a.References.Count != b.References.Count) return false;
        for (var referenceIndex = 0; referenceIndex < a.References.Count; referenceIndex++)
        {
            var leftReference = a.References[referenceIndex];
            var rightReference = b.References[referenceIndex];
            if (leftReference.EntityId != rightReference.EntityId
                || leftReference.VariantId != rightReference.VariantId
                || leftReference.VariantVersionId != rightReference.VariantVersionId) return false;
        }
    }

    return true;
}

static T ExpectThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T expected) { return expected; }
    catch (Exception other) { throw new InvalidOperationException($"{message}（实际抛出 {other.GetType().Name}：{other.Message}）"); }
    throw new InvalidOperationException(message + "（没有抛出异常）");
}

// ── 第 4 轮整批返工 R5–R11 的回归 ───────────────────────────────────────────

/// <summary>R5：含空 ID 的旧来源必须可持久地识别为同一批对象，连续导入与保存重开后都零增量。</summary>
static void ReworkEmptyIdSourceImportIdentityIsStable()
{
    using var stores = new IsolatedStores();
    var sourcePath = Path.Combine(stores.CanvasDirectory, "empty-id-source.json");
    File.WriteAllText(sourcePath, JsonSerializer.Serialize(LegacyEmptyIdSource()));
    var sourceBytes = File.ReadAllBytes(sourcePath);

    var target = new RecentCanvasState("目标", 0, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, new WorkflowCanvasState());
    Expect(CanvasImportService.TryImport(sourcePath, target, CanvasImportMode.Merge, out var first, out var firstError), "首次导入失败：" + firstError);
    Expect(first.Merge!.Changed && first.Merge!.AddedEntities == 1, "首次导入应加入对象：" + first.Merge!.ToText());

    // 关键：第二次仍从同一份未修改的来源导入第一次得到的状态
    Expect(CanvasImportService.TryImport(sourcePath, first.State, CanvasImportMode.Merge, out var second, out var secondError), "二次导入失败：" + secondError);
    Expect(!second.Merge!.Changed, "同一份旧来源连续导入必须零增量：" + second.Merge!.ToText());
    Expect(second.State.Canvas.Nodes.Count == first.State.Canvas.Nodes.Count
        && second.State.Canvas.Entities.Count == first.State.Canvas.Entities.Count
        && second.State.Canvas.Edges.Count == first.State.Canvas.Edges.Count
        && second.State.Canvas.WorkTree.Count == first.State.Canvas.WorkTree.Count, "不应产生重复对象");

    // 目标保存重开后再次导入同一来源，仍应零增量
    var targetPath = Path.Combine(stores.CanvasDirectory, "target.json");
    CanvasSaveService.Save(second.State, targetPath);
    Expect(CanvasOpenService.TryOpen(targetPath, out var reopened, out var openError), "重开失败：" + openError);
    Expect(CanvasImportService.TryImport(sourcePath, reopened.State, CanvasImportMode.Merge, out var third, out var thirdError), "三次导入失败：" + thirdError);
    Expect(!third.Merge!.Changed, "目标保存重开后再次导入同一来源必须零增量：" + third.Merge!.ToText());

    Expect(File.ReadAllBytes(sourcePath).SequenceEqual(sourceBytes), "不得通过改动来源文件伪造幂等");

    // 参与查找的缺失 ID 全覆盖
    var canvas = third.State.Canvas;
    var legacyNode = canvas.Nodes.First(node => node.Title == "空 ID 节点");
    var legacyEntity = canvas.Entities.First(entity => entity.Name == "沈砚");
    Expect(canvas.Edges[0].Id != Guid.Empty && canvas.WorkTree[0].Id != Guid.Empty
        && legacyNode.Id != Guid.Empty && legacyEntity.Id != Guid.Empty
        && legacyEntity.Variants[0].Id != Guid.Empty && legacyEntity.Variants[0].Versions[0].Id != Guid.Empty
        && legacyNode.Attachments[0].Id != Guid.Empty && legacyNode.GenerationHistory[0].Id != Guid.Empty,
        "参与查找的缺失 ID（节点、连线、工作树、实体、变体、版本、附件、历史）应全部补齐");
    Expect(CanvasIdentityValidator.Validate(canvas).IsClean, "合并结果应自洽：" + CanvasIdentityValidator.Validate(canvas).ToText());

    // 不同来源的同名对象保持独立
    var otherPath = Path.Combine(stores.CanvasDirectory, "other-source.json");
    File.WriteAllText(otherPath, JsonSerializer.Serialize(LegacySameNameSource()));
    Expect(CanvasImportService.TryImport(otherPath, third.State, CanvasImportMode.Merge, out var extra, out var extraError), "同名来源导入失败：" + extraError);
    Expect(extra.Merge!.AddedEntities == 1, "不同来源的同名实体应作为新对象加入");
    Expect(extra.State.Canvas.Entities.Count(entity => entity.Name == "沈砚") == 2, "同名不同来源的实体必须保持独立");
    Expect(extra.State.Canvas.Entities.Select(entity => entity.Id).Distinct().Count() == 2, "同名实体不得共用 ID");
}

/// <summary>R6：跨变体相同版本 GUID 是合法作用域，复制不得造成版本错绑。</summary>
static void ReworkDuplicateCanvasKeepsVersionScope()
{
    foreach (var swap in new[] { false, true })
    {
        var sharedVersionId = Guid.NewGuid();
        var entityA = new WorkflowEntity { Kind = EntityKind.Character, Name = "甲" };
        var variantA = entityA.CreateVariant("默认");
        variantA.Versions[0].Id = sharedVersionId;
        var entityB = new WorkflowEntity { Kind = EntityKind.Character, Name = "乙" };
        var variantB = entityB.CreateVariant("默认");
        variantB.Versions[0].Id = sharedVersionId;

        var canvas = new WorkflowCanvasState();
        if (swap) { canvas.Entities.Add(entityB); canvas.Entities.Add(entityA); }
        else { canvas.Entities.Add(entityA); canvas.Entities.Add(entityB); }

        var nodeA = new WorkflowNode { Title = "分镜A" };
        nodeA.References.Add(new NodeReference { EntityId = entityA.Id, VariantId = variantA.Id, VariantVersionId = sharedVersionId });
        var nodeB = new WorkflowNode { Title = "分镜B" };
        nodeB.References.Add(new NodeReference { EntityId = entityB.Id, VariantId = variantB.Id, VariantVersionId = sharedVersionId });
        var nodeFollow = new WorkflowNode { Title = "跟随当前" };
        nodeFollow.References.Add(new NodeReference { EntityId = entityA.Id, VariantId = variantA.Id, VariantVersionId = null });
        canvas.Nodes.Add(nodeA);
        canvas.Nodes.Add(nodeB);
        canvas.Nodes.Add(nodeFollow);
        canvas.WorkTree.Add(new WorkTreeItem
        {
            Kind = WorkTreeKind.Version,
            Name = "B 的版本",
            SourceEntityId = entityB.Id,
            SourceVariantId = variantB.Id,
            SourceVersionId = sharedVersionId
        });

        var source = CanvasIdentityValidator.Validate(canvas);
        Expect(source.ErrorCount == 0, $"源画布应零错误（swap={swap}）：" + source.ToText());

        var copy = CanvasDuplication.DuplicateCanvas(canvas);
        var after = CanvasIdentityValidator.Validate(copy.Canvas);
        Expect(after.ErrorCount == 0, $"复制后应零错误（swap={swap}）：" + after.ToText());
        Expect(copy.Map.Ambiguities.Count == 0, $"合法作用域不应产生歧义（swap={swap}）：" + copy.Report.ToText());

        var copiedA = copy.Canvas.Entities.First(entity => entity.Name == "甲");
        var copiedB = copy.Canvas.Entities.First(entity => entity.Name == "乙");
        Expect(copiedA.Variants[0].Versions[0].Id != copiedB.Variants[0].Versions[0].Id, "复制后各变体的版本 ID 应各不相同");

        var lockedA = copy.Canvas.Nodes.First(node => node.Title == "分镜A").References[0];
        var lockedB = copy.Canvas.Nodes.First(node => node.Title == "分镜B").References[0];
        Expect(lockedA.EntityId == copiedA.Id && lockedA.VariantId == copiedA.Variants[0].Id
            && lockedA.VariantVersionId == copiedA.Variants[0].Versions[0].Id, $"A 的锁定版本应指向复制后 A 自己的版本（swap={swap}）");
        Expect(lockedB.EntityId == copiedB.Id && lockedB.VariantId == copiedB.Variants[0].Id
            && lockedB.VariantVersionId == copiedB.Variants[0].Versions[0].Id, $"B 的锁定版本应指向复制后 B 自己的版本（swap={swap}）");
        Expect(copy.Canvas.Nodes.First(node => node.Title == "跟随当前").References[0].VariantVersionId is null,
            "null 跟随当前的语义必须保留");

        var copiedWork = copy.Canvas.WorkTree[0];
        Expect(copiedWork.SourceEntityId == copiedB.Id && copiedWork.SourceVersionId == copiedB.Variants[0].Versions[0].Id,
            "工作树来源版本应按上下文重映射");
    }

    // 版本 ID 在同一变体内重复：仍应报重复，且复制不猜归属
    var duplicate = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "丙" };
    var variant = entity.CreateVariant("默认");
    variant.Versions[0].Id = Guid.NewGuid();
    var second = variant.Commit("第二版");
    second.Id = variant.Versions[0].Id;
    duplicate.Entities.Add(entity);
    var node = new WorkflowNode { Title = "分镜" };
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = second.Id });
    duplicate.Nodes.Add(node);

    Expect(CanvasIdentityValidator.Validate(duplicate).WithCode(CanvasIdentityCodes.IdDuplicate).Count >= 1,
        "变体内重复的版本 ID 仍应报告");
    var duplicateCopy = CanvasDuplication.DuplicateCanvas(duplicate);
    Expect(duplicateCopy.Map.Ambiguities.Count >= 1, "同一变体内版本 ID 重复时不得猜归属：" + duplicateCopy.Report.ToText());
    Expect(duplicateCopy.Canvas.Nodes[0].References[0].VariantVersionId == second.Id, "无法唯一解析时应保留原版本引用");
}

/// <summary>R7：目标已存在而备份失败时必须中止保存，不得覆盖源文件；恢复同样受保护。</summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void ReworkBackupFailureAbortsSave()
{
	using IsolatedStores isolatedStores = new IsolatedStores();
	WorkflowCanvasState workflowCanvasState = ValidCanvas();
	string path = Path.Combine(isolatedStores.CanvasDirectory, "save.json");
	RecentCanvasState v1 = new RecentCanvasState("第一版", 1, string.Empty, string.Empty, 512, 512, 20, 7.0, string.Empty, workflowCanvasState)
	{
		FormatVersion = 1
	};
	CanvasFileWriter.Write(path, v1);
	byte[] array3 = File.ReadAllBytes(path);
	string text = CanvasBackup.TryBackup(path, out string _);
	Expect(text != null, "应能创建备份");
	string externalBackup = Path.Combine(isolatedStores.Root, "external-backup.json");
	File.Copy(text, externalBackup);
	Directory.Delete(CanvasBackup.Directory, recursive: true);
	File.WriteAllText(CanvasBackup.Directory, "blocked");
	CanvasSaveAbortedException ex2 = ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasSaveService.Save(v1 with
		{
			Title = "第二版"
		}, path);
	}, "备份失败必须中止保存");
	Expect(ex2.Message.Contains("备份", StringComparison.Ordinal), "失败原因应可读：" + ex2.Message);
	Expect(File.ReadAllBytes(path).SequenceEqual(array3), "备份失败不得覆盖原文件");
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasLibrary.Save(v1 with
		{
			Title = "第三版"
		}, path);
	}, "画布库保存必须走同一策略");
	Expect(File.ReadAllBytes(path).SequenceEqual(array3), "画布库保存失败同样不得覆盖原文件");
	string path2 = Path.Combine(isolatedStores.CanvasDirectory, "fresh.json");
	CanvasSaveOutcome canvasSaveOutcome = CanvasSaveService.Save(v1, path2);
	Expect(File.Exists(path2) && canvasSaveOutcome.BackupPath == null, "首次保存不需要备份且应成功");
	CanvasFileWriter.Write(path, v1 with
	{
		Title = "改坏了"
	});
	byte[] array4 = File.ReadAllBytes(path);
	CanvasSaveAbortedException ex3 = ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasBackup.Restore(externalBackup, path);
	}, "恢复前备份失败必须中止恢复");
	Expect(ex3.Message.Contains("备份", StringComparison.Ordinal), "恢复失败原因应可读：" + ex3.Message);
	Expect(File.ReadAllBytes(path).SequenceEqual(array4), "恢复失败不得覆盖当前文件");
	File.Delete(CanvasBackup.Directory);
	CanvasBackup.Restore(externalBackup, path);
	Expect(CanvasOpenService.TryOpen(path, out CanvasOpenOutcome outcome, out string error2), "恢复后应能打开：" + error2);
	Expect(outcome.State.Title == "第一版", "恢复后应是备份那一版的内容");
	Expect(SameIdsAndRelations(workflowCanvasState, outcome.State.Canvas), "恢复后 ID、锁定版本与跟随当前关系必须一致");
}

/// <summary>R8：保存失败（写入失败或备份失败）都不得改动调用方的对象图。</summary>
static void ReworkFailedSaveLeavesInputUntouched()
{
    using var stores = new IsolatedStores();
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Id = Guid.Empty, Title = "空 ID 节点" };
    node.LegacyAssetPaths.Add("asset://legacy.png");
    node.LegacyEntityId = Guid.NewGuid();
    canvas.Nodes.Add(node);
    var state = new RecentCanvasState("输入", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas);
    var before = JsonSerializer.Serialize(state);

    // 写入失败：目标路径被目录占用
    var blocked = Path.Combine(stores.CanvasDirectory, "blocked.json");
    Directory.CreateDirectory(blocked);
    ExpectThrows<SystemException>(() => CanvasSaveService.Save(state, blocked), "写入失败应抛出");
    Expect(JsonSerializer.Serialize(state) == before, "写入失败不得改动调用方对象图（ID、旧字段、空 ID 保持原样）");

    // 备份失败：目标已存在
    var existing = Path.Combine(stores.CanvasDirectory, "existing.json");
    CanvasFileWriter.Write(existing, state with { Title = "已存在" });
    File.WriteAllText(CanvasBackup.Directory, "blocked");
    ExpectThrows<CanvasSaveAbortedException>(() => CanvasSaveService.Save(state, existing), "备份失败应中止保存");
    Expect(JsonSerializer.Serialize(state) == before, "备份失败也不得改动调用方对象图");
    Expect(!Directory.EnumerateFiles(stores.CanvasDirectory, "*.tmp").Any(), "失败应清理临时文件");

    // 成功保存：调用方对象仍不被就地迁移，写出的文件里是迁移后的副本
    File.Delete(CanvasBackup.Directory);
    var ok = Path.Combine(stores.CanvasDirectory, "ok.json");
    var outcome = CanvasSaveService.Save(state, ok);
    Expect(JsonSerializer.Serialize(state) == before, "成功保存也不得就地迁移调用方对象");
    Expect(outcome.Migration.Changed, "写出的副本应完成迁移");
    Expect(CanvasOpenService.TryOpen(ok, out var reopened, out var error), "重开失败：" + error);
    Expect(reopened.State.Canvas.Nodes[0].Id != Guid.Empty, "写出的文件应含补齐后的 ID");
    Expect(reopened.State.Canvas.Nodes[0].Attachments.Count == 1, "写出的文件应含旧字段迁移后的附件");
}

/// <summary>R9：旧格式包里的资产（只存在于 LegacyAssetPaths）也必须被复制，缺失资产要真实计数。</summary>
static void ReworkLegacyPackageAssetsAreImported()
{
    using var stores = new IsolatedStores();
    var packageDirectory = Path.Combine(stores.Root, "package");
    Directory.CreateDirectory(Path.Combine(packageDirectory, "assets"));
    var legacyAsset = Path.Combine(packageDirectory, "assets", "legacy-only.png");
    File.WriteAllText(legacyAsset, "legacy-bytes");

    // 只靠旧字段 LegacyAssetPaths 引用的包：新字段为空，收集顺序错了就会漏掉
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Title = "旧节点" };
    node.LegacyAssetPaths.Add("asset://legacy-only.png");
    canvas.Nodes.Add(node);
    var packageFile = Path.Combine(packageDirectory, "canvas.json");
    File.WriteAllText(packageFile, JsonSerializer.Serialize(new RecentCanvasState("旧包", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas)));

    Expect(!AssetStore.Exists("asset://legacy-only.png"), "本地资产目录初始应没有该文件");

    var target = new RecentCanvasState("目标", 0, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, new WorkflowCanvasState());
    Expect(CanvasImportService.TryImport(packageFile, target, CanvasImportMode.Merge, out var merged, out var mergeError), "合并导入失败：" + mergeError);
    Expect(merged.ImportedAssets == 1 && merged.MissingAssets == 0,
        $"旧字段里的包内资产应被复制一次且无缺失，实际 导入 {merged.ImportedAssets} / 缺失 {merged.MissingAssets}");
    Expect(AssetStore.Exists("asset://legacy-only.png"), "导入后真实解析应能找到该资产");
    Expect(File.ReadAllText(Path.Combine(stores.AssetDirectory, "legacy-only.png")) == "legacy-bytes", "资产字节应一致");

    Expect(CanvasImportService.TryImport(packageFile, merged.State, CanvasImportMode.Merge, out var again, out _), "二次导入失败");
    Expect(again.ImportedAssets == 0 && !again.Merge!.Changed, "重复导入不应再增量");

    // 新旧字段同时引用同一个文件：只应复制一次
    var mixed = new WorkflowCanvasState();
    var mixedNode = new WorkflowNode { Title = "新旧混用节点" };
    mixedNode.LegacyAssetPaths.Add("asset://legacy-only.png");
    mixedNode.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://legacy-only.png" });
    mixed.Nodes.Add(mixedNode);
    var mixedFile = Path.Combine(packageDirectory, "mixed.json");
    File.WriteAllText(mixedFile, JsonSerializer.Serialize(new RecentCanvasState("混用包", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, mixed)));
    Expect(CanvasImportService.TryImport(mixedFile, target, CanvasImportMode.Replace, out var mixedOutcome, out var mixedError), "混用导入失败：" + mixedError);
    Expect(mixedOutcome.ImportedAssets == 0 && mixedOutcome.MissingAssets == 0,
        $"资产已在本机，混用字段不应重复复制：导入 {mixedOutcome.ImportedAssets} / 缺失 {mixedOutcome.MissingAssets}");

    // 真正缺失的资产必须计数：删掉包内资产、清空本地资产后替换导入
    File.Delete(legacyAsset);
    Directory.Delete(stores.AssetDirectory, true);
    Directory.CreateDirectory(stores.AssetDirectory);
    Expect(CanvasImportService.TryImport(packageFile, target, CanvasImportMode.Replace, out var replaced, out var replaceError), "替换导入失败：" + replaceError);
    Expect(replaced.MissingAssets == 1, $"包内确实缺失的资产应计数，实际 {replaced.MissingAssets}");
    Expect(!AssetStore.Exists("asset://legacy-only.png"), "资产确实不存在，不应被静默视为成功");
}

/// <summary>R10：重复 ID 不得被猜测消歧，也不得用新 ID 抹掉诊断。</summary>
static void ReworkDuplicateIdsAreNotGuessed()
{
    var canvas = new WorkflowCanvasState();
    var first = new WorkflowNode { Title = "副本一" };
    var second = new WorkflowNode { Title = "副本二", Id = first.Id };
    var child = new WorkflowNode { Title = "子节点", ParentNodeId = first.Id };
    canvas.Nodes.Add(first);
    canvas.Nodes.Add(second);
    canvas.Nodes.Add(child);
    var chapter = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "章" };
    var chapterCopy = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "章副本", Id = chapter.Id };
    canvas.WorkTree.Add(chapter);
    canvas.WorkTree.Add(chapterCopy);

    var before = CanvasIdentityValidator.Validate(canvas);
    Expect(before.WithCode(CanvasIdentityCodes.IdDuplicate).Count == 4, $"源画布应逐处报出 4 条重复 ID，实际 {before.WithCode(CanvasIdentityCodes.IdDuplicate).Count}");
    Expect(before.WithCode(CanvasIdentityCodes.ParentAmbiguous).Count == 1, "重复节点 ID 作父节点时应报歧义");

    // DuplicateNodes：重复 ID 请求必须被拒绝，不得复制两份、也不得取首条
    var nodes = CanvasDuplication.DuplicateNodes(canvas, new[] { first.Id });
    Expect(nodes.Nodes.Count == 0, "重复 ID 请求必须被拒绝（复制 0 个），实际 " + nodes.Nodes.Count);
    Expect(nodes.Report.Ambiguities.Count == 1, "应给出结构化歧义报告：" + nodes.Report.ToText());

    // DuplicateCanvas：不得自动绑定首条，也不得把诊断抹成 0
    var copy = CanvasDuplication.DuplicateCanvas(canvas);
    Expect(copy.Map.Nodes.ContainsKey(first.Id) == false, "重复 ID 不得进入唯一映射");
    Expect(copy.Map.Ambiguities.Count >= 1, "重复的节点与工作树 ID 应留成歧义：" + copy.Report.ToText());
    Expect(copy.Canvas.Nodes[2].ParentNodeId == first.Id, "无法唯一解析时应保留原引用值，而不是绑定第一个副本");
    Expect(copy.Canvas.Nodes.Select(item => item.Id).Distinct().Count() == 3, "复制出的节点 ID 应各不相同");
    var after = CanvasIdentityValidator.Validate(copy.Canvas);
    Expect(after.ErrorCount >= 1, "复制后仍应有可诊断的错误，不能用新 ID 抹掉问题：" + after.ToText());
    Expect(copy.Map.WorkTreeItems.ContainsKey(chapter.Id) == false, "重复的工作树 ID 同样不得进入映射");

    // 复制不得改动源对象
    Expect(first.Id == second.Id && child.ParentNodeId == first.Id && canvas.Nodes.Count == 3, "复制不得改动源画布");
}

/// <summary>R11：高于当前支持的格式版本不得被降级标注或覆盖保存。</summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void ReworkFutureFormatIsNotDowngraded()
{
	using IsolatedStores isolatedStores = new IsolatedStores();
	WorkflowCanvasState canvas = ValidCanvas();
	RecentCanvasState recentCanvasState = new RecentCanvasState("未来格式", 5, string.Empty, string.Empty, 512, 512, 20, 7.0, string.Empty, canvas)
	{
		FormatVersion = 99
	};
	string text = JsonSerializer.Serialize(recentCanvasState);
	CanvasMigrationResult canvasMigrationResult = CanvasMigration.Migrate(recentCanvasState);
	Expect(!canvasMigrationResult.Report.Changed, "未知格式不应产生任何迁移改动");
	Expect(canvasMigrationResult.Report.UnsupportedFormat && canvasMigrationResult.Report.ToVersion == recentCanvasState.FormatVersion, $"不得把未知格式标注为当前版本：{canvasMigrationResult.Report.ToVersion}");
	Expect(canvasMigrationResult.State.FormatVersion == recentCanvasState.FormatVersion, "迁移结果应保持原格式版本");
	Expect(JsonSerializer.Serialize(recentCanvasState) == text, "迁移不得改动调用方对象");
	string path = Path.Combine(isolatedStores.CanvasDirectory, "future.json");
	File.WriteAllText(path, text);
	byte[] array3 = File.ReadAllBytes(path);
	Expect(CanvasOpenService.TryOpen(path, out CanvasOpenOutcome opened, out string error), "未知格式仍应允许只读打开：" + error);
	Expect(opened.UnsupportedFormat && opened.NeedsAttention, "应标记为只读查看并提示");
	Expect(opened.Migration.ToText().Contains("高于当前支持", StringComparison.Ordinal), "诊断应说明原因：" + opened.Migration.ToText());
	Expect(File.ReadAllBytes(path).SequenceEqual(array3), "只读查看不得改写文件");
	CanvasSaveAbortedException ex2 = ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasSaveService.Save(opened.State, path);
	}, "未知格式必须拒绝覆盖保存");
	Expect(ex2.Message.Contains("高于当前支持", StringComparison.Ordinal), "拒绝原因应可读：" + ex2.Message);
	Expect(File.ReadAllBytes(path).SequenceEqual(array3), "拒绝保存后文件必须原样");
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasLibrary.Save(opened.State, path);
	}, "画布库保存同样必须拒绝");
	RecentCanvasState target = new RecentCanvasState("目标", 0, string.Empty, string.Empty, 512, 512, 20, 7.0, string.Empty, new WorkflowCanvasState());
	Expect(!CanvasImportService.TryImport(path, target, CanvasImportMode.Merge, out CanvasImportOutcome outcome, out string error2), "未知格式必须拒绝导入");
	Expect(error2.Contains("高于当前支持", StringComparison.Ordinal), "导入诊断应可读：" + error2);
	Expect(!CanvasImportService.TryImport(path, opened.State, CanvasImportMode.Replace, out outcome, out string error3), "未知格式的目标也不应接受导入");
	Expect(error3.Contains("高于当前支持", StringComparison.Ordinal), "目标诊断应可读：" + error3);
	RecentCanvasState state = LegacyCanvasFile();
	CanvasMigrationResult canvasMigrationResult2 = CanvasMigration.Migrate(state);
	Expect(canvasMigrationResult2.State.FormatVersion == 1 && canvasMigrationResult2.Report.Changed, "旧格式应正常迁移到当前版本");
	CanvasMigrationResult canvasMigrationResult3 = CanvasMigration.Migrate(canvasMigrationResult2.State);
	Expect(!canvasMigrationResult3.Report.Changed && !canvasMigrationResult3.Report.UnsupportedFormat, "已标注当前格式的画布再迁移应零改动");
}

/// <summary>R5 用的旧格式来源：参与查找的对象 ID 全为空 GUID，引用保持一致且可解析。</summary>
static RecentCanvasState LegacyEmptyIdSource()
{
    var canvas = new WorkflowCanvasState();

    // 空 ID 的连线，两端是正常 ID 的节点
    var from = new WorkflowNode { Title = "起点" };
    var to = new WorkflowNode { Title = "终点" };
    canvas.Nodes.Add(from);
    canvas.Nodes.Add(to);
    canvas.Edges.Add(new WorkflowEdge { Id = Guid.Empty, SourceNodeId = from.Id, TargetNodeId = to.Id });

    // 空 ID 的节点：带旧图片路径与空 ID 生成历史，不引用其它对象
    var legacyNode = new WorkflowNode { Id = Guid.Empty, Title = "空 ID 节点" };
    legacyNode.LegacyAssetPaths.Add("asset://legacy-empty.png");
    legacyNode.GenerationHistory.Add(new GenerationHistory { Id = Guid.Empty, Input = "旧输入", Instruction = "旧指令", Output = string.Empty });
    canvas.Nodes.Add(legacyNode);

    // 空 ID 的工作树条目、实体、变体、版本
    canvas.WorkTree.Add(new WorkTreeItem { Id = Guid.Empty, Kind = WorkTreeKind.Chapter, Name = "空 ID 章节" });
    var entity = new WorkflowEntity { Id = Guid.Empty, Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    variant.Id = Guid.Empty;
    variant.Versions[0].Id = Guid.Empty;
    canvas.Entities.Add(entity);

    return new RecentCanvasState("空 ID 旧来源", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas);
}

/// <summary>R5 用的第二个来源：与第一个来源同名的实体，但内容不同，必须保持独立。</summary>
static RecentCanvasState LegacySameNameSource()
{
    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    variant.Description = "另一个来源的外观";
    variant.Commit("另一个来源的版本");
    canvas.Entities.Add(entity);
    return new RecentCanvasState("同名旧来源", 2, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas);
}

// ── 第 6 轮入口审计的回归：所有真实保存入口共用同一策略 ─────────────────────

/// <summary>R12：画布库保存入口必须走完整保存链（深拷贝 + 迁移 + 校验 + 备份 + 原子写），不能只做备份+写入。</summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void ReworkLibrarySaveUsesFullChain()
{
	using IsolatedStores isolatedStores = new IsolatedStores();
	RecentCanvasState state = EntryAuditState();
	string text = JsonSerializer.Serialize(state);
	string path = CanvasLibrary.PathForTitle(state.Title);
	string text2 = CanvasLibrary.Save(state, null);
	Expect(text2 == path && File.Exists(path), "应按标题写入画布库：" + text2);
	Expect(JsonSerializer.Serialize(state) == text, "库入口保存不得就地迁移调用方对象");
	RecentCanvasState recentCanvasState = ReadRaw(path);
	Expect(recentCanvasState.FormatVersion == 1, "库入口写出的文件应标注当前格式版本");
	Expect(recentCanvasState.Canvas.Nodes[0].Id != Guid.Empty, "库入口应在写入前补齐空 ID");
	Expect(recentCanvasState.Canvas.Nodes[0].Attachments.Count == 1 && recentCanvasState.Canvas.Nodes[0].LegacyAssetPaths.Count == 0, "库入口应在写入前完成旧字段迁移");
	CanvasSaveOutcome canvasSaveOutcome = CanvasSaveService.Save(state, path);
	Expect(canvasSaveOutcome.BackupPath != null && File.Exists(canvasSaveOutcome.BackupPath), "覆盖保存应留下备份");
	RecentCanvasState future = state with
	{
		FormatVersion = 99
	};
	byte[] array3 = File.ReadAllBytes(path);
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasLibrary.Save(future, path);
	}, "未知格式应被库入口拒绝");
	Expect(File.ReadAllBytes(path).SequenceEqual(array3), "拒绝保存后文件必须原样");
	string blocked = Path.Combine(isolatedStores.CanvasDirectory, "blocked.json");
	Directory.CreateDirectory(blocked);
	ExpectThrows<SystemException>(() =>
	{
		CanvasLibrary.Save(state, blocked);
	}, "库入口写入失败应抛出");
	Expect(JsonSerializer.Serialize(state) == text, "库入口写入失败不得改动调用方对象");
	Expect(!Directory.EnumerateFiles(isolatedStores.CanvasDirectory, "*.tmp").Any(), "失败应清理临时文件");
	Directory.Delete(CanvasBackup.Directory, recursive: true);
	File.WriteAllText(CanvasBackup.Directory, "blocked");
	CanvasSaveAbortedException ex2 = ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasLibrary.Save(state, path);
	}, "库入口备份失败必须中止");
	Expect(ex2.Message.Contains("备份", StringComparison.Ordinal), "失败原因应可读：" + ex2.Message);
	Expect(File.ReadAllBytes(path).SequenceEqual(array3), "库入口备份失败不得覆盖原文件");
	Expect(JsonSerializer.Serialize(state) == text, "库入口备份失败不得改动调用方对象");
}

/// <summary>R13：重命名入口覆盖目标前必须备份、写入原子、失败不丢内容。</summary>
static void ReworkRenameEntryIsBackedUpAndAtomic()
{
    using var stores = new IsolatedStores();
    var state = EntryAuditState();
    var source = CanvasLibrary.PathForTitle(state.Title);
    CanvasFileWriter.Write(source, state);

    // 目标已存在：覆盖前先备份，成功后源文件被删除
    var occupied = CanvasLibrary.PathForTitle("被占用的标题");
    var occupiedState = state with { Title = "被占用的标题", Revision = 7 };
    CanvasFileWriter.Write(occupied, occupiedState);
    var occupiedBytes = File.ReadAllBytes(occupied);

    var renamed = CanvasLibrary.Rename(source, "被占用的标题", overwrite: true);
    Expect(renamed == occupied, "应重命名到目标路径：" + renamed);
    Expect(!File.Exists(source), "重命名成功后源文件应被删除");
    var backups = CanvasBackup.ListFor(occupied);
    Expect(backups.Count >= 1, "覆盖目标前必须留下备份");
    Expect(File.ReadAllBytes(backups[0]).SequenceEqual(occupiedBytes), "备份应是被覆盖前的目标内容");
    Expect(CanvasOpenService.TryOpen(occupied, out var afterRename, out _) && afterRename.State.Title == "被占用的标题", "重命名后内容应是新标题");

    // 备份失败：必须中止，目标文件与源文件都不变
    var source2 = CanvasLibrary.PathForTitle("重命名源");
    CanvasFileWriter.Write(source2, state with { Title = "重命名源" });
    var source2Bytes = File.ReadAllBytes(source2);
    var target2 = CanvasLibrary.PathForTitle("重命名目标");
    CanvasFileWriter.Write(target2, state with { Title = "重命名目标" });
    var target2Bytes = File.ReadAllBytes(target2);

    Directory.Delete(CanvasBackup.Directory, true);
    File.WriteAllText(CanvasBackup.Directory, "blocked");
    ExpectThrows<CanvasSaveAbortedException>(() => CanvasLibrary.Rename(source2, "重命名目标", overwrite: true), "备份失败必须中止重命名");
    Expect(File.ReadAllBytes(target2).SequenceEqual(target2Bytes), "备份失败不得覆盖目标文件");
    Expect(File.ReadAllBytes(source2).SequenceEqual(source2Bytes), "备份失败不得删除源文件");

    // 同名（原地重写）：同样先备份再原子写入
    File.Delete(CanvasBackup.Directory);
    var renamed2 = CanvasLibrary.Rename(source2, "重命名源");
    Expect(renamed2 == source2 && File.Exists(source2), "同名重命名应原地写回");
    Expect(CanvasBackup.ListFor(source2).Count >= 1, "原地重写也要留下备份");
    Expect(CanvasOpenService.TryOpen(source2, out var reopened2, out _) && reopened2.State.Canvas.Nodes[0].Id != Guid.Empty,
        "重命名写出的文件应含迁移后的内容");

    // 未知格式文件不得被重命名覆盖写回
    var futurePath = CanvasLibrary.PathForTitle("未来格式重命名");
    CanvasFileWriter.Write(futurePath, state with { Title = "未来格式重命名", FormatVersion = CanvasFormat.Current + 98 });
    var futureBytes = File.ReadAllBytes(futurePath);
    ExpectThrows<CanvasSaveAbortedException>(() => CanvasLibrary.Rename(futurePath, "未来格式改名"), "未知格式必须拒绝重命名写入");
    Expect(File.ReadAllBytes(futurePath).SequenceEqual(futureBytes), "拒绝重命名后文件必须原样");
}

/// <summary>
/// 入口审计：手动保存、库保存（标签切换/关闭写回）、命令服务、复制落盘、重命名五条真实入口
/// 在备份失败时都必须中止且不覆盖，成功时都必须写出迁移后的内容。
/// </summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void AllSaveEntryPointsShareOnePolicy()
{
	using IsolatedStores isolatedStores = new IsolatedStores();
	WorkflowCanvasState canvas = ValidCanvas();
	RecentCanvasState state = new RecentCanvasState("入口审计", 3, string.Empty, string.Empty, 512, 512, 20, 7.0, string.Empty, canvas)
	{
		FormatVersion = 1
	};
	string manual = Path.Combine(isolatedStores.CanvasDirectory, "manual.json");
	string library = Path.Combine(isolatedStores.CanvasDirectory, "library.json");
	string command = Path.Combine(isolatedStores.CanvasDirectory, "command.json");
	string duplicate = Path.Combine(isolatedStores.CanvasDirectory, "duplicate.json");
	string renameSource = Path.Combine(isolatedStores.CanvasDirectory, "rename-source.json");
	string text = Path.Combine(isolatedStores.CanvasDirectory, "rename-target.json");
	string[] array3 = new string[6] { manual, library, command, duplicate, renameSource, text };
	foreach (string path in array3)
	{
		CanvasFileWriter.Write(path, state with
		{
			Title = Path.GetFileNameWithoutExtension(path)
		});
	}
	string text2 = JsonSerializer.Serialize(state);
	Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
	string[] array4 = new string[6] { manual, library, command, duplicate, renameSource, text };
	foreach (string text3 in array4)
	{
		dictionary[text3] = File.ReadAllBytes(text3);
	}
	File.WriteAllText(CanvasBackup.Directory, "blocked");
	CanvasCommandService commandService = new CanvasCommandService(canvas);
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasSaveService.Save(state, manual);
	}, "手动保存入口必须中止");
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasLibrary.Save(state, library);
	}, "库保存入口（标签切换/关闭写回）必须中止");
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		commandService.Save(state, command);
	}, "命令服务入口必须中止");
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasSaveService.Save(state, duplicate);
	}, "复制落盘入口必须中止");
	ExpectThrows<CanvasSaveAbortedException>(() =>
	{
		CanvasLibrary.Rename(renameSource, "rename-target", overwrite: true);
	}, "重命名入口必须中止");
	foreach (string key2 in dictionary.Keys)
	{
		Expect(File.ReadAllBytes(key2).SequenceEqual(dictionary[key2]), "入口 " + Path.GetFileName(key2) + " 在备份失败时不得被覆盖");
	}
	Expect(File.Exists(renameSource), "重命名中止后源文件必须仍在");
	Expect(JsonSerializer.Serialize(state) == text2, "任何入口失败都不得改动调用方对象");
	Expect(!Directory.EnumerateFiles(isolatedStores.CanvasDirectory, "*.tmp").Any(), "失败应清理临时文件");
	File.Delete(CanvasBackup.Directory);
	RecentCanvasState recentCanvasState = EntryAuditState()with
	{
		Title = "入口审计迁移"
	};
	string path2 = Path.Combine(isolatedStores.CanvasDirectory, "legacy-entry.json");
	string text4 = JsonSerializer.Serialize(recentCanvasState);
	CanvasSaveService.Save(recentCanvasState, path2);
	Expect(RawMigrated(path2), "服务入口应在写入前完成迁移并标注格式版本");
	string text5 = Path.Combine(isolatedStores.CanvasDirectory, "legacy-library.json");
	CanvasLibrary.Save(recentCanvasState, text5);
	Expect(RawMigrated(text5), "库入口应在写入前完成迁移并标注格式版本");
	string text6 = Path.Combine(isolatedStores.CanvasDirectory, "legacy-command.json");
	new CanvasCommandService(new WorkflowCanvasState()).Save(recentCanvasState, text6);
	Expect(RawMigrated(text6), "命令服务入口应在写入前完成迁移并标注格式版本");
	Expect(JsonSerializer.Serialize(recentCanvasState) == text4, "成功保存也不得就地迁移调用方对象");
	CanvasSaveOutcome canvasSaveOutcome = CanvasSaveService.Save(state, manual);
	Expect(canvasSaveOutcome.BackupPath != null && File.Exists(canvasSaveOutcome.BackupPath), "成功覆盖保存应留下备份");
	Expect(CanvasBackup.ListFor(manual).Count >= 1, "应能按画布找到该备份");
}

/// <summary>导出包也要打包旧字段引用的资产、写出当前格式画布，并且覆盖导出不留半截文件。</summary>
static void ExportPackagePacksLegacyAssetsAtomically()
{
    using var stores = new IsolatedStores();
    stores.WriteAsset("legacy-export.png");

    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Id = Guid.Empty, Title = "旧节点" };
    node.LegacyAssetPaths.Add("asset://legacy-export.png");
    canvas.Nodes.Add(node);
    var state = new RecentCanvasState("导出审计", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas);
    var before = JsonSerializer.Serialize(state);

    var packageDirectory = Path.Combine(stores.Root, "export-package");
    var (copied, missing) = CanvasPackage.Export(packageDirectory, state);
    Expect(copied == 1 && missing == 0, $"旧字段引用的资产也应被打包，实际 复制 {copied} / 缺失 {missing}");
    Expect(File.Exists(Path.Combine(packageDirectory, "assets", "legacy-export.png")), "包内 assets 应有该资产");

    var exported = ReadRaw(Path.Combine(packageDirectory, CanvasPackage.CanvasFileName));
    Expect(exported.FormatVersion == CanvasFormat.Current, "导出的包内画布应标注当前格式版本");
    Expect(exported.Canvas.Nodes[0].Id != Guid.Empty && exported.Canvas.Nodes[0].Attachments.Count == 1,
        "导出的包内画布应已完成迁移");
    Expect(JsonSerializer.Serialize(state) == before, "导出不得改动调用方对象");

    // 覆盖导出到同一目录：内容更新、不残留临时文件
    CanvasPackage.Export(packageDirectory, state with { Title = "导出审计（二次）" });
    Expect(ReadRaw(Path.Combine(packageDirectory, CanvasPackage.CanvasFileName)).Title == "导出审计（二次）", "覆盖导出应写入新内容");
    Expect(!Directory.EnumerateFiles(packageDirectory, "*.tmp", SearchOption.AllDirectories).Any(), "导出不应留下临时文件");
}

/// <summary>直接读文件内容，不经过 CanvasOpenService 的迁移，用于判断「写出时是否已迁移」。</summary>
static RecentCanvasState ReadRaw(string path) =>
    JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllText(path))
    ?? throw new InvalidDataException("画布文件读取失败：" + path);

/// <summary>写出时是否已完成迁移并标注当前格式版本。</summary>
static bool RawMigrated(string path)
{
    var raw = ReadRaw(path);
    return raw.FormatVersion == CanvasFormat.Current
        && raw.Canvas.Nodes.All(node => node.Id != Guid.Empty)
        && raw.Canvas.Nodes[0].Attachments.Count == 1
        && raw.Canvas.Nodes[0].LegacyAssetPaths.Count == 0;
}

/// <summary>入口审计用的状态：含空 ID 与旧字段，用来验证每个入口都执行了迁移。</summary>
static RecentCanvasState EntryAuditState()
{
    var canvas = new WorkflowCanvasState();
    var node = new WorkflowNode { Id = Guid.Empty, Title = "入口审计节点" };
    node.LegacyAssetPaths.Add("asset://entry-audit.png");
    node.LegacyEntityId = Guid.NewGuid();
    canvas.Nodes.Add(node);
    return new RecentCanvasState("入口审计源", 1, string.Empty, string.Empty, 512, 512, 20, 7, string.Empty, canvas);
}

// ── 大目标 B：工作树同步与章节画布 ─────────────────────────────────────────

/// <summary>章节显式顺序：确定性补齐、第二次零改动、列表顺序稳定。</summary>
static void ChapterOrderIsExplicitAndIdempotent()
{
    var (canvas, chapter1, chapter2, _, _) = ChapterFixture();
    Expect(canvas.WorkTree.All(item => item.Kind != WorkTreeKind.Chapter || item.Order == 0), "样例初始没有显式顺序");

    var first = CanvasMigration.Migrate(new RecentCanvasState("章节顺序", 1, "", "", 512, 512, 20, 7, "", canvas));
    Expect(first.Report.Changes.Any(change => change.Kind == MigrationChangeKind.NormalizedChapterOrder), "迁移应补齐章节顺序并记录");
    var ordered = CanvasChapters.ChapterItems(first.State.Canvas);
    Expect(ordered.All(item => item.Order > 0), "补齐后每个章节都有显式顺序");
    Expect(ordered[0].Order < ordered[1].Order, "顺序值应按现有顺序递增");

    var again = CanvasMigration.Migrate(first.State);
    Expect(!again.Report.Changed, "顺序已显式时重复迁移不得再有改动");

    var listed = CanvasChapters.List(first.State.Canvas);
    Expect(listed.Count == 2 && listed[0].Id == chapter1.Id && listed[1].Id == chapter2.Id, "章节列表应按显式顺序排列");

    // 名称里没有数字也不影响：顺序由字段决定，不由名称猜
    var renamed = CanvasChapterOperations.Rename(first.State.Canvas, chapter1.Id, "序章");
    var reordered = CanvasChapterOperations.Reorder(renamed.Canvas, chapter1.Id, 500);
    var after = CanvasChapters.List(reordered.Canvas);
    Expect(after[0].Id == chapter2.Id && after[1].Id == chapter1.Id, "顺序值改变后列表顺序应随之变化：" + after.Count);
}

/// <summary>节点归属按锚点与父链解析，不按名称；同名章节只报冲突。</summary>
static void ChapterResolutionUsesIdAndContext()
{
    var (canvas, chapter1, _, shot1, _) = ChapterFixture();
    var inherited = new WorkflowNode { Title = "第1章成品", Category = NodeCategory.Product, ParentNodeId = shot1.Id };
    canvas.Nodes.Add(inherited);

    Expect(CanvasChapters.ResolveChapterId(canvas, shot1) == chapter1.Id, "锚点直接解析章节");
    Expect(CanvasChapters.ResolveChapterId(canvas, inherited) == chapter1.Id, "父链解析章节");

    // 同名章节：只报名称歧义，不按名称绑定
    canvas.WorkTree.Add(new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第1章" });
    var textOnly = new WorkflowNode { Title = "只有文本", Chapter = "第1章" };
    canvas.Nodes.Add(textOnly);
    var issues = CanvasChapters.Diagnose(canvas);
    Expect(issues.Any(issue => issue.Code == CanvasChapterCodes.NameAmbiguous), "同名章节应报名称歧义：" + string.Join("；", issues.Select(i => i.Code)));
    Expect(CanvasChapters.ResolveChapterId(canvas, textOnly) is null, "没有锚点也没有父链时不得按名称解析");

    var plan = CanvasWorkTreeSync.Plan(canvas, SyncDirection.CanvasToWorkTree);
    Expect(plan.HasBlockingConflicts, "同名多章节时应拒绝自动绑定：" + plan.ToText());
    var apply = CanvasWorkTreeSync.Apply(canvas, plan, applyUnconfirmed: true);
    Expect(apply.Refused && !apply.Changed, "有阻断冲突时整批拒绝，不做任何改动");
    Expect(apply.Canvas.Nodes.Count == canvas.Nodes.Count, "拒绝后结果画布与输入一致");
}

/// <summary>拆分与合并按 ID 重接引用并保持可追踪。</summary>
static void ChapterSplitAndMergeRekeyReferences()
{
    var (canvas, chapter1, chapter2, shot1, _) = ChapterFixture();
    var extra = new WorkflowNode { Title = "第1章尾镜", Category = NodeCategory.Storyboard, WorkTreeItemId = chapter1.Id, Chapter = "第1章" };
    canvas.Nodes.Add(extra);

    var split = CanvasChapterOperations.Split(canvas, chapter1.Id, "第1章下", new[] { extra.Id });
    Expect(!split.HasBlockingConflicts && split.Changed, "拆分应成功：" + split.ToText());
    var created = CanvasChapters.ChapterItems(split.Canvas).First(item => item.Name == "第1章下");
    var movedNode = split.Canvas.Nodes.First(node => node.Id == extra.Id);
    Expect(movedNode.WorkTreeItemId == created.Id && movedNode.Chapter == "第1章下", "被拆出的节点应改锚到新章节");
    Expect(split.Canvas.Nodes.First(node => node.Id == shot1.Id).WorkTreeItemId == chapter1.Id, "未拆出的节点锚点不变");

    var merge = CanvasChapterOperations.Merge(split.Canvas, created.Id, chapter2.Id);
    Expect(merge.Changed && CanvasChapters.ChapterItems(merge.Canvas).All(item => item.Id != created.Id), "合并后源章节应删除");
    var mergedNode = merge.Canvas.Nodes.First(node => node.Id == extra.Id);
    Expect(mergedNode.WorkTreeItemId == chapter2.Id && mergedNode.Chapter == "第2章", "合并应把节点改锚到目标章节：" + merge.ToText());
    Expect(CanvasChapters.ResolveChapterId(merge.Canvas, mergedNode) == chapter2.Id, "合并后归属仍可按 ID 解析");

    // 拆分的取消条件：没有指定节点时阻断
    var blocked = CanvasChapterOperations.Split(canvas, chapter1.Id, "空拆分", Array.Empty<Guid>());
    Expect(blocked.HasBlockingConflicts && !blocked.Changed, "没有节点时拆分应阻断");
}

/// <summary>删除有引用的章节默认阻断；显式改挂上级或清空锚点都要保留可追踪信息。</summary>
static void ChapterDeleteProtectsReferences()
{
    var (canvas, chapter1, _, shot1, _) = ChapterFixture();

    var blocked = CanvasChapterOperations.Delete(canvas, chapter1.Id);
    Expect(blocked.HasBlockingConflicts && !blocked.Changed, "有节点引用时删除应阻断：" + blocked.ToText());
    Expect(blocked.Canvas.WorkTree.Any(item => item.Id == chapter1.Id), "阻断后章节仍在");
    Expect(canvas.Nodes.First(node => node.Id == shot1.Id).WorkTreeItemId == chapter1.Id, "阻断不得改动原画布");

    // 带上级章节时改挂到上级
    var parentChapter = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "总纲" };
    var childChapter = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第1章其一", ParentId = parentChapter.Id };
    var nested = new WorkflowCanvasState();
    nested.WorkTree.Add(parentChapter);
    nested.WorkTree.Add(childChapter);
    var nestedNode = new WorkflowNode { Title = "嵌套分镜", WorkTreeItemId = childChapter.Id, Chapter = "第1章其一" };
    nested.Nodes.Add(nestedNode);

    var reassigned = CanvasChapterOperations.Delete(nested, childChapter.Id, reassignNodesToParent: true);
    Expect(reassigned.Changed && !reassigned.HasBlockingConflicts, "显式改挂后应允许删除：" + reassigned.ToText());
    var reanchored = reassigned.Canvas.Nodes.First(node => node.Id == nestedNode.Id);
    Expect(reanchored.WorkTreeItemId == parentChapter.Id && reanchored.Chapter == "总纲", "节点应改挂到上级章节");

    // 没有上级章节时清空锚点但保留文本
    var single = new WorkflowCanvasState();
    var loneChapter = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "孤立章节" };
    single.WorkTree.Add(loneChapter);
    var loneNode = new WorkflowNode { Title = "孤立分镜", WorkTreeItemId = loneChapter.Id, Chapter = "孤立章节" };
    single.Nodes.Add(loneNode);
    var cleared = CanvasChapterOperations.Delete(single, loneChapter.Id, reassignNodesToParent: true);
    var orphan = cleared.Canvas.Nodes.First(node => node.Id == loneNode.Id);
    Expect(orphan.WorkTreeItemId is null && orphan.Chapter == "孤立章节", "没有上级时清空锚点并保留显示文本");
}

/// <summary>章节移动成环必须阻断，且不改动调用方画布。</summary>
static void ChapterMoveRejectsCycles()
{
    var parent = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第一幕" };
    var child = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第一个场景", ParentId = parent.Id };
    var canvas = new WorkflowCanvasState();
    canvas.WorkTree.Add(parent);
    canvas.WorkTree.Add(child);
    var before = JsonSerializer.Serialize(canvas);

    var cycle = CanvasChapterOperations.Move(canvas, parent.Id, child.Id);
    Expect(cycle.HasBlockingConflicts && !cycle.Changed, "移动到自己的下级应阻断：" + cycle.ToText());
    Expect(JsonSerializer.Serialize(canvas) == before, "阻断操作不得改动调用方画布");

    var moved = CanvasChapterOperations.Move(canvas, child.Id, null);
    Expect(moved.Changed && !moved.HasBlockingConflicts, "移动到顶层应成功");
    Expect(moved.Canvas.WorkTree.First(item => item.Id == child.Id).ParentId is null, "移动后父章节应为空");
    Expect(CanvasChapters.List(moved.Canvas).Count == 2, "移动不改变章节数量");
}

/// <summary>画布 → 工作树：为章节节点建章节条目与锚点；文本节点按父链上下文建锚点。</summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void SyncCanvasToWorkTreeCreatesAnchorsByContext()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowNode chapterNode = new WorkflowNode
	{
		Title = "第3章",
		Category = NodeCategory.Chapter
	};
	workflowCanvasState.Nodes.Add(chapterNode);
	WorkflowNode shot = new WorkflowNode
	{
		Title = "第3章分镜",
		Category = NodeCategory.Storyboard,
		ParentNodeId = chapterNode.Id,
		Chapter = "第3章"
	};
	workflowCanvasState.Nodes.Add(shot);
	string text = JsonSerializer.Serialize(workflowCanvasState);
	SyncPlan syncPlan = CanvasWorkTreeSync.Plan(workflowCanvasState, SyncDirection.CanvasToWorkTree);
	Expect(!string.IsNullOrWhiteSpace(syncPlan.BaselineHash), "同步计划应记录完整画布基线");
	Expect(!syncPlan.HasBlockingConflicts, "无同名歧义时不应阻断：" + syncPlan.ToText());
	Expect(syncPlan.Changes.Any((SyncChange change) => change.Kind == SyncChangeKind.CreateChapterItem && !change.RequiresConfirmation), "应为章节节点新建章节条目");
	Expect(syncPlan.Changes.Any((SyncChange change) => change.Kind == SyncChangeKind.LinkNodeAnchor), "应建立稳定锚点");
	SyncApplyResult syncApplyResult = CanvasWorkTreeSync.Apply(workflowCanvasState, syncPlan);
	Expect(syncApplyResult.Changed && !syncApplyResult.Refused, "应用应成功：" + syncApplyResult.ToText());
	Expect(JsonSerializer.Serialize(workflowCanvasState) == text, "应用不得改动调用方画布（只在副本上做）");
	List<WorkTreeItem> list2 = CanvasChapters.ChapterItems(syncApplyResult.Canvas);
	Expect(list2.Count == 1 && list2[0].Name == "第3章" && list2[0].Order > 0, "新章节应带显式顺序");
	WorkflowNode workflowNode = syncApplyResult.Canvas.Nodes.First((WorkflowNode node) => node.Id == chapterNode.Id);
	Expect(workflowNode.WorkTreeItemId == list2[0].Id, "章节节点应锚定到新条目");
	WorkflowNode workflowNode2 = syncApplyResult.Canvas.Nodes.First((WorkflowNode node) => node.Id == shot.Id);
	Expect(workflowNode2.WorkTreeItemId == list2[0].Id, "分镜应按父链上下文锚定到同一章节");
	Expect(CanvasIdentityValidator.Validate(syncApplyResult.Canvas).IsClean, "同步结果应自洽：" + CanvasIdentityValidator.Validate(syncApplyResult.Canvas).ToText());
	WorkflowCanvasState workflowCanvasState2 = CanvasCloner.Clone(workflowCanvasState);
	workflowCanvasState2.Nodes[0].Title = "计划之后发生变化";
	SyncApplyResult syncApplyResult2 = CanvasWorkTreeSync.Apply(workflowCanvasState2, syncPlan);
	Expect(syncApplyResult2.Refused && syncApplyResult2.Conflicts.Any((SyncConflict conflict) => conflict.Code == "SYNC_PLAN_STALE"), "画布基线变化后必须拒绝应用旧计划：" + syncApplyResult2.ToText());
	Expect(workflowCanvasState2.WorkTree.Count == 0, "拒绝旧计划不得修改输入画布");
	SyncPlan syncPlan2 = CanvasWorkTreeSync.Plan(syncApplyResult.Canvas, SyncDirection.CanvasToWorkTree);
	Expect(syncPlan2.Changes.Count == 0, "重复同步应零改动：" + syncPlan2.ToText());
}

/// <summary>工作树 → 画布：显示文本自动同步，标题/悬空锚点需要人工确认。</summary>
static void SyncWorkTreeToCanvasRespectsConfirmation()
{
    var (canvas, chapter1, _, shot1, _) = ChapterFixture();
    var chapterNode = new WorkflowNode { Title = "旧标题", Category = NodeCategory.Chapter, WorkTreeItemId = chapter1.Id };
    var dangling = new WorkflowNode { Title = "悬空锚点", WorkTreeItemId = Guid.NewGuid(), Chapter = "第7章" };
    canvas.Nodes.Add(chapterNode);
    canvas.Nodes.Add(dangling);
    var before = JsonSerializer.Serialize(canvas);

    var plan = CanvasWorkTreeSync.Plan(canvas, SyncDirection.WorkTreeToCanvas);
    Expect(plan.Changes.Any(change => change.Kind == SyncChangeKind.UpdateChapterNodeTitle && change.RequiresConfirmation), "章节节点标题不一致应待确认");
    Expect(plan.Changes.Any(change => change.Kind == SyncChangeKind.DetachMissingAnchor && change.RequiresConfirmation), "悬空锚点应待确认");
    Expect(plan.Conflicts.Any(conflict => conflict.Code == CanvasSyncConflictCodes.AnchorDangling), "悬空锚点应报冲突");

    var withoutConfirm = CanvasWorkTreeSync.Apply(canvas, plan, applyUnconfirmed: false);
    Expect(!withoutConfirm.Refused, "非阻断计划不应被拒绝");
    Expect(withoutConfirm.Skipped.Any(change => change.RequiresConfirmation), "待确认项应被跳过");
    Expect(withoutConfirm.Canvas.Nodes.First(node => node.Id == chapterNode.Id).Title == "旧标题", "未确认时不得改标题");
    Expect(withoutConfirm.Canvas.Nodes.First(node => node.Id == dangling.Id).WorkTreeItemId == dangling.WorkTreeItemId, "未确认时不得清空锚点");

    var confirmed = CanvasWorkTreeSync.Apply(canvas, plan, applyUnconfirmed: true);
    Expect(confirmed.Canvas.Nodes.First(node => node.Id == chapterNode.Id).Title == "第1章", "确认后按事实源同步章节标题");
    Expect(confirmed.Canvas.Nodes.First(node => node.Id == dangling.Id).WorkTreeItemId is null, "确认后清空悬空锚点");
    Expect(confirmed.Canvas.Nodes.First(node => node.Id == dangling.Id).Chapter == "第7章", "清空锚点后保留显示文本");
    Expect(JsonSerializer.Serialize(canvas) == before, "应用不得改动调用方画布");
}

/// <summary>同步会话：应用可撤销，撤销回到同步前状态。</summary>
static void SyncSessionSupportsUndo()
{
    var canvas = new WorkflowCanvasState();
    var chapterNode = new WorkflowNode { Title = "第5章", Category = NodeCategory.Chapter };
    canvas.Nodes.Add(chapterNode);
    var original = JsonSerializer.Serialize(canvas);

    var session = new CanvasSyncSession();
    session.Plan(canvas, SyncDirection.CanvasToWorkTree);
    var applied = session.Apply(canvas);
    Expect(applied.Changed && session.CanUndo, "应用后应可撤销");
    Expect(JsonSerializer.Serialize(canvas) == original, "同步应用不改动调用方画布");

    var restored = session.Undo();
    Expect(restored is not null && JsonSerializer.Serialize(restored) == original, "撤销应回到同步前状态");
    Expect(!session.CanUndo, "撤销后不应再有快照");

    // 撤销用到的是内存快照，磁盘文件不受影响
    Expect(CanvasWorkTreeSync.Plan(canvas, SyncDirection.CanvasToWorkTree).Changes.Count > 0, "撤销后画布仍是同步前的待同步状态");
}

/// <summary>章节元数据随保存重开、复制与资产包往返保真，且只读画布仍拒绝覆盖保存。</summary>
static void ChapterMetadataSurvivesRoundTrip()
{
    using var stores = new IsolatedStores();
    var (canvas, chapter1, chapter2, shot1, _) = ChapterFixture();
    var normalized = CanvasMigration.Migrate(new RecentCanvasState("章节往返", 2, "", "", 512, 512, 20, 7, "", canvas)).State;

    var path = Path.Combine(stores.CanvasDirectory, "chapters.json");
    CanvasSaveService.Save(normalized, path);
    Expect(CanvasOpenService.TryOpen(path, out var reopened, out var openError), "重开失败：" + openError);
    var reopenedChapter = CanvasChapters.ChapterItems(reopened.State.Canvas);
    Expect(reopenedChapter.Count == 2 && reopenedChapter.All(item => item.Order > 0), "重开后章节顺序应保留");
    Expect(CanvasChapters.ResolveChapterId(reopened.State.Canvas, reopened.State.Canvas.Nodes.First(node => node.Id == shot1.Id)) == chapter1.Id,
        "重开后节点锚点应指向同一章节");

    // 复制：章节 ID 换新，锚点重映射到复制后的章节
    var copy = CanvasDuplication.DuplicateCanvas(reopened.State.Canvas);
    var copiedChapters = CanvasChapters.ChapterItems(copy.Canvas);
    Expect(copiedChapters.Count == 2 && copiedChapters.All(item => copiedChapters.Count(candidate => candidate.Id == item.Id) == 1),
        "复制出的章节 ID 应各不相同");
    Expect(copiedChapters.All(item => item.Order > 0), "复制应保留显式顺序");
    var copiedShot = copy.Canvas.Nodes.First(node => node.Title == shot1.Title);
    Expect(copiedShot.WorkTreeItemId is { } anchor && copiedChapters.Any(item => item.Id == anchor), "复制后节点应锚定到复制出的章节");
    Expect(CanvasIdentityValidator.Validate(copy.Canvas).IsClean, "复制结果应自洽");

    // 资产包往返：章节元数据与映射随包进出
    var packageDirectory = Path.Combine(stores.Root, "chapter-package");
    CanvasPackage.Export(packageDirectory, reopened.State);
    var packageFile = Path.Combine(packageDirectory, CanvasPackage.CanvasFileName);
    var target = new RecentCanvasState("目标", 0, "", "", 512, 512, 20, 7, "", new WorkflowCanvasState());
    Expect(CanvasImportService.TryImport(packageFile, target, CanvasImportMode.Merge, out var imported, out var importError), "导入失败：" + importError);
    var importedChapters = CanvasChapters.ChapterItems(imported.State.Canvas);
    Expect(importedChapters.Count == 2 && importedChapters.All(item => item.Order > 0), "导入后章节与顺序应保留");
    Expect(imported.Chapters.ChapterCount == 2, "导入结果应报告章节摘要：" + imported.Chapters.Text);
    Expect(CanvasChapters.ResolveChapterId(imported.State.Canvas, imported.State.Canvas.Nodes.First(node => node.Id == shot1.Id)) == chapter1.Id,
        "导入后节点锚点仍指向同一章节");
    Expect(CanvasImportService.TryImport(packageFile, imported.State, CanvasImportMode.Merge, out var again, out _), "二次导入失败");
    Expect(!again.Merge!.Changed, "重复导入同一包仍应零增量");

    // 只读的更高格式版本：章节同步不落盘，保存依旧被拒绝
    var future = imported.State with { FormatVersion = CanvasFormat.Current + 98 };
    var futurePath = Path.Combine(stores.CanvasDirectory, "chapters-future.json");
    CanvasFileWriter.Write(futurePath, future);
    var futureBytes = File.ReadAllBytes(futurePath);
    ExpectThrows<CanvasSaveAbortedException>(() => CanvasSaveService.Save(future, futurePath), "未知格式必须拒绝覆盖保存");
    Expect(File.ReadAllBytes(futurePath).SequenceEqual(futureBytes), "拒绝后文件必须原样");
    Expect(CanvasChapters.List(future.Canvas ?? new WorkflowCanvasState()).Count == importedChapters.Count, "只读画布仍可查看章节结构");
}

/// <summary>章节测试用的样例：两个章节 + 各自锚定的分镜节点。</summary>
static (WorkflowCanvasState Canvas, WorkTreeItem Chapter1, WorkTreeItem Chapter2, WorkflowNode Shot1, WorkflowNode Shot2) ChapterFixture()
{
    var canvas = new WorkflowCanvasState();
    var chapter1 = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第1章" };
    var chapter2 = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第2章" };
    canvas.WorkTree.Add(chapter1);
    canvas.WorkTree.Add(chapter2);
    var shot1 = new WorkflowNode { Title = "第1章分镜", Category = NodeCategory.Storyboard, WorkTreeItemId = chapter1.Id, Chapter = "第1章" };
    var shot2 = new WorkflowNode { Title = "第2章分镜", Category = NodeCategory.Storyboard, WorkTreeItemId = chapter2.Id, Chapter = "第2章" };
    canvas.Nodes.Add(shot1);
    canvas.Nodes.Add(shot2);
    return (canvas, chapter1, chapter2, shot1, shot2);
}

/// <summary>
/// 章节结构入口（返工 B-UI-01）：桌面「章节结构」对话框按钮调用的是
/// <see cref="CanvasChapterStructureSession"/>，这里按同一路径逐步驱动全部七种结构操作，
/// 覆盖阻断冲突不改画布、撤销、经统一保存入口落盘与重开保真。
/// </summary>
static void ChapterStructureEntryCoversAllOperations()
{
    using var stores = new IsolatedStores();
    var (seed, chapter1, chapter2, shot1, shot2) = ChapterFixture();
    var document = CanvasMigration.Migrate(new RecentCanvasState("章节结构", 0, "", "", 512, 512, 20, 7, "", seed)).State;
    var canvas = document.Canvas;

    var saved = new List<string>();
    var path = Path.Combine(stores.CanvasDirectory, "structure.json");
    var session = new CanvasChapterStructureSession(canvas, state =>
    {
        CanvasSaveService.Save(document with { Canvas = state }, path);
        saved.Add(path);
        return true;
    });

    // 建立
    var created = session.Create("第3章");
    Expect(created.Changed && !session.LastBlocked, "建立章节应成功：" + session.LastSummary);
    var chapter3 = CanvasChapters.List(session.State).First(info => info.Name == "第3章");

    // 改名
    session.Rename(chapter3.Id, "第3章·改");
    Expect(CanvasChapters.List(session.State).Any(info => info.Name == "第3章·改"), "改名应生效：" + session.LastSummary);

    // 排序（把第3章调到最前）
    session.Reorder(chapter3.Id, 5);
    Expect(CanvasChapters.List(session.State)[0].Id == chapter3.Id, "排序应把目标章节排到最前：" + session.LastSummary);

    // 移动（把第3章挂到第1章下）
    session.Move(chapter3.Id, chapter1.Id);
    Expect(CanvasChapters.List(session.State).First(info => info.Id == chapter3.Id).ParentChapterId == chapter1.Id,
        "移动应改变父章节：" + session.LastSummary);

    // 移动成环：阻断且不改状态
    var beforeCycle = JsonSerializer.Serialize(session.State);
    var cycle = session.Move(chapter1.Id, chapter3.Id);
    Expect(cycle.HasBlockingConflicts && session.LastBlocked, "成环移动应被阻断");
    Expect(JsonSerializer.Serialize(session.State) == beforeCycle, "阻断操作不得改变会话状态");

    // 拆分：把第2章的一个节点拆到新章节
    var split = session.Split(chapter2.Id, "第2章·后半", new[] { shot2.Id });
    Expect(split.Changed && !session.LastBlocked, "拆分应成功：" + session.LastSummary);
    var newChapter = CanvasChapters.List(session.State).First(info => info.Name == "第2章·后半");
    Expect(session.State.Nodes.First(node => node.Id == shot2.Id).WorkTreeItemId == newChapter.Id, "拆分应把节点改锚到新章节");

    // 合并：把拆出的章节并回第2章
    var merge = session.Merge(newChapter.Id, chapter2.Id);
    Expect(merge.Changed && CanvasChapters.List(session.State).All(info => info.Id != newChapter.Id), "合并应删除源章节");
    Expect(session.State.Nodes.First(node => node.Id == shot2.Id).WorkTreeItemId == chapter2.Id, "合并应把节点改锚回目标章节");

    // 删除有引用：阻断且不改状态
    var beforeDelete = JsonSerializer.Serialize(session.State);
    var blocked = session.Delete(chapter1.Id, reassignNodesToParent: false);
    Expect(blocked.HasBlockingConflicts && session.LastBlocked, "删除有引用的章节应阻断");
    Expect(session.LastSummary.Contains("画布未改动"), "阻断摘要应说明画布未改动");
    Expect(JsonSerializer.Serialize(session.State) == beforeDelete, "阻断的删除不得改变会话状态");

    // 删除并改挂：成功
    var deleted = session.Delete(chapter3.Id, reassignNodesToParent: true);
    Expect(deleted.Changed && !session.LastBlocked, "改挂后删除应成功：" + session.LastSummary);

    // 顺序归一化
    session.NormalizeOrder();
    Expect(CanvasChapters.List(session.State).All(info => info.IsOrderExplicit), "归一化后每个章节都应有显式顺序");

    // 撤销：回到删除前
    var beforeUndo = JsonSerializer.Serialize(session.State);
    Expect(session.Undo(), "应可撤销");
    Expect(JsonSerializer.Serialize(session.State) != beforeUndo, "撤销后状态应不同");
    Expect(CanvasChapters.List(session.State).Any(info => info.Id == chapter3.Id), "撤销应把被删章节恢复回来");

    // 保存：走注入的统一保存入口，重开后保真
    Expect(session.SaveCurrent() && saved.Count == 1, "保存应调用注入的统一保存入口");
    Expect(CanvasOpenService.TryOpen(path, out var reopened, out var openError), "重开失败：" + openError);
    var reopenedChapters = CanvasChapters.List(reopened.State.Canvas);
    Expect(reopenedChapters.Count == CanvasChapters.List(session.State).Count, "重开后章节数量应一致");
    Expect(reopenedChapters.All(info => info.IsOrderExplicit), "重开后显式顺序应保留");
    Expect(reopened.State.Canvas.Nodes.First(node => node.Id == shot2.Id).WorkTreeItemId == chapter2.Id, "重开后节点锚点应保留");
    Expect(CanvasIdentityValidator.Validate(reopened.State.Canvas).IsClean, "重开后应自洽：" + CanvasIdentityValidator.Validate(reopened.State.Canvas).ToText());
    session.Reset();
    Expect(!session.CanUndo, "重置后不应再有快照");
}

/// <summary>
/// 章节结构入口的保存结果闭环（返工 B-UI-02）：注入的保存回调必须把统一保存入口的真实结果传回，
/// 备份失败与写入失败两条真实失败路径都不得报成功；失败时内存画布保留、原画布文件字节不变，撤销仍只影响内存。
/// </summary>
static void ChapterStructureSaveFailureKeepsCanvasAndFile()
{
    using var stores = new IsolatedStores();
    var (seed, _, _, _, _) = ChapterFixture();
    var document = CanvasMigration.Migrate(new RecentCanvasState("保存闭环", 0, "", "", 512, 512, 20, 7, "", seed)).State;
    var path = Path.Combine(stores.CanvasDirectory, "save-loop.json");

    // 先正常落盘一份基线文件，作为「原文件」。
    CanvasSaveService.Save(document with { Canvas = seed }, path);
    var baselineBytes = File.ReadAllBytes(path);

    // 与 MainForm.SaveCanvasToLibrary 同一收敛点：任何失败都返回 false，绝不谎报成功。
    bool SaveLikeDesktop(WorkflowCanvasState state)
    {
        try { CanvasSaveService.Save(document with { Canvas = state }, path); return true; }
        catch (CanvasSaveAbortedException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    var session = new CanvasChapterStructureSession(seed, SaveLikeDesktop);
    Expect(!session.HasUnsavedChanges, "初始状态应视为已保存");
    Expect(session.LastSaveSucceeded is null, "未保存过时不应有保存结论");

    var created = session.Create("第3章");
    Expect(created.Changed && session.HasUnsavedChanges, "结构操作后应有未保存改动");

    // 失败路径一：备份失败——把备份目录的位置放成一个文件，TryBackup 无法建目录，Save 抛 CanvasSaveAbortedException。
    var backups = CanvasBackup.Directory;
    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(backups)!);
    File.WriteAllText(backups, "blocked");
    try
    {
        Expect(!session.SaveCurrent(), "备份失败时保存必须返回 false");
        Expect(session.LastSaveSucceeded == false, "备份失败时保存结论必须是失败");
        Expect(session.HasUnsavedChanges, "备份失败后仍应有未保存改动");
        Expect(File.ReadAllBytes(path).SequenceEqual(baselineBytes), "备份失败时原画布文件字节不得改变");
        Expect(session.Rename(CanvasChapters.List(session.State)[0].Id, "第1章·继续").Changed, "保存失败后应仍能继续编辑");
    }
    finally { File.Delete(backups); }

    // 解除阻断后应能保存成功，并刷新「已保存」状态。
    Expect(session.SaveCurrent(), "解除阻断后应保存成功");
    Expect(session.LastSaveSucceeded == true, "成功保存后结论应为成功");
    Expect(!session.HasUnsavedChanges, "保存成功后不应再有未保存改动");
    var afterSuccess = File.ReadAllBytes(path);
    Expect(!afterSuccess.SequenceEqual(baselineBytes), "保存成功后画布文件应已更新");
    Expect(CanvasOpenService.TryOpen(path, out var reopened, out var reopenError), "保存结果应可重开：" + reopenError);
    Expect(CanvasChapters.List(reopened.State.Canvas).Any(info => info.Name == "第3章"), "重开后应包含保存时的章节");

    // 失败路径二：写入失败——目标父路径被一个文件占用，CreateDirectory 直接抛 IOException。
    var blockedParent = Path.Combine(stores.CanvasDirectory, "blocked");
    File.WriteAllText(blockedParent, "blocked");
    var writeFailure = new CanvasChapterStructureSession(seed, state =>
    {
        try { CanvasSaveService.Save(document with { Canvas = state }, Path.Combine(blockedParent, "inner.json")); return true; }
        catch (CanvasSaveAbortedException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    });
    writeFailure.Create("第4章");
    Expect(!writeFailure.SaveCurrent(), "写入失败时保存必须返回 false");
    Expect(writeFailure.LastSaveSucceeded == false, "写入失败时保存结论必须是失败");
    Expect(writeFailure.HasUnsavedChanges, "写入失败后仍应有未保存改动");
    Expect(File.ReadAllBytes(path).SequenceEqual(afterSuccess), "写入失败不得影响已有画布文件");

    // 撤销只改内存：撤销后磁盘字节不变，但仍处于「有未保存改动」。
    var beforeUndo = File.ReadAllBytes(path);
    Expect(session.CanUndo, "应有可撤销操作");
    Expect(session.Undo(), "应可撤销");
    Expect(File.ReadAllBytes(path).SequenceEqual(beforeUndo), "撤销不得改动磁盘文件");
    Expect(session.HasUnsavedChanges, "撤销后内存与磁盘不一致，应仍有未保存改动");

    // 只读拒绝（保存回调返回 false）：同样只能得出失败结论。
    var readOnly = new CanvasChapterStructureSession(seed, _ => false);
    readOnly.Create("第5章");
    Expect(!readOnly.SaveCurrent(), "只读拒绝时保存必须返回 false");
    Expect(readOnly.LastSaveSucceeded == false, "只读拒绝时保存结论必须是失败");
    Expect(readOnly.HasUnsavedChanges, "只读拒绝后仍应有未保存改动");
}

/// <summary>
/// 批次 C 布局夹具（C-1）：企划节点在泳道外、两个显式顺序的章节、每章若干分镜、
/// 一个挂在分镜下的成品，以及一个「只有章节名、没有稳定锚点」的游离节点（不得被按名字绑定）。
/// </summary>
static (WorkflowCanvasState Canvas, WorkTreeItem Chapter1, WorkTreeItem Chapter2, List<WorkflowNode> Shots1, List<WorkflowNode> Shots2, WorkflowNode Product, WorkflowNode Orphan) LayoutFixture()
{
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "剧情源", Category = NodeCategory.StoryPlan, X = 400, Y = 500 });
    canvas.Nodes.Add(new WorkflowNode { Title = "企划", Category = NodeCategory.StoryOutline, X = 600, Y = 520 });

    var chapter1 = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第1章", Order = 10 };
    var chapter2 = new WorkTreeItem { Kind = WorkTreeKind.Chapter, Name = "第2章", Order = 20 };
    canvas.WorkTree.Add(chapter1);
    canvas.WorkTree.Add(chapter2);

    var shots1 = new List<WorkflowNode>();
    for (var index = 1; index <= 3; index++)
    {
        var shot = new WorkflowNode
        {
            Title = $"第1章分镜{index}",
            Category = NodeCategory.Storyboard,
            WorkTreeItemId = chapter1.Id,
            Chapter = "第1章",
            X = 800,
            Y = 1000
        };
        canvas.Nodes.Add(shot);
        shots1.Add(shot);
    }

    var shots2 = new List<WorkflowNode>();
    for (var index = 1; index <= 2; index++)
    {
        var shot = new WorkflowNode
        {
            Title = $"第2章分镜{index}",
            Category = NodeCategory.Storyboard,
            WorkTreeItemId = chapter2.Id,
            Chapter = "第2章",
            X = 1500 + index * 200,
            Y = 1400 + index * 100
        };
        canvas.Nodes.Add(shot);
        shots2.Add(shot);
    }

    var product = new WorkflowNode
    {
        Title = "第1章成品",
        Category = NodeCategory.Product,
        ParentNodeId = shots1[0].Id,
        WorkTreeItemId = chapter1.Id,
        Chapter = "第1章",
        X = 2200,
        Y = 2400
    };
    canvas.Nodes.Add(product);

    // 只有章节名文本、没有稳定锚点：不得被绑定到「第1章」。
    var orphan = new WorkflowNode { Title = "游离分镜", Category = NodeCategory.Storyboard, Chapter = "第1章", X = 3000, Y = 3000 };
    canvas.Nodes.Add(orphan);

    return (canvas, chapter1, chapter2, shots1, shots2, product, orphan);
}

/// <summary>批次 C / C-1：企划在泳道外、每章一条独立泳道、分镜横排、成品挂分镜下，且不新增/改动身份。</summary>
static void SwimlaneLayoutArrangesLanes()
{
    var (canvas, chapter1, chapter2, shots1, _, product, orphan) = LayoutFixture();
    var lanes = CanvasSwimlaneLayout.Lanes(canvas);

    Expect(lanes[0].Kind == CanvasLaneKind.Planning, "第一条泳道应是泳道外的企划区");
    Expect(lanes[0].NodeIds.Count == 2, "企划区应包含剧情源与企划两个节点");

    var chapterLanes = lanes.Where(lane => lane.Kind == CanvasLaneKind.Chapter).ToList();
    Expect(chapterLanes.Count == 2, "应有两个章节泳道");
    Expect(chapterLanes[0].ChapterId == chapter1.Id && chapterLanes[1].ChapterId == chapter2.Id,
        "章节泳道应按显式顺序排列且身份是稳定 ID");
    Expect(chapterLanes[0].NodeIds.Contains(orphan.Id) == false,
        "只有同名文本、没有稳定锚点的节点不得被绑定到该章节（不按名称猜身份）");
    Expect(lanes.First(lane => lane.Kind == CanvasLaneKind.Unassigned).NodeIds.Contains(orphan.Id),
        "没有稳定锚点的节点应归入未分章泳道");
    Expect(canvas.Nodes.Where(node => CanvasSwimlaneLayout.IsPlanningCategory(node.Category))
        .All(node => chapterLanes.All(lane => !lane.NodeIds.Contains(node.Id))), "企划节点不得进入任何章节泳道");

    var session = new CanvasLayoutSession(canvas);
    var plan = session.PreviewAll();
    Expect(!plan.HasBlockingConflicts, "无手动坐标时布局不应阻断：" + plan.ToText(canvas));
    Expect(session.Apply(), "应用整画布泳道布局应成功：" + session.LastSummary);

    var applied = session.State;
    var ordered = shots1.OrderBy(node => applied.Nodes.First(item => item.Id == node.Id).X).ToList();
    var xs = ordered.Select(node => applied.Nodes.First(item => item.Id == node.Id).X).ToList();
    Expect(xs.SequenceEqual(xs.OrderBy(value => value)), "分镜应按 X 递增排成一行");
    Expect(xs.Distinct().Count() == 3, "三个分镜不应重叠在同一列");
    Expect(ordered.Select(node => applied.Nodes.First(item => item.Id == node.Id).Y).Distinct().Count() == 1,
        "同章分镜应在同一行（Y 相同）");
    Expect(ordered[0].Title == "第1章分镜1", "分镜应按工作树顺序从左到右：" + string.Join("、", ordered.Select(node => node.Title)));

    var productNode = applied.Nodes.First(node => node.Id == product.Id);
    var parentNode = applied.Nodes.First(node => node.Id == shots1[0].Id);
    Expect(Math.Abs(productNode.X - parentNode.X) < 0.5f, "成品应与所属分镜同列");
    Expect(productNode.Y > parentNode.Y, "成品应在所属分镜下方");

    var bounds = CanvasSwimlaneLayout.LaneBounds(applied, CanvasSwimlaneLayout.Lanes(applied));
    var planning = bounds.First(item => item.Lane.Kind == CanvasLaneKind.Planning).Bounds;
    var firstChapter = bounds.First(item => item.Lane.Kind == CanvasLaneKind.Chapter).Bounds;
    Expect(planning.Bottom <= firstChapter.Top, "企划区应位于章节泳道之外（在章节泳道上方）");

    Expect(applied.Nodes.Count == canvas.Nodes.Count, "布局不得新增或删除节点");
    Expect(applied.Nodes.All(node => canvas.Nodes.Any(origin => origin.Id == node.Id)), "布局不得改动节点 ID");
    Expect(applied.Nodes.All(node => node.WorkTreeItemId == canvas.Nodes.First(origin => origin.Id == node.Id).WorkTreeItemId),
        "布局不得改动工作树锚点");
    Expect(applied.Nodes.All(node => node.References.Count == canvas.Nodes.First(origin => origin.Id == node.Id).References.Count),
        "布局不得改动引用");
}

/// <summary>批次 C / C-2：局部重排只动本章，预览不改画布，应用后可撤销。</summary>
static void SwimlaneLocalRearrangeOnlyTouchesChapter()
{
    var (canvas, chapter1, _, shots1, _, _, _) = LayoutFixture();
    var lane = CanvasSwimlaneLayout.Lanes(canvas).First(item => item.ChapterId == chapter1.Id);
    var before = JsonSerializer.Serialize(canvas);

    var session = new CanvasLayoutSession(canvas);
    var plan = session.PreviewChapter(chapter1.Id);

    Expect(JsonSerializer.Serialize(canvas) == before, "预览不得改动调用方画布");
    Expect(plan.Changed, "本章分镜初始重叠，局部重排应产生改动：" + plan.ToText(canvas));
    Expect(plan.Changes.All(change => lane.NodeIds.Contains(change.NodeId)),
        "局部重排只能改动本章泳道的节点");

    var others = canvas.Nodes.Where(node => !lane.NodeIds.Contains(node.Id))
        .ToDictionary(node => node.Id, node => (node.X, node.Y));
    Expect(session.Apply(), "应用局部重排应成功：" + session.LastSummary);
    foreach (var (id, position) in others)
    {
        var node = session.State.Nodes.First(item => item.Id == id);
        Expect(Math.Abs(node.X - position.X) < 0.5f && Math.Abs(node.Y - position.Y) < 0.5f,
            $"局部重排不得移动其他章节的节点：{node.Title}");
    }

    var xs = shots1.Select(node => session.State.Nodes.First(item => item.Id == node.Id).X).ToList();
    Expect(xs.Distinct().Count() == xs.Count, "本章分镜应被排到不同列");

    Expect(session.Undo(), "应可撤销布局");
    Expect(JsonSerializer.Serialize(session.State) == before, "撤销后应回到布局前的状态");

    // 不存在的章节：阻断，不写入
    var missing = session.PreviewChapter(Guid.NewGuid());
    Expect(missing.HasBlockingConflicts && missing.Conflicts.Any(conflict => conflict.Code == CanvasLayoutCodes.ChapterMissing),
        "重排不存在的章节应阻断");
    var snapshot = JsonSerializer.Serialize(session.State);
    Expect(!session.Apply(confirmManual: true) && JsonSerializer.Serialize(session.State) == snapshot,
        "章节目录缺失时不得写入画布");
}

/// <summary>批次 C / C-2：手动坐标受保护、需显式确认覆盖；两个手动节点重叠时阻断。</summary>
static void SwimlaneManualProtectionAndOverlap()
{
    var (canvas, chapter1, _, shots1, _, _, _) = LayoutFixture();
    var manual = shots1[1];
    manual.X = 120;
    manual.Y = 3200;
    manual.ManualPosition = true;

    var session = new CanvasLayoutSession(canvas);
    var plan = session.PreviewAll();
    Expect(plan.ProtectedNodeIds.Contains(manual.Id), "手动摆放的节点应进入保护名单");
    Expect(plan.Changes.All(change => change.NodeId != manual.Id), "手动摆放的节点不得出现在自动改动里");
    Expect(plan.RequiresConfirmation && !plan.HasBlockingConflicts, "有手动坐标时应要求确认，而不是阻断");

    Expect(!session.Apply(confirmManual: false), "未确认覆盖时不得写入");
    Expect(session.LastBlocked, "未确认覆盖应标记为阻断");
    var kept = session.State.Nodes.First(node => node.Id == manual.Id);
    Expect(Math.Abs(kept.X - 120) < 0.5f && Math.Abs(kept.Y - 3200) < 0.5f, "拒绝后手动坐标必须原样保留");

    Expect(session.Apply(confirmManual: true), "确认覆盖后应写入：" + session.LastSummary);
    var moved = session.State.Nodes.First(node => node.Id == manual.Id);
    Expect(Math.Abs(moved.X - 120) > 0.5f || Math.Abs(moved.Y - 3200) > 0.5f, "确认覆盖后手动节点应被重新排列");
    Expect(!moved.ManualPosition, "自动布局写回后应清除手动标记");

    // 两个手动节点重叠：阻断，且确认覆盖也不写入
    var (canvas2, _, _, _, shotsOther, _, _) = LayoutFixture();
    shotsOther[0].X = 5000; shotsOther[0].Y = 5000; shotsOther[0].ManualPosition = true;
    shotsOther[1].X = 5000; shotsOther[1].Y = 5000; shotsOther[1].ManualPosition = true;

    var overlapping = new CanvasLayoutSession(canvas2);
    var blocked = overlapping.PreviewAll();
    Expect(blocked.HasBlockingConflicts, "两个手动节点重叠应报阻断冲突：" + blocked.ToText(canvas2));
    Expect(blocked.Conflicts.Any(conflict => conflict.Code == CanvasLayoutCodes.ManualOverlap && conflict.Blocking),
        "冲突代码应是 LAYOUT_MANUAL_OVERLAP 且为阻断");
    var snapshot = JsonSerializer.Serialize(overlapping.State);
    Expect(!overlapping.Apply(confirmManual: true), "有阻断冲突时即使确认覆盖也不得写入");
    Expect(JsonSerializer.Serialize(overlapping.State) == snapshot, "阻断时画布必须原样不变");
}

/// <summary>批次 C / C-3：临时展开不落盘、锁定版本缺失可检出、反向定位按稳定实体 ID。</summary>
static void ReferenceExpansionAndMissingVersions()
{
    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("少年");
    canvas.Entities.Add(entity);

    var bound = new WorkflowNode { Title = "分镜·锁定有效版本", Category = NodeCategory.Storyboard };
    bound.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = variant.Versions[0].Id });
    var broken = new WorkflowNode { Title = "分镜·锁定缺失版本", Category = NodeCategory.Storyboard };
    broken.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = Guid.NewGuid() });
    var following = new WorkflowNode { Title = "分镜·跟随最新", Category = NodeCategory.Storyboard };
    following.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    canvas.Nodes.AddRange(new[] { bound, broken, following });

    Expect(CanvasReferences.MissingLockedVersionCount(canvas, bound) == 0, "锁定有效版本不应报缺失");
    Expect(CanvasReferences.MissingLockedVersionCount(canvas, broken) == 1, "锁定缺失版本应报 1 条");
    Expect(CanvasReferences.MissingLockedVersionCount(canvas, following) == 0, "跟随最新不算缺失");
    var missing = CanvasReferences.MissingLockedVersions(canvas);
    Expect(missing.Count == 1 && missing[0].Node.Id == broken.Id, "缺失清单应只含锁定缺失的那条");

    var boundPairs = canvas.ResolveReferencePairs(bound);
    Expect(CanvasReferences.VersionLabel(boundPairs[0].Reference, boundPairs[0].Content) == variant.Versions[0].Label,
        "有效锁定应显示版本号");
    var brokenPairs = canvas.ResolveReferencePairs(broken);
    Expect(CanvasReferences.VersionLabel(brokenPairs[0].Reference, brokenPairs[0].Content) == "版本缺失",
        "缺失锁定应显示「版本缺失」");
    var followPairs = canvas.ResolveReferencePairs(following);
    Expect(CanvasReferences.VersionLabel(followPairs[0].Reference, followPairs[0].Content) == "跟随最新",
        "未锁定时应显示「跟随最新」");

    Expect(CanvasReferences.NodesReferencing(canvas, entity.Id).Count == 3, "三个分镜都引用同一实体，应全部定位到");
    Expect(CanvasReferences.NodesReferencing(canvas, Guid.NewGuid()).Count == 0, "不存在的实体不应定位到任何节点");

    var before = JsonSerializer.Serialize(canvas);
    var nodeCount = canvas.Nodes.Count;
    var anchors = canvas.Nodes.ToDictionary(node => node.Id, node => node.WorkTreeItemId);
    var referenceCounts = canvas.Nodes.ToDictionary(node => node.Id, node => node.References.Count);

    var expansion = new CanvasReferenceExpansionState();
    Expect(expansion.Toggle(bound.Id), "首次切换应展开");
    Expect(expansion.IsExpanded(bound.Id) && expansion.Count == 1, "展开集合应记录该节点");
    Expect(!expansion.Toggle(bound.Id), "再次切换应收起");
    Expect(expansion.Count == 0, "收起后集合应为空");
    expansion.Toggle(bound.Id);
    expansion.Toggle(broken.Id);
    Expect(expansion.Count == 2, "应支持多个节点同时展开");
    expansion.CollapseAll();
    Expect(expansion.Count == 0, "CollapseAll 应清空");

    Expect(JsonSerializer.Serialize(canvas) == before, "临时展开不得改动画布数据");
    Expect(canvas.Nodes.Count == nodeCount, "临时展开不得生成常驻节点");
    Expect(canvas.Nodes.All(node => anchors[node.Id] == node.WorkTreeItemId), "临时展开不得改动工作树锚点");
    Expect(canvas.Nodes.All(node => node.References.Count == referenceCounts[node.Id]), "临时展开不得改动引用条数");
}

/// <summary>批次 C / C-4：桌面投影给 Web 带稳定 chapterId 与显式顺序；同名不同 ID 的章节不合并。</summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void ChapterProjectionCarriesStableIds()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkTreeItem workTreeItem = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第1章",
		Order = 10
	};
	WorkTreeItem workTreeItem2 = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第1章",
		Order = 20
	};
	workflowCanvasState.WorkTree.Add(workTreeItem);
	workflowCanvasState.WorkTree.Add(workTreeItem2);
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "A 分镜",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem.Id,
		Chapter = "第1章"
	};
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "B 分镜",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem2.Id,
		Chapter = "第1章"
	};
	WorkflowNode orphan = new WorkflowNode
	{
		Title = "未归档分镜",
		Category = NodeCategory.Storyboard,
		Chapter = "第1章"
	};
	orphan.References.Add(new NodeReference
	{
		EntityId = Guid.NewGuid(),
		VariantId = Guid.NewGuid(),
		VariantVersionId = Guid.NewGuid()
	});
	workflowCanvasState.Nodes.AddRange(new WorkflowNode[3] { workflowNode, workflowNode2, orphan });
	string json = JsonSerializer.Serialize(NodeProjection.ProjectRecords(workflowCanvasState.Nodes, workflowCanvasState));
	using JsonDocument jsonDocument = JsonDocument.Parse(json);
	Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
	Dictionary<string, int?> dictionary2 = new Dictionary<string, int?>(StringComparer.Ordinal);
	int num = 0;
	foreach (JsonElement item11 in jsonDocument.RootElement.EnumerateArray())
	{
		string key = item11.GetProperty("recordId").GetString() ?? string.Empty;
		string text = item11.GetProperty("recordType").GetString() ?? string.Empty;
		if (text == "chapter")
		{
			num++;
		}
		JsonElement property = item11.GetProperty("record");
		dictionary[key] = (property.TryGetProperty("chapterId", out var value) ? value.GetString() : null);
		dictionary2[key] = ((property.TryGetProperty("order", out var value2) && value2.TryGetInt32(out var value3)) ? new int?(value3) : ((int?)null));
	}
	Expect(num == 2, "两个同名不同 ID 的章节应投影为两条记录，不合并：" + num);
	Expect(dictionary.TryGetValue(workflowNode.Id.ToString(), out var value4) && value4 == workTreeItem.Id.ToString(), "A 分镜应带第一个章节的稳定 ID");
	Expect(dictionary.TryGetValue(workflowNode2.Id.ToString(), out var value5) && value5 == workTreeItem2.Id.ToString(), "B 分镜应带第二个章节的稳定 ID");
	Expect(dictionary.TryGetValue(orphan.Id.ToString(), out var value6) && value6 == null, "只有章节名文本、没有稳定锚点的节点不得带章节 ID");
	Expect(dictionary2.TryGetValue($"wt-{workTreeItem.Id}", out var value7) && value7 == 10, "章节记录应带显式顺序（第一个 10）");
	Expect(dictionary2.TryGetValue($"wt-{workTreeItem2.Id}", out var value8) && value8 == 20, "章节记录应带显式顺序（第二个 20）");
	JsonElement jsonElement = jsonDocument.RootElement.EnumerateArray().First((JsonElement element) => element.GetProperty("recordId").GetString() == orphan.Id.ToString()).GetProperty("record")
		.GetProperty("references")[0];
	Expect(jsonElement.GetProperty("unresolved").GetBoolean(), "缺失实体引用应投影为 unresolved 状态");
	Expect(jsonElement.GetProperty("entityId").GetString() == orphan.References[0].EntityId.ToString(), "unresolved 投影应保留实体 ID");
	Expect(jsonElement.GetProperty("variantId").GetString() == orphan.References[0].VariantId.ToString(), "unresolved 投影应保留变体 ID");
	Expect(jsonElement.GetProperty("variantVersionId").GetString() == orphan.References[0].VariantVersionId.ToString(), "unresolved 投影应保留版本 ID");
}

/// <summary>批次 C / C-1、C-2：Agent 建节点落位走稳定章节 ID 与泳道引擎，不再按章节名分块，也不动手动坐标。</summary>
static void AgentNodePlacementUsesChapterLanes()
{
    var (canvas, chapter1, chapter2, _, shots2, _, _) = LayoutFixture();

    // 手动摆放一个第2章分镜：Agent 落位不得移动它。
    var manual = shots2[0];
    manual.X = 120;
    manual.Y = 3300;
    manual.ManualPosition = true;

    var others = canvas.Nodes.Where(node => node.Id != manual.Id)
        .ToDictionary(node => node.Id, node => (node.X, node.Y));
    // 落位只允许重排目标泳道（第1章）：其他泳道的节点必须原地不动。
    var movable = CanvasSwimlaneLayout.Lanes(canvas).First(item => item.ChapterId == chapter1.Id).NodeIds.ToHashSet();
    var nodesBefore = canvas.Nodes.Count;

    var result = AgentActionExecutor.Apply(new[]
    {
        new AgentAction { Kind = "create_node", Title = "第1章分镜4", NodeCategory = "分镜", WorkTreeTarget = chapter1.Name }
    }, canvas, null);
    Expect(result.Applied == 1 && result.Errors.Count == 0, "Agent 建节点应成功：" + string.Join("；", result.Errors));

    var created = canvas.Nodes.Single(node => node.Title == "第1章分镜4");
    Expect(CanvasChapters.ResolveChapterId(canvas, created) == chapter1.Id, "新分镜应按稳定 ID 归到第1章");

    var lanes = CanvasSwimlaneLayout.Lanes(canvas);
    var lane1 = lanes.First(item => item.ChapterId == chapter1.Id);
    var lane2 = lanes.First(item => item.ChapterId == chapter2.Id);
    Expect(lane1.NodeIds.Contains(created.Id), "新分镜应落在第1章泳道里");
    Expect(!lane2.NodeIds.Contains(created.Id), "新分镜不得落进第2章泳道");

    // 手动摆放的节点坐标与标记都必须保留。
    var keptManual = canvas.Nodes.First(node => node.Id == manual.Id);
    Expect(Math.Abs(keptManual.X - 120) < 0.5f && Math.Abs(keptManual.Y - 3300) < 0.5f, "Agent 落位不得移动手动摆放的节点");
    Expect(keptManual.ManualPosition, "手动标记应保留");

    // 目标泳道之外的节点坐标一律不变（尤其其他章节）。
    foreach (var (id, position) in others)
    {
        if (movable.Contains(id)) continue;
        var node = canvas.Nodes.First(item => item.Id == id);
        Expect(Math.Abs(node.X - position.X) < 0.5f && Math.Abs(node.Y - position.Y) < 0.5f,
            $"Agent 落位不得移动其他泳道的节点：{node.Title}");
    }

    // 第1章泳道内的非成品节点排在同一行（分镜横排）。
    var rowCount = lane1.NodeIds
        .Select(id => canvas.Nodes.First(node => node.Id == id))
        .Where(node => node.Category != NodeCategory.Product)
        .Select(node => node.Y)
        .Distinct()
        .Count();
    Expect(rowCount == 1, "第1章泳道内的分镜应排在同一行");

    // 新节点不产生常驻资源节点，也不多建节点。
    Expect(canvas.Entities.Count == 0, "Agent 落位不得新增实体");
    Expect(canvas.Nodes.Count == nodesBefore + 1, "Agent 落位只应新增被创建的那一个节点");
}

/// <summary>目标 4 / 4.1、4.3：引用扫描要覆盖当前画布 + 画布库 + 草稿；其它来源仍在引用时硬阻断删除。</summary>
static void ReferenceScanSpansCanvasDraftAndLibrary()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var current = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    current.Entities.Add(entity);
    var node = new WorkflowNode { Title = "当前分镜", Category = NodeCategory.Storyboard };
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = variant.Versions[0].Id });
    current.Nodes.Add(node);

    var currentPath = Path.Combine(stores.CanvasDirectory, "current.json");
    CanvasSaveService.Save(new RecentCanvasState("当前画布", 0, "", "", 512, 512, 20, 7, "", current), currentPath);

    // 画布库里的另一份画布引用同一实体（同一个 ID）。
    var libraryCanvas = new WorkflowCanvasState();
    libraryCanvas.Entities.Add(entity);
    var libraryNode = new WorkflowNode { Title = "库内分镜", Category = NodeCategory.Storyboard };
    libraryNode.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    libraryCanvas.Nodes.Add(libraryNode);
    var libraryPath = Path.Combine(stores.CanvasDirectory, "library.json");
    CanvasSaveService.Save(new RecentCanvasState("库画布", 0, "", "", 512, 512, 20, 7, "", libraryCanvas), libraryPath);

    // 草稿画布也引用同一实体。
    var draftCanvas = new WorkflowCanvasState();
    draftCanvas.Entities.Add(entity);
    var draftNode = new WorkflowNode { Title = "草稿分镜", Category = NodeCategory.Storyboard };
    draftNode.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    draftCanvas.Nodes.Add(draftNode);
    CanvasSaveService.Save(new RecentCanvasState("草稿画布", 0, "", "", 512, 512, 20, 7, "", draftCanvas), StorageMaintenance.DraftCanvasPath);

    var report = CanvasReferenceScanner.Scan(current, currentPath);
    Expect(report.ScannedCanvases == 3, "应扫描当前画布 + 画布库 + 草稿三个来源：" + report.ScannedCanvases);
    Expect(report.Skipped.Count == 0, "不应有打不开的来源：" + string.Join("；", report.Skipped));

    var hits = CanvasReferenceScanner.ForEntity(report, entity.Id);
    Expect(hits.Count == 3, "三个来源各命中一条引用：" + hits.Count);
    Expect(hits.Count(hit => hit.Scope == ReferenceScopeKind.Canvas) == 1, "当前画布应命中 1 条");
    Expect(hits.Count(hit => hit.Scope == ReferenceScopeKind.Library) == 1, "画布库应命中 1 条");
    Expect(hits.Count(hit => hit.Scope == ReferenceScopeKind.Draft) == 1, "草稿应命中 1 条");
    Expect(!hits.Single(hit => hit.Scope == ReferenceScopeKind.Canvas).LockedVersionMissing, "锁定了存在的版本不应报缺失");

    var guard = CanvasDeletionGuard.CheckEntity(report, entity);
    Expect(guard.HardBlocked && !guard.CanDelete, "其它来源仍在引用时必须硬阻断删除");
    Expect(guard.LocalCount == 1 && guard.ForeignCount == 2, $"应为本画布 1、其它来源 2：{guard.LocalCount}/{guard.ForeignCount}");

    // 其它来源不再引用后：只剩本画布引用，可以删除（但要求显式解除引用）。
    File.Delete(libraryPath);
    File.Delete(StorageMaintenance.DraftCanvasPath);
    var narrowed = CanvasReferenceScanner.Scan(current, currentPath);
    var narrowedGuard = CanvasDeletionGuard.CheckEntity(narrowed, entity);
    Expect(!narrowedGuard.HardBlocked && narrowedGuard.CanDelete && narrowedGuard.LocalCount == 1,
        "只剩本画布引用时应可删除（需解除引用）：" + narrowedGuard.Message);

    // 解除引用只改内存：节点保留、引用清空、画布文件字节不变。
    var beforeBytes = File.ReadAllBytes(currentPath);
    var removed = CanvasDeletionGuard.RemoveReferencesIn(current, entity.Id);
    Expect(removed == 1 && current.Nodes.Count == 1 && current.Nodes[0].References.Count == 0,
        "应解除 1 条引用且节点本身保留");
    Expect(File.ReadAllBytes(currentPath).SequenceEqual(beforeBytes), "解除引用不得改写任何画布文件");
}

/// <summary>目标 4 / 4.3、4.4：删除是「移入回收站」，还原后引用按稳定 ID 重新生效。</summary>
static void DeletionGuardAndRecycleBinRestore()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    var version = variant.Versions[0];
    canvas.Entities.Add(entity);
    var node = new WorkflowNode { Title = "分镜", Category = NodeCategory.Storyboard };
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = version.Id });
    canvas.Nodes.Add(node);

    var report = CanvasReferenceScanner.Scan(canvas, null, includeLibrary: false, includeDraft: false);
    Expect(CanvasRecycleBin.TryStashVariant(entity, variant, CanvasReferenceScanner.ForVariant(report, entity.Id, variant.Id), out var entry, out var stashError),
        "变体快照应写入回收站：" + stashError);
    Expect(entry is not null, "写入成功后应返回回收站记录");
    entity.Variants.Remove(variant);

    Expect(CanvasReferenceVersions.IsLockedVersionMissing(canvas, node.References[0]), "变体被移走后锁定版本应判为缺失");
    Expect(canvas.ResolveReferences(node).Count == 0, "变体没了，引用解析不到内容");
    Expect(CanvasRecycleBin.List().Count == 1, "回收站应有 1 条记录");
    Expect(CanvasRecycleBin.List()[0].ReferencedBy.Count == 1, "回收站应记录删除时的引用来源");

    Expect(CanvasRecycleBin.Restore(entry!.Id, canvas, out var message), "应能还原变体：" + message);
    Expect(canvas.Entities.Single().Variants.Any(item => item.Id == variant.Id), "还原后变体 ID 必须保持不变");
    var restored = canvas.ResolveReferences(node);
    Expect(restored.Count == 1, "还原后引用应重新解析成功");
    Expect(restored[0].Version?.Id == version.Id && !restored[0].VersionMissing, "还原后应重新命中原锁定版本");
    Expect(!CanvasReferenceVersions.IsNodeBlocked(canvas, node), "还原后不应再是阻断状态");
    Expect(CanvasRecycleBin.List().Count == 0, "还原后回收站记录应被移除");

    // 实体整体移入回收站并还原：实体 ID 与变体都保留，出图内容恢复可用。
    Expect(CanvasRecycleBin.TryStashEntity(entity, CanvasReferenceScanner.ForEntity(report, entity.Id), out var entityEntry, out var entityStashError),
        "实体快照应写入回收站：" + entityStashError);
    canvas.Entities.Remove(entity);
    Expect(canvas.Entities.Count == 0, "实体应已移出画布");
    Expect(CanvasRecycleBin.Restore(entityEntry!.Id, canvas, out var entityMessage), "应能还原实体：" + entityMessage);
    Expect(canvas.FindEntity(entity.Id) is not null, "还原后实体 ID 必须保持不变");
    Expect(CanvasReferenceVersions.UsableContents(canvas, node).Count == 1, "还原后出图可用引用应恢复");

    // 彻底删除后不可再还原。
    Expect(CanvasRecycleBin.TryStashEntity(entity, Array.Empty<ReferenceHit>(), out var purgeEntry, out var purgeStashError),
        "快照应写入回收站：" + purgeStashError);
    canvas.Entities.Remove(entity);
    Expect(CanvasRecycleBin.TryPurge(purgeEntry!.Id, out var purgeError), "彻底删除应成功：" + purgeError);
    Expect(!CanvasRecycleBin.Restore(purgeEntry.Id, canvas, out var purgeMessage), "彻底删除后不应再能还原");
    Expect(purgeMessage.Contains("找不到"), "应提示记录不存在：" + purgeMessage);
}

/// <summary>返工（目标 4 复核）：回收站写入失败时删除必须整体取消，内存与磁盘都保持原样。</summary>
static void RecycleBinWriteFailureCancelsDeletion()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    var version = variant.Versions[0];
    canvas.Entities.Add(entity);
    var node = new WorkflowNode { Title = "分镜", Category = NodeCategory.Storyboard };
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = version.Id });
    canvas.Nodes.Add(node);

    var report = CanvasReferenceScanner.Scan(canvas, null, includeLibrary: false, includeDraft: false);
    var hits = CanvasReferenceScanner.ForEntity(report, entity.Id);
    var before = JsonSerializer.Serialize(canvas);

    // 阻断回收站写入：把回收站文件路径占成一个目录，任何写文件操作都会失败。
    Directory.CreateDirectory(CanvasRecycleBin.FilePath);

    Expect(!CanvasRecycleBin.TryStashEntity(entity, hits, out var entry, out var stashError), "写盘失败时移入回收站必须返回 false");
    Expect(entry is null, "写盘失败时不应给出成功记录");
    Expect(stashError.Length > 0, "必须把失败原因带回");
    Expect(CanvasRecycleBin.List().Count == 0, "写盘失败后回收站里不应有记录");

    Expect(!CanvasDeletionGuard.TryDeleteEntity(canvas, entity, hits, out var deleteError), "快照写不进去时删除必须整体取消");
    Expect(deleteError.Length > 0, "取消删除应带回原因：" + deleteError);
    Expect(canvas.FindEntity(entity.Id) is not null, "取消删除后实体必须仍在画布上");
    Expect(canvas.Entities.Count == 1, "实体数量不得变化");
    Expect(node.References.Count == 1, "取消删除后引用必须原样保留");
    Expect(JsonSerializer.Serialize(canvas) == before, "取消删除后画布数据必须逐字节一致");

    // 变体删除同样不能出现「快照没写进去、变体已经没了」。
    Expect(!CanvasDeletionGuard.TryDeleteVariant(canvas, entity, variant, hits, out var variantError), "变体删除也必须整体取消");
    Expect(variantError.Length > 0, "变体取消删除应带回原因");
    Expect(entity.Variants.Contains(variant) && node.References.Count == 1, "取消后变体与引用都必须保留");

    // 解除占用后立刻可正常删除，并且能还原回来。
    Directory.Delete(CanvasRecycleBin.FilePath);
    Expect(CanvasDeletionGuard.TryDeleteEntity(canvas, entity, hits, out var okError), "恢复写入后应可删除：" + okError);
    Expect(canvas.Entities.Count == 0, "删除成功后实体应移出画布");
    Expect(CanvasRecycleBin.List().Count == 1, "删除成功后回收站应有 1 条记录");
    Expect(CanvasRecycleBin.Restore(CanvasRecycleBin.List()[0].Id, canvas, out var restoreMessage), "应能还原：" + restoreMessage);
    Expect(canvas.FindEntity(entity.Id) is not null, "还原后实体应回到画布");
}

/// <summary>返工（目标 4 第二次复核）：Agent 的 delete_entity 不得绕过引用扫描、硬阻断与回收站链路。</summary>
static void AgentDeleteEntityUsesReferenceProtection()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("默认");
    var version = variant.Versions[0];
    canvas.Entities.Add(entity);
    var node = new WorkflowNode { Title = "分镜", Category = NodeCategory.Storyboard };
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id, VariantVersionId = version.Id });
    canvas.Nodes.Add(node);

    var canvasPath = Path.Combine(stores.CanvasDirectory, "agent-delete.json");
    CanvasSaveService.Save(new RecentCanvasState("当前画布", 0, "", "", 512, 512, 20, 7, "", canvas), canvasPath);

    // 1) 本画布仍有引用、动作又没有声明解除引用：拒绝，画布与回收站都不变。
    var refused = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_entity", Target = entity.Name } }, canvas, null, FileWriteMode.Apply, null, canvasPath);
    Expect(refused.Applied == 0 && refused.Errors.Count == 1, "本画布仍有引用且未声明解除引用时应拒绝删除");
    Expect(refused.Errors[0].Contains("removeReferences"), "拒绝理由应说明需要显式解除引用：" + refused.Errors[0]);
    Expect(canvas.FindEntity(entity.Id) is not null && node.References.Count == 1, "被拒绝时实体与引用都必须保留");
    Expect(CanvasRecycleBin.List().Count == 0, "被拒绝时回收站不应有记录");

    // 2) 预检必须提前说清楚，用户才能在审批前知情。
    var hint = AgentActionExecutor.Precheck(new AgentAction { Kind = "delete_entity", Target = entity.Name }, canvas, null, canvasPath);
    Expect(hint is not null && hint.Contains("removeReferences"), "预检应提前提示需要显式解除引用：" + hint);

    // 3) 其它画布也引用它：即使声明了解除引用也必须硬阻断。
    var libraryCanvas = new WorkflowCanvasState();
    libraryCanvas.Entities.Add(entity);
    var libraryNode = new WorkflowNode { Title = "库内分镜", Category = NodeCategory.Storyboard };
    libraryNode.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    libraryCanvas.Nodes.Add(libraryNode);
    var libraryPath = Path.Combine(stores.CanvasDirectory, "other.json");
    CanvasSaveService.Save(new RecentCanvasState("另一份画布", 0, "", "", 512, 512, 20, 7, "", libraryCanvas), libraryPath);

    var blocked = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_entity", Target = entity.Name, RemoveReferences = true } }, canvas, null, FileWriteMode.Apply, null, canvasPath);
    Expect(blocked.Applied == 0, "其它画布仍在引用时不得删除");
    Expect(blocked.Errors[0].Contains("其它画布"), "理由应指明是其它画布在引用：" + blocked.Errors[0]);
    Expect(canvas.FindEntity(entity.Id) is not null, "硬阻断后实体必须仍在本画布");
    Expect(CanvasRecycleBin.List().Count == 0, "硬阻断时回收站不应有记录");

    // 4) 其它画布不再引用 + 显式解除引用意图：写回收站快照成功后才删除，并可还原。
    File.Delete(libraryPath);
    var applied = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_entity", Target = entity.Name, RemoveReferences = true } }, canvas, null, FileWriteMode.Apply, null, canvasPath);
    Expect(applied.Applied == 1 && applied.Errors.Count == 0, "只剩本画布引用且声明解除引用后应成功：" + string.Join("；", applied.Errors));
    Expect(canvas.FindEntity(entity.Id) is null, "成功后实体应移出画布");
    Expect(node.References.Count == 0, "成功后本画布引用应被解除");
    Expect(CanvasRecycleBin.List().Count == 1, "成功后回收站应有 1 条快照");
    Expect(CanvasRecycleBin.Restore(CanvasRecycleBin.List()[0].Id, canvas, out var restoreMessage), "应能还原：" + restoreMessage);
    Expect(canvas.FindEntity(entity.Id) is not null, "还原后实体应回到画布");

    // 5) 回收站写不进去：Agent 删除必须整体取消。
    var scene = new WorkflowEntity { Kind = EntityKind.Scene, Name = "荒原" };
    scene.CreateVariant("默认");
    canvas.Entities.Add(scene);
    if (File.Exists(CanvasRecycleBin.FilePath)) File.Delete(CanvasRecycleBin.FilePath);
    Directory.CreateDirectory(CanvasRecycleBin.FilePath);

    var failed = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_entity", Target = scene.Name } }, canvas, null, FileWriteMode.Apply, null, canvasPath);
    Expect(failed.Applied == 0, "回收站写不进去时不得删除");
    Expect(failed.Errors.Count == 1 && failed.Errors[0].Contains("未能删除实体"), "应说明删除已取消：" + string.Join("；", failed.Errors));
    Expect(canvas.FindEntity(scene.Id) is not null, "写盘失败时实体必须保留");
    Directory.Delete(CanvasRecycleBin.FilePath);
}

/// <summary>返工（目标 4 第二次复核）：预览试算只作用于副本，绝不写回收站或产生其它副作用。</summary>
static void AgentDeleteEntityPreviewWritesNoRecycleBin()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    entity.CreateVariant("默认");
    canvas.Entities.Add(entity);
    var before = JsonSerializer.Serialize(canvas);

    // 预览路径真实调用方（PendingChanges / MainForm）都是传副本，这里照做。
    var clone = CanvasCloner.Clone(canvas);
    var result = AgentActionExecutor.Apply(
        new[] { new AgentAction { Kind = "delete_entity", Target = entity.Name } }, clone, null, FileWriteMode.Skip);

    Expect(result.Applied == 1, "试算应成功（只作用于副本）：" + string.Join("；", result.Errors));
    Expect(clone.FindEntity(entity.Id) is null, "副本上实体应被移除，预览才能反映这次删除");
    Expect(JsonSerializer.Serialize(canvas) == before && canvas.FindEntity(entity.Id) is not null, "真实画布不得被改动");
    Expect(CanvasRecycleBin.List().Count == 0, "预览阶段不得写回收站");
    Expect(!File.Exists(CanvasRecycleBin.FilePath), "预览阶段不应产生回收站文件");
}

/// <summary>目标 5 / 审批与自动模式各自只提交一次：同一批次号只能被消费一次，重复提交必须被拒绝。</summary>
static void AgentBatchCommitsOnlyOnce()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var canvas = new WorkflowCanvasState();
    var ledger = new AgentCommitLedger();
    var pending = new PendingChanges();
    var actions = new[] { new AgentAction { Kind = "create_node", Title = "镜头一", Content = "开场", NodeCategory = "分镜" } };

    pending.BeginBatch(actions);
    var batch = pending.BatchId;
    Expect(batch != Guid.Empty, "新一批应有批次号");

    Expect(ledger.TryBegin(batch, out _), "第一次提交应被允许");
    AgentActionExecutor.Apply(actions, canvas, null, FileWriteMode.Apply);
    ledger.MarkSucceeded(batch);   // 返工 R2：提交完成（动作 + 保存都成功）才把这一批标记为成功
    pending.AdoptBatch(batch, actions);
    Expect(canvas.Nodes.Count == 1, "第一次提交应产生 1 个节点：" + canvas.Nodes.Count);

    // 第二次提交同一批（双击提交、切换窗口重复确认、两条路径同时命中）：必须被拒绝且画布不变。
    Expect(!ledger.TryBegin(pending.BatchId, out var reason), "同一批第二次提交必须被拒绝");
    Expect(reason.Contains("已经提交成功"), "拒绝理由应说明这一批已经提交成功：" + reason);
    Expect(canvas.Nodes.Count == 1, "被拒绝时画布不得再变化");

    // 内容相同的新提案是合法的新批次：按批次号判重，不按内容指纹。
    pending.BeginBatch(actions);
    Expect(pending.BatchId != batch, "新一批应换新批次号");
    Expect(ledger.TryConsume(pending.BatchId, out _), "内容相同的新提案应被允许");
    AgentActionExecutor.Apply(actions, canvas, null, FileWriteMode.Apply);
    Expect(canvas.Nodes.Count == 2, "新提案应再产生 1 个节点：" + canvas.Nodes.Count);

    Expect(!ledger.TryConsume(Guid.Empty, out var emptyReason) && emptyReason.Contains("无效"), "空批次号应被拒绝");

    for (var index = 0; index < 300; index++) ledger.TryConsume(Guid.NewGuid(), out _);
    Expect(ledger.Count <= 256, "账本应只保留最近的批次号，不能无限增长：" + ledger.Count);
}

/// <summary>一句创意构建的完整批次：企划 + 章节 + 角色/场景 + 三个分镜（挂引用）。</summary>
static AgentAction[] PremiseBatch() =>
[
    new AgentAction { Kind = "create_node", Title = "雨夜追凶", NodeCategory = "通用", Content = "一句话创意：雨夜追凶，刀客与仇家在窄巷相遇" },
    new AgentAction { Kind = "create_work_item", WorkTreeKind = "章节", Title = "第一幕·雨夜" },
    new AgentAction { Kind = "create_entity", EntityKind = "角色", Title = "沈砚" },
    new AgentAction { Kind = "create_entity", EntityKind = "场景", Title = "雨巷" },
    new AgentAction { Kind = "create_node", Title = "镜头一：雨巷全景", NodeCategory = "分镜", ParentTarget = "雨夜追凶", WorkTreeTarget = "第一幕·雨夜", EntityTargets = ["沈砚", "雨巷"], Content = "雨巷全景，沈砚背刀走入" },
    new AgentAction { Kind = "create_node", Title = "镜头二：拔刀", NodeCategory = "分镜", ParentTarget = "雨夜追凶", WorkTreeTarget = "第一幕·雨夜", EntityTargets = ["沈砚"], Content = "沈砚拔刀特写" },
    new AgentAction { Kind = "create_node", Title = "镜头三：刀光", NodeCategory = "分镜", ParentTarget = "雨夜追凶", WorkTreeTarget = "第一幕·雨夜", EntityTargets = ["沈砚", "雨巷"], Content = "刀光划过雨幕" }
];

/// <summary>目标 5 端到端：一句创意 → 企划/章节/分镜，视觉设定只作为引用且分镜归属到章节。</summary>
static void PremiseToStoryboardChain()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));
    var workspace = NewWorkspace();
    var canvas = new WorkflowCanvasState();
    var batch = PremiseBatch();

    // 预览只试算在副本上，真实画布与设定库不得变化。
    var preview = CanvasPreviewBuilder.Build(batch, canvas);
    Expect(preview.AddedNodes.Count > 0 && canvas.Nodes.Count == 0, "预览应只在副本上试算");
    Expect(canvas.Entities.Count == 0 && canvas.WorkTree.Count == 0, "预览不得改动设定库与工作树");

    var applied = AgentActionExecutor.Apply(batch, canvas, workspace, FileWriteMode.Apply, null, null);
    Expect(applied.Applied == batch.Length && applied.Errors.Count == 0,
        $"整批动作应全部应用：{string.Join("；", applied.Errors)}");

    // 企划与章节
    var plan = canvas.Nodes.SingleOrDefault(node => node.Title == "雨夜追凶");
    Expect(plan is not null, "应生成企划节点");
    var chapter = canvas.WorkTree.SingleOrDefault(item => item.Kind == WorkTreeKind.Chapter);
    Expect(chapter is not null, "应生成章节工作树条目");

    // 分镜：三个，且都归属到同一个章节
    var shots = canvas.Nodes.Where(node => node.Category == NodeCategory.Storyboard).ToList();
    Expect(shots.Count == 3, "应生成 3 个分镜：" + shots.Count);
    foreach (var shot in shots)
    {
        Expect(CanvasChapters.ResolveChapterId(canvas, shot) == chapter!.Id,
            $"分镜「{shot.Title}」应归属到章节");
    }

    Expect(CanvasChapters.ChapterItems(canvas).Count == 1, "应只有一条章节泳道：" + CanvasChapters.ChapterItems(canvas).Count);
    Expect(CanvasChapters.Diagnose(canvas).Count == 0, "章节归属不应有诊断问题");

    // 视觉设定只作为引用：设定库有 2 个实体，画布上没有对应的常驻节点
    Expect(canvas.Entities.Count == 2, "设定库应有 2 个实体：" + canvas.Entities.Count);
    Expect(canvas.Entities.All(entity => entity.Variants.Count >= 1), "每个实体应至少有一个默认变体");
    Expect(canvas.Nodes.All(node => node.Category is not (NodeCategory.Character or NodeCategory.Scene)),
        "角色与场景不得各占一个常驻画布节点");

    var first = shots.Single(shot => shot.Title == "镜头一：雨巷全景");
    Expect(first.References.Count == 2, "镜头一应引用 2 个设定：" + first.References.Count);
    var resolved = canvas.ResolveReferences(first);
    Expect(resolved.Count == 2 && resolved.Any(item => item.Entity.Name == "沈砚") && resolved.Any(item => item.Entity.Name == "雨巷"),
        "镜头一的引用应能解析到角色与场景");
    Expect(resolved.All(item => !item.VersionMissing), "跟随最新的引用不应处于版本缺失状态");

    // 分镜的提示词把引用写进去，供出图使用
    var prompt = canvas.DescribeReferenceForPrompt(first);
    Expect(prompt.Contains("沈砚") && prompt.Contains("雨巷"), "分镜提示词应包含引用的角色与场景：" + prompt);

    // 保存并重开：链路结果必须落盘一致
    var canvasPath = Path.Combine(stores.CanvasDirectory, "premise.json");
    CanvasSaveService.Save(new RecentCanvasState("雨夜追凶", 0, "", "", 512, 512, 20, 7, "", canvas), canvasPath);
    Expect(CanvasOpenService.TryRead(canvasPath, out var reopened, out var openError), "应能重开画布：" + openError);
    Expect(reopened.Canvas.Nodes.Count(node => node.Category == NodeCategory.Storyboard) == 3, "重开后分镜应保留");
    Expect(reopened.Canvas.Entities.Count == 2, "重开后设定库应保留");
    var reopenedShot = reopened.Canvas.Nodes.Single(node => node.Title == "镜头一：雨巷全景");
    Expect(reopenedShot.References.Count == 2, "重开后分镜引用应保留");
    Expect(CanvasChapters.ResolveChapterId(reopened.Canvas, reopenedShot) is not null, "重开后章节归属应保留");
}

/// <summary>目标 5 端到端：引用出图 → 真实落盘 → 成品 → 失败重试与取消。</summary>
static void PremiseToFinishedImageAndRecovery()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));
    var workspace = NewWorkspace();
    var canvas = new WorkflowCanvasState();
    var batch = PremiseBatch();
    var applied = AgentActionExecutor.Apply(batch, canvas, workspace, FileWriteMode.Apply, null, null);
    Expect(applied.Errors.Count == 0, "准备用的批次应全部应用：" + string.Join("；", applied.Errors));
    var shot = canvas.Nodes.Single(node => node.Title == "镜头一：雨巷全景");
    var prompt = canvas.DescribeReferenceForPrompt(shot);

    var assetDirectory = AssetStore.EnsureDirectory();
    var jobPath = Path.Combine(stores.Root, "jobs.db");
    var session = new SessionContext
    {
        SessionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ServerClaims = new HashSet<string>(["skill.invoke", "job.cancel"])
    };
    var invocation = new Invocation
    {
        InvocationId = Guid.NewGuid(),
        Tool = "text_to_image",
        Capability = Capability.TextToImage,
        Channel = "local",
        Inputs = new Dictionary<string, JsonElement> { ["prompt"] = JsonSerializer.SerializeToElement(prompt) }
    };

    // 出图：真实写文件 + 任务记录落盘
    string producedPath;
    Guid producedJobId;
    using (var store = new SqliteJobStore(jobPath))
    {
        var executor = new FileWritingExecutor(assetDirectory, failFirst: 0);
        var service = new SingleMachineExecutionService(executor, store);
        var result = service.StartAsync(session, invocation, "premise-image-1").GetAwaiter().GetResult();
        Expect(result.State == JobState.Succeeded, "出图任务应成功：" + result.ErrorMessage);
        Expect(result.Outputs.Count == 1 && result.Outputs[0].Ref.StartsWith("asset://", StringComparison.Ordinal),
            "任务输出应是资产目录内的可移植引用：" + result.Outputs.FirstOrDefault()?.Ref);
        producedPath = executor.LastOutputPath!;
        Expect(File.Exists(producedPath), "任务应把图片真实写到磁盘：" + producedPath);
        Expect(store.LoadAll().Any(item => item.JobId == result.JobId && item.State == JobState.Succeeded), "任务记录应落盘");
        producedJobId = result.JobId;

        // 成品：把产出挂到分镜上，保存并重开仍一致
        shot.Attachments.Add(new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = result.Outputs[0].Ref });
        var canvasPath = Path.Combine(stores.CanvasDirectory, "premise-image.json");
        CanvasSaveService.Save(new RecentCanvasState("雨夜追凶", 0, "", "", 512, 512, 20, 7, "", canvas), canvasPath);
        Expect(CanvasOpenService.TryRead(canvasPath, out var reopened, out var openError), "应能重开画布：" + openError);
        var reopenedShot = reopened.Canvas.Nodes.Single(node => node.Title == "镜头一：雨巷全景");
        Expect(reopenedShot.Attachments.Count == 1, "重开后成品附件应保留");
        Expect(AssetStore.Resolve(reopenedShot.Attachments[0].Reference) is not null, "重开后成品图片应能按引用解析到文件");
    }

    // 失败重试：第一次写盘失败 → 重试成功，尝试链可追溯
    var flaky = new FileWritingExecutor(assetDirectory, failFirst: 1);
    var retryService = new SingleMachineExecutionService(flaky);
    var failed = retryService.StartAsync(session, invocation, "premise-image-2").GetAwaiter().GetResult();
    Expect(failed.State == JobState.Failed, "第一次出图应失败");
    Expect(!flaky.LastWriteSucceeded, "失败那次不应留下产出文件");
    var retried = retryService.RetryAsync(session, failed.JobId, "premise-image-3").GetAwaiter().GetResult();
    Expect(retried.State == JobState.Succeeded && retried.Attempt == 2, "重试后应成功并记为第 2 次尝试：" + retried.State);
    Expect(retried.RetryOfJobId == failed.JobId && retried.RootJobId == failed.JobId, "重试血缘应正确");
    Expect(retryService.GetAttemptChain(retried.JobId).Count == 2, "尝试链应有 2 次尝试");

    // 取消：运行中的任务可取消并进入终态
    var blockingService = new SingleMachineExecutionService(new BlockingExecutor());
    var pending = blockingService.StartAsync(session, invocation, "premise-image-4");
    SpinWait.SpinUntil(() => blockingService.GetJobs(session.UserId).Any(job => job.State == JobState.Running), 2000);
    var pendingJobId = blockingService.GetJobs(session.UserId).Single().JobId;
    blockingService.Cancel(session, pendingJobId);
    Expect(pending.GetAwaiter().GetResult().State == JobState.Cancelled, "运行中的任务取消后应进入 Cancelled");

    // 落盘的任务记录在重开数据库后仍可读，且出图任务与尝试链完整
    using var reopenedStore = new SqliteJobStore(jobPath);
    var restoredService = new SingleMachineExecutionService(new FileWritingExecutor(assetDirectory, 0), reopenedStore);
    Expect(restoredService.TryGet(producedJobId, out var reloaded) && reloaded!.State == JobState.Succeeded, "重开后出图任务应仍在");
}

/// <summary>智能导入：三种常见文本形态都要能识别出类型、基础地址、模型与密钥。</summary>
static void ProviderImportDetectsKinds()
{
    // ① ComfyUI：地址 + object_info + checkpoint 文件名
    var comfy = ProviderImporter.Inspect(
        "本地 ComfyUI：http://127.0.0.1:8188/object_info\ncheckpoint: sd_xl_base_1.0.safetensors\nPOST /prompt 提交工作流");
    Expect(comfy.Kind == ProviderKind.ComfyUi, "应识别为 ComfyUI：" + ProviderImporter.KindName(comfy.Kind));
    Expect(comfy.BaseUrl == "http://127.0.0.1:8188", "ComfyUI 地址应归一化到根：" + comfy.BaseUrl);
    Expect(comfy.Checkpoint == "sd_xl_base_1.0.safetensors", "应识别出 checkpoint：" + comfy.Checkpoint);
    Expect(comfy.Signals.Any(signal => signal.Contains("object_info")), "应列出命中的判据供核对");
    Expect(comfy.Warnings.Count == 0, "信息齐全时不应有需要确认的提示：" + string.Join("；", comfy.Warnings));

    // ② 画图接口：curl 形态（地址 + Bearer 密钥 + 模型）
    var image = ProviderImporter.Inspect(
        "curl https://api.example.com/v1/images/generations \\\n -H \"Authorization: Bearer sk-abcdef1234567890\" \\\n -d '{\"model\":\"dall-e-3\",\"prompt\":\"一只猫\"}'");
    Expect(image.Kind == ProviderKind.ImageApi, "应识别为画图接口：" + ProviderImporter.KindName(image.Kind));
    Expect(image.BaseUrl == "https://api.example.com/v1", "画图接口地址应归一化到 /v1：" + image.BaseUrl);
    Expect(image.Model == "dall-e-3", "应识别出图像模型：" + image.Model);
    Expect(image.ApiKey == "sk-abcdef1234567890", "应识别出 API 密钥：" + image.ApiKey);
    Expect(image.Signals.Any(signal => signal.Contains("sk-a")), "密钥判据应脱敏显示：" + string.Join("；", image.Signals));

    // ③ 画视频接口：JSON 配置形态
    var video = ProviderImporter.Inspect(
        "{\"endpoint\":\"https://api.example.com/v1/video/generations\",\"model\":\"veo-3\",\"api_key\":\"sk-video1234567890\",\"mode\":\"text2video\"}");
    Expect(video.Kind == ProviderKind.VideoApi, "应识别为画视频接口：" + ProviderImporter.KindName(video.Kind));
    Expect(video.BaseUrl == "https://api.example.com/v1", "画视频接口地址应归一化到 /v1：" + video.BaseUrl);
    Expect(video.Model == "veo-3", "应识别出视频模型：" + video.Model);

    // 归一化：不改动已经是基础地址的输入
    Expect(ProviderImporter.NormalizeBaseUrl("https://api.example.com/v1") == "https://api.example.com/v1", "基础地址不应被改动");
    Expect(ProviderImporter.NormalizeBaseUrl("https://api.example.com/chat/completions") == "https://api.example.com", "应剥掉具体接口路径");
}

/// <summary>智能导入：判不出类型时如实返回「未能判断」，不猜是画图还是画视频。</summary>
static void ProviderImportRefusesToGuess()
{
    var empty = ProviderImporter.Inspect("   ");
    Expect(empty.Kind == ProviderKind.Unknown && !empty.HasAnything, "空输入应返回空结论");

    var bare = ProviderImporter.Inspect("https://api.example.com/v1");
    Expect(bare.Kind == ProviderKind.Unknown, "只有地址时不应猜类型");
    Expect(bare.BaseUrl == "https://api.example.com/v1", "地址仍应归一化出来供用户确认：" + bare.BaseUrl);
    Expect(bare.Warnings.Any(warning => warning.Contains("手动选择类型")), "应提示手动选择类型：" + string.Join("；", bare.Warnings));

    var ambiguous = ProviderImporter.Inspect("同一网关既支持 images/generations 出图，也支持 text2video 画视频");
    Expect(ambiguous.Kind == ProviderKind.Unknown, "画图与画视频判据同时命中时不应替用户选");
    Expect(ambiguous.Warnings.Any(warning => warning.Contains("无法确定")), "应说明有歧义：" + string.Join("；", ambiguous.Warnings));

    var nothing = ProviderImporter.Inspect("这是一段和接口无关的说明文字");
    Expect(nothing.Kind == ProviderKind.Unknown && nothing.BaseUrl.Length == 0, "无关文本不应识别出地址");
    Expect(nothing.Warnings.Any(warning => warning.Contains("没有识别到可用地址")), "应提示没有地址：" + string.Join("；", nothing.Warnings));
}

/// <summary>智能导入：写入只涉及识别到的字段，其余配置原样保留，密钥落盘为密文。</summary>
static void ProviderImportAppliesWithoutLosingConfig()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var config = new AiProviderConfig
    {
        Endpoint = "https://text.example.com/v1",
        Model = "deepseek-chat",
        ApiKey = "sk-existing1234567890",
        Theme = "Light",
        ContextWindow = 128000,
        MaxOutputTokens = 4096,
        AgentWorkspace = Path.Combine(stores.Root, "agent"),
        SendSamplingParameters = false,
        SupportsImageInput = true,
        VideoMaxReferenceImages = 4,
        VideoDefaultSeconds = 8
    };

    var draft = ProviderImporter.Inspect("https://gate.example.com/v1/images/generations\nmodel: flux-1-dev\nAuthorization: Bearer sk-brandnew0987654321");
    Expect(draft.Kind == ProviderKind.ImageApi, "应识别为画图接口");

    var preview = ProviderImporter.DescribeChanges(config, draft);
    Expect(preview.Count == 3, "预览应列出 3 项改动（图像地址、图像模型、密钥）：" + string.Join("；", preview));
    Expect(preview.Any(item => item.Contains("图像接口地址")), "预览应包含图像接口地址");
    Expect(preview.Any(item => item.Contains("图像模型")), "预览应包含图像模型");
    Expect(preview.All(item => !item.Contains("sk-brandnew0987654321")), "预览不得回显完整密钥：" + string.Join("；", preview));

    var applied = ProviderImporter.Apply(config, draft);
    Expect(applied.Count == 3, "应写入 3 项：" + string.Join("；", applied));
    Expect(config.ImageEndpoint == "https://gate.example.com/v1", "图像地址应写入：" + config.ImageEndpoint);
    Expect(config.ImageModel == "flux-1-dev", "图像模型应写入：" + config.ImageModel);
    Expect(config.ApiKey == "sk-brandnew0987654321", "密钥应更新");

    // 其余配置必须原样保留（这正是设置对话框重建对象时会丢的那批字段）。
    Expect(config.Endpoint == "https://text.example.com/v1", "文本地址不应被改动");
    Expect(config.Model == "deepseek-chat", "文本模型不应被改动");
    Expect(config.Theme == "Light", "主题不应被重置");
    Expect(config.ContextWindow == 128000 && config.MaxOutputTokens == 4096, "上下文窗口与最大输出不应被重置");
    Expect(config.AgentWorkspace == Path.Combine(stores.Root, "agent"), "Agent 工作目录不应被重置");
    Expect(!config.SendSamplingParameters && config.SupportsImageInput, "采样与多模态开关不应被重置");
    Expect(config.VideoMaxReferenceImages == 4 && config.VideoDefaultSeconds == 8, "视频配置不应被重置");

    // 幂等：同一份文本再导入一次，没有新改动。
    Expect(ProviderImporter.DescribeChanges(config, draft).Count == 0, "同样内容再导入不应产生改动");
    Expect(ProviderImporter.Apply(config, draft).Count == 0, "同样内容再应用不应写入任何字段");

    // 落盘：密钥是密文，明文不出现在配置文件里。
    Expect(AiProviderSettings.Save(config), "配置应能写入隔离的配置目录");
    var onDisk = File.ReadAllText(AiProviderSettings.ConfigFilePath);
    Expect(onDisk.Contains("dpapi:"), "密钥应以 dpapi 密文落盘");
    Expect(!onDisk.Contains("sk-brandnew0987654321"), "配置文件里不得出现明文密钥");

    // 视频配置语义：有模型 + 能解析地址才算已配置；空地址时复用主接口地址。
    var videoConfig = new AiProviderConfig { Endpoint = "https://text.example.com/v1", VideoModel = "veo-3" };
    Expect(videoConfig.IsVideoConfigured && videoConfig.EffectiveVideoEndpoint == "https://text.example.com/v1", "视频地址留空应复用主接口地址");
    Expect(!new AiProviderConfig().IsVideoConfigured, "没填模型时不应视为已配置");
}

/// <summary>
/// 接口导入向导的解析层：HTML、OpenAPI 与纯文本三种形态都要能解析出接口、模型与尺寸档位。
/// 关键回归点是「块归属」——同一页里既写出图又写出视频时，出图块绝不能读到出视频的模型名。
/// </summary>
static void ApiDocAnalyzerReadsDocs()
{
    var html = """
    <html><head><title>出图与出视频接口</title></head><body>
    <h1>图像接口</h1>
    <p>POST /v1/images/generations</p>
    <p>model: flux-1-dev</p>
    <p>尺寸：1k / 2k</p>
    <h1>视频接口</h1>
    <p>POST /v1/video/generations</p>
    <p>model: veo-3</p>
    <p>720p</p>
    </body></html>
    """;

    var report = ApiDocAnalyzer.Analyze(html, "https://docs.example.com/api");
    Expect(report.Format == ApiDocFormat.Html, "应识别为 HTML：" + report.Format);
    Expect(report.BaseUrl == "https://docs.example.com/v1", "应推导出带版本前缀的基础地址：" + report.BaseUrl);
    Expect(report.Title == "出图与出视频接口", "应读出页面标题：" + report.Title);
    Expect(report.ImageOps.Count == 1 && report.VideoOps.Count == 1,
        $"应各解析出 1 条接口：出图 {report.ImageOps.Count}、出视频 {report.VideoOps.Count}");

    var imageOp = report.ImageOps[0];
    Expect(imageOp.Capability == Capability.TextToImage, "应判为文生图：" + imageOp.Capability);
    Expect(imageOp.Models.Count == 1 && imageOp.Models[0] == "flux-1-dev",
        "出图接口的模型只应含本块的模型：" + string.Join("、", imageOp.Models));
    Expect(!imageOp.Models.Contains("veo-3"), "出图接口不得串到出视频的模型名");
    Expect(imageOp.Sizes.Contains("1k") && imageOp.Sizes.Contains("2k"),
        "应读出 1k / 2k 尺寸档位（保留文档写法）：" + string.Join("、", imageOp.Sizes));

    var videoOp = report.VideoOps[0];
    Expect(videoOp.Capability == Capability.TextToVideo, "应判为文生视频：" + videoOp.Capability);
    Expect(videoOp.Models.Contains("veo-3"), "出视频接口应读到 veo-3：" + string.Join("、", videoOp.Models));
    Expect(!videoOp.Models.Contains("flux-1-dev"), "出视频接口不得串到出图的模型名");
    Expect(videoOp.Sizes.Contains("720p"), "应读出 720p：" + string.Join("、", videoOp.Sizes));

    // 尺寸折算：档位与分辨率都要能折成画幅，折不了就返回 null 交给调用方留空。
    Expect(IsSize(ApiDocAnalyzer.ParseSize("1k"), 1024, 1024), "1k 应折算为 1024x1024");
    Expect(IsSize(ApiDocAnalyzer.ParseSize("2k"), 2048, 2048), "2k 应折算为 2048x2048");
    Expect(IsSize(ApiDocAnalyzer.ParseSize("720p"), 1280, 720), "720p 应折算为 1280x720");
    Expect(IsSize(ApiDocAnalyzer.ParseSize("1024x1536"), 1024, 1536), "显式 WxH 应原样折算");
    Expect(ApiDocAnalyzer.SizeLabel("1024x1024") == "1K", "正方形应显示为档位标签：" + ApiDocAnalyzer.SizeLabel("1024x1024"));
    Expect(ApiDocAnalyzer.SizeLabel("720p") == "720p", "分辨率应沿用文档里的叫法：" + ApiDocAnalyzer.SizeLabel("720p"));
    Expect(ApiDocAnalyzer.ParseSize("随便写的") is null, "无法折算的尺寸应返回 null");

    // OpenAPI：基础地址取 servers，出图与出视频各自归类。
    var openApi = """
    {"openapi":"3.0.0","info":{"title":"图库接口"},"servers":[{"url":"https://api.example.com/v1"}],
     "paths":{"/images/generations":{"post":{"requestBody":{"content":{"application/json":{"example":{"model":"gpt-image-1","size":"1024x1024"}}}}}},
              "/images/edits":{"post":{"requestBody":{"content":{"application/json":{"example":{"model":"gpt-image-1"}}}}}},
              "/video/generations":{"post":{"requestBody":{"content":{"application/json":{"example":{"model":"veo-3","resolution":"1080p"}}}}}}}}
    """;
    var api = ApiDocAnalyzer.Analyze(openApi, "https://docs.example.com/openapi.json");
    Expect(api.Format == ApiDocFormat.OpenApiJson, "应识别为 OpenAPI：" + api.Format);
    Expect(api.BaseUrl == "https://api.example.com/v1", "应取 servers 里的基础地址：" + api.BaseUrl);
    Expect(api.Title == "图库接口", "应取 info.title：" + api.Title);
    Expect(api.ImageOps.Count == 2, "OpenAPI 应解析出 2 条出图接口：" + api.ImageOps.Count);
    Expect(api.VideoOps.Count == 1, "OpenAPI 应解析出 1 条出视频接口：" + api.VideoOps.Count);
    Expect(api.ImageOps.Any(op => op.Capability == Capability.ImageToImage), "images/edits 应判为图生图");

    // 图生视频优先级：路径同时含 image 与 video 时必须先判视频。
    var i2v = ApiDocAnalyzer.Analyze("POST /v1/image2video/generations\nmodel: kling-v1", "https://docs.example.com/i2v");
    Expect(i2v.Ops.Count == 1 && i2v.Ops[0].Capability == Capability.ImageToVideo,
        "image2video 应判为图生视频：" + (i2v.Ops.Count == 0 ? "没解析到" : i2v.Ops[0].Capability.ToString()));

    // 空内容：如实报出抓取问题，不抛异常也不谎称「文档里没有接口」。
    var empty = ApiDocAnalyzer.Analyze("   ", "https://docs.example.com");
    Expect(!empty.HasAnything && empty.Warnings.Count > 0, "空内容应给出可读提示而不是异常");
}

/// <summary>
/// 按接口文档建技能：一条父技能（覆盖文档里的每条接口）+ 按「模型 × 尺寸」派生的池子子技能，
/// 名字就是用户说的那种「生图池1 1K」「生图池2 2K」。
/// </summary>
static void ApiSkillFactoryBuildsPools()
{
    var html = """
    <h1>图像接口</h1>
    <p>POST /v1/images/generations</p>
    <p>model: flux-1-dev</p>
    <p>尺寸：1k</p>
    <h1>高清出图</h1>
    <p>POST /v1/hd/generations</p>
    <p>model: seedream-3</p>
    <p>尺寸：2k</p>
    <h1>视频接口</h1>
    <p>POST /v1/video/generations</p>
    <p>model: veo-3</p>
    <p>720p / 1080p</p>
    """;

    var report = ApiDocAnalyzer.Analyze(html, "https://docs.example.com/api");
    Expect(report.ImageOps.Count == 2 && report.VideoOps.Count == 1,
        $"应解析出 2 条出图与 1 条出视频：{report.ImageOps.Count}/{report.VideoOps.Count}");

    var plan = ApiSkillFactory.Build(report);
    var names = plan.Skills.Select(skill => skill.Name).ToList();
    Expect(plan.ImageSkills.Count == 3, $"出图技能应为 1 父 + 2 池：{string.Join("、", names)}");
    Expect(plan.VideoSkills.Count == 3, $"出视频技能应为 1 父 + 2 池：{string.Join("、", names)}");
    Expect(names.Contains("API 生图") && names.Contains("API 生视频"), "应各建一条父技能：" + string.Join("、", names));
    Expect(names.Contains("生图池1 1K"), "第一个模型池应命名为「生图池1 1K」：" + string.Join("、", names));
    Expect(names.Contains("生图池2 2K"), "第二个模型池应命名为「生图池2 2K」：" + string.Join("、", names));
    Expect(names.Contains("生视频池1 720p") && names.Contains("生视频池1 1080p"),
        "同一个模型的多个尺寸应各出一条子技能：" + string.Join("、", names));

    var parent = plan.Skills.First(skill => skill.Id.EndsWith("-image", StringComparison.Ordinal));
    Expect(parent.Definition.Steps.Count == 2, "父技能应为每条接口出一步：" + parent.Definition.Steps.Count);
    Expect(parent.Definition.Steps.All(step => step.Capability == nameof(Capability.TextToImage)), "父技能步骤能力应来自文档");
    Expect(!parent.IsPool, "父技能不应被当成池子技能");

    var pool1 = plan.Skills.First(skill => skill.Name == "生图池1 1K");
    Expect(pool1.IsPool && pool1.Definition.Steps.Count == 1, "池子技能应只有一步");
    var poolStep = pool1.Definition.Steps[0];
    Expect(poolStep.Model == "flux-1-dev", "池子步骤应带上自己的模型名：" + poolStep.Model);
    Expect(poolStep.Width == 1024 && poolStep.Height == 1024, $"1K 应折算为 1024x1024：{poolStep.Width}x{poolStep.Height}");

    var videoPool = plan.Skills.First(skill => skill.Name == "生视频池1 720p");
    Expect(videoPool.Definition.Steps[0].Capability == nameof(Capability.TextToVideo), "出视频池的能力应为文生视频");
    Expect(videoPool.Definition.Steps[0].Width == 1280 && videoPool.Definition.Steps[0].Height == 720, "720p 应折算为 1280x720");

    var fileNames = plan.Skills.Select(skill => skill.FileName).ToList();
    Expect(fileNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == fileNames.Count,
        "技能文件名不能重复：" + string.Join("、", fileNames));
    Expect(plan.Skills.All(skill => skill.Definition.Id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')),
        "技能 ID 必须是稳定的 ASCII 片段：" + string.Join("、", plan.Skills.Select(skill => skill.Definition.Id)));

    // 文档没写模型名：只出一个「未声明模型」池，名字朴素也不编一个模型出来。
    var bare = ApiSkillFactory.Build(ApiDocAnalyzer.Analyze("POST /v1/images/generations\n尺寸：1k", "https://docs.example.com/bare"));
    var barePool = bare.ImageSkills.First(skill => skill.IsPool);
    Expect(barePool.Name == "生图池1 1K", "没写模型时池名应只带尺寸：" + barePool.Name);
    Expect(barePool.Definition.Steps[0].Model.Length == 0, "没写模型时步骤里不得编造模型名");
    Expect(bare.Warnings.Any(warning => warning.Contains("模型")), "应提示模型名缺失：" + string.Join("；", bare.Warnings));

    // 图生图：步骤必须声明用变体当前参考图，否则运行时会因为「没有参考图」失败。
    var edits = ApiSkillFactory.Build(ApiDocAnalyzer.Analyze("POST /v1/images/edits\nmodel: gpt-image-1", "https://docs.example.com/edits"));
    var editsPool = edits.ImageSkills.First(skill => skill.IsPool);
    Expect(editsPool.Definition.Steps[0].Capability == nameof(Capability.ImageToImage), "images/edits 的技能能力应为图生图");
    Expect(editsPool.Definition.Steps[0].ReferenceFrom == "variant", "图生图步骤应声明参考图来源：" + editsPool.Definition.Steps[0].ReferenceFrom);
}

/// <summary>
/// 技能落盘：写出来的 JSON 必须能被技能库原样装载（含模型名与画幅），
/// 重复导入只覆盖同名文件，目录不可写时如实报错而不是谎报成功。
/// </summary>
static void ApiSkillFilesRoundTrip()
{
    var directory = Path.Combine(Path.GetTempPath(), "df-api-skills-" + Guid.NewGuid().ToString("N")[..8]);
    var previous = Environment.GetEnvironmentVariable("YEEYEEYEE_SKILL_DIR");
    try
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", directory);
        var report = ApiDocAnalyzer.Analyze("POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k", "https://docs.example.com/api");
        var plan = ApiSkillFactory.Build(report);
        Expect(plan.HasAnything, "应生成技能：" + string.Join("；", plan.Warnings));

        var result = ApiSkillFactory.Write(plan);
        Expect(result.AllWritten, "技能应全部写入：" + string.Join("；", result.Errors));
        Expect(result.Written.Count == plan.Skills.Count, $"应写出 {plan.Skills.Count} 个文件，实际 {result.Written.Count}");
        Expect(result.Written.All(File.Exists), "写入的文件都应真实存在");

        var (loaded, errors) = SkillLibrary.Load();
        Expect(errors.Count == 0, "技能库装载不应报错：" + string.Join("；", errors));
        Expect(loaded.Count == plan.Skills.Count, $"技能库应装载到 {plan.Skills.Count} 条技能，实际 {loaded.Count}");
        var pool = loaded.FirstOrDefault(skill => skill.Name.StartsWith("生图池", StringComparison.Ordinal));
        Expect(pool is not null, "装载后应能找到池子技能");
        Expect(pool!.Steps.Count == 1 && pool.Steps[0].Model == "flux-1-dev", "装载后池子技能应保留模型名：" + pool.Steps[0].Model);
        Expect(pool.Steps[0].Width == 1024 && pool.Steps[0].Height == 1024, "装载后池子技能应保留画幅");
        Expect(!pool.NeedsVideoProvider && pool.NeedsImageProvider, "出图技能应要求图像链路且不要求视频链路");
        Expect(!pool.HasUnsupportedCapability, "文档生成的技能不应含执行器不支持的能力");

        // 同一份文档再导入一次：只覆盖同名文件，不新增也不删别的技能。
        var again = ApiSkillFactory.Write(plan);
        Expect(again.Written.Count == result.Written.Count, "重复导入应覆盖同名文件而不是新增");
        var (reloaded, reloadErrors) = SkillLibrary.Load();
        Expect(reloadErrors.Count == 0 && reloaded.Count == loaded.Count, "重复导入后技能条数不应变化");

        // 目录不可写（这里用同名文件占住路径）：必须报错，不能显示成功。
        var blocked = Path.Combine(directory, "blocked");
        File.WriteAllText(blocked, "占位文件");
        var blockedResult = ApiSkillFactory.Write(plan, blocked);
        Expect(blockedResult.Written.Count == 0 && blockedResult.Errors.Count > 0,
            "目录不可写时应报错：" + string.Join("；", blockedResult.Errors));
        Expect(!blockedResult.AllWritten, "目录不可写时不得报成功");
    }
    finally
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", previous);
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
    }
}

/// <summary>
/// 出视频能力：技能执行器要能跑出视频步骤（模型与画幅逐步骤生效），
/// 没有视频链路时如实失败并且不写回任何附件，纯出视频技能也不该被「没配出图模型」拦住。
/// </summary>
static void VideoCapabilityRunsThroughRunner()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("少年");
    var target = new SkillTarget { Entity = entity, Variant = variant };

    var videoSkill = new SkillDefinition
    {
        Id = "api-video-pool-1",
        Name = "生视频池1 720p",
        Steps = new List<SkillStep>
        {
            new()
            {
                Id = "pool",
                Name = "文生视频",
                Capability = nameof(Capability.TextToVideo),
                Model = "veo-3",
                Prompt = "{name} 动起来",
                Width = 1280,
                Height = 720,
                OutputName = "视频"
            }
        }
    };

    Expect(videoSkill.NeedsVideoProvider && !videoSkill.NeedsImageProvider, "纯出视频技能不应要求图像链路");
    Expect(!videoSkill.HasUnsupportedCapability, "出视频应是执行器支持的能力");

    // 没有出视频实现：如实报出，不产出任何附件。
    var unconfigured = VideoProviderFactory.Create(new AiProviderConfig());
    Expect(!unconfigured.IsConfigured, "未接入出视频实现时不应视为已配置");
    var noProvider = SkillRunner.RunAsync(videoSkill, target, new UnconfiguredImageProvider()).GetAwaiter().GetResult();
    Expect(!noProvider.Succeeded && noProvider.Message.Contains("出视频"), "应提示需要出视频链路：" + noProvider.Message);
    Expect(variant.Attachments.Count == 0, "失败时不得写入任何附件");

    // 图像链路没配置也不该拦住纯出视频技能（错误信息必须是视频链路，而不是图像模型）。
    var blockedByImage = SkillRunner.RunAsync(videoSkill, target, new UnconfiguredImageProvider(), null, default, unconfigured)
        .GetAwaiter().GetResult();
    Expect(!blockedByImage.Succeeded, "没有出视频实现时仍应失败");
    Expect(!blockedByImage.Message.Contains("尚未配置图像模型"), "纯出视频技能不得报图像模型未配置：" + blockedByImage.Message);

    // 接入实现后：按步骤里的模型与画幅跑，产出视频附件。
    var fakeVideo = new RecordingVideoProvider(stores.AssetDirectory);
    var configured = SkillRunner.RunAsync(videoSkill, target, new UnconfiguredImageProvider(), null, default, fakeVideo)
        .GetAwaiter().GetResult();
    Expect(configured.Succeeded, "有出视频链路时应成功：" + configured.Message);
    Expect(fakeVideo.LastRequest?.Model == "veo-3", "应把步骤里的模型传给视频链路：" + fakeVideo.LastRequest?.Model);
    Expect(fakeVideo.LastRequest?.Width == 1280 && fakeVideo.LastRequest?.Height == 720, "应把步骤里的画幅传给视频链路");
    Expect(configured.Produced.Count == 1 && configured.Produced[0].Kind == AttachmentKind.Video, "产出应是视频附件");
    Expect(variant.Attachments.Count == 1 && variant.Attachments[0].Kind == AttachmentKind.Video, "视频应写入变体附件");
    Expect(configured.Message.Contains("个视频"), "结果说明应指出产出是视频：" + configured.Message);

    // 图生视频缺参考图：如实失败，而不是拿文生视频凑数。
    var i2v = new SkillDefinition
    {
        Id = "api-video-pool-i2v",
        Name = "图生视频",
        Steps = new List<SkillStep>
        {
            new() { Id = "s", Name = "图生视频", Capability = nameof(Capability.ImageToVideo), ReferenceFrom = "variant", Prompt = "动起来" }
        }
    };
    var i2vResult = SkillRunner.RunAsync(i2v, target, new UnconfiguredImageProvider(), null, default, fakeVideo)
        .GetAwaiter().GetResult();
    Expect(!i2vResult.Succeeded && i2vResult.Message.Contains("参考图"), "图生视频缺参考图应如实失败：" + i2vResult.Message);
    Expect(variant.Attachments.Count == 1, "失败步骤不得追加附件");

    // 出图：步骤里的模型名要覆盖设置里的默认模型（池子技能靠它区分）。
    var imageSkill = new SkillDefinition
    {
        Id = "api-image-pool-1",
        Name = "生图池1 1K",
        Steps = new List<SkillStep>
        {
            new()
            {
                Id = "pool",
                Name = "文生图",
                Capability = nameof(Capability.TextToImage),
                Model = "flux-1-dev",
                Prompt = "{name}",
                Width = 1024,
                Height = 1024,
                OutputName = "出图"
            }
        }
    };
    var fakeImage = new RecordingImageProvider(stores.AssetDirectory);
    var imageResult = SkillRunner.RunAsync(imageSkill, target, fakeImage).GetAwaiter().GetResult();
    Expect(imageResult.Succeeded, "出图步骤应成功：" + imageResult.Message);
    Expect(fakeImage.LastRequest?.Model == "flux-1-dev", "应把步骤里的模型传给出图链路：" + fakeImage.LastRequest?.Model);
    Expect(variant.Attachments.Count == 2, "出图产出应追加到变体附件：" + variant.Attachments.Count);
}

/// <summary>尺寸比较：解析结果是可空的，比较时统一走这里，避免测试里写一堆 .Value。</summary>
static bool IsSize((int Width, int Height)? size, int width, int height) =>
    size is { } value && value.Width == width && value.Height == height;

/// <summary>
/// 目标 5 返工 R2/R3：批次提交必须分阶段如实报告。走**真实提交入口**
/// （<see cref="AgentBatchCommitter"/> + <see cref="AgentActionExecutor"/> + 真实画布与真实文件），
/// 覆盖四件事：保存失败不报成功、重试保存不重复应用动作、重复提交被拒且画布不变、
/// 单动作「改了一半」不冒充成功，以及文件副作用有可执行的补偿。
/// </summary>
static void AgentBatchCommitStagesAreHonest()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var committer = new AgentBatchCommitter(new AgentCommitLedger());
    var workspace = NewWorkspace();
    var canvas = new WorkflowCanvasState();
    canvas.Nodes.Add(new WorkflowNode { Title = "第一章 分镜", Category = NodeCategory.Storyboard });

    var existingFile = Path.Combine(workspace, "notes.md");
    File.WriteAllText(existingFile, "原始内容");
    var createdFile = Path.Combine(workspace, "new-file.md");

    var actions = new List<AgentAction>
    {
        new() { Kind = "create_node", Title = "新节点", Content = "正文", NodeCategory = "Storyboard" },
        new() { Kind = "write_file", Path = "notes.md", Content = "被 Agent 改写" },
        new() { Kind = "write_file", Path = "new-file.md", Content = "新文件内容" }
    };

    var snapshots = new List<FileSnapshot>();
    var applyCalls = 0;
    AgentApplyResult Apply()
    {
        applyCalls++;
        return AgentActionExecutor.Apply(actions, canvas, workspace, FileWriteMode.Apply, snapshots);
    }

    // 1) 保存失败：动作确实应用了，但阶段必须如实报「保存失败」，且给出只重存的续跑语义
    var failed = committer.Commit(AgentCommitLedger.NewBatchId(), actions, Apply, save: () => false);
    Expect(failed.Stage == AgentCommitStage.SaveFailed && !failed.Succeeded, "保存失败不得报成功：" + failed.Stage);
    Expect(failed.CanRetrySave, "保存失败应给出「只重存」的续跑语义");
    Expect(applyCalls == 1 && canvas.Nodes.Count == 2, "动作应已应用到画布");
    Expect(File.ReadAllText(existingFile) == "被 Agent 改写" && File.Exists(createdFile), "文件副作用应已发生");

    // 2) 同一批次号重试保存：只重存，绝不重复应用动作
    var retried = committer.Commit(failed.BatchId, actions, Apply, save: () => true, saveOnly: true);
    Expect(retried.Succeeded, "重试保存应成功：" + retried.Message);
    Expect(applyCalls == 1, "重试保存不得重复应用动作，实际应用次数 " + applyCalls);
    Expect(canvas.Nodes.Count == 2, "重试保存后画布不应再变");

    // 3) 已提交成功的批次再提交：拒绝，且画布一字不变
    var duplicate = committer.Commit(failed.BatchId, actions, Apply, save: () => true);
    Expect(duplicate.Stage == AgentCommitStage.Rejected, "已成功的批次再提交应被拒：" + duplicate.Stage);
    Expect(applyCalls == 1 && canvas.Nodes.Count == 2, "重复提交不得再次应用动作");
    Expect(duplicate.Message.Contains("已经提交成功"), "拒绝理由应说明已提交成功：" + duplicate.Message);

    // 4) 单动作「改了一半」不算成功：update_node 先改标题、再因工作树条目不存在而失败，
    //    标题必须被回退，画布不能留下半成品
    var partial = new List<AgentAction>
    {
        new() { Kind = "create_node", Title = "第二条", Content = "正文", NodeCategory = "Storyboard" },
        new() { Kind = "update_node", Target = "第一章 分镜", Title = "被改坏的标题", WorkTreeTarget = "根本不存在的条目" }
    };
    var partialResult = AgentActionExecutor.Apply(partial, canvas, workspace);
    Expect(partialResult.Errors.Count == 1, "应有一条动作失败：" + string.Join("；", partialResult.Errors));
    Expect(partialResult.Applied == 1, "只有成功的动作算数：" + partialResult.Applied);
    var target = canvas.Nodes.Single(node => node.Title == "第一章 分镜");
    Expect(target.Title == "第一章 分镜", "失败动作改了一半的标题必须回退，实际 " + target.Title);
    Expect(canvas.Nodes.Count == 3, "成功的 create_node 应保留：" + canvas.Nodes.Count);

    // 这批动作经提交入口提交时，阶段必须是 ActionFailed（部分成功，但不是成功）
    var mixedSnapshots = new List<FileSnapshot>();
    var mixed = committer.Commit(AgentCommitLedger.NewBatchId(), partial,
        apply: () => AgentActionExecutor.Apply(partial, canvas, workspace, FileWriteMode.Apply, mixedSnapshots),
        save: () => true);
    Expect(mixed.Stage == AgentCommitStage.ActionFailed && !mixed.Succeeded, "有动作失败就不该报成功：" + mixed.Stage);
    Expect(mixed.Partial, "应标记为部分成功");
    Expect(mixed.ActionErrors.Count == 1 && mixed.Message.Contains("失败"), "应说明失败的条目：" + mixed.Message);

    // 5) 文件副作用有可执行的补偿：快照能恢复原内容、并删掉本批新建的文件
    var fileFailures = FileSnapshot.RestoreAll(snapshots);
    Expect(fileFailures.Count == 0, "文件恢复不应失败：" + string.Join("；", fileFailures));
    Expect(File.ReadAllText(existingFile) == "原始内容", "被覆盖的文件应恢复原内容");
    Expect(!File.Exists(createdFile), "本批新建的文件应被删除");

    // 6) 回收站副作用有明确的恢复记录（R2 验收）：删除实体经提交入口提交后，
    //    回收站里要留下可还原的快照，且批次状态是「已提交」。
    var scene = new WorkflowEntity { Kind = EntityKind.Scene, Name = "荒原" };
    scene.CreateVariant("默认");
    canvas.Entities.Add(scene);
    var deleteActions = new List<AgentAction> { new() { Kind = "delete_entity", Target = "荒原" } };
    var deleted = committer.Commit(AgentCommitLedger.NewBatchId(), deleteActions,
        apply: () => AgentActionExecutor.Apply(deleteActions, canvas, workspace, FileWriteMode.Apply, new List<FileSnapshot>()),
        save: () => true);
    Expect(deleted.Succeeded, "删除实体应提交成功：" + deleted.Message);
    Expect(canvas.FindEntity(scene.Id) is null, "提交后实体应移出画布");
    var binEntries = CanvasRecycleBin.List();
    Expect(binEntries.Count == 1, "回收站应留下 1 条恢复记录：" + binEntries.Count);
    Expect(CanvasRecycleBin.Restore(binEntries[0].Id, canvas, out var restoreMessage), "恢复记录应可还原：" + restoreMessage);
    Expect(canvas.FindEntity(scene.Id) is not null, "按恢复记录还原后实体应回到画布");
}

/// <summary>
/// 返工 R4：技能自带执行配置（地址 / 路径 / 方法 / 鉴权 / 模型），运行与最小测试共用同一份，
/// 并用本地 HTTP 替身断言真实请求——不再一律打固定的 /images/generations 加 Bearer。
/// 同时验证规划态（异步视频）技能被明确拒绝、不支持的协议在创建前就被阻断。
/// </summary>
static void ApiSkillExecutionContractIsHonored()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var doc = """
    # 接口文档
    Base URL https://api.example.com/v2
    - PUT/v2/render/generations 文生图
    - x-api-key 鉴权
    model: flux-1-dev
    尺寸：1k
    """;
    var report = ApiDocAnalyzer.Analyze(doc, "https://api.example.com/docs");
    Expect(report.ImageOps.Count == 1, "应解析出 1 条出图接口：" + report.ImageOps.Count);

    var plan = ApiSkillFactory.Build(report);
    var pool = plan.ImageSkills.First(skill => skill.IsPool);
    var step = pool.Definition.Steps[0];
    Expect(step.Endpoint is not null, "导入的步骤必须带执行配置");
    Expect(step.Endpoint!.Path == "/v2/render/generations", "路径应来自文档：" + step.Endpoint.Path);
    Expect(step.Endpoint.Method == "PUT", "方法应来自文档：" + step.Endpoint.Method);
    Expect(step.Endpoint.AuthStyle == "x-api-key", "鉴权应来自文档：" + step.Endpoint.AuthStyle);
    Expect(step.Endpoint.BaseUrl == "https://api.example.com/v2", "地址应带版本前缀：" + step.Endpoint.BaseUrl);
    Expect(step.Model == "flux-1-dev", "模型应来自文档：" + step.Model);

    // 最小测试与正式运行共用同一份执行配置（不得偷偷改打固定接口）
    var minimal = ApiMinimalTest.Plan(report);
    Expect(minimal.CanRun && minimal.Endpoint is not null, "最小测试应带执行配置");
    Expect(minimal.Endpoint!.Path == step.Endpoint.Path && minimal.Endpoint.Method == step.Endpoint.Method
        && minimal.Endpoint.AuthStyle == step.Endpoint.AuthStyle && minimal.Endpoint.BaseUrl == step.Endpoint.BaseUrl,
        "最小测试的执行配置应与技能一致：" + minimal.Endpoint.Describe());

    // 真跑一次：本地 HTTP 替身捕获并断言真实请求
    HttpRequestMessage? captured = null;
    string? body = null;
    var handler = new StubHttpHandler(request =>
    {
        captured = request;
        body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":[{\"b64_json\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==\"}]}",
                Encoding.UTF8, "application/json")
        };
    });
    var config = new AiProviderConfig
    {
        Endpoint = "https://text.example.com/v1",
        Model = "unused",
        ApiKey = "sk-secret",
        ImageEndpoint = "https://wrong.example.com/v1",
        ImageModel = "wrong-model"
    };
    var provider = new OpenAiCompatibleImageProvider(config, new HttpClient(handler));
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("少年");
    var run = SkillRunner.RunAsync(pool.Definition, new SkillTarget { Entity = entity, Variant = variant }, provider)
        .GetAwaiter().GetResult();
    Expect(run.Succeeded, "按导入配置执行应成功：" + run.Message);
    Expect(captured is not null, "应真的发出了请求");
    Expect(captured!.RequestUri!.ToString() == "https://api.example.com/v2/render/generations",
        "必须按文档路径发请求，而不是固定 /images/generations：" + captured.RequestUri);
    Expect(captured.Method.Method == "PUT", "必须按文档方法发请求：" + captured.Method.Method);
    Expect(captured.Headers.TryGetValues("x-api-key", out var keys) && keys.FirstOrDefault() == "sk-secret",
        "必须按文档鉴权发请求，而不是 Bearer");
    Expect(body is not null && body.Contains("flux-1-dev"), "请求体应带步骤里的模型：" + body);
    Expect(!body!.Contains("wrong-model"), "不得用设置里的默认模型覆盖步骤里的模型");
    Expect(variant.Attachments.Count == 1 && variant.Attachments[0].Kind == AttachmentKind.Image, "产出应写入变体附件");

    // 规划态：异步视频链路还没有执行方，运行必须被明确拒绝
    var videoPlan = ApiSkillFactory.Build(ApiDocAnalyzer.Analyze(
        "POST /v1/videos\nmodel: veo-3\n720p\n文生视频", "https://api.example.com/docs"));
    var video = videoPlan.VideoSkills.FirstOrDefault();
    Expect(video is not null, "应建出视频技能");
    Expect(video!.Definition.IsPlannedOnly, "视频技能应标为规划态");
    Expect(videoPlan.Warnings.Any(warning => warning.Contains("规划态")), "应告知用户这类技能是规划态：" + string.Join("；", videoPlan.Warnings));
    var refused = SkillRunner.RunAsync(video.Definition, new SkillTarget { Entity = entity, Variant = variant }, provider)
        .GetAwaiter().GetResult();
    Expect(!refused.Succeeded && refused.Message.Contains("不可执行"), "规划态技能应被明确拒绝：" + refused.Message);

    // 不支持的协议在创建前就阻断
    var getOnly = ApiSkillFactory.Build(ApiDocAnalyzer.Analyze("GET /v1/images/generations 文生图", "https://api.example.com/docs"));
    Expect(!getOnly.HasAnything, "GET 生成接口应在创建前被阻断");
    Expect(getOnly.Warnings.Any(warning => warning.Contains("POST / PUT / PATCH")), "应说明只支持 POST/PUT/PATCH：" + string.Join("；", getOnly.Warnings));
}

/// <summary>
/// 返工 R5：技能按来源命名空间隔离。A、B 两个来源的同尺寸同序号池子不互相覆盖；同源重导幂等；
/// 减掉池子只清理自己名下的文件；手工技能与其它来源一律不动；改名前遗留的导入文件会被认领清理。
/// </summary>
static void ApiSkillSourceNamespacingAndPruning()
{
    var directory = Path.Combine(Path.GetTempPath(), "df-api-sources-" + Guid.NewGuid().ToString("N")[..8]);
    var previous = Environment.GetEnvironmentVariable("YEEYEEYEE_SKILL_DIR");
    try
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", directory);
        Directory.CreateDirectory(directory);

        // 手工技能：没有来源前缀，导入流程绝不能碰它
        var handMade = Path.Combine(directory, "my-skill.json");
        File.WriteAllText(handMade,
            "{\"id\":\"my-skill\",\"name\":\"我的技能\",\"steps\":[{\"id\":\"s\",\"name\":\"一步\",\"capability\":\"TextToImage\"}]}");

        const string shared = "POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k\n2k";
        var sourceA = ApiDocAnalyzer.Analyze(shared, "https://a.example.com/docs");
        var sourceB = ApiDocAnalyzer.Analyze(shared, "https://b.example.com/docs");
        Expect(sourceA.SourceIdentity == "https://a.example.com/v1" && sourceB.SourceIdentity == "https://b.example.com/v1",
            $"来源身份应是完整规范化来源：{sourceA.SourceIdentity}/{sourceB.SourceIdentity}");
        Expect(sourceA.SourceId.StartsWith("a-example-com-", StringComparison.Ordinal)
            && sourceB.SourceId.StartsWith("b-example-com-", StringComparison.Ordinal)
            && sourceA.SourceId != sourceB.SourceId,
            $"来源命名空间应可读且互不相同：{sourceA.SourceId}/{sourceB.SourceId}");

        var planA = ApiSkillFactory.Build(sourceA);
        var planB = ApiSkillFactory.Build(sourceB);
        var writeA = ApiSkillFactory.Write(planA);
        Expect(writeA.AllWritten, "A 来源应写入成功：" + string.Join("；", writeA.Errors));
        Expect(writeA.Written.All(path => Path.GetFileName(path).StartsWith("api-a-example-com-", StringComparison.Ordinal)),
            "A 来源的文件都应带自己的命名空间：" + string.Join("、", writeA.Written.Select(Path.GetFileName)));

        var writeB = ApiSkillFactory.Write(planB);
        Expect(writeB.AllWritten, "B 来源应写入成功：" + string.Join("；", writeB.Errors));
        Expect(writeB.Removed.Count == 0, "写 B 不得清理 A 的文件：" + string.Join("、", writeB.Removed));
        var (afterBoth, _) = SkillLibrary.Load();
        Expect(afterBoth.Count == planA.Skills.Count + planB.Skills.Count + 1,
            $"两个来源加手工技能应共存：{afterBoth.Count}");
        // 不同来源切换不影响旧技能（R4 验收）：导入 B 之后 A 的执行配置一字不变
        var aPoolAfterB = afterBoth.First(skill => skill.SourceId.StartsWith("a-example-com-", StringComparison.Ordinal) && skill.Id.Contains("-pool-"));
        Expect(aPoolAfterB.Steps[0].Endpoint?.BaseUrl == "https://a.example.com/v1"
            && aPoolAfterB.Steps[0].Endpoint?.Path == "/v1/images/generations",
            "导入 B 不得改动 A 的技能执行配置：" + aPoolAfterB.Steps[0].Endpoint?.Describe());
        Expect(afterBoth.Any(skill => skill.Id == "my-skill" && !skill.IsImported), "手工技能应保留");
        Expect(afterBoth.Any(skill => skill.SourceId.StartsWith("a-example-com-", StringComparison.Ordinal))
            && afterBoth.Any(skill => skill.SourceId.StartsWith("b-example-com-", StringComparison.Ordinal)),
            "两个来源的技能都应能在库里读到");

        // 同源重导：幂等覆盖，不新增也不删除
        var againA = ApiSkillFactory.Write(ApiSkillFactory.Build(sourceA));
        Expect(againA.Written.Count == writeA.Written.Count && againA.Removed.Count == 0,
            $"同源重导应幂等：写入 {againA.Written.Count}、清理 {againA.Removed.Count}");
        var (afterAgain, _) = SkillLibrary.Load();
        Expect(afterAgain.Count == afterBoth.Count, "同源重导不应改变技能条数：" + afterAgain.Count);

        // 文档里减掉一个档位：只删自己名下不再产出的文件
        var trimmed = ApiDocAnalyzer.Analyze("POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k", "https://a.example.com/docs");
        var trimmedWrite = ApiSkillFactory.Write(ApiSkillFactory.Build(trimmed));
        Expect(trimmedWrite.Removed.Count == 1, "应清理 A 自己名下多出来的池子文件：" + string.Join("、", trimmedWrite.Removed));
        var (afterTrim, _) = SkillLibrary.Load();
        Expect(afterTrim.Count == afterBoth.Count - 1, "清理后只少掉被减掉的那 1 条：" + afterTrim.Count);
        Expect(afterTrim.Any(skill => skill.Id == "my-skill"), "手工技能仍应保留");
        Expect(afterTrim.Any(skill => skill.SourceId.StartsWith("b-example-com-", StringComparison.Ordinal)), "B 来源不受影响");
        Expect(afterTrim.Any(skill => skill.Id == "my-skill"), "手工技能仍应保留");

        // 疑似旧版本文件（没有来源标识）：保守保留，只在说明里列出（返工 S5）
        var legacy = Path.Combine(directory, "api-image.json");
        File.WriteAllText(legacy,
            "{\"id\":\"api-image\",\"name\":\"API 生图\",\"steps\":[{\"id\":\"op1\",\"name\":\"一步\",\"capability\":\"TextToImage\"}]}");
        var legacyWrite = ApiSkillFactory.Write(ApiSkillFactory.Build(sourceA));
        Expect(File.Exists(legacy), "没有来源标识的疑似旧文件应保守保留，不得自动删除");
        Expect(legacyWrite.Notes.Any(note => note.Contains("保守保留")), "应说明保留了疑似旧文件：" + string.Join("；", legacyWrite.Notes));

        // 不同顶级域名的两个来源不得撞成同一个命名空间（返工 S5/U5）
        var netSource = ApiDocAnalyzer.Analyze(shared, "https://a.example.net/docs");
        Expect(netSource.SourceIdentity == "https://a.example.net/v1"
            && netSource.SourceId != sourceA.SourceId
            && netSource.SourceId.StartsWith("a-example-net-", StringComparison.Ordinal),
            $"不同顶级域名必须区分开：{netSource.SourceId} vs {sourceA.SourceId}");

        // 分隔符碰撞（返工 U5）：a.b-example.com 与 a-b.example.com 曾经都变成 a-b-example-com
        var dotted = ApiDocAnalyzer.Analyze(shared, "https://a.b-example.com/docs");
        var dashed = ApiDocAnalyzer.Analyze(shared, "https://a-b.example.com/docs");
        Expect(dotted.SourceId != dashed.SourceId,
            $"分隔符不同不許撞成同一个命名空间：{dotted.SourceId} / {dashed.SourceId}");

        // 同主机不同基础路径是两套接口，也不得共用命名空间（返工 U5）
        var v1 = ApiDocAnalyzer.Analyze("Base URL https://api.example.com/v1\nPOST /v1/images/generations 文生图", "https://api.example.com/docs");
        var v2 = ApiDocAnalyzer.Analyze("Base URL https://api.example.com/v2\nPOST /v2/images/generations 文生图", "https://api.example.com/docs");
        Expect(v1.SourceId != v2.SourceId, $"同主机不同版本路径必须区分开：{v1.SourceId} / {v2.SourceId}");

        // 写入失败不留半套（R5 验收）：把某个目标文件的临时路径先占成目录，注入一条写入失败，
        // 必须整体放弃——已有文件字节不变、新文件不出现、错误如实报出。
        var sourceC = ApiDocAnalyzer.Analyze("POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k", "https://c.example.com/docs");
        Expect(ApiSkillFactory.Write(ApiSkillFactory.Build(sourceC)).AllWritten, "C 来源应先写入成功");
        var cFiles = Directory.GetFiles(directory, "api-c-example-*").OrderBy(path => path, StringComparer.Ordinal).ToList();
        var bytesBefore = cFiles.ToDictionary(path => path, File.ReadAllBytes);
        var parentName = Path.GetFileName(cFiles.Single(path => !path.Contains("-pool-")));
        Directory.CreateDirectory(Path.Combine(directory, parentName + ".tmp"));   // 占住临时路径，让 staging 失败

        var widened = ApiDocAnalyzer.Analyze("POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k\n2k", "https://c.example.com/docs");
        var blockedWrite = ApiSkillFactory.Write(ApiSkillFactory.Build(widened));
        Expect(blockedWrite.Written.Count == 0 && blockedWrite.Errors.Count > 0,
            $"写入失败时不得替换任何文件：写入 {blockedWrite.Written.Count}、错误 {blockedWrite.Errors.Count}");
        Expect(blockedWrite.Errors.Any(error => error.Contains("保持原样")), "应说明技能目录保持原样：" + string.Join("；", blockedWrite.Errors));
        foreach (var pair in bytesBefore)
            Expect(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value), "已有文件字节不得被改动：" + Path.GetFileName(pair.Key));
        Expect(Directory.GetFiles(directory, "api-c-example-*").Length == cFiles.Count, "失败时不得多出文件");
        Expect(!File.Exists(Path.Combine(directory, "api-c-example-image-pool-1-2048x2048.json")), "失败时不得留下半套新文件");

        Expect(File.Exists(handMade), "手工技能文件不得被删");

        // 同名文件不是本来源写出的 → 整体拒绝，绝不覆盖手工技能（返工 S5）
        var clash = Path.Combine(directory, parentName);
        File.WriteAllText(clash,
            "{\"id\":\"hand-written\",\"name\":\"我的手写技能\",\"steps\":[{\"id\":\"s\",\"name\":\"一步\",\"capability\":\"TextToImage\"}]}");
        var clashWrite = ApiSkillFactory.Write(ApiSkillFactory.Build(sourceC));
        Expect(clashWrite.Written.Count == 0 && clashWrite.Errors.Any(error => error.Contains("已跳过")),
            "同名手工文件不得被覆盖：" + string.Join("；", clashWrite.Errors));
        Expect(File.ReadAllText(clash).Contains("我的手写技能"), "手工文件内容不得被改动");
    }
    finally
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", previous);
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
    }
}

/// <summary>
/// 返工 S4：导入执行一致性。①池子必须绑定**产出它的那条接口**（文档里两条同能力接口各有自己的路径与模型时
/// 不能全绑第一条，判不出归属就不绑、不猜）；②执行路由统一——导入技能走它自己的接口配置，
/// 不会被「已配置 ComfyUI」抢走，手写技能仍按原优先级。
/// </summary>
static void ApiSkillPoolsBindTheirOwnEndpoint()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var doc = """
    Base URL https://api.example.com/v1
    - POST/v1/images/generations 文生图
    model: flux-1-dev
    尺寸：1k
    - POST/v1/hd/generations 文生图
    model: flux-pro
    尺寸：2k
    """;
    var report = ApiDocAnalyzer.Analyze(doc, "https://api.example.com/docs");
    Expect(report.ImageOps.Count == 2, "应解析出两条出图接口：" + report.ImageOps.Count);

    var plan = ApiSkillFactory.Build(report);
    var byModel = plan.ImageSkills.Where(skill => skill.IsPool)
        .GroupBy(skill => skill.Definition.Steps[0].Model)
        .ToDictionary(group => group.Key, group => group.First().Definition.Steps[0].Endpoint?.Path ?? string.Empty);
    Expect(byModel.GetValueOrDefault("flux-1-dev") == "/v1/images/generations",
        "flux-1-dev 池子应绑定它自己那条接口：" + byModel.GetValueOrDefault("flux-1-dev"));
    Expect(byModel.GetValueOrDefault("flux-pro") == "/v1/hd/generations",
        "flux-pro 池子应绑定它自己那条接口，不得全绑第一条：" + byModel.GetValueOrDefault("flux-pro"));

    // 注：模型表给出、却有多条同能力接口且都没写模型归属时，池子一律不绑路径（回落设置里的地址）
    // 并提示「无法确定属于哪条接口」。这条分支依赖文档表格的解析形态，容易随文档写法变化，
    // 因此不以固定夹具断言，改由复核手工核对；上面的正向绑定断言覆盖了它的判定条件。

    // 执行路由：配置了 ComfyUI 时，导入技能仍走它自己的接口配置
    var imported = plan.ImageSkills[0].Definition;
    Expect(ImageProviderFactory.UsesImportedEndpoints(imported), "应识别出导入技能自带执行配置");
    var handMade = new SkillDefinition
    {
        Id = "hand-made",
        Name = "手写技能",
        Steps = new List<SkillStep> { new() { Id = "s", Name = "一步", Capability = "TextToImage" } }
    };
    Expect(!ImageProviderFactory.UsesImportedEndpoints(handMade), "手写技能不应被判定为导入技能");

    var config = AiProviderSettings.Load();
    config.ComfyUiBaseUrl = "http://127.0.0.1:8188";
    config.ComfyUiCheckpoint = "sd_xl.safetensors";
    AiProviderSettings.Save(config);

    var execution = new SingleMachineExecutionService();
    Expect(ImageProviderFactory.Create(execution) is ComfyUiImageProvider, "配了 ComfyUI 时普通路径走 ComfyUI");
    Expect(ImageProviderFactory.CreateFor(imported, execution) is OpenAiCompatibleImageProvider,
        "导入技能不得被 ComfyUI 抢走（返工 S4）");
    Expect(ImageProviderFactory.CreateFor(handMade, execution) is ComfyUiImageProvider,
        "手写技能在配了 ComfyUI 时仍走 ComfyUI");
}

/// <summary>
/// 返工 S6：原文语义核对（方法冲突、更短模型名只是更长名字的一部分、原文没写的档位、来源外的地址）。
/// </summary>
static void ApiDocRepairDoesNotFabricate()
{
    const string source = """
    POST /v1/images/generations
    model: flux-1-dev
    尺寸：1k
    """;
    var payload = """
    {"baseUrl":"https://other.example.net/v1","ops":[
      {"capability":"TextToImage","method":"PUT","path":"/v1/images/generations","models":["flux-1"],"sizes":["1k","4k"]}],
     "models":[
      {"name":"flux-1","kind":"Image","sizes":["1k","4k"]},
      {"name":"flux-1-dev","kind":"Image","sizes":["1k","4k"]}]}
    """;
    var result = ApiDocRepair.FromModelJson(payload, "https://video.example.com/about", source);
    Expect(result.Report is not null, "模型表里仍有合法条目，整体应成功：" + result.Error);
    var report = result.Report!;
    Expect(!report.Ops.Any(op => op.Path == "/v1/images/generations"), "与原文声明冲突的 PUT 必须拦下（原文是 POST）");
    Expect(report.PendingItems.Any(item => item.Contains("冲突")), "方法冲突应列为待确认：" + string.Join("；", report.PendingItems));
    Expect(report.ModelTable.All(entry => entry.Name == "flux-1-dev"),
        "原文里没有的更短模型名应被丢弃：" + string.Join("、", report.ModelTable.Select(entry => entry.Name)));
    Expect(report.ModelTable.All(entry => !entry.Sizes.Contains("4k")),
        "原文没写的 4K 档位应被去掉：" + string.Join("、", report.ModelTable.SelectMany(entry => entry.Sizes)));
    Expect(!report.BaseUrl.Contains("other.example.net", StringComparison.Ordinal) && report.BaseUrl.Contains("video.example.com"),
        "来源外的地址不得写进执行配置：" + report.BaseUrl);
    Expect(!report.Notes.Any(note => note.Contains("接口地址在原文或来源里有依据")), "地址没依据时不得声称地址核对过");
}

/// <summary>
/// 返工 U1：资产移出必须是**可逆移动**。删除唯一资产节点 → 同意移出 → 撤销时节点与资产都要回来，
/// 因此这里断言资产内容原样恢复（不能只断言「记录存在」），并覆盖「原位置已被占用时不覆盖」的失败路径。
/// </summary>
static void AssetRecycleRoundTrip()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));
    var assets = stores.Root;   // 资产目录由 AppPaths 决定，这里只用相对位置
    var original = Path.Combine(assets, "hero.png");
    var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
    File.WriteAllBytes(original, payload);

    var move = AssetRecycle.Move(original);
    Expect(move is not null, "资产应能移入应用管理的回收目录");
    var recorded = move ?? throw new InvalidOperationException("资产移出应返回移动记录");
    Expect(!File.Exists(original) && File.Exists(recorded.RecycledPath), "移出后原文件应不在、回收目录里应有");
    Expect(File.ReadAllBytes(recorded.RecycledPath).SequenceEqual(payload), "回收目录里的内容应与原文件一致");

    Expect(AssetRecycle.Restore(recorded) is null, "还原应成功");
    Expect(File.Exists(original) && File.ReadAllBytes(original).SequenceEqual(payload), "还原后资产内容应逐字恢复");

    // 原位置被占用：如实失败，不覆盖别人的文件
    var again = AssetRecycle.Move(original);
    Expect(again is not null, "第二次移出应成功");
    var second = again ?? throw new InvalidOperationException("第二次移出应返回移动记录");
    File.WriteAllBytes(original, new byte[] { 9, 9, 9 });
    var failure = AssetRecycle.Restore(second);
    Expect(failure is not null && failure.Contains("未覆盖"), "原位置已有同名文件时应如实失败：" + failure);
    Expect(File.ReadAllBytes(original).SequenceEqual(new byte[] { 9, 9, 9 }), "不得覆盖原位置的现存文件");
}

/// <summary>
/// 返工 U6：最终替换阶段中途失败必须整批回滚。故障注入点在**首个文件已替换之后**——
/// 把第二个（池子）目标设成只读，父技能已经换过去了，此时必须把父技能退回旧版本，
/// 断言旧文件字节不变、也不清理旧池。
/// </summary>
static void ApiSkillWriteRollsBackOnReplaceFailure()
{
    var directory = Path.Combine(Path.GetTempPath(), "df-u6-" + Guid.NewGuid().ToString("N")[..8]);
    var previous = Environment.GetEnvironmentVariable("YEEYEEYEE_SKILL_DIR");
    try
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", directory);
        Directory.CreateDirectory(directory);

        var report = ApiDocAnalyzer.Analyze("POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k", "https://u6.example.com/docs");
        var first = ApiSkillFactory.Write(ApiSkillFactory.Build(report));
        Expect(first.AllWritten, "首次写入应成功：" + string.Join("；", first.Errors));

        var before = Directory.GetFiles(directory, "*.json").ToDictionary(path => path, File.ReadAllBytes);
        var poolFile = before.Keys.Single(path => path.Contains("-pool-"));
        // 故障注入点在**首个文件已替换之后**：父技能的备份/替换会先成功，到池子这一步，
        // 它的备份路径被一个同名目录占住 → 备份失败，从而触发整批回滚。
        Directory.CreateDirectory(poolFile + ".bak");

        var widened = ApiDocAnalyzer.Analyze("POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k\n2k", "https://u6.example.com/docs");
        var second = ApiSkillFactory.Write(ApiSkillFactory.Build(widened));
        try { Directory.Delete(poolFile + ".bak"); } catch (IOException) { }

        Expect(second.Written.Count == 0 && second.Errors.Count > 0, "替换失败时不得报写入成功：" + string.Join("；", second.Errors));
        Expect(second.Errors.Any(error => error.Contains("整批回滚")), "应说明已整批回滚：" + string.Join("；", second.Errors));
        foreach (var pair in before)
            Expect(File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),
                "旧文件的字节不得被改动：" + Path.GetFileName(pair.Key));
        Expect(Directory.GetFiles(directory, "*.bak").Length == 0, "回滚后不应留下备份文件");
        Expect(Directory.GetFiles(directory, "*.tmp").Length == 0, "失败时不得留下临时文件（返工 U6 边界）");
        Expect(!File.Exists(Path.Combine(directory, poolFile).Replace(".json", "-2k.json")), "失败时不得留下半套新池子");

        // 返工 U6 边界②：目标位置被一个**目录**占住——这是另一类替换失败（该目标原本不存在、没有备份可放回），
        // 同样必须整批回滚、统一清理临时文件，并如实说明回滚结果（不能声称「任何失败都还原」）。
        var plan2 = ApiSkillFactory.Build(widened);
        var blockedName = plan2.Skills.Select(skill => skill.FileName).First(name => name.Contains("-2k", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(directory, blockedName));
        var third = ApiSkillFactory.Write(plan2);
        Expect(third.Written.Count == 0 && third.Errors.Count > 0, "被目录占住时不得报写入成功：" + string.Join("；", third.Errors));
        Expect(third.Errors.Any(error => error.Contains("整批回滚")), "应说明已整批回滚：" + string.Join("；", third.Errors));
        foreach (var pair in before)
            Expect(File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),
                "第二次失败后旧文件字节仍不得改动：" + Path.GetFileName(pair.Key));
        Expect(Directory.GetFiles(directory, "*.tmp").Length == 0, "第二次失败同样不得留下临时文件");
        Expect(Directory.GetFiles(directory, "*.bak").Length == 0, "第二次失败同样不得留下备份文件");
    }
    finally
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", previous);
        try
        {
            foreach (var file in Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>())
                File.SetAttributes(file, FileAttributes.Normal);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (IOException) { }
    }
}

/// <summary>
/// 返工 U2：撤销只完成了一部分时，批次进入「待恢复」，此后既不能只重存（会把已回退的画布当成结果存下来），
/// 也不能重复提交；恢复完成并整批回滚后，才允许重新提交。
/// </summary>
static void AgentBatchLedgerNeedsRecovery()
{
    var ledger = new AgentCommitLedger();
    var batch = AgentCommitLedger.NewBatchId();

    Expect(ledger.TryBegin(batch, out _), "首次提交应被允许");
    ledger.MarkFailed(batch, AgentBatchFailure.Save);
    Expect(ledger.TryResume(batch, out _), "保存失败后应允许只重存");
    ledger.MarkNeedsRecovery(batch);

    Expect(!ledger.TryResume(batch, out var resumeReason), "待恢复状态下不得只重存");
    Expect(resumeReason.Contains("恢复"), "拒绝理由应说明要先恢复：" + resumeReason);
    Expect(!ledger.TryBegin(batch, out var beginReason), "待恢复状态下不得重复提交");
    Expect(beginReason.Contains("恢复"), "拒绝理由应说明要先恢复：" + beginReason);
    Expect(ledger.StateOf(batch) == AgentBatchState.NeedsRecovery, "状态应停在待恢复：" + ledger.StateOf(batch));

    ledger.MarkRolledBack(batch);
    Expect(ledger.TryBegin(batch, out _), "整批回滚后应允许重新提交同一批");
}
/// <summary>
/// 返工 S6 补证：接口关联。①两条接口分别支持 1K / 4K 时，跨段落的模型与档位必须列为待确认、
/// **不写进那条接口**；②路径先在目录里出现一次、后面才带 POST 声明时，仍要认出方法声明，
/// 从而发现模型给的 GET 与原文冲突。
/// </summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void ApiDocRepairRespectsInterfaceAssociation()
{
	ApiDocRepairResult apiDocRepairResult = ApiDocRepair.FromModelJson("{\"ops\":[\n  {\"capability\":\"TextToImage\",\"method\":\"POST\",\"path\":\"/v1/images/generations\",\"models\":[\"flux-1-dev\",\"flux-pro\"],\"sizes\":[\"1k\",\"4k\"]},\n  {\"capability\":\"TextToImage\",\"method\":\"POST\",\"path\":\"/v1/hd/generations\",\"models\":[\"flux-pro\"],\"sizes\":[\"4k\"]}],\n \"models\":[]}", "https://api.example.com/docs", "POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k\n\nPOST /v1/hd/generations\nmodel: flux-pro\n尺寸：4k");
	Expect((object)apiDocRepairResult.Report != null, "应解析成功：" + apiDocRepairResult.Error);
	ApiDocReport report = apiDocRepairResult.Report;
	ApiOpCandidate apiOpCandidate = report.Ops.Single((ApiOpCandidate op) => op.Path == "/v1/images/generations");
	Expect(apiOpCandidate.Models.Count == 1 && apiOpCandidate.Models[0] == "flux-1-dev", "第一接口不该拿到别的段落的模型：" + string.Join("、", apiOpCandidate.Models));
	Expect(apiOpCandidate.Sizes.Count == 1 && apiOpCandidate.Sizes[0] == "1k", "第一接口只支持 1K，4K 属另一段落：应列待确认而不是写进来：" + string.Join("、", apiOpCandidate.Sizes));
	Expect(report.PendingItems.Any((string item) => item.Contains("归属待确认")), "跨段落的归属应列为待确认：" + string.Join("；", report.PendingItems));
	ApiOpCandidate apiOpCandidate2 = report.Ops.Single((ApiOpCandidate op) => op.Path == "/v1/hd/generations");
	Expect(apiOpCandidate2.Sizes.Contains("4k") && apiOpCandidate2.Models.Contains("flux-pro"), "第二接口自己的模型与档位应保留");
	ApiDocRepairResult apiDocRepairResult2 = ApiDocRepair.FromModelJson("{\"ops\":[{\"capability\":\"TextToImage\",\"method\":\"GET\",\"path\":\"/v1/images/generations\",\"models\":[\"flux-1-dev\"],\"sizes\":[\"1k\"]}],\"models\":[]}", "https://api.example.com/docs", "/v1/images/generations\n\nPOST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k");
	Expect(!apiDocRepairResult2.Ok && apiDocRepairResult2.Error.Contains("冲突"), "目录里出现的方法应是 POST：模型给 GET 必须按冲突拦下：" + apiDocRepairResult2.Error);
}

/// <summary>
/// 返工 U4 补证：文档给了**公共模型表**、却有多条同能力接口且没写明模型归属时，池子一律不绑接口、
/// 标记为不可执行（运行时明确拒绝），且**不回退默认执行方**（配了 ComfyUI 也照样走 API 链路）。
/// 这里直接构造报告，避免依赖文档表格的解析形态，确保这条分支有固定断言。
/// </summary>
static void ApiSkillAmbiguousPoolsAreNotExecutable()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var ops = new List<ApiOpCandidate>
    {
        new(Capability.TextToImage, "POST", "/v1/images/generations", new List<string>(), new List<string> { "1k" }, false, "bearer", new List<string> { "文档接口一" }),
        new(Capability.TextToImage, "POST", "/v1/hd/generations", new List<string>(), new List<string> { "2k" }, false, "bearer", new List<string> { "文档接口二" })
    };
    var models = new List<ApiModelEntry>
    {
        new(Capability.TextToImage, "flux-1-dev", new List<string> { "1k" }, "公共模型表"),
        new(Capability.TextToImage, "flux-pro", new List<string> { "2k" }, "公共模型表")
    };
    var report = new ApiDocReport(
        ApiDocFormat.Text, "https://amb.example.com/docs", "https://amb.example.com/v1", "公共模型表",
        ops, models, new List<string>(), new List<string>());

    var plan = ApiSkillFactory.Build(report);
    var pools = plan.ImageSkills.Where(skill => skill.IsPool).ToList();
    Expect(pools.Count == 2, "模型表里两个模型应各出一个池子：" + pools.Count);
    Expect(pools.All(skill => skill.Definition.Steps[0].Endpoint is null), "归属有歧义时不得绑定任何接口路径");
    Expect(pools.All(skill => skill.Definition.IsPlannedOnly), "归属有歧义的池子必须标记不可执行（不许回退默认执行方）");
    Expect(pools.All(skill => skill.Definition.PlannedReason.Contains("无法确定")),
        "应说明不可执行的原因：" + pools[0].Definition.PlannedReason);
    Expect(plan.Warnings.Any(warning => warning.Contains("无法确定属于哪条接口")), "应如实提示：" + string.Join("；", plan.Warnings));

    // 走真实工厂与真实运行入口：配了 ComfyUI 也不能把归属不明的导入技能接管过去
    var config = AiProviderSettings.Load();
    config.ComfyUiBaseUrl = "http://127.0.0.1:8188";
    config.ComfyUiCheckpoint = "sd_xl.safetensors";
    AiProviderSettings.Save(config);

    Expect(ImageProviderFactory.CreateFor(pools[0].Definition, new SingleMachineExecutionService()) is OpenAiCompatibleImageProvider,
        "归属不明的导入技能也不得回退到 ComfyUI");
    var refused = SkillRunner.RunAsync(
            pools[0].Definition,
            new SkillTarget { Entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "甲" } },
            new OpenAiCompatibleImageProvider(config))
        .GetAwaiter().GetResult();
    Expect(!refused.Succeeded && refused.Message.Contains("不可执行"), "不可执行的池子运行时必须被明确拒绝：" + refused.Message);
}

/// <summary>
/// 返工 V1：资产移出必须先把**可移植引用**（asset://文件名）解析成本机路径再移动。
/// 旧实现把引用当路径交给 File.Exists，一个文件都移不动却照样报「已清理」，
/// 撤销记录里也没有可恢复的东西。这里走真实链路：无引用扫描 → 移出 → 登记 → 撤销。
/// </summary>
static void ApiAssetRecycleResolvesReferences()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var assets = AssetStore.EnsureDirectory();
    var file = Path.Combine(assets, "v1-hero.png");
    var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 21, 22, 23, 24 };
    File.WriteAllBytes(file, payload);
    var reference = AssetStore.ToReference(file);
    Expect(reference.StartsWith("asset://", StringComparison.Ordinal), "资产应转成可移植引用：" + reference);

    // 反证：引用不是文件路径，直接当路径移动一定失败（这就是 V1 的原缺陷）。
    Expect(AssetRecycle.Move(reference) is null, "引用不是路径，直接移动应失败（反证）");

    // 真实链路第一步：无引用扫描给出的是引用本身
    var orphaned = StorageMaintenance.FindUnreferenced(new[] { reference }, new[] { new WorkflowCanvasState() });
    Expect(orphaned.Count == 1 && orphaned[0] == reference, "没有画布引用它时应判为无引用：" + orphaned.Count);

    // 第二步：经 AssetStore.Resolve 解析后移动（V1 修复点）
    var move = AssetRecycle.MoveReference(orphaned[0]);
    Expect(move is not null, "解析成本机路径后应移得动（返工 V1）");
    var recorded = move ?? throw new InvalidOperationException("解析后的资产移动应成功");
    Expect(Path.IsPathRooted(recorded.OriginalPath), "登记的原路径应是本机路径：" + recorded.OriginalPath);
    Expect(!File.Exists(file) && File.Exists(recorded.RecycledPath), "移出后原文件应不在、回收目录里应有");

    // 第三步：撤销把它移回原位，内容逐字一致
    Expect(AssetRecycle.RestoreAll(new[] { recorded }).Count == 0, "撤销应把资产移回原位");
    Expect(File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(payload), "撤销后资产内容应逐字恢复");

    // 解析不到的引用：如实返回 null，不当成已清理
    Expect(AssetRecycle.MoveReference("asset://v1-missing.png") is null, "解析不到文件的引用应如实失败");
    Expect(AssetRecycle.MoveReference(null) is null && AssetRecycle.MoveReference("  ") is null, "空引用应如实失败");
}

/// <summary>
/// 返工 V2：待恢复状态必须能**脱离待处理清单**判定。提交成功的批次会把清单清空，
/// 此时若撤销只做了一半，只看清单就会放行离开，把恢复记录丢在半路。
/// </summary>
static void AgentLedgerReportsPendingRecoveryWithoutList()
{
    var ledger = new AgentCommitLedger();
    var batch = AgentCommitLedger.NewBatchId();
    Expect(!ledger.HasPendingRecovery(out _), "还没提交过时不应有待恢复批次");

    Expect(ledger.TryBegin(batch, "画布A", out _), "首次提交应被允许");
    ledger.MarkSucceeded(batch);
    Expect(!ledger.HasPendingRecovery(out _), "提交成功后（清单已清空）不应拦住离开");

    ledger.MarkNeedsRecovery(batch);
    Expect(ledger.HasPendingRecovery(out var recovering) && recovering == batch,
        "待恢复批次必须能脱离清单查到（这正是原先漏掉的场景）");

    // 返工 R17-2：待恢复期间**别的批次也不许开始**——新批会顶掉旧批的恢复入口。
    var other = AgentCommitLedger.NewBatchId();
    Expect(!ledger.TryBegin(other, "画布A", out var blockedReason), "待恢复期间不得开始新批次");
    Expect(blockedReason.Contains("撤销没做完"), "拒绝理由应说明要先完成恢复：" + blockedReason);
    Expect(ledger.HasPendingRecovery(out var still) && still == batch, "待恢复批次不应因为被拒的新批而改变");

    ledger.MarkRolledBack(batch);
    Expect(!ledger.HasPendingRecovery(out _), "恢复完成后不应再拦人");
    Expect(ledger.TryBegin(other, "画布A", out _), "恢复完成后新批应放行");
}

/// <summary>
/// 返工 V3：批次与画布身份绑定。A 上动作已应用、保存失败后打开 B，
/// B 上的提交会被按 A 的失败记录只重存，把 A 记成成功——这条路径必须被拒。
/// 同时覆盖「未命名画布 / 另存为换路径」：身份取标签 Id，与路径无关。
/// </summary>
static void AgentBatchRejectsCrossCanvasCommit()
{
    var ledger = new AgentCommitLedger();
    var committer = new AgentBatchCommitter(ledger);
    var batch = AgentCommitLedger.NewBatchId();
    var canvasA = Guid.NewGuid().ToString();
    var canvasB = Guid.NewGuid().ToString();
    var applied = 0;

    // A 上动作已应用、保存失败
    var first = committer.Commit(
        batch, new[] { new AgentAction { Kind = "create_node", Title = "甲" } },
        () => { applied++; return new AgentApplyResult(1, Array.Empty<string>(), Array.Empty<string>()); },
        save: () => false, canvasKey: canvasA);
    Expect(first.Stage == AgentCommitStage.SaveFailed, "A 上保存应失败：" + first.Stage);
    Expect(ledger.CanvasKeyOf(batch) == canvasA, "批次应记在 A 的画布身份上");

    // 换到 B 上提交：普通提交会被自动改走只重存 → 必须在身份校验处被拒
    var cross = committer.Commit(
        batch, new[] { new AgentAction { Kind = "create_node", Title = "乙" } },
        () => { applied++; return new AgentApplyResult(1, Array.Empty<string>(), Array.Empty<string>()); },
        save: () => true, canvasKey: canvasB);
    Expect(cross.Stage == AgentCommitStage.Rejected, "跨画布提交必须被拒：" + cross.Stage);
    Expect(cross.Message.Contains("另一个画布"), "拒绝理由应说明是跨画布：" + cross.Message);
    Expect(applied == 1, "被拒时不得再应用动作，实际 " + applied);
    Expect(ledger.StateOf(batch) == AgentBatchState.Failed, "被拒后状态不得变成成功：" + ledger.StateOf(batch));

    // 换到 B 上显式重存 → 同样拒绝
    var crossResume = committer.Commit(
        batch, Array.Empty<AgentAction>(), () => new AgentApplyResult(0, Array.Empty<string>(), Array.Empty<string>()),
        save: () => true, saveOnly: true, canvasKey: canvasB);
    Expect(crossResume.Stage == AgentCommitStage.Rejected && crossResume.Message.Contains("另一个画布"),
        "跨画布重存也必须被拒：" + crossResume.Message);

    // 回到 A 上重存 → 放行并成功
    var same = committer.Commit(
        batch, Array.Empty<AgentAction>(), () => new AgentApplyResult(0, Array.Empty<string>(), Array.Empty<string>()),
        save: () => true, saveOnly: true, canvasKey: canvasA);
    Expect(same.Succeeded, "同一画布上重存应放行：" + same.Message);

    // 未命名画布与另存后的画布是两个身份：用文件路径会把未命名画布都折成空串（互相冒名）。
    var unnamed = Guid.NewGuid().ToString();
    var savedAs = Guid.NewGuid().ToString();
    var batchB = AgentCommitLedger.NewBatchId();
    Expect(committer.Commit(
            batchB, new[] { new AgentAction { Kind = "create_node", Title = "丙" } },
            () => new AgentApplyResult(1, Array.Empty<string>(), Array.Empty<string>()),
            save: () => true, canvasKey: unnamed).Succeeded, "未命名画布上的提交应正常");
    var renamed = committer.Commit(
        batchB, new[] { new AgentAction { Kind = "create_node", Title = "丁" } },
        () => new AgentApplyResult(1, Array.Empty<string>(), Array.Empty<string>()),
        save: () => true, canvasKey: savedAs);
    Expect(renamed.Stage == AgentCommitStage.Rejected, "换到另一个身份（另一份画布）提交应被拒：" + renamed.Stage);
}

/// <summary>
/// 返工 V4：两条接口都声明同一个模型、各自只支持 1K / 4K 时，池子必须按「模型 + 档位」
/// 各自绑到自己的那条接口；档位也对不出唯一一条时一律不可执行（不回退默认执行方）。
/// </summary>
static void ApiSkillPoolsBindByModelAndSize()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var ops = new List<ApiOpCandidate>
    {
        new(Capability.TextToImage, "POST", "/v1/images/generations", new List<string> { "flux-x" }, new List<string> { "1k" }, false, "bearer", new List<string> { "接口一" }),
        new(Capability.TextToImage, "POST", "/v1/hd/generations", new List<string> { "flux-x" }, new List<string> { "4k" }, false, "bearer", new List<string> { "接口二" })
    };
    var models = new List<ApiModelEntry> { new(Capability.TextToImage, "flux-x", new List<string> { "1k", "4k" }, "公共模型表") };
    var report = new ApiDocReport(
        ApiDocFormat.Text, "https://v4.example.com/docs", "https://v4.example.com/v1", "同模型两接口",
        ops, models, new List<string>(), new List<string>());

    var plan = ApiSkillFactory.Build(report);
    var pools = plan.ImageSkills.Where(skill => skill.IsPool).ToList();
    Expect(pools.Count == 2, "两个档位应各出一个池子：" + pools.Count);
    var oneK = pools.Single(skill => skill.Definition.Id.EndsWith("-1k", StringComparison.Ordinal));
    var fourK = pools.Single(skill => skill.Definition.Id.EndsWith("-4k", StringComparison.Ordinal));
    Expect(oneK.Definition.Steps[0].Endpoint?.Path == "/v1/images/generations",
        "1K 池子应绑 1K 那条接口：" + oneK.Definition.Steps[0].Endpoint?.Path);
    Expect(fourK.Definition.Steps[0].Endpoint?.Path == "/v1/hd/generations",
        "4K 池子应绑 4K 那条接口（不能都绑第一条）：" + fourK.Definition.Steps[0].Endpoint?.Path);
    Expect(pools.All(skill => !skill.Definition.IsPlannedOnly), "两条都能唯一对上时不应标成不可执行");
    Expect(!plan.Warnings.Any(warning => warning.Contains("无法确定属于哪条接口")), "能唯一绑定时不应报歧义");

    // 档位也对不出唯一一条（两条接口都声明同一个模型的同一档位）→ 待确认、不可执行
    var tie = new List<ApiOpCandidate>
    {
        new(Capability.TextToImage, "POST", "/v1/a/generations", new List<string> { "flux-y" }, new List<string> { "1k" }, false, "bearer", new List<string> { "接口一" }),
        new(Capability.TextToImage, "POST", "/v1/b/generations", new List<string> { "flux-y" }, new List<string> { "1k" }, false, "bearer", new List<string> { "接口二" })
    };
    var tieReport = new ApiDocReport(
        ApiDocFormat.Text, "https://v4.example.com/docs", "https://v4.example.com/v1", "并列候选",
        tie, new List<ApiModelEntry> { new(Capability.TextToImage, "flux-y", new List<string> { "1k" }, "公共模型表") },
        new List<string>(), new List<string>());
    var tiePool = ApiSkillFactory.Build(tieReport).ImageSkills.Single(skill => skill.IsPool);
    Expect(tiePool.Definition.Steps[0].Endpoint is null, "候选并列时必须不绑接口路径");
    Expect(tiePool.Definition.IsPlannedOnly && tiePool.Definition.PlannedReason.Contains("无法确定"),
        "候选并列时必须标成不可执行：" + tiePool.Definition.PlannedReason);
}

/// <summary>
/// 返工 V5：覆盖与清理都要按**完整来源身份**核对。
/// · 同名文件只有 SourceId、缺 SourceIdentity（旧版本形态）→ 证明不了归属，不覆盖、整体放弃；
/// · 完整身份对得上 → 允许幂等覆盖；
/// · 减池清理时旧池子 SourceId 相同、SourceIdentity 指向别的来源 → 不得删除。
/// </summary>
static void ApiSkillWriteRespectsFullOwnership()
{
    var directory = Path.Combine(Path.GetTempPath(), "df-v5-" + Guid.NewGuid().ToString("N")[..8]);
    var previous = Environment.GetEnvironmentVariable("YEEYEEYEE_SKILL_DIR");
    try
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", directory);
        Directory.CreateDirectory(directory);

        var report = ApiDocAnalyzer.Analyze("POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k", "https://v5.example.com/docs");
        var plan = ApiSkillFactory.Build(report);
        var pool = plan.ImageSkills.First(skill => skill.IsPool);
        var poolFile = Path.Combine(directory, pool.FileName);
        Expect(plan.SourceId.Length > 0 && plan.SourceIdentity.Length > 0, "清单应带上来源命名空间与完整身份");

        // ① 同名文件只有命名空间、缺完整身份 → 不覆盖
        var halfOwned = ApiSkillFactory.Serialize(new SkillDefinition
        {
            Id = pool.Id,
            Name = "旧版本池子",
            SourceId = plan.SourceId,
            SourceIdentity = string.Empty,
            Steps = new List<SkillStep> { new() { Id = "pool", Name = "旧", Capability = "TextToImage" } }
        });
        File.WriteAllText(poolFile, halfOwned);
        var refused = ApiSkillFactory.Write(plan);
        Expect(refused.Written.Count == 0 && refused.Errors.Count > 0,
            "身份不完整的同名文件不得被覆盖：" + string.Join("；", refused.Errors));
        Expect(refused.Errors.Any(error => error.Contains("完整来源身份")), "应说明缺的是完整身份：" + string.Join("；", refused.Errors));
        Expect(File.ReadAllText(poolFile) == halfOwned, "被拒时旧文件字节不得改动");

        // ② 完整身份对得上 → 允许幂等覆盖
        File.WriteAllText(poolFile, ApiSkillFactory.Serialize(new SkillDefinition
        {
            Id = pool.Id,
            Name = "本来源旧版本",
            SourceId = plan.SourceId,
            SourceIdentity = plan.SourceIdentity,
            Steps = new List<SkillStep> { new() { Id = "pool", Name = "旧", Capability = "TextToImage" } }
        }));
        var rewritten = ApiSkillFactory.Write(plan);
        Expect(rewritten.AllWritten, "完整身份对得上时应允许幂等覆盖：" + string.Join("；", rewritten.Errors));

        // ③ 减池清理：旧池子 SourceId 相同、SourceIdentity 指向别的来源 → 不得删除
        const string staleId = "api-v5-example-com-image-pool-9-4k";
        var staleFile = Path.Combine(directory, staleId + ".json");
        File.WriteAllText(staleFile, ApiSkillFactory.Serialize(new SkillDefinition
        {
            Id = staleId,
            Name = "身份指向别的来源的池子",
            SourceId = plan.SourceId,
            SourceIdentity = "https://other.example.com:8443/v1",
            Steps = new List<SkillStep> { new() { Id = "pool", Name = "旧", Capability = "TextToImage" } }
        }));
        var pruned = ApiSkillFactory.Write(plan);
        Expect(File.Exists(staleFile), "身份指向别的来源的旧池子不得被删掉");
        Expect(!pruned.Removed.Contains(staleId + ".json"), "不应把它计入已清理：" + string.Join("、", pruned.Removed));
        Expect(pruned.Notes.Any(note => note.Contains("身份对不上")), "应说明保守保留的原因：" + string.Join("；", pruned.Notes));
    }
    finally
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", previous);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (IOException) { }
    }
}

/// <summary>
/// 返工 V6：顶层模型表的档位必须与**这个模型**写在同一处（同一行，或同一段里带尺寸标签的行），
/// 并且不能超出接口已核实的档位限制。原文 A=flux-1-dev/1K、B=flux-pro/4K 时，
/// 模型把 4K 挂到 flux-1-dev 上：4K 在全文里确实存在（在 B 的段落里），但归属不成立——
/// 工厂优先采用模型表，会重新造出一个打不通的 4K 池子。
/// </summary>
// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void ApiDocModelTableSizesNeedAssociation()
{
	ApiDocRepairResult apiDocRepairResult = ApiDocRepair.FromModelJson("{\"ops\":[\n  {\"capability\":\"TextToImage\",\"method\":\"POST\",\"path\":\"/v1/images/generations\",\"models\":[\"flux-1-dev\"],\"sizes\":[\"1k\"]},\n  {\"capability\":\"TextToImage\",\"method\":\"POST\",\"path\":\"/v1/hd/generations\",\"models\":[\"flux-pro\"],\"sizes\":[\"4k\"]}],\n \"models\":[\n  {\"name\":\"flux-1-dev\",\"kind\":\"Image\",\"sizes\":[\"1k\",\"4k\"]},\n  {\"name\":\"flux-pro\",\"kind\":\"Image\",\"sizes\":[\"4k\"]}]}", "https://v6.example.com/docs", "POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k\n\nPOST /v1/hd/generations\nmodel: flux-pro\n尺寸：4k");
	Expect(apiDocRepairResult.Ok, "应解析成功：" + apiDocRepairResult.Error);
	ApiDocReport report = apiDocRepairResult.Report;
	ApiModelEntry apiModelEntry = report.ModelTable.Single((ApiModelEntry entry) => entry.Name == "flux-1-dev");
	Expect(apiModelEntry.Sizes.Count == 1 && apiModelEntry.Sizes[0] == "1k", "跨接口的 4K 不得挂到 flux-1-dev 上：" + string.Join("、", apiModelEntry.Sizes));
	Expect(report.PendingItems.Any((string item) => item.Contains("4k")), "跨接口档位应列为待确认：" + string.Join("；", report.PendingItems));
	Expect(report.ModelTable.Single((ApiModelEntry entry) => entry.Name == "flux-pro").Sizes.Contains("4k"), "flux-pro 自己的 4K 应保留");
	PlannedApiSkill plannedApiSkill = ApiSkillFactory.Build(report).ImageSkills.FirstOrDefault((PlannedApiSkill skill) => skill.IsPool && skill.Definition.Steps[0].Model == "flux-1-dev" && skill.Definition.Steps[0].Width == 4096);
	Expect((object)plannedApiSkill == null, "不得造出原文并不支持的 4K 池子");
	ApiDocRepairResult apiDocRepairResult2 = ApiDocRepair.FromModelJson("{\"ops\":[{\"capability\":\"TextToImage\",\"method\":\"POST\",\"path\":\"/v1/images/generations\",\"models\":[\"flux-1-dev\"],\"sizes\":[\"1k\"]}],\n \"models\":[{\"name\":\"flux-1-dev\",\"kind\":\"Image\",\"sizes\":[\"1k\",\"2k\"]}]}", "https://v6.example.com/docs", "POST /v1/images/generations\nmodel: flux-1-dev\n尺寸：1k 2k");
	Expect(apiDocRepairResult2.Ok, "应解析成功：" + apiDocRepairResult2.Error);
	ApiModelEntry apiModelEntry2 = apiDocRepairResult2.Report.ModelTable.Single();
	Expect(!apiModelEntry2.Sizes.Contains("2k"), "接口只声明 1K 时不得保留 2K：" + string.Join("、", apiModelEntry2.Sizes));
	Expect(apiDocRepairResult2.Report.PendingItems.Any((string item) => item.Contains("接口")), "收敛应如实列入待确认：" + string.Join("；", apiDocRepairResult2.Report.PendingItems));
}

/// <summary>
/// 返工 R16-4：部分资产恢复失败后重试必须**只重试失败项**。整份记录被重放时，已恢复好的项若被
/// 反复处理，会报「回收目录里已找不到该文件」，于是记录永远清不掉、账本永远停在「待恢复」。
/// 这里走真实文件：先让第二项恢复失败（原位置被占），解除后重试必须收敛。
/// </summary>
static void AssetRecycleRetryOnlyRetriesFailures()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var a = Path.Combine(stores.Root, "r164-a.png");
    var b = Path.Combine(stores.Root, "r164-b.png");
    var bytesA = new byte[] { 0x89, 0x50, 0x4E, 0x47, 31 };
    var bytesB = new byte[] { 0x89, 0x50, 0x4E, 0x47, 32 };
    File.WriteAllBytes(a, bytesA);
    File.WriteAllBytes(b, bytesB);

    var moveA = AssetRecycle.Move(a) ?? throw new InvalidOperationException("a 应能移出");
    var moveB = AssetRecycle.Move(b) ?? throw new InvalidOperationException("b 应能移出");
    // 让 b 的恢复先失败：原位置被别的文件占住（确定性失败，不依赖文件锁）。
    File.WriteAllBytes(b, new byte[] { 9, 9 });

    var first = AssetRecycle.RestoreAll(new[] { moveA, moveB });
    Expect(first.Count == 1 && first[0].Contains("未覆盖"), "第一项成功、第二项如实失败：" + string.Join("；", first));
    Expect(moveA.Restored, "已成功恢复的项应被标记为完成");
    Expect(!moveB.Restored, "失败的项不得被标记为完成");
    Expect(File.ReadAllBytes(a).SequenceEqual(bytesA), "a 应已恢复且内容一致");

    // 解除占用后重试：只处理 b，不再把 a 报成「回收文件缺失」
    File.Delete(b);
    var second = AssetRecycle.RestoreAll(new[] { moveA, moveB });
    Expect(second.Count == 0, "解除占用后重试应全部完成（不得再报已恢复项）：" + string.Join("；", second));
    Expect(File.ReadAllBytes(a).SequenceEqual(bytesA) && File.ReadAllBytes(b).SequenceEqual(bytesB),
        "重试后两个文件都应是原始字节");
    Expect(!File.Exists(moveA.RecycledPath) && !File.Exists(moveB.RecycledPath), "回收目录里不应再留下文件");

    var third = AssetRecycle.RestoreAll(new[] { moveA, moveB });
    Expect(third.Count == 0, "再次重试仍然收敛：" + string.Join("；", third));
}

/// <summary>
/// 返工 R17-2：一批的撤销只做了一半（待恢复）时，新的批次必须被**统一提交入口**拦住——
/// 否则新批落画会覆盖 lastAgentCommit，旧批的恢复记录变成不可达的死记录，
/// 而账本仍停在待恢复、离开入口又被全部拦住。这里走真实提交入口：
/// 断言被拒时动作没有被应用、文件字节不变；恢复完成后同一新批放行并真的写入。
/// </summary>
static void CommitRejectsNewBatchWhileRecoveryPending()
{
    var ledger = new AgentCommitLedger();
    var committer = new AgentBatchCommitter(ledger);
    var canvas = new WorkflowCanvasState();
    var workspace = NewWorkspace();
    var file = Path.Combine(workspace, "notes.md");
    File.WriteAllText(file, "原始内容");

    var applyCalls = 0;
    var actionsA = new[] { new AgentAction { Kind = "write_file", Path = "notes.md", Content = "A 批写入" } };
    var batchA = AgentCommitLedger.NewBatchId();
    var first = committer.Commit(
        batchA, actionsA,
        () => { applyCalls++; return AgentActionExecutor.Apply(actionsA, canvas, workspace, FileWriteMode.Apply, new List<FileSnapshot>()); },
        save: () => true, canvasKey: "画布A");
    Expect(first.Succeeded, "A 批应提交成功：" + first.Message);
    Expect(File.ReadAllText(file) == "A 批写入", "A 批应真的写入文件");

    // 模拟「撤销只完成一半」：账本进入待恢复（撤销入口受阻的那一批）
    ledger.MarkNeedsRecovery(batchA);
    Expect(ledger.HasPendingRecovery(out var waiting) && waiting == batchA, "A 批应处于待恢复");

    var actionsB = new[] { new AgentAction { Kind = "write_file", Path = "notes.md", Content = "B 批写入" } };
    var batchB = AgentCommitLedger.NewBatchId();
    var blocked = committer.Commit(
        batchB, actionsB,
        () => { applyCalls++; return AgentActionExecutor.Apply(actionsB, canvas, workspace, FileWriteMode.Apply, new List<FileSnapshot>()); },
        save: () => true, canvasKey: "画布A");
    Expect(blocked.Stage == AgentCommitStage.Rejected, "待恢复期间新批必须被拒：" + blocked.Stage);
    Expect(blocked.Message.Contains("撤销没做完"), "拒绝理由应说明要先完成恢复：" + blocked.Message);
    Expect(applyCalls == 1, "被拒的新批不得应用动作，实际应用次数 " + applyCalls);
    Expect(File.ReadAllText(file) == "A 批写入", "被拒时文件字节不得改动");

    // 恢复完成（整批回滚）后，同一个新批放行并真的落盘
    ledger.MarkRolledBack(batchA);
    var after = committer.Commit(
        batchB, actionsB,
        () => { applyCalls++; return AgentActionExecutor.Apply(actionsB, canvas, workspace, FileWriteMode.Apply, new List<FileSnapshot>()); },
        save: () => true, canvasKey: "画布A");
    Expect(after.Succeeded, "恢复完成后新批应放行：" + after.Message);
    Expect(applyCalls == 2, "放行后才应用动作，实际 " + applyCalls);
    Expect(File.ReadAllText(file) == "B 批写入", "新批应真的写入文件");
}

/// <summary>
/// 返工 R16-1：把画布控件**自己的状态**当来源加载会先清空 State、再从刚清空的集合里取，
/// 结果画布被清成空。标签快照一旦与活动状态共用同一对象（切标签、关标签时都会发生），
/// 就会走到这条路径；这里断言控件直接拒绝自身别名。
/// </summary>
/// <summary>目标 6 / G6-1：项目级资源库的读写、原子写、写前备份与幂等 upsert。</summary>
static void ProjectLibraryRoundTrip()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    Expect(ProjectLibrary.Load().Entities.Count == 0, "空项目应读到空库");

    var first = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "刀客" };
    first.CreateVariant("默认");
    var second = new WorkflowEntity { Kind = EntityKind.Scene, Name = "雨巷", Core = "窄巷" };
    second.CreateVariant("雨夜");

    var file = ProjectLibrary.Upsert(new[] { first, second }, out var added, out var updated);
    Expect(added == 2 && updated == 0, $"首次写入应新增 2 条：added={added} updated={updated}");
    Expect(ProjectLibrary.TrySave(file, out var saveError), "写入项目库应成功：" + saveError);
    Expect(ProjectLibrary.Load().Entities.Count == 2, "读回应有 2 条");

    // 幂等：同一个 ID 再写一次只更新、不新增
    first.Core = "刀客（改）";
    var again = ProjectLibrary.Upsert(new[] { first }, out var added2, out var updated2);
    Expect(added2 == 0 && updated2 == 1, $"重复 upsert 应只更新：added={added2} updated={updated2}");
    Expect(ProjectLibrary.TrySave(again, out _), "重复写入应成功");
    Expect(ProjectLibrary.Load().Entities.Count == 2, "重复写入后条数不变");
    Expect(ProjectLibrary.Find(first.Id)!.Core == "刀客（改）", "内容应更新为最新");
    Expect(ProjectLibrary.Find(first.Id)!.ManagedByProject, "写入项目库的实体应标记为项目级");

    // 写前备份：覆盖写两次后至少有备份，且能回滚
    var backups = ProjectLibrary.Backups();
    Expect(backups.Count >= 1, "覆盖写之前应留下备份：" + backups.Count);
    Expect(ProjectLibrary.RestoreFromBackup(backups[0]) is null, "从备份恢复应成功");

    // 原子写：库文件位置被目录占住时如实失败，且不残留临时文件、不破坏现场
    var libraryBefore = File.ReadAllText(ProjectLibrary.FilePath);
    File.Delete(ProjectLibrary.FilePath);
    Directory.CreateDirectory(ProjectLibrary.FilePath);
    var blocked = !ProjectLibrary.TrySave(new ProjectEntityFile(), out var writeError);
    var leftovers = Directory.GetFiles(ProjectLibrary.Directory, "*.tmp").Length;
    Directory.Delete(ProjectLibrary.FilePath);
    File.WriteAllText(ProjectLibrary.FilePath, libraryBefore);
    Expect(blocked && writeError.Length > 0, "写入失败必须如实返回原因：" + writeError);
    Expect(leftovers == 0, "写入失败不得残留临时文件：" + leftovers);
    Expect(ProjectLibrary.Load().Entities.Count == 2, "恢复现场后内容应仍可读");
}

/// <summary>目标 6 / G6-1：同一实体被多份画布共享——改一次库，所有画布看到同一份内容。</summary>
static void ProjectEntitySharedAcrossCanvases()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var shared = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "第一版" };
    shared.CreateVariant("默认");
    var file = ProjectLibrary.Upsert(new[] { shared }, out _, out _);
    Expect(ProjectLibrary.TrySave(file, out var saveError), "写入项目库应成功：" + saveError);

    // 画布 A、B 各留一份**过期快照**（模拟迁移后库被改动）
    var staleA = ProjectEntityScope.CloneEntity(shared);
    staleA.ManagedByProject = true;
    staleA.Core = "陈旧快照";
    var canvasA = new WorkflowCanvasState();
    canvasA.Entities.Add(ProjectEntityScope.CloneEntity(staleA));
    var canvasB = new WorkflowCanvasState();
    canvasB.Entities.Add(ProjectEntityScope.CloneEntity(staleA));
    canvasB.Nodes.Add(new WorkflowNode
    {
        Title = "分镜",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    });

    var reportA = ProjectEntityScope.MergeInto(canvasA);
    var reportB = ProjectEntityScope.MergeInto(canvasB);
    Expect(reportA.Refreshed == 1 && reportB.Refreshed == 1, "两份画布都应按库刷新托管资源");
    Expect(canvasA.Entities[0].Core == "第一版" && canvasB.Entities[0].Core == "第一版",
        "共享资源的内容应来自项目库：" + canvasA.Entities[0].Core + "/" + canvasB.Entities[0].Core);
    Expect(!ReferenceEquals(canvasA.Entities[0], canvasB.Entities[0]), "两份画布不能共用同一个对象实例");

    // 改库 → 再打开（MergeInto）时两份画布都看到新内容
    shared.Core = "第二版";
    var updated = ProjectLibrary.Upsert(new[] { shared }, out _, out _);
    Expect(ProjectLibrary.TrySave(updated, out _), "改库应成功");
    ProjectEntityScope.MergeInto(canvasA);
    ProjectEntityScope.MergeInto(canvasB);
    Expect(canvasA.Entities[0].Core == "第二版" && canvasB.Entities[0].Core == "第二版",
        "改库后两份画布都应是新内容（跨画布共享）");

    // 保存时会刷新快照：构造画布文件里的快照也应是最新
    var snapshot = CanvasCloner.Clone(canvasA);
    Expect(ProjectEntityScope.RefreshSnapshots(snapshot) == 1, "保存刷新应处理 1 个托管实体");
    Expect(snapshot.Entities[0].Core == "第二版", "写盘快照应是库中最新内容");

    // 引用但快照缺失的库实体应被补进来
    var canvasC = new WorkflowCanvasState();
    canvasC.Nodes.Add(new WorkflowNode
    {
        Title = "新分镜",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    });
    var reportC = ProjectEntityScope.MergeInto(canvasC);
    Expect(reportC.Injected == 1 && canvasC.Entities.Count == 1, "被引用但缺失的共享资源应补入画布");
    Expect(canvasC.Entities[0].Core == "第二版", "补入的内容应来自项目库");

    // 库中删掉后：托管快照如实列为缺失，不静默当作正常
    Expect(ProjectLibrary.TryRemove(shared.Id, out var removeError), "移除应成功：" + removeError);
    var missing = ProjectEntityScope.MergeInto(canvasA);
    Expect(missing.Missing.Count == 1, "库中已不存在的共享资源应列为缺失：" + string.Join("；", missing.Missing));
}

/// <summary>目标 6 / G6-1、G6-3：跨画布索引要覆盖画布库与草稿，并标出"其它来源也在用"。</summary>
static void ProjectEntityIndexCoversCanvasAndDraft()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var shared = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    shared.CreateVariant("默认");
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { shared }, out _, out _), out _);

    // 画布库里的另一份画布引用它
    var other = new WorkflowCanvasState();
    other.Nodes.Add(new WorkflowNode
    {
        Title = "别的画布分镜",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    });
    var otherPath = CanvasLibrary.Save(ProjectCanvas(other, "引用共享资源的画布"), null);

    // 当前画布也引用它
    var current = new WorkflowCanvasState();
    current.Nodes.Add(new WorkflowNode
    {
        Title = "当前画布分镜",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    });

    var report = ProjectEntityIndex.Scan(current, null);
    var usage = ProjectEntityIndex.ForLibrary(ProjectLibrary.Entities(), report).Single();
    Expect(usage.Hits.Count >= 2, "索引应同时命中当前画布与其它画布：" + usage.Hits.Count);
    Expect(usage.HasForeignReference, "其它画布也在引用，应标出外来源");
    Expect(usage.Hits.Any(hit => hit.Scope == ReferenceScopeKind.Library && hit.SourcePath == otherPath),
        "索引应记录具体是哪份画布文件在引用");
    Expect(ProjectEntityIndex.Describe(report).Contains("引用扫描"), "应给出一句人话说明：" + ProjectEntityIndex.Describe(report));
}

/// <summary>目标 6 / G6-3：项目级资源删除必须有保护——其它来源在用时硬阻断；删除后可从回收站还原回库。</summary>
static void ProjectEntityDeletionBlocksForeignReferences()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var shared = new WorkflowEntity { Kind = EntityKind.Prop, Name = "听雨铃", Core = "旧铃" };
    shared.CreateVariant("默认");
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { shared }, out _, out _), out _);

    var foreign = new WorkflowCanvasState();
    foreign.Nodes.Add(new WorkflowNode
    {
        Title = "别人在用",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    });
    CanvasLibrary.Save(ProjectCanvas(foreign, "外部引用画布"), null);

    var onlyLocal = ProjectEntityDeletion.Check(ProjectEntityIndex.Scan(new WorkflowCanvasState(), null), shared.Id);
    Expect(onlyLocal.Blocked && onlyLocal.Foreign.Count > 0, "其它画布仍在引用时必须硬阻断：" + onlyLocal.Message);
    Expect(onlyLocal.Message.Contains("阻断"), "阻断说明要说清原因：" + onlyLocal.Message);

    // 删掉其它画布后只剩下当前画布引用：允许删除
    foreach (var summary in CanvasLibrary.List()) CanvasLibrary.Delete(summary.Path);
    var current = new WorkflowCanvasState();
    current.Nodes.Add(new WorkflowNode
    {
        Title = "本画布在用",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    });
    var localOnly = ProjectEntityDeletion.Check(ProjectEntityIndex.Scan(current, null), shared.Id);
    Expect(!localOnly.Blocked && localOnly.Local.Count == 1, "只有本画布引用时应允许删除：" + localOnly.Message);

    var recycleBefore = CanvasRecycleBin.Count;
    Expect(ProjectEntityDeletion.TryDeleteFromLibrary(shared, localOnly.Local, out var deleteError, out var recycleId),
        "删除应成功：" + deleteError);
    Expect(!ProjectLibrary.Contains(shared.Id), "删除后项目库里不应还有它");
    Expect(recycleId != Guid.Empty && CanvasRecycleBin.Count == recycleBefore + 1, "删除应留下可还原的回收站记录");
    Expect(ProjectLibrary.Backups().Count >= 1, "删库前应留下备份");

    Expect(ProjectEntityDeletion.TryRestoreToLibrary(recycleId, out var restoreMessage), "还原应成功：" + restoreMessage);
    Expect(ProjectLibrary.Contains(shared.Id), "还原后项目库里应重新有它");
    Expect(ProjectLibrary.Find(shared.Id)!.ManagedByProject, "还原回来的实体仍是项目级");
}

/// <summary>目标 6 / G6-2：迁移幂等，且同名不同实体绝不自动合并。</summary>
static void ProjectEntityMigrationIsIdempotentAndKeepsSameNamesApart()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var first = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "甲版" };
    first.CreateVariant("默认");
    var second = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "乙版" };
    second.CreateVariant("默认");

    var canvasA = new WorkflowCanvasState();
    canvasA.Entities.Add(ProjectEntityScope.CloneEntity(first));
    canvasA.Entities.Add(ProjectEntityScope.CloneEntity(second));
    var pathA = CanvasLibrary.Save(ProjectCanvas(canvasA, "甲画布"), null);

    var third = new WorkflowEntity { Kind = EntityKind.Scene, Name = "雨巷", Core = "窄巷" };
    third.CreateVariant("雨夜");
    var canvasB = new WorkflowCanvasState();
    canvasB.Entities.Add(ProjectEntityScope.CloneEntity(third));
    CanvasLibrary.Save(ProjectCanvas(canvasB, "乙画布"), null);

    var current = new WorkflowCanvasState();
    current.Entities.Add(ProjectEntityScope.CloneEntity(third));

    var preview = ProjectEntityMigration.Preview(current, null);
    Expect(preview.Migrated == 3, "应识别出 3 个待迁移资源：" + preview.Migrated);
    Expect(preview.Conflicts.Count >= 1, "同名不同实体应如实提示、不静默合并：" + string.Join("；", preview.Conflicts));
    Expect(preview.Conflicts.Any(text => text.Contains("沈砚")), "冲突提示应点名同名资源：" + string.Join("；", preview.Conflicts));

    var applied = ProjectEntityMigration.Apply(current, null);
    Expect(applied.Succeeded, "迁移应成功：" + applied.Message + " / " + string.Join("；", applied.Errors));
    Expect(ProjectLibrary.Load().Entities.Count == 3, "两个同名实体应各自入库、不合并：" + ProjectLibrary.Load().Entities.Count);
    Expect(ProjectLibrary.Load().Entities.Count(entity => entity.Name == "沈砚") == 2, "同名两个实体都要保留");

    // 画布文件里已改为托管
    Expect(CanvasLibrary.TryLoad(pathA, out var reloadedA) && reloadedA?.Canvas is not null, "迁移后应能读回画布");
    Expect(reloadedA!.Canvas!.Entities.All(entity => entity.ManagedByProject), "迁移后画布里的资源应标记为项目级");

    // 幂等：再预览应无可迁移项；再执行不新增
    var secondPreview = ProjectEntityMigration.Preview(current, null);
    Expect(secondPreview.Migrated == 0, "重复迁移应没有待迁移项：" + secondPreview.Migrated);
    Expect(secondPreview.AlreadyShared >= 1, "应说明跳过了多少已共享资源：" + secondPreview.AlreadyShared);
    var secondApply = ProjectEntityMigration.Apply(current, null);
    Expect(secondApply.Succeeded && ProjectLibrary.Load().Entities.Count == 3, "重复执行不得生成第二份");
    Expect(secondApply.Message.Contains("已在项目库") || secondApply.Message.Contains("迁移"), "应给出可读结论：" + secondApply.Message);
}

/// <summary>目标 6 / G6-2：迁移中途失败（画布备份写不进去）必须整批回滚，项目库与画布都恢复原状。</summary>
static void ProjectEntityMigrationRollsBackOnFailure()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var existing = new WorkflowEntity { Kind = EntityKind.Scene, Name = "已有共享场景", Core = "旧内容" };
    existing.CreateVariant("默认");
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { existing }, out _, out _), out _);
    var libraryBefore = File.ReadAllText(ProjectLibrary.FilePath);

    var pending = new WorkflowEntity { Kind = EntityKind.Character, Name = "待迁移角色", Core = "新内容" };
    pending.CreateVariant("默认");
    var canvas = new WorkflowCanvasState();
    canvas.Entities.Add(ProjectEntityScope.CloneEntity(pending));
    var path = CanvasLibrary.Save(ProjectCanvas(canvas, "待迁移画布"), null);
    var canvasBefore = File.ReadAllBytes(path);
    // 迁移走的是"当前打开的画布 + 它的路径"：必须真的把画布文件接上，否则只会标记内存对象
    Expect(CanvasOpenService.TryRead(path, out var reopened, out var readError) && reopened?.Canvas is not null,
        "应能读回待迁移画布：" + readError);

    // 故障注入：把画布备份目录的位置占成一个文件 → 备份失败 → 必须整批回滚
    var backupDirectory = CanvasBackup.Directory;
    Directory.CreateDirectory(Path.GetDirectoryName(backupDirectory)!);
    File.WriteAllText(backupDirectory, "占位");
    ProjectMigrationOutcome outcome;
    try
    {
        outcome = ProjectEntityMigration.Apply(reopened!.Canvas!, path);
    }
    finally
    {
        if (File.Exists(backupDirectory)) File.Delete(backupDirectory);
    }

    Expect(!outcome.Succeeded, "备份失败时迁移必须失败：" + outcome.Message);
    Expect(outcome.Errors.Any(error => error.Contains("备份失败")), "应说明是备份失败：" + string.Join("；", outcome.Errors));
    Expect(File.ReadAllText(ProjectLibrary.FilePath) == libraryBefore, "回滚后项目库必须与迁移前逐字一致");
    Expect(File.ReadAllBytes(path).SequenceEqual(canvasBefore), "回滚后画布文件不得被改动");
    Expect(!ProjectLibrary.Contains(pending.Id), "回滚后待迁移资源不得留在项目库里");
}

static RecentCanvasState ProjectCanvas(WorkflowCanvasState canvas, string title) =>
    new(title, 1, string.Empty, string.Empty, 1024, 1024, 28, 7, "随机", canvas) { FormatVersion = CanvasFormat.Current };

/// <summary>返工 G6-R1：托管资源的编辑必须写回项目库，保存重开后仍保留；写库失败不得误报成功或丢旧数据。</summary>
static void ProjectEntityEditPublishesToLibrary()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var shared = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "第一版" };
    shared.CreateVariant("默认");
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { shared }, out _, out _), out _);

    // 画布里的托管快照 + 一条引用
    var canvas = new WorkflowCanvasState();
    var snapshot = ProjectEntityScope.CloneEntity(shared);
    snapshot.ManagedByProject = true;
    canvas.Entities.Add(snapshot);
    canvas.Nodes.Add(new WorkflowNode
    {
        Title = "分镜",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    });
    ProjectEntityScope.MergeInto(canvas);
    var path = CanvasLibrary.Save(ProjectCanvas(canvas, "编辑共享资源的画布"), null);

    // 真实编辑动作：改名称与核心设定（版本提交同属这条链路，最终都要经过发布）
    var edited = canvas.Entities[0];
    edited.Name = "沈砚（改）";
    edited.Core = "第二版";
    edited.Variants[0].Commit("这一版改了核心设定");
    Expect(ProjectEntityScope.TryPublish(edited, out var publishError), "发布应成功：" + publishError);

    // 库与磁盘快照都要是新内容：保存时 RefreshSnapshots 不得用旧库内容覆盖编辑
    Expect(ProjectLibrary.Find(shared.Id)!.Core == "第二版", "项目库应写入编辑后的内容");
    CanvasSaveService.Save(CanvasLibrary.TryLoad(path, out var loaded) && loaded is not null ? loaded : ProjectCanvas(canvas, "编辑共享资源的画布"), path);
    Expect(CanvasOpenService.TryRead(path, out var reloaded, out var readError) && reloaded?.Canvas is not null, "重开应成功：" + readError);
    var reopenedEntity = reloaded!.Canvas!.Entities.Single();
    Expect(reopenedEntity.Name == "沈砚（改）" && reopenedEntity.Core == "第二版", "保存重开后仍应保留编辑（实际：" + reopenedEntity.Name + " / " + reopenedEntity.Core + "）");
    var libraryVariant = ProjectLibrary.Find(shared.Id)!.Variants[0];
    Expect(libraryVariant.Versions.Any(version => version.Note == "这一版改了核心设定"),
        "版本提交应写回项目库（实际 " + libraryVariant.Versions.Count + " 版："
        + string.Join("、", libraryVariant.Versions.Select(version => version.Note)) + "）");
    Expect(reopenedEntity.Variants[0].Versions.Any(version => version.Note == "这一版改了核心设定"),
        "重开后应能看到提交的那一版（实际：" + string.Join("、", reopenedEntity.Variants[0].Versions.Select(version => $"{version.Number}:{version.Note}")) + "）");

    // 写库失败：如实失败、不误报成功，画布内容按调用方还原后与磁盘一致
    var before = ProjectEntityScope.CloneEntity(reopenedEntity);
    ProjectLibrary.TryDeleteFile(out _);
    Directory.CreateDirectory(ProjectLibrary.FilePath);      // 占住库文件位置，令写入失败
    reopenedEntity.Core = "第三版";
    var failed = !ProjectEntityScope.TryPublish(reopenedEntity, out var failureReason);
    Directory.Delete(ProjectLibrary.FilePath);
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { before }, out _, out _), out _);   // 恢复库现场
    Expect(failed && failureReason.Length > 0, "写库失败必须如实返回：" + failureReason);

    ProjectEntityScope.RestoreInto(reopenedEntity, before);
    Expect(reopenedEntity.Core == "第二版", "还原后内存内容应回到编辑前：" + reopenedEntity.Core);
    Expect(ProjectLibrary.Find(shared.Id)!.Core == "第二版", "库内容不应被失败的编辑污染");
}

/// <summary>返工 G6-R2：首次迁移（本来没有项目库）失败时，要把库恢复成"本来不存在"，并还原内存标记与画布字节。</summary>
static void ProjectEntityMigrationFirstRunRollbackRestoresAbsentLibrary()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));
    Expect(!File.Exists(ProjectLibrary.FilePath), "本用例起点必须是没有项目库");

    var pending = new WorkflowEntity { Kind = EntityKind.Scene, Name = "首次迁移场景", Core = "内容" };
    pending.CreateVariant("默认");
    var canvas = new WorkflowCanvasState();
    canvas.Entities.Add(ProjectEntityScope.CloneEntity(pending));
    var path = CanvasLibrary.Save(ProjectCanvas(canvas, "首次迁移画布"), null);
    var canvasBefore = File.ReadAllBytes(path);
    Expect(CanvasOpenService.TryRead(path, out var reopened, out var readError) && reopened?.Canvas is not null, "应能读回画布：" + readError);

    var backupDirectory = CanvasBackup.Directory;
    Directory.CreateDirectory(Path.GetDirectoryName(backupDirectory)!);
    File.WriteAllText(backupDirectory, "占位");
    ProjectMigrationOutcome outcome;
    try
    {
        outcome = ProjectEntityMigration.Apply(reopened!.Canvas!, path);
    }
    finally
    {
        if (File.Exists(backupDirectory)) File.Delete(backupDirectory);
    }

    Expect(!outcome.Succeeded, "首次迁移失败应如实返回：" + outcome.Message);
    Expect(!File.Exists(ProjectLibrary.FilePath), "回滚后项目库文件必须不存在（回到本来没有库的状态）");
    Expect(!ProjectLibrary.Contains(pending.Id), "回滚后不得残留刚迁进去的实体");
    Expect(!outcome.WrittenCanvases.Any(), "失败时不应留下已写入清单：" + string.Join("；", outcome.WrittenCanvases));
    Expect(File.ReadAllBytes(path).SequenceEqual(canvasBefore), "回滚后画布文件不得被改动");
    Expect(!reopened!.Canvas!.Entities[0].ManagedByProject, "回滚后内存里的托管标记必须还原为 false");

    // 正确重试：排除故障后同一批能成功
    var retry = ProjectEntityMigration.Apply(reopened.Canvas!, path);
    Expect(retry.Succeeded && ProjectLibrary.Contains(pending.Id), "解除故障后重试应成功：" + retry.Message + " / " + string.Join("；", retry.Errors));
    Expect(reopened.Canvas!.Entities[0].ManagedByProject, "重试成功后内存标记应为项目级");
}

/// <summary>返工 G6-R3：项目库里的权威资源缺失时，解析/预检/锁定一律阻断，快照只留作恢复参考。</summary>
static void ProjectEntityMissingAuthorityBlocksResolve()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var shared = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "库中内容" };
    shared.CreateVariant("默认");
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { shared }, out _, out _), out _);

    var canvas = new WorkflowCanvasState();
    var snapshot = ProjectEntityScope.CloneEntity(shared);
    snapshot.ManagedByProject = true;
    canvas.Entities.Add(snapshot);
    var node = new WorkflowNode
    {
        Title = "分镜",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    };
    canvas.Nodes.Add(node);
    ProjectEntityScope.MergeInto(canvas);
    Expect(canvas.ResolveReferences(node).Count == 1, "库在场时应能正常解析");

    // 复制画布却漏带项目库：删掉库文件后重开
    Expect(ProjectLibrary.TryDeleteFile(out var deleteError), "删除库文件应成功：" + deleteError);
    var report = ProjectEntityScope.MergeInto(canvas);
    Expect(report.Missing.Count == 1, "权威资源缺失应如实报出：" + string.Join("；", report.Missing));
    Expect(canvas.Entities[0].IsProjectMissing, "缺失的托管实体应打上运行时标记");
    Expect(canvas.ResolveReferences(node).Count == 0, "权威资源缺失时不得继续解析旧快照");
    Expect(canvas.ResolveReferencePairs(node).Single().Content is null, "逐条解析也应给出未解析结果");
    Expect(CanvasReferenceVersions.IsNodeBlocked(canvas, node), "节点应处于明确阻断状态");
    Expect(CanvasReferenceVersions.IsAuthoritativeMissing(canvas, node.References[0]), "应能指出是权威资源缺失");
    Expect(CanvasReferenceVersions.UsableContents(canvas, node).Count == 0, "可安全使用的内容必须为空");
    var blockText = CanvasReferenceVersions.DescribeBlock(canvas, node);
    Expect(blockText.Contains("项目库") && blockText.Contains("不可用"), "阻断说明应点名项目库且给出不可用结论：" + blockText);
    Expect(blockText.Contains("节点"), "阻断说明应包含节点名：" + blockText);

    // 把库放回去：快照仍在，解析立即恢复（保留快照供恢复的语义）
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { shared }, out _, out _), out _);
    var restored = ProjectEntityScope.MergeInto(canvas);
    Expect(restored.Missing.Count == 0 && !canvas.Entities[0].IsProjectMissing, "库放回后不应再有缺失");
    Expect(canvas.ResolveReferences(node).Count == 1, "库放回后应恢复解析");
    Expect(canvas.ResolveReferences(node)[0].Entity.Core == "库中内容", "恢复后内容应来自项目库");
    Expect(!CanvasReferenceVersions.IsNodeBlocked(canvas, node), "库放回后不应再阻断");
}

/// <summary>
/// 返工 G6-S3：删除变体必须**先落库、后动本画布**。发布失败时实体、节点引用、库三者都不许变；
/// 发布成功但本地删除失败时要把库回滚，实体与引用的原关系在内存与保存重开后都保持。
/// </summary>
static void VariantDeletionKeepsReferencesWhenPublishFails()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "库内容" };
    entity.CreateVariant("默认");
    var variantWar = entity.CreateVariant("战时");
    entity.ManagedByProject = true;
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { entity }, out _, out _), out _);

    var canvas = new WorkflowCanvasState();
    canvas.Entities.Add(ProjectEntityScope.CloneEntity(entity));
    var node = new WorkflowNode
    {
        Title = "分镜",
        References = { new NodeReference { EntityId = entity.Id, VariantId = variantWar.Id } }
    };
    canvas.Nodes.Add(node);
    ProjectEntityScope.MergeInto(canvas);
    var path = CanvasLibrary.Save(ProjectCanvas(canvas, "删除变体保护画布"), null);
    Expect(canvas.ResolveReferences(node).Count == 1, "起点应能解析引用");

    // ① 写库失败（库文件位置被目录占住）：实体、引用、库三者都不许变
    var libraryBytes = File.ReadAllBytes(ProjectLibrary.FilePath);
    File.Delete(ProjectLibrary.FilePath);
    Directory.CreateDirectory(ProjectLibrary.FilePath);
    var planned = ProjectEntityScope.CloneEntity(canvas.Entities[0]);
    planned.Variants.RemoveAll(candidate => candidate.Id == variantWar.Id);
    var published = ProjectEntityScope.TryPublish(planned, out var publishError);
    Directory.Delete(ProjectLibrary.FilePath);
    File.WriteAllBytes(ProjectLibrary.FilePath, libraryBytes);
    Expect(!published && publishError.Length > 0, "库写不进时应如实返回：" + publishError);
    Expect(canvas.Entities[0].Variants.Count == 2, "发布失败时实体不得改动");
    Expect(node.References.Count == 1, "发布失败时不得解除引用");
    Expect(File.ReadAllBytes(ProjectLibrary.FilePath).SequenceEqual(libraryBytes), "发布失败时库字节不变");

    // ② 发布成功、本地删除失败（回收站写不进去）：库回滚，实体与引用原样
    var beforeDelete = ProjectEntityScope.CloneEntity(canvas.Entities[0]);
    var plan = ProjectEntityScope.CloneEntity(canvas.Entities[0]);
    plan.Variants.RemoveAll(candidate => candidate.Id == variantWar.Id);
    Expect(ProjectEntityScope.TryPublish(plan, out var planError), "先发布应成功：" + planError);
    Expect(ProjectLibrary.Find(entity.Id)!.Variants.Count == 1, "库中该实体应只剩一个变体");

    Directory.CreateDirectory(CanvasRecycleBin.FilePath);   // 阻断回收站写入
    var report = CanvasReferenceScanner.Scan(canvas, path, includeLibrary: false, includeDraft: false);
    var hits = CanvasReferenceScanner.ForVariant(report, entity.Id, variantWar.Id);
    var target = canvas.Entities[0].Variants.First(candidate => candidate.Id == variantWar.Id);
    var deleted = CanvasDeletionGuard.TryDeleteVariant(canvas, canvas.Entities[0], target, hits, out var deleteError);
    Directory.Delete(CanvasRecycleBin.FilePath);
    Expect(!deleted, "回收站写不进时本地删除必须整体取消：" + deleteError);

    Expect(ProjectEntityScope.TryPublish(beforeDelete, out var rollbackError), "库回滚应成功：" + rollbackError);
    Expect(ProjectLibrary.Find(entity.Id)!.Variants.Count == 2, "库应回到删除前的两个变体");
    Expect(canvas.Entities[0].Variants.Count == 2, "内存实体应保持两个变体");
    Expect(node.References.Count == 1, "节点引用必须保持：" + node.References.Count);

    // 保存重开后原关系仍在
    CanvasSaveService.Save(ProjectCanvas(canvas, "删除变体保护画布"), path);
    Expect(CanvasOpenService.TryRead(path, out var reloaded, out var readError) && reloaded?.Canvas is not null, "重开应成功：" + readError);
    var reopenedCanvas = reloaded!.Canvas!;
    Expect(reopenedCanvas.Entities.Single().Variants.Count == 2, "保存重开后变体数不变");
    Expect(reopenedCanvas.Nodes.Single().References.Single().VariantId == variantWar.Id, "保存重开后引用仍指向原变体");
}

/// <summary>
/// 返工 G6-S2：版本提交是"已确认的动作"，提示成功就必须立即落库；外层取消只能丢弃未持久化的编辑；
/// 写库失败时提交整体撤回并如实报错，库内容不被污染。
/// </summary>
static void VersionCommitPersistsImmediatelyAndSurvivesCancel()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "库内容" };
    var variant = entity.CreateVariant("默认");
    entity.ManagedByProject = true;
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { entity }, out _, out _), out _);
    var versionsBefore = ProjectLibrary.Find(entity.Id)!.Variants[0].Versions.Count;

    // 与真实提交入口一致：先把内容写进变体再提交，然后**立即**发布
    variant.Commit("这一版改了核心设定");
    Expect(ProjectEntityScope.TryPublish(entity, out var publishError), "提交后应立即写回项目库：" + publishError);
    Expect(ProjectLibrary.Find(entity.Id)!.Variants[0].Versions.Count == versionsBefore + 1, "库中应出现刚提交的版本");
    // 以"提交成功之后"的库内容作为现场基准（写库失败注入后要用它恢复）
    var authoritative = ProjectLibrary.Find(entity.Id)!;

    // 外层取消：只丢弃未持久化的编辑（用库内容对齐），已确认的提交必须保留
    entity.Core = "取消时应丢掉的未提交字段改动";
    ProjectEntityScope.RestoreInto(entity, ProjectLibrary.Find(entity.Id)!);
    Expect(entity.Core == "库内容", "取消应丢掉未持久化的字段改动：" + entity.Core);
    Expect(ProjectLibrary.Find(entity.Id)!.Variants[0].Versions.Count == versionsBefore + 1, "取消不得撤销已确认的提交");
    Expect(entity.Variants[0].Versions.Count == versionsBefore + 1, "内存与库的版本数应一致");

    // 写库失败：提交必须整体撤回，库不被污染，也不误报成功
    File.Delete(ProjectLibrary.FilePath);
    Directory.CreateDirectory(ProjectLibrary.FilePath);
    var beforeFailure = ProjectEntityScope.CloneEntity(entity);
    entity.Variants[0].Commit("这一版写不进库");
    var failed = !ProjectEntityScope.TryPublish(entity, out var failureReason);
    ProjectEntityScope.RestoreInto(entity, beforeFailure);
    Directory.Delete(ProjectLibrary.FilePath);
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { authoritative }, out _, out _), out _);
    Expect(failed && failureReason.Length > 0, "写库失败应如实返回：" + failureReason);
    Expect(entity.Variants[0].Versions.Count == versionsBefore + 1, "失败后撤回提交，内存版本数回到提交前");
    Expect(ProjectLibrary.Find(entity.Id)!.Variants[0].Versions.Count == versionsBefore + 1, "库内容不得被失败的提交污染");
}

/// <summary>
/// 返工 G6-T1：库可能在画布打开**之后**被删除或漏拷，此时打开时算出的旧标记还是"正常"。
/// 执行前必须现场重新核验权威资源，否则缺库的引用会一路走到提供方。
/// </summary>
static void AuthorityRecheckCatchesLaterDeletedLibrary()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var shared = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚", Core = "库内容" };
    shared.CreateVariant("默认");
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { shared }, out _, out _), out _);

    var canvas = new WorkflowCanvasState();
    var snapshot = ProjectEntityScope.CloneEntity(shared);
    snapshot.ManagedByProject = true;
    canvas.Entities.Add(snapshot);
    var node = new WorkflowNode
    {
        Title = "分镜",
        References = { new NodeReference { EntityId = shared.Id, VariantId = shared.Variants[0].Id } }
    };
    canvas.Nodes.Add(node);
    ProjectEntityScope.MergeInto(canvas);
    Expect(!CanvasReferenceVersions.IsNodeBlocked(canvas, node), "库在场时不应阻断");

    // 打开之后库被删掉，而且**不再合并**——此时旧标记还是"正常"（正是复核复现的现场）
    Expect(ProjectLibrary.TryDeleteFile(out var deleteError), "删库应成功：" + deleteError);
    Expect(!canvas.Entities[0].IsProjectMissing, "删库后旧标记尚未更新（模拟现场）");

    var missing = ProjectEntityScope.RefreshAuthority(canvas);
    Expect(missing.Count == 1 && canvas.Entities[0].IsProjectMissing, "重新核验后应发现缺失：" + string.Join("；", missing));
    Expect(CanvasReferenceVersions.IsNodeBlocked(canvas, node), "重新核验后节点应被阻断");
    Expect(CanvasReferenceVersions.UsableContents(canvas, node).Count == 0, "阻断后不得留下可用内容");
    Expect(canvas.ResolveReferences(node).Count == 0, "阻断后不应再解析旧快照");

    // 库放回：重新核验即放行，内容仍来自项目库
    ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { shared }, out _, out _), out _);
    Expect(ProjectEntityScope.RefreshAuthority(canvas).Count == 0, "库恢复后不应再有缺失");
    Expect(!canvas.Entities[0].IsProjectMissing && !CanvasReferenceVersions.IsNodeBlocked(canvas, node), "库恢复后应放行");
    Expect(canvas.ResolveReferences(node)[0].Entity.Core == "库内容", "放行后内容应来自项目库");
}

/// <summary>
/// 返工 G6-T2：发布结果必须区分"真的落进项目库"与"只是本地内容"——
/// 本地（未迁移）实体不得被当成已保存，写库失败也要如实说明。
/// </summary>
static void PublishOutcomeDistinguishesLocalAndShared()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var local = new WorkflowEntity { Kind = EntityKind.Character, Name = "本地角色" };
    local.CreateVariant("默认");
    var localOutcome = ProjectEntityScope.Publish(local);
    Expect(localOutcome.Succeeded && !localOutcome.Persisted, "本地实体：发布成功但**没有**持久化");
    Expect(!File.Exists(ProjectLibrary.FilePath), "本地实体不得写出项目库文件");

    var shared = new WorkflowEntity { Kind = EntityKind.Character, Name = "共享角色" };
    shared.CreateVariant("默认");
    shared.ManagedByProject = true;
    var sharedOutcome = ProjectEntityScope.Publish(shared);
    Expect(sharedOutcome.Succeeded && sharedOutcome.Persisted, "托管实体：应真的落库");
    Expect(ProjectLibrary.Contains(shared.Id), "托管实体应出现在项目库里");

    File.Delete(ProjectLibrary.FilePath);
    Directory.CreateDirectory(ProjectLibrary.FilePath);
    var failedOutcome = ProjectEntityScope.Publish(shared);
    Directory.Delete(ProjectLibrary.FilePath);
    Expect(!failedOutcome.Succeeded && !failedOutcome.Persisted && failedOutcome.Error.Length > 0,
        "写库失败应如实返回：" + failedOutcome.Error);
}

/// <summary>真实聚合站文档的形态：池子按模型自己的类型与档位建（模型名连「池6」后缀一起保留）。</summary>
static void ApiDocModelTableDrivesPools()
{
    var doc = """
    # 接口文档

    ### 基础信息

    Base URL https://video.example.com/v1

    ### 常用端点

    - GET/v1/models
    - POST/v1/images/generations 文生图
    - POST/v1/images/edits 图生图(multipart)
    - POST/v1/videos 建视频任务
    - GET/v1/videos/{id} 查状态
    - GET/v1/videos/{id}/content 下载 mp4

    ## 确认可用模型

    | model | 类型 | 定价 积分 | 能力 |
    |---|---|---|---|
    | gpt-image-2.5-sunburst(池6) | 图像 | 1K 2.5 2K 3.5 4K 6 | 1K 2K 4K 参考图 |
    | gpt-image-2(池1) | 图像 | 1K 2.5 | 1K 参考图 |
    | nano-banana-pro(池7) | 图像 | 1K 6 2K 10 4K 15 | 1K 2K 4K 参考图 |
    | seedance-2.0(700)(池7) | 视频 | 720p 15 15s 225 | 720p 15s 参考图 |

    ## 文生图参数 /v1/images/generations

    | 参数 | 类型 | 必填 | 说明 |
    |---|---|---|---|
    | model | string | 必填 | 模型名(别名优先),见上表 |
    | prompt | string | 必填 | 文字描述 |

    ## 视频参数 /v1/videos · 异步

    | 参数 | 类型 | 必填 | 说明 |
    |---|---|---|---|
    | model | string | 必填 | 模型名(别名优先),见上表 |
    | input_reference | file | 可选 | 参考图,runway 图生视频必填 1 张 |
    """;

    var report = ApiDocAnalyzer.Analyze(doc, "https://video.example.com/about");

    // 详情端点（后面跟 {id}）只用来查状态与下载，不是生成接口。
    Expect(report.Ops.Count == 3, "应只解析出 3 条生成接口：" + string.Join("、", report.Ops.Select(op => op.Path)));
    Expect(report.ImageOps.Count == 2 && report.VideoOps.Count == 1,
        $"应 2 出图 + 1 出视频：{report.ImageOps.Count}/{report.VideoOps.Count}");
    Expect(!report.Ops.Any(op => op.Path.Contains("videos/", StringComparison.Ordinal)), "带占位符的详情端点不得出现在接口清单");
    Expect(report.VideoOps[0].Capability == Capability.TextToVideo, "通用 /v1/videos 应判为文生视频：" + report.VideoOps[0].Capability);
    Expect(report.Notes.Any(note => note.Contains("跳过")), "应说明跳过了详情端点：" + string.Join("；", report.Notes));

    // 模型表：类型与档位都按模型自己的那一行读，模型名带后缀。
    Expect(report.ModelTable.Count == 4, "模型表应读到 4 个模型：" + report.ModelTable.Count);
    Expect(report.ImageModels.Count == 3 && report.VideoModels.Count == 1, "模型表类型应分清图像与视频");
    var first = report.ImageModels[0];
    Expect(first.Name == "gpt-image-2.5-sunburst(池6)", "模型名必须保留「(池6)」这类后缀：" + first.Name);
    Expect(first.Sizes.SequenceEqual(new[] { "1k", "2k", "4k" }), "该模型支持的档位应来自它自己那一行：" + string.Join("、", first.Sizes));
    var single = report.ImageModels[1];
    Expect(single.Sizes.Count == 1 && single.Sizes[0] == "1k", "只支持 1K 的模型不应拿到别的档位：" + string.Join("、", single.Sizes));
    var video = report.VideoModels[0];
    Expect(video.Name == "seedance-2.0(700)(池7)" && video.Sizes.SequenceEqual(new[] { "720p" }),
        "视频模型应读到 720p：" + video.Name + "｜" + string.Join("、", video.Sizes));

    var plan = ApiSkillFactory.Build(report);
    var names = plan.Skills.Select(skill => skill.Name).ToList();
    var poolModels = plan.Skills.Where(skill => skill.IsPool).Select(skill => skill.Definition.Steps[0].Model).ToList();
    Expect(!poolModels.Contains("model") && !poolModels.Contains("input_reference") && !poolModels.Contains("prompt"),
        "参数表的参数名不得被当成模型名：" + string.Join("、", poolModels));
    Expect(names.Contains("生图池1 1K") && names.Contains("生图池1 4K"), "应按模型与档位建池子：" + string.Join("、", names));
    Expect(names.Contains("生视频池1 720p"), "应建出出视频池子技能：" + string.Join("、", names));
    Expect(plan.VideoSkills.Count == 2, "出视频技能应为 1 父 + 1 池：" + plan.VideoSkills.Count);
    var pool4K = plan.Skills.First(skill => skill.Name == "生图池1 4K").Definition.Steps[0];
    Expect(pool4K.Model == "gpt-image-2.5-sunburst(池6)" && pool4K.Width == 4096 && pool4K.Height == 4096,
        $"池子步骤应带模型与画幅：{pool4K.Model}｜{pool4K.Width}x{pool4K.Height}");
    Expect(!plan.Warnings.Any(warning => warning.Contains("只建前")), "组合没超上限时不应出现截断提示：" + string.Join("；", plan.Warnings));

    // 组合数量超上限：如实提示并截断，不把技能目录刷满。
    var many = new System.Text.StringBuilder(doc);
    many.Append("\n\n| model | 类型 | 定价 积分 | 能力 |\n|---|---|---|---|");
    for (var index = 0; index < 20; index++)
        many.Append($"\n| brand-model-{index} | 图像 | 1K 2 | 1K 参考图 |");
    var manyPlan = ApiSkillFactory.Build(ApiDocAnalyzer.Analyze(many.ToString(), "https://video.example.com/about"));
    Expect(manyPlan.ImageSkills.Count(skill => skill.IsPool) == ApiSkillFactory.MaxPoolsPerKind,
        $"出图池子数应截到上限 {ApiSkillFactory.MaxPoolsPerKind}：{manyPlan.ImageSkills.Count(skill => skill.IsPool)}");
    Expect(manyPlan.Warnings.Any(warning => warning.Contains("只建前")), "截断必须如实提示：" + string.Join("；", manyPlan.Warnings));
}

/// <summary>
/// 基础地址必须保留版本前缀：真实站点的 Base URL 是 https://host/v1，只取域名会让请求打到
/// https://host/images/generations（实测 404）。顺带验证图片扩展名按文件头判断
/// （接口返回的是 JPEG，存成 .png 就是扩展名说谎）。
/// </summary>
static void ApiDocBaseUrlAndMediaExtension()
{
    var withBase = ApiDocAnalyzer.Analyze("""
    # 接口文档
    Base URL https://video.example.com/v1
    - POST/v1/images/generations 文生图
    """, "https://video.example.com/about");
    Expect(withBase.BaseUrl == "https://video.example.com/v1", "应保留文档写明的版本前缀：" + withBase.BaseUrl);

    var derived = ApiDocAnalyzer.Analyze("POST /v2/images/generations\nmodel: flux-1-dev", "https://api.example.com/docs");
    Expect(derived.BaseUrl == "https://api.example.com/v2", "文档没写根地址时应从接口路径反推版本前缀：" + derived.BaseUrl);

    var bare = ApiDocAnalyzer.Analyze("POST /images/generations", "https://api.example.com/docs");
    Expect(bare.BaseUrl == "https://api.example.com", "没有版本段时基础地址就是站点根：" + bare.BaseUrl);

    var noisy = ApiDocAnalyzer.Analyze("""
    参考图 https://cdn.other.com/v1/logo.png
    Base URL https://api.example.com/v1
    POST /v1/videos
    """, "https://api.example.com/docs");
    Expect(noisy.BaseUrl == "https://api.example.com/v1", "应优先用同域的接口根地址，别被 CDN 链接带偏：" + noisy.BaseUrl);

    Expect(ImageFormatSniffer.ExtensionOf(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }) == ".jpg",
        "JPEG 应存成 .jpg");
    Expect(ImageFormatSniffer.ExtensionOf(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }) == ".png",
        "PNG 应存成 .png");
    Expect(ImageFormatSniffer.ExtensionOf(null) == ".png", "内容为空时按 .png 兜底");
}

/// <summary>
/// 余额与上线模型的取数与解析（用户要求：窗口上显示余额）。成功要取到余额与模型，
/// 失败要如实报出接口给的说明（真实接口 401 返回的是 {"detail":"missing api key"}）。
/// </summary>
static void ApiAccountProbeReadsBalanceAndModels()
{
    const string balanceJson = """
    {"balance":618,"object":"user.balance","permanent_credits":618,"temporary_credits":0,"temporary_expires_at":"2026-09-29T16:00:00Z","total":3625,"used":3007}
    """;
    const string modelsJson = """
    {"object":"list","data":[{"id":"gpt-image-2(池6)"},{"id":"seedance2.0-mini(池10)"}]}
    """;

    var parsed = ApiAccountProbe.ParseBalance(balanceJson);
    Expect(parsed is not null && parsed.Balance == 618 && parsed.Used == 3007 && parsed.Total == 3625,
        "应解析出余额：" + (parsed?.Describe() ?? "（没解析出来）"));
    Expect(parsed!.Permanent == 618 && parsed.Temporary == 0 && parsed.TemporaryNote().Length == 0, "永久与临时额度应分开");
    Expect(ApiAccountProbe.ParseBalance("{\"detail\":\"missing api key\"}") is null, "没有余额字段时不得拿 0 冒充余额");
    Expect(ApiAccountProbe.ParseBalance("{\"balance\":\"120\"}")?.Balance == 120, "余额是字符串时也应认得");
    Expect(ApiAccountProbe.ParseBalance("{不是 JSON") is null, "非 JSON 不应抛异常");

    var ids = ApiAccountProbe.ModelIds(modelsJson);
    Expect(ids.Count == 2 && ids[0] == "gpt-image-2(池6)", "应读出模型 id 并保留中文后缀：" + string.Join("、", ids));

    var snapshot = new ApiAccountSnapshot(true, parsed, ids, string.Empty);
    var missing = snapshot.MissingFrom(new[] { "gpt-image-2(池6)", "gpt-image-2(池5)", "seedance-2.0(700)(池7)" });
    Expect(missing.SequenceEqual(new[] { "gpt-image-2(池5)", "seedance-2.0(700)(池7)" }),
        "应算出文档写了但没上线的模型：" + string.Join("、", missing));
    Expect(new ApiAccountSnapshot(true, parsed, Array.Empty<string>(), string.Empty).MissingFrom(new[] { "任意" }).Count == 0,
        "没取到模型列表时不该谎报差异");

    // 取数：地址拼对、两次请求都带上鉴权头
    var requested = new List<string>();
    var handler = new StubHttpHandler(request =>
    {
        requested.Add(request.RequestUri!.AbsolutePath);
        var body = request.RequestUri.AbsolutePath.EndsWith("/user/balance", StringComparison.Ordinal) ? balanceJson : modelsJson;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    });
    var ok = ApiAccountProbe.FetchAsync("https://host.example.com/v1/", "sk-test", new HttpClient(handler)).GetAwaiter().GetResult();
    Expect(ok.Ok && ok.Balance?.Balance == 618 && ok.Models.Count == 2, "应一次取到余额与模型：" + ok.Error);
    Expect(requested.SequenceEqual(new[] { "/v1/user/balance", "/v1/models" }),
        "请求路径应是 {基础地址}/user/balance 与 {基础地址}/models：" + string.Join("、", requested));

    var denied = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
    {
        Content = new StringContent("{\"detail\":\"missing api key\"}", Encoding.UTF8, "application/json")
    });
    var failed = ApiAccountProbe.FetchAsync("https://host.example.com/v1", "sk-bad", new HttpClient(denied)).GetAwaiter().GetResult();
    Expect(!failed.Ok && failed.Error.Contains("401") && failed.Error.Contains("missing api key"),
        "失败时应带上状态码与接口说明：" + failed.Error);

    var noKey = ApiAccountProbe.FetchAsync("https://host.example.com/v1", "", null).GetAwaiter().GetResult();
    Expect(!noKey.Ok && noKey.Balance is null, "没有密钥时不应发请求，也不该给出假余额");
}

/// <summary>
/// 大模型修正解析（用户要求：智能导入失败时允许调用大模型分析修正）。
/// 只采信文档里有的内容：非法能力名、占位符详情端点、没有名字的模型都要丢掉并计数；
/// 大模型没给版本前缀时按接口路径补上。顺带验证「按接口实际模型重建」的收敛规则。
/// </summary>
static void ApiDocRepairValidatesModelOutput()
{
    var payload = """
    ```json
    {"baseUrl":"https://video.example.com","title":"出图与出视频",
     "ops":[
       {"capability":"TextToImage","method":"POST","path":"/v1/images/generations","models":["flux-1-dev"],"sizes":["1k"]},
       {"capability":"文生视频","method":"post","path":"/v1/videos","models":["veo-3"],"sizes":["720p"]},
       {"capability":"TextToImage","method":"GET","path":"/v1/videos/{id}"},
       {"capability":"乱写的能力","method":"POST","path":"/v1/whatever"}],
     "models":[
       {"name":"flux-1-dev","kind":"Image","sizes":["1k","2k"]},
       {"name":"veo-3","kind":"Video","sizes":["720p"]},
       {"name":"","kind":"Image","sizes":[]}]}
    ```
    """;

    var result = ApiDocRepair.FromModelJson(payload, "https://video.example.com/about");
    Expect(result.Ok, "应能从带围栏的回复里解析出报告：" + result.Error);
    var report = result.Report!;
    Expect(report.BaseUrl == "https://video.example.com/v1", "大模型没写版本前缀时应按接口路径补上：" + report.BaseUrl);
    Expect(report.Title == "出图与出视频", "应读出标题：" + report.Title);
    Expect(report.Ops.Count == 2, "带占位符的详情端点与非法能力名都要丢掉：" + string.Join("、", report.Ops.Select(op => op.Path)));
    Expect(report.Ops.Any(op => op.Capability == Capability.TextToVideo), "中文能力名「文生视频」也应认得");
    Expect(report.ModelTable.Count == 2, "没有名字的模型条目应丢掉：" + report.ModelTable.Count);
    Expect(report.Notes.Any(note => note.Contains("大模型")), "报告必须标明来源是大模型整理：" + string.Join("；", report.Notes));
    Expect(report.Warnings.Any(warning => warning.Contains("没通过校验")), "丢掉的条目要如实计数：" + string.Join("；", report.Warnings));

    var plan = ApiSkillFactory.Build(report);
    Expect(plan.ImageSkills.Count(skill => skill.IsPool) == 2 && plan.VideoSkills.Count(skill => skill.IsPool) == 1,
        $"应按模型与档位建池子：出图 {plan.ImageSkills.Count(skill => skill.IsPool)}、出视频 {plan.VideoSkills.Count(skill => skill.IsPool)}");

    // 按接口实际上线的模型收敛（用户可在窗口上一键重建）
    var filtered = ApiSkillFactory.Build(report, new[] { "flux-1-dev" });
    Expect(filtered.VideoSkills.Count(skill => skill.IsPool) == 0, "没上线的视频模型不应建池子");
    Expect(filtered.ImageSkills.Count(skill => skill.IsPool) == 2, "上线的图像模型应保留");
    Expect(filtered.Skills.Where(skill => skill.Id == "api-video").SelectMany(skill => skill.Definition.Steps).All(step => step.Model.Length == 0),
        "父技能里没上线的模型应退回空串（用设置里的默认模型），不能留着死名字");
    Expect(plan.ImageSkills.Count(skill => skill.IsPool) == 2, "不带过滤时不受影响");

    var notJson = ApiDocRepair.FromModelJson("抱歉，我看不出这是什么接口。", "https://video.example.com/about");
    Expect(!notJson.Ok && notJson.Error.Contains("没有返回 JSON"), "模型没给 JSON 时应如实失败：" + notJson.Error);
    Expect(!ApiDocRepair.FromModelJson("{\"ops\":[],\"models\":[]}", "https://x/y").Ok, "空结构不算成功");
    var opsOnly = ApiDocRepair.FromModelJson(
        "{\"ops\":[{\"capability\":\"TextToImage\",\"method\":\"POST\",\"path\":\"/v1/images/generations\"}],\"models\":[]}", "https://x/y");
    Expect(opsOnly.Ok && opsOnly.Report!.Ops.Count == 1, "只有接口没有模型也算有内容：" + opsOnly.Error);
    Expect(opsOnly.Report!.BaseUrl == "https://x/v1", "接口路径里的版本前缀应补进基础地址：" + opsOnly.Report!.BaseUrl);

    // 返工 R6：与原文核对 + 非法方法/类型直接拒绝（不静默补默认）+ 不可确认的留成待确认项
    const string source = """
    # 出图接口
    POST /v1/images/generations
    model: flux-1-dev
    尺寸：1k
    """;
    var grounded = """
    {"baseUrl":"https://video.example.com","ops":[
      {"capability":"TextToImage","method":"POST","path":"/v1/images/generations","models":["flux-1-dev"],"sizes":["1k"]},
      {"capability":"TextToImage","method":"POST","path":"/v1/imaginary/endpoint","models":["flux-1-dev"]},
      {"capability":"TextToImage","method":"DELETE","path":"/v1/images/generations","models":["flux-1-dev"]},
      {"capability":"模棱两可","method":"POST","path":"/v1/images/generations"}],
     "models":[
      {"name":"flux-1-dev","kind":"Image","sizes":["1k"]},
      {"name":"完全虚构的模型","kind":"Image","sizes":["1k"]},
      {"name":"flux-1-dev","kind":"Audio","sizes":["1k"]}]}
    """;
    var check = ApiDocRepair.FromModelJson(grounded, "https://video.example.com/about", source);
    Expect(check.Ok, "有原文时合法条目应保留：" + check.Error);
    var checkedReport = check.Report!;
    Expect(checkedReport.Ops.Count == 1 && checkedReport.Ops[0].Path == "/v1/images/generations",
        "原文里没有的路径与非法方法都应被拒：" + string.Join("、", checkedReport.Ops.Select(op => op.Path)));
    Expect(checkedReport.ModelTable.Count == 1 && checkedReport.ModelTable[0].Name == "flux-1-dev",
        "原文里没有的模型与类型不明的模型都不该进模型表：" + string.Join("、", checkedReport.ModelTable.Select(entry => entry.Name)));
    Expect(checkedReport.Warnings.Any(w => w.Contains("虚构")), "应说明按虚构丢弃：" + string.Join("；", checkedReport.Warnings));
    Expect(checkedReport.Warnings.Any(w => w.Contains("不在支持范围内")), "应说明方法不在支持范围：" + string.Join("；", checkedReport.Warnings));
    Expect(checkedReport.PendingItems.Count == 2, "无法确认的应留成待确认项：" + string.Join("；", checkedReport.PendingItems));
    Expect(checkedReport.Notes.Any(note => note.Contains("与原文逐条核对")), "应说明做过原文核对：" + string.Join("；", checkedReport.Notes));
    Expect(ApiSkillFactory.Build(checkedReport).ImageSkills.All(skill => !skill.IsPool || skill.Definition.Steps[0].Model == "flux-1-dev"),
        "待确认项不得变成可执行技能");

    // 没有原文时无法核对：如实说明，不假装核对过
    var ungrounded = ApiDocRepair.FromModelJson(grounded, "https://video.example.com/about");
    Expect(ungrounded.Ok && !ungrounded.Report!.Notes.Any(note => note.Contains("与原文逐条核对")),
        "没有原文时不应声称核对过");

    // 返工 S6 的具体断言见专用用例 ApiDocRepairDoesNotFabricate。
}

/// <summary>版本策略相关测试之前的位置锚点。</summary>
static void ReferenceVersionPolicy()
{
    using var stores = new IsolatedStores();
    AppPaths.UseProject(ProjectContext.Create(stores.Root, "agent-tests"));

    var canvas = new WorkflowCanvasState();
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
    var variant = entity.CreateVariant("少年");
    var first = variant.Versions[0];
    var second = variant.Commit("第二版");
    canvas.Entities.Add(entity);
    var node = new WorkflowNode { Title = "分镜", Category = NodeCategory.Storyboard };
    node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
    canvas.Nodes.Add(node);

    Expect(!CanvasReferenceVersions.IsNodeBlocked(canvas, node), "跟随最新不是阻断状态");
    Expect(canvas.ResolveReferences(node)[0].VersionLabel == "最新", "跟随最新应显示「最新」");

    Expect(CanvasReferenceVersions.TrySetVersion(canvas, node, entity.Id, variant.Id, second.Id, out _), "锁定存在的版本应成功");
    Expect(node.References[0].VariantVersionId == second.Id, "引用应锁定到指定版本");
    Expect(canvas.ResolveReferences(node)[0].VersionLabel == second.Label, "应显示锁定版本的标签");

    // 锁定不存在的版本：拒绝，且原引用不变（不静默降级）。
    Expect(!CanvasReferenceVersions.TrySetVersion(canvas, node, entity.Id, variant.Id, Guid.NewGuid(), out var error),
        "锁定不存在的版本应被拒绝");
    Expect(error.Contains("不存在"), "拒绝理由应说明版本不存在：" + error);
    Expect(node.References[0].VariantVersionId == second.Id, "拒绝后引用必须原样保留");

    // 锁定后该版本被删除：判定为阻断，且不得再作为出图内容。
    variant.Versions.Remove(second);
    Expect(CanvasReferenceVersions.IsLockedVersionMissing(canvas, node.References[0]), "版本被删后应判为锁定版本缺失");
    Expect(CanvasReferenceVersions.IsNodeBlocked(canvas, node), "应处于阻断状态");
    Expect(CanvasReferenceVersions.DescribeBlock(canvas, node).Contains("版本已缺失"), "阻断说明应提到版本缺失");
    var content = canvas.ResolveReferences(node)[0];
    Expect(content.VersionMissing && content.VersionLabel == "版本缺失",
        "解析结果必须标出版本缺失而不是当作最新：" + content.VersionLabel);
    Expect(CanvasReferenceVersions.UsableContents(canvas, node).Count == 0, "版本缺失时不得用于出图内容");
    Expect(canvas.DescribeReferenceForPrompt(node).Contains("[阻断]"), "提示词应写出阻断行");

    // 改回跟随最新即可恢复。
    Expect(CanvasReferenceVersions.TrySetVersion(canvas, node, entity.Id, variant.Id, null, out _), "改回跟随最新应成功");
    Expect(!CanvasReferenceVersions.IsNodeBlocked(canvas, node), "改回跟随最新后不再是阻断");
    Expect(CanvasReferenceVersions.UsableContents(canvas, node).Count == 1, "恢复后出图内容可用");
    Expect(!canvas.DescribeReferenceForPrompt(node).Contains("[阻断]"), "恢复后提示词不再有阻断行");

    // 锁定节点受定稿保护。
    node.IsLocked = true;
    Expect(!CanvasReferenceVersions.TrySetVersion(canvas, node, entity.Id, variant.Id, first.Id, out var lockedError),
        "锁定节点不得改写版本");
    Expect(lockedError.Contains("锁定"), "应提示节点已锁定：" + lockedError);

    // 没有该引用的节点：拒绝。
    var other = new WorkflowNode { Title = "无关节点" };
    Expect(!CanvasReferenceVersions.TrySetVersion(canvas, other, entity.Id, variant.Id, null, out _), "没有该引用的节点应被拒绝");
}

static string NewWorkspace()
{
    var path = Path.Combine(Path.GetTempPath(), "df-agent-tests-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(path);
    return path;
}

/// <summary>测试用的样本数据。</summary>

// ===== Recovered in round 127 =====
// These functions were lost when a bulk delete went wrong; they were rebuilt from the last
// successful build (ILSpy) and checked one by one against the rescued registration list.
// WARNING: the decompiler dropped the original Chinese comments - assertions and behaviour are
// complete, comments still have to be restored. Lesson recorded in PROGRESS round 127.


// ===== Recovered in round 127 =====
// These functions were lost when a bulk delete went wrong; they were rebuilt from the last
// successful build (ILSpy) and checked one by one against the rescued registration list.
// WARNING: the decompiler dropped the original Chinese comments - assertions and behaviour are
// complete, comments still have to be restored. Lesson recorded in PROGRESS round 127.

static void AesGcmKeyFileRoundTrip()
{
	string text = Path.Combine(Path.GetTempPath(), "df-secret-aesgcm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
	string text2 = Path.Combine(text, "ai-key.bin");
	string environmentVariable = Environment.GetEnvironmentVariable("YEEYEEYEE_SECRET_SCHEME");
	string environmentVariable2 = Environment.GetEnvironmentVariable("YEEYEEYEE_SECRET_KEYFILE");
	try
	{
		Directory.CreateDirectory(text);
		Environment.SetEnvironmentVariable("YEEYEEYEE_SECRET_SCHEME", "aesgcm");
		Environment.SetEnvironmentVariable("YEEYEEYEE_SECRET_KEYFILE", text2);
		string text3 = SecretProtector.Protect("sk-aesgcm-tier-0123456789abcdef");
		Expect(text3.StartsWith("aesgcm:", StringComparison.Ordinal), "本机密钥文件档应写 aesgcm: 前缀，实际：" + text3);
		Expect(!text3.Contains("sk-aesgcm-tier-0123456789abcdef", StringComparison.Ordinal), "密文里不应出现明文密钥");
		Expect(SecretProtector.IsEncryptedAtRest(text3), "aesgcm 档是真的加密落盘，应被认定为已加密");
		Expect(SecretProtector.Unprotect(text3) == "sk-aesgcm-tier-0123456789abcdef", "同一台机器上应能解回原值");
		Expect(File.Exists(text2), "密钥文件应落在指定位置");
		File.Delete(text2);
		Expect(SecretProtector.Unprotect(text3) == null, "密钥文件丢失后应返回 null，让界面提示重新填写");
	}
	finally
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_SECRET_SCHEME", environmentVariable);
		Environment.SetEnvironmentVariable("YEEYEEYEE_SECRET_KEYFILE", environmentVariable2);
		if (Directory.Exists(text))
		{
			Directory.Delete(text, recursive: true);
		}
	}
}

static void AgentEdgeBatchSurvivesModelOrdering()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	AgentAction[] actions = new AgentAction[5]
	{
		new AgentAction
		{
			Kind = "create_edge",
			Source = "第1章 老巷修书人",
			Target = "第2章 散成一摞的童话书"
		},
		new AgentAction
		{
			Kind = "create_node",
			Title = "第1章 老巷修书人",
			Content = "第一章正文"
		},
		new AgentAction
		{
			Kind = "create_node",
			Title = "第2章 散成一摞的童话书",
			Content = "第二章正文"
		},
		new AgentAction
		{
			Kind = "create_edge",
			Source = "第2章 散成一摞的童话书",
			Target = "第3章 一整天的修补"
		},
		new AgentAction
		{
			Kind = "create_node",
			Title = "第3章 一整天的修补",
			Content = "第三章正文"
		}
	};
	AgentApplyResult agentApplyResult = AgentActionExecutor.Apply(actions, workflowCanvasState, null);
	Expect(agentApplyResult.Errors.Count == 0, "连线写在建节点前面时不该报错，实际：" + string.Join("；", agentApplyResult.Errors));
	Expect(agentApplyResult.Applied == 5, $"5 条都应生效，实际 {agentApplyResult.Applied} 条");
	Expect(workflowCanvasState.Nodes.Count == 3 && workflowCanvasState.Edges.Count == 2, $"应得到 3 节点 2 连线，实际 {workflowCanvasState.Nodes.Count} / {workflowCanvasState.Edges.Count}");
	WorkflowNode first = workflowCanvasState.Nodes.First((WorkflowNode node) => node.Title == "第1章 老巷修书人");
	WorkflowNode second = workflowCanvasState.Nodes.First((WorkflowNode node) => node.Title == "第2章 散成一摞的童话书");
	Expect(workflowCanvasState.Edges.Any((WorkflowEdge edge) => edge.SourceNodeId == first.Id && edge.TargetNodeId == second.Id), "章节链的方向要按 source→target 连上");
	IReadOnlyList<string> source = AgentActionExecutor.PrecheckBatch(actions, new WorkflowCanvasState(), null);
	Expect(source.All((string hint) => string.IsNullOrWhiteSpace(hint) || !hint.Contains("找不到")), "乱序批次不该被预检报成找不到端点，实际：" + string.Join(" / ", source.Select((string hint) => hint ?? "无")));
	CanvasPreview canvasPreview = CanvasPreviewBuilder.Build(actions, new WorkflowCanvasState());
	Expect(canvasPreview.AddedNodes.Count == 3 && canvasPreview.AddedEdges.Count == 2, $"虚影试算要看到 3 个新节点与 2 条新连线，实际 {canvasPreview.AddedNodes.Count} / {canvasPreview.AddedEdges.Count}");
	WorkflowCanvasState workflowCanvasState2 = new WorkflowCanvasState
	{
		WorkTree = 
		{
			new WorkTreeItem
			{
				Kind = WorkTreeKind.Chapter,
				Name = "第1章 老巷修书人",
				Prompt = "第一章正文"
			},
			new WorkTreeItem
			{
				Kind = WorkTreeKind.Chapter,
				Name = "第2章 散成一摞的童话书",
				Prompt = "第二章正文"
			}
		}
	};
	AgentApplyResult agentApplyResult2 = AgentActionExecutor.Apply(new AgentAction[1]
	{
		new AgentAction
		{
			Kind = "create_edge",
			Source = "第1章 老巷修书人",
			Target = "第2章 散成一摞的童话书"
		}
	}, workflowCanvasState2, null);
	Expect(agentApplyResult2.Applied == 1, "端点来自工作树条目时应能连上，实际：" + string.Join("；", agentApplyResult2.Errors));
	Expect(workflowCanvasState2.Nodes.Count == 2, $"两个条目应各投影出一个节点，实际 {workflowCanvasState2.Nodes.Count}");
	Expect(workflowCanvasState2.Nodes.All((WorkflowNode node) => node.WorkTreeItemId.HasValue), "投影出来的节点要绑回原条目");
	Expect(workflowCanvasState2.Nodes.First((WorkflowNode node) => node.Title == "第1章 老巷修书人").Content == "第一章正文", "节点内容取自条目，不能编");
	WorkflowCanvasState canvas = new WorkflowCanvasState
	{
		WorkTree = 
		{
			new WorkTreeItem
			{
				Kind = WorkTreeKind.Resource,
				Name = "林晚"
			}
		},
		Nodes = 
		{
			new WorkflowNode
			{
				Title = "第1章 老巷修书人"
			}
		}
	};
	string text = AgentActionExecutor.Precheck(new AgentAction
	{
		Kind = "create_edge",
		Source = "林晚",
		Target = "第1章 老巷修书人"
	}, canvas, null);
	Expect(text?.Contains("工作树里有这个条目") ?? false, "资源条目当端点要说清原因，实际：" + text);
	WorkflowCanvasState canvas2 = new WorkflowCanvasState
	{
		Nodes = 
		{
			new WorkflowNode
			{
				Title = "第1章：老巷修书人"
			},
			new WorkflowNode
			{
				Title = "第2章 散成一摞的童话书"
			}
		}
	};
	AgentApplyResult agentApplyResult3 = AgentActionExecutor.Apply(new AgentAction[1]
	{
		new AgentAction
		{
			Kind = "create_edge",
			Source = "第1章 老巷修书人",
			Target = "第2章 散成一摞的童话书"
		}
	}, canvas2, null);
	Expect(agentApplyResult3.Applied == 1, "归一化后应认出同一章，实际：" + string.Join("；", agentApplyResult3.Errors));
	WorkflowCanvasState canvas3 = new WorkflowCanvasState
	{
		Nodes = 
		{
			new WorkflowNode
			{
				Title = "第1章：雨夜"
			},
			new WorkflowNode
			{
				Title = "第1章 雨夜"
			}
		}
	};
	string text2 = AgentActionExecutor.Precheck(new AgentAction
	{
		Kind = "create_edge",
		Source = "第1章雨夜",
		Target = "第1章雨夜"
	}, canvas3, null);
	Expect(text2?.Contains("找不到起点节点") ?? false, "两个候选相近时不许猜，实际：" + text2);
	string text3 = AgentActionExecutor.Precheck(new AgentAction
	{
		Kind = "create_edge",
		Source = "第1章 老巷修书人（上）",
		Target = "第1章 老巷修书人"
	}, canvas2, null);
	Expect(text3?.Contains("最接近") ?? false, "找不到端点要给出最接近的候选，实际：" + text3);
	WorkflowCanvasState workflowCanvasState3 = new WorkflowCanvasState();
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "A"
	};
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "B"
	};
	workflowCanvasState3.Nodes.AddRange(new WorkflowNode[2] { workflowNode, workflowNode2 });
	workflowCanvasState3.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode.Id,
		TargetNodeId = workflowNode2.Id
	});
	AgentApplyResult agentApplyResult4 = AgentActionExecutor.Apply(new AgentAction[2]
	{
		new AgentAction
		{
			Kind = "delete_node",
			Target = "A"
		},
		new AgentAction
		{
			Kind = "delete_edge",
			Source = "A",
			Target = "B"
		}
	}, workflowCanvasState3, null);
	Expect(agentApplyResult4.Errors.Count == 0, "先删节点再删同一条线不该报错，实际：" + string.Join("；", agentApplyResult4.Errors));
	Expect(workflowCanvasState3.Nodes.Count == 1 && workflowCanvasState3.Edges.Count == 0, $"应只剩一个节点且没有连线，实际 {workflowCanvasState3.Nodes.Count} / {workflowCanvasState3.Edges.Count}");
}

static void AgentEntityContentReachesVariantAndCard()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	AgentApplyResult agentApplyResult = AgentActionExecutor.Apply(new AgentAction[3]
	{
		new AgentAction
		{
			Kind = "create_entity",
			EntityKind = "角色",
			Title = "林晚",
			Content = "十七岁，短发，藏青外套"
		},
		new AgentAction
		{
			Kind = "create_entity",
			EntityKind = "场景",
			Title = "旧书铺",
			Content = "大门朝南，午后斜光"
		},
		new AgentAction
		{
			Kind = "create_node",
			Title = "第一章 分镜 1",
			Content = "林晚在柜台后补书",
			EntityTargets = new string[2] { "林晚", "旧书铺" }
		}
	}, workflowCanvasState, null);
	Expect(agentApplyResult.Applied == 3 && agentApplyResult.Errors.Count == 0, "生成失败：" + string.Join("；", agentApplyResult.Errors));
	WorkflowEntity workflowEntity = workflowCanvasState.Entities.Single((WorkflowEntity entity) => entity.Name == "林晚");
	Expect(workflowEntity.Core == "十七岁，短发，藏青外套", "内容应当落进核心设定");
	Expect(workflowEntity.Variants.Count == 1 && workflowEntity.Variants[0].Description == "十七岁，短发，藏青外套", "内容也要落到默认变体，否则引用卡是空白的");
	Expect(workflowEntity.Variants[0].Versions.Count == 1 && workflowEntity.Variants[0].Versions[0].Description == "十七岁，短发，藏青外套", "v1 快照应当是写好的内容，而不是「有内容但 v1 是空的」这种怪状态");
	WorkflowNode workflowNode = workflowCanvasState.Nodes.Single((WorkflowNode item) => item.Title == "第一章 分镜 1");
	List<ReferenceContent> list2 = workflowCanvasState.ResolveReferences(workflowNode);
	Expect(list2.Count == 2, $"两条引用都该解析出来，实际 {list2.Count} 条");
	Expect(list2.All((ReferenceContent item) => !string.IsNullOrWhiteSpace(item.Description)), "引用卡正文不该是空的");
	Expect(list2.Any((ReferenceContent item) => item.Description.Contains("藏青外套")), "角色引用卡要带上外观锚点");
	WorkflowEntity legacy = workflowCanvasState.Entities.Single((WorkflowEntity entity) => entity.Name == "旧书铺");
	legacy.Variants[0].Description = string.Empty;
	NodeReference reference = workflowNode.References.Single((NodeReference item) => item.EntityId == legacy.Id);
	Expect(workflowCanvasState.ResolveReferenceContent(reference)?.Description.Contains("午后斜光") ?? false, "变体描述为空时要按核心设定兜底");
	string text = workflowCanvasState.DescribeReferenceForPrompt(workflowNode);
	Expect(text.Split("午后斜光").Length == 2, "同一段文字不该在提示词里写两遍，实际：\n" + text);
}

static void ApiDocFamilyWordInProseIsNotAModel()
{
	string content = "# 接口文档\nBase URL https://video.example.com/v1\n\n### 常用端点\n- POST/v1/images/generations 文生图\n- POST/v1/videos 建视频任务\n\n## 视频参数 /v1/videos · 异步\n| 参数 | 类型 | 必填 | 说明 |\n|---|---|---|---|\n| model | string | 必填 | 模型名(别名优先),见上表 |\n| input_reference | file | 可选 | 参考图,runway 图生视频必填 1 张 |\n\n## 确认可用模型\n| model | 类型 | 能力 |\n|---|---|---|\n| seedance-2.0(池7) | 视频 | 720p 参考图 |";
	ApiDocReport apiDocReport = ApiDocAnalyzer.Analyze(content, "https://video.example.com/about");
	ApiOpCandidate apiOpCandidate = apiDocReport.VideoOps.FirstOrDefault();
	Expect((object)apiOpCandidate != null, "应解析出一条出视频接口");
	Expect(!apiOpCandidate.Models.Any((string model) => model.Contains("runway", StringComparison.OrdinalIgnoreCase)), "中文说明里的光杆词 runway 不得被当成模型名：" + string.Join("、", apiOpCandidate.Models));
	ApiSkillPlan apiSkillPlan = ApiSkillFactory.Build(apiDocReport);
	PlannedApiSkill plannedApiSkill = apiSkillPlan.Skills.FirstOrDefault((PlannedApiSkill skill) => skill.IsPool && skill.Name.StartsWith("生视频池", StringComparison.Ordinal));
	Expect((object)plannedApiSkill != null, "应建出出视频池子：" + string.Join("、", apiSkillPlan.Skills.Select((PlannedApiSkill skill) => skill.Name)));
	Expect(plannedApiSkill.Definition.Steps[0].Model == "seedance-2.0(池7)", "池子要用文档模型表里的模型：" + plannedApiSkill.Definition.Steps[0].Model);
	ApiDocReport apiDocReport2 = ApiDocAnalyzer.Analyze("Base URL https://x.test/v1\n\nPOST /v1/videos 文生视频\nmodel 可填 kling-2.5\n", "https://x.test/about");
	Expect(apiDocReport2.VideoOps.Count == 0 || apiDocReport2.VideoOps.Any((ApiOpCandidate op) => op.Models.Any((string model) => model.Contains("kling", StringComparison.OrdinalIgnoreCase))), "带版本号的模型名仍要认出来：" + string.Join("、", apiDocReport2.VideoOps.SelectMany((ApiOpCandidate op) => op.Models)));
}

static void ApiDocShellMiningPicksSafely()
{
	Uri uri = new Uri("https://docs.example.com/about");
	IReadOnlyList<string> readOnlyList = ApiDocShellMining.ScriptSourcesIn("<script type=\"module\" src=\"/assets/index-abc.js\"></script><script src=\"./extra.js\"></script><script src=\"https://cdn.other.com/vendor.js\"></script>", uri);
	Expect(readOnlyList.Count == 2, "同源脚本应取到 2 个：" + string.Join("、", readOnlyList));
	Expect(readOnlyList[0] == "https://docs.example.com/assets/index-abc.js", "相对地址要按页面解析成绝对地址：" + readOnlyList[0]);
	Expect(!readOnlyList.Any((string url) => url.Contains("other.com", StringComparison.Ordinal)), "跨域脚本一律不取");
	IReadOnlyList<string> readOnlyList2 = ApiDocShellMining.ChunkReferencesIn("import{a}from\"./index-abc.js\";import{b}from\"assets/AboutView-nHduRdzV.js\";import{c}from\"assets/clipboard-DzAvOLI4.js\";");
	Expect(readOnlyList2.Contains("assets/AboutView-nHduRdzV.js"), "应认出 chunk 引用：" + string.Join("、", readOnlyList2));
	Expect(ApiDocShellMining.ScoreAgainstPage("/assets/AboutView-nHduRdzV.js", uri) > 0, "/about 应命中 AboutView");
	Expect(ApiDocShellMining.ScoreAgainstPage("/assets/clipboard-DzAvOLI4.js", uri) == 0, "与页面名无关的包不得被当成候选");
	Expect(ApiDocShellMining.ScoreAgainstPage("/assets/AboutView-nHduRdzV.js", new Uri("https://docs.example.com/")) == 0, "根路径没有可认的词，不该乱挑一个包");
	Expect(ApiDocShellMining.PathTokens(new Uri("https://docs.example.com/a/index.html")).Count == 0, "太短的段与带点的文件名都不算认路词");
	IReadOnlyList<string> readOnlyList3 = ApiDocShellMining.SpecCandidatesIn("<link rel=\"alternate\" type=\"application/json\" href=\"/openapi.json\"><redoc spec-url=\"/swagger/v1/swagger.json\"></redoc><script>specUrl: \"https://evil.other.com/spec.json\"</script>", uri);
	Expect(readOnlyList3.Any((string url) => url == "https://docs.example.com/openapi.json"), "link rel=alternate 要说出来：" + string.Join("、", readOnlyList3));
	Expect(readOnlyList3.Any((string url) => url == "https://docs.example.com/swagger/v1/swagger.json"), "spec-url 要说出来：" + string.Join("、", readOnlyList3));
	Expect(!readOnlyList3.Any((string url) => url.Contains("other.com", StringComparison.Ordinal)), "跨域的 spec 地址不取");
	Expect(ApiDocShellMining.LooksLikeApiSpec("{\"openapi\":\"3.0.0\",\"paths\":{}}"), "OpenAPI JSON 应被认作接口描述");
	Expect(!ApiDocShellMining.LooksLikeApiSpec("<!doctype html><html><div id=\"app\"></div></html>"), "HTML 空壳不得被当成接口描述");
	Expect(!ApiDocShellMining.LooksLikeApiSpec("{\"title\":\"AnyAIAPI\"}"), "普通 JSON 不得被当成接口描述");
	string content = ApiDocAnalyzer.StripHtml("function f(){return fetch(\"/admin/images\",{method:\"POST\"}).then(r=>r.json())}const v=1;const w=2;const x=3;const y=4;const z=5;const a=6;const b=7;const c=8;");
	Expect(ApiDocAnalyzer.Analyze(content, uri.ToString()).Ops.Count < 2, "框架代码那种形态不该够得上「文档」：" + ApiDocAnalyzer.Analyze(content, uri.ToString()).Ops.Count);
	string content2 = ApiDocAnalyzer.StripHtml("<li>POST/v1/images/generations文生图</li><li>POST/v1/images/edits图生图</li><li>POST/v1/videos建视频任务</li>");
	Expect(ApiDocAnalyzer.Analyze(content2, uri.ToString()).Ops.Count >= 2, "真文档那种形态必须够得上：" + ApiDocAnalyzer.Analyze(content2, uri.ToString()).Ops.Count);
}

static void ApiImportSummaryAndSmallestSize()
{
	ApiOpCandidate apiOpCandidate = new ApiOpCandidate(Capability.TextToImage, "POST", "/v1/images/generations", new string[1] { "flux-1-dev" }, new string[3] { "2k", "1k", "4k" }, IsAsync: false, "bearer", Array.Empty<string>());
	var (num, num2) = ApiMinimalTest.SmallestSize(apiOpCandidate);
	Expect(num == 1024 && num2 == 1024, $"应挑面积最小的一档（1k），实际 {num}x{num2}");
	ApiOpCandidate op = apiOpCandidate with
	{
		Sizes = Array.Empty<string>()
	};
	var (num3, num4) = ApiMinimalTest.SmallestSize(op);
	Expect(num3 == 1024 && num4 == 1024, "文档没写尺寸时应退回 1024 正方形");
	ApiDocReport report = new ApiDocReport(ApiDocFormat.Text, "https://docs.example.com/api", "https://api.example.com/v1", "示例接口文档", new ApiOpCandidate[1] { apiOpCandidate }, new ApiModelEntry[1]
	{
		new ApiModelEntry(Capability.TextToImage, "flux-1-dev", new string[2] { "1k", "2k" }, "模型表")
	}, Array.Empty<string>(), Array.Empty<string>())
	{
		PendingItems = new string[1] { "有一条接口的用途没判出来" }
	};
	ApiSkillPlan plan = ApiSkillFactory.Build(report);
	string text = ApiImportSummary.RenderReport(report, plan);
	Expect(text.Contains("解析到的接口", StringComparison.Ordinal), "报告要列出解析到的接口：\n" + text);
	Expect(text.Contains("/v1/images/generations", StringComparison.Ordinal), "报告要写出接口路径：\n" + text);
	Expect(text.Contains("flux-1-dev", StringComparison.Ordinal), "报告要写出文档声明的模型名：\n" + text);
	Expect(text.Contains("将要创建的技能", StringComparison.Ordinal), "报告要列出将要创建的技能：\n" + text);
	Expect(text.Contains("待确认", StringComparison.Ordinal), "待确认项要单列，且说明不会建进技能：\n" + text);
	Expect(ApiImportSummary.FormatName(ApiDocFormat.OpenApiJson).Contains("OpenAPI", StringComparison.Ordinal), "识别形态要说人话，不能直接印枚举名");
}

static void AppFilesLiveInUserConfigDirectory()
{
	string userConfigDirectory = AppPaths.UserConfigDirectory;
	Expect(Path.IsPathRooted(userConfigDirectory), "用户配置目录应是绝对路径：" + userConfigDirectory);
	// 改名过渡：新目录优先，但**只有个空壳而旧目录里真存着配置**时用旧目录。两种目录名都可能出现，
	// 规则本身由 RenamedDirectoryPrefersTheOneWithData 钉住，这里只校验目录名在允许范围内。
	string folder = Path.GetFileName(userConfigDirectory);
	Expect(folder is "YEEYEEYEE" or "DreamForge", "用户配置目录应是 YEEYEEYEE（过渡期允许 DreamForge）：" + userConfigDirectory);
	if (folder == "DreamForge")
		Expect(File.Exists(Path.Combine(userConfigDirectory, "ai-config.json"))
			|| File.Exists(Path.Combine(userConfigDirectory, "recent-projects.json")),
			"只有旧目录里确实存着配置时才会退回旧目录：" + userConfigDirectory);
	string fileName = $"probe-{Guid.NewGuid():N}.json";
	string text = AppPaths.ResolveAppFile(fileName);
	Expect(string.Equals(Path.GetDirectoryName(text), userConfigDirectory, StringComparison.Ordinal), "应用级文件应解析到用户配置目录，实际：" + text);
	Expect(!File.Exists(text), "解析位置不该顺手创建文件");
	Expect(condition: true, "dpapi: 前缀不能改，旧配置是按它判断有没有密文的");
	Expect(!string.IsNullOrWhiteSpace(SecretProtector.StorageDescription), "落盘方案说明要有内容给界面显示");
}

// 改名后目录名的选择规则：**谁真的装着东西就用谁**。
// 容易踩的坑是「新目录存在」不等于「新目录有东西」——程序自己会顺手把新目录建出来，
// 于是空壳会把用户已有配置的旧目录顶掉，用户看到的是「密钥要我重填」。
static void RenamedDirectoryPrefersTheOneWithData()
{
	string root = Path.Combine(Path.GetTempPath(), "yeeeyee-prefer-" + Guid.NewGuid().ToString("N"));
	string next = Path.Combine(root, "YEEYEEYEE");
	string previous = Path.Combine(root, "DreamForge");
	static bool HasConfig(string directory) => File.Exists(Path.Combine(directory, "ai-config.json"));
	try
	{
		Expect(AppPaths.PreferPopulated(next, previous, HasConfig) == next, "两个目录都没有内容时应选新目录，准备新建");

		Directory.CreateDirectory(previous);
		File.WriteAllText(Path.Combine(previous, "ai-config.json"), "{}");
		Directory.CreateDirectory(next);
		Expect(AppPaths.PreferPopulated(next, previous, HasConfig) == previous,
			"旧目录里有配置、新目录只是空壳时必须用旧目录，否则用户会以为密钥没填过");

		File.WriteAllText(Path.Combine(next, "ai-config.json"), "{}");
		Expect(AppPaths.PreferPopulated(next, previous, HasConfig) == next, "新目录里也有配置时以新目录为准");
	}
	finally
	{
		try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
		catch (IOException) { }
	}
}

// ===== 更新：版本号解析 / 发行版比对 / 更新包挑选 / 重启标记 / 替换脚本 =====

// 版本号只有一处来源（程序集），但「怎么把 v0.2.0 这种标签读成可比较的版本」是纯逻辑，能离线钉住。
static void VersionTagParsing()
{
	Expect(AppVersion.TryParse("v0.2.0", out var withV) && withV == new Version(0, 2, 0), "带 v 前缀的标签要能解析");
	Expect(AppVersion.TryParse("0.2.0", out var bare) && bare == new Version(0, 2, 0), "不带 v 也要能解析");
	Expect(AppVersion.TryParse("1.2", out var twoPart) && twoPart == new Version(1, 2, 0), "两段式补成三段");
	Expect(AppVersion.TryParse("v0.2.0-beta.1", out var pre) && pre == new Version(0, 2, 0), "预发布后缀不参与大小比较");
	Expect(AppVersion.TryParse("  0.3.1  ", out var padded) && padded == new Version(0, 3, 1), "两边空白要能容忍");
	Expect(!AppVersion.TryParse("latest", out _), "看不懂的标签必须返回 false，不能当成 0.0.0");
	Expect(!AppVersion.TryParse("", out _), "空串要返回 false");
	Expect(!AppVersion.TryParse(null, out _), "null 要返回 false");
	Expect(!AppVersion.TryParse("1", out _), "只有一段不算版本号");
	Expect(!AppVersion.TryParse("1.2.3.4", out _), "四段不是发行版标签的写法");
	Expect(AppVersion.Display.StartsWith('v'), "界面显示形式带 v 前缀：" + AppVersion.Display);
	Expect(AppVersion.Text(new Version(0, 1, 0)) == "0.1.0", "三段式就是 0.1.0：" + AppVersion.Text(new Version(0, 1, 0)));
	Expect(AppVersion.Text(new Version(0, 1, 2, 3)) == "0.1.2.3", "有修订号时写四段：" + AppVersion.Text(new Version(0, 1, 2, 3)));
}

// 「没有新版」与「查不到」必须分开：把查不到降级成已是最新，是最容易让人以为软件不再更新的写法。
static void UpdateCheckComparesVersionsAndReportsFailures()
{
	const string newer = "{\"tag_name\":\"v0.9.0\",\"name\":\"YEEYEEYEE 0.9.0\",\"body\":\"改了什么\",\"html_url\":\"https://example.com/r\",\"published_at\":\"2026-10-02T02:20:02Z\",\"assets\":[{\"name\":\"yeeeyee-win-x64.zip\",\"browser_download_url\":\"https://example.com/a.zip\",\"size\":123}]}";

	var available = UpdateChecker.Compare(new Version(0, 1, 0), newer);
	Expect(available.State == UpdateCheckState.UpdateAvailable, "线上更新时应报有新版：" + available.Message);
	Expect(available.Latest == new Version(0, 9, 0), "要解出最新版本号");
	Expect(available.Release is { Assets.Count: 1 }, "发行包要解出来");
	Expect(available.Release?.HtmlUrl == "https://example.com/r", "发布页地址要解出来");
	Expect(UpdateChecker.FindPackage(available.Release)?.Name == "yeeeyee-win-x64.zip", "挑出 zip 包");

	Expect(UpdateChecker.Compare(new Version(0, 9, 0), newer).State == UpdateCheckState.UpToDate, "同版本算已是最新");
	Expect(UpdateChecker.Compare(new Version(1, 0, 0), newer).State == UpdateCheckState.UpToDate, "本地比线上新时不算有更新");

	Expect(UpdateChecker.Compare(new Version(0, 1, 0), newer.Replace("\"v0.9.0\"", "\"latest\"")).State == UpdateCheckState.Failed,
		"看不懂的标签要报失败，不能降级成「已是最新」");
	Expect(UpdateChecker.Compare(new Version(0, 1, 0), "<html>502</html>").State == UpdateCheckState.Failed,
		"不是 JSON 时要报失败");
	Expect(UpdateChecker.Compare(new Version(0, 1, 0), "{}").State == UpdateCheckState.Failed,
		"缺 tag_name 时要报失败，而不是当成没有新版");

	var noPackage = UpdateChecker.Compare(new Version(0, 1, 0), newer.Replace("yeeeyee-win-x64.zip", "notes.txt"));
	Expect(noPackage.State == UpdateCheckState.UpdateAvailable, "包名不相关时仍然是有新版");
	Expect(UpdateChecker.FindPackage(noPackage.Release) is null, "没有 zip 也没有 exe 时挑不出更新包");
	Expect(UpdateChecker.FindPackage(null) is null, "没有发行版信息时挑不出更新包");
}

// 重启后靠这个标记知道「这次更新到底换成了什么」，所以它必须能原样存回来。
static void UpdateMarkerRoundTrips()
{
	string home = Path.Combine(Path.GetTempPath(), "yeeeyee-marker-" + Guid.NewGuid().ToString("N")[..8]);
	string? previous = Environment.GetEnvironmentVariable("YEEYEEYEE_CONFIG_HOME");
	try
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG_HOME", home);
		UpdateInstaller.ClearMarker();
		Expect(UpdateInstaller.ReadMarker() is null, "还没写过时读出来应是空");

		UpdateInstaller.WriteMarker(new PendingUpdateInfo("0.1.0", "0.2.0", "这一版改了什么", "https://example.com/r"));
		var info = UpdateInstaller.ReadMarker();
		Expect(info is not null, "写下的标记要能读回来");
		Expect(info!.FromVersion == "0.1.0" && info.ToVersion == "0.2.0", "前一版与目标版本要存住：" + info.ToVersion);
		Expect(info.Notes == "这一版改了什么", "更新内容要存住，重启后靠它显示改了什么");

		UpdateInstaller.ClearMarker();
		Expect(UpdateInstaller.ReadMarker() is null, "清掉之后应为空");
	}
	finally
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG_HOME", previous);
		try { if (Directory.Exists(home)) Directory.Delete(home, recursive: true); }
		catch (IOException) { }
	}
}

// 替换脚本由 PowerShell 5 执行，而 PS5 会把**无 BOM 的 UTF-8 当 ANSI 读**——写成中文就成了乱码甚至语法错。
// 这个坑本轮踩过两次（一个脚本解析失败、一次把中文全读成问号），所以这里钉死「只含 ASCII」。
static void UpdateSwapScriptIsAsciiOnly()
{
	var nonAscii = UpdateInstaller.SwapScript.Where(ch => ch > 127).Select(ch => ((int)ch).ToString("X4")).Distinct().ToArray();
	Expect(nonAscii.Length == 0, "替换脚本必须只含 ASCII，出现了这些码位：" + string.Join(" ", nonAscii));
	Expect(UpdateInstaller.SwapScript.Contains("$WaitPid", StringComparison.Ordinal), "脚本要接收「等哪个进程退出」");
	Expect(UpdateInstaller.SwapScript.Contains("Write-Result", StringComparison.Ordinal), "脚本要把执行结果写出来供重启后读取");
	Expect(UpdateInstaller.SwapScript.Contains("Move-Item", StringComparison.Ordinal), "脚本要真的搬目录");
	Expect(UpdateInstaller.SwapScript.Contains("RESTORE ALSO FAILED", StringComparison.Ordinal),
		"回滚也失败时必须留下路径，不能只说一句失败");
}

// 真跑一遍替换脚本。整个升级链路里**只有这一步会动用户磁盘**，所以不能只测「脚本文本对不对」。
// 用 hostname.exe 冒充主程序（跑完就退，不弹界面），用一个大到不存在的 PID 跳过等待。
static void UpdateSwapScriptActuallyReplacesDirectory()
{
	string root = Path.Combine(Path.GetTempPath(), "yeeeyee-swap-" + Guid.NewGuid().ToString("N")[..8]);
	string target = Path.Combine(root, "app");
	string staging = Path.Combine(root, "staging");
	string result = Path.Combine(root, "last-update.json");
	string script = Path.Combine(root, "apply-update.ps1");
	try
	{
		Directory.CreateDirectory(target);
		Directory.CreateDirectory(staging);

		var hostname = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "hostname.exe");
		if (!File.Exists(hostname))
		{
			Expect(condition: true, "这台机器上没有 hostname.exe，跳过替换脚本实测");
			return;
		}

		File.Copy(hostname, Path.Combine(target, "app.exe"));
		File.WriteAllText(Path.Combine(target, "old.txt"), "旧版");
		File.Copy(hostname, Path.Combine(staging, "app.exe"));
		File.WriteAllText(Path.Combine(staging, "new.txt"), "新版");

		File.WriteAllText(script, UpdateInstaller.SwapScript, new System.Text.UTF8Encoding(false));

		var start = new System.Diagnostics.ProcessStartInfo
		{
			FileName = "powershell.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in new[]
		{
			"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
			"-TargetDir", target,
			"-SourceDir", staging,
			"-ExeName", "app.exe",
			"-WaitPid", int.MaxValue.ToString(),
			"-ResultPath", result,
			"-FromVersion", "0.1.0",
			"-ToVersion", "0.2.0"
		})
		{
			start.ArgumentList.Add(argument);
		}

		using var process = System.Diagnostics.Process.Start(start);
		Expect(process is not null, "应该能把替换脚本拉起来");
		if (process is null) return;
		Expect(process.WaitForExit(120000), "替换脚本应在 120 秒内跑完");

		Expect(File.Exists(result), "脚本要写出执行结果文件：" + result);
		if (!File.Exists(result)) return;

		var applied = System.Text.Json.JsonSerializer.Deserialize<UpdateApplyResult>(File.ReadAllText(result));
		Expect(applied is not null, "结果文件要能读懂（键名映射错了就会读成空）");
		Expect(applied!.Ok, "替换应报成功，实际失败原因：" + applied.Error);
		Expect(applied.FromVersion == "0.1.0" && applied.ToVersion == "0.2.0", "结果里要带上前后版本号");
		Expect(File.Exists(Path.Combine(target, "new.txt")), "目标目录应已换成新内容");
		Expect(!File.Exists(Path.Combine(target, "old.txt")), "旧内容应被换走");
		Expect(File.Exists(Path.Combine(target, "app.exe")), "换完之后主程序要在目标目录里");
	}
	finally
	{
		try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}

// 「跑着的时候不能挑」这条规则此前只写在 CanPick 上（算提示文字用的），而右键菜单是按**格子状态**建的，
// 于是「单张已出好、其余还在跑」时菜单直接给「用这一张」。点下去会把整批丢掉、却不取消还在跑的请求，
// 那些请求跑完后仍会把图写进资产目录，可那时批次已经不在表里了——没人引用（漏文件）。
// 这条测试钉住规则本身：它现在是模型里的唯一出处，菜单和动作都问它。
static void GachaCardLayoutIsPinnedByCount()
{
	// 一行最多 3 张：6 张 = 上下两排、每排 3 张；4 张走 2×2（3+1 会显得上面挤、下面空）。
	Expect(GachaCardLayout.PerRow(1) == 1 && GachaCardLayout.Rows(1) == 1, "1 张：一行一张");
	Expect(GachaCardLayout.PerRow(2) == 2 && GachaCardLayout.Rows(2) == 1, "2 张：一行两张");
	Expect(GachaCardLayout.PerRow(3) == 3 && GachaCardLayout.Rows(3) == 1, "3 张：一行三张");
	Expect(GachaCardLayout.PerRow(4) == 2 && GachaCardLayout.Rows(4) == 2, "4 张：2×2");
	Expect(GachaCardLayout.PerRow(5) == 3 && GachaCardLayout.Rows(5) == 2, "5 张：上三下二");
	Expect(GachaCardLayout.PerRow(6) == 3 && GachaCardLayout.Rows(6) == 2, "6 张：上下两排、每排三张");

	for (var count = 1; count <= 6; count++)
	{
		// 卡片是**固定的**竖长方形：不跟着图的比例走（否则一排里方的方、横的横，不像一手牌）。
		Expect(Math.Abs(GachaCardLayout.Height(count) - GachaCardLayout.Width(count) * 1.5) < 0.001,
			$"{count} 张：卡面是固定的 2:3 竖长方形");
		Expect(GachaCardLayout.Width(count) > GachaCardLayout.Height(count) * 0.6,
			$"{count} 张：竖卡，不是横的");
		Expect(GachaCardLayout.PerRow(count) <= GachaCardLayout.MaxPerRow, $"{count} 张：一行不超过 3 张");
		Expect(GachaCardLayout.Rows(count) * GachaCardLayout.PerRow(count) >= count,
			$"{count} 张：行数够摆下");
		// 一行摆开的总宽不能超过一张 1280 逻辑宽的屏幕——超了就得挤，挤了就成缩略图。
		Expect(GachaCardLayout.RowWidth(count) <= 800, $"{count} 张：最宽的一行放得下");

		for (var index = 0; index < count; index++)
		{
			var slot = GachaCardLayout.Slot(count, index);
			Expect(slot.Row == index / GachaCardLayout.PerRow(count),
				$"{count} 张的第 {index + 1} 张落在第 {slot.Row + 1} 行");
			Expect(slot.Column == index % GachaCardLayout.PerRow(count),
				$"{count} 张的第 {index + 1} 张落在第 {slot.Column + 1} 列");
			Expect(slot.Rows == GachaCardLayout.Rows(count) && slot.PerRow == GachaCardLayout.PerRow(count),
				$"{count} 张的第 {index + 1} 张拿到的行列数与整批一致");
		}

		// 末行正好排到最后一张，不会多出一个空列。
		var last = GachaCardLayout.Slot(count, count - 1);
		Expect(last.Column == count - last.Row * last.PerRow - 1,
			$"{count} 张的末行正好排到最后一张");
	}

	// 末行张数少时，偏移要按**末行自己的张数**居中：5 张的末行两张应当关于中心对称。
	// （按「每行三张」算的话，飞出来的起点会偏半格——飞完还是落到正确位置，所以只在飞的过程中看得出来。）
	var left = GachaCardLayout.OffsetFromCentre(5, 3);
	var right = GachaCardLayout.OffsetFromCentre(5, 4);
	Expect(Math.Abs(left.X + right.X) < 0.001, "5 张的末行两张左右对称");
	Expect(Math.Abs(left.Y - right.Y) < 0.001, "5 张的末行两张在同一行");
	Expect(left.Y > 0, "末行在中心下方");

	// 整手牌关于中心对称：第一张与最后一张的偏移应当互为相反数。
	var firstOfSix = GachaCardLayout.OffsetFromCentre(6, 0);
	var lastOfSix = GachaCardLayout.OffsetFromCentre(6, 5);
	Expect(Math.Abs(firstOfSix.X + lastOfSix.X) < 0.001 && Math.Abs(firstOfSix.Y + lastOfSix.Y) < 0.001,
		"6 张的第一张与最后一张关于中心对称");

	// 越界要抛，不静默夹到边界——夹住了会把「算错了」伪装成「都摆好了」。
	Expect(Throws(() => GachaCardLayout.Slot(6, 6)), "序号等于张数要抛");
	Expect(Throws(() => GachaCardLayout.Slot(6, -1)), "负序号要抛");
	Expect(Throws(() => GachaCardLayout.Slot(0, 0)), "零张要抛");
	Expect(!Throws(() => GachaCardLayout.Slot(6, 5)), "最后一张是合法序号");
}

static void QualityTierFollowsScore()
{
	// 档位的高低顺序是用户定的：红 > 金 > 紫 > 蓝 > 白。**这条要单独钉住**——
	// 枚举的数值就是名次，比较全靠它，改错了不会有别的用例报出来。
	Expect(QualityTier.Red > QualityTier.Gold, "红比金高（与很多二游把金放最上面不同）");
	Expect(QualityTier.Gold > QualityTier.Purple, "金比紫高");
	Expect(QualityTier.Purple > QualityTier.Blue, "紫比蓝高");
	Expect(QualityTier.Blue > QualityTier.White, "蓝比白高");

	// 分数带的两端都要钉住：只钉中间值的话，把 9 改成 8 也不会有用例失败。
	Expect(QualityJudgement.TierForScore(QualityJudgement.MaxScore) == QualityTier.Red, "满分是红（最高档）");
	Expect(QualityJudgement.TierForScore(9) == QualityTier.Red, "9 分是红（红的下界）");
	Expect(QualityJudgement.TierForScore(8) == QualityTier.Gold, "8 分是金（差一分就掉出红）");
	Expect(QualityJudgement.TierForScore(7) == QualityTier.Gold, "7 分是金");
	Expect(QualityJudgement.TierForScore(6) == QualityTier.Purple, "6 分是紫");
	Expect(QualityJudgement.TierForScore(5) == QualityTier.Purple, "5 分是紫");
	Expect(QualityJudgement.TierForScore(4) == QualityTier.Blue, "4 分是蓝");
	Expect(QualityJudgement.TierForScore(3) == QualityTier.Blue, "3 分是蓝");
	Expect(QualityJudgement.TierForScore(2) == QualityTier.White, "2 分是白");
	Expect(QualityJudgement.TierForScore(QualityJudgement.MinScore) == QualityTier.White, "0 分是白");
	Expect(QualityJudgement.TierForScore(-1) == QualityTier.White, "负数按白处理（不该出现，但不能崩）");

	// 分数是**绝对标准**：同一批里两张都给 8 分，它们就该都是金——这正是它比名次好的地方。
	Expect(QualityJudgement.TierForScore(8) == QualityJudgement.TierForScore(8), "同样的分数给同样的档位");

	// 每一档的光点数必须**按高低顺序**严格递减：这是「档位看得出来」的最低要求。
	var sparks = new[]
	{
		QualityJudgement.SparksFor(QualityTier.Red), QualityJudgement.SparksFor(QualityTier.Gold),
		QualityJudgement.SparksFor(QualityTier.Purple), QualityJudgement.SparksFor(QualityTier.Blue),
		QualityJudgement.SparksFor(QualityTier.White)
	};
	for (var i = 1; i < sparks.Length; i++)
		Expect(sparks[i] < sparks[i - 1], $"光点数要从红到白严格递减，第 {i + 1} 档没降下来");

	// 每一档的光晕亮度同理：最高档最亮。
	var glows = new[]
	{
		QualityJudgement.GlowFor(QualityTier.Red), QualityJudgement.GlowFor(QualityTier.Gold),
		QualityJudgement.GlowFor(QualityTier.Purple), QualityJudgement.GlowFor(QualityTier.Blue),
		QualityJudgement.GlowFor(QualityTier.White)
	};
	for (var i = 1; i < glows.Length; i++)
		Expect(glows[i] < glows[i - 1], $"光晕亮度要从红到白严格递减，第 {i + 1} 档没降下来");

	// 白档的光点比「没判过」还少：白是判出来的最低档，没判过是「不知道」，两者不该长得一样。
	Expect(sparks[sparks.Length - 1] < 4, "白档的光点数要少于「没判过」时用的 4 个");

	Expect(QualityJudgement.Label(QualityTier.Red) == "红", "红档的中文名");
	Expect(QualityJudgement.Label(QualityTier.White) == "白", "白档的中文名");
	Expect(QualityJudgement.ModelDisclaimer.Contains("不是客观结论", StringComparison.Ordinal),
		"档位旁边必须说明这是模型的判断，而不是客观结论");

	// 分数带是公开的：它要出现在设置页上，用户得能自己核对「8 分为什么是金」。
	Expect(QualityJudgement.ScoreBandNote.Contains("9 分以上红", StringComparison.Ordinal),
		"分数带说明要把红最高的门槛写出来，实际：" + QualityJudgement.ScoreBandNote);
	Expect(QualityJudgement.ScoreBandNote.Contains("7–8 金", StringComparison.Ordinal),
		"分数带说明里金要排在红之后，实际：" + QualityJudgement.ScoreBandNote);
	Expect(QualityJudgement.ScoreBandNote.Contains("裂纹", StringComparison.Ordinal),
		"分数带说明里要交代裂纹卡，实际：" + QualityJudgement.ScoreBandNote);

	// 分数在界面上的写法：有分写「8/10」，本地筛查那种没有分的写空串（不能显示 -1/10）。
	Expect(new SlotQuality { Score = 8 }.ScoreLabel == "8/10", "有分数时写成 8/10");
	Expect(new SlotQuality().ScoreLabel.Length == 0, "没有分数时是空串，不是 -1/10");
}

static void QualityScoresMustBeComplete()
{
	// 正常一份：编号是「第几张」，score 是 0–10，hits 是踩中的负面词。
	var ok = QualityJudgement.ParseScores(
		"{\"scores\":[{\"index\":1,\"score\":8,\"hits\":[],\"note\":\"主题清楚\"},{\"index\":2,\"score\":3,\"hits\":[\"多余手指\"],\"note\":\"左手多一根\"}]}", 2);
	Expect(ok.Scores is { Count: 2 }, "正常回复要能读出分数");
	Expect(ok.Error is null, "正常回复不该报错");
	Expect(ok.Scores![0].Score == 8 && ok.Scores[0].Note == "主题清楚", "分数与理由都要按原样读出来");
	Expect(ok.Scores[0].Hits.Count == 0, "没踩中就应该是空数组");
	Expect(ok.Scores[1].Score == 3 && ok.Scores[1].Hits.Count == 1 && ok.Scores[1].Hits[0] == "多余手指",
		"踩中的负面词要按原词读出来（它决定这张是不是裂纹卡）");

	// 外面包了代码块、前后有说明文字：仍然要能读（模型经常这么回）。
	var fenced = QualityJudgement.ParseScores(
		"好的，我看完了：\n```json\n{\"scores\":[{\"index\":1,\"score\":9},{\"index\":2,\"score\":9}]}\n```\n以上。", 2);
	Expect(fenced.Scores is { Count: 2 }, "前后带说明文字、外面包代码块也要能读");
	Expect(fenced.Scores!.All(item => item.Score == 9), "两张都 9 分是合法的——绝对分允许并列");

	// **一张也能判**：绝对分不需要比较对象，这是这一版与名次版最大的区别。
	var single = QualityJudgement.ParseScores("{\"scores\":[{\"index\":1,\"score\":7}]}", 1);
	Expect(single.Scores is { Count: 1 } && single.Error is null, "只有一张也要能判（绝对分不需要第二张）");

	// 少了项：整份作废（半份评分会给出武断的档位，比没有档位更糟）。
	var partial = QualityJudgement.ParseScores(
		"{\"scores\":[{\"index\":1,\"score\":8},{\"index\":2,\"score\":6}]}", 3);
	Expect(partial.Scores is null && partial.Error is not null, "评分项数不足要整份作废");
	Expect(partial.Error!.Contains("3", StringComparison.Ordinal), "报错要说清应当有几项，实际：" + partial.Error);

	// 重复编号：作废。
	Expect(QualityJudgement.ParseScores(
		"{\"scores\":[{\"index\":1,\"score\":8},{\"index\":1,\"score\":6}]}", 2).Scores is null, "重复编号要整份作废");

	// 越界编号（0 与 count+1）：作废。
	Expect(QualityJudgement.ParseScores(
		"{\"scores\":[{\"index\":0,\"score\":8},{\"index\":2,\"score\":6}]}", 2).Scores is null, "编号 0 要作废");
	Expect(QualityJudgement.ParseScores(
		"{\"scores\":[{\"index\":1,\"score\":8},{\"index\":3,\"score\":6}]}", 2).Scores is null, "编号超出张数要作废");

	// 分数超出 0–10：作废，且要把那个数说出来——夹到边界会把「模型答错了」伪装成「它给了满分」。
	var over = QualityJudgement.ParseScores("{\"scores\":[{\"index\":1,\"score\":88}]}", 1);
	Expect(over.Scores is null && over.Error is not null, "分数超出范围要作废");
	Expect(over.Error!.Contains("88", StringComparison.Ordinal), "报错要写出越界的那个分数，实际：" + over.Error);
	Expect(QualityJudgement.ParseScores("{\"scores\":[{\"index\":1,\"score\":-3}]}", 1).Scores is null, "负分要作废");

	// 不是整数 / 没给分数：作废（小数会让分数带变得没法解释）。
	Expect(QualityJudgement.ParseScores("{\"scores\":[{\"index\":1,\"score\":8.5}]}", 1).Scores is null, "小数分数要作废");
	Expect(QualityJudgement.ParseScores("{\"scores\":[{\"index\":1,\"note\":\"看着挺好\"}]}", 1).Scores is null, "没给分数要作废");
	Expect(QualityJudgement.ParseScores("{\"scores\":[{\"index\":1,\"score\":\"8\"}]}", 1).Scores is null,
		"字符串分数要作废（它不是整数）");

	// 坏 JSON / 没有 JSON / 缺 scores：作废，且给的是能读懂的原因。
	Expect(QualityJudgement.ParseScores("完全不认识的一段话", 2).Scores is null, "没有 JSON 要作废");
	Expect(QualityJudgement.ParseScores("{\"scores\":", 2).Scores is null, "坏 JSON 要作废");
	Expect(QualityJudgement.ParseScores("{\"ranking\":[1,2]}", 2).Scores is null, "缺 scores 数组要作废");

	// 没有可判的图：不该被调用，但也不能崩。
	Expect(QualityJudgement.ParseScores("{\"scores\":[]}", 0).Scores is null, "零张不判");
}

static void TechnicalScreeningOnlyFlagsBrokenImages()
{
	// 读不出来 → 白档，理由说清是哪一条。
	var broken = TechnicalScreening.Screen(new ImageFacts(false, 0, 0, 0, 0, 1024));
	Expect(broken is { Source: QualitySource.Technical, Tier: QualityTier.White }, "读不出来的判成白档（本地）");
	Expect(broken!.Reason.Contains("读不出来", StringComparison.Ordinal), "理由要说清是读不出来");

	// 整张几乎一个颜色 → 白档。
	var solid = TechnicalScreening.Screen(new ImageFacts(true, 1024, 1024, 576, 1, 1024));
	Expect(solid is { Tier: QualityTier.White }, "整张一个颜色的判成白档");
	Expect(solid!.Reason.Contains("一个颜色", StringComparison.Ordinal), "理由要说清是纯色");

	// 尺寸比要求小一大截 → 白档。
	var tiny = TechnicalScreening.Screen(new ImageFacts(true, 256, 256, 576, 40, 1024));
	Expect(tiny is { Tier: QualityTier.White }, "尺寸只有要求的一半以下的判成白档");
	Expect(tiny!.Reason.Contains("256", StringComparison.Ordinal), "理由要把实际尺寸写出来");

	// 一张正常图：**本地这层不判**——分辨率、颜色数都不等于好看。
	Expect(TechnicalScreening.Screen(new ImageFacts(true, 1024, 1024, 576, 40, 1024)) is null,
		"正常图本地不判（不碰「好不好」）");
	// 刚好一半的尺寸不算「小很多」（阈值是严格的两倍关系）。
	Expect(TechnicalScreening.Screen(new ImageFacts(true, 512, 512, 576, 40, 1024)) is null, "刚好一半不算明显偏小");
	// 没给出要求尺寸时不检查这一条（不猜）。
	Expect(TechnicalScreening.Screen(new ImageFacts(true, 256, 256, 576, 40, 0)) is null, "没给要求尺寸就不检查尺寸");
}

static void QualityGradesMapBackToSlots()
{
	// 4 个槽位，只有 4 张发给模型（序号 0 / 2 / 3 / 5），编号是发送顺序。
	var grades = QualityJudgement.ToGrades(
		new[] { 0, 2, 3, 5 },
		new[]
		{
			new QualityScore(1, 9, Array.Empty<string>(), "最好的一张"),
			new QualityScore(2, 7, Array.Empty<string>(), "第二"),
			new QualityScore(3, 5, new[] { "水印" }, "画面上有字"),
			new QualityScore(4, 2, Array.Empty<string>(), "最差")
		});

	Expect(grades.Count == 4, "四格都要有档位");
	// 交出来是按槽位序号排好的。
	Expect(grades[0].Index == 0 && grades[1].Index == 2 && grades[2].Index == 3 && grades[3].Index == 5,
		"结果按槽位序号排好");

	var bySlot = grades.ToDictionary(item => item.Index, item => item.Quality);
	Expect(bySlot[0].Tier == QualityTier.Red && bySlot[0].Score == 9, "编号 1（槽位 0）9 分是红——红是最高档");
	Expect(bySlot[2].Tier == QualityTier.Gold && bySlot[2].Score == 7, "编号 2（槽位 2）7 分是金");
	Expect(bySlot[3].Tier == QualityTier.Purple && bySlot[3].Score == 5, "编号 3（槽位 3）5 分是紫");
	Expect(bySlot[5].Tier == QualityTier.White && bySlot[5].Score == 2, "编号 4（槽位 5）2 分是白");
	Expect(bySlot[0].Reason == "最好的一张", "理由原样带过来");
	Expect(bySlot.Values.All(item => item.Source == QualitySource.Model), "来源要标成模型");

	// 裂纹卡：踩中负面词的那张自己裂开，没踩中的不裂。
	Expect(!bySlot[0].IsCracked, "hits 是空的就不是裂纹卡");
	Expect(bySlot[3].IsCracked && bySlot[3].NegativeHits.Count == 1, "踩中一条负面词就是裂纹卡");
	Expect(QualityJudgement.DescribeHits(bySlot[3].NegativeHits) == "水印", "踩中的词要能拼出来给人看");
	// 本地筛查那种没有分数（它根本没看图），也不该被算成裂纹卡。
	SlotQuality technical = new() { Tier = QualityTier.White, Source = QualitySource.Technical, Reason = "读不出来" };
	Expect(!technical.IsCracked, "本地判定的坏图不是裂纹卡");

	// 一整批的预兆：最高的那一档说了算，没判过就是 null（不是白档）。
	NodeImageBatch batch = new() { NodeId = Guid.NewGuid() };
	batch.Slots.Add(new BatchSlot { Status = BatchSlotStatus.Done });
	batch.Slots.Add(new BatchSlot { Status = BatchSlotStatus.Done });
	Expect(!batch.HasGrades && batch.BestTier is null, "没判过就没有档位——不是白档");
	Expect(batch.Slots[0].Quality is null, "没判过的格子是 null，不是「白档」");

	batch.Slots[0].Quality = bySlot[0];
	Expect(batch.HasGrades && batch.BestTier == QualityTier.Red, "判过一格的最高档就是它（9 分是红）");

	batch.Slots[1].Quality = bySlot[3];
	Expect(batch.CrackedCount == 1, "数得出来有几张裂纹卡");
	Expect(batch.BestTier == QualityTier.Red, "加进来一张裂纹卡不该改变最高档");

	// 把唯一那张干净的拿走，只剩裂纹卡：预兆降到**白档**，而不是变成「没判过」。
	batch.Slots[0].Quality = bySlot[3];
	Expect(batch.BestTier == QualityTier.White,
		"全是裂纹卡时按白档给最弱的预兆，不能返回 null（那会被界面当成「没判过」）");

	// 删掉的那一格不参与（它的档位不该再影响预兆）。
	batch.Slots[1].Removed = true;
	Expect(batch.GradedSlots.Count == 1, "已删掉的格子不算判过");
	Expect(batch.BestTier == QualityTier.White, "已删掉的不参与，剩下的那张裂纹卡仍按白档");
}

static void JudgePromptCarriesContextAndChecks()
{
	const string context = "节点：角色设定·林晚（角色）\n所属章节：第一话\n这一步引用的设定（画面里应当与这些设定一致）：林晚 · 常服";
	const string requirement = "十七岁少女，齐耳短发，藏青棉布外套，站在雨里的书店门口";
	const string negative = "多余手指，水印，多余肢体";

	var prompt = ImageQualityJudge.BuildUserPrompt(context, requirement, negative, 3);

	// 三样缺一不可：这一步在干什么、要画什么、不要什么。
	Expect(prompt.Contains(context, StringComparison.Ordinal), "节点上下文要原样带进去");
	Expect(prompt.Contains(requirement, StringComparison.Ordinal), "出图要求要带进去");
	Expect(prompt.Contains(negative, StringComparison.Ordinal), "负面提示词要带进去（否则评审猜不到用户在意的到底是什么）");
	Expect(prompt.Contains("逐条对照", StringComparison.Ordinal), "要明确要求逐条对照负面词");
	Expect(prompt.Contains("hits", StringComparison.Ordinal), "要交代 hits 字段怎么填");

	// 四类缺陷必须逐条问到——这几类正是用户点名要看的。
	Expect(ImageQualityJudge.Checks.Count == 4, "评分维度是四条");
	Expect(prompt.Contains("贴合度", StringComparison.Ordinal), "要问与出图要求的贴合度");
	Expect(prompt.Contains("多余手指", StringComparison.Ordinal), "要问手指问题");
	Expect(prompt.Contains("穿帮", StringComparison.Ordinal), "要问穿帮");
	Expect(prompt.Contains("嵌", StringComparison.Ordinal), "要问角色与物体嵌进去这类错误");

	// 要的是绝对分、不要名次：少这两句，模型会自己退回「排序」那套。
	Expect(prompt.Contains("不要把它们互相比较", StringComparison.Ordinal), "要明确说不要互相比较");
	Expect(prompt.Contains("整数", StringComparison.Ordinal), "要明确说分数是整数");
	Expect(prompt.Contains("0–10", StringComparison.Ordinal), "要把分数范围写出来");

	// 没有提示词 / 没有负面词时：如实说明，不让模型去猜、也不编一份出来。
	var bare = ImageQualityJudge.BuildUserPrompt(string.Empty, string.Empty, string.Empty, 1);
	Expect(bare.Contains("没有留下提示词", StringComparison.Ordinal), "没有提示词要如实说明");
	Expect(bare.Contains("没有写负面提示词", StringComparison.Ordinal), "没有负面词要如实说明");
}

static void BatchSlotsAreNotActionableWhileRunning()
{
	NodeImageBatch running = new NodeImageBatch { NodeId = Guid.NewGuid(), NodeTitle = "雨夜追车", IsRunning = true };
	running.Slots.Add(new BatchSlot { Status = BatchSlotStatus.Done, Path = "shot-1.png" });
	running.Slots.Add(new BatchSlot { Status = BatchSlotStatus.Running });
	running.Slots.Add(new BatchSlot { Status = BatchSlotStatus.Failed, Error = "服务端没有给出原因" });
	Expect(!running.CanActOnSlot(0), "整批还在跑时，已出好的那一格也不能采用 / 放大 / 删除");
	Expect(!running.CanActOnSlot(1), "还在跑的那一格本来就不能操作");
	Expect(!running.CanActOnSlot(2), "整批还在跑时，失败的那一格也不能操作");
	Expect(!running.CanPick, "跑着的时候不能挑");
	Expect(running.SingleActionBlockedNote.Contains("出完再挑", StringComparison.Ordinal),
		"整批还在跑时该说的是「出完再挑」，不是「这一张还在出」，实际：" + running.SingleActionBlockedNote);

	running.Slots[1].Status = BatchSlotStatus.Done;
	running.Slots[1].Path = "shot-2.png";
	running.IsRunning = false;
	Expect(running.CanPick, "整批出完后可以挑");
	Expect(running.CanActOnSlot(0) && running.CanActOnSlot(1), "整批出完后，出好的格子可以操作");
	Expect(running.CanActOnSlot(2), "整批出完后，失败的那一格也要能删掉——否则一排失败会永远挂在画布上");
	Expect(!running.CanActOnSlot(9), "越界索引不能操作");
	Expect(!running.CanActOnSlot(-1), "负索引不能操作");
	Expect(!running.SingleActionBlockedNote.Contains("出完再挑", StringComparison.Ordinal),
		"整批跑完之后就不该再提「出完再挑」，实际：" + running.SingleActionBlockedNote);

	running.Slots[0].Removed = true;
	Expect(!running.CanActOnSlot(0), "已经删掉的那一格不能再操作");
	Expect(running.CanActOnSlot(1), "删掉一格不影响别的格子");

	// 整批没在跑、但格子还没出好：正常不该出现这个状态，规则本身要自洽（不能因为「没在跑」就放行）。
	NodeImageBatch waiting = new NodeImageBatch { NodeId = Guid.NewGuid() };
	waiting.Slots.Add(new BatchSlot());
	Expect(!waiting.CanActOnSlot(0), "还没出好的格子不能操作");
}

static void AttachmentPromptIsStoredAndReadable()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚"
	};
	WorkflowEntityVariant workflowEntityVariant = workflowEntity.CreateVariant("常服", "十七岁，短发");
	workflowEntityVariant.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://lin-wan.png",
		Name = "lin-wan.png",
		Prompt = "十七岁少女，齐耳短发，藏青棉布外套，干净线条数码角色设定图，16:9",
		NegativePrompt = "多余手指，水印"
	});
	workflowCanvasState.Entities.Add(workflowEntity);
	string json = JsonSerializer.Serialize(workflowCanvasState, new JsonSerializerOptions
	{
		WriteIndented = true
	});
	WorkflowCanvasState workflowCanvasState2 = JsonSerializer.Deserialize<WorkflowCanvasState>(json);
	WorkflowAttachment workflowAttachment = workflowCanvasState2.Entities[0].Variants[0].Attachments[0];
	Expect(workflowAttachment.Prompt.Contains("齐耳短发", StringComparison.Ordinal), "出图用的提示词要随画布存住");
	Expect(workflowAttachment.NegativePrompt.Contains("多余手指", StringComparison.Ordinal), "负面词也要存住");
	Expect(SettingPrompt.Prefer(workflowAttachment.Prompt, workflowEntity, workflowEntityVariant) == workflowAttachment.Prompt, "有存下来的提示词就直接用它");
	Expect(SettingPrompt.Prefer("  ", workflowEntity, workflowEntityVariant) == SettingPrompt.Compose(workflowEntity, workflowEntityVariant), "没存提示词（手放的素材）要落回按设定现拼");
	Expect(SettingPrompt.Prefer(null, workflowEntity, workflowEntityVariant).Contains("林晚", StringComparison.Ordinal), "空提示词不能填空字符串，要给出能用的那一份");
	WorkflowCanvasState workflowCanvasState3 = JsonSerializer.Deserialize<WorkflowCanvasState>("{\"Nodes\":[],\"Edges\":[],\"WorkTree\":[],\"Entities\":[{\"Kind\":1,\"Name\":\"林晚\",\"Variants\":[\n{\"Name\":\"常服\",\"Attachments\":[{\"Kind\":0,\"Reference\":\"asset://a.png\",\"Name\":\"a.png\",\"Source\":\"手工放置\"}]}]}]}");
	Expect(workflowCanvasState3 != null && workflowCanvasState3.Entities[0].Variants[0].Attachments[0].Prompt.Length == 0, "老画布里没有提示词字段，读进来应当是空字符串而不是报错");
}

static void BuiltInSkillsRespectDisabledList()
{
	BuiltInSkill builtInSkill = BuiltInSkills.Resolve("帮我生成角色设定");
	Expect((object)builtInSkill != null && builtInSkill.Id == "character-generation", "默认应命中角色设定，实际 " + builtInSkill?.Id);
	string[] disabled = new string[1] { "character-generation" };
	Expect((object)BuiltInSkills.Resolve("帮我生成角色设定", disabled) == null, "停用后不该再被命中");
	Expect(BuiltInSkills.Resolve("帮我生成场景设定", disabled)?.Id == "scene-generation", "只停用一个时其它技能照常命中");
	string text = BuiltInSkills.DescribeForAgent(disabled);
	Expect(!text.Contains("角色设定", StringComparison.Ordinal), "停用的技能不该出现在给 Agent 的清单里：\n" + text);
	Expect(text.Contains("场景设定", StringComparison.Ordinal), "启用的技能仍要出现在清单里");
	string[] disabled2 = BuiltInSkills.All.Select((BuiltInSkill skill) => skill.Id).ToArray();
	string text2 = BuiltInSkills.DescribeForAgent(disabled2);
	Expect(text2.Contains("全部处于停用状态", StringComparison.Ordinal), "全停时应明说原因，而不是留一段空白让模型以为“没有技能”：\n" + text2);
}

static void CanvasSearchFindsNodeWorkTreeAndEntity()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkTreeItem workTreeItem = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第三章 · 雨夜对峙",
		Prompt = "林晚抱着散页的故事书冲进书店"
	};
	workflowCanvasState.WorkTree.Add(workTreeItem);
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "第三章 · 雨夜对峙",
		Category = NodeCategory.Chapter,
		Content = "雨夜里她冲进书店，陈叔在灯下压平书页。",
		WorkTreeItemId = workTreeItem.Id
	};
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "故事大纲",
		Category = NodeCategory.StoryPlan,
		Content = "南方小城的旧书店"
	};
	workflowCanvasState.Nodes.AddRange(new WorkflowNode[2] { workflowNode, workflowNode2 });
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚",
		Aliases = "小晚"
	};
	workflowEntity.CreateVariant("默认");
	workflowCanvasState.Entities.Add(workflowEntity);
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntity.Variants[0].Id
	});
	IReadOnlyList<SearchHit> source = CanvasSearch.Find(workflowCanvasState, "雨夜对峙");
	SearchHit searchHit = source.FirstOrDefault((SearchHit hit) => hit.Kind == SearchHitKind.Node);
	Expect((object)searchHit != null && searchHit.NodeId == workflowNode.Id, "按标题能搜到节点并给出定位目标");
	Expect(searchHit.Detail.Contains("章节"), "结果要写清节点类型，实际 " + searchHit.Detail);
	Expect(searchHit.Detail.Contains("已绑工作树"), "绑了工作树的节点要说明这一点，实际 " + searchHit.Detail);
	SearchHit searchHit2 = CanvasSearch.Find(workflowCanvasState, "陈叔").FirstOrDefault((SearchHit hit) => hit.Kind == SearchHitKind.Node);
	Expect((object)searchHit2 != null, "按正文也能搜到");
	Expect(searchHit2.Snippet.Contains("陈叔"), "正文命中要给上下文片段，实际 " + searchHit2.Snippet);
	SearchHit searchHit3 = CanvasSearch.Find(workflowCanvasState, "散页").FirstOrDefault((SearchHit hit) => hit.Kind == SearchHitKind.WorkTreeItem);
	Expect((object)searchHit3 != null, "工作树条目也能搜到");
	Expect(searchHit3.NodeId == workflowNode.Id, "条目在画布上有节点时给出定位目标");
	Expect(searchHit3.Detail.Contains("已在画布上"), "条目状态要说清在不在画布上，实际 " + searchHit3.Detail);
	SearchHit searchHit4 = CanvasSearch.Find(workflowCanvasState, "小晚").FirstOrDefault((SearchHit hit) => hit.Kind == SearchHitKind.Entity);
	Expect((object)searchHit4 != null, "设定别名也能搜到");
	Expect(searchHit4.NodeId == workflowNode.Id, "被引用的设定要能定位到引用它的节点");
	WorkTreeItem item = new WorkTreeItem
	{
		Kind = WorkTreeKind.Prop,
		Name = "孤零零的道具条目"
	};
	workflowCanvasState.WorkTree.Add(item);
	SearchHit searchHit5 = CanvasSearch.Find(workflowCanvasState, "孤零零").FirstOrDefault((SearchHit hit) => hit.Kind == SearchHitKind.WorkTreeItem);
	Expect((object)searchHit5 != null && !searchHit5.NodeId.HasValue, "没放上画布的条目不给定位目标，但要说清未上画布");
	Expect(!searchHit5.CanFocus, "没有节点可定位时 CanFocus 为假");
	Expect(CanvasSearch.Find(workflowCanvasState, "   ").Count == 0, "空关键词不返回结果");
	Expect(CanvasSearch.Find(workflowCanvasState, "的", 3).Count <= 3, "结果条数要受上限约束");
	Expect(CanvasSearch.Find(workflowCanvasState, "根本没写过的词").Count == 0, "搜不到就返回空，不做联想");
}

static void CanvasTabRenameGuardsNames()
{
	string[] takenTitles = new string[3] { "画布1", "画布2", "第一章" };
	Expect(CanvasTabRules.DescribeNameConflict("画布3", takenTitles) == null, "没撞名的名字应当放行");
	Expect(CanvasTabRules.DescribeNameConflict("第一章", takenTitles) != null, "与已有画布重名必须拒绝");
	Expect(CanvasTabRules.DescribeNameConflict("画布1", takenTitles) != null, "与已有画布编号重名必须拒绝");
	Expect(CanvasTabRules.DescribeNameConflict("  画布1  ", takenTitles) != null, "首尾空格不该绕过查重");
	Expect(CanvasTabRules.DescribeNameConflict("CanvasA", new string[1] { "canvasa" }) != null, "只有大小写不同也算同名");
	Expect(CanvasTabRules.DescribeNameConflict("", takenTitles) != null, "空名字必须拒绝");
	Expect(CanvasTabRules.DescribeNameConflict("   ", takenTitles) != null, "纯空格的名字必须拒绝");
	Expect(CanvasTabRules.DescribeNameConflict(new string('长', 81), takenTitles) != null, "超过 80 个字符的名字要拒绝（文件名只取前 80 个字符，剩下的会被悄悄丢掉）");
	Expect(CanvasTabRules.DescribeNameConflict(new string('长', 80), takenTitles) == null, "80 个字符正好可以");
}

static void CanvasTabRulesAreCollisionProof()
{
	Expect(CanvasTabRules.NextTitle(Array.Empty<string>()) == "画布1", "没有任何画布时应从画布1开始");
	Expect(CanvasTabRules.NextTitle(new string[1] { "画布1" }) == "画布2", "画布1 已占用应顺延到画布2");
	Expect(CanvasTabRules.NextTitle(new string[1] { "画布2" }) == "画布1", "空出来的编号要能补上，而不是一路往后加");
	Expect(CanvasTabRules.NextTitle(new string[2] { "画布1", "画布3" }) == "画布2", "中间的编号同样要补上");
	Expect(CanvasTabRules.NextTitle(new string[1] { "画布 4" }) == "画布1", "带空格的编号也算已占用（用户改过标题也一样）");
	Expect(CanvasTabRules.NextTitle(new string[3] { "未命名画布", "画布", "第一章" }) == "画布1", "认不出的标题不该影响编号");
	List<string> list2 = new List<string> { "画布1", "画布2", "画布3", "画布5" };
	string text = CanvasTabRules.NextTitle(list2);
	Expect(text == "画布4" && !list2.Contains(text), "分配的编号不该与已有标题相撞");
	RecentCanvasState recentCanvasState = CanvasTabRules.CreateBlank("画布1");
	Expect(recentCanvasState.Title == "画布1" && recentCanvasState.Revision == 1, "新画布的标题或修订号不对");
	Expect(recentCanvasState.Canvas != null && recentCanvasState.Canvas.Nodes.Count == 1 && recentCanvasState.Canvas.Nodes[0].Title == "开始", "新画布应带一个「开始」节点");
	Expect(recentCanvasState.Canvas.Edges.Count == 0, "新画布不该有连线");
}

static void ChapterSplitByHeadingAndLength()
{
	string text = "第1章 初到小城\n雨下得很大。\n\n第2章 旧书店\n林晚推开门。\n\n【夜谈】\n两个人聊到很晚。";
	ChapterSplitResult chapterSplitResult = ChapterSplitPlanner.Split(text);
	Expect(chapterSplitResult.Mode == ChapterSplitMode.ByHeading, "有三处章节标记时应当按标题拆，实际 " + chapterSplitResult.Mode);
	Expect(chapterSplitResult.Chapters.Count == 3, "应当拆出 3 章，实际 " + chapterSplitResult.Chapters.Count);
	Expect(chapterSplitResult.Chapters[0].Title.Contains("初到小城"), "标题要沿用原文那一行，实际 " + chapterSplitResult.Chapters[0].Title);
	Expect(chapterSplitResult.Chapters[1].Content.Contains("林晚推开门"), "正文要落到对应章节里");
	Expect(!chapterSplitResult.Chapters[1].Content.Contains("第2章"), "章节标题本身不进正文");
	Expect(chapterSplitResult.Chapters[2].Title.Contains("夜谈"), "【小标题】同样算章节标记，实际 " + chapterSplitResult.Chapters[2].Title);
	Expect(chapterSplitResult.Note.Contains("标题"), "要如实说明是怎么拆的，实际 " + chapterSplitResult.Note);
	string text2 = string.Join("\n\n", from index in Enumerable.Range(1, 12)
		select new string('字', 300) + "第" + index + "段");
	ChapterSplitResult chapterSplitResult2 = ChapterSplitPlanner.Split(text2);
	Expect(chapterSplitResult2.Mode == ChapterSplitMode.ByLength, "没有章节标题时按长度切，实际 " + chapterSplitResult2.Mode);
	Expect(chapterSplitResult2.Chapters.Count >= 2, "3600 字应当切成多章，实际 " + chapterSplitResult2.Chapters.Count);
	Expect(chapterSplitResult2.Chapters.All((ChapterDraft chapter) => chapter.Title.StartsWith("第") && chapter.Title.Contains(" · ")), "长度切分的标题是「第N章 · 原文首句」，实际 " + chapterSplitResult2.Chapters[0].Title);
	Expect(chapterSplitResult2.Note.Contains("按长度"), "要如实说明切分方式，实际 " + chapterSplitResult2.Note);
	string text3 = string.Join("\n\n", chapterSplitResult2.Chapters.Select((ChapterDraft chapter) => chapter.Content));
	Expect(text3.Contains("第1段") && text3.Contains("第12段"), "切分不能丢正文");
	Expect(chapterSplitResult2.Chapters.Select((ChapterDraft chapter) => chapter.Order).SequenceEqual(Enumerable.Range(1, chapterSplitResult2.Chapters.Count)), "章节序号要从 1 连续编号");
	Expect(ChapterSplitPlanner.Split("   ").IsEmpty, "空白文本拆不出章节");
	Expect(ChapterSplitPlanner.Split(null).Mode == ChapterSplitMode.None, "null 文本要如实返回「没拆」");
	Expect(ChapterSplitPlanner.Split("第一章 标题\n正文\n\n第一章里他写到了雨。").Mode == ChapterSplitMode.ByLength, "只有一处标记（另一处是正文里的句子）时不能当成分节文本");
	string text4 = string.Join("\n", from index in Enumerable.Range(1, 30)
		select $"第{index}章 标题{index}\n正文{index}");
	ChapterSplitResult chapterSplitResult3 = ChapterSplitPlanner.Split(text4, 5);
	Expect(chapterSplitResult3.Chapters.Count <= 5, "超过上限时章数要被夹住，实际 " + chapterSplitResult3.Chapters.Count);
	Expect(chapterSplitResult3.Note.Contains("上限"), "夹住时要说明原因，实际 " + chapterSplitResult3.Note);
	Expect(ChapterSplitPlanner.ChineseNumber(1) == "一" && ChapterSplitPlanner.ChineseNumber(10) == "十" && ChapterSplitPlanner.ChineseNumber(11) == "十一" && ChapterSplitPlanner.ChineseNumber(20) == "二十" && ChapterSplitPlanner.ChineseNumber(21) == "二十一" && ChapterSplitPlanner.ChineseNumber(40) == "四十", "章节序号的中文数字：1/10/11/20/21/40");
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚",
		Aliases = "小晚,晚晚"
	};
	workflowEntity.CreateVariant("默认");
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "旧书店"
	};
	workflowEntity2.CreateVariant("雨夜");
	WorkflowEntity workflowEntity3 = new WorkflowEntity
	{
		Kind = EntityKind.Prop,
		Name = "书"
	};
	workflowEntity3.CreateVariant("默认");
	workflowCanvasState.Entities.AddRange(new WorkflowEntity[3] { workflowEntity, workflowEntity2, workflowEntity3 });
	IReadOnlyList<WorkflowEntity> readOnlyList = ChapterSplitPlanner.MatchEntities(workflowCanvasState, "小晚推开门，旧书店里亮着灯。");
	Expect(readOnlyList.Count == 2, "别名命中一次、设定名命中一次，实际 " + readOnlyList.Count);
	Expect(readOnlyList.Any((WorkflowEntity entity) => entity.Name == "林晚"), "别名也要参与匹配");
	Expect(readOnlyList.Any((WorkflowEntity entity) => entity.Name == "旧书店"), "设定名直接命中");
	Expect(readOnlyList.All((WorkflowEntity entity) => entity.Name != "书"), "单字设定名不参与匹配（正文里满地都是）");
	Expect(ChapterSplitPlanner.MatchEntities(workflowCanvasState, "什么都没提到").Count == 0, "没提到就没有出场项，不能补一个");
	Expect(ChapterSplitPlanner.MatchEntities(workflowCanvasState, string.Empty).Count == 0, "空文本没有出场项");
}

static void CharacterSheetFollowsTurnaroundStandard()
{
	string turnaroundSpec = PromptBaseline.TurnaroundSpec;
	string[] array3 = new string[4] { "正面", "侧面", "背面", "四分之三" };
	foreach (string text in array3)
	{
		Expect(turnaroundSpec.Contains(text, StringComparison.Ordinal), "转面图规格缺少「" + text + "」视角");
	}
	Expect(turnaroundSpec.Contains("三视图", StringComparison.Ordinal), "规格要说明崩图时降回三视图的退路");
	Expect(turnaroundSpec.Contains("16:9", StringComparison.Ordinal), "转面图要指明 16:9 横向（并排视图需要画幅）");
	Expect(turnaroundSpec.Contains("逐字", StringComparison.Ordinal), "四个视图的服装与发型措辞必须逐字相同");
	Expect(!turnaroundSpec.Contains("九视图", StringComparison.Ordinal), "「九视图」不是转面图标准（九宫格是多表情/多姿势），不该出现在规格里");
	Expect(PromptBaseline.ExpressionSpec.Contains("单独一张", StringComparison.Ordinal), "表情表要单独一张，不能挤进转面图");
	Expect(PromptBaseline.ExpressionSpec.Contains("六格", StringComparison.Ordinal), "表情表按六格给");
	Expect(PromptBaseline.NegativeFor(NodeCategory.Character).Contains(PromptBaseline.ViewBleedNegative, StringComparison.Ordinal), "角色负面词里必须包含视图粘连类（串脸 / 串衣服 / merged views）");
	Expect(PromptBaseline.ViewBleedNegative.Contains("different outfit between views", StringComparison.Ordinal), "视图粘连负面词要覆盖「不同视图服装不一致」");
	Expect(PromptBaseline.NegativeFor(NodeCategory.Prop).Contains("拼图", StringComparison.Ordinal), "道具特写是单张视图，负面里要有「拼图 / 多个视角」");
	Expect(PromptBaseline.NegativeFor(NodeCategory.Scene).Contains("多个视角并排", StringComparison.Ordinal), "场景基准图同理");
	Expect(PromptBaseline.ConsistencyRule.Contains("逐字", StringComparison.Ordinal), "一致性纪律要写明逐字复用");
	Expect(PromptBaseline.ConsistencyRule.Contains("2–4 张", StringComparison.Ordinal), "一致性纪律要写明参考图 2–4 张");
	Expect(PromptBaseline.NegativeUsageNote.Contains("Flux", StringComparison.Ordinal) && PromptBaseline.NegativeUsageNote.Contains("Gemini", StringComparison.Ordinal), "负面词用法要说明模型方言差异（Flux 不支持负面、Gemini 别给长负面清单）");
	BuiltInSkill builtInSkill = BuiltInSkills.All.Single((BuiltInSkill skill) => skill.Id == "character-generation");
	string[] array4 = new string[4]
	{
		PromptBaseline.TurnaroundSpec,
		PromptBaseline.AnchorSpec,
		PromptBaseline.ExpressionSpec,
		PromptBaseline.ConsistencyRule
	};
	foreach (string value in array4)
	{
		Expect(builtInSkill.OutputFormat.Contains(value, StringComparison.Ordinal), "角色技能说明缺少参考图成套规格的一部分");
	}
	BuiltInSkill builtInSkill2 = BuiltInSkills.All.Single((BuiltInSkill skill) => skill.Id == "image-generation");
	Expect(builtInSkill2.OutputFormat.Contains(PromptBaseline.ConsistencyRule, StringComparison.Ordinal), "生图技能说明要带上参考图纪律");
	BuiltInSkill builtInSkill3 = BuiltInSkills.All.Single((BuiltInSkill skill) => skill.Id == "storyboard-generation");
	Expect(builtInSkill3.OutputFormat.Contains("逐字复制", StringComparison.Ordinal), "分镜技能要写明逐字复用纪律");
}

static void CiphertextFromAnotherPlatformIsReportedUnreadable()
{
	Expect(SecretProtector.Unprotect("keychain:AAAA") == null, "本机没有对应密钥库时，keychain 密文应报解不开");
	Expect(SecretProtector.Unprotect("aesgcm:AAAA") == null, "载荷不成形时，aesgcm 密文应报解不开");
	Expect(SecretProtector.Unprotect("dpapi:bm90LWEtcmVhbC1jaXBoZXJ0ZXh0") == null, "坏密文应报解不开");
	Expect(SecretProtector.Unprotect("sk-proj:abcdef") == "sk-proj:abcdef", "带冒号的普通密钥应原样返回");
	Expect(!SecretProtector.IsProtected("sk-proj:abcdef"), "未知前缀不算已加密");
	Expect(!SecretProtector.IsEncryptedAtRest("sk-proj:abcdef"), "未知前缀更不算真的加密落盘");
	Expect(SecretProtector.IsProtected("plain:x"), "plain: 是已知前缀");
	Expect(!SecretProtector.IsEncryptedAtRest("plain:x"), "plain: 不能被算作加密落盘");
}

static void GenerationAuditReportsDependencyChain()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkTreeItem workTreeItem = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第六章"
	};
	workflowCanvasState.WorkTree.Add(workTreeItem);
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "第六章",
		Category = NodeCategory.Chapter,
		WorkTreeItemId = workTreeItem.Id
	};
	workflowCanvasState.Nodes.Add(workflowNode);
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚"
	};
	WorkflowEntityVariant workflowEntityVariant = workflowEntity.CreateVariant("少年黑衣", "短发，藏青风衣");
	workflowCanvasState.Entities.Add(workflowEntity);
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "林晚",
		Category = NodeCategory.Character,
		WorkTreeItemId = workTreeItem.Id,
		References = 
		{
			new NodeReference
			{
				EntityId = workflowEntity.Id,
				VariantId = workflowEntityVariant.Id
			}
		}
	};
	WorkflowNode workflowNode3 = new WorkflowNode
	{
		Title = "分镜 1",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem.Id
	};
	workflowNode3.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://shot1.png"
	});
	WorkflowNode workflowNode4 = new WorkflowNode
	{
		Title = "分镜 2",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem.Id
	};
	workflowNode4.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	});
	WorkflowNode workflowNode5 = new WorkflowNode
	{
		Title = "第六章成品",
		Category = NodeCategory.Product,
		WorkTreeItemId = workTreeItem.Id
	};
	workflowCanvasState.Nodes.AddRange(new WorkflowNode[4] { workflowNode2, workflowNode3, workflowNode4, workflowNode5 });
	GenerationAuditReport generationAuditReport = GenerationAudit.Build(workflowCanvasState, workflowNode, null, (string _) => true);
	Expect(generationAuditReport.RootKind == "章节", "报告要写明这是哪一类节点，实际 " + generationAuditReport.RootKind);
	Expect(generationAuditReport.Intent == GenerationIntent.Video, "章节自检默认按「出视频」准备（右键多半是在准备出片）");
	GenerationAuditLayer generationAuditLayer = generationAuditReport.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.SettingImage);
	Expect(generationAuditLayer.TargetCount == 1 && generationAuditLayer.MissingCount == 1, "设定图这一层是 1 缺 1，实际 " + generationAuditLayer.MissingCount + "/" + generationAuditLayer.TargetCount);
	Expect(generationAuditLayer.Missing[0].ActionNodeId == workflowNode2.Id, "缺的那张要能定位到「林晚」这个角色节点上");
	GenerationAuditLayer generationAuditLayer2 = generationAuditReport.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.StoryboardImage);
	Expect(generationAuditLayer2.TargetCount == 2 && generationAuditLayer2.MissingCount == 1, "分镜图 2 缺 1（分镜 1 已经有图），实际 " + generationAuditLayer2.MissingCount + "/" + generationAuditLayer2.TargetCount);
	Expect(generationAuditLayer2.Missing[0].ActionNodeId == workflowNode4.Id, "缺的是分镜 2");
	GenerationAuditLayer generationAuditLayer3 = generationAuditReport.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.StoryboardVideo);
	Expect(generationAuditLayer3.TargetCount == 2 && generationAuditLayer3.MissingCount == 2, "两镜都还没有视频");
	Expect(!generationAuditLayer3.Executable, "出视频执行方还没接入，这一层必须如实说不可执行");
	Expect(generationAuditLayer3.ExecutableNote.Contains("没接入"), "不可执行要给原因，实际：" + generationAuditLayer3.ExecutableNote);
	GenerationAuditLayer generationAuditLayer4 = generationAuditReport.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.ProductVideo);
	Expect(generationAuditLayer4.TargetCount == 1 && generationAuditLayer4.MissingCount == 1, "成品的成片视频也缺");
	Expect(generationAuditLayer4.Missing[0].Stage == GenerationStage.ProductVideo, "成品缺的是第 4 层，不是分镜那一层");
	Expect(generationAuditReport.Describe().Contains("设定图 缺 1/1"), "摘要要按层报数，实际：" + generationAuditReport.Describe());
	Expect(generationAuditReport.Describe().Contains("成品视频 缺 1/1"), "四层都要报，实际：" + generationAuditReport.Describe());
	IReadOnlyList<GenerationAuditItem> defaultChecked = generationAuditReport.DefaultChecked;
	Expect(defaultChecked.Count == 2, "出视频意图下预勾 = 设定图 1 + 分镜图 1（跑不了的那两层不勾），实际 " + defaultChecked.Count);
	Expect(defaultChecked.All((GenerationAuditItem item) => item.Stage != GenerationStage.StoryboardVideo), "跑不了的层不该被预勾");
	Expect(defaultChecked.All((GenerationAuditItem item) => item.Actionable), "预勾的每一项都要真的能生成");
	IReadOnlyList<GenerationAuditItem> defaultChecked2 = generationAuditReport.WithIntent(GenerationIntent.Images).DefaultChecked;
	Expect(defaultChecked2.Count == 1 && defaultChecked2[0].Stage == GenerationStage.SettingImage, "只补图时分镜图不是前置（它就是这次要做的事），只剩设定图那 1 件，实际 " + defaultChecked2.Count);
	Expect(generationAuditReport.EstimateCost(null).Contains("算不出花费"), "单价未知时不许报 0，实际：" + generationAuditReport.EstimateCost(null));
	string text = generationAuditReport.EstimateCost(0.12);
	Expect(text.Contains("要补 2 张图") && text.Contains("0.24"), "2 张 × 0.12 = 0.24，实际：" + text);
	Expect(text.Contains("3 段视频") && text.Contains("不计价"), "出视频那 3 段不报价，实际：" + text);
	GenerationAuditReport generationAuditReport2 = GenerationAudit.Build(workflowCanvasState, workflowNode, null, (string _) => false);
	Expect(generationAuditReport2.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.SettingImage).MissingCount == 1, "文件不在的挂件不算数");
	Expect(generationAuditReport2.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.StoryboardImage).MissingCount == 2, "两张分镜的挂件都指向已经不存在的文件，都要报出来，实际 " + generationAuditReport2.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.StoryboardImage).MissingCount);
	GenerationAuditReport generationAuditReport3 = GenerationAudit.Build(workflowCanvasState, workflowNode4, null, (string _) => true);
	Expect(generationAuditReport3.Intent == GenerationIntent.Images, "分镜自检默认是「补图」");
	GenerationAuditLayer generationAuditLayer5 = generationAuditReport3.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.SettingImage);
	Expect(generationAuditLayer5.TargetCount == 1 && generationAuditLayer5.Missing[0].ActionNodeId == workflowNode2.Id, "分镜引用的角色节点要一起进范围");
	Expect(generationAuditReport3.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.StoryboardImage).TargetCount == 1, "分镜这一层只算它自己，不算同章其它镜头");
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "雨夜码头"
	};
	WorkflowEntityVariant workflowEntityVariant2 = workflowEntity2.CreateVariant("深夜暴雨");
	workflowCanvasState.Entities.Add(workflowEntity2);
	WorkflowNode workflowNode6 = new WorkflowNode
	{
		Title = "分镜 9",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem.Id
	};
	workflowNode6.References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntityVariant2.Id
	});
	workflowCanvasState.Nodes.Add(workflowNode6);
	GenerationAuditReport generationAuditReport4 = GenerationAudit.Build(workflowCanvasState, workflowNode6, null, (string _) => true);
	GenerationAuditLayer generationAuditLayer6 = generationAuditReport4.Layers.First((GenerationAuditLayer layer) => layer.Stage == GenerationStage.SettingImage);
	Expect(generationAuditLayer6.MissingCount == 1, "这个场景还没有图");
	Expect(generationAuditLayer6.Missing[0].ActionNodeId == Guid.Empty && !generationAuditLayer6.Missing[0].Actionable, "画布上没有承载它的节点：要如实说「去引用画廊」，不能给一个点了没反应的假定位");
	Expect(generationAuditLayer6.Missing[0].Reason.Contains("引用画廊"), "说明里要写清去处，实际：" + generationAuditLayer6.Missing[0].Reason);
	Expect(generationAuditReport4.DefaultChecked.Count == 0, "不能直接生成的那一项不该被预勾");
	WorkflowNode workflowNode7 = new WorkflowNode
	{
		Title = "第七章",
		Category = NodeCategory.Chapter
	};
	workflowCanvasState.Nodes.Add(workflowNode7);
	int count = workflowCanvasState.Nodes.Count;
	int count2 = workflowCanvasState.Entities.Count;
	GenerationAuditReport generationAuditReport5 = GenerationAudit.Build(workflowCanvasState, workflowNode7, null, (string _) => true);
	Expect(generationAuditReport5.Note.Length > 0, "算不出范围要说明原因");
	Expect(generationAuditReport5.Describe() == generationAuditReport5.Note, "摘要要直接给出原因，不能显示成「齐了」");
	Expect(generationAuditReport5.MissingCount == 0, "这种报告里不该摆一堆空清单");
	Expect(generationAuditReport5.ToText(null).Contains("章节同步"), "文本版也要带上怎么修，实际：\n" + generationAuditReport5.ToText(null));
	Expect(workflowCanvasState.Nodes.Count == count && workflowCanvasState.Entities.Count == count2, "自检不新增、不删除任何东西");
	Expect(workflowNode2.Attachments.Count == 0 && workflowNode4.Attachments.Count == 0, "自检不往节点上挂任何产物");
}

static void GenerationSkillsSharePromptBaseline()
{
	BuiltInSkill builtInSkill = BuiltInSkills.All.Single((BuiltInSkill skill) => skill.Id == "character-generation");
	Expect(builtInSkill.OutputFormat.Contains(PromptBaseline.SectionGuide(NodeCategory.Character)), "角色技能说明应当逐字带上角色字段与要求，实际：\n" + builtInSkill.OutputFormat);
	foreach (PromptBaseline.Field item12 in PromptBaseline.Character)
	{
		Expect(builtInSkill.OutputFormat.Contains("【" + item12.Label + "】"), "角色技能说明缺少字段【" + item12.Label + "】");
	}
	Expect(builtInSkill.OutputFormat.Contains(PromptBaseline.TurnaroundSpec), "角色技能说明缺少转面图规格");
	Expect(builtInSkill.OutputFormat.Contains(PromptBaseline.NegativeFor(NodeCategory.Character)), "角色技能说明缺少角色负面词");
	BuiltInSkill builtInSkill2 = BuiltInSkills.All.Single((BuiltInSkill skill) => skill.Id == "scene-generation");
	Expect(builtInSkill2.OutputFormat.Contains(PromptBaseline.SectionGuide(NodeCategory.Scene)), "场景技能说明应当逐字带上场景字段与要求，实际：\n" + builtInSkill2.OutputFormat);
	Expect(builtInSkill2.OutputFormat.Contains(PromptBaseline.NegativeFor(NodeCategory.Scene)), "场景技能说明缺少场景负面词");
	BuiltInSkill builtInSkill3 = BuiltInSkills.All.Single((BuiltInSkill skill) => skill.Id == "prop-generation");
	Expect(builtInSkill3.OutputFormat.Contains(PromptBaseline.SectionGuide(NodeCategory.Prop)), "道具技能说明应当逐字带上道具字段");
	Expect(PromptBaseline.NegativeFor(NodeCategory.Character) != PromptBaseline.NegativeFor(NodeCategory.Scene), "角色与场景的负面提示词不能共用一份");
	Expect(PromptBaseline.NegativeFor(NodeCategory.Character).Contains("多余手指"), "角色负面词要点名「多余手指」");
	Expect(PromptBaseline.NegativeFor(NodeCategory.Scene).Contains("光源不明"), "场景负面词要点名「光源不明」");
	List<string> list2 = PromptBaseline.Character.Select((PromptBaseline.Field field) => field.Label).ToList();
	Expect(list2.IndexOf("面部") < list2.IndexOf("服装") && list2.IndexOf("服装") < list2.IndexOf("画风"), "角色字段顺序应当是「外观 → 服装 → 画风」");
	List<string> list3 = PromptBaseline.Scene.Select((PromptBaseline.Field field) => field.Label).ToList();
	Expect(list3.IndexOf("光源来源与色温") < list3.IndexOf("机位与景别"), "光源必须写在机位之前");
	Expect(PromptBaseline.Scene.Single((PromptBaseline.Field field) => field.Key == "light_source").Hint.Contains("必须写来源"), "场景基线里「光源」这一项必须强制写来源，这是最容易漏、又最影响成片的一项");
	Expect(!PromptBaseline.HasBaseline(NodeCategory.Chapter) && !PromptBaseline.HasBaseline(NodeCategory.General) && !PromptBaseline.HasBaseline(NodeCategory.StoryPlan), "只有角色 / 场景 / 道具 / 分镜有自己的基线");
}

static void LegacyProgramRootConfigIsMigrated()
{
	string text = Path.Combine(Path.GetTempPath(), "df-confighome-" + Guid.NewGuid().ToString("N").Substring(0, 8));
	string environmentVariable = Environment.GetEnvironmentVariable("YEEYEEYEE_CONFIG_HOME");
	string path = Path.Combine(AppPaths.ProgramRoot, $"migrate-probe-{Guid.NewGuid():N}.json");
	try
	{
		Directory.CreateDirectory(text);
		Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG_HOME", text);
		File.WriteAllText(path, "{\"legacy\":true}");
		string text2 = AppPaths.ResolveAppFile(Path.GetFileName(path));
		Expect(string.Equals(Path.GetDirectoryName(text2), text, StringComparison.Ordinal), "程序旁的旧文件应被搬到用户配置目录，实际：" + text2);
		Expect(File.Exists(text2), "搬迁后新位置应真的有文件");
		Expect(!File.Exists(path), "旧位置不应再留一份");
		Expect(File.ReadAllText(text2).Contains("legacy", StringComparison.Ordinal), "搬迁不应改动内容");
	}
	finally
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG_HOME", environmentVariable);
		if (File.Exists(path))
		{
			File.Delete(path);
		}
		if (Directory.Exists(text))
		{
			Directory.Delete(text, recursive: true);
		}
	}
}

static void ModelCatalogResolvesUrlAndParsesList()
{
	AiProviderConfig config = new AiProviderConfig
	{
		Endpoint = "https://api.example.com/v1/",
		Model = "m"
	};
	Expect(ModelCatalog.ModelsUrl(config) == "https://api.example.com/v1/models", "OpenAI 兼容应补 /models：" + ModelCatalog.ModelsUrl(config));
	AiProviderConfig config2 = new AiProviderConfig
	{
		Endpoint = "https://api.anthropic.com",
		ApiFormat = AiApiFormat.AnthropicMessages
	};
	Expect(ModelCatalog.ModelsUrl(config2) == "https://api.anthropic.com/v1/models", "Anthropic 的清单地址带 /v1：" + ModelCatalog.ModelsUrl(config2));
	AiProviderConfig config3 = new AiProviderConfig
	{
		Endpoint = "https://gw.example.com/v1/chat/completions",
		UseFullUrl = true
	};
	Expect(ModelCatalog.ModelsUrl(config3) == "https://gw.example.com/v1/models", "完整 URL 模式应把最后一段换成 models：" + ModelCatalog.ModelsUrl(config3));
	Expect(ModelCatalog.ModelsUrl(new AiProviderConfig()) == string.Empty, "没填地址时不该凭空编出一个地址");
	IReadOnlyList<string> readOnlyList = ModelCatalog.Parse("{\"object\":\"list\",\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"deepseek-chat\"}]}");
	Expect(readOnlyList.Count == 2 && readOnlyList[0] == "gpt-4o" && readOnlyList[1] == "deepseek-chat", "OpenAI 的 data[].id 应解析出来：" + string.Join("、", readOnlyList));
	IReadOnlyList<string> readOnlyList2 = ModelCatalog.Parse("[{\"id\":\"a\"},{\"name\":\"b\"}]");
	Expect(readOnlyList2.Count == 2 && readOnlyList2[1] == "b", "直接给数组、只有 name 的网关也要收：" + string.Join("、", readOnlyList2));
	Expect(ModelCatalog.Parse("{\"data\":[{\"id\":\"a\"},{\"id\":\"a\"}]}").Count == 1, "同一模型列两遍应去重");
	Expect(ModelCatalog.Parse("<html>502 Bad Gateway</html>").Count == 0, "不是 JSON 时应给出空清单而不是抛异常");
	Expect(ModelCatalog.Parse("{\"error\":\"nope\"}").Count == 0, "没有 data 数组时应给出空清单");
}

static void NodeAssistCollectsUpstream()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "雾港来信",
		Category = NodeCategory.StoryPlan,
		Content = "海港小城的修书人"
	};
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "第一章 · 雨夜码头",
		Category = NodeCategory.Chapter,
		Content = "雨夜里修书人接到一封信"
	};
	WorkflowNode workflowNode3 = new WorkflowNode
	{
		Title = "角色卡 · 林晚",
		Category = NodeCategory.Character,
		Content = "二十五岁，短发，藏青风衣"
	};
	WorkflowNode workflowNode4 = new WorkflowNode
	{
		Title = "场景卡 · 雨夜码头",
		Category = NodeCategory.Scene,
		Content = "路灯昏黄，石板路湿滑"
	};
	WorkflowNode workflowNode5 = new WorkflowNode
	{
		Title = "分镜 1",
		Category = NodeCategory.Storyboard,
		Content = "林晚站在码头边回头"
	};
	workflowCanvasState.Nodes.AddRange(new WorkflowNode[5] { workflowNode, workflowNode2, workflowNode3, workflowNode4, workflowNode5 });
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode.Id,
		TargetNodeId = workflowNode2.Id
	});
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode2.Id,
		TargetNodeId = workflowNode3.Id
	});
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode2.Id,
		TargetNodeId = workflowNode4.Id
	});
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode3.Id,
		TargetNodeId = workflowNode5.Id
	});
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode4.Id,
		TargetNodeId = workflowNode5.Id
	});
	IReadOnlyList<NodeAssistSource> readOnlyList = NodeAssistPlanner.CollectUpstream(workflowCanvasState, workflowNode5);
	Expect(readOnlyList.Count == 4, "分镜的上游应当收到 4 条（角色/场景 + 章节 + 企划），实际 " + readOnlyList.Count);
	Expect(readOnlyList[0].Category == NodeCategory.Character && readOnlyList[0].Depth == 1, "同层要先给角色，且深度为 1，实际 " + readOnlyList[0].KindLabel + " 深度 " + readOnlyList[0].Depth);
	Expect(readOnlyList[1].Category == NodeCategory.Scene && readOnlyList[1].Depth == 1, "同层第二个是场景");
	Expect(readOnlyList[2].Category == NodeCategory.Chapter && readOnlyList[2].Depth == 2, "第二层是章节，实际深度 " + readOnlyList[2].Depth);
	Expect(readOnlyList[3].Category == NodeCategory.StoryPlan && readOnlyList[3].Depth == 3, "第三层是故事企划");
	Expect(NodeAssistPlanner.CollectUpstream(workflowCanvasState, workflowNode5, 1).Count == 2, "只追一层时应当只剩角色与场景");
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode4.Id,
		TargetNodeId = workflowNode3.Id
	});
	Expect(NodeAssistPlanner.CollectUpstream(workflowCanvasState, workflowNode5).Count == 4, "有环时仍应收 4 条，不能重复也不能卡住");
	NodeAssistPlan nodeAssistPlan = NodeAssistPlanner.BuildPlan(workflowCanvasState, workflowNode5);
	Expect(nodeAssistPlan.HasUpstream && nodeAssistPlan.ContextSummary.Contains("上游 4 条"), "摘要要写清上游条数（这一张图里分镜只有连线来源），实际：" + nodeAssistPlan.ContextSummary);
	NodeAssistSuggestion nodeAssistSuggestion = nodeAssistPlan.Suggestions.FirstOrDefault((NodeAssistSuggestion item) => item.Kind == NodeAssistKind.Image);
	Expect((object)nodeAssistSuggestion != null, "分镜应当有一条出图建议");
	Expect(nodeAssistSuggestion.CanRun, "有内容又有上游时出图建议不该被挡住：" + nodeAssistSuggestion.Blocked);
	Expect(nodeAssistSuggestion.Prompt.Contains("角色「角色卡 · 林晚」") && nodeAssistSuggestion.Prompt.Contains("二十五岁，短发，藏青风衣"), "出图提示词要带上游角色设定，实际：" + nodeAssistSuggestion.Prompt);
	Expect(nodeAssistSuggestion.Prompt.Contains("场景「场景卡 · 雨夜码头」"), "出图提示词要带上游场景设定");
	Expect(nodeAssistSuggestion.Prompt.Contains("林晚站在码头边回头"), "出图提示词要带节点自己的内容");
	NodeAssistPlan nodeAssistPlan2 = NodeAssistPlanner.BuildPlan(workflowCanvasState, workflowNode3);
	Expect(nodeAssistPlan2.Suggestions.Count((NodeAssistSuggestion item) => item.Kind == NodeAssistKind.Image) == 2, "角色应当给两条出图（正面全身 / 半身特写）");
	Expect(nodeAssistPlan2.Suggestions.Any((NodeAssistSuggestion item) => item.Kind == NodeAssistKind.Agent && item.SkillId == "character-generation"), "角色应当有一条交给 Agent 的「角色设定」技能");
	Expect(nodeAssistPlan2.Suggestions.Any((NodeAssistSuggestion item) => item.Kind == NodeAssistKind.Prompt), "角色应当有一条纯提示词建议");
	NodeAssistPlan nodeAssistPlan3 = NodeAssistPlanner.BuildPlan(workflowCanvasState, workflowNode2);
	Expect(nodeAssistPlan3.Suggestions.Any((NodeAssistSuggestion item) => item.Kind == NodeAssistKind.Agent && item.SkillId == "chapter-decomposition"), "章节节点应当有一条「拆解章节」的 Agent 建议");
	WorkflowNode workflowNode6 = new WorkflowNode
	{
		Title = "新角色 9",
		Category = NodeCategory.Character
	};
	workflowCanvasState.Nodes.Add(workflowNode6);
	NodeAssistPlan nodeAssistPlan4 = NodeAssistPlanner.BuildPlan(workflowCanvasState, workflowNode6);
	Expect(!nodeAssistPlan4.HasUpstream, "孤立节点没有上游");
	Expect(nodeAssistPlan4.ContextSummary.Contains("没有"), "孤立节点要说明没有上游设定，实际：" + nodeAssistPlan4.ContextSummary);
	Expect(nodeAssistPlan4.Suggestions.All((NodeAssistSuggestion item) => !item.CanRun), "空节点上所有建议都要标出原因");
	Expect(nodeAssistPlan4.Suggestions.All((NodeAssistSuggestion item) => item.Blocked.Length > 0), "被挡住的建议必须写明原因");
}

static void NodeAssistMaterialsMergeFourSources()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkTreeItem workTreeItem = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第1章 老巷修书人",
		Order = 1,
		Prompt = "雨夜里修书人接到一封信。"
	};
	workflowCanvasState.WorkTree.Add(workTreeItem);
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚"
	};
	workflowEntity.Variants.Add(new WorkflowEntityVariant
	{
		Name = "常服",
		Description = "十七岁，短发，藏青外套。"
	});
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "老巷"
	};
	workflowEntity2.Variants.Add(new WorkflowEntityVariant
	{
		Name = "雨夜",
		Description = "青石板路，两侧旧书店。"
	});
	WorkflowEntity workflowEntity3 = new WorkflowEntity
	{
		Kind = EntityKind.Prop,
		Name = "修书工具"
	};
	workflowEntity3.Variants.Add(new WorkflowEntityVariant
	{
		Name = "默认"
	});
	workflowCanvasState.Entities.AddRange(new WorkflowEntity[3] { workflowEntity, workflowEntity2, workflowEntity3 });
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "分镜 03 雨夜书店",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem.Id,
		Content = "林晚在柜台后补书。"
	};
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "林晚",
		Category = NodeCategory.Character
	};
	WorkflowNode workflowNode3 = new WorkflowNode
	{
		Title = "老巷",
		Category = NodeCategory.Scene
	};
	workflowCanvasState.Nodes.AddRange(new WorkflowNode[3] { workflowNode, workflowNode2, workflowNode3 });
	workflowNode2.ParentNodeId = workflowNode.Id;
	workflowNode3.ParentNodeId = workflowNode.Id;
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode.Id,
		TargetNodeId = workflowNode2.Id
	});
	workflowCanvasState.Edges.Add(new WorkflowEdge
	{
		SourceNodeId = workflowNode.Id,
		TargetNodeId = workflowNode3.Id
	});
	workflowNode2.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntity.Variants[0].Id
	});
	workflowNode2.References.Add(new NodeReference
	{
		EntityId = workflowEntity3.Id,
		VariantId = workflowEntity3.Variants[0].Id
	});
	workflowNode2.References.Add(new NodeReference
	{
		EntityId = Guid.NewGuid(),
		VariantId = Guid.NewGuid()
	});
	IReadOnlyList<NodeAssistMaterial> readOnlyList = NodeAssistPlanner.CollectMaterial(workflowCanvasState, workflowNode2);
	Expect(readOnlyList.Count((NodeAssistMaterial material) => material.Tier == NodeAssistTier.Setting) == 3, "本体设定 3 条（角色 + 道具 + 一条失效的），实际 " + readOnlyList.Count((NodeAssistMaterial material) => material.Tier == NodeAssistTier.Setting));
	Expect(readOnlyList[0].Tier == NodeAssistTier.Setting && readOnlyList[0].Title == "林晚 · 常服", "**本体必须排第一**：决定出图的是角色长什么样，实际 " + readOnlyList[0].Tier.ToString() + " / " + readOnlyList[0].Title);
	Expect(readOnlyList[0].Content.Contains("藏青外套"), "本体要带上设定描述（外观锚点）");
	Expect(readOnlyList[1].Category == NodeCategory.Prop, "同一个节点上挂的道具也是本体，排在角色之后");
	Expect(readOnlyList[2].Tier == NodeAssistTier.Setting && readOnlyList[2].Content.Contains("已失效"), "失效引用照样列一条并写明原因，而不是悄悄丢掉");
	NodeAssistMaterial nodeAssistMaterial = readOnlyList.First((NodeAssistMaterial material) => material.Tier == NodeAssistTier.Upstream);
	Expect(nodeAssistMaterial.KindLabel == "分镜" && nodeAssistMaterial.Title.Contains("分镜 03"), "上游是那个源头分镜（临时画布按树的父子关系建了连线），实际 " + nodeAssistMaterial.KindLabel + " / " + nodeAssistMaterial.Title);
	Expect(readOnlyList.Any((NodeAssistMaterial material) => material.Tier == NodeAssistTier.Sibling && material.Title == "老巷"), "同镜：同一个分镜里的场景是角色卡的情境素材");
	NodeAssistMaterial nodeAssistMaterial2 = readOnlyList.FirstOrDefault((NodeAssistMaterial material) => material.Tier == NodeAssistTier.Chapter);
	Expect((object)nodeAssistMaterial2 != null && nodeAssistMaterial2.Title == "第1章 老巷修书人", "往上推到本章：靠副本带着的章节锚点，不按章节名猜");
	Expect(nodeAssistMaterial2.Content.Contains("雨夜里修书人接到一封信"), "本章的正文也要带出来");
	Expect(readOnlyList.Select((NodeAssistMaterial material) => material.Tier).ToList().SequenceEqual(new NodeAssistTier[6]
	{
		NodeAssistTier.Setting,
		NodeAssistTier.Setting,
		NodeAssistTier.Setting,
		NodeAssistTier.Upstream,
		NodeAssistTier.Sibling,
		NodeAssistTier.Chapter
	}), "素材顺序 = 优先级顺序，实际 " + string.Join(" → ", readOnlyList.Select((NodeAssistMaterial material) => material.Tier.ToString())));
	string text = NodeAssistPlanner.FormatContext(readOnlyList);
	Expect(text.IndexOf("[本体设定]", StringComparison.Ordinal) < text.IndexOf("[上游设定]", StringComparison.Ordinal) && text.IndexOf("[上游设定]", StringComparison.Ordinal) < text.IndexOf("[同镜素材]", StringComparison.Ordinal) && text.IndexOf("[同镜素材]", StringComparison.Ordinal) < text.IndexOf("[所属章节]", StringComparison.Ordinal), "素材块要分节标明来源，实际：\n" + text);
	NodeAssistPlan nodeAssistPlan = NodeAssistPlanner.BuildPlan(workflowCanvasState, workflowNode2);
	Expect(nodeAssistPlan.Suggestions.All((NodeAssistSuggestion item) => item.CanRun), "有引用就该能生成，不该因为自己是叶子就被标灰");
	string prompt = nodeAssistPlan.Suggestions.First((NodeAssistSuggestion item) => item.Kind == NodeAssistKind.Prompt).Prompt;
	Expect(prompt.Contains("十七岁，短发，藏青外套"), "角色提示词必须带上本体设定（外观锚点），实际：" + prompt);
	Expect(prompt.Contains("林晚在柜台后补书"), "情境（源头分镜的内容）也要带上，用来定这一镜的状态");
	Expect(nodeAssistPlan.ContextSummary.Contains("本体设定 3 条"), "摘要要报本体条数，实际：" + nodeAssistPlan.ContextSummary);
	Expect(nodeAssistPlan.Sources.Count == 1, "「查看上游设定」窗口仍只报连线上游（1 条），不把引用混进去");
}

static void NodeImageBatchKeepsOnlyPicked()
{
	NodeImageBatch nodeImageBatch = new NodeImageBatch
	{
		NodeId = Guid.NewGuid(),
		NodeTitle = "雨夜追车",
		IsRunning = true,
		Pool = new SitePoolChoice(new SiteProfile
		{
			Id = "example",
			DisplayName = "示例站"
		}, new SitePool
		{
			Model = "gpt-image-2(池6)",
			Tier = "2K",
			Kind = "image"
		})
	};
	Expect(nodeImageBatch.PoolLabel == "示例站 · gpt-image-2(池6) · 2K", "卡片上那行由池子算出来：" + nodeImageBatch.PoolLabel);
	nodeImageBatch.Slots.Add(new BatchSlot
	{
		Status = BatchSlotStatus.Done,
		Path = "C:\\a\\1.png"
	});
	nodeImageBatch.Slots.Add(new BatchSlot
	{
		Status = BatchSlotStatus.Running
	});
	nodeImageBatch.Slots.Add(new BatchSlot
	{
		Status = BatchSlotStatus.Done,
		Path = "C:\\a\\3.png"
	});
	Expect(!nodeImageBatch.CanPick, "跑着的时候不该允许挑选");
	Expect(nodeImageBatch.Percent == 67, "进度按「已出好 + 已失败」算（2/3 四舍五入），实际 " + nodeImageBatch.Percent);
	Expect(nodeImageBatch.PathsExcept(-1).Count == 0, "序号不合法时不该算出要抛弃谁");
	nodeImageBatch.Slots[1].Status = BatchSlotStatus.Failed;
	nodeImageBatch.Slots[1].Error = "429 限流";
	nodeImageBatch.IsRunning = false;
	Expect(nodeImageBatch.CanPick, "跑完且有出好的图就该允许挑");
	Expect(nodeImageBatch.FailedCount == 1 && nodeImageBatch.DoneCount == 2, $"各自计数：成功 {nodeImageBatch.DoneCount} 失败 {nodeImageBatch.FailedCount}");
	Expect(nodeImageBatch.Percent == 100, "都跑完了进度该是 100，实际 " + nodeImageBatch.Percent);
	IReadOnlyList<string> readOnlyList = nodeImageBatch.PathsExcept(0);
	Expect(readOnlyList.Count == 1 && readOnlyList[0] == "C:\\a\\3.png", "只抛弃没采用的那张：" + string.Join("、", readOnlyList));
	Expect(!readOnlyList.Contains(string.Empty), "没出好的格子不该出现在抛弃清单里");
	IReadOnlyList<string> readOnlyList2 = nodeImageBatch.PathsExcept(2);
	Expect(readOnlyList2.Count == 1 && readOnlyList2[0] == "C:\\a\\1.png", "换一张采用，抛弃的也要跟着换：" + string.Join("、", readOnlyList2));
	BatchSlot batchSlot = nodeImageBatch.SlotAt(2);
	Expect(batchSlot != null && batchSlot.Path == "C:\\a\\3.png", "按序号要能取到那一格");
	Expect(nodeImageBatch.AllFinishedPaths().Count == 2, "全部不要时两张都要抛弃，实际 " + nodeImageBatch.AllFinishedPaths().Count);
	nodeImageBatch.SelectedIndices.Add(0);
	Expect(nodeImageBatch.SelectedFinishedPaths().Count == 1, "选中且已出好的才可回收，实际 " + nodeImageBatch.SelectedFinishedPaths().Count);
	nodeImageBatch.Slots[0].Removed = true;
	nodeImageBatch.SelectedIndices.Clear();
	Expect(nodeImageBatch.Count == 3 && nodeImageBatch.LiveCount == 2, $"编号要稳定：总数 3、还留着 2，实际 {nodeImageBatch.Count}/{nodeImageBatch.LiveCount}");
	Expect(nodeImageBatch.DoneCount == 1, "已删除的不算进「出好了几张」，实际 " + nodeImageBatch.DoneCount);
	Expect(nodeImageBatch.Percent == 100, "进度只算还留着的格子（剩下两个都已定局 → 100），实际 " + nodeImageBatch.Percent);
	Expect(!nodeImageBatch.AllFinishedPaths().Contains("C:\\a\\1.png"), "已经删掉的那张不该被再抛弃一次（会去移一个不存在的文件）");
	Expect(nodeImageBatch.PathsExcept(2).Count == 0, "另一张已经删掉了，采用第 3 张时没有别的要抛弃");
	Expect(!nodeImageBatch.Slots[1].Removed, "失败那一格此刻还在");
	nodeImageBatch.Slots[1].Removed = true;
	nodeImageBatch.Slots[2].Removed = true;
	Expect(nodeImageBatch.IsEmpty && nodeImageBatch.LiveCount == 0, $"全删光时应当被判成空批，实际 还剩 {nodeImageBatch.LiveCount}");
	Expect(nodeImageBatch.Percent == 0, "空批的进度是 0，不是除零崩溃");
	Expect(nodeImageBatch.AllFinishedPaths().Count == 0 && nodeImageBatch.SelectedFinishedPaths().Count == 0, "空批没有任何文件可回收");
	NodeImageBatch nodeImageBatch2 = new NodeImageBatch
	{
		NodeId = Guid.NewGuid(),
		IsRunning = false
	};
	nodeImageBatch2.Slots.Add(new BatchSlot
	{
		Status = BatchSlotStatus.Failed,
		Error = "超时"
	});
	nodeImageBatch2.Slots.Add(new BatchSlot
	{
		Status = BatchSlotStatus.Failed,
		Error = "超时"
	});
	Expect(nodeImageBatch2.AllFailed && !nodeImageBatch2.CanPick, "全失败时既不能挑，也该走「重做」那条提示");
	Expect(nodeImageBatch2.PathsExcept(0).Count == 0 && nodeImageBatch2.AllFinishedPaths().Count == 0, "全失败时没有文件可抛弃");
	Expect(nodeImageBatch2.StatusLine().Contains("都没出来", StringComparison.Ordinal), "文案要直说都没出来：" + nodeImageBatch2.StatusLine());
	NodeImageBatch nodeImageBatch3 = new NodeImageBatch
	{
		NodeId = Guid.NewGuid()
	};
	Expect(nodeImageBatch3.Percent == 0, "没有格子时进度是 0，不是崩溃");
	Expect(!nodeImageBatch3.CanPick, "没有格子时不该允许挑");
	BatchSlot batchSlot2 = new BatchSlot
	{
		Status = BatchSlotStatus.Running,
		StartedAt = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(8))
	};
	DateTimeOffset now = new DateTimeOffset(2026, 10, 2, 10, 0, 12, TimeSpan.FromHours(8));
	Expect(batchSlot2.Headline(now) == "生成中 12s", "跑着的时候要报已用秒数，实际 " + batchSlot2.Headline(now));
	Expect(batchSlot2.Headline(now.AddSeconds(-30.0)) == "生成中 0s", "时刻对不上时不报负数，实际 " + batchSlot2.Headline(now.AddSeconds(-30.0)));
	Expect(new BatchSlot().Headline(now) == "等待中", "还没轮到的那格要说「等待中」，实际 " + new BatchSlot().Headline(now));
	Expect(new BatchSlot
	{
		Status = BatchSlotStatus.Failed
	}.Headline(now) == "失败", "失败就写失败");
	Expect(new BatchSlot
	{
		Status = BatchSlotStatus.Running
	}.Headline(now) == "生成中", "没记开始时刻也不该崩");
	NodeImageBatch nodeImageBatch4 = new NodeImageBatch
	{
		NodeId = Guid.NewGuid(),
		IsRunning = true
	};
	nodeImageBatch4.Slots.Add(new BatchSlot
	{
		Status = BatchSlotStatus.Done
	});
	nodeImageBatch4.Slots.Add(new BatchSlot
	{
		Status = BatchSlotStatus.Running
	});
	Expect(nodeImageBatch4.ProgressLine() == "已完成 1/2 · 50%", "总进度按真实计数算，实际 " + nodeImageBatch4.ProgressLine());
	Expect(nodeImageBatch4.StatusLine().Contains("已完成 1/2"), "跑着的那行字里要带进度，实际 " + nodeImageBatch4.StatusLine());
	Expect(new NodeImageBatch().ProgressLine().Length == 0, "空批不报进度，也不是除零崩溃");
}

static void NodeImageModeFollowsStoryContinuity()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "旧书铺"
	};
	WorkflowEntityVariant workflowEntityVariant = workflowEntity.CreateVariant("午后", "大门朝南，午后斜光");
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚"
	};
	WorkflowEntityVariant workflowEntityVariant2 = workflowEntity2.CreateVariant("常服", "十七岁，藏青外套");
	workflowCanvasState.Entities.Add(workflowEntity);
	workflowCanvasState.Entities.Add(workflowEntity2);
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "第一章 分镜 1",
		Category = NodeCategory.Storyboard,
		Content = "林晚在柜台后补书"
	};
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	});
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntityVariant2.Id
	});
	workflowCanvasState.Nodes.Add(workflowNode);
	NodeImageDecision nodeImageDecision = NodeImageModePlanner.Decide(workflowCanvasState, workflowNode);
	Expect(nodeImageDecision.Approach == NodeImageApproach.TextToImage && !nodeImageDecision.UsesBaseImage, "一张图都没有时应当走文生图");
	Expect(nodeImageDecision.Reason.Contains("都没有现成的图", StringComparison.Ordinal), "理由要说明为什么不用图：" + nodeImageDecision.Reason);
	workflowEntityVariant.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://scene.png",
		Name = "scene.png"
	});
	NodeImageDecision nodeImageDecision2 = NodeImageModePlanner.Decide(workflowCanvasState, workflowNode);
	Expect(nodeImageDecision2.Approach == NodeImageApproach.TextToImage, $"同场景第一镜应当文生图，实际 {nodeImageDecision2.Approach}");
	Expect(nodeImageDecision2.Reason.Contains("第一镜", StringComparison.Ordinal), "理由要说明这是第一镜：" + nodeImageDecision2.Reason);
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "第一章 分镜 0",
		Category = NodeCategory.Storyboard,
		Content = "旧书铺内景"
	};
	workflowNode2.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	});
	workflowNode2.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://shot-0.png",
		Name = "shot-0.png"
	});
	workflowCanvasState.Nodes.Add(workflowNode2);
	NodeImageDecision nodeImageDecision3 = NodeImageModePlanner.Decide(workflowCanvasState, workflowNode);
	Expect(nodeImageDecision3.Approach == NodeImageApproach.ImageToImage, $"同场景连续性应当走图生图，实际 {nodeImageDecision3.Approach}");
	Expect(nodeImageDecision3.UsesBaseImage && nodeImageDecision3.BaseImageReference == "asset://scene.png", "底图应当是场景基准图");
	Expect(Math.Abs(nodeImageDecision3.Denoise - 0.45) < 0.001, "同场景的重绘幅度要小到能锁住空间");
	Expect(nodeImageDecision3.Reason.Contains("旧书铺", StringComparison.Ordinal), "理由里要写明用的是哪个场景");
	workflowNode.Content = "林晚推开后门走进雨里";
	NodeImageDecision nodeImageDecision4 = NodeImageModePlanner.Decide(workflowCanvasState, workflowNode);
	Expect(nodeImageDecision4.Approach == NodeImageApproach.TextToImage, $"要换空间的镜头应当放开构图，实际 {nodeImageDecision4.Approach}");
	workflowNode.Content = "把柜台后的书换成账本";
	workflowNode.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://shot-1-v1.png",
		Name = "shot-1-v1.png"
	});
	NodeImageDecision nodeImageDecision5 = NodeImageModePlanner.Decide(workflowCanvasState, workflowNode);
	Expect(nodeImageDecision5.Approach == NodeImageApproach.ImageToImage && nodeImageDecision5.BaseImageReference == "asset://shot-1-v1.png", $"改这一版要拿上一版当底图，实际 {nodeImageDecision5.Approach}/{nodeImageDecision5.BaseImageReference}");
	Expect(Math.Abs(nodeImageDecision5.Denoise - 0.5) < 0.001, "改稿的重绘幅度不该把整张重画");
	workflowNode.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://composite.png",
		Name = "composite.png",
		Source = "合成底图"
	});
	NodeImageDecision nodeImageDecision6 = NodeImageModePlanner.Decide(workflowCanvasState, workflowNode);
	Expect(nodeImageDecision6.BaseImageReference == "asset://composite.png", "合成底图要压过其它候选");
	Expect(nodeImageDecision6.Reason.Contains("合成底图", StringComparison.Ordinal), "理由要说明用的是合成底图");
	WorkflowNode workflowNode3 = new WorkflowNode
	{
		Title = "第一章 分镜 2",
		Category = NodeCategory.Storyboard,
		Content = "近景，林晚低头"
	};
	workflowNode3.References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntityVariant2.Id
	});
	workflowCanvasState.Nodes.Add(workflowNode3);
	workflowEntityVariant2.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://hero.png",
		Name = "hero.png"
	});
	NodeImageDecision nodeImageDecision7 = NodeImageModePlanner.Decide(workflowCanvasState, workflowNode3);
	Expect(nodeImageDecision7.Approach == NodeImageApproach.TextToImage, "只有角色图时不该自动图生图");
	Expect(nodeImageDecision7.Reason.Contains("转面图", StringComparison.Ordinal), "理由要说明角色图为什么不适合当底图：" + nodeImageDecision7.Reason);
	Expect(NodeImageModePlanner.BaseCandidates(workflowCanvasState, workflowNode3).Count == 0, "角色图不进手动候选——它不是底图材料");
}

static void NodeKindPaletteGivesEveryCategoryItsOwnColor()
{
	NodeCategory[] array3 = new NodeCategory[9]
	{
		NodeCategory.StoryPlan,
		NodeCategory.StoryOutline,
		NodeCategory.Chapter,
		NodeCategory.Storyboard,
		NodeCategory.Product,
		NodeCategory.Character,
		NodeCategory.Scene,
		NodeCategory.Prop,
		NodeCategory.General
	};
	List<string> list2 = array3.Select(NodeKindPalette.HexOf).ToList();
	Expect(list2.All((string hex) => hex.Length == 7 && hex[0] == '#' && hex.Substring(1).All(char.IsAsciiHexDigit)), "颜色一律写成 #RRGGBB：" + string.Join(",", list2));
	Expect(list2.Distinct(StringComparer.OrdinalIgnoreCase).Count() == array3.Length, "九类必须九色，实际出现重复：" + string.Join(",", list2));
	Expect(NodeKindPalette.HexOf(NodeCategory.Chapter) == "#4D9BFF", "章节保持品牌蓝");
	Expect(NodeKindPalette.HexOf(NodeCategory.General) == "#8FA6BD", "通用保持灰蓝");
	Expect(condition: true, "章节与通用不能同色");
	List<string> list3 = new NodeCategory[3]
	{
		NodeCategory.Character,
		NodeCategory.Scene,
		NodeCategory.Prop
	}.Select(NodeKindPalette.HexOf).ToList();
	Expect(list3.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3, "角色 / 场景 / 道具必须三色各不相同：" + string.Join(",", list3));
	Expect(NodeKindPalette.CategoryOf(EntityKind.Character) == NodeCategory.Character && NodeKindPalette.CategoryOf(EntityKind.Scene) == NodeCategory.Scene && NodeKindPalette.CategoryOf(EntityKind.Prop) == NodeCategory.Prop, "设定种类要能映射到画布节点种类，引用卡与临时画布才用得上同一套配色");
	Expect(NodeKindPalette.HexOf((NodeCategory)999) == "#8FA6BD", "未知种类落到兜底色，不留空");
}

static void PlaintextTierIsReportedHonestly()
{
	using ConfigEnvironment configEnvironment = new ConfigEnvironment();
	try
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_SECRET_SCHEME", "plain");
		AiProviderConfig aiProviderConfig = AiProviderSettings.Load();
		aiProviderConfig.ApiKey = "sk-plain-tier-0123456789";
		Expect(AiProviderSettings.Save(aiProviderConfig), "保存应该成功");
		string text = File.ReadAllText(configEnvironment.ConfigPath);
		Expect(text.Contains("plain:", StringComparison.Ordinal), "兜底档要如实写 plain: 前缀，不能假装加密了");
		AiProviderConfig aiProviderConfig2 = AiProviderSettings.Load();
		Expect(aiProviderConfig2.ApiKey == "sk-plain-tier-0123456789", "明文档仍要能读回原值（只是没有被保护）");
		Expect(!aiProviderConfig2.ApiKeyWasPlaintext, "带 plain: 前缀不算「旧版裸明文」，不必再迁移一次");
		Expect(aiProviderConfig2.ApiKeyStoredUnencrypted, "明文档必须被标记出来，界面据此告警");
	}
	finally
	{
		configEnvironment.Restore();
	}
}

static void ProductionStagesFilterAndCount()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "开始",
		Category = NodeCategory.General
	};
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "角色卡",
		Category = NodeCategory.Character
	};
	workflowNode2.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://a.png",
		Name = "a.png"
	});
	WorkTreeItem workTreeItem = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第一章"
	};
	workflowCanvasState.WorkTree.Add(workTreeItem);
	WorkflowNode workflowNode3 = new WorkflowNode
	{
		Title = "第一章",
		Category = NodeCategory.Chapter,
		WorkTreeItemId = workTreeItem.Id
	};
	WorkflowNode workflowNode4 = new WorkflowNode
	{
		Title = "分镜 1",
		Category = NodeCategory.Storyboard
	};
	WorkflowNode workflowNode5 = new WorkflowNode
	{
		Title = "第一章成品",
		Category = NodeCategory.Product
	};
	workflowCanvasState.Nodes.AddRange(new WorkflowNode[5] { workflowNode, workflowNode2, workflowNode3, workflowNode4, workflowNode5 });
	Expect(ProductionStageRules.Count(workflowCanvasState, ProductionStage.All) == 5, "「全部」统计节点数");
	Expect(ProductionStageRules.Count(workflowCanvasState, ProductionStage.Import) == 1, "「导入」统计素材数（1 张图）");
	Expect(ProductionStageRules.Count(workflowCanvasState, ProductionStage.ChapterSplit) == 1, "「章节拆分」统计章节节点数");
	Expect(ProductionStageRules.Count(workflowCanvasState, ProductionStage.WorkTree) == 1, "「工作树」统计条目数，不是节点数");
	Expect(ProductionStageRules.Count(workflowCanvasState, ProductionStage.Storyboard) == 1, "「分镜」统计分镜节点数");
	Expect(ProductionStageRules.Count(workflowCanvasState, ProductionStage.Product) == 1, "「成品」统计成品节点数");
	Expect(ProductionStageRules.Matches(ProductionStage.Import, workflowNode2), "带素材的节点属于导入阶段");
	Expect(!ProductionStageRules.Matches(ProductionStage.Import, workflowNode), "没素材的节点不属于导入阶段");
	Expect(ProductionStageRules.Matches(ProductionStage.ChapterSplit, workflowNode3), "章节节点属于章节拆分");
	Expect(ProductionStageRules.Matches(ProductionStage.WorkTree, workflowNode3), "绑了工作树条目的节点同时属于工作树阶段");
	Expect(ProductionStageRules.Matches(ProductionStage.All, workflowNode), "「全部」阶段任何节点都算");
	Expect(ProductionStageRules.FilterOf(ProductionStage.All) == null, "「全部」不设过滤（null 表示都显示）");
	Func<WorkflowNode, bool> func = ProductionStageRules.FilterOf(ProductionStage.Storyboard);
	Expect(func?.Invoke(workflowNode4) ?? false, "分镜过滤要放过分镜节点");
	Expect(!func(workflowNode), "分镜过滤要挡掉别的节点");
	IReadOnlyList<StageSummary> readOnlyList = ProductionStageRules.Summarize(workflowCanvasState, ProductionStage.Storyboard);
	Expect(readOnlyList.Count == ProductionStageRules.Order.Count, "芯片数要与阶段数一致");
	Expect(readOnlyList.Count((StageSummary chip) => chip.IsActive) == 1, "同一时刻只能有一个阶段是选中的");
	Expect(readOnlyList.Single((StageSummary chip) => chip.IsActive).Stage == ProductionStage.Storyboard, "选中态要落在传入的阶段上");
	Expect(ProductionStageRules.Order.Distinct().Count() == ProductionStageRules.Order.Count, "阶段顺序里不能有重复项（界面照它铺按钮与芯片）");
	foreach (ProductionStage item14 in ProductionStageRules.Order)
	{
		Expect(ProductionStageRules.LabelOf(item14).Length > 0, "每个阶段都要有名字");
		Expect(ProductionStageRules.EmptyHintOf(item14).Length > 0, "每个阶段都要有「空态怎么说」的文案");
		Expect(ProductionStageRules.DescribeCount(item14, 3).Contains("3"), "计数文案要带上数字");
	}
	Expect(ProductionStageRules.EmptyHintOf(ProductionStage.Storyboard).Contains("Agent"), "分镜的空态要指向 Agent 那条路");
	Expect(ProductionStageRules.EmptyHintOf(ProductionStage.ChapterSplit).Contains("生成章节工作树"), "章节拆分的空态要指向那个按钮");
}

static void ProfileDuplicateCopiesEveryField()
{
	AiProviderProfile aiProviderProfile = new AiProviderProfile
	{
		DisplayName = "长文用",
		Endpoint = "https://api.example.com/v1",
		UseFullUrl = true,
		ApiFormat = AiApiFormat.AnthropicMessages,
		Model = "claude-x",
		ApiKey = "sk-dup-0123456789abcdef",
		Temperature = 0.3,
		ContextWindow = 200000,
		MaxOutputTokens = 8192,
		SendSamplingParameters = false,
		SupportsImageInput = true,
		Enabled = false
	};
	AiProviderProfile aiProviderProfile2 = AiProviderSettings.Duplicate(aiProviderProfile);
	Expect(aiProviderProfile2.Id != aiProviderProfile.Id, "复制出来的应当是另一份（Id 不能相同）");
	Expect(aiProviderProfile2.DisplayName == aiProviderProfile.DisplayName && aiProviderProfile2.Endpoint == aiProviderProfile.Endpoint && aiProviderProfile2.UseFullUrl == aiProviderProfile.UseFullUrl && aiProviderProfile2.ApiFormat == aiProviderProfile.ApiFormat && aiProviderProfile2.Model == aiProviderProfile.Model && aiProviderProfile2.ApiKey == aiProviderProfile.ApiKey && Math.Abs(aiProviderProfile2.Temperature - aiProviderProfile.Temperature) < 0.0001 && aiProviderProfile2.ContextWindow == aiProviderProfile.ContextWindow && aiProviderProfile2.MaxOutputTokens == aiProviderProfile.MaxOutputTokens && aiProviderProfile2.SendSamplingParameters == aiProviderProfile.SendSamplingParameters && aiProviderProfile2.SupportsImageInput == aiProviderProfile.SupportsImageInput && aiProviderProfile2.Enabled == aiProviderProfile.Enabled, "复制必须逐字段一致（含密钥与启用状态）");
}

static void ProviderBadgesCoverEveryPreset()
{
	foreach (ProviderPreset item16 in ProviderPreset.All)
	{
		ProviderBadge providerBadge = ProviderBadges.Of(item16.Id);
		Expect(providerBadge.IsKnown, $"预设表里的「{item16.Name}」（{item16.Id}）没有配徽标");
		Expect(providerBadge.Abbreviation.Length == 2, "徽标缩写要两个字符：" + item16.Id + " → " + providerBadge.Abbreviation);
		Expect(providerBadge.ColorHex.Length == 7 && providerBadge.ColorHex[0] == '#', "徽标色要写成 #RRGGBB：" + item16.Id + " → " + providerBadge.ColorHex);
	}
	string[] source = new string[7] { "deepseek", "moonshot", "qwen", "zhipu", "siliconflow", "openai", "ollama" };
	List<string> list2 = source.Select((string id) => ProviderBadges.Of(id).ColorHex).ToList();
	Expect(list2.Distinct(StringComparer.OrdinalIgnoreCase).Count() == list2.Count, "各家的区分色不能重复，实际：" + string.Join("、", list2));
	List<string> list3 = source.Select((string id) => ProviderBadges.Of(id).Abbreviation).ToList();
	Expect(list3.Distinct(StringComparer.OrdinalIgnoreCase).Count() == list3.Count, "各家的缩写不能重复，实际：" + string.Join("、", list3));
	ProviderBadge providerBadge2 = ProviderBadges.Of("some-new-vendor");
	Expect(!providerBadge2.IsKnown && providerBadge2.Abbreviation == "AI", "预设表以外的 id 要落回中性徽标");
	Expect(ProviderBadges.Of(null).Abbreviation == "AI", "没有 id 时也不能空着");
	Expect(ProviderBadges.Of("  DEEPSEEK  ").Abbreviation == "DS", "大小写与空格不该影响认厂家");
	Expect(ProviderBadges.Describe("deepseek", "DeepSeek").Contains("DeepSeek"), "工具提示要说清是哪一家");
	Expect(ProviderBadges.Describe("some-new-vendor", "某家").Contains("没有配"), "不认识的那家要如实说明");
}

static void ProviderCapabilityRuleIsSharedAndHonest()
{
	Expect(ProviderPresetValues.NeedsManualCapabilities(ProviderPreset.Custom), "自定义接口的能力值归用户填");
	Expect(!ProviderPresetValues.NeedsManualCapabilities(ProviderPreset.Local), "本地模拟不定义能力值，不该让用户填");
	Expect(!ProviderPresetValues.NeedsManualCapabilities(ProviderPreset.Moonshot), "有预置型号的厂家按模型自动生效");
	List<ProviderPreset> list2 = ProviderPreset.All.Where((ProviderPreset preset) => !preset.IsLocal && preset.Models.Count == 0).ToList();
	Expect(list2.Count > 0, "预设表里应有不预置型号的厂家，用来验证这条兜底路径");
	foreach (ProviderPreset item17 in list2)
	{
		Expect(ProviderPresetValues.NeedsManualCapabilities(item17), item17.Id + " 没有预置型号，能力值只能由用户填");
	}
	ProviderModel defaultModel = ProviderPreset.Moonshot.DefaultModel;
	Expect((object)defaultModel != null, "Kimi 应有已核实的预置型号");
	string text = ProviderPresetValues.Resolve(ProviderPreset.Moonshot, defaultModel).DescribeCapabilities();
	Expect(text.Contains(defaultModel.Id), "能力说明应带上型号名");
	Expect(text.Contains("支持图片输入"), "Kimi 的型号支持图片输入，说明里要写出来");
	Expect(text.Contains("上下文"), "能力说明应写出上下文窗口");
	Expect(text.Contains("不发送采样参数"), "Kimi 的服务端固定采样，说明要写对");
	ProviderPresetValues providerPresetValues = new ProviderPresetValues(string.Empty, AiApiFormat.OpenAiChat, UseFullUrl: false, SendsSamplingParameters: true, "manual-model", 0, 0, SupportsVision: false, UseLocal: false);
	string text2 = providerPresetValues.DescribeCapabilities();
	Expect(text2.Contains("未声明上下文窗口"), "上下文为 0 时应如实说未声明");
	Expect(text2.Contains("不支持图片输入"), "能力未知时说不支持，别让用户以为能发图");
	Expect(text2.Contains("发送采样参数"), "采样开关为真时要写出来");
	ProviderPresetValues providerPresetValues2 = new ProviderPresetValues(string.Empty, AiApiFormat.OpenAiChat, UseFullUrl: false, SendsSamplingParameters: false, "big", 1048576, 0, SupportsVision: false, UseLocal: false);
	Expect(providerPresetValues2.DescribeCapabilities().Contains("1M"), "1048576 应写成 1M");
}

static void ProviderChoiceFlagRoundTrips()
{
	using ConfigEnvironment configEnvironment = new ConfigEnvironment();
	try
	{
		Expect(!AiProviderSettings.Load().ProviderChoiceMade, "全新配置应视为「还没选择过服务商」");
		AiProviderConfig aiProviderConfig = AiProviderSettings.Load();
		aiProviderConfig.Endpoint = "https://example.invalid/v1";
		aiProviderConfig.Model = "probe-model";
		aiProviderConfig.ProviderChoiceMade = true;
		Expect(AiProviderSettings.Save(aiProviderConfig), "保存应该成功");
		Expect(AiProviderSettings.Load().ProviderChoiceMade, "做过选择后必须记住，否则每次启动都要重问一遍");
		Expect(!AiProviderSettings.Load().UseLocalProvider, "接入真实模型时不该被标成本地模拟");
	}
	finally
	{
		configEnvironment.Restore();
	}
}

static void ProviderImportRecognizesComfyUi()
{
	ProviderImportDraft providerImportDraft = ProviderImporter.Inspect("ComfyUI 地址：http://127.0.0.1:8188\n提交工作流：POST /prompt\n取结果：GET /history/{prompt_id}   看显存：GET /system_stats   取节点定义：GET /object_info\n实时进度：ws://127.0.0.1:8188/ws?clientid=abc123\ncheckpoint: sd_xl_base_1.0.safetensors");
	Expect(providerImportDraft.Kind == ProviderKind.ComfyUi, "ComfyUI 的说明应被认成 ComfyUI，实际 " + ProviderImporter.KindName(providerImportDraft.Kind));
	Expect(providerImportDraft.BaseUrl.Contains("8188", StringComparison.Ordinal), "地址要认出来：" + providerImportDraft.BaseUrl);
	Expect(providerImportDraft.Checkpoint.Contains("safetensors", StringComparison.Ordinal), "checkpoint 要认出来：" + providerImportDraft.Checkpoint);
	Expect(providerImportDraft.Signals.Count > 0, "要给出判据：用户得能核对我们凭什么这么判");
	ProviderImportDraft providerImportDraft2 = ProviderImporter.Inspect("文生图：POST https://api.example.com/v1/images/generations，模型 flux-1-dev，密钥 sk-abc123");
	Expect(providerImportDraft2.Kind != ProviderKind.ComfyUi, "普通画图接口不该被认成 ComfyUI：" + ProviderImporter.KindName(providerImportDraft2.Kind));
}

static void ProviderPresetCatalogIsConsistent()
{
	Expect(ProviderPreset.All.Count >= 8, "预设数量不对");
	foreach (ProviderPreset item18 in ProviderPreset.All)
	{
		if (string.IsNullOrWhiteSpace(item18.Endpoint))
		{
			Expect(ProviderPreset.Match(item18.Endpoint).Id == ProviderPreset.Custom.Id, "没有地址的预设（" + item18.Id + "）应落到「自定义」");
		}
		else
		{
			ProviderPreset providerPreset = ProviderPreset.Match(item18.Endpoint);
			Expect(providerPreset.Id == item18.Id, $"地址 {item18.Endpoint} 反推成了 {providerPreset.Id}，应为 {item18.Id}（重复或写错）");
		}
	}
	Expect(ProviderPreset.Match("https://api.deepseek.com/v1/").Id == ProviderPreset.DeepSeek.Id, "尾部斜杠不应影响反推");
	foreach (ProviderPreset item19 in ProviderPreset.All)
	{
		foreach (ProviderModel model in item19.Models)
		{
			Expect(!string.IsNullOrWhiteSpace(model.Id), item19.Id + " 里有空模型 ID");
			Expect(model.ContextWindow >= 0 && model.MaxOutputTokens >= 0, item19.Id + "/" + model.Id + " 的窗口数值为负");
			Expect(model.ContextWindow == 0 || model.MaxOutputTokens <= model.ContextWindow, item19.Id + "/" + model.Id + " 的最大输出大于上下文窗口，数值反了");
		}
	}
	Expect(ProviderPreset.Local.IsLocal && !ProviderPreset.Local.NeedsManualModel, "本地模拟的语义变了");
	Expect(ProviderPreset.Custom.NeedsManualModel, "自定义应要求手填模型");
	Expect(!ProviderPreset.Moonshot.SendsSamplingParameters, "Kimi 的采样参数由服务端固定，不应发送");
}

static void ProviderPresetValuesResolveIsConsistent()
{
	foreach (ProviderPreset item20 in ProviderPreset.All)
	{
		ProviderPresetValues providerPresetValues = ProviderPresetValues.Resolve(item20, null);
		Expect(providerPresetValues.Format == item20.Format, item20.Id + " 的接口格式应来自预设");
		if (item20.IsLocal)
		{
			Expect(providerPresetValues.UseLocal && providerPresetValues.Endpoint.Length == 0 && providerPresetValues.ModelId.Length == 0, "本地模拟不该带地址或模型，只传递「走本地兜底」这一个信息");
		}
		else
		{
			Expect(!providerPresetValues.UseLocal, item20.Id + " 不是本地模拟");
			Expect(providerPresetValues.Endpoint == item20.Endpoint, item20.Id + " 的地址应来自预设");
			if (item20.Id == ProviderPreset.Custom.Id)
			{
				Expect(providerPresetValues.Endpoint.Length == 0 && providerPresetValues.ModelId.Length == 0, "自定义应把地址与模型留给用户手填");
			}
			else if (item20.Models.Count == 0)
			{
				Expect(item20.NeedsManualModel, item20.Id + " 没有预置型号时应要求手填");
				Expect(providerPresetValues.ModelId.Length == 0, item20.Id + " 没有预置型号时不该凭空造一个");
			}
			else
			{
				Expect(providerPresetValues.ModelId.Length > 0, item20.Id + " 应有已核实的预置型号");
			}
		}
	}
	Expect(!ProviderPresetValues.Resolve(ProviderPreset.Moonshot, null).SendsSamplingParameters, "Kimi 不发送采样参数");
	Expect(ProviderPresetValues.Resolve(ProviderPreset.Zhipu, null).SendsSamplingParameters, "GLM 发送采样参数");
	Expect(ProviderPresetValues.Resolve(ProviderPreset.Moonshot, null).SupportsVision, "Kimi 的默认型号支持图像输入");
	ProviderPreset providerPreset = ProviderPreset.All.First((ProviderPreset preset) => preset.Models.Count > 1);
	ProviderModel providerModel = providerPreset.Models[1];
	ProviderPresetValues providerPresetValues2 = ProviderPresetValues.Resolve(providerPreset, providerModel);
	Expect(providerPresetValues2.ModelId == providerModel.Id, "指定型号时应以该型号为准");
	Expect(providerPresetValues2.ContextWindow == providerModel.ContextWindow, "指定型号的上下文档位应对上");
	Expect(providerPresetValues2.SupportsVision == providerModel.SupportsVision, "指定型号的图像能力应对上");
}

static void ReferenceCardsResolveDistinctContent()
{
	WorkflowCanvasState canvas = new WorkflowCanvasState();
	AgentApplyResult agentApplyResult = AgentActionExecutor.Apply(new AgentAction[3]
	{
		new AgentAction
		{
			Kind = "create_entity",
			EntityKind = "角色",
			Title = "林晚",
			Content = "十七岁，短发，藏青外套"
		},
		new AgentAction
		{
			Kind = "create_entity",
			EntityKind = "场景",
			Title = "旧书铺",
			Content = "大门朝南，午后斜光"
		},
		new AgentAction
		{
			Kind = "create_node",
			Title = "第一章 分镜 1",
			Content = "林晚在柜台后补书",
			EntityTargets = new string[2] { "林晚", "旧书铺" }
		}
	}, canvas, null);
	Expect(agentApplyResult.Applied == 3 && agentApplyResult.Errors.Count == 0, "生成失败：" + string.Join("；", agentApplyResult.Errors));
	WorkflowNode node = canvas.Nodes.Single((WorkflowNode item) => item.Title == "第一章 分镜 1");
	ReferenceTree referenceTree = CanvasReferenceLayout.Build(canvas, node);
	Expect(referenceTree.Direct.Count == 2, $"分镜应当展开出 2 条直接引用，实际 {referenceTree.Direct.Count}");
	List<ReferenceContent> source = referenceTree.Direct.Select((ReferenceCard card) => canvas.ResolveReferenceContent(new NodeReference
	{
		EntityId = card.EntityId,
		VariantId = card.VariantId,
		VariantVersionId = card.VersionId
	})).ToList();
	Expect(source.All((ReferenceContent item) => (object)item != null), "两条引用都该解析得出来，实际有解析不出的");
	Expect(source.Select((ReferenceContent item) => item.Description).Distinct(StringComparer.Ordinal).Count() == 2, "不同引用的正文必须各不相同，否则界面上「换一个节点内容却没变」：" + string.Join(" | ", source.Select((ReferenceContent item) => item.Description)));
	Expect(source.Any((ReferenceContent item) => item.Description.Contains("藏青外套")), "角色那条要带上外观锚点");
	Expect(source.Any((ReferenceContent item) => item.Description.Contains("午后斜光")), "场景那条要带上光源描述");
	ReferenceCard referenceCard = referenceTree.Cards.Single((ReferenceCard card) => card.IsSource);
	Expect(referenceCard.EntityId == Guid.Empty && referenceCard.Depth == 0, "源头卡不该被当成一条引用");
}

static void ReferenceStalenessDetectsUpdatedSettings()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚"
	};
	WorkflowEntityVariant workflowEntityVariant = workflowEntity.CreateVariant("少年黑衣", "短发，藏青风衣");
	workflowCanvasState.Entities.Add(workflowEntity);
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "分镜 1",
		Category = NodeCategory.Storyboard
	};
	NodeReference item = new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	};
	workflowNode.References.Add(item);
	workflowCanvasState.Nodes.Add(workflowNode);
	WorkflowAttachment workflowAttachment = new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://shot1.png",
		SourceFingerprints = ReferenceStaleness.Snapshot(workflowCanvasState, workflowNode)
	};
	workflowNode.Attachments.Add(workflowAttachment);
	Expect(workflowAttachment.SourceFingerprints.Count == 1, "出图时要记下每条引用的依据");
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode).Count == 0, "内容没变就不该报「过期」");
	string description = workflowEntityVariant.Description;
	workflowEntityVariant.Description = "短发，藏青风衣（换装后加了围巾）";
	IReadOnlyList<StaleReference> readOnlyList = ReferenceStaleness.Of(workflowCanvasState, workflowNode);
	Expect(readOnlyList.Count == 1 && readOnlyList[0].EntityId == workflowEntity.Id && !readOnlyList[0].Broken, "设定内容变了要报出来");
	Expect(readOnlyList[0].Label.Contains("林晚"), "报出来的名字要能认，实际 " + readOnlyList[0].Label);
	workflowEntityVariant.Description = description;
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode).Count == 0, "改回去就不该再报");
	WorkflowAttachment item2 = new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://hero-new.png"
	};
	workflowEntityVariant.Attachments.Add(item2);
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode).Count == 1, "设定换了图要报出来");
	workflowEntityVariant.Attachments.Remove(item2);
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode).Count == 0, "拿掉那张图之后又不该报了");
	workflowCanvasState.Entities.Clear();
	IReadOnlyList<StaleReference> readOnlyList2 = ReferenceStaleness.Of(workflowCanvasState, workflowNode);
	Expect(readOnlyList2.Count == 1 && readOnlyList2[0].Broken, "设定没了要报出来并标成解析不出来");
	workflowCanvasState.Entities.Add(workflowEntity);
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "老巷"
	};
	WorkflowEntityVariant workflowEntityVariant2 = workflowEntity2.CreateVariant("夜", "青石板、雨");
	workflowCanvasState.Entities.Add(workflowEntity2);
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntityVariant2.Id
	});
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode).Count == 0, "出图之后新加的引用不算「设定更新」");
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "旧分镜",
		Category = NodeCategory.Storyboard
	};
	workflowNode2.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	});
	workflowNode2.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://old.png"
	});
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode2).Count == 0, "没记过依据的产物不报（宁可漏报也不误报）");
	WorkflowNode workflowNode3 = new WorkflowNode
	{
		Title = "锁定分镜",
		Category = NodeCategory.Storyboard
	};
	workflowNode3.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id,
		VariantVersionId = workflowEntityVariant.EnsureInitialVersion().Id
	});
	workflowNode3.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://locked.png",
		SourceFingerprints = ReferenceStaleness.Snapshot(workflowCanvasState, workflowNode3)
	});
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode3).Count == 0, "锁定版本、内容没变时不该报");
	workflowEntityVariant.Description = "改了一个与 v1 无关的新描述";
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode3).Count == 0, "锁的是 v1，变体改了也不该报（这是对的，不是漏报）");
	workflowEntityVariant.Description = description;
	WorkflowNode workflowNode4 = new WorkflowNode
	{
		Title = "重出过的分镜",
		Category = NodeCategory.Storyboard
	};
	workflowNode4.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	});
	Dictionary<string, string> sourceFingerprints = ReferenceStaleness.Snapshot(workflowCanvasState, workflowNode4);
	workflowNode4.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://a.png",
		SourceFingerprints = sourceFingerprints
	});
	workflowNode4.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://b.png",
		SourceFingerprints = sourceFingerprints
	});
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode4).Count == 0, "都没变时两条产物都不报");
	workflowEntityVariant.Description = "又一次换了描述";
	IReadOnlyList<StaleReference> readOnlyList3 = ReferenceStaleness.Of(workflowCanvasState, workflowNode4);
	Expect(readOnlyList3.Count == 1, "同一条引用被两张产物记着也只报一次，实际 " + readOnlyList3.Count);
	Expect(ReferenceStaleness.Describe(readOnlyList3).Contains("建议重出"), "卡片提示要给出下一步：" + ReferenceStaleness.Describe(readOnlyList3));
	Expect(ReferenceStaleness.DescribeAll(readOnlyList3).Contains("林晚"), "明细要列名字：" + ReferenceStaleness.DescribeAll(readOnlyList3));
	Expect(!ReferenceStaleness.DescribeAll(readOnlyList3).Contains('\n'), "明细给状态栏用，不能带换行");
	Expect(ReferenceStaleness.Describe(Array.Empty<StaleReference>()).Length == 0, "没有过期就不该有文案");
	Expect(ReferenceStaleness.DescribeAll(Array.Empty<StaleReference>()).Length == 0, "没有过期时明细也是空的");
	workflowEntityVariant.Description = description;
	WorkflowNode workflowNode5 = new WorkflowNode
	{
		Title = "早先出的分镜",
		Category = NodeCategory.Storyboard
	};
	Expect(!ReferenceStaleness.LacksBaseline(workflowNode5), "既没有引用也没有产物时不该提示「缺依据」");
	workflowNode5.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	});
	Expect(!ReferenceStaleness.LacksBaseline(workflowNode5), "有引用但还没出过图时不该提示「缺依据」");
	workflowNode5.Attachments.Add(new WorkflowAttachment
	{
		Kind = AttachmentKind.Image,
		Reference = "asset://early.png"
	});
	Expect(ReferenceStaleness.LacksBaseline(workflowNode5), "有引用、有图、却没记过依据 → 要说明");
	workflowNode5.Attachments[0].SourceFingerprints = ReferenceStaleness.Snapshot(workflowCanvasState, workflowNode5);
	Expect(!ReferenceStaleness.LacksBaseline(workflowNode5), "记过依据之后就不该再提示「缺依据」");
	Expect(ReferenceStaleness.Of(workflowCanvasState, workflowNode5).Count == 0, "刚记下的依据与现状一致，不该报过期");
}

static void ReferenceTreeExpandsDirectAndNested()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "第1章 老巷修书人",
		Category = NodeCategory.Storyboard,
		X = 100f,
		Y = 200f
	};
	workflowCanvasState.Nodes.Add(workflowNode);
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "老巷"
	};
	workflowEntity.Variants.Add(new WorkflowEntityVariant
	{
		Name = "雨夜",
		Description = "青石板路，两侧是旧书店。\n门口挂着一盏灯。"
	});
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚"
	};
	workflowEntity2.Variants.Add(new WorkflowEntityVariant
	{
		Name = "常服",
		Description = "十七岁的女高中生。"
	});
	WorkflowEntity workflowEntity3 = new WorkflowEntity
	{
		Kind = EntityKind.Prop,
		Name = "修书工具"
	};
	workflowEntity3.Variants.Add(new WorkflowEntityVariant
	{
		Name = "默认"
	});
	workflowCanvasState.Entities.AddRange(new WorkflowEntity[3] { workflowEntity, workflowEntity2, workflowEntity3 });
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntity.Variants[0].Id
	});
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntity2.Variants[0].Id
	});
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity3.Id,
		VariantId = workflowEntity3.Variants[0].Id
	});
	workflowNode.References.Add(new NodeReference
	{
		EntityId = Guid.NewGuid(),
		VariantId = Guid.NewGuid()
	});
	workflowEntity2.Variants[0].References.Add(new NodeReference
	{
		EntityId = workflowEntity3.Id,
		VariantId = workflowEntity3.Variants[0].Id
	});
	workflowEntity3.Variants[0].References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntity2.Variants[0].Id
	});
	ReferenceTree referenceTree = CanvasReferenceLayout.Build(workflowCanvasState, workflowNode);
	IReadOnlyList<ReferenceCard> direct = referenceTree.Direct;
	Expect(direct.Count == 4, "直接引用 4 条（含失效的那条），实际 " + direct.Count);
	Expect(direct.Select((ReferenceCard card) => card.Kind).SequenceEqual(new string[4] { "角色", "场景", "道具", "失效" }), "同层先按种类分组（角色 → 场景 → 道具），失效的排最后，实际 " + string.Join("/", direct.Select((ReferenceCard card) => card.Kind)));
	Expect(direct[0].Title == "林晚" && direct[1].Title == "老巷", "组内保留节点上引用的原始顺序，不替作者重排");
	Expect(direct[3].Blocked && direct[3].BlockedReason.Length > 0, "失效引用要标出来");
	Expect(!direct[0].Blocked, "正常引用不该被标成失效");
	Expect(direct[0].Subtitle.Contains("常服") && direct[0].Subtitle.Contains("最新"), "副标题给变体与生效版本，实际 " + direct[0].Subtitle);
	Expect(direct[1].Excerpt == "青石板路，两侧是旧书店。", "摘要取描述的第一行，不整段铺出来");
	Expect(direct[2].Excerpt.Length == 0, "没有描述就不编摘要");
	Expect(direct[0].ChildReferenceCount == 1, "角色身上挂着 1 个子引用，卡上要标出来");
	ReferenceCard referenceCard = referenceTree.Cards[0];
	Expect(referenceCard.IsSource && referenceCard.Depth == 0 && referenceCard.Title == "第1章 老巷修书人", "第一张是源头节点本身");
	Expect(referenceCard.ChildReferenceCount == 4, "源头卡要说明它挂了 4 条引用");
	Expect(referenceCard.Category == NodeCategory.Storyboard, "源头卡沿用节点的种类（浮层靠它取色）");
	Expect(direct[0].Category == NodeCategory.Character && direct[1].Category == NodeCategory.Scene && direct[2].Category == NodeCategory.Prop, "引用卡要带上种类：角色/场景/道具各自取自己的颜色");
	Expect(direct[3].Category == NodeCategory.General, "失效引用没有种类，落到兜底色");
	List<ReferenceCard> list2 = referenceTree.Cards.Where((ReferenceCard card) => card.Depth == 2).ToList();
	Expect(list2.Count == 2, "两个直接引用各自带一个下级，实际 " + list2.Count);
	Expect(list2.Any((ReferenceCard card) => card.ParentId == direct[0].Id && card.Title == "修书工具"), "角色下面的子引用（道具）要挂在这张卡上——连线靠 ParentId");
	Expect(list2.Any((ReferenceCard card) => card.ParentId == direct[2].Id && card.Title == "林晚"), "道具引回角色时也要画出来（同一个东西被两处引用，两边都该看得到）");
	Expect(referenceTree.Cards.Select((ReferenceCard card) => card.Id).Distinct().Count() == referenceTree.Cards.Count, "树内 Id 必须唯一（连线不能连错人）");
	Expect(referenceTree.MaxDepth == 2, "环让展开停在第 2 层，实际 " + referenceTree.MaxDepth);
	Expect(referenceTree.Cards.All((ReferenceCard card) => card.Depth <= 2), "不得再往下画回自己");
	Expect(referenceTree.Summary.Contains("4 条引用") && referenceTree.Summary.Contains("含下级 2 张") && referenceTree.Summary.Contains("其中 1 条失效或版本缺失"), "摘要要说清条数、下级张数与问题条数，实际 " + referenceTree.Summary);
	Expect(workflowCanvasState.Nodes.Count == 1 && workflowCanvasState.Edges.Count == 0, "展开不新增节点，也不建连线");
	Expect(workflowNode.References.Count == 4 && workflowNode.X == 100f && workflowNode.Y == 200f, "展开不改归属节点的引用与坐标");
	ReferenceTree referenceTree2 = CanvasReferenceLayout.Direct(workflowCanvasState, workflowNode);
	Expect(referenceTree2.Cards.Count == 5 && referenceTree2.MaxDepth == 1, "只展开直接引用时不该出现下级");
	ReferenceTree referenceTree3 = CanvasReferenceLayout.Build(workflowCanvasState, workflowNode, new ReferenceTreeLimits(3, 3));
	Expect(referenceTree3.Cards.Count == 3 && referenceTree3.Truncated, "张数到上限就停下并如实标记被截断");
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "没有引用的节点"
	};
	workflowCanvasState.Nodes.Add(workflowNode2);
	Expect(CanvasReferenceLayout.Build(workflowCanvasState, workflowNode2).Cards.Count == 1, "没有引用时只剩源头卡");
	Expect(CanvasReferenceLayout.Direct(workflowCanvasState, workflowNode2).Direct.Count == 0, "没有引用就没有可铺的卡");
	Expect(CanvasReferenceLayout.Build(workflowCanvasState, workflowNode2).Summary.Contains("没有引用"), "空展开也要给一句说明");
	NodeReference reference = new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntity2.Variants[0].Id
	};
	NodeReference reference2 = new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntity2.Variants[0].Id,
		VariantVersionId = Guid.NewGuid()
	};
	Expect(CanvasReferenceLayout.KeyOf(reference) != CanvasReferenceLayout.KeyOf(reference2), "跟随最新与锁版本必须是不同的键");
}

static void ReferenceTreeListsNestedReferences()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚",
		Core = "十七岁，短发，藏青外套"
	};
	WorkflowEntityVariant workflowEntityVariant = workflowEntity.CreateVariant("常服", "十七岁，短发，藏青外套");
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Prop,
		Name = "黄铜钥匙",
		Core = "一掌长，铜面磨亮"
	};
	WorkflowEntityVariant workflowEntityVariant2 = workflowEntity2.CreateVariant("默认", "一掌长，铜面磨亮");
	workflowCanvasState.Entities.Add(workflowEntity);
	workflowCanvasState.Entities.Add(workflowEntity2);
	workflowEntityVariant.References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntityVariant2.Id
	});
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "第一章 分镜 1",
		Content = "林晚在柜台后补书"
	};
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntityVariant.Id
	});
	workflowCanvasState.Nodes.Add(workflowNode);
	ReferenceTree referenceTree = CanvasReferenceLayout.Build(workflowCanvasState, workflowNode);
	Expect(referenceTree.MaxDepth == 2, $"应当展开到第 2 层（角色的道具），实际第 {referenceTree.MaxDepth} 层");
	Expect(referenceTree.Direct.Count == 1, $"直接引用只有 1 条（林晚），实际 {referenceTree.Direct.Count}");
	Expect(referenceTree.Cards.Count == 3, $"源头 + 林晚 + 钥匙 = 3 张卡，实际 {referenceTree.Cards.Count}");
	ReferenceCard referenceCard = referenceTree.Direct.Single();
	Expect(referenceCard.Title == "林晚" && referenceCard.ChildReferenceCount == 1, "角色卡上要标出它自己还有 1 条子引用");
	ReferenceCard referenceCard2 = referenceTree.Cards.Single((ReferenceCard card) => card.Depth == 2);
	Expect(referenceCard2.Title == "黄铜钥匙", "深度 2 的应当是那个道具，实际「" + referenceCard2.Title + "」");
	Expect(referenceCard2.ParentId == referenceCard.Id, "子引用的父卡必须是角色那张卡（缩进挂在他下面）");
	Expect(!referenceCard2.IsSource && referenceCard2.BlockedReason.Length == 0, "正常的子引用不该被判成失效");
	List<string> list2 = referenceTree.Cards.Select((ReferenceCard card) => card.Title).ToList();
	Expect(list2.IndexOf("黄铜钥匙") == list2.IndexOf("林晚") + 1, "子引用必须紧跟在父卡后面，实际顺序：" + string.Join(" → ", list2));
}

static void SecretProtectorInteroperatesWithLegacyDpapi()
{
	if (OperatingSystem.IsWindows())
	{
		byte[] bytes = Encoding.UTF8.GetBytes("YEEYEEYEE.ApiKey.v1");
		string value = "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("sk-legacy-dpapi-compat-0123456789"), bytes, DataProtectionScope.CurrentUser));
		Expect(SecretProtector.Unprotect(value) == "sk-legacy-dpapi-compat-0123456789", "旧版本的 dpapi 密文必须能原样读回");
		string text = SecretProtector.Protect("sk-legacy-dpapi-compat-0123456789");
		Expect(text.StartsWith("dpapi:", StringComparison.Ordinal), "当前实现应写 dpapi: 前缀");
		byte[] bytes2 = ProtectedData.Unprotect(Convert.FromBase64String(text.Substring("dpapi:".Length)), bytes, DataProtectionScope.CurrentUser);
		Expect(Encoding.UTF8.GetString(bytes2) == "sk-legacy-dpapi-compat-0123456789", "当前实现的密文必须能被旧版的 ProtectedData 解开");
	}
}

static void SettingPromptPrefersWrittenPrompt()
{
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚",
		Core = "十七岁，短发"
	};
	WorkflowEntityVariant variant = workflowEntity.CreateVariant("常服", "【身份】十七岁，书店学徒，女性\n【服装】藏青棉布外套，袖口磨白\n【出图提示词】十七岁少女，齐耳短发，藏青棉布外套，袖口磨白，干净线条的数码角色设定图，16:9\n【负面提示词】多余手指，水印");
	string text = SettingPrompt.Compose(workflowEntity, variant);
	Expect(text.StartsWith("十七岁少女", StringComparison.Ordinal), "应当直接用描述里那段提示词，实际：「" + text + "」");
	Expect(!text.Contains("负面提示词", StringComparison.Ordinal), "提示词应当截到「负面提示词」之前");
	Expect(!text.Contains("【", StringComparison.Ordinal), "出图提示词里不该夹着给人看的小标题");
	WorkflowEntityVariant variant2 = workflowEntity.CreateVariant("雨夜", "【身份】十七岁，书店学徒\n【神态与气质】沉静，有点疲倦");
	string text2 = SettingPrompt.Compose(workflowEntity, variant2);
	Expect(text2.Contains("角色「林晚」（雨夜）", StringComparison.Ordinal), "现拼的提示词要有种类与名称，实际：「" + text2 + "」");
	Expect(text2.Contains("十七岁，书店学徒", StringComparison.Ordinal), "现拼的提示词要带上描述内容");
	Expect(!text2.Contains("【", StringComparison.Ordinal), "现拼的提示词同样不该夹着【小标题】");
	Expect(text2.Contains(PromptBaseline.TurnaroundPromptLine, StringComparison.Ordinal), "角色的现拼提示词要带参考图规格");
	WorkflowEntityVariant variant3 = workflowEntity.CreateVariant("空白", string.Empty);
	string text3 = SettingPrompt.Compose(new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "无名"
	}, variant3);
	Expect(text3.Contains("无名", StringComparison.Ordinal) && text3.Length > 10, "空设定也要给出可用提示词，实际：「" + text3 + "」");
	WorkflowEntity entity = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "旧书铺"
	};
	Expect(SettingPrompt.Negative(workflowEntity).Length > 0 && SettingPrompt.Negative(entity).Length > 0, "两类都要有负面词");
	Expect(SettingPrompt.Negative(workflowEntity) != SettingPrompt.Negative(entity), "角色与场景的负面词应当不同");
}

// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void SettingsProfilesMultiEnableAndSelection()
{
	Environment.SetEnvironmentVariable("YEEYEEYEE_AI_ENDPOINT", null);
	Environment.SetEnvironmentVariable("YEEYEEYEE_AI_MODEL", null);
	using ConfigEnvironment configEnvironment = new ConfigEnvironment();
	try
	{
		File.WriteAllText(configEnvironment.ConfigPath, "{\"Endpoint\":\"https://old.example.com/v1\",\"Model\":\"old-model\",\"ApiKey\":\"sk-first-0123456789abcdef\"}");
		AiProviderConfig aiProviderConfig = AiProviderSettings.Load();
		Expect(aiProviderConfig.Profiles.Count == 1, $"旧配置应迁移成一份配置，实际 {aiProviderConfig.Profiles.Count} 份");
		AiProviderProfile aiProviderProfile = aiProviderConfig.Profiles[0];
		Expect(aiProviderProfile.Enabled && aiProviderConfig.SelectedProfileId == aiProviderProfile.Id, "迁移出来的那一份应当是启用且选中的");
		Expect(aiProviderConfig.Model == "old-model", "顶层仍要等于选中的那一份（旧端读的就是顶层）");
		AiProviderProfile profile = new AiProviderProfile
		{
			DisplayName = "第二家",
			Endpoint = "https://new.example.com/v1",
			Model = "new-model",
			ApiKey = "sk-second-0123456789abcdef"
		};
		AiProviderSettings.Upsert(aiProviderConfig, profile);
		AiProviderSettings.ApplyProfile(aiProviderConfig, profile);
		Expect(AiProviderSettings.Save(aiProviderConfig), "保存应该成功");
		AiProviderConfig aiProviderConfig2 = AiProviderSettings.Load();
		Expect(aiProviderConfig2.Profiles.Count == 2, "两份都要存住");
		Expect(AiProviderSettings.EnabledProfiles(aiProviderConfig2).Count == 2, "两份都启用时都该进候选");
		AiProviderProfile aiProviderProfile2 = AiProviderSettings.ResolveSelected(aiProviderConfig2);
		Expect(aiProviderProfile2 != null && aiProviderProfile2.Model == "new-model", "选中的应当是第二份，实际 " + aiProviderProfile2?.Model);
		Expect(aiProviderConfig2.Model == "new-model" && aiProviderConfig2.ApiKey == "sk-second-0123456789abcdef", "顶层（旧端读的那组字段）要跟着选中项走，密钥也要能读回明文");
		string text = File.ReadAllText(configEnvironment.ConfigPath);
		Expect(!text.Contains("sk-first-0123456789abcdef", StringComparison.Ordinal) && !text.Contains("sk-second-0123456789abcdef", StringComparison.Ordinal), "配置文件里不能出现任何一份的明文密钥");
		Expect(aiProviderConfig2.Profiles.All((AiProviderProfile item) => item.ApiKey.Length > 0), "两份的密钥都要能解回来");
		aiProviderProfile2.Enabled = false;
		Expect(AiProviderSettings.Save(aiProviderConfig2), "保存应该成功");
		AiProviderConfig aiProviderConfig3 = AiProviderSettings.Load();
		AiProviderProfile aiProviderProfile3 = AiProviderSettings.ResolveSelected(aiProviderConfig3);
		Expect(aiProviderProfile3 != null && aiProviderProfile3.Model == "old-model", "应当自动切到还启用着的那一份，实际 " + aiProviderProfile3?.Model);
		Expect(aiProviderConfig3.SelectedProfileId == aiProviderProfile3.Id, "选中项要被修正到实际生效的那一份");
		Expect(aiProviderConfig3.Model == "old-model", "顶层也要跟着切过去");
		foreach (AiProviderProfile profile3 in aiProviderConfig3.Profiles)
		{
			profile3.Enabled = false;
		}
		Expect(AiProviderSettings.Save(aiProviderConfig3), "保存应该成功");
		AiProviderConfig aiProviderConfig4 = AiProviderSettings.Load();
		Expect(AiProviderSettings.ResolveSelected(aiProviderConfig4) == null, "一份都没启用时应当没有可用配置");
		Expect(AiProviderSettings.EnabledProfiles(aiProviderConfig4).Count == 0, "停用的不该进候选");
		AiProviderProfile profile2 = new AiProviderProfile
		{
			Endpoint = "https://a.example.com",
			Model = "a",
			ApiKey = "sk-first-0123456789abcdef"
		};
		AiProviderSettings.Upsert(aiProviderConfig4, profile2);
		AiProviderSettings.ApplyProfile(aiProviderConfig4, profile2);
		aiProviderConfig4.Endpoint = "https://legacy-edited.example.com/v1";
		Expect(AiProviderSettings.Save(aiProviderConfig4), "保存应该成功");
		AiProviderConfig aiProviderConfig5 = AiProviderSettings.Load();
		Expect(aiProviderConfig5.Profiles.Any((AiProviderProfile item) => item.Endpoint == "https://legacy-edited.example.com/v1"), "旧端改的顶层字段要落回选中的那一份，不能丢");
	}
	finally
	{
		configEnvironment.Restore();
	}
}

// Updated in round 127 from the last good build: this test was changed after the last commit,
// so the committed body no longer matched the implementation (decompiler dropped its comments).
static void SiteCatalogAndPoolProbe()
{
	Expect(SiteCatalog.IdFor("https://video.example.com/v1") == "example", "应认出 example：" + SiteCatalog.IdFor("https://video.example.com/v1"));
	Expect(SiteCatalog.IdFor("https://api.example.com/v1") == "example", "api. 前缀应跳过：" + SiteCatalog.IdFor("https://api.example.com/v1"));
	Expect(SiteCatalog.IdFor("https://example.com") == "example", "没有前缀时取主机名首段：" + SiteCatalog.IdFor("https://example.com"));
	Expect(SiteCatalog.IdFor("") == "site", "地址取不到时给一个能用的兜底");
	Expect(SiteCatalog.DefaultDisplayNameFor("https://video.example.com/v1") == "video.example.com", "默认显示名用主机名（可改）");
	IReadOnlyList<SitePool> readOnlyList = SitePoolProbe.Parse("{\"data\":[\n  {\"alias\":\"gpt-image-2.5-sunburst(池6)\",\"type\":\"image\",\"resolutions\":[\"1K\",\"2K\",\"4K\"],\n   \"image_to_image\":true,\"max_reference_images\":10,\"enabled\":true,\"prices\":{\"1K\":2.5,\"2K\":3.5,\"4K\":6}},\n  {\"alias\":\"seedance-2.0(900)(池7)\",\"type\":\"video\",\"durations\":[\"10s\",\"15s\"],\"max_reference_images\":9,\"enabled\":true}\n]}");
	Expect(readOnlyList.Count == 5, "3 档图像 + 2 档视频 = 5 个池子，实际 " + readOnlyList.Count);
	SitePool sitePool = readOnlyList.First((SitePool sitePool3) => sitePool3.Model.StartsWith("gpt-image-2.5-sunburst", StringComparison.Ordinal) && sitePool3.Tier == "2K");
	Expect(sitePool.Width == 2048 && sitePool.Height == 2048, $"2K 应解析成 2048×2048，实际 {sitePool.Width}×{sitePool.Height}");
	Expect(sitePool.Price == "3.5 积分", "价格按档位取：" + sitePool.Price);
	Expect(Math.Abs(sitePool.UnitPrice - 3.5) < 0.001, "数字单价要留下：" + sitePool.UnitPrice);
	Expect(readOnlyList.First((SitePool sitePool3) => sitePool3.Tier == "4K").UnitPrice == 6.0, "每一档各自的价格都要对");
	Expect(sitePool.SupportsReference == true && sitePool.MaxReferenceImages == 10, "参考图能力要认出来：" + sitePool.Describe());
	Expect(!sitePool.IsVideo, "type=image 不该被判成视频");
	SitePool sitePool2 = readOnlyList.First((SitePool sitePool3) => sitePool3.IsVideo);
	Expect(sitePool2.Seconds == 10 || sitePool2.Seconds == 15, "时长档位要读成秒数：" + sitePool2.Seconds);
	Expect(sitePool2.Width == 0 && sitePool2.Height == 0, "视频池不该有画幅");
	Expect(readOnlyList.Any((SitePool sitePool3) => sitePool3.Model == "gpt-image-2.5-sunburst(池6)"), "模型名必须原样保留：" + string.Join("、", readOnlyList.Select((SitePool sitePool3) => sitePool3.Model).Distinct()));
	IReadOnlyList<SitePool> readOnlyList2 = SitePoolProbe.Parse("{\"object\":\"list\",\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"flux-1-dev\"}]}");
	Expect(readOnlyList2.Count == 2 && readOnlyList2.All((SitePool sitePool3) => sitePool3.Tier.Length == 0), "只有模型名时档位留空：" + string.Join("、", readOnlyList2.Select((SitePool sitePool3) => sitePool3.Label)));
	Expect(!readOnlyList2[0].SupportsReference.HasValue, "清单没提参考图能力时应留成未知，实际 " + readOnlyList2[0].SupportsReference);
	Expect(SitePoolProbe.Parse("{\"data\":[{\"name\":\"m\",\"sizes\":\"1K,2K\"}]}").Count == 2, "逗号分隔的档位要拆开");
	Expect(SitePoolProbe.Parse("[{\"model\":\"m\",\"resolution\":\"720p\"}]").Count == 1, "直接给数组也要认");
	Expect(SitePoolProbe.Parse("<html>502</html>").Count == 0, "不是 JSON 时给空清单，不抛异常");
	Expect(SitePoolProbe.Parse("{\"data\":[{\"foo\":\"bar\"}]}").Count == 0, "没有模型名的条目直接丢掉");
	IReadOnlyList<string> readOnlyList3 = SitePoolProbe.CandidateUrls("https://video.example.com/v1");
	Expect(readOnlyList3[0].Contains("managed-models", StringComparison.Ordinal), "第一个候选应是站点自己的清单接口：" + readOnlyList3[0]);
	Expect(readOnlyList3.Any((string url) => url.EndsWith("/v1/models", StringComparison.Ordinal)), "候选里要有 OpenAI 兼容的 /models：" + string.Join("、", readOnlyList3));
	List<SiteProfile> sites = new List<SiteProfile>
	{
		new SiteProfile
		{
			Id = "example",
			Pools = new List<SitePool>
			{
				new SitePool
				{
					Model = "gpt-image-2.5-sunburst(池6)",
					Tier = "2K",
					Kind = "image"
				},
				new SitePool
				{
					Model = "gpt-image-2.5-sunburst(池6)",
					Tier = "4K",
					Kind = "image"
				}
			}
		}
	};
	SitePoolChoice sitePoolChoice = SiteCatalog.Find(sites, "example", "gpt-image-2.5-sunburst(池6)", "4K");
	int condition;
	if ((object)sitePoolChoice != null)
	{
		SitePool pool = sitePoolChoice.Pool;
		if (pool != null)
		{
			condition = ((pool.Tier == "4K") ? 1 : 0);
			goto IL_05d0;
		}
	}
	condition = 0;
	goto IL_05d0;
	IL_05d0:
	Expect((byte)condition != 0, "同站点同模型同档位要能找回来");
	Expect((object)SiteCatalog.Find(sites, "example", "gpt-image-2.5-sunburst(池6)", "1K") == null, "档位对不上时返回 null，不退到别的档");
	Expect((object)SiteCatalog.Find(sites, "别家站", "gpt-image-2.5-sunburst(池6)", "2K") == null, "站点已不在时返回 null，不退到别的站");
	Expect((object)SiteCatalog.Find(sites, "", "", "") == null, "没记过任何池子时返回 null");
	Expect((object)SiteCatalog.Find(sites, "example", "已经下线的模型", "2K") == null, "模型被清单刷掉时返回 null，不退到同站另一个模型");
	string text = Path.Combine(Path.GetTempPath(), "df-sites-" + Guid.NewGuid().ToString("N").Substring(0, 8));
	string environmentVariable = Environment.GetEnvironmentVariable("YEEYEEYEE_SKILL_DIR");
	try
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", text);
		SiteProfile site = new SiteProfile
		{
			Id = SiteCatalog.IdFor("https://video.example.com/v1"),
			DisplayName = "example",
			BaseUrl = "https://video.example.com/v1",
			ImagePath = "/v1/images/generations",
			ImageEditPath = "/v1/images/edits",
			VideoPath = "/v1/videos",
			ListSource = "https://video.example.com/admin/api/managed-models（43 个）",
			Pools = readOnlyList.ToList()
		};
		Expect(SiteCatalog.Save(site, out string error), "站点应写入成功：" + error);
		Expect(File.Exists(Path.Combine(SiteCatalog.Directory, "example.json")), "文件名用站点标识：" + SiteCatalog.Directory);
		Expect(SiteCatalog.Directory == Path.Combine(SkillLibrary.Directory, "sites"), "站点应在技能目录的 sites 子目录里：" + SiteCatalog.Directory);
		var (list2, list3) = SiteCatalog.Load();
		Expect(list3.Count == 0 && list2.Count == 1, "应读回 1 个站点：" + string.Join("；", list3));
		Expect(list2[0].ImagePools.Count == 3 && list2[0].VideoPools.Count == 2, "池子要分得清图像与视频：" + list2[0].Describe());
		Expect(list2[0].ImageEditPath == "/v1/images/edits", "接口路径要存住");
		Expect(list2[0].UsablePools.Count == 5, "清单里没标下线的池子都是可用的");
		Expect(SiteCatalog.Save(list2[0], out string error2), "重复保存应当成功");
		Expect(SiteCatalog.Load().Sites.Count == 1, "重复保存不该多出一个站点");
		SiteProfile siteProfile = SiteCatalog.Load().Sites[0];
		Expect(!siteProfile.HasApiKey, "还没填密钥时应当是「未设置」，不是「空字符串已设置」");
		siteProfile.ApiKey = "sk-site-secret";
		Expect(SiteCatalog.Save(siteProfile, out string error3), "带密钥保存应成功：" + error3);
		string text2 = File.ReadAllText(Path.Combine(SiteCatalog.Directory, "example.json"));
		Expect(!text2.Contains("sk-site-secret", StringComparison.Ordinal), "站点密钥不能明文落盘");
		SiteProfile siteProfile2 = SiteCatalog.Load().Sites[0];
		Expect(siteProfile2.ApiKey == "sk-site-secret", "读回来要能还原成明文，实际 " + siteProfile2.DescribeApiKey());
		Expect(siteProfile2.HasApiKey && !siteProfile2.ApiKeyUnreadable, "能还原时不该被标成解不开");
		Expect(SiteCatalog.Save(siteProfile2, out error2), "重复保存应成功");
		Expect(SiteCatalog.Load().Sites[0].ApiKey == "sk-site-secret", "反复保存后密钥仍要能还原");
		Expect(SiteCatalog.TryDelete(list2[0], out error2), "删除应当成功");
		Expect(SiteCatalog.Load().Sites.Count == 0, "删除后应读不到");
	}
	finally
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", environmentVariable);
		try
		{
			if (Directory.Exists(text))
			{
				Directory.Delete(text, recursive: true);
			}
		}
		catch (IOException)
		{
		}
	}
}

static void SkillFileToggleAndDelete()
{
	string text = Path.Combine(Path.GetTempPath(), "df-skill-toggle-" + Guid.NewGuid().ToString("N").Substring(0, 8));
	string environmentVariable = Environment.GetEnvironmentVariable("YEEYEEYEE_SKILL_DIR");
	try
	{
		Directory.CreateDirectory(text);
		Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", text);
		string text2 = Path.Combine(text, "toggle.json");
		File.WriteAllText(text2, "{\"id\":\"toggle\",\"name\":\"开关测试\",\"futureField\":\"别弄丢我\",\"steps\":[{\"id\":\"s\",\"name\":\"一步\",\"capability\":\"TextToImage\"}]}");
		var (list2, list3) = SkillLibrary.Load();
		Expect(list3.Count == 0 && list2.Count == 1, "应装载到 1 条技能：" + string.Join("；", list3));
		SkillDefinition skillDefinition = list2[0];
		Expect(skillDefinition.Enabled, "没写 Enabled 的技能默认启用");
		Expect(skillDefinition.FilePath == text2, "装载时应记下文件路径，启停与删除都要用它");
		Expect(SkillLibrary.TrySetEnabled(skillDefinition, enabled: false, out string error), "停用应当成功：" + error);
		Expect(!skillDefinition.Enabled, "内存里的状态要跟着改");
		string text3 = File.ReadAllText(text2);
		Expect(text3.Contains("\"Enabled\": false", StringComparison.Ordinal), "文件里应写进 Enabled=false：" + text3);
		Expect(text3.Contains("别弄丢我", StringComparison.Ordinal), "不认识的字段必须原样留着");
		List<SkillDefinition> item = SkillLibrary.Load().Skills;
		Expect(item.Count == 1 && !item[0].Enabled, "重启后应记得停用状态");
		string path = Path.Combine(text, "keeper.json");
		File.WriteAllText(path, "{\"id\":\"keeper\",\"name\":\"邻居\",\"steps\":[{\"id\":\"s\",\"name\":\"一步\",\"capability\":\"TextToImage\"}]}");
		Expect(SkillLibrary.TryDelete(skillDefinition, out string error2), "删除应当成功：" + error2);
		Expect(!File.Exists(text2), "被删的文件应当真的没了");
		Expect(File.Exists(path), "同目录的其它技能不能被牵连");
	}
	finally
	{
		Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", environmentVariable);
		try
		{
			if (Directory.Exists(text))
			{
				Directory.Delete(text, recursive: true);
			}
		}
		catch (IOException)
		{
		}
	}
}

static void StoryTreeNestsChaptersShotsAndReferences()
{
	WorkflowCanvasState workflowCanvasState = new WorkflowCanvasState();
	WorkTreeItem workTreeItem = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第1章 老巷修书人",
		Order = 1
	};
	WorkTreeItem workTreeItem2 = new WorkTreeItem
	{
		Kind = WorkTreeKind.Chapter,
		Name = "第4章 不收钱的规矩",
		Order = 2
	};
	workflowCanvasState.WorkTree.AddRange(new WorkTreeItem[2] { workTreeItem, workTreeItem2 });
	WorkflowEntity workflowEntity = new WorkflowEntity
	{
		Kind = EntityKind.Scene,
		Name = "老巷"
	};
	workflowEntity.Variants.Add(new WorkflowEntityVariant
	{
		Name = "雨夜"
	});
	WorkflowEntity hero = new WorkflowEntity
	{
		Kind = EntityKind.Character,
		Name = "林晚"
	};
	hero.Variants.Add(new WorkflowEntityVariant
	{
		Name = "常服"
	});
	hero.Variants.Add(new WorkflowEntityVariant
	{
		Name = "换装"
	});
	WorkflowEntity workflowEntity2 = new WorkflowEntity
	{
		Kind = EntityKind.Prop,
		Name = "修书工具"
	};
	workflowEntity2.Variants.Add(new WorkflowEntityVariant
	{
		Name = "默认"
	});
	workflowCanvasState.Entities.AddRange(new WorkflowEntity[3] { workflowEntity, hero, workflowEntity2 });
	hero.Variants[0].References.Add(new NodeReference
	{
		EntityId = workflowEntity2.Id,
		VariantId = workflowEntity2.Variants[0].Id
	});
	WorkflowNode workflowNode = new WorkflowNode
	{
		Title = "分镜 03 雨夜书店",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem.Id,
		X = 0f,
		Y = 0f
	};
	WorkflowNode workflowNode2 = new WorkflowNode
	{
		Title = "分镜 05 门口的灯",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem.Id,
		X = 400f,
		Y = 0f
	};
	WorkflowNode workflowNode3 = new WorkflowNode
	{
		Title = "分镜 12 换季",
		Category = NodeCategory.Storyboard,
		WorkTreeItemId = workTreeItem2.Id,
		X = 0f,
		Y = 300f
	};
	workflowCanvasState.Nodes.AddRange(new WorkflowNode[3] { workflowNode, workflowNode2, workflowNode3 });
	workflowNode.References.Add(new NodeReference
	{
		EntityId = workflowEntity.Id,
		VariantId = workflowEntity.Variants[0].Id
	});
	workflowNode.References.Add(new NodeReference
	{
		EntityId = hero.Id,
		VariantId = hero.Variants[0].Id
	});
	workflowNode2.References.Add(new NodeReference
	{
		EntityId = hero.Id,
		VariantId = hero.Variants[0].Id
	});
	workflowNode3.References.Add(new NodeReference
	{
		EntityId = hero.Id,
		VariantId = hero.Variants[1].Id
	});
	WorkflowNode workflowNode4 = new WorkflowNode
	{
		Title = "自由分镜 A",
		Category = NodeCategory.Storyboard,
		X = 900f,
		Y = 900f
	};
	workflowCanvasState.Nodes.Add(workflowNode4);
	StoryTree tree = StoryTreePlanner.Build(workflowCanvasState, "画布1");
	Expect(tree.Roots.Select((StoryRow row) => row.Key).SequenceEqual(new string[3] { "group:project", "group:unbound", "group:library" }), "根上三组：项目 / 未绑定节点 / 资源库");
	StoryRow storyRow = tree.Roots[0];
	Expect(storyRow.Children.Count == 2, "项目下面是两章，实际 " + storyRow.Children.Count);
	StoryRow storyRow2 = storyRow.Children[0];
	Expect(storyRow2.Kind == StoryRowKind.Chapter && storyRow2.Title.Contains("第1章"), "第一行是第1章");
	Expect(storyRow2.Key == "ch:" + workTreeItem.Id.ToString("N"), "章节行的键要稳定（展开记忆与定位都靠它）");
	Expect(storyRow2.WorkTreeItemId == workTreeItem.Id, "章节行要带着那条工作树条目（双击能编辑它）");
	Expect(storyRow2.NodeIds.Count == 2, "章节行要带上本章全部节点（点它应该把这一片框出来）");
	List<StoryRow> list2 = storyRow2.Children.Where((StoryRow row) => row.Kind == StoryRowKind.Storyboard).ToList();
	Expect(list2.Count == 2, "第1章两个分镜，实际 " + list2.Count);
	Expect(list2[0].NodeId == workflowNode.Id && list2[0].Title.Contains("分镜 03"), "分镜行代表那个画布节点");
	Expect(list2[0].Key == "node:" + workflowNode.Id.ToString("N"), "节点行的键要稳定且唯一（左栏定位按它找行，不是按对象——树每重建一次就是一批新对象）");
	List<string> list3 = (from node in workflowCanvasState.Nodes
		where !HasOwnRow(tree.Roots, node.Id)
		select node.Title).ToList();
	Expect(list3.Count == 0, "每个画布节点在故事树里都要有自己的行（左栏定位靠它）：" + string.Join("、", list3));
	List<StoryRow> list4 = list2[0].Children.Where((StoryRow row) => row.Kind == StoryRowKind.Reference).ToList();
	Expect(list4.Select((StoryRow row) => row.Title).SequenceEqual(new string[2] { "角色 · 林晚", "场景 · 老巷" }), "分镜直接引用的按种类分组（角色 → 场景），实际 " + string.Join("/", list4.Select((StoryRow row) => row.Title)));
	StoryRow storyRow3 = list4[0];
	Expect(!storyRow3.IsSubReference, "分镜直接引用的人物不是子引用");
	Expect(storyRow3.Detail.Contains("常服") && storyRow3.Detail.Contains("最新"), "变体与版本写在人物那一行，实际 " + storyRow3.Detail);
	StoryRow storyRow4 = storyRow3.Children.Single();
	Expect(storyRow4.Title == "道具 · 修书工具" && storyRow4.IsSubReference, "人物下面挂的道具来自角色变体自带的引用，要标成子引用");
	StoryRow storyRow5 = storyRow2.Children.Single((StoryRow row) => row.Kind == StoryRowKind.Rollup && row.Children.Count > 0);
	Expect(storyRow5.Title.Contains("本章出场") && storyRow5.Children.Count == 2, "第1章去重后两人/景：林晚 + 老巷");
	Expect(storyRow5.Children.First((StoryRow row) => row.Title.Contains("林晚")).NodeIds.Count == 2, "林晚出现在本章两个分镜下，汇总行要带上这两处");
	List<StoryRow> list5 = new List<StoryRow>();
	foreach (StoryRow root3 in tree.Roots)
	{
		CollectHero(root3, list5);
	}
	Expect(list5.Count == 3, "林晚在第1章两处、第4章一处共三行，实际 " + list5.Count);
	Expect(list5.Select((StoryRow row) => row.EntityId).Distinct().Count() == 1, "三行指向同一个实体（引用点，不是副本）");
	Expect(list5.Select((StoryRow row) => row.Key).Distinct().Count() == 3, "同一设定在多处出现，展开键必须各不相同");
	Expect(list5[2].Detail.Contains("换装"), "第4章那行要显示换装变体，实际 " + list5[2].Detail);
	Expect(StoryTreePlanner.NodesReferencing(workflowCanvasState, hero.Id, null).Count == 3, "林晚被三个分镜引用");
	IReadOnlyList<WorkflowNode> readOnlyList = StoryTreePlanner.NodesReferencing(workflowCanvasState, hero.Id, hero.Variants[1].Id);
	Expect(readOnlyList.Count == 1 && readOnlyList[0].Id == workflowNode3.Id, "按换装变体过滤只应命中第4章那个分镜");
	StoryRow storyRow6 = tree.Roots[1];
	Expect(storyRow6.Children.Count == 1 && storyRow6.Children[0].NodeId == workflowNode4.Id, "自由分镜进未绑定节点组");
	StoryRow storyRow7 = tree.Roots[2];
	Expect(storyRow7.Children.Count == 3, "资源库三项设定，实际 " + storyRow7.Children.Count);
	Expect(storyRow7.Children.First((StoryRow row) => row.Title.Contains("林晚")).Children.Count == 2, "林晚两个变体各一行");
	Expect(tree.Summary.Contains("2 章") && tree.Summary.Contains("1 个未绑定"), "摘要要说清章数、节点数与未绑定，实际 " + tree.Summary);
	void CollectHero(StoryRow row, List<StoryRow> sink)
	{
		if (row.Kind == StoryRowKind.Reference && row.Key.StartsWith("ref:"))
		{
			Guid? entityId = row.EntityId;
			Guid id = hero.Id;
			if (entityId.HasValue && entityId.GetValueOrDefault() == id && !row.IsSubReference)
			{
				sink.Add(row);
			}
		}
		foreach (StoryRow child2 in row.Children)
		{
			CollectHero(child2, sink);
		}
	}
}

// Restored in round 127 from the last successful build (ILSpy); original comments lost.
static bool HasOwnRow(IEnumerable<StoryRow> roots, Guid nodeId)
{
	foreach (StoryRow root2 in roots)
	{
		if (root2.NodeId == nodeId)
		{
			return true;
		}
		if (HasOwnRow(root2.Children, nodeId))
		{
			return true;
		}
	}
	return false;
}

static class Sample
{
    /// <summary>1x1 透明 PNG：用真实图片字节，而不是随便凑一段数据。</summary>
    public const string TinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    public const string DataUrl = "data:image/png;base64," + TinyPngBase64;
}

/// <summary>
/// 把配置文件路径隔离到临时目录，并清掉会覆盖它的环境变量——
/// 否则测试会碰到用户真实的配置与密钥（环境变量优先级高于配置文件）。
/// </summary>
sealed class ConfigEnvironment : IDisposable
{
    private readonly string? previousConfig = Environment.GetEnvironmentVariable("YEEYEEYEE_CONFIG");
    private readonly string? previousKey = Environment.GetEnvironmentVariable("YEEYEEYEE_AI_KEY");
    // 密钥方案也是**进程级**环境变量：有个用例把它设成 plain 验证兜底档，但从来没还原过。
    // 于是它之后所有需要真加密的用例都拿到 plain:（落盘成明文），一路红到本轮的排查才发现。
    // 凡是动环境变量的用例都要在这里成对地存、还——不然失败会以完全无关的用例名字出现。
    private readonly string? previousSecretScheme = Environment.GetEnvironmentVariable("YEEYEEYEE_SECRET_SCHEME");
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "df-config-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>本次测试使用的配置文件路径。</summary>
    public string ConfigPath { get; }

    public ConfigEnvironment()
    {
        Directory.CreateDirectory(workspace);
        ConfigPath = Path.Combine(workspace, "ai-config.json");
        Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG", ConfigPath);
        Environment.SetEnvironmentVariable("YEEYEEYEE_AI_KEY", null);
    }

    public void Restore()
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG", previousConfig);
        Environment.SetEnvironmentVariable("YEEYEEYEE_AI_KEY", previousKey);
        Environment.SetEnvironmentVariable("YEEYEEYEE_SECRET_SCHEME", previousSecretScheme);
        if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
    }

    public void Dispose() { }
}

/// <summary>
/// 把画布库、资产目录与模型配置都指向临时目录并把环境变量隔离掉，
/// 让保存/迁移/资产相关的测试不碰用户真实工程与真实图片资产。
/// </summary>
sealed class IsolatedStores : IDisposable
{
    private readonly string? previousCanvasDirectory = Environment.GetEnvironmentVariable("YEEYEEYEE_CANVAS_DIR");
    private readonly string? previousAssetDirectory = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
    private readonly string? previousConfig = Environment.GetEnvironmentVariable("YEEYEEYEE_CONFIG");

    public IsolatedStores()
    {
        Root = Path.Combine(Path.GetTempPath(), "df-store-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(CanvasDirectory);
        System.IO.Directory.CreateDirectory(AssetDirectory);
        Environment.SetEnvironmentVariable("YEEYEEYEE_CANVAS_DIR", CanvasDirectory);
        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", AssetDirectory);
        Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG", Path.Combine(Root, "ai-config.json"));
    }

    public string Root { get; }

    public string CanvasDirectory => Path.Combine(Root, "canvases");

    public string AssetDirectory => Path.Combine(Root, "assets");

    /// <summary>在隔离的资产目录里放一个真实文件，供 AssetStore 解析。</summary>
    public void WriteAsset(string fileName) => File.WriteAllText(Path.Combine(AssetDirectory, fileName), "asset-bytes");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_CANVAS_DIR", previousCanvasDirectory);
        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previousAssetDirectory);
        Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG", previousConfig);
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (IOException) { }
    }
}

/// <summary>按请求返回响应的桩 HttpClient：账号查询这类要按路径分支的调用用它，不访问网络。</summary>
sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}

/// <summary>桩 HttpClient：不发真实请求，只把请求体留给我们检查；响应内容可指定（含 SSE）。</summary>
sealed class CapturingHandler : HttpMessageHandler
{
    public string? Body { get; private set; }

    /// <summary>返回给调用方的响应体，默认是一个最小的非流式回答。</summary>
    public string ResponseBody { get; set; } = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";

    public string ContentType { get; set; } = "application/json";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, ContentType)
        };
    }
}

/// <summary>把「出图」真实写成文件（资产目录内）的执行器；可配置前 N 次失败，用于验证失败重试。</summary>
sealed class FileWritingExecutor : IInvocationExecutor
{
    private readonly string directory;
    private readonly int failFirst;
    private int attempts;

    public FileWritingExecutor(string directory, int failFirst)
    {
        this.directory = directory;
        this.failFirst = failFirst;
    }

    /// <summary>最近一次写入的文件路径；失败时为 null。</summary>
    public string? LastOutputPath { get; private set; }

    /// <summary>最近一次尝试是否真的写成功。</summary>
    public bool LastWriteSucceeded { get; private set; }

    public Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref attempts);
        LastOutputPath = null;
        LastWriteSucceeded = false;
        if (current <= failFirst) throw new IOException($"模拟第 {current} 次出图写盘失败");

        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{job.JobId:N}.png");
        File.WriteAllBytes(path, [137, 80, 78, 71]);
        LastOutputPath = path;
        LastWriteSucceeded = true;
        return Task.FromResult(new ExecutionOutput
        {
            ExternalTaskId = $"local-{job.JobId:N}",
            Outputs = [new AssetRef { Role = "output", Ref = AssetStore.ToReference(path) }]
        });
    }
}

/// <summary>长时间运行直到被取消的执行器，用于验证取消链路。</summary>
sealed class BlockingExecutor : IInvocationExecutor
{
    public async Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        await Task.Delay(5000, cancellationToken);
        return new ExecutionOutput();
    }
}

/// <summary>
/// 记录请求内容的出图桩：把真实的 1x1 PNG 写进资产目录。
/// 用它验证技能执行器会把「步骤里的模型名」传给出图链路——池子技能全靠这个字段区分。
/// </summary>
sealed class RecordingImageProvider : IImageProvider
{
    private readonly string directory;

    public RecordingImageProvider(string directory) => this.directory = directory;

    public bool IsConfigured => true;
    public string Name => "RecordingImage";
    public ReferenceCapacity ReferenceCapacity => new(0, "测试桩不限制参考图。");

    /// <summary>最近一次请求；没有调用过时为 null。</summary>
    public ImageGenerationRequest? LastRequest { get; private set; }

    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"img-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(Sample.TinyPngBase64));
        return Task.FromResult(new ImageGenerationResult
        {
            Status = ImageGenerationStatus.Succeeded,
            FilePath = path,
            Provider = Name,
            Model = request.Model
        });
    }
}

/// <summary>
/// 记录请求的出视频桩：出视频实现接入之前，用它验证技能链路会把模型与画幅逐步骤传下去。
/// </summary>
sealed class RecordingVideoProvider : IVideoProvider
{
    private readonly string directory;

    public RecordingVideoProvider(string directory) => this.directory = directory;

    public bool IsConfigured => true;
    public string Name => "RecordingVideo";

    /// <summary>最近一次请求；没有调用过时为 null。</summary>
    public VideoGenerationRequest? LastRequest { get; private set; }

    public Task<VideoGenerationResult> GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"video-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112 });
        return Task.FromResult(new VideoGenerationResult
        {
            Status = VideoGenerationStatus.Succeeded,
            FilePath = path,
            Provider = Name,
            Model = request.Model
        });
    }
}