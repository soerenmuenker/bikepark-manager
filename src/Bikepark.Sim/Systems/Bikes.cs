using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Bikes as a business (see <see cref="FacilityRules"/>): renting a fleet bike on arrival, its wear, returning it, and
/// broken bikes (a crash that only hurt the bike) fixed at a service shop or swapped at the rental station.
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
    /// A guest with a broken bike, between laps: a renter swaps it at the station it came from (free, if a bike is free
    /// there); an own bike goes to a service shop in reach with a free workbench, if they take its price (seated for the
    /// repair). True if it is being fixed or was swapped; otherwise the guest goes home (<see cref="GuestSystem"/>).
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
        foreach (var (shop, type) in FacilityMath.InReach(state, FacilityMath.GuestPoint(state, ctx.Network, guest), FacilityKind.Service))
        {
            if (!FacilityMath.HasFreeSeat(state, shop, type)) continue;
            if (FacilityMath.ProductsOf(state, type, shop).FirstOrDefault(p => p.Repair) is not { } repair) continue;
            long price = FacilityMath.PriceOf(state, repair);
            if (guest.CashCents < price || !ctx.Rng.ChancePermille(FacilityMath.BuyFactorPermille(repair, price))) return false;
            FacilityMath.Sell(state, ctx.Tick, shop, guest, repair);
            ctx.Publish(new ProductSold(ctx.Tick, guest.Id, shop.Id, repair.Id, price));
            guest.BikeBroken = false;
            guest.Activity = RiderActivity.Eating; // waiting at the workbench
            guest.FacilityId = shop.Id;
            guest.BusyUntilTick = ctx.Tick + ctx.Rng.Range(repair.MinMinutes, repair.MaxMinutes + 1);
            state.Stats.TotalRepairs++;
            ctx.Publish(new BikeFixed(ctx.Tick, guest.Id, shop.Id, Swapped: false));
            return true;
        }
        return false;
    }
}
