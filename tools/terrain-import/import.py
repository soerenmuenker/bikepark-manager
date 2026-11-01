#!/usr/bin/env python3
"""Turns a square of the real world into a map: terrain content (data/terrain/<id>.json) and, with --scenario, a career
scenario (data/scenarios/<id>.json) whose only infrastructure is what really exists: major gravel routes, lifts, parking.

Elevation: AWS open terrain tiles (Terrarium PNG, no key). Everything else: OpenStreetMap via Overpass.
Python stdlib + curl. Downloads are cached in out/terrain-import/, so re-runs are cheap. The browser tool (server.py) uses this too.

  python3 tools/terrain-import/import.py --lat 47.74 --lon 12.04 --size 1500 --id farrenpoint
  python3 tools/terrain-import/import.py --peak Farrenpoint --near 47.74,12.04 --offset -550,0 --id farrenpoint --scenario --arrival 987,1244

--offset moves the square's centre from the point, in meters east,south. --arrival: map meters x,z; guests arrive at the road
junction nearest to it (a real parking lot wins). The map's +X is east, +Z is south.
"""
import argparse
import base64
import hashlib
import json
import math
import struct
import subprocess
import sys
import time
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / "out" / "terrain-import"
TILES = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png"
OVERPASS = "https://overpass-api.de/api/interpreter"
USER_AGENT = "bikepark-manager-terrain-import/0.1"
ATTRIBUTION = "Elevation: Mapzen/AWS Terrain Tiles; land cover (c) OpenStreetMap contributors (ODbL)"
ZOOM = 14
SMOOTH_METERS = 10  # blur radius per pass
MAX_WAY_METERS = 3500  # TrailRules.MaxLengthMeters is 4000
GAP_METERS = 20  # track ends this close to another track join it
LINK_METERS = 60  # a real lift or parking lot further from any route stays out: it couldn't be reached
START_MONEY_CENTS = 7_500_000
M_PER_DEG_LAT = 111_320.0


def fetch(url, data=None):
    """Downloads through curl: Python's own HTTPS often has no CA certificates on macOS."""
    key = hashlib.sha1((url + (data or "")).encode()).hexdigest()
    path = CACHE / key
    if path.exists():
        return path.read_bytes()
    CACHE.mkdir(parents=True, exist_ok=True)
    cmd = ["curl", "-sS", "-L", "-m", "200", "-A", USER_AGENT, "-w", "%{http_code}", "-o", str(path) + ".part", url]
    if data:
        cmd += ["--data-urlencode", "data@-"]
    wait = 15
    for _ in range(6):
        run = subprocess.run(cmd, input=(data or "").encode(), capture_output=True)
        code = run.stdout.decode().strip()
        if run.returncode == 0 and code == "200":
            Path(str(path) + ".part").rename(path)
            return path.read_bytes()
        if code not in ("000", "429", "500", "502", "503", "504"):
            sys.exit(f"{url}: HTTP {code} {run.stderr.decode().strip()}")
        print(f"  busy (HTTP {code}), waiting {wait} s", file=sys.stderr)
        time.sleep(wait)
        wait *= 2
    sys.exit(f"Could not fetch {url}; try again later.")


def overpass(query):
    return json.loads(fetch(OVERPASS, "[out:json][timeout:180];" + query))


# ---------------------------------------------------------------- elevation


def decode_png(raw):
    """8-bit RGB / RGBA PNG → (width, height, channels, rows of bytes)."""
    if raw[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    pos, idat, width, height, channels = 8, bytearray(), 0, 0, 0
    while pos < len(raw):
        length, kind = struct.unpack(">I4s", raw[pos:pos + 8])
        chunk = raw[pos + 8:pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
            channels = {2: 3, 6: 4}.get(color, 0)
            if depth != 8 or not channels or interlace:
                raise ValueError("unsupported PNG format")
        elif kind == b"IDAT":
            idat += chunk
    data = zlib.decompress(bytes(idat))
    stride = width * channels
    rows, prev, p = [], bytearray(stride), 0
    for _ in range(height):
        kind, row = data[p], bytearray(data[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = row[i - channels] if i >= channels else 0
            b = prev[i]
            c = prev[i - channels] if i >= channels else 0
            if kind == 1:
                row[i] = (row[i] + a) & 255
            elif kind == 2:
                row[i] = (row[i] + b) & 255
            elif kind == 3:
                row[i] = (row[i] + (a + b) // 2) & 255
            elif kind == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                row[i] = (row[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(row)
        prev = row
    return width, height, channels, rows


class Elevation:
    """Bilinear lookups in the stitched tiles."""

    def __init__(self):
        self.tiles = {}

    def tile(self, x, y):
        if (x, y) not in self.tiles:
            w, h, ch, rows = decode_png(fetch(TILES.format(z=ZOOM, x=x, y=y)))
            self.tiles[(x, y)] = (w, ch, rows)
        return self.tiles[(x, y)]

    def pixel(self, px, py):
        w, ch, rows = self.tile(px // 256, py // 256)
        r = rows[py % 256]
        i = (px % 256) * ch
        return r[i] * 256 + r[i + 1] + r[i + 2] / 256 - 32768

    def at(self, lat, lon):
        n = 256 * 2 ** ZOOM
        fx = (lon + 180) / 360 * n
        s = math.radians(lat)
        fy = (1 - math.log(math.tan(s) + 1 / math.cos(s)) / math.pi) / 2 * n
        fx, fy = fx - 0.5, fy - 0.5
        x0, y0 = int(fx), int(fy)
        tx, ty = fx - x0, fy - y0
        top = self.pixel(x0, y0) * (1 - tx) + self.pixel(x0 + 1, y0) * tx
        bot = self.pixel(x0, y0 + 1) * (1 - tx) + self.pixel(x0 + 1, y0 + 1) * tx
        return top * (1 - ty) + bot * ty


# ---------------------------------------------------------------- land cover


def rings_of(element):
    """(outer rings, inner rings) of a closed way or a multipolygon, as lists of (lat, lon)."""
    if element["type"] == "way":
        g = element.get("geometry")
        return ([[(p["lat"], p["lon"]) for p in g]], []) if g else ([], [])
    outer, inner = [], []
    for m in element.get("members", []):
        if m.get("type") == "way" and m.get("geometry"):
            (inner if m.get("role") == "inner" else outer).append([(p["lat"], p["lon"]) for p in m["geometry"]])
    return stitch(outer), stitch(inner)


def stitch(parts):
    """Joins multipolygon way pieces into closed rings."""
    parts, rings = [list(p) for p in parts], []
    while parts:
        ring = parts.pop()
        changed = True
        while ring[0] != ring[-1] and changed:
            changed = False
            for i, p in enumerate(parts):
                if p[0] == ring[-1]:
                    ring += p[1:]
                elif p[-1] == ring[-1]:
                    ring += p[-2::-1]
                elif p[-1] == ring[0]:
                    ring = p[:-1] + ring
                elif p[0] == ring[0]:
                    ring = p[:0:-1] + ring
                else:
                    continue
                parts.pop(i)
                changed = True
                break
        rings.append(ring)
    return rings


def spans(ring, to_cell, samples):
    """Per grid row, the x crossings of this ring."""
    rows = {}
    pts = [to_cell(*p) for p in ring]
    for (x0, z0), (x1, z1) in zip(pts, pts[1:] + pts[:1]):
        if z0 == z1:
            continue
        lo, hi = min(z0, z1), max(z0, z1)
        for z in range(max(0, math.ceil(lo)), min(samples - 1, math.ceil(hi) - 1) + 1):
            rows.setdefault(z, []).append(x0 + (z - z0) * (x1 - x0) / (z1 - z0))
    return rows


def fill(mask, ring, to_cell, samples, value):
    for z, xs in spans(ring, to_cell, samples).items():
        xs.sort()
        for a, b in zip(xs[::2], xs[1::2]):
            for x in range(max(0, math.ceil(a)), min(samples - 1, math.floor(b)) + 1):
                mask[z * samples + x] = value


def blur(mask, n):
    out = bytearray(n * n)
    for z in range(n):
        for x in range(n):
            total = count = 0
            for dz in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    xx, zz = x + dx, z + dz
                    if 0 <= xx < n and 0 <= zz < n:
                        total += mask[zz * n + xx]
                        count += 1
            out[z * n + x] = total // count
    return out


def smooth(values, n, radius):
    """Three box blur passes (about a Gaussian): takes out the source's facets, which would read as steep streaks."""
    for _ in range(3):
        out = [0.0] * (n * n)
        for z in range(n):
            for x in range(n):
                z0, z1, x0, x1 = max(0, z - radius), min(n - 1, z + radius), max(0, x - radius), min(n - 1, x + radius)
                total = sum(sum(values[zz * n + x0:zz * n + x1 + 1]) for zz in range(z0, z1 + 1))
                out[z * n + x] = total / ((z1 - z0 + 1) * (x1 - x0 + 1))
        values = out
    return values


def pack(raw):
    c = zlib.compressobj(9, zlib.DEFLATED, -15)  # raw deflate: what .NET's DeflateStream reads
    return base64.b64encode(c.compress(bytes(raw)) + c.flush()).decode()


def find_peak(name, near):
    lat, lon = near
    q = f'node["natural"="peak"]["name"="{name}"]({lat - .1},{lon - .1},{lat + .1},{lon + .1});out;'
    found = overpass(q)["elements"]
    if not found:
        sys.exit(f"No peak named {name} within ~10 km of {lat},{lon}")
    return found[0]["lat"], found[0]["lon"]


class Square:
    """The map's square: lat/lon to map meters and back."""

    def __init__(self, lat, lon, size):
        self.lat, self.lon, self.size = lat, lon, size
        self.m_lon = M_PER_DEG_LAT * math.cos(math.radians(lat))

    def latlon(self, x, z):
        return self.lat - (z - self.size / 2) / M_PER_DEG_LAT, self.lon + (x - self.size / 2) / self.m_lon

    def xz(self, la, lo):
        return (lo - self.lon) * self.m_lon + self.size / 2, (self.lat - la) * M_PER_DEG_LAT + self.size / 2

    def box(self):
        s, w = self.latlon(0, self.size)
        n, e = self.latlon(self.size, 0)
        return f"{s},{w},{n},{e}"


def write_terrain(square, cell, map_id):
    """Fetches heights and land cover, writes data/terrain/<id>.json. Returns a one-line summary."""
    size, n = square.size, square.size // cell + 1
    print(f"Elevation for {n}x{n} samples around {square.lat:.5f}, {square.lon:.5f}")
    elevation = Elevation()
    meters = [elevation.at(*square.latlon(x * cell, z * cell)) for z in range(n) for x in range(n)]
    meters = smooth(meters, n, max(1, SMOOTH_METERS // cell))
    low = math.floor(min(meters))
    heights = bytearray()
    for m in meters:
        heights += struct.pack("<H", round((m - low) * 10))

    print("Land cover from OpenStreetMap")
    box = square.box()
    osm = overpass(
        f'(way["landuse"="forest"]({box});way["natural"="wood"]({box});'
        f'relation["landuse"="forest"]({box});relation["natural"="wood"]({box});'
        f'way["natural"~"^(bare_rock|scree)$"]({box});relation["natural"~"^(bare_rock|scree)$"]({box}););out geom;')
    to_cell = lambda la, lo: tuple(v / cell for v in square.xz(la, lo))
    forest, rock = bytearray(n * n), bytearray(n * n)
    for el in osm["elements"]:
        tags = el.get("tags", {})
        mask = rock if tags.get("natural") in ("bare_rock", "scree") else forest
        outer, inner = rings_of(el)
        for r in outer:
            fill(mask, r, to_cell, n, 255)
        for r in inner:
            fill(mask, r, to_cell, n, 0)

    out = {
        "cellMeters": cell,
        "samples": n,
        "minElevationCm": low * 100,
        "heightsDm": pack(heights),
        "forest": pack(blur(forest, n)),
        "rock": pack(blur(rock, n)),
        "origin": f"{square.lat:.6f},{square.lon:.6f}",
        "attribution": ATTRIBUTION,
    }
    path = ROOT / "data" / "terrain" / f"{map_id}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(out, indent=1) + "\n")
    summary = (f"{path.relative_to(ROOT)}: {path.stat().st_size // 1024} KB, {low} .. {max(meters):.0f} m, "
               f"forest {sum(1 for v in forest if v) * 100 // (n * n)} %, rock {sum(1 for v in rock if v) * 100 // (n * n)} %")
    print(summary)
    return elevation, summary


# ---------------------------------------------------------------- real infrastructure


def is_major(tags):
    """Roads and tracks a car or tractor can use; hiking paths and overgrown grade 4-5 tracks are not major routes."""
    highway = tags.get("highway")
    if highway in ("tertiary", "unclassified", "residential", "service"):
        return True
    if highway != "track":
        return False
    grade = tags.get("tracktype")
    if grade:
        return grade in ("grade1", "grade2", "grade3")
    return tags.get("surface") in ("gravel", "fine_gravel", "compacted", "paved", "asphalt", "concrete", "unpaved")


def fetch_infrastructure(square):
    box = square.box()
    return overpass(f'(way["highway"]({box});way["aerialway"]({box});way["amenity"="parking"]({box});'
                    f'node["amenity"="parking"]({box}););out body geom;')["elements"]


def clip_runs(nodes, pts, size):
    """Splits a polyline into its runs inside the map (inset 4 m); ends at the border get node id None."""
    lo, hi, runs, run = 4.0, size - 4.0, [], []
    inside = lambda p: lo <= p[0] <= hi and lo <= p[1] <= hi

    def border(a, b):
        t = 1.0
        for i in (0, 1):
            for edge in (lo, hi):
                if (a[i] - edge) * (b[i] - edge) < 0:
                    t = min(t, (edge - a[i]) / (b[i] - a[i]))
        return a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t

    for i, p in enumerate(pts):
        if inside(p):
            if not run and i > 0:
                run.append((None, border(p, pts[i - 1])))
            run.append((nodes[i], p))
        elif run:
            run.append((None, border(run[-1][1], p)))
            runs.append(run)
            run = []
    if run:
        runs.append(run)
    return [r for r in runs if len(r) >= 2]


def simplify(pts, tolerance=3.0):
    """Douglas-Peucker: the bends stay, the control points get few."""
    if len(pts) < 3:
        return pts
    (ax, az), (bx, bz) = pts[0], pts[-1]
    length = math.hypot(bx - ax, bz - az) or 1.0
    worst, at = 0.0, 0
    for i in range(1, len(pts) - 1):
        d = abs((pts[i][0] - ax) * (bz - az) - (pts[i][1] - az) * (bx - ax)) / length
        if d > worst:
            worst, at = d, i
    if worst <= tolerance:
        return [pts[0], pts[-1]]
    return simplify(pts[:at + 1], tolerance)[:-1] + simplify(pts[at:], tolerance)


def road_network(elements, square):
    """The major routes as edges between junctions: (node a, node b, points). Nodes are OSM ids, or negative at the border."""
    runs = []
    for el in elements:
        if el["type"] == "way" and is_major(el.get("tags", {})) and el.get("geometry"):
            pts = [square.xz(p["lat"], p["lon"]) for p in el["geometry"]]
            runs += clip_runs(el["nodes"], pts, square.size)

    uses = {}
    for run in runs:
        for i, (node, _) in enumerate(run):
            if node is not None:
                uses[node] = uses.get(node, 0) + (2 if 0 < i < len(run) - 1 else 1)

    # Split at every junction, so ways only ever meet at their ends.
    edges, border = [], -1
    for run in runs:
        piece = [run[0]]
        for item in run[1:]:
            piece.append(item)
            if item[0] is not None and uses.get(item[0], 0) > 2 or item is run[-1]:
                ends = []
                for node, _ in (piece[0], piece[-1]):
                    if node is None:
                        node, border = border, border - 1
                    ends.append(node)
                edges.append([ends[0], ends[1], [p for _, p in piece]])
                piece = [item]

    # Mappers often stop a track a few meters short of another one: such an end joins it.
    def tie(end_index, e):
        px, pz = e[2][0] if end_index == 0 else e[2][-1]
        best, target = GAP_METERS, None
        for other in edges:
            if other is e:
                continue
            pts = other[2]
            for i, ((ax, az), (bx, bz)) in enumerate(zip(pts, pts[1:])):
                run = (bx - ax) ** 2 + (bz - az) ** 2 or 1.0
                t = max(0.0, min(1.0, ((px - ax) * (bx - ax) + (pz - az) * (bz - az)) / run))
                q = (ax + (bx - ax) * t, az + (bz - az) * t)
                d = math.hypot(px - q[0], pz - q[1])
                if d < best:
                    best, target = d, (other, i, q)
        return target

    counts = {}
    for e in edges:
        counts[e[0]] = counts.get(e[0], 0) + 1
        counts[e[1]] = counts.get(e[1], 0) + 1
    for e in list(edges):
        for end_index in (0, 1):
            node = e[end_index]
            if counts[node] > 1:
                continue
            target = tie(end_index, e)
            if target is None:
                continue
            other, i, q = target
            pts = other[2]
            if math.dist(q, pts[0]) < 1:
                joined = other[0]
            elif math.dist(q, pts[-1]) < 1:
                joined = other[1]
            else:
                joined, border = border, border - 1
                edges.append([joined, other[1], [q] + pts[i + 1:]])
                other[1], other[2] = joined, pts[:i + 1] + [q]
            e[end_index] = joined
            if end_index == 0:
                e[2][0] = q
            else:
                e[2][-1] = q
            counts[joined] = counts.get(joined, 0) + 2

    # Two junctions this close are one junction.
    merged = True
    while merged:
        merged = False
        where = {}
        for e in edges:
            where.setdefault(e[0], e[2][0])
            where.setdefault(e[1], e[2][-1])
        nodes = list(where)
        for i, a in enumerate(nodes):
            for b in nodes[i + 1:]:
                if math.dist(where[a], where[b]) < GAP_METERS and not any({e[0], e[1]} == {a, b} for e in edges):
                    for e in edges:
                        if e[0] == b:
                            e[0], e[2][0] = a, where[a]
                        if e[1] == b:
                            e[1], e[2][-1] = a, where[a]
                    merged = True
                    break
            if merged:
                break

    # Join pieces meeting at a node nothing else uses, so one track stays one way.
    changed = True
    while changed:
        changed = False
        at = {}
        for e in edges:
            at.setdefault(e[0], []).append(e)
            at.setdefault(e[1], []).append(e)
        for node, here in at.items():
            if len(here) != 2 or here[0] is here[1]:
                continue
            a, b = here
            if a[1] != node:
                a[0], a[1], a[2] = a[1], a[0], a[2][::-1]
            if b[0] != node:
                b[0], b[1], b[2] = b[1], b[0], b[2][::-1]
            a[1], a[2] = b[1], a[2] + b[2][1:]
            edges.remove(b)
            changed = True
            break

    # Rings can't attach both ends at one spot, and way length is limited: cut them in the middle.
    out = []
    for a, b, pts in edges:
        length = sum(math.dist(p, q) for p, q in zip(pts, pts[1:]))
        if length < 15:
            continue
        if a == b or length > MAX_WAY_METERS:
            mid = len(pts) // 2
            out += [[a, border, pts[:mid + 1]], [border, b, pts[mid:]]]
            border -= 1
        else:
            out.append([a, b, pts])
    return out


def order_from(edges, arrival):
    """All edges, breadth first from the junction nearest to arrival; routes not connected to it come last (they stand alone
    until the player links them). Returns (start point, ordered edges, how many stand alone)."""
    ends = {}
    for a, b, pts in edges:
        ends.setdefault(a, pts[0])
        ends.setdefault(b, pts[-1])
    if not ends:
        return None, [], 0
    start = min(ends, key=lambda n: math.dist(ends[n], arrival))
    ordered, seen, used, connected = [], set(), set(), 0
    for root in [start] + list(ends):
        if root in seen:
            continue
        seen.add(root)
        queue = [root]
        while queue:
            node = queue.pop(0)
            for i, (a, b, pts) in enumerate(edges):
                if i in used or node not in (a, b):
                    continue
                used.add(i)
                ordered.append(pts if a == node else pts[::-1])
                other = b if a == node else a
                if other not in seen:
                    seen.add(other)
                    queue.append(other)
        if root == start:
            connected = len(ordered)
    return ends[start], ordered, len(ordered) - connected


LIFT_TYPES = {"t-bar": "tbar", "j-bar": "tbar", "platter": "tbar", "drag_lift": "tbar", "rope_tow": "tbar", "magic_carpet": "tbar",
              "chair_lift": "chairlift_4", "mixed_lift": "chairlift_4", "gondola": "gondola_8", "cable_car": "gondola_8"}


def real_structures(elements, square, elevation):
    """Lifts and parking lots on the map: (lifts, parking), in map meters."""
    lifts, parking = [], []
    on_map = lambda p: 15 <= p[0] <= square.size - 15 and 15 <= p[1] <= square.size - 15
    for el in elements:
        tags = el.get("tags", {})
        if el["type"] == "way" and tags.get("aerialway") in LIFT_TYPES and el.get("geometry"):
            a, b = el["geometry"][0], el["geometry"][-1]
            if elevation.at(a["lat"], a["lon"]) > elevation.at(b["lat"], b["lon"]):
                a, b = b, a
            valley, mountain = square.xz(a["lat"], a["lon"]), square.xz(b["lat"], b["lon"])
            if on_map(valley) and on_map(mountain):
                lifts.append({"typeId": LIFT_TYPES[tags["aerialway"]], "name": tags.get("name") or "Old lift",
                              "valley": valley, "mountain": mountain})
        elif tags.get("amenity") == "parking":
            pts = [square.xz(p["lat"], p["lon"]) for p in el["geometry"]] if el.get("geometry") else [square.xz(el["lat"], el["lon"])]
            center = (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))
            if on_map(center):
                capacity = tags.get("capacity", "")
                spaces = int(capacity) if capacity.isdigit() else 30
                parking.append({"name": tags.get("name") or "Parking", "center": center, "spaces": max(10, min(200, spaces))})
    return lifts, parking


def cm(p):
    return {"x": round(p[0] * 100), "z": round(p[1] * 100)}


def nearest_on(ways, p):
    best, at = 1e18, None
    for pts in ways:
        for q in pts:
            d = math.dist(p, q)
            if d < best:
                best, at = d, q
    return best, at


def scenario_commands(edges, lifts, parking, arrival, operator, height):
    """Tick-0 commands: lifts and parking lots near a route first (their pads need the room), then the routes from the arrival
    outward, which attach to them, then a short drive to each structure no route reaches."""
    if parking:
        arrival = parking[0]["center"]
    start, ordered, left_out = order_from(edges, arrival)
    # Guests arrive at the low end of the first path: start with a route that climbs away from the arrival.
    for i, pts in enumerate(ordered):
        if pts[0] == start and height(pts[-1]) > height(start):
            ordered.insert(0, ordered.pop(i))
            break
    ways = [simplify(pts) for pts in ordered]
    routes = [{"type": "buildWay", "kind": "accessPath", "name": "", "origin": "scenario", "points": [cm(p) for p in pts]} for pts in ways]
    commands, skipped = [], []

    def linked(what, name, hub):
        distance, road = nearest_on(ways, hub)
        if road is None or distance > LINK_METERS:
            skipped.append(f"{what} {name}: {distance:.0f} m from any route" if road else f"{what} {name}: no route on the map")
            return None
        return road

    links = []
    for lift in lifts:
        road = linked("Lift", lift["name"], lift["valley"])
        if road is None:
            continue
        command = {"type": "buildLift", "typeId": lift["typeId"], "name": lift["name"], "origin": "scenario",
                   "valley": cm(lift["valley"]), "mountain": cm(lift["mountain"])}
        if lift["typeId"] == "tbar":
            command["derelict"] = True
        else:
            command.update({"operatorId": operator, "initialTier": 0})
        commands.append(command)
        links.append((lift["valley"], road))
    for lot in parking:
        road = linked("Parking", lot["name"], lot["center"])
        if road is None:
            continue
        toward = road if math.dist(road, lot["center"]) > 1 else (lot["center"][0] + 10, lot["center"][1])
        commands.append({"type": "buildParkingLot", "name": lot["name"], "origin": "scenario", "spaces": lot["spaces"],
                         "center": cm(lot["center"]), "toward": cm(toward)})
        links.append((lot["center"], road))
    commands += routes
    for hub, road in links:
        if math.dist(hub, road) > 2:
            commands.append({"type": "buildWay", "kind": "accessPath", "name": "", "origin": "scenario", "points": [cm(hub), cm(road)]})
    return commands, {"routes": len(ways), "routesStandingAlone": left_out, "skipped": skipped, "arrival": start}


def default_parcels(size, arrival):
    """Three by three, the middle one centred on the arrival as far as the map allows: that one is yours, the rest for
    sale, dearer further out. Meant to be redrawn in the scenario editor."""
    third = size / 3
    cuts = []
    for v in arrival:
        low = max(size / 6, min(size * 5 / 6 - third, v - third / 2))
        cuts.append([0, low, low + third, size])
    parcels, counter = [], 0
    for zi in range(3):
        for xi in range(3):
            away = max(abs(xi - 1), abs(zi - 1))
            owned = away == 0
            counter += 0 if owned else 1
            x0, x1, z0, z1 = cuts[0][xi], cuts[0][xi + 1], cuts[1][zi], cuts[1][zi + 1]
            parcels.append({
                "id": "home" if owned else f"parcel_{counter}",
                "name": "Home ground" if owned else f"Parcel {counter}",
                "requiredLevel": 0 if owned else 3 + 2 * (counter - 1),
                "priceCents": 0 if owned else 4_000_000 + 2_000_000 * (counter - 1),
                "ownedAtStart": owned,
                "description": "Where you start." if owned else "More of the mountain.",
                "outline": [cm(p) for p in [(x0, z0), (x1, z0), (x1, z1), (x0, z1)]],
            })
    return parcels


def write_scenario(map_id, title, place, square, arrival, commands, parcels, stats, has_lifts):
    """data/scenarios/<id>.json: the Starter Valley career's rules (comments kept) with this place's map."""
    text = (ROOT / "data" / "scenarios" / "starter_valley.json").read_text()

    def between(start, end, new):
        nonlocal text
        a = text.index(start)
        b = text.index(end, a) if end else len(text)
        text = text[:a] + new + text[b:]

    menu = {"title": title, "blurb": f"A real place: {place}. Only what really stands there is built: the roads and gravel tracks"
            + (" and the lifts" if has_lifts else ", no lifts") + f". 75,000 € to start, enough for a first T-bar, and the first trails are yours to draw.",
            "defaultParkName": f"{title} Bikepark", "credits": ATTRIBUTION}
    between('  "id": "starter_valley"', '  "seed":',
            f'  "id": {json.dumps(map_id)},\n  "name": {json.dumps(title)},\n'
            f'  "description": {json.dumps("Career on a real place: " + place + ".")},\n'
            f'  "menu": {json.dumps(menu, ensure_ascii=False)},\n')
    between('  "parkName":', '  "entryFeeCents":', f'  "parkName": {json.dumps(menu["defaultParkName"])},\n  "startingMoneyCents": {START_MONEY_CENTS},\n')
    between('  // Terrain is regenerated', '  "reputationRules"',
            '  // Real terrain from tools/terrain-import; roughness is the fine detail on top.\n'
            '  // The tree line is out of reach: forest and meadows come from OpenStreetMap.\n'
            f'  "terrain": {{\n    "sizeMeters": {square.size},\n    "heightmapFile": "../terrain/{map_id}.json",\n'
            '    "roughnessPermille": 90,\n    "treeLineCm": 900000\n  },\n  "terrainSculpt": [],\n')
    between('  "parcels": [', '  "welcome": {',
            '  "parcels": [\n' + ",\n".join("    " + json.dumps(p, ensure_ascii=False) for p in parcels) + "\n  ],\n")
    welcome = {"title": "Welcome to {park}!", "paragraphs": [
        f"This is a real place: {place}. Nothing has been made up here: what you see standing is what really stands there, and the gravel tracks are the real ones.",
        ("The lifts here are the real ones too. " if has_lifts else "There is no lift, so your guests pedal up the tracks and tire quickly. A T-bar (Build menu) is the first thing to save for: you have just enough money to order one on your own land. Link its valley station to a track with a gravel path (P), or nobody can reach it. ")
        + "Guests arrive on the track at the edge of your land.",
        "1. Hire a worker or two in the Crew menu (C). Your crew builds everything you plan.\n2. Press T to draw your first trail: start on a gravel track, wind across the slope and end on a track or lift further down. Press Enter and the crew gets to work.",
        "Steep real slopes rate trails harder, so use switchbacks. Every trail and feature earns XP. New park levels unlock harder features, more land and bigger lifts. Good luck!"]}
    between('  "welcome": {', '  "commands": [', '  "welcome": ' + json.dumps(welcome, ensure_ascii=False, indent=2).replace("\n", "\n  ") + ",\n")
    between('  "commands": [', None,
            '  "commands": [\n' + ",\n".join('    { "tick": 0, "command": ' + json.dumps(c) + " }" for c in commands) + "\n  ]\n}\n")
    path = ROOT / "data" / "scenarios" / f"{map_id}.json"
    path.write_text(text)
    return path


def make_map(square, cell, map_id, title, place, arrival):
    """Terrain and scenario for this square. Returns what happened, for the command line and the browser tool."""
    elevation, summary = write_terrain(square, cell, map_id)
    elements = fetch_infrastructure(square)
    lifts, parking = real_structures(elements, square, elevation)
    commands, stats = scenario_commands(road_network(elements, square), lifts, parking, arrival, "alpine_lifts",
                                        lambda p: elevation.at(*square.latlon(*p)))
    home = stats["arrival"] or arrival
    write_scenario(map_id, title, place, square, home, commands, default_parcels(square.size, home), stats,
                   any(c["type"] == "buildLift" for c in commands))
    stats.update({"terrain": summary, "lifts": sum(c["type"] == "buildLift" for c in commands),
                  "parking": sum(c["type"] == "buildParkingLot" for c in commands)})
    return stats


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lat", type=float)
    ap.add_argument("--lon", type=float)
    ap.add_argument("--peak", help="centre on this OSM peak (needs --near lat,lon)")
    ap.add_argument("--near")
    ap.add_argument("--offset", default="0,0", help="move the centre, meters east,south")
    ap.add_argument("--size", type=int, default=1500)
    ap.add_argument("--cell", type=int, default=5)
    ap.add_argument("--id", required=True)
    ap.add_argument("--scenario", action="store_true", help="also write data/scenarios/<id>.json")
    ap.add_argument("--title", help="the map's name (default: the id)")
    ap.add_argument("--place", help="what the place is, for the texts")
    ap.add_argument("--arrival", default=None, help="map meters x,z near where guests should arrive (default: the centre)")
    args = ap.parse_args()
    if args.size % args.cell:
        sys.exit("--size must be a multiple of --cell")

    if args.peak:
        if not args.near:
            sys.exit("--peak needs --near lat,lon")
        lat, lon = find_peak(args.peak, tuple(float(v) for v in args.near.split(",")))
        print(f"{args.peak}: {lat:.5f}, {lon:.5f}")
    elif args.lat is not None and args.lon is not None:
        lat, lon = args.lat, args.lon
    else:
        sys.exit("Give --lat and --lon, or --peak and --near")

    east, south = (float(v) for v in args.offset.split(","))
    square = Square(lat - south / M_PER_DEG_LAT, lon + east / (M_PER_DEG_LAT * math.cos(math.radians(lat))), args.size)
    if not args.scenario:
        write_terrain(square, args.cell, args.id)
        return
    arrival = tuple(float(v) for v in args.arrival.split(",")) if args.arrival else (args.size / 2, args.size / 2)
    title = args.title or args.id.replace("_", " ").title()
    print(json.dumps(make_map(square, args.cell, args.id, title, args.place or title, arrival), indent=1))


if __name__ == "__main__":
    main()
