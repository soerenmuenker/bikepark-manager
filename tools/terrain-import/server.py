#!/usr/bin/env python3
"""Map maker: pick a square of the real world on OpenStreetMap, see what really stands there, make it a career map.

    python3 tools/terrain-import/server.py      # then open http://localhost:8767

Python stdlib + curl; the page loads Leaflet from a CDN and tiles from OpenStreetMap / OpenTopoMap.
"""
import base64
import contextlib
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location("terrain_import", HERE / "import.py")
ti = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ti)
PORT = 8767


def preview(body):
    """What the importer would take from this square, as lat/lon for the map."""
    square = ti.Square(float(body["lat"]), float(body["lon"]), int(body["size"]))
    elements = ti.fetch_infrastructure(square)
    edges = ti.road_network(elements, square)
    minor = []
    for el in elements:
        tags = el.get("tags", {})
        if el["type"] == "way" and tags.get("highway") and not ti.is_major(tags) and el.get("geometry"):
            minor.append([[p["lat"], p["lon"]] for p in el["geometry"]])
    junctions = {}
    for e in edges:
        junctions.setdefault(e[0], e[2][0])
        junctions.setdefault(e[1], e[2][-1])
    lifts, parking = ti.real_structures(elements, square, ti.Elevation())
    scenery = ti.scenery_from(elements, square, [])
    ll = lambda p: list(square.latlon(p["x"] / 100, p["z"] / 100))
    corners = []
    for b in scenery["buildings"]:
        lx, lz = b["halfLengthCm"] / 100 * b["dirX"] / 1000, b["halfLengthCm"] / 100 * b["dirZ"] / 1000
        wx, wz = -b["halfWidthCm"] / 100 * b["dirZ"] / 1000, b["halfWidthCm"] / 100 * b["dirX"] / 1000
        cx, cz = b["centerX"] / 100, b["centerZ"] / 100
        corners.append([list(square.latlon(cx + sl * lx + sw * wx, cz + sl * lz + sw * wz)) for sl, sw in ((1, 1), (1, -1), (-1, -1), (-1, 1))])
    return {
        "routes": [[list(square.latlon(*p)) for p in e[2]] for e in edges if e[3] == "gravel"],
        "roads": [[list(square.latlon(*p)) for p in e[2]] for e in edges if e[3] == "tarmac"],
        "minor": minor,
        "junctions": [list(square.latlon(*p)) for p in junctions.values()],
        "lifts": [{"name": l["name"], "type": l["typeId"], "line": [list(square.latlon(*l["valley"])), list(square.latlon(*l["mountain"]))]} for l in lifts],
        "parking": [{"name": p["name"], "at": list(square.latlon(*p["center"]))} for p in parking],
        "streams": [{"line": [ll(p) for p in s["points"]], "width": s["widthCm"]} for s in scenery["streams"]],
        "lakes": [[ll(p) for p in l["outline"]] for l in scenery["lakes"]],
        "buildings": corners,
    }


def create(body):
    map_id = body["id"]
    if not re.fullmatch(r"[a-z][a-z0-9_]{1,40}", map_id):
        raise ValueError("The id may only use lower case letters, digits and _.")
    if map_id in ("starter_valley", "demo_valley"):
        raise ValueError("That id belongs to a built-in map.")
    if (ROOT / "data" / "scenarios" / f"{map_id}.json").exists() and not body.get("overwrite"):
        return {"exists": True}
    square = ti.Square(float(body["lat"]), float(body["lon"]), int(body["size"]))
    arrival = square.xz(*body["arrival"]) if body.get("arrival") else (square.size / 2, square.size / 2)
    log = io.StringIO()
    with contextlib.redirect_stdout(log):
        stats = ti.make_map(square, 5, map_id, body["title"], body.get("place") or body["title"], arrival)
    stats["arrival"] = [round(v) for v in stats["arrival"]] if stats["arrival"] else None

    # The game's own validators have the last word.
    png = ROOT / "out" / f"{map_id}_map.png"
    run = subprocess.run(["dotnet", "run", "--project", "src/Bikepark.SimRunner", "--", "terrain", "--scenario", f"data/scenarios/{map_id}.json",
                          "--out", str(png), "--mode", "relief"], cwd=ROOT, capture_output=True, text=True,
                         env={**os.environ, "PATH": "/usr/local/share/dotnet:" + os.environ.get("PATH", "")})
    if run.returncode != 0:
        raise ValueError("The game could not load the map:\n" + run.stderr[-800:])
    result = json.loads(run.stdout[run.stdout.index("{"):])
    stats["rejected"] = [str(r) for r in result.get("rejectedCommands", [])]
    stats["waysBuilt"] = len(result.get("ways", []))
    stats["meters"] = sum(w["lengthMeters"] for w in result.get("ways", []))
    stats["image"] = base64.b64encode(png.read_bytes()).decode()
    stats["scenario"] = f"data/scenarios/{map_id}.json"
    return stats


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, payload, kind="application/json"):
        data = payload if isinstance(payload, bytes) else json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path in ("/", "/index.html"):
            self._send(200, (HERE / "index.html").read_bytes(), "text/html; charset=utf-8")
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        routes = {"/api/preview": preview, "/api/create": create}
        if self.path not in routes:
            return self._send(404, {"error": "not found"})
        try:
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            self._send(200, routes[self.path](body))
        except (ValueError, KeyError) as e:
            self._send(400, {"error": str(e)})
        except SystemExit as e:
            self._send(502, {"error": str(e)})
        except Exception as e:  # the page shows it instead of an empty reply
            self._send(500, {"error": f"{type(e).__name__}: {e}"})

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    print(f"Map maker: http://localhost:{PORT}")
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
