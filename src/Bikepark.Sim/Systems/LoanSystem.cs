using Bikepark.Sim.Banking;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Collects the loan installments due today at the last minute of the day (before <see cref="FinanceSystem"/> closes the
/// books). An installment the park can't pay is missed: a late fee is added to what is owed and it is due again in a week.
/// </summary>
internal sealed class LoanSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (!GameTime.IsLastMinuteOfDay(ctx.Tick) || state.Loans.Count == 0)
            return;

        long day = GameTime.Day(ctx.Tick);
        foreach (var loan in state.Loans.ToList())
        {
            if (loan.NextDueDay != day) continue;
            loan.NextDueDay += LoanMath.DaysPerInstallment;
            long due = loan.NextPaymentCents;
            if (state.Finance.MoneyCents < due)
            {
                long fee = due * state.LoanRules.LateFeePermille / 1000;
                loan.RemainingCents += fee;
                loan.MissedPayments++;
                ctx.Publish(new LoanPaymentMissed(ctx.Tick, loan.Id, loan.Name, due, fee));
                continue;
            }
            LoanMath.Pay(state, loan, due, ctx.Tick);
            loan.PaymentsMade++;
            ctx.Publish(new LoanPaid(ctx.Tick, loan.Id, loan.Name, due, loan.RemainingCents));
            if (loan.RemainingCents <= 0)
            {
                state.Loans.Remove(loan);
                ctx.Publish(new LoanRepaid(ctx.Tick, loan.Id, loan.Name));
            }
        }
    }
}
