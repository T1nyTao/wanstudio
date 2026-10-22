#!/usr/bin/env bash
# ad-hoc self test for tools/mock-dashscope; output pasted into the QA run log
set -u
B=http://127.0.0.1:8787
SUBMIT=$B/api/v1/services/aigc/video-generation/video-synthesis
BODY='{"model":"wan3.0-video-prime","input":{"prompt":"qa selftest cat"},"parameters":{"resolution":"480P","ratio":"adaptive","duration":5}}'
# NOTE: keep temp files on relative paths; the curl on PATH is native Windows curl,
# which resolves /tmp differently from Git Bash.
TMPDIR_REL=qa/_tmp
mkdir -p "$TMPDIR_REL"

echo "### [0] reset"
curl -s $B/__reset; echo; echo

echo "### [1] POST submit without Authorization"
curl -s -i -X POST "$SUBMIT" -H 'Content-Type: application/json' \
  -H 'X-DashScope-Async: enable' --data "$BODY" | sed -e 's/\r$//' | grep -E '^(HTTP/|Content-Type:|\{"|$)' ; echo

echo "### [2] POST submit with Bearer bad-key"
curl -s -i -X POST "$SUBMIT" -H 'Authorization: Bearer bad-key' -H 'Content-Type: application/json' \
  -H 'X-DashScope-Async: enable' --data "$BODY" | sed -e 's/\r$//' | grep -E '^(HTTP/|\{"|$)' ; echo

echo "### [3] POST submit with key but missing X-DashScope-Async"
curl -s -i -X POST "$SUBMIT" -H 'Authorization: Bearer sk-qa-test' -H 'Content-Type: application/json' \
  --data "$BODY" | sed -e 's/\r$//' | grep -E '^(HTTP/|\{"|$)' ; echo

echo "### [4] POST submit happy path"
RESP=$(curl -s -X POST "$SUBMIT" -H 'Authorization: Bearer sk-qa-test' -H 'Content-Type: application/json' \
  -H 'X-DashScope-Async: enable' --data "$BODY")
echo "raw: $RESP"
TASK=$(printf '%s' "$RESP" | python -c "import json,sys;print(json.load(sys.stdin)['output']['task_id'])")
echo "task_id=$TASK"; echo

echo "### [5] poll GET /api/v1/tasks/$TASK x4"
for i in 1 2 3 4; do
  printf 'poll#%d -> ' "$i"
  curl -s "$B/api/v1/tasks/$TASK"; echo
done
echo

echo "### [6] poll unknown task id"
curl -s -i "$B/api/v1/tasks/does-not-exist" | sed -e 's/\r$//' | grep -E '^(HTTP/|\{"|$)'; echo

echo "### [7] fetch video_url (full)"
curl -s -o "$TMPDIR_REL/sample.mp4" -w 'status=%{http_code} bytes=%{size_download} type=%{content_type}\n' \
  "$B/files/sample.mp4"
ls -l "$TMPDIR_REL/sample.mp4" | awk '{print "local bytes="$5}'
cmp -s "$TMPDIR_REL/sample.mp4" fixtures/long-20s.mp4 && echo "byte-identical to fixtures/long-20s.mp4" || echo "MISMATCH vs fixture"
echo

echo "### [8] fetch video_url (Range: bytes=0-1023)"
curl -s -o "$TMPDIR_REL/sample-range.bin" -D "$TMPDIR_REL/range-headers.txt" -w 'status=%{http_code} bytes=%{size_download}\n' \
  -H 'Range: bytes=0-1023' "$B/files/sample.mp4"
grep -i -E 'content-range|accept-ranges|content-length' "$TMPDIR_REL/range-headers.txt" | tr -d '\r'; echo

echo "### [9] last-requests.jsonl (Authorization masked)"
cat tools/mock-dashscope/last-requests.jsonl; echo

echo "### [10] GET /__requests"
curl -s "$B/__requests" | python -c "
import json,sys
d=json.load(sys.stdin)
print('count=%d' % d['count'])
for r in d['requests']:
    print('  %s %-70s -> %s  auth=%s' % (r['method'], r['path'], r['status'], r['authorization']))
"
