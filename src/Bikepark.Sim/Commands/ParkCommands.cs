namespace Bikepark.Sim.Commands;

public sealed record SetEntryFeeCommand(long FeeCents) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (FeeCents < 0) return "Entry fee cannot be negative.";
        if (FeeCents > state.Rules.MaxEntryFeeCents) return $"Entry fee cannot exceed {state.Rules.MaxEntryFeeCents} cents.";
        return null;
    }

    public void Apply(SimContext ctx) => ctx.State.Park.EntryFeeCents = FeeCents;
}

public sealed record SetLiftTicketCommand(long TicketCents) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (TicketCents < 0) return "Lift ticket cannot be negative.";
        if (TicketCents > ctx.State.Rules.MaxEntryFeeCents) return $"Lift ticket cannot exceed {ctx.State.Rules.MaxEntryFeeCents} cents.";
        return null;
    }

    public void Apply(SimContext ctx) => ctx.State.Park.LiftTicketCents = TicketCents;
}

/// <summary>The parking fee per car (only charged where there are parking lots), 0..<see cref="State.ParkRules.MaxParkingFeeCents"/>.</summary>
public sealed record SetParkingFeeCommand(long FeeCents) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (FeeCents < 0) return "The parking fee cannot be negative.";
        if (FeeCents > ctx.State.Rules.MaxParkingFeeCents) return $"The parking fee cannot exceed {ctx.State.Rules.MaxParkingFeeCents} cents.";
        return null;
    }

    public void Apply(SimContext ctx) => ctx.State.Park.ParkingFeeCents = FeeCents;
}

public sealed record RenameParkCommand(string Name) : ICommand
{
    public const int MaxLength = 40;

    public string? Validate(SimContext ctx)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Park name cannot be empty.";
        if (Name.Trim().Length > MaxLength) return $"Park name cannot exceed {MaxLength} characters.";
        return null;
    }

    public void Apply(SimContext ctx) => ctx.State.Park.Name = Name.Trim();
}
