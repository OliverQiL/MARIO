#!/usr/bin/env python3
"""Mock of John's laptop gateway, for developing NetworkClient without the real comms stack.

Implements the server side of Docs/GATEWAY_PROTOCOL.md over TLS with a self-signed
certificate (generated on first run into tools/certs/). Prints the SHA-256
fingerprint to paste into NetworkClient's "Pinned Cert Sha256" field.

Usage:
  python3 tools/mock_gateway.py                 # interactive: type actions to send
  python3 tools/mock_gateway.py --auto 1.0      # send a random action every 1.0 s
  python3 tools/mock_gateway.py --script "stomp,stomp,dup,fireball,low:jump" --exit-after-acks
  python3 tools/mock_gateway.py --plain         # ws:// instead of wss:// (debug only)
  python3 tools/mock_gateway.py --command-file /tmp/gw.cmd
                                                # also run commands appended to a file
                                                # (echo jump >> /tmp/gw.cmd), for hands-free
                                                # device testing while someone else plays
  python3 tools/mock_gateway.py --keys          # single keys, no Enter: play along on the device
                                                # holding the Mac's keyboard (see "Keys" below)

Interactive commands:
  stand | walk | jump | squat | fireball | stomp   send a GAME_EVENT (confidence 0.95)
  low:<action>                                     send with confidence 0.3 (Gate 2 reject)
  dup                                              resend the last event (duplicate test)
  newsession                                       start a new session_id
  kick                                             close the connection (reconnect test)
  quit
Keys (--keys):
  1 stand  2 walk  3 jump  4 squat  5 fireball  6 stomp
  Hold 1/2/6 to keep sending (every 0.1 s, like classifier windows); 3/4/5 send
  once per press (a held key's auto-repeat is ignored).
  l = next action at low confidence   d = duplicate   k = kick   q = quit
  Headset tuning (DEV_TUNE, development only): = / - window bigger/smaller,
  ] / [ gap between the eye images wider/narrower, 0 reset.
Requires: pip install websockets
"""
import argparse
import asyncio
import datetime
import hashlib
import json
import os
import pathlib
import random
import ssl
import subprocess
import sys
import time

import websockets

ACTIONS = ["stand", "walk", "jump", "squat", "fireball", "stomp"]
CONTINUOUS = {"stand", "walk", "stomp"}


class KeyMapper:
    """Turns single key presses (with the OS's auto-repeat while held) into gateway commands.

    Continuous actions repeat while their key is held, at most every `hop` seconds
    (the classifier's window hop). Discrete actions fire once per physical press:
    auto-repeats arrive faster than `repeat_gap`, so a key seen again within it is
    still being held and is ignored.
    """
    KEYS = {"1": "stand", "2": "walk", "3": "jump", "4": "squat", "5": "fireball", "6": "stomp",
            "d": "dup", "k": "kick", "q": "quit",
            # Headset tuning while wearing it (DEV_TUNE): window size, image separation, reset.
            "=": "tune:eyeImageScale:0.05", "+": "tune:eyeImageScale:0.05", "-": "tune:eyeImageScale:-0.05",
            "]": "tune:lensSeparation:0.01", "[": "tune:lensSeparation:-0.01", "0": "tune:reset:0"}

    def __init__(self, hop=0.1, repeat_gap=0.3):
        self.hop, self.repeat_gap = hop, repeat_gap
        self.last_seen = {}   # key -> time of its last character (press or auto-repeat)
        self.last_sent = {}   # key -> time a command was last produced for it
        self.low_next = False

    def press(self, ch, now):
        ch = ch.lower()
        if ch == "l":
            self.low_next = True
            return None
        cmd = self.KEYS.get(ch)
        if cmd is None:
            return None
        held = now - self.last_seen.get(ch, float("-inf")) < self.repeat_gap
        self.last_seen[ch] = now
        if cmd in CONTINUOUS:
            if now - self.last_sent.get(ch, float("-inf")) < self.hop:
                return None
        elif held:
            return None  # discrete: one event per press
        self.last_sent[ch] = now
        if cmd in ACTIONS and self.low_next:
            self.low_next = False
            return "low:" + cmd
        return cmd
CERT_DIR = pathlib.Path(__file__).parent / "certs"


def ensure_cert(host_ip: str):
    """Create a self-signed cert (valid for localhost + the LAN IP) if missing."""
    CERT_DIR.mkdir(exist_ok=True)
    cert, key = CERT_DIR / "gateway.crt", CERT_DIR / "gateway.key"
    if not cert.exists():
        subprocess.run(
            ["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "365",
             "-keyout", str(key), "-out", str(cert), "-subj", "/CN=cg4002-mock-gateway",
             "-addext", f"subjectAltName=DNS:localhost,IP:127.0.0.1,IP:{host_ip}"],
            check=True, capture_output=True)
    der = ssl.PEM_cert_to_DER_cert(cert.read_text())
    return cert, key, hashlib.sha256(der).hexdigest().upper()


def lan_ip():
    try:
        return subprocess.run(["ipconfig", "getifaddr", "en0"], capture_output=True, text=True).stdout.strip() or "127.0.0.1"
    except FileNotFoundError:
        return "127.0.0.1"


def log(msg):
    print(f"[{datetime.datetime.now():%H:%M:%S.%f}"[:-3] + f"] {msg}", flush=True)


class Gateway:
    def __init__(self, args):
        self.args = args
        self.session = f"s-{int(time.time())}"
        self.next_id = 1
        self.last_event = None
        self.sent_at = {}
        self.acks = 0
        self.clients = set()
        self.done = asyncio.Event()

    def make_event(self, action, confidence=0.95):
        evt = {"type": "GAME_EVENT", "protocol_version": 1, "session_id": self.session,
               "event_id": f"e-{self.next_id:06d}", "window_id": self.next_id * 10,
               "action": action, "confidence": confidence, "timestamp": time.time()}
        self.next_id += 1
        return evt

    async def send(self, evt):
        if not self.clients:
            log("no phone connected — event not sent")
            return
        self.last_event = evt
        self.sent_at[(evt["session_id"], evt["event_id"])] = time.perf_counter()
        for ws in list(self.clients):
            await ws.send(json.dumps(evt))
        log(f"→ GAME_EVENT {evt['event_id']} {evt['action']} conf={evt['confidence']}")

    async def command(self, line):
        line = line.strip()
        if not line:
            return
        if line == "dup":
            if self.last_event:
                await self.send(self.last_event)
        elif line == "newsession":
            self.session = f"s-{int(time.time())}"
            log(f"new session {self.session}")
        elif line == "kick":
            for ws in list(self.clients):
                await ws.close(1001, "kicked by mock gateway")
        elif line.startswith("tune:"):
            _, param, delta = line.split(":")
            msg = {"type": "DEV_TUNE", "param": param, "delta": float(delta)}
            for ws in list(self.clients):
                await ws.send(json.dumps(msg))
            log(f"→ DEV_TUNE {param} {float(delta):+.2f}" if param != "reset" else "→ DEV_TUNE reset")
        elif line.startswith("low:"):
            await self.send(self.make_event(line[4:], 0.3))
        elif line in ACTIONS:
            await self.send(self.make_event(line))
        else:
            log(f"unknown command '{line}'")

    async def handler(self, ws):
        peer = ws.remote_address
        log(f"phone connected from {peer}")
        self.clients.add(ws)
        try:
            async for raw in ws:
                msg = json.loads(raw)
                kind = msg.get("type")
                if kind == "EVENT_ACK":
                    key = (msg.get("session_id"), msg.get("event_id"))
                    rtt = (time.perf_counter() - self.sent_at.get(key, time.perf_counter())) * 1000
                    self.acks += 1
                    log(f"← EVENT_ACK {msg.get('event_id')} {msg.get('outcome')} gate={msg.get('gate')} "
                        f"({msg.get('reason')}) phone={msg.get('processing_ms', 0):.2f}ms rtt={rtt:.1f}ms")
                    if self.args.exit_after_acks and self.acks >= self.expected_acks:
                        self.done.set()
                elif kind == "HAPTIC_CMD":
                    log(f"← HAPTIC_CMD {msg.get('event_id')} target={msg.get('target')} pattern={msg.get('pattern')}")
                elif kind == "VISUALIZER_READY":
                    log(f"← VISUALIZER_READY app_version={msg.get('app_version')}")
                    if self.args.script:
                        asyncio.create_task(self.run_script())
                else:
                    log(f"← unknown {raw}")
        except websockets.ConnectionClosed:
            pass
        finally:
            self.clients.discard(ws)
            log(f"phone disconnected ({peer})")

    async def run_script(self):
        await asyncio.sleep(0.5)
        for cmd in self.args.script.split(","):
            await self.command(cmd)
            await asyncio.sleep(0.3)

    async def stdin_loop(self):
        loop = asyncio.get_running_loop()
        while True:
            line = await loop.run_in_executor(None, sys.stdin.readline)
            if not line or line.strip() == "quit":
                self.done.set()
                return
            await self.command(line)

    async def keys_loop(self):
        """Single-key control from a terminal in cbreak mode (no Enter, no echo)."""
        import termios, tty
        fd = sys.stdin.fileno()
        saved = termios.tcgetattr(fd)
        tty.setcbreak(fd)
        mapper = KeyMapper()
        loop = asyncio.get_running_loop()
        queue = asyncio.Queue()
        loop.add_reader(fd, lambda: queue.put_nowait(os.read(fd, 32).decode(errors="ignore")))
        log("keys: 1 stand  2 walk  3 jump  4 squat  5 fireball  6 stomp  |  hold 1/2/6 to repeat  |  l low-conf  d dup  k kick  q quit")
        log("headset: = / - window bigger/smaller   ] / [ image gap wider/narrower   0 reset")
        try:
            while True:
                for ch in await queue.get():
                    cmd = mapper.press(ch, time.monotonic())
                    if cmd == "quit":
                        self.done.set()
                        return
                    if cmd:
                        await self.command(cmd)
        finally:
            loop.remove_reader(fd)
            termios.tcsetattr(fd, termios.TCSADRAIN, saved)

    async def command_file_loop(self, path):
        f = pathlib.Path(path)
        f.touch()
        offset = f.stat().st_size  # only commands appended after startup
        while True:
            await asyncio.sleep(0.1)
            size = f.stat().st_size
            if size < offset:
                offset = 0  # truncated
            if size > offset:
                with f.open() as fh:
                    fh.seek(offset)
                    chunk = fh.read()
                    offset = fh.tell()
                for line in chunk.splitlines():
                    await self.command(line)

    async def auto_loop(self, period):
        while True:
            await asyncio.sleep(period)
            if self.clients:
                await self.send(self.make_event(random.choice(ACTIONS), round(random.uniform(0.4, 1.0), 2)))

    async def main(self):
        ip = lan_ip()
        ssl_ctx = None
        if not self.args.plain:
            cert, key, fingerprint = ensure_cert(ip)
            ssl_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            ssl_ctx.minimum_version = ssl.TLSVersion.TLSv1_2
            ssl_ctx.load_cert_chain(cert, key)
        scheme = "ws" if self.args.plain else "wss"
        self.expected_acks = len(self.args.script.split(",")) if self.args.script else 0

        host_name = subprocess.run(["scutil", "--get", "LocalHostName"], capture_output=True, text=True).stdout.strip()
        async with websockets.serve(self.handler, None, self.args.port, ssl=ssl_ctx):  # all interfaces, IPv4 + IPv6
            log(f"mock gateway listening on {scheme}://{host_name}.local:{self.args.port}/  (= {scheme}://{ip}:{self.args.port}/)")
            if not self.args.plain:
                log(f"certificate SHA-256 (paste into NetworkClient 'Pinned Cert Sha256'): {fingerprint}")
            tasks = []
            if self.args.auto:
                tasks.append(asyncio.create_task(self.auto_loop(self.args.auto)))
            if self.args.command_file:
                tasks.append(asyncio.create_task(self.command_file_loop(self.args.command_file)))
                log(f"reading commands appended to {self.args.command_file}")
            if self.args.keys:
                if not sys.stdin.isatty():
                    sys.exit("--keys needs an interactive terminal")
                tasks.append(asyncio.create_task(self.keys_loop()))
            elif not self.args.script and not self.args.auto and sys.stdin.isatty():
                tasks.append(asyncio.create_task(self.stdin_loop()))
            try:
                if self.args.timeout:
                    await asyncio.wait_for(self.done.wait(), self.args.timeout)
                else:
                    await self.done.wait()
            except asyncio.TimeoutError:
                log("timeout")
                sys.exit(2)
            finally:
                for t in tasks:
                    t.cancel()


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--plain", action="store_true", help="ws:// without TLS")
    p.add_argument("--auto", type=float, metavar="SECONDS", help="send a random action periodically")
    p.add_argument("--command-file", help="poll this file for appended commands (one per line)")
    p.add_argument("--script", help="comma-separated commands to send after VISUALIZER_READY")
    p.add_argument("--exit-after-acks", action="store_true", help="with --script: exit once every scripted event is ACKed")
    p.add_argument("--timeout", type=float, help="exit with code 2 after this many seconds")
    p.add_argument("--keys", action="store_true", help="single-key control: 1-6 send actions (hold 1/2/6 to repeat)")
    p.add_argument("--print-fingerprint", action="store_true", help="print the cert fingerprint and exit")
    args = p.parse_args()
    if args.print_fingerprint:
        print(ensure_cert(lan_ip())[2])
        sys.exit(0)
    try:
        asyncio.run(Gateway(args).main())
    except KeyboardInterrupt:
        pass
