using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// A bar, bike shop or campground: what it does now, the prices of what it sells (park-wide), a bike shop's store (shelves,
/// range, workshop) and fleet, its upgrades, its money, what its guests say about the park and what it does for them.
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
        Facility is { } f
            ? $"{f.Level}|{Ctx.Kpi.Level}|{f.Fleet.Count}|{State.Reputation.TotalReviews / 10}|{string.Join(",", f.Departments)}|{f.Range}|" +
              $"{string.Join(",", f.ServicesOff)}|{f.ReserveBench}"
            : "";

    protected override void Setup()
    {
        var type = Type!;
        AddTab("Overview", Overview);
        if (type.Kind == FacilityKind.BikeShop) AddTab("Store", Store);
        if (FacilityMath.ProductsOf(State, type, Facility!).Any()) AddTab("Prices", Prices);
        if (FacilityMath.HasRental(type, Facility!)) AddTab("Fleet", Fleet);
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
                FacilityKind.Campground => $"{FacilityMath.PitchesTaken(State, f)} / {FacilityMath.Pitches(type, f)}",
                _ => $"{FacilityMath.SeatsTaken(State, f)} / {FacilityMath.Places(type, f)}",
            };
        }
        string busyCaption = type.Kind switch
        {
            FacilityKind.Bar => "Seats taken",
            FacilityKind.Campground => "Pitches booked",
            _ => "Benches busy",
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
                $"{p.Name} {UiTheme.MoneyExact(FacilityMath.PriceAt(State, p, f))}"));
        });
        if (FacilityMath.HasRental(type, Facility!))
        {
            var fleet = Text(body, "", 12);
            Live(() =>
            {
                if (Facility is not { } f) return;
                fleet.Text = $"Rental: {f.Fleet.Count(b => b.RentedBy == 0)} of {f.Fleet.Count} bikes in the rack" +
                             (f.Fleet.Count > 0 ? $" · condition {f.Fleet.Average(b => b.ConditionPermille) / 10:0} %" : "");
            });
        }
    }

    /// <summary>The one thing to know right now (busy, full, cheap, idle).</summary>
    private string StatusLine()
    {
        if (Facility is not { } f || Type is not { } type) return "";
        if (FacilityMath.HasRental(type, f) && f.Fleet.Count == 0)
            return "No rental bikes yet: buy some in the Fleet tab.";
        if (f.TurnedAwayToday > 0 && type.Kind == FacilityKind.Bar)
            return $"{f.TurnedAwayToday} times today a guest found no free seat: an upgrade adds more.";
        if (f.TurnedAwayToday > 0 && type.Kind == FacilityKind.BikeShop)
            return FacilityMath.HasRental(type, f)
                ? $"{f.TurnedAwayToday} times today a guest found no free workbench or rental bike: upgrade, or buy bikes (Fleet)."
                : $"{f.TurnedAwayToday} times today a broken bike found no free workbench: an upgrade adds one.";
        if (type.Kind == FacilityKind.BikeShop && f.Departments.Count < FacilityMath.ShelfSlots(type, f))
            return $"{FacilityMath.ShelfSlots(type, f) - f.Departments.Count} shelf slots are empty: stock them in the Store tab.";
        if (f.OverpricedToday > 0)
            return $"{f.OverpricedToday} of today's sales were above the fair price: guests feel ripped off (Prices).";
        if (f.Customers == 0)
            return "No customers yet. Guests buy at the nearest bar or shop within reach of where they are.";
        return "Running fine.";
    }

    private void Prices(VBoxContainer body)
    {
        var type = Type!;
        Text(body, "One price per product for the whole park: it applies at every building that sells it." +
                   (type.Kind == FacilityKind.BikeShop && Facility!.Range != ShopRange.Standard
                       ? $" This shop's {FacilityMath.RangeOf(State, Facility!).Name.ToLowerInvariant()} range scales its departments' prices." : ""), 12);
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
        long cents = FacilityMath.PriceAt(State, product, Facility);
        long fair = FacilityMath.ReferenceAt(State, product, Facility);
        int factor = FacilityMath.BuyFactorAt(State, product, Facility);
        string demand = cents > fair ? $"{(1000 - factor) / 10} % fewer buy · rip-off"
            : cents < fair ? $"+{(factor - 1000) / 10} % buy" : "fair price";
        string here = cents != FacilityMath.PriceOf(State, product) ? $"here {UiTheme.MoneyExact(cents)} · " : "";
        var sold = Facility?.Sales.FirstOrDefault(s => s.ProductId == product.Id);
        return $"{here}fair {UiTheme.MoneyExact(fair)} · {demand} · {sold?.SoldToday ?? 0} sold here today";
    }

    // ---------------------------------------------------------------- the store (bike shops)

    private void Store(VBoxContainer body)
    {
        var type = Type!;
        var f = Facility!;
        int id = f.Id;
        int slots = FacilityMath.ShelfSlots(type, f);

        Heading(body, $"Shelves · {f.Departments.Count} / {slots} slots");
        Text(body, "Pick what this shop stocks: each department takes a slot. Stock what your guests like: beginners buy " +
                   "protectors, experts gear and (with the demo center) whole bikes.", 11);
        var unlocked = FacilityMath.UnlockedDepartments(State, type, f).Select(p => p.Id).ToList();
        foreach (var product in State.ProductTypes.Where(p => p.Department && (unlocked.Contains(p.Id) || UnlockedBy(type, p.Id) is not null)))
        {
            var p = product;
            bool stocked = f.Departments.Contains(p.Id);
            var row = Row(body);
            Button toggle;
            if (!unlocked.Contains(p.Id))
            {
                toggle = UiTheme.Button($"🔒 {p.Name}", () => { }, $"Comes with the {UnlockedBy(type, p.Id)} upgrade");
                toggle.Disabled = true;
            }
            else
            {
                var next = stocked ? f.Departments.Where(d => d != p.Id).ToList() : [.. f.Departments, p.Id];
                toggle = UiTheme.Button(stocked ? $"✓ {p.Name}" : $"+ {p.Name}",
                    () => Ctx.Host.Enqueue(new SetShopDepartmentsCommand(id, next)),
                    stocked ? $"Take {p.Name.ToLowerInvariant()} off the shelves (frees a slot)" : $"Stock {p.Name.ToLowerInvariant()}");
                toggle.Disabled = !stocked && f.Departments.Count >= slots;
                if (toggle.Disabled) toggle.TooltipText = "All shelf slots are taken: take a department off first, or upgrade (Bigger store)";
            }
            toggle.CustomMinimumSize = new Vector2(150, 0);
            row.AddChild(toggle);
            var info = UiTheme.Label($"{UiTheme.MoneyExact(FacilityMath.PriceAt(State, p, f))} · {WhoBuys(p)}", 11, stocked ? UiTheme.Text : UiTheme.TextDim);
            info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(info);
        }

        var range = FacilityMath.RangeOf(State, f);
        Heading(body, $"Range · {range.Name}");
        var ranges = Row(body);
        foreach (var r in Enum.GetValues<ShopRange>())
        {
            var choice = r;
            var rules = (int)r < State.FacilityRules.ShopRanges.Count ? State.FacilityRules.ShopRanges[(int)r] : ShopRangeRules.Neutral;
            var button = UiTheme.Button(f.Range == r ? $"● {rules.Name}" : rules.Name, () => Ctx.Host.Enqueue(new SetShopRangeCommand(id, choice)),
                $"{rules.Name} range\nPrices ×{rules.PriceFactorPermille / 1000.0:0.0#}, goods ×{rules.GoodsFactorPermille / 1000.0:0.0#}, " +
                $"upkeep ×{rules.UpkeepFactorPermille / 1000.0:0.0#}\n\n{GroupLine(rules.Groups)}");
            button.Disabled = f.Range == r;
            ranges.AddChild(button);
        }
        Text(body, $"Departments cost ×{range.PriceFactorPermille / 1000.0:0.0#} here (that's the fair price for this range). " +
                   GroupLine(range.Groups) + $" · upkeep {UiTheme.Money(FacilityMath.Upkeep(State, type, f))}/day.", 11);

        Heading(body, $"Workshop · {FacilityMath.Workbenches(type, f)} workbench{(FacilityMath.Workbenches(type, f) == 1 ? "" : "es")}");
        Text(body, "Crash repairs are always on: riders whose bike broke in a crash get it fixed here and ride on.", 11);
        foreach (var product in State.ProductTypes.Where(p => p.IsService && (FacilityMath.Sells(type, f, p.Id) || UnlockedBy(type, p.Id) is not null)))
            ServiceRow(body, type, f, product.Id, product.Name, ServiceText(product), UnlockedBy(type, product.Id));
        if (type.Upgrades.Any(u => u.MaxFleet > 0))
            ServiceRow(body, type, f, FacilityMath.FleetCare, "Fleet care",
                $"each night every bench restores {State.FacilityRules.FleetCarePerBenchPermille / 10} % of bike condition, worst bikes first, " +
                $"for {UiTheme.MoneyExact(State.FacilityRules.FleetCareCentsPerPermille * 10)} of parts per %" +
                (f.FleetCareLastNight > 0 ? $" · last night {f.FleetCareLastNight / 10} %" : ""),
                FacilityMath.HasRental(type, f) ? null : type.Upgrades.First(u => u.MaxFleet > 0).Name);
        var reserve = Row(body);
        var keep = UiTheme.Button(f.ReserveBench ? "✓ Keep a bench free" : "Keep a bench free", () => Ctx.Host.Enqueue(new SetReserveBenchCommand(id, !f.ReserveBench)),
            "Services never take the last free workbench: it stays free for riders with a broken bike");
        keep.CustomMinimumSize = new Vector2(150, 0);
        reserve.AddChild(keep);
        reserve.AddChild(UiTheme.Label("for crash repairs", 11, UiTheme.TextDim));
    }

    private void ServiceRow(VBoxContainer body, FacilityType type, Facility f, string service, string name, string text, string? lockedBy)
    {
        int id = f.Id;
        var row = Row(body);
        bool on = FacilityMath.ServiceOn(State, type, f, service);
        Button toggle;
        if (lockedBy is not null && !(service == FacilityMath.FleetCare ? FacilityMath.HasRental(type, f) : FacilityMath.Sells(type, f, service)))
        {
            toggle = UiTheme.Button($"🔒 {name}", () => { }, $"Comes with the {lockedBy} upgrade");
            toggle.Disabled = true;
        }
        else
            toggle = UiTheme.Button(on ? $"✓ {name}" : $"{name}: off", () => Ctx.Host.Enqueue(new SetWorkshopServiceCommand(id, service, !on)),
                on ? $"Stop offering {name.ToLowerInvariant()}" : $"Offer {name.ToLowerInvariant()} again");
        toggle.CustomMinimumSize = new Vector2(150, 0);
        row.AddChild(toggle);
        var info = UiTheme.Label(text, 11, on ? UiTheme.Text : UiTheme.TextDim);
        info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(info);
    }

    private static HBoxContainer Row(VBoxContainer body)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        body.AddChild(row);
        return row;
    }

    /// <summary>The upgrade that unlocks a product, or null (built in, or never).</summary>
    private static string? UnlockedBy(FacilityType type, string productId) =>
        type.Products.Contains(productId) ? null : type.Upgrades.FirstOrDefault(u => u.Products.Contains(productId))?.Name;

    /// <summary>When and by whom a department sells, in a few words.</summary>
    private static string WhoBuys(ProductType p)
    {
        var when = new List<string>();
        if (p.HomeChancePermille > 0) when.Add("on the way home");
        if (p.ImpulsePermille > 0) when.Add("between laps");
        if (p.RainImpulsePermille > 0) when.Add("rain jackets in the rain");
        if (p.WithServicePermille > 0) when.Add("with repairs and services");
        if (p.ByCard) when.Add("paid by card");
        string groups = GroupLine(p.Groups);
        return string.Join(", ", when) + (groups.Length > 0 ? " · " + groups : "");
    }

    /// <summary>"beginners ×2, experts ×0.6" for the groups that differ from 1.</summary>
    private static string GroupLine(GroupFactors g)
    {
        var parts = new List<string>();
        if (g.Beginner != 1000) parts.Add($"beginners ×{g.Beginner / 1000.0:0.0#}");
        if (g.Intermediate != 1000) parts.Add($"intermediates ×{g.Intermediate / 1000.0:0.0#}");
        if (g.Expert != 1000) parts.Add($"experts ×{g.Expert / 1000.0:0.0#}");
        return parts.Count == 0 ? "everyone alike" : string.Join(", ", parts);
    }

    private static string ServiceText(ProductType p) =>
        $"{UiTheme.MoneyExact(p.ReferencePriceCents)} fair, a bench for {p.MinMinutes}–{p.MaxMinutes} min, offered once to riders on their own bike: " +
        Effects(p) + (GroupLine(p.Groups) is var g && g != "everyone alike" ? $" · {g}" : "");

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
            ("Upkeep / day", () => Facility is { } f ? UiTheme.Money(FacilityMath.Upkeep(State, type, f)) : "–"),
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
            result.Text = $"Today so far: {Signed(f.RevenueTodayCents - FacilityMath.Upkeep(State, type, f))} after its upkeep " +
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
            case FacilityKind.BikeShop:
                facts.Add(("Shelves", $"{f.Departments.Count} of {FacilityMath.ShelfSlots(type, f)} slots stocked, {FacilityMath.RangeOf(State, f).Name.ToLowerInvariant()} range: " +
                                      "guests buy on their way home and between laps, at most one of each department a visit"));
                facts.Add(("Workshop", $"{FacilityMath.Workbenches(type, f)} workbenches: riders with a bike broken in a crash ride on instead of going home"));
                if (FacilityMath.HasRental(type, f))
                    facts.Add(("Rental", "Brings extra guests without a bike of their own; no free bike = turned away"));
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
        if (p.Merch) effects.Add(p.ByCard ? "bought to take home" : "a souvenir on the way home");
        if (p.WithServicePermille > 0) effects.Add("sold with repairs and services");
        if (p.TuneUpPermille > 0) effects.Add($"a tuned bike: {(1000 - p.TuneUpPermille) / 10} % fewer crashes break it");
        if (p.RedBlackFun > 0) effects.Add($"suspension set up: +{p.RedBlackFun / 10} % fun on red and black trails");
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
