using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;

namespace Bikepark.Sim.Tests;

public class CommandQueueTests
{
    [Fact]
    public void Command_IsAppliedAtStartOfItsTick_BeforeSystemsRun()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.Commands.Enqueue(new SetEntryFeeCommand(4000), atTick: 5);

        sim.RunTicks(5);
        Assert.Equal(1500, sim.State.Park.EntryFeeCents);

        sim.Step();
        Assert.Equal(4000, sim.State.Park.EntryFeeCents);

        var applied = Assert.Single(sim.Events.Pending.OfType<CommandApplied>());
        Assert.Equal(5, applied.Tick);
        // CommandApplied is published before anything a system emits in the same tick.
        Assert.Same(applied, sim.Events.Pending.First(e => e.Tick == 5));
    }

    [Fact]
    public void CommandsForSameTick_ApplyInEnqueueOrder_EvenIfEnqueuedOutOfTickOrder()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.Commands.Enqueue(new SetEntryFeeCommand(300), atTick: 10);
        sim.Commands.Enqueue(new SetEntryFeeCommand(100), atTick: 3);
        sim.Commands.Enqueue(new SetEntryFeeCommand(200), atTick: 10);

        Assert.Equal([3L, 10L, 10L], sim.Commands.Pending.Select(c => c.Tick));

        sim.RunTicks(11);

        Assert.Equal(200, sim.State.Park.EntryFeeCents);
        Assert.Equal(
            [100L, 300L, 200L],
            sim.Events.Pending.OfType<CommandApplied>().Select(e => ((SetEntryFeeCommand)e.Command).FeeCents));
        Assert.Equal(0, sim.Commands.Count);
    }

    [Fact]
    public void CommandWithoutTick_RunsOnNextStep_AndPastTicksAreClamped()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.RunTicks(100);

        sim.Commands.Enqueue(new RenameParkCommand("Now"));
        sim.Commands.Enqueue(new SetEntryFeeCommand(0), atTick: 3);
        Assert.All(sim.Commands.Pending, c => Assert.Equal(100, c.Tick));

        sim.Step();
        Assert.Equal("Now", sim.State.Park.Name);
        Assert.Equal(0, sim.State.Park.EntryFeeCents);
    }

    [Fact]
    public void InvalidCommand_IsRejected_AndStateUnchanged()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.Commands.Enqueue(new SetEntryFeeCommand(-1));
        sim.Commands.Enqueue(new RenameParkCommand("   "));
        sim.Step();

        Assert.Equal(1500, sim.State.Park.EntryFeeCents);
        Assert.Equal("Test Park", sim.State.Park.Name);
        Assert.Equal(2, sim.Events.Pending.OfType<CommandRejected>().Count());
    }

    [Fact]
    public void EventBus_DispatchesInOrder_ToTypedAndUntypedSubscribers_ThenClears()
    {
        var sim = new Simulation(TestWorlds.Create());
        var all = new List<ISimEvent>();
        var applied = new List<CommandApplied>();
        sim.Events.SubscribeAll(all.Add);
        using var sub = sim.Events.Subscribe<CommandApplied>(applied.Add);

        sim.Commands.Enqueue(new RenameParkCommand("A"));
        sim.RunDays(1);
        int expected = sim.Events.Pending.Count;

        Assert.Equal(expected, sim.Events.Dispatch());
        Assert.Equal(expected, all.Count);
        Assert.Single(applied);
        Assert.Contains(all, e => e is DayEnded);
        Assert.Empty(sim.Events.Pending);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Tick <= p.Second.Tick));
    }
}
