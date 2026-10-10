# YEEYEEYEE

> 面向 AI 创作的 Windows 桌面工作区

[![构建与测试](https://github.com/Cybeta/YEEYEEYEE/actions/workflows/build.yml/badge.svg)](https://github.com/Cybeta/YEEYEEYEE/actions/workflows/build.yml)

YEEYEEYEE 把 **企划、章节、分镜、图片、视频和成片** 放在同一个项目里。角色、道具和场景可以作为设定被分镜引用，设定更新后会提示受影响的内容。

## 界面预览

<p align="center">
  <img src="docs/screenshots/01-start.png" alt="主界面" width="32%" />
  <img src="docs/screenshots/11-gacha-reveal.png" alt="抽卡开奖" width="32%" />
  <img src="docs/screenshots/16-gacha-graded.png" alt="出图判档" width="32%" />
</p>

## 快速开始

### 下载运行

从[最新发行版](https://github.com/Cybeta/YEEYEEYEE/releases/latest)下载 `win-x64.zip`，解压后运行 `YEEYEEYEE.Desktop.Avalonia.exe`。

需要 Windows 和 .NET 10 运行时。建议放在可写目录，应用内更新需要替换程序文件。

### 从源码运行

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet build YEEYEEYEE.slnx
dotnet run --project YEEYEEYEE.Desktop.Avalonia
```

### 配置模型

在设置页填写聊天、图像和视频服务的地址、模型名和密钥。密钥会加密保存，不写入项目文件。判图质量需要支持图片输入的模型。

也可以接入本地或远程 ComfyUI，使用它的工作流生成图片和视频。

## 核心能力

- 项目文件夹管理画布、素材、技能和工作流。
- 企划、章节、分镜和成品按阶段组织。
- 角色、道具、场景支持设定图和版本引用。
- 分镜支持批量出图、开奖展示和可选判档。
- 分镜图片可作为视频首帧，成片支持无损拼接。
- Agent 支持询问、预览和确认后执行。
- Web 端支持登录、权限和多人编辑锁。
- ComfyUI 工作流支持批量导入、转换、检查和参数绑定。

## ComfyUI

在设置页粘贴 ComfyUI 首页地址并测试连接。应用会：

1. 递归读取工作流；API Prompt 格式直接读取。
2. 网页编辑器格式由桌面 `NativeWebView` 加载目标实例的官方前端，调用 `app.loadGraphData()` 和 `app.graphToPrompt().output` 导出。
3. Windows 下默认两个 FIFO worker 共享一个 WebView2 环境，每个 worker 使用独立页面；内存条件触发时降为单 worker。
4. 导出时核对请求、worker、会话、原稿 SHA-256 和图身份，检查节点注册、资源与动态稳定性；失败或超时销毁页面。没有官方前端宿主时明确失败，不回退 Playwright 或旧学习转换规则。
5. 读取 `/object_info` 识别输入和文件槽位，生成前检查缺失输入、文件与无效选项；只绑定明确识别的参数。

Reroute、旁路、动态下拉和子图的转换交给目标实例官方前端；兼容性仍取决于该实例的节点包和资源。多参考图和中文输入名按实际槽位识别，已连接输入和固定随机节点不会被强行覆盖。ComfyUI 视频时长沿用工作流，不承诺桌面时长设置会发送到工作流。

详细判据和限制见[ComfyUI 接入说明](docs/notes-comfyui.md)。

## Web 服务

本地开发：

```powershell
npm.cmd --prefix YEEYEEYEE.Canvas install
npm.cmd --prefix YEEYEEYEE.Canvas run build
dotnet run --project YEEYEEYEE.Web
```

Docker 部署：

```powershell
docker compose up -d --build
```

默认访问 `http://localhost:8080`。首次启动创建管理员账号。公网部署前请设置初始化令牌，并配置反向代理和 HTTPS。

## 资产检查与媒体预览

分镜的角色、场景、道具需求必须分别明确为“无需求”或“必要”；未知需求、需求与引用冲突、失效引用、指定版本缺失、图片缺失和版本决策待确认会阻断生成。必要参考不能取消、过滤或裁剪，超限或来源不支持参考时必须调整配置或更换来源，不能降级为无参考生成。

图像批次保存实际请求和引用指纹，分镜每张候选提交前再次检查最终参考路径和当前指纹；采用图片保留生成时依据。视频要求引用分镜先采用有效首帧，并核对首帧路径及必要参考依据。候选图不算已采用首帧，确认需求不能给旧产物补记生成依据。

画布产物视频单击原位播放，图片和视频双击打开应用内大窗。视频预览框须完整位于可见区才允许原位播放；默认静音，播放器按需创建并只保留一个活动实例。视频封面由 FFmpeg 从视频本身提取首帧，提取失败保留播放入口，不用设定图代替。引用预览入口另走引用画廊，音频仍使用系统播放器。

## 限制

- 不同 ComfyUI 工作流的输入命名和节点能力不同，无法识别的槽位会明确提示，不会猜测。
- 图像参考上限取用户设置、已声明的池子能力和工作流单组槽位容量的最小值；必要参考超限会阻断。
- 单图视频接口使用已采用的分镜首帧，设定资产应先参与首帧生成；视频多参考能力取决于接口或工作流。
- 引用指纹记录描述、布局、附件引用、子引用和版本标签，不是图片文件内容哈希。
- 人物跨镜一致性尚未实际验收通过，历史项目资产尚未实际补齐；参考图机制和自检结果不能代替生成效果验收。

## 文档

- [ComfyUI 接入说明](docs/notes-comfyui.md)
- [参考图与设定锁定](docs/spec-参考图与设定锁定.md)
- [出图开奖与判档](docs/notes-开奖与判档.md)
- [发布说明](docs/发布说明.md)

## 验证

```powershell
dotnet run --project YEEYEEYEE.Agent.Tests -c Release
dotnet build YEEYEEYEE.slnx -c Release --no-restore
```

当前版本：`0.1.5.1`（2026-10-10），桌面项目 `<Version>` 已为 `0.1.5.1`。最新实际验证结果：Agent 340 项、Core 32 项通过；solution Release 增量构建 0 警告、0 错误。

前轮已用真实鼠标验证图片、静止视频及原位播放中的视频双击打开应用内大窗，关闭大窗后回到首帧。全库 ComfyUI 兼容性仍未验收；既有四样本记录三个成功、一个图身份校验失败，不能推广为全库通过。人物跨镜一致性与旧项目资产补齐仍未验收。

已清理 12,937 个文件，共 3.16 GiB，删除记录见 `Dream清理删除清单-20261010.csv`。没有明确死码证据时不盲删源码。本次执行本地发布打包及包验证，不操作 git，不做外部发布。
