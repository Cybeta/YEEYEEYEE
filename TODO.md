# YEEYEEYEE 待办清单

这份文件只放**还没做的事**。已完成的内容、每轮的验证结果与判断留在 [PROGRESS.md](PROGRESS.md)，
两边的分工是：PROGRESS 用于追溯「当时做了什么、跑没跑过」，这份用于回答「接下来要做什么」。
已经做掉的那几条不会从这里消失，而是标成「✅ 已完成」并只留一句结论——编号与你当初提的一致，
你要对照「第几条」的时候不会错位。

记录于 2026-10-02。每条都注明「用户原话 → 具体指什么 → 代码里现在是什么样 → 待定的问题」，
其中代码位置是**核对过的**；凡是没核对的都写成「待确认」，不猜。

---

## 1. 抽卡特效

- **要做的事**：出图时给一个「抽卡」的观感——一批图出来时播一段特效。
- **现状**：没有。出图结果现在落在节点上方那排候选窗口里（`AddBatchGhosts` /
  `CreateBatchGhost`），窗口本身有呼吸光晕与「生成中 Ns」的实时读数，但没有「抽卡」这一层仪式感。
  站点池子（`SitePool.Pool1..6` 那种「池6」后缀）是「同一个模型的不同池」，与抽卡的「稀有度」不是一回事。
- **与第 8 条是同一件事的两半**：特效分级要有分级依据，第 8 条定义依据。
- **待定**：特效播在什么时候（每张出好就播 / 一批出完播一次 / 保存那一张时播）？
  播在哪里（候选窗口上 / 画布中央 / 全屏遮罩）？是否需要一个「跳过动画」的开关（连续出图时很吵）？

## 2. 更新提示

- **要做的事**：有新版本时在界面上提示。
- **现状**：**完全没有**——代码里没有任何版本检查（没有「检查更新 / 新版本 / release」这类逻辑）。
- **待定**：版本从哪里取（GitHub Releases / 官网接口 / 自己搭一个 JSON）？
  是「启动时静默查一次、有就提示」还是「设置里有按钮手动查」？下载安装由谁做（打开浏览器 / 应用内下载）？
  工程基准版本目前写在 `PROGRESS.md` 的基线段落里（`0.1.0-dev`），不在代码里——真要做检查得先把版本号落到程序集或一个常量上。

## 3. 引用的节点更新了图之后，下游节点没有提示「资源需要更新」 ✅ 已完成（2026-10-02）

- **做法**：出图时把**当时每条引用的设定指纹**记在产物上（`WorkflowAttachment.SourceFingerprints`），
  判定时现算一遍与记录比，不一样就报。用「记下来再比对」而不是「改设定时通知下游」——
  通知那条路要覆盖所有能改设定的入口，漏一个就永远不报，而静默不报正是这个问题。
- **提示形态**：卡片上引用标签下面**一行字**（「引用的设定已更新：…；这一镜建议重出」），
  点一下在状态栏列出每一条。**不阻挡操作**——判断依据是内容比对，不该拦着人干活。
- **三条取舍**（都有测试）：没有记录的产物不报、出图之后新加的引用不报、锁定版本的不报。
- **老产物**：本次更新之前出的图没有依据，卡片上用一行灰字如实说明；**不补记当前指纹**
  （补记等于宣称它当时用的就是现在这一版，设定真变过就永远不报了）。
- 细节与验证见 PROGRESS.md 第 126 轮。
- **留给以后的**：生成链自检（第 121 轮那套）里也可以顺带列一行「引用已更新、建议重出」，
  本轮先只做了卡片提示。

<details>
<summary>当初记下的待定问题（保留备查，已在实现中拍板）</summary>

- **现状（部分已有，但覆盖不到图）**：
  - **版本**这一层已经有机制：`EntityVariantVersion` + `VersionDecision` + `CanvasReferenceVersions.IsNodeBlocked`，
    并且有「出现新的有效版本后重新需要确认」的行为与测试（`NewEffectiveVersionReopensConfirmation`）。
  - **图片附件**这一层**没有任何提示**：变体上加了 / 换了图（`WorkflowEntityVariant.Attachments`），
    或者下游节点自己引用的那个设定换了图，下游完全无感。
- **现成的口径**：`WorkflowEntityVariant.Fingerprint()` 已经把「描述 + 布局 + 附件引用」拼成一个指纹。
- **待定**：提示的强度（只是卡片上一行字 / 挡住重新出图直到确认）？「更新」的判定是看指纹，
  还是还要看具体是哪一张图变了？

</details>


## 4. 去掉左下角的「制作阶段」 ✅ 已完成（2026-10-02）

- **按你的意思只删左栏那一处，画布上方的芯片留着。**
- 已删：左栏的「制 作 阶 段」标题 + `StageButtonPanel`（`MainWindow.axaml`），
  以及代码里生成那排按钮的那一段（`RefreshStageUi` 现在只建芯片）。
- 保留：画布上方那排芯片 `StageChipPanel`、筛选规则 `ProductionStageRules`、
  画布上的阶段筛选与状态栏那句「只看「分镜」…」。点击处理器 `Stage_OnClick` 两边共用，原样保留。
- 腾出来的位置**全部给了工作树**（`MainWindow.axaml`：树外面那圈 `Border` 不设 Dock 去当填满项，
  `TreeView` 去掉写死的 `Height="240"`）。这一条至此没有遗留问题。
- 细节与验证见 PROGRESS.md 第 124 轮。

## 5. 选中节点后工作树没有跟着跳转 ✅ 已完成（2026-10-02）

- **根因是两个问题叠在一起**（不是缺功能，是已有实现压根没跑到）：
  1. `BuildWorkTreeList()` 每次都会**重造一批全新的行对象**，而原来的代码是「先从旧树里找出目标行 →
     重建 → 拿**旧**对象去 `SelectedItem` 与找容器」——那两个对象不在新树里，于是永远选不中、
     永远滚不过去，只在状态栏留一句「可能需要手动展开」。
  2. 左栏只要不在「工作树」那一页就直接 `return`，静默什么都不做。
- 已改成：先用**数据故事树**（`StoryTreePlanner`）算出该展开哪些行 → 需要时把左栏切回工作树 →
  重建 → 按稳定的 `StoryRow.Key` 找回**新**行 → 选中并 `BringIntoView`（重试 3 拍）。
  树里确实没有这一行时，如实说一句并指出「未绑定节点」那一组（不再静默）。
- 细节与验证见 PROGRESS.md 第 124 轮。

## 6. Agent 面板关掉后，右下角保留一个常驻图标（图标按模型厂家显示） ✅ 已完成（2026-10-02，走的是方案 B）

- **已做**：面板收起来时右下角出现一枚圆环徽标（点一下重新打开），面板开着时它自己消失。
  字母与颜色跟着当前模型的厂家走，厂家用 `ProviderPreset.Match` 按已保存地址反推。
- **图标用的是方案 B**：自绘的**两个字母 + 一个区分色**（`ProviderBadges`，放共享层并由测试钉住），
  **不用别家的 logo**。理由（商标 / 素材来源 / 维护 / 分发范围）见下面的原文，保留备查。
- 颜色是**我们自己挑的区分色**，不是官方色值；要严谨的品牌色与 logo，得走方案 C（官方素材包）。
- 已知取舍：它浮在画布右下角，会挡住那个位置底下的节点。要是不顺手，可选：半透明、悬停才显形、
  挪进底部状态栏。
- 细节与验证见 PROGRESS.md 第 125 轮。

<details>
<summary>当初为什么先问 logo（保留备查）</summary>

**第一件事实**：这个仓库里除了自家 logo（原 `YEEYEEYEE.Desktop/Assets/YeeYeeYee-logo.png`，随旧端在第 128 轮一并删除）
**一张图片资源都没有**——Avalonia 端连自家那个都没在用，标题栏那个「头像」是文字 `LM`。
所以第三方厂家的 logo 一张都没有，「按厂家显示」得先决定图标从哪来。

- **商标与品牌规范**：DeepSeek / OpenAI / Anthropic / Kimi / 通义千问 / 智谱 / 豆包 这些标识都是各家商标。
  通常允许「说明与某服务兼容 / 集成」这种指示性使用，但普遍要求不得改形状与颜色、不得裁切、
  保持规定留白、不得暗示官方合作。各家条款不一样，有的明确要求必须用官方素材包。
- **素材来源与许可不明**：网上抓下来的 png 往往来源、许可都说不清，不适合直接塞进仓库；
  官方素材包一般要先同意条款才能下。
- **维护成本**：厂家换标、新接一家都要再来一轮；各家素材风格尺寸也不统一。
- **分发范围决定风险大小**：只自己用 / 内部用，实际风险很低；一旦对外发布（官网、商店、开源仓库），
  上面两条就变成真问题。

| 方案 | 做法 | 风险 | 工作量 |
|---|---|---|---|
| A 自绘矢量 | 用品牌色 + 简洁几何形 / 首字母自绘 SVG | 无 | 中 |
| **B 文字缩写 + 品牌色（采用）** | 两个字母 + 区分色 | 无 | 很小 |
| C 用官方素材包 | 按各家品牌规范用官方素材，界面注明「各标识归其所有者」 | 中（逐家读条款） | 中 |
| D 让你自己放图 | 设置里选一张本地图片当这个厂家的图标 | 转给使用者 | 小 |

</details>


## 7. 删除旧版桌面端（WinForms） ✅ 已完成（2026-10-02）

**结果**：旧端先移入 `_legacy_winforms/`（带 README，不在任何构建里），第 128 轮确认无人引用后**整个删除**；
共享代码只有一份来源
（`YEEYEEYEE.Desktop.Core` 放模型与画布基础设施，`YEEYEEYEE.Desktop.Shared` 放无界面逻辑）；
两份分叉的核心已按「以带完整链条的那一份为准」合并；三个测试项目改引用 Shared。
解决方案构建 0 错误，Agent 测试 190 项全部通过（197 − 7 个删掉的 WinForms 测试），
Migration `8/8`、G6V1 通过。完整过程（含一次误删与恢复）见 PROGRESS 第 127 轮。

**这次改动顺带修掉的真问题**（都不是我引入的，是两份分叉躺了很久的）：
- `CanvasLibrary.Save` 在 Core 那份里是**自己序列化写盘的旁路**（不迁移、不备份、不校验版本），
  而现在两端都走统一保存链了 —— 也就是说**应用那边以前可能有一个绕开备份的写盘路径**。
- `CanvasLibrary.Rename` / `StorageMaintenance.FindUnreferenced` 等只在旧端那份里，现已并回。
- 两个同名类共用一个命名空间（`StorageMaintenance`）、`AppPaths`/`ProjectContext` 被放在 `.Core`
  命名空间靠转发壳访问、`RecentCanvasState` 重复声明 —— 都清掉了。
- **测试卫生**：有个用例把 `YEEYEEYEE_SECRET_SCHEME` 设成 `plain` 后不还原，会污染它之后所有
  需要真加密的用例（失败信息还伪装成「加密坏了」）。已改成成对存 / 还。

**剩下的小事**（都不影响使用）：
1. 恢复出来的约 55 个测试函数**丢了原有中文注释**（断言与行为完整）。动到它们时把注释补回来；
   恢复段落都有 `// ===== Recovered in round 127 =====` 标注。
2. 被删掉的 7 个 WinForms 测试里，有两处**真正的覆盖损失**，将来想找回就得在共享层重写：
   版本状态的文案（`GetNodeVersionStatusSummary` 的三种说法）与画布控件「拒绝把自身状态当来源加载」。
3. ~~`_legacy_winforms/` 确认无用后可以整个删掉~~ → **第 128 轮已删**（连根目录三个空残留目录一起）。
4. `02_Architecture.md` 里「桌面为 WinForms 自绘主端」那段基线描述需要跟着改（本轮已改，见该文件）。

<details>
<summary>当初的判断与后来的修正（保留备查）</summary>

- 初步判断是「搬 42 个文件 + 删一个项目」；实际查出**两份核心已经分叉**，
  所以真正的工作量在**合并**，不在搬文件。
- 当时还没意识到：这个测试文件**没有提交**（工作区比 HEAD 新很多），这个前提让后来的
  一次误删变得很危险 —— 详见 PROGRESS 第 127 轮第 4 节。

</details>


<details>
<summary>当初的记录：两份核心是怎么分叉的（保留备查）</summary>

**旧端那份不是副本，是另一份分叉，而且是双向的。** `YEEYEEYEE.Desktop.Core` 有自己的一整套模型与
工具类（WorkflowCanvasModels / WorkflowEntities / WorkTree / AppPaths / AssetStore / CanvasLibrary /
ProjectContext / StorageMaintenance），旧端项目里**也有一套**，成员并不相同。例如只有**旧端那份**有
`CanvasLibrary.Rename`、`CanvasLibrary.FindByTitle`、`CanvasLibrary.LoadCurrentCanvasPath` /
`SaveCurrentCanvasPath`、`StorageMaintenance.FindUnreferenced`。也就是说：**测试跑的是旧端那份，
应用跑的是 Core 那份**——测试全绿并不能证明应用里这些地方是对的。

**另有一批 UI-free 逻辑从来没被任何一端共享过**（只在旧端项目里编译，被测试用到）：
`AgentBatchCommitter`(+`AgentCommitStage`)、`CanvasChapterOperations`、`CanvasChapterStructureSession`、
`CanvasCommandService`、`CanvasDuplication`、`CanvasImportMerge`(+`CanvasImportService` / `CanvasImportMode`)、
`CanvasPackage`、`CanvasReferenceExpansion`(+`CanvasReferenceExpansionState` / `CanvasReferences`)、
`CanvasWorkTreeSync`(+`CanvasSyncSession` / `SyncChangeKind` / `SyncDirection` / `CanvasSyncConflictCodes`)。

**有 7 个测试直接依赖 WinForms 类型**（`MainForm`、`ReferenceLayerCanvasForm`、`WorkflowCanvasControl`、
`AgentPaneHost`）——按你的决定直接删掉，没有搬进无界面库。

**归档而不是删除的依据**：旧端 66 个受版本控制的文件里 **63 个带着未提交的改动**
（它们的当前内容并不在 git 里），直接删就是永久丢失，所以先移进 `_legacy_winforms/`。

</details>

<details>
<summary>当初的初步判断（已被第 127 轮的核对修正，保留备查）</summary>

- **现状（为什么不能直接删）**：旧端 `YEEYEEYEE.Desktop` **不是纯界面**——它里面装着共享的无界面代码。
  两边的关系是「Avalonia 端用 `<Compile Include>` 链接旧项目里的文件」：
  - `YEEYEEYEE.Desktop.Avalonia.csproj` 里有 **42 条**链接；
  - 3 个测试项目直接引用旧项目：`YEEYEEYEE.Agent.Tests`、`YEEYEEYEE.Migration.Tests`、`YEEYEEYEE.G6V1.Tests`；
  - `YEEYEEYEE.slnx` 里**已经**没有旧项目了（只有 Core / Desktop.Core / Avalonia / Host / Web）。
  - 补：`YEEYEEYEE.Desktop.Core.csproj` 自己也链接了旧端的 **9 个**文件
    （CanvasChapters / NodeProjection / CanvasMigration / CanvasIdentityValidator / CanvasStorage /
    ProjectLibrary / CanvasReferenceScan / CanvasRecycleBin / UnifiedWorkTree）。

</details>

## 8. AI 识别抽卡的图质量 → 对应特效：出金 / 出红 / 出紫 / 出蓝 / 出白（可能需要动画与视频）

- **要做的事**：一批图出好后，由 AI 判断每一张的「质量档」，按档位播不同的抽卡特效。
  五档：金 > 红 > 紫 > 蓝 > 白。
- **现状**：没有。批次里每格只有「等待 / 生成中 / 出好 / 失败」四种状态（`BatchSlotStatus`），
  没有质量维度；也没有任何质量判定。
- **技术上要补的东西**：
  - **判定链路**：要么让**视觉模型**看图打分（聊天链路已经支持图片块输入，见 `AgentAttachments` 与
    「报文：OpenAI 格式携带图片块」那组测试——能力在，但**要多花一次模型调用**，而且判定本身不确定），
    要么先做**本地可解释的启发式**（分辨率 / 是否被负面词命中 / 与提示词的偏离度…）。
  - **特效本身**：如果要做成动画/视频，需要素材与播放器；这一类素材要么生成（视频链路目前**未接入执行方**），
    要么用代码画（现在画布上的光效都是代码画的：`BoxShadow` + 锥形渐变转圈 + 33ms 时钟）。
- **待定（这几条不定下来做不了）**：
  1. 质量由谁判——调视觉模型（花钱、慢、结果不确定）还是本地指标？两者可以混用（本地先筛、模型再判）。
  2. 五档的判据是什么，谁定阈值？「金」的标准要说清楚，否则每次判定都要重新吵一次。
  3. 特效要做成什么形态（代码画的粒子/光效，还是视频素材）？
  4. **诚实前提**：把「AI 判定的档位」当结果展示给用户时，必须说清这是模型判断而不是客观事实；
     不许把模型的一句话包装成「这张图就是金卡」。

---

# 需要你确认的几件事（按上面的条目）

1. 第 1 条：抽卡特效播的时机与位置（每张出好 / 一批出完 / 保存时；候选窗口上 / 画布中央 / 全屏）？
2. 第 2 条：版本从哪里取、怎么查？
3. 第 8 条：质量档由谁判（视觉模型 / 本地启发式 / 两者混用），阈值谁来定？
4. 第 6 条留下的口味问题：那枚右下角徽标**浮在画布上会挡住底下的节点**，要不要改成半透明 /
   悬停才显形 / 挪进底部状态栏？

（第 3、4、5、6、7 条已经做完，对应的问题已消掉。第 4 条腾出来的位置给了工作树；第 6 条按方案 B 做了字母徽标。）
