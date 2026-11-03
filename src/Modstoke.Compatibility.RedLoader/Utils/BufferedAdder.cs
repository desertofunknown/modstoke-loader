using System;
using System.Collections.Generic;

namespace RedLoader.Utils;

/// <summary>
/// Processes items immediately when allowed and queues the rest until <see cref="Flush"/> is called.
/// </summary>
public class BufferedAdder<T>
{
    private readonly Queue<T> _queue = new();
    private readonly Func<bool> _canAddFunc;
    private readonly Action<T> _addFunc;
    
    /// <param name="canAddFunc">Determines whether <see cref="Add"/> processes an item immediately.</param>
    /// <param name="addFunc">Processes each item, either immediately or during <see cref="Flush"/>.</param>
    public BufferedAdder(Func<bool> canAddFunc, Action<T> addFunc)
    {
        _canAddFunc = canAddFunc;
        _addFunc = addFunc;
    }
    
    public void Add(T item)
    {
        if (_canAddFunc())
        {
            _addFunc(item);
        }
        else
        {
            _queue.Enqueue(item);
        }
    }
    
    /// <summary>
    /// Processes queued items in order without checking the add condition.
    /// </summary>
    /// <remarks>
    /// Each item is removed before processing. If processing throws, the remaining items stay queued.
    /// </remarks>
    public void Flush()
    {
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();
            _addFunc(item);
        }
    }
}
