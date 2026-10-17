using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Rating menu, in three tabs. Overview: the overall rating and its trend, the sub-ratings (fun, safety, smoothness,
/// challenge, jumps, variety, value) for everyone or one skill group, the biggest complaints with where they come from,
/// visitor demand and the last days. Reviews: every review in the window to browse like a review site. Level &amp; social:
/// the park level with its XP parts and FakeSocial.
/// </summary>
public partial class ReputationPanel : HudPanel
{
    private const float ContentWidth = 860;

    private TabStrip _tabs = null!;
    private VBoxContainer _overview = null!, _browse = null!, _social = null!;

    // Overview
    private StarsView _stars = null!;
    private Label _rating = null!, _ratingHint = null!, _trend = null!, _reviews = null!, _level = null!, _demand = null!, _demandParts = null!;
    private TabStrip _groupStrip = null!;
    private VBoxContainer _aspects = null!, _complaints = null!;
    private Label _groupNote = null!;
    private string _aspectsSignature = "";
    private RatingChart _chart = null!;

    // Reviews
    private ReviewBrowser? _browser;

    // Level & social
    private Label _xpText = null!, _xpParts = null!, _post = null!;
    private ProgressBar _xpBar = null!;

    public override string Title => "Rating";

    protected override void Build()
    {
        _tabs = new TabStrip();
        _tabs.Add("Overview", "The rating, its sub-ratings and the biggest complaints");
        _tabs.Add("Reviews", "Read the reviews: sort and filter them");
        _tabs.Add("Level & social", "Park level, XP and FakeSocial");
        _tabs.Selected += ShowTab;
        Body.AddChild(_tabs);

        _overview = Page();
        _browse = Page();
        _social = Page();
        BuildOverview();
        BuildSocial();
        ShowTab(0);
    }

    private VBoxContainer Page()
    {
        var page = new VBoxContainer { CustomMinimumSize = new Vector2(ContentWidth, 0) };
        page.AddThemeConstantOverride("separation", 10);
        Body.AddChild(page);
        return page;
    }

    private void ShowTab(int index)
    {
        _overview.Visible = index == 0;
        _browse.Visible = index == 1;
        _social.Visible = index == 2;
        if (index == 1 && _browser is null)
        {
            _browser = new ReviewBrowser(Ctx, width: ContentWidth);
            _browse.AddChild(_browser);
        }
        if (Ctx is not null) Refresh();
    }

    private void BuildOverview()
    {
        var top = Row(28);
        var overall = new VBoxContainer();
        overall.AddThemeConstantOverride("separation", 2);
        overall.AddChild(UiTheme.Label("RATING", 11, UiTheme.TextDim, bold: true));
        var starsRow = Row(8);
        _stars = new StarsView(22);
        starsRow.AddChild(_stars);
        _rating = UiTheme.Label("–", 24, bold: true);
        starsRow.AddChild(_rating);
        _trend = UiTheme.Label("", 14, bold: true);
        _trend.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _trend.MouseFilter = MouseFilterEnum.Stop;
        starsRow.AddChild(_trend);
        overall.AddChild(starsRow);
        _ratingHint = UiTheme.Label("", 11, UiTheme.TextDim);
        overall.AddChild(_ratingHint);
        top.AddChild(overall);
        top.AddChild(UiTheme.StatTile("Reviews", out _reviews));
        top.AddChild(UiTheme.StatTile("Park level", out _level));
        top.AddChild(UiTheme.StatTile("Visitor demand", out _demand));
        _overview.AddChild(top);
        _demandParts = UiTheme.Label("", 12, UiTheme.TextDim);
        _overview.AddChild(_demandParts);
        _overview.AddChild(UiTheme.Separator());

        var columns = Row(28);
        _overview.AddChild(columns);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        left.AddThemeConstantOverride("separation", 8);
        columns.AddChild(left);
        left.AddChild(UiTheme.Label("SUB-RATINGS — hover one to see what moves it", 11, UiTheme.TextDim, bold: true));
        _groupStrip = new TabStrip();
        foreach (string name in new[] { "Everyone", "Beginners", "Intermediates", "Experts" }) _groupStrip.Add(name);
        _groupStrip.Selected += _ =>
        {
            _aspectsSignature = "";
            Refresh();
        };
        left.AddChild(_groupStrip);
        _aspects = new VBoxContainer();
        _aspects.AddThemeConstantOverride("separation", 4);
        left.AddChild(_aspects);
        _groupNote = UiTheme.Label("", 11, UiTheme.TextDim);
        _groupNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _groupNote.CustomMinimumSize = new Vector2(420, 0);
        left.AddChild(_groupNote);

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
        right.AddThemeConstantOverride("separation", 8);
        columns.AddChild(right);
        right.AddChild(UiTheme.Label("BIGGEST COMPLAINTS", 11, UiTheme.TextDim, bold: true));
        _complaints = new VBoxContainer();
        _complaints.AddThemeConstantOverride("separation", 6);
        right.AddChild(_complaints);
        right.AddChild(UiTheme.Label("LAST DAYS — rating and reviews/day", 11, UiTheme.TextDim, bold: true));
        _chart = new RatingChart { CustomMinimumSize = new Vector2(400, 80) };
        right.AddChild(_chart);
    }

    private void BuildSocial()
    {
        _social.AddChild(UiTheme.Label("PARK LEVEL — trails, diversity, visitors", 11, UiTheme.TextDim, bold: true));
        _xpText = UiTheme.Label("", 14, bold: true);
        _social.AddChild(_xpText);
        _xpBar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(500, 10), MaxValue = 1000 };
        _social.AddChild(_xpBar);
        _xpParts = UiTheme.Label("", 12, UiTheme.TextDim);
        _social.AddChild(_xpParts);
        _social.AddChild(UiTheme.Separator());
        _social.AddChild(UiTheme.Label("FAKESOCIAL", 11, UiTheme.TextDim, bold: true));
        _post = UiTheme.Label("", 13);
        _post.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _post.CustomMinimumSize = new Vector2(ContentWidth, 0);
        _social.AddChild(_post);
    }

    /// <summary>Key: a tab ("overview", "reviews", "level & social").</summary>
    public override void Focus(string key)
    {
        int index = _tabs.IndexOf(key);
        if (index >= 0) _tabs.Select(index);
    }

    public override void Refresh()
    {
        if (_tabs is null) return;
        switch (_tabs.Current)
        {
            case 0: RefreshOverview(); break;
            case 1: _browser?.Refresh(); break;
            default: RefreshSocial(); break;
        }
    }

    private void RefreshOverview()
    {
        var state = Ctx.Sim.State;
        var rules = state.ReputationRules;
        var rep = state.Reputation;

        int? overall = ReputationMath.RatingTenths(state);
        _stars.Tenths = overall;
        _rating.Text = StarsText(overall);
        _ratingHint.Text = !rules.Enabled ? "Reputation is off in this scenario."
            : overall is null ? $"No rating yet: {rep.TotalReviews} of {rules.MinReviewsForRating} reviews"
            : $"average of the last {rep.Reviews.Count} reviews";
        long today = GameTime.Day(state.Tick);
        var recent = ReviewMath.AverageStars(rep.Reviews.Where(r => r.Day > today - 7));
        var before = ReviewMath.AverageStars(rep.Reviews.Where(r => r.Day <= today - 7 && r.Day > today - 14));
        int delta = recent is { } a && before is { } b ? a - b : 0;
        _trend.Text = recent is null || before is null ? "" : delta > 0 ? $"▲ {delta / 10.0:0.0}" : delta < 0 ? $"▼ {-delta / 10.0:0.0}" : "▶ steady";
        _trend.AddThemeColorOverride("font_color", delta > 0 ? UiTheme.Good : delta < 0 ? UiTheme.Bad : UiTheme.TextDim);
        _trend.TooltipText = "This week's reviews against the week before";
        _reviews.Text = $"{rep.TotalReviews}";

        var xp = ParkProgress.Xp(state, Ctx.Sim.Network);
        _level.Text = $"{ParkProgress.Level(rules, xp.Total)} / {ReputationRules.MaxLevel}";
        var demand = ReputationMath.Demand(state);
        _demand.Text = $"{demand.Total / 10} %";
        _demand.AddThemeColorOverride("font_color", demand.Total >= 1000 ? UiTheme.Good : demand.Total >= 700 ? UiTheme.Warn : UiTheme.Bad);
        _demandParts.Text = !rules.Enabled
            ? "Reputation is off: guests come at the normal rate."
            : $"Demand: visibility {demand.Visibility / 10} % (grows with reviews)  ·  rating ×{demand.Rating / 1000.0:0.00}" +
              (overall is null ? " (no rating yet)" : "") +
              (demand.Influencer != 0 ? $"  ·  influencer {demand.Influencer / 10:+0;-0} %" : "") +
              (rep.BuzzPermille != 0 ? $"  ·  clips online +{rep.BuzzPermille / 10} %" : "");

        // The group buttons show their rating.
        string[] names = ["Everyone", "Beginners", "Intermediates", "Experts"];
        for (int i = 0; i < names.Length; i++)
        {
            int? tenths = i == 0 ? overall : ReputationMath.RatingTenths(state, (SkillGroup)(i - 1));
            if (_groupStrip.GetChild(i) is Button button) button.Text = tenths is { } t ? $"{names[i]} {StarsText(t)} ★" : names[i];
        }

        string signature = $"{_groupStrip.Current}|{rep.TotalReviews}|{state.WaysRevision}|{state.Facilities.Count}|{state.Lifts.Count}";
        if (signature != _aspectsSignature)
        {
            _aspectsSignature = signature;
            var reviews = Selected(state).ToList();
            foreach (var child in _aspects.GetChildren()) child.QueueFree();
            var (average, count) = ReviewMath.AspectAverages(reviews);
            foreach (var (name, drives, of) in Aspects.All)
                _aspects.AddChild(Aspects.Bar(name, drives, of(average), count > 0));
            _groupNote.Text = count == 0 ? "No reviews yet: guests write one when they leave after riding."
                : $"From {count} reviews. Each group weighs these differently: beginners care most about safety and fun, experts about challenge and jumps.";
            RefreshComplaints(state, reviews, average, count);
        }

        _chart.Days = Ctx.Days;
        _chart.QueueRedraw();
    }

    private IEnumerable<Review> Selected(Bikepark.Sim.State.WorldState state) =>
        _groupStrip.Current == 0 ? state.Reputation.Reviews : state.Reputation.Reviews.Where(r => r.Group == (SkillGroup)(_groupStrip.Current - 1));

    /// <summary>The two weakest sub-ratings, what to do, and the trail, lift or building whose guests rate it lowest.</summary>
    private void RefreshComplaints(Bikepark.Sim.State.WorldState state, List<Review> reviews, ReviewScores average, int count)
    {
        foreach (var child in _complaints.GetChildren()) child.QueueFree();
        if (count == 0)
        {
            _complaints.AddChild(UiTheme.Label("Nothing yet.", 12, UiTheme.TextDim));
            return;
        }
        var weakest = Aspects.All.Select(a => (a.Name, Score: a.Of(average), a.Of)).OrderBy(a => a.Score).Take(2).ToList();
        foreach (var (name, score, of) in weakest)
        {
            var card = new PanelContainer();
            var style = UiTheme.Box(UiTheme.Card, 8, 10, 8);
            style.ShadowSize = 0;
            card.AddThemeStyleboxOverride("panel", style);
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 2);
            card.AddChild(box);
            box.AddChild(UiTheme.Label($"{name}  {Aspects.Stars(score):0.0} ★", 14, UiTheme.MoodColor(250 + score * 3 / 4), bold: true));
            var advice = UiTheme.Label(Advice(name), 12, UiTheme.Text);
            advice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            advice.CustomMinimumSize = new Vector2(380, 0);
            box.AddChild(advice);
            if (Worst(state, reviews, name, of) is { } worst)
            {
                var link = UiTheme.Button($"Lowest: {worst.Name} ({Aspects.Stars(worst.Score):0.0} ★ from {worst.Count} reviews) →",
                    () => Ctx.Inspect(worst.Target), "Open its window");
                link.AddThemeFontSizeOverride("font_size", 12);
                link.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                box.AddChild(link);
            }
            _complaints.AddChild(card);
        }
    }

    private static string Advice(string aspect) => aspect switch
    {
        "Fun" => "Riders aren't enjoying their runs: flowy trails that suit their skill, features in good shape, food and drinks help.",
        "Safety" => "Riders crash, see crashes or end up on trails too hard for them: check ratings, crossings and worn features.",
        "Smoothness" => "Too much waiting: long lift queues (more bike cabins, another lift) or slow riders blocking trails.",
        "Challenge" => "Riders find it too easy: add harder (red, black) trails and features.",
        "Jumps" => "Few jumps or poor ones: add tables, kickers, doubles or drops and keep them repaired.",
        "Variety" => "Too few trails to choose from: build more different ones.",
        _ => "Guests feel they pay too much: entrance, parking, the lift pass or prices above the fair price.",
    };

    /// <summary>The trail, lift or building whose guests rate this aspect lowest (at least a few reviews), or null.</summary>
    private static (string Name, int Score, int Count, InspectTarget Target)? Worst(Bikepark.Sim.State.WorldState state, List<Review> reviews,
        string aspect, Func<ReviewScores, int> of)
    {
        // Only places that can cause it: trails for how riding feels, lifts and trails for waiting, buildings and lifts for prices.
        bool trails = aspect is "Fun" or "Safety" or "Smoothness" or "Challenge" or "Jumps";
        bool lifts = aspect is "Smoothness" or "Value";
        bool buildings = aspect is "Value";
        const int MinReviews = 5;
        var candidates = new List<(string, int, int, InspectTarget)>();
        void Consider(string name, IEnumerable<Review> mentions, InspectTarget target)
        {
            var (avg, n) = ReviewMath.AspectAverages(mentions);
            if (n >= MinReviews) candidates.Add((name, of(avg), n, target));
        }
        foreach (var way in state.Ways.Where(w => trails && w.Kind == WayKind.Trail && w.Built))
            Consider(way.Name, reviews.Where(r => r.TrailIds.Contains(way.Id)), new InspectTarget(InspectKind.Trail, way.Id, "Reviews"));
        foreach (var lift in state.Lifts.Where(_ => lifts))
            Consider(lift.Name, reviews.Where(r => r.LiftIds.Contains(lift.Id)), new InspectTarget(InspectKind.Lift, lift.Id, "Reviews"));
        foreach (var facility in state.Facilities.Where(_ => buildings))
            Consider(facility.Name, reviews.Where(r => r.FacilityIds.Contains(facility.Id)), new InspectTarget(InspectKind.Facility, facility.Id, "Reviews"));
        return candidates.Count == 0 ? null : candidates.MinBy(c => c.Item2);
    }

    private void RefreshSocial()
    {
        var state = Ctx.Sim.State;
        var rules = state.ReputationRules;
        var rep = state.Reputation;
        var xp = ParkProgress.Xp(state, Ctx.Sim.Network);
        int level = ParkProgress.Level(rules, xp.Total);
        int floor = level == 0 ? 0 : rules.LevelXp[level - 1];
        if (ParkProgress.NextLevelXp(rules, level) is { } next)
        {
            _xpBar.Value = (xp.Total - floor) * 1000.0 / Math.Max(1, next - floor);
            _xpText.Text = $"Level {level} · {xp.Total} XP · {next - xp.Total} XP to level {level + 1}";
        }
        else
        {
            _xpBar.Value = 1000;
            _xpText.Text = rules.LevelXp.Count == 0 ? $"{xp.Total} XP" : $"Level {level} (top level) · {xp.Total} XP";
        }
        _xpParts.Text = $"Trails {PerColour(r => $"{xp.TrailMeters[r] / 1000.0:0.0} km")}  +{xp.TrailXp} XP\n" +
                        $"Features {PerColour(r => $"{xp.Features[r]}")}  +{xp.FeatureXp} XP\n" +
                        $"Visitors {xp.AverageVisitors}/day  +{xp.VisitorXp} XP\n" +
                        $"XP per km: {PerColour(r => $"{rules.XpPerKmByRating.For((TrailRating)r)}")} · per feature: " +
                        PerColour(r => $"{rules.XpPerFeatureByRating.For((TrailRating)r)}");

        long day = GameTime.Day(state.Tick);
        _post.Text = ReputationMath.ActivePost(state, day) is { } post
            ? $"{post.Name}: {StarsText(post.StarsTenths)} stars — {PostLine(post)}. Visitors {ReputationMath.InfluencerEffect(state, day) / 10:+0;-0;0} % today, fading until day {post.EndsDay + 1}."
            : rep.InfluencerDue ? "An influencer is on the way today."
            : state.Guests.FirstOrDefault(g => g.IsInfluencer) is { } guest ? $"{ReputationMath.InfluencerName(guest.Id)} is riding the park right now (follow them with F or a click)."
            : rep.Posts.Count > 0 ? $"Last post: {rep.Posts[^1].Name}, day {rep.Posts[^1].Day + 1}, {StarsText(rep.Posts[^1].StarsTenths)} stars."
            : "No influencer has visited yet.";
        if (rep.Posts.Count > 1)
            _post.Text += "\n\nEarlier posts:\n" + string.Join("\n", rep.Posts.SkipLast(1).Reverse().Take(5)
                .Select(p => $"Day {p.Day + 1} · {p.Name} · {StarsText(p.StarsTenths)} stars — {PostLine(p)}"));
    }

    private static string PostLine(InfluencerPost post) => post.StarsTenths switch
    {
        >= 45 => "\"Insane lines, best park around!\"",
        >= 35 => "\"Solid park, worth the trip.\"",
        >= 26 => "\"It's okay. Needs more work.\"",
        _ => "\"Don't bother. Queues and sketchy trails.\"",
    };

    /// <summary>"green a · blue b · red c · black d" for the four ratings.</summary>
    private static string PerColour(Func<int, string> value) =>
        $"green {value(0)} · blue {value(1)} · red {value(2)} · black {value(3)}";

    /// <summary>"3.4" for tenths, "–" without a rating.</summary>
    internal static string StarsText(int? tenths) => tenths is { } t ? $"{t / 10}.{t % 10}" : "–";
}

/// <summary>Five stars, filled to a rating in tenths (all dim without one).</summary>
public partial class StarsView : Control
{
    private int? _tenths;
    private readonly float _size;

    public StarsView() { _size = 16; }

    public StarsView(float size)
    {
        _size = size;
        CustomMinimumSize = new Vector2(size * 5 + 4 * 2, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public int? Tenths
    {
        get => _tenths;
        set
        {
            if (_tenths == value) return;
            _tenths = value;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var gold = new Color(1f, 0.80f, 0.25f);
        var dim = new Color(1, 1, 1, 0.14f);
        float fill = (_tenths ?? 0) / 10f;
        for (int i = 0; i < 5; i++)
        {
            var rect = new Rect2(i * (_size + 2), 0, _size, _size);
            var star = Star(rect);
            DrawColoredPolygon(star, dim);
            float part = Math.Clamp(fill - i, 0, 1);
            if (part <= 0) continue;
            var clip = new[]
            {
                rect.Position, rect.Position + new Vector2(rect.Size.X * part, 0),
                rect.Position + new Vector2(rect.Size.X * part, rect.Size.Y), rect.Position + new Vector2(0, rect.Size.Y),
            };
            foreach (var piece in Geometry2D.IntersectPolygons(star, clip))
                DrawColoredPolygon(piece, gold);
        }
    }

    /// <summary>A five-pointed star filling the square.</summary>
    internal static Vector2[] Star(Rect2 rect)
    {
        var points = new Vector2[10];
        var center = rect.GetCenter() + new Vector2(0, rect.Size.Y * 0.05f);
        float outer = rect.Size.X * 0.5f, inner = outer * 0.45f;
        for (int k = 0; k < 10; k++)
        {
            float angle = -Mathf.Pi / 2 + k * Mathf.Pi / 5;
            float r = k % 2 == 0 ? outer : inner;
            points[k] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
        }
        return points;
    }
}

/// <summary>Reviews per closed day (bars) and the rating at its end (line, 1–5 stars), last 14 days.</summary>
public partial class RatingChart : Control
{
    internal List<DayReport> Days { get; set; } = [];

    public override void _Draw()
    {
        var days = Days.TakeLast(14).ToList();
        var font = UiTheme.Regular;
        if (days.Count == 0)
        {
            DrawString(font, new Vector2(0, 20), "No day closed yet.", HorizontalAlignment.Left, -1, 13, UiTheme.TextDim);
            return;
        }
        float slot = Size.X / 14f;
        float bottom = Size.Y - 16;
        float height = bottom - 6;
        DrawLine(new Vector2(0, bottom), new Vector2(Size.X, bottom), UiTheme.Line, 1);
        float maxReviews = Math.Max(1, days.Max(d => d.Reviews));
        var line = new List<Vector2>();
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            float h = d.Reviews / maxReviews * height * 0.6f;
            DrawRect(new Rect2(i * slot + slot * 0.3f, bottom - h, slot * 0.4f, h), new Color(UiTheme.Accent, 0.35f));
            if (d.RatingTenths is { } r)
                line.Add(new Vector2(i * slot + slot / 2, bottom - (r - 10) / 40f * height));
            DrawString(font, new Vector2(i * slot, Size.Y - 2), $"D{d.Day + 1}", HorizontalAlignment.Center, slot, 10, UiTheme.TextDim);
        }
        var gold = new Color(1f, 0.80f, 0.25f);
        if (line.Count > 1) DrawPolyline(line.ToArray(), gold, 2, true);
        foreach (var p in line) DrawCircle(p, 3, gold);
        if (days[^1].RatingTenths is { } last)
            DrawString(UiTheme.Bold, line[^1] + new Vector2(-slot / 2, -8), ReputationPanel.StarsText(last), HorizontalAlignment.Center, slot, 11, gold);
    }
}
