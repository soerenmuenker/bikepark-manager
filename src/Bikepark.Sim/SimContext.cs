using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;

namespace Bikepark.Sim;

/// <summary>What systems and commands get to work with during a tick.</summary>
public sealed class SimContext
{
    internal SimContext(WorldState state, EventBus events)
    {
        State = state;
        Events = events;
    }

    public WorldState State { get; }

    /// <summary>The tick currently being simulated.</summary>
    public long Tick => State.Tick;

    public SimRandom Rng => State.Rng;

    public void Publish(ISimEvent simEvent) => Events.Publish(simEvent);

    internal EventBus Events { get; }
}
