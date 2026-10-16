using Bikepark.Sim.Events;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Tests;

/// <summary>Money booked by category: the daily budget (regular) and the recent transactions (one-off).</summary>
public class LedgerTests
{
    [Fact]
    public void Bookings_LandInTheirCategory_AndInTheTotals()
    {
        var finance = new FinanceState { MoneyCents = 100_000 };
        finance.Earn(MoneyCategory.EntryFees, 1_500);
        finance.Earn(MoneyCategory.EntryFees, 1_500);
        finance.Spend(MoneyCategory.Wages, 4_000);
        finance.Invest(MoneyCategory.Land, 50_000, 42, "Bought the South slope");
        finance.Borrow(20_000, 43, "Took the starter loan");

        Assert.Equal(3_000, finance.Today(MoneyCategory.EntryFees));
        Assert.Equal(4_000, finance.Today(MoneyCategory.Wages));
        Assert.Equal(50_000, finance.Today(MoneyCategory.Land));
        Assert.Equal(20_000, finance.Today(MoneyCategory.LoanTaken));
        Assert.Equal(3_000, finance.RevenueTodayCents); // a loan is no revenue
        Assert.Equal(54_000, finance.ExpensesTodayCents);
        Assert.Equal(100_000 + 3_000 - 54_000 + 20_000, finance.MoneyCents);

        // Only the one-offs are transactions, signed.
        Assert.Equal([new MoneyTransaction(42, MoneyCategory.Land, -50_000, "Bought the South slope"),
                      new MoneyTransaction(43, MoneyCategory.LoanTaken, 20_000, "Took the starter loan")], finance.Transactions);
    }

    [Fact]
    public void Transactions_KeepOnlyTheLatest()
    {
        var finance = new FinanceState();
        for (int i = 0; i < FinanceState.MaxTransactions + 5; i++)
            finance.Invest(MoneyCategory.Wood, 100, i, $"Bought {i} wood");
        Assert.Equal(FinanceState.MaxTransactions, finance.Transactions.Count);
        Assert.Equal(5, finance.Transactions[0].Tick);
    }

    [Fact]
    public void Categories_AreSplitIntoRegularAndOneOff()
    {
        Assert.True(Ledger.IsRegular(MoneyCategory.EntryFees));
        Assert.True(Ledger.IsRegular(MoneyCategory.LoanInstallments));
        Assert.False(Ledger.IsRegular(MoneyCategory.Land));
        Assert.False(Ledger.IsRegular(MoneyCategory.LoanTaken));
        Assert.True(Ledger.IsIncome(MoneyCategory.Merch));
        Assert.True(Ledger.IsIncome(MoneyCategory.LoanTaken));
        Assert.False(Ledger.IsIncome(MoneyCategory.MerchGoods));
        Assert.True(Ledger.IsRegular(MoneyCategory.Drinks) && Ledger.IsIncome(MoneyCategory.Drinks));
        Assert.True(Ledger.IsRegular(MoneyCategory.FoodGoods) && !Ledger.IsIncome(MoneyCategory.FoodGoods));
        Assert.Equal(0, Ledger.Get(null, MoneyCategory.Food));
        Assert.Equal(0, Ledger.Get([1, 2], MoneyCategory.Food));
    }

    [Fact]
    public void DayReport_SplitsTheDaysMoney_ByCategory_AndTodayStartsAfresh()
    {
        var scenario = TestWorlds.LiftScenario();
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in TestWorlds.DemoLiftNetwork().Concat(TestWorlds.DemoFacilities()))
            sim.Commands.Enqueue(c.Command, c.Tick);
        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        sim.RunDays(2);
        sim.Events.Dispatch();

        Assert.Equal(2, days.Count);
        foreach (var day in days)
        {
            var all = Enum.GetValues<MoneyCategory>();
            Assert.Equal(day.RevenueCents, all.Where(c => Ledger.IsIncome(c) && c != MoneyCategory.LoanTaken).Sum(c => Ledger.Get(day.ByCategory, c)));
            Assert.Equal(day.ExpensesCents, all.Where(c => !Ledger.IsIncome(c)).Sum(c => Ledger.Get(day.ByCategory, c)));
            Assert.True(Ledger.Get(day.ByCategory, MoneyCategory.EntryFees) > 0);
            Assert.Equal(day.FoodCents, Ledger.Get(day.ByCategory, MoneyCategory.Food));
            Assert.Equal(day.MerchCents, Ledger.Get(day.ByCategory, MoneyCategory.Merch));
            Assert.Equal(day.DrinksCents, Ledger.Get(day.ByCategory, MoneyCategory.Drinks));
        }
        // Upkeep by kind: the Demo has a snack bar (with its espresso machine), a beer rondel and a merch shop.
        var types = sim.State.FacilityTypes;
        var snackBar = types.Single(t => t.Id == "snack_bar");
        Assert.Equal(snackBar.UpkeepPerDayCents + snackBar.Upgrades[0].UpkeepPerDayCents + types.Single(t => t.Id == "beer_rondel").UpkeepPerDayCents,
            Ledger.Get(days[1].ByCategory, MoneyCategory.BarUpkeep));
        Assert.True(Ledger.Get(days[1].ByCategory, MoneyCategory.Drinks) > 0);
        Assert.True(Ledger.Get(days[1].ByCategory, MoneyCategory.FoodGoods) > 0);
        Assert.Equal(types.Single(t => t.Id == "merch_shop").UpkeepPerDayCents, Ledger.Get(days[1].ByCategory, MoneyCategory.ShopUpkeep));
        Assert.All(sim.State.Finance.TodayCents, c => Assert.Equal(0, c));
        Assert.Equal(days[^1].ByCategory, sim.State.DayHistory[^1].ByCategory);
    }

    [Fact]
    public void CareerOpening_ListsTheLoan_AndTheRestoredLift_AsTransactions()
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()));
        foreach (var c in TestWorlds.CareerOpening())
            sim.Commands.Enqueue(c.Command, c.Tick);
        sim.RunDays(1);

        var transactions = sim.State.Finance.Transactions;
        Assert.Contains(transactions, t => t.Category == MoneyCategory.LoanTaken && t.Cents > 0 && t.Label.Contains("loan"));
        Assert.Contains(transactions, t => t.Category == MoneyCategory.LiftBuild && t.Cents < 0 && t.Label.StartsWith("Restored"));
        Assert.DoesNotContain(transactions, t => Ledger.IsRegular(t.Category));
    }
}
