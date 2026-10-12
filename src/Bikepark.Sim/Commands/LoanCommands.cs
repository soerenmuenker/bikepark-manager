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
        var loan = LoanMath.Open(ctx.State, offer, ctx.Tick);
        ctx.State.Loans.Add(loan);
        ctx.State.Finance.Borrow(offer.PrincipalCents);
        ctx.Publish(new LoanTaken(ctx.Tick, loan.Id, loan.Name, loan.PrincipalCents));
    }
}

/// <summary>Pays back everything still owed on a loan at once (interest included: there is no discount).</summary>
public sealed record RepayLoanCommand(int LoanId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (ctx.State.Loans.FirstOrDefault(l => l.Id == LoanId) is not { } loan) return "No such loan.";
        if (ctx.State.Finance.MoneyCents < loan.RemainingCents)
            return $"Not enough money: {loan.RemainingCents / 100:N0} € are still owed on the {loan.Name.ToLowerInvariant()}.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var loan = ctx.State.Loans.First(l => l.Id == LoanId);
        long owed = loan.RemainingCents;
        LoanMath.Pay(ctx.State, loan, owed);
        ctx.Publish(new LoanPaid(ctx.Tick, loan.Id, loan.Name, owed, 0));
        ctx.State.Loans.Remove(loan);
        ctx.Publish(new LoanRepaid(ctx.Tick, loan.Id, loan.Name));
    }
}
