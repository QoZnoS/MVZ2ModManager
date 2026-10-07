namespace MVZ2ModManager.UI.Controls;

/// <summary>
/// 自绘列表里"卡片"的两行文字（标题 + 副标题）。
///
/// <para>原来两个列表（快照点 / 模组包）各自用写死的 +6 / +26 / +27 摆这两行字：
/// 行高一旦跟着字体变，整块文字就偏，而且没有裁剪，长名字会糊到卡片外面。
/// 这里统一按字体实际行高把两行**整体**在卡片里居中，并带上省略号。</para>
/// </summary>
internal static class CardText
{
    /// <summary>标题与副标题之间的间距。</summary>
    private const int Gap = 2;

    /// <summary>两行文字整体的绘制范围：左右留白由调用方给，纵向按字体行高居中。</summary>
    public const TextFormatFlags Flags =
        TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    /// <summary>两行文字整体的起始 y（相对 <paramref name="card"/> 垂直居中）。</summary>
    public static int BlockTop(Rectangle card, Font titleFont, Font subFont)
    {
        int block = titleFont.Height + Gap + subFont.Height;
        // 卡片矮到装不下两行时按 0 处理：文字溢出卡片是没办法的事，但也别再往下推。
        return card.Top + Math.Max(0, (card.Height - block) / 2);
    }

    /// <summary>
    /// 画"标题 + 副标题"。两张字体都由调用方持有并负责释放（<c>using</c>）。
    /// </summary>
    public static void Draw(
        Graphics g, Rectangle card, int padding,
        Font titleFont, string title, Color titleColor,
        Font subFont, string subtitle, Color subColor)
    {
        int left = card.Left + padding;
        int width = Math.Max(0, card.Width - padding * 2);
        int top = BlockTop(card, titleFont, subFont);

        TextRenderer.DrawText(g, title, titleFont,
            new Rectangle(left, top, width, titleFont.Height), titleColor, Flags);

        TextRenderer.DrawText(g, subtitle, subFont,
            new Rectangle(left, top + titleFont.Height + Gap, width, subFont.Height), subColor, Flags);
    }
}
