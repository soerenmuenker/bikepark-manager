using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Facilities;

public enum FacilityKind : byte
{
    /// <summary>Food and drink: lunch (with a limited number of seats), snacks, coffee, beer.</summary>
    Bar = 0,

    /// <summary>Merchandise: guests on their way home buy something, the happier the likelier.</summary>
    Shop = 1,

    /// <summary>Pitches for campers: guests who stay a few nights and ride again each morning.</summary>
    Campground = 2,

    /// <summary>A rental station: a fleet of bikes the park bought, rented by the day to guests without a bike.</summary>
    Rental = 3,

    /// <summary>A service shop: workbenches where guests' broken bikes are fixed, so they can ride on.</summary>
    Service = 4,
}

/// <summary>A building model from <c>data/facility_types.json</c>, copied into the world.</summary>
public sealed class FacilityType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public FacilityKind Kind { get; set; }
    public long BuildCostCents { get; set; }
    public long UpkeepPerDayCents { get; set; }

    /// <summary>The pad it stands on.</summary>
    public int LengthMeters { get; set; } = 10;
    public int WidthMeters { get; set; } = 8;

    /// <summary>Park level needed to build it (a sandbox has everything).</summary>
    public int RequiredLevel { get; set; }

    /// <summary>What it sells (<see cref="ProductType"/> ids), before upgrades.</summary>
    public List<string> Products { get; set; } = [];

    /// <summary>Guests who can sit at the same time (bars: lunch and après-bike).</summary>
    public int Seats { get; set; }

    /// <summary>Chance that a perfectly happy guest leaving buys something, permille (shops; scaled by mood and price).</summary>
    public int BuyChancePermille { get; set; }

    /// <summary>Campers it sleeps (campgrounds).</summary>
    public int Pitches { get; set; }

    /// <summary>Mood its campers wake up with (campgrounds: showers, huts).</summary>
    public int MorningMood { get; set; }

    /// <summary>Rental stations: what one more fleet bike costs, and how many the rack holds.</summary>
    public long BikeCostCents { get; set; }
    public int MaxFleet { get; set; }

    /// <summary>Upgrades bought one after the other (<see cref="Facility.Level"/>), each adding to the building.</summary>
    public List<FacilityUpgrade> Upgrades { get; set; } = [];

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"facilityTypes[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("facilityTypes: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (!Enum.IsDefined(Kind)) errors.Add($"{p}.kind is unknown");
        if (BuildCostCents < 0 || UpkeepPerDayCents < 0) errors.Add($"{p}: costs must be >= 0");
        if (LengthMeters is < 4 or > 60 || WidthMeters is < 4 or > 60) errors.Add($"{p}: lengthMeters and widthMeters must be within 4..60");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel)
            errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        if (Products.Count == 0) errors.Add($"{p}.products: it must sell something");
        if (Kind == FacilityKind.Bar && Seats is < 1 or > 1000) errors.Add($"{p}.seats must be within 1..1000");
        if (Kind == FacilityKind.Campground && Pitches is < 1 or > 1000) errors.Add($"{p}.pitches must be within 1..1000");
        if (Pitches is < 0 or > 1000 || MorningMood is < 0 or > 1000) errors.Add($"{p}: pitches and morningMood within 0..1000");
        if (Kind == FacilityKind.Rental && (MaxFleet is < 1 or > 500 || BikeCostCents <= 0)) errors.Add($"{p}: a rental station needs maxFleet 1..500 and bikeCostCents > 0");
        if (Kind == FacilityKind.Service && Seats is < 1 or > 100) errors.Add($"{p}.seats (workbenches) must be within 1..100");
        if (BuyChancePermille is < 0 or > 1000) errors.Add($"{p}.buyChancePermille must be within 0..1000");
        foreach (var upgrade in Upgrades) errors.AddRange(upgrade.Validate(p));
        return errors;
    }
}

/// <summary>One step of a building's upgrades: what it costs and what it adds.</summary>
public sealed class FacilityUpgrade
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long CostCents { get; set; }

    /// <summary>Added upkeep per day.</summary>
    public long UpkeepPerDayCents { get; set; }
    public int RequiredLevel { get; set; }

    /// <summary>More seats, new products, a better buy chance (shops) and longer après-bike stays.</summary>
    public int Seats { get; set; }
    public List<string> Products { get; set; } = [];
    public int BuyChancePermille { get; set; }
    public int ApresMinutes { get; set; }
    public int Pitches { get; set; }
    public int MorningMood { get; set; }

    /// <summary>A bigger rack (rental stations), and whether the fleet becomes e-bikes.</summary>
    public int MaxFleet { get; set; }
    public bool EBikes { get; set; }

    public List<string> Validate(string owner)
    {
        var errors = new List<string>();
        string p = $"{owner}.upgrades[{Name}]";
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{owner}.upgrades: name is required");
        if (CostCents < 0 || UpkeepPerDayCents < 0) errors.Add($"{p}: costs must be >= 0");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel) errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        if (Seats is < 0 or > 1000 || BuyChancePermille is < 0 or > 1000 || ApresMinutes is < 0 or > 600)
            errors.Add($"{p}: seats, buyChancePermille within 0..1000, apresMinutes within 0..600");
        if (Pitches is < 0 or > 1000 || MorningMood is < 0 or > 1000) errors.Add($"{p}: pitches and morningMood within 0..1000");
        return errors;
    }
}

/// <summary>A bar or shop the park built next to a gravel path or hub; it serves the guests nearby.</summary>
public sealed class Facility
{
    public int Id { get; set; }
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public int TerrainEditId { get; set; }

    /// <summary>Upgrades bought (0 = as built; the first <see cref="FacilityType.Upgrades"/> up to this count).</summary>
    public int Level { get; set; }

    public long TotalRevenueCents { get; set; }
    public long RevenueTodayCents { get; set; }

    /// <summary>Sales (lunches, drinks, snacks, merch).</summary>
    public int Customers { get; set; }

    /// <summary>Rental stations: the bikes the park owns there, in id order.</summary>
    public List<RentalBike> Fleet { get; set; } = [];
}

/// <summary>One bike of a rental fleet: worn by rides and crashes, retired at 0 %.</summary>
public sealed class RentalBike
{
    public int Id { get; set; }

    /// <summary>0..1000.</summary>
    public int ConditionPermille { get; set; } = 1000;

    /// <summary>The guest riding it today (0: in the rack).</summary>
    public int RentedBy { get; set; }
}

/// <summary>Which facility serves a guest, what it sells at which price, and the money it brings. Pure.</summary>
public static class FacilityMath
{
    public static FacilityType? FindType(WorldState state, string typeId) => state.FacilityTypes.FirstOrDefault(t => t.Id == typeId);

    public static ProductType? FindProduct(WorldState state, string productId) => state.ProductTypes.FirstOrDefault(p => p.Id == productId);

    // ---------------------------------------------------------------- upgrades

    /// <summary>The upgrades a building has (in order).</summary>
    public static IEnumerable<FacilityUpgrade> Bought(FacilityType type, Facility facility) => type.Upgrades.Take(Math.Clamp(facility.Level, 0, type.Upgrades.Count));

    /// <summary>The next upgrade to buy, or null.</summary>
    public static FacilityUpgrade? NextUpgrade(FacilityType type, Facility facility) =>
        facility.Level >= 0 && facility.Level < type.Upgrades.Count ? type.Upgrades[facility.Level] : null;

    public static int Seats(FacilityType type, Facility facility) => type.Seats + Bought(type, facility).Sum(u => u.Seats);

    public static long Upkeep(FacilityType type, Facility facility) => type.UpkeepPerDayCents + Bought(type, facility).Sum(u => u.UpkeepPerDayCents);

    public static int BuyChancePermille(FacilityType type, Facility facility) =>
        Math.Min(1000, type.BuyChancePermille + Bought(type, facility).Sum(u => u.BuyChancePermille));

    public static int ApresBonusMinutes(FacilityType type, Facility facility) => Bought(type, facility).Sum(u => u.ApresMinutes);

    public static int Pitches(FacilityType type, Facility facility) => type.Pitches + Bought(type, facility).Sum(u => u.Pitches);

    public static int MorningMood(FacilityType type, Facility facility) => type.MorningMood + Bought(type, facility).Sum(u => u.MorningMood);

    public static int MaxFleet(FacilityType type, Facility facility) => type.MaxFleet + Bought(type, facility).Sum(u => u.MaxFleet);

    /// <summary>The fleet is e-bikes (after the upgrade).</summary>
    public static bool EBikes(FacilityType type, Facility facility) => Bought(type, facility).Any(u => u.EBikes);

    /// <summary>
    /// The best free rental bike of the park (stations in build order, the bike in the best condition, then the lowest id)
    /// with its station, or null.
    /// </summary>
    public static (Facility Station, FacilityType Type, RentalBike Bike)? FreeRentalBike(WorldState state, int stationId = 0)
    {
        foreach (var station in state.Facilities)
        {
            if (stationId != 0 && station.Id != stationId) continue;
            if (FindType(state, station.TypeId) is not { Kind: FacilityKind.Rental } type) continue;
            RentalBike? best = null;
            foreach (var bike in station.Fleet)
                if (bike.RentedBy == 0 && bike.ConditionPermille > 0 && (best is null || bike.ConditionPermille > best.ConditionPermille))
                    best = bike;
            if (best is not null) return (station, type, best);
        }
        return null;
    }

    /// <summary>The station a rental bike belongs to, and the bike, or null.</summary>
    public static (Facility Station, RentalBike Bike)? FindRentalBike(WorldState state, int bikeId)
    {
        if (bikeId == 0) return null;
        foreach (var station in state.Facilities)
            if (station.Fleet.FirstOrDefault(b => b.Id == bikeId) is { } bike)
                return (station, bike);
        return null;
    }

    /// <summary>Why the park can't buy that many bikes for the station now, or null.</summary>
    public static string? CannotBuyBikes(WorldState state, Facility station, int count)
    {
        if (FindType(state, station.TypeId) is not { Kind: FacilityKind.Rental } type) return $"{station.Name} is no rental station.";
        if (count is < 1 or > 50) return "Buy 1 to 50 bikes at a time.";
        int max = MaxFleet(type, station);
        if (station.Fleet.Count + count > max) return $"The rack holds {max} bikes ({station.Fleet.Count} there).";
        long cost = count * BikeCost(state, type);
        if (state.Finance.MoneyCents < cost) return $"Not enough money: {count} bikes cost {cost / 100:N0} €.";
        return null;
    }

    /// <summary>Pitches booked: every camper registered there, asleep or out riding (a pitch is theirs for the whole stay).</summary>
    public static int PitchesTaken(WorldState state, Facility camp) => state.Guests.Count(g => g.CampgroundId == camp.Id);

    /// <summary>The first campground (build order) with a free pitch selling a night, with that product, or null.</summary>
    public static (Facility Camp, FacilityType Type, ProductType Night)? FreeCampground(WorldState state)
    {
        foreach (var camp in state.Facilities)
        {
            if (FindType(state, camp.TypeId) is not { Kind: FacilityKind.Campground } type) continue;
            if (PitchesTaken(state, camp) >= Pitches(type, camp)) continue;
            if (ProductsOf(state, type, camp).FirstOrDefault(p => p.Overnight) is { } night) return (camp, type, night);
        }
        return null;
    }

    /// <summary>Everything paid for the building (built and upgraded).</summary>
    public static long InvestedCents(FacilityType type, Facility facility) => type.BuildCostCents + Bought(type, facility).Sum(u => u.CostCents);

    /// <summary>What building it costs now (a sponsor may give a discount).</summary>
    public static long BuildCost(WorldState state, FacilityType type) =>
        Sponsors.SponsorMath.Discounted(state, Sponsors.SponsorBenefitKind.BuildingDiscount, type.BuildCostCents, type.Id);

    /// <summary>What an upgrade costs now (a sponsor's building discount counts here too).</summary>
    public static long UpgradeCost(WorldState state, FacilityType type, FacilityUpgrade upgrade) =>
        Sponsors.SponsorMath.Discounted(state, Sponsors.SponsorBenefitKind.BuildingDiscount, upgrade.CostCents, type.Id);

    /// <summary>What one more fleet bike costs now (a bike brand may give a discount).</summary>
    public static long BikeCost(WorldState state, FacilityType type) =>
        Sponsors.SponsorMath.Discounted(state, Sponsors.SponsorBenefitKind.BikeDiscount, type.BikeCostCents);

    /// <summary>True if the building sells the product (as built or after an upgrade).</summary>
    public static bool Sells(FacilityType type, Facility facility, string productId) =>
        type.Products.Contains(productId) || Bought(type, facility).Any(u => u.Products.Contains(productId));

    /// <summary>The products it sells, in the order of <see cref="WorldState.ProductTypes"/> (lunch and coffee before snacks).</summary>
    public static IEnumerable<ProductType> ProductsOf(WorldState state, FacilityType type, Facility facility) =>
        state.ProductTypes.Where(p => Sells(type, facility, p.Id));

    /// <summary>Why the park can't buy the building's next upgrade now, or null.</summary>
    public static string? CannotUpgrade(WorldState state, int level, FacilityType type, Facility facility)
    {
        if (NextUpgrade(type, facility) is not { } next) return $"{facility.Name} is fully upgraded.";
        if (state.Rules.UnlockLevel(level) < next.RequiredLevel)
            return $"The {next.Name.ToLowerInvariant()} needs park level {next.RequiredLevel} (you are level {level}).";
        if (state.Finance.MoneyCents < UpgradeCost(state, type, next))
            return $"Not enough money: the {next.Name.ToLowerInvariant()} costs {UpgradeCost(state, type, next) / 100:N0} €.";
        return null;
    }

    // ---------------------------------------------------------------- prices

    /// <summary>The park's price for a product (its reference price unless set).</summary>
    public static long PriceOf(WorldState state, ProductType product) =>
        state.Park.Prices.FirstOrDefault(p => p.ProductId == product.Id)?.PriceCents ?? product.ReferencePriceCents;

    /// <summary>
    /// How the price changes the chance to buy, permille: 1000 at the reference price, less above it
    /// (<see cref="ProductType.ElasticityPermille"/> per reference price), more below it (at most 1500).
    /// </summary>
    public static int BuyFactorPermille(ProductType product, long priceCents)
    {
        long reference = Math.Max(1, product.ReferencePriceCents);
        long factor = 1000 + (reference - priceCents) * product.ElasticityPermille / reference;
        return (int)Math.Clamp(factor, 0, 1500);
    }

    /// <summary>Mood a guest loses paying <paramref name="priceCents"/>: <see cref="FacilityRules.RipOffMood"/> per reference price above it.</summary>
    public static int RipOffMood(FacilityRules rules, ProductType product, long priceCents) =>
        priceCents <= product.ReferencePriceCents ? 0
            : (int)Math.Min(1000, (priceCents - product.ReferencePriceCents) * rules.RipOffMood / Math.Max(1, product.ReferencePriceCents));

    // ---------------------------------------------------------------- where

    /// <summary>
    /// Where a guest between laps is: their hub, their spot on a way, or the valley station of the lift whose queue they
    /// are in (null: on the way, or nowhere).
    /// </summary>
    public static PointCm? GuestPoint(WorldState state, WayNetwork network, Guest guest)
    {
        if (guest.Activity == RiderActivity.Queuing)
            return state.Lifts.FirstOrDefault(l => l.Queue.Contains(guest.Id)) is { } lift ? PadCenter(state, lift.Valley.TerrainEditId) : null;
        if (guest.Activity is not (RiderActivity.Idle or RiderActivity.Eating or RiderActivity.Apres)) return null; // camping: asleep
        if (guest.LocationHubId != 0)
            return network.FindHub(guest.LocationHubId) is { } hub ? new PointCm(hub.Pad.CenterX, hub.Pad.CenterZ) : null;
        if (guest.LocationWayId == 0 || !network.TryGetGeometry(guest.LocationWayId, out var geometry)) return null;
        var at = geometry.PositionAt(guest.LocationCm);
        return new PointCm(at.X, at.Z);
    }

    private static PointCm? PadCenter(WorldState state, int terrainEditId) =>
        LiftNetwork.Pad(state, terrainEditId) is { } pad ? new PointCm(pad.CenterX, pad.CenterZ) : null;

    /// <summary>The park's facilities of a kind serving a point (within <see cref="FacilityRules.ServeReachMeters"/>), nearest first.</summary>
    public static List<(Facility Facility, FacilityType Type)> InReach(WorldState state, PointCm? point, FacilityKind kind)
    {
        var found = new List<(Facility Facility, FacilityType Type, long Distance)>();
        if (point is not { } at) return [];
        long reach = state.FacilityRules.ServeReachMeters * 100L;
        foreach (var facility in state.Facilities)
        {
            if (FindType(state, facility.TypeId) is not { } type || type.Kind != kind) continue;
            if (LiftNetwork.Pad(state, facility.TerrainEditId) is not { } pad) continue;
            long d = pad.DistanceOutside(at.X, at.Z);
            if (d <= reach) found.Add((facility, type, d));
        }
        // Stable: nearest first, then build order.
        return found.OrderBy(f => f.Distance).Select(f => (f.Facility, f.Type)).ToList();
    }

    /// <summary>
    /// True if a built gravel path or a hub (station, parking lot, platform) is within <paramref name="reachCm"/> of the pad:
    /// guests and supplies can get there.
    /// </summary>
    public static bool HasAccess(WayNetwork network, TerrainPad pad, long reachCm)
    {
        foreach (var hub in network.Hubs)
            if (PadGap(hub.Pad, pad) <= reachCm) return true;
        foreach (var way in network.Ways)
        {
            if (way.Kind != WayKind.AccessPath || !way.Built || !network.TryGetGeometry(way.Id, out var geometry)) continue;
            for (int i = 0; i < geometry.SampleCount; i++)
                if (pad.DistanceOutside(geometry.Xs[i], geometry.Zs[i]) <= reachCm) return true;
        }
        return false;
    }

    /// <summary>About the gap between two pads (corners and centres of each against the other).</summary>
    private static long PadGap(TerrainPad a, TerrainPad b)
    {
        long gap = b.DistanceOutside(a.CenterX, a.CenterZ);
        foreach (var c in a.Corners()) gap = Math.Min(gap, b.DistanceOutside(c.X, c.Z));
        gap = Math.Min(gap, a.DistanceOutside(b.CenterX, b.CenterZ));
        foreach (var c in b.Corners()) gap = Math.Min(gap, a.DistanceOutside(c.X, c.Z));
        return gap;
    }

    /// <summary>Guests sitting at the bar right now (lunch or après-bike).</summary>
    public static int SeatsTaken(WorldState state, Facility bar) =>
        state.Guests.Count(g => g.FacilityId == bar.Id && g.Activity is RiderActivity.Eating or RiderActivity.Apres);

    public static bool HasFreeSeat(WorldState state, Facility bar, FacilityType type) => SeatsTaken(state, bar) < Seats(type, bar);

    /// <summary>
    /// True if lunch and snacks need a bar: the scenario has bars to build. Scenarios without facility content keep
    /// the old rule (lunch and snacks anywhere, at the park's prices).
    /// </summary>
    public static bool BarsNeeded(WorldState state) => state.FacilityTypes.Any(t => t.Kind == FacilityKind.Bar);

    /// <summary>Why the park can't build this type now (level, money), or null.</summary>
    public static string? CannotBuild(WorldState state, int level, FacilityType type)
    {
        if (state.Rules.UnlockLevel(level) < type.RequiredLevel)
            return $"The {type.Name.ToLowerInvariant()} needs park level {type.RequiredLevel} (you are level {level}).";
        if (state.Finance.MoneyCents < BuildCost(state, type))
            return $"Not enough money: the {type.Name.ToLowerInvariant()} costs {BuildCost(state, type) / 100:N0} €.";
        return null;
    }

    /// <summary>The bar or shop whose pad is at the point (within <paramref name="marginCm"/>), or null.</summary>
    public static Facility? AtPoint(WorldState state, long x, long z, long marginCm = 0) =>
        state.Facilities.FirstOrDefault(f => LiftNetwork.Pad(state, f.TerrainEditId) is { } pad && pad.DistanceOutside(x, z) <= marginCm);

    /// <summary>The money category a product's sales are booked under.</summary>
    public static MoneyCategory CategoryOf(ProductType product) =>
        product.Overnight ? MoneyCategory.Camping
        : product.Clip ? MoneyCategory.Clips
        : product.Rental ? MoneyCategory.Rentals
        : product.Repair ? MoneyCategory.Repairs
        : product.Merch || product.Protection ? MoneyCategory.Merch
        : product.IsDrink ? MoneyCategory.Drinks : MoneyCategory.Food;

    /// <summary>Where the goods a sale uses are booked.</summary>
    private static MoneyCategory GoodsOf(ProductType product) =>
        product.Merch || product.Protection ? MoneyCategory.MerchGoods
        : product.Overnight ? MoneyCategory.CampUpkeep
        : product.Rental || product.Repair ? MoneyCategory.BikeCosts
        : MoneyCategory.FoodGoods;

    /// <summary>
    /// A guest buys a product at a facility at the park's price: revenue for the park and the facility, the goods' cost,
    /// sales stats, the product's effects and a little lost mood (and value) when it was overpriced.
    /// </summary>
    public static long Sell(WorldState state, long tick, Facility? facility, Guest guest, ProductType product)
    {
        long price = PriceOf(state, product);
        var finance = state.Finance;
        guest.CashCents -= price;
        finance.Earn(CategoryOf(product), price);
        if (facility is not null)
        {
            facility.TotalRevenueCents += price;
            facility.RevenueTodayCents += price;
            facility.Customers++;
        }
        // A sponsor (brewery, bike brand, ...) may supply the goods cheaper.
        long goods = Sponsors.SponsorMath.Discounted(state, Sponsors.SponsorBenefitKind.GoodsDiscount, price * product.GoodsCostPermille / 1000, product.Id);
        if (goods > 0)
            finance.Spend(GoodsOf(product), goods);
        if (product.Clip)
        {
            finance.TotalClipCents += price;
            finance.ClipTodayCents += price;
        }
        else if (product.Overnight)
        {
            finance.TotalCampingCents += price;
            finance.CampingTodayCents += price;
        }
        else if (product.Rental || product.Repair)
        {
            if (product.Rental) finance.TotalRentalCents += price;
            else finance.TotalRepairCents += price;
            if (product.Rental) finance.RentalTodayCents += price;
            else finance.RepairTodayCents += price;
        }
        else if (product.Merch || product.Protection)
        {
            finance.TotalMerchCents += price;
            finance.MerchTodayCents += price;
            finance.TotalMerchCostCents += goods;
        }
        else if (product.IsDrink)
        {
            finance.TotalDrinksCents += price;
            finance.DrinksTodayCents += price;
        }
        else
        {
            // Lunch in the food total, snacks apart (both in today's food money).
            if (product.Lunch) finance.TotalFoodCents += price;
            else finance.TotalSnackCents += price;
            finance.FoodTodayCents += price;
        }
        var sales = finance.ProductSales.FirstOrDefault(s => s.ProductId == product.Id);
        if (sales is null) finance.ProductSales.Add(sales = new ProductSales { ProductId = product.Id });
        sales.Sold++;
        sales.SoldToday++;
        sales.RevenueCents += price;
        sales.RevenueTodayCents += price;

        guest.Energy = Math.Min(1000, guest.Energy + product.Energy);
        int boost = product.Happiness * Sponsors.SponsorMath.Benefit(state, Sponsors.SponsorBenefitKind.MoodBoost, product.Id) / 1000;
        guest.Happiness = Math.Clamp(guest.Happiness + product.Happiness + boost - RipOffMood(state.FacilityRules, product, price), 0, 1000);
        guest.Alcohol = Math.Min(1000, guest.Alcohol + product.Alcohol);
        if (product.Protection) guest.Protected = true;
        if (product.CaffeineMinutes > 0) guest.CaffeineUntilTick = Math.Max(guest.CaffeineUntilTick, tick + product.CaffeineMinutes);
        guest.OverpaidCents += Math.Max(0, price - product.ReferencePriceCents);
        return price;
    }
}
