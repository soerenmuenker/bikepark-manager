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

    public static readonly Color StoneColor = new Color(0.55f, 0.55f, 0.53f).SrgbToLinear();

    public static Color BaseColor(FeatureMaterial material) => material switch
    {
        FeatureMaterial.Wood => WoodColor,
        FeatureMaterial.Stone => StoneColor,
        _ => DirtColor,
    };

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
    public static ArrayMesh Build(TerrainGrid grid, WayGeometry g, TrailFeatureType type, long startCm, Color color, long placedLengthCm = 0, long rampCm = 0)
    {
        if (type.Kind == FeatureKind.FlatDrop) return BuildFlatDrop(grid, g, type, startCm, placedLengthCm > 0 ? placedLengthCm : type.LengthCm, rampCm, color);
        if (type.Kind == FeatureKind.RockGarden) return BuildRocks(grid, g, type, startCm, color);
        long lengthCm = placedLengthCm > 0 ? placedLengthCm : type.LengthCm;
        float meters = lengthCm / 100f;
        int steps = Math.Max(6, (int)(meters * 3));
        float outside = OutsideSign(g, startCm, startCm + lengthCm);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(color);

        Vector3[]? previous = null;
        Vector3[] first = [], last = [];
        float startHeight = g.HeightAt(startCm) / 100f;
        bool previousFlat = false;
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            long d = startCm + (long)(lengthCm * t);
            var p = g.PositionAt(d);
            float fallen = startHeight - p.Y / 100f; // how far the trail has dropped since the start (level decks)
            var ahead = g.PositionAt(Math.Min(g.LengthCm, d + 50));
            var behind = g.PositionAt(Math.Max(0, d - 50));
            var dir = new Vector2(ahead.X - behind.X, ahead.Z - behind.Z).Normalized();
            var right = new Vector2(-dir.Y, dir.X);
            var center = new Vector2(p.X / 100f, p.Z / 100f);

            var profile = Profile(type, t, outside, fallen, meters);
            var section = new Vector3[profile.Length];
            for (int k = 0; k < profile.Length; k++)
            {
                var (u, h) = profile[k];
                var xz = center + right * u;
                float ground = grid.HeightAt((long)MathF.Round(xz.X * 100), (long)MathF.Round(xz.Y * 100)) / 100f;
                section[k] = new Vector3(xz.X, ground + 0.05f + h, xz.Y);
            }

            // Nothing over a gap (both sections flat on the ground): the trail shows through.
            bool flat = profile.All(q => q.H <= 0f);
            if (previous is not null && !(flat && previousFlat))
                for (int k = 0; k < section.Length - 1; k++)
                    Quad(st, previous[k], previous[k + 1], section[k + 1], section[k]);
            previousFlat = flat;
            if (i == 0) first = section;
            last = section;
            previous = section;
        }
        Cap(st, first);
        Cap(st, last);

        st.GenerateNormals();
        return st.Commit();
    }

    private const float DeckHalfWidth = 0.9f, DeckThickness = 0.14f, LandingHeight = 1.3f;

    /// <summary>
    /// A flat drop: a level plank deck on posts, from where it meets the hillside to its lip on top of the rock (open underneath, the
    /// rock stands in it); then the gap; then the wooden landing falling to the trail.
    /// </summary>
    private static ArrayMesh BuildFlatDrop(TerrainGrid grid, WayGeometry g, TrailFeatureType type, long startCm, long lengthCm, long rampCm, Color color)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float deck = g.HeightAt(startCm) / 100f + 0.05f;
        var dark = new Color(color.R * 0.82f, color.G * 0.82f, color.B * 0.82f, color.A);
        const float PlankMeters = 0.5f, PostEveryMeters = 2f;
        float deckMeters = rampCm / 100f;
        int planks = Math.Max(1, (int)(deckMeters / PlankMeters));
        for (int i = 0; i < planks; i++)
        {
            long a = startCm + rampCm * i / planks, b = startCm + rampCm * (i + 1) / planks;
            var (ca, ra) = Frame(g, a);
            var (cb, rb) = Frame(g, b);
            st.SetColor(i % 2 == 0 ? color : dark);
            Vector3 At(Vector2 c, Vector2 r, float side, float y) => new(c.X + r.X * side, y, c.Y + r.Y * side);
            float top = deck, under = deck - DeckThickness;
            Quad(st, At(ca, ra, -DeckHalfWidth, top), At(ca, ra, DeckHalfWidth, top), At(cb, rb, DeckHalfWidth, top), At(cb, rb, -DeckHalfWidth, top));
            Quad(st, At(ca, ra, -DeckHalfWidth, under), At(ca, ra, DeckHalfWidth, under), At(cb, rb, DeckHalfWidth, under), At(cb, rb, -DeckHalfWidth, under));
            Quad(st, At(ca, ra, -DeckHalfWidth, top), At(cb, rb, -DeckHalfWidth, top), At(cb, rb, -DeckHalfWidth, under), At(ca, ra, -DeckHalfWidth, under));
            Quad(st, At(ca, ra, DeckHalfWidth, top), At(cb, rb, DeckHalfWidth, top), At(cb, rb, DeckHalfWidth, under), At(ca, ra, DeckHalfWidth, under));
            if (i == planks - 1) Quad(st, At(cb, rb, -DeckHalfWidth, top), At(cb, rb, DeckHalfWidth, top), At(cb, rb, DeckHalfWidth, under), At(cb, rb, -DeckHalfWidth, under));
        }
        st.SetColor(dark);
        for (float along = deckMeters; along > 0.2f; along -= PostEveryMeters)
        {
            var (c, r) = Frame(g, startCm + (long)(along * 100));
            foreach (float side in new[] { -DeckHalfWidth + 0.1f, DeckHalfWidth - 0.1f })
                Post(st, grid, c + r * side, deck - DeckThickness);
        }

        // The landing: wooden, highest at its start.
        long landingCm = type.LandingDeckMeters * 100L, from = startCm + lengthCm - landingCm;
        st.SetColor(color);
        const int Steps = 12;
        Vector3[]? previous = null;
        Vector3[] first = [], last = [];
        for (int i = 0; i <= Steps; i++)
        {
            float t = (float)i / Steps;
            var (c, r) = Frame(g, from + (long)(landingCm * t));
            var section = Hump(LandingHeight * (1 - t), 0.9f).Select(q =>
            {
                var xz = c + r * q.Item1;
                return new Vector3(xz.X, grid.HeightAt((long)MathF.Round(xz.X * 100), (long)MathF.Round(xz.Y * 100)) / 100f + 0.05f + q.Item2, xz.Y);
            }).ToArray();
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

    /// <summary>A square post from the ground up to the given height.</summary>
    private static void Post(SurfaceTool st, TerrainGrid grid, Vector2 at, float top)
    {
        float bottom = grid.HeightAt((long)MathF.Round(at.X * 100), (long)MathF.Round(at.Y * 100)) / 100f - 0.1f;
        if (top <= bottom) return;
        const float H = 0.07f;
        Vector2[] corners = [at + new Vector2(-H, -H), at + new Vector2(H, -H), at + new Vector2(H, H), at + new Vector2(-H, H)];
        for (int k = 0; k < 4; k++)
        {
            var a = corners[k];
            var b = corners[(k + 1) % 4];
            Quad(st, new Vector3(a.X, bottom, a.Y), new Vector3(b.X, bottom, b.Y), new Vector3(b.X, top, b.Y), new Vector3(a.X, top, a.Y));
        }
    }

    /// <summary>The spot on the trail (meters) and the direction to the right of travel.</summary>
    private static (Vector2 Center, Vector2 Right) Frame(WayGeometry g, long d)
    {
        var p = g.PositionAt(Math.Clamp(d, 0, g.LengthCm));
        var ahead = g.PositionAt(Math.Min(g.LengthCm, d + 50));
        var behind = g.PositionAt(Math.Max(0, d - 50));
        var dir = new Vector2(ahead.X - behind.X, ahead.Z - behind.Z).Normalized();
        return (new Vector2(p.X / 100f, p.Z / 100f), new Vector2(-dir.Y, dir.X));
    }

    /// <summary>
    /// How high a rider is here on a flat drop (meters): level on the deck, a falling arc over the gap, down the landing. Null off it.
    /// </summary>
    public static float? RideHeight(WayGeometry g, TrailFeatureType type, long startCm, long lengthCm, long rampCm, long d)
    {
        if (type.Kind != FeatureKind.FlatDrop || d < startCm || d > startCm + lengthCm) return null;
        float deck = g.HeightAt(startCm) / 100f + 0.05f;
        long lip = startCm + rampCm, landingCm = type.LandingDeckMeters * 100L, close = startCm + lengthCm - landingCm;
        if (d <= lip) return deck;
        float landingTop = g.HeightAt(close) / 100f + 0.05f + LandingHeight;
        if (d <= close)
        {
            float t = (float)(d - lip) / Math.Max(1, close - lip);
            return deck + (landingTop - deck) * t * t;
        }
        return g.HeightAt(d) / 100f + 0.05f + LandingHeight * (1 - (float)(d - close) / Math.Max(1, landingCm));
    }

    /// <summary>
    /// Cross-section at <paramref name="t"/> (0..1 along the feature), from one side to the other: (lateral offset to the
    /// right of travel, height). The ends of each section are on the ground.
    /// </summary>
    private static (float U, float H)[] Profile(TrailFeatureType type, float t, float outside, float fallen, float meters)
    {
        float ease = Mathf.Clamp(Math.Min(t, 1 - t) * 5f, 0f, 1f); // ramps in and out over the first/last 20 %
        // Where the gap is (after the takeoff); landings are the slope beyond it.
        float gap = (float)type.GapMeters / Math.Max(1, type.LengthMeters);
        switch (type.Kind)
        {
            case FeatureKind.WoodRoller:
            {
                // A raised deck over the rock, with a roller about every meter.
                float bump = Mathf.Sin(t * meters * Mathf.Tau);
                return Hump(Mathf.Clamp(Math.Min(t, 1 - t) * meters, 0f, 1f) * (0.7f + 0.07f * bump), 0.8f);
            }
            case FeatureKind.LongJump:
            {
                float takeoff = (1 - gap) * 0.4f, land = takeoff + gap;
                float h = t < takeoff ? 1.8f * MathF.Pow(t / takeoff, 1.4f)
                    : t < takeoff + 0.03f ? 1.8f * (takeoff + 0.03f - t) / 0.03f
                    : t < land ? 0f
                    : t < land + 0.05f ? 1.6f * (t - land) / 0.05f
                    : 1.6f * (1 - t) / Math.Max(0.05f, 1 - land - 0.05f);
                return Hump(h, 1.5f);
            }
            case FeatureKind.BigDrop:
            {
                // A wooden ramp up to a level deck that ends over the gap; the trail falls away below it.
                float deckEnd = 1 - gap;
                float h = t < 0.15f ? 1.0f * t / 0.15f : t <= deckEnd ? 1.0f + Math.Max(0f, fallen) : 0f;
                return h <= 0f ? Hump(0f, 0.9f) : Hump(h, 0.9f);
            }
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

    /// <summary>
    /// A rock garden: low-poly boulders of different sizes strewn over the trail, placed deterministically (same feature,
    /// same rocks), a little lighter or darker each.
    /// </summary>
    private static ArrayMesh BuildRocks(TerrainGrid grid, WayGeometry g, TrailFeatureType type, long startCm, Color color)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int count = type.LengthMeters * 2;
        for (int i = 0; i < count; i++)
        {
            uint h = Hash((uint)i * 2654435761u ^ (uint)type.LengthMeters);
            float along = (i + 0.5f + ((h & 0xFF) / 255f - 0.5f) * 0.8f) / count;
            float lateral = (((h >> 8) & 0xFF) / 255f - 0.5f) * 2.6f;
            float size = 0.25f + ((h >> 16) & 0xFF) / 255f * 0.45f;
            float shade = 0.8f + ((h >> 24) & 0xFF) / 255f * 0.4f;
            long d = startCm + (long)(type.LengthCm * along);
            var p = g.PositionAt(d);
            var ahead = g.PositionAt(Math.Min(g.LengthCm, d + 50));
            var behind = g.PositionAt(Math.Max(0, d - 50));
            var dir = new Vector2(ahead.X - behind.X, ahead.Z - behind.Z).Normalized();
            var right = new Vector2(-dir.Y, dir.X);
            var xz = new Vector2(p.X / 100f, p.Z / 100f) + right * lateral;
            float ground = grid.HeightAt((long)MathF.Round(xz.X * 100), (long)MathF.Round(xz.Y * 100)) / 100f;
            st.SetColor(new Color(color.R * shade, color.G * shade, color.B * shade, color.A));
            Boulder(st, new Vector3(xz.X, ground - size * 0.25f, xz.Y), size, h);
        }
        st.GenerateNormals();
        return st.Commit();
    }

    /// <summary>A squashed, slightly irregular octahedron (eight faces) standing on the ground.</summary>
    private static void Boulder(SurfaceTool st, Vector3 at, float size, uint h)
    {
        float Jitter(int shift) => 0.75f + ((h >> shift) & 0x7) / 7f * 0.5f;
        var top = at + new Vector3(0, size * 1.1f * Jitter(1), 0);
        var bottom = at;
        Vector3[] ring =
        [
            at + new Vector3(size * Jitter(3), size * 0.45f, 0),
            at + new Vector3(0, size * 0.5f, size * Jitter(6)),
            at + new Vector3(-size * Jitter(9), size * 0.4f, 0),
            at + new Vector3(0, size * 0.45f, -size * Jitter(12)),
        ];
        for (int k = 0; k < 4; k++)
        {
            var a = ring[k];
            var b = ring[(k + 1) % 4];
            st.AddVertex(top); st.AddVertex(b); st.AddVertex(a);
            st.AddVertex(bottom); st.AddVertex(a); st.AddVertex(b);
        }
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    /// <summary>A flat see-through strip over the trail where a feature's landing must stay clear (placement preview).</summary>
    public static ArrayMesh BuildLanding(TerrainGrid grid, WayGeometry g, long fromCm, long toCm, Color color)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(color);
        Vector3? prevLeft = null, prevRight = null;
        for (long d = fromCm; ; d = Math.Min(toCm, d + 100))
        {
            var p = g.PositionAt(Math.Min(d, g.LengthCm));
            var ahead = g.PositionAt(Math.Min(g.LengthCm, d + 50));
            var behind = g.PositionAt(Math.Max(0, d - 50));
            var dir = new Vector2(ahead.X - behind.X, ahead.Z - behind.Z).Normalized();
            var right = new Vector2(-dir.Y, dir.X) * 1.4f;
            var center = new Vector2(p.X / 100f, p.Z / 100f);
            Vector3 At(Vector2 xz) => new(xz.X, grid.HeightAt((long)MathF.Round(xz.X * 100), (long)MathF.Round(xz.Y * 100)) / 100f + 0.12f, xz.Y);
            var left = At(center - right);
            var rightPoint = At(center + right);
            if (prevLeft is { } pl && prevRight is { } pr)
                Quad(st, pl, pr, rightPoint, left);
            prevLeft = left;
            prevRight = rightPoint;
            if (d >= toCm) break;
        }
        st.GenerateNormals();
        return st.Commit();
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
