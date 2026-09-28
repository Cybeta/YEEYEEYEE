# DreamForge 画布 ⇄ 宿主消息协议（Canvas ⇄ Host Protocol）

- 协议版本：`1`
- 状态：实现已落地（`DreamForge.Core\Protocol.cs` 校验 + `DreamForge.Host\HostBridge.cs` 桥接 + TS 侧同构实现 + 控制台用例），但**当前只有部分宿主在用它**，见下方适用范围
- 适用范围：**任何画布侧实现与其 C# 宿主之间**。当前实际使用方为 `DreamForge.Host`（测试与桥接）、`DreamForge.Web`（作业服务，并以 WebSocket 承载 `DreamForge.Canvas` 的 TS 画布）与 `DreamForge.Canvas`（TS 侧同构实现）；**桌面端 `DreamForge.Desktop` 的 WinForms 自绘画布不使用本协议**，它走 Agent JSON 协议，只用 HTTP 把投影后的 records 推给 Web（见 `02_Architecture.md` 第 4.4 节）
- 不适用范围：C# 宿主之间、服务端内部调用、Yjs 房间内部同步（协作未实现）

## 1. 设计约束

1. 画布是独立的 TypeScript/React 包，不是 Razor 组件。C# 宿主与画布之间**只能**通过本协议通信，不得假设存在共享的 Razor 画布或共享 .NET 对象图。（注：当前参考实现 `DreamForge.Canvas` 渲染的是普通 div 卡片，`tldraw` 依赖存在但未被使用，也不在解决方案内。）
2. 协议不承认画布上报的身份。画布**禁止**上报 `role`、`clientType`、`userId` 作为鉴权依据；宿主通过 `host/init` 下发由服务端会话派生的能力位（capabilities），画布只按能力位决定 UI 是否可编辑、可执行。
3. 协议版本必须精确匹配。不匹配时宿主拒绝初始化（fail-closed），不得降级为"尽力兼容"或"忽略新字段继续跑"。
4. 未知消息 `type` 一律 fail-closed：不得静默丢弃后继续处理后续消息，不得产生任何画布或数据变更。
5. 消息体为 JSON 对象，字段名小驼峰。**未知字段必须忽略**（向前兼容），但**未知 `type` 不得忽略**（见第 4 条）。
6. tldraw 记录（records）对宿主是不透明的：宿主不解析节点语义，只负责去重、修订号（revision）与转发。
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
| `canvas/op.batch` | `batchId`, `baseRevision`, `source`(`user`\|`ai`), `ops[]` | 画布本地产生的记录变更。`ops` 为不透明 tldraw 记录数组。 |
| `canvas/undo.request` | `localOnly`(必须为 `true`) | 单人本地撤销。`localOnly=false` 一律拒绝。 |
| `canvas/redo.request` | `localOnly`(必须为 `true`) | 单人本地重做。 |
| `canvas/invoke.request` | `invocation`, `idempotencyKey` | 请求执行 Skill/Tool。宿主必须按会话身份重新鉴权。 |
| `canvas/job.cancel.request` | `jobId` | 请求取消异步任务。 |
| `canvas/selection.changed` | `recordIds[]` | 选中上下文，供 AI 提示词使用；无权时宿主可忽略（当前 Web 画布还会带上 `entityId`、`entityKind`、`openResourceLibrary`，表示"选中了节点里的某个引用元素"）。 |
| `canvas/resource.replace.request` | `recordId`, `entityId`, `variantId`, `variantVersionId`(可空) | 请求替换节点引用所锁定的设定版本；`variantVersionId=null` 表示"跟随最新"。三个 ID 必须是合法 uuid，字段缺失即 `PROTOCOL_MALFORMED`。 |
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

## 5. 状态机

画布侧：`created` → （发出 `canvas/hello`）→ `handshaking` → （收到 `host/init`）→ `ready`；收到 `host/error{severity:fatal}` 或版本不匹配 → `fatal`（停止处理业务消息，仅允许重连握手）。

宿主侧：`idle` → （收到 `canvas/hello` 且版本匹配）→ `initializing` → （发出 `host/init`）→ `active` → （`host/error{fatal}` 或会话失效）→ `closed`。

## 6. 失败处理矩阵

| 场景 | 宿主行为 | 画布行为 |
| --- | --- | --- |
| 版本不匹配 | 不回 `host/init`，回 `PROTOCOL_VERSION_MISMATCH` | 进入 `fatal`，提示升级，不发送业务消息 |
| 未知 `type` | fail-closed，回 `PROTOCOL_UNKNOWN_TYPE` | 停止应用后续消息，进入 `fatal` |
| 载荷缺字段/类型错 | 回 `PROTOCOL_MALFORMED`，不做变更 | 忽略该消息并在本地记录诊断 |
| 画布越权执行 | 回 `PROTOCOL_UNAUTHORIZED`，每次执行都重新鉴权 | 回滚本地乐观 UI，禁用入口 |
| 依赖引用失败 | 回 `REFERENCE_*`，不执行、不降级 | 展示失败，不生成占位节点 |
| 修订号冲突 | 下发 `host/scene.reset` 或要求画布重取 | 丢弃本地未确认批次，重放已确认批次 |

## 7. 跨语言一致性夹具

`protocol/fixtures/` 与 `protocol/fixtures/manifest.json` 是 C# 端与 TypeScript 端的**共同事实来源**：

- C# 端 `DreamForge.Core.Tests` 与 TS 端 `vitest` 都读取 `manifest.json`；
- 对 `valid: true` 的夹具，两端编解码后必须得到相同的 `type` 与 `payload` 关键字段；
- 对 `valid: false` 的夹具，两端必须抛出/返回 `manifest` 中声明的 `expectedErrorCode`。

任何新增消息类型必须先加夹具再改两端实现，禁止只改一端。

> **当前偏差（待补）**：`canvas/resource.replace.request` 与 `host/resource.replace.result` 已在 C#（`DreamForge.Core\Protocol.cs`、`DreamForge.Host\HostBridge.cs`）与 TS（`VersionedMessages.ts`、`CanvasMessageCodec.ts`、`CanvasBridge.ts`）两侧落地，并有 C# 控制台用例覆盖，但 `protocol/fixtures/` 里**还没有对应的夹具与 manifest 条目**。下一步应补齐这两条夹具，恢复"先夹具后实现"的规则。
