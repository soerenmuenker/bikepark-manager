using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Persistence;

/// <summary>JSON save/load of <see cref="WorldState"/>. A save is the full state; events are not saved.</summary>
public static class SaveGame
{
    /// <summary>Bump when the save format changes incompatibly, and add a migration in <see cref="Migrate"/>.</summary>
    public const int CurrentVersion = 3;

    private sealed record SaveFile(int Version, WorldState World);

    public static string Serialize(WorldState state) =>
        JsonSerializer.Serialize(new SaveFile(CurrentVersion, state), SimJson.Indented);

    public static WorldState Deserialize(string json)
    {
        json = Migrate(json);
        var save = JsonSerializer.Deserialize<SaveFile>(json, SimJson.Indented)
                   ?? throw new InvalidDataException("Save file is empty.");
        if (save.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported save version {save.Version} (expected {CurrentVersion}).");
        return save.World ?? throw new InvalidDataException("Save file has no world.");
    }

    /// <summary>Upgrades older save JSON step by step to <see cref="CurrentVersion"/>.</summary>
    private static string Migrate(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        int version = root?["version"]?.GetValue<int>() ?? CurrentVersion;
        if (root is null || version >= CurrentVersion) return json;

        if (version == 1)
        {
            // v2: trail gradient limits moved from grade permille to the -10..10 gradient score; old values are dropped
            // (the new defaults apply).
            if (root["world"]?["trailRules"] is JsonObject rules)
                foreach (string old in new[] { "pathMaxGradePermille", "trailMaxDownGradePermille", "trailMaxUpGradePermille" })
                    rules.Remove(old);
            version = 2;
        }

        if (version == 2)
        {
            // v3: only trail features wear (Feature.Condition) and repairs are manual. The per-segment trail condition,
            // the maintain switch, the old thresholds and repair-trail jobs are dropped (features start perfect).
            if (root["world"] is JsonObject world)
            {
                if (world["ways"] is JsonArray ways)
                    foreach (var way in ways.OfType<JsonObject>())
                    {
                        way.Remove("condition");
                        way.Remove("maintain");
                    }
                if (world["wearRules"] is JsonObject wear)
                {
                    wear.Remove("closeBelowPermille");
                    wear.Remove("maintainBelowPermille");
                }
                world["crewRules"]?.AsObject().Remove("repairMinutesPerSegment");
                if (world["jobs"] is JsonArray jobs)
                    for (int i = jobs.Count - 1; i >= 0; i--)
                        if (jobs[i]?["kind"]?.GetValue<string>() == "repairTrail")
                        {
                            int id = jobs[i]!["id"]!.GetValue<int>();
                            jobs.RemoveAt(i);
                            if (world["crew"] is JsonArray crew)
                                foreach (var member in crew.OfType<JsonObject>().Where(m => m["jobId"]?.GetValue<int>() == id))
                                    member["jobId"] = 0;
                        }
            }
            version = 3;
        }

        root["version"] = version;
        return root.ToJsonString();
    }

    public static void Save(WorldState state, string path) => SaveAtomic(state, path);

    /// <summary>
    /// Writes the save next to <paramref name="path"/> first and then moves it into place, so a crash while writing never
    /// leaves a broken file behind (the old one stays until the new one is complete).
    /// </summary>
    public static void SaveAtomic(WorldState state, string path)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(state));
        File.Move(temp, path, overwrite: true);
    }

    public static WorldState Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Deep copy via a save/load round trip.</summary>
    public static WorldState Clone(WorldState state) => Deserialize(Serialize(state));
}

/// <summary>Stable fingerprint of a <see cref="WorldState"/>, for determinism checks and desync detection.</summary>
public static class StateHash
{
    public static string Compute(WorldState state)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, SimJson.Compact);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    public static string Compute(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
