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

    public static ArrayMesh CreateRock(float flatten = 0.65f)
    {
        // Icosahedron with deterministic radial jitter, flattened.
        float p = (1f + MathF.Sqrt(5f)) / 2f;
        Vector3[] v =
        [
            new(-1, p, 0), new(1, p, 0), new(-1, -p, 0), new(1, -p, 0),
            new(0, -1, p), new(0, 1, p), new(0, -1, -p), new(0, 1, -p),
            new(p, 0, -1), new(p, 0, 1), new(-p, 0, -1), new(-p, 0, 1),
        ];
        float[] jitter = [1.0f, 0.82f, 0.93f, 1.1f, 0.88f, 1.05f, 0.95f, 0.8f, 1.12f, 0.9f, 1.0f, 0.86f];
        for (int i = 0; i < v.Length; i++)
        {
            var dir = v[i].Normalized() * 0.6f * jitter[i];
            v[i] = new Vector3(dir.X, dir.Y * flatten + 0.2f, dir.Z);
        }

        int[][] faces =
        [
            [0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11],
            [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6], [7, 1, 8],
            [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9],
            [4, 9, 5], [2, 4, 11], [6, 2, 10], [8, 6, 7], [9, 8, 1],
        ];

        var b = new FlatMeshBuilder();
        var center = new Vector3(0, 0.2f, 0);
        for (int f = 0; f < faces.Length; f++)
        {
            float shade = 0.5f + 0.06f * (f % 4);
            b.Triangle(v[faces[f][0]], v[faces[f][1]], v[faces[f][2]], new Color(shade, shade * 0.98f, shade * 0.94f), center);
        }
        return b.ToMesh();
    }

    /// <summary>
    /// A piece of rock face: a finely divided lump with broken ledges (bands of rock stepping in and out) and cracks, wider
    /// than tall, its foot below the ground. <paramref name="variant"/> gives another shape. Without ledges: a boulder.
    /// </summary>
    public static ArrayMesh CreateFace(int variant, bool ledges = true, int detail = 3)
    {
        float p = (1f + MathF.Sqrt(5f)) / 2f;
        Vector3[] v =
        [
            new(-1, p, 0), new(1, p, 0), new(-1, -p, 0), new(1, -p, 0),
            new(0, -1, p), new(0, 1, p), new(0, -1, -p), new(0, 1, -p),
            new(p, 0, -1), new(p, 0, 1), new(-p, 0, -1), new(-p, 0, 1),
        ];
        int[][] faces =
        [
            [0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11],
            [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6], [7, 1, 8],
            [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9],
            [4, 9, 5], [2, 4, 11], [6, 2, 10], [8, 6, 7], [9, 8, 1],
        ];
        float seed = variant * 1.9f + 0.4f;
        const float Band = 0.17f;

        float Wave(Vector3 d, float f) => MathF.Sin(d.X * f + seed) * MathF.Sin(d.Y * f * 0.8f + seed * 2.1f) * MathF.Sin(d.Z * f * 1.1f + seed * 0.7f);

        // A function of the direction alone, so neighbouring triangles share their corners.
        Vector3 Shape(Vector3 direction)
        {
            var d = direction.Normalized();
            float radius = 1f + 0.26f * Wave(d, 2.3f) + 0.14f * Wave(d, 5.1f) + 0.06f * Wave(d, 11f);
            if (!ledges) return new Vector3(d.X * radius, d.Y * radius * 0.68f + 0.18f, d.Z * radius * 0.9f);
            float y = d.Y * radius * 0.72f;
            int band = (int)MathF.Floor(y / Band);
            float inBand = y / Band - band;
            // Ledges: each band sits in or out, its top nearly flat.
            float ledge = 1f + 0.11f * MathF.Sin(band * 2.7f + seed) + 0.05f * MathF.Sin(MathF.Atan2(d.Z, d.X) * 7f + band * 1.3f);
            float stepped = (band + Math.Min(1f, inBand * 1.7f)) * Band;
            return new Vector3(d.X * radius * ledge * 1.15f, stepped + 0.12f, d.Z * radius * ledge * 0.85f);
        }

        var b = new FlatMeshBuilder();
        var center = new Vector3(0, 0.1f, 0);
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
            Vector3 pa = Shape(a), pc = Shape(c), pe = Shape(e);
            float mid = (pa.Y + pc.Y + pe.Y) / 3f;
            float shade = 0.56f + 0.05f * MathF.Sin(mid * 37f + seed) + 0.03f * Wave(a, 9f);
            b.Triangle(pa, pc, pe, new Color(shade, shade * 0.98f, shade * 0.93f), center);
        }
        foreach (var f in faces)
            Split(v[f[0]].Normalized(), v[f[1]].Normalized(), v[f[2]].Normalized(), detail);
        return b.ToMesh();
    }

    public static StandardMaterial3D CreateMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 0.9f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
}
