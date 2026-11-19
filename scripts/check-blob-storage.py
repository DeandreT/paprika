"""Qualify the local Paprika image across replacement, one synthetic packet per storage profile."""

import argparse
import base64
from contextlib import ExitStack, closing
from email.utils import formatdate
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import signal
import subprocess
import sys
import time


AZURITE = ("mcr.microsoft.com/azure-storage/azurite:3.35.0@"
           "sha256:647c63a91102a9d8e8000aab803436e1fc85fbb285e7ce830a82ee5d6661cf37")
ACCOUNT = "smoketest"
SMOKE = Path(__file__).with_name("container-smoke.py")


def docker(*args, env=None):
    return subprocess.run(["docker", *args], env=env, check=True,
                          stdout=subprocess.PIPE, text=True).stdout.strip()


def container(stack, image, options, command=(), env=None):
    cid = docker("create", *options, image, *command, env=env)
    stack.callback(docker, "rm", "-fv", cid)  # Also removes anonymous Azurite /data.
    docker("start", cid)
    return cid


def host_port(cid, number):
    address = docker("port", cid, f"{number}/tcp")
    match = re.fullmatch(r"127\.0\.0\.1:(\d+)", address)
    if not match:
        raise ValueError(f"Expected a single loopback mapping for {number}/tcp")
    return int(match[1])


class Azurite:
    def __init__(self, stack, network, env):
        key = secrets.token_bytes(64)
        encoded = base64.b64encode(key).decode("ascii")
        self.key = key
        self.bucket = "packets-" + secrets.token_hex(8)
        self.prefix = f"/{ACCOUNT}/{self.bucket}"
        self.cid = container(stack, AZURITE, [
            "--network", network, "--network-alias", "azurite", "-p", "127.0.0.1::10000",
            "-e", "AZURITE_ACCOUNTS", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
        ], ["azurite-blob", "--blobHost", "0.0.0.0", "--location", "/data",
            "--disableProductStyleUrl", "--disableTelemetry"],
            env=dict(env, AZURITE_ACCOUNTS=f"{ACCOUNT}:{encoded}"))
        self.port = host_port(self.cid, 10000)
        self.connection = (f"DefaultEndpointsProtocol=http;AccountName={ACCOUNT};AccountKey={encoded};"
                           f"BlobEndpoint=http://azurite:10000/{ACCOUNT}")
        # Provision the private container ourselves, just as an operator could.
        deadline = time.monotonic() + 30
        while True:
            try:
                self.request("PUT", self.prefix, query="restype=container", status=201)
                break
            except (OSError, http.client.RemoteDisconnected):
                if time.monotonic() >= deadline:
                    raise ValueError("Azurite did not become ready")
                time.sleep(.25)

    def request(self, method, path, *, query="", status=200):
        # A direct, signed Blob REST request to the *emulator*, independent of the app/SDK.
        date, version = formatdate(usegmt=True), "2021-12-02"
        canonical = (method + "\n" * 12 + f"x-ms-date:{date}\nx-ms-version:{version}\n/{ACCOUNT}{path}")
        if query:
            canonical += "\n" + query.replace("=", ":", 1)
        sig = base64.b64encode(hmac.digest(self.key, canonical.encode("ascii"), "sha256")).decode("ascii")
        headers = {"x-ms-date": date, "x-ms-version": version, "Authorization": f"SharedKey {ACCOUNT}:{sig}"}
        with closing(http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)) as conn:
            conn.request(method, path + ("?" + query if query else ""), headers=headers)
            response = conn.getresponse()
            if response.status != status:
                raise ValueError(f"Direct Azurite {method} expected {status}, got {response.status}")
            return response.read()

    def verify_downloads(self, previous_logs, report):
        # Only app traffic *after* replacement qualifies. Ignore HEAD, metadata and list requests.
        logs = docker("logs", self.cid)
        if not logs.startswith(previous_logs):
            raise ValueError("Azurite access log changed unexpectedly")
        paths = set(re.findall(r'"GET ([^?\s"]+)(?:\?timeout=\d+)? HTTP/[\d.]+" (?:200|206) ',
                               logs[len(previous_logs):]))
        packet = f"{self.prefix}/packets/{report['packet']}"
        exports = f"{packet}/exports/{report['revision']}"
        hashes = report["sha256"]
        expected = {
            "original": re.escape(f"{packet}/originals/{report['source']}.pdf"),
            "individualPdf": re.escape(f"{exports}/document-001-{hashes['individualPdf']}.pdf"),
            "approvedZip": re.escape(f"{exports}/packet-") + r"(?:[0-9a-f]{64}-)?"
                           + re.escape(hashes["approvedZip"] + ".zip"),
        }
        for key, pattern in expected.items():
            matches = [path for path in paths if re.fullmatch(pattern, path)]
            if len(matches) != 1 or hashlib.sha256(self.request("GET", matches[0])).hexdigest() != hashes[key]:
                raise ValueError(f"Replacement did not read the matching Azurite {key} blob")


def qualify(image, profile):
    network = "paprika-smoke-" + secrets.token_hex(12)
    env = dict(os.environ, PAPRIKA_SMOKE_TOKEN=secrets.token_hex(32))
    env["paprika__Operations__Users__0__Token"] = env["PAPRIKA_SMOKE_TOKEN"]
    with ExitStack() as resources:
        docker("network", "create", network)
        resources.callback(docker, "network", "rm", network)
        volume = docker("volume", "create")
        resources.callback(docker, "volume", "rm", volume)
        options = [
            "--pull", "never", "--network", network, "-p", "127.0.0.1::8080", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--mount", f"type=volume,src={volume},dst=/data",
            "-e", "paprika__Operations__Users__0__Name=smoke",
            "-e", "paprika__Operations__Users__0__Roles__0=administrator",
            "-e", "paprika__Operations__Users__0__Token",
        ]
        az = None
        if profile == "AzureBlob":
            az = Azurite(resources, network, env)
            options += ["-e", "paprika__Storage__Provider=AzureBlob", "-e", "paprika__Storage__ConnectionString",
                        "-e", f"paprika__Storage__Container={az.bucket}", "-e", "NO_PROXY=azurite,localhost,127.0.0.1",
                        "-e", "no_proxy=azurite,localhost,127.0.0.1"]
            env["paprika__Storage__ConnectionString"] = az.connection
        before = None
        for _ in range(2):
            with ExitStack() as instance:
                previous_logs = docker("logs", az.cid) if az and before else ""
                app = container(instance, image, options, env=env)
                if docker("exec", app, "id", "-u") == "0":
                    raise ValueError("The app must not run as root")
                url = f"http://127.0.0.1:{host_port(app, 8080)}"
                args = [sys.executable, str(SMOKE), url, "--report"]
                if before:
                    args += ["--verify-only", before["packet"]]
                report = json.loads(subprocess.check_output(args, env=env, text=True))
                if before is not None:
                    if before != report:
                        raise ValueError(f"{profile} replacement mismatch: before={before}, after={report}")
                    if az:
                        az.verify_downloads(previous_logs, report)
                elif az:
                    # Only this disposable, now-approved packet: force a cold read of its original.
                    if not re.fullmatch("[a-f0-9]{32}", report["packet"]) or not re.fullmatch(
                            "[A-Za-z0-9-]{1,64}", report["source"]):
                        raise ValueError("Unexpected synthetic packet/source ID")
                    cache = f"/data/{report['packet']}/{report['source']}.pdf"
                    docker("exec", app, "rm", "-f", "--", cache)
                before = report
                docker("stop", "--timeout", "30", app)
    print(f"PASS {profile} replacement: {json.dumps(before, sort_keys=True)}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", help="locally built or loaded Linux app image (never pulled)")
    parser.add_argument("--profile", choices=["both", "filesystem", "azure"], default="both")
    args = parser.parse_args()
    # Unwind the stacks on ordinary CI cancellation as well as Ctrl-C and test failures.
    def terminate(signum, _frame):
        raise SystemExit(128 + signum)
    signal.signal(signal.SIGTERM, terminate)
    try:
        image = docker("image", "inspect", "--format", "{{.Id}}", "--", args.image)
        if not re.fullmatch("sha256:[0-9a-f]{64}", image):
            raise ValueError("Expected exactly one local app image")
        if args.profile != "azure":
            qualify(image, "Filesystem")
        if args.profile != "filesystem":
            qualify(image, "AzureBlob")
    except (ValueError, OSError, http.client.HTTPException, subprocess.CalledProcessError) as exc:
        parser.exit(1, f"Storage qualification failed: {exc}\n")


if __name__ == "__main__":
    main()
