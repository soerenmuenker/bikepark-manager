using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Facilities;

public enum FacilityKind : byte
{
    /// <summary>Food and drink: lunch (with a limited number of seats), snacks, coffee, beer.</summary>
    Bar = 0,

    /// <summary>
    /// A bike shop: departments on its shelves (merch, clothing, protectors, parts, bikes), a workshop whose workbenches fix
    /// broken bikes and do services, and (after the upgrade) a rental fleet.
    /// </summary>
    BikeShop = 1,

    /// <summary>Pitches for campers: guests who stay a few nights and ride again each morning.</summary>
    Campground = 2,
}

/// <summary>A bike shop's range (<see cref="Facility.Range"/>): what its departments cost and who buys there.</summary>
public enum ShopRange : byte
{
    Budget = 0,
    Standard = 1,
    Premium = 2,
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

    /// <summary>Bike shops: how many departments it can stock, and the workshop's workbenches.</summary>
    public int ShelfSlots { get; set; }
    public int Workbenches { get; set; }

    /// <summary>Campers it sleeps (campgrounds).</summary>
    public int Pitches { get; set; }

    /// <summary>Mood its campers wake up with (campgrounds: showers, huts).</summary>
    public int MorningMood { get; set; }

    /// <summary>Bike shops: what one more fleet bike costs, and how many the rack holds (the rental counter upgrade adds it).</summary>
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
        if (Kind != FacilityKind.BikeShop && (ShelfSlots != 0 || Workbenches != 0 || MaxFleet != 0 || Upgrades.Any(u => u.ShelfSlots != 0 || u.Workbenches != 0 || u.MaxFleet != 0 || u.EBikes)))
            errors.Add($"{p}: shelf slots, workbenches and rental bikes are for bike shops");
        if (Kind == FacilityKind.BikeShop)
        {
            if (ShelfSlots is < 1 or > 20 || Workbenches is < 1 or > 100) errors.Add($"{p}: a bike shop needs shelfSlots 1..20 and workbenches 1..100");
            if (MaxFleet is < 0 or > 500 || Upgrades.Sum(u => u.MaxFleet) + MaxFleet > 500) errors.Add($"{p}: maxFleet within 0..500");
            if ((MaxFleet > 0 || Upgrades.Any(u => u.MaxFleet > 0)) && BikeCostCents <= 0) errors.Add($"{p}: a rental fleet needs bikeCostCents > 0");
        }
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

    /// <summary>More seats, new products (bike shops: departments and services to offer), longer après-bike stays.</summary>
    public int Seats { get; set; }
    public List<string> Products { get; set; } = [];
    public int ApresMinutes { get; set; }
    public int Pitches { get; set; }
    public int MorningMood { get; set; }

    /// <summary>Bike shops: more shelf slots and workbenches, a (bigger) rack, and whether the fleet becomes e-bikes.</summary>
    public int ShelfSlots { get; set; }
    public int Workbenches { get; set; }
    public int MaxFleet { get; set; }
    public bool EBikes { get; set; }

    /// <summary>Presentation only: which extension of the building the game draws for it (unknown looks draw nothing).</summary>
    public string Look { get; set; } = "";

    public List<string> Validate(string owner)
    {
        var errors = new List<string>();
        string p = $"{owner}.upgrades[{Name}]";
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{owner}.upgrades: name is required");
        if (CostCents < 0 || UpkeepPerDayCents < 0) errors.Add($"{p}: costs must be >= 0");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel) errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        if (Seats is < 0 or > 1000 || ApresMinutes is < 0 or > 600) errors.Add($"{p}: seats within 0..1000, apresMinutes within 0..600");
        if (ShelfSlots is < 0 or > 20 || Workbenches is < 0 or > 100 || MaxFleet is < 0 or > 500)
            errors.Add($"{p}: shelfSlots within 0..20, workbenches within 0..100, maxFleet within 0..500");
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
    public int CustomersToday { get; set; }

    /// <summary>Guests who wanted a seat, workbench or bike here and found none (today and all time).</summary>
    public int TurnedAwayToday { get; set; }
    public long TurnedAway { get; set; }

    /// <summary>Sales today above the product's reference price (the guest felt ripped off a little).</summary>
    public int OverpricedToday { get; set; }

    /// <summary>Sales per product at this building, in the order first sold.</summary>
    public List<ProductSales> Sales { get; set; } = [];

    /// <summary>Bike shops with a rental counter: the bikes the park owns there, in id order.</summary>
    public List<RentalBike> Fleet { get; set; } = [];

    // ---- Bike shops (see FacilityMath: Departments, Services) ----

    /// <summary>The departments on its shelves (department product ids, at most <see cref="FacilityMath.ShelfSlots"/>).</summary>
    public List<string> Departments { get; set; } = [];

    /// <summary>Budget, standard or premium: prices, goods, upkeep and who likes to buy here.</summary>
    public ShopRange Range { get; set; } = ShopRange.Standard;

    /// <summary>Workshop services switched off (service product ids, or <see cref="FacilityMath.FleetCare"/>).</summary>
    public List<string> ServicesOff { get; set; } = [];

    /// <summary>Services never take the last free workbench: it stays free for crash repairs.</summary>
    public bool ReserveBench { get; set; }

    /// <summary>Fleet condition restored by the workshop last night (permille, summed over the bikes).</summary>
    public int FleetCareLastNight { get; set; }
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

    /// <summary>The building's upkeep as built and upgraded (before a bike shop's range).</summary>
    public static long Upkeep(FacilityType type, Facility facility) => type.UpkeepPerDayCents + Bought(type, facility).Sum(u => u.UpkeepPerDayCents);

    /// <summary>What the building costs a day: its upkeep, scaled by a bike shop's range.</summary>
    public static long Upkeep(WorldState state, FacilityType type, Facility facility) =>
        type.Kind == FacilityKind.BikeShop ? Upkeep(type, facility) * RangeOf(state, facility).UpkeepFactorPermille / 1000 : Upkeep(type, facility);

    // ---------------------------------------------------------------- bike shops

    /// <summary>The workshop service of looking after the rental fleet at night (<see cref="Facility.ServicesOff"/>).</summary>
    public const string FleetCare = "fleetCare";

    public static int ShelfSlots(FacilityType type, Facility facility) => type.ShelfSlots + Bought(type, facility).Sum(u => u.ShelfSlots);

    public static int Workbenches(FacilityType type, Facility facility) => type.Workbenches + Bought(type, facility).Sum(u => u.Workbenches);

    /// <summary>Guests a building can seat at a time: a bar's seats, a bike shop's workbenches.</summary>
    public static int Places(FacilityType type, Facility facility) => type.Kind == FacilityKind.BikeShop ? Workbenches(type, facility) : Seats(type, facility);

    /// <summary>True if the building rents bikes (a bike shop with its rental counter).</summary>
    public static bool HasRental(FacilityType type, Facility facility) => type.Kind == FacilityKind.BikeShop && MaxFleet(type, facility) > 0;

    /// <summary>The departments the shop could stock now (unlocked, catalog order).</summary>
    public static IEnumerable<ProductType> UnlockedDepartments(WorldState state, FacilityType type, Facility facility) =>
        state.ProductTypes.Where(p => p.Department && Sells(type, facility, p.Id));

    /// <summary>The workshop services unlocked (tune-up, suspension setup; catalog order), switched on or not.</summary>
    public static IEnumerable<ProductType> UnlockedServices(WorldState state, FacilityType type, Facility facility) =>
        state.ProductTypes.Where(p => p.IsService && Sells(type, facility, p.Id));

    /// <summary>True if the shop offers the service (unlocked and not switched off; fleet care: with a rental counter).</summary>
    public static bool ServiceOn(WorldState state, FacilityType type, Facility facility, string service) =>
        !facility.ServicesOff.Contains(service)
        && (service == FleetCare ? HasRental(type, facility) : UnlockedServices(state, type, facility).Any(p => p.Id == service));

    /// <summary>The first departments unlocked in catalog order, as many as fit on the shelves (a new shop's stock).</summary>
    public static List<string> DefaultDepartments(WorldState state, FacilityType type, Facility facility) =>
        UnlockedDepartments(state, type, facility).Take(ShelfSlots(type, facility)).Select(p => p.Id).ToList();

    /// <summary>Why the shop can't stock these departments, or null.</summary>
    public static string? CannotStock(WorldState state, Facility facility, IReadOnlyList<string> departments)
    {
        if (FindType(state, facility.TypeId) is not { Kind: FacilityKind.BikeShop } type) return $"{facility.Name} is no bike shop.";
        if (departments.Distinct().Count() != departments.Count) return "Each department only once.";
        int slots = ShelfSlots(type, facility);
        if (departments.Count > slots) return $"{facility.Name} has {slots} shelf slots.";
        foreach (string id in departments)
            if (!UnlockedDepartments(state, type, facility).Any(p => p.Id == id))
                return $"{facility.Name} can't stock '{id}' (yet).";
        return null;
    }

    /// <summary>The rules of the shop's range (standard factors without content).</summary>
    public static ShopRangeRules RangeOf(WorldState state, Facility facility)
    {
        var ranges = state.FacilityRules.ShopRanges;
        int index = (int)facility.Range;
        return index < ranges.Count ? ranges[index] : ShopRangeRules.Neutral;
    }

    /// <summary>How much a guest of their skill group likes buying the product here: the product's and the range's liking, permille.</summary>
    public static int GroupFactorPermille(WorldState state, ProductType product, Facility? facility, Guest guest)
    {
        var group = Reputation.ReviewMath.GroupOf(state.ReputationRules, guest.Skill);
        long factor = product.Groups.For(group);
        if (facility is not null && product.Department && FindType(state, facility.TypeId)?.Kind == FacilityKind.BikeShop)
            factor = factor * RangeOf(state, facility).Groups.For(group) / 1000;
        return (int)factor;
    }

    /// <summary>True if the guest already bought this department's goods during the visit (each once).</summary>
    public static bool HasBought(Guest guest, ProductType product) =>
        product.Protection ? guest.Protected : product.Department && guest.ShopItems.Contains(product.Id);

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
            if (FindType(state, station.TypeId) is not { Kind: FacilityKind.BikeShop } type || !HasRental(type, station)) continue;
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
        if (FindType(state, station.TypeId) is not { Kind: FacilityKind.BikeShop } type) return $"{station.Name} is no bike shop.";
        if (!HasRental(type, station)) return $"{station.Name} has no rental counter yet.";
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

    /// <summary>True if the building has the product (as built or after an upgrade; a bike shop may not stock or offer it).</summary>
    public static bool Sells(FacilityType type, Facility facility, string productId) =>
        type.Products.Contains(productId) || Bought(type, facility).Any(u => u.Products.Contains(productId));

    /// <summary>
    /// The products it sells, in the order of <see cref="WorldState.ProductTypes"/> (lunch and coffee before snacks). A bike
    /// shop sells the departments on its shelves and the services switched on.
    /// </summary>
    public static IEnumerable<ProductType> ProductsOf(WorldState state, FacilityType type, Facility facility) =>
        state.ProductTypes.Where(p => Sells(type, facility, p.Id)
                                      && (type.Kind != FacilityKind.BikeShop
                                          || (p.Department ? facility.Departments.Contains(p.Id) : !facility.ServicesOff.Contains(p.Id))));

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

    /// <summary>A bike shop's department scales with its range (budget cheaper, premium dearer); everything else is as is.</summary>
    private static long RangeScaled(WorldState state, ProductType product, Facility? facility, long cents) =>
        facility is not null && product.Department && FindType(state, facility.TypeId)?.Kind == FacilityKind.BikeShop
            ? cents * RangeOf(state, facility).PriceFactorPermille / 1000
            : cents;

    /// <summary>What the product costs at this building: the park's price, scaled by a bike shop's range.</summary>
    public static long PriceAt(WorldState state, ProductType product, Facility? facility) => RangeScaled(state, product, facility, PriceOf(state, product));

    /// <summary>The price guests find fair at this building (the reference price, scaled by a bike shop's range).</summary>
    public static long ReferenceAt(WorldState state, ProductType product, Facility? facility) =>
        RangeScaled(state, product, facility, product.ReferencePriceCents);

    /// <summary>The price's buy factor at this building (<see cref="BuyFactorPermille(ProductType, long, long)"/> against its fair price).</summary>
    public static int BuyFactorAt(WorldState state, ProductType product, Facility? facility) =>
        BuyFactorPermille(product, PriceAt(state, product, facility), ReferenceAt(state, product, facility));

    /// <summary>
    /// How the price changes the chance to buy, permille: 1000 at the reference price, less above it
    /// (<see cref="ProductType.ElasticityPermille"/> per reference price), more below it (at most 1500).
    /// </summary>
    public static int BuyFactorPermille(ProductType product, long priceCents) => BuyFactorPermille(product, priceCents, product.ReferencePriceCents);

    /// <summary>The buy factor against a given fair price (a bike shop's range moves it).</summary>
    public static int BuyFactorPermille(ProductType product, long priceCents, long referenceCents)
    {
        long reference = Math.Max(1, referenceCents);
        long factor = 1000 + (reference - priceCents) * product.ElasticityPermille / reference;
        return (int)Math.Clamp(factor, 0, 1500);
    }

    /// <summary>Mood a guest loses paying <paramref name="priceCents"/>: <see cref="FacilityRules.RipOffMood"/> per reference price above it.</summary>
    public static int RipOffMood(FacilityRules rules, ProductType product, long priceCents) =>
        RipOffMood(rules, priceCents, product.ReferencePriceCents);

    private static int RipOffMood(FacilityRules rules, long priceCents, long referenceCents) =>
        priceCents <= referenceCents ? 0 : (int)Math.Min(1000, (priceCents - referenceCents) * rules.RipOffMood / Math.Max(1, referenceCents));

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

    /// <summary>Guests sitting at the bar right now (lunch or après-bike), or waiting at a bike shop's workbenches.</summary>
    public static int SeatsTaken(WorldState state, Facility bar) =>
        state.Guests.Count(g => g.FacilityId == bar.Id && g.Activity is RiderActivity.Eating or RiderActivity.Apres);

    public static bool HasFreeSeat(WorldState state, Facility bar, FacilityType type) => SeatsTaken(state, bar) < Places(type, bar);

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
        : product.IsWorkshop ? MoneyCategory.Repairs
        : product.Merch || product.Protection ? MoneyCategory.Merch
        : product.IsDrink ? MoneyCategory.Drinks : MoneyCategory.Food;

    /// <summary>Where the goods a sale uses are booked.</summary>
    private static MoneyCategory GoodsOf(ProductType product) =>
        product.Merch || product.Protection ? MoneyCategory.MerchGoods
        : product.Overnight ? MoneyCategory.CampUpkeep
        : product.Rental || product.IsWorkshop ? MoneyCategory.BikeCosts
        : MoneyCategory.FoodGoods;

    /// <summary>
    /// A guest buys a product at a facility at the park's price (a bike shop's range scales its departments): revenue for
    /// the park and the facility, the goods' cost, sales stats, the product's effects and a little lost mood (and value)
    /// when it was overpriced. Paid from the guest's cash, unless by card (bikes).
    /// </summary>
    public static long Sell(WorldState state, long tick, Facility? facility, Guest guest, ProductType product)
    {
        long price = PriceAt(state, product, facility);
        long reference = ReferenceAt(state, product, facility);
        var finance = state.Finance;
        if (!product.ByCard) guest.CashCents -= price;
        finance.Earn(CategoryOf(product), price);
        if (facility is not null)
        {
            facility.TotalRevenueCents += price;
            facility.RevenueTodayCents += price;
            facility.Customers++;
            facility.CustomersToday++;
            if (price > reference) facility.OverpricedToday++;
            var own = facility.Sales.FirstOrDefault(s => s.ProductId == product.Id);
            if (own is null) facility.Sales.Add(own = new ProductSales { ProductId = product.Id });
            own.Sold++;
            own.SoldToday++;
            own.RevenueCents += price;
            own.RevenueTodayCents += price;
            if (!guest.FacilitiesUsed.Contains(facility.Id)) guest.FacilitiesUsed.Add(facility.Id);
        }
        // A sponsor (brewery, bike brand, ...) may supply the goods cheaper.
        // Goods: of the standard price, scaled by the range's goods (budget ranges are cheaper to stock, premium dearer).
        long goods = PriceOf(state, product) * product.GoodsCostPermille / 1000;
        if (facility is not null && product.Department && FindType(state, facility.TypeId)?.Kind == FacilityKind.BikeShop)
            goods = goods * RangeOf(state, facility).GoodsFactorPermille / 1000;
        goods = Sponsors.SponsorMath.Discounted(state, Sponsors.SponsorBenefitKind.GoodsDiscount, goods, product.Id);
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
        else if (product.Rental || product.IsWorkshop)
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
        guest.Happiness = Math.Clamp(guest.Happiness + product.Happiness + boost - RipOffMood(state.FacilityRules, price, reference), 0, 1000);
        guest.Alcohol = Math.Min(1000, guest.Alcohol + product.Alcohol);
        if (product.Protection) guest.Protected = true;
        if (product.Department && !guest.ShopItems.Contains(product.Id)) guest.ShopItems.Add(product.Id);
        if (product.TuneUpPermille > 0) guest.TuneUpPermille = product.TuneUpPermille;
        if (product.RedBlackFun > 0) guest.RedBlackFun = Math.Max(guest.RedBlackFun, product.RedBlackFun);
        if (product.CaffeineMinutes > 0) guest.CaffeineUntilTick = Math.Max(guest.CaffeineUntilTick, tick + product.CaffeineMinutes);
        guest.OverpaidCents += Math.Max(0, price - reference);
        return price;
    }
}
