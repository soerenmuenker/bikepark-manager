# Wear and weather (Phase 5.2)

Status: implemented 2026-10-07. Rules that must not break are in `architecture.md` §16. Builds on
[daily_rhythm.md](daily_rhythm.md) (Phase 5.1).

Plan row: *"Segment condition, rain, closures, maintenance jobs. Done when: popular lines degrade and close without care."*

**Reworked on 2026-10-07:** only trail **features** wear (the trail itself never does), and there is **no automatic
maintenance**. When a feature is below 20 % the player gets a warning pop-up and sends workers; the trail is closed
while the crew repairs. A feature that reaches 0 % closes the trail by itself.

Decisions:
- **Weather is a daily seeded forecast**: each day is rolled at midnight, and tomorrow's roll is shown as the forecast.
- **Only features wear.** Trails without features (and gravel paths) never wear or close.
- **Repairs are manual**: the player picks the feature and how many workers (pop-up, or Trails menu → Repair…).
  The repair goes to the front of the crew queue.
- **The whole trail closes during a repair** (riders are routed per trail), from the moment the crew is assigned until
  the feature is perfect again. Riders already on the trail finish their run, and the crew only starts working once the
  last of them has left: there is never traffic on a trail with work going on.
- **A repair restores the feature to 100 %.** Work = `repairWorkPermille` (50 %) of its build work, scaled by the wear.

## What the player sees

- **Weather line under the clock**, for example "Showers 11–14 h · tomorrow Rain 06–17 h · ground 49 % wet", or
  RAINING while it rains.
  - Toasts: today's weather and the forecast at midnight, and "It's raining: trails wear faster when wet".
- **Clouds and rain.** Cloudy days grey the sky and dim the sun. Rain darkens it further, thickens the fog and shows
  falling streaks around where the camera looks.
- **Fewer guests on bad days** (Starter Valley):

  | Weather | Guests |
  |---|---|
  | Sunny | 105 % |
  | Cloudy | 95 % |
  | Showers | 80 % |
  | Rain | 45 % |

  Riders out in the rain lose mood.
- **Worn features.** The stretch of trail under a feature turns darker and rutted below 70 % condition, and reddish below
  20 %. Wet ground darkens all trails. Riding a worn feature is slower and less fun.
- **Warning pop-up.** When a feature falls below 20 % ("needs repair") a pop-up shows the trail's worn features with their
  condition, the crew time needed, a "Workers to send" stepper and a **Repair** button per feature (or **Repair all**,
  worst first). **✕** (top right) or **Later** closes it. It also appears again when a feature reaches 0 % and the
  trail closes; a trail you closed it for stays quiet until then (or until its features are repaired).
  A toast says which trail needs attention. Several warnings for one trail share one pop-up.
  **Time:** the game pauses when a warning pops up and continues at 1x when the last pop-up is closed (also when the
  repairs were sent). The pop-up opened from the Trails menu doesn't pause.
- **Closures.**
  - At 0 % on any feature the trail closes: its stripe turns grey and its label says "CLOSED · worn out". It reopens
    when the repairs leave no feature at 0 %.
  - While the crew **builds a new feature** or **repairs** one, the trail is closed ("CLOSED · crew at work") and opens
    again when the work is done. Between two jobs on the same trail it stays closed (up to 15 minutes) instead of
    opening for a minute. A feature that is only planned doesn't close the trail until the crew is assigned to it, and
    cancelling the job opens it again. The workers wait at a closed trail until the last rider is off it (job queue:
    "waiting for the last riders to leave"), then start.
  - The player can also close or open a trail. A worn-out trail stays closed until repaired.
  - Riders already on the trail finish their run. Every trail entrance is checked again: riders still on their way
    (walking, in the lift queue, on the lift, or about to use a trail as a connector) whose next trail closed since they
    planned the lap pick another open trail at random (weighted as usual) from where they stand, so nobody enters a
    closed trail. Routes never lead over closed trails.
- **Feature overview.** Clicking a trail in the Trails menu (or its **Features…** button) opens the same pop-up as a status
  overview: every built feature with its condition, the crew time to repair, the workers stepper and Repair / Repair all.
  Perfect features show "like new". It doesn't pause the game and stays open until you close it (✕ / Close).
- **Trails menu**, for each trail: the worst feature and its state ("Worst feature: Tabletop at 220 m, 20 % · worn"),
  and the buttons **Features…** (opens the overview) and **Close / Open**. The whole trail card is clickable.
- **Crew menu.** "Repair Berm on Flow Country at 40 m" jobs (tool icon) sit at the top of the queue. Cancelling one opens
  the trail again; the feature stays worn.

## Done-when KPI

```bash
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/demo_valley.json --days 14 --commands data/scripts/demo_lift_network.json --commands data/scripts/demo_features.json --commands data/scripts/demo_crew.json --daily [--auto-repair]
```

Starter Valley, seed 1337, 19 demo features on the two trails, 3 workers with tools. `--auto-repair` stands in for the
player: it sends 3 workers to every feature at the moment it falls below 20 %. Without the flag nobody repairs.

| Run (14 days) | Flow Country | Red Rocket | Lift rides | Crew-h |
|---|---|---|---|---|
| no repairs | worn out on day 6, closed 5,175 open-min | worn out on day 7, closed 4,577 open-min | 8,869 (0 a day from day 8) | 76 (all building) |
| warnings answered at once | open, 21 repairs, closed 968 open-min | open, 20 repairs, closed 1,123 open-min | 18,706 | 137 |

- A feature takes about 800 passes a day, so tabletops (the hardest) fall below 20 % after ~4.5 days, berms and rollers
  after ~5.5 days. Wet days wear up to three times faster.
- Answered warnings cost about 60 crew-h over the 14 days. The closed minutes are the crew building the features on days 1–3 plus the repairs; the other trail takes the riders, so the lift rides barely change.
- Known gap: with every trail closed, guests still come and pay. They only leave sooner and unhappier. Visitors
  reacting to closures and ratings is Phase 9 (reputation).
- The weather changes the guests (rain day: 127 visitors instead of ~280), but barely the lift rides, because the
  gondola, not the number of guests, limits riding.

## Model

| Piece | Stored (WorldState) | Derived |
|---|---|---|
| Weather | `Weather`: `Today`, `Tomorrow` (`DayWeather`: kind, rain start/end minute), `WetnessPermille`, `RainMinutes`, `RainDays` | raining now (`WeatherMath.IsRaining`) |
| Weather tuning | `WeatherRules` (scenario `weatherRules`): odds, shower/rain hours, wetting/drying, arrivals per kind, rain mood | – |
| Condition | `TrailFeature.Condition` (millionths, default perfect), `TrailFeature.Warned` | speed and fun loss, dirt tint |
| Closures | `Way.Closed` (player), `Way.WornOut` (a feature at 0), `Way.Repairing` (crew at work) | `Way.IsRideable` |
| Wear tuning | `WearRules` (scenario `wearRules`): `wearPerPass`, wet factor, rough/speed/fun, `warnBelowPermille` | – |
| Repairs | `JobKind.RepairFeature = 3` (`WayId`, `FeatureId`, `Workers`); `CrewRules.RepairWorkPermille` | – |
| Stats | `WayStats.ClosedMinutes`, `Repairs` | – |

All new rules default to off: no weather odds means always sunny with no randomness used, and `WearPerPass = 0`
means no wear. **Save version 3** (the 5.2 rework): a version 2 save is migrated by dropping the per-segment trail
condition, the maintain switch, the old thresholds and any repair jobs; every feature starts perfect.

### Rules

- **`WeatherSystem`** runs first in the tick.
  - At 00:00, `Today = Tomorrow` (rolled on the first day) and a new `Tomorrow` is rolled from the odds.
  - Showers last 1–3 h, between 06:00 and 20:00. Rain lasts 4–10 h, between 03:00 and 22:00.
  - Each minute in the rain the ground gets `wetPerRainMinute` wetter. Otherwise it dries by
    `dryPerMinuteSunny` / `dryPerMinuteCloudy`.
- **Arrivals** = base × fee factor × arrival profile × today's weather factor.
- **Wear** (`RiderSystem.Advance`). Each time a rider on a run passes the start of a built feature, it loses:

  ```
  wearPerPass × (1000 + type.difficulty) / 1000 × (1000 + wetWearPermille × wetness / 1000) / 1000   (millionths)
  ```

  Below `roughBelowPermille` (70 %), speed on the feature's stretch drops by up to `wornSpeedLossPermille` (30 %) and
  the feature's fun by up to `wornFunLoss` (400 points), both scaling linearly down to condition 0.
- **`TrailCareSystem`** runs after `GuestSystem` and before `JobSystem`, every minute, with no randomness. It never
  queues work. For each built trail with features:
  - a feature below `warnBelowPermille` (20 %) → `FeatureWarning` once (`Warned`, cleared by a repair);
  - a feature at 0 → `WornOut`, `TrailClosed(WornOut)` and another `FeatureWarning` (the pop-up appears again);
  - a repair job on one of its features that has started (progress or a worker on it) → `Repairing`, `TrailClosed(Repair)`;
    when it is gone → `TrailReopened`;
  - closed while the park is open → `ClosedMinutes++`.
- **Repair job.** Its work is fixed when it is queued: `type.workMinutes × repairWorkPermille / 1000 × missing condition`
  (digging or carpentry like the build). `Workers` limits the crew on it (0 = `maxWorkersPerJob`). When it completes
  the feature is perfect, `WornOut` is reset if no feature is left at 0, and `TrailReopened` is sent. Deleting the trail
  or the feature cancels the job.
- **Riders** only start laps on `IsRideable` trails. The network keeps the live `Way` and `TrailFeature` objects, so
  wear and closures need no rebuild (and no `WaysRevision` bump).

### Commands

| Command | Rejected when |
|---|---|
| `repairFeature {wayId, featureId, workers}` | not a built trail or feature, a repair already queued, workers outside 0..max, or perfect condition |
| `setTrailClosed {wayId, closed}` | not a built trail; opening a worn-out trail leaves it closed |

The earlier `repairTrail` and `setTrailMaintain` commands are gone (automatic maintenance was removed).

### View

- `Hud`: the weather line, plus toasts for the forecast, the start of rain, closures, reopenings and repairs.
- `ui/RepairDialog.cs`: the warning pop-up (queued per trail, refreshed with the sim, closes when nothing is left to repair).
- `DayLight`: an overcast factor (sunny 0, showers 0.35, cloudy 0.55, rain day 0.75, raining 1), eased over a few
  seconds. It dims the sun, greys the sun, sky and horizon colours, and thickens the fog.
- `world/RainView.cs`: GPU particle streaks in a 180 m box above the camera's focus while it rains.
- `WayView`: dirt colour per sample, only under worn features, and the wetness; a grey stripe and a "CLOSED" label
  on closed trails. It redraws when a feature's condition crosses a 10 % step, a trail closes, or the wetness crosses a
  25 % step.
- Debug: `--demo --demo-features --demo-crew --advance=6700 --speed=0 --no-night-skip` shows the pop-up for Flow Country
  on day 5 (nobody repaired).

## Tests

- `WearAndWeatherTests`:
  - no odds → always sunny, no randomness; the roll follows the odds; the forecast becomes today;
  - rain wets the ground, which dries afterwards, and arrivals drop;
  - riders wear features (not the trail), three times as much when wet; a trail without features never wears;
  - speed and fun loss; no wear rules → perfect features;
  - one warning below 20 % and no automatic repair; a feature at 0 closes the trail and warns again, riders pick another;
  - a repair closes the trail while the crew works, restores the feature, reopens the trail;
  - a worn-out trail reopens only when no feature is left at 0;
  - the repair and close commands (incl. the worker count), a worn-out trail stays closed when opened by hand;
  - rule validation; save and load; a version 2 save is migrated; command discriminators.
- `TestWorlds.Script()` gains the new commands (incl. rejections).
- The existing Starter Valley determinism and save/load tests now run with weather and wear on.
