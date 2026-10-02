namespace YEEYEEYEE.Desktop;

/// <summary>揭晓里一张卡摆在哪儿：第几行、第几列，以及这一批的卡有多大。</summary>
public readonly record struct GachaCardSlot(int Row, int Column, int PerRow, int Rows, double Width, double Height);

/// <summary>
/// 出图开奖那一屏的排布规则。**纯计算、没有界面依赖**，所以规则本身可以用几行断言钉死——
/// 放在界面里就只能跑真机一张张看，而排布错了正是「看不出来但一直在那儿」的那类问题。
///
/// 三条规则：
/// · **一行最多 3 张**，超了换行：6 张就是上下两排、每排 3 张。
/// · **4 张走 2×2**：3+1 会显得上面挤、下面空。
/// · 卡片是**固定的竖长方形**（<see cref="Aspect"/> ＝ 高/宽 ＝ 1.5，即 2:3），**不跟着图的比例走**：
///   一副牌就是要大小一致；跟着图走的话一排里方的方、横的横，看着是一排缩略图而不是一手牌。
///   张数少就摆大一点（一行反正放得下）。
///   代价是**方图（出图默认就是方的）放进竖卡里要裁掉两侧约三分之一**；六张裁法完全一样
///   （比的是同一把尺子），而收进节点的是没裁过的原图，所以裁掉的那点构图并没有丢。
/// </summary>
public static class GachaCardLayout
{
	/// <summary>卡面固定比例（高 / 宽）。2:3 是二游卡牌最常见的竖长方形。</summary>
	public const double Aspect = 1.5;

	/// <summary>同一行里两张卡之间的间距。</summary>
	public const double Gap = 18;

	/// <summary>两行之间的间距。</summary>
	public const double RowGap = 30;

	/// <summary>一行最多摆几张。再挤就成缩略图了，不像一手牌。</summary>
	public const int MaxPerRow = 3;

	/// <summary>这一批几张、平摊到每一行几张。</summary>
	public static int PerRow(int count) => count switch
	{
		<= 0 => 0,
		<= 3 => count,
		4 => 2,
		_ => MaxPerRow
	};

	/// <summary>这一批要摆几行。</summary>
	public static int Rows(int count)
	{
		var perRow = PerRow(count);
		return perRow <= 0 ? 0 : (count + perRow - 1) / perRow;
	}

	/// <summary>这一批每张卡多宽。张数少就给大一点。</summary>
	public static double Width(int count) => count switch
	{
		<= 0 => 0,
		1 => 300,
		2 => 260,
		3 => 240,
		_ => 200
	};

	/// <summary>这一批每张卡多高（固定的竖长方形）。</summary>
	public static double Height(int count) => Width(count) * Aspect;

	/// <summary>最宽那一行的宽度（卡排下面的台面边线按它给）。</summary>
	public static double RowWidth(int count)
	{
		var perRow = PerRow(count);
		return perRow <= 0 ? 0 : perRow * Width(count) + (perRow - 1) * Gap;
	}

	/// <summary>
	/// 第 <paramref name="index"/> 张卡的位置。
	/// 越界**要抛**，不静默夹到边界：夹住了会把「算错了」伪装成「都摆好了」。
	/// </summary>
	public static GachaCardSlot Slot(int count, int index)
	{
		if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), count, "一批至少得有一张。");
		if (index < 0 || index >= count)
			throw new ArgumentOutOfRangeException(nameof(index), index, $"序号必须落在 0..{count - 1} 之间。");

		var perRow = PerRow(count);
		return new GachaCardSlot(
			index / perRow, index % perRow, perRow, Rows(count), Width(count), Height(count));
	}

	/// <summary>
	/// 第 <paramref name="index"/> 张卡相对**整手牌中心**的偏移。卡片飞出来的动画要用它
	/// （「从中心飞出来」＝ 起点摆在这个偏移的相反方向）。
	///
	/// **末行张数少时按它自己的张数算**：5 张是上三下二，末行那两张是居中摆的，
	/// 若按「每行三张」算偏移，飞出来的起点就会偏半格——飞完还是落到正确的位置，
	/// 所以只在飞的过程中看得出来，不影响结果，但一样是算错了。
	/// </summary>
	public static (double X, double Y) OffsetFromCentre(int count, int index)
	{
		var slot = Slot(count, index);
		var inRow = Math.Min(slot.PerRow, count - slot.Row * slot.PerRow);
		var x = (slot.Column - (inRow - 1) / 2.0) * (slot.Width + Gap);
		var y = (slot.Row - (slot.Rows - 1) / 2.0) * (slot.Height + RowGap);
		return (x, y);
	}
}
