using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim;

/// <summary>What systems and commands get to work with during a tick.</summary>
public sealed class SimContext
{
    private readonly Simulation _simulation;

    internal SimContext(Simulation simulation, EventBus events)
    {
        _simulation = simulation;
        State = simulation.State;
        Events = events;
    }

    public WorldState State { get; }

    /// <summary>The tick currently being simulated.</summary>
    public long Tick => State.Tick;

    public SimRandom Rng => State.Rng;

    public TerrainGrid Terrain => _simulation.Terrain;

    /// <summary>The way network as of now (rebuilt after ways change, even within a tick).</summary>
    public WayNetwork Network => _simulation.Network;

    public void Publish(ISimEvent simEvent) => Events.Publish(simEvent);

    internal EventBus Events { get; }
}
