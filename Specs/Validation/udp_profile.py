"""Loopback-only UDP shaper for one standalone client; delay/jitter are ONE WAY.

Example: python udp_profile.py --listen 7771 --target 7770 --delay 50 --jitter 10 --loss .01
Runs until Ctrl+C or --seconds expires. Shapes all datagrams, including reliable retransmissions.
"""
import argparse
import heapq
import random
import selectors
import socket
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--listen", type=int, default=7771)
    parser.add_argument("--target", type=int, default=7770)
    parser.add_argument("--delay", type=float, default=50)
    parser.add_argument("--jitter", type=float, default=10)
    parser.add_argument("--loss", type=float, default=.01)
    parser.add_argument("--seconds", type=float, default=160)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()
    if not (0 < args.listen < 65536 and 0 < args.target < 65536 and
            args.delay >= 0 and args.jitter >= 0 and 0 <= args.loss <= 1):
        parser.error("Invalid endpoint or network profile")
    rng = random.Random(args.seed)
    client = None
    queue = []
    sequence = 0
    dropped = 0
    forwarded = 0
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as front, \
            socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as back, \
            selectors.DefaultSelector() as selector:
        front.bind(("127.0.0.1", args.listen))
        back.bind(("127.0.0.1", 0))
        front.setblocking(False)
        back.setblocking(False)
        selector.register(front, selectors.EVENT_READ)
        selector.register(back, selectors.EVENT_READ)
        end = time.monotonic() + args.seconds
        while time.monotonic() < end:
            now = time.monotonic()
            while queue and queue[0][0] <= now:
                _, _, sock, destination, data = heapq.heappop(queue)
                sock.sendto(data, destination)
                forwarded += 1
            timeout = max(0, min(.01, queue[0][0] - now)) if queue else .01
            for key, _ in selector.select(timeout):
                data, source = key.fileobj.recvfrom(65535)
                if key.fileobj is front:
                    if client is not None and source != client:
                        continue
                    client = source
                    sock, destination = back, ("127.0.0.1", args.target)
                else:
                    if client is None or source != ("127.0.0.1", args.target):
                        continue
                    sock, destination = front, client
                if rng.random() < args.loss:
                    dropped += 1
                    continue
                delay = max(0, args.delay + rng.uniform(-args.jitter, args.jitter)) / 1000
                sequence += 1
                heapq.heappush(queue, (time.monotonic() + delay, sequence, sock, destination, data))
                if len(queue) > 10000:
                    raise RuntimeError("Shaper backlog exceeded 10000 datagrams")
    print(f"forwarded={forwarded} dropped={dropped} pending={len(queue)} seed={args.seed}")


if __name__ == "__main__":
    main()
