using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;

namespace Bikepark.Sim;

/// <summary>What systems and commands get to work with during a tick.</summary>
public sealed class SimContext
{
    private readonly Lazy<TerrainGrid> _terrain;

    internal SimContext(WorldState state, EventBus events, Lazy<TerrainGrid> terrain)
    {
        State = state;
        Events = events;
        _terrain = terrain;
    }

    public WorldState State { get; }

    /// <summary>The tick currently being simulated.</summary>
    public long Tick => State.Tick;

    public SimRandom Rng => State.Rng;

    public TerrainGrid Terrain => _terrain.Value;

    public void Publish(ISimEvent simEvent) => Events.Publish(simEvent);

    internal EventBus Events { get; }
}
