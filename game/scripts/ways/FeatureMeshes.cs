using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// Low-poly meshes for trail features, swept along the trail: each kind is a cross-section (lateral offset, height above
/// the ground, in meters) that changes along the feature. Berms and wall-rides sit on the outside of the bend.
/// </summary>
internal static class FeatureMeshes
{
    // Given in sRGB, stored linear (the shaders use vertex colors as they are).
    public static readonly Color DirtColor = new Color(0.52f, 0.36f, 0.22f).SrgbToLinear();
    public static readonly Color WoodColor = new Color(0.74f, 0.56f, 0.33f).SrgbToLinear();

    public static Color BaseColor(FeatureMaterial material) => material == FeatureMaterial.Wood ? WoodColor : DirtColor;

    /// <summary>Lit material for built features (feature.gdshader: both sides, drawn over the trail ribbon).</summary>
    public static ShaderMaterial CreateMaterial() => new() { Shader = GD.Load<Shader>("res://shaders/feature.gdshader") };

    /// <summary>Planned features: light blue, see-through; turns towards the material color as the crew builds it.</summary>
    public static readonly Color PlannedColor = new(0.55f, 0.85f, 1f, 0.45f);

    public static Color ProgressColor(FeatureMaterial material, float progress) =>
        PlannedColor.Lerp(BaseColor(material), progress * 0.8f) with { A = 0.45f + 0.45f * progress };

    /// <summary>See-through material for planned features (depth-tested, unlike the ghost).</summary>
    public static StandardMaterial3D CreatePlannedMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    /// <summary>See-through unshaded material for the placement ghost.</summary>
    public static StandardMaterial3D CreateGhostMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true,
    };

    /// <summary>The feature's mesh in world coordinates, from <paramref name="startCm"/> along the trail.</summary>
    public static ArrayMesh Build(TerrainGrid grid, WayGeometry g, TrailFeatureType type, long startCm, Color color)
    {
        long lengthCm = type.LengthCm;
        int steps = Math.Max(6, type.LengthMeters * 3);
        float outside = OutsideSign(g, startCm, startCm + lengthCm);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(color);

        Vector3[]? previous = null;
        Vector3[] first = [], last = [];
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            long d = startCm + (long)(lengthCm * t);
            var p = g.PositionAt(d);
            var ahead = g.PositionAt(Math.Min(g.LengthCm, d + 50));
            var behind = g.PositionAt(Math.Max(0, d - 50));
            var dir = new Vector2(ahead.X - behind.X, ahead.Z - behind.Z).Normalized();
            var right = new Vector2(-dir.Y, dir.X);
            var center = new Vector2(p.X / 100f, p.Z / 100f);

            var profile = Profile(type.Kind, t, outside);
            var section = new Vector3[profile.Length];
            for (int k = 0; k < profile.Length; k++)
            {
                var (u, h) = profile[k];
                var xz = center + right * u;
                float ground = grid.HeightAt((long)MathF.Round(xz.X * 100), (long)MathF.Round(xz.Y * 100)) / 100f;
                section[k] = new Vector3(xz.X, ground + 0.05f + h, xz.Y);
            }

            if (previous is not null)
                for (int k = 0; k < section.Length - 1; k++)
                    Quad(st, previous[k], previous[k + 1], section[k + 1], section[k]);
            if (i == 0) first = section;
            last = section;
            previous = section;
        }
        Cap(st, first);
        Cap(st, last);

        st.GenerateNormals();
        return st.Commit();
    }

    /// <summary>
    /// Cross-section at <paramref name="t"/> (0..1 along the feature), from one side to the other: (lateral offset to the
    /// right of travel, height). The ends of each section are on the ground.
    /// </summary>
    private static (float U, float H)[] Profile(FeatureKind kind, float t, float outside)
    {
        float ease = Mathf.Clamp(Math.Min(t, 1 - t) * 5f, 0f, 1f); // ramps in and out over the first/last 20 %
        switch (kind)
        {
            case FeatureKind.Berm:
            {
                float h = 1.1f * ease;
                return Side(outside, [(-0.9f, 0f), (0.4f, 0.12f * ease), (1.3f, h), (1.8f, h), (2.3f, 0f)]);
            }
            case FeatureKind.Rollers:
            {
                float s = Mathf.Sin(3 * Mathf.Pi * t);
                return Hump(0.5f * s * s, 1.0f);
            }
            case FeatureKind.Table:
            {
                float h = t < 0.3f ? 1.2f * t / 0.3f : t < 0.75f ? 1.2f : 1.2f * (1 - t) / 0.25f;
                return Hump(h, 1.3f);
            }
            case FeatureKind.Double:
            {
                float h = t < 0.35f ? 1.4f * Mathf.Pow(t / 0.35f, 1.5f)
                    : t < 0.4f ? 1.4f * (0.4f - t) / 0.05f
                    : t < 0.55f ? 0f
                    : t < 0.6f ? 1.4f * (t - 0.55f) / 0.05f
                    : 1.4f * (1 - t) / 0.4f;
                return Hump(h, 1.3f);
            }
            case FeatureKind.Kicker:
                return Hump(1.1f * Mathf.Pow(t, 1.6f), 0.9f);
            case FeatureKind.Drop:
                return Hump(t < 0.25f ? 1.3f * t / 0.25f : 1.3f, 0.9f);
            case FeatureKind.WallRide:
            {
                float h = 2.6f * Mathf.Clamp(Math.Min(t, 1 - t) * 4f, 0.25f, 1f);
                return Side(outside, [(0.85f, 0f), (0.85f, h), (1.05f, h), (1.05f, 0f)]);
            }
            default:
                return Hump(0.3f, 1f);
        }
    }

    /// <summary>A symmetric bump of the given height and half width with sloped sides.</summary>
    private static (float, float)[] Hump(float h, float halfWidth) =>
        [(-halfWidth - 0.3f, 0f), (-halfWidth, h), (halfWidth, h), (halfWidth + 0.3f, 0f)];

    /// <summary>A section given towards the outside of the bend, mirrored to the trail's right or left.</summary>
    private static (float, float)[] Side(float outside, (float O, float H)[] points)
    {
        var result = points.Select(p => (p.O * outside, p.H)).ToArray();
        if (outside < 0) Array.Reverse(result);
        return result;
    }

    /// <summary>+1 if the outside of the bend is to the right of travel (a left turn), -1 otherwise.</summary>
    private static float OutsideSign(WayGeometry g, long startCm, long endCm)
    {
        var a0 = g.PositionAt(startCm);
        var a1 = g.PositionAt(Math.Min(g.LengthCm, startCm + 100));
        var b0 = g.PositionAt(Math.Max(0, endCm - 100));
        var b1 = g.PositionAt(endCm);
        long dx0 = a1.X - a0.X, dz0 = a1.Z - a0.Z, dx1 = b1.X - b0.X, dz1 = b1.Z - b0.Z;
        long cross = dx0 * dz1 - dz0 * dx1; // > 0: turning right (X east, Z south)
        return cross > 0 ? -1f : 1f;
    }

    private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        st.AddVertex(a);
        st.AddVertex(b);
        st.AddVertex(c);
        st.AddVertex(a);
        st.AddVertex(c);
        st.AddVertex(d);
    }

    /// <summary>Closes an end of the sweep down to the ground (a fan over the section, whose ends are on the ground).</summary>
    private static void Cap(SurfaceTool st, Vector3[] section)
    {
        if (section.Length < 3) return;
        for (int k = 1; k < section.Length - 1; k++)
        {
            st.AddVertex(section[0]);
            st.AddVertex(section[k]);
            st.AddVertex(section[k + 1]);
        }
    }
}
