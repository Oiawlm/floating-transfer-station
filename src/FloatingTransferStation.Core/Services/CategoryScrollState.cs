using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class CategoryScrollState
{
    private readonly Dictionary<BoardCategory, double> _offsets =
        BoardCategoryCatalog.Ordered.ToDictionary(category => category, _ => 0d);

    public void Save(BoardCategory category, double offset)
    {
        Validate(category);
        _offsets[category] = Normalize(offset);
    }

    public double GetClamped(BoardCategory category, double scrollableHeight)
    {
        Validate(category);
        var height = Normalize(scrollableHeight);
        return height <= 0 ? 0 : Math.Min(_offsets[category], height);
    }

    private static double Normalize(double value) =>
        double.IsFinite(value) && value >= 0 ? value : 0;

    private static void Validate(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }
    }
}
