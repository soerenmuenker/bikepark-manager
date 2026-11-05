namespace Bikepark.Sim.Terrain;

/// <summary>What a rock you see means for a way. Read from the rock itself, never from the ground under the line.</summary>
public enum RockClass : byte
{
    /// <summary>A stone, at most knee high: the crew clears it; packed close together they make a stone field (a black trail).</summary>
    Stone = 0,

    /// <summary>A loose rock taller than any stone (<see cref="RockClasses.StoneMaxHeightCm"/>): a trail takes a flat drop over it, a path goes around.</summary>
    Boulder = 1,

    /// <summary>A piece of rock face (ledged shapes): every way goes around.</summary>
    Face = 2,
}

public static class RockClasses
{
    /// <summary>No stone is higher than this; every boulder and face is far higher.</summary>
    public const int StoneMaxHeightCm = 60;

    public static RockClass Of(ScatterInstance rock) => rock.Shape switch
    {
        >= RockShape.Ledged0 => RockClass.Face,
        _ => RockShapes.HeightCm(rock) > StoneMaxHeightCm ? RockClass.Boulder : RockClass.Stone,
    };

    /// <summary>The formation a way records for a rock of this class (what <see cref="Trails.WayRock"/> stores).</summary>
    public static RockFormation Formation(RockClass kind) => kind switch
    {
        RockClass.Face => RockFormation.RockFace,
        RockClass.Boulder => RockFormation.MediumRock,
        _ => RockFormation.SparseStones,
    };
}
