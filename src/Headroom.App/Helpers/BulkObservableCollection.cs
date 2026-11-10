using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Headroom.App.Helpers;

/// <summary>
/// ObservableCollection com operações em lote que disparam um único Reset, para listas
/// virtualizadas com dezenas de milhares de itens não travarem a interface.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (items.Count < 8)
        {
            for (int i = 0; i < items.Count; i++)
            {
                Insert(index + i, items[i]);
            }

            return;
        }

        if (Items is List<T> list)
        {
            list.InsertRange(index, items);
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                Items.Insert(index + i, items[i]);
            }
        }

        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (count < 8)
        {
            for (int i = 0; i < count; i++)
            {
                RemoveAt(index);
            }

            return;
        }

        if (Items is List<T> list)
        {
            list.RemoveRange(index, count);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Items.RemoveAt(index);
            }
        }

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
