# YEEYEEYEE 画布 ⇄ 宿主消息协议（Canvas ⇄ Host Protocol）

- 协议版本：`1`
- 状态：信封、方向和基础载荷校验已在 C# 与 TS 两侧实现，资源替换桥接和控制台用例已存在；两侧的细粒度校验仍有差异，跨语言夹具尚未覆盖资源替换，当前只有部分宿主在用它，见下方适用范围
- 适用范围：**任何画布侧实现与其 C# 宿主之间**。当前实际使用方为 `YEEYEEYEE.Host`（测试与桥接）、`YEEYEEYEE.Web`（作业服务及保留的 WebSocket 画布桥接；普通浏览器现走第 8 节 HTTP 路径）与 `YEEYEEYEE.Canvas`（TS 侧同构实现）；**桌面端 `YEEYEEYEE.Desktop.Avalonia` 的自绘画布不使用本协议**，其 Agent 使用独立 JSON actions 协议；桌面通过 HTTP 推送 records 并轮询引用替换请求（见 [桌面与 Web 数据通路](../02_Architecture.md#桌面与-web-数据通路)）
- 不适用范围：C# 宿主之间、服务端内部调用、Yjs 房间内部同步（协作未实现）
- **Goal7 待复核补充**：下方第 8 节记录现有 `/api/web` JSON/HTTP 接口；它不是此处版本 `v: 1` 的消息信封，也不继承 `canvas/*`、`host/*` 消息的校验/权限承诺。不要把 HTTP 状态码与 `host/error` 混用。

## 1. 设计约束

1. 画布是独立的 TypeScript/React 包，不是 Razor 组件。C# 宿主与画布之间**只能**通过本协议通信，不得假设存在共享的 Razor 画布或共享 .NET 对象图。（注：当前参考实现 `YEEYEEYEE.Canvas` 渲染的是普通 div 卡片，未使用 tldraw，相关依赖已清理，前端工程也不在解决方案内。）
2. 协议不承认画布上报的身份。画布**禁止**上报 `role`、`clientType`、`userId` 作为鉴权依据；宿主通过 `host/init` 下发由服务端会话派生的能力位（capabilities），画布只按能力位决定 UI 是否可编辑、可执行。
3. 协议版本必须精确匹配。不匹配时宿主拒绝初始化（fail-closed），不得降级为"尽力兼容"或"忽略新字段继续跑"。
4. 未知消息 `type` 一律 fail-closed：不得静默丢弃后继续处理后续消息，不得产生任何画布或数据变更。
5. 消息体为 JSON 对象，字段名小驼峰。**未知字段必须忽略**（向前兼容），但**未知 `type` 不得忽略**（见第 4 条）。
6. 场景记录（records）是传输数据，不限定为 tldraw 格式；当前 HostBridge 转发投影，尚未实现通用批次去重和修订冲突处理。
7. 单人撤销是画布本地语义；宿主不得用快照回滚覆盖协作房间中他人的 CRDT 改动。
8. 能力位必须可被服务端声明集覆盖：对携带 `serverClaims` 的消息（`host/init`、`host/capabilities`），任一为 `true` 的能力位都必须有对应声明（`canEditCanvas`←`canvas.edit`、`canInvokeSkill`←`skill.invoke`、`canCancelJob`←`job.cancel`、`canUndo`←`canvas.undo`）。出现"能力位超出声明集"时画布必须 fail-closed，不得按 `role` 提升权限。
9. 消息方向即契约的一部分：`canvasToHost` 的处理器必须拒绝 `host/*` 类型，反之亦然，不得因为"类型已注册"就接受反向消息。

## 2. 信封（Envelope）

所有消息均为同一信封结构：

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `v` | integer | 是 | 协议版本，当前必须为 `1`。不匹配即 fail-closed。 |
| `id` | string(uuid) | 是 | 消息唯一 ID。缺失或非 uuid 视为 `PROTOCOL_MALFORMED`。 |
| `replyTo` | string(uuid) | 否 | 请求-响应关联。仅用于应答类消息。 |
| `type` | string | 是 | 命名空间化类型，如 `canvas/hello`。未注册类型 fail-closed。 |
| `ts` | integer | 是 | 发送端 Unix 毫秒时间戳。仅用于日志与超时诊断，不参与鉴权。 |
| `payload` | object | 是 | 类型相关载荷；无载荷时使用 `{}`，不得为 `null`。 |

```
{
  "v": 1,
  "id": "8f1a2c34-5b6d-4e7f-8a90-1b2c3d4e5f60",
  "replyTo": null,
  "type": "canvas/hello",
  "ts": 1790295308000,
  "payload": { }
}
```

## 3. 消息目录

### 3.1 画布 → 宿主（canvasToHost）

| 类型 | 载荷 | 说明 |
| --- | --- | --- |
| `canvas/hello` | `canvasVersion`, `protocolVersion`, `minHostProtocol`, `features[]` | 握手。宿主必须先校验版本再回 `host/init`。 |
| `canvas/op.batch` | `batchId`, `baseRevision`, `source`(`user`\|`ai`), `ops[]` | 画布本地产生的记录变更。`ops` 为场景记录操作数组；HostBridge 当前无接收处理分支。 |
| `canvas/undo.request` | `localOnly`(必须为 `true`) | 单人本地撤销。`localOnly=false` 一律拒绝。 |
| `canvas/redo.request` | `localOnly`(必须为 `true`) | 单人本地重做。 |
| `canvas/invoke.request` | `invocation`, `idempotencyKey` | 请求执行 Skill/Tool。宿主必须按会话身份重新鉴权。 |
| `canvas/job.cancel.request` | `jobId` | 请求取消异步任务。 |
| `canvas/selection.changed` | `recordIds[]` | 选中上下文，供 AI 提示词使用；无权时宿主可忽略（当前 Web 画布还会带上 `entityId`、`entityKind`、`openResourceLibrary`，表示"选中了节点里的某个引用元素"）。 |
| `canvas/resource.replace.request` | `recordId`, `entityId`, `variantId`, `variantVersionId`(可空) | 请求替换节点引用所锁定的设定版本；`variantVersionId=null` 表示"跟随最新"。C# 校验三个必填 ID 和可选版本 ID 为 uuid；TS 当前只校验字段存在，uuid 细校验待统一。 |
| `canvas/diagnostic` | `level`, `code`, `message` | 画布侧诊断上报，只写日志，不触发状态变更。 |

### 3.2 宿主 → 画布（hostToCanvas）

| 类型 | 载荷 | 说明 |
| --- | --- | --- |
| `host/init` | `protocolVersion`, `hostVersion`, `session`, `capabilities`, `scene`, `locale` | 唯一允许建立会话的消息。`session` 为服务端派生事实，仅供展示与关联。 |
| `host/op.batch` | `batchId`, `revision`, `origin`(`remote`\|`host`), `actorSessionId`, `ops[]` | 下发远端/宿主产生的记录变更。 |
| `host/scene.reset` | `revision`, `reason`, `scene` | 全量场景重置（如首次载入、修订号丢失）。 |
| `host/undo.result` | `ok`, `localOnly`, `revision`, `reason` | 撤销/重做结果。 |
| `host/job.update` | `jobId`, `invocationId`, `state`, `progressPercent`, `errorCode`, `errorMessage`, `outputs[]` | 异步任务状态推送，包含排队/运行/取消中/成功/失败/已取消。 |
| `host/capabilities` | `serverClaims[]`, `canEditCanvas`, `canInvokeSkill`, `canCancelJob`, `canUndo`, `reason` | 能力位变更（如权限被回收）。能力位仍需被 `serverClaims` 覆盖（见约束 8）。 |
| `host/error` | `code`, `message`, `severity`(`warning`\|`fatal`), `relatedType` | 协议级错误。`fatal` 表示画布必须停止处理后续业务消息。 |
| `host/resource.replace.result` | `requestId`, `ok`, `message`, `revision`(可空) | 资源版本替换结果。`requestId` 由请求方（Web 服务）分配并回带；宿主未接入替换处理器时改为回 `host/error` 的 `RESOURCE_REPLACE_UNAVAILABLE`。 |

## 4. 错误码

| 错误码 | 触发条件 | 处理 |
| --- | --- | --- |
| `PROTOCOL_VERSION_MISMATCH` | 信封 `v` 或握手 `protocolVersion` 与本地不匹配 | fatal，拒绝初始化 |
| `PROTOCOL_UNKNOWN_TYPE` | `type` 未在目录中注册 | fatal，丢弃且不做任何变更 |
| `PROTOCOL_DIRECTION_MISMATCH` | 收到反向消息（如画布收到 `host/*` 之外的宿主专属类型被误投） | fatal，丢弃且不做任何变更 |
| `PROTOCOL_MALFORMED` | 缺少 `id`/`type`/`ts`/`payload`，或类型不符 | fatal |
| `PROTOCOL_UNAUTHORIZED` | 画布尝试执行能力位未授予的操作 | warning，拒绝该操作 |
| `PROTOCOL_FAIL_CLOSED` | 依赖校验失败、状态非法等 | fatal，进入降级态 |
| `SCENE_REVISION_CONFLICT` | `baseRevision` 落后于宿主修订号 | warning，宿主下发 `host/scene.reset` |
| `JOB_NOT_FOUND` | 取消的任务不存在或不属于当前会话 | warning |
| `JOB_NOT_CANCELLABLE` | 任务已进入终态 | warning |
| `RESOURCE_REPLACE_UNAVAILABLE` | 宿主没有接入画布资源替换处理器 | warning，回 `host/error`，不做变更 |
| `RESOURCE_REPLACE_FAILED` | 资源替换处理器抛错（找不到节点/变体、节点已锁定等） | warning，回 `host/error`，不做变更 |
| `REFERENCE_UNRESOLVED` | 引用目标不存在（Skill/Tool/Channel/Asset） | fatal，不降级为空节点 |
| `REFERENCE_UNAUTHORIZED` | 当前会话无引用权限 | fatal |
| `REFERENCE_VERSION_UNSATISFIED` | 版本约束不满足 | fatal |
| `REFERENCE_RECURSION_LIMIT` | 依赖图超过递归上限 | fatal |
| `REFERENCE_CYCLE` | 依赖图存在循环引用 | fatal |

## 5. 目标状态机与实现边界

下列状态机和失败矩阵是契约目标，不能视为已全部实现。当前 `HostBridge.Initialize` 独立发送 `host/init`；收到 `canvas/hello` 仅设置 `ready`，尚未检查 payload 中的握手版本或据此发送 init。接收分支仅处理 hello、invoke、resource.replace、job.cancel；op.batch、undo/redo、selection 和 diagnostic 尚无业务处理。资源替换处理器只检查会话与 ready，没有单独检查 `canvas.edit` 声明；正式权限接入属于后续目标。

两端 codec 对多数 payload 只检查字段存在，未实现完整 schema：`replyTo` 未校验，TS 的 `ts` 未限定整数、`host/capabilities` 未检查能力位对应声明、Job 状态和 ID 校验较 C# 少；C# 某些类型错误可能从 JSON getter 抛出普通异常，未统一为协议错误。以下规则描述应达到的行为，补实现前须增加反例夹具。

画布侧：`created` → （发出 `canvas/hello`）→ `handshaking` → （收到 `host/init`）→ `ready`；收到 `host/error{severity:fatal}` 或版本不匹配 → `fatal`（停止处理业务消息，仅允许重连握手）。

宿主侧：`idle` → （收到 `canvas/hello` 且版本匹配）→ `initializing` → （发出 `host/init`）→ `active` → （`host/error{fatal}` 或会话失效）→ `closed`。

## 6. 失败处理矩阵

| 场景 | 宿主行为 | 画布行为 |
| --- | --- | --- |
| 版本不匹配 | 不回 `host/init`，回 `PROTOCOL_VERSION_MISMATCH` | 进入 `fatal`，提示升级，不发送业务消息 |
| 未知 `type` | fail-closed，回 `PROTOCOL_UNKNOWN_TYPE` | 停止应用后续消息，进入 `fatal` |
| 载荷缺字段/类型错 | 目标为回 `PROTOCOL_MALFORMED`，不做变更；当前类型校验缺口见第 5 节 | fatal 时停止业务处理，记录诊断 |
| 画布越权执行 | 回 `PROTOCOL_UNAUTHORIZED`，每次执行都重新鉴权 | 回滚本地乐观 UI，禁用入口 |
| 依赖引用失败 | 回 `REFERENCE_*`，不执行、不降级 | 展示失败，不生成占位节点 |
| 修订号冲突 | 下发 `host/scene.reset` 或要求画布重取 | 丢弃本地未确认批次，重放已确认批次 |

## 7. 跨语言一致性夹具

`protocol/fixtures/` 与 `protocol/fixtures/manifest.json` 是 C# 端与 TypeScript 端的**共同事实来源**：

- C# 端 `YEEYEEYEE.Core.Tests` 与 TS 端 `vitest` 都读取 `manifest.json`；
- 对 `valid: true` 的夹具，两端编解码后必须得到相同的 `type` 与 `payload` 关键字段；
- 对 `valid: false` 的夹具，两端应抛出/返回 `manifest` 中声明的 `expectedErrorCode`；若两端当前校验深度不同，先补夹具和一致性用例，再收紧实现。

任何新增消息类型必须先加夹具再改两端实现，禁止只改一端。

> **当前偏差（待补）**：`canvas/resource.replace.request` 与 `host/resource.replace.result` 已在 C#（`YEEYEEYEE.Core\Protocol.cs`、`YEEYEEYEE.Host\HostBridge.cs`）与 TS（`VersionedMessages.ts`、`CanvasMessageCodec.ts`、`CanvasBridge.ts`）两侧落地，并有 C# 控制台用例覆盖，但 `protocol/fixtures/` 里**还没有对应的夹具与 manifest 条目**。此外，TS 尚未复现 C# 对资源 ID、Job 状态等字段的全部细粒度校验。下一步先补两条共享夹具和跨语言断言，再决定是否统一校验深度。

## 8. Goal7 本机 Web HTTP 契约（现有实现，待独立复核）

本节只描述 `YEEYEEYEE.Web/WebSceneApi.cs`、`WebSkillJobApi.cs`、`ProjectCanvasSceneStore.cs` 当前路由；不属于上文 v1 信封及跨语言夹具。`/api/canvas/*` 和 `/ws/canvas` 是保留的桌面桥接路径，亦非 `/api/web` 的别名。所有三类路径目前由同一中间件先验证回环来源和配置的 `YEEYEEYEE:WebToken`：非回环 `403 LOCAL_ONLY`，令牌未配置 `503 TOKEN_NOT_CONFIGURED`，缺失/错误 Bearer `401 UNAUTHORIZED`。这只是一台机器的共享令牌边界，不是多用户身份体系；`/health` 和静态资源不在此中间件保护范围。成功体直接是 JSON，无 `v/id/type/ts/payload`；失败体为 `{ "code": "...", "message": "..." }`，以 HTTP 状态判定，而非 `host/error`。

| 方法与路径 | 输入 | 成功响应与约束 |
| --- | --- | --- |
| `GET /api/web/scene` | 无 | `{revision,records}`；项目模式另有 `readOnly,formatVersion,migration,validation`。records 为投影项 `{recordId,recordType,record,...}`，只读获取不迁移写盘。 |
| `PUT /api/web/records/{recordId}` | JSON `{baseRevision,title,content}` | 每次请求由服务端配置 `YEEYEEYEE:WebClaims` 重验 `canvas.edit`；仅 Bearer 不足以写入。通过后返回 `{revision,record}`；仅更新已有节点标题/内容，不改引用。独立模式 revision 为递增整数；项目模式为画布原始字节 SHA-256 前 6 字节的非负数值，保存后返回新字节对应值，不能混用两种修订。 |
| `GET /api/web/assets` | 无 | `{entities:[...]}`，读取配置的 `ProjectEntitiesPath` 中 `entities.json`；只读，当前服务端返回原实体数组，不提供创建、替换或删除端点。 |
| `GET /api/web/skills` | 无 | `{skills:[...]}`；仅当服务端配置 `preapproved-local-image`、ComfyUI checkpoint 和 `skill.invoke` 声明同时满足时列出固定 `comfyui.text-to-image`。 |
| `POST /api/web/skills/{skillId}/invoke` | 仅 `{prompt,idempotencyKey}`，非空且分别最多 4000/128 字符 | 返回任务视图；不接受客户端自选 tool/能力，键与既有调用输入冲突报 409；返回任务并不等于执行成功。 |
| `GET /api/web/jobs`、`GET /api/web/jobs/{jobId:guid}` | 无 | `{jobs:[...]}` 或单任务视图；限当前令牌派生用户及固定技能；视图含 `jobId,invocationId,state,progressPercent,errorCode,errorMessage,externalTaskId,outputs,attempt,retryOfJobId,rootJobId,tool,canRetry`。 |
| `POST /api/web/jobs/{jobId:guid}/cancel`、`/retry` | 无 | 返回任务视图；取消要求 `job.cancel` 且排队/运行，重试要求预授权与 `skill.invoke`、符合既有尝试链限制。 |

稳定 ID：项目画布 `recordId` 对应桌面节点 GUID，写入按该 ID 寻找节点；`record.references[].entityId/variantId/variantVersionId` 对应项目库的 `entities[].id/variants[].id/versions[].id`，名称仅用于显示，同名不能作键；版本 ID 存在为锁定，空值为跟随。Web 资产面板目前只读、按实体 ID 定位，不修改引用、不建常驻设定节点。独立 JSON 场景允许非空字符串 `recordId`，不能宣称所有模式均强制 GUID。项目画布在 `canvases` 目录且与 `project.json`、配置的同项目 `project/entities.json` 对应才启用；否则不回退到独立场景。独立模式须显式 `AllowStandaloneWebScene` 和绝对 `WebScenePath`。项目读取验证权威资源，保存重新检查原字节、项目库和租约并备份后替换；高版本/校验错误只读。

权限和错误反例（拒绝不得算成功）：匿名 PUT → `401 UNAUTHORIZED`，非回环 → `403 LOCAL_ONLY`；项目及独立场景的 PUT 缺少服务端 `canvas.edit` 声明 → `403 CANVAS_EDIT_FORBIDDEN`（拒绝不改盘，GET 仍只读可用）；未预授权/无 `skill.invoke` 调用或重试 → `403 SKILL_NOT_APPROVED`，无 `job.cancel` → `403 JOB_FORBIDDEN`；客户端附带 `tool` 的调用 → `400 INVALID_REQUEST`；旧 `baseRevision` → `409 SCENE_REVISION_CONFLICT`，缺失记录 → `404 RECORD_NOT_FOUND`，锁定节点 → `409 NODE_LOCKED`；未知技能/任务分别 `404 SKILL_NOT_FOUND`/`JOB_NOT_FOUND`（他人的任务同样隐藏为 404）；终态取消 → `409 JOB_NOT_CANCELLABLE`，不可重试 → `409 JOB_NOT_RETRYABLE`；项目上下文不可信 → `503 PROJECT_CANVAS_UNAVAILABLE`，项目读写不安全 → `503 PROJECT_CANVAS_READ_FAILED`/`PROJECT_CANVAS_WRITE_FAILED`，高版本 → `409 CANVAS_READ_ONLY`。独立模式还会有 `SCENE_MODE_NOT_CONFIGURED`、`SCENE_PATH_NOT_CONFIGURED`、`SCENE_INVALID`、`SCENE_READ_FAILED`/`SCENE_WRITE_FAILED`；资产端点可能返回 `ASSETS_NOT_CONFIGURED`、`ASSETS_INVALID`、`ASSETS_READ_FAILED`。写入提交后出错另有 `PROJECT_CANVAS_COMMITTED`，不能把它当成“未落盘”盲目重试。

已知边界：HTTP 项目/独立场景 PUT 已按每次请求的服务端声明重验 `canvas.edit`，但这仍是单机共享令牌与配置声明，不是多用户身份/角色体系；旧 `/api/canvas/*` 桌面桥接路径不属于此 PUT 授权结论。项目资源端点对配置路径直接读文件，未与场景读取一样验证项目归属；前端参考解析与同名/缺失单测不是浏览器交互证据。技能只有本地预授权 ComfyUI 文生图，不是通用技能审批与注册表；真实提供方成功出图、完整浏览器点击及多端并发/断电未验证。旧 WebSocket v1 与 HTTP 路径并存，不据此宣称双向实时协作已实现。
