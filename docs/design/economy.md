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
shelf slots, workbenches or longer après-bike stays:

| Building | Sells | Upgrades |
|---|---|---|
| Snack bar (level 0) | lunch, snack · 24 seats | Espresso machine (+coffee) → Terrace (+24 seats, level 2) |
| Bike shop (1) | departments on 3 shelf slots, crash repairs · 1 workbench | see 9.3 (seven upgrades, each a visible extension) |
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
- **Going home:** one thing at the bike shop in reach (merch, clothing or a bike; 9.3).

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

## 9.3 Bikes (built)

One **bike shop** (level 1, 15,000 €) does everything bike: a store, a workshop and, upgraded, the rental. It replaced
the merch shop, pro shop, service shop and rental station, which offered little to decide. The player manages it with
three light levers (Store tab of its window) and grows it with upgrades; every upgrade is a visible extension of the
building (`FacilityUpgrade.Look`; the 24 × 14 m pad is laid out for the grown shop, so upgrades never touch the terrain).

| Upgrade | Adds | Level | Extension |
|---|---|---|---|
| Second workbench | +1 bench, tune-ups | 2 | open workshop lean-to with a workstand |
| Rental counter | rental fleet (rack of 20, 900 € a bike), fleet care | 2 | rental hut and a rack showing the fleet |
| Bigger store | +2 shelf slots | 3 | a second store wing |
| Bigger rack | +20 bikes | 4 | a second rack row |
| Suspension corner | +1 bench, suspension setups | 5 | glass annex |
| E-bike fleet | rental e-bikes | 6 | charging posts, green trim |
| Demo center | the Bikes department | 7 | flags and a test loop |

- **Departments (shelf slots):** more departments than slots (3, 5 with the bigger store), so the player picks what
  fits the crowd: **park merch** (going home, everyone), **clothing** (going home; rain jackets between laps in the rain),
  **protectors** (between laps, beginners ×2, twice as likely after seeing a crash; halve the share of serious crashes),
  **spare parts** (sold with repairs and services) and **bikes** (demo center: rare 3,200 € sales to happy intermediates
  and experts going home). Gear and bikes are paid by card (not from the day's cash); each department at most once a visit.
- **Range** (one per shop): budget (prices ×0.7, goods ×0.65, upkeep ×0.9; beginners ×1.4, experts ×0.5), standard,
  premium (prices ×1.5, goods ×1.5, upkeep ×1.3; beginners ×0.5, experts ×1.6). The range moves the fair price too, so
  a premium price is no rip-off: the range decides who buys and how big the ticket is. The park-wide price per product
  stays the lever on top.
- **Workshop:** crash repairs are always on (a share of the minor crashes, 35 %, only break the bike; the rider rolls
  down, a renter swaps the bike at the shop, an own bike is fixed on a free bench for 25 € and 15–30 min, else home).
  Services, offered once a visit to riders on their own bike at their first stop near the shop: **tune-up** (20 €, 60 %
  fewer crashes break their bike) and **suspension setup** (40 €, +8 % fun on red and black, intermediates and experts).
  Each can be switched off; **keep a bench free** saves the last bench for crash repairs.
- **Rental and fleet care:** guests without a bike come on top of the others (15 % of the arrivals at the fair 35 € a
  day), only when a bike is free: mostly beginners and casual riders; on e-bikes anyone, and they pedal up cheaply and
  fast. Bikes wear per run and in crashes and are retired at 0 %. With **fleet care** on, every bench restores 40 % of
  bike condition each night (worst bikes first, 0.25 € of parts per %), so the fleet lasts far longer.
- The Demo has a bike shop with its second workbench, rental counter and 15 bikes.
- Old careers (save v12) lose their merch shops, pro shops, service shops and rental stations on loading; the bike shop
  is new content, so only new careers can build it.

## 9.4 Sponsors and photo spots (built)

- **Trail sponsors:** apply to a brand for a trail (it answers in two days) or wait for brands to offer on their own
  for very good trails (rating, fun stars, runs a day). Accepting renames the trail ("Gravity Red Rocket") and the
  crew puts up a big sign in the brand's colour at its entrance; the deal pays per day and per run once the sign stands.
- **Park sponsor:** one at a time, worth several trail deals: per day and per visitor, a longer contract, and it
  needs a park level and rating stars. The park shows as "… presented by …" and arriving guests take the brand well
  or badly (`mood`).
- **Benefits:** every brand gives something in its line: Valley Cycles cheaper repair parts and protectors, Talbank
  half the loan interest, Bergbräu cheaper beer, Summit Outdoor cheaper campgrounds and bike shops, Bolt Energy cheaper
  coffee and snacks that cheer more, Gravity Bikes cheap rental bikes, Steelrope cheaper lifts, Alpen Motors half again
  on every parking fee. A park deal gives them in full, a trail deal 40 %; the best of a kind counts.
- Deals end at their last day, when the trail is renaturalized or closed for more than two days, or when you end them.
- **Photo spots:** a camera at a built jump or drop (3,500 €, 30 €/day). Riders filmed landing it may buy their clip
  (12 €) after the run, the more they enjoyed the jump the likelier; every clip shared adds a little buzz (demand).

## Roadmap

- **Phase 10 politics:** local businesses and the council can react to sponsors (local brands welcome, big brands
  controversial).

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
