using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>The seven things a review judges, with what moves each one (in plain words, for the player).</summary>
internal static class Aspects
{
    public static readonly (string Name, string Drives, Func<ReviewScores, int> Of)[] All =
    [
        ("Fun", "How much riders enjoyed their runs and how their mood was: flowy, well-built trails that match their skill, features in good condition, food and drinks.", s => s.Fun),
        ("Safety", "Crashes they had or saw, runs on trails too hard for them, and trails that crash more than their rating allows.", s => s.Safety),
        ("Smoothness", "Time lost waiting: long lift queues and being held up behind slower riders on trails.", s => s.Smoothness),
        ("Challenge", "Whether the hardest trail they rode was hard enough for them: experts want black lines, beginners are happy on green.", s => s.Challenge),
        ("Jumps", "How much they enjoyed the jumps and drops they landed (tables, kickers, doubles, drops).", s => s.Jumps),
        ("Variety", "How many different trails they rode in one visit.", s => s.Variety),
        ("Value", "Runs they got for their money: entrance fee, parking, lift pass and anything overpriced at bars and shops.", s => s.Value),
    ];

    /// <summary>"★★★★☆" for a rating in tenths (rounded to whole stars).</summary>
    public static string Glyphs(int tenths)
    {
        int full = Math.Clamp((tenths + 5) / 10, 0, 5);
        return new string('★', full) + new string('☆', 5 - full);
    }

    /// <summary>A score (0..1000) as stars, 1.0..5.0 (the same scale reviews use).</summary>
    public static double Stars(int permille) => 1 + Math.Clamp(permille, 0, 1000) * 4 / 1000.0;

    /// <summary>A sub-rating: name, stars, a bar and the count it is from; the tooltip says what drives it.</summary>
    public static Control Bar(string name, string drives, int permille, bool hasData)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = $"{name}\n{drives}" };
        row.AddThemeConstantOverride("separation", 10);
        var label = UiTheme.Label(name, 13, bold: true);
        label.CustomMinimumSize = new Vector2(100, 0);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(label);
        var bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1000, Value = hasData ? permille : 0, ShowPercentage = false,
            CustomMinimumSize = new Vector2(200, 10), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var fill = UiTheme.Box(UiTheme.MoodColor(250 + permille * 3 / 4), 4, 0, 0);
        fill.ShadowSize = 0;
        bar.AddThemeStyleboxOverride("fill", fill);
        row.AddChild(bar);
        var stars = UiTheme.Label(hasData ? $"{Stars(permille):0.0} ★" : "–", 13, hasData ? UiTheme.Text : UiTheme.TextDim, bold: true);
        stars.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(stars);
        return row;
    }

    /// <summary>All seven bars for these reviews.</summary>
    public static Control Bars(IEnumerable<Review> reviews)
    {
        var (average, count) = ReviewMath.AspectAverages(reviews);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        foreach (var (name, drives, of) in All)
            box.AddChild(Bar(name, drives, of(average), count > 0));
        return box;
    }
}

/// <summary>Names and words for a review (derived from the guest id and the review; the sim stores none).</summary>
internal static class ReviewText
{
    private static readonly string[] First =
    [
        "Anna", "Ben", "Clara", "David", "Elif", "Felix", "Greta", "Hannes", "Ida", "Jonas", "Kim", "Lea", "Max", "Nora", "Oskar",
        "Paula", "Quentin", "Rosa", "Simon", "Tara", "Ulla", "Vince", "Wiebke", "Yusuf", "Zoe", "Mia", "Luca", "Emma", "Noah", "Lina",
        "Theo", "Sofia", "Jan", "Marie", "Tom", "Hanna", "Erik", "Nele", "Paul", "Amira",
    ];

    private static uint Hash(int id, int salt = 0) => (uint)(id * 2654435761u + (uint)salt * 40503u) ^ (uint)(id >> 3);

    public static string Name(Review review) => review.Influencer
        ? ReputationMath.InfluencerName(review.GuestId)
        : $"{First[Hash(review.GuestId) % First.Length]} {(char)('A' + Hash(review.GuestId, 7) % 26)}.";

    public static string Initials(Review review)
    {
        string name = Name(review).TrimStart('@');
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }

    public static string Group(SkillGroup group) => group switch
    {
        SkillGroup.Beginner => "Beginner",
        SkillGroup.Intermediate => "Intermediate",
        _ => "Expert",
    };

    public static string When(Review review) =>
        review.LeftTick > 0 ? $"Day {review.Day + 1}, {GameTime.Format(review.LeftTick)[^5..]}" : $"Day {review.Day + 1}";

    private static readonly Dictionary<string, string[]> Good = new()
    {
        ["Fun"] = ["Had an absolute blast.", "So much fun, grinning all day.", "Pure flow, loved every run."],
        ["Safety"] = ["Felt safe the whole day.", "Well built, never felt sketchy.", "Trails are clean and safe."],
        ["Smoothness"] = ["Hardly any waiting.", "No queues, lap after lap.", "Got loads of laps in, zero waiting."],
        ["Challenge"] = ["Proper challenging lines.", "Just the right level of spicy.", "Pushed my limits in a good way."],
        ["Jumps"] = ["The jumps are perfectly shaped.", "Airtime heaven.", "Jump line is a dream."],
        ["Variety"] = ["Great variety of trails.", "Something different every run.", "Lots of trails to explore."],
        ["Value"] = ["Great value for money.", "Fair prices.", "Worth every euro."],
    };

    private static readonly Dictionary<string, string[]> Bad = new()
    {
        ["Fun"] = ["Honestly a bit boring.", "The trails just weren't fun.", "Meh. Not much flow."],
        ["Safety"] = ["Felt dangerous at times.", "Saw too many crashes.", "Some sections are sketchy."],
        ["Smoothness"] = ["Spent half the day queuing.", "Way too much waiting.", "Got stuck behind slow riders all day."],
        ["Challenge"] = ["Too tame for me.", "Needs harder lines.", "Nothing really challenging."],
        ["Jumps"] = ["Where are the jumps?", "Needs more jumps.", "The jumps were disappointing."],
        ["Variety"] = ["Same trail over and over.", "Not enough trails.", "Little choice of trails."],
        ["Value"] = ["Way overpriced.", "Too expensive for what you get.", "Prices are a rip-off."],
    };

    private static string Pick(string[] options, int id, int salt) => options[Hash(id, salt) % options.Length];

    /// <summary>
    /// One to three sentences: the best thing (what this guest cared about and got), the worst (what they cared about and
    /// missed), how the day ended.
    /// </summary>
    public static string Body(Review review, ReputationRules rules)
    {
        var values = rules.Values(review.Group);
        int[] weights = values is null ? [1, 1, 1, 1, 1, 1, 1]
            : [values.Fun, values.Safety, values.Smoothness, values.Challenge, values.Jumps, values.Variety, values.Value];
        var s = review.Scores;
        var scored = Aspects.All.Select((a, i) => (a.Name, Score: a.Of(s), Weight: weights[i])).Where(a => a.Weight > 0).ToList();
        var sentences = new List<string>();
        if (scored.Count > 0)
        {
            var best = scored.MaxBy(a => a.Weight * a.Score);
            var worst = scored.MaxBy(a => a.Weight * (1000 - a.Score));
            if (best.Score >= 650) sentences.Add(Pick(Good[best.Name], review.GuestId, 1));
            if (worst.Score < 450 && worst.Name != best.Name) sentences.Add(Pick(Bad[worst.Name], review.GuestId, 2));
            if (sentences.Count == 0)
                sentences.Add(review.StarsTenths >= 35 ? "Solid day out." : review.StarsTenths >= 25 ? "It was okay." : "Not great.");
        }
        string? ending = review.Reason switch
        {
            GuestLeaveReason.Evacuated => "Ended up in the helicopter. Not how I planned the day.",
            GuestLeaveReason.Injured => "Crashed and had to call it a day.",
            GuestLeaveReason.BikeBroken => "Broke my bike and nobody could fix it.",
            GuestLeaveReason.Drunk => "One beer too many, rode home early.",
            GuestLeaveReason.Unhappy => "Left early.",
            _ => null,
        };
        if (ending is not null) sentences.Add(ending);
        return string.Join(" ", sentences);
    }
}

/// <summary>What a guest says about one building (from <see cref="PlaceReview"/>: what happened there; the sim stores no words).</summary>
internal static class PlaceText
{
    private static string Pick(int id, int salt, params string[] options) => options[(uint)(id * 2654435761u + (uint)salt * 40503u) % options.Length];

    /// <summary>A few sentences about this building only: what they got there, a repair or rental bike, a missing seat, the prices.</summary>
    public static string Body(Review review, PlaceReview place, WorldState state)
    {
        int id = review.GuestId;
        var kind = state.Facilities.FirstOrDefault(f => f.Id == place.FacilityId) is { } f
            ? Bikepark.Sim.Facilities.FacilityMath.FindType(state, f.TypeId)?.Kind : null;
        var products = place.Products.Select(p => state.ProductTypes.FirstOrDefault(t => t.Id == p)).OfType<Bikepark.Sim.Facilities.ProductType>().ToList();
        var sentences = new List<string>();

        if (place.Repaired)
            sentences.Add(place.RentalConditionPermille >= 0
                ? Pick(id, 1, "Broke my rental bike, they swapped it right away.", "Rental bike broke, got a new one in minutes.")
                : Pick(id, 1, "Bike broke on the trail and the workshop fixed it in no time. Saved my day!", "Mechanics got my bike running again after a crash. Legends."));
        else if (place.NoPlace > 0 && products.Count == 0)
            sentences.Add(kind == Bikepark.Sim.Facilities.FacilityKind.BikeShop
                ? Pick(id, 2, "Broke my bike and the workshop was too busy to help.", "No free workbench for my broken bike. Day over.")
                : Pick(id, 2, "Couldn't get a seat, ended up with a packed lunch.", "Completely full at lunchtime."));
        else if (place.NoPlace > 0)
            sentences.Add(Pick(id, 2, "Had to wait for a free seat.", "Packed, took a while to get a place."));

        foreach (var p in products)
        {
            string? line = p switch
            {
                { Lunch: true } => Pick(id, 3, "Lunch was tasty.", "Good food for a hungry rider.", "Solid lunch break."),
                { Alcohol: > 0 } => Pick(id, 4, "Cold beer after the runs, perfect.", "Great spot for a drink."),
                { CaffeineMinutes: > 0 } => Pick(id, 5, "Coffee kept me going.", "Proper espresso halfway through the day."),
                { Rental: true } when place.RentalConditionPermille is >= 0 and < 400 => Pick(id, 6, "My rental bike was falling apart.", "The rental bike was in rough shape."),
                { Rental: true } when place.RentalConditionPermille >= 750 => Pick(id, 6, "Rental bike was in top shape.", "Great rental bike, rode like new."),
                { Rental: true } => "Rented a bike, did the job.",
                { TuneUpPermille: > 0 } => Pick(id, 7, "Quick tune-up, bike ran like new.", "The tune-up was worth it."),
                { RedBlackFun: > 0 } => Pick(id, 8, "Suspension setup made the red lines so much better.", "They dialled in my fork, felt amazing."),
                { Protection: true } => Pick(id, 9, "Picked up some pads, felt much safer.", "Bought protectors, good advice."),
                { Overnight: true } => Pick(id, 10, "Nice place to camp.", "Slept well at the campground."),
                { Id: "bikes" } => "Bought a new bike here!",
                { Id: "clothing" } => Pick(id, 11, "Got a new jersey.", "Bought a rain jacket just in time."),
                { Merch: true } => Pick(id, 12, "Grabbed a souvenir.", "Cool merch."),
                _ => null,
            };
            if (line is not null && !sentences.Contains(line)) sentences.Add(line);
            if (sentences.Count >= 3) break;
        }

        if (place.SpentCents > 0)
        {
            long share = place.OverpaidCents * 1000 / place.SpentCents;
            if (share >= 250) sentences.Add(Pick(id, 13, "Way too pricey here.", "Prices are a rip-off."));
            else if (share > 0) sentences.Add("A bit expensive.");
            else if (sentences.Count < 3) sentences.Add(Pick(id, 13, "Fair prices.", "Good value."));
        }
        return sentences.Count == 0 ? "Stopped by." : string.Join(" ", sentences);
    }

    /// <summary>"Bought lunch, coffee · 18 € here".</summary>
    public static string Chips(PlaceReview place, WorldState state)
    {
        var names = place.Products.Select(p => state.ProductTypes.FirstOrDefault(t => t.Id == p)?.Name.ToLowerInvariant() ?? p).ToList();
        var chips = new List<string>();
        if (names.Count > 0) chips.Add("Bought " + string.Join(", ", names));
        if (place.SpentCents > 0) chips.Add($"{UiTheme.Money(place.SpentCents)} here");
        if (place.NoPlace > 0) chips.Add(place.NoPlace == 1 ? "no free place once" : $"no free place {place.NoPlace}×");
        return string.Join(" · ", chips);
    }
}

/// <summary>
/// Reviews to browse like a review site: sort (newest, highest, lowest), filter by stars, group, influencers and what
/// they mention (a trail, lift or building), ten per page. With a fixed mention (an inspector's Reviews tab) the
/// mention filter is left out. With a focus building (its window) only reviews of guests who used or tried to use it are
/// shown, with the stars and words about that building.
/// </summary>
internal sealed partial class ReviewBrowser : VBoxContainer
{
    private const int PageSize = 10;

    private readonly HudContext _ctx;
    private readonly Func<Review, bool>? _fixedFilter;
    private readonly int _focusFacilityId;
    private readonly int _pageSize;
    private readonly VBoxContainer _list;
    private readonly Label _count;
    private readonly Button _prev, _next;
    private OptionButton _sort = null!, _stars = null!, _group = null!;
    private OptionButton? _mention;
    private CheckBox _influencers = null!;
    private readonly List<(string Label, Func<Review, bool> Match)> _mentions = [];
    private int _page;
    private string _signature = "";

    public ReviewBrowser(HudContext ctx, Func<Review, bool>? fixedFilter = null, int pageSize = PageSize, float width = 560, int focusFacilityId = 0)
    {
        _ctx = ctx;
        _focusFacilityId = focusFacilityId;
        _fixedFilter = focusFacilityId != 0 ? r => r.Places.Any(p => p.FacilityId == focusFacilityId) : fixedFilter;
        _pageSize = pageSize;
        CustomMinimumSize = new Vector2(width, 0);
        AddThemeConstantOverride("separation", 8);

        var filters = new HFlowContainer();
        filters.AddThemeConstantOverride("h_separation", 6);
        filters.AddThemeConstantOverride("v_separation", 6);
        _sort = Options(filters, "Newest", "Highest", "Lowest");
        _stars = Options(filters, "All stars", "5 ★", "4 ★", "3 ★", "2 ★", "1 ★");
        _group = Options(filters, "Everyone", "Beginners", "Intermediates", "Experts");
        if (_fixedFilter is null)
        {
            _mention = new OptionButton { FocusMode = FocusModeEnum.None, FitToLongestItem = false, CustomMinimumSize = new Vector2(150, 0) };
            _mention.AddThemeFontSizeOverride("font_size", 12);
            _mention.ItemSelected += _ => Changed();
            filters.AddChild(_mention);
        }
        _influencers = new CheckBox { Text = "Influencers", FocusMode = FocusModeEnum.None };
        _influencers.AddThemeFontSizeOverride("font_size", 12);
        _influencers.Toggled += _ => Changed();
        filters.AddChild(_influencers);
        AddChild(filters);

        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 6);
        AddChild(_list);

        var pager = new HBoxContainer();
        _prev = UiTheme.Button("‹ Newer", () => Turn(-1));
        _next = UiTheme.Button("Older ›", () => Turn(1));
        _count = UiTheme.Label("", 12, UiTheme.TextDim);
        _count.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _count.HorizontalAlignment = HorizontalAlignment.Center;
        _count.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        pager.AddChild(_prev);
        pager.AddChild(_count);
        pager.AddChild(_next);
        AddChild(pager);
    }

    private OptionButton Options(Container into, params string[] items)
    {
        var options = new OptionButton { FocusMode = FocusModeEnum.None };
        options.AddThemeFontSizeOverride("font_size", 12);
        foreach (string item in items) options.AddItem(item);
        options.Selected = 0;
        options.ItemSelected += _ => Changed();
        into.AddChild(options);
        return options;
    }

    private void Changed()
    {
        _page = 0;
        _signature = "";
        Refresh();
    }

    private void Turn(int step)
    {
        _page = Math.Max(0, _page + step);
        _signature = "";
        Refresh();
    }

    /// <summary>Fills the "mentions" choices from the park's trails, lifts and buildings.</summary>
    private void RefreshMentions(WorldState state)
    {
        if (_mention is null) return;
        var items = new List<(string, Func<Review, bool>)> { ("Mentions anything", _ => true) };
        foreach (var way in state.Ways.Where(w => w.Kind == Bikepark.Sim.Trails.WayKind.Trail && w.Built))
        {
            int id = way.Id;
            items.Add((way.Name, r => r.TrailIds.Contains(id)));
        }
        foreach (var lift in state.Lifts)
        {
            int id = lift.Id;
            items.Add((lift.Name, r => r.LiftIds.Contains(id)));
        }
        foreach (var facility in state.Facilities)
        {
            int id = facility.Id;
            items.Add((facility.Name, r => r.FacilityIds.Contains(id)));
        }
        if (items.Select(i => i.Item1).SequenceEqual(_mentions.Select(m => m.Label))) return;
        string? current = _mention.Selected > 0 && _mention.Selected < _mentions.Count ? _mentions[_mention.Selected].Label : null;
        _mentions.Clear();
        _mentions.AddRange(items);
        _mention.Clear();
        foreach (var (label, _) in items) _mention.AddItem(label);
        _mention.Selected = Math.Max(0, current is null ? 0 : _mentions.FindIndex(m => m.Label == current));
    }

    /// <summary>The reviews the filters let through, sorted.</summary>
    private List<Review> Filtered(WorldState state)
    {
        IEnumerable<Review> reviews = state.Reputation.Reviews;
        if (_fixedFilter is not null) reviews = reviews.Where(_fixedFilter);
        if (_mention is not null && _mention.Selected > 0 && _mention.Selected < _mentions.Count)
            reviews = reviews.Where(_mentions[_mention.Selected].Match);
        if (_stars.Selected > 0)
        {
            int stars = 6 - _stars.Selected;
            reviews = reviews.Where(r => (StarsOf(r) + 5) / 10 == stars);
        }
        if (_group.Selected > 0)
        {
            var group = (SkillGroup)(_group.Selected - 1);
            reviews = reviews.Where(r => r.Group == group);
        }
        if (_influencers.ButtonPressed) reviews = reviews.Where(r => r.Influencer);
        // Newest first: the window is in the order written.
        var list = reviews.Reverse().ToList();
        return _sort.Selected switch
        {
            1 => list.OrderByDescending(StarsOf).ToList(),
            2 => list.OrderBy(StarsOf).ToList(),
            _ => list,
        };
    }

    /// <summary>The focus building as this review sees it, or null.</summary>
    private PlaceReview? Place(Review review) =>
        _focusFacilityId == 0 ? null : review.Places.FirstOrDefault(p => p.FacilityId == _focusFacilityId);

    /// <summary>The stars that count here: for the focus building, or the whole visit.</summary>
    private int StarsOf(Review review) => Place(review)?.StarsTenths ?? review.StarsTenths;

    public void Refresh()
    {
        var state = _ctx.Sim.State;
        RefreshMentions(state);
        var reviews = Filtered(state);
        int pages = Math.Max(1, (reviews.Count + _pageSize - 1) / _pageSize);
        _page = Math.Min(_page, pages - 1);
        string signature = $"{state.Reputation.TotalReviews}|{reviews.Count}|{_page}|{state.WaysRevision}";
        if (signature == _signature) return;
        _signature = signature;

        foreach (var child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
        if (reviews.Count == 0)
            _list.AddChild(UiTheme.Label(state.Reputation.Reviews.Count == 0 ? "No reviews yet: guests write one when they leave." : "No reviews match.", 13, UiTheme.TextDim));
        foreach (var review in reviews.Skip(_page * _pageSize).Take(_pageSize))
            _list.AddChild(Card(state, review));
        _count.Text = reviews.Count == 0 ? "" : $"{_page * _pageSize + 1}–{Math.Min(reviews.Count, (_page + 1) * _pageSize)} of {reviews.Count}";
        _prev.Disabled = _page == 0;
        _next.Disabled = _page >= pages - 1;
    }

    /// <summary>One review: avatar, name, group, stars and when; the text; what they rode and spent.</summary>
    private Control Card(WorldState state, Review review)
    {
        var card = new PanelContainer();
        var style = UiTheme.Box(UiTheme.Card, 8, 10, 8, review.Influencer ? new Color(0.95f, 0.45f, 0.75f, 0.8f) : null, review.Influencer ? 1 : 0);
        style.ShadowSize = 0;
        card.AddThemeStyleboxOverride("panel", style);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        card.AddChild(row);

        var avatar = new PanelContainer { CustomMinimumSize = new Vector2(36, 36), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        var hue = review.GuestId * 37 % 100 / 100f;
        avatar.AddThemeStyleboxOverride("panel", UiTheme.Box(Color.FromHsv(hue, 0.45f, 0.65f), 18, 0, 0));
        var initials = UiTheme.Label(ReviewText.Initials(review), 13, Colors.White, bold: true);
        initials.HorizontalAlignment = HorizontalAlignment.Center;
        initials.VerticalAlignment = VerticalAlignment.Center;
        avatar.AddChild(initials);
        row.AddChild(avatar);

        var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        texts.AddThemeConstantOverride("separation", 2);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(UiTheme.Label(ReviewText.Name(review), 13, bold: true));
        head.AddChild(UiTheme.Label(ReviewText.Group(review.Group) + (review.Influencer ? " · influencer" : ""), 11, UiTheme.TextDim));
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        head.AddChild(spacer);
        head.AddChild(UiTheme.Label(ReviewText.When(review), 11, UiTheme.TextDim));
        texts.AddChild(head);
        var place = Place(review);
        int stars = StarsOf(review);
        var starRow = new HBoxContainer();
        starRow.AddThemeConstantOverride("separation", 8);
        starRow.AddChild(UiTheme.Label(Aspects.Glyphs(stars) + $"  {stars / 10.0:0.0}", 13, UiTheme.MoodColor((stars - 10) * 25), bold: true));
        if (place is not null)
            starRow.AddChild(UiTheme.Label($"whole visit {review.StarsTenths / 10.0:0.0} ★", 11, UiTheme.TextDim));
        texts.AddChild(starRow);
        var body = UiTheme.Label(place is not null ? PlaceText.Body(review, place, state) : ReviewText.Body(review, state.ReputationRules), 12);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.CustomMinimumSize = new Vector2(Math.Max(200, CustomMinimumSize.X - 80), 0);
        texts.AddChild(body);

        var chips = new List<string>();
        if (place is not null)
        {
            var only = UiTheme.Label(PlaceText.Chips(place, state), 11, UiTheme.Accent);
            only.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            only.CustomMinimumSize = new Vector2(Math.Max(200, CustomMinimumSize.X - 80), 0);
            if (only.Text.Length > 0) texts.AddChild(only);
            row.AddChild(texts);
            return card;
        }
        var trails = review.TrailIds.Select(id => state.Ways.FirstOrDefault(w => w.Id == id)?.Name).OfType<string>().ToList();
        if (trails.Count > 0) chips.Add("Rode " + string.Join(" · ", trails));
        var places = review.FacilityIds.Select(id => state.Facilities.FirstOrDefault(f => f.Id == id)?.Name).OfType<string>().ToList();
        if (places.Count > 0) chips.Add("Visited " + string.Join(" · ", places));
        if (review.SpentCents > 0) chips.Add($"Spent {UiTheme.Money(review.SpentCents)}");
        if (chips.Count > 0)
        {
            var line = UiTheme.Label(string.Join("   ", chips), 11, UiTheme.Accent);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            line.CustomMinimumSize = new Vector2(Math.Max(200, CustomMinimumSize.X - 80), 0);
            texts.AddChild(line);
        }
        row.AddChild(texts);
        return card;
    }
}
