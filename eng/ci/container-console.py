"""Check two container console clients with real Unix TTYs, then remain attached for stop."""

import fcntl
import os
import pty
import select
import struct
import subprocess
import sys
import termios
import time


def acquire_terminal():
    os.setsid()
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)


class Console:
    def __init__(self, container):
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
        self.process = subprocess.Popen(
            ["docker", "exec", "-it", container, "dotnet", "/app/HostingRemoteSample.dll",
             "--remote-console", "repl-hosting-sample"],
            stdin=slave, stdout=slave, stderr=slave,
            env=dict(os.environ, TERM="xterm-256color"), preexec_fn=acquire_terminal,
        )
        os.close(slave)
        self.output = bytearray()

    def send(self, text):
        os.write(self.master, text.encode())

    def read(self):
        readable, _, _ = select.select([self.master], [], [], 0.1)
        if readable:
            try:
                self.output.extend(os.read(self.master, 8192))
            except OSError:
                if self.process.poll() is None:
                    self.process.wait(timeout=2)

    def until(self, text):
        marker = text.encode()
        deadline = time.monotonic() + 12
        while marker not in self.output:
            if time.monotonic() > deadline or self.process.poll() is not None:
                raise AssertionError(f"Missing {text!r}, exit={self.process.poll()}: {self.output.decode(errors='replace')}")
            self.read()
        del self.output[:self.output.index(marker) + len(marker)]

    def exited(self):
        deadline = time.monotonic() + 12
        while self.process.poll() is None:
            if time.monotonic() > deadline:
                raise TimeoutError(f"Console did not exit: {self.output.decode(errors='replace')}")
            self.read()
        return self.process.returncode

    def close(self):
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait(timeout=5)
        os.close(self.master)


clients = []
try:
    first = Console(sys.argv[1])
    clients.append(first)
    second = Console(sys.argv[1])
    clients.append(second)
    first.until("> ")
    second.until("> ")
    first.send("settings set greeting native-shared\r")
    first.until("updated to 'native-shared'")
    first.until("> ")
    second.send("settings show greeting\r")
    second.until("greeting = native-shared")
    second.until("> ")
    first.send("watch\r")
    first.until("Watching...")
    second.send("send native-bus-marker\r")
    second.until("Message sent.")
    second.until("> ")
    first.until("native-bus-marker")
    first.send("\x03")
    first.until("Cancelled.")
    first.until("> ")
    second.send("health\r")
    second.until("ready")
    second.until("> ")
    first.send("\x04")
    assert first.exited() == 130
    second.send("watch\r")
    second.until("Watching...")
    print("READY_FOR_STOP", flush=True)
    assert sys.stdin.readline().strip() == "stopped"
    # A server shutdown may reach the client as a normal terminal close or a transport failure.
    assert second.exited() in (0, 1, 130)
    print("Native console interruption, detach, shared state and host shutdown passed.", flush=True)
finally:
    for client in clients:
        client.close()
