using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Lifts;

/// <summary>A planned lift: its two pads, line numbers and issues.</summary>
public sealed class LiftPlan
{
    public LiftType? Type { get; init; }
    public TerrainPad? ValleyPad { get; init; }
    public TerrainPad? MountainPad { get; init; }
    public long HorizontalCm { get; init; }
    public int RiseCm { get; init; }
    public long LengthCm { get; init; }
    public int RideSeconds { get; init; }
    public required List<WayIssue> Issues { get; init; }

    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
    public string? FirstError => Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
}

/// <summary>A planned parking lot.</summary>
public sealed class ParkingPlan
{
    public TerrainPad? Pad { get; init; }

    /// <summary>The lift whose valley station it would serve (0 = none in reach).</summary>
    public int LiftId { get; init; }

    public required List<WayIssue> Issues { get; init; }

    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
    public string? FirstError => Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
}

/// <summary>
/// Validates lifts, parking lots and their pads against the terrain, the network and the existing structures. Pure
/// and read-only: build commands and the in-game preview both use it, so they can never disagree.
/// </summary>
public static class StructurePlanner
{
    public const int MinParkingSpaces = 10;
    public const int MaxParkingSpaces = 2_000;

    // ---------------------------------------------------------------- lifts

    public static LiftPlan PlanLift(
        TerrainGrid grid, WayNetwork network, WorldState state, string typeId, PointCm valley, PointCm mountain,
        int plateauLengthMeters = 0, int plateauWidthMeters = 0, string? operatorId = null, int initialTier = 0, bool scenario = false)
    {
        var issues = new List<WayIssue>();
        var type = LiftNetwork.FindType(state, typeId);
        if (type is null)
            return new LiftPlan { Issues = [Error("unknownType", $"Unknown lift type '{typeId}'.")] };
        if (operatorId is not null)
        {
            var op = LiftNetwork.FindOperator(state, operatorId);
            if (op is null) issues.Add(Error("unknownOperator", $"Unknown lift company '{operatorId}'."));
            else if (initialTier < 0 || initialTier >= op.BikeAccessTiers.Count) issues.Add(Error("badTier", "No such bike access tier."));
        }
        if (!grid.Contains(valley.X, valley.Z) || !grid.Contains(mountain.X, mountain.Z))
            return new LiftPlan { Type = type, Issues = [.. issues, Error("outsideMap", "Both stations must be on the map.")] };

        var (dirX, dirZ) = TerrainPad.Direction(mountain.X - valley.X, mountain.Z - valley.Z);
        var rules = state.LiftRules;
        var valleyPad = MakePad(grid, rules, valley, type.StationLengthMeters, type.StationWidthMeters, dirX, dirZ);
        var mountainPad = MakePad(grid, rules, mountain,
            plateauLengthMeters > 0 ? plateauLengthMeters : type.PlateauLengthMeters,
            plateauWidthMeters > 0 ? plateauWidthMeters : type.PlateauWidthMeters, dirX, dirZ);

        var (horizontal, rise, length) = LiftMath.Line(valleyPad, mountainPad);
        if (horizontal < type.MinLengthMeters * 100L)
            issues.Add(Error("tooShort", $"Too short: at least {type.MinLengthMeters} m."));
        if (horizontal > type.MaxLengthMeters * 100L)
            issues.Add(Error("tooLong", $"Too long: at most {type.MaxLengthMeters} m."));
        if (rise <= 0)
            issues.Add(Error("notUphill", "The mountain station must be higher than the valley station."));
        else if (horizontal > 0)
        {
            int gradient = Gradient.FromPermille((int)Math.Min(int.MaxValue, rise * 1000L / horizontal));
            if (gradient > type.MaxGradient)
                issues.Add(Error("tooSteep", $"Too steep for a {type.Name}: {Gradient.Format(gradient)} (limit {Gradient.Format(type.MaxGradient)})."));
        }

        CheckPad(grid, network, state, rules, valleyPad, "Valley station", issues, checkLand: !scenario);
        CheckPad(grid, network, state, rules, mountainPad, "Plateau", issues, checkLand: !scenario);
        if (PadsOverlap(valleyPad, mountainPad))
            issues.Add(Error("stationsOverlap", "The stations overlap."));
        // A T-bar pulls its riders up a track on the ground: ways crossing it are collision spots.
        if (type.Kind == LiftKind.TBar)
            foreach (var way in network.Ways)
                if (network.TryGetGeometry(way.Id, out var geometry) && Crossings.FindOnTrack(geometry, valleyPad, mountainPad).Count > 0)
                    issues.Add(new WayIssue(IssueSeverity.Warning, "crossing", $"The track crosses {way.Label}: riders can collide there."));

        return new LiftPlan
        {
            Type = type,
            ValleyPad = valleyPad,
            MountainPad = mountainPad,
            HorizontalCm = horizontal,
            RiseCm = rise,
            LengthCm = length,
            RideSeconds = LiftMath.RideSeconds(type, length),
            Issues = issues,
        };
    }

    // ---------------------------------------------------------------- parking

    /// <summary>A parking lot centred at <paramref name="center"/>, its length pointing towards <paramref name="toward"/>.</summary>
    public static ParkingPlan PlanParking(TerrainGrid grid, WayNetwork network, WorldState state, PointCm center, PointCm toward, int spaces,
        bool scenario = false)
    {
        var issues = new List<WayIssue>();
        var rules = state.LiftRules;
        if (spaces is < MinParkingSpaces or > MaxParkingSpaces)
            return new ParkingPlan { Issues = [Error("badSpaces", $"A parking lot has {MinParkingSpaces}..{MaxParkingSpaces} spaces.")] };
        if (!grid.Contains(center.X, center.Z))
            return new ParkingPlan { Issues = [Error("outsideMap", "The parking lot must be on the map.")] };

        var (lengthM, widthM) = ParkingSize(rules, spaces);
        var (dirX, dirZ) = TerrainPad.Direction(toward.X - center.X, toward.Z - center.Z);
        var pad = MakePad(grid, rules, center, lengthM, widthM, dirX, dirZ);
        CheckPad(grid, network, state, rules, pad, "Parking lot", issues, checkLand: !scenario);

        // It serves the nearest valley station within reach.
        int liftId = 0;
        long best = long.MaxValue;
        foreach (var lift in state.Lifts)
        {
            var station = LiftNetwork.Pad(state, lift.Valley.TerrainEditId);
            if (station is null) continue;
            long d = pad.Corners().Append(new PointCm(pad.CenterX, pad.CenterZ)).Min(p => station.DistanceOutside(p.X, p.Z));
            if (d < best) { best = d; liftId = lift.Id; }
        }
        if (liftId == 0 || best > rules.StationReachMeters * 100L)
        {
            liftId = 0;
            issues.Add(Error("noStation", $"Place it within {rules.StationReachMeters} m of a valley station."));
        }

        return new ParkingPlan { Pad = pad, LiftId = liftId, Issues = issues };
    }

    /// <summary>Side of a gravel platform (square).</summary>
    public const int PlatformMeters = 12;

    /// <summary>A square gravel platform centred at <paramref name="center"/>, turned towards <paramref name="toward"/>; on the park's land.</summary>
    public static ParkingPlan PlanPlatform(TerrainGrid grid, WayNetwork network, WorldState state, PointCm center, PointCm toward)
    {
        var issues = new List<WayIssue>();
        if (!grid.Contains(center.X, center.Z))
            return new ParkingPlan { Issues = [Error("outsideMap", "The platform must be on the map.")] };
        var (dirX, dirZ) = TerrainPad.Direction(toward.X - center.X, toward.Z - center.Z);
        var pad = MakePad(grid, state.LiftRules, center, PlatformMeters, PlatformMeters, dirX, dirZ);
        CheckPad(grid, network, state, state.LiftRules, pad, "Platform", issues);
        return new ParkingPlan { Pad = pad, Issues = issues };
    }

    /// <summary>Length and width in meters of a lot with that many spaces.</summary>
    public static (int LengthMeters, int WidthMeters) ParkingSize(LiftRules rules, int spaces) =>
        (Math.Max(10, (spaces * rules.ParkingSquareMetersPerSpace + rules.ParkingWidthMeters - 1) / rules.ParkingWidthMeters), rules.ParkingWidthMeters);

    // ---------------------------------------------------------------- pads

    /// <summary>A pad at the average natural height under it (cut and fill roughly balance).</summary>
    public static TerrainPad MakePad(TerrainGrid grid, LiftRules rules, PointCm center, int lengthMeters, int widthMeters, int dirX, int dirZ)
    {
        var probe = new TerrainPad(center.X, center.Z, lengthMeters * 50, widthMeters * 50, dirX, dirZ, 0, rules.EmbankmentGradient);
        long sum = 0, count = 0;
        ForEachSampleInside(grid, probe, (x, z) => { sum += grid.HeightAtSample(x, z); count++; });
        int target = count == 0 ? grid.HeightAt(center.X, center.Z) : (int)(sum / count);
        return probe with { TargetHeightCm = target };
    }

    private static void CheckPad(TerrainGrid grid, WayNetwork network, WorldState state, LiftRules rules, TerrainPad pad, string what,
        List<WayIssue> issues, bool checkLand = true)
    {
        if (pad.Corners().Any(c => !grid.Contains(c.X, c.Z)))
        {
            issues.Add(Error("outsideMap", $"{what} must lie on the map."));
            return;
        }
        if (checkLand && (!Land.LandMath.IsOwned(state, pad.CenterX, pad.CenterZ) || pad.Corners().Any(c => !Land.LandMath.IsOwned(state, c.X, c.Z))))
            issues.Add(Error("notYourLand", $"{what}: not your land. Buy the parcel first (Land menu)."));

        int worst = 0;
        ForEachSampleInside(grid, pad, (x, z) => worst = Math.Max(worst, Math.Abs(grid.HeightAtSample(x, z) - pad.TargetHeightCm)));
        if (worst > rules.MaxPadCutFillCm)
            issues.Add(Error("tooUneven", $"{what}: ground too uneven ({worst / 100} m cut or fill, limit {rules.MaxPadCutFillCm / 100} m)."));

        if (state.TerrainEdits.Any(e => PadsOverlap(e.Pad, pad)))
            issues.Add(Error("overlapsStructure", $"{what} overlaps another structure."));

        foreach (var way in network.Ways)
        {
            var geometry = network.Geometry(way.Id);
            long half = (way.Kind == WayKind.AccessPath ? state.TrailRules.PathCorridorCm : state.TrailRules.TrailCorridorCm) / 2;
            for (int i = 0; i < geometry.SampleCount; i++)
            {
                if (pad.DistanceOutside(geometry.Xs[i], geometry.Zs[i]) > half) continue;
                issues.Add(Error("overlapsWay", $"{what} overlaps {way.Label}."));
                break;
            }
        }
    }

    /// <summary>Calls <paramref name="visit"/> for every 1 m terrain sample inside the pad's flat area.</summary>
    private static void ForEachSampleInside(TerrainGrid grid, TerrainPad pad, Action<int, int> visit)
    {
        var (x0, z0, x1, z1) = TerrainEditor.Bounds(pad, grid.SizeMeters, 0);
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                if (pad.Contains(x * 100L, z * 100L))
                    visit(x, z);
    }

    /// <summary>Separating-axis test for two oriented rectangles (touching edges count as overlap).</summary>
    public static bool PadsOverlap(TerrainPad a, TerrainPad b)
    {
        var ca = a.Corners();
        var cb = b.Corners();
        foreach (var (ax, az) in new[] { (a.DirX, a.DirZ), (-a.DirZ, a.DirX), (b.DirX, b.DirZ), (-b.DirZ, b.DirX) })
        {
            long minA = long.MaxValue, maxA = long.MinValue, minB = long.MaxValue, maxB = long.MinValue;
            foreach (var p in ca) { long d = (long)p.X * ax + (long)p.Z * az; minA = Math.Min(minA, d); maxA = Math.Max(maxA, d); }
            foreach (var p in cb) { long d = (long)p.X * ax + (long)p.Z * az; minB = Math.Min(minB, d); maxB = Math.Max(maxB, d); }
            if (maxA < minB || maxB < minA) return false;
        }
        return true;
    }

    private static WayIssue Error(string code, string message) => new(IssueSeverity.Error, code, message);
}
