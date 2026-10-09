using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>Ribbon meshes along a <see cref="WayGeometry"/>, in world coordinates.</summary>
internal static class WayMeshes
{
    /// <summary>Rating colors; same as the SimRunner map renderer.</summary>
    public static Color RatingColor(TrailRating rating) => rating switch
    {
        TrailRating.Green => new Color(0.15f, 0.65f, 0.25f),
        TrailRating.Blue => new Color(0.15f, 0.40f, 0.90f),
        TrailRating.Red => new Color(0.90f, 0.15f, 0.15f),
        _ => new Color(0.08f, 0.08f, 0.08f),
    };

    /// <summary>"Red (steep overall)", "Blue (steep sections)", "Black (features)": the rating and what decided it.</summary>
    public static string RatingText(WayGeometry g) => g.Rating == TrailRating.Green ? "Green" : $"{g.Rating} ({g.RatingCause switch
    {
        RatingCause.OverallSteepness => "steep overall",
        RatingCause.SteepSections => "steep sections",
        _ => "features",
    }})";

    /// <summary>
    /// Preview color for a segment's gradient score (tenths): green = easy, yellow, orange = steep, red = beyond the
    /// limit; purple = climbing on a trail. Paths are judged in both directions, trails by their drop.
    /// </summary>
    public static Color GradientColor(WayKind kind, int tenths, TrailRules rules)
    {
        var easy = new Color(0.25f, 0.80f, 0.30f);
        var medium = new Color(0.95f, 0.85f, 0.20f);
        var steep = new Color(1.0f, 0.50f, 0.10f);
        var tooSteep = new Color(0.95f, 0.10f, 0.10f);
        if (kind == WayKind.AccessPath)
        {
            int g = Math.Abs(tenths);
            return g > rules.PathMaxGradient ? tooSteep
                : g > rules.PathSteepGradient ? steep
                : g > rules.PathSteepGradient / 2 ? medium
                : easy;
        }
        if (tenths > rules.TrailMaxClimbGradient) return tooSteep;
        if (tenths > rules.TrailSteepClimbGradient) return new Color(0.70f, 0.30f, 0.90f);
        int drop = -tenths;
        return drop > rules.TrailMaxDropGradient ? tooSteep
            : drop > rules.TrailSteepDropGradient ? steep
            : drop > rules.TrailSteepDropGradient / 2 ? medium
            : easy;
    }

    // Ways use an unshaded material (lit ribbons rendered dark), so colors are given in sRGB and converted to linear.
    public static readonly Color Gravel = new Color(0.78f, 0.78f, 0.79f).SrgbToLinear(); // light rock grey
    public static readonly Color Dirt = new Color(0.45f, 0.31f, 0.19f).SrgbToLinear(); // wood earth tone

    /// <param name="followGround">Edges sit on the terrain (trails); otherwise on the way's own graded height (paths).</param>
    /// <param name="color">Color per sample index.</param>
    public static ArrayMesh? Ribbon(TerrainGrid grid, WayGeometry g, float width, float lift, bool followGround, Func<int, Color> color)
    {
        int n = g.SampleCount;
        if (n < 2) return null;

        var vertices = new Vector3[n * 2];
        var normals = new Vector3[n * 2];
        var colors = new Color[n * 2];
        var indices = new int[(n - 1) * 6];
        float half = width / 2f;

        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            var dir = new Vector2(g.Xs[b] - g.Xs[a], g.Zs[b] - g.Zs[a]).Normalized();
            var side = new Vector2(-dir.Y, dir.X) * half;
            var center = new Vector2(g.Xs[i] / 100f, g.Zs[i] / 100f);
            for (int s = 0; s < 2; s++)
            {
                var p = center + (s == 0 ? -side : side);
                float y = followGround
                    ? grid.HeightAt((long)MathF.Round(p.X * 100), (long)MathF.Round(p.Y * 100)) / 100f
                    : g.Ys[i] / 100f;
                vertices[i * 2 + s] = new Vector3(p.X, y + lift, p.Y);
                normals[i * 2 + s] = Vector3.Up;
                colors[i * 2 + s] = color(i);
            }
        }

        for (int i = 0; i < n - 1; i++)
        {
            int l0 = i * 2, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
            indices[i * 6 + 0] = l0;
            indices[i * 6 + 1] = r0;
            indices[i * 6 + 2] = l1;
            indices[i * 6 + 3] = r0;
            indices[i * 6 + 4] = r1;
            indices[i * 6 + 5] = l1;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public static ShaderMaterial CreateMaterial(float depthBias = 0.0015f)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/way.gdshader") };
        material.SetShaderParameter("depth_bias", depthBias);
        return material;
    }

    /// <summary>Blueprint blue for planned ways (see-through, dashed by alpha).</summary>
    public static readonly Color Blueprint = new(0.55f, 0.85f, 1f);

    /// <summary>See-through material for planned ways (blueprint.gdshader); alpha comes from the vertex colors.</summary>
    public static ShaderMaterial CreateBlueprintMaterial() => new() { Shader = GD.Load<Shader>("res://shaders/blueprint.gdshader") };

    /// <summary>A world position on a way geometry, in meters.</summary>
    public static Vector3 ToWorld(WayPoint p) => new(p.X / 100f, p.Y / 100f, p.Z / 100f);
}
