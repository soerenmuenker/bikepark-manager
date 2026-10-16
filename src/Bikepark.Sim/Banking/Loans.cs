using Bikepark.Sim.Core;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Banking;

/// <summary>
/// A loan the bank offers (content, from the scenario's <c>loanRules</c>): a fixed amount, paid back in equal weekly
/// installments with a flat interest on the whole amount (10,000 € at 8 % over 10 weeks = 10 × 1,080 €).
/// </summary>
public sealed class LoanOffer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long PrincipalCents { get; set; }
    public int Weeks { get; set; } = 10;

    /// <summary>Interest on the principal over the whole term, permille (80 = 8 %).</summary>
    public int InterestPermille { get; set; }

    /// <summary>Park level needed to get it (a sandbox has everything).</summary>
    public int RequiredLevel { get; set; }

    /// <summary>What the park pays back in total.</summary>
    public long TotalRepayCents => PrincipalCents * (1000 + InterestPermille) / 1000;

    /// <summary>The weekly installment (rounded up to whole euros; the last one is whatever is left).</summary>
    public long InstallmentCents => ((TotalRepayCents + Weeks - 1) / Weeks + 99) / 100 * 100;

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"loanRules.offers[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("loanRules.offers: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (PrincipalCents is < 100 or > 100_000_000_00L) errors.Add($"{p}.principalCents must be within 100..10000000000");
        if (Weeks is < 1 or > 520) errors.Add($"{p}.weeks must be within 1..520");
        if (InterestPermille is < 0 or > 5000) errors.Add($"{p}.interestPermille must be within 0..5000");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel)
            errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        return errors;
    }
}

/// <summary>Loan offers and what a missed installment costs (content, from the scenario).</summary>
public sealed class LoanRules
{
    public List<LoanOffer> Offers { get; set; } = [];

    /// <summary>A missed installment adds this share of it to what is owed, permille.</summary>
    public int LateFeePermille { get; set; } = 100;

    public LoanOffer? Find(string offerId) => Offers.FirstOrDefault(o => o.Id == offerId);

    public List<string> Validate()
    {
        var errors = new List<string>();
        foreach (var offer in Offers) errors.AddRange(offer.Validate());
        if (Offers.Select(o => o.Id).Distinct().Count() != Offers.Count) errors.Add("loanRules.offers: ids must be unique");
        if (LateFeePermille is < 0 or > 1000) errors.Add("loanRules.lateFeePermille must be within 0..1000");
        return errors;
    }
}

/// <summary>A loan the park has taken. The installment is due every 7 days at the end of the day.</summary>
public sealed class Loan
{
    public int Id { get; set; }
    public string OfferId { get; set; } = "";
    public string Name { get; set; } = "";
    public long PrincipalCents { get; set; }

    /// <summary>Still owed (interest and late fees included).</summary>
    public long RemainingCents { get; set; }

    public long InstallmentCents { get; set; }
    public long TakenDay { get; set; }

    /// <summary>The day whose end the next installment is due.</summary>
    public long NextDueDay { get; set; }

    public int PaymentsMade { get; set; }
    public int MissedPayments { get; set; }

    /// <summary>What the next installment takes (the last one: only what is left).</summary>
    public long NextPaymentCents => Math.Min(InstallmentCents, RemainingCents);
}

public static class LoanMath
{
    public const int DaysPerInstallment = 7;

    /// <summary>Why the park can't take this loan now (level, already running), or null.</summary>
    public static string? CannotTake(WorldState state, int level, LoanOffer offer)
    {
        if (state.Rules.UnlockLevel(level) < offer.RequiredLevel)
            return $"The {offer.Name.ToLowerInvariant()} needs park level {offer.RequiredLevel} (you are level {level}).";
        if (state.Loans.Any(l => l.OfferId == offer.Id))
            return $"You already have the {offer.Name.ToLowerInvariant()}: pay it back first.";
        return null;
    }

    /// <summary>All the park still owes on its loans.</summary>
    public static long DebtCents(WorldState state) => state.Loans.Sum(l => l.RemainingCents);

    /// <summary>The installments due at the end of <paramref name="day"/>.</summary>
    public static long DueCents(WorldState state, long day) => state.Loans.Where(l => l.NextDueDay == day).Sum(l => l.NextPaymentCents);

    /// <summary>
    /// Pays <paramref name="cents"/> off a loan (an expense: loan payments): a weekly installment, or everything at once
    /// (<paramref name="early"/>: a one-off transaction).
    /// </summary>
    public static void Pay(WorldState state, Loan loan, long cents, long tick, bool early = false)
    {
        if (early) state.Finance.Invest(MoneyCategory.LoanRepaid, cents, tick, $"Repaid the {loan.Name.ToLowerInvariant()}");
        else state.Finance.Spend(MoneyCategory.LoanInstallments, cents);
        state.Finance.TotalLoanPaymentsCents += cents;
        state.Finance.LoanPaymentsTodayCents += cents;
        loan.RemainingCents -= cents;
    }

    /// <summary>The interest the park gets on the offer now (a bank sponsor may cut it).</summary>
    public static int InterestPermille(WorldState state, LoanOffer offer) =>
        offer.InterestPermille - offer.InterestPermille * Sponsors.SponsorMath.Benefit(state, Sponsors.SponsorBenefitKind.LoanInterest) / 1000;

    /// <summary>What the park repays in all, and per week, for the offer now.</summary>
    public static (long Total, long Installment) Terms(WorldState state, LoanOffer offer)
    {
        long total = offer.PrincipalCents * (1000 + InterestPermille(state, offer)) / 1000;
        return (total, ((total + offer.Weeks - 1) / offer.Weeks + 99) / 100 * 100);
    }

    public static Loan Open(WorldState state, LoanOffer offer, long tick) => new()
    {
        Id = state.AllocateEntityId(),
        OfferId = offer.Id,
        Name = offer.Name,
        PrincipalCents = offer.PrincipalCents,
        RemainingCents = Terms(state, offer).Total,
        InstallmentCents = Terms(state, offer).Installment,
        TakenDay = GameTime.Day(tick),
        NextDueDay = GameTime.Day(tick) + DaysPerInstallment - 1,
    };
}
