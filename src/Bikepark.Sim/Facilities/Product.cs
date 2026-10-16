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

    /// <summary>Sold in shops to guests going home (else a bar's food or drink).</summary>
    public bool Merch { get; set; }

    /// <summary>Chance per minute between laps (and, scaled up, per stop on the way) that a guest at a bar buys it.</summary>
    public int ImpulsePermille { get; set; }

    /// <summary>Only bought by guests with less energy than this (1000: always).</summary>
    public int WhenEnergyBelow { get; set; } = 1000;

    public bool IsDrink => Alcohol > 0 || CaffeineMinutes > 0;

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
        if (Lunch && Merch) errors.Add($"{p}: lunch and merch exclude each other");
        if (ImpulsePermille is < 0 or > 1000 || WhenEnergyBelow is < 0 or > 1000)
            errors.Add($"{p}: impulsePermille and whenEnergyBelow must be within 0..1000");
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
        return errors;
    }
}
