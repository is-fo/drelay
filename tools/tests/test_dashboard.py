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
        "_comment": [
            "written by tools/tests/test_dashboard.py",
            "the relay now writes settings back into this file, so the test keeps a comment block",
            "and an unknown key here to prove neither is lost by a settings write"
        ],
        "listenHost": "127.0.0.1",
        "logDirectory": str(ROOT / "work" / "logs" / "dashboard-test"),
        "web": {"host": "127.0.0.1", "port": web_port},
        "somethingFromTheFuture": {"keep": [1, 2]},
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
                       "id=\"nxApply\"", "id=\"stApply\"", "id=\"stEffects\"", "id=\"stripPill\"",
                       "/api/state", "/api/events", "/api/nexus", "/api/strip"):
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
        for key in ("health", "nexus", "strip", "counters", "filters", "log", "web", "sessions",
                    "primary"):
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
        # The strip ships on by default with Confused and Hallucinating armed: a page that showed it as
        # off, or a relay that reported an empty armed set, would be the difference between a protected
        # run and a run where the debuff simply never arrived. Order is asserted because the startup
        # line and the panel pill both render the set in iteration order.
        strip_state = state.get("strip", {})
        if not isinstance(strip_state.get("config"), dict):
            failures.append("/api/state's strip.config is not an object: %r" % strip_state)
        elif strip_state["config"].get("effects") != [11, 16]:
            failures.append("the strip is not on by default with Confused and Hallucinating armed: %r"
                            % strip_state["config"])
        elif not strip_state.get("active"):
            failures.append("the strip reports itself inactive while it is armed: %r" % strip_state)
        elif [entry.get("name") for entry in strip_state.get("named", [])] != \
                ["confused", "paralyzed", "slowed", "hallucinating"]:
            failures.append("the strip's named effect list changed: %r" % strip_state.get("named"))
        elif [entry.get("armed") for entry in strip_state.get("named", [])] != \
                [True, False, False, True]:
            failures.append("the wrong default effects are armed: %r" % strip_state.get("named"))
        elif any(entry.get("armed") for entry in strip_state.get("named", [])
                 if entry.get("name") in ("paralyzed", "slowed")):
            failures.append("an effect in the movement law is armed by default: %r"
                            % strip_state.get("named"))
        print("   /api/state: nexus.config is an object, strip armed with %s, port=%s"
              % (strip_state.get("config", {}).get("effects"), state["web"].get("port")))

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

        # The bracket regression. The page keeps its filters as JSON and posts them back as JSON, so
        # the endpoint receives arrays; it used to stringify them, and every save then added a layer
        # of brackets to every packet name ("[]" -> "[[]]" -> "[[[]]]") until nothing matched.
        # Two round trips must be a no-op, in content and in type.
        posted = [{"name": "hp only", "enabled": True, "kinds": ["health", "nexus"],
                   "packets": ["HealthUpdate", "MapInfo"], "sessions": []}]
        for round_trip in (1, 2):
            status, updated = post(base + "/api/filters", {"filters": posted})
            entry = (updated.get("filters") or [{}])[0]
            if entry.get("packets") != ["HealthUpdate", "MapInfo"]:
                failures.append("filter packets changed on round trip %d: %r"
                                % (round_trip, entry.get("packets")))
            if entry.get("kinds") != ["health", "nexus"]:
                failures.append("filter kinds changed on round trip %d: %r"
                                % (round_trip, entry.get("kinds")))
            if any("[" in name or "]" in name for name in entry.get("packets") or []):
                failures.append("a bracket survived into a packet name on round trip %d: %r"
                                % (round_trip, entry.get("packets")))
        print("   an array-valued filter list survives two round trips unchanged")

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

        print("== settings are written back to the route table")
        persistence = applied.get("persistence", {})
        if not persistence.get("saved"):
            failures.append("posting to /api/nexus did not save the route table: %r" % persistence)
        elif str(config) not in str(persistence.get("file")):
            failures.append("the settings were saved to %r, expected %s"
                            % (persistence.get("file"), config))
        saved = json.loads(config.read_text(encoding="utf-8"))
        if saved.get("autoNexus", {}).get("thresholdPercent") != 55:
            failures.append("the route table does not carry the new threshold: %r" % saved.get("autoNexus"))
        # Everything the writer was not asked to change has to still be there: a settings write that
        # dropped a comment block, a route or an unknown key would corrupt the file the relay starts from.
        if len(saved.get("_comment", [])) != 3:
            failures.append("the settings write lost the comment block: %r" % saved.get("_comment"))
        if saved.get("somethingFromTheFuture") != {"keep": [1, 2]}:
            failures.append("the settings write lost an unknown key: %r" % saved.get("somethingFromTheFuture"))
        if (saved.get("web") or {}).get("port") != web_port:
            failures.append("the settings write changed the web port: %r" % saved.get("web"))
        if len(saved.get("routes") or []) != 1:
            failures.append("the settings write lost the routes: %r" % saved.get("routes"))
        print("   auto-nexus saved to %s with the comment, routes and unknown keys intact"
              % persistence.get("file"))

        print("== the strip module's endpoint")
        status, raw = get(base + "/api/strip")
        strip_view = json.loads(raw)
        if strip_view.get("config", {}).get("effects") != [11, 16]:
            failures.append("GET /api/strip does not report the default armed set: %r" % strip_view)
        status, applied = post(base + "/api/strip", {"enabled": True, "effects": [6, 7, 11],
                                                     "min_votes": 4})
        strip_config = applied.get("config", {})
        if sorted(strip_config.get("effects") or []) != [6, 7, 11]:
            failures.append("posting an effect set did not take effect: %r" % strip_config)
        if strip_config.get("minVotes") != 4:
            failures.append("posting min_votes did not take effect: %r" % strip_config)
        saved = json.loads(config.read_text(encoding="utf-8"))
        if sorted(saved.get("strip", {}).get("effects") or []) != [6, 7, 11]:
            failures.append("the route table does not carry the armed effects: %r" % saved.get("strip"))
        if saved.get("strip", {}).get("minVotes") != 4:
            failures.append("the route table does not carry minVotes: %r" % saved.get("strip"))
        # Disarming everything is a state the page offers, and it has to persist as an empty list
        # rather than as a missing key the next start would read as "use the default".
        status, applied = post(base + "/api/strip", {"enabled": True, "effects": []})
        saved = json.loads(config.read_text(encoding="utf-8"))
        if saved.get("strip", {}).get("effects") != []:
            failures.append("disarming every effect did not persist as an empty list: %r"
                            % saved.get("strip"))
        if applied.get("config", {}).get("enabled") is not True:
            failures.append("the strip's master switch changed while disarming effects: %r"
                            % applied.get("config"))
        # Back to the default so a later step sees what a fresh install would.
        post(base + "/api/strip", {"enabled": True, "effects": [11, 16], "min_votes": 3})
        print("   strip settings applied and saved: 6,7,11 with minVotes 4, then disarmed")

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
