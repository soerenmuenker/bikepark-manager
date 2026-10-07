using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;

namespace Bikepark.Sim.Tests;

public class DeterminismTests
{
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    [InlineData(ulong.MaxValue)]
    public void SameSeedAndCommands_ProduceIdenticalState(ulong seed)
    {
        var a = TestWorlds.Run(seed, days: 5, TestWorlds.Script());
        var b = TestWorlds.Run(seed, days: 5, TestWorlds.Script());

        Assert.Equal(SaveGame.Serialize(a.State), SaveGame.Serialize(b.State));
        Assert.Equal(StateHash.Compute(a.State), StateHash.Compute(b.State));
    }

    [Fact]
    public void SameSeedAndCommands_ProduceIdenticalEventStream()
    {
        var a = TestWorlds.Run(42, days: 2, TestWorlds.Script());
        var b = TestWorlds.Run(42, days: 2, TestWorlds.Script());

        Assert.NotEmpty(a.Events.Pending);
        Assert.Equal(a.Events.Pending, b.Events.Pending);
    }

    [Fact]
    public void SimulationActuallyDoesSomething()
    {
        var sim = TestWorlds.Run(1337, days: 3);

        Assert.True(sim.State.Stats.TotalVisitors > 0);
        Assert.True(sim.State.Finance.TotalRevenueCents > 0);
        Assert.Equal(GameTime.TicksForDays(3), sim.State.Tick);
    }

    [Fact]
    public void DifferentSeeds_Diverge()
    {
        var a = TestWorlds.Run(1, days: 2);
        var b = TestWorlds.Run(2, days: 2);

        Assert.NotEqual(StateHash.Compute(a.State), StateHash.Compute(b.State));
    }

    [Fact]
    public void DifferentCommands_Diverge()
    {
        var a = TestWorlds.Run(1337, days: 3);
        var b = TestWorlds.Run(1337, days: 3, TestWorlds.Script());

        Assert.NotEqual(StateHash.Compute(a.State), StateHash.Compute(b.State));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(777L)]
    [InlineData(1439L)]
    [InlineData(3L * 1440 + 600)]
    public void SaveLoadMidRun_ContinuesIdentically(long saveAtTick)
    {
        const long totalTicks = 6L * 1440;

        var uninterrupted = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.Script())
            uninterrupted.Commands.Enqueue(c.Command, c.Tick);
        uninterrupted.RunTicks(totalTicks);

        var first = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.Script())
            first.Commands.Enqueue(c.Command, c.Tick);
        first.RunTicks(saveAtTick);
        string save = SaveGame.Serialize(first.State);

        var resumed = new Simulation(SaveGame.Deserialize(save));
        resumed.RunTicks(totalTicks - saveAtTick);

        Assert.Equal(StateHash.Compute(uninterrupted.State), StateHash.Compute(resumed.State));
    }

    [Theory]
    [InlineData(12L * 60 + 30)] // lunch
    [InlineData(18L * 60 + 10)] // last rides
    public void SaveLoadMidDay_InStarterValley_ContinuesIdentically(long saveAtTick)
    {
        const long totalTicks = 1440 + 600;
        var uninterrupted = TestWorlds.RunLift(5, totalTicks, TestWorlds.DemoLiftNetwork());

        var first = TestWorlds.RunLift(5, saveAtTick, TestWorlds.DemoLiftNetwork());
        Assert.NotEmpty(first.State.Guests);
        var resumed = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(first.State)));
        resumed.RunTicks(totalTicks - saveAtTick);

        Assert.Equal(StateHash.Compute(uninterrupted.State), StateHash.Compute(resumed.State));
    }

    [Fact]
    public void SteppingInChunks_EqualsSteppingAtOnce()
    {
        var once = TestWorlds.Run(9, days: 2);

        var chunked = new Simulation(TestWorlds.Create(9));
        long remaining = GameTime.TicksForDays(2);
        foreach (long chunk in new long[] { 1, 59, 600, 7, 1000 })
        {
            chunked.RunTicks(chunk);
            remaining -= chunk;
        }
        chunked.RunTicks(remaining);

        Assert.Equal(StateHash.Compute(once.State), StateHash.Compute(chunked.State));
    }
}
