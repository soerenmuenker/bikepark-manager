namespace Bikepark.Sim.Trails;

public enum FeatureMaterial : byte
{
    Dirt = 0,
    Wood = 1,
}

/// <summary>The shape of a feature; decides how the view draws it.</summary>
public enum FeatureKind : byte
{
    Berm = 0,
    Rollers = 1,
    Table = 2,
    Double = 3,
    WallRide = 4,
    Kicker = 5,
    Drop = 6,
}

/// <summary>
/// A trail feature model from <c>data/trail_features.json</c>, copied into the world. Gradients are along the direction
/// of travel in tenths of the -10..+10 score (see <see cref="Gradient"/>) and must hold on every covered segment; turn
/// is the segment's <see cref="WaySegment.TurnPermille"/>.
/// </summary>
public sealed class TrailFeatureType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public FeatureMaterial Material { get; set; }
    public FeatureKind Kind { get; set; }

    /// <summary>Distance along the trail it covers.</summary>
    public int LengthMeters { get; set; } = 10;

    /// <summary>0..1000, on the segment difficulty scale.</summary>
    public int Difficulty { get; set; } = 300;

    /// <summary>Steepest drop allowed on a covered segment (e.g. -50 = -5.0).</summary>
    public int MinGradient { get; set; } = -50;

    /// <summary>Steepest climb (or, if negative, the least drop) allowed on a covered segment.</summary>
    public int MaxGradient { get; set; }

    /// <summary>At least one covered segment must turn this much (0 = straight is fine).</summary>
    public int MinTurn { get; set; }

    /// <summary>How much flow and technical riders enjoy it, 0..1000.</summary>
    public int FlowAffinity { get; set; } = 500;
    public int TechAffinity { get; set; } = 500;

    /// <summary>How many fun samples riding it counts as (a trail segment counts as one).</summary>
    public int FunWeight { get; set; } = 1;

    /// <summary>Crew-minutes to build it at base speed (digging for dirt, carpentry for wood).</summary>
    public int WorkMinutes { get; set; } = 480;

    /// <summary>Wood used to build it.</summary>
    public int Wood { get; set; }

    public long LengthCm => LengthMeters * 100L;

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"trailFeatureTypes[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("trailFeatureTypes: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (LengthMeters is < 1 or > 100) errors.Add($"{p}.lengthMeters must be within 1..100");
        if (Difficulty is < 0 or > 1000) errors.Add($"{p}.difficulty must be within 0..1000");
        if (MinGradient < -Gradient.MaxTenths || MaxGradient > Gradient.MaxTenths || MinGradient > MaxGradient)
            errors.Add($"{p}: gradient window must satisfy -{Gradient.MaxTenths} <= minGradient <= maxGradient <= {Gradient.MaxTenths}");
        if (MinTurn is < 0 or > 1000) errors.Add($"{p}.minTurn must be within 0..1000");
        if (FlowAffinity is < 0 or > 1000 || TechAffinity is < 0 or > 1000) errors.Add($"{p}: affinities must be within 0..1000");
        if (FunWeight is < 1 or > 10) errors.Add($"{p}.funWeight must be within 1..10");
        if (WorkMinutes is < 1 or > 100_000) errors.Add($"{p}.workMinutes must be within 1..100000");
        if (Wood is < 0 or > 1000) errors.Add($"{p}.wood must be within 0..1000");
        return errors;
    }
}

/// <summary>A feature the player placed on a trail: its type and where it starts. Everything else is derived.</summary>
public sealed class TrailFeature
{
    public int Id { get; set; }
    public string TypeId { get; set; } = "";

    /// <summary>Distance along the trail where the feature starts.</summary>
    public long DistanceCm { get; set; }

    /// <summary>False while the feature is only planned (a crew job builds it); only built features count.</summary>
    public bool Built { get; set; } = true;

    /// <summary>Wear (see <see cref="TrailCondition"/>): millionths, 1,000,000 = perfect. Riders passing it wear it down.</summary>
    public int Condition { get; set; } = TrailCondition.Perfect;

    /// <summary>The "needs repair" warning was raised for this wear; cleared by a repair.</summary>
    public bool Warned { get; set; }

    /// <summary>Riders who crashed on it.</summary>
    public int Crashes { get; set; }
}

/// <summary>A placed feature resolved against the catalog: the stretch of trail it covers.</summary>
public readonly record struct PlacedFeature(TrailFeature Feature, TrailFeatureType Type, long StartCm, long EndCm)
{
    public bool Covers(long distanceCm) => distanceCm >= StartCm && distanceCm < EndCm;
}

public static class TrailFeatures
{
    public static TrailFeatureType? FindType(IReadOnlyList<TrailFeatureType> types, string typeId) =>
        types.FirstOrDefault(t => t.Id == typeId);

    /// <summary>The way's features that resolve to a known type, in distance order.</summary>
    public static List<PlacedFeature> Resolve(Way way, IReadOnlyList<TrailFeatureType> types)
    {
        var placed = new List<PlacedFeature>(way.Features.Count);
        foreach (var feature in way.Features)
            if (FindType(types, feature.TypeId) is { } type)
                placed.Add(new PlacedFeature(feature, type, feature.DistanceCm, feature.DistanceCm + type.LengthCm));
        return placed;
    }
}
