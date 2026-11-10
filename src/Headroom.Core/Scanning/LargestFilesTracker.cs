namespace Headroom.Core.Scanning;

/// <summary>
/// Mantém os N maiores arquivos usando uma min-heap (<see cref="PriorityQueue{TElement,TPriority}"/>).
/// Thread-safe: o limiar mínimo é lido sem lock, então só arquivos candidatos disputam o lock.
/// </summary>
public sealed class LargestFilesTracker
{
    private readonly PriorityQueue<ScanNode, long> _heap;
    private readonly object _gate = new();
    private readonly int _capacity;
    private long _threshold = -1;

    public LargestFilesTracker(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _heap = new PriorityQueue<ScanNode, long>(_capacity + 1);
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _heap.Count;
            }
        }
    }

    public void Offer(ScanNode file)
    {
        long size = file.Size;
        if (size <= Volatile.Read(ref _threshold))
        {
            return;
        }

        lock (_gate)
        {
            if (_heap.Count < _capacity)
            {
                _heap.Enqueue(file, size);
                if (_heap.Count == _capacity && _heap.TryPeek(out _, out long min))
                {
                    Volatile.Write(ref _threshold, min);
                }

                return;
            }

            if (size <= _threshold)
            {
                return;
            }

            _heap.EnqueueDequeue(file, size);
            if (_heap.TryPeek(out _, out long newMin))
            {
                Volatile.Write(ref _threshold, newMin);
            }
        }
    }

    /// <summary>Retorna os arquivos do maior para o menor.</summary>
    public List<ScanNode> ToSortedList()
    {
        List<ScanNode> list;
        lock (_gate)
        {
            list = new List<ScanNode>(_heap.Count);
            foreach (var (element, _) in _heap.UnorderedItems)
            {
                list.Add(element);
            }
        }

        list.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return list;
    }
}
