// 本文件由 DreamForge.Desktop/Theme.cs 抽出：只保留与界面技术无关的主题枚举，
// 供 AI 配置等共享代码使用（WinForms 自绘用的 Theme 类仍在 Theme.cs，依赖 System.Drawing）。

namespace DreamForge.Desktop;

/// <summary>界面主题。默认深色，可在标题栏一键切换。</summary>
public enum AppTheme { Dark, Light }
