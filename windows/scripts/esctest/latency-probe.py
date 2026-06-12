#!/usr/bin/env python3
"""Measure VT query round-trip latency through the live terminal/PTY stack.

esctest's aggregate pass/fail at a fixed --timeout cannot separate a response
that arrives *slowly* (just past the window) from one that is genuinely *lost*.
This probe answers that directly: it sends each query many times with a generous
per-read timeout and records, per query class, the latency of every response and
the count that never returned. Run it as the spawned command of a real terminal
(same WSL-over-ConPTY mechanism the esctest harness uses) so the path measured is
the same double-PTY the interactive apps (vim CPR, DSR, DA) traverse.

stdin and stdout are the same tty under a pty: the query is written to fd 1 and
the response read back from fd 0. Between queries stdin is drained non-blocking so
a late-arriving (or lost-then-trailing) response can't be misattributed to the
next query.
"""
import sys, os, time, termios, tty, select, json

# (label, query bytes, terminating byte). The terminator is the last byte of the
# expected reply, used to frame it; DECRQCRA replies with a DCS ... ST, whose final
# byte is the ST backslash. These map onto the esctest timeout buckets: CPR/DSR/DA
# are the answered classes (they land in mismatch, not timeout), while DECRQCRA,
# XTWINOPS and DECRQM are the suspected no-response classes driving the timeouts.
QUERIES = [
    ("CPR", b"\x1b[6n", b"R"),                  # cursor position    -> ESC[row;colR  (control: answered)
    ("DSR", b"\x1b[5n", b"n"),                  # device status      -> ESC[0n
    ("DA1", b"\x1b[c",  b"c"),                  # primary attrs       -> ESC[?...c
    ("DA2", b"\x1b[>c", b"c"),                  # secondary attrs      -> ESC[>...c
    ("DECRQCRA", b"\x1b[1;0;1;1;1;1*y", b"\\"), # rect checksum       -> DCS Pid!~hhhh ST  (esctest screen readback)
    ("XTWINOPS_18", b"\x1b[18t", b"t"),         # text area size chars-> ESC[8;h;wt  (esctest reset() uses this)
    ("XTWINOPS_14", b"\x1b[14t", b"t"),         # text area size px   -> ESC[4;h;wt
    ("DECRQM_25", b"\x1b[?25$p", b"y"),         # request mode DECTCEM (DEC form) -> ESC[?25;Ps$y
    ("DECRQM_ANSI_4", b"\x1b[4$p", b"y"),       # request mode IRM (ANSI form) -> ESC[4;Ps$y (suspected silent)
    # --- residual report-query triage (#79 phase 1) ---
    ("WINOP_11_state",   b"\x1b[11t", b"t"),    # report window state (normal/iconified)
    ("WINOP_13_pos",     b"\x1b[13t", b"t"),    # report window position
    ("WINOP_15_scrpx",   b"\x1b[15t", b"t"),    # report screen size in pixels
    ("WINOP_16_charpx",  b"\x1b[16t", b"t"),    # report char cell size in pixels
    ("WINOP_19_scrchars",b"\x1b[19t", b"t"),    # report screen size in chars
    ("DECXCPR_6",        b"\x1b[?6n", b"R"),    # extended cursor position (DEC ?6n -> ...R)
    ("DECDSR_printer_15",b"\x1b[?15n",b"n"),    # printer status
    ("DECDSR_udk_25",    b"\x1b[?25n",b"n"),    # UDK status
    ("DECDSR_kbd_26",    b"\x1b[?26n",b"n"),    # keyboard status
]

REPS = int(os.environ.get("PROBE_REPS", "30"))
PER_READ_TIMEOUT = float(os.environ.get("PROBE_TIMEOUT", "10"))  # slow (<this) vs lost


def drain(fd):
    """Discard any bytes already waiting (late/partial from a prior query)."""
    while True:
        r, _, _ = select.select([fd], [], [], 0)
        if not r:
            return
        if not os.read(fd, 4096):
            return


def read_response(fd, terminator, timeout):
    """Return (latency_seconds, raw_bytes) or (None, partial) on timeout."""
    start = time.monotonic()
    buf = b""
    while True:
        remaining = timeout - (time.monotonic() - start)
        if remaining <= 0:
            return None, buf
        r, _, _ = select.select([fd], [], [], remaining)
        if not r:
            return None, buf
        chunk = os.read(fd, 64)
        if not chunk:
            return None, buf
        buf += chunk
        if terminator in buf:
            return time.monotonic() - start, buf


def main(outpath):
    fd = sys.stdin.fileno()
    old = termios.tcgetattr(fd)
    tty.setraw(fd)
    results = {}
    try:
        for label, query, term in QUERIES:
            lats = []
            lost = 0
            for _ in range(REPS):
                drain(fd)
                os.write(1, query)
                dt, _buf = read_response(fd, term, PER_READ_TIMEOUT)
                if dt is None:
                    lost += 1
                else:
                    lats.append(dt * 1000.0)  # ms
                time.sleep(0.05)
            lats_sorted = sorted(lats)
            n = len(lats_sorted)
            results[label] = {
                "reps": REPS,
                "got": n,
                "lost": lost,
                "min_ms": round(lats_sorted[0], 1) if n else None,
                "median_ms": round(lats_sorted[n // 2], 1) if n else None,
                "p95_ms": round(lats_sorted[min(n - 1, int(n * 0.95))], 1) if n else None,
                "max_ms": round(lats_sorted[-1], 1) if n else None,
                "all_ms": [round(x, 1) for x in lats],
            }
    finally:
        termios.tcsetattr(fd, termios.TCSADRAIN, old)
    with open(outpath, "w") as f:
        json.dump(results, f, indent=2)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "/tmp/latency-probe.json")
