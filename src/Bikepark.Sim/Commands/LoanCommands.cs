using Bikepark.Sim.Banking;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reputation;

namespace Bikepark.Sim.Commands;

/// <summary>Takes a loan from the bank: the money is there at once, installments are due weekly (<see cref="LoanMath"/>).</summary>
public sealed record TakeLoanCommand(string OfferId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (ctx.State.LoanRules.Find(OfferId) is not { } offer) return $"Unknown loan '{OfferId}'.";
        if (ctx.State.Park.Bankrupt) return "The park is bankrupt.";
        return LoanMath.CannotTake(ctx.State, ParkProgress.CurrentLevel(ctx.State, ctx.Network), offer);
    }

    public void Apply(SimContext ctx)
    {
        var offer = ctx.State.LoanRules.Find(OfferId)!;
        var loan = LoanMath.Open(ctx.State, ctx.Network, offer, ctx.Tick);
        ctx.State.Loans.Add(loan);
        ctx.State.Finance.Borrow(offer.PrincipalCents, ctx.Tick, $"Took the {loan.Name.ToLowerInvariant()}");
        ctx.Publish(new LoanTaken(ctx.Tick, loan.Id, loan.Name, loan.PrincipalCents));
    }
}

/// <summary>Pays off on a loan at once (the interest still to come is not charged).</summary>
public sealed record RepayLoanCommand(int LoanId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (ctx.State.Loans.FirstOrDefault(l => l.Id == LoanId) is not { } loan) return "No such loan.";
        if (ctx.State.Finance.MoneyCents < loan.PayoffCents)
            return $"Not enough money: {loan.PayoffCents / 100:N0} € are still owed on the {loan.Name.ToLowerInvariant()}.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var loan = ctx.State.Loans.First(l => l.Id == LoanId);
        long owed = loan.PayoffCents;
        LoanMath.Pay(ctx.State, loan, owed, ctx.Tick, early: true);
        ctx.Publish(new LoanPaid(ctx.Tick, loan.Id, loan.Name, owed, 0));
        ctx.State.Loans.Remove(loan);
        ctx.Publish(new LoanRepaid(ctx.Tick, loan.Id, loan.Name));
    }
}
