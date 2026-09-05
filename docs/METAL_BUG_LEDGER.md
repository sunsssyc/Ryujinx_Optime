# Metal backend — picture and stability bug ledger (TOTK), as of 2026-09-05

One line per bug: what the user saw, what it really was, the commit that fixed it, how it
was verified, and the switch if one exists. The long-form investigations live in the
hand-off documents (`METAL_HANDOFF_2026-07-18.md`, `METAL_CURRENT_HANDOFF_2026-08-02.md`,
`METAL_WHITE_FLASH_HANDOFF_2026-08-08.md`, `METAL_PERF_HANDOFF_2026-09-05.md`) and in the
commit messages; this file is the index. Dates are the fix dates.

## Fixed

| Symptom (as reported) | Root cause | Fix | Verified by | Switch |
|---|---|---|---|---|
| Whole picture cyan-tinted and crushed blacks | `CAMetalLayer` had no colour space, so an sRGB image was shown raw on a P3 display | bd4827f1 (2026-07-18) | screenshots against Vulkan | - |
| Vegetation billboards vanish or flicker up close | non-indexed draws through the topology-conversion path dropped `instanceCount` / `firstVertex` / `firstInstance` | b2f08b2e (2026-07-19) | draw trace: the missing plants were the lost instances | - |
| Gloom (miasma) renders as sparse red streaks instead of a full field | Metal cannot use block-compressed formats on 3D textures; the backend advertised support, so 4 BC4 volume-noise textures decoded to garbage and the material discarded almost every fragment | 14c8533e (2026-07-24) | Metal vs Vulkan screenshots at the same save | - |
| Same, plus five validation-layer classes: texture usage `Unknown`, false SIMD-width promise on compute pipelines, illegal compute barrier scope, mip copies out of range, 65535 scissor sentinel | each an undefined-behaviour path the validation layer named | 161aa69c, 9ddf5f2f, 93c44024 (2026-07-19) | `MTL_DEBUG_LAYER=1 ... _MODE=nslog` count went to zero | - |
| Bushes and grass missing in the Depths (a whole clump gone) | `UpdateCullMode` set "cull both faces" from the stale face register even with culling disabled; the backend emulates cull-both with a 0x0 scissor, so double-sided vegetation cards produced no fragments at all | ab451955 (2026-07-25) | three in-game A/Bs (discard, depth compare, Xcode post-transform) left only rasterisation; scissor fixed it | - |
| Single-frame white (sometimes red) full-screen flash by day | translator dead-code elimination (`Optimizer.RemoveNode`) cascaded and cleared the source list of a still-live call; the sun-visibility kernel's atomic argument was left uninitialised and the 1x1 auto-exposure texture saturated to 1.0 for that frame | 01a1e37f (2026-08-26); mechanism e63dc628 (2026-08-22) | offline re-translation of the guest shader; per-frame exposure probe | `RYUJINX_METAL_FLASHGUARD` (the earlier present-side mitigation) is withdrawn |
| Hard-edged teal sheets over Depths terrain (fog looks like stone slabs) | the MSL emitter dropped the constant offset of `texelFetch`, so the 12-tap depth-fade neighbourhood collapsed onto one texel | fix in the good-display merge, CodeGenVersion 7378; A/B toggles b1a39608 (2026-08-26) | same-build A/B with `MSL_FETCH_OFFSET=0` reproducing it | `RYUJINX_MSL_FETCH_OFFSET=0` reproduces |
| Everything too dark on the main branch (6.5x in the Depths) | `Window.Present` swapped the Unorm shadow texture for its sRGB sibling (a leftover flash mitigation); presenting through an sRGB view decodes once more and the whole transfer function is lost | 3d33ab70 (2026-08-26, v349) | luminance percentiles matched stock Ryujinx 1.2 to the decimal; the gamma-curve shape of the error was the tell | `RYUJINX_GPU_PRESENT_SIBLING=1` restores the old behaviour |
| Depths map layer vanishes for one frame (terrain, grid, frame; UI intact) | `TexturePool.GetInternal` returned the cached `Items[id]` without comparing the descriptor, so a slot rewritten after the per-frame sync handed out the previous occupant | 7c1d8ef4 (2026-08-29) | 10 events per 10 minutes -> 0, same build, hot-switched; stock Ryujinx had it too | `RYUJINX_POOL_DESC_CHECK=0` |
| Five crash shapes in the encoder/command-buffer lifecycle (double commit, double endEncoding, live encoder at commit, ...) | non-backend threads called `FlushCommandsImpl` directly (sync creation, counters, staging, texture read-back) | 46b58c0c (2026-08-30) | thread census; crashes stopped | - |
| Guest null dereferences on respawn after a fall (three different PCs) | the mod's frame cap at 60 breaks the game's own timing; not an emulator bug | config: `MaxFPS` 45 (now 50, verified crash-free on 2026-09-04) | 3/3 reproduction at 60, none at 45 | `sdcard/UltraCam/TOTK/Config/maxlastbreath.ini` |
| Water surface collapses into a tiled sand texture | RG11B10 self-reads were being served by framebuffer fetch, but refraction samples the HDR target at a displaced coordinate, which fetch cannot express | 02f6962c (2026-09-03, v408) | user's play; hazard+fetch reproduces, R32F-only does not | fetch formats are R32F only |
| Black decal blocks flickering with primitive order (hill save) | a draw that writes the slot it reads was fetched; the back face read the front face's fresh write instead of the pre-draw value | e186fff2 (2026-09-03, v409) | burst diff 301 cells -> 0 | - |
| Black wedges at the hill save with `barrier=hazard` | hazard mode skipped guest barriers that guard stale-policy self-reads and fragment storage stores (visibility feedback flags) | c0f67455 (2026-09-04, v410/v411) | burst diff 241/291 -> 108/23 (baseline noise) | `RYUJINX_METAL_BARRIER_SCOPE=all` |
| Wall/ground sunlight changes for one frame at the Nachoyah Shrine manual save | Resource preparation can retire the render encoder after the dirty-state check, leaving clean binding sets unprepared for the new encoder | v445 (source committed with this ledger): reprepare all render bindings after encoder/command-buffer transitions during prepass; cached output-map correction also retained | v444 on/control: 0/16 PRESENT jump edges per 6000 frames; clean v445 600s + user-reload 180s video: 0 patch excursions, ~49.6 FPS, no sustained RSS growth | `RYUJINX_METAL_PREPASS_REBIND=2` records without repairing; default 1 |

## Open

| Symptom | State | Where the trail is |
|---|---|---|
| Gloom deals damage in places that are safe on Vulkan (gameplay-breaking, Depths edge) | not reproduced since 2026-07-24; mechanism unknown (occlusion query, 3D BC, texture read-back, transform feedback all excluded) | `METAL_HANDOFF_2026-07-18.md` gloom sections |
| Distant clouds show through mountains, flipping between states | reproduces with every optimisation off; likely the game's cloud impostor reading a periodically refreshed depth; upstream-class | memory note `metal-nvn-self-skip` |
| Fog sheet / sky-island cloud band missing or popping for 1-3 frames every 1-2 s | unresolved; the old binding-136 evidence was invalidated by the 2026-09-05 declaration audit: the watched program only uses 128/129, and the probe included stale slots from previous draws | `METAL_PERF_HANDOFF_2026-09-05.md`, open issue 1 and its correction |
| Segfault in the driver's `drawIndexedPrimitives` (nil object at +0x8a0), four times on 2026-09-04 including a clean build | regression from the v412-v419 range or the same-day config change; crash ring (v425/v426) armed to catch the next one | `METAL_PERF_HANDOFF_2026-09-05.md`, open issue 2 |

## Candidate correction, not a resolved picture bug

`MetalRenderer.LoadProgramBinary` omitted `ShaderInfo.FragmentOutputMap` when creating
the Metal program. Newly translated programs passed it correctly. The omission disabled
the write-mask protection for channels absent from a cached fragment shader. The v429
candidate passes the saved map; cache loading and the masked-pipeline counter confirm
that it runs. Sunlight still flickers in v429, so this correction does **not** close the
sunlight issue by itself. v445 retains it alongside the validated prepass rebind fix. Both source changes are committed with this ledger; the v445 binary remains a local artifact.
