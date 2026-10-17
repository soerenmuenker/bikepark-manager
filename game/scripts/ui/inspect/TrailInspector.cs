using Bikepark.Game.Ways;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Sponsors;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;
using Godot;
using Gradient = Bikepark.Sim.Trails.Gradient;

namespace Bikepark.Game.Ui;

/// <summary>A trail: how it rides and runs, its features (repairs, cameras), its crashes, its sponsor and its reviews.</summary>
internal sealed partial class TrailInspector : InspectorWindow
{
    private int _workers = 3;

    public TrailInspector(HudContext ctx, InspectTarget target) : base(ctx, target)
    {
        _workers = Math.Clamp(ctx.Sim.State.Crew.Count, 1, Math.Max(1, ctx.Sim.State.CrewRules.MaxWorkersPerJob));
    }

    private WorldState State => Ctx.Sim.State;
    private WayNetwork Network => Ctx.Sim.Network;
    private Way? Trail => State.Ways.FirstOrDefault(w => w.Id == Target.Id && w.Kind == WayKind.Trail);

    public override bool IsGone => Trail is null;

    protected override UiIcon Icon => UiIcon.Trail;

    protected override string TitleText() => Trail?.Name ?? "";

    protected override string SubtitleText()
    {
        if (Trail is not { } way || !Network.TryGetGeometry(way.Id, out var g)) return "";
        return $"{WayMeshes.RatingText(g)} · {g.LengthCm / 100} m · {Status(way)}";
    }

    private string Status(Way way) =>
        !way.Built ? "planned"
        : !Network.IsConnected(way) ? "not connected (closed)"
        : way.WornOut ? "closed: worn out"
        : way.Repairing ? "closed: crew at work"
        : way.CrashClosedUntilTick != 0 ? "closed: crash"
        : way.Closed ? "closed" : "open";

    protected override Vector2? Location() =>
        Trail is { } way && Network.TryGetGeometry(way.Id, out var g) ? g.PositionAt(g.LengthCm / 2) is var p ? new Vector2(p.X / 100f, p.Z / 100f) : null : null;

    protected override string Signature() =>
        Trail is { } w
            ? $"{w.Built}|{w.Closed}|{w.WornOut}|{w.Repairing}|{w.CrashClosedUntilTick != 0}|{_workers}|{State.Crew.Count}|" +
              string.Join(',', w.Features.Select(f => $"{f.Id}:{f.Built}:{f.Condition / 20_000}:{f.Camera}:{Bikepark.Sim.Crew.Jobs.ForRepair(State, f.Id)?.Progress / 20_000}")) +
              $"|{string.Join(",", State.Sponsorships.Where(d => d.WayId == w.Id).Select(d => $"{d.Status}{d.SignBuilt}"))}|{State.Reputation.TotalReviews / 10}"
            : "";

    protected override void Setup()
    {
        AddTab("Overview", Overview);
        AddTab("Features", Features);
        if (State.CrashRules.Enabled) AddTab("Safety", SafetyTab);
        if (State.SponsorTypes.Count > 0) AddTab("Sponsor", Sponsor);
        if (State.ReputationRules.Enabled) AddTab("Reviews", Reviews);
    }

    private void Overview(VBoxContainer body)
    {
        var way = Trail!;
        int id = way.Id;
        if (!way.Built)
        {
            Text(body, "Planned: the crew builds it (Crew menu). Riders can't use it yet.", 13, UiTheme.Warn);
            return;
        }
        Tiles(body,
            ("Runs today", () => $"{Trail?.Stats.RunsToday ?? 0}"),
            ("Runs total", () => $"{Trail?.Stats.Runs ?? 0}"),
            ("Avg run", () => Trail is { Stats.Runs: > 0 } w ? $"{(double)w.Stats.SumRunMinutes / w.Stats.Runs:F1} min" : "–"),
            ("Fun", () => Trail is { Stats.Runs: > 0 } w ? $"{w.Stats.SumFun / w.Stats.Runs / 10} %" : "–"));
        if (Network.TryGetGeometry(id, out var g))
            Facts(body,
            [
                ("Rating", $"{WayMeshes.RatingText(g)} (difficulty {g.DifficultyScore})"),
                ("Length", $"{g.LengthCm / 100} m, {(g.StartHeightCm - g.EndHeightCm) / 100} m down"),
                ("Steepest", Gradient.Format(-g.MaxDropGradient)),
            ]);
        if (!Network.IsConnected(way))
            Text(body, "A loose end: riders can't reach its start or can't get away from its end. Draw a trail from its loose end (or onto its loose start) to connect it.", 12, UiTheme.Warn);
        var buttons = new HBoxContainer();
        buttons.AddChild(UiTheme.Button("Rename", () => Ctx.RenameTrail(id), "Give the trail a new name"));
        buttons.AddChild(UiTheme.Button(way.Closed ? "Open" : "Close", () => Ctx.Host.Enqueue(new SetTrailClosedCommand(id, !way.Closed)),
            way.Closed ? "Let riders on it again (a worn-out trail stays closed until repaired)" : "Close it to riders (those on it finish their run)"));
        body.AddChild(buttons);
    }

    private void Features(VBoxContainer body)
    {
        var way = Trail!;
        var built = way.Features.Where(f => f.Built).OrderBy(f => f.DistanceCm).ToList();
        int planned = way.Features.Count(f => !f.Built);
        Text(body, built.Count == 0 ? "No built features yet: Build → trail features." :
            "Only features wear. Repairs close the trail while the crew works; at 0 % it closes by itself." +
            (planned > 0 ? $" {planned} more planned." : ""), 12);
        if (built.Count == 0) return;
        var workers = new HBoxContainer();
        workers.AddChild(UiTheme.Label("Workers to send", 13, bold: true));
        workers.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        workers.AddChild(UiTheme.Button("−", () => _workers = Math.Max(1, _workers - 1), "Fewer workers"));
        workers.AddChild(UiTheme.Label($"{_workers}", 14, bold: true));
        workers.AddChild(UiTheme.Button("+", () => _workers = Math.Min(State.CrewRules.MaxWorkersPerJob, _workers + 1), "More workers (faster repair)"));
        workers.AddChild(UiTheme.Button("Repair all", RepairAll, "Send the crew to every worn feature of this trail, the worst first"));
        body.AddChild(workers);
        foreach (var feature in built)
            body.AddChild(RepairDialog.FeatureRow(Ctx, State, way, feature, _workers));
        if (State.ProductTypes.FirstOrDefault(p => p.Clip) is { } clip && built.Any(f => f.Camera))
        {
            Heading(body, "Clips — riders filmed on a camera jump may buy theirs");
            Lever(body, clip.Name, () => UiTheme.MoneyExact(FacilityMath.PriceOf(State, clip)),
                () => ChangePrice(clip, -FacilityInspector.PriceStep(clip)), () => ChangePrice(clip, FacilityInspector.PriceStep(clip)),
                "Cheaper clips: more riders buy theirs", "Dearer clips: fewer buy", () =>
                    $"fair {UiTheme.MoneyExact(clip.ReferencePriceCents)} · park-wide price, all cameras", FacilityInspector.ProductTooltip(clip));
        }
    }

    private void ChangePrice(ProductType product, long delta) =>
        Ctx.Host.Enqueue(new SetProductPriceCommand(product.Id, Math.Clamp(FacilityMath.PriceOf(State, product) + delta, 0, product.MaxPriceCents)));

    private void RepairAll()
    {
        if (Trail is not { } way) return;
        foreach (var feature in way.Features.Where(f => f.Built && f.Condition < TrailCondition.Perfect && Bikepark.Sim.Crew.Jobs.ForRepair(State, f.Id) is null)
                     .OrderByDescending(f => f.Condition))
            Ctx.Host.Enqueue(new RepairFeatureCommand(way.Id, feature.Id, _workers));
    }

    private void SafetyTab(VBoxContainer body)
    {
        var way = Trail!;
        Tiles(body,
            ("Crashes", () => $"{Trail?.Stats.Crashes ?? 0}"),
            ("Serious", () => $"{Trail?.Stats.SeriousCrashes ?? 0}"),
            ("Collisions", () => $"{Trail?.Stats.Collisions ?? 0}"),
            ("Per 1000 runs", () => Trail is { } w ? $"{CrashMath.TrailRatePer10k(State.CrashRules, w) / 10.0:0.0}" : "–"));
        var summary = Text(body, "", 12, UiTheme.Text);
        Live(() =>
        {
            if (Trail is { } w) summary.Text = TrailsPanel.CrashSummary(State, w, Network);
        });
        var crossings = Network.Crossings.Count(c => c.WayA == way.Id || c.WayB == way.Id);
        if (crossings > 0)
            Text(body, $"It crosses other ways {crossings} time{(crossings == 1 ? "" : "s")}: riders can collide there.", 12, UiTheme.Warn);
        var worst = Network.FeaturesOn(way.Id).Where(f => f.Feature.Crashes > 0).OrderByDescending(f => f.Feature.Crashes).Take(5).ToList();
        if (worst.Count == 0) return;
        Heading(body, "Crashes per feature");
        Facts(body, worst.Select(f => ($"{f.Type.Name} at {f.Feature.DistanceCm / 100} m", $"{f.Feature.Crashes}")));
    }

    private void Sponsor(VBoxContainer body)
    {
        var way = Trail!;
        int level = Ctx.Kpi.Level;
        long day = GameTime.Day(State.Tick);
        var deal = SponsorMath.DealOn(State, way.Id, offeredToo: true);
        if (deal is not null && SponsorMath.Find(State, deal.SponsorId) is { } sponsor)
        {
            int id = deal.Id;
            Heading(body, sponsor.Name);
            var row = new HBoxContainer();
            string detail = deal.Status switch
            {
                SponsorshipStatus.Offered => $"Offers {UiTheme.Money(sponsor.DailyCents)}/day for {sponsor.Days} days",
                SponsorshipStatus.Applied => $"You applied: answer on day {GameTime.Day(deal.AnswerTick) + 1}",
                _ => deal.SignBuilt ? $"{UiTheme.Money(sponsor.DailyCents)}/day · paid {UiTheme.Money(deal.PaidCents)} · {Math.Max(0, deal.EndDay - day)} days left"
                    : "The crew is putting up the sign (Crew menu)",
            };
            var label = UiTheme.Label(detail, 12, UiTheme.Text);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            if (deal.Status == SponsorshipStatus.Offered)
            {
                row.AddChild(UiTheme.Button("Accept", () => Ctx.Host.Enqueue(new AcceptSponsorCommand(id)), "The crew puts up the brand's sign; it pays from then"));
                row.AddChild(UiTheme.Button("No", () => Ctx.Host.Enqueue(new DeclineSponsorCommand(id)), "Turn the offer down"));
            }
            else if (deal.Status != SponsorshipStatus.Applied)
                row.AddChild(UiTheme.Button("End", () => Ctx.Host.Enqueue(new EndSponsorshipCommand(id)), "End the deal now (the money stops)"));
            body.AddChild(row);
            Text(body, SponsorMath.Describe(sponsor, State.SponsorRules.TrailBenefitPermille), 12, UiTheme.Good);
            return;
        }
        Text(body, "No sponsor. Brands may offer for good trails by themselves, or apply:", 12);
        foreach (var brand in State.SponsorTypes.OrderBy(b => b.RequiredLevel))
        {
            var row = new HBoxContainer();
            var name = UiTheme.Label(brand.Name, 12, bold: true);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            string? misfit = SponsorMath.Open(State, brand.Id) is not null ? $"{brand.Name} already has a deal going."
                : SponsorMath.TrailMisfit(State, Network, brand, way, level);
            string id = brand.Id;
            int wayId = way.Id;
            var apply = UiTheme.Button($"Apply · {UiTheme.Money(brand.DailyCents)}/day", () => Ctx.Host.Enqueue(new ApplySponsorCommand(id, wayId)),
                misfit ?? $"{brand.Name}: {SponsorMath.Describe(brand, State.SponsorRules.TrailBenefitPermille)}");
            apply.Disabled = misfit is not null;
            row.AddChild(apply);
            body.AddChild(row);
        }
    }

    private void Reviews(VBoxContainer body)
    {
        int id = Target.Id;
        var mentioning = ReviewMath.Mentioning(State.Reputation, trailId: id).ToList();
        var stars = ReviewMath.AverageStars(mentioning);
        Heading(body, $"Guests who rode it · {mentioning.Count} reviews");
        body.AddChild(UiTheme.Label(stars is { } s ? $"{Aspects.Glyphs(s)}  {s / 10.0:0.0} from their whole visit" : "No reviews from its riders yet.", 15,
            stars is { } c ? UiTheme.MoodColor((c - 10) * 25) : UiTheme.TextDim, bold: true));
        body.AddChild(Aspects.Bars(mentioning));
        var browser = new ReviewBrowser(Ctx, r => r.TrailIds.Contains(id), pageSize: 5, width: Width - 20);
        body.AddChild(browser);
        Live(browser.Refresh);
    }
}
