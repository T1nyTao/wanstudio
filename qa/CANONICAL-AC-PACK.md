# Canonical AC Pack — Wan3.0 binding B (2026-10-22)

**Source cuts (SoT):**
- **SoT = `CUT-2026-10-22-Wan3.0-BINDING.md`** (BINDING — approved by owner 2026-10-22). AC-01…AC-09 and AC-L01…L11 are taken from this file **verbatim in ID and meaning**.
- Additive binding: `CUT-2026-10-22-Wan3.0-CUT3-MODES.md` (AC-M01…M09; does **not** supersede B, AC-L* remain in force).
- `CUT-2026-10-22-Wan3.0-MVP.md` is **SUPERSEDED** by BINDING — kept for history only, never a numbering source. `CUT-2026-10-22-Wan3.0-cut2-longvideo-draft.md` is likewise historical.

**Status:** SoT is **BINDING** (owner approved B). This pack is numbering-aligned to BINDING as of 2026-10-22 (see change log). QA may gate on the AC IDs below; per-AC status is **not** PASS by default — see `PLAN-2026-10-22-NEXT-AI.md` §2 for the honest per-AC status table. **Never soft-pass:** missing proof ⇒ FAIL, missing real Key ⇒ BLOCKED.

**Deploy under test:** WinUI app from `C:\Users\<user>\Desktop\Wan3.0` (local desktop; no Production URL yet).

**Fixtures SoT:** `C:\Users\<user>\Desktop\Wan3.0\qa\fixtures.json`

## BLOCK suite

| ID | Criterion | Proof | Severity |
|----|-----------|-------|----------|
| AC-01 | App launches to main UI without crash | screenshot | BLOCK |
| AC-02 | New task: enter prompt, pick model/resolution, can submit | screenshot | BLOCK |
| AC-03 | In-progress shows progress and cancel works | screenshot | BLOCK |
| AC-04 | Downloaded video under Wan3.0 plays/opens (result screen playable + saved into Wan3.0) | screenshot | BLOCK |
| AC-05 | API Key save + switch persists (survives restart); Key not in repo/logs | screenshot + git grep/scan note | BLOCK |
| AC-06 | Both models `wan3.0-video-prime` / `wan3.0-video` each submit successfully once | screenshot | BLOCK |
| AC-07 | Valid prompt → task_id → poll SUCCEEDED → video_url | API JSON (redact Authorization) | BLOCK |
| AC-08 | Missing/invalid Key or task FAILED shows clear error; no false success | screenshot | BLOCK |
| AC-09 | Eng/SWE design self-check: no open P0 on happy path (hierarchy / whitespace / primary-path usability); error states clear | Eng checklist note + screenshot | BLOCK |

**Refuse soft-pass.** Eng CI CLEAN ≠ AC PASS.

## WARN (non-blocking)

- Awwwards/Webby/FWA aspiration — vision only, not BLOCK.
- Smart recommended segment mode — next cut.


## Change log
- 2026-10-22: AC-09 reassigned from Critiquito sign-off to Eng/SWE design self-check (owner: Critiquito out). Binding cut must match via @PM bot.



## Long video / continue (B BINDING updated 2026-10-22) — AC-L*

**SoT:** `CUT-2026-10-22-Wan3.0-BINDING.md`

| ID | Criterion | Proof | Severity |
|----|-----------|-------|----------|
| AC-L01 | Fixed-seconds 1–15s adjustable + shot-cut mode; default fixed seconds | screenshot | BLOCK |
| AC-L02 | Segment preview with scrub/edit; no synthesis before confirm | screenshot + API/log | BLOCK |
| AC-L03 | Thumbnail + checkbox; Generate disabled / no fake confirm / no submit when none checked | screenshot + API/log | BLOCK |
| AC-L04 | Only checked segments generate; per-segment status; fail can retry | screenshot | BLOCK |
| AC-L05 | Successful segments enter video pool | screenshot | BLOCK |
| AC-L06 | Official continue (must-do): pick an **already-generated** clip **from the video pool** as `first_clip`, submit via continue-capable i2v (e.g. wan2.7-i2v) on `video-synthesis` with `media:[{type:"first_clip", url}]` (optional `last_frame`); output contains the original clip. **Not** default ffmpeg. **Scope narrowed 2026-10-22:** the first-clip source is the **generated** video pool only — locally **imported** clips are excluded (`WanStudio/Services/VideoPool.cs:50`, `CanContinue => !IsImported && …`). This is an owner product decision, **not** a defect. | screenshot + API JSON showing media type first_clip (redact Auth) **+ the oss:// upload chain**: getPolicy request/response, the multipart upload, and the resulting `oss://` URL appearing in the submit body, **with the `X-DashScope-OssResourceResolve: enable` request header shown** | BLOCK |
| AC-L07 | Single continue result playable; duration inside documented window (~2–15s); longer video = chain the next official continue. Note: `duration` is the **output total**, not the continuation delta (official example: 3s input + `duration=15` → 12s continued, 15s output, billed 15s). | screenshot + fixture id **+ the same oss:// upload chain / `X-DashScope-OssResourceResolve` header evidence as AC-L06** | BLOCK |
| AC-L08 | Local hard-stitch if kept: default OFF; explicit enable; empty-pool empty state; never silent stitch | screenshot | BLOCK |
| AC-L09 | Hard-stitch preview + seam marks; fail red; UI must not label hard-stitch as continue/seamless | screenshot | BLOCK |
| AC-L10 | Model list includes continue-capable i2v (e.g. wan2.7-i2v); when no such model / no first clip / no prompt → continue entry disabled **with reason written** | screenshot | BLOCK |
| AC-L11 | Eng self-check: continue copy never markets hard-stitch as official continue | checklist + screenshot | BLOCK |

**Refuse:** default ffmpeg hard-stitch marketed as official continue / seamless → FAIL. Official continue without `first_clip` API proof → AC-L06 FAIL.


## Cut-3 modes (BINDING additive) — AC-M*

**SoT:** `CUT-2026-10-22-Wan3.0-CUT3-MODES.md` (does **not** supersede B; AC-L* remain)

| ID | Criterion | Proof | Severity |
|----|-----------|-------|----------|
| AC-M01 | New page can switch modes; current mode clear | screenshot | BLOCK |
| AC-M02 | Image-to-video: pick reference image + prompt; missing image disables Generate with reason | screenshot | BLOCK |
| AC-M03 | Image-to-video happy path: submit → task → SUCCEEDED → openable file (needs Key) | screenshot + API JSON (redact Auth) | BLOCK |
| AC-M04 | Character replace: required refs present; missing disables Generate with reason | screenshot | BLOCK |
| AC-M05 | Character replace: one successful generate through Tasks/download (needs Key; fields per live API) | screenshot + API JSON (redact) | BLOCK |
| AC-M06 | Motion ref: action video + subject; missing disables Generate with reason | screenshot | BLOCK |
| AC-M07 | Motion ref: one successful generate through Tasks/download (needs Key) | screenshot + API JSON (redact) | BLOCK |
| AC-M08 | Mode failures / invalid assets show clear error; no false success | screenshot | BLOCK |
| AC-M09 | Eng self-check: no open P0 on mode switch hierarchy/whitespace/disable reasons; Lead glance | checklist + screenshot | BLOCK |

**QA note:** Without Key, AC-M03/05/07 stay BLOCKED (missing Key) — never soft-PASS. B open FAILs remain until proof.

## Change log
- 2026-10-22: Appended Cut-3 AC-M01…M09 from BINDING cut.

- 2026-10-22: Synced AC-L01…L11 official first_clip continue; forbid ffmpeg-as-continue.

- **2026-10-22 (eng-docs, numbering alignment):** Fixed the AC-04/05/06/07 rotation against `CUT-2026-10-22-Wan3.0-BINDING.md`. The pack previously had 04=Key, 05=models, 06=task_id, 07=download; BINDING (and `PLAN-2026-10-22-NEXT-AI.md` §2) define **04=result playable+download, 05=Key persist, 06=both models submit, 07=task_id→SUCCEEDED→video_url**. Rows renumbered to BINDING; AC-08/AC-09 were already correct. Also replaced the header's "source cuts = MVP.md + cut2-draft" with **BINDING as SoT** (MVP is marked SUPERSEDED inside its own file) and restated AC-L06 to BINDING's exact scope ("already-generated **or imported** clip"), AC-L07 to "single continue result playable + ~2–15s band", AC-L10 to "disabled **with reason written**". No AC ID was added or removed.

- **2026-10-22 (eng-design, chain-continue narrowing):** AC-L06 first-clip source narrowed to the **generated video pool only** — local imports excluded per owner decision (`VideoPool.cs:50`), recorded as product decision rather than defect; the "Open discrepancy for PM (AC-L06)" note is marked **RESOLVED** for history. AC-L06 and AC-L07 evidence requirements extended to require the **oss:// upload chain** (getPolicy → multipart → `oss://` in the submit body) **and the `X-DashScope-OssResourceResolve: enable` header**. AC-L07 also now spells out that `duration` is the **output total**, not the continuation delta. Companion doc: `qa/CONTINUE-DESIGN-2026-10-22.md`. No AC ID added or removed; no status promoted (AC-L06 remains "真机半", AC-L07 remains BLOCKED).

### AC-L06 first-clip source — **RESOLVED 2026-10-22 (owner decision), kept for history**
`WanStudio/Services/VideoPool.cs:50` defines `CanContinue => !IsImported && !string.IsNullOrWhiteSpace(RemoteUrl)`, so a **user-imported** clip is explicitly refused as `first_clip` (reason string at `VideoPool.cs:58`). BINDING AC-L06 previously read "已生成/导入 clip".

**Resolution:** the owner narrowed AC-L06 to **generated pool only**; local import is excluded by product decision, **not** a defect. The AC-L06 row above now carries the narrowed wording, so QA may gate on it as written. Design rationale and the full causal chain (why an upload channel is required at all) are in `qa/CONTINUE-DESIGN-2026-10-22.md`.

**Still not PASS-able on pool-only evidence:** the upload channel (`oss://` + `X-DashScope-OssResourceResolve`) must appear in the proof, and real-machine evidence is still missing (no real Key) — AC-L06 stays at "真机半", AC-L07 stays BLOCKED.

---

## 2026-10-22 UI-driven evidence run (Lead) — what moved and what did not

Full report: `qa/run-2026-10-22-chain-continue-e2e.md`

| AC | Before | After | Evidence |
|----|--------|-------|----------|
| AC-L03 | PASS (screenshot) | **PASS (closed)** | zero-select disabled state **plus** `/__requests` count=0 — `qa/lv-L03-requests.json` |
| AC-L04 | TODO (unverified) | **PASS** | UI-driven: 2 checked → exactly 2 POSTs, `media[0].type=first_frame`, distinct frames — `qa/lv-L04-requests.json` |
| AC-L05 | TODO (unverified) | **PASS** | pool header went 0 → 2 with correct `图生 · 段 NN` labels — `qa/lv-L05-pool-2.png` |
| AC-L06 | PARTIAL (protocol only, CLI) | **PARTIAL (UI-driven protocol)** | UI click path produced `first_clip` + `oss://` + `X-DashScope-OssResourceResolve: enable`. **Real-machine half still BLOCKED.** |
| AC-08 | PARTIAL (UI unverified) | **PASS** | bad key → 401, task marked 失败, retry enabled, open/save-as disabled, no false success — `qa/lv-AC08-badkey-401.png` |

**Still BLOCKED (no real Key — do not soft-pass):** AC-06, AC-07, AC-L07, AC-M03, AC-M05, AC-M07.
**Still PARTIAL:** AC-L01 (shot-cut screenshot), AC-L02 (preview screenshot), AC-L09/L11 (hard-stitch artifact + screenshot), AC-L10 (screenshot).

**Environment caveat that applies to every item above:** the app was run from `Desktop\verify-wanstudio\` (same binary, outside the workspace tree) because an exe inside the workspace tree cannot spawn ffmpeg to write files in this execution sandbox. Evidence is still from the real UI, but the run location must be stated whenever this report is cited. Credential writes must also happen from the app's own directory tree (PasswordVault is scoped per executable tree for unpackaged apps).

## Change log (continued)
- **2026-10-22 (Lead, UI-driven run):** AC-L03/L04/L05/AC-08 moved to PASS on UI-driven evidence; AC-L06 moved from CLI-only protocol proof to UI-driven protocol proof (real-machine half still BLOCKED). Also fixed 7 real defects this round (local media sent as `file://`, illegal `reference_image`/`reference_video` types, `duration` misread as continuation delta, missing upload channel, missing `oss://` resolve header, TasksPage QA seed backdoor, non-persistent pool). No AC ID added or removed; no BLOCKED item promoted.
- **2026-10-22 (Lead, multi-reference + Clip Bin):** **Corrected a wrong conclusion from the previous entry.** `reference_image` / `reference_video` are NOT illegal types — they belong to **`wan2.7-r2v` (reference-to-video)**, not `wan2.7-i2v`. Downgrading them to `first_frame` / `first_clip` had silently removed the multi-character capability. Added `wan2.7-r2v` to the model table (reference materials ≤5, each material holds a single character, so **multi-person = multiple `reference_image`**), `SubmitReferenceAsync`, `ReferenceCutter` (auto-split long reference videos into ≤30s chunks, official `reference_video` limit is 1–30s), `ClipKind` on the pool, and a card-grid `ClipBinPanel` holding both cut reference clips and generated results in one library. r2v parameters normalised to the official vocabulary (`720P`/`1080P`, `16:9`/`9:16`/`1:1`/`4:3`/`3:4`; `duration` 2–10 with video reference, 2–15 without). **`reference_voice` (voice/timbre reference) also wired**: it is an optional field *inside* a `media` item (not its own type), paired to reference materials in order, official limits wav/mp3 + 1–10s + ≤15MB, and it overrides a reference video's own audio track. Evidence: `qa/run-2026-10-22-multiref-clipbin.md`. **AC-M05 / AC-M07 remain BLOCKED** (real-machine multi-character behaviour and timbre transfer unverified — only the request shape is proven); card preview not implemented yet.



