using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// A bar, shop, campground, rental station or service shop: what it does now, the prices of what it sells (park-wide),
/// its upgrades, its money, what its guests say about the park and what it does for them.
/// </summary>
internal sealed partial class FacilityInspector : InspectorWindow
{
    private const int BikeBatch = 5;

    public FacilityInspector(HudContext ctx, InspectTarget target) : base(ctx, target) { }

    private WorldState State => Ctx.Sim.State;
    private Facility? Facility => State.Facilities.FirstOrDefault(f => f.Id == Target.Id);
    private FacilityType? Type => Facility is { } f ? FacilityMath.FindType(State, f.TypeId) : null;

    public override bool IsGone => Facility is null || Type is null;

    protected override UiIcon Icon => FacilityMath.FindType(State, Facility?.TypeId ?? "")?.Kind switch
    {
        FacilityKind.Bar => UiIcon.Bar,
        FacilityKind.Campground => UiIcon.Tent,
        FacilityKind.Rental => UiIcon.Riders,
        FacilityKind.Service => UiIcon.Tool,
        _ => UiIcon.Shop,
    };

    protected override string TitleText() => Facility?.Name ?? "";

    protected override string SubtitleText() =>
        Type is { } type && Facility is { } f
            ? $"{type.Name}" + (f.Level > 0 ? $" · {string.Join(", ", FacilityMath.Bought(type, f).Select(u => u.Name))}" : "")
            : "";

    protected override Vector2? Location() =>
        Facility is { } f && State.TerrainEdits.FirstOrDefault(e => e.Id == f.TerrainEditId) is { } edit
            ? new Vector2(edit.Pad.CenterX / 100f, edit.Pad.CenterZ / 100f)
            : null;

    protected override string Signature() =>
        Facility is { } f ? $"{f.Level}|{Ctx.Kpi.Level}|{f.Fleet.Count}|{State.Reputation.TotalReviews / 10}" : "";

    protected override void Setup()
    {
        var type = Type!;
        AddTab("Overview", Overview);
        if (FacilityMath.ProductsOf(State, type, Facility!).Any()) AddTab("Prices", Prices);
        if (type.Kind == FacilityKind.Rental) AddTab("Fleet", Fleet);
        if (type.Upgrades.Count > 0) AddTab("Upgrades", Upgrades);
        AddTab("Finances", Finances);
        if (State.ReputationRules.Enabled) AddTab("Reviews", Reviews);
        AddTab("Info", Info);
    }

    // ---------------------------------------------------------------- tabs

    private void Overview(VBoxContainer body)
    {
        var type = Type!;
        string Busy()
        {
            if (Facility is not { } f) return "";
            return type.Kind switch
            {
                FacilityKind.Bar or FacilityKind.Service => $"{FacilityMath.SeatsTaken(State, f)} / {FacilityMath.Seats(type, f)}",
                FacilityKind.Campground => $"{FacilityMath.PitchesTaken(State, f)} / {FacilityMath.Pitches(type, f)}",
                FacilityKind.Rental => $"{f.Fleet.Count(b => b.RentedBy == 0)} / {f.Fleet.Count}",
                _ => $"{FacilityMath.BuyChancePermille(type, f) / 10} %",
            };
        }
        string busyCaption = type.Kind switch
        {
            FacilityKind.Bar => "Seats taken",
            FacilityKind.Service => "Benches busy",
            FacilityKind.Campground => "Pitches booked",
            FacilityKind.Rental => "Bikes free",
            _ => "Guests who buy",
        };
        Tiles(body,
            (busyCaption, Busy),
            ("Customers today", () => $"{Facility?.CustomersToday ?? 0}"),
            ("Revenue today", () => UiTheme.Money(Facility?.RevenueTodayCents ?? 0)),
            ("Turned away today", () => $"{Facility?.TurnedAwayToday ?? 0}"));

        var status = Text(body, "", 12);
        Live(() => status.Text = StatusLine());
        Heading(body, "Sells");
        var sells = Text(body, "", 12, UiTheme.Text);
        Live(() =>
        {
            if (Facility is not { } f) return;
            sells.Text = string.Join(" · ", FacilityMath.ProductsOf(State, type, f).Select(p =>
                $"{p.Name} {UiTheme.MoneyExact(FacilityMath.PriceOf(State, p))}"));
        });
    }

    /// <summary>The one thing to know right now (busy, full, cheap, idle).</summary>
    private string StatusLine()
    {
        if (Facility is not { } f || Type is not { } type) return "";
        if (f.TurnedAwayToday > 0 && type.Kind is FacilityKind.Bar or FacilityKind.Service)
            return $"{f.TurnedAwayToday} times today a guest found no free {(type.Kind == FacilityKind.Bar ? "seat" : "workbench")}: an upgrade adds more.";
        if (f.TurnedAwayToday > 0 && type.Kind == FacilityKind.Rental)
            return $"{f.TurnedAwayToday} guests came for a bike today and found none free: buy more bikes (Fleet).";
        if (type.Kind == FacilityKind.Rental && f.Fleet.Count == 0)
            return "No bikes yet: buy some in the Fleet tab.";
        if (f.OverpricedToday > 0)
            return $"{f.OverpricedToday} of today's sales were above the fair price: guests feel ripped off (Prices).";
        if (f.Customers == 0)
            return "No customers yet. Guests buy at the nearest bar or shop within reach of where they are.";
        return "Running fine.";
    }

    private void Prices(VBoxContainer body)
    {
        var type = Type!;
        Text(body, "One price per product for the whole park: it applies at every building that sells it.", 12);
        foreach (var product in FacilityMath.ProductsOf(State, type, Facility!))
        {
            var p = product;
            Lever(body, p.Name,
                () => UiTheme.MoneyExact(FacilityMath.PriceOf(State, p)),
                () => ChangePrice(p, -PriceStep(p)), () => ChangePrice(p, PriceStep(p)),
                $"Cheaper {p.Name.ToLowerInvariant()}: more guests buy it",
                $"Dearer {p.Name.ToLowerInvariant()}: fewer buy it, and above the fair price guests feel ripped off",
                () => PriceDetail(p), ProductTooltip(p));
        }
        if (FacilityMath.ProductsOf(State, type, Facility!).Any(p => p.Alcohol > 0))
        {
            int from = State.Park.BeerFromMinute;
            Text(body, $"Alcohol for riders: {BeerHours(State, from)}. Set it in the Park menu [H].", 11);
        }
    }

    private string PriceDetail(ProductType product)
    {
        long cents = FacilityMath.PriceOf(State, product);
        int factor = FacilityMath.BuyFactorPermille(product, cents);
        string demand = cents > product.ReferencePriceCents ? $"{(1000 - factor) / 10} % fewer buy · rip-off"
            : cents < product.ReferencePriceCents ? $"+{(factor - 1000) / 10} % buy" : "fair price";
        var here = Facility?.Sales.FirstOrDefault(s => s.ProductId == product.Id);
        return $"fair {UiTheme.MoneyExact(product.ReferencePriceCents)} · {demand} · {here?.SoldToday ?? 0} sold here today";
    }

    private void Fleet(VBoxContainer body)
    {
        var type = Type!;
        Tiles(body,
            ("Bikes", () => $"{Facility?.Fleet.Count ?? 0} / {(Facility is { } f ? FacilityMath.MaxFleet(type, f) : 0)}"),
            ("Rented now", () => $"{Facility?.Fleet.Count(b => b.RentedBy != 0) ?? 0}"),
            ("Condition", () => Facility is { Fleet.Count: > 0 } f ? $"{f.Fleet.Average(b => b.ConditionPermille) / 10:0} %" : "–"),
            ("No bike today", () => $"{Facility?.TurnedAwayToday ?? 0}"));
        var f = Facility!;
        int room = FacilityMath.MaxFleet(type, f) - f.Fleet.Count;
        int count = Math.Clamp(room, 1, BikeBatch);
        string? cannot = FacilityMath.CannotBuyBikes(State, f, count);
        int id = f.Id;
        var buy = UiTheme.Button(room <= 0 ? "Rack full (upgrade for more)" : $"Buy {count} bikes · {UiTheme.Money(count * FacilityMath.BikeCost(State, type))}",
            () => Ctx.Host.Enqueue(new BuyRentalBikesCommand(id, count)),
            cannot ?? $"Rental bikes, {UiTheme.Money(FacilityMath.BikeCost(State, type))} each: they wear with every run and are retired at 0 %");
        buy.Disabled = cannot is not null;
        body.AddChild(buy);
        Text(body, FacilityMath.EBikes(type, f) ? "E-bikes: renters ride like everyone else and climb cheaply." : "Renters are mostly beginners and casual riders.", 11);
    }

    private void Upgrades(VBoxContainer body)
    {
        var type = Type!;
        var f = Facility!;
        int level = Ctx.Kpi.Level;
        for (int i = 0; i < type.Upgrades.Count; i++)
        {
            var upgrade = type.Upgrades[i];
            var card = new PanelContainer();
            var style = UiTheme.Box(i < f.Level ? new Color(UiTheme.AccentDark, 0.6f) : UiTheme.Card, 8, 10, 8);
            style.ShadowSize = 0;
            card.AddThemeStyleboxOverride("panel", style);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            card.AddChild(row);
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 0);
            texts.AddChild(UiTheme.Label(upgrade.Name, 14, bold: true));
            var description = UiTheme.Label(upgrade.Description + (upgrade.UpkeepPerDayCents > 0 ? $" · upkeep +{UiTheme.Money(upgrade.UpkeepPerDayCents)}/day" : ""), 11, UiTheme.TextDim);
            description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            description.CustomMinimumSize = new Vector2(300, 0);
            texts.AddChild(description);
            row.AddChild(texts);
            if (i < f.Level)
                row.AddChild(UiTheme.Label("✓ Bought", 13, UiTheme.Good, bold: true));
            else if (i == f.Level)
            {
                string? locked = FacilityMath.CannotUpgrade(State, level, type, f);
                int id = f.Id;
                var buy = UiTheme.Button(State.Rules.UnlockLevel(level) < upgrade.RequiredLevel ? $"Level {upgrade.RequiredLevel}"
                    : $"Buy {UiTheme.Money(FacilityMath.UpgradeCost(State, type, upgrade))}", () => Ctx.Host.Enqueue(new UpgradeFacilityCommand(id)),
                    locked ?? "Upgrades are bought in order");
                buy.Disabled = locked is not null;
                buy.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                row.AddChild(buy);
            }
            else
                row.AddChild(UiTheme.Label($"{UiTheme.Money(upgrade.CostCents)} · after the one above", 11, UiTheme.TextDim));
            body.AddChild(card);
        }
    }

    private void Finances(VBoxContainer body)
    {
        var type = Type!;
        Tiles(body,
            ("Revenue today", () => UiTheme.Money(Facility?.RevenueTodayCents ?? 0)),
            ("Revenue total", () => UiTheme.Money(Facility?.TotalRevenueCents ?? 0)),
            ("Upkeep / day", () => Facility is { } f ? UiTheme.Money(FacilityMath.Upkeep(type, f)) : "–"),
            ("Invested", () => Facility is { } f ? UiTheme.Money(FacilityMath.InvestedCents(type, f)) : "–"));
        Heading(body, "Sales per product");
        var table = new GridContainer { Columns = 4 };
        table.AddThemeConstantOverride("h_separation", 18);
        table.AddThemeConstantOverride("v_separation", 3);
        foreach (string head in new[] { "", "Today", "Total sold", "Revenue" })
            table.AddChild(UiTheme.Label(head, 11, UiTheme.TextDim, bold: true));
        foreach (var product in FacilityMath.ProductsOf(State, type, Facility!))
        {
            var p = product;
            table.AddChild(UiTheme.Label(p.Name, 12, bold: true));
            var today = UiTheme.Label("", 12);
            var sold = UiTheme.Label("", 12);
            var revenue = UiTheme.Label("", 12);
            table.AddChild(today);
            table.AddChild(sold);
            table.AddChild(revenue);
            Live(() =>
            {
                var s = Facility?.Sales.FirstOrDefault(x => x.ProductId == p.Id);
                today.Text = $"{s?.SoldToday ?? 0} · {UiTheme.Money(s?.RevenueTodayCents ?? 0)}";
                sold.Text = $"{s?.Sold ?? 0}";
                revenue.Text = UiTheme.Money(s?.RevenueCents ?? 0);
            });
        }
        body.AddChild(table);
        var result = Text(body, "", 12);
        Live(() =>
        {
            if (Facility is not { } f) return;
            result.Text = $"Today so far: {Signed(f.RevenueTodayCents - FacilityMath.Upkeep(type, f))} after its upkeep " +
                          "(the goods' cost, food, merch or bike parts, is booked on each sale in the park's budget).";
        });
    }

    private void Reviews(VBoxContainer body)
    {
        int id = Target.Id;
        var mentioning = ReviewMath.Mentioning(State.Reputation, facilityId: id).ToList();
        var stars = ReviewMath.AverageStars(mentioning);
        Heading(body, $"Guests who bought here · {mentioning.Count} reviews");
        body.AddChild(UiTheme.Label(stars is { } s ? $"{Aspects.Glyphs(s)}  {s / 10.0:0.0} from their whole visit" : "No reviews from its guests yet.", 15,
            stars is { } c ? UiTheme.MoodColor((c - 10) * 25) : UiTheme.TextDim, bold: true));
        body.AddChild(Aspects.Bars(mentioning));
        Text(body, "Value drops when guests pay more than the fair price here; food and drinks lift their mood (Fun).", 11);
        var browser = new ReviewBrowser(Ctx, r => r.FacilityIds.Contains(id), pageSize: 5, width: Width - 20);
        body.AddChild(browser);
        Live(browser.Refresh);
    }

    private void Info(VBoxContainer body)
    {
        var type = Type!;
        var f = Facility!;
        var rules = State.FacilityRules;
        var facts = new List<(string, string)>
        {
            ("Serves", $"Guests within {rules.ServeReachMeters} m of it (the nearest building of its kind first)"),
        };
        switch (type.Kind)
        {
            case FacilityKind.Bar:
                facts.Add(("Seats", $"{FacilityMath.Seats(type, f)}: lunch guests sit {MinutesOf(FacilityMath.ProductsOf(State, type, f).FirstOrDefault(p => p.Lunch))}; riders stop on their way between laps"));
                if (rules.ApresEndMinute > 0)
                    facts.Add(("Après-bike", $"Guests done riding may stay for drinks until {rules.ApresEndMinute / 60:00}:{rules.ApresEndMinute % 60:00}" +
                                             (FacilityMath.ApresBonusMinutes(type, f) > 0 ? $", {FacilityMath.ApresBonusMinutes(type, f)} min longer here" : "")));
                break;
            case FacilityKind.Campground:
                facts.Add(("Campers", $"{FacilityMath.Pitches(type, f)} pitches, stays of {rules.MinNights}–{rules.MaxNights} nights; campers wake rested (+{FacilityMath.MorningMood(type, f) / 10} % mood here)"));
                break;
            case FacilityKind.Rental:
                facts.Add(("Rental", "Brings extra guests without a bike of their own; no free bike = turned away"));
                break;
            case FacilityKind.Service:
                facts.Add(("Repairs", $"{FacilityMath.Seats(type, f)} workbenches: riders with a bike broken in a crash ride on instead of going home"));
                break;
            default:
                facts.Add(("Shoppers", $"{FacilityMath.BuyChancePermille(type, f) / 10} % of happy guests buy on their way home or between laps"));
                break;
        }
        Facts(body, facts);
        Heading(body, "What its products do");
        foreach (var product in FacilityMath.ProductsOf(State, type, f))
            Text(body, $"{product.Name}: {Effects(product)}", 12, UiTheme.Text);
    }

    // ---------------------------------------------------------------- helpers

    private static string MinutesOf(ProductType? lunch) => lunch is null ? "a while" : $"{lunch.MinMinutes}–{lunch.MaxMinutes} min";

    internal static string Effects(ProductType p)
    {
        var effects = new List<string>();
        if (p.Lunch) effects.Add($"lunch, a seat for {p.MinMinutes}–{p.MaxMinutes} min");
        if (p.Energy > 0) effects.Add($"+{p.Energy / 10} % energy");
        if (p.Happiness > 0) effects.Add($"+{p.Happiness / 10} % mood");
        if (p.CaffeineMinutes > 0) effects.Add($"climbing costs half the energy for {p.CaffeineMinutes} min");
        if (p.Alcohol > 0) effects.Add("alcohol: riders pick harder trails and crash more");
        if (p.Protection) effects.Add("protectors: crashes are less often serious");
        if (p.Merch) effects.Add("a souvenir on the way home");
        if (p.Overnight) effects.Add("a night at the campground");
        if (p.Rental) effects.Add("a bike for the day");
        if (p.Repair) effects.Add("fixes a broken bike");
        if (p.Clip) effects.Add("a video of their jump");
        return effects.Count == 0 ? "–" : string.Join(", ", effects);
    }

    internal static string ProductTooltip(ProductType p) =>
        $"{p.Name}\nFair price {UiTheme.MoneyExact(p.ReferencePriceCents)}, at most {UiTheme.MoneyExact(p.MaxPriceCents)}\n\n" +
        $"• {Effects(p)}\n• Goods cost {p.GoodsCostPermille / 10} % of the price";

    internal static long PriceStep(ProductType product) => product.ReferencePriceCents < 1000 ? 50 : 100;

    private void ChangePrice(ProductType product, long delta)
    {
        long price = Math.Clamp(FacilityMath.PriceOf(State, product) + delta, 0, product.MaxPriceCents);
        Ctx.Host.Enqueue(new SetProductPriceCommand(product.Id, price));
    }

    internal static string BeerHours(WorldState state, int from) =>
        from <= 0 ? "all day" : from >= GameTime.MinutesPerDay ? "never"
        : from >= state.Rules.CloseMinute ? "après-bike only" : $"from {from / 60:00}:{from % 60:00}";
}
