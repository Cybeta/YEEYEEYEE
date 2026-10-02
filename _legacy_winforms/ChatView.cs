using System.Drawing.Drawing2D;

namespace DreamForge.Desktop;

/// <summary>对话条目的性质，决定卡片的配色与强调色。</summary>
public enum ChatRole
{
    /// <summary>系统提示：环境状态、忽略提议之类的说明。</summary>
    System,

    /// <summary>用户自己说的话。</summary>
    User,

    /// <summary>模型（或本地模拟）的回答。</summary>
    Assistant,

    /// <summary>出错。</summary>
    Error
}

/// <summary>对话区里的一条消息。正文与思考分开存，思考可以折叠。</summary>
public sealed class ChatEntry
{
    public string Speaker { get; init; } = "系统";
    public Color Accent { get; init; } = Theme.TextMuted;
    public ChatRole Role { get; init; } = ChatRole.System;

    public string Body { get; set; } = string.Empty;

    /// <summary>模型的思考过程**全文**：折叠时只显示一行入口，展开就地读到全文。</summary>
    public string Thinking { get; set; } = string.Empty;
    public bool ThinkingExpanded { get; set; }

    /// <summary>正在流式接收：标题右侧显示「生成中」，正文尾部带一个光标块。</summary>
    public bool Streaming { get; set; }

    /// <summary>收尾说明（「已停止生成」「生成失败」之类），画在正文下方一行小字。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>附加提示（例如「已生成 3 条改动建议」）。</summary>
    public string Badge { get; set; } = string.Empty;
}

/// <summary>
/// 对话区：**自绘的消息列表**，不用 RichTextBox。
///
/// 为什么不用 RichTextBox（这是上一轮留下的欠账，文件里原本就写着"真要 inline 展开得改成结构化消息 + 整体重绘"）：
/// 1. 它没法做卡片、角色标识、折叠区，只能把内容当一整段纯文本追加，看起来就是"一个白框里堆字"；
/// 2. 它的颜色格式要在有句柄之后才生效，面板又是启动后才建的，于是"黑底黑字"；
/// 3. 折叠只能靠字符下标反查位置，脆弱且做不了真正的展开。
///
/// 折行由本类自己算（<see cref="Wrap"/>），不用 TextRenderer 的 WordBreak：
/// 只有自己知道每一行从哪个字符开始，才能把"点在某处"换算成"第几个字"，
/// 选中高亮也才能和绘制严格对齐。
/// </summary>
public sealed class ChatView : Control
{
    private readonly List<ChatEntry> entries = new();
    private readonly List<Row> rows = new();
    private readonly Font speakerFont = new(Theme.SmallFont.FontFamily, Theme.SmallFont.Size, FontStyle.Bold);
    private readonly ContextMenuStrip menu = new();

    private int contentHeight;
    private int scroll;
    private bool draggingThumb;
    private bool selecting;
    private ChatEntry? hovered;
    private Rectangle? hoveredToggle;
    private ChatEntry? rightClicked;

    /// <summary>选区两端。用（条目, 区域, 字符下标）表示，排序后取中间那段。</summary>
    private Position? selectionAnchor;
    private Position? selectionCaret;

    /// <summary>上一次量测用的卡片宽度。宽度一变就得整体重量，见 RebuildFrom。</summary>
    private int measuredCardWidth = -1;

    /// <summary>可被选中的文本区域。</summary>
    private enum TextBlock { Body, Thinking }

    /// <summary>选区里的一个位置：哪条消息、哪个文本块、块内第几个字符。</summary>
    private readonly record struct Position(ChatEntry Entry, TextBlock Block, int Offset);

    public ChatView()
    {
        BackColor = Theme.FieldBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        menu.Opening += (_, _) => BuildMenu();
    }

    /// <summary>一条消息的绘制度量。带 Rect 的字段都是**卡片内坐标**（左上角为原点）。</summary>
    private sealed class Row
    {
        public ChatEntry Entry = null!;
        public int Top;
        public int Height;
        public Rectangle Card;
        public Rectangle Toggle;
        public Rectangle ThinkingBox;
        public bool HasToggle;
        public List<TextLine> BodyLines = new();
        public int BodyTop;
        public List<TextLine> ThinkingLines = new();
        public int ThinkingTop;
        public int LineHeight;
        public int ThinkingLineHeight;
    }

    /// <summary>折行后的一行：文本、它在原文里的起始字符下标、实测宽度。</summary>
    private sealed record TextLine(string Text, int Start, int Width);

    // ---------- 对外接口 ----------

    public ChatEntry Add(ChatEntry entry)
    {
        entries.Add(entry);
        RebuildFrom(rows.Count);
        ScrollToEnd();
        Invalidate();
        return entry;
    }

    /// <summary>往最后一条追加正文（流式）。</summary>
    public void AppendBody(ChatEntry entry, string text)
    {
        if (text.Length == 0) return;
        var atEnd = IsAtEnd();
        entry.Body += text;
        RebuildFrom(Math.Max(0, rows.Count - 1));
        if (atEnd) ScrollToEnd();
        Invalidate();
    }

    public void AppendThinking(ChatEntry entry, string text)
    {
        if (text.Length == 0) return;
        var atEnd = IsAtEnd();
        entry.Thinking += text;
        RebuildFrom(Math.Max(0, rows.Count - 1));
        if (atEnd) ScrollToEnd();
        Invalidate();
    }

    /// <summary>条目内容变化（收尾说明、流式状态）后重排并重绘。名字避开 Control.Refresh，语义也更准。</summary>
    public void Relayout()
    {
        var atEnd = IsAtEnd();
        Rebuild();
        if (atEnd) ScrollToEnd();
        Invalidate();
    }

    public void Clear()
    {
        entries.Clear();
        rows.Clear();
        contentHeight = 0;
        scroll = 0;
        ClearSelection();
        Invalidate();
    }

    /// <summary>滚轮路由：焦点在输入框里时，滚轮事件会冒泡到这里，按光标位置决定要不要滚对话区。</summary>
    public void ScrollByLines(int lines)
    {
        var max = Math.Max(0, contentHeight - ClientSize.Height);
        if (max == 0) return;
        scroll = Math.Clamp(scroll + lines * Px(56), 0, max);
        Invalidate();
    }

    /// <summary>用于判断"追加前是否已经贴底"：用户翻上去看历史时不要把他拽回底部。</summary>
    public bool IsAtEnd() => scroll >= Math.Max(0, contentHeight - ClientSize.Height) - Px(24);

    public void ScrollToEnd()
    {
        var max = Math.Max(0, contentHeight - ClientSize.Height);
        if (scroll == max) return;
        scroll = max;
        Invalidate();
    }

    // ---------- 折行与度量 ----------

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Rebuild();
        ScrollToEnd();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Rebuild();
    }

    /// <summary>设计时像素 → 当前 DPI 实际像素。自绘的间距必须跟着缩放走，否则高分屏上会挤成一团。</summary>
    private int Px(int value) => Dpi.Scale(this, value);

    private int CardWidth => Math.Max(Px(80), ClientSize.Width - Px(10) * 2 - (NeedsScrollBar ? Px(10) : 0));

    private bool NeedsScrollBar => contentHeight > ClientSize.Height;

    private static int TextWidth(string text, Font font) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), Flags).Width;

    /// <summary>
    /// 按像素宽度折行。贪心 + 二分：先二分出这一行最多能放下多少字，
    /// 若断点落在一个英文单词中间，就退回上一个空格——中文没有空格，按字断即可。
    /// </summary>
    private static List<TextLine> Wrap(string text, Font font, int maxWidth)
    {
        var lines = new List<TextLine>();
        if (text.Length == 0 || maxWidth <= 0) return lines;

        var index = 0;
        while (index < text.Length)
        {
            // 段内显式换行优先
            var newline = text.IndexOf('\n', index);
            var paragraphEnd = newline < 0 ? text.Length : newline;

            if (paragraphEnd == index)
            {
                lines.Add(new TextLine(string.Empty, index, 0));
                index = paragraphEnd + 1;
                continue;
            }

            while (index < paragraphEnd)
            {
                var low = index + 1;
                var high = paragraphEnd;
                var fit = index + 1;
                while (low <= high)
                {
                    var mid = (low + high) / 2;
                    if (TextWidth(text[index..mid], font) <= maxWidth) { fit = mid; low = mid + 1; }
                    else high = mid - 1;
                }

                // 断在英文单词中间时回退到上一个空格（只在中段回退，避免把整行让出去）
                if (fit < paragraphEnd && fit - index > 1)
                {
                    var space = text.LastIndexOf(' ', fit - 1, fit - index);
                    if (space > index + (fit - index) / 2) fit = space + 1;
                }

                lines.Add(new TextLine(text[index..fit], index, TextWidth(text[index..fit], font)));
                index = fit;
            }
            if (newline >= 0) index = newline + 1;
        }
        return lines;
    }

    private void Rebuild() => RebuildFrom(0);

    /// <summary>
    /// 从第 <paramref name="from"/> 条起重新度量。
    /// 流式时只有最后一条在变，全量重量会把没变的几十条也跟着量一遍。
    /// </summary>
    private void RebuildFrom(int from)
    {
        if (ClientSize.Width <= 0) { contentHeight = 0; rows.Clear(); return; }
        // 卡片宽度变了（面板缩放、或滚动条出现/消失）就必须整体重量：
        // 旧行是按旧宽度折行的，只补量新行会让两段文字的折行宽度不一致。
        var cardWidth = CardWidth;
        if (cardWidth != measuredCardWidth) { rows.Clear(); from = 0; measuredCardWidth = cardWidth; }
        if (from <= 0 || from > rows.Count) { rows.Clear(); from = 0; }
        else rows.RemoveRange(from, rows.Count - from);

        var top = rows.Count > 0 ? rows[^1].Top + rows[^1].Height : Px(8);
        for (var index = from; index < entries.Count; index++)
        {
            var row = Measure(entries[index], top);
            rows.Add(row);
            top += row.Height;
        }
        contentHeight = top + Px(4);
        scroll = Math.Clamp(scroll, 0, Math.Max(0, contentHeight - ClientSize.Height));
    }

    private Row Measure(ChatEntry entry, int top)
    {
        var cardWidth = CardWidth;
        var inner = cardWidth - Px(28);
        var textX = Px(14);
        var cursor = Px(9);

        var row = new Row { Entry = entry, Top = top, LineHeight = TextHeight(Theme.UiFont), ThinkingLineHeight = TextHeight(Theme.MonoFont) };
        cursor += TextHeight(speakerFont) + Px(6);

        if (entry.Thinking.Length > 0)
        {
            var toggleHeight = TextHeight(Theme.SmallFont) + Px(6);
            row.HasToggle = true;
            row.Toggle = new Rectangle(textX, cursor, inner, toggleHeight);
            cursor += toggleHeight + Px(4);
            if (entry.ThinkingExpanded)
            {
                row.ThinkingLines = Wrap(entry.Thinking, Theme.MonoFont, inner - Px(16));
                var boxHeight = row.ThinkingLines.Count * row.ThinkingLineHeight + Px(16);
                row.ThinkingBox = new Rectangle(textX, cursor, inner, boxHeight);
                row.ThinkingTop = cursor + Px(8);
                cursor += boxHeight + Px(8);
            }
        }

        var body = entry.Body.Length > 0 ? entry.Body : (entry.Streaming ? "…" : string.Empty);
        if (body.Length > 0)
        {
            row.BodyLines = Wrap(body, Theme.UiFont, inner);
            row.BodyTop = cursor;
            cursor += row.BodyLines.Count * row.LineHeight + Px(6);
        }
        if (entry.Badge.Length > 0) cursor += Wrap(entry.Badge, Theme.SmallFont, inner).Count * TextHeight(Theme.SmallFont) + Px(4);
        if (entry.Note.Length > 0) cursor += Wrap(entry.Note, Theme.SmallFont, inner).Count * TextHeight(Theme.SmallFont) + Px(4);

        cursor += Px(9);
        row.Height = cursor + Px(8);   // 8 = 卡片之间的间距
        row.Card = new Rectangle(Px(10), top, cardWidth, cursor);
        return row;
    }

    private static int TextHeight(Font font) =>
        TextRenderer.MeasureText("测", font, new Size(int.MaxValue, int.MaxValue), Flags).Height;

    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix;

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Theme.FieldBg)) graphics.FillRectangle(back, ClientRectangle);

        foreach (var row in rows)
        {
            var top = row.Top - scroll;
            if (top + row.Height - Px(8) < 0 || top > ClientSize.Height) continue;
            DrawRow(graphics, row, top);
        }
        DrawScrollBar(graphics);
    }

    private void DrawRow(Graphics graphics, Row row, int top)
    {
        var entry = row.Entry;
        var card = new Rectangle(row.Card.X, top, row.Card.Width, row.Card.Height);
        var radius = Px(8);

        using (var path = RailIconPainter.RoundedRect(card, radius))
        {
            using var fill = new SolidBrush(CardFill(entry));
            graphics.FillPath(fill, path);
            using var border = new Pen(Theme.Border);
            graphics.DrawPath(border, path);
        }
        // 左侧强调条：一眼区分"谁在说话"，比只靠标题文字清楚。
        using (var accent = new SolidBrush(entry.Accent))
        using (var path = RailIconPainter.RoundedRect(new Rectangle(card.X + Px(1), card.Y + Px(8), Px(3), Math.Max(Px(6), card.Height - Px(16))), Px(2)))
            graphics.FillPath(accent, path);
        if (ReferenceEquals(entry, hovered))
        {
            using var path = RailIconPainter.RoundedRect(card, radius);
            using var hover = new SolidBrush(Color.FromArgb(18, Theme.Text));
            graphics.FillPath(hover, path);
        }

        var inner = card.Width - Px(28);
        var x = card.X + Px(14);
        var cursor = card.Y + Px(9);

        // 标题行：发言者 + 右侧状态
        var headerHeight = TextHeight(speakerFont);
        TextRenderer.DrawText(graphics, entry.Speaker, speakerFont, new Point(x, cursor), entry.Accent, Flags);
        if (entry.Streaming)
            TextRenderer.DrawText(graphics, "生成中…", Theme.SmallFont,
                new Rectangle(x, cursor, inner, headerHeight), Theme.TextDim,
                Flags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        cursor += headerHeight + Px(6);

        // 思考区：折叠时只有一行入口，点开就地展开
        if (row.HasToggle)
        {
            var toggle = new Rectangle(x, cursor, inner, row.Toggle.Height);
            var label = $"{(entry.ThinkingExpanded ? "▾" : "▸")} 思考过程（{entry.Thinking.Length} 字）";
            var hover = hoveredToggle == row.Toggle;
            if (hover)
            {
                using var path = RailIconPainter.RoundedRect(toggle, Px(4));
                using var fill = new SolidBrush(Theme.Hover);
                graphics.FillPath(fill, path);
            }
            TextRenderer.DrawText(graphics, label, Theme.SmallFont, toggle,
                hover ? Theme.Text : Theme.Accent, Flags | TextFormatFlags.VerticalCenter);
            cursor += toggle.Height + Px(4);

            if (entry.ThinkingExpanded && row.ThinkingLines.Count > 0)
            {
                var box = new Rectangle(x, cursor, inner, row.ThinkingBox.Height);
                using (var path = RailIconPainter.RoundedRect(box, Px(6)))
                {
                    using var fill = new SolidBrush(Theme.PanelBg);
                    graphics.FillPath(fill, path);
                }
                DrawLines(graphics, row, row.ThinkingLines, TextBlock.Thinking, box.X + Px(8), box.Y + Px(8), row.ThinkingLineHeight, Theme.MonoFont, Theme.TextMuted);
                cursor += box.Height + Px(8);
            }
        }

        // 正文
        if (row.BodyLines.Count > 0)
        {
            var color = entry.Role == ChatRole.System ? Theme.TextMuted : Theme.Text;
            DrawLines(graphics, row, row.BodyLines, TextBlock.Body, x, cursor, row.LineHeight, Theme.UiFont, color);
            cursor += row.BodyLines.Count * row.LineHeight + Px(6);
            if (entry.Streaming)
            {
                using var caret = new SolidBrush(entry.Accent);
                graphics.FillRectangle(caret, x, cursor - Px(8), Px(8), Px(3));
            }
        }

        if (entry.Badge.Length > 0) cursor = DrawSmall(graphics, row, entry.Badge, x, cursor, inner, Theme.Accent);
        if (entry.Note.Length > 0) DrawSmall(graphics, row, entry.Note, x, cursor, inner, Theme.Warning);
    }

    /// <summary>画一个文本块的所有行，并顺便画选中高亮——两者用同一份行布局，所以必然对齐。</summary>
    private void DrawLines(Graphics graphics, Row row, List<TextLine> lines, TextBlock block, int x, int y, int lineHeight, Font font, Color color)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var lineTop = y + index * lineHeight;
            DrawSelection(graphics, row, block, line, x, lineTop, lineHeight, font);
            TextRenderer.DrawText(graphics, line.Text, font, new Point(x, lineTop), color, Flags);
        }
    }

    private int DrawSmall(Graphics graphics, Row row, string text, int x, int y, int width, Color color)
    {
        var height = TextHeight(Theme.SmallFont);
        var lines = Wrap(text, Theme.SmallFont, width);
        for (var index = 0; index < lines.Count; index++)
            TextRenderer.DrawText(graphics, lines[index].Text, Theme.SmallFont, new Point(x, y + index * height), color, Flags);
        return y + lines.Count * height + Px(4);
    }

    /// <summary>某一行的选中高亮：把选中区间换算成这一行里的起止像素。</summary>
    private void DrawSelection(Graphics graphics, Row row, TextBlock block, TextLine line, int x, int y, int lineHeight, Font font)
    {
        var (start, end) = BlockSelection(row.Entry, block);
        if (start >= end) return;

        var lineEnd = line.Start + line.Text.Length;
        var from = Math.Max(start, line.Start);
        var to = Math.Min(end, lineEnd);
        if (from >= to) return;

        var left = x + TextWidth(line.Text[..(from - line.Start)], font);
        var right = x + TextWidth(line.Text[..(to - line.Start)], font);
        using var brush = new SolidBrush(Color.FromArgb(70, Theme.Accent));
        graphics.FillRectangle(brush, left, y, Math.Max(1, right - left), lineHeight);
    }

    /// <summary>文本块在一条消息里的顺序：思考在上、正文在下，和绘制顺序一致。</summary>
    private static int BlockOrder(TextBlock block) => block == TextBlock.Thinking ? 0 : 1;

    /// <summary>按"文档顺序"比较两个位置：条目序号 → 块序号 → 块内字符下标。</summary>
    private int Compare(Position a, Position b)
    {
        var byEntry = entries.IndexOf(a.Entry).CompareTo(entries.IndexOf(b.Entry));
        if (byEntry != 0) return byEntry;
        var byBlock = BlockOrder(a.Block).CompareTo(BlockOrder(b.Block));
        return byBlock != 0 ? byBlock : a.Offset.CompareTo(b.Offset);
    }

    private (Position From, Position To)? Ordered()
    {
        if (selectionAnchor is not { } anchor || selectionCaret is not { } caret) return null;
        return Compare(anchor, caret) <= 0 ? (anchor, caret) : (caret, anchor);
    }

    /// <summary>某个块落在选区里的字符区间；完全不相交时返回 (0,0)。绘制与复制共用这一份换算。</summary>
    private (int Start, int End) BlockSelection(ChatEntry entry, TextBlock block)
    {
        if (Ordered() is not { } range) return (0, 0);
        var (from, to) = range;

        var length = block == TextBlock.Body ? entry.Body.Length : entry.Thinking.Length;
        if (length == 0) return (0, 0);
        if (Compare(to, new Position(entry, block, 0)) <= 0) return (0, 0);
        if (Compare(from, new Position(entry, block, length)) >= 0) return (0, 0);
        return (Compare(from, new Position(entry, block, 0)) > 0 ? from.Offset : 0,
                Compare(to, new Position(entry, block, length)) < 0 ? to.Offset : length);
    }

    private static Color CardFill(ChatEntry entry) => entry.Role switch
    {
        ChatRole.User => Theme.IsDark ? Color.FromArgb(38, 42, 60) : Color.FromArgb(238, 242, 255),
        ChatRole.Error => Theme.IsDark ? Color.FromArgb(48, 34, 36) : Color.FromArgb(255, 240, 240),
        ChatRole.Assistant => Theme.PanelBg,
        _ => Theme.IsDark ? Color.FromArgb(32, 32, 38) : Color.FromArgb(246, 246, 250)
    };

    private void DrawScrollBar(Graphics graphics)
    {
        if (!NeedsScrollBar) return;
        var trackHeight = ClientSize.Height - Px(16);
        if (trackHeight <= 0) return;
        var x = ClientSize.Width - Px(8);
        var thumbHeight = Math.Max(Px(30), (int)(trackHeight * (double)ClientSize.Height / contentHeight));
        var max = contentHeight - ClientSize.Height;
        var thumbTop = Px(8) + (int)((trackHeight - thumbHeight) * (scroll / (double)Math.Max(1, max)));

        using (var track = new SolidBrush(Theme.Border))
        using (var path = RailIconPainter.RoundedRect(new Rectangle(x, Px(8), Px(4), trackHeight), Px(2)))
            graphics.FillPath(track, path);
        using (var thumb = new SolidBrush(Theme.TextDim))
        using (var path = RailIconPainter.RoundedRect(new Rectangle(x, thumbTop, Px(4), thumbHeight), Px(2)))
            graphics.FillPath(thumb, path);
    }

    // ---------- 交互 ----------

    private Rectangle ScrollBarZone => new(ClientSize.Width - Px(14), 0, Px(14), ClientSize.Height);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (draggingThumb) { ScrollThumbTo(e.Y); return; }
        if (selecting) { ExtendSelection(e.Location); return; }

        var row = HitRow(e.Location);
        Rectangle? toggle = row?.HasToggle == true && row.Toggle.Contains(ToCardPoint(row, e.Location)) ? row.Toggle : null;
        var changed = !ReferenceEquals(row?.Entry, hovered) || toggle != hoveredToggle;
        hovered = row?.Entry;
        hoveredToggle = toggle;
        Cursor = toggle is not null ? Cursors.Hand : Cursors.IBeam;
        if (changed) Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hovered is null && hoveredToggle is null) return;
        hovered = null;
        hoveredToggle = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();

        if (NeedsScrollBar && ScrollBarZone.Contains(e.Location))
        {
            draggingThumb = true;
            ScrollThumbTo(e.Y);
            return;
        }

        var row = HitRow(e.Location);
        if (row?.HasToggle == true && row.Toggle.Contains(ToCardPoint(row, e.Location)))
        {
            row.Entry.ThinkingExpanded = !row.Entry.ThinkingExpanded;
            Rebuild();
            Invalidate();
            return;
        }

        // 从光标位置开始拉选区
        var hit = HitText(e.Location);
        if (hit is null) { ClearSelection(); Invalidate(); return; }
        selectionAnchor = hit;
        selectionCaret = hit;
        selecting = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        draggingThumb = false;
        selecting = false;
        if (e.Button != MouseButtons.Right) return;
        rightClicked = HitRow(e.Location)?.Entry;
        menu.Show(this, e.Location);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollByLines(-Math.Sign(e.Delta) * 3);
    }

    /// <summary>Ctrl+C 复制选中，Ctrl+A 全选。焦点在对话区时生效（在输入框里时不会走到这里）。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.Control) == Keys.Control)
        {
            if ((keyData & Keys.KeyCode) == Keys.C) { Copy(SelectedText()); return true; }
            if ((keyData & Keys.KeyCode) == Keys.A) { SelectAll(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SelectAll()
    {
        if (entries.Count == 0) return;
        selectionAnchor = new Position(entries[0], TextBlock.Body, 0);
        var last = entries[^1];
        selectionCaret = new Position(last, TextBlock.Body, last.Body.Length);
        Invalidate();
    }

    private void ClearSelection()
    {
        selectionAnchor = null;
        selectionCaret = null;
    }

    private void ExtendSelection(Point location)
    {
        var hit = HitText(location) ?? HitNearest(location);
        if (hit is not { } position || position == selectionCaret) return;
        selectionCaret = position;
        Invalidate();
    }

    /// <summary>拖到卡片外面时，把光标吸附到最近的一条文本末尾，别让选区断掉。</summary>
    private Position? HitNearest(Point location)
    {
        if (rows.Count == 0) return null;
        var y = location.Y + scroll;
        if (y < rows[0].Top) return new Position(rows[0].Entry, TextBlock.Body, 0);
        var last = rows[^1];
        return new Position(last.Entry, TextBlock.Body, last.Entry.Body.Length);
    }

    /// <summary>把鼠标点换算成（条目, 区域, 字符下标）。</summary>
    private Position? HitText(Point location)
    {
        var row = HitRow(location);
        if (row is null) return null;
        var point = ToCardPoint(row, location);

        if (row.HasToggle && row.ThinkingLines.Count > 0 && row.ThinkingBox.Contains(new Point(Px(14), point.Y)))
            return new Position(row.Entry, TextBlock.Thinking,
                CharAt(row.ThinkingLines, row.ThinkingTop, row.ThinkingLineHeight, Theme.MonoFont, point, Px(14) + Px(8)));

        if (row.BodyLines.Count > 0 && point.Y >= row.BodyTop)
            return new Position(row.Entry, TextBlock.Body,
                CharAt(row.BodyLines, row.BodyTop, row.LineHeight, Theme.UiFont, point, Px(14)));

        return new Position(row.Entry, TextBlock.Body, 0);
    }

    /// <summary>
    /// 行内二分：找出光标横坐标落在第几个字符之前。
    /// <paramref name="textLeft"/> 是这个文本块在卡片内的左边距——正文与思考的左边距不同，
    /// 不区分就会整体偏几个像素。
    /// </summary>
    private int CharAt(List<TextLine> lines, int top, int lineHeight, Font font, Point point, int textLeft)
    {
        var index = Math.Clamp((point.Y - top) / Math.Max(1, lineHeight), 0, lines.Count - 1);
        var line = lines[index];
        var x = point.X - textLeft;
        if (x <= 0) return line.Start;
        if (x >= line.Width) return line.Start + line.Text.Length;

        var low = 0;
        var high = line.Text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (TextWidth(line.Text[..mid], font) <= x) low = mid;
            else high = mid - 1;
        }
        return line.Start + low;
    }

    /// <summary>按绘制顺序（思考在上、正文在下）把各条选中的文字拼出来。</summary>
    private string SelectedText()
    {
        if (Ordered() is not { } range) return string.Empty;
        var (from, to) = range;
        if (Compare(from, to) == 0) return string.Empty;

        var first = entries.IndexOf(from.Entry);
        var last = entries.IndexOf(to.Entry);
        var parts = new List<string>();
        for (var index = first; index <= last; index++)
        {
            var entry = entries[index];
            var pieces = new List<string>();
            foreach (var (block, text) in new[] { (TextBlock.Thinking, entry.Thinking), (TextBlock.Body, entry.Body) })
            {
                var (start, end) = BlockSelection(entry, block);
                if (start < end) pieces.Add(text[start..end]);
            }
            if (pieces.Count > 0) parts.Add(string.Join("\n", pieces));
        }
        return string.Join("\n", parts);
    }

    /// <summary>把滑块拖到鼠标位置对应的滚动偏移。</summary>
    private void ScrollThumbTo(int y)
    {
        var max = Math.Max(0, contentHeight - ClientSize.Height);
        if (max == 0) return;
        var track = Math.Max(1, ClientSize.Height - Px(16));
        var ratio = Math.Clamp((y - Px(8)) / (double)track, 0, 1);
        var next = (int)Math.Round(max * ratio);
        if (next == scroll) return;
        scroll = next;
        Invalidate();
    }

    private void BuildMenu()
    {
        menu.Items.Clear();
        var selected = SelectedText();
        if (selected.Length > 0)
            menu.Items.Add(new ToolStripMenuItem("复制选中", null, (_, _) => Copy(selected)));
        if (rightClicked is { } entry)
            menu.Items.Add(new ToolStripMenuItem("复制这条", null, (_, _) => Copy(entry.Body)));
        menu.Items.Add(new ToolStripMenuItem("复制全部对话", null, (_, _) => Copy(BuildTranscript())));
        menu.Items.Add(new ToolStripMenuItem("滚到底部", null, (_, _) => ScrollToEnd()));
    }

    private string BuildTranscript() => string.Join("\n\n", entries.Select(item =>
        item.Thinking.Length > 0
            ? $"{item.Speaker}：{item.Body}\n[思考] {item.Thinking}"
            : $"{item.Speaker}：{item.Body}"));

    /// <summary>
    /// 剪贴板是全局资源，可能正被别的进程占用；`SetDataObject` 比 `SetText` 更稳，
    /// 再配几次重试。真失败就说出来——静默失败会让人以为"点了没反应"。
    /// </summary>
    private void Copy(string text)
    {
        if (text.Length == 0) return;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                // SetText 直接写入 Unicode 文本；SetDataObject(copy:true) 在部分剪贴板管理器下
                // 会在菜单关闭后丢失所有权，表现为点击了复制但粘贴为空。
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    && string.Equals(Clipboard.GetText(TextDataFormat.UnicodeText), text, StringComparison.Ordinal))
                    return;
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException
                                          or System.Threading.ThreadStateException
                                          or InvalidOperationException)
            {
                // 剪贴板是全局资源，短暂被其他进程占用时重试。
            }
            Thread.Sleep(80);
        }
        MessageBox.Show("复制到剪贴板失败：剪贴板可能正被其它程序占用，请稍后重试。", "复制");
    }

    /// <summary>把鼠标点换算成"卡片内坐标"，用于和卡片里的子矩形比较。</summary>
    private Point ToCardPoint(Row row, Point location) => new(location.X - row.Card.X, location.Y - (row.Card.Y - scroll));

    private Row? HitRow(Point location)
    {
        var y = location.Y + scroll;
        foreach (var row in rows)
            if (y >= row.Top && y < row.Top + row.Height) return row;
        return null;
    }
}
