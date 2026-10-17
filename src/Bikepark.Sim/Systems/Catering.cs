using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Safety;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Guests buying food and drink at bars (see <see cref="FacilityRules"/>): lunch, impulse buys between laps and on stops
/// on the way, après-bike after their last lap. All at the park's prices (<see cref="FacilityMath.PriceOf"/>).
/// </summary>
internal static class Catering
{
    /// <summary>Riders are served alcohol now (<see cref="ParkState.BeerFromMinute"/>).</summary>
    public static bool AlcoholServed(WorldState state, long tick) => GameTime.MinuteOfDay(tick) >= state.Park.BeerFromMinute;

    /// <summary>
    /// Maybe buys one thing at the nearest bar serving <paramref name="point"/>: products in catalog order, each with its
    /// impulse chance × <paramref name="scalePermille"/> × the price's buy factor (drinks with alcohol also × mood, and
    /// less the more the guest has had). Lunch and merch are not impulse buys. True if something was bought.
    /// </summary>
    public static bool TryImpulse(SimContext ctx, Guest guest, PointCm? point, int scalePermille, FacilityKind kind = FacilityKind.Bar)
    {
        var state = ctx.State;
        var bars = FacilityMath.InReach(state, point, kind);
        if (bars.Count == 0) return false;
        var (bar, type) = bars[0];
        foreach (var product in FacilityMath.ProductsOf(state, type, bar))
        {
            if (product.Lunch || product.Merch || product.ImpulsePermille <= 0 || guest.Energy >= product.WhenEnergyBelow) continue;
            if (product.Protection && guest.Protected) continue; // has some already
            if (product.Alcohol > 0 && !AlcoholServed(state, ctx.Tick)) continue;
            long price = FacilityMath.PriceOf(state, product);
            if (guest.CashCents < price) continue;
            long chance = (long)product.ImpulsePermille * scalePermille / 1000 * FacilityMath.BuyFactorPermille(product, price) / 1000;
            if (product.Alcohol > 0)
                chance = chance * Math.Clamp(guest.Happiness, 0, 1000) / 1000 * (1000 - Math.Clamp(guest.Alcohol, 0, 1000)) / 1000;
            if (!ctx.Rng.ChancePermille((int)Math.Min(1000, chance))) continue;
            Buy(ctx, bar, guest, product);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A bar at <paramref name="point"/> with a free seat serving lunch the guest can afford, with its lunch product, or null.
    /// </summary>
    public static (Facility Bar, ProductType Lunch)? LunchBar(WorldState state, Guest guest, PointCm? point)
    {
        foreach (var (bar, type) in FacilityMath.InReach(state, point, FacilityKind.Bar))
        {
            if (!FacilityMath.HasFreeSeat(state, bar, type)) continue;
            foreach (var product in FacilityMath.ProductsOf(state, type, bar))
                if (product.Lunch && guest.CashCents >= FacilityMath.PriceOf(state, product))
                    return (bar, product);
        }
        return null;
    }

    /// <summary>
    /// A guest looked for a seat (or workbench) at <paramref name="point"/> and found none: the nearest full building of the
    /// kind counts them as turned away.
    /// </summary>
    public static void NoteNoSeat(WorldState state, PointCm? point, FacilityKind kind)
    {
        foreach (var (facility, type) in FacilityMath.InReach(state, point, kind))
        {
            if (FacilityMath.HasFreeSeat(state, facility, type)) continue;
            facility.TurnedAwayToday++;
            facility.TurnedAway++;
            return;
        }
    }

    /// <summary>True if some bar of the park serves a lunch the guest could afford (worth riding on to find a seat).</summary>
    public static bool AnyLunchAffordable(WorldState state, Guest guest) =>
        state.Facilities.Any(f => FacilityMath.FindType(state, f.TypeId) is { Kind: FacilityKind.Bar } type
                                  && FacilityMath.ProductsOf(state, type, f).Any(p => p.Lunch && guest.CashCents >= FacilityMath.PriceOf(state, p)));

    /// <summary>The guest sits down for lunch at the bar: paid, seated until the lunch is over.</summary>
    public static void SitDownForLunch(SimContext ctx, Guest guest, Facility bar, ProductType lunch)
    {
        long paid = Buy(ctx, bar, guest, lunch);
        guest.HadLunch = true;
        guest.Activity = RiderActivity.Eating;
        guest.FacilityId = bar.Id;
        guest.BusyUntilTick = ctx.Tick + ctx.Rng.Range(lunch.MinMinutes, lunch.MaxMinutes + 1);
        ctx.Publish(new GuestAteLunch(ctx.Tick, guest.Id, paid, bar.Id));
    }

    /// <summary>
    /// The price decides whether a guest takes the lunch on offer: certain at or below the reference price, less likely
    /// above it (<see cref="FacilityMath.BuyFactorPermille"/>).
    /// </summary>
    public static bool AcceptsLunch(SimContext ctx, ProductType lunch) =>
        ctx.Rng.ChancePermille(FacilityMath.BuyFactorPermille(lunch, FacilityMath.PriceOf(ctx.State, lunch)));

    /// <summary>
    /// A rider at the end of a route leg (top of a lift, a junction on the way to their trail) passes a bar: once per lap
    /// they stop there, for lunch when it is due (seated: they go on with the next leg afterwards) or for an impulse buy
    /// with the stop scale (<see cref="FacilityRules.StopScalePermille"/>). True if they sat down (the route pauses).
    /// </summary>
    public static bool StopOnTheWay(SimContext ctx, WayNetwork network, Guest guest, RouteLeg finished, int nextLeg)
    {
        var state = ctx.State;
        if (guest.StoppedThisLap || state.FacilityRules.StopScalePermille <= 0 || state.Facilities.Count == 0) return false;
        var point = LegEnd(state, network, finished);
        if (FacilityMath.InReach(state, point, FacilityKind.Bar).Count == 0) return false;
        guest.StoppedThisLap = true;
        int minute = GameTime.MinuteOfDay(ctx.Tick);
        if (!guest.HadLunch && guest.LunchMinute >= 0 && minute >= guest.LunchMinute && LunchBar(state, guest, point) is { } lunch
            && AcceptsLunch(ctx, lunch.Lunch))
        {
            SitDownForLunch(ctx, guest, lunch.Bar, lunch.Lunch);
            guest.ResumeLeg = nextLeg;
            return true;
        }
        TryImpulse(ctx, guest, point, state.FacilityRules.StopScalePermille);
        return false;
    }

    /// <summary>Where a route leg ends: on its way, or at the hub a lift or walk arrives at.</summary>
    public static PointCm? LegEnd(WorldState state, WayNetwork network, RouteLeg leg)
    {
        if (leg.Kind == LegKind.Way)
        {
            if (!network.TryGetGeometry(leg.WayId, out var geometry)) return null;
            var at = geometry.PositionAt(leg.ToCm);
            return new PointCm(at.X, at.Z);
        }
        if (network.FindLink(leg.Kind, leg.WayId) is not { } link) return null;
        int hubId = leg.Kind == LegKind.Walk && leg.ToCm < leg.FromCm ? link.FromHubId : link.ToHubId;
        return network.FindHub(hubId) is { } hub ? new PointCm(hub.Pad.CenterX, hub.Pad.CenterZ) : null;
    }

    /// <summary>
    /// Après-bike: a guest going home who is near a bar serving alcohol with a free seat stays for a drink or a few, with
    /// <see cref="FacilityRules.ApresChancePermille"/> × mood × the price's buy factor, until
    /// <see cref="FacilityRules.ApresEndMinute"/> at the latest. True if they stay.
    /// </summary>
    public static bool TryApres(SimContext ctx, WayNetwork network, Guest guest)
    {
        var state = ctx.State;
        var rules = state.FacilityRules;
        int minute = GameTime.MinuteOfDay(ctx.Tick);
        if (rules.ApresEndMinute <= 0 || minute >= rules.ApresEndMinute || minute < state.Rules.OpenMinute) return false;
        if (guest.Activity != RiderActivity.Idle || guest.Injury != InjurySeverity.None || guest.Alcohol >= rules.TooDrunkAlcohol) return false;
        foreach (var (bar, type) in FacilityMath.InReach(state, FacilityMath.GuestPoint(state, network, guest), FacilityKind.Bar))
        {
            if (!FacilityMath.HasFreeSeat(state, bar, type)) continue;
            var drink = FacilityMath.ProductsOf(state, type, bar).FirstOrDefault(p => p.Alcohol > 0);
            if (drink is null) continue;
            long price = FacilityMath.PriceOf(state, drink);
            if (guest.CashCents < price) return false;
            long chance = (long)rules.ApresChancePermille * Math.Clamp(guest.Happiness, 0, 1000) / 1000 * FacilityMath.BuyFactorPermille(drink, price) / 1000;
            if (!ctx.Rng.ChancePermille((int)Math.Min(1000, chance))) return false;
            long end = GameTime.Day(ctx.Tick) * GameTime.MinutesPerDay + rules.ApresEndMinute;
            int stay = ctx.Rng.Range(rules.ApresMinMinutes, rules.ApresMaxMinutes + 1) + FacilityMath.ApresBonusMinutes(type, bar);
            guest.Activity = RiderActivity.Apres;
            guest.FacilityId = bar.Id;
            guest.BusyUntilTick = Math.Min(end, ctx.Tick + stay);
            Buy(ctx, bar, guest, drink);
            state.Stats.TotalApresGuests++;
            ctx.Publish(new ApresStarted(ctx.Tick, guest.Id, bar.Id, guest.BusyUntilTick));
            return true;
        }
        return false;
    }

    /// <summary>
    /// A minute of après-bike: one more drink every <see cref="FacilityRules.ApresDrinkEveryMinutes"/> (if they can pay).
    /// True when it is over (time is up, the bar is gone, or the evening ends).
    /// </summary>
    public static bool ApresMinute(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        var bar = state.Facilities.FirstOrDefault(f => f.Id == guest.FacilityId);
        if (bar is null || ctx.Tick >= guest.BusyUntilTick || !ParkSchedule.GuestsAllowed(state, ctx.Tick)) return true;
        int every = state.FacilityRules.ApresDrinkEveryMinutes;
        if ((guest.BusyUntilTick - ctx.Tick) % every == 0 && FacilityMath.FindType(state, bar.TypeId) is { } type
            && FacilityMath.ProductsOf(state, type, bar).FirstOrDefault(p => p.Alcohol > 0) is { } drink
            && guest.CashCents >= FacilityMath.PriceOf(state, drink))
            Buy(ctx, bar, guest, drink);
        return false;
    }

    /// <summary>
    /// A guest going home passes the shop nearest to where they are (or to the arrival base) and buys something with the
    /// shop's chance, scaled by their mood and the price.
    /// </summary>
    public static void BuyMerch(SimContext ctx, WayNetwork network, Guest guest)
    {
        var state = ctx.State;
        if (state.Facilities.Count == 0) return;
        var shops = FacilityMath.InReach(state, FacilityMath.GuestPoint(state, network, guest), FacilityKind.Shop);
        if (shops.Count == 0 && network.BaseHub is { } hub)
            shops = FacilityMath.InReach(state, new PointCm(hub.Pad.CenterX, hub.Pad.CenterZ), FacilityKind.Shop);
        if (shops.Count == 0) return;
        var (shop, type) = shops[0];
        var merch = FacilityMath.ProductsOf(state, type, shop).FirstOrDefault(p => p.Merch && guest.CashCents >= FacilityMath.PriceOf(state, p));
        if (merch is null) return;
        long price = FacilityMath.PriceOf(state, merch);
        long chance = (long)FacilityMath.BuyChancePermille(type, shop) * Math.Clamp(guest.Happiness, 0, 1000) / 1000
                      * FacilityMath.BuyFactorPermille(merch, price) / 1000;
        if (!ctx.Rng.ChancePermille((int)Math.Min(1000, chance))) return;
        Buy(ctx, shop, guest, merch);
        ctx.Publish(new MerchSold(ctx.Tick, guest.Id, shop.Id, price));
    }

    private static long Buy(SimContext ctx, Facility facility, Guest guest, ProductType product)
    {
        long price = FacilityMath.Sell(ctx.State, ctx.Tick, facility, guest, product);
        ctx.Publish(new ProductSold(ctx.Tick, guest.Id, facility.Id, product.Id, price));
        return price;
    }
}
