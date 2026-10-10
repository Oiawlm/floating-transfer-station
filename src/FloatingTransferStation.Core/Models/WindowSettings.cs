namespace FloatingTransferStation.Models;

public sealed record WindowSettings(
    double PanelWidth,
    double WindowHeight,
    double Top,
    Dictionary<BoardCategory, string>? CategoryNames = null,
    int ReviewMigrationVersion = 0,
    int CategoryNameMigrationVersion = 0,
    IReadOnlyList<BoardCategory>? CategoryOrder = null)
{
    public const double TabWidth = 58;
    public const double MinPanelWidth = 280;
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
            CategoryOrder = CategoryOrder is null ? null : new List<BoardCategory>(CategoryOrder)
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
