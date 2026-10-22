# QA run — Wan3.0 P2 recheck (post form restore)

**Date:** 2026-10-22 Asia/Shanghai  
**Evidence:** `p2-home.png`, `p2-tasks.png`, `p2-settings.png` (mtime ~01:21)  
**STATUS:** FAIL (suite incomplete; live Key path not exercised)

## Results

| ID | Result | Evidence | Notes |
|----|--------|----------|-------|
| AC-01 | PASS | prior + current shells | launches |
| AC-02 | PASS | p2-home.png | prompt/model/res/ratio/duration + Generate visible |
| AC-03 | FAIL | p2-tasks.png | empty state + Cancel/Retry/Open present but **no in-progress poll proof**; cancel-while-running not shown |
| AC-04 | NOT RUN | p2-settings.png | UI for Key save exists; persist + no-leak not proven |
| AC-05…07 | NOT RUN | — | need Tiny Key + live SUCCEEDED |
| AC-08 | PASS | p2-home + p2-settings | missing Key: Generate disabled + reason; settings shows 未保存 Key. Invalid Key / task FAILED still need a live fail shot |
| AC-09 | PASS* | three shots | primary path readable; *Lead one-glance note still expected in chat |
| AC-L* | NOT RUN | — | P3 |

## Cut-3
No binding yet — QA will not invent ACs. Waiting `CUT-…-CUT3-MODES.md` from PM.

```yaml
status: FAIL
ac_02: PASS
ac_03: FAIL
ac_08: PASS
ac_09: PASS
run_id: run-2026-10-22-p2-recheck
```
