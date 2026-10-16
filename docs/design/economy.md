# Spending money: food, drink and more (Phase 9)

Phase 9 gives the park more ways to earn and spend money, and some fun along the way. The rules in short live in
`CLAUDE.md` (rules 14c, 14d); this document explains the design and the roadmap.

## 9.1 Food and drink (built)

### Products and prices

Bars and shops sell **products** (`data/products.json` → `WorldState.ProductTypes`): lunch, coffee, beer, radler, snack,
merch. The park sets **one price per product** for all buildings (`Park.Prices`, Finances menu → Prices,
`SetProductPriceCommand`). Each product has a fair `referencePriceCents`; guests compare the price with it:

- **Buy factor** (`FacilityMath.BuyFactorPermille`): 1000 at the reference, `elasticityPermille` less per reference price
  above it (beer 600: double price, 40 % of the buyers), up to +50 % when cheaper. It scales every buy chance and is the
  chance a guest accepts a lunch.
- **Rip-off:** paying above the reference costs mood (`FacilityRules.RipOffMood` per reference price above) and adds to
  `Guest.OverpaidCents`, which counts like a higher entrance fee in the review's value-for-money score.
- **Goods:** `goodsCostPermille` of each sale goes out as `FoodGoods` (bars) or `MerchGoods` (shops).

### Buildings and upgrades

Bars and shops stand **anywhere on the park's land with a gravel path, platform, station or parking lot within
`accessReachMeters`** (25 m); they serve guests within `serveReachMeters` (80 m). They no longer belong to a lift. Each
type has **upgrades** bought in order from the Finances menu (`UpgradeFacilityCommand`), each adding seats, products,
a better shop buy chance or longer après-bike stays:

| Building | Sells | Upgrades |
|---|---|---|
| Snack bar (level 0) | lunch, snack · 24 seats | Espresso machine (+coffee) → Terrace (+24 seats, level 2) |
| Merch shop (1) | merch | Bigger shelves (+6 % buy chance, level 3) |
| Beer rondel (2) | beer, radler, snack · 30 places | Bigger rondel (+30, level 4) → DJ booth (+30 min après, level 5) |
| Bike café (6) | lunch, coffee, snack · 80 seats | Barista bar (+radler, +20 seats, level 7) → Sun deck (+40 seats, level 8) |

### When guests buy

- **Between laps** (idle, queuing, at lunch): each minute, at the nearest bar in reach, an impulse buy per product in
  catalog order (`impulsePermille` × buy factor; alcohol also × mood and less the more they had; coffee only below
  `whenEnergyBelow`).
- **Lunch** once per visit at a bar in reach with a free seat, or a packed lunch.
- **On the way** (new): at the end of a route leg (top of a lift, a junction) with a bar in reach, once per lap: a due
  lunch (they sit down and the lap goes on afterwards: `Guest.ResumeLeg`) or an impulse buy with the chances scaled by
  `stopScalePermille`. A café at the top station now earns money.
- **Going home:** merch at the shop in reach.

### Coffee, beer and après-bike

- **Coffee:** energy plus a caffeine boost: climbing costs half the energy for an hour. Riders stay longer and ride more.
- **Beer:** alcohol (0..1000, wears off 2 per minute) makes riders **bolder** (they pick trails as if their skill were
  higher: `BoldnessPermille`) and **sloppier** (crash chances × up to 3 at full alcohol). Too much and they go home.
  The **beer hours** lever (all day / from 15:00 / après-bike only / never) trades drink sales for safety.
- **Après-bike** (from 9.1, both scenarios until 19:30): guests done for the day may stay at a bar serving alcohol for a
  drink or a few (DJ booth: longer), then go home; the park is empty by the end minute and the night is skipped as before.

Saves: version 9 turns the old per-building prices into products and drops `Facility.LiftId`. Old careers keep what
their buildings sold, at their old prices; the new buildings and upgrades come with new careers.

## 9.2 Parking fee and campers (built)

- **Parking fee** per car (Finances → Levers), only where there are parking lots. Each guest pays their share (fee /
  guests per car) on arrival; the share counts with the entrance fee as one arrival cost against the fair one
  (reference entry fee + reference parking share, free in Starter Valley), so a dearer car park means fewer guests,
  like a dearer entrance, and a lower value score.
- **Campground** (level 3: 40 pitches; Showers → More pitches → Glamping huts). Some arriving guests camp 1–3 nights
  (15 % at the fair night price of 15 €, fewer when dearer, only with a free pitch). They pay their nights on arrival,
  park at the camp (no parking fee, not counted against the lots), and when they are done for the day (closing, their
  stay, tired, drunk; après-bike first) they go to sleep instead of home. Asleep they are not in the park, so the night
  is still skipped. At the opening they wake rested (energy, mood + showers), buy a new day ticket and ride again.
  They review the whole stay once, when they finally leave.
- The Demo has a campground with showers next to the valley parking.

## Roadmap

- **9.3 Bikes:** `Guest.OwnBike` and bike condition. A rental station (buy a fleet at a discount, rent bikes with gear,
  attracts guests without a bike, the fleet wears), a pro shop (parts and gear; merch generalised) and a service shop
  (repairs guests' bikes after crashes and wear, else they leave early).
- **9.4 Brands and media:** trail sponsorship and naming rights (daily income, brand mood; ties into the Phase 10
  politics of town businesses and council); a photo/video spot at a signature jump (sells clips after the run, boosts
  influencer posts).

## More ideas

- Weather menu: hot chocolate in the rain, ice cream and radler on hot days.
- Energy drink: a big boost, then a dip later (sugar crash).
- A terrace with a view of a signature jump: watchers gain mood, riders landing it in front of a crowd get extra fun
  and a little boldness.
- Counter queues: service capacity per bar, waiting costs mood (upgrade: a second till).
- First-aid hut: quicker care for minor crashes, less mood lost.
- Skills school: paid lessons give skill for the visit, fewer crashes, attract beginners.
- E-bike rental with charging points: e-bikers pedal up gravel paths (the existing pedal-or-lift choice).
- Pump track or kids' area by the café: families stay longer and spend more.
- Race weekends: entry fees, spectators, heavy wear, a guaranteed influencer.
- Season passes for locals: cash up front, steady visitors.
- Bike wash: a small fee, a mood boost on rainy days.
