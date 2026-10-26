namespace Bikepark.Sim.Reputation;

/// <summary>
/// One guest's review, written when they left. Stars in tenths (10..50); the aspects (0..1000) are what the stars were
/// weighted from (see <see cref="ReviewMath"/>).
/// </summary>
public sealed record Review(
    long Day,
    int GuestId,
    SkillGroup Group,
    int StarsTenths,
    int Fun,
    int Safety,
    int Smoothness,
    int Challenge,
    int Jumps,
    int Variety,
    bool Influencer = false,
    int Value = 0)
{
    /// <summary>When the guest left (tick).</summary>
    public long LeftTick { get; init; }

    /// <summary>Trails ridden, most runs first (at most <see cref="ReviewMath.MaxTrails"/>).</summary>
    public List<int> TrailIds { get; init; } = [];

    /// <summary>Bars and shops the guest bought something at, in the order first used (at most <see cref="ReviewMath.MaxFacilities"/>).</summary>
    public List<int> FacilityIds { get; init; } = [];

    /// <summary>
    /// What the guest says about each building they used or tried to use (no free seat, bench or bike), in the order first
    /// used (at most <see cref="ReviewMath.MaxFacilities"/>): its own stars and what happened there.
    /// </summary>
    public List<PlaceReview> Places { get; init; } = [];

    /// <summary>Lifts the guest rode, in the order first ridden (at most <see cref="ReviewMath.MaxLifts"/>).</summary>
    public List<int> LiftIds { get; init; } = [];

    /// <summary>Everything the guest paid during the visit (entry, parking, lift pass, food, nights, rental...).</summary>
    public long SpentCents { get; init; }

    public Events.GuestLeaveReason Reason { get; init; }

    /// <summary>The guest was hurt in a crash.</summary>
    public bool Crashed { get; init; }

    /// <summary>The visit's aspect scores.</summary>
    public ReviewScores Scores => new(Fun, Safety, Smoothness, Challenge, Jumps, Variety, Value);
}

/// <summary>
/// One building in a review (see <see cref="ReviewMath.PlaceStars"/>): stars in tenths (10..50) from its value (overpaid
/// share of what was spent there) and its service (no free seat, bench or rental bike; the rental bike's condition; a broken
/// bike fixed), with a little of the guest's mood; and what happened there, for the words.
/// </summary>
public sealed record PlaceReview(int FacilityId, int StarsTenths, int Value, int Service)
{
    /// <summary>Products bought there, in the order first bought.</summary>
    public List<string> Products { get; init; } = [];
    public long SpentCents { get; init; }
    public long OverpaidCents { get; init; }

    /// <summary>Times the guest found no free seat or workbench there.</summary>
    public int NoPlace { get; init; }

    /// <summary>Condition of the rental bike from there when given back (permille; -1: none rented there).</summary>
    public int RentalConditionPermille { get; init; } = -1;

    /// <summary>A bike broken in a crash was fixed or swapped there.</summary>
    public bool Repaired { get; init; }
}

/// <summary>
/// An influencer's FakeSocial post: their visit's stars and the demand change it brings (on its day; it fades to 0 by
/// <see cref="EndsDay"/>).
/// </summary>
public sealed record InfluencerPost(long Day, int GuestId, string Name, int StarsTenths, int EffectPermille, long EndsDay);

/// <summary>Reviews, influencer visits and the visitor history the park XP is derived from. Plain state, saved with the game.</summary>
public sealed class ReputationState
{
    /// <summary>The recent reviews in the order written, at most <see cref="ReputationRules.WindowSize"/> per group.</summary>
    public List<Review> Reviews { get; set; } = [];

    /// <summary>All-time review counts (overall and per group).</summary>
    public long TotalReviews { get; set; }
    public long BeginnerReviews { get; set; }
    public long IntermediateReviews { get; set; }
    public long ExpertReviews { get; set; }

    public int ReviewsToday { get; set; }

    /// <summary>An influencer comes today: the next guest admitted is them.</summary>
    public bool InfluencerDue { get; set; }

    /// <summary>Clips shared online lately: extra demand (permille), fading day by day (see Sponsors.SponsorRules).</summary>
    public int BuzzPermille { get; set; }

    /// <summary>The day the last influencer was booked (-1: none yet).</summary>
    public long LastInfluencerDay { get; set; } = -1;

    /// <summary>Posts in the order published (the last few).</summary>
    public List<InfluencerPost> Posts { get; set; } = [];

    /// <summary>Visitors of the last few days (oldest first), for the park XP.</summary>
    public List<int> DailyVisitors { get; set; } = [];

    /// <summary>The level last announced (XP and the level are derived; this only lets the level-up fire once).</summary>
    public int Level { get; set; }

    public long ReviewsOf(SkillGroup group) => group switch
    {
        SkillGroup.Beginner => BeginnerReviews,
        SkillGroup.Intermediate => IntermediateReviews,
        _ => ExpertReviews,
    };
}
