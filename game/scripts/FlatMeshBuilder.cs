using Godot;

namespace Bikepark.Game;

/// <summary>Accumulates non-indexed triangles with per-face normals pointing away from a reference point.</summary>
internal sealed class FlatMeshBuilder
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

    /// <summary>An axis-aligned box from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public void Box(Vector3 min, Vector3 max, Color color)
    {
        var c = (min + max) / 2f;
        Vector3 V(float x, float y, float z) => new(x, y, z);
        Vector3[] p =
        [
            V(min.X, min.Y, min.Z), V(max.X, min.Y, min.Z), V(max.X, max.Y, min.Z), V(min.X, max.Y, min.Z),
            V(min.X, min.Y, max.Z), V(max.X, min.Y, max.Z), V(max.X, max.Y, max.Z), V(min.X, max.Y, max.Z),
        ];
        int[][] faces = [[0, 1, 2, 3], [5, 4, 7, 6], [4, 0, 3, 7], [1, 5, 6, 2], [3, 2, 6, 7], [4, 5, 1, 0]];
        foreach (var f in faces)
        {
            Triangle(p[f[0]], p[f[1]], p[f[2]], color, c);
            Triangle(p[f[0]], p[f[2]], p[f[3]], color, c);
        }
    }

    /// <summary>A flat disc (wheel) in the YZ plane at <paramref name="center"/>, <paramref name="thickness"/> wide in X.</summary>
    public void Wheel(Vector3 center, float radius, float thickness, int sides, Color color)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            var r0 = new Vector3(0, MathF.Sin(a0) * radius, MathF.Cos(a0) * radius);
            var r1 = new Vector3(0, MathF.Sin(a1) * radius, MathF.Cos(a1) * radius);
            var left = Vector3.Left * thickness / 2f;
            var right = Vector3.Right * thickness / 2f;
            Triangle(center + left, center + left + r0, center + left + r1, color, center + right);
            Triangle(center + right, center + right + r1, center + right + r0, color, center + left);
            Triangle(center + left + r0, center + right + r0, center + right + r1, color, center);
            Triangle(center + left + r0, center + right + r1, center + left + r1, color, center);
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
