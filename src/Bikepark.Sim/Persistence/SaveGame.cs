using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Persistence;

/// <summary>JSON save/load of <see cref="WorldState"/>. A save is the full state; events are not saved.</summary>
public static class SaveGame
{
    /// <summary>Bump when the save format changes incompatibly, and add a migration in <see cref="Load"/>.</summary>
    public const int CurrentVersion = 1;

    private sealed record SaveFile(int Version, WorldState World);

    public static string Serialize(WorldState state) =>
        JsonSerializer.Serialize(new SaveFile(CurrentVersion, state), SimJson.Indented);

    public static WorldState Deserialize(string json)
    {
        var save = JsonSerializer.Deserialize<SaveFile>(json, SimJson.Indented)
                   ?? throw new InvalidDataException("Save file is empty.");
        if (save.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported save version {save.Version} (expected {CurrentVersion}).");
        return save.World ?? throw new InvalidDataException("Save file has no world.");
    }

    public static void Save(WorldState state, string path) => File.WriteAllText(path, Serialize(state));

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
