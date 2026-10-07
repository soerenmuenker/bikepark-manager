# Wear and weather (Phase 5.2)

Status: implemented 2026-10-07. Rules that must not break are in `architecture.md` §16. Builds on
[daily_rhythm.md](daily_rhythm.md) (Phase 5.1).

Plan row: *"Segment condition, rain, closures, maintenance jobs. Done when: popular lines degrade and close without care."*

Decisions:
- **Weather is a daily seeded forecast**: each day is rolled at midnight, and tomorrow's roll is shown as the forecast.
- **Repairs are automatic and manual**: every trail has a *Maintain* switch, on by default. When a maintained trail
  gets worn, a repair job is queued for the crew. The player can also order a repair, or close and open a trail by hand.
- **Only trails wear.** Gravel paths don't.
- **A repair restores the whole trail.** The work is fixed when the job is queued, from the wear at that moment.

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
- **Worn trails** turn darker and rutted below 70 % condition, and reddish below 25 %. Wet ground darkens all trails.
  Riding a worn segment is slower and less fun.
- **Closures.** When any segment of a trail drops below 25 %, the trail closes: its stripe turns grey, its label says
  "CLOSED · worn out", and a toast appears. Riders already heading for it finish; nobody picks it any more. It reopens
  when a repair finishes. The player can also close or open a trail. A worn-out trail stays closed until it is repaired.
- **Trails menu**, for each trail:
  - A condition line, for example "Condition 81 % (worst 75 %) · crew repairs it below 60 %", "repair 40 % done", or
    "not maintained: closes below 25 %".
  - Buttons: **Repair**, **Close / Open**, and a **Maintain** check box.
- **Crew menu.** "Repair Flow Country" jobs (tool icon) sit in the queue like any other job. Cancelling a repair also
  turns off that trail's maintenance, otherwise the job would be queued again at once.

## Done-when KPI

```bash
dotnet run --project src/Bikepark.SimRunner -- --scenario data/scenarios/starter_valley.json --days 10 --commands data/scripts/demo_lift_network.json --daily [--commands data/scripts/demo_no_care.json]
```

Starter Valley, seed 1337, two workers, default bike access. `demo_no_care.json` turns off maintenance on both trails.

| Run | Weather (10 days) | Flow Country | Red Rocket | Lift rides day 8 / 9 / 10 | Crew-h | Exit mood |
|---|---|---|---|---|---|---|
| maintained | 4 sunny, 3 cloudy, 2 showers, 1 rain | open, avg 97 % (worst 96 %), 2 repairs | open, 91 % (89 %), 2 repairs | 1,349 / 1,287 / 1,357 | 45 | 65 % |
| no care | same for the first 7 days, then it differs | **worn out** day 8 ~16:00, avg 43 % (worst 24 %), closed 1,195 min | **worn out** day 8 ~14:00, 41 % (24 %), closed 1,293 min | 1,081 / **0** / **0** | 0 | 60 % |

- A popular line (~800 runs/day each) loses roughly 90–100 ‰ per day on dry ground. It reaches the repair level
  (60 %) after ~4.5 days and closes (25 %) after ~7.5 days without care. Wet days wear up to three times faster.
- Two workers keep both trails open with ~4.5 crew-h/day of repairs. The jobs are queued on their own and finish in
  about half a day (one ends in overtime, day 5 18:48).
- The 10-day feature run of [crew_and_jobs.md](crew_and_jobs.md) (3 workers) finishes the line on day 3 at
  11:16 (after the work cuts). Both repairs follow on day 4.
- The weather changes the guests (rain day: 127 visitors instead of ~280), but barely the lift rides, because the
  gondola, not the number of guests, limits riding.
- Known gap: with every trail closed, guests still come and pay. They only leave sooner and unhappier. Visitors
  reacting to closures and ratings is Phase 9 (reputation).

## Model

| Piece | Stored (WorldState) | Derived |
|---|---|---|
| Weather | `Weather`: `Today`, `Tomorrow` (`DayWeather`: kind, rain start/end minute), `WetnessPermille`, `RainMinutes`, `RainDays` | raining now (`WeatherMath.IsRaining`) |
| Weather tuning | `WeatherRules` (scenario `weatherRules`): odds, shower/rain hours, wetting/drying, arrivals per kind, rain mood | – |
| Condition | `Way.Condition`: millionths per segment index (missing = perfect; empty = mint) | worst / average permille, speed and fun loss |
| Closures | `Way.Closed` (player), `Way.WornOut` (wear), `Way.Maintain` | `Way.IsRideable` |
| Wear tuning | `WearRules` (scenario `wearRules`) | – |
| Repairs | `JobKind.RepairTrail = 3` with `WorkMinutes`; `CrewRules.RepairMinutesPerSegment` | – |
| Stats | `WayStats.ClosedMinutes`, `Repairs` | – |

All new rules default to off: no weather odds means always sunny with no randomness used, and `WearPerPass = 0`
means no wear. New fields have defaults, so `SaveGame.CurrentVersion` stays 2. Older saves load sunny and with mint
trails that are maintained.

### Rules

- **`WeatherSystem`** runs first in the tick.
  - At 00:00, `Today = Tomorrow` (rolled on the first day) and a new `Tomorrow` is rolled from the odds.
  - Showers last 1–3 h, between 06:00 and 20:00. Rain lasts 4–10 h, between 03:00 and 22:00.
  - Each minute in the rain the ground gets `wetPerRainMinute` wetter. Otherwise it dries by
    `dryPerMinuteSunny` / `dryPerMinuteCloudy`.
- **Arrivals** = base × fee factor × arrival profile × today's weather factor.
- **Wear** (`RiderSystem.Advance`). Each time a rider crosses a trail segment boundary, the segment loses:

  ```
  wearPerPass × (1000 + difficulty) / 1000 × (1000 + wetWearPermille × wetness / 1000) / 1000   (millionths)
  ```

  Below `roughBelowPermille` (70 %), speed drops by up to `wornSpeedLossPermille` (30 %) and the segment's fun by up
  to `wornFunLoss` (400 points), both scaling linearly down to condition 0.
- **`TrailCareSystem`** runs after `GuestSystem` and before `JobSystem`, every minute. It uses no randomness and only
  touches the network for trails that have wear. For each built trail:
  - worst segment < `closeBelowPermille` → `WornOut`, with a `TrailClosed` event;
  - maintained and worst < `maintainBelowPermille` with no repair job yet → `Jobs.QueueRepair`;
  - closed while the park is open → `ClosedMinutes++`.
- **Repair job.** Its work is `Σ missing condition × repairMinutesPerSegment` (digging, so the shovel set helps).
  When it completes, the trail is perfect again (`Condition` cleared), `WornOut` is reset and `TrailReopened` is sent.
  Deleting the trail cancels the job.
- **Riders** only start laps on `IsRideable` trails. The network keeps the live `Way` objects, so a closure needs no
  rebuild (and no `WaysRevision` bump).

### Commands

| Command | Rejected when |
|---|---|
| `repairTrail {wayId}` | not a built trail, a repair already queued, or no wear |
| `setTrailClosed {wayId, closed}` | not a built trail; opening a worn-out trail leaves it closed |
| `setTrailMaintain {wayId, maintain}` | not a built trail |

### View

- `Hud`: the weather line, plus toasts for the forecast, the start of rain, closures, reopenings and repairs.
- `DayLight`: an overcast factor (sunny 0, showers 0.35, cloudy 0.55, rain day 0.75, raining 1), eased over a few
  seconds. It dims the sun, greys the sun, sky and horizon colours, and thickens the fog.
- `world/RainView.cs`: GPU particle streaks in a 180 m box above the camera's focus while it rains.
- `WayView`: dirt colour per sample from the segment's condition and the wetness; a grey stripe and a "CLOSED" label
  on closed trails. It redraws when a segment's condition crosses a 10 % step, a trail closes, or the wetness crosses a
  25 % step.
- Debug: `--script=<file>` queues a command script, e.g. `--demo --script=data/scripts/demo_no_care.json
  --advance=10980` shows day 8 15:00 with Red Rocket worn out.

## Tests

- `WearAndWeatherTests`:
  - no odds → always sunny, no randomness;
  - the roll follows the odds and the rain windows;
  - the forecast becomes today;
  - rain wets the ground, which dries afterwards, and arrivals drop;
  - riders wear trails, three times as much when wet, and paths never;
  - speed and fun loss;
  - no wear rules → mint trails;
  - a worn-out trail closes and riders pick the other one;
  - a maintained trail gets a repair job that restores it and reopens it;
  - the repair, close and maintain commands, and cancelling a repair;
  - a worn-out trail stays closed when the player opens it;
  - rule validation;
  - save and load;
  - command discriminators.
- `TestWorlds.Script()` gains the new commands (incl. rejections).
- The existing Starter Valley determinism and save/load tests now run with weather and wear on.
