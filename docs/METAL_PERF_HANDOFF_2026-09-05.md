# TOTK on the Metal backend — performance line and open issues, state as of 2026-09-05

This is the hand-off for the work between v393 (2026-08-30) and v426 (2026-09-04). The
commit messages carry the per-change reasoning; this file is the map. Numbers are
scene-scoped: a result measured at one save says nothing about another (that lesson
cost a week once).

Test rig: M1 Max, TOTK 1.4.2, UltraCam mod (`sdcard/UltraCam/TOTK/Config/maxlastbreath.ini`),
render 1920x1080 (was 2560x1440 until 2026-09-04), `MaxFPS = 50` (45 before; 60 crashes
the guest - see below). Every stats block the backend logs ends with a `config:` field
naming the switches it ran under, so a log can always be told from another build.

## What is on by default now, and why

| Change | Commit / build | Effect (measured) | Switch |
|---|---|---|---|
| Self-reads of 8-bit colour attachments do not split the pass (Switch semantics: the read is allowed to see stale data) | e5d8c073, 1417564a, default since v400 | equal scene 27.8 -> 44.7 fps median (+61%), passes 530 -> 198, sync waits 2686 -> 193 ms per 120 frames | `RYUJINX_METAL_SKIP_SELF_SPLIT=0`, hot file `/tmp/ryujinx-metal-skip-self-split` |
| Same-pixel self-reads of the R32Float attachment are served by framebuffer fetch instead of a split | d5835430, 02f6962c, e186fff2 (v405-v409) | surface save passes 278 -> 186, GPU busy 81 -> 65%; a draw that writes the slot it reads is never fetched (intra-draw semantics differ - it painted black decals) | `RYUJINX_METAL_FB_FETCH=0`, hot file `/tmp/ryujinx-metal-fb-fetch` |
| Guest texture barriers end the pass only for a hazard the draw-time check would split on | ab5ad5ec, c0f67455, 74471eda (v407-v419) | after the v410/v411 corrections it saves about nothing (hill save 269 vs 273 passes, sky-island save 247 vs 245); kept as default on the user's decision, `all` is the conservative fallback | `RYUJINX_METAL_BARRIER_SCOPE=all`, hot file `/tmp/ryujinx-metal-barrier-scope` (`hazard`/`all`) |
| Crash ring: memory-mapped record of the render thread's last 32k events plus a separate 4k ring of buffer/program disposals | 9402007b, cbc2cd56 (v425/v426) | nanoseconds per event, no I/O; survives a segfault | `RYUJINX_METAL_CRASH_RING=0`; file `/tmp/ryujinx-metal-crash-ring.bin` |

The format whitelist for stale self-reads was reached by three user-visible failures and
should not be widened: R32F stale broke Ultrahand (a data read, not a picture), RG11B10
stale striped the water (refraction samples the HDR target at a displaced coordinate).
The same displaced-coordinate read is why RG11B10 was withdrawn from fetch (v408): the
patcher cannot prove a sample is same-pixel from the shader text alone, so only R32F
depth self-reads - same-pixel by construction - are fetched. Any colour target needs a
runtime same-pixel proof first.

## The frame-time model, and where it stops applying

Measured with the command buffers' own GPU timestamps (v393 era, 1440p):
`frame ms ~= 45.8 us x render passes + 11.4 ms`, R = 0.92, over real play. About 60% of
the per-pass cost is fixed pipeline drain, 40% scales with pixels (0.8x resolution: 62.5
-> 53.0 us per pass, fps unchanged at that save).

Dense-scene picture from the user's play (v402 log, 34.8 minutes): 4000-5000 draws a
frame sits at the cap; 5000-6500 draws 40.5 fps at 349 passes, GPU 85%; the densest
blocks pair GPU saturation with `BufferModifiedRange` guest waits (22.6 ms per frame
summed over three guest threads) - both relax when passes drop.

The model has a floor. At a 9200-draw view (row-2 sky save) the GPU is only 69% busy,
the guest takes exactly three sync waits a frame (two created by WaitForIdle, one by
SetReference, all `BufferModifiedRange`) and the GPU idles 7.3 ms a frame in three
bubbles. There, removing passes does not move the frame: every "save passes but add
latency" idea lost (tile snapshot, below). Do not measure pass-count ideas at that save.

## Lines that were tried and are closed (do not redo without new evidence)

- **Tile-shader snapshot for writer self-reads** (v415-v418, 1080fbe4, off by default,
  `RYUJINX_METAL_TILE_SNAPSHOT=1`): a memoryless twin attachment filled by a tile kernel
  so the reader can fetch the pre-draw value. Works, correct (screenshots identical), but
  at the row-2 save GPU time fell 0.7 ms and frame time rose 1.35 ms. Mode 2 of the
  experiment is not interpretable (it contradicts mode 1); do not quote its per-dispatch cost.
- **Learned store actions / dead stores** (4d7d9357, `StoreLiveness`, `RYUJINX_METAL_STORE_LIVENESS=1`):
  700-1030 stores a frame, only ~1 a frame (a clear) is really dead. Not worth a policy.
- **Skipping barriers by storage-buffer identity** (v414 probe): fragment stores hit
  99-104 draws a frame that rebind an overlapping buffer; WAW cannot be told from RAW at
  the binding level. Closed.
- **Guest-sync line**: ceiling +14% at the user's dense scenes (GPU 88% busy), and the
  lever is core thread hand-off, not the backend. Not opened.
- **Residency sets, argument-buffer hashing, 45 -> 60 cap, pass reordering**: closed
  earlier (`docs/METAL_WHITE_FLASH_HANDOFF_2026-08-08.md` and commit messages).
- **PSO compile bursts** (80f38773 probe): they explain about a quarter of the slow frames
  while turning the camera and none of the sustained frame rate. The fix, if wanted, is
  async pipeline creation with skip-until-ready, not a binary archive.

The one untested mechanism with a large prize (the ~300 writer self-read splits a frame
in dense scenes): a stamp-and-twin fetch variant - a memoryless uint attachment stamped
with the draw id and a memoryless copy of the pre-draw value, so later fragments of the
same draw read the copy. Exact semantics without a tile dispatch; its cost is fetch's
own serialisation of overlapping fragments, unmeasured. Only worth building if a play log
shows GPU >= 85% busy with fetchWriter >= 200 a frame.

## Open issue 1: fog/cloud layer missing for single frames

Symptom: a fog sheet (or the sky-island cloud band) drops out, or pops in, for one to
three frames, every 1-2 s while it is in view; stronger at the hot-spring save (Y=263).
Present with `barrier=all`, with `RYUJINX_METAL_DEPTH_RAW=1`, and in a clean build.

Frame-level mechanism (v420-v424 probes, `RYUJINX_WATCH_PROG=02cf57fb52f91612
RYUJINX_WATCH_BINDINGS_ALL=1 RYUJINX_WATCH_BINDING=136`): the composite that blends
the fog buffer (960x540 R11G11B10Float, sampled at fragment binding 136) runs once a
frame; on the bad frames its binding resolves to a different texture (pool id 728, a
500x400 R16G16Unorm at 0x10FB800000, descriptor and texture consistent). The handle the
emulator read from the guest's table at bind time really was 728 (`atBind == raw`), and a
direct read of the same word microseconds later, in the same GPU-thread call, gives yet
another stable value. So the table word is rewritten right after the emulator reads it,
every frame; on the bad frames the rewrite lands before the read. Who rewrites it is the
remaining question. Candidates, in order: a host-to-guest flush of a buffer that still
carries a stale "GPU-modified" range (`BufferCache.CopyBufferSingleRange` marks a copy
destination modified and leaves guest memory untouched when the source was modified);
guest-side table recycling; a real guest write. The map-flicker fix of 2026-08-29
(`TexturePool.GetInternal` descriptor check, `RYUJINX_POOL_DESC_CHECK`) is on and is not
this. Tooling: `tools/` and the scratchpad scripts named in the memory notes
(`video_ab.sh`, `flash_count.py`, `band_events.py`, `crash_ring_read.py`).

## Open issue 2: segfault inside `drawIndexedPrimitives`

Four times on 2026-09-04 (02:30 tile experiment - explained, stale `_applied` pipeline;
then 20:12, 20:23, 21:03), all on `GUI.RenderThread`, all `KERN_INVALID_ADDRESS at 0x8a0`
inside `-[AGXG13XFamilyRenderContext drawIndexedPrimitives:...]+204`, i.e. the driver
dereferencing a nil object. The 21:03 one was the clean v419 build, 19 minutes in, in the
Depths; the earlier two were minutes after warping to the Great Sky Island. v405 ran 11
hours overnight without a crash, so this is a regression from the v412-v419 range or from
the same-day config change (50 fps cap, 1080p). Excluded: a failed pipeline build (the
draw guard skips those), fetch variants (0 at the time), tile snapshot (off). The Metal
validation layer ran 4 minutes without reproducing. Next crash: read the crash ring
(`crash_ring_read.py --last 200`) - it marks a draw whose pipeline belongs to a disposed
program, whose index buffer was disposed earlier, or whose pipeline pointer is null.

## Working rules that were learned the hard way

- Ask before restarting or closing the game, even an instance this side launched - the
  user may have taken it over. Kill by PID, never `pkill -x Ryujinx`.
- One-frame artefacts need per-frame video (`screencapture -x -v -V 30 -D 2`), not
  screenshot bursts; a 1 fps burst cannot tell a drift from a single-frame event.
- Before an A/B, make the changed code prove it is running (the `config:` field, a
  counter in the stats line). Before quoting a probe, check the probe against a case
  whose answer is known.
- Any change to the mod ini or config is an intervention and goes into the A/B ledger.
