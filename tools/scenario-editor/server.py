#!/usr/bin/env python3
"""Scenario map editor: edit a scenario's land parcels, its tick-0 structures (lifts, parking lots, ways) and the terrain
(raise / lower / paint strokes) on the 2D map.

Usage (from the repo root):
    python3 tools/scenario-editor/server.py [data/scenarios/starter_valley.json] [--port 8765]
then open http://localhost:8765

The backdrop is rendered by the SimRunner `terrain` subcommand (--bare). Saving rewrites only the "parcels",
"commands" and "terrainSculpt" blocks of the scenario file (comments elsewhere are kept), then runs the same command to validate: rejected
scenario commands (a lift on too steep ground, ...) are listed in the editor. Tooling only, nothing here feeds the sim.
"""

import json
import os
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
OUT = ROOT / "out" / "editor"
DOTNET = "/usr/local/share/dotnet/dotnet" if Path("/usr/local/share/dotnet/dotnet").exists() else "dotnet"
RUNNER = ["run", "--project", str(ROOT / "src" / "Bikepark.SimRunner"), "--no-build", "--"]
lock = threading.Lock()


# ---------------------------------------------------------------- JSON with comments

def _skip_string(text, i):
    """text[i] is the opening quote; returns the index after the closing quote."""
    i += 1
    while text[i] != '"':
        i += 2 if text[i] == "\\" else 1
    return i + 1


def strip_comments(text):
    out, i = [], 0
    while i < len(text):
        c = text[i]
        if c == '"':
            j = _skip_string(text, i)
            out.append(text[i:j])
            i = j
        elif text.startswith("//", i):
            i = text.find("\n", i)
            i = len(text) if i < 0 else i
        elif text.startswith("/*", i):
            i = text.index("*/", i) + 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def find_top_level_value(text, key):
    """(start, end) of the value of a top-level key (depth 1), skipping strings and comments."""
    depth, i = 0, 0
    while i < len(text):
        c = text[i]
        if c == '"':
            j = _skip_string(text, i)
            if depth == 1 and text[i + 1:j - 1] == key:
                k = j
                while text[k] in " \t\r\n:":
                    k += 1
                return k, _value_end(text, k)
            i = j
        elif text.startswith("//", i):
            i = text.find("\n", i)
        elif text.startswith("/*", i):
            i = text.index("*/", i) + 2
        else:
            if c in "[{":
                depth += 1
            elif c in "]}":
                depth -= 1
            i += 1
    return None


def _value_end(text, i):
    depth = 0
    while True:
        c = text[i]
        if c == '"':
            i = _skip_string(text, i)
            if depth == 0:
                return i
            continue
        if text.startswith("//", i):
            i = text.find("\n", i)
            continue
        if c in "[{":
            depth += 1
        elif c in "]}":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1


# ---------------------------------------------------------------- formatting (matches the hand-written style)

def dump(v):
    if isinstance(v, dict) and set(v) == {"x", "z"}:
        return '{"x": %d, "z": %d}' % (v["x"], v["z"])
    if isinstance(v, list):
        return "[" + ", ".join(dump(e) for e in v) + "]"
    if isinstance(v, dict):
        return "{ " + ", ".join(f"{json.dumps(k)}: {dump(e)}" for k, e in v.items()) + " }"
    return json.dumps(v, ensure_ascii=False)


def format_parcels(parcels):
    lines = []
    for p in parcels:
        head = ", ".join(f'"{k}": {dump(p[k])}' for k in ("id", "name", "requiredLevel", "priceCents", "ownedAtStart") if k in p)
        rest = [k for k in p if k not in ("id", "name", "requiredLevel", "priceCents", "ownedAtStart", "description", "outline")]
        head += "".join(f', "{k}": {dump(p[k])}' for k in rest)
        outline = ", ".join('{"x":%d, "z":%d}' % (q["x"], q["z"]) for q in p["outline"])
        lines.append(f"    {{ {head},\n      \"description\": {dump(p.get('description', ''))},\n      \"outline\": [{outline}] }}")
    return "[\n" + ",\n".join(lines) + "\n  ]"


def format_commands(commands):
    blocks = []
    for timed in commands:
        inner = []
        for k, v in timed.items():
            if k != "command":
                inner.append(f'      "{k}": {dump(v)}')
        cmd = timed["command"]
        scalars = [k for k, v in cmd.items() if not isinstance(v, (dict, list))]
        points = [k for k, v in cmd.items() if isinstance(v, dict)]
        lists = [k for k, v in cmd.items() if isinstance(v, list)]
        body = ["        " + ", ".join(f'"{k}": {dump(cmd[k])}' for k in scalars)]
        if points:
            body.append("        " + ", ".join(f'"{k}": {dump(cmd[k])}' for k in points))
        body += [f'        "{k}": {dump(cmd[k])}' for k in lists]
        inner.append('      "command": {\n' + ",\n".join(body) + "\n      }")
        blocks.append("    {\n" + ",\n".join(inner) + "\n    }")
    return "[\n" + ",\n".join(blocks) + "\n  ]"


def format_sculpt(strokes):
    if not strokes:
        return "[]"
    lines = []
    for s in strokes:
        head = ", ".join(f'"{k}": {dump(v)}' for k, v in s.items() if k != "points")
        lines.append(f"    {{ {head},\n      \"points\": {dump(s['points'])} }}")
    return "[\n" + ",\n".join(lines) + "\n  ]"


SCULPT_COMMENT = ("  // Hand-made terrain changes from tools/scenario-editor (raise / lower / paint strokes), applied in order to the\n"
                  "  // generated terrain before the structures' pads.\n")


def patched_text(path, parcels, commands, sculpt):
    text = path.read_text()
    if find_top_level_value(text, "terrainSculpt") is None:
        if not sculpt:
            sculpt = None
        else:  # first strokes: add the block right after "terrain"
            span = find_top_level_value(text, "terrain")
            if span is None:
                raise ValueError('scenario has no top-level "terrain" block')
            text = text[:span[1]] + ',\n' + SCULPT_COMMENT + '  "terrainSculpt": []' + text[span[1]:]
    # Replace from the back so the first range stays valid.
    edits = []
    blocks = [("parcels", format_parcels(parcels)), ("commands", format_commands(commands))]
    if sculpt is not None:
        blocks.append(("terrainSculpt", format_sculpt(sculpt)))
    for key, value in blocks:
        span = find_top_level_value(text, key)
        if span is None:
            raise ValueError(f'scenario has no top-level "{key}" block')
        edits.append((span, value))
    for (start, end), value in sorted(edits, key=lambda e: -e[0][0]):
        text = text[:start] + value + text[end:]
    json.loads(strip_comments(text))  # still valid
    return text


# ---------------------------------------------------------------- SimRunner

def run_terrain(scenario_path, out_png, bare):
    args = [DOTNET, *RUNNER, "terrain", "--scenario", str(scenario_path), "--out", str(out_png), "--mode", "relief"]
    if bare:
        args.append("--bare")
    proc = subprocess.run(args, cwd=ROOT, capture_output=True, text=True)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr.strip() or proc.stdout.strip())
    return json.loads(proc.stdout)


def render_backdrop(scenario_path, mode):
    OUT.mkdir(parents=True, exist_ok=True)
    png = OUT / f"{scenario_path.stem}_bare_{mode}.png"
    args = [DOTNET, *RUNNER, "terrain", "--scenario", str(scenario_path), "--out", str(png), "--mode", mode, "--bare"]
    proc = subprocess.run(args, cwd=ROOT, capture_output=True, text=True)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr.strip() or proc.stdout.strip())
    return png


def validate(text, scenario_path):
    """Runs the terrain command on the given scenario text (next to the original, so catalog paths resolve)."""
    OUT.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".editor_", suffix=".json", dir=scenario_path.parent)
    try:
        with os.fdopen(fd, "w") as f:
            f.write(text)
        stats = run_terrain(Path(tmp), OUT / f"{scenario_path.stem}_preview.png", bare=False)
    finally:
        os.unlink(tmp)
    return {
        "rejectedCommands": stats.get("rejectedCommands", []),
        "lifts": stats.get("lifts", []),
        "hubs": stats.get("hubs", []),
        "ways": list(stats.get("ways", [])),
        "preview": f"/out/{scenario_path.stem}_preview.png",
    }


# ---------------------------------------------------------------- HTTP

class Handler(BaseHTTPRequestHandler):
    scenario = None  # Path

    def log_message(self, fmt, *args):
        pass

    def send(self, code, body, ctype="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urlparse(self.path)
        q = parse_qs(url.query)
        try:
            if url.path == "/":
                self.send(200, (HERE / "index.html").read_bytes(), "text/html; charset=utf-8")
            elif url.path == "/api/scenario":
                data = json.loads(strip_comments(self.scenario.read_text()))
                catalog = data.get("liftTypesFile")
                lift_types = []
                if catalog:
                    lift_types = json.loads(strip_comments((self.scenario.parent / catalog).read_text()))
                self.send(200, {
                    "path": str(self.scenario.relative_to(ROOT)),
                    "sizeMeters": data.get("terrain", {}).get("sizeMeters", 1000),
                    "parcels": data.get("parcels", []),
                    "commands": data.get("commands", []),
                    "terrainSculpt": data.get("terrainSculpt", []),
                    "liftTypes": lift_types,
                })
            elif url.path == "/api/raw":
                raw = OUT / f"{self.scenario.stem}_raw.bin"
                with lock:
                    if not raw.exists() or "fresh" in q:
                        OUT.mkdir(parents=True, exist_ok=True)
                        args = [DOTNET, *RUNNER, "terrain", "--scenario", str(self.scenario), "--raw", str(raw)]
                        proc = subprocess.run(args, cwd=ROOT, capture_output=True, text=True)
                        if proc.returncode != 0:
                            raise RuntimeError(proc.stderr.strip() or proc.stdout.strip())
                self.send(200, raw.read_bytes(), "application/octet-stream")
            elif url.path == "/api/map":
                mode = q.get("mode", ["relief"])[0]
                if mode not in ("relief", "slope", "surface"):
                    return self.send(400, {"error": "bad mode"})
                png = OUT / f"{self.scenario.stem}_bare_{mode}.png"
                with lock:
                    if not png.exists() or "fresh" in q:
                        render_backdrop(self.scenario, mode)
                self.send(200, png.read_bytes(), "image/png")
            elif url.path.startswith("/out/"):
                f = OUT / Path(url.path).name
                self.send(200, f.read_bytes(), "image/png") if f.exists() else self.send(404, {"error": "not found"})
            else:
                self.send(404, {"error": "not found"})
        except Exception as e:  # noqa: BLE001 - tooling: report everything to the page
            self.send(500, {"error": str(e)})

    def do_POST(self):
        try:
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            text = patched_text(self.scenario, body["parcels"], body["commands"], body.get("terrainSculpt", []))
            with lock:
                if self.path == "/api/validate":
                    self.send(200, validate(text, self.scenario))
                elif self.path == "/api/save":
                    result = validate(text, self.scenario)
                    self.scenario.write_text(text)
                    result["saved"] = str(self.scenario.relative_to(ROOT))
                    self.send(200, result)
                else:
                    self.send(404, {"error": "not found"})
        except Exception as e:  # noqa: BLE001
            self.send(500, {"error": str(e)})


def main():
    args = sys.argv[1:]
    port = 8765
    if "--port" in args:
        i = args.index("--port")
        port = int(args[i + 1])
        del args[i:i + 2]
    scenario = (ROOT / (args[0] if args else "data/scenarios/starter_valley.json")).resolve()
    Handler.scenario = scenario
    print("building SimRunner ...", flush=True)
    subprocess.run([DOTNET, "build", str(ROOT / "src" / "Bikepark.SimRunner"), "-v", "q", "-nologo"], cwd=ROOT, check=True,
                   stdout=subprocess.DEVNULL)
    try:
        server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    except OSError as e:
        sys.exit(f"port {port} is busy ({e.strerror}): is the editor already running? Open http://localhost:{port} "
                 f"or start another one with --port {port + 1}")
    print(f"editing {scenario.relative_to(ROOT)} - open http://localhost:{port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
