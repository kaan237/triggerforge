"""Tactile phase 6 test sender. Sends to 127.0.0.1 only.

  py -3 udptest.py native            native commands (right trigger, lightbar), then reset
  py -3 udptest.py dsx               DSX packets (Resistance on L2, Galloping on R2, RGB, LEDs), then reset
  py -3 udptest.py garbage           malformed packets; the app must keep running
  py -3 udptest.py forza [seconds]   fake Forza Horizon packets to port 5555 (brake ramps, ABS, revs)
  py -3 udptest.py f1 [seconds]      fake EA SPORTS F1 24 packets to port 20777 (car telemetry, status, motion ex)
  py -3 udptest.py beamng [seconds]  fake BeamNG OutGauge packets to port 4444
Options: --port N (UDP API port, default 6969), --forza-port N (default 5555),
         --f1-port N (default 20777), --beamng-port N (default 4444)
"""
import json
import math
import socket
import struct
import sys
import time

args = sys.argv[1:]


def opt(name, default):
    if name in args:
        i = args.index(name)
        v = int(args[i + 1])
        del args[i:i + 2]
        return v
    return default


PORT = opt("--port", 6969)
FORZA_PORT = opt("--forza-port", 5555)
F1_PORT = opt("--f1-port", 20777)
BEAMNG_PORT = opt("--beamng-port", 4444)
mode = args[0] if args else "native"
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.settimeout(1.0)


def send(obj_or_bytes, port=PORT, expect=True):
    data = obj_or_bytes if isinstance(obj_or_bytes, bytes) else json.dumps(obj_or_bytes).encode()
    sock.sendto(data, ("127.0.0.1", port))
    if expect:
        try:
            reply, _ = sock.recvfrom(65536)
            print("  <-", reply.decode(errors="replace")[:200])
        except socket.timeout:
            print("  <- (no answer)")


if mode == "native":
    print("native: right trigger weapon, red lightbar")
    send({"tactile": 1, "commands": [
        {"cmd": "trigger", "side": "right", "config": {"mode": "weapon", "params": {"start": 3, "end": 6, "strength": 8}}},
        {"cmd": "lightbar", "r": 255, "g": 0, "b": 0},
        {"cmd": "playerLeds", "mask": 21}]})
    time.sleep(5)
    print("native: status, then reset")
    send({"tactile": 1, "commands": [{"cmd": "status"}]})
    send({"tactile": 1, "commands": [{"cmd": "reset"}]})
elif mode == "dsx":
    print("dsx: L2 Resistance 0/8, R2 Galloping, RGB blue, player 3, mic pulse")
    send({"instructions": [{"type": 1, "parameters": [0, 1, 13, 0, 8]}]})
    send({"instructions": [{"type": 1, "parameters": [0, 2, 15, 0, 9, 2, 4, 10]}]})
    send({"instructions": [{"type": 2, "parameters": [0, 0, 0, 255]},
                           {"type": 6, "parameters": [0, 2]},
                           {"type": 5, "parameters": [0, 1]}]})
    time.sleep(6)
    print("dsx: reset to user settings")
    send({"instructions": [{"type": 7, "parameters": [0]}]})
elif mode == "garbage":
    junk = [b"", b"{", b"null", b"[]", b"{\"tactile\":1}", b"{\"instructions\":5}",
            b"{\"instructions\":[{\"type\":1,\"parameters\":[0,2,999,\"x\",{}]}]}",
            ("[" * 200).encode(), b"\xff\xfe\x00garbage", b"x" * 20000]
    for j in junk:
        print("garbage", j[:30])
        send(j)
    print("then a good packet:")
    send({"tactile": 1, "commands": [{"cmd": "status"}]})
elif mode == "forza":
    seconds = float(args[1]) if len(args) > 1 else 20
    print(f"forza: {seconds:.0f} s of fake Horizon packets to {FORZA_PORT}")
    t0 = time.time()
    while time.time() - t0 < seconds:
        t = time.time() - t0
        phase = (t % 8) / 8          # 8 s cycle: 0-4 s accelerate, 4-8 s brake
        accel = 1.0 if phase < 0.5 else 0.0
        brake = 0.0 if phase < 0.5 else (phase - 0.5) * 2
        rpm = 1000 + 6500 * (phase * 2 if phase < 0.5 else 1 - (phase - 0.5) * 2)
        slip_front = 1.5 if brake > 0.7 else 0.2      # ABS in the hard part of braking
        pkt = bytearray(324)
        struct.pack_into("<iIfff", pkt, 0, 1, int(t * 1000), 8000.0, 800.0, rpm)
        struct.pack_into("<ffff", pkt, 180, slip_front, slip_front, 0.1, 0.1)
        struct.pack_into("<f", pkt, 256, 30.0)
        pkt[315] = int(accel * 255)
        pkt[316] = int(brake * 255)
        pkt[319] = 3
        sock.sendto(bytes(pkt), ("127.0.0.1", FORZA_PORT))
        time.sleep(1 / 60)
    print("stopping (raceOn 0)")
    sock.sendto(bytes(324), ("127.0.0.1", FORZA_PORT))
elif mode == "f1":
    seconds = float(args[1]) if len(args) > 1 else 20
    print(f"f1: {seconds:.0f} s of fake F1 24 packets to {F1_PORT}")

    def f1_packet(packet_id, length, player=0):
        pkt = bytearray(length)
        struct.pack_into("<H", pkt, 0, 2024)
        pkt[5] = 1
        pkt[6] = packet_id
        struct.pack_into("<Q", pkt, 7, 424242)
        pkt[27] = player
        return pkt

    t0 = time.time()
    while time.time() - t0 < seconds:
        t = time.time() - t0
        phase = (t % 8) / 8
        accel = 1.0 if phase < 0.5 else 0.0
        brake = 0.0 if phase < 0.5 else (phase - 0.5) * 2
        revs = phase * 2 if phase < 0.5 else 1 - (phase - 0.5) * 2
        rpm = int(4000 + 8000 * revs)
        kerb = 1 if 0.6 < phase < 0.7 else 0
        slip = 0.4 if brake > 0.7 else 0.05
        car = bytearray(29 + 22 * 60 + 3)
        car[:29] = f1_packet(6, 29)
        struct.pack_into("<H", car, 29, int(60 + 200 * revs))
        struct.pack_into("<fff", car, 31, accel, 0.0, brake)
        car[29 + 14] = 0
        car[29 + 15] = 1 + int(revs * 6)
        struct.pack_into("<H", car, 29 + 16, rpm)
        car[29 + 18] = 1 if accel and revs > 0.8 else 0
        car[29 + 19] = int(revs * 100)
        for wheel in range(4):
            car[29 + 56 + wheel] = kerb
        sock.sendto(bytes(car), ("127.0.0.1", F1_PORT))
        status = f1_packet(7, 29 + 22 * 55)
        struct.pack_into("<HH", status, 29 + 17, 12000, 4000)
        struct.pack_into("<f", status, 29 + 37, 2_000_000.0)
        sock.sendto(bytes(status), ("127.0.0.1", F1_PORT))
        motion = f1_packet(13, 237)
        struct.pack_into("<ffff", motion, 93, slip, slip, slip, slip)
        sock.sendto(bytes(motion), ("127.0.0.1", F1_PORT))
        time.sleep(1 / 60)
    print("stopping (raceOn 0)")
    sock.sendto(bytes(f1_packet(6, 29 + 22 * 60 + 3)), ("127.0.0.1", F1_PORT))
elif mode == "beamng":
    seconds = float(args[1]) if len(args) > 1 else 20
    print(f"beamng: {seconds:.0f} s of fake OutGauge packets to {BEAMNG_PORT}")
    t0 = time.time()
    while time.time() - t0 < seconds:
        t = time.time() - t0
        phase = (t % 8) / 8
        accel = 1.0 if phase < 0.5 else 0.0
        brake = 0.0 if phase < 0.5 else (phase - 0.5) * 2
        revs = phase * 2 if phase < 0.5 else 1 - (phase - 0.5) * 2
        lights = 0
        if revs > 0.9:
            lights |= 1 << 0           # shift light
        if brake > 0.7:
            lights |= 1 << 10          # ABS
        pkt = bytearray(96)
        pkt[10] = 2 + int(revs * 4)
        struct.pack_into("<ff", pkt, 12, 5 + 40 * revs, 1000 + 6000 * revs)
        struct.pack_into("<I", pkt, 44, lights)
        struct.pack_into("<fff", pkt, 48, accel, brake, 0.0)
        sock.sendto(bytes(pkt), ("127.0.0.1", BEAMNG_PORT))
        time.sleep(1 / 60)
    print("stopping (raceOn 0)")
    sock.sendto(bytes(96), ("127.0.0.1", BEAMNG_PORT))
else:
    print(__doc__)
