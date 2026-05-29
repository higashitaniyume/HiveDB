namespace HiveDB.Storage;

internal sealed class PageCache
{
    private readonly int _capacity;
    private readonly Dictionary<int, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _lru;
    private readonly object _lock = new();

    private record CacheEntry(int PageNumber, byte[] Buffer);

    public PageCache(int capacity = 64)
    {
        _capacity = capacity;
        _map = new(capacity);
        _lru = new();
    }

    public bool TryGet(int pageNumber, out byte[] buffer)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(pageNumber, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                buffer = node.Value.Buffer;
                return true;
            }
        }
        buffer = Array.Empty<byte>();
        return false;
    }

    public void Put(int pageNumber, byte[] buffer)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(pageNumber, out var existing))
                _lru.Remove(existing);
            else if (_map.Count >= _capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.PageNumber);
            }

            var node = _lru.AddFirst(new CacheEntry(pageNumber, buffer));
            _map[pageNumber] = node;
        }
    }

    public void Invalidate(int pageNumber)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(pageNumber, out var node))
            {
                _lru.Remove(node);
                _map.Remove(pageNumber);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    internal int Count
    {
        get { lock (_lock) return _map.Count; }
    }
}
