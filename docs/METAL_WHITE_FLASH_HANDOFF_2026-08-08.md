# TOTK white-flash on the Metal backend — state as of 2026-08-08

**Root cause: still unknown.** **Visible artefact: suppressed**, by a guard in the
present path that is on by default (`RYUJINX_METAL_FLASHGUARD=0` disables it).

The flat frames are still produced - the in-process probe, which samples the present
source *before* the guard, still counts them. What the guard does is keep them off the
screen.

## The mitigation

`Shaders/KeepGood.metal` folds each frame into a persistent texture unless nine taps show
it is flat and near-white, in which case the stored frame is left in place; the present
blit then reads that texture. Both the test and the choice happen in the fragment shader,
for the frame they apply to. Earlier attempts failed for reasons worth keeping:

- a CPU-side check needs the pixels back before the present blit is chosen, so it has to
  stall the pipeline - the frame rate collapsed to single digits
- replacing a flat frame with "the previous surface" does nothing, because the present
  source alternates between two textures and flat frames come in runs averaging 1.6, so
  the previous surface is flat about as often as the current one
- keying the replacement on a CPU classification that arrives two frames late picks the
  same surface again, since there are only two in rotation

Verified from macOS compositor screenshots, not the emulator's own probe:

| | flat frames on screen | frame rate | output |
| --- | --- | --- | --- |
| guard off | 17/40 (42.5%) | 30.0 fps | normal, with flashes |
| guard on | 0/40 (0%) | 30.0 fps | 40 distinct normal frames |
| default (no override) | 0/50 (0%) | 30.0 fps | 50 distinct normal frames |

Cost: one extra full-resolution pass per present. No frame rate change was measurable in
the scene it was verified in, which is vsync-capped at 30.

The rest of this file records what is measured about the underlying fault, what was
retracted, and which instruments exist, so the next attempt does not re-walk the dead
ends.

## Symptom

Whole 3D scene replaced by a flat near-white image for 1–10 consecutive frames, while
the HUD composites correctly on top of it. Strongly view dependent — it only occurs
looking toward the sun in daylight, and stops within about two minutes as in-game time
moves the sun off the triggering angle. Vulkan does not show it, so the fault is inside
the Metal backend.

Rate, measured with the 3x3 grid detector and cross-checked against macOS compositor
screenshots (45.8% vs 31% in the same scene, same order of magnitude): **25–45% of
frames while the trigger view is held**.

## What is established

1. The guest tonemap program covers the whole screen on normal frames - painting its
   fragment stage a solid colour turns the whole scene that colour with the HUD still on
   top. This says nothing about white frames; see the redirect above.
2. That shader reads **uniform 1.0** from its scene input (texture slot 128,
   1600x896 RG11B10Float) on roughly 21% of frames. Real, but not causal: skipping the
   draw entirely leaves the white frames untouched.
3. Its curve asymptotes to 1.0, so white is the correct output for that input.
4. The input texture is written by 6–7 render passes and ~30 draws in the same frame,
   confirmed using **view-root identity** (see the trap below).
5. The binding is correct: the resource id written into the argument buffer equals the
   id of the texture the binding intended, 60/60 frames (30 white, 30 good).
6. The texture is single-mip, single-slice (levels=1, firstLevel=0), so the shader
   cannot be reading an unwritten level.

## Excluded, each with a measurement

| Hypothesis | Result |
| --- | --- |
| SynchronizeMemory / guest memory upload | counters zero on white and good frames alike |
| Present buffer selection, FSR, present path | HUD composites correctly over white; probe samples before FSR |
| State cache skipping redundant binds | in-session A/B, 29.86% vs 32.42% |
| Dirty-flag tracking (`RYUJINX_METAL_FULL_REBIND=1`) | 31.17% |
| Metal fast math | 31.90% |
| Fragment-barrier skip (`TextureBarrier` early return) | 38.23% vs 35.65% |
| Serialising every draw (pass split after each) | 49.13% vs 34.17%, not eliminated |
| **Hard GPU sync before the tonemap draw** (commit + WaitUntilCompleted) | 36.88% vs 37.47% — ordering is not the cause |
| Guarding the shader's reciprocal against a near-zero divisor | 35.16% |
| Clamping the shader's texture fetches | 33.99% |
| Tonemap luminance weights | identical, correct Rec.601 values |
| Swizzled vs identity view for sampling | 37.67% vs 36.17% |
| Texture aliasing / non-render writers | no copies or uploads touch it |
| Skipping the draws that write the input | paired arms, 59% vs 50% control — no effect |
| Array-segment binding path | tonemap binds through the non-array path |
| Sampled mip/slice out of written range | texture is levels=1, firstLevel=0, single slice |
| Collapsed sample coordinates (every pixel reading one texel) | interpolant is a clean gradient on 48/48 frames |

## Retracted — do not build on these

- **Early flash rates (19–20%, 7.38%).** The original probe sampled two pixels and
  flagged the frame when they were equal, so an overexposed sky lit it continuously; a
  later spike-vs-neighbours test scored every frame inside a run of white frames as
  normal. Both numbers are meaningless.
- **"A white frame carries one extra empty pass."** Produced by comparing white frames
  against a control sampled 600 frames apart, usually from a different scene. Matched
  controls (the frame immediately before each run) are identical.
- **"No render pass writes the tonemap's input."** Comparison used raw handles and
  object references. A view carries its own MTLTexture pointer, so writes made through
  one look like writes to an untouched texture. `Texture.ViewRootPtr` exists precisely
  to avoid this and the comment above it documents the same mistake being made and
  retracted once before. With root identity the writes are plainly there.
- **"Painting the writer shaders removes the white frames."** Circular: painting makes
  the screen magenta, and the detector looks for uniform *white*, so it can never fire.
- **"Skipping the draws that write the input drops the rate from 44% to 18%."** Measured
  with sequential windows while the in-game sun kept moving. Re-run with the skip on even
  frames and odd frames as a matched control, it is 59% vs 50% - no effect. Sequential
  windows cannot measure anything here.
- A separate, real instrument fault found along the way: the skip's target set was keyed
  on a shader `DebugLabel`, which is not stable across runs, so for one whole round the
  set was empty and nothing was ever skipped. Every masking experiment needs an arm that
  must visibly change the picture, or "no effect" and "never applied" look identical.

## Validated result: something writes the present source every frame

Attribution bookkeeping could not answer who fills the present source, so this bypasses
it entirely. After each present, the source texture is cleared to green. If the next
present of that texture still showed green, nothing had written it in between.

**Positive control passes**: staining *before* the present blit turns all 40 sampled
frames green, so the mechanism demonstrably reaches the texture. (It needed an explicit
`EndCurrentPass` first - creating an encoder while a pass is open is an instant driver
assertion, and that is exactly how an earlier version of this probe failed silently.)

**Result**: with the stain applied after each present, green came back 0 times out of 40,
while 9 frames were white and 31 showed the scene. Something overwrites the present
source between every pair of presents, white frames included.

That retires the "missing composite" idea below. A white frame is a frame where
something **wrote uniform white** into the present source, not one where the composite
was skipped.

This is the only result in this document with a passing positive control. Everything
above it was measured with instruments that were never checked that way, and several of
them turned out to be lying.

## Strongest lead at the end of the day

The present-sized LDR buffer **reads white when nothing writes it**. Found by accident:
masking writes into it on even frames sent the *odd* control arm to 85% and then 100%
white, because the present textures are double buffered and one of them simply stopped
being updated. That breaks the paired design (the arms stop being independent whenever
the masked resource carries state across frames - worth remembering), so the run says
nothing about whether those writes cause white. What it does show is what an unwritten
present buffer looks like: white.

That reframes the symptom. A white frame is plausibly a frame where **the scene was
never composited into the present buffer at all**, not a frame where something wrote
white into it. It fits the two things that never fit before: dropping the tonemap draw
changes nothing (it writes the float target, not this buffer), and the HUD still appears
correctly (it is drawn straight into the present buffer, so it survives while the scene
composite is missing).

Next step: for each frame, record whether anything wrote the present source between one
Present and the next, and compare that against the white classification. If white frames
are exactly the frames with no composite, the question becomes why that composite is
skipped - and the answer is upstream of the Metal backend's draw path.

## The finding that redirects everything

Dropping the tonemap's own draw - applied on even present indices, odd frames as the
matched control - does not stop the white frames. They still occur at 33-39% on frames
where that draw never executed (33 vs 41 over n=270, 39 vs 42 over n=300). With no skip
applied at all the two arms already differ by 7 points (39 vs 32, n=240), so that is the
noise floor and neither figure clears it.

**The white is not produced by the tonemap shader.** "The shader reads 1.0" is not even
a symptom of the mechanism - whether the shader runs at all makes no difference. Every
conclusion in this document that treats that shader, its inputs, or the passes writing
them as the site of the fault is chasing the wrong stage.

What is left is everything downstream of it: the composite/UI stage that draws over the
full-resolution target, the copy into the present buffer, and the present path. Those
were dismissed early on the grounds that the HUD composites correctly over the white -
which only shows the HUD is drawn after whatever turns the frame white, and excludes
nothing.

## Where it actually stands

The shader reads uniform 1.0 from its scene input, and the frame comes out white. But
the content of that input does not decide it: with the skip applied on even frames and
odd frames as a matched control - same seconds, same camera, sample sizes equal -
skipping every write into it gives 59% white against 50% on the control, and no
sub-range differs from its control either.

So "the shader reads 1.0" is something that accompanies a white frame rather than
something that causes it. What actually selects a white frame is still unidentified, and
the causal direction assumed for most of this investigation was wrong.

Anything measured here without paired arms should be treated as unreliable: an unpaired
version of this same experiment read 44% -> 18% purely from the sun moving during the
window.

## Instruments available

Runtime toggles, re-read once per frame, so both arms can be measured inside one
trigger window (cross-run comparison is useless here — the rate swings 16–45% with the
camera):

- `/tmp/ryujinx-metal-state-cache` — 0 off … 3 full
- `/tmp/ryujinx-metal-strict-barrier` — 1 never skips the fragment barrier
- `/tmp/ryujinx-metal-serialize` — 1 ends the pass after every draw
- `/tmp/ryujinx-metal-hardsync` — 1 commits and waits before the tonemap draw
- `/tmp/ryujinx-metal-identity-sample` — 1 samples through the identity view
- `/tmp/ryujinx-metal-skiprange` — "lo hi" skips draws by position among the writes to
  the tonemap's input; label-free, so it survives the fact that shader DebugLabels are
  **not stable across runs**

Compile-time diagnostics, selected by shader label (only usable within the run that
produced the label, and defeated by a warm shader cache since the patch is not part of
the cache key):

- `RYUJINX_METAL_PAINT` — fragment writes solid magenta
- `RYUJINX_METAL_SHOW_INPUT` — fragment outputs its raw scene fetch
- `RYUJINX_METAL_GUARD_DIVIDE`, `RYUJINX_METAL_CLAMP_FETCH`

`PresentProbe` reports every 60 classified frames: white-frame count, run lengths, draw
and dispatch counts, and the per-frame records `HdrPassProbe` collects.

## Attribution is instrumented but still failing

`Texture.CanonicalPtr` now gives every texture one identity, set unconditionally at
construction (its own storage, or the storage a view was made from), and `HdrPassProbe`
records every colour attachment of a pass rather than only attachment 0. A self-check
runs on each good frame and logs `ATTRIBUTION-BROKEN` when a good frame's present source
has no recorded writer.

**It still fires**: 4561 hits in one run, with a writer found on 1 good frame out of 79.
So the Metal backend records no render pass and no copy that fills the texture it hands
to Present, on frames that visibly contain a correct image.

Either the attribution is still wrong in a way not yet found, or - if it is now right -
the texture being presented is not the one the frame was rendered into, and the choice is
made above the Metal backend in the GPU layer's texture cache. That is compatible with
Vulkan not flashing: the shared layer branches on backend capabilities.

Do not spend another session on ownership questions until this self-check is silent.
It is the cheapest available signal that the instrument is lying.

## Start here next time: the identity problem

Attribution in this document is built on a `RootOf()` helper that returns `ViewRootPtr`
when set and `GetHandle().NativePtr` otherwise. That is **not canonical**: `ViewRootPtr`
is only computed when a view is constructed, and the fallback is the swizzled handle,
while attachments elsewhere use the identity handle. The same underlying resource
therefore hashes to different "roots" depending on which path observes it, and every
ownership comparison silently misses.

The symptom to recognise: the instrument reports that nothing writes a texture that
visibly contains correct content. It has now happened three times in this investigation
- for the tonemap's scene input, for the present source's render passes, and for copies
into the present source (0 copies on good frames, which cannot be true).

Before any further attribution work: give `Texture` one canonical identity - the identity
handle, or the underlying source texture pointer captured at construction - use it
everywhere, and add a self-check that fails loudly if a good frame's present source has
no writer. Until that exists, treat every "nothing writes X" conclusion here, including
the negative results, as unproven.

## Traps worth remembering

- Compare textures by **view root**, never by handle or object reference.
- A control frame must come from the same scene; the frame before a run is the only
  cheap matched control.
- A diagnostic that changes what the screen looks like will also change what the
  detector sees — check the coupling before believing the result.
- Shader `DebugLabel` is not stable across runs.
- The compile-time patches are not part of the shader cache key, so a warm cache serves
  unpatched binaries and the diagnostic silently does nothing (watch the "compiling …"
  log line, not the absence of an effect).
- Queue-scope GPU captures on this backend produce multi-GB traces that the watchdog
  aborts before the next present; the bundles are unusable.

## Unrelated fix made the same day

`drawPrimitives` SIGSEGV at 0x8a0. `AppliedRenderState.Retarget` identified a render
command encoder by its pointer alone, but encoders are released when their pass ends and
the allocator hands the same address to the next one, so the cache reported its fields
as already applied and nothing was set on the new encoder — the first draw then
dereferenced a null pipeline. Fixed by pairing the pointer with a process-wide
generation counter bumped for every render encoder created. Verified over long runs with
the state cache enabled.
