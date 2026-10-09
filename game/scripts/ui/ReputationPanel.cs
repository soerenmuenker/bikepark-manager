using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reputation;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Reputation menu: the overall rating, one card per skill group (its rating and what it values), how reputation drives
/// visitor demand, the park level with its XP parts, FakeSocial posts and the latest reviews.
/// </summary>
public partial class ReputationPanel : HudPanel
{
    private StarsView _stars = null!;
    private Label _rating = null!, _reviews = null!, _level = null!, _demand = null!, _ratingHint = null!;
    private Label _demandParts = null!, _xpText = null!, _xpParts = null!, _post = null!, _latest = null!;
    private ProgressBar _xpBar = null!;
    private readonly List<(SkillGroup Group, StarsView Stars, Label Rating, Label Count)> _groups = [];
    private RatingChart _chart = null!;
    private bool _built;

    public override string Title => "Reputation";

    protected override void Build()
    {
        var top = Row(24);
        var overall = new VBoxContainer();
        overall.AddThemeConstantOverride("separation", 2);
        overall.AddChild(UiTheme.Label("RATING", 11, UiTheme.TextDim, bold: true));
        var starsRow = Row(8);
        _stars = new StarsView(22);
        starsRow.AddChild(_stars);
        _rating = UiTheme.Label("–", 24, bold: true);
        starsRow.AddChild(_rating);
        overall.AddChild(starsRow);
        _ratingHint = UiTheme.Label("", 11, UiTheme.TextDim);
        overall.AddChild(_ratingHint);
        top.AddChild(overall);
        top.AddChild(UiTheme.StatTile("Reviews", out _reviews));
        top.AddChild(UiTheme.StatTile("Park level", out _level));
        top.AddChild(UiTheme.StatTile("Visitor demand", out _demand));
        Body.AddChild(top);
        _demandParts = UiTheme.Label("", 12, UiTheme.TextDim);
        Body.AddChild(_demandParts);
        Body.AddChild(UiTheme.Separator());
        Body.AddChild(UiTheme.Label("WHAT RIDERS VALUE — each skill group rates its own visit", 11, UiTheme.TextDim, bold: true));
        var groups = Row(10);
        Body.AddChild(groups);
        _groupRow = groups;
        Body.AddChild(UiTheme.Separator());

        // Bottom: level and FakeSocial (left), latest reviews and the chart (right).
        var bottom = Row(24);
        Body.AddChild(bottom);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        left.AddThemeConstantOverride("separation", 6);
        bottom.AddChild(left);
        left.AddChild(UiTheme.Label("PARK LEVEL — trails, diversity, visitors", 11, UiTheme.TextDim, bold: true));
        _xpText = UiTheme.Label("", 13, bold: true);
        left.AddChild(_xpText);
        _xpBar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(330, 10), MaxValue = 1000 };
        left.AddChild(_xpBar);
        _xpParts = UiTheme.Label("", 12, UiTheme.TextDim);
        left.AddChild(_xpParts);
        left.AddChild(UiTheme.Label("FAKESOCIAL", 11, UiTheme.TextDim, bold: true));
        _post = UiTheme.Label("", 12);
        _post.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _post.CustomMinimumSize = new Vector2(330, 0);
        left.AddChild(_post);

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
        right.AddThemeConstantOverride("separation", 6);
        bottom.AddChild(right);
        right.AddChild(UiTheme.Label("LATEST REVIEWS", 11, UiTheme.TextDim, bold: true));
        _latest = UiTheme.Label("", 12);
        right.AddChild(_latest);
        right.AddChild(UiTheme.Label("LAST DAYS — rating and reviews/day", 11, UiTheme.TextDim, bold: true));
        _chart = new RatingChart { CustomMinimumSize = new Vector2(300, 70) };
        right.AddChild(_chart);
    }

    private HBoxContainer _groupRow = null!;

    /// <summary>The group cards need the rules (the world), so they are built on the first refresh (and again after a load).</summary>
    private void BuildGroups()
    {
        foreach (var child in _groupRow.GetChildren()) child.QueueFree();
        _groups.Clear();
        foreach (var values in Ctx.Sim.State.ReputationRules.Groups)
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 8, 12, 10));
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(206, 0) };
            box.AddThemeConstantOverride("separation", 4);
            card.AddChild(box);
            box.AddChild(UiTheme.Label(values.Name.ToUpperInvariant(), 12, UiTheme.Accent, bold: true));
            var row = Row(6);
            var stars = new StarsView(14);
            row.AddChild(stars);
            var rating = UiTheme.Label("–", 15, bold: true);
            row.AddChild(rating);
            box.AddChild(row);
            var count = UiTheme.Label("", 11, UiTheme.TextDim);
            box.AddChild(count);
            var text = UiTheme.Label(values.Values, 11, UiTheme.TextDim);
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.CustomMinimumSize = new Vector2(206, 0);
            box.AddChild(text);
            foreach (var (aspect, weight) in new[]
                     {
                         ("Fun", values.Fun), ("Safety", values.Safety), ("Smoothness", values.Smoothness),
                         ("Challenge", values.Challenge), ("Jumps", values.Jumps), ("Variety", values.Variety), ("Value", values.Value),
                     }.Where(a => a.Item2 > 0).OrderByDescending(a => a.Item2))
            {
                var line = Row(6);
                var name = UiTheme.Label(aspect, 11);
                name.CustomMinimumSize = new Vector2(76, 0);
                line.AddChild(name);
                var bar = new ProgressBar
                {
                    ShowPercentage = false, MaxValue = 1000, Value = weight, CustomMinimumSize = new Vector2(84, 8),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = $"{weight / 10} % of the stars",
                };
                line.AddChild(bar);
                line.AddChild(UiTheme.Label($"{weight / 10} %", 11, UiTheme.TextDim));
                box.AddChild(line);
            }
            _groupRow.AddChild(card);
            _groups.Add((values.Group, stars, rating, count));
        }
        if (_groups.Count == 0)
            _groupRow.AddChild(UiTheme.Label("Reputation is off in this scenario.", 12, UiTheme.TextDim));
    }

    public override void Refresh()
    {
        var state = Ctx.Sim.State;
        var rules = state.ReputationRules;
        var rep = state.Reputation;
        if (!_built || _groups.Count != rules.Groups.Count)
        {
            BuildGroups();
            _built = true;
        }

        int? overall = ReputationMath.RatingTenths(state);
        _stars.Tenths = overall;
        _rating.Text = StarsText(overall);
        _ratingHint.Text = overall is null
            ? $"No rating yet: {rep.TotalReviews} of {rules.MinReviewsForRating} reviews"
            : $"average of the last {rep.Reviews.Count} reviews";
        _reviews.Text = $"{rep.TotalReviews}";

        var xp = ParkProgress.Xp(state, Ctx.Sim.Network);
        int level = ParkProgress.Level(rules, xp.Total);
        _level.Text = $"{level} / {ReputationRules.MaxLevel}";

        var demand = ReputationMath.Demand(state);
        _demand.Text = $"{demand.Total / 10} %";
        _demand.AddThemeColorOverride("font_color", demand.Total >= 1000 ? UiTheme.Good : demand.Total >= 700 ? UiTheme.Warn : UiTheme.Bad);
        _demandParts.Text = !rules.Enabled
            ? "Reputation is off: guests come at the normal rate."
            : $"Visibility {demand.Visibility / 10} % (grows with reviews)  ·  rating ×{demand.Rating / 1000.0:0.00}" +
              (overall is null ? " (no rating yet)" : "") +
              (demand.Influencer != 0 ? $"  ·  influencer {demand.Influencer / 10:+0;-0} %" : "");

        foreach (var (group, stars, rating, count) in _groups)
        {
            int? tenths = ReputationMath.RatingTenths(state, group);
            stars.Tenths = tenths;
            rating.Text = StarsText(tenths);
            long reviews = rep.ReviewsOf(group);
            count.Text = tenths is null ? $"{reviews} of {rules.MinReviewsForRating} reviews" : $"{reviews} reviews";
        }

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
        _xpParts.Text = $"Trails {xp.TrailMeters / 1000.0:0.0} km  +{xp.TrailXp} XP\n" +
                        $"Trail ratings {xp.Ratings} of 4  +{xp.RatingXp} XP\n" +
                        $"Feature kinds {xp.FeatureKinds} of 7  +{xp.FeatureXp} XP\n" +
                        $"Visitors {xp.AverageVisitors}/day  +{xp.VisitorXp} XP";

        long day = GameTime.Day(state.Tick);
        _post.Text = ReputationMath.ActivePost(state, day) is { } post
            ? $"{post.Name}: {StarsText(post.StarsTenths)} stars — {PostLine(post)}. Visitors {ReputationMath.InfluencerEffect(state, day) / 10:+0;-0;0} % today, fading until day {post.EndsDay + 1}."
            : rep.InfluencerDue ? "An influencer is on the way today."
            : state.Guests.FirstOrDefault(g => g.IsInfluencer) is { } guest ? $"{ReputationMath.InfluencerName(guest.Id)} is riding the park right now (follow them with F or a click)."
            : rep.Posts.Count > 0 ? $"Last post: {rep.Posts[^1].Name}, day {rep.Posts[^1].Day + 1}, {StarsText(rep.Posts[^1].StarsTenths)} stars."
            : "No influencer has visited yet.";

        _latest.Text = rep.Reviews.Count == 0
            ? "No reviews yet: guests write one when they leave after riding."
            : string.Join('\n', rep.Reviews.TakeLast(4).Reverse().Select(r =>
                $"{StarsText(r.StarsTenths)} ★ {r.Group}{(r.Influencer ? " (influencer)" : "")}: {ReviewWords(r)}"));

        _chart.Days = Ctx.Days;
        _chart.QueueRedraw();
    }

    /// <summary>The review's best and worst aspect, in words.</summary>
    private static string ReviewWords(Review r)
    {
        var aspects = new[] { ("fun", r.Fun), ("safe", r.Safety), ("smooth", r.Smoothness), ("challenging", r.Challenge), ("jumps", r.Jumps), ("variety", r.Variety), ("value", r.Value) };
        var best = aspects.MaxBy(a => a.Item2);
        var worst = aspects.MinBy(a => a.Item2);
        return $"liked {best.Item1} ({best.Item2 / 10}), missed {worst.Item1} ({worst.Item2 / 10})";
    }

    private static string PostLine(InfluencerPost post) => post.StarsTenths switch
    {
        >= 45 => "\"Insane lines, best park around!\"",
        >= 35 => "\"Solid park, worth the trip.\"",
        >= 26 => "\"It's okay. Needs more work.\"",
        _ => "\"Don't bother. Queues and sketchy trails.\"",
    };

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
