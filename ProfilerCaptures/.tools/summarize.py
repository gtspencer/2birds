import argparse
import csv
import json
import math
from pathlib import Path


def statistics(values):
    ordered = sorted(values)

    def percentile(fraction):
        position = (len(ordered) - 1) * fraction
        index = int(position)
        return ordered[index] + (ordered[min(index + 1, len(ordered) - 1)] - ordered[index]) * (position - index)

    return dict(median=percentile(.5), p95=percentile(.95), p99=percentile(.99), maximum=max(ordered), total=sum(ordered))


def main():
    parser = argparse.ArgumentParser(description='Summarize one recording, deduplicating overlapping CSV exports by recording ID and frame timestamp.')
    parser.add_argument('captures', nargs='+', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--start-seconds', type=float, default=0, help='Interval start relative to the earliest frame across supplied CSVs.')
    parser.add_argument('--duration-seconds', type=float, help='Interval length; omitted means through the final supplied frame.')
    parser.add_argument('--long-frame-ms', type=float, default=20)
    args = parser.parse_args()
    for value in (args.start_seconds, args.duration_seconds, args.long_frame_ms):
        if value is not None and (not math.isfinite(value) or value < 0):
            parser.error('Interval and threshold values must be finite and nonnegative.')
    if args.duration_seconds == 0 or args.long_frame_ms == 0:
        parser.error('Duration and long-frame threshold must be positive.')
    if args.output.exists():
        parser.error('Output already exists; choose a fresh filename.')

    frames = {}
    duplicates = 0
    ids = set()
    for path in args.captures:
        with path.open(encoding='utf-8-sig', newline='') as source:
            for row in csv.DictReader(source):
                recording_id = row.pop('recordingId')
                ids.add(recording_id)
                timestamp = int(row.pop('startNs'))
                frame = int(row.pop('frame'))
                values = {name: float(value) for name, value in row.items()}
                key = (recording_id, timestamp)
                if key in frames:
                    duplicates += 1
                    continue
                frames[key] = dict(startNs=timestamp, frame=frame, source=str(path), **values)
    if len(ids) != 1:
        parser.error('Supply nonempty exports from one recording only; summarize independent runs separately.')
    rows = sorted(frames.values(), key=lambda row: row['startNs'])
    start = rows[0]['startNs'] + round(args.start_seconds * 1e9)
    end = None if args.duration_seconds is None else start + round(args.duration_seconds * 1e9)
    selected = [row for row in rows if row['startNs'] >= start and (end is None or row['startNs'] < end)]
    if not selected:
        parser.error('No frames fall within the requested interval.')
    covered_ms = sum(row['mainThreadMs'] for row in selected)
    elapsed_ms = (selected[-1]['startNs'] - selected[0]['startNs']) / 1e6 + selected[-1]['mainThreadMs']
    gaps = [dict(afterSource=a['source'], afterFrame=a['frame'], gapMs=gap)
            for a, b in zip(selected, selected[1:])
            if (gap := (b['startNs'] - a['startNs']) / 1e6 - a['mainThreadMs']) > 1]
    metrics = {key: statistics([row[key] for row in selected]) for key in (
        'mainThreadMs', 'playerLoopMs', 'waitForTargetFPSMs', 'presentWaitMs', 'physicsMs', 'gcAllocBytes', 'gcCollectMs')}
    metrics['loopMinusNamedWaitsMs'] = statistics([
        max(0, row['playerLoopMs'] - row['waitForTargetFPSMs'] - row['presentWaitMs']) for row in selected])
    result = dict(recordingId=next(iter(ids)), inputs=[str(path) for path in args.captures], frames=len(selected),
                  duplicateFramesRemoved=duplicates, requestedStartSeconds=args.start_seconds,
                  requestedDurationSeconds=args.duration_seconds, firstStartNs=selected[0]['startNs'], lastStartNs=selected[-1]['startNs'],
                  elapsedSeconds=elapsed_ms / 1000, coveredFrameSeconds=covered_ms / 1000, gaps=gaps,
                  mainThreadAllocationBytesPerCoveredSecond=metrics['gcAllocBytes']['total'] / (covered_ms / 1000) if covered_ms else None,
                  longFrameThresholdMs=args.long_frame_ms, longFrames=sum(row['mainThreadMs'] > args.long_frame_ms for row in selected),
                  metrics=metrics, longestFrames=sorted(selected, key=lambda row: row['mainThreadMs'], reverse=True)[:5],
                  limitations='Main-thread samples only. Loop remainder still includes other waits. Rates use captured frame time; inspect gaps and interval coverage. Long-frame threshold is not a missed-deadline count.')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('x', encoding='utf-8') as destination:
        json.dump(result, destination, indent=2)
    print(json.dumps(dict(output=str(args.output), frames=len(selected), duplicatesRemoved=duplicates, gaps=len(gaps))))


if __name__ == '__main__':
    main()
