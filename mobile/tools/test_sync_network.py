"""Opt-in actual Android Wi-Fi/cellular transition regression with a local echo peer."""
import argparse
import json
from pathlib import Path
import socketserver
import subprocess
import threading


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    assert args.serial.startswith("emulator-")
    connections = []

    class Echo(socketserver.BaseRequestHandler):
        def handle(self):
            self.request.settimeout(30)
            value = self.request.recv(1)
            connections.append(value == b"*")
            self.request.sendall(value)
            # Keep the peer open so the test exercises an active connection at each transition.
            try:
                while self.request.recv(1):
                    pass
            except (TimeoutError, ConnectionError):
                pass

    class Server(socketserver.ThreadingTCPServer):
        daemon_threads = True

    with Server(("127.0.0.1", 0), Echo) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            result = subprocess.run(["adb", "-s", args.serial, "shell", "am", "instrument", "-w", "-e", "class",
                "com.tinyfish.jeekmusic.data.sync.SyncNetworkPolicyTest", "-e", "jeekNetworkFixture", "true",
                "-e", "networkHost", "10.0.2.2", "-e", "networkPort", str(server.server_address[1]),
                "com.tinyfish.jeekmusic.debug.test/androidx.test.runner.AndroidJUnitRunner"],
                capture_output=True, text=True, timeout=150)
            assert result.returncode == 0 and "OK (1 test)" in result.stdout, result.stdout + result.stderr + f"\nPeer exchanges: {connections}"
            assert connections == [True, True, True], connections
        finally:
            server.shutdown(); thread.join()
    report = dict(wifiAllowed=True, cellularBlockedByDefault=True, activeTransferClosedOnWifiLoss=True,
                  cellularAllowedWhenDisabled=True, activeCellularConnectionClosedWhenEnabled=True,
                  wifiReturnResumes=True, settingPersisted=True, acceptedConnections=len(connections))
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
