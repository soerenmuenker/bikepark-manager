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

    public static ArrayMesh CreateRock()
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
            v[i] = new Vector3(dir.X, dir.Y * 0.65f + 0.2f, dir.Z);
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

    public static StandardMaterial3D CreateMaterial() => new()
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 0.9f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Accumulates non-indexed triangles with per-face normals pointing away from a reference point.</summary>
    private sealed class FlatMeshBuilder
    {
        private readonly List<Vector3> _vertices = [];
        private readonly List<Vector3> _normals = [];
        private readonly List<Color> _colors = [];

        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color, Vector3 inside)
        {
            var normal = (c - a).Cross(b - a).Normalized();
            if (normal.Dot((a + b + c) / 3f - inside) < 0)
            {
                (b, c) = (c, b);
                normal = -normal;
            }
            foreach (var v in new[] { a, b, c })
            {
                _vertices.Add(v);
                _normals.Add(normal);
                _colors.Add(color);
            }
        }

        public void Prism(float radius, float y0, float y1, int sides, Color color)
        {
            var axis = new Vector3(0, (y0 + y1) / 2f, 0);
            for (int i = 0; i < sides; i++)
            {
                var (p0, p1) = (Ring(radius, i, sides), Ring(radius, i + 1, sides));
                Vector3 a = p0 + Vector3.Up * y0, b = p1 + Vector3.Up * y0, c = p1 + Vector3.Up * y1, d = p0 + Vector3.Up * y1;
                Triangle(a, b, c, color, axis);
                Triangle(a, c, d, color, axis);
            }
        }

        public void Cone(float radius, float y0, float y1, int sides, Color color)
        {
            var tip = new Vector3(0, y1, 0);
            var axis = new Vector3(0, y0 + (y1 - y0) * 0.3f, 0);
            for (int i = 0; i < sides; i++)
            {
                var a = Ring(radius, i, sides) + Vector3.Up * y0;
                var b = Ring(radius, i + 1, sides) + Vector3.Up * y0;
                Triangle(a, b, tip, color, axis);
                Triangle(a, b, new Vector3(0, y0, 0), color * 0.8f, axis + Vector3.Up * 10f); // underside
            }
        }

        public ArrayMesh ToMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }

        private static Vector3 Ring(float radius, int i, int sides)
        {
            float angle = Mathf.Tau * i / sides;
            return new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
        }
    }
}
