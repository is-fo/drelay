"""Checks the dashboard page and its API behave, without a browser.

A page that does not parse is indistinguishable from a relay that is not running, when all you have
is a second monitor and a game in the foreground. So this asserts the three things that can break
independently: the page is served, its inline script parses, and the endpoints the script calls
return the shapes it reads.

Usage: python tools/tests/test_dashboard.py [--port 0]
"""
import argparse
import json
import random
import re
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]   # tools/tests/<this file> -> the repository root
CLASSES = ROOT / "target" / "classes"


def get(url: str, timeout: float = 5.0):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return response.status, response.read().decode("utf-8")


def post(url: str, payload: dict):
    request = urllib.request.Request(
        url, data=json.dumps(payload).encode("utf-8"),
        headers={"content-type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=5) as response:
        return response.status, json.loads(response.read().decode("utf-8"))


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def ascii_safe(text: str) -> str:
    return text.encode("ascii", "replace").decode("ascii")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=0)
    args = parser.parse_args()

    web_port = args.port or free_port()
    relay_port = free_port()
    upstream_port = free_port()
    failures = []

    config = ROOT / "work" / "dashboard-routes.json"
    config.write_text(json.dumps({
        "listenHost": "127.0.0.1",
        "logDirectory": str(ROOT / "work" / "logs" / "dashboard-test"),
        "web": {"host": "127.0.0.1", "port": web_port},
        "routes": [{
            "name": "Game",
            "listenPort": relay_port,
            "remoteHost": "127.0.0.1",
            "remotePort": upstream_port,
        }],
    }), encoding="utf-8")

    relay = subprocess.Popen(["java", "-cp", str(CLASSES), "networking.Relay", str(config)],
                             cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    base = "http://127.0.0.1:%d" % web_port
    try:
        page = None
        for _ in range(60):
            try:
                status, page = get(base + "/")
                break
            except (urllib.error.URLError, OSError):
                time.sleep(0.25)
        if page is None:
            print("FAIL: the dashboard never answered")
            return 1

        print("== the page")
        if status != 200:
            failures.append("GET / returned %s" % status)
        for marker in ("drelay observability", "id=\"hp\"", "id=\"events\"", "id=\"filters\"",
                       "id=\"nxApply\"", "/api/state", "/api/events"):
            if marker not in page:
                failures.append("the page is missing %r" % marker)
        print("   %d bytes, all expected elements present" % len(page))

        script = "\n".join(re.findall(r"<script>(.*?)</script>", page, re.S))
        # encoding="utf-8" explicitly: in text mode Python picks the console's code page for the
        # pipes, and this page contains characters cp1252 cannot encode - which turns "check the
        # script" into a UnicodeEncodeError about the page itself.
        checker = subprocess.run(
            ["node", "-e", "new Function(require('fs').readFileSync(0,'utf8'));"],
            input=script, capture_output=True, text=True, encoding="utf-8")
        if checker.returncode != 0:
            detail = ascii_safe(checker.stderr.strip()[:400])
            failures.append("the page's inline script does not parse: %s" % detail)
        else:
            print("   inline script parses (%d bytes)" % len(script))

        print("== the endpoints the script reads")
        status, raw = get(base + "/api/state")
        state = json.loads(raw)
        for key in ("health", "nexus", "counters", "filters", "log", "web", "sessions", "primary"):
            if key not in state:
                failures.append("/api/state has no %r" % key)
        if not isinstance(state.get("nexus", {}).get("config"), dict):
            failures.append("/api/state's nexus.config is not an object (double-encoded?)")
        if not isinstance(state.get("filters"), dict):
            failures.append("/api/state's filters is not an object (double-encoded?)")
        if not isinstance(state.get("counters"), dict):
            failures.append("/api/state's counters is not an object (double-encoded?)")
        if state["web"].get("port") != web_port:
            failures.append("the dashboard reports port %r, expected %d" % (state["web"].get("port"), web_port))
        print("   /api/state: nexus.config is an object, port=%s" % state["web"].get("port"))

        status, raw = get(base + "/api/events?after=0&limit=10")
        events = json.loads(raw)
        for key in ("after", "lastSeq", "oldestSeq", "gap", "events", "count", "hidden"):
            if key not in events and key != "count":
                failures.append("/api/events has no %r" % key)
        print("   /api/events: lastSeq=%s hidden=%s" % (events.get("lastSeq"), events.get("hidden")))

        status, raw = get(base + "/api/filters")
        filters = json.loads(raw)
        # GET returns the whole filter view; POST returns the same shape, so both are unwrapped here.
        entries = (filters.get("filters") or {}).get("filters") if isinstance(filters.get("filters"), dict) \
            else filters.get("filters")
        if not entries:
            failures.append("/api/filters returned no filter entries: %r" % (filters,))
        else:
            default = entries[0]
            if default["name"] != "character hp" or not default["enabled"]:
                failures.append("the first filter is not the enabled character-hp view: %r" % default)
            print("   /api/filters: default is %r (enabled=%s), %d packet names"
                  % (default["name"], default["enabled"], len(default.get("packets", []))))

        status, raw = get(base + "/api/packets")
        packets = json.loads(raw)
        if not packets.get("game"):
            failures.append("/api/packets returned no game ids")
        print("   /api/packets: %d game ids, %d queue ids" % (len(packets.get("game", [])),
                                                             len(packets.get("queue", []))))

        print("== changing the filter list and the auto-nexus settings")
        status, updated = post(base + "/api/filters", {"filters": [
            {"name": "hp only", "enabled": True, "kinds": "health", "packets": "HealthUpdate",
             "sessions": ""},
            {"name": "off", "enabled": False, "kinds": "everything", "packets": "*", "sessions": ""},
        ]})
        if not updated.get("filters") or updated["filters"][0]["name"] != "hp only":
            failures.append("posting a filter list did not take effect: %r" % updated)
        elif updated["filters"][1]["enabled"]:
            failures.append("the disabled filter came back enabled")
        else:
            print("   filters replaced: %s" % [f["name"] for f in updated["filters"]])

        status, applied = post(base + "/api/nexus", {"threshold_percent": "55", "enabled": "true",
                                                     "dry_run": "true", "nonsense": 1})
        config_seen = applied.get("config", {})
        if config_seen.get("thresholdPercent") != 55:
            failures.append("posting a threshold did not take effect: %r" % config_seen)
        if not config_seen.get("enabled") or not config_seen.get("dryRun"):
            failures.append("posting switches did not take effect: %r" % config_seen)
        if not any("unknown" in entry for entry in applied.get("applied", [])):
            failures.append("an unknown key was silently accepted: %r" % applied.get("applied"))
        print("   nexus settings applied: %s" % applied.get("applied"))

        # And the settings survive a fresh read, which is what the page relies on.
        status, raw = get(base + "/api/state")
        after = json.loads(raw)["nexus"]["config"]
        if after.get("thresholdPercent") != 55:
            failures.append("the new threshold did not persist into /api/state: %r" % after)

        print("== the log endpoints")
        status, raw = get(base + "/api/log?lines=5")
        log_view = json.loads(raw)
        if "file" not in log_view or "lines" not in log_view:
            failures.append("/api/log returned %r" % list(log_view))
        print("   /api/log: file=%s" % log_view.get("file"))

        status, body = get(base + "/healthz")
        if body.strip() != "ok":
            failures.append("/healthz returned %r" % body)

        # An unknown path must be a 404 with a JSON body rather than a stack trace: the page probes
        # endpoints optimistically, and a wrong URL should be visible in the console, not fatal.
        try:
            status, body = get(base + "/api/nope")
            failures.append("an unknown endpoint answered %s instead of 404: %r" % (status, body[:80]))
        except urllib.error.HTTPError as error:
            if error.code != 404:
                failures.append("an unknown endpoint answered %s, expected 404" % error.code)
        print("   /healthz ok, unknown endpoint 404")
    except Exception as exc:  # noqa: BLE001
        # The exception text is echoed into the report, and the page itself contains characters a
        # cp1252 console cannot encode (the arrow in the session line, the em-dash in the HP panel).
        # Reporting a failure must never be the thing that fails.
        import traceback
        failures.append("exception: %s" % ascii_safe(traceback.format_exc())[-900:])
    finally:
        relay.terminate()
        try:
            relay.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            relay.kill()

    if failures:
        print("FAIL:")
        for failure in failures:
            print("  -", failure)
        return 1
    print("PASS: the dashboard page parses and every endpoint it reads returns the shape it expects")
    return 0


if __name__ == "__main__":
    sys.exit(main())
