namespace Bikepark.Sim.Terrain;

/// <summary>The kit every rock is drawn from, whatever its size. Ledged shapes are pieces of rock face.</summary>
public enum RockShape : byte
{
    Slab = 0,
    Block = 1,
    Wedge = 2,
    Fin = 3,
    Split = 4,
    Ledged0 = 5,
    Ledged1 = 6,
    Ledged2 = 7,
}

/// <summary>How far each shape reaches: the meshes are fitted to these numbers, so the rules and the view agree.</summary>
public static class RockShapes
{
    public const int LooseCount = 5, LedgedCount = 3;

    /// <summary>Radius and height above the ground, in cm at scale 1.</summary>
    public static (int RadiusCm, int HeightCm) Size(RockShape shape) => shape switch
    {
        RockShape.Slab => (120, 50),
        RockShape.Block => (85, 90),
        RockShape.Wedge => (100, 85),
        RockShape.Fin => (65, 125),
        RockShape.Split => (110, 70),
        _ => (125, 105),
    };

    public static long RadiusCm(ScatterInstance rock) => (long)rock.ScalePermille * Size(rock.Shape).RadiusCm / 1000;

    public static long HeightCm(ScatterInstance rock) => (long)rock.ScalePermille * Size(rock.Shape).HeightCm / 1000;

    /// <summary>The highest scale a loose shape may have and still be a stone (<see cref="RockClasses.StoneMaxHeightCm"/>).</summary>
    public static int StoneMaxScale(RockShape shape) => RockClasses.StoneMaxHeightCm * 1000 / Size(shape).HeightCm;
}
