using Avalonia.Controls;

namespace DreamForge.Desktop.Avalonia;

/// <summary>
/// 设置窗口里的一页。
///
/// 为什么要有这个契约：设置分三页（模型接入 / 生图生视频 / 技能管理），
/// 但它们对「界面上的编辑什么时候写进配置」必须有一套统一答案——
/// 各页各自保存会出现「切页丢改动」「保存按钮到底存了哪一页」这类说不清的状况。
/// 所以每页只负责三件事：①给出根控件；②在被要求时把编辑写回**内存里**的 config；
/// ③可选地重新回显（切换「当前配置」之后要把表单刷成新那一份）。
/// 真正的落盘由外壳统一做，见 <see cref="SettingsWindow"/> 的「保存」。
/// </summary>
internal sealed class SettingsPageSection
{
    /// <summary>左侧页签上的名字。</summary>
    public required string Title { get; init; }

    /// <summary>页签上的小图标（单字符，与主窗口其余图标同一套风格）。</summary>
    public required string Glyph { get; init; }

    /// <summary>页签下的一句说明，回答「这一页管什么」。</summary>
    public required string Summary { get; init; }

    public required Control Root { get; init; }

    /// <summary>把界面上的编辑写回 config（内存）。外壳在保存前会依次调用每一页的这一项。</summary>
    public required Action Commit { get; init; }

    /// <summary>从 config 重新回显。页内没有表单时可以不给。</summary>
    public Action? Reload { get; init; }
}
