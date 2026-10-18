using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// The job market: a fresh list of applicants at every opening (only where the scenario has worker grades); the first
/// list is rolled when the world is created (<see cref="JobMarket.Roll"/>).
/// </summary>
internal sealed class StaffSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (state.CrewRules.Grades.Count == 0) return;
        long day = GameTime.Day(ctx.Tick);
        bool first = state.ApplicantsDay < 0; // an older save
        bool opening = GameTime.MinuteOfDay(ctx.Tick) == state.Rules.OpenMinute && state.ApplicantsDay != day;
        if (first || opening) JobMarket.Roll(state, day);
    }
}

/// <summary>
/// Rolls the day's applicants for the crew: a grade by weight, speed and wage a little around the grade's, a name not yet
/// in the crew.
/// The rolls come from their own <see cref="SimRandom"/> stream, seeded by the world seed and the day (deterministic, and
/// the job market never shifts the guests' random numbers).
/// </summary>
public static class JobMarket
{
    private static readonly string[] FirstNames =
    [
        "Anna", "Ben", "Clara", "David", "Elif", "Finn", "Greta", "Hannes", "Ida", "Jonas", "Kim", "Lea", "Marco", "Nina",
        "Oskar", "Paula", "Quentin", "Rosa", "Simon", "Tara", "Uwe", "Vera", "Wim", "Xenia", "Yusuf", "Zoe", "Luca", "Mia",
        "Noah", "Emil", "Lotta", "Matteo", "Sanna", "Tobi", "Jule", "Arne",
    ];

    private static readonly string[] LastInitials = ["B.", "D.", "F.", "G.", "H.", "K.", "L.", "M.", "N.", "P.", "R.", "S.", "T.", "W."];

    private const ulong ApplicantStream = 0x5747FF;

    /// <summary>Applicant slots per day (<see cref="CrewRules.ApplicantCount"/> is at most 20).</summary>
    private const int MaxApplicants = 100;

    /// <summary>Replaces the applicants with <paramref name="day"/>'s (nothing without worker grades).</summary>
    public static void Roll(WorldState state, long day)
    {
        var rules = state.CrewRules;
        if (rules.Grades.Count == 0) return;
        state.ApplicantsDay = day;
        var rng = SimRandom.FromSeed(state.Seed ^ ((ulong)(day + 1) * 0x9E3779B97F4A7C15UL), ApplicantStream);
        state.Applicants.Clear();
        int totalWeight = rules.Grades.Sum(g => g.Weight);
        for (int i = 0; i < rules.ApplicantCount; i++)
        {
            int roll = rng.NextInt(totalWeight);
            var grade = rules.Grades.First(g => (roll -= g.Weight) < 0);
            int spread = rules.ApplicantSpreadPermille;
            int factor = 1000 + rng.Range(-spread, spread + 1);
            string name = $"{FirstNames[rng.NextInt(FirstNames.Length)]} {LastInitials[rng.NextInt(LastInitials.Length)]}";
            if (state.Crew.Any(m => m.Name == name) || state.Applicants.Any(a => a.Name == name))
                name = $"{name} {i + 2}";
            state.Applicants.Add(new Applicant
            {
                // Not an entity id: rolling applicants must not shift the ids of what the scenario builds. Unique per day and slot.
                Id = (int)(day * MaxApplicants + i + 1),
                Name = name,
                GradeId = grade.Id,
                SpeedPermille = Math.Max(100, grade.SpeedPermille * factor / 1000),
                // Faster applicants ask for a bit more.
                WagePerDayCents = Math.Max(0, grade.WagePerDayCents * factor / 1000 / 100 * 100),
            });
        }
    }
}
