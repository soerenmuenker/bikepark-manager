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
    int Value = 0);

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
