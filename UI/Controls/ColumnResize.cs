namespace MVZ2ModManager.UI.Controls;

/// <summary>
/// 列宽拖动的算术。
///
/// <para>原生 <see cref="System.Windows.Forms.ListView"/> 的规矩是：拖动分隔条只改**左边**那一列，
/// 多出来的宽度由最后一列吸收 —— 于是拖中间的分隔条会让整张表的列宽总和变来变去，
/// 右边的列跟着乱跳（拖"名称"和"状态"之间的分隔条，"GUID"列会跟着晃）。</para>
///
/// <para>这里改成常见表格的规矩：**两列之和保持不变**，多出来的宽度从右边那列里扣。</para>
///
/// <para>抽成纯函数是为了能自检 —— 拖拽本身靠人手，算术不该靠人手验。</para>
/// </summary>
internal static class ColumnResize
{
    /// <summary>列的最小宽度。再窄就只剩省略号，没有意义了。</summary>
    public const int MinWidth = 40;

    /// <summary>
    /// 第 <paramref name="left"/> 列想变成 <paramref name="wanted"/> 时，
    /// 算出「左列新宽度, 右列新宽度」，使两者之和与原来一致。
    /// </summary>
    /// <param name="left">左列当前宽度（拖动前）。</param>
    /// <param name="right">右列当前宽度。</param>
    /// <param name="wanted">左列被拖到的目标宽度。</param>
    /// <returns>
    /// 无法在不突破 <see cref="MinWidth"/> 的前提下完成时返回 <c>null</c>
    /// （调用方应当**什么都不做**，让原生逻辑照常处理）。
    /// </returns>
    public static (int Left, int Right)? Preserve(int left, int right, int wanted)
    {
        int total = left + right;

        // 两列都撑到极限仍放不下 → 这个目标宽度本身越界，别插手。
        if (total < MinWidth * 2) return null;

        wanted = Math.Clamp(wanted, MinWidth, total - MinWidth);

        // 夹紧之后跟原来一样 → 没有变化可言，交给原生逻辑。
        if (wanted == left) return null;

        return (wanted, total - wanted);
    }
}
