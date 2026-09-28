# YEEYEEYEE

> 当前基线：2026-09-29

YEEYEEYEE 寓意 YES 工程师 · YES 艺术家。工程、程序集和协议标识仍为 `DreamForge`。当前主端是 Windows WinForms 自绘无限画布，支持文本、图像生成与项目文件管理；Web 提供作业服务、桌面画布镜像及引用版本回写，尚不能独立完成创作。

产品主线固定为：企划 → 章节 → 分镜 → 成品。角色、道具、场景不建常驻画布节点；分镜通过 `References`/`entityTargets` 引用设定，用户需要查看时再临时展开可视化。`ParentNodeId` 只负责画布布局，`WorkTreeItemId` 是叙事锚点，`References` 是视觉引用，三者不能互相替代。

工作树和资源库同时保留。当前 `Entities` 仍随画布保存，目标是项目级资源库；迁移前必须完成稳定 ID、引用扫描、删除保护、回收站和版本锁定。Web 当前只负责桌面状态的镜像展示，以及引用版本的替换/锁定请求回写。

## 阅读顺序

| 顺序 | 文档 | 职责 |
|---|---|---|
| 入口 | [README](README.md) | 启动、当前边界、故障案例 |
| 01 | [产品决策](01_Project_Plan.md) | 产品目标、两轴分工与待实现方案 |
| 02 | [架构](02_Architecture.md) | 源码事实、架构图、数据与迁移边界 |
| 03 | [技能](03_Skill_System.md) | 生成技能、角色能力与插件 |
| 进度 | [PROGRESS](PROGRESS.md) | 按依赖推进的小目标、验收和验证基线 |
| 协议 | [protocol/PROTOCOL](protocol/PROTOCOL.md) | Canvas ⇄ Host v1 消息契约与实现缺口 |

## 当前可用与尚缺能力

| 范围 | 核实后的状态 |
|---|---|
| 桌面画布 | 节点、连线、拖动、缩放平移、章节网格分块、Agent 虚影预览已存在 |
| Agent | Ask / AutoStage / ReadOnly；13 种 action；审批、保存与有条件的提交快照回滚 |
| 两轴数据 | 工作树保存叙事条目；资源库（界面称设定库）保存实体、变体和视觉版本；两者均保留 |
| 文本与图像 | OpenAI 兼容、Anthropic 文本/多模态调用；OpenAI 兼容图像与 ComfyUI 图像链路、任务持久化 |
| Web | React/Vite div 卡片；桌面推全量投影，浏览器可请求换/锁引用版本；缺独立创作、资产 HTTP 端点与完整交互联动 |
| 新方案 | 企划→章节→分镜→成品、章节泳道、保留手动位置的局部布局、资源临时展开、自动同步及项目级资源迁移均待实现 |
| 其他 | 视频生成、账号、房间协同、配额和审计未实现；MCP 为独立只读服务，未接入桌面端 |

现有 `Entities` 随每张画布保存，并非项目级共享资源库。已有 `SourceEntityId`、`WorkTreeItemId` 等字段也不等于自动同步已经完成。具体边界以 [架构](02_Architecture.md) 为准。

## 快速开始

需要 Windows、.NET 10 SDK；Web 前端另需支持当前 Vite 工具链的 Node.js/npm。在仓库根目录执行：

```powershell
dotnet build DreamForge.slnx
.\DreamForge.Desktop\bin\Debug\net10.0-windows\DreamForge.Desktop.exe
```

首次启动选择可写项目目录，在设置中配置 Endpoint、Model 与 ApiKey，再使用设定库、工作树和 Agent。模型密钥以 Windows 当前用户 DPAPI 密文存入程序目录的 `ai-config.json`。

可选 Web 镜像，先构建前端再启动 Web，随后打开桌面项目：

```powershell
npm.cmd --prefix DreamForge.Canvas install
npm.cmd --prefix DreamForge.Canvas run build
dotnet run --project DreamForge.Web
```

浏览器访问 `http://localhost:5000`。桌面端使用该硬编码地址推送 `/api/canvas/scene`，并每 500ms 轮询引用替换请求；Web 未启动时推送失败不阻断桌面使用。前端 `dist` 需单独构建，未纳入解决方案自动构建。

## 验证与已修复项

2026-09-29 最终验证为 Agent 48 项、Core 19 项、TS 24 项测试全部通过；解决方案构建、前端 TypeScript 检查与 Vite 构建、`git diff --check` 均通过，命令与范围见 [PROGRESS](PROGRESS.md)。

- `AutoStage` 已改为准备动作→预览暂存→统一保存，避免同批重复执行。
- `MarkVersionAdopted` 已解析；仅 JSON 布尔 `true` 开启采纳标记，已有回归用例。
- 旧契约草稿、Core/Host 空类、tldraw 探针、类型声明及依赖已从当前工作树移除。原有代码改动尚在工作区，本次文档整理不改动它们。

## 故障案例 新建项目被拒绝访问

现象为 `项目创建失败：Access to the path '<项目父目录>\<项目名>' is denied.`。2026-09-28 的历史排查确认，该次实例由 IDE 沙箱启动，目标目录不在允许范围；进程链为 IDE→shell→沙箱宿主→shell→dotnet→Desktop。

原记录中的证据：目标目录 ACL 允许修改、无 Deny ACE，无只读/重解析/加密标记；磁盘为 NTFS 且健康，受控文件夹访问关闭。同盘此前有成功创建的项目；沙箱内同路径写删均被拒绝，工作区内的 `Directory.CreateDirectory` 成功。沙箱允许清单的 104 条路径均在系统盘，没有目标盘路径。这些是历史现场记录，本轮未重新采集。

源码中已保留两项修正：`AppPaths.CanWriteDirectory` 实际创建目录、写入并删除探针；`ProjectStartupForm` 在失败时提示权限或安全策略拦截。该次故障可通过资源管理器直接启动、选择允许写入的目录，或调整 IDE 允许列表绕过。

同一错误也可能来自普通目录权限或安全软件，不能把所有拒绝访问都断言为沙箱问题。排查时先确认启动方式及目标目录是否可写，再检查 ACL 与系统策略。

## 当前按依赖推进的小目标

详细前置、产出和验收见 [PROGRESS](PROGRESS.md)。顺序固定为：0 清理基线；1 稳定 ID 与模型；2 同步引擎；3 章节泳道局部布局；4 引用交互与资源保护；5 AI 全流程；6 项目级资源库；7 Web 创作；8 多端协同。

## 文档维护

现状写在架构，产品决定写在 01，任务状态与验收只在 PROGRESS 维护。新增设计必须标记待实现；旧开发轮次由 Git 追溯。第三方 `node_modules` 文档不在自有文档整理范围内。
