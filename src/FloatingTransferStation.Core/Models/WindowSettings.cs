namespace FloatingTransferStation.Models;

public sealed record WindowSettings(
    double PanelWidth,
    double WindowHeight,
    double Top,
    Dictionary<BoardCategory, string>? CategoryNames = null,
    int ReviewMigrationVersion = 0,
    int CategoryNameMigrationVersion = 0,
    IReadOnlyList<BoardCategory>? CategoryOrder = null,
    Dictionary<BoardCategory, IReadOnlyList<string>>? SubgroupNames = null)
{
    // 标签轨道宽＝内容无关的结构常量（2026-11-04 回归修复，设计契约 #54）：
    // 2026-11-03 W7（Issue #280）曾为四字默认名「快捷启动」单行显示加宽到 66，
    // 实为回归——结构尺寸禁止为内容加宽；恢复 58，内容侧按两字/行自适应折行
    // （CategoryLabelTextStyle 两字宽文本列 30＋自然 Wrap：「快捷启动」＝
    // 快捷/启动 两行居中、6 字名至多 3 行无截断），58−页边 5×2−边框 1×2＝
    // 46 DIP 对 2 字×12px＝24 DIP 充裕。轨道列宽唯一消费面＝MainWindow.xaml
    // LayoutRoot 第二列（x:Static 直引本常量的 XAML 镜像）。
    public const double TabWidth = 58;
    // 子分组按钮轨高＝内容无关的结构常量（实验性，契约 #57）：ChipRail 开＝头行
    //（44）与 BoardList 之间的固定高行；Off＝整行 Collapsed 零占位（BoardList
    // 几何与现状逐 DIP 一致）。高度恒定，不随过滤态/名称变化。
    public const double SubgroupRailHeight = 36;
    // 卡片内容宽 = PanelWidth − 50（页边 12×2 + 边框 1×2 + 卡内边距 12×2），
    // 右半区 = 内容宽/2 必须容纳右上两列按钮网格占地 2×30=60（1.33.0 起网格几何，
    // Issue #103；四列时代曾为 4×30=120 下限；正文另让 4 DIP 缓冲合计内缩 64，
    // 见 CardTextReservedRightInset）：(PanelWidth − 50)/2 ≥ 60 → PanelWidth ≥ 170，
    // 取 310 维持 1.29.0 起的产品下限不变（非几何约束）。
    public const double MinPanelWidth = 310;
    public const double MaxPanelWidth = 640;
    public const double MinWindowHeight = 360;

    public static WindowSettings Default { get; } = new(360, 640, 80);

    public string CategoryName(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }

        return CategoryNames is not null &&
               CategoryNames.TryGetValue(category, out var name) &&
               BoardCategoryCatalog.IsValidDisplayName(name)
            ? name
            : BoardCategoryCatalog.DisplayName(category);
    }

    /// <summary>
    /// 标签显示顺序（1.25.0）：分类名与顺序解耦的独立持久化状态；只调顺序、
    /// 不能增删分类。null（未定制）或非法（成员未定义、数量不符、重复）一律
    /// 回落目录默认序 <see cref="BoardCategoryCatalog.Ordered"/>，校验在读取端
    /// 一次做齐，消费面只认本属性。
    /// </summary>
    public IReadOnlyList<BoardCategory> DisplayOrder
    {
        get
        {
            if (CategoryOrder is { } order &&
                order.Count == BoardCategoryCatalog.Ordered.Count &&
                order.All(BoardCategoryCatalog.IsDefined) &&
                order.Distinct().Count() == order.Count)
            {
                return order;
            }

            return BoardCategoryCatalog.Ordered;
        }
    }

    /// <summary>采纳新的标签显示顺序：与分类名相互独立，改名不改序（零耦合）。</summary>
    public WindowSettings WithCategoryOrder(IReadOnlyList<BoardCategory> order) =>
        this with { CategoryOrder = order.ToArray() };

    /// <summary>
    /// 子分组槽名解析（契约 #57，照 <see cref="CategoryName"/> 的逐项回落口径）：
    /// null（未定制）或该槽条目缺失/清单短/非法名（空白或超 3 字）一律回落
    /// <see cref="SubgroupCatalog.DefaultName"/>；消费面只认本方法。名清单唯一
    /// 持久化在 settings.json（与 <see cref="CategoryNames"/> 并存方式同构）。
    /// </summary>
    public string SubgroupName(BoardCategory category, int slot)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }

        if (!SubgroupCatalog.IsValidSlot(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }

        return SubgroupNames is not null &&
               SubgroupNames.TryGetValue(category, out var names) &&
               names is not null &&
               slot < names.Count &&
               SubgroupCatalog.IsValidName(names[slot])
            ? names[slot]
            : SubgroupCatalog.DefaultName(slot);
    }

    /// <summary>采纳单个子分组槽名（契约 #57）：只改目标槽、其余槽原样（含未定制
    /// 空位），与分类名/顺序解耦；槽号或名称非法时抛参数异常（调用方先归一）。</summary>
    public WindowSettings WithSubgroupName(BoardCategory category, int slot, string name)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }

        if (!SubgroupCatalog.IsValidSlot(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }

        if (!SubgroupCatalog.IsValidName(name))
        {
            throw new ArgumentException(
                $"Subgroup names must contain 1-{SubgroupCatalog.MaxNameLength} visible characters.",
                nameof(name));
        }

        var names = SubgroupNames is null
            ? new Dictionary<BoardCategory, IReadOnlyList<string>>()
            : new Dictionary<BoardCategory, IReadOnlyList<string>>(SubgroupNames);
        names[category] = Enumerable.Range(0, SubgroupCatalog.SlotCount)
            .Select(current => current == slot ? name : RawSubgroupName(category, current))
            .ToArray();
        return this with { SubgroupNames = names };
    }

    /// <summary>未定制槽的存储占位（空串＝非法名 → 逐槽回落默认；见 SubgroupName）。</summary>
    private string RawSubgroupName(BoardCategory category, int slot) =>
        SubgroupNames is not null &&
        SubgroupNames.TryGetValue(category, out var names) &&
        names is not null &&
        slot < names.Count &&
        names[slot] is { } raw &&
        SubgroupCatalog.IsValidName(raw)
            ? raw
            : string.Empty;

    public WindowSettings WithCategoryName(BoardCategory category, string name)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }

        if (!BoardCategoryCatalog.IsValidDisplayName(name))
        {
            throw new ArgumentException(
                $"Category names must contain at most {BoardCategoryCatalog.MaxDisplayNameLength} characters.",
                nameof(name));
        }

        var names = BoardCategoryCatalog.Ordered.ToDictionary(
            current => current,
            CategoryName);
        names[category] = name;
        return this with { CategoryNames = names };
    }

    public WindowSettings ResetToDefault(double workAreaWidth, double workAreaHeight) =>
        (Default with
        {
            CategoryNames = CategoryNames is null ? null : new(CategoryNames),
            ReviewMigrationVersion = ReviewMigrationVersion,
            CategoryNameMigrationVersion = CategoryNameMigrationVersion,
            CategoryOrder = CategoryOrder is null ? null : new List<BoardCategory>(CategoryOrder),
            SubgroupNames = SubgroupNames is null ? null : new(SubgroupNames)
        })
            .Normalize(workAreaWidth, workAreaHeight);

    public WindowSettings Normalize(double workAreaWidth, double workAreaHeight)
    {
        var maxPanelWidth = Math.Max(0, Math.Min(MaxPanelWidth, workAreaWidth - TabWidth));
        var minPanelWidth = Math.Min(MinPanelWidth, maxPanelWidth);
        var panelWidth = Math.Clamp(PanelWidth, minPanelWidth, maxPanelWidth);
        var minHeight = Math.Min(MinWindowHeight, workAreaHeight);
        var height = Math.Clamp(WindowHeight, minHeight, workAreaHeight);
        var top = Math.Clamp(Top, 0, Math.Max(0, workAreaHeight - height));
        return this with
        {
            PanelWidth = panelWidth,
            WindowHeight = height,
            Top = top
        };
    }
}
