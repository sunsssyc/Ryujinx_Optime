# Start here — hand-off for the next agent (2026-09-05)

Branch `codex/native-metal-backend`, a Ryujinx fork with a native Metal backend, tuned
for *The Legend of Zelda: Tears of the Kingdom* 1.4.2 on an M1 Max. The user is not a
graphics engineer; they play, report what they see, and decide what to switch. Talk to
them in Chinese.

## Read in this order

1. `docs/METAL_BUG_LEDGER.md` — every picture/stability bug: cause, fix, switch, open ones.
2. `docs/METAL_PERF_HANDOFF_2026-09-05.md` — the performance line, what is on by default,
   what is closed and why, the two open issues with their evidence.
3. Commit messages on this branch — each carries the reasoning and the numbers.
4. Older long-form investigations only when a ledger line points you there.

## The state of things

- Player build: `artifacts/terminal/Ryujinx-metal-v425-crash-ring` (v426 in the same
  directory is the same plus a better crash ring; use it for the next launch).
- Working tree: three files under `src/Ryujinx.Graphics.Gpu` carry an uncommitted,
  env-gated probe (`RYUJINX_WATCH_PROG` / `RYUJINX_WATCH_BINDINGS_ALL` /
  `RYUJINX_WATCH_BINDING` in `DrawManager.cs`, `TextureBindingsManager.cs`,
  `BufferCache.cs`). Default behaviour is unchanged. Commit or drop as you see fit.
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
- Unattended: `SAVE_ROW=index SAVE_INDEX=<row> tools/drive_in.sh <candidate> <log> [ENV=val ...]`
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
2. For the fog dropout: the next probe is a write watch on the handle-table word
   (`TextureBindingsManager` records its address for the watched binding) to learn who
   rewrites it — the buffer flush path or the guest. Reproduces best at the hot-spring
   save (player Y = 263), standing still, about ten events a minute.
3. Do not reopen the closed performance lines in the perf hand-off without new evidence.
