"""Smoke-test a disposable, shared-mode Paprika instance. Prints its verified packet ID."""

import argparse
from contextlib import closing
import hashlib
import http.client
import io
import json
import os
import re
import struct
import time
from urllib.parse import urlsplit
import zipfile


FILENAME = "container-smoke.pdf"
DOC_ID = "smoke-reversed"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fixture():
    """Deterministic two-page PDF with embedded text, no fonts or files needed."""
    objects = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Count 2 /Kids [4 0 R 6 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    for number in (1, 2):
        text = f"BT /F1 14 Tf 12 80 Td (Smoke invoice page {number}) Tj ET"
        objects.extend([
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 220 110] "
            f"/Resources << /Font << /F1 3 0 R >> >> /Contents {2 * number + 3} 0 R >>",
            f"<< /Length {len(text)} >>\nstream\n{text}\nendstream",
        ])
    pdf = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, obj in enumerate(objects, 1):
        offsets.append(len(pdf))
        pdf.extend(f"{i} 0 obj\n{obj}\nendobj\n".encode("ascii"))
    start = len(pdf)
    pdf.extend(f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode())
    for offset in offsets:
        pdf.extend(f"{offset:010} 00000 n \n".encode())
    pdf.extend(f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n".encode())
    return bytes(pdf)


class Api:
    def __init__(self, url):
        origin = urlsplit(url)
        require(origin.scheme == "http" and origin.hostname in ("127.0.0.1", "localhost", "::1")
                and origin.port and origin.path in ("", "/") and not origin.username
                and not origin.password and not origin.query and not origin.fragment,
                "Expected http://127.0.0.1:<testPort>")
        self.host, self.port = origin.hostname, origin.port
        self.token = os.environ.get("PAPRIKA_SMOKE_TOKEN", "")
        require(self.token and self.token.isascii() and self.token.isprintable(),
                "Set PAPRIKA_SMOKE_TOKEN to the disposable administrator bearer")

    def request(self, path, method="GET", *, auth=True, body=None, kind=None,
                status=200, media=None, timeout=30):
        headers = {"Host": "localhost"}
        if auth:
            headers["Authorization"] = "Bearer " + self.token
        if method != "GET":
            headers["X-paprika"] = "1"
        if kind:
            headers["Content-Type"] = kind
        # Direct loopback only: no proxy, redirect following, cookies, or bearer in output.
        with closing(http.client.HTTPConnection(self.host, self.port, timeout=timeout)) as conn:
            conn.request(method, path, body=body, headers=headers)
            response = conn.getresponse()
            require(response.status == status, f"{method} {path}: expected {status}, got {response.status}")
            if media:
                require(media in response.headers.get_content_type(), f"Wrong content type for {path}")
            return response.read()

    def json(self, path, method="GET", data=None, **kwargs):
        if data is not None:
            kwargs.update(body=json.dumps(data).encode(), kind="application/json")
        return json.loads(self.request(path, method, media="application/json", **kwargs))


def preflight(api):
    deadline = time.monotonic() + 60
    while True:
        try:
            health = api.json("/api/health", timeout=2)
            break
        except (OSError, http.client.RemoteDisconnected):
            require(time.monotonic() < deadline, "Timed out waiting for /api/health")
            time.sleep(.5)
    require(health["status"] == "ok", "API health was not ok")
    for path in ("/health/live", "/health/ready"):
        require(api.request(path, auth=False) == b"", f"Unexpected probe content: {path}")
    require(b"<app-root" in api.request("/", auth=False, media="text/html"), "Missing Angular app")
    worker = api.request("/assets/pdf.worker.min.mjs", auth=False, media="javascript")
    require(b"WorkerMessageHandler" in worker, "Missing PDF.js worker")
    api.request("/api/packets", auth=False, status=401)
    session = api.json("/api/operations-platform/session")
    require(session["mode"] == "shared" and session["identity"]["administrator"], "Expected shared administrator")
    config = api.json("/api/config")
    require(config["mode"] == "simulation" and not config["ocrConfigured"] and not config["classifierConfigured"],
            "Use a disposable simulation instance without provider credentials")


def ordered_pages(packet):
    pages = sorted(packet["pages"], key=lambda p: p["pageNumber"], reverse=True)
    require([p["pageNumber"] for p in pages] == [2, 1]
            and len({p["sourceId"] for p in pages}) == 1
            and all(p["sourceName"] == FILENAME for p in pages), "Unexpected source pages")
    return pages


def create(api, pdf):
    boundary = "paprika-fixture-upload"
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="{FILENAME}"\r\n'
            'Content-Type: application/pdf\r\n\r\n').encode() + pdf + f"\r\n--{boundary}--\r\n".encode()
    packet_id = api.json("/api/packets", "POST", body=body, kind=f"multipart/form-data; boundary={boundary}")["id"]
    path = "/api/packets/" + packet_id
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        packet = api.json(path, timeout=5)
        if packet["status"] == "review":
            break
        require(packet["status"] in ("queued", "processing"), f"Unexpected packet state: {packet['status']}")
        time.sleep(.5)
    else:
        raise ValueError(f"Timed out waiting for review: {packet_id}")

    pages = ordered_pages(packet)
    rev = packet["revision"]
    api.request(path + "/export", status=409)
    tasks = "/api/operations-platform/tasks"
    task = api.json(tasks + "/open", "POST", {"packetId": packet_id, "revision": rev})
    api.json(f"{tasks}/{task['id']}/claim", "POST", {"revision": task["revision"]})
    api.json(f"/api/preprocessing/{packet_id}", "POST", {
        "revision": rev, "sourceId": pages[0]["sourceId"], "pageNumber": 2,
        "policy": {"rotation": 90, "grayscale": True, "contrast": 1},
    })
    doc = {"id": DOC_ID, "type": "invoice", "pageIds": [p["id"] for p in pages]}
    api.json(path + "/review", "PUT", {"revision": rev, "approve": True, "documents": [doc]})
    return packet_id


def check_pdf(data):
    require(data.startswith(b"%PDF-") and data.rstrip().endswith(b"%%EOF"), "Invalid PDF download")


def verify(api, packet_id, pdf):
    require(re.fullmatch("[a-f0-9]{32}", packet_id), "Expected a smoke packet ID")
    path = "/api/packets/" + packet_id
    packet = api.json(path)
    require(packet["id"] == packet_id and packet["status"] == "approved", "Packet is not approved")
    pages = ordered_pages(packet)
    source = pages[0]["sourceId"]
    digest = hashlib.sha256(pdf).hexdigest()
    original = api.request(f"{path}/sources/{source}", media="application/pdf")
    require(original == pdf, "Original PDF changed")

    raster = f"/api/preprocessing/{packet_id}"
    derivatives = api.json(raster)
    derivative, = [d for d in derivatives if d["sourceId"] == source and d["pageNumber"] == 2
                   and d["policy"]["rotation"] == 90 and d["originalSha256"] == digest]
    png = api.request(f"{raster}/derivatives/{derivative['id']}/image", media="image/png")
    require(png.startswith(b"\x89PNG\r\n\x1a\n") and hashlib.sha256(png).hexdigest() == derivative["sha256"]
            and struct.unpack(">II", png[16:24]) == (derivative["width"], derivative["height"])
            and 0 < derivative["width"] < derivative["height"], "Invalid rotated native PDF raster")

    document = api.request(f"{path}/documents/{DOC_ID}/download", media="application/pdf")
    check_pdf(document)
    exported = api.request(path + "/export", media="application/zip")
    with zipfile.ZipFile(io.BytesIO(exported)) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        require(manifest["id"] == packet_id and manifest["revision"] == packet["revision"], "Wrong manifest revision")
        src, = manifest["sources"]
        require((src["name"], src["id"], src["sha256"], src["pageCount"]) == (FILENAME, source, digest, 2),
                "Manifest source does not match the uploaded PDF")
        doc, = manifest["documents"]
        require(doc["id"] == DOC_ID and doc["type"] == "invoice" and
                [(p["sourceId"], p["pageNumber"], p["pageId"]) for p in doc["pages"]] ==
                [(source, p["pageNumber"], p["id"]) for p in pages], "Wrong approved page order in manifest")
        require(archive.read(doc["file"]) == document, "ZIP member differs from the approved PDF")
    hashes = {name: hashlib.sha256(data).hexdigest() for name, data in (
        ("original", original), ("approvedZip", exported), ("individualPdf", document))}
    return {"packet": packet_id, "source": source, "revision": packet["revision"], "sha256": hashes}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("url", help="http://127.0.0.1:<testPort>")
    parser.add_argument("--verify-only", metavar="PACKET_ID", help="repeat read-only checks after a restart")
    parser.add_argument("--report", action="store_true", help="print JSON with the packet ID and download hashes")
    args = parser.parse_args()
    try:
        api = Api(args.url)
        preflight(api)
        pdf = fixture()
        packet_id = args.verify_only if args.verify_only is not None else create(api, pdf)
        report = verify(api, packet_id, pdf)
    except (ValueError, OSError, KeyError, TypeError, struct.error, zipfile.BadZipFile, http.client.HTTPException) as exc:
        parser.exit(1, f"Container smoke failed: {exc}\n")
    print(json.dumps(report) if args.report else packet_id)


if __name__ == "__main__":
    main()
