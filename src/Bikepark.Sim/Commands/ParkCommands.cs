using Bikepark.Sim.State;

namespace Bikepark.Sim.Commands;

public sealed record SetEntryFeeCommand(long FeeCents) : ICommand
{
    public string? Validate(WorldState state)
    {
        if (FeeCents < 0) return "Entry fee cannot be negative.";
        if (FeeCents > state.Rules.MaxEntryFeeCents) return $"Entry fee cannot exceed {state.Rules.MaxEntryFeeCents} cents.";
        return null;
    }

    public void Apply(SimContext ctx) => ctx.State.Park.EntryFeeCents = FeeCents;
}

public sealed record RenameParkCommand(string Name) : ICommand
{
    public const int MaxLength = 40;

    public string? Validate(WorldState state)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Park name cannot be empty.";
        if (Name.Trim().Length > MaxLength) return $"Park name cannot exceed {MaxLength} characters.";
        return null;
    }

    public void Apply(SimContext ctx) => ctx.State.Park.Name = Name.Trim();
}
