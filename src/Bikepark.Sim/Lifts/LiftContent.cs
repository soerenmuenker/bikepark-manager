using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Lifts;

public enum LiftKind : byte
{
    Gondola = 0,
    Chairlift = 1,
    TBar = 2,
}

/// <summary>
/// A lift model from <c>data/lift_types.json</c>, copied into the world. Only carriers equipped for bikes take
/// riders (<see cref="BikesPerCarrier"/> each); how many are equipped is the lift's bike usage grade.
/// </summary>
public sealed class LiftType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public LiftKind Kind { get; set; }

    /// <summary>Persons per carrier (hikers, not simulated yet).</summary>
    public int CarrierCapacity { get; set; } = 8;

    /// <summary>Riders with bikes per bike-equipped carrier.</summary>
    public int BikesPerCarrier { get; set; } = 2;

    /// <summary>Seconds between two carriers leaving a station.</summary>
    public int IntervalSeconds { get; set; } = 12;

    public int SpeedCmPerS { get; set; } = 500;
    public int MinLengthMeters { get; set; } = 100;
    public int MaxLengthMeters { get; set; } = 2_500;

    /// <summary>Steepest average line gradient, in tenths of the gradient score.</summary>
    public int MaxGradient { get; set; } = 40;

    /// <summary>Valley station platform (along × across the line).</summary>
    public int StationLengthMeters { get; set; } = 30;
    public int StationWidthMeters { get; set; } = 20;

    /// <summary>Default mountain plateau size.</summary>
    public int PlateauLengthMeters { get; set; } = 50;
    public int PlateauWidthMeters { get; set; } = 40;

    /// <summary>Width cleared of trees under the line.</summary>
    public int CorridorCm { get; set; } = 1_000;

    // ---- Owning one (lifts the park builds itself; company lifts are rented, see LiftOperator) ----

    /// <summary>What a contractor charges to build one (restoring a derelict one costs half).</summary>
    public long BuildCostCents { get; set; }

    /// <summary>Running costs per day of a lift the park owns (staff, power, maintenance).</summary>
    public long UpkeepPerDayCents { get; set; }

    /// <summary>Park level needed to build one.</summary>
    public int RequiredLevel { get; set; }

    /// <summary>Days the contractor needs to build one (it runs from the next opening after) and to restore a derelict one.</summary>
    public int BuildDays { get; set; } = 1;
    public int RestoreDays { get; set; } = 1;

    /// <summary>Riders on it stay dry (closed cabins): no mood lost in the rain.</summary>
    public bool Sheltered { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public long RestoreCostCents => BuildCostCents / 2;

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"liftTypes[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("liftTypes: id is required");
        if (CarrierCapacity <= 0 || BikesPerCarrier <= 0 || BikesPerCarrier > CarrierCapacity) errors.Add($"{p}: carrier capacities are inconsistent");
        if (IntervalSeconds is < 1 or > 600) errors.Add($"{p}.intervalSeconds must be within 1..600");
        if (SpeedCmPerS is < 50 or > 2_000) errors.Add($"{p}.speedCmPerS must be within 50..2000");
        if (MinLengthMeters < 10 || MaxLengthMeters <= MinLengthMeters) errors.Add($"{p}: min/max length are inconsistent");
        if (MaxGradient is <= 0 or >= Gradient.MaxTenths) errors.Add($"{p}.maxGradient must be within 1..{Gradient.MaxTenths - 1}");
        if (StationLengthMeters < 5 || StationWidthMeters < 5 || PlateauLengthMeters < 5 || PlateauWidthMeters < 5)
            errors.Add($"{p}: station and plateau sizes must be at least 5 m");
        if (CorridorCm < 0) errors.Add($"{p}.corridorCm must be >= 0");
        if (BuildCostCents < 0 || UpkeepPerDayCents < 0) errors.Add($"{p}: costs must be >= 0");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel) errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        if (BuildDays is < 0 or > 365 || RestoreDays is < 0 or > 365) errors.Add($"{p}: build and restore days must be within 0..365");
        return errors;
    }
}

/// <summary>One step of bike access a lift company offers: how many carriers take bikes, and what it costs per day.</summary>
public sealed class BikeAccessTier
{
    public string Name { get; set; } = "";

    /// <summary>Share of carriers equipped for bikes, 0..1000.</summary>
    public int BikeCarrierPermille { get; set; }

    public long DailyFeeCents { get; set; }
}

/// <summary>An external company that runs lifts in the park (from the scenario). The park rents bike access in tiers.</summary>
public sealed class LiftOperator
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<BikeAccessTier> BikeAccessTiers { get; set; } = [];

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"operators[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("operators: id is required");
        if (BikeAccessTiers.Count == 0) errors.Add($"{p}: at least one bike access tier is required");
        for (int i = 0; i < BikeAccessTiers.Count; i++)
        {
            var tier = BikeAccessTiers[i];
            if (tier.BikeCarrierPermille is < 0 or > 1000) errors.Add($"{p}.bikeAccessTiers[{i}].bikeCarrierPermille must be within 0..1000");
            if (i > 0 && tier.BikeCarrierPermille <= BikeAccessTiers[i - 1].BikeCarrierPermille)
                errors.Add($"{p}.bikeAccessTiers must have strictly increasing bikeCarrierPermille");
            if (tier.DailyFeeCents < 0) errors.Add($"{p}.bikeAccessTiers[{i}].dailyFeeCents must be >= 0");
        }
        return errors;
    }
}

/// <summary>Structure and queue tuning, loaded from the scenario and saved with the game.</summary>
public sealed class LiftRules
{
    public int WalkSpeedCmPerS { get; set; } = 120;

    /// <summary>Average guests per car (1000 = one), for parking capacity.</summary>
    public int GuestsPerCarPermille { get; set; } = 2_000;

    public int ParkingSquareMetersPerSpace { get; set; } = 25;
    public int ParkingWidthMeters { get; set; } = 40;

    /// <summary>Furthest distance between a parking lot and the valley station it serves.</summary>
    public int StationReachMeters { get; set; } = 80;

    /// <summary>Deepest cut or highest fill under a pad.</summary>
    public int MaxPadCutFillCm { get; set; } = 1_500;

    /// <summary>Steepest embankment around pads, in tenths of the gradient score.</summary>
    public int EmbankmentGradient { get; set; } = 35;

    /// <summary>Waiting this long costs no mood; every further minute costs <see cref="QueueMoodPenaltyPerMinute"/>.</summary>
    public int QueueGraceMinutes { get; set; } = 5;
    public int QueueMoodPenaltyPerMinute { get; set; } = 3;

    /// <summary>Energy regained per minute while queuing or riding a lift.</summary>
    public int RestEnergyPerMinute { get; set; } = 3;

    /// <summary>Routing cost of one minute on the lift / in the queue, in cm of flat travel (paths cost distance + 10 × climb).</summary>
    public int LiftRideCostCmPerMinute { get; set; } = 9_000;
    public int LiftWaitCostCmPerMinute { get; set; } = 18_000;

    /// <summary>Routing cost of the lift day pass a guest still has to buy, per cent (80: a 10 € pass weighs like four and a half minutes of waiting).</summary>
    public int LiftTicketCostCmPerCent { get; set; } = 80;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (WalkSpeedCmPerS <= 0) errors.Add("liftRules.walkSpeedCmPerS must be > 0");
        if (GuestsPerCarPermille < 1000) errors.Add("liftRules.guestsPerCarPermille must be >= 1000");
        if (ParkingSquareMetersPerSpace <= 0 || ParkingWidthMeters < 10) errors.Add("liftRules parking sizes are out of range");
        if (StationReachMeters <= 0) errors.Add("liftRules.stationReachMeters must be > 0");
        if (MaxPadCutFillCm <= 0) errors.Add("liftRules.maxPadCutFillCm must be > 0");
        if (EmbankmentGradient is <= 0 or >= Gradient.MaxTenths) errors.Add($"liftRules.embankmentGradient must be within 1..{Gradient.MaxTenths - 1}");
        if (QueueGraceMinutes < 0 || QueueMoodPenaltyPerMinute < 0 || RestEnergyPerMinute < 0) errors.Add("liftRules queue values must be >= 0");
        if (LiftRideCostCmPerMinute < 0 || LiftWaitCostCmPerMinute < 0 || LiftTicketCostCmPerCent < 0) errors.Add("liftRules routing costs must be >= 0");
        return errors;
    }
}
