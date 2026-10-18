using Bikepark.Sim.Staff;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Sets how many people of a park staff role the park employs (<see cref="StaffRole.MaxCount"/> at most). Takes effect at
/// once; wages are paid at the end of the day for whoever is employed then.
/// </summary>
public sealed record SetStaffCommand(string RoleId, int Count) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (!state.StaffRules.Enabled) return "This park has no staff roles to manage.";
        if (StaffMath.FindRole(state, RoleId) is not { } role) return $"Unknown role '{RoleId}'.";
        if (Count < 0 || Count > role.MaxCount) return $"Employ 0 to {role.MaxCount} ({role.Name.ToLowerInvariant()}).";
        if (Count > StaffMath.Count(state, RoleId))
        {
            int level = Reputation.ParkProgress.CurrentLevel(state, ctx.Network);
            if (state.Rules.UnlockLevel(level) < role.RequiredLevel)
                return $"{role.Name} needs park level {role.RequiredLevel} (you are level {level}).";
        }
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var staff = ctx.State.Staff;
        if (staff.FirstOrDefault(p => p.RoleId == RoleId) is { } position) position.Count = Count;
        else staff.Add(new StaffPosition { RoleId = RoleId, Count = Count });
    }
}
