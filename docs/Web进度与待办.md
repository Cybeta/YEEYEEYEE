# Web 进度与待办

源码/已发布版本：`0.1.5.5`；发布提交：`aab82f17b9ef0d27194bdc98a0110304c4adb723`；tag：`v0.1.5.5`；核对日期：2026-10-17。发布资产见 [GitHub Release v0.1.5.5](https://github.com/Cybeta/YEEYEEYEE/releases/tag/v0.1.5.5)。这里记录当前源码能力与验证边界，不使用完成百分比。桌面状态见 [Desktop 进度与待办](Desktop进度与待办.md)，发行历史见 [发布说明](发布说明.md)。

## 已实现

| 范围 | 当前能力 | 源码入口 |
| --- | --- | --- |
| 项目工作台 | 列出、创建和打开服务端项目，添加和切换画布；项目路径受父目录和符号链接检查约束 | `WebProjectApi.cs`、`ProjectWorkbench.tsx` |
| 画布协作 | 身份权限、节点编辑租约、修订冲突检查、画布保存、布局、在线名单和 SSE；画布作用域隔离 | `WebCanvasApi.cs`、`EditLeaseApi.cs`、`CanvasEventHub.cs` |
| 聊天 | SQLite 会话持久化，用户与画布隔离，文本/思考/usage 独立流式事件，取消、失败或中断回复重试；附件支持图片（PNG/JPEG/WebP/GIF）、音视频（MP4/WebM/MOV、MP3/WAV/M4A/FLAC/OGG）、PDF 与文本（TXT/Markdown/JSON/CSV），落消息元数据并在聊天里按种类预览或降级为链接 | `WebChatApi.cs`、`WebChatAttachments.cs`、`WebChatStore.cs`、`ChatPanel.tsx` |
| 聊天动作 | readonly / approval / auto；画布动作预览、租约及修订检查后提交、满足修订条件时撤销；支持受限技能调用 | `WebChatApi.cs`、`AgentActions.cs` |
| 聊天技能参数与引用 | 技能声明字符串/数字/布尔/枚举/数组/对象六类参数，前端按声明渲染编辑器、服务端按声明校验（`SKILL_PARAM_UNKNOWN`/`SKILL_PARAM_TYPE`/`SKILL_PARAM_INVALID`/`SKILL_PARAM_REQUIRED`/`SKILL_INPUT_UNSUPPORTED`），参数落到执行层输入；人物/场景/道具/项目节点实体引用按 ID 解析并回填类型、显示名与版本，随消息保存回显 | `WebChatSkillParameters.cs`、`WebChatApi.cs`、`WebSkillJobApi.cs`、`ProjectCanvasSceneStore.cs`、`chatSkillParams.ts`、`ChatPanel.tsx` |
| 资源管理 | 实体、变体、版本编辑/提交/回退，媒体添加/改名/移除；删除前检查跨画布与历史引用 | `WebEntityApi.cs`、`EntityManager.tsx` |
| 生成与成片 | 技能任务、任务状态和结果附件回写；选择至少两个镜头按序无损拼接，列出和下载成片版本 | `WebSkillJobApi.cs`、`WebJobAttachmentService.cs`、`FilmPanel.tsx` |
| 质量判档（可选） | 默认关；整批成功后先做本地坏图筛查，再由读图模型按提示词与负面词打分，档位/裂纹按 Desktop 规则展示；结果按任务与产物下标保存，失败或超时不影响看图与采用 | `WebImageQuality.cs`、`WebImageProbe.cs`、`shell/imageQuality.ts`、`GachaReveal.tsx` |
| 管理设置 | 管理员模型增删改、选用、清密钥和请求测试；媒体配置、技能及工作流管理 | `WebSettingsApi.cs`、`ModelSettings.tsx`、`SkillSettings.tsx` |

“已实现”表示代码路径存在，不表示公网部署或真实模型效果已验收。聊天技能只允许当前白名单，不能把任务已提交写成生成已完成。

## 2026-10-16 源码状态汇总

- Agent 审批预检已统一去重键；失败后同步预检状态与动作错误、刷新消息并允许重试，隔离跨会话异步响应。附件与引用变化支持画布虚影预览。
- 节点图片生成固定绑定提交时的目标节点，支持 1–4 个候选槽位及采用/丢弃；采用后才追加正式附件，按 jobId 与 outputIndex 幂等。丢弃不取消后台任务或删除生成文件。
- 节点菜单视频入口已接通，任务固定绑定原节点，成功后直接回挂；无需再次派发视频入口开发任务。
- 节点媒体、任务时间轴和引用详情已接入，包含实体、变体、锁定历史版本媒体解析及聊天 usage 展示。相关前后端回归用例一并纳入源码。
- 本次新增 Web 隔离来源 ComfyUI 官方前端导出：管理员短期会话固定目标与所选工作流，浏览器加载目标官方前端并复用 Desktop Guard/Inspect/Export，服务端代理只读 HTTP 与 WebSocket。API JSON 仍直接导入；UI 原稿与 API 正文独立落盘，后续调用使用保存的 API JSON 与站点地址。
- 本轮后端构建、转换 HTTP/WebSocket 专项、工作流 HTTP 调用链、共享 ComfyUI 9 项回归、前端 TypeScript 和转换生命周期 10 项测试通过。完整 Web 回归曾在 ChatRegression 并发流状态断言失败，不能宣称完整验收；真实 ComfyUI 与 Docker 阻塞仍保留。
- 聊天技能参数、实体引用与附件类型已按声明补齐：参数支持字符串/数字/布尔/枚举/数组/对象六类，声明同时供服务端校验、模型提示词与前端编辑器使用；实体引用支持人物、场景、道具与项目节点，按 ID 解析并回填类型、显示名（变体名）与锁定版本号（聊天上下文按**项目库**列资源视觉内容，模型才写得出可用的引用）；附件在原有 PNG/JPEG/文本之外增加 WebP/GIF/PDF/MP4/WebM/MOV/MP3/WAV/M4A/FLAC/OGG/Markdown/JSON/CSV，上传校验、下载 MIME、参考图判定与前端可选类型共用一份服务端注册表。参数提交按界面上显示的那一整套**替换**（清空即不再生效），出错的参数有控件时留在可重试状态。
- 断线恢复补齐「重连后补取快照」这个入口：`useServerEvents` 新增 `onReconnected`（首次连上不触发，断过之后每次重连触发一次），`WebCanvasApp.resyncAfterReconnect` 在重连时重新取一次场景——手上干净就复用 `followScene`，有草稿则不覆盖、只按修订差异提示一句。会话失效（守卫接口 401）时 `request()` 广播一次信号，界面回登录页并显示「登录已失效，请重新登录」，几条轮询随之停下，不再空转。

## 2026-10-18 Web 开奖第一阶段（候选出图对齐桌面体验）

**已完成。** Web 节点出图新增「抽卡揭晓动画」浏览器偏好（节点右键菜单里的出图选项，写入 `localStorage`，默认关；关闭时保持原有候选卡与逐张采用）。打开后：候选区只露卡背与任务进度，成功图不提前露出；整批进入成功/失败/取消终态后才出现「开奖」，完成不会自动弹全屏；失败与取消项在候选区和揭晓页如实列出，全部失败时只显示错误与现有操作，不进入成功开奖。

揭晓层用 React + CSS 复刻 Desktop `GachaRevealDialog` 的视觉与时序（蓄势 → 爆发 → 飞卡 → 依次翻面，常数见 `shell/gachaRules.ts`）：黑白卡背、按图片主色相取的卡后光晕、一行最多 3 张/4 张走 2×2、聚光与选中压暗均按桌面实现。提供「跳过动画」「先不开」「用这一张」；跳过立即进入可选态；关闭只收弹层，本批候选保留，可再次开奖。**本阶段没有质量判档**，卡面统一显示「未判档」，不编造分数或档位。

采用继续走现有 `/api/web/jobs/{id}/outputs/{index}/adopt`，未改权限、租约、节点锁定与幂等规则；提交期间禁用重复操作，成功后收起整批候选并 `loadScene` 刷新原节点，失败保留候选与选中并显示服务端原话。批次替换、关闭、项目切换（页面重载）都会作废在途回包；不删除任何未采用文件，也没有新增回收逻辑。

同时修复了一个既有交互问题：画布视口在原生 `pointerdown` 里把候选卡区域的按下当成空白处平移并捕获指针，导致候选卡上的「用这一张」等按钮收不到 `click`；现在 `.df-node, .df-candidate-layer` 内的按下不再进入平移。

**验证。** 前端类型检查与直接受影响用例通过：`tests/candidateBatch.test.ts`、`tests/gachaRules.test.tsx` 共 41 项（整批终态/部分失败/全部失败、牌堆构成、跳过与重开、采用成功/失败/重复点击、批次替换与旧回包隔离、桌面/移动端静态输出）。浏览器用真实构建产物在 1440×900、390×844 与 844×390 下走通「生成本地候选 → 主动开奖 → 跳过 → 选中 → 采用后收起」；但这轮浏览器验收的生成、任务与采用响应来自 **Playwright 路由 mock**，只证明界面与交互，**不是真实模型出图或真实服务端采用**。

**仍未实现/未验证。** Web 裂纹卡不自动重出，是否重新生成由用户决定；批次持久化、未采用文件回收均未做。质量判档已在本文件后面的「Web 质量判档接入」一节单独接入，不属于本阶段结论。本轮真实的 OpenAI 兼容图片 API 验收见下一节，并发已改为服务端串行闸门，但「同一批两张都成功」的真实复验仍受上游当前超时影响；生产 HTTPS/移动真机触摸仍沿用既有部署待办。

**截图**（mock 数据，非真实出图）：候选卡背 [web-4-gacha-candidates.png](screenshots/web-4-gacha-candidates.png)、桌面开奖 [web-5-gacha-desktop.png](screenshots/web-5-gacha-desktop.png)、选中 [web-6-gacha-selected.png](screenshots/web-6-gacha-selected.png)、手机竖屏 [web-7-gacha-mobile.png](screenshots/web-7-gacha-mobile.png)、手机横屏 [web-8-gacha-mobile-landscape.png](screenshots/web-8-gacha-mobile-landscape.png)、采用后收起 [web-9-gacha-adopted.png](screenshots/web-9-gacha-adopted.png)。

**源码入口。** `shell/gachaRules.ts`（终态/牌堆/时序/会话 reducer/浏览器偏好）、`shell/GachaReveal.tsx`（预加载、动画、跳过与关闭）、`shell/CanvasView.tsx`（卡背候选区与开奖入口）、`WebCanvasApp.tsx`（开关、会话、采用结果与旧回包隔离）、`shell/tokens.css`（揭晓与移动端/减少动态效果）。

## 2026-10-18 Web 开奖真实验收（OpenAI 兼容图片 API）

**与上一节 mock 的区别。** 本节使用站点已配置的真实图片服务，任务、输出文件、采用与节点附件都来自真实服务端；没有用 Playwright 路由 mock 替代。上游是 OpenAI 兼容图片接口 `https://video.anyaiapi.top/v1/images/generations`（模型 `gpt-image-2(池6)`，密钥为 `dpapi:` 保护），不是 ComfyUI；`127.0.0.1:8188` 无监听，本轮未使用。ComfyUI 视频链路仍不可用。

**隔离环境。** 独立数据目录 `C:\Users\cy\AppData\Local\Temp\yeeeyee-gacha-accept-20261018`，临时项目「开奖真实验收-20261018032356」（canvas `78BAC44A3674DFB1C295AC38`、节点 `98e74e8f-1f0a-4934-846c-7e6e851e5617`）；服务只监听 `127.0.0.1:5025`，验收结束后已停止。未修改正式项目或正式配置；日志与本文不记录密钥。

**修掉的真实链路问题。** `/api/web/skills/{skill}/invoke` 原先 `await StartAsync(...)`，而该 Task 表示「整次执行完成」，导致候选批次要等图片生成结束才拿到响应、拿不到 jobId。现在登记后立即返回快照（实测 `INVOKE_MS 132`，`state=Queued`），前端能立刻显示卡背与任务进度；生成完成仍由任务状态推进。媒体 HTTP 客户端同时固定 HTTP/1.1，并在失败信息里附「Web显式代理=…」，避免把上游超时误判成代理问题（本轮失败信息均为 `Web显式代理=未配置（使用系统默认）`）。

**真实验证结果。**

- 单任务：真实提交成功，约 31 秒返回 `asset://87e1420e36d545d69d407bee7dd03d52.png`。
- 两槽批次 A：两张任务卡先只显示卡背（`cards=2, images=0, overlay=0`，说明行「已出好 0/2 · 等待全部结束」）；整批结束后出现「开奖」，不自动弹层。该批一张成功（job `ae4b83c8-36d4-42aa-b330-adfb4276865b`，`asset://899d093b2b9b46979e85e110306eec72.png`，1024×1024），另一张在 300 秒处真实超时（job `f1ad3fc4-64c8-424e-88a9-346712b4932c`）；揭晓页如实列出「成功 1 · 失败 1」及失败原因，未把失败画成成功卡。
- 开奖交互：主动开奖、跳过动画、选中、关闭后重新打开均可用；重开仍是同一张图（卡片 `src` 前后一致），没有重新生成。
- 真实采用（被拒）：另一账号持有整树编辑租约时，采用返回 409 `EDIT_CONFLICT`「验收编辑（网页端）正在编辑」；弹层与选中保留，候选卡仍在，错误原话可见。
- 真实采用（成功）：释放租约后采用返回 200，节点从 0 个附件变为 1 个（`6191ef24-54f0-49ac-76dd-7d2a51884e79`，`image/png`，2,277,889 字节）；整批候选收起，节点出现 1 个图片块。刷新并重新打开项目后仍是 1 个附件、图片可读（1024 宽），提示词原文保留，没有重复附件。
- 文件：采用后资产目录 2 个文件，两个任务产物都存在；被采用的是其中 1 个，另一个未被误删。
- 全部失败：把模型改成不存在的 `gpt-image-2-nonexistent-acceptance` 后，两张真实任务均返回 404；候选区显示「已出好 0/2 · 失败 2」「全部失败，没有可开奖的图」，没有「开奖」按钮，也没有开奖弹层。测试后模型配置已还原。
- 直接模式：关闭开奖动画后，真实成功的候选显示缩略图与「用这一张」，说明原流程没有被开奖模式破坏。
- 手机宽度：在 390×844 与 844×390 浏览器视口下截取开奖页；**没有真机触摸证据**，只能说明响应式布局与按钮可见。

**并发问题处理与出图方式。** 已确认上游按密钥串行：并发提交时后一张连接会一直挂到超时。Web 端已在图片 API 分支加进程级串行闸门（`WebInvocationExecutor.ImageApiGate`），候选任务在服务端排队，一张完成后再发下一张；单张超时默认放宽到 10 分钟（`ImageHttpTimeout` 可配），避免「慢但最终成功」被 5 分钟误杀。同时补上右键菜单的「出图方式」下拉：`/api/web/skills` 里所有 `TextToImage` 技能（Web 图片 API、本机 ComfyUI、已安装工作流）都能选，选择记在这台浏览器；候选批次标记 `candidateBatch` 现在也接受工作流技能，工作流不发送比例字段。真实环境已验证 `/api/web/skills` 同时返回「Web 图片 API」与「本机 ComfyUI 文生图」，菜单下拉可见（[web-real-24-image-source-menu.png](screenshots/web-real-24-image-source-menu.png)）。**串行闸门用本地 3 秒 stub 做了确定性验证**：两个任务 2/2 成功、两个不同产物文件都存在，stub 记录显示请求 1 结束到请求 2 开始相隔 137ms（严格串行）；这是 fixture 结果，不是真实出图。随后用真实上游复验时，两个串行请求都在 10 分钟处超时，因此**真实 2/2 仍未拿到**，不能拿 stub 结果冒充真实验收；这属于上游当前不可用，不是并发逻辑问题。**2026-10-18 复测（1.5 秒 stub，`yeeeyee-image-concurrency-20261018`）**：同时提交两个 `web.text-to-image` 任务（`1ffd7614-5e4f-4747-947b-76874d1227d5`、`0913f568-a1d1-4085-932e-3ebeabf0df1e`），两个任务均 `Succeeded` 且产物不同（`259c17c0…png`、`a3af240a…png`）；stub `/stats` 为 `count=2`、`maxActive=1`，请求 1 结束到请求 2 开始相隔 142ms（单次 1517ms / 1505ms），再次确认闸门严格串行。仍是 fixture 结果，不代表真实上游可用。

**仍不完整。** 真实两张成功图同批揭晓、两图与任务/文件一一对应仍未完成（上游本轮最后复验两个串行请求均超时）；直接模式真实采用也未完成。手机仍只有浏览器视口证据，没有真机触摸。上一节 `web-4` 至 `web-9` 是 mock 截图，本节 `web-real-*` 才是真实服务端证据，两者不要混用。

**截图（真实出图/真实服务端）。** 生成中卡背与进度 [web-real-1-progress.png](screenshots/web-real-1-progress.png)、整批结束待开奖 [web-real-2-candidates-ready.png](screenshots/web-real-2-candidates-ready.png)、开奖动画 [web-real-3-reveal-animation.png](screenshots/web-real-3-reveal-animation.png)、桌面揭晓（1 成功 + 1 失败）[web-real-4-reveal-desktop.png](screenshots/web-real-4-reveal-desktop.png)、手机竖屏 [web-real-5-reveal-mobile.png](screenshots/web-real-5-reveal-mobile.png)、直接模式候选 [web-real-6-direct-candidates.png](screenshots/web-real-6-direct-candidates.png)、选中 [web-real-7-selected.png](screenshots/web-real-7-selected.png)、采用被拒 [web-real-8-adopt-rejected.png](screenshots/web-real-8-adopt-rejected.png)、采用后收起 [web-real-9-adopted.png](screenshots/web-real-9-adopted.png)、刷新重读 [web-real-10-refreshed.png](screenshots/web-real-10-refreshed.png)、全部失败 [web-real-23-all-failed.png](screenshots/web-real-23-all-failed.png)、出图方式下拉 [web-real-24-image-source-menu.png](screenshots/web-real-24-image-source-menu.png)。上一节 `web-4` 至 `web-9` 仍是 mock 截图，两者不要混用。

**源码改动。** `YEEYEEYEE.Web/WebSkillJobApi.cs`（invoke 立即返回登记快照；工作流接受 `candidateBatch`）、`YEEYEEYEE.Web/WebInvocationExecutor.cs`（图片 API 串行闸门、单张超时默认 10 分钟且可由 `ImageHttpTimeout` 覆盖、媒体 HTTP 固定 HTTP/1.1、失败信息附代理口径）；前端 `Canvas/src/shell/imageSource.ts`（出图方式偏好与选择）、`WebCanvasApp.tsx`（出图方式下拉、改用用户选中的技能、工作流不发送比例）、`shell/tokens.css`（下拉样式）。开奖与候选区改动见上一节源码入口。

**验证边界。** 后端 `dotnet build YEEYEEYEE.Web/YEEYEEYEE.Web.csproj -c Debug` 通过（仅既有 `WebChatApi.cs` 可空警告）；前端 `tsc --noEmit`、Vite 构建通过，直接受影响 6 个测试文件共 80 项通过（新增 `imageSource` 6 项，另有 `gachaRules`、`candidateBatch`、`nodeMenu`、`uiText`、`webCanvasAssets`）。真实生成、真实采用、刷新持久化与全部失败为本轮实际执行；两图同批的真实 2/2 与直接模式真实采用未完成；串行闸门的 2/2 只用本地 stub 验证，未冒充真实出图。

## 2026-10-18 Web 质量判档接入（可选、默认关）

**范围。** 本次只接判档：出图完成后可选地先做客观坏图筛查、再让读图模型按同一份提示词与负面词打分，并把结果按「任务 + 产物下标」保存与展示。Web 裂纹卡不自动重出，是否重新生成由用户决定；批次持久化、未采用文件回收不在本次范围。

**规则复用。** Web 没有另写一套档位规则：档位枚举与分数带、0–10 解析与容错、负面词命中（裂纹卡）、客观坏图判据都直接用 `Desktop.Shared/Canvas/ImageQuality.cs` 与 `ImageQualityJudge.cs` 里的 `QualityJudgement`、`TechnicalScreening`、`ImageQualityJudge`；Web 侧只补了输入形态（`WebQualityRunner`）与图片测量（`WebImageProbe`）。档位顺序仍是红 > 金 > 紫 > 蓝 > 白，分数带仍为 9+ 红、7–8 金、5–6 紫、3–4 蓝、2 以下白；本地坏图只判白档、不判好看。

**开关与执行。** 节点右键菜单出图选项新增「生成后质量判档」浏览器偏好，默认关，旁边明确写「开启后会额外调用模型，可能增加费用与等待；判档失败不影响看图与采用」。关着时前端不发判档请求，卡面写「未判档」。开启后整批落终态才发起一次（同一段提示词与负面词合成一组），本地先筛「读不出 / 纯色 / 尺寸明显偏小」，剩下的才送模型；没有可读图模型时只报配置问题并允许跳过，不拿默认档冒充。失败、超时、非法返回都只影响判档状态，不改变任务成功、也不阻止查看与采用。

**服务端边界。** 新增 `POST /api/web/image-quality/grade` 与 `POST /api/web/image-quality/skip`，请求体只有任务 ID（1–8 个）。服务端逐条校验登录、`skill.invoke` 权限、任务归属、当前画布、候选批次标记、成功状态、出图能力与产物；产物路径只从任务自己的 `outputs` 解析（`asset://`），前端传不了任意路径或 URL。图片以 data URL 随 OpenAI 兼容的图片块发出，密钥、接口地址与模型原始响应都不回传卡面。`/api/web/skills` 同时报告当前模型是否能读图，菜单在不可用时直接说明。

**保存与返回。** 结果写入独立 SQLite（`QualityDatabasePath`，默认与任务库同目录的 `yeeeyee.quality.db`），主键是 `(job_id, output_index)`，保存状态（`graded` / `skipped` / `failed`）、来源（`technical` / `model`）、档位、分数、理由、负面词命中、规则版本 `desktop-quality-v1` 与模型显示名。任务列表的 `quality` 数组按 `outputIndex` 返回；前端按任务与下标取，不按数组或模型回复顺序。没有记录、跳过、失败与判出的白档在卡面上分别显示，未判档保持中性。

**展示。** 开奖与候选卡显示与 Desktop 同口径：档位小牌写「金 8/10」这类文字，裂纹卡写「裂纹 9/10」并在卡面画出裂纹、在档位牌下补「命中：…」，选中行写出「谁判的、分数、理由、踩中的负面词」；有模型档位时画面上显示「不是客观结论」的说明。判档尚未结束时揭晓页写明「可以先开奖，档位不会补进这一层」；已有结果不会因迟到而重播动画、重开弹层或恢复已收起的批次。关掉开关时即使服务端有历史结果也只显示「未判档」。

**验证（实际执行）。**

- `dotnet build YEEYEEYEE.Web/YEEYEEYEE.Web.csproj -c Debug` 通过（仅既有 `WebChatApi.cs` 可空警告）。
- `dotnet run --project YEEYEEYEE.Web.Tests -- --image-quality-only` 通过：客观坏图/纯色/尺寸、回复乱序映射、无模型、超时、非法返回、同键重复保存、重复请求默认不再扣费（失败/跳过只有明确重试才放行）、归属/当前画布/候选/成败/类型/产物校验、能力判定，以及一次**真实 HTTP 往返**（本地 OpenAI 兼容桩 + 仓库内真实 PNG，确认请求里带 `data:image/png;base64,` 并读回 9 分红档）。本地桩只验证链路与报文，**不是真实视觉模型的评分**。
- `node node_modules/typescript/bin/tsc --noEmit` 通过；`tests/imageQuality.test.ts`、`tests/gachaRules.test.tsx`、`tests/candidateBatch.test.ts`、`tests/nodeMedia.test.ts` 共 120 项通过（开关默认关与关闭不发起、跳过/失败/白档区分、裂纹、未判档中性、按 outputIndex 绑定、跳过之后仍可开奖与采用、开奖会话快照不因迟到结果改写、旧回包隔离）。

**当时的阻塞（已由下一节真实模型验收取代）。** 当时 Web 服务配置里选中的是测试桩 `stub-model`（`SupportsImageInput=false`，图片 API 未配置），因此没有完成真实模型判档；真实 DeepSeek 识图与判档见下一节。端点级 HTTP 鉴权与并发重复点击没有单独跑真机用例，归属与类型拒绝由 `ValidateJob` 回归覆盖。此前同批两张成功图开奖、直接模式真实采用仍保留待验收，不因本次判档完成而勾选通过；裂纹卡自动重出、未采用文件回收仍未做。

**源码入口。** `YEEYEEYEE.Web/WebImageQuality.cs`（规则版本、运行器、结果存储、服务层与资格校验）、`WebImageQualityApi.cs`（两个端点）、`WebImageProbe.cs`（纯托管 PNG 采样与 JPEG/GIF 尺寸）、`WebSkillJobApi.cs`（任务视图带 `quality`、技能目录报告判档能力）、`Program.cs`（依赖注册与路由）；前端 `shell/imageQuality.ts`（偏好、卡面/选中文案、发起判档的纯决策）、`GachaReveal.tsx`、`CanvasView.tsx`、`WebCanvasApp.tsx`、`shell/tokens.css`。

## 2026-10-18 DeepSeek V4 Flash 真实识图与判档验收

**运行中的 Web 与验收配置不是同一份。**

- 当前真正监听 `8080` 的是 Docker 容器 `dream-web-1`（镜像 `yeeeyee-web:local`，镜像构建时间 **2026-10-02**），只挂载 `dream_yeeeyee-data:/data`。容器内既没有 `/app/web-ai-config.json`，也没有 `/data/config`；环境变量里没有 `YEEYEEYEE__WebAiConfigPath`。因此这台正在运行的服务**没有选中 DeepSeek**，而且这个旧镜像还不包含本次新增的 `/api/web/image-quality/*` 路由。本次没有修改生产容器、镜像或正式配置。
- 实际接入 DeepSeek V4 Flash 的配置在隔离验收目录：`C:\Users\cy\AppData\Local\Temp\yeeeyee-gacha-accept-20261018\data\config\web-ai-config.json`（`dream-verify-a`、`dream-web-settings-latest` 下也有同款）。脱敏后：`DisplayName=DeepSeek`、`Model=deepseek-flash`、`Endpoint=https://api.deepseek.com/v1`、`ApiFormat=OpenAiChat`、`UseFullUrl=false`、`SupportsImageInput=true`、密钥已设置且本次在本机可解密。没有新增供应商，也没有修改能力标记。
- 之前选中 `stub-model` 的原因：验收桩配置在仓库构建输出 `YEEYEEYEE.Web/bin/Debug/net10.0/web-ai-config.json`，启动时没有指向 DeepSeek 配置；它与运行中的 Docker 容器也不是同一份配置。运行中的 8080 要真正用上 DeepSeek，需要用当前源码重建镜像/重建容器，并把现有 DeepSeek 配置与同目录密钥文件放进卷内 `/data/config`；这两步未在本次擅自执行。

**真实识图（产品同一条图片请求链）。** 用 `AiProviderSettings.Load(filePath:...)` 读取上述配置，再用 `AgentAttachmentLoader.Load` + `OpenAiCompatibleProvider.CompleteJsonWithImagesAsync`（与判档相同的图片块请求链）发送两张现有图片。提示词只要求描述主体、动作、穿着/物件、空间关系、光线与环境，**不包含答案或文件名提示**。

- 林夏角色参考（JPEG；图片 data URL 195,155 字符）：模型回复识别出年轻女性、正面站立、白色圆领 T 恤、米白针织开衫、深蓝百褶短裙、黑色及膝长袜、棕色系带帆布鞋、米色单肩包与灰色毛绒兔子，纯白背景；与人工核对一致。
- 旧书店场景参考（JPEG；图片 data URL 504,347 字符）：模型回复识别出斑驳墙面、通顶书架与旧书、中央木桌与藤编木椅、两盏金属吊灯、右侧整面多格窗、窗台白杯与书堆、窗外绿树/水面/浅色建筑及玻璃水痕；与人工核对一致。
- 结论：当前端点确实读取了图片数据，不是只收到文件名或文字描述；HTTP 200 和泛泛描述没有被当作识图成功，证据是回复里出现了提示词没有给出的具体细节。

**真实判档（服务端 + 真实 DeepSeek）。** 用本次源码构建启动隔离验收服务（端口 5025，数据目录同上，未改正式配置）；`/api/web/skills` 返回 `quality.available=true`、`message=""`。对同一项目/画布/节点的 4 个已有成功候选调用 `/api/web/image-quality/grade`：

| 任务 | 产物 | 真实结果 |
| --- | --- | --- |
| `beac74dc` | `4236973d…png` | 金档 8/10，`书页有明显印刷文字，与无文字要求略冲突` |
| `b76f8d13` | `7d78ddea…png` | 金档 8/10，`书页文字明显，构图光影正常无崩坏` |
| `0f6584da` | `a9d556df…png` | 金档 8/10，`书页文字可读，背景稍杂但整体达标` |
| `4ddbb439` | `ca42fe01…png` | 红档 9/10，`书页文字模糊，光影构图干净可用` |

- 首次真实调用耗时 **16,912ms**；每条结果都带正确的 `jobId`、`outputIndex=0`、`source=model`、`tier`、`score`、`reason`、`ruleVersion=desktop-quality-v1`、`model=DeepSeek`，不是按前端数组顺序或模型回复顺序落位。
- 同一请求原样重放耗时 **77ms**，`updatedAt` 不变，说明已保存的结果被直接读取，没有再次调用付费模型。
- 浏览器验收：从已有成功候选中装配 2 个候选（`beac74dc` 已有 8 分金档，`e112302d` 未判），开启「抽卡揭晓动画」与「生成后质量判档」；服务端只对 `e112302d` 发起真实模型评分（浏览器计数 1 次 grade 请求），返回金档 7/10、`主体光影达标，书页有文字略违要求`，`beac74dc` 的 `updatedAt` 保持原值。开奖弹层显示「第 1 张：金 8/10」「第 2 张：金 7/10」；选中第 2 张时说明为「第 2 张 · 金档 7/10（模型判定）：主体光影达标，书页有文字略违要求」。
- 采用：点击「用这一张」返回「已采用：图片已追加到原节点」，节点附件从 1 项变为 2 项，新附件 `bb332a80…`（`图片 · e112302d · 1`，`asset://87e1420e…png`），修订从 `135689432735826` 变为 `71163389507080`。
- 刷新与重启：刷新页面并重新打开项目后仍是 2 个附件，质量记录仍在；停止并重启同一验收服务后再次读取，`e112302d` 仍为金档 7/10、`updatedAt` 不变，附件仍为 2 项。说明判档结果与采用附件都落在服务端持久化数据里。
- **候选批次来源说明**：上游图片生成仍会超时，因此没有重新生成。浏览器里用一次本地 `fetch` 替身把两次 invoke 指向已有成功 `jobId`；之后的 `/api/web/jobs`、判档与采用都是真实服务端、真实 DeepSeek。没有绕过任务归属、当前画布、候选标记、产物路径与图片类型校验。
- 跳过/失败：自动化测试覆盖「跳过/失败仍可开奖与采用、不会显示成最低档」；本轮真实浏览器走的是「已有档位 + 新判档」路径，没有单独复跑真实跳过按钮。

**截图（真实 DeepSeek 判档）。** 开奖弹层 [web-quality-real-gacha.png](screenshots/web-quality-real-gacha.png)、选中并显示模型理由 [web-quality-real-selected.png](screenshots/web-quality-real-selected.png)、刷新后节点保留 2 个附件 [web-quality-real-refreshed.png](screenshots/web-quality-real-refreshed.png)。

**验证边界。** 真实识图、真实判档、绑定关系、重复请求不重复扣费、采用与刷新/重启持久化均已实际执行；运行中的 8080 Docker 服务仍是旧镜像且未配置 DeepSeek，属于部署差异，不是本次代码验证结果。Web 裂纹卡不自动重出，是否重新生成由用户决定；未采用文件回收仍未做；此前「同批两张成功图开奖」「直接模式真实采用」仍按原状态保留。

## 2026-10-18 双图开奖与直接模式真实生成验收（上游受阻，未完成）

**环境。** 隔离验收服务仍使用 `C:\Users\cy\AppData\Local\Temp\yeeeyee-gacha-accept-20261018` 数据目录，端口 `5025`，未操作本机 Docker，未改正式项目。图片生成走已配置的 Web 图片 API（`https://video.anyaiapi.top/v1`，模型 `gpt-image-2(池6)`；密钥不记录），判档关闭，避免重复已经完成的判档验收；两次尝试均为页面真实提交，没有 mock invoke、替换 jobId 或注入历史任务。

**独立新项目的尝试与阻塞。** 通过页面创建了独立项目「双图开奖与直接采用验收-20261018」（project `256d3a8b…`，canvas `5161D26B…`），新建节点后从右键菜单真实提交两张；两张都在**提交阶段**被服务端拒绝，未产生付费图片请求：
`GENERATION_ASSETS_INVALID：角色资产需求未知，请明确确认无需求或需要资产；场景资产需求未知…；道具资产需求未知…`。
原因是 Web 新建节点的 `AssetRequirements` 默认为 `Unknown`，当前 Web 界面没有把角色/场景/道具明确设为「无需求」的入口。该问题不是抽卡/判档逻辑，本次没有为它扩展新功能；后续改用已有的隔离验收项目（「开奖真实验收-20261018032356」，节点 `98e74e8f…`，资产需求已明确）执行真实生成。

**① 双图开奖：未达到成功条件。** 在已有隔离项目中关闭质量判档、打开抽卡揭晓动画、张数选 2，从页面提交真实生成：

- 任务 `9199e0dd-c8c4-4797-84dd-c1d97abef2e9`：提交阶段超时，`600,027ms` 后 Failed，错误 `图像接口请求超时`。
- 任务 `87bad2da-9a8e-445a-b6b1-9e76549016d2`：读取响应阶段 Failed，`140,044ms`，`图像接口返回 524（响应正文未记录）`。

两个任务均为 `Failed`、无产物，候选区显示「已出好 0/2 · 失败 2」「全部失败，没有可开奖的图」。因此**没有出现两张真实成功候选，双图开奖与对应关系验收不成立**；没有进行开奖、采用或刷新重读，也没有用历史成功图冒充本批结果。证据：[web-dual-real-failed.png](screenshots/web-dual-real-failed.png)。

**② 直接模式：未达到采用条件。** 关闭「抽卡揭晓动画」、关闭质量判档、张数选 1，从页面提交真实生成：

- 任务 `62d68aed-e315-4f20-b980-ae6ba66df3f5`：读取响应阶段 Failed，`129,468ms`，`图像接口返回 524（响应正文未记录）`。
- 候选区直接显示失败文本「已出好 0/1 · 失败 1」，没有出现开奖弹层；因为没有任何成功候选，无法点击「用这一张」，也无法验证直接模式采用与刷新保留。证据：[web-direct-real-failed.png](screenshots/web-direct-real-failed.png)。

**修复：Web 新建分镜的资产需求默认值。** `ProjectCanvasSceneStore.CreateNode` 现在为 Web 新建的分镜写入「明确无角色 / 无场景 / 无道具」，不再留下 `Unknown`；`AddReference` 在网页端挂上一条引用时会把对应类别自动改成 `Required`，避免「明确无该类资产却存在该类引用」的自相矛盾。这样新项目里的分镜可以直接提交生成，不必依赖 Desktop 的「确认资产需求」对话框。验证：`dotnet build YEEYEEYEE.Web/YEEYEEYEE.Web.csproj -c Debug` 通过；`dotnet run --project YEEYEEYEE.Web.Tests -- --node-defaults-only` 通过（新建分镜默认 None 且通过图片提交审计、挂角色引用后自动 Required、非分镜保持 Unknown）。本轮没有为旧节点补做手动资产需求 UI；已有 `Unknown` 的旧节点仍会按原规则被阻断。

**结论与边界。** 本轮真实任务标识、状态与脱敏错误已记录；上游图片服务当前持续超时/524，且没有循环重试或新增付费请求。该轮双图开奖、直接模式采用均未完成，不能用此前已有候选或上一轮判档验收替代。上面这处新建节点默认值的修复不影响已验证的开奖动画、质量判档与判档模式下真实采用结论。Docker 8080 仍按上一节的部署差异保留，未操作。

## 2026-10-18 修复后复验：直接模式完成，双图仍差一张

**修复后的提交链路已验证。** 用修复后的服务新建分镜 `ad776f07…`（project `256d3a8b…` / canvas `5161D26B…`），页面真实提交不再出现 `GENERATION_ASSETS_INVALID`；首个真实任务 `03242fb7-f435-45c9-8f10-40c4746f06a8` 成功，产物 `asset://6c95b4f2203c42c7a97523a9f287a9a2.png`。说明「Web 新建分镜默认明确无资产」的修复生效。

**① 直接模式：完成。** 关闭抽卡揭晓动画、关闭质量判档、张数 1，从页面真实生成并成功：

- 任务 `03242fb7…` → `asset://6c95b4f2…png`。
- 候选区直接显示缩略图与「用这一张」，没有强制开奖层。
- 点击「用这一张」后节点附件写入 `64c4fdca-cbbf-ba83-f12b-27af696b4b8d`（`图片 · 03242fb7 · 1`），画布修订变为 `201297790923107`。
- 刷新页面并重新打开项目后，节点 `ad776f07…` 显示「已完成」并保留 `图片 · 03242fb7 · 1`，没有重复写入。
- 证据：[直接模式候选](screenshots/web-direct-real-candidates.png)、[直接模式采用后刷新](screenshots/web-direct-real-adopted.png)。

**② 双图开奖：仍差第二张成功图，未完成。** 在同一新项目节点上开启抽卡揭晓动画、关闭质量判档、张数 2，从页面真实提交：

- 任务 `31ce1cf5-6d7d-4547-998b-8f10630f7bc2`：成功，产物 `asset://aacb6b4f686e4d35957cf4f34423fd2a.png`。
- 任务 `17cf12e0-6a7d-4881-b690-1cb9b95db7c9`：`600,015ms` 后提交阶段超时 Failed，无产物。
- 候选区显示「已出好 1/2 · 失败 1」，开奖层如实列出第 2 张失败原因，只有第 1 张成功卡。
- 因此**没有两张真实成功图片，双图开奖、双图与 jobId/outputIndex/文件一一对应验收仍不成立**；没有把这一张成功图冒充双图结果，也没有再用历史候选补位。证据：[双图 1/2 候选](screenshots/web-dual-real-partial.png)、[双图 1/2 开奖](screenshots/web-dual-real-partial-gacha.png)。

**结论。** 直接模式真实生成、采用与刷新保留已完成；双图仍差一张成功图，保持未完成。本轮只做了这一轮修复后复验，没有循环重试；Docker 8080 未操作，验收服务已停止。

## 2026-10-18 双图真实生成 2/2（anyaiapi，接口直调）

**范围。** 用户要求用 anyaiapi 真实生成两张图确认是否成功。复用隔离数据目录 `yeeeyee-gacha-accept-20261018`、项目 `256d3a8b…`、画布 `5161D26B…`、节点 `ad776f07…`；服务只监听 `127.0.0.1:5027`，`/api/web/settings/media` 返回真实图片接口 `https://video.anyaiapi.top/v1`、模型 `gpt-image-2(池6)`。**没有走页面**，两个候选批次请求由 HTTP 接口直接提交（`candidateBatch=true`、同一目标节点、不同幂等键），因此本节只证明真实生成，不代表页面开奖或采用已复验。

**结果（2/2 成功）。**

| 任务 | 状态 | 产物 | 文件 |
| --- | --- | --- | --- |
| `8e7cbbbd-f680-4fcb-ab49-eac08e0ca316` | Succeeded | `asset://c67e942120554f0fbcb393a511c83765.png` | 2,468,680 字节，18:56:49 |
| `7c6d2635-ed5d-4f4d-86e5-c0fda4edd891` | Succeeded | `asset://71e647af2baf4ba1bafed4e2e71a9fd2.png` | 2,259,111 字节，18:57:17 |

- 第二张仍在第一张结束后才落盘（相差 28 秒），与进程级串行闸门一致；两个任务都在 10 分钟内完成，未再出现上一轮的 600 秒超时。
- 两张图人工核对均为真实书店场景：摊开的旧书、白瓷杯、窗外晨光、浅景深、无人物无文字，与提示词一致，且互不相同，不是同一文件复制。
- 失败尝试记录：首次提交到节点 `eaa8579a…` 返回 `GENERATION_ASSETS_INVALID`（该节点建于默认值修复之前，资产需求仍为 Unknown）；本轮未改建旧节点数据，改用已确认过的 `ad776f07…` 节点后提交成功。

**仍未验证。** 页面上的双图开奖、两张候选与 jobId/outputIndex 的界面绑定、选择采用与刷新保留没有在本轮重跑；Docker 8080 与正式配置未动，5027 验收服务在测试后已停止。

## 2026-10-18 双图开奖与直接模式页面复验（上游超时，两项均未完成）

**环境与服务。** 复用隔离数据目录 `C:\Users\cy\AppData\Local\Temp\yeeeyee-gacha-accept-20261018`，用当前源码构建启动独立验收服务，只监听 `127.0.0.1:5025`，验收后已停止（端口已确认关闭）。未修改正式项目与正式配置，未操作本机 Docker。页面读取到的是真实图片接口 `https://video.anyaiapi.top/v1`、模型 `gpt-image-2(池6)`，密钥已配置（本文与证据不记录密钥）；生成技能只有 `web.text-to-image` / 「Web 图片 API」，没有使用测试桩。质量判档在菜单里显式关闭，任务的 `quality` 为空，本轮没有发起判档请求。两张生成均为页面右键菜单真实提交（`candidateBatch` 两张、同一目标节点），没有 mock invoke、替换 jobId 或注入历史任务。证据文件：`C:\Users\cy\AppData\Local\Temp\yeeeyee-gacha-accept-20261018\evidence-dual-direct.json`。

**独立验收项目。** 新建项目「双图开奖与直接采用复验-20261018114636」（project `2b6b8ea1-3a04-4ddb-90a9-8896d970b109`，canvas `F680644D0DCDDDC4F08C8325`），两个新分镜：双图节点 `d2490383-d02b-443e-bcf3-26ea08330b4c`（「双图开奖镜头」）、直接模式节点 `eaa7aeff-4a0d-40c8-a481-e22874e33111`（「直接采用镜头」）。未复用此前验收项目的候选。

**① 双图开奖：未完成（1/2 成功）。** 抽卡揭晓动画开启、张数 2，从页面真实提交一批，共两个任务：

| 任务 | 状态 | 产物 | 结果 |
| --- | --- | --- | --- |
| `e4fce416-dd60-40b9-8ab1-4df8ea74fc9f` | Failed | 无 | 提交阶段 `600,015ms` 后 `图像接口请求超时` |
| `9a1a3581-a902-44dc-8ea9-a2e1c6e0bc45` | Succeeded | `asset://4741724011914b4f8896d5c400da7c35.png` | 2,485,194 字节，`sha256 b524bd73…f1f75` |

- 生成中候选区为两张卡背：`已出好 0/2 / 等待全部结束`，没有提前露图；终态为 `已出好 1/2 · 失败 1`，仍保留「开奖」入口。
- 因只有一张成功，**没有点击开奖、没有选择或采用，也没有刷新保留结论**；双图开奖、两图和 jobId/outputIndex/文件的一一对应验收不成立。没有用上一张历史成功图补位。
- 唯一成功任务做了只读映射核对：`jobId 9a1a3581…` → `outputs/0` → `asset://4741724011914b4f8896d5c400da7c35.png`；产物接口返回 200 `image/png`，2,485,194 字节，与磁盘文件字节数、SHA-256 完全一致。另一张没有产物，未生成文件。
- 证据：[双图生成中：两张卡背](screenshots/web-dual2-01-progress.png)、[双图终态：1/2 成功](screenshots/web-dual2-02-terminal.png)。

**② 直接模式采用：未完成（0/1 成功）。** 关闭「抽卡揭晓动画」、关闭质量判档、张数 1，从页面真实提交一次：

- 任务 `8e45a5b4-6f85-49a1-b0ed-a4e6351004ff`：提交阶段 `600,025ms` 后 Failed，`图像接口请求超时`，无产物。
- 候选区直接显示 `已出好 0/1 · 失败 1`，没有开奖层；因为没有成功候选，没有缩略图、没有「用这一张」，无法验证采用、节点附件更新与刷新保留。证据：[直接模式：0/1 失败](screenshots/web-direct2-01-ready.png)。
- 本轮**没有**采用或刷新截图可附；上一节 2026-10-18「修复后复验」中的直接模式真实采用结果仍按原记录保留，不由本轮替代。

**节点最终状态。** 两个节点在服务停止前均为 0 个附件；没有产生采用写入，也没有重复写入。

**边界与处理。** 上游本轮连续在 600 秒提交阶段超时，本轮共发出 3 次付费生成请求（双图 2 次、直接模式 1 次），失败后没有循环重试或继续追加请求。两项验收均保持未完成，不以历史候选、接口直调结果或 mock 结果替代。本轮没有修改产品代码；验收脚本只是按可访问名称定位「抽卡揭晓动画」和「生成后质量判档」两个复选框（菜单里同时存在两个复选框），这属于验收工具调整，不是产品修复，因此没有额外运行构建或测试。

## 2026-10-18 Web 三张并发生成复测（0/3，上游超时）

**范围与环境。** 在同一隔离数据目录与同一验收服务（`127.0.0.1:5025`，当前源码构建）上，从页面右键菜单一次性提交 3 张真实生成；服务只保留这一份真实图片配置（`https://video.anyaiapi.top/v1`、`gpt-image-2(池6)`、密钥已配置），质量判档与开奖动画均关闭。目标节点 `a924a530-1bc8-496c-9af1-511c9e596643`（canvas `F680644D0DCDDDC4F08C8325`）。提交前确认该画布没有仍在执行的出图任务，避免重复提交；本轮只提交这一批，失败后没有重试。验收结束后服务已停止，端口已确认关闭。证据：`C:\Users\cy\AppData\Local\Temp\yeeeyee-gacha-accept-20261018\evidence-concurrent3.json`。

**提交与界面。**
- 页面在 168 毫秒内发出 3 个 `POST /api/web/skills/web.text-to-image/invoke`（23:21:32.276、.386、.444），候选区立即摆出 3 张卡：`已出好 0/3`。
- 三个任务在界面上同时显示 `Running`；运行期间没有出现缩略图，也没有采用入口。

**任务时间线（按实际失败时刻排序）。**

| 任务 | 提交后终态 | 失败耗时 | 产物 |
| --- | --- | --- | --- |
| `2a7be609-1e9b-4b58-ada8-1c31b16db549` | 23:31:33 Failed | 600,031ms | 无 |
| `ff9bdb31-7879-4e2d-929f-aadd7173828b` | 23:41:34 Failed | 600,018ms | 无 |
| `6db9e920-8cbe-4d02-ab5c-2099566ce45c` | 23:51:33 Failed | 600,018ms | 无 |

三个任务的错误一致：提交阶段读取不到响应，`600,0xx ms` 后 `图像接口请求超时`，均为 `https://video.anyaiapi.top/v1/images/generations` 的上游超时，无响应正文。

**并发结论。**
- **前端提交是并发的**：三次 invoke 在 168 毫秒内发出，三个任务都已被服务端接受并独立登记。
- **上游请求仍是串行的**：三次失败时刻相隔约 600 秒（23:31:33 → 23:41:34 → 23:51:33），与 `WebInvocationExecutor.ImageApiGate` 的单请求闸门一致；排队中的任务在界面上保持 `Running`，所以「三个 Running」不能当成三个上游请求同时在跑。
- 三次请求串行耗尽各自的 10 分钟超时，总耗时约 30 分钟，**0 张成功、0 个产物文件**；终态候选区为 `已出好 0/3 · 失败 3`，没有缩略图、没有「用这一张」。证据：[三张卡背与 0/3](screenshots/web-concurrent3-01-progress.png)、[三张全部失败](screenshots/web-concurrent3-02-terminal.png)。

**边界。** 本轮只验证到「页面并发提交 + 服务端串行闸门 + 上游超时」这一层，没有产生成功图片，因此不能评价三张成功时的出图质量、候选映射或采用；也没有修改产品代码。上游当前持续超时，未循环重试，未增加额外付费请求。

## 2026-10-18 真实 ComfyUI 端到端验收

**与 fixture 的区别。** 本节全部使用真实 ComfyUI 0.37.0（官方前端 1.52.7）、真实节点定义与真实 GPU 生成；没有用 mock 生成结果代替。此前 `--conversion-only` 只验证到隔离路由与模拟导出，不能等同于本节结论。

**环境。** 本轮提供的真实 ComfyUI 地址为临时云隧道，不写入公开目录（证据只留在本机临时目录）；版本 0.37.0 / 前端 1.52.7 / templates 0.11.66；RTX 4080 SUPER；5986 种节点、114 个 checkpoint；选用 `DreamShaper_8_pruned.safetensors`。节点包包含 `comfyui-minimax-h3-audio-T8`、`RES4LYF`、`ComfyUI-KJNodes`、`comfyui-vrgamedevgirl`、`was-ns`、`ComfyUI-Easy-Use` 等。ComfyUI 未通过 API 暴露服务端绝对输出目录，实际产物逻辑位置为 `output` 类型下的 `yeeeyee-acceptance/20261018a/`。

**隔离。** 本地 Web 仅监听 `127.0.0.1:5000`（Development），临时项目与临时工作流均在独立目录；主站 `http://127.0.0.1:5000`、隔离来源 `http://localhost:5000`，worker 为 `w0-<session>.localhost:5000`。未修改正式项目、已有生产工作流或正式配置。

**成功链路。** 临时 UI 工作流 `YEEYEEYEE-acceptance-20261018a/Y01-temp-text2image.json` 含 7 个实际节点（CheckpointLoaderSimple、CLIPTextEncode ×2、EmptyLatentImage、KSampler、VAEDecode、SaveImage）与 9 条连线。隔离来源页面加载官方前端并成功导出 API，结果 `converted=1, failed=0`，note 为 `官方前端 graphToPrompt.output`；保存的 API JSON 为 1133 字节，节点、连线与 `ckpt_name/width/height/seed/steps/cfg/sampler/scheduler/denoise` 与 UI 稿一致，不是 UI graph JSON。真实提交得到 prompt_id `74bf53f0-b2e1-4fed-8dd2-69e43e0153c6`，history `status_str=success`、`completed=true`，输出 `acceptance_00001_.png`（252470 字节、512×512、PNG，SHA-256 `bf0f44db...0d2f`）；Web 任务 Succeeded 并从 ComfyUI 下载到字节一致的产物，浏览器可正常打开。重启 Web 后已安装工作流、技能 ID、任务与产物仍在，API JSON 与站点配置哈希前后一致；刷新设置页后“已安装工作流”正常列出。

**失败与安全。** 真实注入结果：模型不存在时 Failed + ComfyUI `value_not_in_list`；缺失节点接入输出链时 Failed + `missing_node_type`（node 7）；VAE 通道不匹配时 Failed + 节点 6 执行错误；长时间任务可 `Cancelling` → `Cancelled`，无产物残留；移走产物后输出接口 404 `OUTPUT_NOT_FOUND`。错误 ComfyUI 地址返回 502 `WORKFLOW_READ_FAILED`；目标连接被拒时转换页明确显示连接错误；非 localhost 的 HTTP 来源因无 `crypto.subtle` 明确拒绝导出（生产必须 HTTPS，本地 `*.localhost` 属安全上下文）。隔离 worker 仅允许 GET/HEAD/OPTIONS（POST `/prompt` 405、GET `/prompt` 403），WebSocket 错误或缺少 Origin 均为 403；`/__conversion/result` 错误 Origin 404、无效 body 400；伪造会话 404。临时目录与浏览器存储扫描未发现口令、初始化令牌或 Authorization/Bearer。

**发现与修复。** 未配置项目/技能目录时 `GET /api/web/settings/skills` 原本返回 500「尚未打开项目」，导致设置页在渲染“已安装工作流”之前中断。现已改为返回 200 与明确错误项（`技能目录不可用：…`），工作流清单与已安装列表仍可显示；修复后用无 `SKILL_DIR` 的隔离实例复验，设置页显示该提示且已安装工作流可见。此为直接相关的最小修复；Web 项目构建 0 错误，完整 Web 回归退出码 0。

**仍未覆盖。** 生产 HTTPS 通配域名/TLS 终止与公网 DNS 未验证；没有独立的生成总超时（轮询失败会一直重试，本次只验证了取消）；API 导入对“缺少节点且没有提示词槽位”的临时稿仍报 `converted=true` 但不会出现在可执行技能列表，属于导入报告与可执行性口径的已知边界；不宣称全节点/全模型兼容。证据保存在本机临时目录 `C:\Users\cy\AppData\Local\Temp\yeeeyee-comfy-accept-20261018\evidence`（未纳入仓库）。

## 2026-10-17 持久化数据、版本目录与升级/回滚

### 全部持久化路径

新布局把程序与数据彻底分开。`data\` 是唯一的数据根，升级只替换 `versions\<版本>`：

| 数据 | 位置 | 说明 |
| --- | --- | --- |
| 账号、口令哈希、登录会话 | `data\yeeeyee.users.db`（含 `-wal`/`-shm`） | 认证数据；会话 token 以哈希存库。 |
| 首次初始化令牌 | `data\.setup-token` | 仅在还没有管理员、且未配置部署令牌时短暂存在；建出管理员后删除。 |
| 生成任务 | `data\yeeeyee.jobs.db` | 任务与输入/输出快照。 |
| 聊天历史与聊天附件 | `data\yeeeyee.chat.db` | 消息、思考、usage、附件元数据与附件 BLOB。 |
| 生成媒体与资产 | `data\assets` | 服务端落盘的图片/视频等。 |
| 模型/媒体配置 | `data\config\web-ai-config.json` | 不含明文密钥。 |
| 加密密钥文件 | `data\config\ai-key.bin` | 仅使用本机 AES-GCM 兜底时存在；Windows 默认 DPAPI 时没有该文件，但配置里的密文同样依赖同一服务账号。 |
| ComfyUI 站点与工作流 | `data\config\sites` | 站点目录、导入的工作流与取舍。 |
| Web 项目 | `data\projects` | 项目、画布、资源库与项目内媒体。 |
| 独立场景（如启用） | `data\scene.json` | 仅 `AllowStandaloneWebScene` 时使用。 |
| 运行状态 | `data\run\web.pid`、`data\logs` | 进程号与日志；不是业务数据。 |
| 程序版本 | `current.txt`、`versions\<版本>` | 可并存多个版本，升级不覆盖旧版本目录。 |
| 备份 | `backups\data-<时间>`、`backups\data-before-rollback-<时间>` | 升级前数据备份（复制及校验成功后写 `backup-complete.json`）与数据恢复前的原目录留档（原样移动，无完成记录）。 |

旧布局把上述 `yeeeyee.*.db`、`assets`、`web-ai-config.json`、`config`、`Projects` 直接写在程序目录。新包不再使用这种布局；迁移由 `install-web.ps1` 完成。`YEEYEEYEE__WebToken`、`YEEYEEYEE__SetupToken` 属于部署方提供的环境变量，不是包内文件，脚本不会读取或打印它们的值。

### 新布局与脚本

- `start-web.ps1`：固定数据目录的启动器。显式设置 `CanvasDistPath`、`WebProjectsDirectory`、三个数据库路径、`AssetDirectory`、`WebAiConfigPath`、`WebScenePath` 与 `YEEYEEYEE_CONFIG_HOME`；读取 `current.txt` 选择版本，写 `data\run\web.pid`。相对路径不再落进版本目录。
- `install-web.ps1`：首次安装/旧布局迁移。把程序复制到 `versions\<版本>`，把旧目录数据**复制并逐文件校验**到 `data\`，保留旧目录作为恢复副本，不删除原件。
- `upgrade-web.ps1`：校验 ZIP 与可选 SHA-256、版本、后端 DLL、`canvas-dist` 完整性；拒绝路径穿越；停止**仅本安装目录**的 Web 进程；确认退出后备份数据；切换 `current.txt`；启动并轮询 `/health`。备份先写 `backup-state.json`（进行中），复制后逐文件比对大小与 SHA-256，全部成功才写 `backup-complete.json`（来源版本、`dataFormat`、完成时间、文件清单/大小/哈希，不含凭据）。备份或安装新程序目录失败时，会重新启动旧版本并保留原安装，不把站点留在停机状态。
- `rollback-web.ps1`：程序回滚与数据恢复分开。程序回滚默认保留当前数据；数据恢复必须显式 `-RestoreData` 与 `-ConfirmDataLoss`，并在**停止服务和移动数据之前**校验完成记录、文件清单、大小与哈希；校验不通过直接拒绝，当前服务与数据不受影响。通过后先把当前数据移到 `backups\data-before-rollback-<时间>`，再按清单逐文件复制并再次校验。
- `web-common.ps1`：共享的哈希、进程识别、备份、健康检查与日志过滤。所有脚本只含 ASCII，兼容 Windows PowerShell 5。
- `tools/publish-web.ps1`：构建 Web ZIP、写入 `release-manifest.json`（版本、`dataFormat`、后端与首页哈希）、检查排除项并输出 SHA-256 与 sidecar 文件。

### 升级期间的任务、上传与写入

升级会停止服务，脚本在停止前明确提示：

- 正在生成的聊天回复/生成任务会被中断，恢复后需要重试；不会伪装成已完成。
- 正在上传的附件/媒体会失败，需要重新上传。
- 未保存的草稿应先在页面保存；升级脚本不会替用户保存。
- 已打开的页面在服务恢复后自动重连并补取快照；有未保存草稿时页面保留草稿、不覆盖本地编辑，必要时用户应自行刷新确认。

### 回滚边界

- **程序回滚 ≠ 数据恢复。** 只切换 `current.txt` 时，新版本写过的数据仍在，旧程序必须能读它。
- 自动程序回滚只在旧、新 `release-manifest.json` 的 `dataFormat` 都存在且相等时进行。任一缺失或不等时，脚本停止在新版本并打印手动步骤，不把旧程序直接指向可能已迁移的数据。
- 数据恢复会把服务数据回退到备份时刻；备份之后的聊天、任务、上传与项目写入不再被服务读取。当前数据会先移到 `data-before-rollback-<时间>` 留档，但不会被自动合并。
- **只有明确完成的备份才能恢复。** 恢复前要求目录非空、存在 `backup-complete.json`、记录中的文件清单与当前文件一一对应且大小/SHA-256 全部一致；还会拒绝仍带 `backup-state.json` 的进行中/中断目录。历史无完成记录的备份（包括旧的 `data-<时间>` 和 `data-before-rollback-<时间>` 原样留档）不默认视为完整，`-RestoreData` 会明确拒绝。校验在停止服务、移动或替换数据之前完成，拒绝时服务与数据保持不变。
- 存在数据格式差异时，程序回滚必须显式 `-ForceIncompatibleData`；数据恢复必须显式 `-ConfirmDataLoss`。这两个开关都不是默认行为。

### 隔离验证结果

验证使用隔离目录、独立端口和临时账号库/项目/聊天库/素材，未触碰真实服务、真实项目或真实配置。

1. **旧布局迁移（`0.1.5.4` → 新布局 `0.1.5.5`）通过。** 旧目录中的账号可登录；原项目可读；迁移后可新建项目；聊天会话保留用户与助手两条消息；附件下载字节一致；`web-ai-config.json` 迁移前后 SHA-256 相同；保存的模型密钥可解密并通过本地 fixture 的模型测试；`config` 为空时（Windows DPAPI 路径）不产生伪造的 `ai-key.bin`。
2. **升级（`0.1.5.5` → `0.1.5.6`，验证用包）通过。** 停止后备份 13 个文件/230,508 字节；升级后健康检查通过；登录、项目、两条聊天消息、附件和加密配置均保留。
3. **同数据格式启动失败自动回滚通过。** 构造缺少 `ComfyUI:BaseUrl` 的 `0.1.5.7` 包，启动失败后脚本自动恢复 `0.1.5.6` 并健康；失败目录保留，数据未被备份覆盖。
4. **不同数据格式拒绝自动回滚通过。** 构造 `dataFormat=2` 的 `0.1.5.8` 包，启动失败后脚本拒绝自动回滚，`current.txt` 停在 `0.1.5.8` 且服务保持停止；不带开关的程序回滚被拒绝并说明数据边界；带 `-ForceIncompatibleData` 可恢复到 `0.1.5.6` 并健康。
5. **数据恢复通过。** 用升级前备份恢复后，升级前项目与聊天历史恢复，备份之后新建的项目不再被服务读取；恢复前的数据目录完整保留在 `data-before-rollback-*`。
6. **已打开页面的重连行为通过（真实浏览器）。** 页面在服务停止时显示「推送连接断开，正在重连」与在线人数 0；服务恢复后约 20 秒自动回到在线状态，编辑锁与在线名单恢复；未保存的节点名称草稿全程保留，没有冲突弹窗、没有自动覆盖，也没有静默丢失。页面没有主动要求刷新；由于当时草稿未保存，页面没有用服务端快照覆盖它，画布修订保持不变。全程未手动刷新页面。
7. **失败日志过滤通过。** 用包含 `password`、`Authorization`、`API key`、中文“初始化令牌/密码/密钥”的样本日志验证：这些行不进入升级输出，普通日志行仍保留；日志按 UTF-8 读取，避免 Windows PowerShell 5 把中文按 ANSI 读成乱码后漏过滤。
8. **停止旧服务后备份失败，旧版自动恢复（故障注入）通过。**
   - 环境：临时根目录 `C:\Users\cy\AppData\Local\Temp\yeeeyee-web-fault-20261017132909`；独立端口 `18811`；本地模型 fixture `18812`；账号库、项目、聊天库、素材和配置都在临时目录。验证脚本为 `artifacts\verify\upgrade-fault-injection.ps1`（依赖本机 Node 与 `artifacts` 下的 0.1.5.5/0.1.5.6 包；再次运行会新建时间戳目录，使用同一组隔离端口）。
   - 故障构造：在 `data\assets\fault-injection\hold.bin` 上用 `FileShare.None` 持有句柄，不修改 ACL、不写满磁盘；升级停止服务后，文件级备份读取该文件失败。
   - 结果：退出码 1；输出顺序为 `web service stopped` → `backup failed, upgrade aborted: ... hold.bin ... being used by another process` → `previous version is healthy again: 0.1.5.5`；`current.txt` 保持 `0.1.5.5`；`versions\0.1.5.6` 未出现；本安装仅有 1 个 `dotnet` 服务进程；旧版 `/health` 正常。
   - 数据（2026-10-17 用带完成记录的脚本复跑，根目录 `C:\Users\cy\AppData\Local\Temp\yeeeyee-web-fault-20261017140354`，端口与 fixture 同上）：登录、项目列表、聊天两条消息、附件字节（`fault-injection-attachment`）、模型密钥解密与本地 fixture 测试全部通过；业务文件哈希未变化；失败残留目录仅含 1 个文件/157 字节的 `backup-state.json`（进行中标记），没有 `backup-complete.json`，因此不会被 `-RestoreData` 接受。手工删除该目录与 `staging\<版本>-<时间>` 后，旧版本、`data` 和服务健康均保持。
9. **备份成功后安装新版本目录失败，旧版自动恢复（故障注入）通过。**
   - 故障构造：预建 `versions\0.1.5.6` 并在其中持有 `hold.bin` 的共享读句柄，使脚本把已存在的目标目录移开时 `Move-Item` 失败；未修改文件权限。
   - 结果：备份先完成（复跑为 10 个文件/262,032 字节；排除日志、`run`、WAL/SHM 后 8 个文件/47,961 字节，数据侧 7 个文件/47,954 字节）；随后退出码 1，输出 `could not install the new program directory: ... being used by another process` 并自动恢复 `0.1.5.5`；`current.txt` 保持原版本，旧版 `/health` 正常，仅有 1 个服务进程；登录、项目、聊天、附件和加密配置全部可用；业务文件哈希未变化。
   - 清理：PowerShell 5 在目录移动被拒后会留下空的 `versions\0.1.5.6.replaced-<时间>`；删除该空目录、注入目录和 staging 后，旧版本目录、`data` 与完整备份均保留，服务仍健康。
10. **备份完成记录与恢复前校验（隔离验证）通过。**
   - 环境：临时根目录 `C:\Users\cy\AppData\Local\Temp\yeeeyee-web-backup-20261017140820`；独立端口 `18821`；账号库、项目、聊天库、附件、素材与配置均在临时目录；验证脚本为 `artifacts\verify\backup-completion-verification.ps1`。未触碰真实服务或真实数据。
   - 完整备份：安装 `0.1.5.5`、建项目并上传附件后升级到 `0.1.5.6`；升级输出 `backup complete: 10 files, 190557 bytes`；备份目录有 `backup-complete.json`、无 `backup-state.json`。记录含 `sourceVersion=0.1.5.5`、`dataFormat=1`、完成时间、10 个文件的路径/大小/SHA-256，未出现 token/password/secret/authorization/API key 等凭据字段。
   - 拒绝路径（每次均在服务运行时执行，全部退出码 1 且输出原因）：空目录 → `backup directory is empty`；仅有普通文件、无完成记录 → `no completion record`；仅有 `backup-state.json` 的残留 → `backup did not finish`；同时带有旧完成记录但存在进行中标记 → 同样 `backup did not finish`；删除记录中的一个项目文件 → `backup file is missing`；同长度改写该文件 → `backup file hash differs`；完整备份但不带 `-ConfirmDataLoss` → `without -ConfirmDataLoss`。
   - 拒绝后的状态：7 次均未出现 `web service stopped`，`current.txt` 保持 `0.1.5.6` 且字节未变，`/health` 正常，登录/项目/附件字节/业务文件可用，业务文件哈希未变化，未产生 `data-before-rollback-*`，本安装始终只有 1 个服务进程。
   - 完整恢复：通过校验后输出 `backup verified: 10 files, 190557 bytes`，停止服务并把当前数据移到 `data-before-rollback-*`，按完成记录逐文件复制并再次校验；恢复到 `0.1.5.5` 后 `/health` 正常，登录/项目/附件/业务文件可用，记录内所有文件都以记录中的哈希恢复，`backup-complete.json`/`backup-state.json` 未被复制进 `data`。

### 已发布 0.1.5.5

- 发布：[GitHub Release v0.1.5.5](https://github.com/Cybeta/YEEYEEYEE/releases/tag/v0.1.5.5)；发布提交 `aab82f17b9ef0d27194bdc98a0110304c4adb723`，tag `v0.1.5.5`。
- Web 包：`yeeeyee-web-0.1.5.5-win-x64.zip`，2,358,774 字节（2.25 MB），SHA-256 `92B78C53A3C916E2A9348DFE3508073823E08A93263DC55DE23FA7296B6D8AC7`；远端资产 digest 与本地一致。
- 版本来源已对齐：桌面 csproj `<Version>` 为 `0.1.5.5`，`publish-web.ps1` 未传 `-Version` 时读取该值；包内 `release-manifest.json` 的 `version` 为 `0.1.5.5`、`dataFormat` 为 `1`，后端程序集版本为 `0.1.5.5`。
- 包检查：27 个文件；含后端、`canvas-dist`、安装/升级/回滚/启动脚本、`README-WEB.txt` 与 `release-manifest.json`；清单中后端与首页哈希一致；`web-common.ps1` 含完成记录与恢复校验，`upgrade-web.ps1` 写完成记录，`rollback-web.ps1` 恢复前校验；不含桌面主程序、`Projects`、`data`、数据库、`web-ai-config.json`、`ai-key.bin`、PDB、FFmpeg、验收材料或 `0.1.5.6`～`0.1.5.9` 验证包；凭据模式扫描 0 命中。
- 最小启动烟测：从最终包隔离安装到临时目录，`current.txt` 为 `0.1.5.5`，`/health` 返回 ok，首启初始化令牌生成；随后只停止该隔离实例。未触碰正式服务。
- 复用历史验证：旧布局迁移、`0.1.5.5 → 0.1.5.6` 升级、同/不同数据格式失败回滚、数据恢复、备份完成记录与恢复前校验（见上文第 1～10 项）继续有效；本轮版本号变更未改动这些脚本逻辑。
- 本批集中检查：Release 解决方案构建 0 错误、2 条既有 nullable 警告；Web 完整回归首次在并发聊天 `commit` 返回 409 `CHAT_BUSY`，定位与修复见下节；修复后 `--chat-only` 1/1、完整 runner 3/3 通过；前端 29 文件 346 项、TypeScript 与 Vite 构建通过。
- `0.1.5.6`、`0.1.5.7`、`0.1.5.8`、`0.1.5.9` 仅用于验证升级/回滚，**不是发行候选**，不上传、不作为正式版本。`0.1.5.6` 的 SHA-256 为 `42CBB1FACE56F119BAE531720810EEA94C56400331DD0644958DEA61F2C239C6`，保留作验证记录。

### Docker Hub 镜像发布（工作流已配置，尚未推送）

- 新增 `.github/workflows/docker-publish.yml`：在 Release published 或手动触发时，从指定 tag 构建 `linux/amd64` 镜像并推送到 `<DOCKERHUB_USERNAME>/yeeeyee-web`（可用仓库变量 `DOCKERHUB_REPOSITORY` 覆盖完整镜像名）；标签包含版本、次版本、主版本、`sha-<提交>`，Release 时更新 `latest`。
- 必需仓库 Secrets：`DOCKERHUB_USERNAME`、`DOCKERHUB_TOKEN`（Docker Hub 访问令牌，需写权限）。本节撰写时本机 Docker daemon 未运行；它后来已启动并完成容器运行验收（见下文），但仓库仍没有 Docker Hub 凭据，因此**未执行推送**，不能声称 Docker Hub 上已有该镜像；工作流在缺少 Secrets 时会明确失败。
- 容器镜像构建本身已在 `aab82f17` 的 CI 中通过。直接 `docker run` 时需要设置 `YEEYEEYEE__WebProjectsDirectory=/data/projects`、`YEEYEEYEE__AllowStandaloneWebScene=true`、`YEEYEEYEE__WebScenePath=/data/scene.json`；README 已给出命令与 Docker Hub / compose 两种用法。
- 容器数据在 `/data` 卷（账号、任务、聊天、素材、项目、配置与密钥）。ZIP 布局的 `install/upgrade/rollback` 与完整备份校验不适用于容器；容器升级与备份以镜像标签和 `/data` 卷为单位。

### 2026-10-17 Docker 容器运行与持久化验收（通过）

- 环境：Windows 主机上的 Docker Desktop 4.93.0，Linux engine 29.8.1（linux/amd64），WSL2 `docker-desktop`；镜像从当前源码（提交 `2cdbed1`）本地构建。隔离 Compose 项目 `yeeeyee-accept`、测试卷 `yeeeyee-accept_accept-data`、HTTP 端口 `18080`、本地 HTTPS 反向代理端口 `18443`。正式容器 `dream-web-1`（8080）与正式卷未操作。
- 实际通过项：
  1. 健康检查 `/health` 返回 200；首页在真实浏览器中渲染并完成登录；带 `?canvas=` 的项目画布加载出节点，选中节点显示已保存正文，资源库告警消失。
  2. 首次初始化：自动生成的 `/data/.setup-token` 生效；错误令牌返回 403 `SETUP_TOKEN_REQUIRED`；首个管理员建成后令牌文件被删除；重复 `/api/auth/setup` 返回 409 `SETUP_ALREADY_DONE`。
  3. 非 root：容器进程 `uid=1654(app)`；账号库、聊天库、项目与模型配置均以 `1654:1654` 写入 `/data`；`/data/config/ai-key.bin` 权限为 `0600`。
  4. 业务链路：创建项目与画布、创建节点、保存标题/正文并读回；聊天 SSE 收到 3 个文本增量并落库为 `completed`；PNG 附件上传后下载字节一致；`GET /api/web/events?canvas=...` 建立后编辑画布收到 `canvas.changed`。
  5. 模型密钥以 `aesgcm:` 密文落盘；`/api/web/models/{id}/test` 能解密并成功请求上游。
  6. 重建容器保留卷（`compose up --force-recreate web`，未执行 `down -v`）：账号、项目、画布正文、聊天、附件、模型配置全部可用，密钥仍可解密；`web-ai-config.json` 与 `ai-key.bin` 的 SHA-256 重建前后一致（`05728b26…` / `04899ec3…`）。
  7. 本地 HTTPS 反向代理（自签证书、Node 转发）后：登录、前端、聊天 SSE 流与 `canvas.changed` 实时推送均通过。
- 发现并修复：
  1. 模型/媒体配置默认写在 `/app/web-ai-config.json`，重建容器即丢失，而 `ai-key.bin` 在 `/data/config`。已在 Dockerfile 与 compose 中固定 `YEEYEEYEE__WebAiConfigPath=/data/config/web-ai-config.json`。
  2. 新建卷或旧版本升级得到的 `/data` 卷没有 `/data/projects`，首个项目创建报「项目存放位置不存在」。Dockerfile 预建 `/data/projects`、`/data/config`、`/data/assets`，同时 `WebProjectApi` 在启动时补建配置的项目父目录；不可写时只记日志并保持创建入口禁用。
- 未覆盖：公网域名、正式 CA 证书、外部反向代理（nginx/traefik）、DNS/端口转发与公网安全加固。本地自签 HTTPS 只证明应用在代理后可登录、流式聊天与实时推送，不能写成生产 HTTPS 验收。Docker Hub 推送仍未执行（无凭据）；真实 ComfyUI/外部模型生成仍未验收。
- 验收结束后已停止并移除隔离容器与网络，**测试卷 `yeeeyee-accept_accept-data` 保留**（未执行 `down -v`）；镜像保留为 `yeeeyee-web:accept-20261017`。正式容器 `dream-web-1` 与正式卷未受影响。

### 2026-10-17 并发聊天 CHAT_BUSY 偶发失败定位

- 现象：完整 Web 回归首次在 `mixed` 用例的 readonly 分支失败——`actions/<id>/commit` 期望 403（只读提议不能提交），实际得到 409 `CHAT_BUSY`。同一构建的 `--chat-only` 和随后完整重跑通过，说明不是稳定复现。
- 运行证据（隔离 fixture，临时诊断代码已移除，日志只含会话/消息/任务 ID，不含凭据或用户内容）：技能任务失败后，`CompleteJob` 会先取得与聊天发送、技能发起、提交相同的会话忙碌键，再写回 `skillState`、回挂产物并保存；诊断运行捕获到的顺序为：`skill-invoke release`（任务已提交）→ `complete-job acquire`（job Failed）→ commit 返回 409 → `complete-job release` → 同一 commit 返回 403。读取会话只能看到 `CompleteJob` 保存后的状态，保存发生在释放键之前。
- 结论：**测试抢跑**，不是产品忙碌状态未释放。`CompleteJob` 的 `finally` 会释放键；真正并发的聊天流在流式期间仍由同一缺陷之外的既有用例断言返回 409（`生成期间并行发送` 用例），本次未放宽该断言。
- 修复：只读与审批分支在断言提交结果前，轮询等待该回复的 `skillState` 变为 `completed`/`failed`（明确的落库完成条件，非固定 sleep）；未重试 409，未修改产品源码。
- 稳定性：修复后 `--chat-only` 1/1、完整 runner 3/3 通过（每次都会经过 mixed 的 readonly/approval/auto）。修复前 2 次完整运行中失败 1 次；3 次通过不能证明所有机器上彻底消除时序波动，但已把窗口从“完成回调全程”缩小到“状态落库到释放键之间的极短尾部”，与既有审批分支使用同一条件。若回调因拿不到键而跳过，新的等待会以 `skill did not settle` 失败，不会静默放过。
- 影响范围：仅 `YEEYEEYEE.Web.Tests/ChatRegression.cs` 变化；产品源码与 Web 发行包未变，包 SHA-256 仍为 `92B78C53A3C916E2A9348DFE3508073823E08A93263DC55DE23FA7296B6D8AC7`，无需重建，并已随 `v0.1.5.5` Release 上传。

### 未覆盖与限制

- `dataFormat` 是发行清单声明的整数，不是对 SQLite 架构的自动探测；清单缺失或人为改写时，脚本会拒绝自动回滚，这是有意保守。
- 备份是**服务停止后的文件级复制**，不是 SQLite 逻辑导出；验证覆盖了本机样本，未覆盖超大数据库、磁盘写满、备份中途断电等故障注入。
- 「备份或安装新程序目录失败时重新启动旧版」已做故障注入验证：备份阶段用被占用文件制造读取失败，安装阶段用被占用的目标版本目录制造移动失败；两种失败都恢复旧版并通过数据可用性检查。未覆盖：磁盘写满/只读卷、备份中途断电、超大数据库、ACL 或杀毒软件锁文件、`Move-Item` 移动 staging 的最后一步失败（与已验证的安装失败进入同一 catch 分支，但未单独构造）。
- 备份失败会留下 `backups\data-<时间>`（本次复跑为 1 个 157 字节的 `backup-state.json`，无完成记录），脚本不自动删除；`rollback-web.ps1 -RestoreData` 会以 `backup did not finish` 拒绝。历史无完成记录的旧备份同样被拒绝，脚本不提供“忽略校验”开关；确需使用旧备份时应人工核验文件后再决定，不能把它当作已验证备份。清理失败产物时只应删除该目录和 `staging\<版本>-<时间>`；本轮已验证删除后旧版本、`data` 与完整备份均保留。
- 恢复前校验会逐文件计算 SHA-256，大目录会增加等待时间；未覆盖超大备份、校验期间文件被外部进程改写、磁盘写满/只读卷或断电。
- 仅验证 Windows PowerShell 5、脚本启动/停止的单一实例；未验证 Windows 服务、任务计划、IIS、Docker、Linux、反向代理或多实例并发升级。
- 未验证 HTTPS、真实 ComfyUI、真实外部模型、正式域名与生产数据；这些不能由本地隔离结果替代。
- 升级日志可能包含请求路径和异常文本；脚本在打印日志尾部时会过滤含 token/password/secret/api key/authorization 的行，但日志文件本身仍应按运维数据保护。
- Web `dotnet publish` 当前有 1 条既有 nullable 警告 `CS8602`（`WebChatApi.cs:452`）；本批未改动该代码路径，构建与打包成功，但未宣称完整 Web 编译零警告。

## 自动测试（历史批次）

不同批次测试数量不同，分别保留，不累加。以下为历史记录，本次新增验证单独记录如下。

2026-10-16 断线恢复与 presence 边界专项验收批次（**这是专项验收，不是完整 Web 验收**）：

- 恢复机制结论：SSE 只推「变了」，**不存档、不重放**；断线期间的事件不会补投。所以恢复靠**重新获取快照**，而不是事件重放——本次没有新增任何重放机制，也没有改 SSE 的投递语义。原先的自动重连只保证「之后的新推送接得上」，断线期间别人改的内容会一直看不到（界面上连「有变动」都不会出现）；本次补上「重连后主动补取一次快照」这个入口：`useServerEvents` 的 `onReconnected`（首次连上不触发，断过之后每次重连触发一次）→ `WebCanvasApp.resyncAfterReconnect`，手上干净就复用 `followScene`（不动选中与视角），有草稿则不覆盖、只按修订差异给一条提示。
- 会话失效结论：守卫接口回 401（cookie 过期 / 被停用 / 口令被改）时，`request()` 广播一次会话失效信号，界面回到登录页并显示「登录已失效，请重新登录」，几条轮询随 `enabled=false` 停下，不再空转；`/api/auth/login` 的 401（凭据不对）不算，避免把「登录失败」读成「会话失效」。SSE 那一侧遇到 401 时 `EventSource` 本身就会放弃重连（响应不是 `text/event-stream`），不存在无限重试。
- presence 边界结论：主依据是**活着的 SSE 连接**（断开即注销一条连接，最后一条断开就下线），会话表 `last_seen_at` 只作 TTL 兜底；心跳 20 秒、TTL 默认 120 秒——所以「心跳间隔必须远小于 TTL」是这套语义成立的前提，TTL 配得比心跳还短时，连着的用户会被 TTL 过滤出名单（配置项允许 1..86400 秒；测试里故意设 3 秒来快速验兜底过期，生产用默认值）。
- presence 与租约的关系：**两条独立依据**，代码里没有任何「人离线就释放他的锁」的路径；本次新增回归把它钉住——掉线、甚至过了在线 TTL，仍然有效的节点租约必须原封不动，本人回来还能续期。
- 在线名单的作用域：`GET /api/web/presence` 是**服务级**的（在线问的是「这个人」，不按画布隔离）；画布级的编辑锁与 `canvas.changed` / `edits.changed` 推送仍按画布隔离（已有回归），`presence.changed` 同样是服务级广播。
- 断线期间切换画布：切换是**整页导航**（`window.location.assign`），旧页面连同它的 `EventSource` 一起销毁，旧画布的事件不可能落到新画布上。
- 过期修订：重连补取走的是 GET（拿服务端的修订），写入仍走 `baseRevision` CAS；冲突仍回 409 `SCENE_REVISION_CONFLICT` 且画布字节不变，没有静默覆盖。
- 复核方式与结果（自动化）：
  - 后端 `dotnet run --project YEEYEEYEE.Web.Tests --no-build` 全流程通过（Chat / Job attach / Node target / Skill / Workflow / 项目多画布 / 空画布 / HTTP / Presence / Auth / 编辑锁 / 布局 / 变更推送 / 桌面接入 / 结构 / 连线 / 位置 / 类别各分组）。Presence 分组新增两项断言：重新上线以 `connection` 依据回到名单、掉线（及过 TTL）不释放仍有效的节点租约且本人可续期。
  - 前端 `node node_modules/vitest/vitest.mjs run` 29 文件 346 项通过；`node node_modules/typescript/bin/tsc --noEmit` 与 `vite build` 通过。
  - 新增前端用例：`tests/serverEventsStream.test.ts`（首连不补取、断开重连各补一次、反复断连仍只有一条订阅、重连后的事件只应用一次、关订阅清掉旧错误、未登录不订阅）与 `tests/api.test.ts` 里的会话失效信号三项（守卫接口 401 广播、登录接口 401 不广播、其它失败不广播）。
- 复核方式与结果（真实浏览器，隔离实例 `http://127.0.0.1:18741`：独立项目 `reconnect-acceptance`、两个账号 jia/yix、账号库/任务库/项目/素材都在临时目录，未触碰任何真实项目与配置）：
  - 两个账号同时在线：页面（yix）在线芯片显示「2 人在线」，展开列出 jia（在线）与 yi（在线）。
  - 真实断开：直接杀掉服务进程。页面控制台出现 `net::ERR_CONNECTION_RESET` / `net::ERR_CONNECTION_REFUSED`（`/api/web/events` 与几条轮询），界面显示「推送连接断开，正在重连（编辑锁仍按 15 秒轮询兜底）」。
  - **重连后与最终状态一致**：**同一个文档**（`performance.timeOrigin` 显示文档创建于断网之前、全程没有重载）在服务恢复后，画布节点变成了「断线期间在服务端被改成的样子」——标题是断线期间直接改在画布文件里的 `offline-edited-title`，正文是断线前那次写入的 `jia-edited-body`，页脚修订号也与服务端当时的修订一致。断线期间服务不在，一条事件都没投递过；能变成这一版的唯一路径就是**重连后的补取快照**。
  - 重载后重连正常：刷新页面后不再显示断开提示，在线名单与画布立即取自服务端。
  - 该环境里**未能**完成的部分：本环境内置浏览器在「服务被杀、长连接被重置」之后，页面后续请求会一直挂住（连页面内 `fetch` + AbortController 也不落地），因此「断网之后同一次连接内的即时推送跟随」无法在这个页面里继续观察，也无法在同一个文档里再跑一轮断开/重连。真实 Edge/Chromium 的「网络层断开 → 自动重连 → 收到后续 `canvas.changed`」此前已单独验证；本轮的补取快照是在其之上新增的，两者合起来才覆盖「断线期间的变更」。
  - 附带观察（不在本轮范围）：验收时手写的 `project.json` 缺 `Id` 字段，服务端每次列项目都会给它生成一个新 Id，于是页面「项目与画布」下拉里的同名项会随轮询累积。真实项目的 `project.json` 由 `ProjectContext.Create` 写入、带固定 Id，不受影响，这里只作记录。
- 边界与未覆盖：服务端没有可注入时钟，心跳/TTL 的边界仍靠「把 TTL 缩到几秒 + 真实等待」来验；异常断网（对端静默消失、TCP 不发 FIN）只能靠心跳写失败或 TTL 兜底回收，本轮未单独构造这种网络条件；本轮的恢复是**快照补取**，不是逐条重放历史事件——「逐条重放」既不是现有设计，也没有实现或验收。

2026-10-16 聊天参数/实体/附件**真实浏览器验收**批次：

- 环境：隔离服务实例（`http://127.0.0.1:18750`，独立 `WebProjectsDirectory` / 账号库 / 任务库 / 聊天库 / 素材目录与 AI 配置，全部在临时目录）；对话模型用本地可控 fixture（同时提供 `/v1/images/generations` 与 `/images/edits`，返回固定 1x1 PNG 并记录收到的请求体）；ComfyUI 地址指向 `127.0.0.1:1`（不可达，用于验证「参数到达执行层」而不伪装成真实生成）。未改动任何真实项目、真实模型配置或用户素材。
- 验收方式：真实浏览器（Chromium）操作页面；执行层证据从隔离任务的 `jobs.db` 输入、fixture 收到的请求体、以及产品自身的 HTTP 接口读取，不只凭界面文字判断。
- 操作与结果（预期 = 实际）：
  - 新建隔离项目「验收聊天参数」并打开画布；创建人物/场景/道具三个设定与一个节点。
  - 附件：上传 PNG/TXT/PDF/MP3/WAV/MP4 六件并发送 → 消息按名称与种类回显，图片/音频/视频就地预览，文本与 PDF 只给链接（降级）；**刷新页面后历史与预览保持**；逐个下载得到 200 + 正确 MIME + 与本地文件逐字节一致 + `Range` 206；`application/zip` 返回「不支持的附件类型：application/zip」，文本冒充 PNG 返回「附件内容与声明的类型不符」；第 9 个附件提示「每条消息最多8个附件」。
  - 权限：匿名读附件 401；换到同项目另一张画布读该附件 404（`ATTACHMENT_NOT_FOUND`），该画布的会话列表为空。
  - 参数（编辑 → 提交 → 执行层）：文生图卡片上选比例 16:9、把模型给的负面词清空、种子填 0、勾选参考图 → 任务输入为 `aspectRatio=16:9, width=1536, height=864, seed=0`，**没有 negativePrompt**，参考图落为 `referenceImages` 并带字节哈希；另一轮填 4:3 + 负面词 `accept-negative` + 参考图，fixture 实际收到 `prompt=接口文生图用例：一只青色的猫\n避免出现：accept-negative`、`size=1024x768`，产物经审批预检通过后提交，画布节点上出现 `生成媒体` 附件。
  - 「0 是有效值」与「清空真的清空」两条都通过：`seed=0` 原样送达；清空负面词后该键不再出现在执行层输入里（不是退回模型的值）。
  - 实体引用：卡片显示 `实体引用 · 4 项`（人物/场景/道具/项目节点），服务端解析后回显；提交后执行层输入得到四条引用，含类型、ID、显示名（验收人物/验收场景/验收道具/开始）、变体名（默认）与节点分类（General）；无效引用返回 400 `SKILL_ENTITY_INVALID`「引用的实体不在当前画布或项目资源库中」并把该条提议留在可重试状态（不误用其它实体、不新建任务）。
  - 技能参数报错可修正：模型给出 `seed="不是数字"` → 卡片显示 `SKILL_PARAM_TYPE` 且保持可重试，改成合法值后重新提交成功；界面没有控件的参数（如给文生图传 `width`）则整条 `blocked`。
  - 普通聊天/流式：文本、思考、usage 三类事件分别到达并落库；模型首次失败时消息进入 `failed` 并出现「重试回复」，点击后**同一条消息**重试成功（不重复消息）。
  - 窄面板：参数卡（比例/反向词/种子/参考图/实体引用多行）在 334px 宽的面板里 `scrollWidth == clientWidth`，无横向溢出。
- 本次验收发现并修复的问题：
  1. 聊天上下文只列画布快照里的实体，项目库中新建设定在挂到节点前不进上下文 → 模型写不出可用的实体引用（实测四条引用全为空 ID）。改为按**项目库**列「资源视觉内容」，修复后模型给出的四条引用全部有效。
  2. 提交时前端只带用户**编辑过**的实体引用，未编辑时发空数组 → 界面显示着的引用在提交时被清掉（实测任务输入里没有 `entityReferences`）。改为编辑器与提交共用同一处「界面显示的引用集合」。
  3. 参数报错一律把卡片置为 `blocked` → 界面上有控件的参数（种子/负面词）改完也无法重新提交。改为：有控件的参数错误留在可重试状态，没有控件的仍 `blocked`。
  4. （代码复核发现）聊天出视频只把秒数放进输入，而 ComfyUI 视频模板认的是 `videoFrames`，用户要的时长被静默丢掉。改为按后端把秒折算成 1+8k 帧（与节点菜单同一折法）。
- 未覆盖：布尔与对象两类参数目前没有任何内置技能声明可编辑，因此浏览器上没有对应控件（校验与编辑器由后端与前端回归覆盖）；真实 ComfyUI 与真实视频服务不可用，相关任务在 provider 处失败，只验证到「执行层收到了什么」；真实浏览器中少数卡片内的按钮点击因自动化工具的元素引用在频繁重渲染下失效未能完成，重试与引用提交改用产品同一条接口、同一请求体补验。

2026-10-16 聊天参数/实体/附件批次：

- `dotnet run --project YEEYEEYEE.Web.Tests -- --chat-only` 通过（本地 fixture，不替代外部模型验收）：协议空/坏块拒绝、设置、模型、历史、工作树、技能、附件、实体、成片、并发聊天等原有分组全部通过，并新增两组——`Chat attachment type regression passed`（WebP/GIF/PDF/MP4/MP3/Markdown 上传、签名与类型上限拒绝、下载 MIME 与字节往返、Range 206、非图片参考图拒绝、服务端声明的可选类型与参数清单）与 `Chat parameter and entity reference regression passed`（六类参数校验与全部稳定错误码、参数落到执行层输入、人物/场景/道具/项目节点四类引用解析回填与回显、未知实体/类型不符/缺 ID 明确拒绝）；同批次含「替换语义」（改过的按用户的、清空的真的清空、`seed=0` 原样送达、参考图清空后不再提交）、可编辑声明的断言、秒折帧、以及「有控件的参数错误可重试 / 无控件的整条挡住」。
- 修复后复跑：`dotnet run --project YEEYEEYEE.Web.Tests --no-build -- --chat-only` 再次通过（含聊天上下文改列项目库之后的全部原有分组）；前端 `tsc --noEmit`、`vite build` 与 28 文件 337 项 Vitest 通过。
- `dotnet run --project YEEYEEYEE.Agent.Tests` 全部通过（含共享动作解析与执行器用例）；`dotnet run --project YEEYEEYEE.Core.Tests` 31 项通过。
- 前端 `tests/chatSkillParams.test.ts` 新增 12 项，与 `chatStream.test.ts` 一起重跑；`node node_modules/vitest/vitest.mjs run` 全量 28 文件 334 项通过；`tsc --noEmit` 与 `vite build` 通过。`chatStream.test.ts` 里一处源码断言按等价结构更新（参考图提示改为按 `hasTargetNode` 入参分岔）。
- 未做真实浏览器人工验收，也未接真实模型/外部生成服务；本轮未重跑转换专项与 Docker 相关检查。

2026-10-16 隔离转换批次：

- `dotnet run --project YEEYEEYEE.Web.Tests -- --workflow-only` 通过：API 导入/安装、UI 直接导入拒绝、重命名、声明参数与实际 prompt 绑定、history 轮询、下载、停用与删除。新增筛选入口复用原用例，避免聊天失败阻塞相关回归。
- `dotnet run --project YEEYEEYEE.Web.Tests --no-build -- --conversion-only` 通过：隔离路由、权限与会话绑定、错误身份/结果拒绝、目标认证与头隔离、路径前缀与重定向、HTTP/WS 重写及 echo/close、取消、mock 导出结果到 API/UI 双文件落盘。未运行真实官方前端。
- `dotnet run --project YEEYEEYEE.Agent.Tests -- ComfyUiLibrary ComfyUiSiteIds` 全部 9 项通过。两项旧落盘断言已按 API/UI 双文件更新，并核对原稿原样读回与孤儿文件成对清理。
- `tsc --noEmit` 与 `workflowConversion.test.ts` 10 项通过；本轮未重复前端生产构建或全套前端测试。`git diff --check` 通过。
- 独立 `--chat-only` 回归通过：聊天协议、设置、模型、历史、工作树、技能、附件、实体、成片及并发聊天等专项均通过；完整 Web runner 之前曾在并发消息 POST 流状态断言失败，已补数字状态码与请求路径。该专项使用本地 fixture，不能替代外部模型验收。

2026-10-11 历史记录：前端 Vitest **172 项通过**；`tsc --noEmit && vite build` 通过；`tokens:check` 通过；本机 Node 为 24.21.0，CI 与 Docker 使用 Node 22，本机结果不替代 CI。

历史隔离 Web 后端回归 **27 个分组通过**，使用本地模型/ComfyUI mock，清除代理后直连回环地址；该结果不替代真实模型或生成服务验收。账号管理入口修复批次复用现有会话面板，记录为 19 个测试文件、163 项用例、TypeScript 检查和 Vite 构建通过；与上述 172 项及下文生命周期修复的 171 项属于不同批次，当前记录未明确其先后顺序。

Web 控制台回归包含认证权限、画布保存和锁、项目与多画布隔离、资源版本和引用保护、任务与聊天回归。`ChatRegression.cs` 用本地模型服务覆盖 OpenAI/Anthropic 流、会话隔离与恢复、取消/重试、模型密钥不回显及加密存储。后端历史运行结果见 [Desktop 验证记录](Desktop进度与待办.md#自动测试与构建)；失败或阻塞不能沿用历史通过数字。

## 部署条件与限制

- Windows ZIP 部署不再直接启动 DLL：先解压到临时目录，用 `install-web.ps1 -InstallRoot <目录> [-LegacyDataDir <旧目录>]` 建立 `versions/` + `data/` 布局，再用 `start-web.ps1` 启动。升级用 `upgrade-web.ps1`，回滚用 `rollback-web.ps1`；具体命令见包内 `README-WEB.txt`。
- `start-web.ps1` 默认监听 `http://localhost:5000/health`（与 `appsettings.json` 一致）；用 `-Port` 覆盖 Kestrel 监听与健康检查地址。`DataDir` 默认是安装根下的 `data`，也可以显式指定到另一个持久卷。
- 源码开发默认地址为 `http://localhost:5000`（`appsettings.json`）；Docker Compose 默认为 `http://localhost:8080`。Docker 路径仍使用 `/data` 卷，既有的 `/data` 布局逻辑不变。
- 要在页面创建/打开项目，配置 `YEEYEEYEE__WebProjectsDirectory` 为服务账号可写的项目父目录；已配置项目画布模式时也可使用该项目父目录。独立 scene 模式未配父目录时不提供创建/打开能力。
- Docker 卷需同时覆盖任务、账号、聊天库、素材、配置及项目目录。仅构建镜像不能证明重启后持久化或密钥可解密；Linux 本机密钥文件与配置应由同一服务账号保存并安全备份。
- 新模型/媒体密钥保存必须取得实际加密结果；加密不可用返回错误。换机器或账号后旧密钥可能不可读，需重新填写。
- Web 媒体设置保存共享配置；工作流清单/导入使用页面所选目标，保存后的工作流调用、状态、下载与取消绑定该站点地址。内置模板等既有执行器仍需核对启动配置，不能据此认定全部媒体执行器热切换。
- UI 官方导出需配置 `ComfyUIConversion:MainOrigin` 与 `ComfyUIConversion:ConversionOrigin`（环境变量为 `ComfyUIConversion__MainOrigin`、`ComfyUIConversion__ConversionOrigin`），均为无路径纯来源；生产必须 HTTPS。转换来源应使用与主站不同的可注册域，禁止主站父子域，主站 Cookie 使用 host-only。当前代码未集成公共后缀列表，同可注册域的兄弟域需由部署方排除。
- 转换域及其一层通配子域均需 DNS/TLS 覆盖；会话 `{session}.转换域`、worker `w0-{session}.转换域` 指向同一 Web 服务。代理须保留 Host、端口、scheme 并支持 WebSocket Upgrade。当前严格校验实际请求 scheme，TLS 应直达应用或重新加密；未经可信 forwarded headers 配置验证，不可直接假设 TLS 终止代理可用。
- 目标认证只从服务器 `ComfyUI:Authorization` 读取，且仅用于精确匹配启动 `ComfyUI:BaseUrl` 的目标；其他地址暂不支持独立凭据。不得在页面 URL 填 userinfo 或 query 密钥。主站 Cookie、浏览器认证和无关请求头不转发到目标。
- 生成仍需管理员预授权 `WebWorkflowApprovalMode=preapproved-local-workflows` 及已有技能权限。转换来源未配置时显示提示，API JSON 直接导入可继续使用。
- 会话最长 15 分钟，逐份重建官方前端 iframe；支持取消、关窗与过期回收。结果已知 Content-Length 限 16 MiB，chunked body 及代理文本缓冲尚无同等显式限制，部署应设置请求/响应资源上限。
- 聊天附件按类型注册表受限（图片 5MiB、音视频 16MiB、PDF 8MiB、文本类 64KiB，请求体硬上限 16MiB），并逐类校验签名与 MIME；类型不认识的直接拒绝，不按扩展名猜。技能参数只接受技能自己声明的名字与类型，声明之外一律返回错误码，不会静默忽略。实体引用现为「校验 + 保存 + 回显 + 交给执行层输入」：三个内置生成技能当前不据此改变出图条件（参考图仍按绑定实体变体与聊天附件解析），工作流技能明确拒收实体引用。内置技能目前没有声明对象类型参数（校验与前端编辑器已支持，加声明即可用）。

## 历史 P0 验收证据

- [x] 两个真实浏览器账号同时在线；同节点租约互斥已验证：编辑者持锁时管理员的节点名称、正文和应用修改均禁用，并显示占用者。
- [x] 编辑者保存后，管理员会收到变动提示并看到相同正文和新修订；随后锁释放。
- [x] 跨画布隔离已验证：管理员在新画布保存内容后，原画布节点和正文保持不变。
- [x] 两个真实浏览器账号在不同画布分别保留草稿并成功保存；画布 A 的节点与正文未被画布 B 串写。
- [x] 旧修订服务级保存保护：使用旧 `baseRevision` 返回 `409 / SCENE_REVISION_CONFLICT`，标题、正文、完整快照、画布文件字节、SHA-256 和备份数量均保持不变。
- [x] 租约超时与关闭页面：关闭编辑者标签后未立即释放；服务端租约在约两分钟兜底超时后回收，管理员刷新后可重新编辑，未保存草稿未落盘。
- [x] 项目切换与刷新：浏览器新建并切换到隔离项目 `P0切换恢复验收20261011` 后可加载默认画布；重新打开 `127.0.0.1` 会话并刷新原画布后，项目、画布、修订和已保存正文恢复。
- [x] SSE 浏览器订阅在刷新/重新打开后重新建立；服务级断开后重订阅收到新 `canvas.changed` 事件，recordId、actor、scope 和 revision 一致。
- [x] 浏览器网络层主动断开 SSE 后显示断开状态；网络恢复后 EventSource 自动重连并收到后续 `canvas.changed` 事件，界面更新为恢复后的正文。

## 历史实机记录与待验收

- [x] 关闭页状态回收根因与源码修复：在线名单将 `recent`（默认 120 秒内最近活跃会话）混计为在线；现仅统计 `basis=connection` 且连接数大于零，最近活跃在展开名单单独标注。节点锁补充 `pagehide` 按 leaseId 的 DELETE keepalive 释放、停止心跳及 bfcache `pageshow` 重新申请，处理晚到申请和旧周期异步响应；普通切后台不释放，异常退出继续依靠服务端 TTL。
- [x] 状态回收自动回归：新增 6 项节点租约生命周期和 2 项在线展示测试；相关 5 文件 49 项、完整前端 20 文件 171 项通过，TypeScript 检查、Vite 生产构建及 `git diff --check` 通过。
- [x] 新构建真实浏览器复测：隔离 5099 两账号实际加载 `index-7cwG0Gy3.js`；编辑者持锁时管理员正文禁用，确认关闭编辑者标签后在线人数从 2 降为 1。重新打开编辑者页面仍登录，项目、原正文及修订 `199673082247042` 恢复，测试草稿未落盘。
- 历史自动化工具关页失败记录（真实 Edge 窗口复测已通过，见下项）：接受未保存提示后确实关闭标签，但服务端仍返回 leaseId `b2457332-95fe-4a7f-88d6-ea4da368f13f`，申请时间 `07:07:20Z`、到期时间 `07:09:20Z`；随后租约列表清空，管理员恢复可编辑，TTL 兜底有效。还原草稿提前释放后再关闭不计为关闭释放通过。代码与实际 bundle 均同步发起 pagehide keepalive DELETE；针对性 8 项测试及 tsc 通过，但尚不能区分页面事件未执行、请求未送达或服务端拒绝，需采集事件与 DELETE 到达/响应证据。
- [x] 关闭释放链路对照采证：临时 localStorage trace 确认观测构建 `index-hYEzzF0o.js` 实际加载。工具关闭持锁标签后，租约 `35d1ec2e-6560-472a-8f8e-cc93e90517bf` 留存，trace 仅有申请成功、没有 pagehide/stop/release 记录；整页导航则在 `1791703629661` 记录 pagehide（persisted=false）并同步 release-start，5 毫秒后 release-failure，浏览器网络记录 DELETE 为 `net::ERR_ABORTED`，无 HTTP 拒绝状态证据。页面内还原草稿的 cleanup 对照使用租约 `1f82385b-b6a1-41cf-811b-c76977cacf16`，DELETE 约 102 毫秒后成功。因此页面内释放路径有效，退出时事件/传输仍有缺口；未确定 ERR_ABORTED 的底层原因，也不能据工具关页推断普通浏览器关页行为。临时源码观测已移除，隔离入口恢复 `index-7cwG0Gy3.js`，针对性 8 项测试和生产构建通过。后续真实 Edge 关闭结果见下项；DELETE HTTP 状态与服务端日志仍未单独采集。
- [x] 真实 Edge 窗口关闭回收复测通过：人工登录 test 编辑者后，在隔离画布 `BB61E4D82B41D67E232C710D` 的节点 `77c4125b-f7dd-4270-97a0-bd1ff63110ec` 保留“Edge窗口关闭未保存验收。”草稿，界面显示“你正在编辑”和“有未保存修改”。服务端确认租约 `93168f32-b236-4a66-adea-4e7f34b9dae9`，申请时间 `07:45:21Z`、到期时间 `07:47:21Z`。点击真实 Edge 窗口关闭按钮并接受“离开”后，`07:46:02Z` 查询租约列表为空，早于 TTL；应用列表确认 Edge 窗口已退出，管理员正文可编辑，保存正文与修订 `199673082247042` 未变。本次确认真实窗口退出回收有效，但未单独记录 DELETE 的 HTTP 状态或服务端请求日志。此前工具关闭标签和导航 aborted 仍作为工具路径限制保留，不能视为真实 Edge 关闭失败；自动化浏览器中另有 test 页面，因此本次不要求在线人数降为 1。
- [x] 同账号跨标签页租约问题已修复：每个编辑周期携带独立 `ownerId`，服务端按持有周期幂等申请；关闭或结束 A 不释放 B。修复前回归确认共享 leaseId，修复后 HTTP 与真实 Chromium 双标签确认独立 leaseId、A 释放后 B 可续期且继续阻止其他用户。
- [x] 真实 Chromium bfcache：自然导航离开记录 `pagehide persisted=true`，返回记录 `pageshow persisted=true`；草稿保留并申请到新的 leaseId。普通 back_forward 重新加载不计为此项通过。
- [x] 真实浏览器旧修订冲突：外部推进 revision 后保存返回 409，界面显示冲突并保留本地草稿。隔离 TCP 代理断开 SSE 后显示重连提示，恢复后自动接收后续事件。浏览器证据位于临时目录 `dream-browser-p0-20261011/run-1791710933044`。本次未证明断线期间全部历史事件逐条重放。
- [x] 隔离 Web 真实 DeepSeek `deepseek-flash` HTTP 验收：111 个文本增量、真实 usage、完成消息与落库一致；部分输出后取消持久化为 cancelled，重启后重试成功且不重复创建消息；生成中强制终止后恢复 interrupted 并保留 58 字。真实配置 SHA256 前后相同，密钥未回显。
- [x] 真实模型动作 HTTP 验收：approval 的提议/预览不改画布，commit 落盘且 undo 恢复；readonly 提交 403，auto 实际提交；重复/过期提交 409，带 ask 的 auto 不自动提交。证据位于临时目录 `dream-real-chat-20261011-unique`。另完成真实 Chromium 聊天：生成完成前正文持续增长，刷新后内容一致；取消后刷新保留部分正文和 cancelled，重试不重复消息。三轮分别收到 35、1021、2081 个真实 thinking SSE 事件，界面显示思考折叠项；证据位于 `dream-browser-p0-20261011/real-ui-1791711420689`。
- [x] 管理员配置浏览器保存：修改隔离模型显示名称、密钥留空，PUT 200，刷新后名称保持且已有密钥标志保留；验收后恢复隔离配置，真实配置 SHA256 不变。独立配置脚本退出 0，证据位于 `dream-browser-p0-20261011/settings-ui-1791711713000`。
- [x] 真实 ComfyUI 端到端验收：官方前端加载并导出 API、Web 保存/重启重读、真实提交获得 prompt_id、history 成功、下载并与 ComfyUI `/view` 字节一致的产物；失败、缺模型、缺节点、执行错误、取消与输出缺失均明确。见「2026-10-18 真实 ComfyUI 端到端验收」。未覆盖生产 TLS/通配 DNS；未回写正式项目（按任务要求不操作正式项目）。
- [x] 隔离真实 HTTP 资源验收：跨画布、草稿与历史引用阻止删除且文件字节不变；损坏画布返回 REFERENCE_SCAN_FAILED；回退恢复描述、布局和引用且保留历史，再提交新版本成功；无引用实体可删除。
- [x] 隔离成片 POST、版本列表、MP4 下载、Range 206 字节一致及匿名 401 验收；两段拼接下载样本为 128×128 H.264、48 帧、2 秒，ffmpeg 完整解码成功。Web 回归与 Agent 媒体筛选 10 项通过。随后另生成两段各 1.2 秒 H.264/AAC，经产品 Mp4Concatenator 拼接为 2.400 秒、60 帧；双轨从 0 开始且时长一致，全文件解码成功，1.2 秒接缝包时间戳连续，Chromium 解码 60 帧并播放至 ended 无媒体错误。证据位于 `dream-aac-acceptance-639273084501877285`；未做人工听感或 Avalonia 播放器验收。
- [x] Docker 服务启动、首次管理员初始化、HTTPS 反向代理、非 root 写盘、重启后数据与密钥恢复：2026-10-17 在隔离 Compose 项目、测试卷与独立端口上实际通过（见上文「Docker 容器运行与持久化验收」）。仍未覆盖公网域名、正式 TLS 与外部反向代理。

## 未完成与后续

- [x] Web 官方导出已用真实 ComfyUI 0.37.0 / 官方前端 1.52.7 验收：隔离 `*.localhost` 安全上下文加载官方前端、导出 API、真实生成并下载。仍需：正式隔离域名/通配 DNS/TLS 终止、不同前端版本与更广自定义节点/模型组合；生产必须 HTTPS（非 localhost 的 HTTP 会因无 `crypto.subtle` 明确失败）。
- [x] 聊天技能的任意声明参数、人物/场景/道具/项目节点实体引用与更多附件类型已接通声明、前端编辑、提交、后端校验、执行层读取与错误码返回，并有前后端回归；本批已完成**真实浏览器验收**（隔离实例 + 可控模型 fixture：六类参数中字符串/数字/枚举/数组在浏览器上编辑提交并核对到执行层与 provider 请求、四类实体引用选择/回显/无效引用明确拒绝、六种附件上传/回显/预览或降级/下载/权限边界、刷新与切换画布后信息正确、失败重试）。仍未覆盖：布尔与对象参数没有内置技能声明（无界面控件）、真实 ComfyUI 与真实视频服务（任务在 provider 处失败，只验证到执行层输入）、以及真实模型据此改变出图条件的验收。
- [x] Web 开奖第一阶段已接入：浏览器偏好、卡背候选区、整批终态后主动开奖、React/CSS 揭晓动画、跳过/关闭重开、桌面与手机/横屏/减少动态效果、现有采用接口与成功收起/失败保留、批次替换与旧回包隔离。前端类型检查与 41 项直接受影响用例通过；浏览器（真实构建 + mock 接口）在桌面、竖屏与横屏走通开奖、跳过、选中与采用收起。仍未实现：质量判档、裂纹卡自动重出、批次持久化、未采用文件回收；真实 ComfyUI 出图与真实采用回写因本机 8188 无服务而未验证（2026-10-18 后补环境即可按同一路径复验）。
- [ ] 全库 ComfyUI 兼容性、跨镜人物一致性和旧项目资产补齐尚未完成验收。
- [x] Docker 环境阻塞已解除：Docker Desktop 4.93.0 的 Linux engine 29.8.1 就绪，隔离 Compose 项目完成构建、初始化、非 root 写盘、HTTPS 代理及重启数据/密钥恢复（见「Docker 容器运行与持久化验收」）。原有 `docker_data.vhdx` 与正式卷保留、未重置。剩余未覆盖项是公网域名、正式 TLS、外部反向代理与 Docker Hub 推送。
- [x] 已使用现有真实配置在独立隔离 Web 完成真实模型 HTTP 与浏览器验收；原 5099 服务未更改模型配置。
- [x] ComfyUI 环境阻塞已用本轮提供的真实地址解除：0.37.0、5986 种节点、114 个 checkpoint，真实生成与下载通过（见「2026-10-18 真实 ComfyUI 端到端验收」）。原画布附件回写未在本轮验收（按任务使用无目标节点的技能调用，未写回正式项目）；本地 8188 与 ZEALMAN 面板地址不在本轮范围。
- [x] 断线恢复与 presence 活跃时间边界已单独验收（隔离实例 + 两个账号 + 真实浏览器，见上文「断线恢复与 presence 边界专项验收批次」）：恢复靠**重连后补取快照**（不是逐条重放），重连后画布与服务端最终状态一致；presence 的断开、TTL 过期与重新上线边界、同账号多标签聚合、掉线不释放仍有效的节点租约均有回归或实测。仍未覆盖：异常断网条件（对端静默消失、TCP 不发 FIN）未单独构造；自动化浏览器在长连接被重置后网络栈会挂住，因此「断网后即时推送跟随」只能沿用此前真实 Edge/Chromium 的结论；人工音画听感仍未单独验收（音视频时间戳连续性此前已通过）。**逐条重放历史事件不是现有设计，也不在待办内。**
