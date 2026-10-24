#!/usr/bin/env python3
"""Contacts send-speed driver (docs/contacts-oconnect-speed-guide.md Phase 0).

Stages a file, sends it to a Contacts device through the bridge API, waits for the
result and prints the send's timeline straight out of sender.log. Scenarios:

  cold             force-stop OShare on the receiver (adb), stage, send
  warm             stage + send immediately after the previous run (< 10 s gap)
  stage-then-send  exactly the cold flow but with the send issued < 1 s after staging
  idle             120 s gap between runs, no force-stop
  back-to-back     repeated warm sends, no gap

Usage:
  python scripts/contacts-speed-test.py --token TOKEN --device "一加平板" \
      [--file C:/path/small.bin] [--runs 5] [--mode cold] [--serial YDQ...] [--idle 120]
"""
import argparse
import json
import subprocess
import sys
import time
import urllib.request

LOG = None  # %LOCALAPPDATA%/OSharePC/sender.log


def api(base, token, method, path, body=None, timeout=20):
    req = urllib.request.Request(base + path, method=method)
    req.add_header("X-OSharePC-Bridge-Token", token)
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, data=data, timeout=timeout) as r:
            raw = r.read().decode()
            return r.status, json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode() or "{}")


def adb(serial, *args):
    cmd = ["adb"]
    if serial:
        cmd += ["-s", serial]
    cmd += list(args)
    return subprocess.run(cmd, capture_output=True, text=True, timeout=30)


def log_size():
    return LOG.stat().st_size if LOG.exists() else 0


def read_new_send_summary(offset):
    """The last TIMING[send -> ...] SUMMARY line after `offset` plus its per-mark lines."""
    with open(LOG, "r", encoding="utf-8", errors="replace") as f:
        f.seek(offset)
        text = f.read()
    import re
    marks = {}
    for m in re.finditer(r"TIMING\[send -> [^\]]+\] \+(\d+)ms (\S+)", text):
        marks[m.group(2)] = int(m.group(1))
    s = re.findall(r"TIMING\[send -> [^\]]+\] SUMMARY (\w+) total=(\d+)ms \| (.*)", text)
    summary = s[-1] if s else None
    return marks, summary, text


def run_once(base, token, device_name, file, serial, mode, idle_secs, index):
    pre = f"[{mode} #{index + 1}]"
    if mode == "cold" or mode == "stage-then-send":
        r = adb(serial, "shell", "am", "force-stop", "com.coloros.oshare")
        if r.returncode != 0:
            print(f"{pre} WARN force-stop failed: {r.stderr.strip()}")
        time.sleep(1.5)
    elif mode == "idle" and index > 0:
        print(f"{pre} idling {idle_secs}s…")
        time.sleep(idle_secs)

    status, devices = api(base, token, "GET", "/api/devices", timeout=10)
    if status != 200:
        print(f"{pre} FAIL list devices: {status}")
        return False
    target = next((d for d in devices if device_name in d.get("name", "")), None)
    if target is None:
        print(f"{pre} FAIL device '{device_name}' not visible")
        return False

    offset = log_size()
    t0 = time.time()

    status, staged = api(base, token, "POST", "/api/stage", {"files": [file]})
    if status != 200 or not staged.get("taskId"):
        print(f"{pre} FAIL stage: {status} {staged}")
        return False
    task_id = staged["taskId"]

    if mode != "stage-then-send":
        time.sleep(max(0.0, {"cold": 2.0, "warm": 0.2, "idle": 0.2, "back-to-back": 0.2}[mode]))

    status, resp = None, None
    for attempt in range(4):
        status, resp = api(base, token, "POST", "/api/send",
                           {"address": target["address"], "taskId": task_id}, timeout=20)
        if status != 409:
            break
        time.sleep(2)  # a previous send is still winding down
    if status != 202:
        print(f"{pre} FAIL send: {status} {resp}")
        return False

    # wait for completion via /api/events, starting from the current seq (avoids stale events)
    status, st = api(base, token, "GET", "/api/status", timeout=10)
    seq = st.get("seq", 0) if status == 200 else 0
    outcome = None
    deadline = time.time() + 120
    while time.time() < deadline:
        status, events = api(base, token, "GET", f"/api/events?since={seq}", timeout=10)
        if status == 200:
            for e in events if isinstance(events, list) else events.get("events", []):
                seq = max(seq, e.get("seq", 0))
                if e.get("type") == "sendCompleted":
                    outcome = "ok"
                elif e.get("type") == "sendFailed":
                    outcome = "fail:" + str(e.get("data", {}).get("error", ""))
        if outcome:
            break
        time.sleep(0.3)

    wall = time.time() - t0
    if outcome is None:
        print(f"{pre} TIMEOUT waiting for outcome (wall {wall:.1f}s)")
        return False

    time.sleep(0.4)  # let the SUMMARY line flush
    marks, summary, _ = read_new_send_summary(offset)
    hs = marks.get("handshake-read")
    waited = next((v for k, v in marks.items() if k.startswith("send-waited-for-prewarm")), None)
    total = int(summary[1]) if summary else -1
    outcome_s = summary[0] if summary else "?"
    print(f"{pre} {outcome_s} total={total}ms handshake-read={hs}ms "
          f"waited-for-prewarm={waited}ms wall={wall:.1f}s")
    if summary and (outcome != "ok" or total > 10000):
        print(f"        segments: {summary[2]}")
    return outcome == "ok"


def main():
    global LOG
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8960")
    ap.add_argument("--token", required=True)
    ap.add_argument("--device", required=True, help="substring of the device name")
    ap.add_argument("--file", default="C:/Users/Anton/Downloads/contacts-speed-test.bin")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--mode", default="cold",
                    choices=["cold", "warm", "idle", "stage-then-send", "back-to-back"])
    ap.add_argument("--serial", default="", help="adb serial of the receiver")
    ap.add_argument("--idle", type=int, default=120)
    ap.add_argument("--make-file-mb", type=float, default=0.5)
    args = ap.parse_args()

    import os, pathlib
    LOG = pathlib.Path(os.environ["LOCALAPPDATA"]) / "OSharePC" / "sender.log"
    p = pathlib.Path(args.file)
    if not p.exists():
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(os.urandom(int(args.make_file_mb * 1024 * 1024)))
        print(f"made test file {p}")

    ok = 0
    for i in range(args.runs):
        if run_once(args.base, args.token, args.device, str(p).replace("\\", "/"),
                    args.serial, args.mode, args.idle, i):
            ok += 1
    print(f"== {args.mode}: {ok}/{args.runs} succeeded")


if __name__ == "__main__":
    main()
