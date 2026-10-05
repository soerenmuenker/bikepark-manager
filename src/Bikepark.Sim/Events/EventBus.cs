namespace Bikepark.Sim.Events;

/// <summary>
/// One-way channel from the simulation to observers (Godot view, SimRunner, tests).
/// The sim only <see cref="Publish"/>es; events are buffered and delivered when the host calls <see cref="Dispatch"/>,
/// typically once per frame. Handlers must never mutate <see cref="State.WorldState"/>; to react, enqueue a command.
/// The host is responsible for dispatching (or <see cref="Clear"/>ing) regularly, otherwise the buffer grows.
/// </summary>
public sealed class EventBus
{
    private readonly List<ISimEvent> _pending = [];
    private readonly List<Action<ISimEvent>> _handlers = [];

    public IReadOnlyList<ISimEvent> Pending => _pending;

    internal void Publish(ISimEvent simEvent) => _pending.Add(simEvent);

    /// <summary>Subscribes to events of type <typeparamref name="T"/> (including subtypes).</summary>
    public IDisposable Subscribe<T>(Action<T> handler) where T : ISimEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeAll(e =>
        {
            if (e is T typed) handler(typed);
        });
    }

    public IDisposable SubscribeAll(Action<ISimEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    /// <summary>Delivers all buffered events in publication order and clears the buffer.</summary>
    /// <returns>The number of events delivered.</returns>
    public int Dispatch()
    {
        if (_pending.Count == 0) return 0;

        var batch = _pending.ToArray();
        _pending.Clear();
        var handlers = _handlers.ToArray();
        foreach (var simEvent in batch)
            foreach (var handler in handlers)
                handler(simEvent);
        return batch.Length;
    }

    /// <summary>Drops buffered events without delivering them.</summary>
    public void Clear() => _pending.Clear();

    private sealed class Subscription(EventBus bus, Action<ISimEvent> handler) : IDisposable
    {
        public void Dispose() => bus._handlers.Remove(handler);
    }
}
