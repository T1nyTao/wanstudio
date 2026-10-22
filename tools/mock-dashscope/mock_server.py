#!/usr/bin/env python3
"""Local DashScope-compatible mock for WanStudio QA.

Standard library only. Speaks just enough of the Wan text/image-to-video API for
`Services/DashScopeClient.cs` to work end to end without a real key or network:

  POST /api/v1/services/aigc/video-generation/video-synthesis   -> submit
  GET  /api/v1/tasks/<task_id>                                  -> poll
  GET  /files/sample.mp4                                        -> fixture bytes
  GET  /__requests                                              -> captured requests
  GET  /__reset                                                 -> clear state

Temporary-upload flow (chain continuation, "upload local file -> temporary URL"):

  GET  /api/v1/uploads?action=getPolicy&model=<model>           -> upload policy
  POST /oss/upload                                              -> OSS-style multipart POST
  POST /api/v1/services/.../video-synthesis  with media[].url="oss://..." requires
       header X-DashScope-OssResourceResolve: enable, else 400.

Contract details that the app relies on:
  * missing Authorization            -> 401 InvalidApiKey / "No API-key provided."
  * "Bearer bad-key" (or empty key)   -> 401 InvalidApiKey / "Invalid API-key provided."
  * missing X-DashScope-Async: enable -> 400 (auth is checked first)
  * submit reply                      -> {"request_id","output":{"task_id","task_status":"PENDING"}}
  * polls 1..3                        -> RUNNING, poll 4+ -> SUCCEEDED + output.video_url

Gate order on submit is auth (401) -> X-DashScope-Async (400) -> oss:// resolve (400),
so every pre-existing branch answers exactly as it did before upload support existed.

The upload endpoints are a protocol double, not a real object store: no signature is
verified, no bytes are persisted, and the credentials in the policy response are
obviously fake so they can never be mistaken for real ones.

Usage:
  python tools/mock-dashscope/mock_server.py --port 8787 --fixture fixtures/long-20s.mp4
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import traceback
import threading
import time
import uuid
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs

SUBMIT_PATH = "/api/v1/services/aigc/video-generation/video-synthesis"
TASK_PREFIX = "/api/v1/tasks/"
POLICY_PATH = "/api/v1/uploads"
OSS_UPLOAD_PATH = "/oss/upload"
MASKED_AUTH = "Bearer ****"
OSS_RESOLVE_HEADER = "X-DashScope-OssResourceResolve"

# Keys the mock treats as invalid on purpose, so QA can exercise the 401 branch.
REJECTED_KEYS = {"bad-key"}

# Fields the OSS form POST must carry (official upload flow, step 2).
OSS_REQUIRED_FIELDS = ("OSSAccessKeyId", "Signature", "policy", "key")

LOCK = threading.Lock()


class State:
    """All mutable server state, guarded by LOCK."""

    def __init__(self, fixture: str, success_after: int) -> None:
        self.fixture = fixture
        self.success_after = max(1, success_after)
        self.counter = 0                  # submit counter, drives mock-<n> ids
        self.tasks: dict[str, int] = {}   # task_id -> number of polls seen
        self.requests: list[dict[str, Any]] = []  # everything received, for /__requests
        self.upload_dirs: list[str] = []  # every upload_dir handed out by getPolicy
        self.uploads: dict[str, int] = {}  # oss key -> byte count accepted by /oss/upload
        self.log_path: str = ""

    def next_id(self) -> str:
        self.counter += 1
        return f"mock-{self.counter}"

    def next_upload_dir(self, model: str) -> str:
        day = time.strftime("%Y%m%d")
        slug = re.sub(r"[^A-Za-z0-9._-]", "-", model or "wan2.7-i2v")[:40]
        upload_dir = f"dashscope-instant/{day}/{slug}/{uuid.uuid4().hex}"
        self.upload_dirs.append(upload_dir)
        return upload_dir

    def reset(self) -> None:
        self.counter = 0
        self.tasks.clear()
        self.requests.clear()
        self.upload_dirs.clear()
        self.uploads.clear()
        if self.log_path and os.path.exists(self.log_path):
            with open(self.log_path, "w", encoding="utf-8"):
                pass


STATE: State


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S%z")


def append_jsonl(entry: dict[str, Any]) -> None:
    if not STATE.log_path:
        return
    line = json.dumps(entry, ensure_ascii=False, sort_keys=False)
    with open(STATE.log_path, "a", encoding="utf-8") as fh:
        fh.write(line + "\n")


def parse_range(header: str, total: int) -> tuple[int, int] | None:
    """Parse a single `bytes=start-end` range. Returns inclusive (start, end)."""
    m = re.match(r"^bytes=(\d*)-(\d*)$", header.strip())
    if not m:
        return None
    raw_start, raw_end = m.group(1), m.group(2)
    if raw_start == "" and raw_end == "":
        return None
    if raw_start == "":
        length = int(raw_end)
        if length <= 0:
            return None
        start = max(0, total - length)
        end = total - 1
    else:
        start = int(raw_start)
        end = int(raw_end) if raw_end else total - 1
    if start >= total:
        return None
    end = min(end, total - 1)
    if end < start:
        return None
    return start, end


def extract_boundary(content_type: str) -> str | None:
    """Pull the boundary token out of `multipart/form-data; boundary=...`."""
    m = re.search(r'boundary="?([^";]+)"?', content_type or "", re.IGNORECASE)
    return m.group(1).strip() if m else None


def parse_multipart(body: bytes, boundary: str) -> list[dict[str, Any]]:
    """Minimal multipart/form-data parser (stdlib only; `cgi` is gone in 3.13).

    Returns one dict per part:
      name / filename / content_type / size / data (bytes) / text (str, for plain fields)

    Splitting on the delimiter is enough here because the mock never has to be
    byte-exact about OSS semantics -- it only needs field names, the file name and
    the byte count, and it must not crash on binary payloads.
    """
    delimiter = b"--" + boundary.encode("utf-8")
    parts: list[dict[str, Any]] = []

    for chunk in body.split(delimiter)[1:]:
        if chunk.startswith(b"--"):  # closing delimiter
            break
        if chunk.startswith(b"\r\n"):
            chunk = chunk[2:]
        head, sep, data = chunk.partition(b"\r\n\r\n")
        if not sep:
            continue
        if data.endswith(b"\r\n"):
            data = data[:-2]

        headers: dict[str, str] = {}
        for line in head.split(b"\r\n"):
            if b":" in line:
                key, _, value = line.partition(b":")
                headers[key.decode("utf-8", "replace").strip().lower()] = (
                    value.decode("utf-8", "replace").strip()
                )

        # .NET Core 3.0+ stopped quoting name/filename in Content-Disposition
        # (form-data; name=OSSAccessKeyId; filename=a.mp4), while other clients do
        # quote them, so both shapes must parse. The leading [;\s] keeps `name=`
        # from matching inside `filename=`.
        disposition = headers.get("content-disposition", "")
        name_match = re.search(r'(?:^|[;\s])name=(?:"([^"]*)"|([^;\s]+))', disposition, re.IGNORECASE)
        file_match = re.search(r'(?:^|[;\s])filename=(?:"([^"]*)"|([^;\s]+))', disposition, re.IGNORECASE)
        parts.append(
            {
                "name": (name_match.group(1) or name_match.group(2)) if name_match else None,
                "filename": (file_match.group(1) or file_match.group(2)) if file_match else None,
                "content_type": headers.get("content-type"),
                "size": len(data),
                "data": data,
                "text": None if file_match else data.decode("utf-8", "replace"),
            }
        )
    return parts


class Handler(BaseHTTPRequestHandler):
    server_version = "MockDashScope/1.1"
    protocol_version = "HTTP/1.1"

    # ---------- plumbing ----------

    def log_message(self, fmt: str, *args: Any) -> None:  # keep stdout tidy
        sys.stderr.write("[mock] %s - %s\n" % (self.address_string(), fmt % args))

    def _read_body(self) -> bytes:
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            return self._read_chunked()
        length = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(length) if length > 0 else b""

    def _read_chunked(self) -> bytes:
        out = bytearray()
        while True:
            line = self.rfile.readline(64)
            if not line:
                break
            size_text = line.split(b";", 1)[0].strip()
            try:
                size = int(size_text, 16)
            except ValueError:
                break
            if size == 0:
                while True:  # drain trailers
                    trailer = self.rfile.readline(64)
                    if trailer in (b"\r\n", b"\n", b""):
                        break
                break
            out.extend(self.rfile.read(size))
            self.rfile.read(2)  # trailing CRLF
        return bytes(out)

    def _send_json(self, status: int, payload: dict[str, Any]) -> None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _send_empty(self, status: int) -> None:
        """OSS answers the form POST with an empty 200 when success_action_status=200.

        Connection: close is explicit on purpose. Clients that sent a large multipart
        body (the upload flow) otherwise wait on a keep-alive connection that this
        single-threaded double never services, and surface the reply as
        "The response ended prematurely" even though the POST already returned 200.
        """
        self.send_response(status)
        self.send_header("Content-Length", "0")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True

    def _record(
        self,
        method: str,
        path: str,
        status: int,
        body: str | None,
        extra: dict[str, Any] | None = None,
    ) -> None:
        auth = self.headers.get("Authorization")
        entry = {
            "ts": _now(),
            "method": method,
            "path": path,
            "status": status,
            "authorization": MASKED_AUTH if auth else None,
            "x_dashscope_async": self.headers.get("X-DashScope-Async"),
            "x_dashscope_oss_resource_resolve": self.headers.get(OSS_RESOLVE_HEADER),
            "content_type": self.headers.get("Content-Type"),
            "body": body,
        }
        if extra:
            entry.update(extra)
        with LOCK:
            STATE.requests.append(entry)

    def _host(self) -> str:
        return self.headers.get("Host") or f"127.0.0.1:{self.server.server_address[1]}"

    def _auth_failure(self) -> str | None:
        """Return the 401 message to send, or None when the key is acceptable."""
        auth = (self.headers.get("Authorization") or "").strip()
        if not auth:
            return "No API-key provided."
        key = auth[len("Bearer "):].strip() if auth.lower().startswith("bearer ") else auth
        if not key or key.lower() in REJECTED_KEYS:
            return "Invalid API-key provided."
        return None

    # ---------- routes ----------

    def do_POST(self) -> None:  # noqa: N802
        path = self.path.split("?", 1)[0]
        try:
            raw = self._read_body()
        except Exception:
            # 读 body 失败就无从回应；把原因打出来，否则客户端只看到空回复。
            traceback.print_exc(file=sys.stderr)
            self.close_connection = True
            return

        # Upload endpoint is not a DashScope API route: it mimics OSS and takes the
        # multipart form verbatim, so it is dispatched before the JSON submit path.
        if path == OSS_UPLOAD_PATH:
            self._handle_oss_upload(raw)
            return

        body_text = raw.decode("utf-8", "replace")

        if path != SUBMIT_PATH:
            self._record("POST", path, 404, body_text or None)
            self._send_json(HTTPStatus.NOT_FOUND, {"code": "NotFound", "message": f"No route {path}"})
            return

        # 1) auth gate first: the real API answers 401 before it looks at headers.
        auth_error = self._auth_failure()
        if auth_error is not None:
            self._record("POST", path, 401, body_text or None)
            self._send_json(
                HTTPStatus.UNAUTHORIZED,
                {"code": "InvalidApiKey", "message": auth_error},
            )
            return

        # 2) async header gate.
        if (self.headers.get("X-DashScope-Async") or "").strip().lower() != "enable":
            self._record("POST", path, 400, body_text or None)
            self._send_json(
                HTTPStatus.BAD_REQUEST,
                {
                    "code": "InvalidParameter",
                    "message": "X-DashScope-Async header is required and must be 'enable'.",
                },
            )
            return

        # 3) oss:// media needs the resource-resolve header, otherwise the real API
        #    cannot dereference the temporary URL.
        oss_urls = self._oss_media_urls(body_text)
        if oss_urls and (self.headers.get(OSS_RESOLVE_HEADER) or "").strip().lower() != "enable":
            self._record(
                "POST",
                path,
                400,
                body_text or None,
                extra={"oss_resolve_required_for": oss_urls},
            )
            self._send_json(
                HTTPStatus.BAD_REQUEST,
                {
                    "code": "InvalidParameter",
                    "message": (
                        f"Header '{OSS_RESOLVE_HEADER}: enable' is required when media[].url "
                        f"uses the oss:// scheme, but it was missing or not 'enable'. "
                        f"Offending url(s): {', '.join(oss_urls)}"
                    ),
                },
            )
            return

        # 4) happy path.
        with LOCK:
            task_id = STATE.next_id()
            STATE.tasks[task_id] = 0
        entry = {
            "ts": _now(),
            "method": "POST",
            "path": path,
            "status": 200,
            "authorization": MASKED_AUTH,
            "x_dashscope_async": "enable",
            "x_dashscope_oss_resource_resolve": self.headers.get(OSS_RESOLVE_HEADER),
            "content_type": self.headers.get("Content-Type"),
            "task_id": task_id,
            "body": body_text,
        }
        if oss_urls:
            # Record whether each oss:// key was actually uploaded through this mock,
            # so QA can tell a real round-trip from a hand-written URL.
            with LOCK:
                entry["oss_keys_uploaded"] = {
                    url: (url[len("oss://"):] in STATE.uploads) for url in oss_urls
                }
        with LOCK:
            STATE.requests.append(entry)
        append_jsonl(entry)
        self._send_json(
            HTTPStatus.OK,
            {
                "request_id": task_id,
                "output": {"task_id": task_id, "task_status": "PENDING"},
            },
        )

    def _oss_media_urls(self, body_text: str) -> list[str]:
        """oss:// URLs found in input.media[].url; empty for anything unparseable."""
        if not body_text.strip():
            return []
        try:
            doc = json.loads(body_text)
        except (ValueError, TypeError):
            return []
        if not isinstance(doc, dict):
            return []
        payload = doc.get("input")
        if not isinstance(payload, dict):
            return []
        media = payload.get("media")
        if not isinstance(media, list):
            return []
        urls: list[str] = []
        for item in media:
            if isinstance(item, dict):
                url = str(item.get("url") or "")
                if url.lower().startswith("oss://"):
                    urls.append(url)
        return urls

    def do_GET(self) -> None:  # noqa: N802
        path = self.path.split("?", 1)[0]

        if path == "/__requests":
            with LOCK:
                payload = {"count": len(STATE.requests), "requests": list(STATE.requests)}
            self._record_get_quiet(path, 200)
            self._send_json(HTTPStatus.OK, payload)
            return

        if path == "/__reset":
            with LOCK:
                STATE.reset()
            self._record_get_quiet(path, 200)
            self._send_json(HTTPStatus.OK, {"ok": True, "reset": True})
            return

        if path == POLICY_PATH:
            query = self.path.split("?", 1)[1] if "?" in self.path else ""
            self._handle_policy(query, self.path)
            return

        if path.startswith(TASK_PREFIX):
            self._handle_task(path[len(TASK_PREFIX):], path)
            return

        if path.startswith("/files/"):
            self._handle_file(path)
            return

        self._record_get_quiet(path, 404)
        self._send_json(HTTPStatus.NOT_FOUND, {"code": "NotFound", "message": f"No route {path}"})
        return

    def do_HEAD(self) -> None:  # noqa: N802
        if self.path.split("?", 1)[0].startswith("/files/"):
            self._handle_file(self.path.split("?", 1)[0], head_only=True)
            return
        self.send_response(HTTPStatus.METHOD_NOT_ALLOWED)
        self.send_header("Content-Length", "0")
        self.end_headers()

    # ---------- handlers ----------

    def _record_get_quiet(self, path: str, status: int) -> None:
        if path.startswith(("/__", "/files/")):
            return  # control + fixture endpoints stay out of the request log
        with LOCK:
            STATE.requests.append(
                {
                    "ts": _now(),
                    "method": "GET",
                    "path": path,
                    "status": status,
                    "authorization": MASKED_AUTH if self.headers.get("Authorization") else None,
                    "body": None,
                }
            )

    def _handle_policy(self, query: str, path: str) -> None:
        """GET /api/v1/uploads?action=getPolicy&model=<model> -> temporary upload policy."""
        params = parse_qs(query)
        action = (params.get("action") or [""])[0]
        model = (params.get("model") or [""])[0]

        auth_error = self._auth_failure()
        if auth_error is not None:
            self._record("GET", path, 401, None)
            self._send_json(
                HTTPStatus.UNAUTHORIZED,
                {"code": "InvalidApiKey", "message": auth_error},
            )
            return

        if action != "getPolicy":
            self._record("GET", path, 400, None)
            self._send_json(
                HTTPStatus.BAD_REQUEST,
                {
                    "code": "InvalidParameter",
                    "message": "query parameter 'action' is required and must be 'getPolicy'.",
                },
            )
            return

        with LOCK:
            upload_dir = STATE.next_upload_dir(model)
        payload = {
            "request_id": f"policy-{uuid.uuid4().hex[:8]}",
            "data": {
                "upload_dir": upload_dir,
                "oss_access_key_id": "MOCK-OSS-AK-NOT-A-REAL-CREDENTIAL",
                "signature": "MOCK-SIGNATURE-NOT-A-REAL-CREDENTIAL",
                "policy": "MOCK-POLICY-BASE64-NOT-A-REAL-CREDENTIAL",
                "x_oss_object_acl": "private",
                "x_oss_forbid_overwrite": "true",
                "upload_host": f"http://{self._host()}{OSS_UPLOAD_PATH}",
            },
        }
        self._record(
            "GET",
            path,
            200,
            None,
            extra={
                "policy_model": model,
                "policy_upload_dir": upload_dir,
            },
        )
        self._send_json(HTTPStatus.OK, payload)

    def _handle_oss_upload(self, raw: bytes) -> None:
        """POST /oss/upload -- OSS form POST; records the key and byte count, keeps nothing."""
        boundary = extract_boundary(self.headers.get("Content-Type") or "")
        if not boundary:
            self._record("POST", OSS_UPLOAD_PATH, 400, "<no multipart boundary>")
            self._send_json(
                HTTPStatus.BAD_REQUEST,
                {
                    "code": "InvalidParameter",
                    "message": "Content-Type must be multipart/form-data with a boundary parameter.",
                },
            )
            return

        parts = parse_multipart(raw, boundary)
        fields = {p["name"]: p for p in parts if p["name"] and not p["filename"]}
        file_part = next((p for p in parts if p["name"] == "file"), None)

        missing = [name for name in OSS_REQUIRED_FIELDS if name not in fields]
        if missing or file_part is None or file_part["size"] == 0:
            problems: list[str] = []
            if missing:
                problems.append("missing form field(s): " + ", ".join(missing))
            if file_part is None:
                problems.append("missing form field: file")
            elif file_part["size"] == 0:
                problems.append("form field 'file' is empty")
            self._record(
                "POST",
                OSS_UPLOAD_PATH,
                400,
                None,
                extra={
                    "oss_upload": {
                        "rejected": problems,
                        "field_names": [p["name"] for p in parts],
                        "bytes": file_part["size"] if file_part else 0,
                    }
                },
            )
            self._send_json(
                HTTPStatus.BAD_REQUEST,
                {"code": "InvalidParameter", "message": "; ".join(problems)},
            )
            return

        key = (fields["key"]["text"] or "").strip()
        size = file_part["size"]
        with LOCK:
            STATE.uploads[key] = size

        summary = {
            "key": key,
            "filename": file_part["filename"],
            "bytes": size,
            "content_type": file_part["content_type"],
            "field_names": [p["name"] for p in parts],
            "field_count": len(parts),
            "oss_access_key_id": fields["OSSAccessKeyId"]["text"],
            "x_oss_object_acl": (fields.get("x-oss-object-acl") or {}).get("text"),
            "x_oss_forbid_overwrite": (fields.get("x-oss-forbid-overwrite") or {}).get("text"),
            "success_action_status": (fields.get("success_action_status") or {}).get("text"),
        }
        self._record("POST", OSS_UPLOAD_PATH, 200, None, extra={"oss_upload": summary})
        append_jsonl({"ts": _now(), "method": "POST", "path": OSS_UPLOAD_PATH, "status": 200, "oss_upload": summary})
        self._send_empty(HTTPStatus.OK)

    def _handle_task(self, task_id: str, path: str) -> None:
        task_id = task_id.strip("/")
        with LOCK:
            known = task_id in STATE.tasks
            if known:
                STATE.tasks[task_id] += 1
                polls = STATE.tasks[task_id]
            else:
                polls = 0

        if not known:
            self._record_get_quiet(path, 404)
            self._send_json(
                HTTPStatus.NOT_FOUND,
                {"code": "NotFound", "message": f"Task {task_id} does not exist."},
            )
            return

        if polls < STATE.success_after:
            payload = {
                "request_id": task_id,
                "output": {"task_id": task_id, "task_status": "RUNNING"},
            }
            self._record_get_quiet(path, 200)
            self._send_json(HTTPStatus.OK, payload)
            return

        host = self.headers.get("Host") or f"127.0.0.1:{self.server.server_address[1]}"
        payload = {
            "request_id": task_id,
            "output": {
                "task_id": task_id,
                "task_status": "SUCCEEDED",
                "video_url": f"http://{host}/files/sample.mp4",
            },
            "usage": {"video_count": 1, "duration": 5},
        }
        self._record_get_quiet(path, 200)
        self._send_json(HTTPStatus.OK, payload)

    def _handle_file(self, path: str, head_only: bool = False) -> None:
        target = STATE.fixture
        # /files/<name> always serves the configured fixture, whatever the name;
        # that keeps sample.mp4 stable while fixtures change.
        if not os.path.isfile(target):
            self._send_json(
                HTTPStatus.NOT_FOUND,
                {"code": "NotFound", "message": f"Fixture missing: {target}"},
            )
            return

        total = os.path.getsize(target)
        rng = self.headers.get("Range")
        span = parse_range(rng, total) if rng else None

        if rng and span is None:
            self.send_response(HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
            self.send_header("Content-Range", f"bytes */{total}")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        if span is None:
            start, end, status = 0, total - 1, HTTPStatus.OK
        else:
            start, end, status = span[0], span[1], HTTPStatus.PARTIAL_CONTENT

        length = end - start + 1
        self.send_response(status)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        if status == HTTPStatus.PARTIAL_CONTENT:
            self.send_header("Content-Range", f"bytes {start}-{end}/{total}")
        self.end_headers()
        if head_only:
            return

        with open(target, "rb") as fh:
            fh.seek(start)
            remaining = length
            while remaining > 0:
                chunk = fh.read(min(64 * 1024, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)


def main(argv: list[str] | None = None) -> int:
    global STATE

    here = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.abspath(os.path.join(here, "..", ".."))

    ap = argparse.ArgumentParser(description="DashScope video-synthesis mock server")
    ap.add_argument("--port", type=int, default=8787)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument(
        "--fixture",
        default=os.path.join(repo_root, "fixtures", "long-20s.mp4"),
        help="mp4 served at /files/sample.mp4",
    )
    ap.add_argument(
        "--success-after",
        type=int,
        default=4,
        help="poll number that flips to SUCCEEDED (default 4: polls 1-3 are RUNNING)",
    )
    ap.add_argument(
        "--log",
        default=os.path.join(here, "last-requests.jsonl"),
        help="jsonl file for captured submit bodies",
    )
    args = ap.parse_args(argv)

    fixture = os.path.abspath(args.fixture)
    if not os.path.isfile(fixture):
        sys.stderr.write(f"[mock] WARNING fixture not found: {fixture}\n")

    STATE = State(fixture=fixture, success_after=args.success_after)
    STATE.log_path = os.path.abspath(args.log)
    os.makedirs(os.path.dirname(STATE.log_path), exist_ok=True)
    with open(STATE.log_path, "a", encoding="utf-8"):
        pass

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    host, port = server.server_address[0], server.server_address[1]
    sys.stderr.write(
        "[mock] DashScope mock listening on http://%s:%d\n"
        "[mock] submit : POST %s\n"
        "[mock] poll   : GET  %s<task_id>  (1-3 RUNNING, %d+ SUCCEEDED)\n"
        "[mock] policy : GET  %s?action=getPolicy&model=<model>\n"
        "[mock] upload : POST %s  (multipart/form-data, OSS form POST)\n"
        "[mock] fixture: %s (%d bytes) at /files/sample.mp4\n"
        "[mock] log    : %s\n"
        % (
            host,
            port,
            SUBMIT_PATH,
            TASK_PREFIX,
            STATE.success_after,
            POLICY_PATH,
            OSS_UPLOAD_PATH,
            fixture,
            os.path.getsize(fixture) if os.path.isfile(fixture) else 0,
            STATE.log_path,
        )
    )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        sys.stderr.write("[mock] shutting down\n")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    raise SystemExit(main())
