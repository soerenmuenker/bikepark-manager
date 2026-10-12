using Bikepark.Sim.Banking;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Systems;

namespace Bikepark.Sim.Tests;

/// <summary>Bank loans (weekly installments, late fees, early repayment) and bankruptcy.</summary>
public class LoanTests
{
    /// <summary>The test world without guests (no revenue; the daily upkeep still costs money).</summary>
    private static Simulation World(Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.Scenario();
        scenario.Rules.BaseArrivalPermille = 0;
        scenario.LoanRules.Offers.Add(new LoanOffer { Id = "bank", Name = "Bank loan", PrincipalCents = 3_000_000, Weeks = 20, InterestPermille = 120, RequiredLevel = 3 });
        tweak?.Invoke(scenario);
        return new Simulation(ScenarioLoader.CreateWorld(scenario));
    }

    private static string? Reject(Simulation sim, ICommand command)
    {
        sim.Events.Clear();
        sim.Commands.Enqueue(command);
        sim.Step();
        return sim.Events.Pending.OfType<CommandRejected>().SingleOrDefault()?.Reason;
    }

    [Fact]
    public void Offer_SplitsTheAmountPlusInterest_IntoWeeklyInstallments()
    {
        var offer = new LoanOffer { PrincipalCents = 1_000_000, Weeks = 10, InterestPermille = 80 };
        Assert.Equal(1_080_000, offer.TotalRepayCents);
        Assert.Equal(108_000, offer.InstallmentCents);
        var odd = new LoanOffer { PrincipalCents = 1_000_000, Weeks = 7, InterestPermille = 0 };
        Assert.Equal(142_900, odd.InstallmentCents); // 1428.57 € rounded up to whole euros
    }

    [Fact]
    public void TakingALoan_AddsTheMoney_AndPaysItBackWeekly_UntilItIsGone()
    {
        var sim = World(s => s.StartingMoneyCents = 10_000_000);
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new TakeLoanCommand("starter")));
        Assert.Equal(money + 1_000_000, sim.State.Finance.MoneyCents);
        Assert.Equal(0, sim.State.Finance.TotalRevenueCents); // borrowed, not earned
        var loan = Assert.Single(sim.State.Loans);
        Assert.Equal(6, loan.NextDueDay);
        Assert.Contains("already have", Reject(sim, new TakeLoanCommand("starter")));

        var paid = new List<LoanPaid>();
        var days = new List<DayReport>();
        sim.Events.Subscribe<LoanPaid>(paid.Add);
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        bool repaid = false;
        sim.Events.Subscribe<LoanRepaid>(_ => repaid = true);
        sim.RunDays(70);
        sim.Events.Dispatch();

        Assert.Equal(10, paid.Count);
        Assert.All(paid, p => Assert.Equal(108_000, p.PaidCents));
        Assert.Equal([6L, 13, 20], days.Where(d => d.LoanPaymentsCents > 0).Take(3).Select(d => d.Day));
        Assert.True(repaid);
        Assert.Empty(sim.State.Loans);
        Assert.Equal(1_080_000, sim.State.Finance.TotalLoanPaymentsCents);
        Assert.Null(Reject(sim, new TakeLoanCommand("starter"))); // paid off: can be taken again
    }

    [Fact]
    public void MissedInstallment_AddsALateFee_AndIsDueAgainNextWeek()
    {
        var sim = World(s => s.StartingMoneyCents = 0);
        Assert.Null(Reject(sim, new TakeLoanCommand("starter")));
        var loan = sim.State.Loans[0];
        sim.State.Finance.MoneyCents = 0; // spent it all
        var missed = new List<LoanPaymentMissed>();
        sim.Events.Subscribe<LoanPaymentMissed>(missed.Add);
        sim.RunTicks(GameTime.TicksForDays(7) - sim.State.Tick);
        sim.Events.Dispatch();

        var miss = Assert.Single(missed);
        Assert.Equal(108_000, miss.DueCents);
        Assert.Equal(10_800, miss.LateFeeCents);
        Assert.Equal(1_080_000 + 10_800, loan.RemainingCents);
        Assert.Equal(1, loan.MissedPayments);
        Assert.Equal(13, loan.NextDueDay);
    }

    [Fact]
    public void RepayingEarly_PaysEverythingStillOwed()
    {
        var sim = World(s => s.StartingMoneyCents = 500_000);
        Assert.Null(Reject(sim, new TakeLoanCommand("starter")));
        int id = sim.State.Loans[0].Id;
        sim.State.Finance.MoneyCents = 500_000;
        Assert.Contains("Not enough money", Reject(sim, new RepayLoanCommand(id)));
        sim.State.Finance.MoneyCents = 2_000_000;
        Assert.Null(Reject(sim, new RepayLoanCommand(id)));
        Assert.Empty(sim.State.Loans);
        Assert.Equal(2_000_000 - 1_080_000, sim.State.Finance.MoneyCents);
        Assert.Contains("No such loan", Reject(sim, new RepayLoanCommand(id)));
    }

    [Fact]
    public void BiggerLoans_NeedTheParkLevel()
    {
        var sim = World(s => s.Rules.UnlockAll = false);
        Assert.Contains("needs park level 3", Reject(sim, new TakeLoanCommand("bank")));
        var sandbox = World();
        Assert.Null(Reject(sandbox, new TakeLoanCommand("bank")));
    }

    [Fact]
    public void ParkInTheRed_IsWarned_AndGoesBankrupt_AfterTheLimit()
    {
        var sim = World(s =>
        {
            s.StartingMoneyCents = -1_000_000;
            s.Rules.BankruptcyDays = 10;
        });
        var warnings = new List<DebtWarning>();
        bool bankrupt = false;
        sim.Events.Subscribe<DebtWarning>(warnings.Add);
        sim.Events.Subscribe<ParkBankrupt>(_ => bankrupt = true);

        sim.RunDays(9);
        sim.Events.Dispatch();
        Assert.Equal([9, 7, 1], warnings.Select(w => w.DaysLeft));
        Assert.False(sim.State.Park.Bankrupt);
        Assert.Equal(9, sim.State.Finance.DaysInDebt);

        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.True(bankrupt);
        Assert.True(sim.State.Park.Bankrupt);
        Assert.False(ParkSchedule.IsOpen(sim.State, GameTime.TicksForDays(11) + sim.State.Rules.OpenMinute));
        Assert.Contains("bankrupt", Reject(sim, new TakeLoanCommand("starter")));

        sim.RunDays(1);
        Assert.Empty(sim.State.Guests); // nobody comes any more
    }

    [Fact]
    public void ADayInTheBlack_ResetsTheDebtCount()
    {
        var sim = World(s =>
        {
            s.StartingMoneyCents = -100;
            s.Rules.BankruptcyDays = 10;
        });
        sim.RunDays(2);
        Assert.Equal(2, sim.State.Finance.DaysInDebt);
        sim.State.Finance.MoneyCents = 10_000_000;
        sim.RunDays(1);
        Assert.Equal(0, sim.State.Finance.DaysInDebt);
    }

    [Fact]
    public void Loans_SurviveASave_AndContinueIdentically()
    {
        var sim = World();
        Assert.Null(Reject(sim, new TakeLoanCommand("starter")));
        sim.RunDays(8);
        var loaded = new Simulation(SaveGame.Clone(sim.State));
        Assert.Equal(sim.State.Loans[0].RemainingCents, loaded.State.Loans[0].RemainingCents);
        sim.RunDays(7);
        loaded.RunDays(7);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
    }
}
