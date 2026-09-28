# Debug Session: new-project-failure

Status: [RESOLVED]

## Symptom
新建项目失败，报错 `项目创建失败：Access to the path '<项目父目录>\<项目名>' is denied.`
（用户输入的 `＼`、`（` 为中文输入法全角转写，实际路径为 ASCII 字符。）

## Root Cause
应用实例是被**外部 IDE 的沙箱**拉起的，沙箱的文件白名单不包含用户选择的项目父目录，写入被拦截。

进程链（采集于 2026-09-28 06:35）：

```
IDE 主进程 → shell → 沙箱宿主进程 → shell → dotnet.exe → DreamForge.Desktop.exe
```

沙箱的权限清单共 104 条，全部位于系统盘（工作区与各类工具链缓存），**没有任何其它盘符的路径**。
沙箱内对该目录的写/删返回 `Access to the path '...' is denied.`（`UnauthorizedAccessException`），
由 `ProjectContext.Create` 的 `Directory.CreateDirectory(root)` 抛出，冒泡到 `ProjectStartupForm` 的 catch 后原样显示。

## Evidence
- 目标目录 ACL 正常：`NT AUTHORITY\Authenticated Users : Modify (继承)`，无 Deny ACE；目录无 ReadOnly、无 ReparsePoint；未加密。
- 该卷：NTFS / Healthy / OK；`EnableControlledFolderAccess = 0`（受控文件夹访问关闭）；仅 Windows Defender，无第三方杀软。
- 该盘上另有同一应用此前成功创建的项目（含 `project.json`、`canvases`、`assets`、`skills`、`plugins`），
  说明应用写入该盘本身没有问题，问题在"由沙箱启动"这一上下文。
- 复现：在同一环境对同一路径执行删除，得到与用户完全一致的 `Access to the path '...' is denied.`；
  代理文件工具直接返回 `Edit operations are restricted to the working directory.`
- `.NET` 层同一 API（`Directory.CreateDirectory`）在工作区白名单内调用成功。

## Changes
1. `AppPaths.CanWriteDirectory`：原先只做 `Directory.Exists`（与命名/调用语义相反），使 `GetWritableProjectsRoot()`
   可能返回不存在或不可写的默认位置，用户直接点"创建并进入项目"会先撞上"请选择一个已存在且可访问的项目存放文件夹"。
   改为真实写入探测（建目录 + 写探针文件 + 删除）。
2. `ProjectStartupForm`：创建失败提示追加可操作说明（权限不足 / 被沙箱或安全策略拦截时提示更换位置）。
3. 已知外部绕行方式：项目位置放在工作区（白名单）内；或直接在资源管理器中双击 exe 启动（不经沙箱）；
   或在 IDE 设置中把目标路径加入允许列表。
