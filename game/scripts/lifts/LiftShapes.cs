using Bikepark.Sim.Terrain;
using Godot;

namespace Bikepark.Game.Lifts;

/// <summary>Where lift parts are in the world: rope line, cabin positions, queue slots. Shared by the lift and rider views.</summary>
internal static class LiftShapes
{
    /// <summary>Rope height above the station platforms.</summary>
    public const float CableHeight = 9f;

    /// <summary>Half the distance between the up and down rope.</summary>
    public const float RopeOffset = 3f;

    /// <summary>How far a cabin hangs below the rope.</summary>
    public const float CabinDrop = 2.6f;

    public static Vector3 Center(TerrainPad pad, float lift = 0f) =>
        new(pad.CenterX / 100f, pad.TargetHeightCm / 100f + lift, pad.CenterZ / 100f);

    /// <summary>Horizontal unit vector to the right of the pad's length axis.</summary>
    public static Vector3 Right(TerrainPad pad) => new Vector3(-pad.DirZ, 0, pad.DirX).Normalized();

    public static Vector3 Forward(TerrainPad pad) => new Vector3(pad.DirX, 0, pad.DirZ).Normalized();

    /// <summary>A point on the up (t = 0 valley .. 1 top) or down rope, at rope height.</summary>
    public static Vector3 OnRope(TerrainPad valley, TerrainPad mountain, float t, bool up)
    {
        var right = Right(valley) * (up ? RopeOffset : -RopeOffset);
        return Center(valley, CableHeight).Lerp(Center(mountain, CableHeight), Mathf.Clamp(t, 0f, 1f)) + right;
    }

    /// <summary>Where the <paramref name="index"/>-th rider in the queue stands: rows of four behind the valley station.</summary>
    public static Vector3 QueueSlot(TerrainPad valley, int index)
    {
        var back = -Forward(valley);
        var right = Right(valley);
        float along = valley.HalfLengthCm / 100f - 4f - index / 4 * 1.3f;
        float across = (index % 4 - 1.5f) * 1.2f + RopeOffset;
        return Center(valley, 0.05f) - back * along + right * across;
    }
}
