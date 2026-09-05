# Start here — hand-off for the next agent (2026-09-05)

Branch `codex/native-metal-backend`, a Ryujinx fork with a native Metal backend, tuned
for *The Legend of Zelda: Tears of the Kingdom* 1.4.2 on an M1 Max. The user is not a
graphics engineer; they play, report what they see, and decide what to switch. Talk to
them in Chinese.

## Latest verified sunlight fix: v445 (2026-09-05)

Current verified terminal build: `artifacts/terminal/Ryujinx-metal-v445-prepass-rebind`.
Keep the original v426 directory as the read-only rollback baseline. v445 is a clean
`910872594a9ec3897c1376a5dc006042ab8d98b7` build with only the prepass rebind fix (enabled by default) and the previously
identified cached-program FragmentOutputMap correction. Exact patch, source hashes,
binary SHA-256 and signature are under `artifacts/diagnostics/fog-2026-09-05/v445-*`.
The two source fixes are committed with this hand-off. The binary, videos, launcher,
and diagnostic manifests remain local artifacts, not Git contents. The other working-tree
probes are excluded from the fix commit.

The render-state dirty check ran before resource preparation. Resolving buffers during
preparation can end the render encoder (observed Render -> Blit) or change command
buffers. The new encoder then needs all bindings prepared again, including sets that
were clean before the transition. v445 detects the transition and prepares the bindings again.

Evidence: v443 same-process mirrors on/off/on, 6000 PRESENT frames per arm, yielded
26 / 0 / 35 jump edges (not flash counts). v444 same-binary rebind on vs detection-only
control yielded 0 / 16 edges; on ran at 49.8 FPS. The clean v445 then passed 600 seconds
of video plus 180 seconds after the user reloaded the manual save: no short brightness
excursions in the four scene patches. The same detector found 11 events in the bad
control video. Long-run average was 49.6 FPS; RSS did not grow continuously.

Last verified sole game PID: 23611, v445, window 91462; recheck before actions. The user
switched input to DualSense and audio to AudioToolbox. Preserve those settings; keyboard
scripts no longer operate Player 1. The final reload was performed by the user.

This closes the tested wall/ground sunlight issue at the Nachoyah Shrine manual save.
It does not establish a fix for the older hot-spring cloud-band dropout or the driver's
indexed-draw crash. Keep those issues open. Finder/.app delivery was not validated.

## Earlier hand-off update (2026-09-05, sunlight investigation)

The current sunlight issue is **not fixed and its root cause is unconfirmed**.
Read the latest entries of
`artifacts/diagnostics/fog-2026-09-05/investigation.md` after the two documents below.
They contain the trial history and artifact paths; do not repeat failed experiments.

- Protected player build remains v426. Last observed sole game PID was **8381**, running
  `artifacts/terminal/Ryujinx-metal-v434-hdr-feedback-snapshot/Ryujinx` with
  `RYUJINX_METAL_HDR_FEEDBACK_SNAPSHOT=0`, `RYUJINX_METAL_SYNC_STRICT=3`.
  Recheck the live PID/executable before any action; this is a diagnostic build, not a fix.
- v434 HDR copying failed its comparison (off 11 / on 16 events per 90 seconds).
  Strict sync also failed: the user saw flashes, then a 90-second recording detected 19;
  the contact sheet confirms one-frame sunlight. Quiet first loads prove nothing.
- The next prepared diagnostic is a read-only comparison of binding-cache Texture/Sampler
  objects against `Pool.GetCachedItem`, supplementing the existing descriptor audit.
  Changes in `BindingCacheAudit.cs` and its caller compiled with zero warnings/errors,
  but **have not been packaged or run in the game**. No new runtime result exists.
- Four exported lighting fragment shaders passed Metal uninitialized-variable diagnostics;
  a deliberately broken control failed as expected. This does not prove shader semantics.
- No fix delivered, no commits made. Preserve all pre-existing dirty changes; their backup
  is `artifacts/diagnostics/fog-2026-09-05/baseline/pre-existing-probes.patch`.
- Current-round user authorization covered switching games and PID-focused Quartz scripts.
  Keep one instance, close only a verified PID, preserve configuration and manual saves.
  Use `fog_keys.py` with fail-fast shells and inspect UI before dependent actions.
  Record the verified window (`fog_soak.py --window`), not a display number; one display-2
  recording captured the wrong screen. Last observed window was 88681; recheck it.

## Read in this order

1. `docs/METAL_BUG_LEDGER.md` — every picture/stability bug: cause, fix, switch, open ones.
2. `docs/METAL_PERF_HANDOFF_2026-09-05.md` — the performance line, what is on by default,
   what is closed and why, the two open issues with their evidence.
3. Commit messages on this branch — each carries the reasoning and the numbers.
4. Older long-form investigations only when a ledger line points you there.

## The state of things

- Current verified build: `artifacts/terminal/Ryujinx-metal-v445-prepass-rebind`.
  Read-only rollback baseline: `artifacts/terminal/Ryujinx-metal-v426-crash-ring2`.
  The original long-running session recorded during this investigation used v425.
- Working tree: uncommitted GPU binding/write probes, probe-attribution corrections,
  and a one-line Metal cached-program output-mask correction. v429 contains only
  the output-mask correction on HEAD; it still flickers. v430 adds a read-only
  binding-cache descriptor audit; v431/v432 provide GPU output and CPU input traces.
  Those earlier versions are diagnostic candidates, not the v445 sunlight fix. See the current sunlight investigation in the perf
  hand-off and the local build manifests before claiming what a candidate contains.
- Two open bugs in play: the fog/cloud single-frame dropout and the driver segfault in
  `drawIndexedPrimitives`. The ledger says where each trail ends.
- Game-side config the user chose: 1920x1080, `MaxFPS = 50` in
  `~/Library/Application Support/Ryujinx/sdcard/UltraCam/TOTK/Config/maxlastbreath.ini`
  (rewritten by the mod on every exit). `Config.json` is theirs; do not "fix" it.

## Rules the user set (breaking them cost trust)

- **Ask before starting, restarting or closing the game** — including instances you
  launched yourself, because the user takes them over. Kill by PID
  (`kill $(cat <your pid file>)`), never `pkill -x Ryujinx`.
- **Never run two instances at once** (shared shader cache and config get trampled).
- Publish and switch are two separate acts; publishing can be silent, switching cannot.
- When the user says an artefact is there, it is there. When your instrument says it is
  not, suspect the instrument first (screenshot bursts cannot see one-frame events;
  record video with `screencapture -x -v -V 30 -D 2 out.mov`, 60 fps, and diff frames).
- Any change to the game's ini or the emulator config is an intervention: log it, A/B it.
- Performance conclusions are scene-scoped. Say which save and which view.

## How to build a candidate

```bash
# from a clean HEAD (the script refuses a dirty src/Ryujinx.Graphics.Metal)
zsh tools/handoff-2026-09-05/build_v426.sh      # edit CAND/SNAP names first
```
It archives HEAD into a scratch snapshot, `dotnet publish` (osx-arm64, self-contained,
`-p:Version=1.3.3+local-metal-<name>`), then ad-hoc codesigns with
`distribution/macos/entitlements.xml`. Output: `artifacts/terminal/Ryujinx-metal-<name>`.
Every build logs its version string; every 120-frame stats block ends with `config: ...`
naming the switches it ran under. Check both before trusting a measurement.

## How to run it

- For the user: `cd <candidate> && ./Ryujinx --graphics-backend Metal "<xci>"` boots to
  the title screen in about 75 s; they load their save. ROM path is in `tools/drive_in.sh`.
- **Do not run `tools/drive_in.sh` unchanged: it contains name-based process closing.**
  The current local replacement is `tools/handoff-2026-09-05/launch_fog.py`, which
  refuses to launch if any Ryujinx instance exists and never closes one. User-authorized
  controls use `fog_pid.swift` with an explicitly verified PID. Consult the latest local
  investigation entry for the current PID; historical entries contain stopped PIDs.
- Historical unattended workflow: `SAVE_ROW=index SAVE_INDEX=<row> tools/drive_in.sh <candidate> <log> [ENV=val ...]`
  loads the save `row` down from the cursor in the load list (row 0 = newest) and prints
  `IN GAMEPLAY` plus the player position it read from the game's play reports
  (`"PlayerPosY"` in the log). Look at `/tmp/drive_savelist.png` to see which row is which
  — the manual save is the entry without the Autosave badge. `tools/handoff-2026-09-05/reload_rotate.sh`
  wraps it with a camera turn (right stick = IJKL keys, A = Z).
- Logs: `~/Library/Logs/Ryujinx/` keeps the last three sessions; use `grep -a` (binary bytes).
- Hot switches, re-read once a frame: `/tmp/ryujinx-metal-barrier-scope` (`hazard`/`all`),
  `/tmp/ryujinx-metal-skip-self-split` (`0`), `/tmp/ryujinx-metal-fb-fetch` (`0`),
  `/tmp/ryujinx-metal-tile-snapshot` (`1`). Only switch to the *more conservative* setting
  while the user is playing.

## Instruments (local, `tools/` is git-ignored)

| Script | Use |
|---|---|
| `crash_ring_read.py [--last N]` | decode `/tmp/ryujinx-metal-crash-ring.bin` after a segfault |
| `video_ab.sh <tag> [rounds] [secs]` | alternate a hot switch while recording the screen |
| `flash_count.py`, `pop_index.py`, `band_events.py`, `band_trace.py` | per-frame analysis of a recording: whole-frame flashes, region pops, cloud-band events |
| `wait_picture.py <log>` | per-block sync-wait / GPU-idle picture from a session log |
| `watchbind_report.py <log>` | binding-identity excursions from the `RYUJINX_WATCH_*` probe |
| `tools/dense_scene_report.py`, `tools/pso_report.py`, `tools/hazard_report.py`, `tools/fetch_ab_report.py` | earlier report scripts, usage in their headers |
| `tools/mslscan/` | offline reader of the MSL shader cache; proves whether a shader's sample is same-pixel |

## Where to start tomorrow

1. If the game crashed: `python3 tools/handoff-2026-09-05/crash_ring_read.py --last 200`
   and the newest `~/Library/Logs/DiagnosticReports/Ryujinx-*.ips`.
2. For the fog dropout: read the correction in open issue 1 first. The old watched
   program does not use binding 136; its table-write evidence was a probe attribution
   error. Do not use it as a proven cause. The newer Nachoyah Shrine manual save also
   exposes one-frame sunlight loss on walls/ground, confirmed by the user; keep that
   symptom separate from the older hot-spring cloud-band report (player Y = 263).
3. Do not reopen the closed performance lines in the perf hand-off without new evidence.
