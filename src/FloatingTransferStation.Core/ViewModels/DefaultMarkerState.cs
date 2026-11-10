namespace FloatingTransferStation.ViewModels;

/// <summary>
/// 默认接收标记三态环语法（契约 #56 修订，2026-11-07）：凡具备落点能力的位置
/// 恒有环——Active＝当前有效默认落点（紫，AccentBrush）、Inactive＝具备落点
/// 能力但非当前（灰，SecondaryTextBrush）、None＝永不具备落点能力（无环）。
/// 两宿主同名属性承载：分类主标签（<see cref="CategoryViewModel"/>——内容桶
/// 两枚恒环：默认接收 Active、另一 Inactive；记录/启动恒 None）与子分组
/// chip（<see cref="SubgroupChipViewModel"/>——轨可见时恰一枚独占环：其分类的
/// 默认子分组，未设兜底显示序首槽恒 Inactive；其余槽 None〔#56 修订 2026-11-08
/// 环独占化，取代「每 chip 恒有环」旧语义〕）；
/// 用色与显隐由样式 <c>CategoryDefaultMarkerStyle</c> 触发器单点驱动。
/// </summary>
public enum DefaultMarkerState
{
    None,
    Inactive,
    Active
}
