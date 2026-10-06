using System.Text.Json.Serialization;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Terrain;

/// <summary>
/// A flattened rectangle (station platform, plateau, parking lot) with embankments down or up to the natural
/// ground. Oriented by an integer direction vector of length <see cref="DirScale"/> along its length axis.
/// All distances in cm; the embankment is limited to <see cref="EmbankmentGradient"/> (tenths of a gradient point).
/// </summary>
public sealed record TerrainPad(
    int CenterX,
    int CenterZ,
    int HalfLengthCm,
    int HalfWidthCm,
    int DirX,
    int DirZ,
    int TargetHeightCm,
    int EmbankmentGradient)
{
    public const int DirScale = 1024;

    /// <summary>Direction (dx, dz) scaled to length <see cref="DirScale"/>; (DirScale, 0) for a zero vector.</summary>
    public static (int X, int Z) Direction(long dx, long dz)
    {
        long length = FixedMath.ISqrt(dx * dx + dz * dz);
        if (length == 0) return (DirScale, 0);
        return ((int)(dx * DirScale / length), (int)(dz * DirScale / length));
    }

    /// <summary>Position relative to the pad: U along its length, V across, in cm.</summary>
    public (long U, long V) Local(long xCm, long zCm)
    {
        long dx = xCm - CenterX, dz = zCm - CenterZ;
        return ((dx * DirX + dz * DirZ) / DirScale, (-dx * DirZ + dz * DirX) / DirScale);
    }

    /// <summary>Horizontal distance from the flat area (0 inside).</summary>
    public long DistanceOutside(long xCm, long zCm)
    {
        var (u, v) = Local(xCm, zCm);
        long e = Math.Max(0, Math.Abs(u) - HalfLengthCm);
        long f = Math.Max(0, Math.Abs(v) - HalfWidthCm);
        return e == 0 ? f : f == 0 ? e : FixedMath.ISqrt(e * e + f * f);
    }

    public bool Contains(long xCm, long zCm) => DistanceOutside(xCm, zCm) == 0;

    /// <summary>World position of a pad-local point.</summary>
    public PointCm ToWorld(long u, long v) =>
        new((int)(CenterX + (u * DirX - v * DirZ) / DirScale), (int)(CenterZ + (u * DirZ + v * DirX) / DirScale));

    /// <summary>The four corners, counter-clockwise from (-L, -W).</summary>
    public PointCm[] Corners() =>
    [
        ToWorld(-HalfLengthCm, -HalfWidthCm), ToWorld(HalfLengthCm, -HalfWidthCm),
        ToWorld(HalfLengthCm, HalfWidthCm), ToWorld(-HalfLengthCm, HalfWidthCm),
    ];

    /// <summary>Closest point of the flat area to (x, z) (the point itself if inside).</summary>
    public PointCm ClosestPoint(long xCm, long zCm)
    {
        var (u, v) = Local(xCm, zCm);
        return ToWorld(Math.Clamp(u, -HalfLengthCm, HalfLengthCm), Math.Clamp(v, -HalfWidthCm, HalfWidthCm));
    }

    /// <summary>Closest point on the edge of the flat area (for points inside: the nearest edge).</summary>
    public PointCm ClosestEdgePoint(long xCm, long zCm)
    {
        var (u, v) = Local(xCm, zCm);
        if (Math.Abs(u) > HalfLengthCm || Math.Abs(v) > HalfWidthCm)
            return ClosestPoint(xCm, zCm);
        long toLengthEdge = HalfLengthCm - Math.Abs(u), toWidthEdge = HalfWidthCm - Math.Abs(v);
        return toLengthEdge < toWidthEdge
            ? ToWorld(u >= 0 ? HalfLengthCm : -HalfLengthCm, v)
            : ToWorld(u, v >= 0 ? HalfWidthCm : -HalfWidthCm);
    }

    /// <summary>Grade in permille that the embankment may not exceed.</summary>
    [JsonIgnore]
    public int EmbankmentPermille => Math.Max(1, Gradient.ToPermille(EmbankmentGradient));
}

/// <summary>A stored terrain change. <see cref="OwnerId"/> is the structure that made it (lift, parking lot).</summary>
public sealed record TerrainEdit(int Id, int OwnerId, TerrainPad Pad);
