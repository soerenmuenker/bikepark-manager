using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Bikes as a business (see <see cref="FacilityRules"/>), all at bike shops: renting a fleet bike on arrival, its wear,
/// returning it, broken bikes (a crash that only hurt the bike) fixed in the workshop or swapped for another rental bike,
/// workshop services (tune-up, suspension setup) with parts sold on top, and the fleet serviced at night.
/// </summary>
internal static class Bikes
{
    /// <summary>The rental product a station sells, or null.</summary>
    public static ProductType? RentalProduct(WorldState state, FacilityType type, Facility station) =>
        FacilityMath.ProductsOf(state, type, station).FirstOrDefault(p => p.Rental);

    /// <summary>Hands a renter the bike: paid at the park's price, an e-bike if the fleet is.</summary>
    public static void Rent(SimContext ctx, Guest guest, Facility station, FacilityType type, RentalBike bike, ProductType rental)
    {
        FacilityMath.Sell(ctx.State, ctx.Tick, station, guest, rental);
        bike.RentedBy = guest.Id;
        guest.RentalBikeId = bike.Id;
        guest.EBike = FacilityMath.EBikes(type, station);
        ctx.State.Stats.TotalRenters++;
    }

    /// <summary>A rental bike wears: a little each run (more on hard trails), a lot in a crash.</summary>
    public static void Wear(WorldState state, Guest guest, int wear)
    {
        if (FacilityMath.FindRentalBike(state, guest.RentalBikeId) is { } found)
            found.Bike.ConditionPermille = Math.Max(0, found.Bike.ConditionPermille - wear);
    }

    /// <summary>The guest gives their rental bike back (going home or swapping it); worn out, it is retired.</summary>
    public static void Return(SimContext ctx, Guest guest)
    {
        if (FacilityMath.FindRentalBike(ctx.State, guest.RentalBikeId) is { } found)
        {
            found.Bike.RentedBy = 0;
            if (found.Bike.ConditionPermille <= 0)
            {
                found.Station.Fleet.Remove(found.Bike);
                ctx.State.Stats.BikesRetired++;
                ctx.Publish(new RentalBikeRetired(ctx.Tick, found.Station.Id, found.Bike.Id));
            }
        }
        guest.RentalBikeId = 0;
    }

    /// <summary>
    /// A guest with a broken bike, between laps: a renter swaps it at the shop it came from (free, if a bike is free
    /// there); an own bike goes to a bike shop in reach with a free workbench, if they take its price (seated for the
    /// repair, maybe buying parts too). True if it is being fixed or was swapped; otherwise the guest goes home
    /// (<see cref="GuestSystem"/>).
    /// </summary>
    public static bool TryFix(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        if (guest.RentalBikeId != 0 || guest.EBike)
        {
            if (FacilityMath.FindRentalBike(state, guest.RentalBikeId) is not { } found) return false;
            int stationId = found.Station.Id;
            if (FacilityMath.FreeRentalBike(state, stationId) is not { } swap) return false;
            Return(ctx, guest);
            swap.Bike.RentedBy = guest.Id;
            guest.RentalBikeId = swap.Bike.Id;
            guest.BikeBroken = false;
            ctx.Publish(new BikeFixed(ctx.Tick, guest.Id, stationId, Swapped: true));
            return true;
        }
        var point = FacilityMath.GuestPoint(state, ctx.Network, guest);
        foreach (var (shop, type) in FacilityMath.InReach(state, point, FacilityKind.BikeShop))
        {
            if (!FacilityMath.HasFreeSeat(state, shop, type)) continue;
            if (FacilityMath.ProductsOf(state, type, shop).FirstOrDefault(p => p.Repair) is not { } repair) continue;
            long price = FacilityMath.PriceAt(state, repair, shop);
            if (guest.CashCents < price || !ctx.Rng.ChancePermille(FacilityMath.BuyFactorAt(state, repair, shop))) return false;
            FacilityMath.Sell(state, ctx.Tick, shop, guest, repair);
            ctx.Publish(new ProductSold(ctx.Tick, guest.Id, shop.Id, repair.Id, price));
            guest.BikeBroken = false;
            AtTheBench(ctx, guest, shop, repair);
            state.Stats.TotalRepairs++;
            ctx.Publish(new BikeFixed(ctx.Tick, guest.Id, shop.Id, Swapped: false));
            return true;
        }
        Catering.NoteNoSeat(state, point, FacilityKind.BikeShop);
        return false;
    }

    /// <summary>
    /// Once per visit, a guest with their own bike between laps near a bike shop is offered its workshop services (catalog
    /// order: tune-up, suspension setup), each with its chance × how much their skill group likes it × the price's buy
    /// factor, if a workbench is free (the reserved one stays free for crash repairs). True if they sit down at a bench.
    /// </summary>
    public static bool TryService(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        if (guest.WorkshopAsked || guest.RentalBikeId != 0 || guest.EBike || guest.BikeBroken) return false;
        var shops = FacilityMath.InReach(state, FacilityMath.GuestPoint(state, ctx.Network, guest), FacilityKind.BikeShop);
        if (shops.Count == 0) return false;
        guest.WorkshopAsked = true;
        var (shop, type) = shops[0];
        foreach (var service in FacilityMath.ProductsOf(state, type, shop))
        {
            if (!service.IsService) continue;
            int reserved = shop.ReserveBench ? 1 : 0;
            if (FacilityMath.SeatsTaken(state, shop) + reserved >= FacilityMath.Workbenches(type, shop)) return false;
            long price = FacilityMath.PriceAt(state, service, shop);
            if (guest.CashCents < price) continue;
            long chance = (long)service.ServiceChancePermille * FacilityMath.GroupFactorPermille(state, service, shop, guest) / 1000
                          * FacilityMath.BuyFactorAt(state, service, shop) / 1000;
            if (!ctx.Rng.ChancePermille((int)Math.Min(1000, chance))) continue;
            FacilityMath.Sell(state, ctx.Tick, shop, guest, service);
            ctx.Publish(new WorkshopServiceSold(ctx.Tick, guest.Id, shop.Id, service.Id, price));
            AtTheBench(ctx, guest, shop, service);
            return true;
        }
        return false;
    }

    /// <summary>The guest waits at the workbench while the work is done; parts may be sold on top.</summary>
    private static void AtTheBench(SimContext ctx, Guest guest, Facility shop, ProductType work)
    {
        guest.Activity = RiderActivity.Eating; // waiting at the workbench
        guest.FacilityId = shop.Id;
        guest.BusyUntilTick = ctx.Tick + ctx.Rng.Range(work.MinMinutes, work.MaxMinutes + 1);
        if (FacilityMath.FindType(ctx.State, shop.TypeId) is not { } type) return;
        foreach (var parts in FacilityMath.ProductsOf(ctx.State, type, shop))
        {
            if (parts.WithServicePermille <= 0) continue;
            long price = FacilityMath.PriceAt(ctx.State, parts, shop);
            if (guest.CashCents < price) continue;
            long chance = (long)parts.WithServicePermille * FacilityMath.BuyFactorAt(ctx.State, parts, shop) / 1000;
            if (!ctx.Rng.ChancePermille((int)Math.Min(1000, chance))) continue;
            FacilityMath.Sell(ctx.State, ctx.Tick, shop, guest, parts);
            ctx.Publish(new ProductSold(ctx.Tick, guest.Id, shop.Id, parts.Id, price));
        }
    }

    /// <summary>
    /// At the end of the day the workshop of each bike shop with fleet care on services its rental bikes: each workbench
    /// restores <see cref="FacilityRules.FleetCarePerBenchPermille"/> of condition, the worst bikes first, for
    /// <see cref="FacilityRules.FleetCareCentsPerPermille"/> of parts (bike costs). No randomness.
    /// </summary>
    public static void FleetCare(WorldState state)
    {
        var rules = state.FacilityRules;
        foreach (var shop in state.Facilities)
        {
            if (FacilityMath.FindType(state, shop.TypeId) is not { Kind: FacilityKind.BikeShop } type) continue;
            shop.FleetCareLastNight = 0;
            if (shop.Fleet.Count == 0 || !FacilityMath.ServiceOn(state, type, shop, FacilityMath.FleetCare)) continue;
            int budget = FacilityMath.Workbenches(type, shop) * rules.FleetCarePerBenchPermille;
            foreach (var bike in shop.Fleet.Where(b => b.RentedBy == 0).OrderBy(b => b.ConditionPermille).ThenBy(b => b.Id).ToList())
            {
                if (budget <= 0) break;
                int add = Math.Min(budget, 1000 - bike.ConditionPermille);
                if (add <= 0) continue;
                bike.ConditionPermille += add;
                budget -= add;
                shop.FleetCareLastNight += add;
            }
            long cost = (long)shop.FleetCareLastNight * rules.FleetCareCentsPerPermille;
            if (cost > 0) state.Finance.Spend(MoneyCategory.BikeCosts, cost);
        }
    }
}
