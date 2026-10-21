"""Exercise the shipped console client through a real Unix pseudo-terminal."""

import fcntl
import os
import pty
import select
import signal
import struct
import subprocess
import sys
import termios
import time


master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))


def acquire_terminal():
    os.setsid()
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)


environment = dict(os.environ, TERM="xterm-256color")
process = subprocess.Popen(
    ["dotnet", sys.argv[1], "--remote-console", sys.argv[2]],
    stdin=slave, stdout=slave, stderr=slave, env=environment,
    preexec_fn=acquire_terminal,
)
os.close(slave)
output = bytearray()


def until(marker):
    encoded = marker.encode("utf-8")
    deadline = time.monotonic() + 10
    while encoded not in output:
        if process.poll() is not None:
            raise AssertionError(f"Client exited {process.returncode}: {output.decode('utf-8', errors='replace')}")
        if time.monotonic() > deadline:
            raise TimeoutError(f"Missing {marker!r}: {output.decode('utf-8', errors='replace')}")
        readable, _, _ = select.select([master], [], [], 0.1)
        if readable:
            output.extend(os.read(master, 8192))
    del output[:output.index(encoded) + len(encoded)]


try:
    until("> ")
    os.write(master, b"health\r")
    until("native-ready-marker")
    until("> ")
    fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", 45, 120, 0, 0))
    os.kill(process.pid, signal.SIGWINCH)
    os.write(master, b"size\r")
    until("native-size:120x45")
    until("> ")
    os.write(master, b"ec\t ab\x1b[DZ\r")
    until("native-echo:aZb")
    until("> ")
    os.write(master, b"wait\r")
    until("native-wait-started")
    os.write(master, b"\x03")
    until("Cancelled.")
    until("> ")
    os.write(master, b"\x04")
    deadline = time.monotonic() + 10
    while process.poll() is None:
        if time.monotonic() > deadline:
            raise TimeoutError(f"Detach did not finish: {output.decode('utf-8', errors='replace')}")
        readable, _, _ = select.select([master], [], [], 0.1)
        if readable:
            try:
                output.extend(os.read(master, 8192))
            except OSError:
                process.wait(timeout=1)
    assert process.returncode == 130, process.returncode
    print("Native console editing, resize, interruption and detach passed.")
finally:
    if process.poll() is None:
        process.kill()
        process.wait(timeout=5)
    os.close(master)
