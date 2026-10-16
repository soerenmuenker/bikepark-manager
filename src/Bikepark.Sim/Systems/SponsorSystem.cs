using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Sponsors;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Brands and the park (see <see cref="SponsorRules"/>). At each opening: applications are answered, old offers expire and,
/// every <see cref="SponsorRules.OfferCheckDays"/>, brands with nothing going on may offer a deal for the best trail they
/// fit (or the whole park). At the day's end: running deals with their sign up pay (per day and per run or visitor), deals
/// end at their last day, when their trail is gone or closed too long; the photo spots' buzz fades. Runs before
/// <see cref="FinanceSystem"/> (it reads today's runs and visitors). Off without sponsor types.
/// </summary>
internal sealed class SponsorSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (GameTime.IsLastMinuteOfDay(ctx.Tick))
        {
            var rules = state.SponsorRules;
            state.Reputation.BuzzPermille = state.Reputation.BuzzPermille * rules.BuzzKeepPermille / 1000;
        }
        if (state.SponsorTypes.Count == 0) return;
        if (GameTime.MinuteOfDay(ctx.Tick) == state.Rules.OpenMinute && !state.Park.Bankrupt)
            Morning(ctx);
        if (GameTime.IsLastMinuteOfDay(ctx.Tick))
            Evening(ctx);
    }

    private static void Morning(SimContext ctx)
    {
        var state = ctx.State;
        var network = ctx.Network;
        int level = Reputation.ParkProgress.CurrentLevel(state, network);
        foreach (var deal in state.Sponsorships.ToList())
        {
            if (SponsorMath.Find(state, deal.SponsorId) is not { } brand) continue;
            if (deal.Status == SponsorshipStatus.Applied && ctx.Tick >= deal.AnswerTick)
            {
                string? no = deal.IsPark ? SponsorMath.ParkMisfit(state, brand, level)
                    : state.Ways.FirstOrDefault(w => w.Id == deal.WayId) is { } trail ? SponsorMath.TrailMisfit(state, network, brand, trail, level)
                    : "The trail is gone.";
                if (no is null) MakeOffer(ctx, deal);
                else End(ctx, deal, no);
                ctx.Publish(new SponsorAnswered(ctx.Tick, deal.Id, no is null, no ?? ""));
            }
            else if (deal.Status == SponsorshipStatus.Offered && ctx.Tick >= deal.ExpiresTick)
                End(ctx, deal, "The offer expired.");
        }

        // Brands come by themselves now and then: one new offer per check at most.
        var rules = state.SponsorRules;
        if (GameTime.Day(ctx.Tick) % rules.OfferCheckDays != 0) return;
        foreach (var brand in state.SponsorTypes)
        {
            if (SponsorMath.Open(state, brand.Id) is not null) continue;
            int wayId;
            if (SponsorMath.ParkMisfit(state, brand, level) is null && SponsorMath.DealOn(state, 0, offeredToo: true) is null)
                wayId = 0;
            else if (SponsorMath.BestTrail(state, network, brand, level) is { } trail)
                wayId = trail.Id;
            else
                continue;
            if (!ctx.Rng.ChancePermille(rules.OfferChancePermille)) continue;
            var deal = New(state, brand, wayId);
            MakeOffer(ctx, deal);
            return;
        }
    }

    private static void Evening(SimContext ctx)
    {
        var state = ctx.State;
        var network = ctx.Network;
        long day = GameTime.Day(ctx.Tick);
        foreach (var deal in state.Sponsorships.ToList())
        {
            if (deal.Status != SponsorshipStatus.Active || SponsorMath.Find(state, deal.SponsorId) is not { } brand) continue;
            if (!deal.IsPark)
            {
                var trail = state.Ways.FirstOrDefault(w => w.Id == deal.WayId);
                if (trail is null || !trail.Built || trail.Kind != WayKind.Trail)
                {
                    End(ctx, deal, "The trail is gone.");
                    continue;
                }
                deal.ClosedDays = network.IsRideable(trail) ? 0 : deal.ClosedDays + 1;
                if (deal.ClosedDays > state.SponsorRules.ClosedDaysAllowed)
                {
                    End(ctx, deal, $"{trail.Name} was closed too long.");
                    continue;
                }
            }
            if (deal.Pays)
            {
                long cents = SponsorMath.Payment(state, brand, deal);
                if (cents > 0)
                {
                    state.Finance.Earn(MoneyCategory.Sponsors, cents);
                    state.Finance.TotalSponsorCents += cents;
                    state.Finance.SponsorTodayCents += cents;
                    deal.PaidCents += cents;
                }
            }
            if (day >= deal.EndDay)
                End(ctx, deal, "The contract is over.");
        }
    }

    /// <summary>A new deal (not yet in any status) for a brand and a trail or the park (0).</summary>
    internal static Sponsorship New(WorldState state, SponsorType brand, int wayId)
    {
        var deal = new Sponsorship { Id = state.AllocateEntityId(), SponsorId = brand.Id, WayId = wayId };
        state.Sponsorships.Add(deal);
        // Keep the log short: the oldest finished deals go first.
        while (state.Sponsorships.Count > SponsorMath.MaxDeals && state.Sponsorships.FirstOrDefault(s => s.Status == SponsorshipStatus.Ended) is { } old)
            state.Sponsorships.Remove(old);
        return deal;
    }

    private static void MakeOffer(SimContext ctx, Sponsorship deal)
    {
        deal.Status = SponsorshipStatus.Offered;
        deal.ExpiresTick = ctx.Tick + (long)ctx.State.SponsorRules.OfferDays * GameTime.MinutesPerDay;
        ctx.Publish(new SponsorOffered(ctx.Tick, deal.Id, deal.SponsorId, deal.WayId));
    }

    /// <summary>
    /// Ends a deal (or turns an application or offer down): the trail gets its name back, an unbuilt sign's job is
    /// cancelled, the sign comes down.
    /// </summary>
    internal static void End(SimContext ctx, Sponsorship deal, string note)
    {
        var state = ctx.State;
        bool wasActive = deal.Status == SponsorshipStatus.Active;
        deal.Status = SponsorshipStatus.Ended;
        deal.Note = note;
        deal.SignBuilt = false;
        if (Jobs.Find(state, deal.SignJobId) is { } job)
            Jobs.Cancel(ctx, job);
        deal.SignJobId = 0;
        if (wasActive && !deal.IsPark && state.Ways.FirstOrDefault(w => w.Id == deal.WayId) is { } trail && deal.OriginalName.Length > 0
            && Commands.RenameTrailCommand.CannotName(state, deal.OriginalName, trail.Id) is null)
        {
            trail.Name = deal.OriginalName;
            ctx.Publish(new TrailRenamed(ctx.Tick, trail.Id, trail.Name));
        }
        if (wasActive) ctx.Publish(new SponsorshipEnded(ctx.Tick, deal.Id, note));
    }
}
