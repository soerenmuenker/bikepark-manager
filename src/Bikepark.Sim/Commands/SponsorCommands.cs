using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Sponsors;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Asks a brand to sponsor a trail (<paramref name="WayId"/>) or the whole park (0). The brand answers after
/// <see cref="SponsorRules.ApplyAnswerDays"/> days, at an opening: an offer if it fits then, else no (with the reason).
/// </summary>
public sealed record ApplySponsorCommand(string SponsorId, int WayId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (SponsorMath.Find(state, SponsorId) is not { } brand) return $"Unknown sponsor '{SponsorId}'.";
        if (SponsorMath.Open(state, SponsorId) is { } open)
            return open.Status == SponsorshipStatus.Active ? $"{brand.Name} already sponsors you." : $"You are already talking to {brand.Name}.";
        // One brand at a time per trail or park: running, offered or applied for.
        bool taken = state.Sponsorships.Any(s => s.WayId == WayId && s.Status != SponsorshipStatus.Ended);
        if (WayId == 0)
            return !brand.SponsorsParks ? $"{brand.Name} doesn't sponsor whole parks." : taken ? "The park already has a sponsor (or is talking to one)." : null;
        if (state.Ways.FirstOrDefault(w => w.Id == WayId) is not { Kind: WayKind.Trail, Built: true } trail) return "Only built trails can be sponsored.";
        return taken ? $"{trail.Name} already has a sponsor (or is talking to one)." : null;
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var deal = SponsorSystem.New(state, SponsorMath.Find(state, SponsorId)!, WayId);
        deal.Status = SponsorshipStatus.Applied;
        // Answered at an opening at least this many days from now.
        long day = GameTime.Day(ctx.Tick) + Math.Max(1, state.SponsorRules.ApplyAnswerDays);
        deal.AnswerTick = GameTime.TicksForDays(day) + state.Rules.OpenMinute;
    }
}

/// <summary>
/// Accepts a brand's offer: the trail takes the brand's name (the park: "… presented by …" once the sign stands) and the
/// crew puts up the sign at its entrance. It pays, and its benefits count, from the day the sign stands.
/// </summary>
public sealed record AcceptSponsorCommand(int SponsorshipId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Sponsorships.FirstOrDefault(s => s.Id == SponsorshipId) is not { Status: SponsorshipStatus.Offered } deal) return "No such offer.";
        if (SponsorMath.DealOn(state, deal.WayId) is not null) return deal.IsPark ? "The park already has a sponsor." : "The trail already has a sponsor.";
        if (!deal.IsPark && state.Ways.FirstOrDefault(w => w.Id == deal.WayId) is not { Built: true }) return "The trail is gone.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var deal = state.Sponsorships.First(s => s.Id == SponsorshipId);
        var brand = SponsorMath.Find(state, deal.SponsorId)!;
        long today = GameTime.Day(ctx.Tick);
        deal.Status = SponsorshipStatus.Active;
        deal.StartDay = today;
        deal.EndDay = today + (deal.IsPark ? brand.ParkDays : brand.Days);
        if (!deal.IsPark && state.Ways.FirstOrDefault(w => w.Id == deal.WayId) is { } trail)
        {
            deal.OriginalName = trail.Name;
            trail.Name = SponsorMath.SponsoredName(state, brand, trail);
            ctx.Publish(new TrailRenamed(ctx.Tick, trail.Id, trail.Name));
        }
        var job = Jobs.Queue(ctx, new Job
        {
            Kind = JobKind.BuildSign,
            WayId = deal.WayId,
            FeatureId = deal.Id,
            WorkMinutes = state.SponsorRules.SignWorkMinutes,
            MainWorkType = WorkType.Carpentry,
            Wood = state.SponsorRules.SignWood,
        });
        deal.SignJobId = job.Id;
        ctx.Publish(new SponsorshipStarted(ctx.Tick, deal.Id));
    }
}

/// <summary>Turns a brand's offer down.</summary>
public sealed record DeclineSponsorCommand(int SponsorshipId) : ICommand
{
    public string? Validate(SimContext ctx) =>
        ctx.State.Sponsorships.Any(s => s.Id == SponsorshipId && s.Status == SponsorshipStatus.Offered) ? null : "No such offer.";

    public void Apply(SimContext ctx) =>
        SponsorSystem.End(ctx, ctx.State.Sponsorships.First(s => s.Id == SponsorshipId), "You turned it down.");
}

/// <summary>Ends a running deal early (no penalty: the money just stops; the trail gets its name back).</summary>
public sealed record EndSponsorshipCommand(int SponsorshipId) : ICommand
{
    public string? Validate(SimContext ctx) =>
        ctx.State.Sponsorships.Any(s => s.Id == SponsorshipId && s.Status == SponsorshipStatus.Active) ? null : "No such deal.";

    public void Apply(SimContext ctx) =>
        SponsorSystem.End(ctx, ctx.State.Sponsorships.First(s => s.Id == SponsorshipId), "You ended it.");
}

/// <summary>Puts a camera at a built jump or drop (a photo spot), paid at once; riders filmed there may buy their clip.</summary>
public sealed record PlaceCameraCommand(int WayId, int FeatureId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Ways.FirstOrDefault(w => w.Id == WayId)?.Features.FirstOrDefault(f => f.Id == FeatureId) is not { } feature) return "No such feature.";
        if (!feature.Built) return "Build the feature first.";
        if (TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId) is not { } type || !Reputation.ReviewMath.IsJump(type.Kind))
            return "Cameras film jumps and drops.";
        if (feature.Camera) return "There is a camera already.";
        long cost = state.SponsorRules.CameraCostCents;
        if (state.Finance.MoneyCents < cost) return $"Not enough money: a camera costs {cost / 100:N0} €.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var way = state.Ways.First(w => w.Id == WayId);
        var feature = way.Features.First(f => f.Id == FeatureId);
        feature.Camera = true;
        long cost = state.SponsorRules.CameraCostCents;
        if (cost > 0) state.Finance.Invest(MoneyCategory.Buildings, cost, ctx.Tick, $"Camera on {way.Name}");
        ctx.Publish(new CameraPlaced(ctx.Tick, WayId, FeatureId, true));
    }
}

/// <summary>Takes a camera down (free).</summary>
public sealed record RemoveCameraCommand(int WayId, int FeatureId) : ICommand
{
    public string? Validate(SimContext ctx) =>
        ctx.State.Ways.FirstOrDefault(w => w.Id == WayId)?.Features.FirstOrDefault(f => f.Id == FeatureId) is { Camera: true } ? null : "No camera there.";

    public void Apply(SimContext ctx)
    {
        ctx.State.Ways.First(w => w.Id == WayId).Features.First(f => f.Id == FeatureId).Camera = false;
        ctx.Publish(new CameraPlaced(ctx.Tick, WayId, FeatureId, false));
    }
}
