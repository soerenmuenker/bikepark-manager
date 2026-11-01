using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Trails;

/// <summary>A medium rock or rock face on a trail's line: only a wooden feature gets across it.</summary>
public sealed record RockCrossing(RockFormation Kind, long FromCm, long ToCm, long DropCm)
{
    public long SpanCm => ToCm - FromCm;
}

/// <summary>A wooden feature over rock, chosen while drawing the trail: its type and the stretch it covers.</summary>
/// <remarks><paramref name="RampCm"/>: a flat drop's level deck, from where it leaves the hillside to its lip.</remarks>
public sealed record RockFeaturePlacement(string TypeId, long StartCm, long LengthCm, long RampCm = 0);

/// <summary>Which feature types over rock this park can use (a debug instant build: all).</summary>
public sealed record RockFeatureChoice(IReadOnlyList<TrailFeatureType> Types, Func<TrailFeatureType, bool> Unlocked);

/// <summary>
/// The rules for wooden features over rock. Pure and read-only: the build command and the trail tool use the same checks. A flat
/// drop is a level deck resting on top of the rock, reaching back until it meets the hillside, then a gap and a landing below the
/// rock; a wooden roller covers the rock.
/// </summary>
public static class RockFeatures
{
    private const long MarginCm = 100;

    /// <summary>The gap may be this much wider than the rock limit: ramp and landing stand clear of the rock.</summary>
    private const long GapSlackCm = 400;

    private const long MinFlatDropCm = 50;

    private const long DeckHalfWidthCm = 90, DeckClearanceCm = 30;

    /// <summary>The rock meshes' size at scale 1 m: how far they reach sideways and up from where they stand, in cm per 1000 of scale.</summary>
    private const long RockReachPer1000 = 60, RockTopPer1000 = 95;

    public static List<RockCrossing> Crossings(WayGeometry geometry) =>
        geometry.Kind != WayKind.Trail ? [] : geometry.Rocks.Where(r => r.Kind >= RockFormation.MediumRock)
            .Select(r => new RockCrossing(r.Kind, geometry.Distances[r.From], geometry.Distances[r.To], Math.Max(0, (long)geometry.Ys[r.From] - geometry.Ys[r.To]))).ToList();

    /// <summary>The big rocks standing on the trail between two distances, what a flat drop rests on.</summary>
    public static List<ScatterInstance> RocksOn(TerrainGrid grid, WayGeometry geometry, long fromCm, long toCm)
    {
        var samples = Enumerable.Range(0, geometry.SampleCount).Where(i => geometry.Distances[i] >= fromCm && geometry.Distances[i] <= toCm).ToList();
        if (samples.Count == 0) return [];
        const int Widen = 20; // meters: a rock's cell can lie this far from the line
        var found = new List<ScatterInstance>();
        TerrainScatter.Collect(grid, samples.Min(i => geometry.Xs[i]) / 100 - Widen, samples.Min(i => geometry.Zs[i]) / 100 - Widen,
            samples.Max(i => geometry.Xs[i]) / 100 + Widen, samples.Max(i => geometry.Zs[i]) / 100 + Widen, found);
        return found.Where(rock => rock.Kind is ScatterKind.Boulder or ScatterKind.Face && samples.Any(i =>
        {
            long dx = geometry.Xs[i] - rock.XCm, dz = geometry.Zs[i] - rock.ZCm, reach = DeckHalfWidthCm + rock.ScalePermille * RockReachPer1000 / 1000;
            return dx * dx + dz * dz <= reach * reach;
        })).ToList();
    }

    /// <summary>How high a flat drop's deck has to be between the rock's start and the lip: over the rocks and the ground there.</summary>
    public static long RockTopCm(TerrainGrid grid, WayGeometry geometry, long fromCm, long lipCm)
    {
        long top = geometry.HeightAt(fromCm) + DeckClearanceCm;
        for (long d = fromCm; d <= lipCm; d += 100) top = Math.Max(top, geometry.HeightAt(d) + DeckClearanceCm);
        foreach (var rock in RocksOn(grid, geometry, fromCm, lipCm))
            top = Math.Max(top, rock.YCm + rock.ScalePermille * RockTopPer1000 / 1000);
        return top;
    }

    /// <summary>Where a level deck with its lip here meets the hillside, going back up the trail; null if the ground is too far off.</summary>
    public static long? DeckStart(TerrainGrid grid, WayGeometry geometry, TrailFeatureType type, RockCrossing crossing, long lipCm)
    {
        long top = RockTopCm(grid, geometry, crossing.FromCm, lipCm);
        long limit = Math.Max(0, crossing.FromCm - type.RampMeters * 100L);
        for (long d = crossing.FromCm; d >= limit; d -= 50)
            if (geometry.HeightAt(d) >= top) return d;
        return null;
    }

    /// <summary>A flat drop with this lip on the rock and this landing start. If no ground meets the deck, it starts at the limit (and <see cref="CannotPlace"/> says why).</summary>
    public static RockFeaturePlacement FlatDropAt(TerrainGrid grid, WayGeometry geometry, TrailFeatureType type, RockCrossing crossing, long lipCm, long landingCm)
    {
        lipCm = Math.Clamp(lipCm, crossing.FromCm, crossing.ToCm);
        long start = DeckStart(grid, geometry, type, crossing, lipCm) ?? crossing.FromCm - type.RampMeters * 100L;
        return new RockFeaturePlacement(type.Id, start, landingCm + type.LandingDeckMeters * 100L - start, lipCm - start);
    }

    /// <summary>Where the feature goes unless the player says otherwise: tight around the rock (a flat drop's deck over all of it).</summary>
    public static RockFeaturePlacement DefaultPlacement(TerrainGrid grid, WayGeometry geometry, TrailFeatureType type, RockCrossing crossing) =>
        IsFlatDrop(type)
            ? FlatDropAt(grid, geometry, type, crossing, crossing.ToCm, crossing.ToCm + MarginCm)
            : new RockFeaturePlacement(type.Id, crossing.FromCm - MarginCm, crossing.SpanCm + 2 * MarginCm);

    public static bool IsFlatDrop(TrailFeatureType type) => type.Kind == FeatureKind.FlatDrop;

    /// <summary>Why this placement can't be, or null.</summary>
    public static string? CannotPlace(TerrainGrid grid, WayGeometry geometry, TrailRules rules, TrailFeatureType type, RockFeaturePlacement placement)
    {
        string name = type.Name.ToLowerInvariant();
        if (type.CrossesRock == RockFormation.None) return $"A {name} doesn't go over rock.";
        long start = placement.StartCm, end = start + placement.LengthCm;
        bool drop = IsFlatDrop(type);
        long open = start + placement.RampCm, close = end - type.LandingDeckMeters * 100L;
        var crossings = Crossings(geometry);
        if (drop)
        {
            if (crossings.FirstOrDefault(c => c.FromCm <= open && open <= c.ToCm) is null) return "The deck must end on top of the rock.";
        }
        if (start < rules.FeatureStartMarginMeters * 100L) return $"Too close to the trail start for a {name}: leave {rules.FeatureStartMarginMeters} m.";
        if (end > geometry.LengthCm - rules.FeatureEndMarginMeters * 100L) return $"Too close to the trail end for a {name}: leave {rules.FeatureEndMarginMeters} m.";

        if (close <= open) return "The landing must lie below the deck.";
        foreach (var crossing in crossings)
        {
            if (crossing.ToCm <= start || crossing.FromCm >= end) continue;
            if (crossing.Kind > type.CrossesRock) return $"A {name} can't cross a rock face.";
            if (!drop && crossing.FromCm < open) return $"The {name} must start above the rock.";
            if (crossing.ToCm > close) return drop ? "The landing must start below the rock." : $"The {name} must end below the rock.";
            if (crossing.SpanCm > type.MaxSpanMeters * 100L) return $"The rock is {crossing.SpanCm / 100} m wide: a {name} crosses at most {type.MaxSpanMeters} m.";
        }
        if (close - open > type.MaxSpanMeters * 100L + GapSlackCm) return $"Too long for a {name}: at most {type.MaxSpanMeters + GapSlackCm / 100} m over the rock.";

        long fall = geometry.HeightAt(drop ? start : open) - geometry.HeightAt(close);
        if (fall > type.MaxRockDropMeters * 100L) return $"{fall / 100.0:0.0} m down: a {name} takes at most {type.MaxRockDropMeters} m.";
        if (drop)
        {
            if (fall < MinFlatDropCm) return $"A {name} must go down: at least {MinFlatDropCm / 100.0:0.0} m here.";
            if (DeckStart(grid, geometry, type, crossings.First(c => c.FromCm <= open && open <= c.ToCm), open) != start)
                return $"The rock stands too high: the deck doesn't meet the hillside within {type.RampMeters} m.";
        }
        else if (Gradient.FromPermille((int)(-Math.Max(0, fall) * 1000 / Math.Max(1, close - open))) < type.MinGradient)
            return $"Too steep for a {name} (limit {Gradient.Format(type.MinGradient)}).";
        return null;
    }

    /// <summary>The types that fit a crossing with their default placement: the reason is null if it does, else why not.</summary>
    public static List<(TrailFeatureType Type, RockFeaturePlacement Placement, string? Reason)> Options(TerrainGrid grid, WayGeometry geometry, TrailRules rules, RockFeatureChoice choice, RockCrossing crossing)
    {
        var options = new List<(TrailFeatureType, RockFeaturePlacement, string?)>();
        foreach (var type in choice.Types.Where(t => t.CrossesRock != RockFormation.None))
        {
            var placement = DefaultPlacement(grid, geometry, type, crossing);
            string? reason = CannotPlace(grid, geometry, rules, type, placement);
            if (reason is null && !choice.Unlocked(type)) reason = $"From park level {type.RequiredLevel}.";
            options.Add((type, placement, reason));
        }
        return options;
    }

    /// <summary>Why these features don't get the trail across its rock, or null: each is valid, none overlap, every crossing is covered.</summary>
    public static string? CannotCross(TerrainGrid grid, WayGeometry geometry, TrailRules rules, RockFeatureChoice choice, IReadOnlyList<RockFeaturePlacement>? placements)
    {
        var placed = (placements ?? []).OrderBy(p => p.StartCm).ToList();
        for (int i = 0; i < placed.Count; i++)
        {
            if (choice.Types.FirstOrDefault(t => t.Id == placed[i].TypeId) is not { } type) return $"Unknown feature '{placed[i].TypeId}'.";
            if (!choice.Unlocked(type)) return $"The {type.Name.ToLowerInvariant()} needs park level {type.RequiredLevel}.";
            if (placed[i].LengthCm <= 0) return "A feature needs a length.";
            if (CannotPlace(grid, geometry, rules, type, placed[i]) is { } reason) return reason;
            if (i > 0 && placed[i - 1].StartCm + placed[i - 1].LengthCm + rules.FeatureGapMeters * 100L > placed[i].StartCm)
                return $"Two features over rock are too close: keep {rules.FeatureGapMeters} m apart.";
        }
        foreach (var crossing in Crossings(geometry))
            if (!placed.Any(p => p.StartCm <= crossing.FromCm && p.StartCm + p.LengthCm >= crossing.ToCm))
                return $"Rock at {crossing.FromCm / 100} m: choose a wooden feature to cross it.";
        return null;
    }
}
