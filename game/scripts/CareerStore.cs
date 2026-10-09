using System.Text;
using System.Text.Json;
using Bikepark.Sim;
using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Reporting;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game;

/// <summary>What the start screen shows about a saved career (written next to its save, so listing is cheap).</summary>
public sealed record CareerInfo(string Id, string ParkName, long Day, long MoneyCents, int Level, int? RatingTenths, DateTime LastPlayedUtc);

/// <summary>
/// The saved careers: one save per career in <c>user://careers/&lt;id&gt;.json</c> (the whole world, written atomically)
/// plus <c>&lt;id&gt;.meta.json</c> (<see cref="CareerInfo"/>). Ids come from the creation time and the park name. The
/// wall clock is only used here, in the view, never in the sim.
/// </summary>
public static class CareerStore
{
    private static readonly JsonSerializerOptions MetaJson = new() { WriteIndented = true };

    public static string Directory => ProjectSettings.GlobalizePath("user://careers");

    private static string WorldPath(string id) => Path.Combine(Directory, $"{id}.json");

    private static string MetaPath(string id) => Path.Combine(Directory, $"{id}.meta.json");

    /// <summary>A new id: creation time plus the park name as a slug, e.g. <c>20261012-1430-old-ski-hill</c>.</summary>
    public static string NewId(string parkName)
    {
        var slug = new StringBuilder();
        foreach (char c in parkName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
            if (slug.Length >= 32) break;
        }
        string id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{slug.ToString().Trim('-')}".TrimEnd('-');
        return File.Exists(WorldPath(id)) ? $"{id}-{Guid.NewGuid().ToString("N")[..4]}" : id;
    }

    /// <summary>Saved careers, last played first (careers whose meta file is missing or broken are skipped).</summary>
    public static List<CareerInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var careers = new List<CareerInfo>();
        foreach (string meta in System.IO.Directory.GetFiles(Directory, "*.meta.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<CareerInfo>(File.ReadAllText(meta), MetaJson) is { } info && File.Exists(WorldPath(info.Id)))
                    careers.Add(info);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                GD.PushWarning($"Skipping career file {meta}: {ex.Message}");
            }
        }
        return careers.OrderByDescending(c => c.LastPlayedUtc).ToList();
    }

    /// <summary>Saves the career (world and meta), replacing its previous save.</summary>
    public static CareerInfo Save(string id, Simulation sim)
    {
        System.IO.Directory.CreateDirectory(Directory);
        SaveGame.SaveAtomic(sim.State, WorldPath(id));
        var kpi = KpiReport.From(sim.State, includeHash: false, network: sim.Network);
        var info = new CareerInfo(id, sim.State.Park.Name, GameTime.Day(sim.State.Tick), kpi.MoneyCents, kpi.Level, kpi.RatingTenths,
            DateTime.UtcNow);
        string temp = MetaPath(id) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(info, MetaJson));
        File.Move(temp, MetaPath(id), overwrite: true);
        return info;
    }

    public static WorldState Load(string id) => SaveGame.Load(WorldPath(id));

    public static void Delete(string id)
    {
        foreach (string path in new[] { WorldPath(id), MetaPath(id) })
            if (File.Exists(path)) File.Delete(path);
    }
}
