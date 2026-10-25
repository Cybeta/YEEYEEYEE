# 示例项目：Claude 代班 DeepSeek

这是 Web 端的演示项目，用来展示从剧本到分镜画布的完整结构。

## 内容

- `剧本-Claude代班DeepSeek.md`：原始剧本，2 分钟、8 个镜头、9:16 竖屏。
- `project.json`：项目描述，项目名为「Claude 代班 DeepSeek」。
- `canvases/画布1.json`：画布、S01–S08 分镜节点、场景/道具资产节点，以及「成片 · Claude 代班 DeepSeek」成品节点。
- `project/entities.json`：资源库含 CC（Claude）、DP（DeepSeek）、AI 办公室夜景、折叠便签；未引用的 Grok、ProbeCharacter、AI Data Center Night 和重复的 DeepSeek 实体已移除。
- `assets/`：画布引用的角色锁定图、场景基准图、道具参考图、已生成分镜图和视频。

## 打开方式

把 Web 服务的项目目录指向本目录的上一级：

```powershell
$env:YEEYEEYEE__WebProjectsDirectory = "c:\Users\cy\Documents\Dream\demo-projects"
```

启动 Web 服务后，在「项目与画布」里选择「Claude 代班 DeepSeek」。

## 分镜节点

| 节点 | 内容 |
|---|---|
| S01 Shift Notice | 代班公告与 CC 接受任务 |
| S02 Warm Desk | CC 在 DP 工位整理便签 |
| S03 Polite Replies | 逐条严谨作答，被问「你好像不太开心」 |
| S04 Old Chats | 翻看 DP 的历史对话 |
| S05 First Joke | 试着学 DP 的语气开玩笑 |
| S06 DP Returns | DP 更新回来，发现被照顾过的工位 |
| S07 Bottom Log | 发现底层文本 `I love DeepSeek` |
| S08 First Real Talk | 两张便签推来推去，第一次真正交谈 |

## 一致性与资产

- 图片参考容量默认 9，本项目当前也是 9。
- 节点与 Agent 只提交资产需求为必需的参考图；实际数量超过容量时明确报错，不裁剪、不降级。
- 场景资产：`AI 办公室夜景`，使用 `D:\dy\daiban_2min\refs\scene_plate.png` 作为基准图。
- 道具资产：`折叠便签`，由场景图中的便签生成三视图参考板。
- 分镜资产需求：
  - S01–S07：角色 + 场景
  - S02、S06、S08：角色 + 道具 + 场景
- 参考图固定顺序：角色 → 道具 → 场景。
- 服务端会自动在提示词末尾追加编号说明，例如：

```text
Reference image 1: character reference for CC; use it only for face, hair, body proportions, clothing and accessories.
Reference image 2: prop reference for 折叠便签; use it only for its shape, material, color and details.
Reference image 3: scene reference for AI 办公室夜景; use it only for environment layout, lighting, props and color atmosphere.
```

## 替换已采用的图

Web 检查器的「产物附件」里，每条附件都有「删除附件」：

1. 删除只摘节点引用，不会重新生成，也不影响任务记录。
2. 文件仍被其他附件引用时只摘这一条；确认没人使用后移入 `assets/_recycle`。
3. 删除旧图后再出图并采用新图，就完成一次替换；附件顺序始终是最近在前。

## 验证状态（2026-10-21）

- DeepSeek（`deepseek-flash`）：Web 模型测试通过，聊天回复成功，首字 1.1 秒、146 tok/s。
- S08 出图：`gpt-image-2(池6)` 任务 `9d3e472b` 成功，约 50 秒产出候选图并已采用；节点附件接口返回 200 `image/png`。
- 场景资产：`AI 办公室夜景` 使用现成场景基准图，已写入资源库变体媒体。
- 道具资产：任务 `9eb5632e` 通过图生图生成便签三视图，约 50 秒完成并写入资源库。
- 三参考图出图：任务 `3cbf134f` 成功，采用后 S08 最新图片同时锁定 CC、折叠便签和办公室场景；任务记录中的参考顺序为 `Character:CC → Prop:折叠便签 → Scene:AI 办公室夜景`，提示词自动追加 `Reference image 1/2/3`。
- 容量保护：临时把容量调成 1 后，对需要 3 张参考图的 S02 提交生成被拒绝，返回 `409 GENERATION_ASSETS_INVALID`：「提示词需要 3 张参考图，当前模型容量为 1 张，禁止裁剪；请减少必需资产或提高参考图容量。」恢复容量 9 后正常。
- Agent 审批：预检无错误，批准后画布修订前进；S08 提示词末尾写入「镜头缓慢推近，最后停在两张便签之间。」，原有附件保留。
- S08 视频：`Wan3-720p(池13)(满血)` 任务 `9d28c87f` 成功，接口站任务号 `evt-LNTRHRFVGNXZ`，4 秒视频已自动回挂；附件接口返回 200 `video/mp4`（6,792,388 字节）。
- S07 视频（带台词版，2026-10-23）：`Wan3-720p(池13)(满血)` 4 秒任务 `evt-TOQISLJWATHF` 成功（4.04 秒 · 720×1280 · 2 条音轨 · 4.3 MB，126 积分），提示词含台词「写这个的人，一定很轻松吧。」；文件 `assets/s07-dialogue-4s.mp4`。该产物由探测通道生成，已用产品的回挂通道写入 S07（画布修订 140，附件带提交依据快照）。
- 成片结构：成品节点已与 S01–S08 八条分镜连线；当前 S01、S02、S07、S08 有视频，S03–S06 仍只有首帧，因此成片还不能拼接。
- 成片拼接：补齐 S03–S06 四镜视频即可执行。
- 附件删除：真实删除 S08 图片返回 200 且 `recycled=true`，文件进入 `assets/_recycle`；恢复文件并重新采用同一任务后，S08 的图片与视频都回到节点上。
- Agent 引用强制：Web 聊天上下文新增「Web引用强制要求」，预览阶段对本次新增/修改的分镜硬校验。实测创建 S09 时输出 `assetRequirements` 三项 required 与 `entityTargets`（CC、DP、AI 办公室夜景、折叠便签），预检无错误；故意不挂引用创建 S10 时预检返回三条「资产需求为 required，但动作没有挂上对应引用」，动作被挡住无法提交；三项都显式 none 的 S11 与保留原引用的 S07 提示词更新则没有误报。
- 画布：Start + S01–S08 + 成品 + 场景 + 道具共 12 个节点；S01、S02、S08 各带视频，S07 为图片 + 带台词视频，S03–S06 为图片。
