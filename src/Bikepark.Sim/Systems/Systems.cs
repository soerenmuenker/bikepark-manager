using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>A unit of per-tick game logic. Systems are stateless; all state lives in <see cref="WorldState"/>.</summary>
public interface ISimSystem
{
    void Update(SimContext ctx);
}

public static class ParkSchedule
{
    public static bool IsOpen(WorldState state, long tick)
    {
        int minute = GameTime.MinuteOfDay(tick);
        return minute >= state.Rules.OpenMinute && minute < state.Rules.CloseMinute;
    }
}

internal sealed class ParkHoursSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        int minute = GameTime.MinuteOfDay(ctx.Tick);
        if (minute == ctx.State.Rules.OpenMinute)
            ctx.Publish(new ParkOpened(ctx.Tick));
        else if (minute == ctx.State.Rules.CloseMinute)
            ctx.Publish(new ParkClosed(ctx.Tick));
    }
}

/// <summary>Spawns guests while the park is open. Arrival rate drops as the entry fee rises above the reference fee.</summary>
internal sealed class GuestArrivalSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (!ParkSchedule.IsOpen(state, ctx.Tick))
            return;

        int expectedPermille = ExpectedArrivalsPermille(state);
        int arrivals = expectedPermille / 1000 + (ctx.Rng.ChancePermille(expectedPermille % 1000) ? 1 : 0);

        for (int i = 0; i < arrivals; i++)
            TryAdmitGuest(ctx);
    }

    internal static int ExpectedArrivalsPermille(WorldState state)
    {
        var rules = state.Rules;
        long reference = Math.Max(1, rules.ReferenceEntryFeeCents);
        long feeFactor = 1000 + (reference - state.Park.EntryFeeCents) * 500 / reference;
        feeFactor = Math.Clamp(feeFactor, 100, 1500);
        return (int)(rules.BaseArrivalPermille * feeFactor / 1000);
    }

    private static void TryAdmitGuest(SimContext ctx)
    {
        var state = ctx.State;
        long fee = state.Park.EntryFeeCents;
        long cash = ctx.Rng.Range(2_000, 8_001);

        if (cash < fee)
        {
            state.Stats.TotalTurnedAway++;
            state.Stats.TurnedAwayToday++;
            ctx.Publish(new GuestTurnedAway(ctx.Tick));
            return;
        }

        long reference = Math.Max(1, state.Rules.ReferenceEntryFeeCents);
        int feePenalty = (int)Math.Max(0, (fee - reference) * 200 / reference);

        var guest = new Guest
        {
            Id = state.AllocateEntityId(),
            CashCents = cash - fee,
            Happiness = Math.Clamp(ctx.Rng.Range(550, 801) - feePenalty, 0, 1000),
            ArrivedTick = ctx.Tick,
            PlannedStayMinutes = ctx.Rng.Range(60, 361),
        };

        state.Finance.Earn(fee);
        state.Guests.Add(guest);
        state.Stats.TotalVisitors++;
        state.Stats.VisitorsToday++;
        ctx.Publish(new GuestArrived(ctx.Tick, guest.Id));
    }
}

/// <summary>Updates guests in the park: mood, spending, and leaving.</summary>
internal sealed class GuestSystem : ISimSystem
{
    private const int SnackChancePermille = 8;
    private const int SnackHappinessBoost = 15;
    private const int CrowdingPenalty = 2;
    private const int UnhappyThreshold = 250;

    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        var guests = state.Guests;
        if (guests.Count == 0)
            return;

        bool closing = GameTime.MinuteOfDay(ctx.Tick) >= state.Rules.CloseMinute
                       || GameTime.MinuteOfDay(ctx.Tick) < state.Rules.OpenMinute;
        bool crowded = guests.Count > state.Rules.Capacity;

        // Iterate in list order and compact in place: deterministic and allocation-free.
        int write = 0;
        for (int read = 0; read < guests.Count; read++)
        {
            var guest = guests[read];
            GuestLeaveReason? leave = closing ? GuestLeaveReason.ParkClosed : UpdateGuest(ctx, guest, crowded);

            if (leave is { } reason)
            {
                var stats = state.Stats;
                stats.TotalGuestsLeft++;
                stats.SumExitHappiness += guest.Happiness;
                if (reason == GuestLeaveReason.Unhappy)
                    stats.TotalLeftUnhappy++;
                ctx.Publish(new GuestLeft(ctx.Tick, guest.Id, reason));
            }
            else
                guests[write++] = guest;
        }
        guests.RemoveRange(write, guests.Count - write);
    }

    private static GuestLeaveReason? UpdateGuest(SimContext ctx, Guest guest, bool crowded)
    {
        var rules = ctx.State.Rules;

        guest.Happiness += ctx.Rng.Range(-1, 2);
        if (crowded)
            guest.Happiness -= CrowdingPenalty;

        if (guest.CashCents >= rules.SnackPriceCents && ctx.Rng.ChancePermille(SnackChancePermille))
        {
            guest.CashCents -= rules.SnackPriceCents;
            ctx.State.Finance.Earn(rules.SnackPriceCents);
            guest.Happiness += SnackHappinessBoost;
        }

        guest.Happiness = Math.Clamp(guest.Happiness, 0, 1000);

        if (guest.Happiness < UnhappyThreshold)
            return GuestLeaveReason.Unhappy;
        if (ctx.Tick - guest.ArrivedTick >= guest.PlannedStayMinutes)
            return GuestLeaveReason.StayCompleted;
        return null;
    }
}

/// <summary>Charges daily upkeep and closes the books at the last minute of each day.</summary>
internal sealed class FinanceSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        if (!GameTime.IsLastMinuteOfDay(ctx.Tick))
            return;

        var state = ctx.State;
        state.Finance.Spend(state.Rules.DailyUpkeepCents);

        var report = new DayReport(
            Day: GameTime.Day(ctx.Tick),
            Visitors: state.Stats.VisitorsToday,
            TurnedAway: state.Stats.TurnedAwayToday,
            RevenueCents: state.Finance.RevenueTodayCents,
            ExpensesCents: state.Finance.ExpensesTodayCents,
            MoneyCents: state.Finance.MoneyCents);
        ctx.Publish(new DayEnded(ctx.Tick, report));

        state.Stats.VisitorsToday = 0;
        state.Stats.TurnedAwayToday = 0;
        state.Finance.RevenueTodayCents = 0;
        state.Finance.ExpensesTodayCents = 0;
    }
}
