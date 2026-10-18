using Bikepark.Sim.Core;

namespace Bikepark.Sim.Crew;

/// <summary>The kind of work a job needs right now; tools speed up one kind each.</summary>
public enum WorkType : byte
{
    /// <summary>Digging and shaping: trails, paths and dirt features.</summary>
    Digging = 0,

    /// <summary>Building wood features.</summary>
    Carpentry = 1,

    /// <summary>Cutting trees (clearings and the corridors of planned ways).</summary>
    Felling = 2,
}

/// <summary>
/// A tool from <c>data/tools.json</c>, copied into the world. The park owns tools in numbers (<see cref="OwnedTool"/>);
/// each worker on a job uses one free tool of the job's kind of work, the best first, and is faster with it. Tools wear
/// out with use.
/// </summary>
public sealed class ToolType
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public WorkType WorkType { get; set; }
    public long PriceCents { get; set; }

    /// <summary>Added to the base speed of 1000 for the worker using it.</summary>
    public int SpeedBonusPermille { get; set; }

    /// <summary>Worked minutes until it is worn out and gone (0 = it lasts forever).</summary>
    public int LifeMinutes { get; set; }

    /// <summary>Park level needed to buy it (a sandbox has everything).</summary>
    public int RequiredLevel { get; set; }

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"toolTypes[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("toolTypes: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (!Enum.IsDefined(WorkType)) errors.Add($"{p}.workType is unknown");
        if (PriceCents < 0) errors.Add($"{p}.priceCents must be >= 0");
        if (SpeedBonusPermille is < 1 or > 5000) errors.Add($"{p}.speedBonusPermille must be within 1..5000");
        if (LifeMinutes < 0) errors.Add($"{p}.lifeMinutes must be >= 0");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel)
            errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        return errors;
    }
}

/// <summary>A tool the park owns.</summary>
public sealed class OwnedTool
{
    public int Id { get; set; }
    public string TypeId { get; set; } = "";

    /// <summary>Minutes it has been worked with (worn out at <see cref="ToolType.LifeMinutes"/>).</summary>
    public long MinutesUsed { get; set; }
}

/// <summary>Crew, work and wood tuning, loaded from the scenario and saved with the game. Work is in crew-minutes at base speed.</summary>
public sealed class CrewRules
{
    /// <summary>The wage of a standard worker (hired without an applicant; also the "skilled" reference).</summary>
    public long WagePerDayCents { get; set; } = 18_000;

    /// <summary>
    /// A way is built in sections of this length, each by up to <see cref="WorkersPerSection"/> workers at once: a long
    /// trail takes many workers in parallel. Features, repairs and signs are one spot: <see cref="WorkersPerSection"/>.
    /// </summary>
    public int SectionMeters { get; set; } = 20;
    public int WorkersPerSection { get; set; } = 2;

    /// <summary>Worker grades applicants come in (none: no applicants, every hire is a standard worker).</summary>
    public List<CrewGrade> Grades { get; set; } = [];

    /// <summary>Applicants waiting each day (rolled at the opening).</summary>
    public int ApplicantCount { get; set; } = 4;

    /// <summary>How much an applicant's speed and wage vary around their grade's (permille, either way).</summary>
    public int ApplicantSpreadPermille { get; set; } = 100;

    public int TrailWorkMinutesPerMeter { get; set; } = 4;
    public int PathWorkMinutesPerMeter { get; set; } = 6;

    /// <summary>Extra work on segments steeper than the "steep" gradient (permille of the normal work).</summary>
    public int SteepExtraPermille { get; set; } = 500;

    public int FellMinutesPerTree { get; set; } = 20;

    /// <summary>Repairing a fully worn feature takes this share of its build work (500 = half); less wear, less work.</summary>
    public int RepairWorkPermille { get; set; } = 500;
    public int WoodPerTree { get; set; } = 2;
    public long WoodPriceCents { get; set; } = 2_500;

    public int MinClearingRadiusMeters { get; set; } = 5;
    public int MaxClearingRadiusMeters { get; set; } = 40;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (WagePerDayCents < 0) errors.Add("crewRules.wagePerDayCents must be >= 0");
        if (SectionMeters is < 1 or > 1000) errors.Add("crewRules.sectionMeters must be within 1..1000");
        if (WorkersPerSection is < 1 or > 20) errors.Add("crewRules.workersPerSection must be within 1..20");
        foreach (var grade in Grades) errors.AddRange(grade.Validate());
        if (Grades.Select(g => g.Id).Distinct().Count() != Grades.Count) errors.Add("crewRules.grades: ids must be unique");
        if (Grades.Count > 0 && Grades.Sum(g => g.Weight) <= 0) errors.Add("crewRules.grades: weights must add up to more than 0");
        if (ApplicantCount is < 0 or > 20) errors.Add("crewRules.applicantCount must be within 0..20");
        if (ApplicantSpreadPermille is < 0 or > 500) errors.Add("crewRules.applicantSpreadPermille must be within 0..500");
        if (TrailWorkMinutesPerMeter is < 1 or > 1000 || PathWorkMinutesPerMeter is < 1 or > 1000)
            errors.Add("crewRules: work minutes per meter must be within 1..1000");
        if (SteepExtraPermille is < 0 or > 10_000) errors.Add("crewRules.steepExtraPermille must be within 0..10000");
        if (FellMinutesPerTree is < 1 or > 1000) errors.Add("crewRules.fellMinutesPerTree must be within 1..1000");
        if (RepairWorkPermille is < 1 or > 5000) errors.Add("crewRules.repairWorkPermille must be within 1..5000");
        if (WoodPerTree is < 0 or > 100) errors.Add("crewRules.woodPerTree must be within 0..100");
        if (WoodPriceCents < 0) errors.Add("crewRules.woodPriceCents must be >= 0");
        if (MinClearingRadiusMeters < 1 || MaxClearingRadiusMeters < MinClearingRadiusMeters || MaxClearingRadiusMeters > 200)
            errors.Add("crewRules: clearing radius must satisfy 1 <= min <= max <= 200");
        return errors;
    }
}

/// <summary>A worker grade (content): how fast its workers are and what they earn.</summary>
public sealed class CrewGrade
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Work speed of the worker (1000 = base); a tool's bonus is scaled with it.</summary>
    public int SpeedPermille { get; set; } = 1000;
    public long WagePerDayCents { get; set; }

    /// <summary>How often applicants of this grade turn up, relative to the other grades.</summary>
    public int Weight { get; set; } = 1;

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"crewRules.grades[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("crewRules.grades: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (SpeedPermille is < 100 or > 5000) errors.Add($"{p}.speedPermille must be within 100..5000");
        if (WagePerDayCents < 0) errors.Add($"{p}.wagePerDayCents must be >= 0");
        if (Weight < 0) errors.Add($"{p}.weight must be >= 0");
        return errors;
    }
}

/// <summary>Someone looking for work today; hired with <see cref="Commands.HireCrewCommand"/>.</summary>
public sealed class Applicant
{
    /// <summary>Unique per day and slot (not an entity id).</summary>
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string GradeId { get; set; } = "";
    public int SpeedPermille { get; set; } = 1000;
    public long WagePerDayCents { get; set; }
}
