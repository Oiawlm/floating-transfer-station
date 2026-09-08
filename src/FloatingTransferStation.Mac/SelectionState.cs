namespace FloatingTransferStation.Mac;

public sealed class SelectionState
{
    private readonly HashSet<Guid> _ids = [];
    private Guid? _anchor;
    public IReadOnlySet<Guid> Ids => _ids;

    public void Select(Guid id, IReadOnlyList<Guid> order, bool toggle, bool range)
    {
        if (!order.Contains(id)) return;
        var anchorIndex = _anchor is { } anchor ? IndexOf(order, anchor) : -1;
        if (range && anchorIndex >= 0)
        {
            if (!toggle) _ids.Clear();
            var end = IndexOf(order, id);
            for (var i = Math.Min(anchorIndex, end); i <= Math.Max(anchorIndex, end); i++)
                _ids.Add(order[i]);
            return;
        }
        if (!toggle) _ids.Clear();
        if (!_ids.Add(id))
        {
            _ids.Remove(id);
            _anchor = null;
        }
        else _anchor = id;
    }

    public void Clear()
    {
        _ids.Clear();
        _anchor = null;
    }

    public void SelectAll(IEnumerable<Guid> ids)
    {
        _ids.Clear();
        _ids.UnionWith(ids);
        _anchor = null;
    }

    public void Prune(IEnumerable<Guid> ids)
    {
        _ids.IntersectWith(ids);
        if (_anchor is { } anchor && !ids.Contains(anchor)) _anchor = null;
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return -1;
    }
}
