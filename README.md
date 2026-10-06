# YEEYEEYEE

> 面向 AI 创作的 Windows 桌面工作区

[![构建与测试](https://github.com/Cybeta/YEEYEEYEE/actions/workflows/build.yml/badge.svg)](https://github.com/Cybeta/YEEYEEYEE/actions/workflows/build.yml)

YEEYEEYEE 把 **企划、章节、分镜、图片、视频和成片** 放在同一个项目里。角色、道具和场景可以作为设定被分镜引用，设定更新后会提示受影响的内容。

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

1. 递归读取工作流。
2. 将网页编辑器格式转换为 API Prompt 格式。
3. 读取 `/object_info`，识别节点输入和文件槽位。
4. 导入前检查缺失输入、缺失文件、静音或绕过节点。
5. 生成时按工作流实际槽位写入提示词、比例、时长、种子和参考素材。

支持 Reroute、旁路节点、动态下拉、子图、多参考图，以及中文输入名。已连接的输入和固定随机节点不会被强行覆盖。

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

## 限制

- 不同 ComfyUI 工作流的输入命名和节点能力不同，无法识别的槽位会明确提示，不会猜测。
- 参考图数量受用户设置、服务能力和工作流槽位共同限制。
- 视频接口是否支持多参考图取决于接口本身。
- 参考图用于提高一致性，不保证像素级相同。

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

当前 Agent 测试共 318 项，解决方案构建无错误。当前版本：`0.1.4.1`。
