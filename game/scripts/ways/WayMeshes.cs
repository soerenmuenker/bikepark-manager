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

    public static readonly Color Gravel = new(0.80f, 0.77f, 0.70f);
    public static readonly Color Dirt = new(0.42f, 0.30f, 0.19f);

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

    /// <summary>A world position on a way geometry, in meters.</summary>
    public static Vector3 ToWorld(WayPoint p) => new(p.X / 100f, p.Y / 100f, p.Z / 100f);
}
