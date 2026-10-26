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
        long cost = FacilityMath.UpgradeCost(state, type, upgrade);
        if (cost > 0)
        {
            state.Finance.Invest(MoneyCategory.Buildings, cost, ctx.Tick, $"{facility.Name}: {upgrade.Name}");
            state.Finance.TotalFacilityBuildCents += cost;
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
/// Buys bikes for a bike shop's rental fleet (new, in perfect condition), at the shop's price per bike, up to its rack
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
        long cost = Count * FacilityMath.BikeCost(state, type);
        state.Finance.Invest(MoneyCategory.Buildings, cost, ctx.Tick, $"{station.Name}: {Count} rental bike{(Count == 1 ? "" : "s")}");
        state.Finance.TotalFacilityBuildCents += cost;
        ctx.Publish(new RentalBikesBought(ctx.Tick, station.Id, Count));
    }
}

/// <summary>Sets the departments on a bike shop's shelves (unlocked ones, at most its shelf slots; in any order).</summary>
public sealed record SetShopDepartmentsCommand(int FacilityId, List<string> Departments) : ICommand
{
    // Value equality for the list too (the event stream of two runs compares commands).
    public bool Equals(SetShopDepartmentsCommand? other) =>
        other is not null && FacilityId == other.FacilityId && Departments.SequenceEqual(other.Departments);

    public override int GetHashCode() => HashCode.Combine(FacilityId, Departments.Count);

    public string? Validate(SimContext ctx) =>
        ctx.State.Facilities.FirstOrDefault(f => f.Id == FacilityId) is not { } shop
            ? "No such building."
            : FacilityMath.CannotStock(ctx.State, shop, Departments);

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var shop = state.Facilities.First(f => f.Id == FacilityId);
        // Kept in catalog order, so the shelves read the same however the player picked them.
        shop.Departments = state.ProductTypes.Where(p => Departments.Contains(p.Id)).Select(p => p.Id).ToList();
    }
}

/// <summary>Sets a bike shop's range: budget, standard or premium.</summary>
public sealed record SetShopRangeCommand(int FacilityId, ShopRange Range) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Facilities.FirstOrDefault(f => f.Id == FacilityId) is not { } shop) return "No such building.";
        if (FacilityMath.FindType(state, shop.TypeId)?.Kind != FacilityKind.BikeShop) return $"{shop.Name} is no bike shop.";
        return Enum.IsDefined(Range) ? null : "Unknown range.";
    }

    public void Apply(SimContext ctx) => ctx.State.Facilities.First(f => f.Id == FacilityId).Range = Range;
}

/// <summary>
/// Switches a bike shop's workshop service on or off: an unlocked service product (tune-up, suspension setup) or
/// <see cref="FacilityMath.FleetCare"/> (with a rental counter). Crash repairs are always on.
/// </summary>
public sealed record SetWorkshopServiceCommand(int FacilityId, string Service, bool On) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Facilities.FirstOrDefault(f => f.Id == FacilityId) is not { } shop) return "No such building.";
        if (FacilityMath.FindType(state, shop.TypeId) is not { Kind: FacilityKind.BikeShop } type) return $"{shop.Name} is no bike shop.";
        bool known = Service == FacilityMath.FleetCare
            ? FacilityMath.HasRental(type, shop)
            : FacilityMath.UnlockedServices(state, type, shop).Any(p => p.Id == Service);
        return known ? null : $"{shop.Name} doesn't offer '{Service}'.";
    }

    public void Apply(SimContext ctx)
    {
        var shop = ctx.State.Facilities.First(f => f.Id == FacilityId);
        shop.ServicesOff.Remove(Service);
        if (!On) shop.ServicesOff.Add(Service);
    }
}

/// <summary>Keeps (or stops keeping) the last free workbench of a bike shop for crash repairs.</summary>
public sealed record SetReserveBenchCommand(int FacilityId, bool On) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Facilities.FirstOrDefault(f => f.Id == FacilityId) is not { } shop) return "No such building.";
        return FacilityMath.FindType(state, shop.TypeId)?.Kind == FacilityKind.BikeShop ? null : $"{shop.Name} is no bike shop.";
    }

    public void Apply(SimContext ctx) => ctx.State.Facilities.First(f => f.Id == FacilityId).ReserveBench = On;
}
