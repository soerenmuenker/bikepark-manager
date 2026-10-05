using System.Text.Json.Serialization;

namespace Bikepark.Sim.Commands;

/// <summary>
/// A player (or AI/script) intent. The only way anything outside the simulation changes <see cref="State.WorldState"/>.
/// Commands are queued and applied at the start of their target tick, so they are replayable and serializable.
/// Every concrete command must be registered below with a stable discriminator; never rename a discriminator
/// once saves exist.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetEntryFeeCommand), "setEntryFee")]
[JsonDerivedType(typeof(RenameParkCommand), "renamePark")]
[JsonDerivedType(typeof(BuildWayCommand), "buildWay")]
[JsonDerivedType(typeof(DeleteWayCommand), "deleteWay")]
public interface ICommand
{
    /// <summary>Returns null if the command can be applied, otherwise a human-readable rejection reason.</summary>
    string? Validate(SimContext ctx);

    /// <summary>Applies the command. Only called after <see cref="Validate"/> returned null.</summary>
    void Apply(SimContext ctx);
}

/// <summary>A command waiting in the queue. Ordered by (Tick, Sequence).</summary>
public sealed record ScheduledCommand(long Tick, long Sequence, ICommand Command);

/// <summary>A command with a target tick, as written in scenario or command-script files.</summary>
public sealed record TimedCommand(long Tick, ICommand Command);
