namespace FloatingTransferStation.Models;

public sealed record WindowSettings(
    double PanelWidth,
    double WindowHeight,
    double Top,
    Dictionary<BoardCategory, string>? CategoryNames = null)
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
        (Default with { CategoryNames = CategoryNames is null ? null : new(CategoryNames) })
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
