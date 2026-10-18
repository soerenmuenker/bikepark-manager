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
    public const int CurrentVersion = 11;

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

        if (version == 3)
        {
            // v4: the park day is just the opening hours (no last rides, lift warm-up, crew shift or overtime).
            if (root["world"] is JsonObject world)
            {
                world["rules"]?.AsObject().Remove("lastRideMinutes");
                world["rules"]?.AsObject().Remove("liftWarmupMinutes");
                if (world["crewRules"] is JsonObject crewRules)
                    foreach (string old in new[] { "workStartMinute", "workEndMinute", "overtimeMinutes" })
                        crewRules.Remove(old);
            }
            version = 4;
        }

        if (version == 4)
        {
            // v5: collisions are certain on close contact (collisionDistanceCm, collisionMinSpeedCmPerS) instead of a
            // chance within a window; existing parks get the new defaults.
            if (root["world"]?["crashRules"] is JsonObject crash)
            {
                crash.Remove("collisionPpm");
                crash.Remove("crossingWindowCm");
                var defaults = new Safety.CrashRules();
                crash["collisionDistanceCm"] = defaults.CollisionDistanceCm;
                crash["collisionMinSpeedCmPerS"] = defaults.CollisionMinSpeedCmPerS;
            }
            version = 5;
        }

        if (version == 5)
        {
            // v6: XP per km and per built feature by rating instead of per distinct rating / feature kind; feature types
            // have a park level (existing parks keep everything unlocked: their level 0).
            if (root["world"]?["reputationRules"] is JsonObject rep)
                foreach (string old in new[] { "xpPerTrailKm", "xpPerRating", "xpPerFeatureKind" })
                    rep.Remove(old);
            version = 6;
        }

        if (version == 6)
        {
            // v7: tools are owned in numbers and wear out: each owned tool id becomes one new tool.
            if (root["world"] is JsonObject world)
            {
                var tools = new JsonArray();
                if (world["ownedToolIds"] is JsonArray ids)
                {
                    int next = world["nextEntityId"]?.GetValue<int>() ?? 1;
                    foreach (var id in ids)
                        tools.Add(new JsonObject { ["id"] = next++, ["typeId"] = id?.GetValue<string>(), ["minutesUsed"] = 0 });
                    world["nextEntityId"] = next;
                }
                world.Remove("ownedToolIds");
                world["tools"] = tools;
            }
            version = 7;
        }

        if (version == 7)
        {
            // v8: no trail upkeep (the crew's wages already pay for keeping the trails up) and no accident insurance (crashes
            // close their trail for a while and trails crashing above their rating's rate cost reviews instead).
            if (root["world"] is JsonObject world)
            {
                if (world["crashRules"] is JsonObject crash)
                {
                    foreach (string old in new[] { "insuranceBaseCents", "insurancePerMinorCents", "insurancePerSeriousCents", "insurancePerFeatureByRating" })
                        crash.Remove(old);
                    if (crash["insuranceDays"] is { } days)
                    {
                        crash.Remove("insuranceDays");
                        crash["rateDays"] = days.GetValue<int>();
                    }
                }
                if (world["safety"] is JsonObject safety)
                {
                    safety.Remove("totalInsuranceCents");
                    safety.Remove("insuranceTodayCents");
                }
                world["trailRules"]?.AsObject().Remove("upkeepCentsPerKm");
                if (world["trailFeatureTypes"] is JsonArray types)
                    foreach (var type in types.OfType<JsonObject>())
                        type.Remove("upkeepCentsPerDay");
                if (world["finance"] is JsonObject finance)
                {
                    finance.Remove("totalTrailUpkeepCents");
                    finance.Remove("trailUpkeepTodayCents");
                }
                if (world["dayHistory"] is JsonArray history)
                    foreach (var day in history.OfType<JsonObject>())
                    {
                        day.Remove("trailUpkeepCents");
                        day.Remove("insuranceCents");
                    }
            }
            version = 8;
        }

        if (version == 8)
        {
            // v9: bars and shops sell products at park-wide prices, can be upgraded and no longer belong to a lift. Old
            // buildings keep what they sold (lunch and snacks, merch) at their old prices: the products are made from them.
            if (root["world"] is JsonObject world)
            {
                var products = new JsonArray();
                var prices = new JsonArray();
                void AddProduct(string id, string name, long price, JsonObject extra)
                {
                    if (price <= 0 || products.OfType<JsonObject>().Any(p => p["id"]?.GetValue<string>() == id)) return;
                    var product = new JsonObject
                    {
                        ["id"] = id, ["name"] = name, ["referencePriceCents"] = price, ["maxPriceCents"] = price * 3,
                    };
                    foreach (var (key, value) in extra) product[key] = value?.DeepClone();
                    products.Add(product);
                }
                var rules = world["rules"] as JsonObject;
                long Rule(string key, long fallback) => rules?[key]?.GetValue<long>() ?? fallback;
                if (world["facilityTypes"] is JsonArray types)
                    foreach (var type in types.OfType<JsonObject>())
                    {
                        var sells = new JsonArray();
                        long lunch = type["lunchPriceCents"]?.GetValue<long>() ?? 0;
                        long snack = type["snackPriceCents"]?.GetValue<long>() ?? 0;
                        long merch = type["merchPriceCents"]?.GetValue<long>() ?? 0;
                        int merchCost = type["merchCostPermille"]?.GetValue<int>() ?? 0;
                        if (lunch > 0)
                        {
                            AddProduct("lunch", "Lunch", lunch, new JsonObject
                            {
                                ["lunch"] = true, ["energy"] = Rule("lunchEnergy", 250), ["happiness"] = Rule("lunchHappiness", 40),
                                ["minMinutes"] = Rule("lunchMinMinutes", 20), ["maxMinutes"] = Rule("lunchMaxMinutes", 40),
                            });
                            sells.Add("lunch");
                        }
                        if (snack > 0)
                        {
                            AddProduct("snack", "Snack", snack, new JsonObject { ["happiness"] = 15, ["impulsePermille"] = 8 });
                            sells.Add("snack");
                        }
                        if (merch > 0)
                        {
                            AddProduct("merch", "Merch", merch, new JsonObject { ["merch"] = true, ["goodsCostPermille"] = merchCost });
                            sells.Add("merch");
                        }
                        foreach (string old in new[] { "lunchPriceCents", "snackPriceCents", "merchPriceCents", "merchCostPermille" })
                            type.Remove(old);
                        type["products"] = sells;
                    }
                // Two building types with different prices for the same product: the first price is the reference, the
                // others are dropped (the park sets one price now).
                world["productTypes"] = products;
                if (world["park"] is JsonObject park) park["prices"] = prices;
                if (world["facilities"] is JsonArray facilities)
                    foreach (var facility in facilities.OfType<JsonObject>())
                    {
                        facility.Remove("liftId");
                        facility["level"] = 0;
                    }
            }
            version = 9;
        }

        if (version == 9)
        {
            // v10: reviews remember the visit (trails, buildings, lifts, money spent, why the guest left); buildings and
            // lifts keep their own stats. Old reviews and stats start empty; guests in the park get what they paid so far
            // as the money they came with.
            if (root["world"]?["guests"] is JsonArray guests)
                foreach (var guest in guests.OfType<JsonObject>())
                    guest["broughtCents"] = (guest["cashCents"]?.GetValue<long>() ?? 0) + (guest["paidEntryCents"]?.GetValue<long>() ?? 0);
            version = 10;
        }

        if (version == 10)
        {
            // v11: workers have their own speed and wage (grades), the crew has no size limit and ways are built in
            // sections (workersPerSection replaces maxWorkersPerJob). Old workers become standard workers at the old wage;
            // park staff roles stay off (the flat base staff cost goes on).
            if (root["world"] is JsonObject world)
            {
                long wage = 18_000;
                int perSection = 2;
                if (world["crewRules"] is JsonObject crew)
                {
                    wage = crew["wagePerDayCents"]?.GetValue<long>() ?? wage;
                    crew.Remove("maxCrew");
                    crew.Remove("maxWorkersPerJob");
                }
                if (world["crew"] is JsonArray members)
                    foreach (var member in members.OfType<JsonObject>())
                    {
                        member["speedPermille"] = 1000;
                        member["wagePerDayCents"] = wage;
                    }
                if (world["jobs"] is JsonArray jobs)
                    foreach (var job in jobs.OfType<JsonObject>())
                        if ((job["workers"]?.GetValue<int>() ?? 0) > perSection)
                            job["workers"] = perSection;
            }
            version = 11;
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
