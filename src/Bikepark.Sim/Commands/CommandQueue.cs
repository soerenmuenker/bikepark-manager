using Bikepark.Sim.State;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Schedules commands for application at the start of a tick. The queue's contents live in
/// <see cref="WorldState.PendingCommands"/> so they are saved and hashed with the rest of the state.
/// </summary>
public sealed class CommandQueue
{
    private readonly WorldState _state;

    internal CommandQueue(WorldState state) => _state = state;

    public int Count => _state.PendingCommands.Count;

    public IReadOnlyList<ScheduledCommand> Pending => _state.PendingCommands;

    /// <summary>
    /// Queues a command for <paramref name="atTick"/>, defaulting to the next tick to be simulated.
    /// Ticks in the past are clamped to the next tick. Commands for the same tick apply in enqueue order.
    /// </summary>
    /// <returns>The command's sequence number.</returns>
    public long Enqueue(ICommand command, long? atTick = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        long tick = Math.Max(atTick ?? _state.Tick, _state.Tick);
        var scheduled = new ScheduledCommand(tick, _state.NextCommandSequence++, command);

        // Sequence numbers are monotonic, so inserting after the last entry with Tick <= tick keeps (Tick, Sequence) order.
        var pending = _state.PendingCommands;
        int index = pending.Count;
        while (index > 0 && pending[index - 1].Tick > tick)
            index--;
        pending.Insert(index, scheduled);
        return scheduled.Sequence;
    }

    /// <summary>Removes and returns all commands due at or before <paramref name="tick"/>, in application order.</summary>
    internal List<ScheduledCommand> TakeDue(long tick)
    {
        var pending = _state.PendingCommands;
        int count = 0;
        while (count < pending.Count && pending[count].Tick <= tick)
            count++;

        var due = pending.GetRange(0, count);
        pending.RemoveRange(0, count);
        return due;
    }
}
