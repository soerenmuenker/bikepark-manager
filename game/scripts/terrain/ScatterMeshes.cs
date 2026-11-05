using Bikepark.Sim.Terrain;
using Godot;

namespace Bikepark.Game.Terrain;

/// <summary>Procedural low-poly, flat-shaded meshes for trees and rocks (vertex colors, no textures).</summary>
internal static class ScatterMeshes
{
    public static ArrayMesh CreateTree()
    {
        var b = new FlatMeshBuilder();
        var bark = new Color(0.33f, 0.23f, 0.14f);
        b.Prism(radius: 0.2f, y0: 0f, y1: 1.6f, sides: 6, bark);

        (float Y0, float Radius, float Y1, Color Color)[] tiers =
        [
            (1.1f, 1.7f, 4.6f, new Color(0.12f, 0.30f, 0.14f)),
            (2.8f, 1.35f, 6.0f, new Color(0.14f, 0.34f, 0.16f)),
            (4.4f, 0.9f, 7.4f, new Color(0.16f, 0.38f, 0.18f)),
        ];
        foreach (var t in tiers)
            b.Cone(t.Radius, t.Y0, t.Y1, sides: 7, t.Color);
        return b.ToMesh();
    }

    /// <summary>A low, wide desert juniper: a twisted trunk under grey-green crowns.</summary>
    public static ArrayMesh CreateJuniper()
    {
        var b = new FlatMeshBuilder();
        b.Prism(radius: 0.18f, y0: 0f, y1: 1.1f, sides: 5, new Color(0.20f, 0.14f, 0.09f));
        b.Cone(1.5f, 0.6f, 2.5f, sides: 7, new Color(0.10f, 0.15f, 0.07f));
        b.Cone(1.05f, 1.5f, 3.5f, sides: 6, new Color(0.13f, 0.18f, 0.09f));
        return b.ToMesh();
    }

    /// <summary>A knee-high sage-coloured shrub.</summary>
    public static ArrayMesh CreateShrub()
    {
        var b = new FlatMeshBuilder();
        b.Cone(0.8f, -0.05f, 0.9f, sides: 6, new Color(0.22f, 0.23f, 0.12f));
        b.Cone(0.5f, 0.25f, 1.15f, sides: 5, new Color(0.27f, 0.28f, 0.15f));
        return b.ToMesh();
    }

    /// <summary>The planes cutting a loose rock (normal, distance): they make its flat faces and hard edges.</summary>
    private static (Vector3 Normal, float Distance)[] Cuts(RockShape shape) => shape switch
    {
        RockShape.Slab => [(new(0.25f, 1, 0.1f), 0.22f), (new(1, 0.5f, 0.3f), 0.5f), (new(-0.8f, 0.7f, 0.9f), 0.42f), (new(0.3f, 0.6f, -1), 0.46f), (new(-1, 0.4f, -0.6f), 0.52f), (new(0.8f, 0.3f, 1), 0.56f)],
        RockShape.Block => [(new(0.18f, 1, -0.14f), 0.42f), (new(1, 0.08f, 0.15f), 0.44f), (new(-1, 0.12f, 0.3f), 0.4f), (new(0.2f, 0.1f, 1), 0.46f), (new(-0.15f, 0.05f, -1), 0.42f), (new(0.8f, 0.9f, 0.7f), 0.62f)],
        RockShape.Wedge => [(new(1, 0.9f, 0.15f), 0.2f), (new(-1, 0.02f, 0.1f), 0.42f), (new(0.1f, 0.15f, 1), 0.5f), (new(0.2f, 0.2f, -1), 0.44f), (new(-0.5f, 1, 0.4f), 0.55f)],
        RockShape.Fin => [(new(0.7f, 1, 0.1f), 0.5f), (new(-0.6f, 1, -0.2f), 0.52f), (new(0.08f, 0.1f, 1), 0.2f), (new(-0.12f, 0.05f, -1), 0.17f), (new(1, 0.1f, 0.25f), 0.42f), (new(-1, 0.2f, 0.1f), 0.46f)],
        _ => [(new(0.15f, 1, 0.2f), 0.34f), (new(1, 0.35f, -0.25f), 0.5f), (new(-1, 0.4f, 0.15f), 0.5f), (new(0.3f, 0.4f, 1), 0.42f), (new(-0.2f, 0.4f, -1), 0.46f), (new(0.7f, 0.5f, 0.9f), 0.5f)],
    };

    /// <summary>
    /// The rocks a piece of rock face is stacked from (kit shape, centre, half size; 1 = the piece's radius and height): wide
    /// ones low down and narrower ones on top make its ledges, the lowest reach far below the ground for steep slopes.
    /// </summary>
    private static (RockShape Shape, Vector3 Center, Vector3 Size)[] Stack(RockShape shape) => shape switch
    {
        RockShape.Ledged0 =>
        [
            (RockShape.Block, new(0, -0.35f, 0), new(1, 0.75f, 0.9f)),
            (RockShape.Slab, new(-0.15f, 0.5f, 0.1f), new(0.75f, 0.22f, 0.7f)),
            (RockShape.Block, new(0.2f, 0.72f, -0.1f), new(0.5f, 0.28f, 0.45f)),
        ],
        RockShape.Ledged1 =>
        [
            (RockShape.Block, new(-0.3f, -0.4f, 0), new(0.7f, 0.7f, 0.85f)),
            (RockShape.Fin, new(0.35f, 0.1f, 0.15f), new(0.35f, 0.9f, 0.6f)),
            (RockShape.Wedge, new(-0.4f, 0.5f, -0.1f), new(0.55f, 0.3f, 0.6f)),
        ],
        RockShape.Ledged2 =>
        [
            (RockShape.Split, new(0.1f, -0.35f, 0), new(0.95f, 0.7f, 0.75f)),
            (RockShape.Wedge, new(-0.35f, 0.5f, 0.15f), new(0.6f, 0.42f, 0.6f)),
            (RockShape.Fin, new(0.4f, 0.6f, -0.2f), new(0.3f, 0.4f, 0.5f)),
        ],
        _ => [(shape, Vector3.Zero, Vector3.One)],
    };

    /// <summary>
    /// A rock of the kit, fitted to its size in <see cref="RockShapes"/> (its foot below the ground): a ball cut flat by a few
    /// planes. Ledged shapes, the pieces of rock face, are stacks of those.
    /// </summary>
    public static ArrayMesh CreateRock(RockShape shape, int detail)
    {
        if (shape == RockShape.DropBlock) return CreateDropBlock();
        var parts = new List<(Vector3 Center, List<(Vector3 A, Vector3 B, Vector3 C)> Triangles)>();
        foreach (var (kit, center, size) in Stack(shape))
        {
            var triangles = CutBall(kit, detail);
            var points = triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
            Vector3 low = points.Aggregate((a, b) => a.Min(b)), high = points.Aggregate((a, b) => a.Max(b));
            // A stacked rock fills its box; a single one keeps its foot where the cuts left it.
            Vector3 Place(Vector3 p) => shape < RockShape.Ledged0 ? p : center + ((p - low) / (high - low) * 2f - Vector3.One) * size;
            parts.Add((center, triangles.Select(t => (Place(t.A), Place(t.B), Place(t.C))).ToList()));
        }

        // Fitted exactly: the rules count on these numbers.
        float radius = 0, height = 0;
        foreach (var (_, triangles) in parts)
            foreach (var (a, c, e) in triangles)
                foreach (var p in new[] { a, c, e })
                {
                    radius = Math.Max(radius, new Vector2(p.X, p.Z).Length());
                    height = Math.Max(height, p.Y);
                }
        var (radiusCm, heightCm) = RockShapes.Size(shape);
        Vector3 Fit(Vector3 p) => new(p.X / radius * radiusCm / 100f, p.Y / height * heightCm / 100f, p.Z / radius * radiusCm / 100f);

        var b = new FlatMeshBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            var inside = Fit(parts[i].Center) + new Vector3(0, 0.1f, 0);
            foreach (var (a, c, e) in parts[i].Triangles)
            {
                Vector3 pa = Fit(a), pc = Fit(c), pe = Fit(e);
                // The same shade across one flat face, another on the next; each rock of a stack its own.
                var n = (pc - pa).Cross(pe - pa).Normalized();
                float shade = 0.55f + 0.05f * MathF.Sin(MathF.Abs(n.X) * 4f + MathF.Abs(n.Z) * 7f + MathF.Abs(n.Y) * 3f) + 0.025f * i;
                b.Triangle(pa, pc, pe, new Color(shade, shade * 0.98f, shade * 0.93f), inside);
            }
        }
        return b.ToMesh();
    }

    /// <summary>
    /// A boulder made for a flat drop: a level, light top (flush with the hillside behind it), a small bevel and sheer sides
    /// that reach <see cref="RockShapes.DropBlockDepth"/> heights down, so the ground hides its foot on any slope. Its sheer
    /// front looks along +X, down the fall line. Sized exactly to <see cref="RockShapes.Size"/>: the rules count on it.
    /// </summary>
    private static ArrayMesh CreateDropBlock()
    {
        var (radiusCm, heightCm) = RockShapes.Size(RockShape.DropBlock);
        float r = radiusCm / 100f / 1.5f, h = heightCm / 100f, rim = h - 0.12f, foot = -RockShapes.DropBlockDepth * h;
        // The footprint, counter-clockwise from above: a straight front (+X), a rounded back. All within the radius.
        Vector2[] outline =
        [
            new(1.2f, -0.75f), new(1.26f, 0.02f), new(1.2f, 0.8f), new(0.3f, 1.05f),
            new(-0.8f, 0.9f), new(-1.25f, 0.2f), new(-1.1f, -0.7f), new(-0.1f, -1.05f),
        ];
        var b = new FlatMeshBuilder();
        var inside = new Vector3(0, foot / 2, 0);
        Vector3 At(Vector2 p, float y, float scale = 1f) => new(p.X * r * scale, y, p.Y * r * scale);
        var top = new Color(0.70f, 0.69f, 0.65f);
        var middle = At(Vector2.Zero, h);
        for (int i = 0; i < outline.Length; i++)
        {
            Vector2 a = outline[i], c = outline[(i + 1) % outline.Length];
            b.Triangle(middle, At(a, h, 0.9f), At(c, h, 0.9f), top, inside);
            var bevel = new Color(0.62f, 0.61f, 0.58f);
            b.Triangle(At(a, h, 0.9f), At(a, rim), At(c, rim), bevel, inside);
            b.Triangle(At(a, h, 0.9f), At(c, rim), At(c, h, 0.9f), bevel, inside);
            // The front faces darker, the back a touch lighter: the sheer side reads from above.
            float facing = (a.X + c.X) / 2.5f;
            float shade = 0.5f - 0.06f * facing + 0.02f * (i % 2);
            var side = new Color(shade, shade * 0.98f, shade * 0.93f);
            b.Triangle(At(a, rim), At(a, foot), At(c, foot), side, inside);
            b.Triangle(At(a, rim), At(c, foot), At(c, rim), side, inside);
        }
        return b.ToMesh();
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C)> CutBall(RockShape shape, int detail)
    {
        var cuts = Cuts(shape);
        Vector3 Cut(Vector3 direction)
        {
            var p = direction.Normalized();
            for (int pass = 0; pass < 3; pass++) // cutting by one plane can push a point over another
                foreach (var (normal, distance) in cuts)
                {
                    var n = normal.Normalized();
                    float over = p.Dot(n) - distance;
                    if (over > 0) p -= n * over;
                }
            if (shape == RockShape.Split && p.Y > 0.05f) // a crack across the top
                p.Y -= 0.32f * Math.Max(0f, 1f - MathF.Abs(p.X + 0.1f * p.Z - 0.08f) / 0.16f);
            return p;
        }

        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        void Split(Vector3 a, Vector3 c, Vector3 e, int depth)
        {
            if (depth > 0)
            {
                Vector3 ac = (a + c).Normalized(), ce = (c + e).Normalized(), ea = (e + a).Normalized();
                Split(a, ac, ea, depth - 1);
                Split(ac, c, ce, depth - 1);
                Split(ea, ce, e, depth - 1);
                Split(ac, ce, ea, depth - 1);
                return;
            }
            triangles.Add((Cut(a), Cut(c), Cut(e)));
        }
        var (corners, faces) = Icosahedron();
        foreach (var f in faces) Split(corners[f[0]], corners[f[1]], corners[f[2]], detail);
        return triangles;
    }

    private static (Vector3[] Corners, int[][] Faces) Icosahedron()
    {
        float p = (1f + MathF.Sqrt(5f)) / 2f;
        return ([
            new Vector3(-1, p, 0), new Vector3(1, p, 0), new Vector3(-1, -p, 0), new Vector3(1, -p, 0),
            new Vector3(0, -1, p), new Vector3(0, 1, p), new Vector3(0, -1, -p), new Vector3(0, 1, -p),
            new Vector3(p, 0, -1), new Vector3(p, 0, 1), new Vector3(-p, 0, -1), new Vector3(-p, 0, 1),
        ], [
            [0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11],
            [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6], [7, 1, 8],
            [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9],
            [4, 9, 5], [2, 4, 11], [6, 2, 10], [8, 6, 7], [9, 8, 1],
        ]);
    }

    public static StandardMaterial3D CreateMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 0.9f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
}
