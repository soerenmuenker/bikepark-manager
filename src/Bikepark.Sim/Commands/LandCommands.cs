using Bikepark.Sim.Events;
using Bikepark.Sim.Land;
using Bikepark.Sim.Reputation;

namespace Bikepark.Sim.Commands;

/// <summary>Buys a parcel of land: needs its park level and its price, which is paid at once.</summary>
public sealed record BuyParcelCommand(string ParcelId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var parcel = ctx.State.Parcels.FirstOrDefault(p => p.Id == ParcelId);
        if (parcel is null) return $"Unknown parcel '{ParcelId}'.";
        return LandMath.CannotBuy(ctx.State, parcel, ParkProgress.CurrentLevel(ctx.State, ctx.Network));
    }

    public void Apply(SimContext ctx)
    {
        var parcel = ctx.State.Parcels.First(p => p.Id == ParcelId);
        ctx.State.Finance.Spend(parcel.PriceCents);
        ctx.State.Finance.TotalLandCents += parcel.PriceCents;
        ctx.State.OwnedParcelIds.Add(parcel.Id);
        ctx.Publish(new ParcelBought(ctx.Tick, parcel.Id, parcel.PriceCents));
    }
}
