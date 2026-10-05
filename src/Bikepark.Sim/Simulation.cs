using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;

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

    public Simulation(WorldState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        Events = new EventBus();
        Commands = new CommandQueue(state);
        _context = new SimContext(state, Events);

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
