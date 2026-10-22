# QA run — Wan3.0 P2 screenshots

**Date:** 2026-10-22 Asia/Shanghai  
**Cut:** CUT-2026-10-22-Wan3.0-BINDING.md  
**Evidence reviewed:** `p2-home.png`, `p2-tasks.png`, `p2-settings.png`  
**STATUS:** FAIL

## Results

| ID | Result | Notes |
|----|--------|-------|
| AC-01 | PASS (prior) | p0-scaffold / p0-desktop |
| AC-02 | FAIL | `p2-home.png` is a near-blank white window; no visible prompt / model / resolution / duration / Generate controls |
| AC-03 | FAIL | `p2-tasks.png` does not show Tasks polling/cancel UI (desktop+chat capture) |
| AC-04…07 | NOT RUN | need live Key + successful submit evidence |
| AC-08 | FAIL | no clear missing-Key / FAILED error-state screenshot |
| AC-09 | FAIL | cannot self-check primary-path usability from blank/wrong shots; Eng Lead visual OK does not replace screenshot proof |
| AC-L* | NOT RUN | P3+ |

## Refuse
Will not soft-pass on verbal “P2 done” or green `dotnet build`. Re-capture in-app screenshots showing the actual controls.

```yaml
status: FAIL
reason: p2_screenshots_do_not_show_claimed_ui
ac_02: FAIL
ac_03: FAIL
ac_08: FAIL
ac_09: FAIL
run_id: run-2026-10-22-p2-partial
```
