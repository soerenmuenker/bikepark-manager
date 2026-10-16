namespace Bikepark.Sim.State;

/// <summary>
/// What money came in or went out for. Regular categories happen every day and depend on the guests (the daily budget);
/// the others are one-off transactions the player decided on (<see cref="FinanceState.Transactions"/>). Saved by number:
/// append new ones, never reorder.
/// </summary>
public enum MoneyCategory : byte
{
    // Regular income
    EntryFees = 0,
    LiftPasses = 1,
    /// <summary>Lunch and snacks.</summary>
    Food = 2,
    Merch = 3,

    // Regular expenses
    /// <summary>The scenario's base staff (<see cref="ParkRules.DailyUpkeepCents"/>: ticket office, first aid, cleaning).</summary>
    ParkStaff = 4,
    Wages = 5,
    /// <summary>Bike access booked on company lifts.</summary>
    LiftFees = 6,
    /// <summary>Upkeep of the park's own lifts.</summary>
    LiftUpkeep = 7,
    BarUpkeep = 8,
    ShopUpkeep = 9,
    /// <summary>The goods merch shops sell.</summary>
    MerchGoods = 10,
    /// <summary>Weekly loan installments.</summary>
    LoanInstallments = 11,

    // One-off transactions
    Land = 12,
    /// <summary>Lifts built or restored by a contractor.</summary>
    LiftBuild = 13,
    /// <summary>Bars and shops built.</summary>
    Buildings = 14,
    Tools = 15,
    Wood = 16,
    /// <summary>Lifts and parking lots torn down by a contractor.</summary>
    Removal = 17,
    /// <summary>Money borrowed: in, but not revenue.</summary>
    LoanTaken = 18,
    /// <summary>A loan paid back in full ahead of time.</summary>
    LoanRepaid = 19,

    // Appended (Phase 9.1)
    /// <summary>Coffee, beer and other drinks (regular income).</summary>
    Drinks = 20,
    /// <summary>The goods bars sell (regular expense).</summary>
    FoodGoods = 21,
}

/// <summary>A one-off booking, for the recent transactions list. <see cref="Cents"/> is signed (+ in, − out).</summary>
public sealed record MoneyTransaction(long Tick, MoneyCategory Category, long Cents, string Label);

/// <summary>
/// Money per <see cref="MoneyCategory"/> of one closed day (index = category), compared by value so day reports stay
/// equal records. Saved as a plain array.
/// </summary>
public sealed class MoneyByCategory : List<long>, IEquatable<MoneyByCategory>
{
    public MoneyByCategory() { }

    public MoneyByCategory(IEnumerable<long> cents) : base(cents) { }

    public bool Equals(MoneyByCategory? other) => other is not null && this.SequenceEqual(other);

    public override bool Equals(object? obj) => Equals(obj as MoneyByCategory);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (long c in this) hash.Add(c);
        return hash.ToHashCode();
    }
}

public static class Ledger
{
    public static readonly int Count = Enum.GetValues<MoneyCategory>().Length;

    /// <summary>Happens every day and depends on the guests (the daily budget), as opposed to a one-off transaction.</summary>
    public static bool IsRegular(MoneyCategory category) =>
        category <= MoneyCategory.LoanInstallments || category is MoneyCategory.Drinks or MoneyCategory.FoodGoods;

    /// <summary>Money coming in (revenue, or a loan for <see cref="MoneyCategory.LoanTaken"/>).</summary>
    public static bool IsIncome(MoneyCategory category) =>
        category <= MoneyCategory.Merch || category is MoneyCategory.LoanTaken or MoneyCategory.Drinks;

    /// <summary>The cents of <paramref name="category"/> in a per-category list (a day report's or today's), 0 if missing.</summary>
    public static long Get(IReadOnlyList<long>? byCategory, MoneyCategory category) =>
        byCategory is not null && (int)category < byCategory.Count ? byCategory[(int)category] : 0;
}
