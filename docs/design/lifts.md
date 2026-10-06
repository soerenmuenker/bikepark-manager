# Lifts, stations, parking and queues (Phase 3)

Status: implemented 2026-10-06. Rules that must not break are in `architecture.md` §12.

## What the player sees

Guests drive to the **valley parking lot**, walk to the **valley station** and queue for the **gondola**. Only some
cabins take bikes. Riders get off on the **plateau**, a flattened pad on the mountain where the player's trails start,
and ride down. Long queues cost mood. Fit riders pedal up the old **gravel hiking route** instead. The park pays the
lift company a **daily fee** for each step of bike access ("every 4th cabin", "every 2nd", "all cabins"). A booked
step takes effect at the next opening.

Proof KPI (SimRunner, Starter Valley + `demo_lift_network.json`, 3 days): queues reach ~60 at the default tier
(every 4th cabin, 150 riders/h, ~18 min average wait). About one lap in ten is pedalled. €300/day in lift fees shows
in the expenses. At "all cabins" the wait drops to ~1 min, laps triple and mood rises, but the fee is €1,400/day.

## Model

| Piece | Stored (WorldState) | Derived |
|---|---|---|
| Pad (platform, plateau, parking surface) | `TerrainEdits`: oriented rectangle, target height, embankment gradient | edited `TerrainGrid` (heights, slopes, cleared trees) |
| Lift | `Lifts`: type, operator, two stations (hub id + pad), `BikeCarrierPermille`, rented tier, FIFO `Queue`, dispatch clock, stats | line length, ride time, network hubs and lift link |
| Parking lot | `ParkingLots`: spaces, pad, served lift | capacity = spaces × guests per car; walk link |
| Lift types | `LiftTypes` (copied from `data/lift_types.json`) | – |
| Lift company | `Operators` with `BikeAccessTiers` (from the scenario) | – |

- **Bike usage grade**: carrier *k* takes bikes iff `(k+1)·p/1000 > k·p/1000`. At 250 ‰ that is exactly every 4th
  cabin, evenly spread. The view uses the same rule to colour the cabins.
- **Throughput**: riders/h = 3600 / interval × p/1000 × bikes per carrier. For the 8-seat gondola (12 s, 2 bikes):
  150 / 300 / 600 riders/h.
- **Hubs**: stations and parking lots are network nodes. A way end within snap radius of a pad joins the hub at the
  pad edge, and all ways on one hub meet. Links: lift (valley → top, one-way), walk (parking ↔ valley station).
- **Base**: the parking lot, else the valley station, else the first path's start (Phase 2 worlds still work).

## Rider loop

1. Pick a trail (as in Phase 2).
2. Compare two routes to its start:
   - Lift route: cost of the ride plus the expected queue wait (queue ÷ throughput × wait cost).
   - Climbing route: costs more for tired riders.
3. Walk → join queue → `LiftSystem` boards from the front at the bike carriers → ride up at line speed → trail.
4. Queuing and riding restore energy. After `queueGraceMinutes` each minute in the queue costs mood. Unhappy
   riders leave the queue (and the park).

## Not in Phase 3 (groundwork only)

- Hikers in the cabins that don't take bikes, and negotiation with the lift company (Phase 10 politics). The tiers
  are content, so negotiation can later change them or the fees.
- Park-owned lifts exist (`OperatorId = null`, all cabins take bikes). Their cost, upkeep and build time come in
  Phases 4/7.
- `Way.Origin = Scenario` marks the pre-existing hiking route. It cannot be deleted; there is no hiker traffic yet.
