using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Terrain;

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
    private readonly Lazy<TerrainGrid> _terrain;

    public Simulation(WorldState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        Events = new EventBus();
        Commands = new CommandQueue(state);
        _terrain = new Lazy<TerrainGrid>(() => TerrainCache.Get(state.Terrain, state.Seed));
        _context = new SimContext(state, Events, _terrain);

        // Order matters for determinism and gameplay. Append new systems deliberately.
        _systems =
        [
            new ParkHoursSystem(),
            new GuestArrivalSystem(),
            new GuestSystem(),
            new FinanceSystem(),
        ];
    }

    public WorldState State { get; }

    public EventBus Events { get; }

    public CommandQueue Commands { get; }

    /// <summary>
    /// The terrain, derived from <see cref="WorldState.Terrain"/> (and the world seed). Generated on first access.
    /// </summary>
    public TerrainGrid Terrain => _terrain.Value;

    public void Step()
    {
        foreach (var scheduled in Commands.TakeDue(State.Tick))
        {
            string? rejection = scheduled.Command.Validate(State);
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
