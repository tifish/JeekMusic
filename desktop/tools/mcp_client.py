"""Call JeekMusic's production stdio MCP endpoint using only Python's standard library."""
import argparse
import collections
import json
import os
from pathlib import Path
import queue
import subprocess
import threading


DEFAULT_EXE = Path(__file__).resolve().parents[2] / "output" / "JeekMusic.exe"


class McpClient:
    def __init__(self, exe=DEFAULT_EXE, data=None, timeout=40, env=None):
        self.timeout = timeout
        self.messages = queue.Queue()
        self.errors = collections.deque(maxlen=20)
        self.next_id = 0
        env = {**os.environ, **(env or {})}
        if data is not None:
            env["JEEKMUSIC_DATA"] = str(Path(data).resolve())
        self.process = subprocess.Popen(
            [str(Path(exe).resolve()), "--mcp"], env=env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.error_reader = threading.Thread(target=self._read_errors, daemon=True)
        self.reader.start()
        self.error_reader.start()
        try:
            self.request("initialize", {
                "protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "jeekmusic-python", "version": "1"},
            })
            self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        except BaseException:
            self.close()
            raise

    def _read(self):
        try:
            for line in self.process.stdout:
                self.messages.put(json.loads(line))
        except (ValueError, OSError) as error:
            self.messages.put(error)
        finally:
            self.messages.put(None)

    def _read_errors(self):
        for line in self.process.stderr:
            self.errors.append(line.rstrip())

    def _send(self, message):
        self.process.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        message = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            message["params"] = params
        self._send(message)
        while True:
            try:
                response = self.messages.get(timeout=self.timeout)
            except queue.Empty as error:
                raise TimeoutError(f"JeekMusic MCP timed out: {method}") from error
            if response is None:
                code = self.process.wait(timeout=5)
                self.error_reader.join(timeout=2)
                raise ConnectionError(f"JeekMusic MCP disconnected (exit={code}). " + "\n".join(self.errors))
            if isinstance(response, Exception):
                raise response
            if response.get("id") != self.next_id:
                continue
            if "error" in response:
                raise RuntimeError(response["error"])
            return response["result"]

    def call(self, name, arguments=None):
        return self.request("tools/call", {"name": name, "arguments": arguments or {}})

    def close(self):
        if not self.process.stdin.closed:
            try:
                self.process.stdin.close()
            except BrokenPipeError:
                pass
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        self.reader.join(timeout=2)
        self.error_reader.join(timeout=2)
        self.process.stdout.close()
        self.process.stderr.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", default=str(DEFAULT_EXE))
    parser.add_argument("--data", help="Isolated data directory (sets JEEKMUSIC_DATA)")
    parser.add_argument("--list", action="store_true", help="List available tools and input schemas")
    parser.add_argument("tool", nargs="?", default="get_status")
    parser.add_argument("arguments", nargs="?", default="{}", help="JSON object")
    args = parser.parse_args()
    with McpClient(args.exe, args.data) as client:
        result = client.request("tools/list") if args.list else client.call(args.tool, json.loads(args.arguments))
        print(json.dumps(result, indent=2, ensure_ascii=False))
        return 1 if result.get("isError", False) else 0


if __name__ == "__main__":
    raise SystemExit(main())
