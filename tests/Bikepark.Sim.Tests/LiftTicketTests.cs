using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Scenarios;

namespace Bikepark.Sim.Tests;

public class LiftTicketTests
{
    private const long Ticks = GameTime.MinutesPerDay + 15 * 60;

    private static Simulation Run(long ticketCents)
    {
        TimedCommand[] commands = [.. TestWorlds.DemoLiftNetwork(), new(0, new SetLiftTicketCommand(ticketCents))];
        return TestWorlds.RunLift(5, Ticks, commands);
    }

    [Fact]
    public void SetLiftTicket_ValidatesRange_AndChangesOnlyTheTicket()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.Commands.Enqueue(new SetLiftTicketCommand(-1));
        sim.Commands.Enqueue(new SetLiftTicketCommand(sim.State.Rules.MaxEntryFeeCents + 1));
        sim.Commands.Enqueue(new SetLiftTicketCommand(1200));
        sim.RunTicks(1);

        Assert.Equal(1200, sim.State.Park.LiftTicketCents);
        Assert.Equal(1500, sim.State.Park.EntryFeeCents);
    }

    [Fact]
    public void LiftPass_IsPaidOnceOnTheFirstLift_AndBookedAsRevenue()
    {
        var sim = Run(1000);

        var holders = sim.State.Guests.Where(g => g.HasLiftPass).ToList();
        Assert.NotEmpty(holders);
        Assert.True(sim.State.Finance.TotalLiftTicketsCents > 0);
        // Whoever bought the pass paid entrance + exactly one pass.
        Assert.All(holders, g => Assert.Equal(sim.State.Park.EntryFeeCents + 1000, g.PaidEntryCents));
        Assert.All(sim.State.Guests.Where(g => !g.HasLiftPass), g => Assert.Equal(sim.State.Park.EntryFeeCents, g.PaidEntryCents));
        Assert.Equal(0, sim.State.Finance.TotalLiftTicketsCents % 1000);
    }

    [Fact]
    public void FreeLifts_SellNoTickets()
    {
        var sim = Run(0);
        Assert.Equal(0, sim.State.Finance.TotalLiftTicketsCents);
        Assert.Contains(sim.State.Guests, g => g.HasLiftPass); // the pass is still "had", just free
    }

    [Fact]
    public void ExpensiveLiftPass_SendsGuestsPedalling()
    {
        var cheap = Run(0);
        var dear = Run(9_000);

        long Rides(Simulation s) => s.State.Lifts.Sum(l => l.Stats.Riders);
        Assert.True(Rides(dear) < Rides(cheap), $"{Rides(dear)} !< {Rides(cheap)}");
    }
}
