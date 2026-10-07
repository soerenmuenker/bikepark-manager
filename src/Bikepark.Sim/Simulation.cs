using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim;

/// <summary>
/// Fixed-step simulation driver. One <see cref="Step"/> simulates one game minute:
/// <list type="number">
///   <item>apply all queued commands due this tick (in (Tick, Sequence) order),</item>
///   <item>run every system in a fixed order,</item>
///   <item>advance <see cref="WorldState.Tick"/>.</item>
/// </list>
/// The simulation knows nothing about real time; the host decides how many steps to run per frame.
/// </summary>
public sealed class Simulation
{
    private readonly SimContext _context;
    private readonly IReadOnlyList<ISimSystem> _systems;
    private readonly Lazy<TerrainGrid> _baseTerrain;
    private TerrainGrid? _terrain;
    private int _terrainRevision = -1;
    private WayNetwork? _network;
    private (int Ways, int Terrain) _networkRevision = (-1, -1);

    public Simulation(WorldState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        Events = new EventBus();
        Commands = new CommandQueue(state);
        _baseTerrain = new Lazy<TerrainGrid>(() => TerrainCache.Get(state.Terrain, state.Seed));
        _context = new SimContext(this, Events);

        // Order matters for determinism and gameplay. Append new systems deliberately.
        _systems =
        [
            new ParkHoursSystem(),
            new GuestArrivalSystem(),
            new RiderSystem(),
            new LiftSystem(),
            new GuestSystem(),
            new FinanceSystem(),
        ];
    }

    public WorldState State { get; }

    public EventBus Events { get; }

    public CommandQueue Commands { get; }

    /// <summary>
    /// The terrain, derived from <see cref="WorldState.Terrain"/> (and the world seed) plus the stored
    /// <see cref="WorldState.TerrainEdits"/>. Generated on first access, re-derived when <see cref="WorldState.TerrainRevision"/>
    /// changes.
    /// </summary>
    public TerrainGrid Terrain
    {
        get
        {
            if (_terrain is null || _terrainRevision != State.TerrainRevision)
            {
                _terrain = TerrainEditor.Apply(_baseTerrain.Value, State.TerrainEdits);
                _terrainRevision = State.TerrainRevision;
            }
            return _terrain;
        }
    }

    /// <summary>The generated terrain without edits.</summary>
    public TerrainGrid BaseTerrain => _baseTerrain.Value;

    /// <summary>
    /// The way network derived from <see cref="WorldState.Ways"/>, the lifts and the parking lots, rebuilt when
    /// <see cref="WorldState.WaysRevision"/> or <see cref="WorldState.TerrainRevision"/> changes (every structure change
    /// edits the terrain). With nothing built it is empty and does not generate the terrain.
    /// </summary>
    public WayNetwork Network
    {
        get
        {
            var revision = (State.WaysRevision, State.TerrainRevision);
            if (_network is null || _networkRevision != revision)
            {
                var (hubs, links) = LiftNetwork.Build(State);
                _network = State.Ways.Count == 0 && hubs.Count == 0
                    ? WayNetwork.Empty
                    : WayNetwork.Build(State.Ways, hubs, links, Terrain, State.TrailRules, State.TrailFeatureTypes);
                _networkRevision = revision;
            }
            return _network;
        }
    }

    public void Step()
    {
        foreach (var scheduled in Commands.TakeDue(State.Tick))
        {
            string? rejection = scheduled.Command.Validate(_context);
            if (rejection is null)
            {
                scheduled.Command.Apply(_context);
                _context.Publish(new CommandApplied(State.Tick, scheduled.Command));
            }
            else
            {
                _context.Publish(new CommandRejected(State.Tick, scheduled.Command, rejection));
            }
        }

        foreach (var system in _systems)
            system.Update(_context);

        State.Tick++;
    }

    public void RunTicks(long ticks)
    {
        for (long i = 0; i < ticks; i++)
            Step();
    }

    public void RunDays(long days) => RunTicks(GameTime.TicksForDays(days));
}
