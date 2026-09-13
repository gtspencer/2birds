"""Summarize client tick-matched reconcile errors after a five-second route warm-up."""
import argparse
import csv
import json
from pathlib import Path


def percentile(values, quantile):
    return sorted(values)[min(len(values) - 1, int((len(values) - 1) * quantile))] if values else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    summaries = []
    for path in sorted(args.directory.rglob("player-*.csv")):
        with path.open() as stream:
            rows = list(csv.DictReader(stream))
        if not rows:
            continue
        start = float(rows[0]["time"]) + 5
        samples = [r for r in rows if r["kind"] == "reconcile"
                   and r["isServer"] == "False" and float(r["time"]) >= start]
        ticks = [r for r in rows if r["kind"] == "tick" and float(r["time"]) >= start]
        errors = [float(r["errorM"]) for r in samples]
        tick_span = float(ticks[-1]["time"]) - float(ticks[0]["time"]) if len(ticks) > 1 else 0
        tail_start = float(samples[-1]["time"]) - .5 if samples else 0
        summaries.append({
            "file": str(path.relative_to(args.directory)), "samples": len(errors),
            "role": "server" if rows[0]["isServer"] == "True" else ("owner" if rows[0]["isOwner"] == "True" else "spectator"),
            "error_p95_m": percentile(errors, .95), "error_p99_m": percentile(errors, .99),
            "error_max_m": max(errors, default=None),
            "replay_correction_max_m": max((float(r["postReplayM"]) for r in samples), default=None),
            "tick_cpu_p95_ms": percentile([float(r["tickMs"]) for r in ticks], .95),
            "replay_cpu_p95_ms": percentile([float(r["replayMs"]) for r in samples], .95),
            "tick_hz": (int(ticks[-1]["localTick"]) - int(ticks[0]["localTick"])) / tick_span if tick_span else None,
            "rtt_median_ms": percentile([float(r["rttMs"]) for r in samples], .5),
            "tail_error_max_m": max((float(r["errorM"]) for r in samples if float(r["time"]) >= tail_start), default=None),
            "reset_revision": max((int(r["resetRevision"]) for r in rows), default=0),
        })
    print(json.dumps(summaries, indent=2))


if __name__ == "__main__":
    main()
