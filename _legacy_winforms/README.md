# 旧版桌面端（WinForms）· 归档

这个目录是**旧版桌面客户端**的源码归档，**不在构建里**：不在 `DreamForge.slnx` 中，也没有任何项目引用它。
留着只是为了「万一要看当年的实现」——它已经不再编译，也不该再往里加东西。

## 为什么归档而不是直接删

第 127 轮核对过：这个目录下 66 个受版本控制的文件里，**63 个带着未提交的改动**，
也就是说它们的当前内容并不在 git 里（git 只有更早的版本）。直接删除等于把那些内容永久丢掉。
用户当时的决定是「**先归档**」——确认之后可以整个目录删掉，或直接用 git 取回历史版本。

## 它原来是什么

- `MainForm.cs` / `Theme.cs` / `ChatView.cs` / `*Dialog.cs` / `WorkflowCanvasControl.cs` 等：
  WinForms 界面（自绘主窗、画布控件、各种对话框）。
- `WorkflowEntities.cs` / `WorkflowCanvasControl.cs`（模型部分）/ `WorkTree.cs` / `AppPaths.cs` /
  `AssetStore.cs` / `CanvasLibrary.cs` / `ProjectContext.cs`：**它自己那一份**模型与工具类。

## 最后那件事：两份核心是分叉的

上面最后一行是关键。旧端这套模型与 `DreamForge.Desktop.Core` 里那套是**分叉的两份**，
而且**双向不同**——例如 `CanvasLibrary.Rename` / `FindByTitle` / `LoadCurrentCanvasPath` /
`SaveCurrentCanvasPath`、`StorageMaintenance.FindUnreferenced` 只在这一份里有；
`CanvasLibrary.Save` 在这里走的是「完整保存链」（备份 + 迁移 + 原子替换），而 Core 那份当时是
自己写 JSON 的另一条路径。

于是出现过一个看不见的错位：**测试跑的是旧端这份，应用跑的是 Core 那份**。
第 127 轮把这些差异逐条并回 Core / Shared（以旧端那份为准，因为测试与现有功能都以它为准），
然后把这份归档。

## 现在这些代码住在哪

| 原来的位置 | 现在 |
| --- | --- |
| 42 个无界面文件（Agent 服务层、密钥、出图出视频、智能导入、站点池子…） | `DreamForge.Desktop.Shared/Agent/` |
| 画布纯逻辑（布局、自检、候选图、引用过期、命令服务、导入合并、工作树同步…） | `DreamForge.Desktop.Shared/Canvas/` |
| 画布模型 / 存储 / 迁移 / 章节身份 / 回收站 / 打包（9 个文件） | `DreamForge.Desktop.Core/` |
| 存储维护（统计占用、资产引用、清理无引用图片） | `DreamForge.Desktop.Shared/Agent/StorageMaintenance.cs` |
| WinForms 界面与那份重复的模型 | 就在这里，不再编译 |
