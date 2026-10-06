using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 面板条目集合：在标准 <see cref="ObservableCollection{T}"/> 之上提供整批替换。
/// 批量删除、置顶重排、撤销恢复等结构性变更通过 <see cref="ReplaceAll"/> 静默
/// 重排底层列表后只发出单个 <see cref="NotifyCollectionChangedAction.Reset"/> 通知，
/// 取代「Clear + 逐条 Add」的 N 次事件（每次事件都会触发列表容器measure与入场
/// 动画调度，条目多时直接卡住界面）。新内容入库仍走逐条 Add，保持入场动画语义。
/// 替换结果与当前内容逐引用完全一致时不发出任何通知。
/// </summary>
public sealed class BoardItemCollection : ObservableCollection<BoardItem>
{
    public void ReplaceAll(IEnumerable<BoardItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var desired = items as IList<BoardItem> ?? items.ToList();
        if (desired.Count == Count)
        {
            var unchanged = true;
            for (var index = 0; index < desired.Count; index++)
            {
                if (!ReferenceEquals(desired[index], this[index]))
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        CheckReentrancy();
        var silent = Items;
        silent.Clear();
        foreach (var item in desired)
        {
            silent.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
