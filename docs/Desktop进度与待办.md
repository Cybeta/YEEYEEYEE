# Desktop 进度与待办

对应版本：`0.1.5.3`；核对日期：2026-10-16。本次仅同步文档，未重新构建或测试。版本唯一来源为 `YEEYEEYEE.Desktop.Avalonia.csproj` 的 `<Version>`，界面通过 `AppVersion` 读取。Web 状态见 [Web 进度与待办](Web进度与待办.md)。

## 已实现

| 范围 | 当前能力 | 主要源码 |
| --- | --- | --- |
| 创作项目 | 项目、画布、企划、章节、分镜和工作树；章节同步解除悬空锚点需明确确认 | `ProjectLibrary.cs`、`CanvasStorage.cs`、`CanvasWorkTreeSync.cs` |
| Agent 与设置 | 模型选择、聊天/思考流、动作预览与审批、技能管理；密钥按平台保存 | `AgentPanel.axaml.cs`、`SettingsModelPage.cs`、`AgentActions.cs` |
| 参考锁定 | 资产需求三态、版本引用、必要参考硬检查、生成依据快照及采用首帧校验 | `GenerationAudit.cs`、`ReferenceStaleness.cs`、`OneClickRun.cs` |
| ComfyUI | API 格式读取；桌面官方前端 UI 导出，共享 WebView2 环境、双 FIFO worker 与内存降级，参数绑定及体检 | `ComfyUiNativeWebViewExporter.cs`、`ComfyUi.Export.js`、`ComfyUiLibrary.cs` |
| 媒体查看 | 原位视频播放、图片/视频大窗、按需释放；独立串行 WebView 截封面和有界缓存 | `LocalVideoPlayer.cs`、`VideoThumbnailService.cs`、`CanvasSurface.axaml.cs` |
| MP4 成片 | 按样本搬运无损拼接；样本边界剪头、ctts 编辑原点及轨道时长处理；不安全输入拒绝且不写输出 | `Mp4Concatenator.cs` |

封面短于 1 秒取中点，其余取 0.5 秒；保留比例且不超过 640×360。截图宿主空闲 5 秒释放，缓存限制 256 MiB / 1024 项 / 30 天。当前打包不再需要 FFmpeg；历史 0.1.5.1 包的依赖不据此改写。

## 自动测试与构建

2026-10-11 历史批次：solution Release 还原构建成功，**0 警告、0 错误**；Agent **343 项**、Core **32 项**通过；Migration **8/8 场景**通过，G6V1 **40 项检查**通过；Web 控制台回归完成认证、租约、SSE、项目/多画布、资源、聊天、附件、成片、设置及工作流本地 HTTP 检查。首次 `--no-restore` 因缺少 assets 失败，随后 `--ignore-failed-sources` 还原构建解决；该批次零警告是当时构建输出，不宣称完整 Rebuild 警告清零。

前端 **163 项 / 19 文件通过**，类型检查、Vite 打包和设计令牌检查通过。独立目录 `artifacts/publish-0153-prepare` 的 Windows x64 framework-dependent publish 成功，主程序文件版本 `0.1.5.3`，未发现 PDB 或 FFmpeg；未覆盖旧 publish 和 ZIP，正式发行包应在发布提交后重建。构建通过不代表 Windows WebView、真实生成、更新或音频播放验收。

## 历史实机证据与边界

2026-10-10 的鼠标记录涵盖正式应用图片、静止视频、播放中视频双击打开唯一大窗，关闭恢复封面及退出后文件可独占打开。封面候选另有真实解码、0.4 秒视频中点、宿主释放和缓存验证。这些是旧候选证据，本轮未重新操作界面。

MP4 旧候选已有真实两镜 normal/reverse 独立解码及隔离 WebView 播放证据；六段 repeat6 为 1446 帧、57.84 秒。整体仍为部分通过：五接缝 9.64 / 19.28 / 28.92 / 38.56 / 48.20 秒、人工听感、主观同步和 AAC 尾包实际裁切未完成。最近同步采集在默认输出端点 `GetDefaultAudioEndpoint(eRender,eConsole)` 返回 `0x80070490`，发生在开始采集之前，没有新播放、音频包或媒体时间遥测；退出码 0 不表示通过。本地验收目录及交接记录保留，不作为发行资产。

## 实机待验收

- [ ] 0.1.5.3 ZIP 解压启动、版本显示、打开项目、正常退出与视频文件解锁；应用内下载/替换/重启更新。
- [ ] 0.5～1 秒可区分画面样本、不同编码/损坏视频、截图失败和超时，核对封面及释放。
- [ ] 恢复可用默认音频端点，以播放器 currentTime 配对 loopback QPC，从零播放完整 repeat6；确认 ended、五接缝和尾包覆盖，并人工听音及观察同步。
- [ ] 在目标 ComfyUI 实例逐份核对官方 UI 导出、节点包/资源和 API 输出，再真实提交并检查 prompt_id、node_errors、history 与下载媒体。
- [ ] 逐镜补齐旧项目需求、引用版本和设定图，重新生成并采用有效首帧；实际验收人物、服装、场景、道具一致性。

## 未完成与限制

- [ ] 全库 ComfyUI 兼容性与所有编码的 MP4 支持尚未成立；不宣称任意 MP4 可拼接。version 1、复杂编辑表及非同步首样本受支持边界约束。
- [ ] 指纹不是图片字节哈希，同路径替换图片不能保证检出；生成效果仍需人工判断。
- [ ] 非 Windows 桌面与官方前端宿主未实机验收；当前发行目标是 Windows x64。
- [ ] 历史项目资产实际补齐和跨镜一致性未完成。

## 发布准备

现有 `tools/publish-release.ps1` 默认读取产品版本，生成 framework-dependent Windows x64 ZIP；需 .NET 10 运行时和 WebView2 Runtime。脚本排除 PDB、项目数据和 FFmpeg；发布前仍需检查包内容、程序集版本与哈希。CI 分别执行 Windows 构建/五个控制台 runner、Node 22 前端测试/构建和 Linux Docker 构建，没有自动 Release 上传任务。源码已提交并推送；正式包仍需在确定发布提交后构建，确保程序集提交标识对应发布提交。本次未核验 GitHub tag、Release 或安装包资产。
