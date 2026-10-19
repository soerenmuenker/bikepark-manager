using Bikepark.Sim.Core;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Sponsors;

/// <summary>What a sponsor's deal gives the park besides money (see <see cref="SponsorBenefit"/>).</summary>
public enum SponsorBenefitKind : byte
{
    /// <summary>Rental bikes for the fleet cost less.</summary>
    BikeDiscount = 0,

    /// <summary>The goods of these products (<see cref="SponsorBenefit.Targets"/>: product ids) cost less.</summary>
    GoodsDiscount = 1,

    /// <summary>These buildings (facility type ids) and their upgrades cost less.</summary>
    BuildingDiscount = 2,

    /// <summary>The sponsor adds this share on top of every parking fee.</summary>
    ParkingRevenue = 3,

    /// <summary>New loans come with less interest.</summary>
    LoanInterest = 4,

    /// <summary>Lifts cost less to build or restore.</summary>
    LiftDiscount = 5,

    /// <summary>Guests buying these products (product ids) get this much more mood (permille of the product's mood).</summary>
    MoodBoost = 6,
}

/// <summary>One benefit of a brand: <see cref="Permille"/> off (or on top), for <see cref="Targets"/> where the kind needs them.</summary>
public sealed class SponsorBenefit
{
    public SponsorBenefitKind Kind { get; set; }
    public int Permille { get; set; }
    public List<string> Targets { get; set; } = [];
}

/// <summary>A brand that sponsors trails or the whole park (<c>data/sponsors.json</c>, copied into the world).</summary>
public sealed class SponsorType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Put in front of a sponsored trail's name ("Gravity Red Rocket").</summary>
    public string ShortName { get; set; } = "";

    /// <summary>The colour of its signs (#rrggbb).</summary>
    public string Color { get; set; } = "#d04020";

    /// <summary>Park level needed before the brand deals with the park at all.</summary>
    public int RequiredLevel { get; set; }

    // ---- A trail deal ----

    /// <summary>What a trail needs: at least this rating, these stars (average fun as 1.0..5.0, tenths) and runs per day.</summary>
    public TrailRating MinRating { get; set; }
    public int MinStarsTenths { get; set; } = 30;
    public int MinRunsPerDay { get; set; }
    public long DailyCents { get; set; }
    public long PerRunCents { get; set; }
    public int Days { get; set; } = 14;

    // ---- The park deal (0 = the brand doesn't sponsor whole parks) ----

    public long ParkDailyCents { get; set; }

    /// <summary>Paid per visitor of the day on top.</summary>
    public long ParkPerVisitorCents { get; set; }
    public int ParkMinLevel { get; set; }

    /// <summary>The park's rating needed (tenths of a star; 0 = no rating needed).</summary>
    public int ParkMinStarsTenths { get; set; }
    public int ParkDays { get; set; } = 28;

    /// <summary>How guests take the brand while it sponsors the park: mood on arrival (negative: too commercial).</summary>
    public int Mood { get; set; }

    public List<SponsorBenefit> Benefits { get; set; } = [];

    public bool SponsorsParks => ParkDailyCents > 0;

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"sponsorTypes[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("sponsorTypes: id is required");
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(ShortName)) errors.Add($"{p}: name and shortName are required");
        if (Color is not { Length: 7 } || Color[0] != '#') errors.Add($"{p}.color must be #rrggbb");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel || ParkMinLevel is < 0 or > Reputation.ReputationRules.MaxLevel)
            errors.Add($"{p}: levels must be within 0..{Reputation.ReputationRules.MaxLevel}");
        if (!Enum.IsDefined(MinRating)) errors.Add($"{p}.minRating is unknown");
        if (MinStarsTenths is < 10 or > 50 || ParkMinStarsTenths is < 0 or > 50) errors.Add($"{p}: stars within 1.0..5.0 (tenths)");
        if (MinRunsPerDay < 0 || DailyCents < 0 || PerRunCents < 0 || ParkDailyCents < 0 || ParkPerVisitorCents < 0) errors.Add($"{p}: amounts must be >= 0");
        if (Days is < 1 or > 365 || ParkDays is < 1 or > 365) errors.Add($"{p}: days within 1..365");
        if (Mood is < -500 or > 500) errors.Add($"{p}.mood must be within -500..500");
        foreach (var b in Benefits)
        {
            if (!Enum.IsDefined(b.Kind)) errors.Add($"{p}.benefits: unknown kind");
            if (b.Permille is < 0 or > 1000) errors.Add($"{p}.benefits: permille within 0..1000");
            if (b.Kind is SponsorBenefitKind.GoodsDiscount or SponsorBenefitKind.BuildingDiscount or SponsorBenefitKind.MoodBoost && b.Targets.Count == 0)
                errors.Add($"{p}.benefits: {b.Kind} needs targets");
        }
        return errors;
    }
}

/// <summary>How brands deal with the park, and the photo spots (scenario content).</summary>
public sealed class SponsorRules
{
    /// <summary>Brands look at the park every this many days for a trail (or the park) to sponsor.</summary>
    public int OfferCheckDays { get; set; } = 3;

    /// <summary>Chance a brand that finds a fitting trail (or the park) makes an offer, per check.</summary>
    public int OfferChancePermille { get; set; } = 350;

    /// <summary>An offer stands this many days; an application is answered after this many days.</summary>
    public int OfferDays { get; set; } = 7;
    public int ApplyAnswerDays { get; set; } = 2;

    /// <summary>A trail sponsor's benefits count this much of a park sponsor's (permille).</summary>
    public int TrailBenefitPermille { get; set; } = 400;

    /// <summary>The sign at the entrance: crew work (carpentry) and wood.</summary>
    public int SignWorkMinutes { get; set; } = 240;
    public int SignWood { get; set; } = 10;

    /// <summary>A sponsored trail closed (worn out, by the player) at the end of more days in a row than this ends the deal.</summary>
    public int ClosedDaysAllowed { get; set; } = 2;

    // ---- Photo spots ----

    public long CameraCostCents { get; set; } = 350_000;
    public long CameraUpkeepPerDayCents { get; set; } = 3_000;

    /// <summary>Chance a rider filmed at a camera buys the clip, at full fun (scaled by the jump's fun and the clip's price).</summary>
    public int ClipChancePermille { get; set; } = 250;

    /// <summary>Demand each clip shared online adds (permille), how much of it is left the next day, and the most it adds up to.</summary>
    public int BuzzPerClipPermille { get; set; } = 2;
    public int BuzzKeepPermille { get; set; } = 850;
    public int MaxBuzzPermille { get; set; } = 120;

    public List<string> Validate()
    {
        var errors = new List<string>();
        const string p = "sponsorRules";
        if (OfferCheckDays is < 1 or > 60 || OfferDays is < 1 or > 60 || ApplyAnswerDays is < 0 or > 30) errors.Add($"{p}: days out of range");
        if (OfferChancePermille is < 0 or > 1000 || TrailBenefitPermille is < 0 or > 1000) errors.Add($"{p}: permille within 0..1000");
        if (SignWorkMinutes is < 0 or > 10_000 || SignWood is < 0 or > 1000) errors.Add($"{p}: sign work and wood out of range");
        if (ClosedDaysAllowed is < 0 or > 60) errors.Add($"{p}.closedDaysAllowed within 0..60");
        if (CameraCostCents < 0 || CameraUpkeepPerDayCents < 0) errors.Add($"{p}: camera costs must be >= 0");
        if (ClipChancePermille is < 0 or > 1000 || BuzzPerClipPermille is < 0 or > 1000 || BuzzKeepPermille is < 0 or > 1000 || MaxBuzzPermille is < 0 or > 2000)
            errors.Add($"{p}: photo permille out of range");
        return errors;
    }
}

public enum SponsorshipStatus : byte
{
    /// <summary>The park applied; the brand answers at <see cref="Sponsorship.AnswerTick"/>.</summary>
    Applied = 0,

    /// <summary>The brand offers a deal until <see cref="Sponsorship.ExpiresTick"/>.</summary>
    Offered = 1,

    /// <summary>Running: paying once the sign stands, until <see cref="Sponsorship.EndDay"/>.</summary>
    Active = 2,

    /// <summary>Over (or turned down, or never answered): kept for the log.</summary>
    Ended = 3,
}

/// <summary>A deal with a brand, for a trail (<see cref="WayId"/>) or the whole park (0), from application to its end.</summary>
public sealed class Sponsorship
{
    public int Id { get; set; }
    public string SponsorId { get; set; } = "";

    /// <summary>The trail (0: the park).</summary>
    public int WayId { get; set; }
    public SponsorshipStatus Status { get; set; }
    public long AnswerTick { get; set; }
    public long ExpiresTick { get; set; }
    public long StartDay { get; set; }
    public long EndDay { get; set; }

    /// <summary>The crew built the sign: it pays and its benefits count.</summary>
    public bool SignBuilt { get; set; }
    public int SignJobId { get; set; }
    public long PaidCents { get; set; }

    /// <summary>The trail's own name, back after the deal.</summary>
    public string OriginalName { get; set; } = "";

    /// <summary>Days in a row the trail closed the day closed.</summary>
    public int ClosedDays { get; set; }

    /// <summary>Why it ended (or was turned down), for the log.</summary>
    public string Note { get; set; } = "";

    public bool IsPark => WayId == 0;
    public bool Pays => Status == SponsorshipStatus.Active && SignBuilt;
}

/// <summary>Who fits whom, what deals pay, and the benefits in effect. Pure.</summary>
public static class SponsorMath
{
    public const int MaxDeals = 40;

    public static SponsorType? Find(WorldState state, string id) => state.SponsorTypes.FirstOrDefault(s => s.Id == id);

    /// <summary>Runs per day over the trail's recent days (and today).</summary>
    public static int RunsPerDay(Way trail)
    {
        var history = trail.Stats.History;
        if (history.Count == 0) return trail.Stats.RunsToday;
        return (int)(history.Sum(d => (long)d.Runs) / history.Count);
    }

    /// <summary>The trail's average fun as stars (tenths, 10..50); 30 before anybody rode it.</summary>
    public static int StarsTenths(Way trail) =>
        trail.Stats.Runs == 0 ? 30 : 10 + (int)Math.Clamp(trail.Stats.SumFun / trail.Stats.Runs, 0, 1000) * 40 / 1000;

    /// <summary>A trail's appeal to brands (0..1000): rating, stars and traffic.</summary>
    public static int Score(WayNetwork network, Way trail)
    {
        if (!network.TryGetGeometry(trail.Id, out var geometry)) return 0;
        int rating = (int)geometry.Rating * 120; // 0..360
        int stars = (StarsTenths(trail) - 10) * 10; // 0..400
        int traffic = Math.Min(240, RunsPerDay(trail) * 2);
        return Math.Clamp(rating + stars + traffic, 0, 1000);
    }

    /// <summary>A deal of the brand that is running, offered or applied for.</summary>
    public static Sponsorship? Open(WorldState state, string sponsorId) =>
        state.Sponsorships.FirstOrDefault(s => s.SponsorId == sponsorId && s.Status != SponsorshipStatus.Ended);

    /// <summary>The running (or offered) deal on a trail or the park (0).</summary>
    public static Sponsorship? DealOn(WorldState state, int wayId, bool offeredToo = false) =>
        state.Sponsorships.FirstOrDefault(s => s.WayId == wayId && (s.Status == SponsorshipStatus.Active || offeredToo && s.Status == SponsorshipStatus.Offered));

    /// <summary>Why the brand wouldn't sponsor this trail now, or null.</summary>
    public static string? TrailMisfit(WorldState state, WayNetwork network, SponsorType brand, Way trail, int level)
    {
        if (state.Rules.UnlockLevel(level) < brand.RequiredLevel) return $"{brand.Name} deals with parks from level {brand.RequiredLevel}.";
        if (trail.Kind != WayKind.Trail || !trail.Built || !network.TryGetGeometry(trail.Id, out var geometry)) return "Only built trails can be sponsored.";
        if (!network.IsConnected(trail)) return $"{trail.Name} isn't connected.";
        if (DealOn(state, trail.Id) is { } running && running.SponsorId != brand.Id) return $"{trail.Name} already has a sponsor.";
        if (geometry.Rating < brand.MinRating) return $"{brand.Name} wants a {brand.MinRating.ToString().ToLowerInvariant()} trail or harder.";
        if (StarsTenths(trail) < brand.MinStarsTenths) return $"{brand.Name} wants {brand.MinStarsTenths / 10.0:0.0}★ of fun ({trail.Name}: {StarsTenths(trail) / 10.0:0.0}★).";
        if (RunsPerDay(trail) < brand.MinRunsPerDay) return $"{brand.Name} wants {brand.MinRunsPerDay} runs a day ({trail.Name}: {RunsPerDay(trail)}).";
        return null;
    }

    /// <summary>Why the brand wouldn't sponsor the whole park now, or null.</summary>
    public static string? ParkMisfit(WorldState state, SponsorType brand, int level)
    {
        if (!brand.SponsorsParks) return $"{brand.Name} doesn't sponsor whole parks.";
        if (state.Rules.UnlockLevel(level) < Math.Max(brand.RequiredLevel, brand.ParkMinLevel)) return $"{brand.Name} sponsors parks from level {brand.ParkMinLevel}.";
        if (DealOn(state, 0) is { } running && running.SponsorId != brand.Id) return "The park already has a sponsor.";
        if (brand.ParkMinStarsTenths > 0 && (Reputation.ReputationMath.RatingTenths(state) ?? 0) < brand.ParkMinStarsTenths)
            return $"{brand.Name} wants a park rated {brand.ParkMinStarsTenths / 10.0:0.0}★.";
        return null;
    }

    /// <summary>The best trail the brand would sponsor (score, then id), or null.</summary>
    public static Way? BestTrail(WorldState state, WayNetwork network, SponsorType brand, int level) =>
        network.Trails.Where(t => TrailMisfit(state, network, brand, t, level) is null && DealOn(state, t.Id, offeredToo: true) is null)
            .OrderByDescending(t => Score(network, t)).ThenBy(t => t.Id).FirstOrDefault();

    /// <summary>The benefit of a kind (for a target, where it needs one) the park has now: the best of its paying deals, permille.</summary>
    public static int Benefit(WorldState state, SponsorBenefitKind kind, string? target = null)
    {
        int best = 0;
        foreach (var deal in state.Sponsorships)
        {
            if (!deal.Pays || Find(state, deal.SponsorId) is not { } brand) continue;
            foreach (var b in brand.Benefits)
            {
                if (b.Kind != kind || target is not null && b.Targets.Count > 0 && !b.Targets.Contains(target)) continue;
                int value = deal.IsPark ? b.Permille : b.Permille * state.SponsorRules.TrailBenefitPermille / 1000;
                best = Math.Max(best, value);
            }
        }
        return best;
    }

    /// <summary>A price with the best discount of that kind taken off.</summary>
    public static long Discounted(WorldState state, SponsorBenefitKind kind, long cents, string? target = null) =>
        cents - cents * Benefit(state, kind, target) / 1000;

    /// <summary>The name a trail gets while the brand sponsors it (within the name length; its own name if that's taken).</summary>
    public static string SponsoredName(WorldState state, SponsorType brand, Way trail)
    {
        string name = $"{brand.ShortName} {trail.Name}";
        if (name.Length > Commands.BuildWayCommand.MaxNameLength) name = name[..Commands.BuildWayCommand.MaxNameLength].TrimEnd();
        return Commands.RenameTrailCommand.CannotName(state, name, trail.Id) is null ? name : trail.Name;
    }

    /// <summary>What the deal pays today: per day, plus per run (trail) or per visitor (park).</summary>
    public static long Payment(WorldState state, SponsorType brand, Sponsorship deal) =>
        deal.IsPark
            ? brand.ParkDailyCents + brand.ParkPerVisitorCents * state.Stats.VisitorsToday
            : brand.DailyCents + brand.PerRunCents * (state.Ways.FirstOrDefault(w => w.Id == deal.WayId)?.Stats.RunsToday ?? 0);

    /// <summary>The park's name as guests see it (with its sponsor once the sign stands).</summary>
    public static string ParkTitle(WorldState state) =>
        DealOn(state, 0) is { Pays: true } deal && Find(state, deal.SponsorId) is { } brand
            ? $"{state.Park.Name} presented by {brand.Name}" : state.Park.Name;

    /// <summary>A short line of the brand's benefits, e.g. "rental bikes −40 % · beer goods −60 %".</summary>
    public static string Describe(SponsorType brand, int scalePermille = 1000) =>
        string.Join(" · ", brand.Benefits.Select(b =>
        {
            int v = b.Permille * scalePermille / 1000 / 10;
            string targets = string.Join(", ", b.Targets);
            return b.Kind switch
            {
                SponsorBenefitKind.BikeDiscount => $"rental bikes −{v} %",
                SponsorBenefitKind.GoodsDiscount => $"{targets} goods −{v} %",
                SponsorBenefitKind.BuildingDiscount => $"{targets.Replace('_', ' ')} −{v} %",
                SponsorBenefitKind.ParkingRevenue => $"+{v} % on parking fees",
                SponsorBenefitKind.LoanInterest => "loan interest at the lowest rate",
                SponsorBenefitKind.LiftDiscount => $"lifts −{v} %",
                SponsorBenefitKind.MoodBoost => $"{targets}: +{v} % mood",
                _ => b.Kind.ToString(),
            };
        }));

    public static long Today(long tick) => GameTime.Day(tick);
}
