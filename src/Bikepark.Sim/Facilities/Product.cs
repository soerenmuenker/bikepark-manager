using Bikepark.Sim.Reputation;

namespace Bikepark.Sim.Facilities;

/// <summary>
/// Something bars and shops sell (<c>data/products.json</c>, copied into the world). The park sets one price per product
/// (<see cref="State.ParkState.Prices"/>); guests compare it with <see cref="ReferencePriceCents"/>.
/// </summary>
public sealed class ProductType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The price guests find fair; the default price.</summary>
    public long ReferencePriceCents { get; set; }

    /// <summary>The highest price the park can set.</summary>
    public long MaxPriceCents { get; set; }

    /// <summary>What one sale costs the park in goods, permille of the price.</summary>
    public int GoodsCostPermille { get; set; }

    /// <summary>
    /// How much the price scares buyers off: each step of the reference price above it removes this share of the buy
    /// chance (1000: double the reference price, no sales), cheaper prices add the same (at most +50 %).
    /// </summary>
    public int ElasticityPermille { get; set; } = 1000;

    /// <summary>Energy (0..1000) and mood it brings.</summary>
    public int Energy { get; set; }
    public int Happiness { get; set; }

    /// <summary>Alcohol it adds to the guest (0..1000): bolder trail choices, more crashes (see <see cref="FacilityRules"/>).</summary>
    public int Alcohol { get; set; }

    /// <summary>A caffeine boost: climbing costs half the energy for this many minutes.</summary>
    public int CaffeineMinutes { get; set; }

    /// <summary>Lunch: eaten at a seat for <see cref="MinMinutes"/>..<see cref="MaxMinutes"/> once per visit.</summary>
    public bool Lunch { get; set; }
    public int MinMinutes { get; set; }
    public int MaxMinutes { get; set; }

    /// <summary>Booked as merch (park merch, clothing, bikes; else a bar's food or drink).</summary>
    public bool Merch { get; set; }

    // ---- Bike shops ----

    /// <summary>A bike shop department: takes a shelf slot; the player picks which ones a shop stocks.</summary>
    public bool Department { get; set; }

    /// <summary>Chance a perfectly happy guest going home buys it at a bike shop in reach (scaled by mood, price, group, range).</summary>
    public int HomeChancePermille { get; set; }

    /// <summary>Impulse chance per minute between laps while it rains (rain jackets), on top of <see cref="ImpulsePermille"/>.</summary>
    public int RainImpulsePermille { get; set; }

    /// <summary>How much each skill group likes buying it (permille; 0: never).</summary>
    public GroupFactors Groups { get; set; } = new();

    /// <summary>Paid by card: not from the cash the guest brought for the day (gear and bikes).</summary>
    public bool ByCard { get; set; }

    /// <summary>Bought with a repair or service at the same shop, with this chance (spare parts).</summary>
    public int WithServicePermille { get; set; }

    /// <summary>
    /// A workshop service (a workbench for <see cref="MinMinutes"/>..<see cref="MaxMinutes"/>): offered once per visit to
    /// guests with their own bike at the first stop near the shop, with this chance (scaled by group, price).
    /// </summary>
    public int ServiceChancePermille { get; set; }

    /// <summary>Tune-up: of the would-be bike-only crashes this share still breaks the bike, permille (0: no effect).</summary>
    public int TuneUpPermille { get; set; }

    /// <summary>Suspension setup: fun added to red and black trail segments and features for the visit.</summary>
    public int RedBlackFun { get; set; }

    /// <summary>A night at a campground (one per night, paid on arrival).</summary>
    public bool Overnight { get; set; }

    /// <summary>A rental bike for the day, a repair of a bike broken in a crash (a workbench for MinMinutes..MaxMinutes); bike shops.</summary>
    public bool Rental { get; set; }
    public bool Repair { get; set; }

    /// <summary>Protective gear: crashes of a guest wearing it are less often serious.</summary>
    public bool Protection { get; set; }

    /// <summary>A video clip from a photo spot, bought after a filmed run (no building sells it).</summary>
    public bool Clip { get; set; }

    /// <summary>Chance per minute between laps (and, scaled up, per stop on the way) that a guest at a bar buys it.</summary>
    public int ImpulsePermille { get; set; }

    /// <summary>Only bought by guests with less energy than this (1000: always).</summary>
    public int WhenEnergyBelow { get; set; } = 1000;

    public bool IsDrink => Alcohol > 0 || CaffeineMinutes > 0;

    /// <summary>A workshop service offered to guests (tune-up, suspension setup).</summary>
    public bool IsService => ServiceChancePermille > 0;

    /// <summary>Workshop money: repairs, services and the parts sold with them.</summary>
    public bool IsWorkshop => Repair || IsService || WithServicePermille > 0;

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"productTypes[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("productTypes: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (ReferencePriceCents <= 0 || MaxPriceCents < ReferencePriceCents) errors.Add($"{p}: 0 < referencePriceCents <= maxPriceCents");
        if (GoodsCostPermille is < 0 or > 1000) errors.Add($"{p}.goodsCostPermille must be within 0..1000");
        if (ElasticityPermille is < 0 or > 5000) errors.Add($"{p}.elasticityPermille must be within 0..5000");
        if (Energy is < 0 or > 1000 || Happiness is < 0 or > 1000 || Alcohol is < 0 or > 1000)
            errors.Add($"{p}: energy, happiness and alcohol must be within 0..1000");
        if (CaffeineMinutes is < 0 or > 600) errors.Add($"{p}.caffeineMinutes must be within 0..600");
        if (Lunch && (MinMinutes < 1 || MaxMinutes < MinMinutes || MaxMinutes > 240))
            errors.Add($"{p}: lunch minutes must satisfy 1 <= minMinutes <= maxMinutes <= 240");
        if ((Lunch ? 1 : 0) + (Merch ? 1 : 0) + (Overnight ? 1 : 0) + (Rental ? 1 : 0) + (Repair ? 1 : 0) + (Protection ? 1 : 0) + (Clip ? 1 : 0)
            + (IsService ? 1 : 0) + (WithServicePermille > 0 ? 1 : 0) > 1)
            errors.Add($"{p}: lunch, merch, overnight, rental, repair, protection, clip, services and parts exclude each other");
        if ((Repair || IsService) && (MinMinutes < 1 || MaxMinutes < MinMinutes || MaxMinutes > 240))
            errors.Add($"{p}: workshop minutes must satisfy 1 <= minMinutes <= maxMinutes <= 240");
        if (HomeChancePermille is < 0 or > 1000 || RainImpulsePermille is < 0 or > 1000 || WithServicePermille is < 0 or > 1000
            || ServiceChancePermille is < 0 or > 1000 || TuneUpPermille is < 0 or > 1000 || RedBlackFun is < 0 or > 1000)
            errors.Add($"{p}: homeChancePermille, rainImpulsePermille, withServicePermille, serviceChancePermille, tuneUpPermille and redBlackFun within 0..1000");
        if (!Department && (HomeChancePermille > 0 || RainImpulsePermille > 0 || WithServicePermille > 0 || ByCard))
            errors.Add($"{p}: homeChancePermille, rainImpulsePermille, withServicePermille and byCard are for departments");
        if (Department && (Lunch || Overnight || Rental || Repair || Clip || IsService || IsDrink))
            errors.Add($"{p}: a department sells goods (merch, clothing, protectors, parts, bikes)");
        errors.AddRange(Groups.Validate(p));
        if (ImpulsePermille is < 0 or > 1000 || WhenEnergyBelow is < 0 or > 1000)
            errors.Add($"{p}: impulsePermille and whenEnergyBelow must be within 0..1000");
        return errors;
    }
}

/// <summary>How much each skill group likes something, permille (1000: as is).</summary>
public sealed class GroupFactors
{
    public int Beginner { get; set; } = 1000;
    public int Intermediate { get; set; } = 1000;
    public int Expert { get; set; } = 1000;

    public int For(SkillGroup group) => group switch
    {
        SkillGroup.Beginner => Beginner,
        SkillGroup.Intermediate => Intermediate,
        _ => Expert,
    };

    public List<string> Validate(string owner) =>
        Beginner is < 0 or > 5000 || Intermediate is < 0 or > 5000 || Expert is < 0 or > 5000 ? [$"{owner}.groups: within 0..5000"] : [];
}

/// <summary>
/// A bike shop range (<see cref="ShopRange"/>): its departments' prices and fair prices, the goods and the upkeep scale by
/// these, and each skill group likes it this much.
/// </summary>
public sealed class ShopRangeRules
{
    public string Name { get; set; } = "";
    public int PriceFactorPermille { get; set; } = 1000;
    public int GoodsFactorPermille { get; set; } = 1000;
    public int UpkeepFactorPermille { get; set; } = 1000;
    public GroupFactors Groups { get; set; } = new();

    /// <summary>All factors 1000 (scenarios without ranges).</summary>
    public static readonly ShopRangeRules Neutral = new() { Name = "Standard" };

    public List<string> Validate(string owner)
    {
        var errors = new List<string>();
        string p = $"{owner}[{Name}]";
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{owner}: name is required");
        if (PriceFactorPermille is < 100 or > 5000 || GoodsFactorPermille is < 100 or > 5000 || UpkeepFactorPermille is < 100 or > 5000)
            errors.Add($"{p}: factors within 100..5000");
        errors.AddRange(Groups.Validate(p));
        return errors;
    }
}

/// <summary>The park's price for a product (products without one sell at their reference price).</summary>
public sealed record ProductPrice(string ProductId, long PriceCents);

/// <summary>Sales of one product (park-wide).</summary>
public sealed class ProductSales
{
    public string ProductId { get; set; } = "";
    public long Sold { get; set; }
    public int SoldToday { get; set; }
    public long RevenueCents { get; set; }
    public long RevenueTodayCents { get; set; }
}

/// <summary>How bars, shops and their guests behave (scenario content, copied into the world).</summary>
public sealed class FacilityRules
{
    /// <summary>A bar or shop needs a gravel path or a hub (station, parking lot, platform) this close to its pad.</summary>
    public int AccessReachMeters { get; set; } = 25;

    /// <summary>It serves guests between laps or stopping on the way this close to its pad.</summary>
    public int ServeReachMeters { get; set; } = 80;

    /// <summary>
    /// A rider passing a bar on the way to a trail (at a lift's top, a junction of their route) stops there with the
    /// products' impulse chances scaled by this (permille; once per lap; 0: no stops on the way).
    /// </summary>
    public int StopScalePermille { get; set; } = 8000;

    /// <summary>Mood lost when paying double the reference price (scaled by how much above it they paid).</summary>
    public int RipOffMood { get; set; } = 30;

    /// <summary>Alcohol lost per minute.</summary>
    public int AlcoholDecayPerMinute { get; set; } = 2;

    /// <summary>
    /// Dutch courage: a guest picks trails as if their skill were higher by alcohol × this / 1000 (crashes still roll
    /// against the real skill).
    /// </summary>
    public int BoldnessPermille { get; set; } = 400;

    /// <summary>Guests with at least this much alcohol stop riding and go home.</summary>
    public int TooDrunkAlcohol { get; set; } = 700;

    /// <summary>Crashes with at least this much alcohol count as under the influence.</summary>
    public int TipsyAlcohol { get; set; } = 150;

    // ---- Après-bike (off while ApresEndMinute is 0) ----

    /// <summary>Après-bike: guests going home may stay at a bar serving alcohol until this minute of the day (0 = off).</summary>
    public int ApresEndMinute { get; set; }

    /// <summary>Chance a perfectly happy guest going home stays for après-bike (scaled by mood and the price).</summary>
    public int ApresChancePermille { get; set; } = 400;
    public int ApresMinMinutes { get; set; } = 20;
    public int ApresMaxMinutes { get; set; } = 60;

    /// <summary>One more drink every this many minutes.</summary>
    public int ApresDrinkEveryMinutes { get; set; } = 20;

    // ---- Camping (only with a campground) ----

    /// <summary>Share of arriving guests who would camp (at the fair price, with a free pitch), permille.</summary>
    public int CampChancePermille { get; set; } = 150;

    /// <summary>Nights a camper stays.</summary>
    public int MinNights { get; set; } = 1;
    public int MaxNights { get; set; } = 3;

    /// <summary>Energy campers wake up with, and the mood a night outdoors brings (plus the campground's own).</summary>
    public int CampEnergyMin { get; set; } = 850;
    public int CampEnergyMax { get; set; } = 1000;
    public int CampMood { get; set; } = 30;

    // ---- Bikes (only with a bike shop) ----

    /// <summary>
    /// Bike shop ranges in <see cref="ShopRange"/> order (budget, standard, premium): price, goods and upkeep factors and how
    /// much each skill group likes buying there.
    /// </summary>
    public List<ShopRangeRules> ShopRanges { get; set; } =
    [
        new() { Name = "Budget", PriceFactorPermille = 700, GoodsFactorPermille = 650, UpkeepFactorPermille = 900,
                Groups = new() { Beginner = 1400, Intermediate = 1000, Expert = 500 } },
        new() { Name = "Standard" },
        new() { Name = "Premium", PriceFactorPermille = 1500, GoodsFactorPermille = 1500, UpkeepFactorPermille = 1300,
                Groups = new() { Beginner = 500, Intermediate = 1000, Expert = 1600 } },
    ];

    /// <summary>Protectors sell this much better (permille) to guests who have seen a crash.</summary>
    public int CrashSeenProtectionPermille { get; set; } = 2000;

    /// <summary>Fleet care: condition (permille, over the fleet) each workbench restores a night, and the parts per permille.</summary>
    public int FleetCarePerBenchPermille { get; set; } = 400;
    public int FleetCareCentsPerPermille { get; set; } = 25;

    /// <summary>Guests without a bike who come to rent one, in permille of the other arrivals (at the fair rental price, with a free bike).</summary>
    public int RenterSharePermille { get; set; } = 150;

    /// <summary>Renters are beginners: skill triangular in 0..this (on e-bikes: the usual mix).</summary>
    public int RenterSkillMax { get; set; } = 600;

    /// <summary>Wear of a rental bike per run (plus difficulty / 200), and per crash.</summary>
    public int RentalWearPerRun { get; set; } = 3;
    public int RentalCrashWear { get; set; } = 100;

    /// <summary>On an e-bike climbing costs this share of the energy (permille), and riders climb faster by 1000 / this, at most double.</summary>
    public int EBikeClimbPermille { get; set; } = 400;

    public List<string> Validate()
    {
        var errors = new List<string>();
        const string p = "facilityRules";
        if (AccessReachMeters is < 1 or > 200 || ServeReachMeters is < 1 or > 500) errors.Add($"{p}: accessReachMeters within 1..200, serveReachMeters within 1..500");
        if (StopScalePermille is < 0 or > 100_000) errors.Add($"{p}.stopScalePermille must be within 0..100000");
        if (RipOffMood is < 0 or > 1000) errors.Add($"{p}.ripOffMood must be within 0..1000");
        if (AlcoholDecayPerMinute is < 0 or > 1000 || BoldnessPermille is < 0 or > 5000) errors.Add($"{p}: alcoholDecayPerMinute within 0..1000, boldnessPermille within 0..5000");
        if (TooDrunkAlcohol is < 1 or > 1000 || TipsyAlcohol is < 1 or > 1000) errors.Add($"{p}: tooDrunkAlcohol and tipsyAlcohol within 1..1000");
        if (ApresEndMinute is < 0 or > Core.GameTime.MinutesPerDay) errors.Add($"{p}.apresEndMinute must be within the day");
        if (ApresChancePermille is < 0 or > 1000) errors.Add($"{p}.apresChancePermille must be within 0..1000");
        if (ApresMinMinutes < 1 || ApresMaxMinutes < ApresMinMinutes || ApresMaxMinutes > 600) errors.Add($"{p}: 1 <= apresMinMinutes <= apresMaxMinutes <= 600");
        if (ApresDrinkEveryMinutes is < 1 or > 600) errors.Add($"{p}.apresDrinkEveryMinutes must be within 1..600");
        if (CampChancePermille is < 0 or > 1000) errors.Add($"{p}.campChancePermille must be within 0..1000");
        if (MinNights < 1 || MaxNights < MinNights || MaxNights > 30) errors.Add($"{p}: 1 <= minNights <= maxNights <= 30");
        if (CampEnergyMin < 0 || CampEnergyMax < CampEnergyMin || CampEnergyMax > 1000 || CampMood is < 0 or > 1000)
            errors.Add($"{p}: 0 <= campEnergyMin <= campEnergyMax <= 1000, campMood within 0..1000");
        if (RenterSharePermille is < 0 or > 5000 || RenterSkillMax is < 0 or > 1000) errors.Add($"{p}: renterSharePermille within 0..5000, renterSkillMax within 0..1000");
        if (RentalWearPerRun is < 0 or > 1000 || RentalCrashWear is < 0 or > 1000) errors.Add($"{p}: rental wear within 0..1000");
        if (EBikeClimbPermille is < 1 or > 1000) errors.Add($"{p}.eBikeClimbPermille must be within 1..1000");
        if (ShopRanges.Count is not (0 or 3)) errors.Add($"{p}.shopRanges: budget, standard and premium (or none)");
        foreach (var range in ShopRanges) errors.AddRange(range.Validate($"{p}.shopRanges"));
        if (CrashSeenProtectionPermille is < 0 or > 10_000) errors.Add($"{p}.crashSeenProtectionPermille must be within 0..10000");
        if (FleetCarePerBenchPermille is < 0 or > 20_000 || FleetCareCentsPerPermille is < 0 or > 10_000)
            errors.Add($"{p}: fleetCarePerBenchPermille within 0..20000, fleetCareCentsPerPermille within 0..10000");
        return errors;
    }
}
