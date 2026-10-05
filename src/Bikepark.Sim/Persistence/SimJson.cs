using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bikepark.Sim.Persistence;

/// <summary>Shared JSON settings for saves, scenarios, command scripts and hashing.</summary>
public static class SimJson
{
    /// <summary>Compact, canonical output. Used for hashing; property order follows declaration order.</summary>
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    /// <summary>Human-readable output for save files and reports.</summary>
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
