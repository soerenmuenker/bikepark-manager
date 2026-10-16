using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Commands;

/// <summary>Sets the park's price for a product (all bars and shops), between 0 and its <see cref="ProductType.MaxPriceCents"/>.</summary>
public sealed record SetProductPriceCommand(string ProductId, long PriceCents) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (FacilityMath.FindProduct(ctx.State, ProductId) is not { } product) return $"Unknown product '{ProductId}'.";
        if (PriceCents < 0) return "A price cannot be negative.";
        if (PriceCents > product.MaxPriceCents) return $"{product.Name} cannot cost more than {product.MaxPriceCents / 100.0:0.00} €.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var prices = ctx.State.Park.Prices;
        int index = prices.FindIndex(p => p.ProductId == ProductId);
        if (index >= 0) prices[index] = new ProductPrice(ProductId, PriceCents);
        else prices.Add(new ProductPrice(ProductId, PriceCents));
    }
}

/// <summary>Buys a building's next upgrade (<see cref="FacilityType.Upgrades"/>): paid at once, in use at once.</summary>
public sealed record UpgradeFacilityCommand(int FacilityId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Facilities.FirstOrDefault(f => f.Id == FacilityId) is not { } facility) return "No such building.";
        if (FacilityMath.FindType(state, facility.TypeId) is not { } type) return $"Unknown building '{facility.TypeId}'.";
        return FacilityMath.CannotUpgrade(state, Reputation.ParkProgress.CurrentLevel(state, ctx.Network), type, facility);
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var facility = state.Facilities.First(f => f.Id == FacilityId);
        var type = FacilityMath.FindType(state, facility.TypeId)!;
        var upgrade = FacilityMath.NextUpgrade(type, facility)!;
        facility.Level++;
        if (upgrade.CostCents > 0)
        {
            state.Finance.Invest(MoneyCategory.Buildings, upgrade.CostCents, ctx.Tick, $"{facility.Name}: {upgrade.Name}");
            state.Finance.TotalFacilityBuildCents += upgrade.CostCents;
        }
        ctx.Publish(new FacilityUpgraded(ctx.Tick, facility.Id, upgrade.Name, facility.Level));
    }
}

/// <summary>
/// From which minute of the day bars serve alcohol to riders (0 = all day, the closing minute = only après-bike, 1440 =
/// never to riders; après-bike after the last lap is always served).
/// </summary>
public sealed record SetBeerHoursCommand(int FromMinute) : ICommand
{
    public string? Validate(SimContext ctx) =>
        FromMinute is < 0 or > GameTime.MinutesPerDay ? $"The minute must be within 0..{GameTime.MinutesPerDay}." : null;

    public void Apply(SimContext ctx) => ctx.State.Park.BeerFromMinute = FromMinute;
}

/// <summary>
/// Buys bikes for a rental station's fleet (new, in perfect condition), at the station's price per bike, up to its rack
/// (<see cref="FacilityMath.MaxFleet"/>).
/// </summary>
public sealed record BuyRentalBikesCommand(int FacilityId, int Count) : ICommand
{
    public string? Validate(SimContext ctx) =>
        ctx.State.Facilities.FirstOrDefault(f => f.Id == FacilityId) is not { } station
            ? "No such building."
            : FacilityMath.CannotBuyBikes(ctx.State, station, Count);

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var station = state.Facilities.First(f => f.Id == FacilityId);
        var type = FacilityMath.FindType(state, station.TypeId)!;
        for (int i = 0; i < Count; i++)
            station.Fleet.Add(new RentalBike { Id = state.AllocateEntityId() });
        long cost = Count * type.BikeCostCents;
        state.Finance.Invest(MoneyCategory.Buildings, cost, ctx.Tick, $"{station.Name}: {Count} rental bike{(Count == 1 ? "" : "s")}");
        state.Finance.TotalFacilityBuildCents += cost;
    }
}
