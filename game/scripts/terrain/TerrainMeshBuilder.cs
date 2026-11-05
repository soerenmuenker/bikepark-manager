using Bikepark.Sim.Terrain;
using Godot;

namespace Bikepark.Game.Terrain;

/// <summary>
/// Builds the mesh for one square terrain chunk from the <see cref="TerrainGrid"/>. Vertices are relative to the
/// chunk center (so visibility ranges measure distance per chunk). Vertex data layout is documented in
/// <c>shaders/terrain.gdshader</c>. Edges get a short vertical skirt that hides cracks between chunks drawn at
/// different levels of detail.
/// </summary>
internal static class TerrainMeshBuilder
{
    private const float SkirtDepth = 4f;

    /// <summary>
    /// A coarse mesh's height: the ground, or the bed of a brook within half a step. Coarse samples miss a narrow bed, and
    /// on steep ground the water would float over the mesh.
    /// </summary>
    private static int CoarseHeight(TerrainGrid grid, int x, int z, int step)
    {
        const int LakeDepthCm = 150;
        int n = grid.SamplesX, rows = grid.SamplesZ, reach = step / 2, best = int.MaxValue, height = grid.HeightAtSample(x, z);
        if (grid.Scenery is null) return height;
        var water = grid.WaterDepth;
        for (int dz = -reach; dz <= reach; dz++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                int sx = x + dx, sz = z + dz;
                if (sx < 0 || sz < 0 || sx >= n || sz >= rows) continue;
                int depth = water[sz * n + sx], distance = dx * dx + dz * dz;
                if (depth is 0 or >= LakeDepthCm || distance >= best) continue;
                best = distance;
                height = grid.HeightAtSample(sx, sz);
            }
        return height;
    }

    /// <param name="x0">West edge of the chunk in meters (sample index).</param>
    /// <param name="z0">North edge of the chunk in meters (sample index).</param>
    /// <param name="sizeX">Chunk width in meters, already clipped to the map.</param>
    /// <param name="sizeZ">Chunk depth in meters, already clipped to the map.</param>
    /// <param name="step">Sample spacing in meters (1 = full detail).</param>
    public static ArrayMesh Build(TerrainGrid grid, int x0, int z0, int sizeX, int sizeZ, int step, Vector3 center)
    {
        int cellsX = (sizeX + step - 1) / step, cellsZ = (sizeZ + step - 1) / step;
        int sideX = cellsX + 1, sideZ = cellsZ + 1;
        int vertexCount = sideX * sideZ + 2 * sideX + 2 * sideZ;

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new Color[vertexCount];
        var uv2 = new Vector2[vertexCount];
        var uv = new Vector2[vertexCount]; // x: 1 in a stone field (scree tint, looks only)
        var indices = new List<int>((cellsX * cellsZ + 2 * cellsX + 2 * cellsZ) * 6);

        for (int iz = 0; iz < sideZ; iz++)
        {
            for (int ix = 0; ix < sideX; ix++)
            {
                int sx = Math.Min(x0 + ix * step, x0 + sizeX), sz = Math.Min(z0 + iz * step, z0 + sizeZ);
                int v = iz * sideX + ix;
                var sample = grid.SampleAt(sx, sz);
                vertices[v] = new Vector3(sx, (step > 1 ? CoarseHeight(grid, sx, sz, step) : sample.HeightCm) / 100f, sz) - center;
                normals[v] = Normal(grid, sx, sz);
                colors[v] = new Color(sample.TreeDensity / 255f, sample.Rock / 255f, sample.Roots / 255f, sample.WaterDepthCm / 255f);
                uv2[v] = new Vector2((float)sample.Surface, sample.SlopePermille / 1000f);
                uv[v] = new Vector2(grid.RockFormationAt(sx * 100L, sz * 100L) == RockFormation.DenseStones ? 1f : 0f, 0f);
            }
        }

        // Clockwise (Godot's front face) when seen from above.
        for (int iz = 0; iz < cellsZ; iz++)
        {
            for (int ix = 0; ix < cellsX; ix++)
            {
                int i00 = iz * sideX + ix, i10 = i00 + 1, i01 = i00 + sideX, i11 = i01 + 1;
                indices.AddRange([i00, i10, i01, i10, i11, i01]);
            }
        }

        // Skirts: duplicate each edge's vertices a few meters lower and stitch them to the edge.
        int next = sideX * sideZ;
        foreach (var edge in Edges(sideX, sideZ))
        {
            int start = next;
            for (int k = 0; k < edge.Length; k++)
            {
                int src = edge[k];
                vertices[next] = vertices[src] + Vector3.Down * SkirtDepth;
                normals[next] = normals[src];
                colors[next] = colors[src];
                uv2[next] = uv2[src];
                uv[next] = uv[src];
                next++;
            }
            for (int k = 0; k < edge.Length - 1; k++)
                indices.AddRange([edge[k], edge[k + 1], start + k, edge[k + 1], start + k + 1, start + k]);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static IEnumerable<int[]> Edges(int sideX, int sideZ)
    {
        yield return Enumerable.Range(0, sideX).ToArray();                                     // north
        yield return Enumerable.Range(0, sideX).Select(i => (sideZ - 1) * sideX + i).ToArray(); // south
        yield return Enumerable.Range(0, sideZ).Select(i => i * sideX).ToArray();               // west
        yield return Enumerable.Range(0, sideZ).Select(i => i * sideX + sideX - 1).ToArray();   // east
    }

    /// <summary>Normal from central differences on the full-resolution grid, so chunk borders match.</summary>
    private static Vector3 Normal(TerrainGrid grid, int x, int z)
    {
        int x0 = Math.Max(0, x - 1), x1 = Math.Min(grid.WidthMeters, x + 1);
        int z0 = Math.Max(0, z - 1), z1 = Math.Min(grid.DepthMeters, z + 1);
        float dx = (grid.HeightAtSample(x1, z) - grid.HeightAtSample(x0, z)) / 100f / (x1 - x0);
        float dz = (grid.HeightAtSample(x, z1) - grid.HeightAtSample(x, z0)) / 100f / (z1 - z0);
        return new Vector3(-dx, 1f, -dz).Normalized();
    }
}
