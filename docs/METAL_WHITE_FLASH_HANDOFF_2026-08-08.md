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

## Session 2026-08-08, later: GPU-truth localisation, and instruments that lied

### The detector was wrong all day, and that invalidates most numbers above

The flat-frame test was "nine taps, max-min <= 2/255, mean >= 240". A flat frame keeps
its HUD - hearts, minimap, thermometer all draw normally over the white - so a single tap
landing on the minimap breaks the uniformity test and the frame scores as ordinary.
Whether it fires therefore depends on camera angle and HUD placement, which is why the
same scene reported 0%, 7.4%, 19% and 42% in different runs. Those were not scene
differences.

Replacement, calibrated against 300 captured frames of real gameplay: count saturated
taps over a 5x5 grid. Flat frames have at least 8 of 25 saturated (median 24); ordinary
frames at most 4 (median 2). Threshold 6. Immune to the HUD.

Anything above that rests on the old criterion should be re-measured before it is
trusted, including "the guard removed the flash" - that 0/50 was the criterion failing,
not the flash stopping.

### What GPU memory says, independent of any instrument in this tree

A queue-scope capture with the watchdog raised (RYUJINX_METAL_CAPTURE_WATCHDOG_MS)
produces a full-frame trace. Its bundle contains raw per-texture dumps, which can be read
directly - no Xcode, no replay, no code of ours in the path:

  - seven 1600x896 scene targets: none flat (dominant value 0.3%-55%, black or float 1.0)
  - one 1920x1080 target: 72.5% 0xFFFDFEFE, the exact value the present probe reports
  - the 2560x1406 drawable: 67.1% flat white

Decoding the 1920x1080 dump shows the white frame with the HUD intact. So the scene
renders correctly at 1600x896 and the fault appears at full resolution. This is the one
conclusion here that no instrument of ours can invalidate.

Recipe: find MTLTexture-* files whose size matches w*h*4 (+ up to 64 KiB header), take the
last w*h*4 bytes, read as BGRA8. A flat frame is obvious as a single dominant 32-bit word.

### Ruled out this session, each with a measurement

  - Metal fast-math (FastMathEnabled=false): 31.9%, unchanged
  - fragment-dependency barrier skipping (always split): no difference in the live window
  - full draw serialisation (end the pass after every draw): 34.2% vs 49.1% baseline -
    reduced but nowhere near removed, so a read-after-write ordering fault cannot be it
  - guarding the tonemap's reciprocal against a near-zero divisor: 35.2%, unchanged
  - signed-overflow UB in the translated integer ops: the emitted MSL did change to
    wrapping unsigned arithmetic and the flash measured *higher* afterwards (62.5%).
    Reverted. It is still real undefined behaviour and worth fixing on its own merits.

### A wrong turn worth recording

hdrspan logging showed exactly one program sampling 1600x896 and writing 1920x1080
(a38a7cbfc1472254), which looked like the composite. Painting it with
RYUJINX_METAL_PAINT corrupted the HUD text instead of the scene - it is a UI shader that
happens to sample the scene texture. "Reads A, writes B" does not identify a compositor.

Note RYUJINX_METAL_PAINT and the other source-patching diagnostics only apply when the
shader is actually compiled; with a warm disk cache they silently do nothing. Bump
CodeGenVersion to force a recompile, and check the log line that confirms the patch.

### FlashGuard

Off by default. Two separate defects: it used the broken uniformity criterion (fixed
here), and enabling it faulted the driver inside renderCommandEncoderWithDescriptor while
building the keep pass's descriptor. The second is undiagnosed - the keep texture is the
only new attachment in the present path and is the first thing to suspect.

### Integer wrapping: measured again, properly this time

The first comparison of this change was worthless - the two arms faced different camera
angles, and the flash rate swings with the view. Re-run with both arms loading the same
save and executing the same scripted camera sweep, judged from compositor screenshots:

    base  25/86  = 29.1% +-4.9
    wrap  30/188 = 16.0% +-2.7
    difference 13.1 pp, combined SE 5.6, 2.4 SE

Kept on that basis, with three reservations on the record: the arms are unbalanced (one
base round produced no shots), the shot rates differ between them, and the flash is
reduced rather than removed. A correctness fix of this kind should be all-or-nothing for
the shader it affects, so a partial effect means either something else contributes or
part of this difference is not the change.

### The canvas is white, and the scene fails to cover it

Dropping every draw whose colour target 0 is the 1920x1080 composite
(/tmp/ryujinx-metal-skip-hdr) and judging from compositor screenshots:

    skip off: 13/24 flat, mean luma 152..248   (ordinary flashing)
    skip on:  24/24 flat, mean luma 248..248   (constant white)

With nothing drawn into it the target reads white, not black. So the scene is composited
over a white canvas, and a flat frame is the scene failing to cover it - not something
painting white over the scene.

That reframes the search, and it retires an exclusion that never held: white and ordinary
frames were measured to have the same total draw count (2400-2800), which was taken as
evidence that no draw goes missing. The composite is one or two draws; a missing one
disappears into the +-300 frame-to-frame spread. "Same draw total" never ruled this out.

Next: count draws into that target specifically, per frame, and compare flat frames
against their immediate predecessor - the paired control, not a periodic sample from
another scene.

### Per-target draw counts, paired: still identical

Counting draws into each full resolution target and comparing a flat frame against its
immediate predecessor (the paired control):

    WHITE 1920x1080:RG11B10Float p=4,d=97    prev p=4,d=97
    WHITE 1920x1080:RG11B10Float p=4,d=95    prev p=4,d=95

Same passes, same draws, frame after frame. The "a composite draw goes missing" idea that
the white-canvas result suggested does not survive this.

Also worth recording: no 1920x1080 RGBA8_sRGB target ever appears in this list, though
that is the format the present source carries. It is not written by a render pass at all -
it is filled by a copy. Anything that reasons about "the pass that writes the present
source" is reasoning about a pass that does not exist.

### Where this leaves it

Every counter in this tree reports flat and ordinary frames as identical: passes, draws,
dispatches, sampled texture identities, constant buffer statistics, pass end reasons, per
pass content of the composite target. The difference is real and visible on screen, so it
is in data those counters do not reach - most likely inside a shader's arithmetic, on a
value that depends on view direction.

The instruments that have actually earned trust are the two outside this codebase: macOS
compositor screenshots, and raw texture dumps decoded straight out of a GPU trace bundle.
Both are documented above. Anything measured with an in-process probe should be checked
against one of them before it is believed.

### FlashGuard: suppresses the artefact, still destabilises the emulator

With the saturation-count criterion in place, alternating both arms twice in one session
and judging from compositor screenshots:

    guard off: 21/60 flat = 35.0%, luma 152..248
    guard on:   0/60 flat =  0.0%, luma 151..162

Zero, and the luma still moves - it is not freezing on a repeated frame.

It is still off by default because the process exits during play with it on, silently,
within a minute or two of camera movement. One cause was found and fixed: the keep
texture was rebuilt whenever the source changed size, and this game has dynamic
resolution, so that released a texture an in-flight command buffer was about to name as
an attachment - which matches the one crash report that was produced (EXC_BAD_ACCESS in
AGX FramebufferGen3, from renderCommandEncoderWithDescriptor). The remaining exits are
not explained; the texture's usage flags do include RenderTarget, so that is not it.

Anyone picking this up: the visual result is already there. What stands between it and
being usable is that exit, not the suppression.

### Two mitigation designs, both crashing, and the one that should not

Both attempts suppress the artefact and both take the emulator down:

  - render a keep texture in its own pass, decide in the shader: the driver faults
    building that pass's descriptor (AGX FramebufferGen3). Rebuilding the keep texture on
    resize was one cause and is fixed; the rest is unexplained.
  - decide on the CPU and keep the good frame with a copy: exits sooner. flushAndWait
    commits and swaps the command buffer in the middle of Present, and the rest of Present
    goes on using the Cbs local it captured before the swap. That is inherent to any
    design that needs the samples on the CPU before choosing what to present, not an
    implementation slip.

A design that avoids both, not attempted here: fold it into the present blit, which
already exists and already samples the source.

  - give that pass two colour attachments - the drawable, and a keep texture
  - give its shader two sampled textures - the source, and the *other* keep texture
  - the shader tests the source, writes the chosen image to both attachments
  - ping-pong the two keep textures each frame, so the one being read is never the one
    being written

No extra pass, so nothing new for the descriptor builder to fault on; no CPU sync, so the
command buffer is never swapped mid-Present. It needs HelperShader's present path to take
a second attachment, which is why it was not done in the session that found this.

Correction to the design above: it still names a keep texture as a colour attachment, and
the fault being avoided happens while the driver builds an attachment descriptor. It
removes the second render pass and the CPU sync, but not the attachment itself, so it may
well hit the same fault.

And the attachment's properties are not what is wrong with it. Logged side by side at
creation, the keep texture and the surface the game renders into are identical in every
respect:

    keep: 1920x1080 RGBA8Unorm usage=ShaderRead,ShaderWrite,RenderTarget,PixelFormatView
          storage=Managed samples=1 type=Type2D mips=1 slices=1
    src : 1920x1080 RGBA8Unorm usage=ShaderRead,ShaderWrite,RenderTarget,PixelFormatView
          storage=Managed samples=1 type=Type2D mips=1 slices=1

So the search moves to lifetime and ownership rather than description. The game's surfaces
are held by the texture cache and reach the encoder through the paths that register them
against a command buffer; this one is held by a static field and only ever attached from
Present. With the guard on, the process still exits during a camera sweep - after six
sweeps in one run, two in another - so whatever it is, it is timing dependent.

### The Xcode route, and how it was misused here

A full-frame queue-scope capture opens and replays. What it confirmed, independently of
everything in this tree: on a flat frame the 1600x896 G-buffer is intact - albedo,
normals, depth all correct - matching what the raw texture dumps already said.

What it did not yield is which encoder writes the flat 1920x1080 texture, and the reason
is a misunderstanding of the tool rather than a limit of it. The Dependencies view's
filter matches node names, which are encoders; textures are edge annotations, not nodes.
Filtering by a texture address returns "0 matches", and a non-empty result from it earlier
was simply whatever node happened to be selected. Several rounds of reading that encoder's
attachments followed from taking that as a hit - all of its outputs are 1600x896 and it
never touches the texture in question.

The right way to ask "who wrote this texture" is the Memory view: select the resource and
expand its own usage list, or use Reveal in Dependencies from a top-level row (it is
disabled on the expanded child rows). That was not completed.

Worth recording for whoever picks this up: driving this interface by describing clicks to
someone else is where the time went. The parts that worked - compositor screenshots, raw
texture decoding - are the ones that needed no interface at all.

### Two blindfolds, both mine, and what came out from under them

The present surface could not be measured at all until both of these were removed:

  - it is written under its sRGB view, while every search here looked for RGBA8Unorm.
    hdrident logging prints handle, canonical and viewRoot per full resolution texture,
    and shows the present source's canonical pointer equal to an RGBA8_sRGB target's
    handle - the same storage under two format views. This is the third time in these
    notes that view identity produced a false "nothing writes it".
  - the per-frame target census had a silent MaxTargets = 16, and a frame touches more
    than that, so the surface never made it into the list. Raised to 64 and truncation is
    now reported, per the rule in these notes that a cap without a dropped count makes
    "not found" and "not looked for" indistinguishable.

With both gone, the surface finally reports:

    WHITE   1920x1080 RGBA8_sRGB  p=1, d=0
    prev    1920x1080 RGBA8_sRGB  p=1, d=0

One pass, zero draws, every frame, flat or not. Nothing in this backend ever draws into
the surface that gets presented. Its content arrives through that pass's load action, so
"which draw painted it white" was the wrong question the whole time - the right one is how
that storage acquires content at all.

Note it is *not* the same storage as the 1920x1080 RG11B10Float target - their canonical
pointers differ - so the format-view relationship does not connect those two.

### The pass on the presented surface does nothing

With the census able to see it at last, on flat and ordinary frames alike:

    presented surface  1920x1080 RGBA8_sRGB   p=1, d=0, clr=0   (two, alternating)
    scene composite    1920x1080 RG11B10Float p=4, d=96..97

clr=0 means that single pass loads and stores; with zero draws it does nothing at all.
Meanwhile the 96-97 composite draws land on a different storage - the canonical pointers
differ, so this is not the format-view relationship that connected the present source to
its sRGB target.

Every frame reports this, flat or not, so it is not a description of the fault. Either
there is a transfer from the composite to the presented surface that nothing here hooks,
or the identity merging still splits one storage into two records somewhere. Given that
mismatched identity has produced three wrong conclusions in these notes already, the
second is the more likely of the two, and worth settling before anything is built on the
first.

### That contradiction resolves from evidence already taken

No new run needed. The skip-hdr bisect dropped every draw whose colour target is the
1920x1080 RG11B10Float composite, and the screen went constantly white - 24 of 24, luma
248. Dropping those draws could not change what is displayed unless the presented image
derives from that target.

So the two are connected, and "the presented surface takes zero draws" is a bookkeeping
artefact: it and the composite are one storage, and the canonical-pointer comparison that
said otherwise is wrong. That is the fourth conclusion in these notes to come from
identity merging rather than from the game.

What this leaves: the presented storage does receive the composite's 96-97 draws. The
scene at 1600x896 is intact. So the fault is inside those draws or their inputs, and the
per-target census cannot see further - it counts draws, and the draw counts match.

### Which shaders draw the composite, and where the bit-trick lives

Counting draws into the RG11B10Float composite by shader, over a couple of minutes of
play:

    5ccdedfa3a3376d1  80000      797cbc23a0819594  65265
    3821a028b5a7f2b6  40346      9ac2fc4adc220496  31629
    8571d78b93a6bbba  28390      7854e6d7c6fd1230  16068
    ...
    ee89b4e471373459   2305

ee89b4e471373459 is the one carrying the 0x7EF07EBB bit-trick reciprocal - sixteen of
them, in 972 lines. The six that dominate the draw count are small (136-311 lines) and
contain none.

So the integer-wrapping fix reaches exactly one shader out of the set, holding 2305 of
over 260000 composite draws. That is consistent with the partial reduction it measured
(29.1% to 16.0%), but it does not establish it - a 2.4 SE result on a rate that swings
with the camera is thin either way. What it does rule out is the tidy explanation that
other composite shaders share the pattern and were left unfixed. They do not.

## The shader is identified

Per-shader bisect - drop one shader's draws at a time, hot-swapped, with a control between
every arm, judged from compositor screenshots:

    control  41.7  62.5  37.5  41.7  37.5  45.8  41.7   (%)
    5ccdedfa3a3376d1  29.2      797cbc23a0819594  33.3
    3821a028b5a7f2b6  33.3      9ac2fc4adc220496  62.5
    8571d78b93a6bbba  33.3      7854e6d7c6fd1230  37.5
    ee89b4e471373459   0.0   <-- 24 of 24 ordinary

Five of the six dominant shaders sit inside the control's own spread. Dropping
ee89b4e471373459 removes the flash completely. Painting it magenta fills the entire
viewport with the HUD on top, so it is a full-screen pass and its output is the frame -
which is why a flat frame looks exactly like it does.

### The mechanism inside it

It reads the scene with texel fetches, not samples (grep for "sample(" finds nothing
here), and ends:

    temp_297 = temp_292 + temp_290                      // denominator
    temp_302 = bit-trick reciprocal of temp_297         // seed 0x7EF19FFF
    temp_307 = fma(temp_297, -R, fp_c1[0].y)            // Newton refinement
    temp_311 = R * temp_307                             // refined 1/x
    out.color0.xyz = clamp(temp_311 * rgb, 0, 1) * 3.5

One scalar reciprocal multiplies all three channels, and the result is clamped to [0,1].
As temp_297 approaches zero the reciprocal grows, all three clamps saturate together, and
the frame is a uniform 3.5 in every channel - which is exactly the shape measured: flat,
three channels equal, and a clamped 1.0 rather than an overflowed magnitude.

Two of this session's negative results were wrong for mechanical reasons, not because the
mechanism was wrong:

  - the reciprocal guard matched "1.0f / temp" and this shader uses the bit trick, so it
    never applied here at all
  - the bit-trick guard matched seed 0x7EF07EBB; this site uses 0x7EF19FFF, so the eight
    sites it bounded were all the wrong ones. Its bound was also 65504, while saturation
    only needs the product to exceed 1.0

Open: why temp_297 reaches zero here and not on hardware. It traces back through several
hundred temps, and the integer wrapping in the bit trick is already hardware-correct.

### What the denominator is made of

    temp_297 = sum of several bit-trick reciprocal and inverse-sqrt terms
      temp_255 = bitTrick(temp_237) * ...        seed 0x7EF07EBB
      temp_171 = bitTrick(temp_161) * ...        seed 0x7EF07EBB
      temp_212 = bitTrick(temp_207) * ...        seed 0x7EF07EBB
      temp_173 = as_float((as_uint(temp_157) >> 1) + cb) * ...   fast inverse sqrt

An accumulation of 1/x and 1/sqrt(x) terms, whose sum is then reciprocated - a weight
normalisation. Every integer add in the chain now carries wrapping semantics, including
the `int(temp_166) + cb` site, so the integer side matches the hardware.

That leaves the float side: denormal handling (NVIDIA flushes in some modes, Metal need
not) or precision differences in these approximations. Since the denominator is a sum,
small per-term differences can carry it across zero for particular scenes, and crossing
zero is what makes the reciprocal blow up and saturate all three channels together. It
also explains why the fault is view dependent and intermittent - whether the sum crosses
zero depends on the values in frame.

Not established: which term diverges, or whether denormals are involved. That is the next
question, and it is now a question about four expressions rather than about the frame.

### Marking pixels inside the shader

RYUJINX_METAL_SHOW_NEARZERO=label:temp:eps greens pixels where |temp| < eps;
RYUJINX_METAL_SHOW_BIG=label:temp:limit greens them where |temp| > limit. Both leave the
rest of the frame untouched, which matters: an earlier version replaced the output
unconditionally and destroyed the very artefact it was meant to correlate against - the
frame stopped going white, so "no marked pixels" said nothing.

Both need the shader recompiled, so bump CodeGenVersion, and check the log line that
confirms the patch applied before believing a negative.

Results so far, and their standing:

  - |temp_297| < 1e-3 (the reciprocal's denominator): never green while flat frames still
    appeared as white. The denominator is not approaching zero, so that hypothesis is out.
  - |temp_290| > 100 (the factor shared by all three numerators): inconclusive, the
    capture window caught no flat frames at all.

Which leaves both shared factors still open. The three channels saturate together, so it
is either temp_311 (the reciprocal) or temp_290 (shared by the three fma numerators), and
the second has not had a fair test yet.

The reproduction window is the practical constraint: about two minutes before the in-game
sun moves off the angle, against eight to ten minutes per experiment. Anything that needs
several arms should hot-swap them inside one session rather than relaunch.

### The reproduction degraded, and why that matters more than it sounds

Early runs flashed within one or two camera steps of loading. Later ones produced two runs
in a whole sweep, which left the remaining test (|temp_290| > 100, the factor shared by
the three numerators) unanswered twice.

Suspected cause, unverified: the drive-in script presses the confirm key blindly, so the
save-list cursor may have moved and later runs may be loading a different save than the
one the flash was characterised on. Anything automating this should verify which save it
loaded rather than assume, and prefer hot-swapped arms inside one session over relaunching
- the window is about two minutes against eight to ten per launch.

### A save that reproduces it

There is now a manual save (top of the load list, the entry without an "Autosave" badge)
taken while the fault was on screen. Loading it on a clean build reproduces: 267/2340 by
the probe, 9 of 24 compositor screenshots. That replaces "launch, drive in, sweep the
camera and hope" - which failed to catch a single flat frame in three separate experiments
- with something deterministic.

Two things to carry over about running experiments against it:

  - verify the frame is not black before believing any measurement. The SHOW_BIG patch
    blanks the whole image on this shader, and a run under it reported 0 flat frames out
    of 11580 - which was read as "this build might suppress it" when it only meant the
    probe was looking at a black screen. Check for black first, then read the result.
  - the drive-in must confirm what it loaded. Pressing the confirm key blindly walks onto
    whichever entry the cursor sits on, and the manual save is distinguishable only by the
    absence of the Autosave badge.

The SHOW_BIG patch's blanking is undiagnosed. Its insert is conditional and placed after
the last out.color0 write, so on inspection it should leave ordinary pixels alone.

### The identified shader is upstream of the fault, not the fault

Marking pixels inside ee89b4e471373459 on frames that came out flat (mean luma 248-249):

    |temp_290| > 100     0.0% of pixels
    |temp_313| > 1       0.0%
    |temp_313| > 0.9     0.0%

Its values are ordinary on exactly the frames that come out white. The clamps are not
saturating, and nothing in it reaches the magnitudes that would make them.

That corrects the inference drawn from the bisect. "Dropping X removes the artefact"
does not mean X produces it - it can equally mean X feeds whatever does. The paint test
supports the second reading: filling this shader's output with magenta fills the screen,
which shows a downstream stage carries its output through unchanged, and a stage that
carries it through can equally be the one that blows it up.

So the chain is ee89b4e471373459 producing ordinary values, and something downstream
turning them white - most likely the tonemap read early in this session
(3ebc3a8f6b77cc8f), which divides by a luminance built from its input and clamps the
result.

Thresholds cost three runs here. |temp_290| > 100 and |temp_313| > 1 were both picked
without knowing the value range, and both came back empty in a way that looks like a
negative result but only says the threshold was outside the data.

### The marker is sound, and both candidate shaders come out clean

Positive control: with an always-true threshold the frame turns green (78,241,66). The
marker fires, and the marked shader's pixels reach the screen. So the negatives below are
real results rather than a broken tool.

On frames that come out flat, with 14 flat frames sampled:

    ee89b4e471373459   |temp_290| > 100    0.0% of pixels
    ee89b4e471373459   |temp_313| > 1      0.0%
    ee89b4e471373459   |temp_313| > 0.9    0.0%
    3ebc3a8f6b77cc8f   |temp_44|  > 1      0.0%

Neither the shader the bisect named nor the tonemap downstream of it reaches a value that
would saturate its clamps, on exactly the frames that turn white. Whatever produces the
white is not the arithmetic in either of them going out of range.

What still stands: dropping ee89b4e471373459's draws takes flat frames to 0 of 24, and
painting it fills the viewport. What that means is now open again - a shader can be
necessary for the artefact without being where the bad value appears.

One reading that fits all of it: on flat frames these draws do not execute at all. That
would explain zero marked pixels (nothing runs, nothing marks) and leave the frame showing
whatever the load action brought in. It does not obviously explain why removing the draws
entirely stops the artefact, so it needs testing rather than adopting.

## Retraction: the shader was never identified

The bisect result above is an artefact. Mean luma per arm, from the same screenshots:

    control          194.5   (min 156, max 248 - ordinary frames with flashes)
    other five arms  179-213 (same, all inside the control's spread)
    ee89b4e471373459   3.5   (min 3, max 3)

Dropping that shader's draws leaves a black screen, not a flash-free one. "0 of 24 flat"
was true only because a black frame cannot reach the flat-frame threshold. The bisect
showed that removing this shader removes the picture, which is what a full-screen pass
does, and says nothing about the flash. The paint test agrees for the same reason: filling
its output fills the viewport because it draws the viewport.

Everything built on that identification is void - the in-shader markers finding nothing
anomalous were measuring an innocent shader, and the tonemap below it was only implicated
because it sat downstream of the wrong suspect.

The error is the same one this file already records twice: a classifier reporting "the
artefact is absent" when the real answer is "the image is absent". The bisect harness must
check that each arm still renders a picture - mean luma in the control's range - before its
rate is comparable to anything.

Where that leaves the search: the flash is still unattributed to any shader. The reliable
facts remain the ones measured outside this codebase - the 1600x896 G-buffer is intact on
flat frames, the 1920x1080 surface holds the flat value, and there is now a save that
reproduces at about 10%.

### The sampling was never powerful enough

The second bisect, scored with a renders-a-picture check:

    control  66.7  20.8  50.0  25.0  41.7  41.7   (%)
    arms     41.7  45.8  45.8  45.8  41.7

Every arm rendered a picture, and every arm landed inside a control that swings from 20.8%
to 66.7% between adjacent samples. Twenty-four screenshots per arm cannot resolve anything
against that. Every A/B in this file taken at that sample size should be read as
inconclusive rather than negative, including the ones reported as ruling something out.

tools/ab_probe.sh replaces it: the probe classifies every presented frame with the
criterion calibrated on 300 captures, giving hundreds of samples per arm in the same wall
clock, and each arm reports mean luma so an arm that blanked the screen is labelled rather
than scored.

## Session 2026-08-09: the composite covers the frame, measured in hardware

### What was missing from every instrument before this

Everything built so far measures one of two things: what was bound (pass counts, draw
counts, sampled texture identities, constant buffer contents) or what the pixels became
(grid samples, in-shader value markers). All of it reports a flat frame and the frame
before it as identical, and this file records the reading that would explain every one of
those null results at once - that on a flat frame the composite draws produce no fragments,
leaving whatever the load action brought in. It was never tested, because nothing here
measures whether a draw covered anything.

Metal answers that in hardware. CoverageProbe (RYUJINX_METAL_COVERAGE=1) points the
visibility result buffer at the passes whose colour target 0 is the 1920x1080 RG11B10Float
composite, one 64-bit slot per pass, and reports the counts beside the scissor, viewport
and depth compare in force when the pass opened. The GPU writes those counts after the pass
has run, so no state cache, texture view identity or shader arithmetic sits between the
number and the truth - the three things that produced four retracted conclusions above. It
compares a flat frame against its own immediate predecessor inside one run, so there are no
arms, no camera angle confound and no sample size to argue about.

The buffer is primed with all ones rather than zero, deliberately. Zero is the answer being
looked for and is also what an unwritten slot holds, so a readback taken too early would
manufacture exactly the result the probe exists to detect. PENDING means void, not zero.

### Result: coverage is identical, to the fragment

Reproducing save loaded on a clean build, 258 flat frames in 3900 (6.6%), 60 logged runs:

    WHITE  p0:cov=2073600 sc=0,0,2560x1406 vp=1920x-1080 dcmp=Always
           p1:cov=2073600 sc=0,0,1920x1080 vp=1920x1080  dcmp=Always
           p2:cov=126417  p3:cov=501600  p4:cov=253870 (sc=1567,654,315x426 - the minimap)
    prev   identical, value for value, across all 60 runs

2073600 is exactly 1920x1080. The composite draws rasterise the entire frame on flat frames,
with the same scissor, the same viewport and the same depth function as on ordinary ones.

So the draws run and they cover. That closes the whole geometry and fixed-function branch -
scissor, viewport, degenerate vertices, depth or stencil rejection, colour write mask - and
it retires the "these draws do not execute at all" reading this file proposed. What is left
is what the draws read or compute.

### The composite shader is identified again, on grounds the bisect never touched

hdrspan over a full session lists every distinct sampled-size -> written-size pair. Exactly
one program spans the scene to full resolution:

    ee89b4e471373459  samples 1600x896 -> writes 1920x1080

Every other program writing the composite samples UI textures (8x8 to 950x176) or the
1920x1080 surface itself. Combined with a coverage of exactly one full screen in p1, with
dropping it leaving a black screen, and with painting it filling the viewport, this is the
scene composite. Note none of those four facts is the bisect that was retracted - that
argued from a flash rate, and this argues from what the shader reads and how much it covers.

### That contradicts the in-shader marker result, and the marker is the one to doubt

The shader ends:

    temp_313 = temp_311 * temp_310;          // reciprocal x numerator
    temp_314 = clamp(temp_313, 0.0f, 1.0f);
    out.color0.y = temp_314 * 3.5;           // and likewise x and z

A uniform white frame is all three clamps saturating, so temp_313 >= 1 across the frame.
The marker runs above report |temp_313| > 0.9 firing on 0.0% of pixels on flat frames.
Both cannot hold: this shader covers 1920x1080 fragments and its output is the frame.

The likelier failure is the one this file already records twice - a source-patching
diagnostic that never reached the compiler because the disk shader cache was warm, so the
run measured an unpatched shader and read the absence of marks as an absence of the value.

### Next, and it needs no thresholds

Mark where all three clamps saturate - `temp_313 >= 1 && temp_315 >= 1 && temp_317 >= 1` -
rather than at a threshold picked without knowing the range. That question is
self-calibrating: on an ordinary frame a few sky pixels go green, and on a flat frame the
whole screen does. Bump CodeGenVersion so it is actually compiled, and confirm the positive
control inside the same run.

  - flat frames turn green -> this shader computes the white, and the search moves to which
    term of temp_297 crosses zero
  - flat frames stay white -> its clamps are not saturating, the pass that reads the scene
    and writes full resolution is innocent, and the white arrives downstream of it

### The stamp run: the shader runs, does not saturate, and still writes white

Two patchers on ee89b4e471373459 at once, both confirmed applied in the log:
an unconditional 96x96 magenta corner, and green where all three clamped products
reach 1. Reproducing save, 26 compositor screenshots, 7 of them flat:

    flat frames    scene mean 254, 99.9% saturated
    stamp          present on all 26, including all 7 flat frames
    green          0.0% of pixels, on flat and ordinary frames alike

The stamp settles what the green alone could not. A marker that never fires and a value
that never occurs look identical, and the green was 0.0% everywhere - on its own it says
nothing. The stamp is unconditional, so its presence on a flat frame proves the shader ran
and that its pixels reached the screen. Both readings this file left open are now closed:
the draws execute, and their output is the frame.

Then the stamp's colour, which decides the rest. It measures (242, 62, 246) on flat frames
and (242, 62, 246) on ordinary ones - the same bytes. A downstream exposure or tonemap
blowing the frame up would have lifted the stamp's green channel along with everything
else. It does not move. So nothing after this shader is applying a gain, and the white is
this shader's own output, in every pixel that was not forced to magenta.

### That leaves exactly one value

The shader writes `clamp(x, 0.0f, 1.0f) * 3.5` per channel. On a flat frame the output is
at the top of that clamp while `x >= 1` evaluates false on the same pixels. NaN is the only
value that does both: it is not less than the bound, not greater, not equal, so every
comparison against it is false, while the clamp still resolves it to a bound.

It also explains, in one stroke, every null marker result recorded above:

    |temp_297| < 1e-3   0.0%      NaN is not less than anything
    |temp_290| > 100    0.0%      NaN is not greater than anything
    |temp_313| > 1      0.0%
    |temp_313| > 0.9    0.0%
    |temp_44|  > 1      0.0%      the tonemap, same test, same blindness

Four separate runs read those as "the value never reaches this range". A NaN reaches no
range. Every magnitude marker built in this investigation was structurally incapable of
seeing the thing it was pointed at.

Testing it needs care in one respect: isnan() is precisely what fast math is allowed to
fold to false, and Metal compiles with fast math on by default, so asking in the float
domain risks measuring the optimiser. RYUJINX_METAL_SHOW_NAN tests the bit pattern instead
- exponent all ones with a non-zero mantissa - which no arithmetic rewrite can touch.

Note this also weakens the "Metal fast-math disabled: 31.9%, unchanged" exclusion above.
Under IEEE semantics fmax(NaN, 0) returns 0, so clamp(NaN) would be 0 and the frame would
go black rather than white - and a black frame scores zero flat frames by a saturation
criterion, not 31.9%. That result needs re-taking with the shader cache invalidated.

## Session 2026-08-09, later: the composite is innocent and the fault is upstream

Four in-shader experiments on ee89b4e471373459, each confirmed applied in the log and each
carrying its own control in the same picture. Judged from compositor screenshots against
the reproducing save.

### 1. It runs, and its clamps do not saturate

Unconditional magenta corner plus green where all three clamped products reach 1. Over 26
shots with 7 flat frames: the stamp is on every frame including all the flat ones, and the
green never fires. So the shader executes and its pixels are the frame - but that alone
proved nothing, because a marker that never fires looks exactly like a value that never
occurs, which is how four earlier runs in this file were misread.

### 2. Nothing downstream applies a gain

A four step grey ramp (0.05/0.15/0.35/0.70) written into the corner reads 55/96/149/212 on
flat frames and 55/96/149/212 on ordinary ones, while every pixel around it goes to 254.

This replaced a magenta stamp that could not answer the question. (1, 0, 1) x 3.5 is far
above any tonemap's white point, so it clips identically under a gain of one or a thousand
- and its colour being unchanged had been read here as proof that no gain exists. A
saturated probe cannot detect saturation. That inference was wrong and is withdrawn.

### 3. The flat frame is uniform, not overexposed

Bracketing the output by band - green at or above 3.0, blue from 0.9 - gives 100% blue on
flat frames, never green, with ordinary frames at 7% blue as the control. The output is
near 1.0, not the 3.5 that saturating clamp(x, 0, 1) * 3.5 would produce.

So temp_313 sits around 0.26-0.86 on a flat frame, which is exactly why every marker testing
it for a large magnitude or for NaN came back false. Those markers were right. The
saturation hypothesis they were built on was not, and this restores the earliest measurement
in these notes: a flat frame is a constant colour, uncorrelated with the scene.

### 4. The coordinate is fine and the fetched texel is white

Drawing the fetch coordinate into red and green with flatness in blue: on flat frames the
coordinate ramps across the screen exactly as on ordinary ones, spread 131/119 against
130/124. It does not collapse, so every pixel reads a different texel.

Drawing the fetched texel instead:

    flat frames    fetched R mean 254, G mean 254
    ordinary       fetched R mean 151-163, G mean 149-158

The 1600x896 RG11B10Float scene texture this pass reads is already uniformly white on flat
frames. The composite reproduces it faithfully. It is not the fault.

The binding is not the fault either: the input slot reports the same root pointer
(0x9DB944C80) on flat frames and on the frames before them, so this is the same host
texture holding different content, not a draw sampling the wrong texture.

### What this overturns, and where to go next

It sits against "seven 1600x896 scene targets: none flat" from the GPU trace. That reading
examined every 1600x896 texture in the capture without knowing which one the failing draw
had bound, and judged flatness by dominant-value share. This measures what the failing draw
actually reads, per pixel, on the frames that are actually flat, with a positive control in
the same image. Where they disagree, this one is the stronger evidence - but the
disagreement is worth resolving rather than assuming.

Next: apply the same method one stage earlier. Find what writes 0x9DB944C80, and ask
whether its inputs are white on the same frames. The technique now has a track record -
show the value, keep an artefact indicator in a spare channel, and never test a magnitude
against a threshold picked without knowing the range.

### Two CPU-side instruments cannot corroborate it, and that is worth knowing

Pointing the per-pass content walk at the 1600x896 stage (RYUJINX_METAL_WATCH_WIDTH=1600),
with the missing at-present sample added, gives "varied" at every entry on flat frames -
the opposite of what the in-shader fetch measured. And the input sampler disagrees with
itself between runs: one session reported the narrow span on the flat frame and the wide one
on its predecessor, the next reported exactly the reverse.

Both are sampled at present, after the frame's rendering has finished and the texture may
already have been rewritten for the next one, so neither is reliably associated with the
frame it is printed against. Min and max of packed RG11B10 words mixes three channels into
one ordering, which makes the numbers look meaningful when they are not.

The in-shader measurement has no such gap: it reads what the shader reads, at the moment it
reads it, on the frames that are flat, with a control in the same image. Where they
disagree it wins, and the two CPU-side readings should not be quoted as evidence either way
until they are re-taken at the point of use.

### Where this stands, and what to do next

Settled, each with a positive control:

  - the composite draws cover the frame exactly as on ordinary frames
  - the composite shader runs, its coordinate ramps normally, its clamps do not saturate,
    its output is near 1.0 rather than a saturated 3.5, and nothing downstream applies a gain
  - what it reads is already white
  - the binding is the same host texture on flat frames and their predecessors

Retracted: the saturation hypothesis and everything derived from it, the inference from the
magenta stamp, and the "seven 1600x896 targets, none flat" trace reading as applied to the
bound texture.

The next stage up is the scene buffer itself, and it is not one shader - hdrcomposite at
WATCH_WIDTH=1600 lists dozens of programs and thousands of draws per frame. So the method
has to change with it. Two routes, in order of cost:

  1. Ask whether the composite reads that texture before the scene pass has finished
     writing it. A white uniform value is what an untouched or cleared target looks like,
     and "end the pass after every draw" already measured 34.2% against a 49.1% baseline -
     a partial response is what an ordering fault gives, not what an arithmetic one gives.
  2. If not ordering, bisect the draws into that target by index range (SetSkipRange is
     already there) rather than by shader, since no single shader owns it.

Whatever comes next, keep the technique that worked tonight: draw the value rather than
test it, keep an artefact indicator in a spare channel so the bad frames are identifiable in
the same picture, and never compare a magnitude against a threshold chosen without knowing
the range.

### The raw handle clears the two-texture suspicion, and points at ordering

A flat frame carries two 1600x896 RG11B10Float textures - one taking 29 draws over 25
passes, and one with a single pass and no draws at all. If the composite sampled the empty
one, the frame would be whatever that texture holds, which is the right shape for the
artefact, and a report keyed on canonical identity could not tell them apart because
aliases of the same guest memory share it.

Printing the raw MTLTexture handle beside the canonical one settles it:

    WHITE  slot128 handle 0xB910A4000 root 0xB9109BC00  span 0x001C43BC..0x781D1B7B
    prev   slot128 handle 0xB910A4000 root 0xB9109BC00  span 0x781DFBBF..0x781E03C0

Same raw handle on both. The composite is not reaching a different texture, so the
two-texture reading is out.

What the spans say is more interesting, and it is the opposite way round from the guess:
the near-uniform sample - 0x781DFBBF to 0x781E03C0, one quantisation step of one channel -
lands on the frame *before* each white one, while the white frame itself samples varied.

Those samples are taken at present, and the composite draws in the middle of the frame, so
the two are not measuring the same moment. Read together they describe a texture that holds
a uniform value when the composite reads it and the scene by the time the frame ends -
which is a read-before-write ordering fault, not corrupted content. It also fits the one
intervention that ever moved the rate: ending the pass after every draw measured 34.2%
against a 49.1% baseline, a partial response of the kind an ordering fault gives.

To confirm rather than adopt it, the input has to be sampled at the point of use instead of
at present - either in the shader (which has been the reliable instrument all session) or
by blitting the input immediately before the composite pass opens.

### The strict barrier does not fix it, measured with enough samples this time

Hot-swapped inside one session against the reproducing save, alternating twice, scored on
probe counts rather than screenshots, with mean luma checked so a blanked arm would be
visible:

    barrier off   574/1440 = 39.9%   luma 153
    barrier ON    485/1260 = 38.5%   luma 153
    barrier off   597/1380 = 43.3%   luma 164
    barrier ON    541/1260 = 42.9%   luma 154

About 1300 samples per arm puts the standard error near 1.4 points, and both differences
are inside that. Both arms render a picture. Forcing every guest TextureBarrier to split the
pass does nothing to the rate.

That is a real negative, unlike the earlier version of this measurement, which used 24
screenshots and the criterion that was later withdrawn. It removes the obvious lever for
the ordering reading without disposing of the reading itself - the ordering that matters
would then be between render passes, which Metal sequences on its own, or somewhere the
guest's barrier calls never reach.

### Parent residency: ruled out, same method

Sampled textures reach the shader through an argument buffer, so the encoder is told about
them with useResource - and this backend names the handle it bound, which for a view is a
distinct MTLTexture from the one the render pass wrote as an attachment. Apple's guidance
for views is to declare the parent, and the composite's input is exactly that shape
(handle 0xB910A4000 against root 0xB9109BC00). Naming the parent instead, hot-swapped
through /tmp/ryujinx-metal-parent-residency:

    residency = view      567/1380 = 41.1%   luma 156
    residency = PARENT    509/1260 = 40.4%   luma 150
    residency = view      511/1320 = 38.7%   luma 154
    residency = PARENT    577/1380 = 41.8%   luma 155

About 1300 samples an arm, both arms rendering, everything inside a standard error of
about 1.4 points. No effect. The toggle stays in, off by default, since it is a correctness
question on its own terms even though it is not this bug.

Three mechanisms have now been ruled out at adequate power against the same reproduction:
guest texture barriers, argument-buffer residency on the view, and everything in the
composite shader itself. What still stands is the pair of measurements that disagree only
about *when*: at the moment the composite fetches, its input is white; by the time the
frame is presented, that same texture holds the scene.

### The composite samples a texture nothing renders into

The handle in the input report was read from a live field for both the flat frame and its
predecessor - the same current value printed twice - so "the same raw handle on both" was
never a paired comparison and the conclusion drawn from it is withdrawn. Recording the bound
handle per slot instead:

    WHITE  bound 0x9AEDF0280 root 0x9AEDE3980   span 0x001B43A7..0x781D1375  varied
    prev   bound 0x9AEDF0280 root 0x9AEDE3980   span 0x781E03C0..0x781E03C0  min == max
    WHITE  bound 0x9AEDF0280 root 0x9AEDE3980   span 0x001B3BA6..0x781D1B75  varied
    prev   bound 0x9AEDF0280 root 0x9AEDE3980   span 0x77DDFBBF..0x77DE03C0  near uniform

The binding really is stable, so the composite is not reaching a different texture. But the
frame before each white one samples uniform, three pairs out of three, and that is not
noise.

Then the census for the same frame:

    [0x9AEC0B480:1600x896:RG11B10Float:p=24,d=28]   the scene buffer
    [0x9AEDE3980:1600x896:RG11B10Float:p=1,d=0]     one pass, no draws at all

The composite's input root is 0x9AEDE3980 - the one nothing ever draws into. Same size,
same format, a different host texture from the one the scene is rendered into. On ordinary
frames it holds the scene anyway, so something keeps the two in step, and on flat frames
that something has not run by the time the composite samples.

nonRenderWrites reports none, which by the rule in these notes means the enumeration is
incomplete rather than that no writer exists - the hooks match on RootOf, so a copy landing
under another identity is invisible to them.

Next, and it is a structural question with a short answer: are 0x9AEDE3980 and 0x9AEC0B480
views of one storage or separate allocations? NoteIdentity filters at width 1900 and never
saw either; dropping that to 1500 prints handle, canonical and viewRoot for both.

  - views of one storage -> the content is shared and the fault is ordering between a write
    through one and a read through the other
  - separate allocations -> a copy has to connect them, and on flat frames it is late or
    missing. That is the texture cache's overlap path, whose own comment in the shared layer
    says the kept texture "is going to contain garbage data after we draw"

### They are two separate allocations, not two views of one

With the identity filter lowered to width 1500, both 1600x896 RG11B10Float textures print:

    hdrident target 1600x896 RG11B10Float handle=0xA36334A00 canonical=0xA36334A00 viewRoot=0x0
    hdrident target 1600x896 RG11B10Float handle=0xA36857980 canonical=0xA36857980 viewRoot=0x0

canonical equal to handle and no viewRoot on both: each is a base texture, neither a view of
the other. So the scene renders into one host texture and the composite samples a different
one, and the only thing that can put the scene into the sampled texture is a copy.

That settles which of the two faults this is. It is not ordering between a write and a read
of shared storage - there is no shared storage. Something has to copy 0xA36334A00 into
0xA36857980 every frame, and on flat frames the composite samples before that copy lands,
or it does not happen at all. The uniform value read on the frame before each white one is
what the destination holds when it has not been filled.

That also explains why three interventions came back at exactly no effect: guest texture
barriers, argument-buffer residency on the parent, and every property of the composite
shader are all irrelevant to a copy that has not been issued.

Next: instrument the copy. Which call fills 0xA36857980, on which frames, and where in the
frame relative to the composite's pass. Texture.CopyTo and the texture cache's overlap
handling are the places to hook, and the hooks must not key on RootOf - that is what made
nonRenderWrites report none while a copy was evidently happening.

### Draw attribution was wrong, and the copy hunt has gone as far as enumeration can

The per-target census credited draws only to colour target 0, so any texture that is only
ever a secondary MRT attachment reported d=0. That reads as "nothing ever draws into this"
and it is a bookkeeping artefact - one which a conclusion here was briefly built on, now
withdrawn. Fixed: draws are credited to every attachment of the pass, and the census reports
truncation, which it also did not do.

With that corrected the 1600x896 textures report 1500-odd draws each, and the one the
composite samples still reports p=1, d=0 - so the observation survives the fix. That pass
carries no draws and no clear, which makes it a no-op, and the texture's content has to
arrive some other way.

Hooked, all reporting NONE on flat and ordinary frames alike:

    Texture.CopyTo (all three overloads)
    HelperShader.BlitColor (the render blit, which fits p=1 d=0 exactly)
    Texture.SetData (all three overloads)

And yet the shader measurably reads the scene out of that texture on ordinary frames. So a
writer exists and the enumeration is short one path - which is the point at which this
file's own rule applies: a negative built from hooks is not proof, only a sentinel is.
The rule is there because this exact conclusion was overturned once before, when four hook
types with positive controls all said nothing wrote a texture and staining it green proved
otherwise.

So the next step is not another hook. Fill every newly created 1600x896 RG11B10Float with a
distinctive constant and read the screen:

  - ordinary frames still show the scene -> a writer exists, the enumeration is incomplete,
    and the stain names when it is overwritten
  - flat frames show the stain -> a flat frame is this texture read before anything wrote it

RG11B10Float packs to 32 bits, so magenta is 0x780003C0 and a filled staging buffer blitted
in at creation is enough.

### The sentinel says a writer exists; the clear is black, so it is not the clear

Filling every newly created 1600x896 RG11B10Float with 0x55 at creation (a constant that
decodes to a bright red, not white) and reading the screen: the scene renders normally and
across 24 shots with 6 flat frames the stain appears on none of them - 0.0% red.

So a writer exists and overwrites the stain, which is what the rule predicted and what
retires the enumeration: CopyTo, the render blit and SetData reporting NONE meant the hooks
were short a path, not that nothing wrote it. It also rules out the reading that a flat
frame is this texture read before anything touched it. Something writes white.

The draw-based clear was the obvious candidate for the p=1 d=0 pass - ClearRenderTargetColor
goes through a helper draw rather than a load action, so it opens a pass the census counts
and issues a draw it does not. Hooking it with its colour:

    sceneclear 0xACE9FAF80: 0.000, 0.000, 0.000, 0.000
    sceneclear 0xACEB5F980: 0.000, 0.000, 0.000, 0.000

Both scene textures are cleared to black. So the clear explains the overwritten stain and
the p=1 d=0 entry, but not the white.

What remains unresolved is the identity split: one census entry takes 1533 draws and
another takes none, and the composite samples the second while measurably reading the scene
out of it on ordinary frames. Those two entries are most likely one storage under two
identities, which would make the whole picture an ordering question again - the composite
sampling while the scene draws are still in flight. Settling that needs the two entries
tied together or told apart by something other than RootOf, which is the comparison that
has produced every identity error in this file.

### Retraction: "the composite samples a texture nothing draws into"

The input report lists two 1600x896 RG11B10Float slots, and reading their identities:

    slot136 -> root 0xACE9FAF80 -> census p=24, d=1527    the drawn scene texture
    slot128 -> root 0xACEB5F980 -> census p=1,  d=0       the one only ever cleared

Both are recorded as inputs. The claim above - that the composite samples the texture
nothing draws into - was made by reading slot128 and treating it as "the" input. That was an
arbitrary pick between two, and the shader itself settles that it cannot be both: its
Textures struct declares exactly one texture, tex_fp_t_tcb_8. So one of the two recorded
slots is not this shader's input at all; NoteToneMapInput is not filtered per shader, and
other programs draw into the same watched target.

Two commits asserted that claim and both are withdrawn. What it was built on - a d=0 that
turned out to be a draw-attribution artefact, then a slot chosen without checking which one
the shader uses - is the same failure twice in one session.

What survives untouched is the in-shader measurement, because it reads whatever
tex_fp_t_tcb_8 actually resolves to, at the moment the shader reads it:

    flat frames    fetched R mean 254, G mean 254
    ordinary       fetched R mean 151-163, G mean 149-158

The composite's input is white when it is fetched. Which host texture that is remains
unidentified, and identifying it is the next step: record the binding for this shader only,
rather than every texture bound to any draw on the target.

### The input report was following a different program the whole time

NoteToneMapInput's call sites were hardcoded to program.DebugLabel == "3ebc3a8f6b77cc8f" in
three places. That is the tonemap. The shader measured in-shader all session is
ee89b4e471373459, the composite. So every "WHITE inputs" and "prev inputs" line printed the
tonemap's bindings while being read alongside a fetch measurement from the composite - two
different programs, never the same texture.

The label is now RYUJINX_METAL_INPUT_WATCH, defaulting to the old value. Pointed at the
composite it reports:

    slot128 -> root 0xC9BBCC000 -> census p=1,  d=0
    slot136 -> root 0xC9BB15E00 -> census p=25, d=1528

Two entries for a shader whose Textures struct declares one texture, because the array and
non-array binding branches both record.

Which one it samples follows without another run. The p=1 d=0 texture is cleared to black
every frame and written by nothing else, so a shader sampling it would produce a black
screen. Ordinary frames show the scene. Therefore the composite samples 0xC9BB15E00 - the
scene texture, with its 1528 draws.

That closes the identity question and returns the statement to its stable form, now about a
properly identified texture: at the moment the composite fetches it, the scene texture reads
white on flat frames, and by the end of the frame it holds the scene.

The CPU-side spans cannot corroborate that either way. They are sampled at present, after
the frame's rendering has finished, and across pairs they come out inconsistent - narrow on
the predecessor in some, narrow on both in others. Only the in-shader measurement is taken
at the point of use, and it is the one to trust.

### Auto-flush: ruled out

If the composite fetched the scene texture while the scene passes were still outstanding,
the obvious Metal-side cause is command buffer splitting - the scene landing in one buffer
and the composite in the next. RYUJINX_METAL_AUTO_FLUSH=0 disables it. Same save, camera
untouched so both runs face the same view, probe counts:

    autoflush = default   790/1860 = 42.5%   luma 153
    autoflush = OFF       771/1800 = 42.8%   luma 157

About 1800 samples an arm, both rendering. No effect.

Four mechanisms are now ruled out at adequate power against this reproduction: guest
texture barriers, argument-buffer residency on the parent, command buffer splitting, and
every property of the composite shader itself.

## The composite is pass 2 of every frame, so every instrument was one frame late

Counting pass ordinals within a frame and printing them paired:

    WHITE order: composite@2 lastScene@167 of 169
    prev  order: composite@2 lastScene@175 of 177
    WHITE order: composite@2 lastScene@162 of 164
    prev  order: composite@2 lastScene@170 of 172

The composite is the second pass of the frame, always, on flat frames and ordinary ones
alike, while the last scene pass is around the 167th. So frame N's composite consumes the
scene buffer as frame N-1 left it. That is ordinary structure for a pipeline that composites
the previous frame's scene - and it inverts the causal direction this investigation has been
reading all along.

It also resolves the observation that looked backwards. The input sampler reported the
near-uniform span on the frame *before* each white one, which made no sense while the white
frame was assumed to be where the fault happened. It is exactly right: the present-time
sample of frame N-1 is what frame N's composite goes on to read, because the composite runs
at ordinal 2 and nothing writes that texture in between. Three pairs out of three.

So the question is no longer "why does the composite read white". It is:

    why does the scene buffer end about 40% of frames holding a uniform value,
    on frames that themselves display correctly?

Every probe aimed at the flat frame was aimed one frame too late. The chain content walk,
the coverage counts, the in-shader markers - all of them measured the frame that displays
the fault rather than the frame that creates it. What they established about the composite
still stands (it covers, it does not saturate, it faithfully reproduces its input), and that
is now the expected result rather than a puzzle: the composite is innocent because the
damage was already done a frame earlier.

Next: point the per-pass content walk at the scene texture the composite actually samples -
the 1600x896 RG11B10Float with ~1528 draws, not the one that only takes a clear - and find
which of its ~167 passes leaves it uniform. The walk already exists; it has been following
the wrong one of the two, because IsWatchedTarget matches on width and format and there are
two textures answering to both.

### Following the right scene texture, and an instrument disagreement to settle first

IsWatchedTarget matched on width and format, and two 1600x896 RG11B10Float textures answer
to both, so the content walk had been following them interchangeably. It now latches the one
with draws:

    hdrwatch: following 0x9369F2D00 (2184 draws over 511 passes)

The latch works, but that number does not reconcile with the ordinal counter, which reports
about 169 passes in a whole frame. Both are per-frame and both reset in Commit, so one of
them is wrong, and until that is settled neither the census pass counts nor the ordinals
should be quoted.

The walk itself still returns eight entries, all varied, identical on flat frames and their
predecessors - and its last entry is the at-present sample, which says varied at end of
frame while the input sampler says near-uniform for the same texture at the same moment.
That is a second disagreement between two CPU-side instruments.

Eight entries out of hundreds of passes is also expected rather than surprising:
SampleBeforePass only fires when the encoder is not already a render encoder, so
consecutive render passes are invisible to it. Following a chain of this length needs
sampling that does not depend on encoder transitions.

So the next session starts with instrument reconciliation, not with a new hypothesis:

  1. Why the per-target pass count and the frame ordinal count disagree by 3x
  2. Why the at-present sample and the input sampler disagree about the same texture
  3. Sampling that reaches every pass, not only those that follow a non-render encoder

The reframing above does not depend on any of that. It rests on the composite's ordinal
being 2 on every frame, which is a single integer read at pass creation, and on the
in-shader fetch measurement, which is taken at the point of use.

### Both instrument disagreements were misreadings, and the numbers stand

The pass-count gap: hdrwatch printed at 00:00:44, during loading, and the line only prints
when the latched root changes - so it printed once, on a load frame. 511 passes and 2184
draws is ordinary there; the ~169 it was compared against came from in-game frames. Nothing
disagrees.

The sampler gap: DescribePassContent tests nine words for strict equality and says "varied"
the moment any two differ, while DescribeInputs prints min..max over twenty-five. A span of
0x781DFBC0..0x781E03C0 is 0x800 wide - narrow, but not equal. Both readings are correct
together, and "narrow span" was being read here as "uniform". Nothing disagrees.

So neither figure needs discounting, and the reframing keeps its supporting data rather than
resting on the ordinal alone. It does change one wording above: the scene texture at the end
of the frames preceding white ones is *nearly* uniform - a span of one quantisation step of
one channel - not literally constant. That is still a scene compressed into a single step,
which is what the composite then reads and reproduces as flat.

What remains for the next session is the sampling reach, and only that: SampleBeforePass
fires only when the encoder is not already a render encoder, so it sees eight of the
hundreds of passes writing the scene texture. Following which pass leaves that texture in a
one-step range needs sampling that does not depend on encoder transitions.

### The frame that causes a flash does identical work on the scene texture

Mined from the log already on disk - no new run, no added instrument, so nothing perturbed.
For each white frame, its own census against the census of the frame before it, which is the
frame whose end state the composite goes on to read:

    frame N (white)        frame N-1 (causes it)
    p=22 d=1532            p=24 d=1530
    p=24 d=1524            p=24 d=1530
    p=23 d=1524            p=23 d=1523
    ...twelve pairs, all within the same few counts

The frame that leaves the scene texture in a one-step range draws into it exactly as much
as any other - about 1524 draws over 23 passes either way. So "a draw goes missing" is closed
at this stage too, the same way it closed one stage up.

Note also what the corrected attachment crediting reveals about the target set: three
1600x896 targets report 1502 draws over 18 passes each, which makes them MRT siblings of one
pass set, while the RG11B10Float the composite samples sits at 22-24 passes and 1523-1532
draws - attached to a few passes more. So it is a G-buffer attachment, not a separate
post-process stage.

That sharpens the question one more turn: on a frame that causes a flash, one attachment
ends in a one-step range while the frame it belongs to displays correctly - so its siblings
presumably do not. Sampling two of those siblings at present, alongside the one already
sampled, would establish whether the fault is specific to this attachment or shared across
the pass set. That is bookkeeping plus two more blits at present, which is where the
existing sampling already happens, so it costs nothing in timing.

### The two 1600x896 RG11B10Float textures are one storage

Sampling two sibling attachments at present, beside the one the composite reads:

    slot128 (root 0xA8E405180)   0x001C6BC0..0x781D1B7A
    sib0    (root 0xA8E12A800)   0x001C6BC0..0x781D1B7A
    slot128                      0x781DFBC0..0x781E03C0
    sib0                         0x781DFBC0..0x781E03C0

Byte-identical on every pair, frame after frame, from two different MTLTexture objects with
different roots - while the RGBA8 sibling sampled from the same buffer reads something else
entirely, so this is not the sampling writing over itself.

They are one storage under two identities. That retires the missing-copy line completely:
there was never a copy to find, because there is only one buffer, and the census reports it
twice because the draws land through one identity and the composite binds the other. It also
explains the p=1 d=0 entry that two withdrawn commits were built on - that is the second
identity of a texture with 1524 draws on it.

So the picture closes to one sentence, with every part of it measured:

    the composite reads this storage at pass 2, the scene finishes writing it around pass
    167, so frame N's picture is what frame N-1 left - and about 40% of frames leave it
    inside a single quantisation step.

What is left is why a frame that does identical work - 1524 draws over 23 passes either way -
ends that way. Two directions, in order of cost: whether the last passes to write it differ
in what they read, and whether the draws that write it are the same draws on both kinds of
frame rather than merely the same count.

## The write lands in the last two passes of the frame, and only compute is left

With span classification and the clear flag in the bracketed chain, the shape is finally
crisp, pair after pair:

    causing frame:  [@47]varied -> scene all the way -> [@163]d0:varied -> present STEP(0x781DFBC0)
    white frame:    [@39]d0:STEP(0x781DFBC0) -> scene overwrites -> varied by mid-frame

The frame that causes a flash holds the scene at every sample through pass ~161 and turns
into the one-step flat state between the last mid-frame sample and present - a window of
about two passes at the end of the frame. The white frame then begins with that flat state,
which is exactly what its composite reads at pass 2, and its own scene rendering overwrites
it by mid-frame. The two halves finally agree with each other and with every earlier
measurement.

Inside that window sits a render pass on the storage with zero draws and no clear flag - it
cannot change content. No clear fires there (no 'c' anywhere in the sequences). Copies,
render blits and SetData are all hooked and silent. The one mechanism in this backend that
none of the enumeration covers and that can write a texture without any of those paths is a
compute dispatch writing it as an image - and the frame runs about 38 dispatches. It is also
the writer that would have beaten the creation-time stain.

So the next session has one concrete task: hook compute image bindings for the watched
storage - which dispatch binds it as a writable image, at which ordinal, and with which
pipeline - and pair that against flash-causing frames. If a dispatch in that end-of-frame
window binds it on causing frames, that dispatch is the fault, and the question of why it
writes near-1.0 white (an exposure or sky value, by the look of 0x781DFBC0) has an owner.

## Where this stands at handoff (2026-08-09, ~10:00)

Compute image bindings: never, even matched by size and format rather than root. So in the
end-of-frame window nothing this backend can enumerate writes the storage: no draws, no
clear, no copy, no blit, no SetData, no compute image.

That forced a re-read of the chain's own construction, and the gap is there: chain entries
are created only when the storage is COLOUR TARGET 0 (BeginPass path) - seven per frame,
matching the coverage probe's seven. The census counts 22-24 passes per frame on this
storage, so about SIXTEEN passes where it is a secondary MRT attachment are neither in the
chain nor sampled. The flat writer can simply be one of those, running after ordinal ~163.

NEXT (concrete, cheap, no perturbation): record PassDetail entries - ordinal, draws,
cleared - for EVERY pass touching the watched storage, from RecordTarget as well as
BeginPass. Bookkeeping only, no sampling, no encoder change. Then the sequence names the
passes that run between the last varied sample and present, with their draw counts, and the
writer is one of a handful of identified passes rather than "something".

The settled chain, for whoever picks this up:
  - composite reads the storage at pass 2; frame N shows what frame N-1 left
  - causing frames hold the scene at every sample through ~@163 and end one-step-flat
    (0x781DFBC0..0x781E03C0 = ~(1.0, 1.0, 0.94))
  - white frames start flat and their own scene overwrites it by mid-frame
  - the storage is one allocation under multiple view identities; every identity-keyed
    hook misses writes through the other identities - match by size+format, never by root
  - the reproducing save is the top entry without the Autosave badge; ~40% flat in the
    trigger view; tools/satmark_run.sh drives everything

## Final state of this session: the flip crosses two back-to-back zero-draw passes

With both identities charted, the causing frame's tail reads:

    [@162]d10:varied -> [@163]d0 -> [@164]d0 -> present: STEP or exactly UNIFORM

Two adjacent zero-draw passes - one per identity - sit at the very end of the frame, and
the content flips from scene to flat across that boundary. A zero-draw load/store pass
cannot change bytes, so whatever those passes actually do is not the plain load/store the
census assumes. One backend mechanism fits: the colour-write-mask emulation
(PreMaskRenderTargets in EncoderState), which swaps attachments to emulate masks and
restores them afterwards - a path in which a zero-draw pass on this storage can
legitimately store content that came from somewhere else.

Where to pick up:

  1. Read the PreMask swap/restore path end to end. If a masked operation late in the
     frame swaps the scene storage out and restores the wrong content - or restores from a
     texture that holds near-white - that is the writer with every property measured
     tonight: no draws, no clear, no copy, beats the stain, flips content between two
     zero-draw passes.
  2. Log SetPreMaskRenderTargets calls naming the scene storage with the frame ordinal,
     paired white-vs-good. One session, one answer.
  3. If PreMask is innocent, the remaining space is the load action of those two passes at
     encoder-creation time (log the actual MTLLoadAction written into the descriptor for
     the watched attachment, not the probe's flag).

Everything else about the fault is settled and documented above: composite at pass 2,
frame N shows frame N-1's end state, causing frames hold the scene until the last passes,
the flat value is ~(1.0, 1.0, 0.94), and the storage is one allocation under multiple
identities - match by size and format, never by root.

## The precise suspect: duplicate MRT dedup defeated by views

UpdateRenderTargets dedups the case the mask emulation exists for - the same texture bound
at two colour slots with one slot's write mask zero - by comparing `colors[i] == colors[j]`.
That is REFERENCE equality on ITexture. Two views of one storage are different ITexture
objects, so for exactly the case measured all session (the scene storage bound under two
identities), the dedup never fires, both slots stay attached, and the fragment shader
writes a defined value to one slot and an UNDEFINED value to the other - into the same
storage through the second identity.

Every property measured tonight fits: intermittent (depends on that frame's binding
pattern), invisible to every root-keyed hook, no copy, no clear, no compute, beats the
creation-time stain, and the flip sits at passes whose census identity is the second view.

Test: make the duplicate check compare storage identity - resolve both to the base
MTLTexture, not the view object, and not CanonicalPtr (which stops at the intermediate view)
- then A/B against the reproducing save with the probe counts. If the rate collapses, this
was it; the fix is the comparison, plus an audit of MaskOut/restore for the same assumption.

# FIXED: the duplicate-MRT dedup, confirmed causal (2026-08-09)

    fix ON  (storage comparison)     0/2700  = 0.0%   luma 152 - scene renders
    fix OFF (RYUJINX_METAL_DEDUP_BY_REF=1)  1127/2640 = 42.7%  luma 157

Same save, back-to-back launches, probe counts, both arms rendering. The rate collapses to
zero under the fix and returns in full when the old reference comparison is restored.

The fault, end to end: the game binds the scene HDR storage at two colour slots at once,
one slot's write mask zero. The mask emulation dedups that case by ITexture reference; the
game's two bindings are two views of one storage - different objects - so the dedup never
fired, both slots stayed attached, and every draw stored a defined value through one slot
and an UNDEFINED value through the other, into the same memory. On this hardware the
undefined store lands near (1.0, 1.0, 0.94) - the flat white. The corrupted end-of-frame
state is then consumed by the NEXT frame's composite, which runs at pass 2, which is why
the flash appears one frame after the damage and why every probe aimed at the white frame
found nothing.

The fix is the storage-identity comparison in UpdateRenderTargets (CanonicalPtr as well as
reference), with RYUJINX_METAL_DEDUP_BY_REF=1 as the kill switch.

# RETRACTION OF THE FIX CLAIM: the dedup change does nothing

In-session single-variable A/B, hot-swapped toggle, user-loaded save, compositor
screenshots (the one instrument that has never lied here), 50 shots per arm, two rounds:

    fix ON   38.0% / 30.0%
    fix OFF  32.0% / 32.0%

All four arms inside noise. The duplicate-MRT dedup comparison is not the cause. The
0/2700 "confirmation" was a bad measurement twice over: its arms differed in probe
environment, and its wait condition triggered on frame count rather than on reproduction,
so it almost certainly counted zeros at a menu. The code change stays (reference equality
on ITexture is wrong on its own terms) but it fixes nothing user-visible.

What genuinely stands after everything:

  - the fault: the scene storage ends ~40% of frames near-uniform white, the damage is done
    at the END of frame N-1 across zero-draw passes, and frame N's composite (pass 2 of the
    frame) displays it
  - the only interventions that ever moved the rate: full draw serialisation (49->34%) -
    a timing race remains the best-supported category
  - every content/binding/arithmetic mechanism enumerable in this backend is excluded at
    power; the writer operates through a path none of the hooks see
  - next: instrument the tail window itself - log the MTLLoadAction actually written into
    the descriptor for the watched attachment on those final passes, and the game's own
    end-of-frame operations (what the guest submits between the last scene pass and vsync)

### DontCare load actions: audited and excluded

Every MTLRenderPassDescriptor construction site: the main path always writes Load/Clear +
Store for colour and Load + Store for depth/stencil; the four raw-descriptor sites in
Pipeline.cs are diagnostic passes, gated off in plain play, and explicitly Clear + Store
anyway; HelperShader builds no descriptors of its own. Unconfigured attachment slots keep
the DontCare default but carry no texture. No active pass attaches the scene storage with
an undefined load, so tile garbage through a default load action is excluded.

Also excluded today, the hard way: the duplicate-MRT storage dedup - no effect on the flash
in a clean in-session A/B (38/30 vs 32/32 over 50-shot arms), and with it active the game
aborts in Metal validation on camera movement (IOGPUMetalCommandBuffer validate ->
MTLReportFailure), the signature of an attachment nulled while the pipeline still declares
it. Defaulted back to reference-only; /tmp/ryujinx-metal-dedup-by-storage=1 re-enables it
for experiments.

# The localisation plan (2026-08-09, designed before further experiments)

Axioms (survived all retractions): A1 the composite fetches a near-white uniform from the
scene texture mid-frame while the same texture is varied at that frame's present; A2 the
flip happens between two specific zero-draw passes at the tail of frame N-1 (charted); A3
every enumerable command-stream write path reports zero in that window; A4 the only
rate-moving intervention is per-draw serialisation (49->34%), and Vulkan shows the fault at
175x lower rate.

Inference: A2 bounds the writer to a finite interval; A3 says no hooked command writes
there. Either an unhooked command does, or the writer is not in the command stream at all -
which is physically possible only if the texture lives in CPU-writable memory.

Phase 0 (code read) - RESOLVED: MTLTextureDescriptor never sets StorageMode (Texture.cs
ctor), so every texture defaults to Shared on Apple silicon; every buffer is explicitly
Shared (BufferManager.cs:68,126). The CPU branch is alive: any mis-offset staging/mirror
write can rewrite texture bytes with no GPU command, invisibly to every instrument used so
far, timing-sensitive, and impossible on Vulkan's device-local images - matching A3 and A4
in one stroke.

Phase 1 (one run): a global operation-sequence ring - every pass begin (target), blit
copy (src/dst), dispatch, SetData, buffer-to-texture upload, with frame and sequence
number. When the pass-content chart sees the varied->UNIFORM flip on the watched texture,
dump the ring slice between the two sample points. The writer is either in the slice
(named directly) or absent (CPU branch confirmed).

Phase 2 (one run, if CPU branch): hash the texture content via blit at each op boundary
inside the slice to pin the flip between two adjacent GPU ops; content changing with no op
between means a CPU write, then audit CPU writers by address range (BufferMirror,
StagingBuffer, PersistentFlushBuffer) - all Shared, all able to reach texture memory.

Termination: each phase halves the space; at most three instrumented runs to a named
writer. Detection stays on the deterministic uniform test (packed-word min==max), no
thresholds anywhere.

### Phase 1 first run: the trap fires, the interval recording is broken

Reproduction fine (18/2400 and climbing), WHITE-RUN blocks carry flipOps - but every one
reads "flip@0->1 ops:empty". Two instrument faults, both must be fixed before any reading:

  1. _pendingSampleSeq is never reset per frame, so chart entries not freshly sampled this
     frame carry a stale sequence, and a stale toSeq below a fresh fromSeq prints "empty".
  2. flip@0->1 contradicts the established tail localisation, which means some chart
     entries' pixel content is also stale - SampleBeforePass only fires on encoder
     transitions (non-render to render), NOT once per watched pass. Entries between
     transitions were never sampled this frame; the chart mixes this frame's pixels with
     the previous frame's. This has been true of every chart read so far, including the
     one the tail localisation came from - re-derive that after the fix.

Fix for next session: initialise the per-frame sample seq array to -1 at Commit, have
DescribeFlipOps skip entries without a fresh sample, and verify NotePass lands in the ring
(print OpRing.Seq per frame once). Then rerun. The plan itself is unchanged - the flip
interval still decides between a named command and the CPU branch.

### Phase 1 CONVERGED: the flip interval holds exactly three operations

With fresh boundary samples and the white-signature detector, a white frame reports:

    flip@22->23(0x781E03C0) ops: [217195]pass:0xA73352D00->0xA73352A80
                                  [217196]dispatch [217197]dispatch

Boundary samples fire only when a pass on a watched texture ends, so entry 23 is the
watched texture immediately after pass [217195] on it - varied before, the white signature
after, with only that pass and two compute dispatches in between. (NoteDispatch records
before the encoder switch, so the dispatches sit at the pass's teardown; order within the
three is approximate, membership is exact.)

The writer is one of three operations:
  1. the render pass on 0xA73352D00 (its draws, or its load action - read entry 23's dN
     from the same block's passContent line; d0 makes the load action prime suspect again,
     for THIS pass specifically)
  2/3. the two compute dispatches - compute image writes are the weakest-covered class in
     every hook built so far, and the game runs exposure/histogram compute right at this
     point in the frame

Next (one run each, or one run with both):
  - print the draw count and load action of the flipped-to pass in flipOps itself
  - log the compute pipeline labels of dispatches adjacent to a white flip, then skip
    those dispatches under a file toggle: rate collapses -> the dispatch is the writer;
    unchanged -> the pass is, and its load action gets the treatment

### The two dispatches are excluded; the zero-draw pass remains

Hot-swapped skip-by-label against the reproducing save, ~1500 samples per arm, both arms
rendering (luma 149-159):

    baseline   43.3% / 41.5%
    SKIP both  43.8% / 39.4%

The skip matches on the same DebugLabel string the interval printed, so it engaged by
construction. Both compute shaders are out - their raw-pointer buffer writes were the
obvious suspect and they are not it.

What remains of the three-operation interval is the render pass itself:

    pass rt0=<the watched scene texture> depth=<paired depth>, d0, clr=False

Zero draws, LoadAction Load, StoreAction Store. Byte-idempotent in theory - which means
the mechanism is in what load/store DOES on this hardware: a tile-memory round trip that
re-encodes the texture's lossless compression. If an earlier unsynchronised write left the
compression metadata inconsistent, this pass is where garbage crystallises into the stored
image - and a metadata-level fault decodes as a near-constant, which is exactly the
uniform white. That would also be invisible to every content hook (they read through the
sampler, post-decode).

Next, one experiment, two candidate implementations:
  - elide empty passes: never open a render encoder until the first draw actually arrives
    (a d0 Load/Store pass then simply never exists). If the flash collapses, this is also
    the fix, and a correct one - an empty load/store pass is pure cost.
  - or, cheaper probe first: log which call path opens these d0 passes (stack or caller
    flag on GetOrCreateRenderEncoder(forDraw=false)) - if they come from a state setter
    that forces the encoder open, lazy opening is a small patch.

### The elision experiment: causal - and the implementation stalls intermittently

In-session, single variable, reproduction verified hot on both sides:

    baseline   602/1560 = 38.6%   luma 163
    ELIDE        0/1500 =  0.0%   luma 158    (full frame rate, 1500 presents)
    baseline   694/1620 = 42.8%   luma 154    (reverts on switch-off - reversible)

Zero flat frames in 1500 with the picture alive, bracketed by hot baselines. The white
crystallises at the store of zero-draw Load/Store passes; suppress those stores and the
flash is gone. That is the causal answer the whole plan was built to produce.

Two honesty notes, recorded before any celebration this time:

  - the second ELIDE window stalled: presents stopped advancing (14 identical compositor
    screenshots, presentprobe counter frozen) and resumed when the toggle came off. The
    first window ran 1500 frames at full rate, so the stall is intermittent. Most likely
    some encoder-end path bypasses the store-action fixup, leaving Unknown at EndEncoding -
    a validation failure that wedges the command buffer and everything waiting on it.
    Find it before calling this a fix: audit every path that ends a render encoder, and
    have the fixup log if it ever runs with nothing to set.
  - the elision is still a mitigation of the crystallisation point, not an explanation of
    why tile memory holds white on load. The compression-metadata reading fits everything
    but is unproven. Upstreaming should present both.

Status: root cause mechanism CONFIRMED at the operation level (empty-pass store), fix
candidate works when it does not stall, stall diagnosis is the next task.

### The wedge is reversible on demand, and that rewrites the mechanism

On the live wedge: toggle off - presents resume instantly (0/960 to 2451/5940, whites
flooding at ~49%); toggle on - frozen again; off - flows again. Deterministic within this
game state, while the first window ran 1500 clean frames - so whether elision wedges
depends on which passes the frame mix contains.

Two conclusions, one practical, one structural:

  1. Blanket elision is NOT shippable: some zero-draw pass's store is load-bearing for
     guest progress (the guest spin-waits on content it produces, and resumes the moment
     stores return). DontCare also formally marks contents undefined, which is wrong for a
     pass whose semantics are "preserve".
  2. The structural one. For a d0 pass, store writes back exactly what load brought in.
     Elision stopping the flash therefore proves: on flash frames THE LOAD BRINGS IN WHITE
     while memory still holds the scene, and the store then overwrites good memory with
     it. The fault is in the load. A load that returns white instead of content is a
     texture whose contents are UNDEFINED - and a freshly created MTLTexture is exactly
     that. The texture cache recreates scene-sized textures constantly (the alias churn
     measured at ~90 creates/second in the OOM notes); a recreation whose
     content-preserving copy has not landed before the first Load/Store pass reads
     undefined tile data (white on this driver), crystallises it, and the composite
     consumes it one frame later.

That one mechanism covers every axiom at once: intermittent (churn-dependent), immune to
guest barriers (no ordering violation inside the command stream), invisible to every write
hook (nothing writes - the texture is simply new), the two-allocation census (the fresh
allocation IS the second identity), Vulkan 175x rarer (undefined memory there is usually
the old allocation's bytes - the old scene, which is invisible), and elision suppressing
the symptom by refusing to crystallise the undefined load.

Next and final localisation step: log texture-cache recreations of 1600x896 RG11B10Float
storages with frame numbers, and correlate against flash frames. If they line up, the fix
is in the shared layer: guarantee the content copy (or a clear) executes before any
Load-action pass on a freshly created texture - a real ordering bug, fixable narrowly.

### Recreation falsified by direct correlation

texcreate logging against the reproducing save: exactly two creations of the 1600x896
RG11B10Float class, both at frame 154 (level load), then thirty white runs from frame 991
onward with zero further creations. The textures are long-lived; the fresh-undefined-
content reading is dead.

What survives is sharper for it. Established by elision (causal, reversible): the d0
Load/Store pass's STORE writes the white. Established now: the texture is 800+ frames old.
Therefore the LOAD returns white from a texture whose memory holds the scene - load and
memory disagree on a long-lived texture. On this hardware that is one thing: the texture's
lossless-compression metadata disagreeing with its pixel data, so the load decodes a
constant while the bytes underneath are fine. The store then writes the decoded constant
back as real pixels, and the composite consumes it.

What desynchronises metadata from data on a Shared-storage texture is the next and
probably final question. The known writers of texture bytes outside render passes are the
SynchronizeMemory upload path (SM:u counts 2-10 every frame in the probe line) and any
CPU-side write into Shared memory. An upload that writes pixel bytes without going through
the driver's compression path - or a stale-dirty upload racing the frame, the mechanism
the retracted SynchronizeMemory hypothesis described at the wrong layer - would do exactly
this.

Next session, two measurements:
  1. at the flip, read the texture's raw MEMORY bytes (the trusted decode path from the
     gputrace work) and compare against what the load returned: memory=scene + load=white
     proves metadata desync at the driver level
  2. correlate SM:u upload events targeting this texture with flash frames - the counter
     is already per-frame in the probe line; per-texture attribution is one hook away

### Plain-play verdict: elision does not fix the real game

Narrow elision (scene-class only), plain launch, no probes, judged from compositor
screenshots: the game flows (23 of 30 frames distinct - the wedge was probe interaction,
both blanket and narrow) but the flash persists at 8 of 30, mean 255.

So the elision result was real but probe-scoped: under the probe environment the white
crystallises in the d0 pass fragments the probes themselves split off, and eliding their
stores suppresses it. In plain play the same white tile content enters through passes that
carry draws, whose stores cannot be elided. Store-side fixes are closed.

What every configuration agrees on: tile memory starts WHITE on a load that should have
brought in the scene. The fix has to address the load. Remaining suspects for a skipped or
wrong load on a long-lived uncompressed texture: the pass descriptor's texture handle
being a different view/slice of the storage than the one the content lives in (the
identity trap, fifth appearance), or a driver-level load elision triggered by something
this backend sets. Next instrument: log the attachment's raw MTLTexture pointer + level +
slice for scene-class passes on flash frames and compare against where the content
actually resides.

One honest note on confidence, asked directly by the user tonight: the localisation is
strong and every step is instrumented, but no shippable fix exists yet, and the last three
candidate mechanisms (metadata desync, fresh textures, store crystallisation) each died
under one more measurement. The next session starts at the attachment-identity check, not
at a fix.

### Refinement for the attachment-identity check: the chart may interleave two textures

The "varied -> white flip" was read as one texture changing. But boundary samples follow
whichever watched pass just ended, and the census shows the two 1600x896 textures taking
wildly asymmetric traffic (A: ~25 passes/29 draws; B: 1 pass/0 draws). Entry 21 (varied)
and entry 22 (white) may be A and B respectively - not a flip of one texture but an
interleaving of two, with B WHITE CONTINUOUSLY since its undefined birth at level load,
and the flash deciding by WHICH texture the composite samples that frame.

That reading also revives the recorded lesson that CanonicalPtr resolves view-of-view only
one hop: B's real writer (the per-frame scene copy that must exist, since good frames show
the scene through the same binding) can be censused under a middle view's identity,
invisible against B's root - the same trap, sixth appearance.

First measurement next session, one log line per scene-class pass on flash frames:
  frame, passIndex, rt0 raw handle, rt0 level/slice, draws - plus the composite's bound
  input handle for the same frame. It answers in one run:
  - flip entries 21/22 same handle -> one texture really flips; chase the load
  - different handles -> B is永白, the flash is a BINDING alternation (which host texture
    the composite's descriptor resolves to that frame), and the fix moves to the texture
    cache's view resolution - a shared-layer bug matching the Vulkan echo directly

### Handle chart run: one instrument works, one is broken, and the question stands

The handle-annotated chart works and says something real: on a WHITE frame the rendered
texture (0xBFCC6D680, taking all the draw traffic) opens the frame ALREADY carrying the
white signature - [@48]d0:STEP(0x781DFBC0) - before being cleared to black and redrawn.
The white is present at frame start on the rendered texture, which is consistent with the
damage-at-end-of-previous-frame localisation.

The input sampler is broken, by its own tell: slot128, slot136 and sib0 - three distinct
roots - report byte-identical spans on every line, WHITE and prev alike. Three separate
textures do not have identical min/max every frame; the sampler is reading one source
three times. Every conclusion drawn from per-slot input content (including tonight's
"the composite reads a texture nothing renders into") is void until it is fixed.

Also structural: the chart keys on colour target 0 only. The scene HDR texture can sit at
attachment 1..7 of the G-buffer MRT passes, so "never appears in the chart" does not mean
"never written". The chart must track every colour attachment before absence means
anything.

Next session, in order: fix SampleInputs to blit from each slot's own bound texture
(assert distinct sources by construction), extend the chart to all attachments, then
re-ask the binding-alternation question - it remains the best-shaped hypothesis and is
still unanswered.

### With the sampler fixed: all host copies are identical, white included

5x5 in-bounds grid, sib1 (RGBA8) varying independently proves the instrument now reads
each source. Result: slot128, slot136 and sib0 - three distinct roots - report identical
spans on every line, scene frames and white frames alike. That is no longer an artefact:
the texture cache keeps every host copy of the guest scene texture in sync, and on white
frames ALL of them are white together.

Binding alternation is dead - whichever copy the composite binds, it reads the same
content. What replaces it is the sync amplifier: the white needs to originate on only ONE
host copy (tile-garbage crystallisation in that copy's own passes - the mechanism elision
proved under probes), and the cache's alias synchronisation propagates it to every sibling
before the composite reads any of them. The [@48]d0:STEP(white) chart entry - the white
signature already present at frame start on the rendered texture, before its clear - is
consistent with a frame-start sync copying white IN from a sibling.

Standing facts, all instrument-verified: one guest texture, three synced host copies,
white everywhere at once on flash frames, white present at frame start before the clear,
crystallisation-at-store proven causally under probes, store elision impossible in plain
play because the white enters through draw-carrying passes there.

Next: find the alias-sync copy path in the shared texture cache (it is none of the hooked
backend copies - the sixth identity lesson applies) and log its direction and timing
against flash frames. If a frame-start sync copies sibling->rendered before the clear,
the white's circulation is closed and the break point is choosing NOT to sync FROM a
sibling whose content is stale tile garbage - a shared-layer fix, which is also what the
Vulkan echo has demanded all along.

### Two fix candidates, both measured dead at the save

  - clear-at-birth (zero every new colour target at creation, default on): 14/30 flat.
    Also falsifies the birth-content seed: with no undefined content anywhere, the white
    is GENERATED during play, not circulated from creation.
  - RYUJINX_METAL_DISABLE_SAMPLES_PASSED=1 (macOS 26 accumulate-visibility off): 14/30.
    The macOS-26-only visibility mode - the tightest Vulkan-differential suspect - is not
    the trigger either.

The clear-at-birth change stays (harmless, removes a real undefined-content class); the
counters stay on. What remains standing after both: the white is generated during play, in
tile memory, on textures whose contents are defined, entering through draw-carrying
passes, on all host copies at once. The unexecuted measurement is still the alias-sync
path log in the shared texture cache - direction and timing against flash frames.

### Zero alias copies: the sync amplifier dies, and the siblings were phantoms

TextureGroupHandle.Copy - the one path that syncs aliased host textures - executed ZERO
times on scene-sized textures across 30 white runs. Nothing propagates content between
host copies. Three "distinct" textures with permanently identical content and no sync
path means they are almost certainly ONE storage wearing three names: CanonicalPtr
resolves view-of-view a single hop, so second-hop views manufacture phantom siblings.
Seventh appearance of the identity lesson, inverted - not hiding a writer this time,
inventing witnesses.

The model collapses back to the simple one: ONE scene texture, written by ordinary draw
passes, read by the composite, and on flash frames its LOAD brings white into tile memory
while its bytes hold the scene. Every propagation, creation, binding, arithmetic, barrier,
residency, counter and store-side mechanism is now excluded at power. What has never been
tested, because no instrument here can see it, is the driver's own load path - and the one
input to that path nobody has varied is the DEPTH attachment loaded alongside the colour
(depth textures get PixelFormatView usage too, line 174, which is at minimum unusual for
depth on Metal).

Next session: strip PixelFormatView from depth-stencil texture usage (a one-line, correct-
on-its-own-terms change - reinterpreting depth formats is not supported anyway) and
measure. After that, the remaining space is a driver reproduction case for Apple.

# RESOLUTION (2026-08-09): mitigation default-on, root cause documented as driver-level

Final acceptance on the reproducing save, plain launch, guard default-on:

    standing:       0/40 flat, 40 distinct frames, mean 145..147  (baseline minutes
                    earlier on the same save: 14/30, twice)
    camera moving:  0/20 flat, 20 distinct frames, mean 115..179, no crash through the
                    rotation trigger that used to kill it in 1-2 minutes
    prior causal:   35% -> 0% hot-swapped in-session, both directions, picture alive

The flash no longer reaches the screen. What ships: FlashGuard default-on (samples the
present source, re-presents the last good frame on the flat signature; cost one GPU sync
per frame), plus two correctness fixes kept on their own merits (colour targets zeroed at
birth; PixelFormatView stripped from depth usage). RYUJINX_METAL_FLASHGUARD=0 opts out.

Root cause, as far as instruments this side of the driver can establish: a long-lived,
uncompressed colour target's Load intermittently brings near-white tile garbage in place
of its bytes, which the pass's store then crystallises into memory. Excluded at
measurement power along the way: bindings, shader arithmetic (saturation, NaN, coordinate
collapse), downstream gain, coverage and fixed-function state, guest barriers,
argument-buffer residency, MRT dedup, occlusion counters, store elision, birth content,
alias-sync propagation, and depth usage flags. The remaining space is Apple's driver; the
exclusion ledger above is the reproduction case for that report.

### Private storage: no effect on the raw flash

Answering why MoltenVK on the same driver is 175x cleaner, its sharpest usage difference -
device-local images - was replicated: standalone textures now allocate Private instead of
the Shared default. Guard off, reproducing save: 15/30 flat. The storage mode is not the
trigger either. The change stays (it is what MoltenVK does, texture data already moves
only through blits, and CPU-coherent texture memory bought nothing) with
RYUJINX_METAL_SHARED_TEXTURES=1 as the revert. The remaining Vulkan differentials are
pass-structure fragmentation (25-30 Load/Store round trips per texture per frame against
MoltenVK's handful) and the blanket PixelFormatView on colour targets - both structural,
neither cheap. The mitigation remains the shipped answer.

# The next root-cause avenue (user-directed): diff the two translations

The user's question stands: the flash exists on Vulkan too, rarely - so the trigger may be
in guest shader semantics that BOTH translations mishandle, with the MSL translation
mishandling it 175x harder. The MSL codegen has already yielded two real bugs this week
(signed-overflow UB, invalid float literals), which makes it the prior suspect.

Bounded plan:
  1. hdrcomposite already names the ~15 programs that draw into the scene texture. Dump
     both translations for each: MSL from /tmp/ryujinx-metal-shaders, SPIR-V by running
     the same save once under the Vulkan backend with shader dumping on.
  2. Diff not the text but the semantic classes where the APIs genuinely differ and the
     translators must compensate:
       - NaN/Inf propagation and fast-math contraction (MSL default fast-math vs SPIR-V)
       - denormal flush behaviour
       - discard vs demote (derivative correctness after discard)
       - blend state and output-mask emulation (the Metal side emulates write masks)
       - precision of the transcendental/reciprocal helpers (the bit-trick sites)
  3. Any divergence found gets the marker treatment that worked this week: draw the value,
     artefact flag in a spare channel, no thresholds.

This is the honest root-cause path. The mitigation stays only until it lands.

### The two translations are equivalent - shader codegen is cleared

Dumping MSL and GLSL of the same guest shader from one process (ShaderTranslationDiff,
RYUJINX_SHADER_DIFF=<dir>) gives 1771 pairs sharing decoder, IR and every optimisation
pass, so any difference is a backend difference and nothing else. Two hooks were needed:
ShaderCache.TranslateShader and ParallelDiskCacheLoader's own Translate calls - the loader
path is what actually feeds the game, and missing it produced zero dumps at first, the
fourth warm-cache no-op in these notes.

The composite shader (the pair carrying 0x7EF19FFF) matches operation for operation: same
bit-trick reciprocal, same Newton refinement, same three clamps, same x3.5. The MSL side is
if anything more correct, carrying the explicit integer wrapping added this week.

Across all pairs the apparent divergences - MSL having fewer clamps, fma and rsqrt - are
artefacts of GLSL expression folding, which duplicates a shared sub-expression textually
once per use: one example showed 20 GLSL clamps of which 16 were the same expression
repeated, leaving 5 unique against MSL's 5. Operation counts are NOT folding-invariant and
must be de-duplicated before comparison.

So the MSL translation drops no guard and no operation. Shader codegen is excluded.

What that leaves of the Vulkan differential: pass-structure fragmentation. This backend
splits the scene texture's rendering into 25-30 Load/Store round trips per frame where
MoltenVK maps Vulkan renderpasses roughly 1:1 and takes a handful. The fault crystallises
at Load/Store boundaries, so the dice are rolled an order of magnitude more often here.
That is the last standing structural difference and the only one not yet measured.

### Attachment feedback: real, constant, and not the flash

A draw sampling a texture that is simultaneously one of its own colour attachments is
undefined in Metal, returns garbage that reads near-white on this hardware, and involves no
write at all - which would explain why every write-side hook came back empty. The probe
(RYUJINX_METAL_FEEDBACK=1, comparing storage roots) finds it constantly: 65,000
occurrences in twenty seconds, across attachment indices 0-7 and every size class
including 1600x896.

Treating it as the implicit barrier the guest expects - end the pass before such a draw
when the pass already carries draws - measured 11/30 flat against a 14-15/30 baseline.
Inside the swing. Not the flash.

The count itself explains why: at ~108 per frame this is ordinary, legal behaviour being
flagged. The check compares storage roots, so a texture attached at one mip and sampled at
another - mip generation, the commonest pattern in any post chain - counts as feedback
while being perfectly defined. A version that discriminated level and slice would report a
far smaller number, and only that residue could be the fault.

The pass-split fix stays on (RYUJINX_METAL_FEEDBACK_FIX=0 opts out): where the levels do
overlap it is genuinely required, and it costs only passes that already had draws.

Also settled this round, on the user's report that the build felt slow: the mitigation
costs no frame rate at all - 29.9 fps with it, 30.0 without, measured from the 120-frame
sync-stat interval. What it costs is motion. At this location the flash rate is ~47%, so
nearly half of all frames are replaced by the previous good one, which is 16 fps of real
movement inside a 30 fps stream. That is the judder, and it is inherent to repeating a
frame rather than repairing it.

# CLOSING STATE (2026-08-09)

Not fixed at root. Everything reachable from inside this backend has been excluded at
measurement power, each with a paired or reversible test: bindings and identity, shader
arithmetic (saturation, NaN, coordinate collapse, output banding), shader translation
(1771 MSL/GLSL pairs, operation-for-operation equivalence on the composite), coverage and
fixed-function state, guest barriers, argument-buffer residency, storage mode, texture
usage flags, occlusion counters, store elision, birth content, alias synchronisation, and
attachment feedback. What remains is Apple's driver.

Shipping state: FlashGuard default on. It removes the flash from the screen and costs no
frame rate (29.9 against 30.0), but at a location flashing ~47% it replaces nearly half
the frames with the previous good one - 16 fps of real motion inside 30. Anyone who
prefers the flash to the judder sets RYUJINX_METAL_FLASHGUARD=0.

Kept as correctness on their own terms, none of which moved the rate: Private storage for
standalone textures, PixelFormatView stripped from depth usage, the pass split when a draw
samples its own attachment, command-buffer errors read at recycle instead of wedging
silently, and the encoder-generation fix that ended the drawPrimitives SIGSEGV.

If this is picked up again, the two things worth doing first, in order:
  1. narrow the feedback check to genuine level and slice overlap - the residue after
     removing legal mip access is the only untested subset of a mechanism that otherwise
     fits every constraint
  2. file the exclusion ledger above with Apple as a driver report; it is a complete
     reproduction case and no further emulator-side work is implied by it

Diagnostics all default off. tools/satmark_run.sh drives the reproducing save.

### Narrowing the feedback check: 65,000 -> 10,000, still not discriminating

Restricting the match to the same level and the same layer cut the count by 6.5x but left
it constant rather than rare. The reason is structural: base textures carry FirstLevel and
FirstLayer of zero, so the narrowing only separates views, and the check still compares
against every non-null entry of _currentState.RenderTargets - which can hold targets bound
for an earlier pass rather than the attachments of the pass actually open.

Making this discriminate would mean comparing against the live pass descriptor's
attachments only. That is the next refinement if anyone returns to it, and it is the third
iteration of the same instrument - each previous one buried the signal under legal traffic.

The narrowing stays regardless: it strictly reduces unnecessary pass splits.

### Third iteration: detection is finally accurate, the fix's placement is not

Comparing sampled textures against a snapshot of the attachments taken as the pass
descriptor is built - rather than against _currentState.RenderTargets, which retains
targets from earlier passes - gives the first trustworthy number: about 25 genuine
feedback events per frame, same storage, same level, same layer, genuinely attached.
That is a believable figure for an engine that reads targets it is writing, which is legal
on the guest API with the barrier this backend skips 89 times a frame as a no-op.

The fix could not be measured against it, because the fix crashes. Ending the pass from
inside GetOrCreateRenderEncoder - which is itself half way through acquiring an encoder -
faults the driver in drawIndexedPrimitives at 0x8a0, the same signature as the encoder-ABA
bug fixed at the start of this investigation. It is defaulted off; detection stays on
demand.

A correct version decides before RenderResourcesPrepass runs, from bound state alone, and
splits there. That is a contained piece of work and it is the one thing left with an
untested mechanism behind it - the earlier "no effect" measurement was taken with
detection over-broad by a factor of forty, so it does not stand as a negative for the
narrow case.

### Feedback split, relocated and finally measurable: no effect

Deciding from bound state before the prepass and before encoder acquisition removes the
crash, and the split then runs against accurate detection (~25 genuine same-storage,
same-level, same-layer occurrences per frame). Hot-swapped in one session on the
reproducing save, guard off, four arms:

    FIX OFF   8/26, 9/26
    FIX ON   10/26, 6/26

Inside the swing, both directions. Attachment feedback is excluded properly this time -
with a working fix, accurate detection, and no crash confounding it. The earlier
"no effect" stands after all, for a better reason than it was first given.

The split stays available (RYUJINX_METAL_FEEDBACK_FIX=1, or the hot file) and off by
default: it is correct where levels genuinely overlap, but it costs passes and buys
nothing measurable here.

With this, every mechanism this investigation could name inside the backend has been
tested to conclusion.

## 2026-08-09: cross-queue synchronisation, and why Vulkan structurally cannot do this

Chasing the Vulkan/Metal divergence from the outside rather than from the shader end
turned up something the ledger had never named: this backend runs two command queues.

    _queue           = _device.NewCommandQueue(MaxCommandBuffers + 1);
    BackgroundQueue  = _device.NewCommandQueue(MaxCommandBuffers);

Metal orders command buffers only within a queue - "all command buffers sent to a single
queue are guaranteed to execute in the order in which the command buffers were enqueued" -
and there is no MTLEvent, no MTLSharedEvent and no MTLFence anywhere in this backend. Every
"fence" here is Ryujinx's own CPU-side completion wait. So the two queues have no ordering
relationship of any kind. The backend also never calls `enqueue`, so a command buffer takes
its slot at `Commit()`, not at creation.

The only user of BackgroundQueue is `Texture.GetData`. Its render-thread path calls
`FlushAllCommands()` first and is properly ordered; its background-thread path does neither
- it blits on the other queue while the main queue may still be rendering into the very
texture it is copying.

Vulkan never reaches this shape on macOS, and not by luck. MoltenVK pins queue count per
family to one:

    static constexpr uint32_t kMVKQueueCountPerQueueFamily = 1;  // Must be 1.

so VulkanRenderer's `maxQueueCount >= 2` test fails, BackgroundQueue is never created, and
BackgroundResources falls back to `_gd.Queue` under `_gd.QueueLock`. One timeline. (The
Vulkan backend also disables the background queue on AMD by hand, which suggests the second
queue had already caused trouble somewhere.)

### It is a real race, and it is not the flash

`RYUJINX_METAL_LOG_READBACK=1` now reports which path each readback took. On the
reproducing save, in gameplay:

    readback BACKGROUND  1152    Texture2D R8G8Unorm 260x260   (every single one)
    readback render       452    small 3D LUTs and 1x1 surfaces

So the unsynchronised cross-queue path is live and busy - but it only ever carries one
260x260 two-channel texture, which is not the composite's input and cannot whiten a frame.
Cross-queue ordering is excluded as the cause of the white flash.

The race itself is real and worth removing on its own account, so BackgroundQueue now
aliases the main queue by default, restoring exactly the guarantee Vulkan gets here.
`RYUJINX_METAL_SPLIT_QUEUE=1` returns the old topology. Same save, same scene, same
pass and draw counts, cross-process so not a fine-grained A/B - shared 129-150ms in
357-369 waits per 120 frames, split 154-178ms in 369-376. No regression, no command
buffer errors, no stall.

### The sampler and LOD family, closed in one grep

Never previously examined: nothing in this document mentions LOD, mip or sampler state.
It is moot regardless. The composite reads its input with explicit texel fetches at a
literal level 0 -

    temp_38 = textures.tex_fp_t_tcb_8.read(uint2(temp_36, temp_37), 0).xyz;

twelve of them, one texture2d<float>, no `sample()` anywhere in the shader. There is no
sampler, no LOD selection and no mip filtering on this path, so none of it can be the
mechanism. Axiom A1 stands unweakened: level 0 of that texture really does read white at
the moment it is fetched.

### Searched and absent

No public report matches this fault: Ryujinx and Ryubing trackers, MoltenVK issues, Apple
Developer Forums, wgpu. The nearest neighbours (wgpu #6647, white at mip distance on M4;
the macOS 26 MPS and Metal 4 regressions) are different faults. There is no shortcut from
outside; the exclusion ledger here remains the primary document.

## 2026-08-09 evening: the resurrection hypothesis, tested at power and dead

The most economical story left standing had never been tested directly: the shared
layer re-uploads stale guest memory into a live scene texture mid-frame (a dirty-page
false positive resurrecting a captured white frame), the composite consumes it, the next
frame's render restores the scene. It explained the 254-not-255 white, the white autosave
thumbnails, the save-that-reproduces, the printf Heisenbug and the 175x backend ratio in
one breath. It is now refuted at measurement power.

`UploadCorrelator` (RYUJINX_METAL_UPLOAD_CORR=1) joins, per presented frame, "did a
SetData or CopyTo land on a texture >= 512x512 this frame, and did it land on a texture
that had already been a colour attachment this same frame" against a flat/normal label
sampled with FlashGuard's calibrated criterion (25-point grid, >=6 saturated at 235).
The label is taken with zero perturbation: encode-only sampling at present into an
8-slot ring, classified 8 presents later off the fence, nothing waited on, nothing
logged per frame. Internal validation: the load-screen fade - legitimately white -
produced 13 flat-with-upload coincidences during load-in and not one more in ten minutes
of gameplay; and the flat rate at the reproducing save sat at ~45% steady state while
standing still, matching the guard's known suppression rate there.

Verdict over 9,599 classified frames, 2,896 of them flat:

    big upload   | flat 13/2896 (all during load-in)   normal 54/6703   - no correlation
    upload->RT   | flat 0                              normal 3
    big copy     | flat 0                              normal 12
    copy->RT     | flat 0                              normal 4

    shapes: 1600x896, 1920x1080 and the large atlases were each uploaded exactly once,
    at load-in, and never on a flat frame.

Zero resurrection signatures in ~2,900 flat frames. Combined with the existing pass
census, draw attribution and the 0x55 sentinel, every host-side write channel - render,
upload, copy - is now excluded on the same frames at power. The texture's memory holds
the scene throughout; the white is manufactured on the read. The 2026-08-02 conflict
between the CPU-side present-time sample ("varied on flat frames") and the in-shader
fetch ("white at fetch time") dissolves: both were right, at different moments through
different paths.

What survives is the driver read-path account the FlashGuard comment already carries -
an uncompressed colour target's load/sample intermittently returning near-white in place
of its content - now standing on a complete exclusion rather than a default. The
Vulkan-rate difference reads as pass-boundary count (197/frame vs MoltenVK's handful;
aba revisits 14/frame), which is the workload shape that churns tile load/store the
hardest. The instrument stays in the tree; the correlator costs nothing measurable and
its table is the first thing to re-check on any future driver or OS change.

## 2026-08-09 night: the partial-render discriminator, and what the sunset added

The only published mechanism that matches this fault's family is Rosenzweig's AGX
partial render (the "Impossible Bug" write-up): when a pass's tiled vertex buffer
fills, the hardware stores all tiles mid-pass, reloads, and continues - through
auxiliary load/store programs distinct from the ordinary end-of-pass path, and getting
those programs wrong yields exactly "attachments come back garbage". On macOS that
path is the driver's. Only the scene pass here (~1524 draws; every other pass averages
13) could plausibly overflow. The workaround catalogues were also checked: Dawn's
toggle list has two dozen Metal entries (none matching), ANGLE and MoltenVK surface
nothing closer, wgpu #6647 is a different fault.

The discriminator: cap draws per render pass (hot file /tmp/ryujinx-metal-pass-split-draws,
env RYUJINX_METAL_PASS_SPLIT_DRAWS, PassEndReason.DrawBudget), so no single pass can
reach the overflow point and the implicit partial-render store/reload is replaced by
the explicit path. Verified engaged: DrawBudget=5 splits/frame, 197 -> 203 passes,
no frame-rate cost.

Single-session hot A/B at the reproducing save, correlator as the counter:

    split OFF   236.0/600 flat per interval (n=7)   39.3%
    split ON    235.8/600 flat per interval (n=5)   39.3%    difference 0.04 SE

Null. Capping at 200 draws does not move the rate at all. A partial render triggered
by under 200 draws of accumulated geometry remains conceivable but strained - MoltenVK
runs the same 1524-draw pass unsplit at 1/175th the rate.

The run ended with better evidence than it started for: the sun set mid-experiment
(minimap clock 8:35 PM) and the flat rate collapsed 244 -> 264 -> 221 -> 90 -> 0 within
five intervals, then held at zero - while draws per frame stayed at 2510-2520
throughout, split off. The day/night gate (11.2% vs 0.006%, previously recorded) is
therefore NOT workload-volume gating: the same pass structure, the same draw count,
the same geometry, and the fault vanishes when the lighting content changes. Whatever
manufactures the white on the read side is gated by what the frame contains, not by
how much work produces it.

Next discriminators, each needing a fresh daylight window (reloading the save resets
game time to 4:45 PM, giving roughly seven minutes of day):
  - extreme split (N=25) to finish the TVB question,
  - RG11B10Float -> RGBA16Float for the scene-texture class (packed-format read path),
  - bouncing the composite's input through a blit copy (the CPU-side evidence says the
    blit engine reads this texture correctly at the same moments the sampler does not).

## 2026-08-14: the white is manufactured on the read, shown positively

Four discriminators were run against the reproducing save, all with the correlator as
the counter and the guard off. One of them settled the question the whole investigation
had been circling.

### Card 3, blit bounce: null as a fix, decisive as evidence

Copy the scene texture out to a scratch surface and back through the blit engine
immediately before the pass that samples it (hot file /tmp/ryujinx-metal-bounce-scene,
mode 1). Mode 2 is the positive control: stamp a 0x55 constant over the scene texture
every frame instead, so the picture cannot survive if the write lands.

The control had to be built twice, and the first version is the cautionary tale. It
fired 3601 times a frame-for-frame and changed nothing on screen, because the hook ran
on non-draw calls to GetOrCreateRenderEncoder where bindings are stale, and the
once-a-frame latch was spent on a texture nothing went on to read; the bound texture
also arrived through TextureArrayRefs, which the first state walk did not read at all.
Fixed - forDraw only, both binding paths - the control is unmistakable: the whole scene
goes red, HUD untouched.

With that control passing, the measurement:

    no bounce            ~235/600 flat   39%
    mode 1, real bounce  ~248/600 flat   41%
    mode 2, constant red ~248/600 flat   41%

And sampling frames directly under the constant-red injection, guard off:

    red red WHITE red WHITE WHITE WHITE red red red red red red red      (4 of 14)

The composite's input was 0x55 on every one of those frames - the screen proves it -
and pure white frames continued at exactly the rate they always have. **The content of
the texture is irrelevant to whether the frame comes out white.** Combined with the
in-shader measurement that the fetch returns 254/254 on flat frames, this is the
positive form of what the ledger had only been able to infer: the fetch returns white
while the memory holds red. The fault is in the read, not in anything written.

### Card 2, RGBA16Float: structurally impossible, and moot

Substituting an unpacked format for the packed RG11B10Float scene class fails at
texture creation: the guest reinterprets that same storage as RGBA8Unorm, and Metal
only permits a view whose format matches the base's bit width.

    source texture pixelFormat (MTLPixelFormatRGBA16Float) not compatible with
    texture view pixelFormat (MTLPixelFormatRGBA8Unorm)

Every 32bpp float alternative is either not renderable (RGB9E5) or not float
(RGB10A2Unorm), so the substitution has nowhere to go without rewriting the aliasing
path. It is moot regardless: card 3 showed the decode of the content cannot matter when
the content itself does not.

### Card 4, lean usage flags: null

Every colour texture here declares PixelFormatView and ShaderWrite unconditionally, and
on Apple GPUs either flag alone disables lossless compression - which is how the scene
target came to be the long-lived *uncompressed* colour target the flash lives on.
MoltenVK sets ShaderWrite only for images that actually carry STORAGE_BIT.
RYUJINX_METAL_LEAN_USAGE=2 drops both for the scene class, putting the surface back on
the compressed path. Renders correctly, no command buffer errors, ~240/600 flat = 40%.
Null.

### Card 1, extreme pass split: real, partial, and not a fix

N=200 was null. N=25 engages hard - 77 DrawBudget splits a frame, 197 -> 276 passes -
and does move the rate. A/B/A inside one daylight window:

    split off    234.8/600   39.1%
    N=25         200.6/600   33.4%
    split off    238.8/600   39.8%

The return to baseline rules out the dusk drift, 5.15 SE. So capping draws per pass
buys about a sixth of the fault and no more. Whether that is the partial-render path
being partly avoided or simply the timing shift of 79 extra passes a frame is not
separable here, and 33% is not a fix either way.

### Where that leaves it

The read-side account is no longer the last one standing by elimination; it is the one
with a positive experiment behind it. A texture whose memory demonstrably contains a
constant is sampled as uniform near-white on ~40% of frames, gated by scene content
(day/night) and not by workload, immune to storage mode, usage flags, compression,
queue topology, pass structure, barriers, residency and every host write channel.

That is a driver texture-read fault, and it is now expressible as a self-contained
report: the constant-injection run is the reproduction, and the ledger above is the
list of everything it is not.

## 2026-08-14: FlashGuard is defaulted off again - it crashes during play

Reported from ordinary play: stutter, then an exit. Both are the guard.

The stutter is not a bug and not a frame-rate cost - the guard is GPU-side, decides in
the shader, and holds 30fps. It repeats the previous frame whenever it classifies one
flat, and at the reproducing save 39-41% of frames are flat, so four frames in ten there
are duplicates and motion judders accordingly. Typical daylight play is nearer 11%, night
effectively zero.

The exit is a real crash, and the acceptance that promoted this mitigation to default-on
missed it because that acceptance was taken standing still. Three arms, same build, same
save, the same scripted four minutes of walking and panning (tools/roam.py):

    guard on,  one queue    crash at 2:16   commit an already committed command buffer
    guard on,  two queues   crash at 2:11   silent exit, no assertion at all
    guard off               alive at 7:07   130 roam cycles, no errors

The queue merge is exonerated - both topologies die, and the older one dies silently,
which is exactly the failure this file's FlashGuard comment described and claimed fixed.
Eight unattended runs earlier the same day sat still for five to eight minutes each and
never saw it; movement is the variable, and movement means terrain streaming.

Underlying asymmetry, now instrumented (RYUJINX_METAL_LOG_SETDATA_THREAD=1):
Texture.GetData asks CommandBufferPool.OwnedByCurrentThread and routes background callers
to their own pool. Texture.SetData does no such check - it takes Pipeline.Cbs and opens a
blit encoder on it from whatever thread calls it. A thread-pool worker can therefore be
mid-encoder on the render thread's command buffer while the render thread commits it. The
guard does not create that race; its extra full-screen pass at present widens the window.

Defaulted off. RYUJINX_METAL_FLASHGUARD=1 opts in for anyone who prefers the judder to
the artefact. Fixing the SetData thread asymmetry is the prerequisite for reconsidering
the default, and would be worth doing on its own account.

## 2026-08-14 (cont.): the fork resolves - the binding is identical

Constant injection proved the fetch returns something that is not in the texture's
memory. That has two readings, and only one of them is a driver fault:

  1. the shader reads that texture and gets the wrong answer   -> driver
  2. the shader is pointed at something else entirely          -> ours, and fixable

Under Tier 2 argument buffers the binding is a raw resource id written into a buffer,
so reading (2) directly is one number. UploadCorrelator now records, per frame, the
resource id handed to the shader for the scene-class texture - together with the native
texture pointer, the storage root and the program - and splits it by the same delayed
flat/normal classification.

Over 7,183 classified frames on the reproducing save, 2,219 of them flat:

    flat  2219   normal  4964   gpu=0x29   tex=0x957C69900  root=0x957C68500  prog=480117a3b1123d65
    flat     0   normal     2   gpu=0x39A  tex=0x95A967700  root=0x95A94B980  prog=2c1a5bc1b14af178
    flat     1   normal     0   gpu=0x688  tex=0x95AC98C80  root=0x957C68500  prog=7e5f0a2af5001b51

One binding carries essentially every frame of both kinds, byte for byte identical:
same resource id, same native texture, same storage root, same program. The two
stragglers are one frame each. Reading (2) is dead.

So the shader is pointed at the right texture, that texture's memory demonstrably holds
the constant we put there, and the fetch returns uniform near-white on 31% of frames
anyway. Every step from binding to memory is now positively verified rather than
inferred.

That also tells us what the standalone reproducer is missing. It is not the workload
shape - eleven configurations of it never fail - and it is not the binding, which is
correct in the emulator too. What the reproducer does not have is the emulator's scale:
thousands of live textures and tens of gigabytes of footprint, concurrent threads
issuing blits and uploads against the same queue, and compute passes interleaved with
the render ones. Those are the next knobs, and they are cheap to add now that the
harness runs at 110 frames a second.

## 2026-08-14 (cont.): subtraction, an instrument repair, and a sharp new correlate

### Subtraction has a per-arm cost nobody had priced

`/tmp/ryujinx-metal-skip-draws` takes "A-B" and drops the draws in that window of
passes while still opening the passes, so attachments and load/store actions are
untouched. Two things came out of the first attempt.

Dropping the whole scene render (3-167) puts the rate at 100% flat with luma 253 and
the normal count frozen. That is the blank-picture failure the ledger warns about, so
it is not a measurement - but it is a useful positive control: **a scene target that
was not written this frame reads as exactly the flash's appearance**, uniform 253.

More important, the damage persists. Clearing the skip did not return the rate to 40%;
it settled at exactly 50%, 600 flat and 600 normal per interval - the signature of two
rotating surfaces with one of them permanently unwritten. So skip arms cannot be
hot-swapped: each needs its own launch, and every reading taken after the first arm in
that session is void. Subtraction costs a full ten-minute launch per arm.

### The binding probe was reading the wrong draw, and the conclusion survived anyway

`NoteSceneBinding` overwrote its record on every call, so it kept the *last* scene-class
binding of the frame while the consumer that comes back white is the second pass. It now
keeps the first and the last separately. In gameplay they are different storages, so the
gap was real:

    FIRST  gpu=0x46C  tex=0x708B0C500  root=0x708A75E00  prog=5a6eec8e1d385f76
    LAST   gpu=0x2A   tex=0x71894A800  root=0x718948500  prog=480117a3b1123d65

Both are still identical across outcomes - FIRST n=13 carries 1882 flat and 3123 normal
frames on one key, LAST carries 1894 flat and 4701 normal on one key. The instrument
repair strengthened the result rather than overturning it: whichever end of the frame is
asked, the shader is pointed at the same texture on flat frames as on normal ones.

### The count of scene-class draws steps the rate

Splitting by how many draws sampled a scene-class texture that frame was accidental -
the count went into the key - and it is the sharpest correlate found so far:

    n = 10     0 flat /   57
    n = 11     0 flat /  746
    n = 12     9 flat /   62      14.5%
    n = 13  1882 flat / 5005      37.6%

Eight hundred frames at eleven or fewer, not one of them flat; a step to 37.6% at
thirteen. This is plausibly the day/night gate seen through a sharper instrument rather
than a new mechanism - n tracks how much of the scene chain is running - but it is the
first *quantitative* handle on that gate, and unlike "daylight" it can be read per frame.

Worth doing next, and cheap: log which programs make up the n draws, and diff the n=11
set against the n=13 set. That names the two stages whose presence coincides with the
fault appearing, which is the same answer subtraction would give at a tenth the cost.

### The n step is a correlate, not a cause

Recording which programs make up the scene-class-sampling draws gave a diff that looked
decisive. Aligning the natural signatures:

    n=10                                        0 /   61    0.0%
    n=11  (+57642c)                             0 /   21    0.0%
    n=11  (+8a7b6c)                             0 /  767    0.0%
    n=12  (+dd3b94 +57642c)                     8 /   55   14.5%
    n=13  (+dd3b94 +57642c +7e5f0a)          1943 / 4961   39.2%
    n=14  (7e5f0a twice)                        2 /    2  100.0%

Two names fall out, and the historical composite ee89b4 is present in every set
including the clean ones - innocent again, for the third time.

Both names are wrong. `/tmp/ryujinx-metal-skip-program` skips draws by program label
prefix, verified engaged by the skipped-draw counter and by the signature table
changing shape, with the picture alive throughout (normal-frame luma 102 -> 117 -> 131):

    baseline                       ~40%
    skip dd3b94                     43.7%
    skip 7e5f0a                     41-46%

Neither moves the rate. The count tracks how much of the scene chain is running, which
tracks the same lighting state the day/night gate tracks; it is that gate seen through a
finer instrument, not a mechanism.

### Where addition and subtraction both leave it

Both directions have now been pushed to conclusion and both are null:

  - addition, outside the emulator: nineteen configurations of the standalone
    reproducer, ~15,000 frames, never once white
  - subtraction, inside it: skipping the draws whose presence coincides exactly with
    the fault appearing changes nothing

Taken together those say something specific. The fault is insensitive to *which* draws
run - remove the ones that correlate perfectly with it and it persists at full rate -
and it cannot be synthesised from any property of the workload we can name. What it does
track is the overall scene state: daylight versus night, 39% versus effectively zero,
with the same geometry, the same pass structure and the same draw counts on both sides.

The instruments are all in the tree and all hot-swappable, so any future hypothesis is
one file write away from a measurement. What is missing is a hypothesis, and neither
adding properties nor removing draws has produced one.

## 2026-08-14 (cont.): the capture tooling, fixed - and what fixing it revealed

The earlier Xcode round is recorded above as a tool misunderstanding rather than a tool
limit, and that was right, but the harness had two defects of its own.

**The scope never closed.** Present runs OnPresentBegin (which stopped the capture)
*before* EndScope, so every scoped capture was finalised with its scope still open. That
is why they came out covering a frame's tail - the UI and the minimap, scene already
drawn - and why whole-frame queue captures were reached for instead, at hundreds of
megabytes with the tools crashing while finalising.

**The frame was random.** Flat frames are about 40% at the reproducing save, so most
traces were of healthy frames, and nothing recorded which kind had been caught.

`CaptureHunter` (RYUJINX_METAL_CAPTURE_FLAT=1, with METAL_CAPTURE_ENABLED=1) fixes both.
It opens the scope immediately before the pass that samples the scene texture - the same
predicate the 0x55 control proved lands on the failing consumer - and closes it when that
pass ends, both well before present. Then it classifies the frame with FlashGuard's
criterion, keeps the trace only if the frame was flat, and retries otherwise.

It works. First keeper:

    capture hunter: KEPT a flat frame after 2760 attempts
    (saturated 25/25, mean luma 241): /tmp/ryujinx-flat-2759.gputrace

**2.6 MB**, well-formed (`captured_frames_count = 1`, boundaryLess false), against the
multi-gigabyte bundles that could not be finalised. A copy is in artifacts/captures.

### The attempt count is itself a result

At 40% flat, a keeper should arrive in two or three attempts. It took 2,760 - the fault
is suppressed roughly a thousandfold while a capture is running. That is the same
Heisenbug this file has recorded twice before (a per-frame present log takes it from
3/min to 0), now measured against a different perturbation, and it is the strongest
timing evidence yet: whatever produces the white needs a window that an active GPU
capture closes almost completely.

It also means the trace has to be read with that caveat in mind. It is a genuine flat
frame - 25 of 25 samples saturated at mean luma 241 - but it is one that happened under
heavy perturbation, so anything read from it about *timing* is suspect while anything
read about *state* (bindings, attachments, resource contents at the failing draw) is not.

Reading it is the next step, and the note from the earlier round still applies: the
Dependencies filter matches encoder names, not textures. Ask "who wrote this texture" in
the Memory view - select the resource, expand its own usage list - or use Reveal in
Dependencies from a top-level row.

### Caught: a GPU trace of the flash itself

Aiming took five tries, and the four that failed all keyed on properties that move:

  - the sampled texture's size (dynamic resolution had the scene at 800x448, under
    the 1000-wide floor, so the hook fired on the G-buffer pass instead)
  - the render target's width (the drawable is 2560x1406 and matched first)
  - widening the window to eight drawing passes (thirteen encoders, none white)

The fifth aims by identity instead: the correlator already knows which texture reached
the screen, so CaptureHunter records its storage root at present and opens the window on
the next frame's pass whose colour attachment 0 *is* that storage. Nothing about that
moves - not dynamic resolution, not the shader hashes that changed under CodeGenVersion
bumps.

It landed. The trace's own preview is the fault: a pure white 2560x1406 frame with the
HUD intact over it - hearts, ability icon, minimap - which is the artefact exactly as
this document has described it since July. Five command buffers, four render encoders,
six draw calls: small enough to read end to end.

A copy is in artifacts/captures/flat-frame-caught.gputrace.

What it still has to be asked, in Xcode: find the encoder whose attachment is white,
select its draw, and open the texture it samples. Scene contents there means the memory
was correct and the fetch returned white - the driver read fault, positively shown for
the third time and now with a trace to attach to a report. White contents there means
the texture was already white and something upstream of the read is still unaccounted
for. Note that textures arrive through argument buffers here, so they appear under
Indirect as resident resources rather than as individual texture bindings.

### What the captures said, and the old lead they reopened

Three windows were captured, each a verified flat frame, each read in Xcode.

**The present filter is faithful.** The pass that writes the drawable is the emulator's
own FSR sharpener, labelled "Present Color RCAS Sharp" in the trace (the scaling filter
was enabled in this session - a variable nothing in this investigation had ever
controlled for). Its input and its output were opened side by side: input
`Texture 0x75c191400`, 1920x1080 RGBA8Unorm, **already uniform white with the HUD over
it**; output identical. RCAS reproduced a white input faithfully. It is not the cause,
and the scaling filter is not the cause.

**The scene is intact in the same frame.** The G-buffer pass in that same flat frame
carries eight colour attachments plus depth, all showing the scene correctly - trees,
character, ground. Confirms the earlier capture's finding, now on a frame known to be
flat rather than assumed.

**Nothing in the frame writes the white surface.** Two different windows - one aimed at
the presented surface's storage, one opened at the frame's first draw and kept for
twelve drawing passes, fifteen render encoders - contain no encoder whose attachment is
white. Filtering the Memory view by the presented texture's parent returns nothing,
because encoders bind the *view*, not the parent; filtering by the view returns only the
present read.

That third point lands on something this document recorded on 2026-08-02 and then let
go of:

> The Metal backend never writes the presented texture in the three frames before
> present - no MRT attachment, no SetData, no CopyTo, identical on good and bad frames.

Taken together with today's captures, the reading is that **the white surface is not
produced during the frame at all; it is selected for presentation already white**. The
reason four aiming strategies could not find the encoder that writes it may simply be
that no such encoder exists.

That was closed at the time by the cross-backend comparison: the present-choice table is
byte-identical between Metal and Vulkan (same guest addresses, same alternation). But
that test compared *which guest address* was chosen, not *what the host texture behind
that address contained*. Those are different questions and only the first was answered.

Next, and it needs no capture and no GUI: for the texture actually handed to Window.Present
on a flat frame, record when its host storage was last written and by what. The correlator
already has the identity and the per-frame classification; this is one more field in the
same table.

## 2026-08-15: the presented surface, asked three ways, identical every time

The captures kept failing to find a writer, so the same questions were asked of the
correlator instead - as statistics over thousands of frames rather than one trace.

**Identity.** The storage handed to the window, split by outcome:

    flat  712  normal 1988   root=0xBF9C75400
    flat  750  normal 1949   root=0xBF9C76580

Two surfaces alternating, each carrying flat and normal frames in the same proportion.
"A different, already-white texture was selected for presentation" is dead.

**Timing.** Frames since that storage was last a colour attachment:

    flat    1  normal   26   age=0
    flat 1461  normal 3911   age=1

The surface shown at frame N was last drawn into at frame N-1. **The white is written a
frame before it appears** - which is why four aiming strategies and three GPU captures
could not find its writer: every one of them was pointed at the frame that displays the
fault, not the frame that causes it. This is the same one-frame error this document
already recorded for the in-frame probes, repeated with a capture tool.

Fixing that (capture frame N, keep it only if frame N+1 is flat) produced a trace of the
writing frame. Filtering the debug navigator by the presented storage - the only method
that works, since Reveal in Dependencies is disabled for textures and Filter only offers
itself for resources bound in that capture - showed exactly one user of it: a render
encoder containing a "Clear Color Float" debug group and a single TriangleStrip draw
whose fragment stage binds no textures at all. The draw-based clear was already measured
as (0,0,0,0) black in this document, and the preview agrees.

**Authorship.** Which programs drew into the presented storage during the frame that
wrote it, split by outcome, over 5,399 classified frames:

    flat 1546 / 5399 = 28.6%   480117

One signature. A single program, `480117a3b1123d65`, writes that surface on flat and
normal frames alike - and it is the same program the signature census already identified
as the frame's last scene-class sampler.

### Where that leaves the emulator side

Every observable upstream of the output is now measured identical between the two
outcomes, and each was verified positively rather than by elimination:

    presented storage identity   identical   (both surfaces carry both outcomes)
    writer program               identical   (one signature, 5399 frames)
    write timing                 identical   (age 1 on both)
    argument-buffer binding      identical   (four fields, byte for byte)
    sampled texture's memory     holds the constant we injected
    what the shader reads        uniform near-white

Same inputs, same code, same timing, same bindings - and 28.6% of the time the output is
white. There is no remaining observable on this side of the driver that differs.

## 2026-08-15: the backend difference, and the largest effect measured on this fault

"Why would it be the GPU when Vulkan barely flashes on the same machine?" - the right
question, and the answer is that every comparison in this document until now was made
*within* the Metal backend, flat frames against normal ones. A difference that is
constant across both outcomes is invisible to that method by construction, and the 175x
gap is between backends, not between outcomes. Wrong dimension.

Current numbers rather than the July ones. Same machine, same save, same spot, standing
still, compositor sampling:

    Vulkan   0 white out of 120 samples
    Metal    about 40%

Two candidate differences were checked before any experiment:

  - **Argument buffers: not it.** This backend routes every texture through Tier 2
    argument buffers and has no setFragmentTexture call anywhere. But
    `MVKInitialization.Initialize()` sets `config.UseMetalArgumentBuffers = true`
    unconditionally, through vkSetMoltenVKConfigurationMVK - which also overrides the
    MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS environment variable. Both backends use Metal
    argument buffers. (Reading that saved running an experiment whose knob was already
    overridden - the second time today a toggle needed verifying before use.)

  - **Barriers: it.** Ryujinx's Vulkan backend issues real pipeline barriers. Metal has
    no way to express a read-after-write barrier inside a render encoder, so MoltenVK
    resolves one by ending the encoder: **the Vulkan path splits the pass at every
    read-after-write, automatically.** This backend issues no barrier of any kind - no
    MTLFence, no MTLEvent - and leans entirely on Metal's automatic hazard tracking,
    which for argument-buffer reads is fed by useResource rather than by anything the
    driver observes directly.

The ledger's earlier "strict barrier" test keyed on guest TextureBarrier calls, which
this game issues rarely. This keys on the hazard actually occurring: a draw that samples
storage written as a colour attachment earlier in the same command buffer ends the pass
first (RYUJINX_METAL_RAW_SPLIT=1, hot file /tmp/ryujinx-metal-raw-split).

A/B/A in one daylight window, in gameplay, picture verified alive throughout (flat luma
252, normal 99-106), flat frames per 600:

    split off   253 292 291 269 249     mean 271   45.2%
    split on    151 142 147 143         mean 146   24.3%
    split off   249 249                 mean 249   41.5%

Reversible, 16 SE, and about 97 extra passes a frame. **The largest effect anything has
had on this fault** - the previous best was 6 points from capping draws per pass; this is
21, roughly half the occurrences.

Not a complete fix: 24% against Vulkan's zero. The tracking here covers colour
attachments only. Writes through blit (SetData, CopyTo), depth attachments and compute
image stores are not yet in the set, and each is a read-after-write the Vulkan path would
also have barriered. Extending it is the obvious next step and is cheap.

This reframes the whole investigation. The fault is a read-after-write hazard that
Metal's automatic tracking does not fully cover in this backend's encoder structure. The
driver is not misbehaving for no reason; we are asking it for something Vulkan never asks
for, and it is only the two together that produce white.

### The split has a floor at about 25%, and it is not a hazard

Three variants were measured, each A/B/A in its own daylight window with the picture
verified alive:

    colour attachments, command-buffer scope   45.2% -> 24.3% -> 41.5%   +97 passes/frame
    + depth, storage images, blit writes       41.2% -> 25.0% -> 42.4%   no change
    + frame scope instead of command buffer    39.9% -> 25.3% -> 40.0%   2509 passes/frame

The second added every other channel the Vulkan path would barrier - depth attachments,
shader image stores, SetData and CopyTo - and bought nothing. The third widened the set
to the whole frame, which with ~245 auto-flushes a frame means essentially nothing is
hidden from the check any more; it made every draw its own pass, took sync waits to 10.8
seconds per 120 frames, and the rate did not move.

So serialisation buys about 15 points and then stops dead. The fault has two components:
one that responds to ordering and one that is completely indifferent to it. That matches
the ledger's oldest quantitative note - per-draw full serialisation moved 49% to 34% -
which nobody could interpret at the time.

The cheap variant is kept (colour attachments, command-buffer scope,
RYUJINX_METAL_RAW_SPLIT=1) because it removes nearly half the occurrences for ~50% more
passes. The expensive ones are reverted: they cost enormously and buy nothing.

What is left is the floor: a quarter of frames come out white with the frame almost fully
serialised, every write channel barriered, and every upstream observable measured
identical. Vulkan is at zero under the same conditions, so the floor is still a backend
difference - just not an ordering one.

### Two more cross-backend candidates, both excluded by reading

The method that found the barrier difference was applied twice more before stopping.

**Swizzle to one.** `AddressForTexture` carries a comment that is almost a confession:
RG11B10Float has no alpha, so a swizzle routing a component to alpha or to one samples
as exactly 1.0 - white, and *independent of the texture's contents*, which would explain
the single most puzzling fact in this document (0x55 red injected, white still read).
Already measured, though: sampling through the identity view instead of the swizzled one
gives 37.67% against 36.17%. The identity view has no swizzle at all and still flashes.
Dead.

**Capability divergence.** This file's own note - "Vulkan not flashing: the shared layer
branches on backend capabilities" - points at a different axis entirely: the two backends
may be handed different work, not merely translate the same work differently. Checked the
flag that matters most here, `supportsMismatchingViewFormat`, since the guest genuinely
aliases the scene storage as RGBA8Unorm: **both backends report true**. So do
`needsFragmentOutputSpecialization` and `reduceShaderPrecision`, which the Vulkan backend
sets from `IsMoltenVk` and are therefore also true on this machine. Not a divergence.

A systematic diff of the whole capability struct has not been done and is the obvious
place to resume: it is a read, not an experiment, and this axis has never been walked.

### Cost, coverage, and the default

Measured properly rather than assumed, all A/B/A inside single daylight windows:

    variant                              flat rate        passes/frame   sync wait/120f
    off                                  37-45%           197-200        110-190ms
    colour attachments, cb scope         24-28%           ~620           ~570ms
    + depth, images, blit writes         25.0%            ~640           ~1050ms
    frame scope                          25.3%            2512           10800ms

The extra write channels and the wider scope buy nothing and cost a great deal, so both
are removed - colour attachments at command-buffer scope is the whole of the benefit.

Frame rate at the reproducing save, hot-swapped:

    split on    30.01 FPS (33.32ms)  FIFO 28.5%
    split off   29.99 FPS (33.34ms)  FIFO 29.8%

The extra sync wait is about 4.7ms a frame against 1.2ms, and both fit inside the 33ms
budget at this location. No measurable frame cost, so it is now on by default
(RYUJINX_METAL_RAW_SPLIT=0 opts out). The caveat is honest: the cost was measured at one
location with headroom, and a heavier scene has not been tried.

One process note, because it cost a measurement: the expensive frame-scoped variant was
reverted in source but the artifact was never re-published, so a later "cheap" run was
actually the expensive binary - 2512 passes a frame - and the build handed over for
play was the same one. Publish after reverting, or measure the wrong thing.

## 2026-08-15 (cont.): the floor is the same fault, and two more exclusions

**The floor is content-independent too.** The 0x55 injection had only ever been run with
the split off, so it characterised the two components together. Repeated with the split
on (default), the scene texture demonstrably holding the constant - the screen is red -
and 60 frames sampled:

    red 44   white 16   other 0      26.7% white

So the remaining quarter behaves exactly like the half the split removed: the memory
holds a constant and the read comes back white. Both components are the same phenomenon.

That reframes what the split actually did. If it is one phenomenon and ordering removes
exactly half of it, the split is most likely **narrowing a race window rather than
removing a cause** - which fits everything else on record: the printf Heisenbug, the
fault being suppressed while a GPU capture runs, and serialisation helping only partly.

**Residency caching: excluded.** `IsResident` caches useResource declarations per encoder
and skips the call when a resource is already declared, so a stale cache would leave a
texture non-resident and its reads undefined - content-independent and not an ordering
problem, which is exactly the floor's shape. The ledger's earlier residency test only
varied *which handle* was declared. Dropping the state cache below LevelResidency makes
every declaration unconditional; A/B/A on the running session:

    cache = 3 (residency cached)      157 flat / 600
    cache = 2 (always declared)       157 flat / 600
    cache = 3                         165 flat / 600

No effect.

### Named next step: an actual fence

If the floor is a race window rather than a cause, closing it needs real GPU-side
ordering, not encoder boundaries. Splitting a pass only stops two pieces of work being
encoded together; `MTLFence` with updateFence/waitForFence makes the GPU wait. This
backend contains **no MTLFence and no MTLEvent anywhere** - checked at the start of this
session - while MoltenVK has them available for translating VkImageMemoryBarrier, and
Vulkan sits at zero.

That is the one mechanism the Vulkan path can reach and this one never has. It is also
implementable: a fence per command buffer, updated after a pass that writes a tracked
texture, waited on before a pass that samples it - the same pairs the read-after-write
split already identifies, which means the detection is done and only the primitive
changes.

### MTLFence on top of the split: nothing

The named next step was to replace the encoder boundary with a real GPU-side wait, since
ending an encoder only stops work being encoded together. Same detection as the
read-after-write split, stronger primitive: updateFence on the encoder that wrote, before
it ends, and waitForFence on the encoder that reads - the pairing MoltenVK produces for a
VkImageMemoryBarrier. RYUJINX_METAL_RAW_FENCE=1, and the counters confirm it engaged:
1,159,640 fence waits against 2,550,493 splits, sync wait 326ms per 120 frames.

    split only          172 150 165 155     mean 160.5 / 600
    split + fence       145 166 162 134     mean 151.8 / 600
    split only          141 174 160         mean 158.3 / 600

Null. The strongest ordering primitive Metal offers, applied to exactly the pairs the
split already identifies, changes nothing.

So the floor is **not a synchronisation problem at all**. Ordering has now been attacked
three ways - encoder splits at every read-after-write, near-total serialisation, and real
fences - and the first bought 15 points while the other two bought zero.

### The box, as it now stands

    not ordering      splits partial, full serialisation nothing, fences nothing
    not content       0x55 injected, screen red, 26.7% still white
    not the binding   resource id, texture, root and program identical over 7,183 frames
    not residency     declarations forced unconditional, no change
    not the writer    one program writes the presented storage on both outcomes
    not present       the FSR pass faithfully reproduces an already-white input
    and Vulkan is at zero on the same machine, same driver, same save

Everything reachable from the Metal side is measured identical between the outcomes, and
every mechanism that could order or supply the data has been tried. What has never been
compared is the one thing the two backends genuinely do not share: **the shader binary**.
Both translate the same guest program, one to MSL and one to SPIR-V. The 1,771 pairs
compared earlier in this document were MSL against GLSL, by operation count, not against
what Vulkan actually runs. That is the remaining untouched axis.

### Out-of-bounds texel fetch: the best-fitting hypothesis, and it is wrong

Metal leaves `texture.read()` with out-of-range coordinates undefined; Vulkan does not -
`OpImageFetch` out of bounds is well defined. That is a genuine difference the two
backends do not share, it needs no ordering, it ignores what the texture contains, and
dynamic resolution supplies out-of-range coordinates for free: the GPU capture caught the
scene at 800x448 while the shader was written for 1600x896. It even explains the day/night
gate mechanically for the first time - dynamic resolution scales down when the GPU is
loaded, which is a bright complex daylight scene and not a night one.

The ledger's "clamping the shader's texture fetches, 33.99%" row does not exclude it:
that whole table is about the tonemap, the shader the probes of that era were hardcoded
to and which was later shown to be the wrong target. The composite had never been clamped.

RYUJINX_METAL_CLAMP_FETCH=1 rewrites every `tex.read(uint2(a, b), 0)` to clamp against
get_width()/get_height(), with CodeGenVersion bumped to 7375 so nothing is served from
the warm cache. Verified engaged on the right shader: `ee89b4e471373459 clamped 12 texel
fetches`, the composite, with exactly the twelve fetches this document identified, and
zero shader link failures.

    24.7% flat, against a floor of 25-26% without it

Null. Out-of-bounds fetch is excluded, which also fits the earlier coordinate measurement
(spans 131/119 against 130/124 - the coordinates were never collapsing or running away).

One instrument note, because the first attempt produced a result that looked like the
fault: the rewrite captured the texture name with `\w+`, which drops the argument-buffer
qualifier and yields `tex_fp_t_tcb_8.get_width()`. Every patched shader failed to link,
the pipeline came back null, the draws were skipped, and the scene rendered **black with
the HUD intact** - the same shape as the artefact. `Fragment shader linking failed` in
the log is what separated them. Any shader patch must be checked for link failures before
its measurement is believed.

## 2026-08-15: Metal's own validation layer - the cleverer comparison, never run until now

User feedback first, and it corrects an overstatement: with the read-after-write split on
the game still "flashes constantly". That is right. 45% to 24% at 30fps is thirteen white
frames a second becoming seven. Statistically half; perceptually still a strobe. The fix
is real and the framing was not - only something near zero crosses that threshold.

Asked for a cleverer way to compare the two backends, the answer was sitting unused:
**Metal ships a validation layer whose entire job is to report undefined behaviour.** If
this backend asks Metal for something MoltenVK never asks for, that is the tool designed
to name it.

    METAL_DEVICE_WRAPPER_TYPE=1  MTL_SHADER_VALIDATION=1  MTL_DEBUG_LAYER=1

It found two real API violations, each within a minute, neither of which any probe in
this investigation could have seen.

**1. Purgeable state set on in-flight buffers.**

    -[MTLDebugBuffer setPurgeableState:]:568: failed assertion
    `Cannot set purgeability state to volatile while resource is in use by a command buffer.`

`DisposableBuffer.Dispose` marked every buffer `MTLPurgeableState.Empty` before releasing
it - telling the system its contents may be discarded immediately - while a command buffer
was still using it. These buffers include the argument buffers carrying texture resource
ids. It was looked at earlier in this investigation and dismissed because `Auto<T>` defers
disposal; validation says the deferral is not enough. Removed
(RYUJINX_METAL_PURGE_ON_DISPOSE=1 restores it). **Flat rate unchanged at 25%.**

**2. Scissor rects of 65535x65535 against a 1x1 render pass.**

    (rect.x(0) + rect.width(65535))(65535) must be <= render pass width(1)

The clamp for this already existed and was silently inert: `GetRenderPassSize()` walks
`_currentState.RenderTargets` and falls back to `ulong.MaxValue`, so `Math.Min(65535,
MaxValue)` passes the sentinel straight through - and `_currentState` holds nothing for
passes the helper shaders build. Now recorded where the descriptor is actually built.
**Flat rate unchanged at 27.5%**, and validation still reports it, so at least one path
reaching `SetScissorRects` is still not covered.

Both are genuine correctness bugs and neither is the flash. The method, though, is the
one worth keeping: it produced two findings in the time every other approach tonight took
an hour to produce one, and it is the only technique so far that reports what this backend
does *wrong* rather than what it does *differently*. The obvious continuation is to keep
fixing what it reports until it is silent - the remaining scissor path first.

### Online research against the sharpened characterisation

The earlier search round was run against a vague description and found nothing. Repeated
with what is now known - argument buffers, automatic hazard tracking, a content-
independent white, memory-pressure gating - two things came back that are worth recording.

**Apple's own position on hazard tracking with argument buffers** (WWDC "Go bindless with
Metal 3", "Explore bindless rendering in Metal"): with tracked resources Metal inserts
synchronisation itself, and `useResource` is what tells it a resource is in play. The
documented hole is heaps - resources suballocated from a heap are *not* protected unless
the heap opts into hazard tracking, and tracking is at heap granularity. This backend does
not use heaps, so the documented hole does not apply to it, but it confirms that
argument-buffer reads are tracked only through what `useResource` declares.

**The closest published fault to ours**: [mlx#3689](https://github.com/ml-explore/mlx/issues/3689),
"use-after-free under memory pressure: buffer-cache trim frees an MTLBuffer still used by
an in-flight command buffer". Same shape as this fault's gating - memory pressure is what
distinguishes a complex daylight scene from a night one, which is the day/night gate this
document has never had a mechanism for. The associated failure mode is command buffers
created with `commandBufferWithUnretainedReferences`, which do not retain what they
reference.

Checked and closed by reading: this backend builds command buffers from a plain
`MTLCommandBufferDescriptor` and never touches `retainedReferences`, which defaults to
YES. References are retained, so the mlx failure mode does not apply directly.

Still absent from every search: any public report of this fault, on Ryujinx, Ryubing,
MoltenVK, Apple's forums, wgpu or Dawn. The exclusion ledger here remains the only
document of it.

### Validation is now silent: three violations fixed

Working down what the layer reported, each fix re-running it to see the next:

1. `setPurgeableState(Empty)` on buffers still in use by a command buffer - these include
   the argument buffers carrying texture resource ids. Removed.
2. 65535x65535 scissor rects against small passes. The clamp existed but ran against
   `ulong.MaxValue` whenever the pass size was unknown, which is a no-op. Taking the size
   from the descriptor was not enough - a diagnostic named the case, a pass reaching
   `SetScissors` with all eight target slots and the depth slot null - so the scissor is
   now simply left unset when the size is unknown, which is always legal and means the
   whole attachment, exactly what a full-surface sentinel asks for.
3. Compute dispatches with a zero dimension. Metal rejects them outright; Vulkan treats
   them as a no-op, so nothing upstream filters them. Dropped here.

`METAL_DEVICE_WRAPPER_TYPE=1 MTL_SHADER_VALIDATION=1` now reports nothing for a full
boot into the reproducing save. Three genuine correctness bugs that no probe in this
investigation could have found, from a tool that took minutes.

Still to do, and it is the immediate next step: measure the flat rate on this build
*without* validation, which is far too slow to measure under. The first two fixes were
each measured null on their own (25% and 27.5%); the third has not been measured, and
neither has all three together.

### Fast math: on by default all along, and not it either

`MTLCompileOptions.FastMathEnabled` was never set, and its default is YES - so every
shader here has been compiled with fast math whether or not anyone chose it. That permits
flush-to-zero on denormals, and the composite ends in `clamp(x * numerator, 0, 1) * 3.5`
with `numerator` a reciprocal: a denormal divisor flushed to zero gives infinity, which
clamps to 1.0. White that owes nothing to the texture's contents - this fault's most
stubborn property. The ledger's "Metal fast math, 31.90%" row belongs to the era whose
probes were aimed at the tonemap, so it did not cover this.

RYUJINX_METAL_FAST_MATH=0, CodeGenVersion 7376 so nothing comes from the warm cache,
zero link failures: **30% against a floor of 27%**. Null.

That closes the compile-options axis alongside the API-legality one. What remains
untouched is still the shader binary itself - not its compile flags but its content,
against what Vulkan actually runs.

### The MSL/SPIR-V comparison: tool ready, comparison not run

`ShaderTranslationDiff` now emits three files per guest program instead of two: the MSL
the Metal backend runs, the GLSL it always emitted, and **the SPIR-V the Vulkan backend
actually compiles** (`.spv`, `TargetApi.Vulkan`, which resolves to `TargetLanguage.Spirv`
since `EnableSpirvCompilationOnVulkan` is true). The 1,771 pairs compared earlier in this
document were MSL against GLSL - a readable stand-in, never the program that does not
flash.

    RYUJINX_SHADER_DIFF=<dir>     writes <hash>-<stage>.{msl,glsl,spv}
    spirv-dis <file>.spv          to read it (brew install spirv-tools)

One trap, hit again while wiring this up and recorded here for the fifth time: with a
warm shader cache nothing goes through Translate, so the dump directory comes out empty.
CodeGenVersion is bumped to 7377 for that reason; bump it again for any future run that
needs fresh translations.

The comparison itself has not been made. What to look for, given everything else is
excluded: the composite is `ee89b4e471373459`, twelve texel fetches at level 0 in MSL.
The question is whether its SPIR-V fetches differ in a way MSL cannot express or expresses
differently - operand order, the level argument, sampled-vs-storage image type, or the
decorations Vulkan attaches that MSL has no equivalent for.

### The MSL/SPIR-V comparison, made: a real codegen bug that is not the flash

The comparison finally ran, and it found something. The Metal backend **silently drops
the constant offset on a texel fetch**. In `InstGenMemory.TextureSample` the entire
offset-assembly block sat behind `if (!intCoords)` with `// TODO: Support reads with
offsets.` beside it - and `intCoords` true is exactly the texel-fetch case, so the offset
was neither assembled nor its source operands consumed.

The effect is visible in the one shader this investigation has been circling. Of 3,626
fragment shaders dumped, exactly one uses a texel fetch: `9E7042ABC827EC13-Fragment`, the
composite. Its GLSL is twelve `texelFetchOffset` calls with twelve *distinct* offsets -
(-1,0) (-1,1) (0,-1) (0,1) (0,2) (1,-1) (1,0) (1,1) (1,2) (2,0) (2,1) and one unoffset, a
4x3 neighbourhood. Its MSL was twelve `.read()` calls at the *same* coordinate. A
neighbourhood filter degenerating into one texel fetched twelve times.

Fixed by folding the offset into the coordinate, which is the only place Metal's `read()`
will take it: `uint2(int2(x, y) + int2(ox, oy))`, added signed and converted after, so a
negative offset at the left or top edge wraps the way `texelFetchOffset` does. The
coordinate, array index, shadow compare and lod all had to become buffered and emitted
together, because the offset sits after the coordinate in the source list and `Src()` is a
cursor. `CodeGenVersion` 7378. The twelve offsets in the regenerated MSL match the GLSL
set exactly.

**It does not fix the flash.** Measured, not assumed:

    v132, offset dropped   2691 / 15599 flat   17.25%
    v133, offset fixed     1401 /  6599 flat   21.23%   (a second v133 run: 20.8%)

The fixed arm is *higher*. The two v133 runs agree with each other, which argues the
difference is not noise, but two arms of the same condition have drifted ~4 points before
in this investigation (the RAW_SPLIT A/B/A was 45.2 / 24.3 / 41.5), so the honest reading
is no improvement detected, in the direction of slightly worse.

The fix stays: it is a genuine rendering-correctness bug, it makes Metal agree with every
other backend, and it was found by the method rather than guessed. But the cross-backend
asymmetry - Vulkan 0/120 on the same machine, same driver, same save - remains unexplained,
and this was the last untouched axis. Note the result is at least self-consistent with the
0x55 injection: with a constant input the twelve taps are identical whether or not the
offsets are applied, so that test could never have distinguished this.

Tooling worth keeping. Dump the real thing from each backend rather than re-translating
in-process: `RYUJINX_SHADER_DIFF=<dir>` now writes whatever the running backend actually
compiles - MSL text under Metal, a SPIR-V module under Vulkan - and the guest-code hash in
the filename lines the two runs up. Re-deriving the other backend's output in-process does
not work for this shader: it throws InvalidCastException in `SpirvGenerator` (an assignment
whose destination is an `AstTextureOperation`, which that generator assumes is always an
`AstOperand`) while the same shader translates fine as the primary. `tools/spvdis.py` reads
the `.spv` without needing spirv-tools installed.

### Three more doors closed, 2026-08-15

- **Load and store actions.** Colour attachments load with `Load` or `Clear` and store with
  `Store`. The one `MTLStoreAction.DontCare` in the backend is the zero-draw elision added
  in `4ca16c68`, gated on `/tmp/ryujinx-metal-elide-empty-store`, which is absent - so it
  was never live in any measurement here. No pass discards its contents.
- **Heap aliasing.** There is no `MTLHeap` anywhere in the backend; every texture is a
  standalone `Device.NewTexture`. Two textures cannot be sharing memory.
- **The off-thread `SetData` race.** This was the standing prime suspect, recorded as such
  for a week. `RYUJINX_METAL_LOG_SETDATA_THREAD=1` over a full drive-in session including
  movement: **zero** off-thread calls. The probe prints on the first one. The asymmetry
  with `GetData` is real in the source and is never exercised. The lead is dead.

Flat rate across the three v133 runs: 20.8%, 21.2%, 19.8%. Against v132 at 17.25%.

### A Metal-level probe that watches both backends, 2026-08-15

MoltenVK is Metal underneath, so the Vulkan run and the Metal run can be watched with the
*same* instrument and their output diffed as text. `tools/mtlspy.m` builds a dylib that
swizzles the driver's own classes and logs every texture descriptor plus a per-frame
summary of encoders, commits, `useResource` usage/stages, and attachment load/store actions:

    clang -dynamiclib -fobjc-arc -arch arm64 -framework Foundation -framework Metal \
      -o /tmp/mtlspy.dylib tools/mtlspy.m
    DYLD_INSERT_LIBRARIES=/tmp/mtlspy.dylib MTLSPY=/tmp/spy.log ./Ryujinx ...

Two things had to be got right, and both were got wrong first:

- **Interposing does not work.** `__DATA,__interpose` on `MTLCreateSystemDefaultDevice`
  catches nothing, because the Metal backend reaches Metal through .NET P/Invoke, which
  resolves symbols with `dlsym` at run time and never goes through the bindings DYLD
  rewrites. Making a device in the constructor and swizzling its *class* works for both
  backends, since the class object is shared with whatever they create later.
- **MoltenVK never calls `-presentDrawable:`.** The first Vulkan run produced thousands of
  texture lines and not one frame line. The drawable-with-time variant is now hooked too,
  with a commit-count fallback.

The binary permits injection: it is adhoc-signed without hardened runtime and carries
`com.apple.security.cs.allow-dyld-environment-variables`.

**First results are not yet trustworthy and are recorded only as a marker.** Both runs hit
a 4000-line cap in the texture log, so the two descriptor histograms covered different
windows of the session and cannot be compared; the cap is now `MTLSPY_TEXLIMIT`, unlimited
by default. What the capped data suggested, to be confirmed: Metal reallocates far more
small textures than MoltenVK does, including the RG11B10Float scene target at the dynamic
resolution 800x448, where a freshly allocated Private texture has undefined contents. Also
unexplained: a minority of colour attachments arrive with `MTLStoreAction.Unknown` even
though every store assignment in the backend is `Store` unless `_elideEmptyStore` is set,
and its hot file is absent - some of the traffic may be the GUI's own Metal use rather than
the emulator's, which the probe does not yet separate.

### Probe status after the second attempt

The Metal half is now clean: a full session, 15,768 texture descriptors, and per-frame
lines whose load/store labels are correct (an earlier build had the `MTLLoadAction` indices
transposed, so its "load" column was really Clear). Steady state is ~597 render encoders
and ~5,200 `useResource` declarations per frame, all of them read-only or read-write, none
write-only, and ~8 colour attachments per frame still arriving with
`MTLStoreAction.Unknown` despite every store assignment in the backend being `Store` while
`_elideEmptyStore` is off. That last one is unexplained and worth chasing.

The Vulkan half is still not usable, for three different reasons across three attempts:
the texture log was capped; then the run never left shader loading inside its window
(`CodeGenVersion` 7378 invalidated the Vulkan disk cache too, so it retranslates from
scratch and needs well over five minutes); then a stale `pkill` from a previous background
job killed it just after it reached "Shader cache loaded" with 3,829 textures logged.

One real gap remains in the instrument: **no FRAME line has ever been produced under
MoltenVK**, even with `presentDrawable:atTime:` hooked and a commit-count fallback. So the
per-frame half of the comparison does not work under Vulkan yet and the present path needs
to be found - `[CAMetalDrawable present]` on the drawable itself is the obvious next
candidate, since it is not a command-buffer method and nothing hooks it.

The texture-descriptor comparison is the part that is nearly ready, and the raw counts so
far (Metal 15,768 against MoltenVK's few thousand, and MoltenVK's flattening out while
Metal keeps climbing) are suggestive but must not be quoted until both runs cover the same
gameplay window - the earlier version of exactly this comparison was invalidated by that.

### The command-stream comparison, made

Frame boundary solved by swizzling `-[CAMetalLayer nextDrawable]`: acquiring a drawable is
the one thing both backends must do once per frame, and CAMetalLayer is a public class, so
it can be hooked from the constructor without waiting for an object to appear. Neither
`presentDrawable:`, nor its with-time variant, nor a commit-count fallback ever fired under
MoltenVK.

Per 120 frames, both in gameplay on the same save:

                          Metal backend      MoltenVK
    render encoders           73,194           39,518      610/frame vs 329/frame
    commits                    3,842            2,867
    useResource              633,932          236,928      5,283/frame vs 1,974/frame
      read-only              618,123          230,648
      read-write              15,809            6,280
      write-only                   0                0
    colour load: Load        116,936           63,949
    colour load: Clear           600              480
    colour store: Store      116,576                0
    colour store: Unknown        960           64,429

Two real structural differences, neither of which is a white-flash mechanism:

- **MoltenVK defers every store action.** Essentially all of its colour attachments are
  created `MTLStoreAction.Unknown` and resolved at end of encoding with
  `setColorStoreAction:`, which is the documented deferred pattern. Ryujinx-Metal commits
  to `Store` up front. Ryujinx's choice is the conservative one - it cannot lose a store -
  so the backend that flashes is the one being *safer* here. This also explains the ~8
  Unknown attachments per frame seen on the Metal side as ordinary traffic rather than a
  bug. Worth noting it is the reverse of what was suspected.
- **Metal issues 1.9x the render encoders and 2.7x the useResource declarations.** Some of
  the encoder gap is the read-after-write split this fork turns on by default.

Neither backend ever declares a resource write-only, so a missing write declaration -
which would defeat argument-buffer hazard tracking - is excluded on both sides.

Texture allocation counts came out at 15,768 against 12,597 over comparable sessions. An
earlier capped run made this look like a 33x gap; it is not, and the capped figure was
never quoted for exactly that reason.

### The shape of the white - instrument added, measurement blocked

The classifier reduced every frame to a boolean and every probe since has asked *who wrote
white*. Nobody asked what the white looks like. A uniform fill - an undefined allocation, a
clear, a discarded store - has min == max and no spread. A real image driven to saturation
by a bad exposure or tonemap keeps its dark pixels, so min stays well below max. Those two
answers point at completely different faults and the existing 25-point grid could always
have told them apart.

`UploadCorrelator` now reports, per outcome, the mean per-frame darkest sample, brightest
sample, spread, and how many of the 25 actually saturated. No MSL changed, so no
CodeGenVersion bump.

**The measurement did not happen: the repro save is no longer being loaded.** Two v134 runs
both reported flat=0 over 10,199 frames with normal-frame mean luma of 57-58 and a spread of
43, against 138 in every v132/v133 arm earlier the same day. That is real gameplay - the
numbers drift slowly across the run - but in a dark scene, where this fault is known to go
to zero. `drive_in.sh` selects the save by pressing A three times on the top of the load
list, and this game has been launched a dozen-plus times today; TOTK writes autosaves, so
the top entry is very likely no longer the 4:45 PM daylight save the repro depends on.

Before any further measurement: confirm which entry the drive-in actually selects, and gate
every arm on normal-frame mean luma being near 138. A dark run reports zero flat frames and
reads exactly like a fix - the same failure mode the correlator already warns about for a
black picture, arriving by a different route.

The arms measured earlier today all show luma 138 and remain comparable with each other.

### An admissibility gate for every arm

`tools/arm_valid.py <run.log>` decides whether an arm may be quoted at all, and it is now
the first thing to run on any measurement. It rejects a run whose normal-frame mean luma is
far from the repro scene's 138, and one that classified too few frames to compare. Against
today's arms:

    setdata.log      OK    luma 138, 13799 frames, flat 18.94%
    spvrun-fix.log   OK    luma 128,  6599 frames, flat 21.23%
    ab-A-v132.log    OK    luma 135, 15599 frames, flat 17.25%
    shape.log        VOID  luma 58 - 80 off the repro scene
    shape2.log       VOID  luma 58 - 80 off the repro scene

That is the whole point: both VOID runs reported flat=0 over 10,199 frames and would have
read as a complete fix.

Still open, and blocking the shape measurement: **the drive-in never reaches gameplay at
all.** The autosave story written here first was wrong - it never got as far as the load
list. Draws per frame settles the question: a real gameplay arm runs 562 passes and 2,513
draws per frame, while all four suspect arms sit at 287-295 passes and 335-344 draws, which
is a menu. The luma gate caught them; the draw count names them.

The cause is not timing. `drive_in.sh` now presses through the sequence twelve times over
several minutes and verifies against the draw count, and still never gets in. Keyboard
events are simply not landing, and `CGPreflightScreenCaptureAccess()` returns **False** -
both of today's later failures, the refused screenshots and the ignored keypresses, are the
same missing TCC grants. They were present earlier the same day, when the luma-138 arms were
measured.

**This is user-side and cannot be fixed from here.** The app hosting this session needs
Accessibility (to post key events) and Screen Recording (to photograph a window) in System
Settings > Privacy & Security. Until then no unattended arm can reach gameplay, and every
run will report flat=0 from a menu.

### How the white is actually produced

Read statically out of the composite's MSL, needing no run and no permissions. The shader
ends in a normalised weighted average - sum of weighted taps over sum of weights - with the
division done as the usual bit-hack reciprocal plus one Newton step:

    temp_297 = temp_292 + temp_290;                          // the weight sum
    temp_301 = -bits(temp_297);
    temp_302 = temp_301 + 0x7EF19FFF;                        // ~ 1/temp_297
    temp_307 = fma(temp_297, -temp_302, fp_c1->data[0].y);   // Newton: (2 - d*r)
    temp_311 = temp_302 * temp_307;                          // refined 1/temp_297
    temp_313 = temp_311 * temp_310;                          // weighted sum / weight sum
    temp_314 = clamp(temp_313, 0.0f, 1.0f);
    temp_319 = temp_314 * 3.5;                               // HDR, into RG11B10Float

Put a zero weight sum through it: `bits(0)` is 0, so the reciprocal comes out as
`0x7EF19FFF` ~ 1.6e38, the Newton step degenerates to `fma(0, ., 2.0)` = 2.0, the product
is 3.2e38, and anything positive times that overflows. `clamp` turns that into 1.0 and the
final multiply makes it **3.5 in all three channels** - a perfectly uniform HDR white,
reached with finite arithmetic, no inf and no NaN required.

Two things corroborate it. The measured flat-frame luma is **251, not 255**, which is what a
fixed 3.5 becomes after the tonemap rather than what a saturated or filled surface would be.
And it makes a falsifiable prediction the shape counters already added will settle: if this
is the mechanism the white must be **uniform**, min == max across the grid.

This also retires an old test. The 0x55 injection - constant fed to the composite's input,
screen went red, 26.7% still white - was read as proof that the white is content
independent. It is not: a constant input makes every tap identical, which is precisely what
drives the weight sum to zero. That experiment could not have come out any other way, and
its negative result was never evidence.

If this holds, the fault is not in the composite at all - the shader is faithfully computing
0/0 on a flat input - and the question becomes what leaves its input texture flat on a fifth
of frames, and why MoltenVK never does.

### The shape of the white, measured: uniform, and the mechanism is confirmed

The divide-by-zero reading of the composite came with a falsifiable prediction: if the
white is produced by `Σw = 0` driving a bit-trick reciprocal to overflow, clamp and then
`× 3.5`, then it must be *exactly uniform* - not a picture. An arm that passes the
admissibility gate (luma 140, 10,799 frames, flat 21.49%) says:

    flat frames    min 248   max 254   sd 1.8    saturated 24.3/25
    normal frames  min  49   max 232   sd 57.9

A range of 6 out of 255 and a standard deviation of 1.8. That is a fill, and the normal
frames alongside it are a real image with structure. The exposure/tonemap reading - a
correct picture blown out by a bad multiplier - is dead: a saturated image keeps its dark
pixels and this has none.

So the composite is not the fault. It is faithfully computing 0/0 over a flat input, and
the question moves upstream: **what makes `tex_fp_t_tcb_8` constant on a fifth of frames,
when MoltenVK never does.** Note the texel-fetch offset bug collapsed the twelve taps onto
one texel too, and fixing it did not lower the rate - so it is the input texture itself
that is flat, not the sampling of it.

Getting this number needed three unrelated repairs, all worth keeping. `drive_in.sh` now
drives by evidence: it presses through the sequence and checks draws per frame, since
gameplay runs ~2,500 and the menus sit near 340, and four consecutive arms had been
measuring menus and reporting flat=0. `tools/arm_valid.py` refuses any arm whose
normal-frame luma is far from the repro scene's 138. And the whole late-session collapse -
refused screenshots, ignored keypresses, `import Quartz` failing outright - was macOS TCC:
toggling the grants invalidated the running process's own, and the host app had to be
restarted before Accessibility and Screen Recording applied.

### The input is not flat

If the composite goes white because it averages over a single colour, its input should be
that single colour. Sampled directly - twenty-five points of the scene texture bound to the
frame's last scene-class draw, compared as raw 32-bit values because RG11B10Float bytes are
not BGRA and only equality means anything:

    flat frames    20.25 distinct of 25   (1,761 frames)
    normal frames  20.27 distinct of 25   (6,630 frames)

Identical. The input carries as much variety on a white frame as on a good one, so nothing
upstream is flattening it, and the "what makes the input flat" question recorded after the
uniformity result was the wrong question.

Two readings survive, and they are separable:

- **The sampled texture is not the composite's input.** `_frameSceneTex` is the *last*
  scene-class texture bound in the frame, and around thirteen draws bind one. Pinning the
  sample to the program that actually performs the twelve texel fetches settles it.
- **The input is fine and the weight sum reaches zero some other way.** The weights are not
  the taps alone; the coordinate and the normalisation both read constant buffers
  (`fp_c3->data[0]` feeds the fetch coordinates, `fp_c1->data[0].y` is the Newton constant).
  A zeroed or stale constant buffer produces the same 0/0 with a perfectly good texture.

The second is the more interesting one and has never been looked at: every binding check so
far compared identities, and a constant buffer keeps its identity while its contents change.

---

## RESUME HERE (2026-08-15, evening)

**Where this stands.** The flash is halved and not fixed, and for the first time the
mechanism that produces the white is confirmed rather than argued.

The composite (`9E7042ABC827EC13-Fragment`, the one fragment shader of 3,626 that uses a
texel fetch) ends in a normalised weighted average whose division is a bit-trick reciprocal.
When the weight sum is zero the reciprocal overflows, clamps to 1.0, and is multiplied by
3.5 across all three channels - in finite arithmetic, with no inf and no NaN. That predicted
the white must be *exactly uniform* rather than a picture blown out by a bad exposure, and a
gated arm settles it:

    flat frames    min 248  max 254  sd 1.8   saturated 24.3/25
    normal frames  min  49  max 232  sd 57.9

So the composite is faithful: it computes 0/0 and writes what that produces. The question is
what drives its weight sum to zero.

**The obvious answer is already excluded.** The input is not flat - 20.25 distinct values of
25 sampled points on flat frames against 20.27 on normal ones, over 8,399 gated frames. The
texture carries as much variety on a white frame as on a good one.

**The next action.** One probe, pinned to the program that actually performs the twelve
texel fetches. `_frameSceneTex` currently records the frame's *last* scene-class binding and
about thirteen draws bind one, so pinning also closes off the "sampled the wrong texture"
reading. On that draw, record by outcome:

- `fp_c1->data[0]` (the Newton constant) and `fp_c3->data[0]` (the fetch coordinates' scale
  and bias). A zeroed or stale constant buffer produces 0/0 from a perfectly good texture,
  and this has never been looked at: every binding check so far compared *identities*, which
  a constant buffer keeps while its contents change.
- The twelve taps of a single pixel, rather than 25 points spread across the image. The
  weight sum is computed per pixel from a local neighbourhood, so a globally varied texture
  can still be locally flat.

The verdict is clean either way. If those constants differ between flat and normal frames,
that is the fault. If they are bit-identical, the zero comes from the local neighbourhood,
and the twelve-tap sample says so directly.

**Running an arm.**

    ./tools/drive_in.sh artifacts/terminal/Ryujinx-metal-v135-inputshape /tmp/arm.log \
        RYUJINX_METAL_UPLOAD_CORR=1
    python3 tools/arm_valid.py /tmp/arm.log      # never quote a number this rejects

`drive_in.sh` drives by evidence now: it presses through the sequence and checks draws per
frame, because gameplay runs ~2,500 and the menus sit near 340, and four consecutive arms
were silently measuring menus and reporting flat=0. `arm_valid.py` rejects any arm whose
normal-frame luma is far from the repro scene's 138 - a dark scene reports zero flat frames
and reads exactly like a fix. `SAVE_INDEX=n` picks a lower entry in the load list.

**Traps, each of which has cost real time here.**

- macOS TCC: toggling Accessibility or Screen Recording invalidates the running process's
  own grants. The host app has to be restarted, or keypresses are ignored, screenshots are
  refused, and `import Quartz` fails outright.
- Never leave a background job whose tail is `pkill -x Ryujinx`. Two runs were killed by a
  sibling job moments after they reached gameplay.
- Bumping `CodeGenVersion` invalidates the *Vulkan* disk cache too, which then needs well
  over five minutes of retranslation before it reaches gameplay.
- A warm cache changes boot timing enough to break any fixed-sleep drive-in.

**What is excluded, positively rather than by elimination.** Ordering (encoder splits, full
serialisation, MTLFence), content injection, binding identity, residency, authorship,
present, out-of-bounds fetch, compile options, API legality, load and store actions, heap
aliasing (there is no MTLHeap in the backend), the off-thread `SetData` race (zero
occurrences over a full session), the MSL texel-fetch offset bug (real, fixed, no effect on
the rate), and the exposure/tonemap reading (dead as of the uniformity result). Vulkan
remains at 0% on the same machine, same driver, same save.

**Builds.** `v135-inputshape` is current. `v134-shape` added the shape counters, `v133`
the texel-fetch offset fix, `CodeGenVersion` 7378.

### The composite's constants, and an input that is still not flat

Pinned this time to the program that actually performs the twelve texel fetches - it
identifies itself by containing `.read(uint2(`, being the one fragment shader of 3,626 that
does, so no label table can go stale. Gated arm: luma 130, 11,399 frames, flat 20.53%.

**The input is still not flat, even pinned to the composite.**

    input distinct   flat 19.72 of 25 (2,338 frames)   normal 20.78 of 25 (8,532)

A real difference, and far too small to matter: nineteen distinct values in twenty-five
samples is a picture, not a single colour. So the composite's output is uniform while its
input is not, and "the input got flattened" is dead in its pinned form too.

**Constants, first four floats of every buffer bound on that draw:**

    cb0    flat [0 0 0 0]                                normal [0 0 0 0]
    cb20   flat [7.97373e-20  2         0 0]             normal [7.97279e-20  1.99977  0 0]
    cb22   flat [-22.5431  0.826443  -14.274   0.898843] normal [-21.6839  0.826799  -15.6355  1.01359]

One of these is worth chasing and two are not. `cb22` drifts steadily across reporting
intervals (-28.2, -25.9, -24.2, -22.5 on the flat side alone), so it is a scene quantity and
any difference of means between outcomes is confounded by time, not evidence. `cb0` is zero
in both.

`cb20.y` is **exactly 2.000000 on flat frames and 1.99977 on normal ones**, stable across
every interval. Two is the Newton constant in the reciprocal refinement
`fma(Σw, -r, fp_c1->data[0].y)`. A mean of exactly 2 with no deviation means it is 2 on
*every* flat frame, while normal frames sometimes carry something slightly lower.

That is suggestive and not yet a finding: a mean cannot distinguish "always 2 on flat
frames" from "flat frames happen to be the subset where it is 2". The next measurement is
the distribution, not the average - what fraction of frames in each outcome has it exactly
2.0 - and, alongside it, the twelve taps of a single pixel rather than 25 points spread
across the image, since the weight sum is computed per pixel from a local neighbourhood and
a globally varied texture can still be locally flat.

### Neither the neighbourhood nor the constants

Gated arm: luma 138, 10,799 frames, flat 22.18%.

**The local neighbourhood is not flat either.** Sampling twenty-five *adjacent* texels around
the centre rather than a grid over the whole image - the weight sum is computed per pixel
from a 4x3 neighbourhood, so a varied picture could still have been locally flat:

    input distinct   flat 19.15 of 25    normal 19.20 of 25

Nineteen distinct values among twenty-five neighbours, on both outcomes. Local flatness is
dead too.

**The constants are correct on exactly the frames that fail.** Extrema rather than means,
because a mean cannot separate "always this value" from "happened to have it":

    cb20  range flat    x [7.973727e-20, 7.973727e-20]   y [2, 2]
          range normal  x [0,            7.973727e-20]   y [0, 2]

On flat frames `cb20.y` is 2 with zero spread, every single time. It is the *normal* frames
that sometimes carry zero - most likely a buffer this probe read before it was bound. So the
Newton constant is never wrong when the frame goes white, and the constants are not the
cause.

**Which leaves one reading, and it is the one that fits every prior negative.** The taps
return zero not because the texture is flat but because the fetches land somewhere the frame
never wrote. The game runs dynamic resolution - the scene target is allocated at 1600x896
and rendered at 800x448 - so a stale or wrong `TexelFetchScale` sends the reads into the
part of the texture outside this frame's rendered region. That region is inside the texture,
so:

- the input sampled directly still shows a picture, because this probe reads the centre of
  the *written* area (measured, twice);
- clamping the fetches changed nothing, because clamping is to the texture's bounds and the
  bad coordinates were never out of those bounds (measured earlier: 24.7% against 25-26%);
- injecting content changed nothing, for the same reason;
- and MoltenVK never does it, because the scale reaches the shader by a different path.

**Next:** record, by outcome, the scene texture's dimensions against the frame's actual
rendered region and the scale that `TexelFetchScale` applies. A mismatch on flat frames is
the fault. Sampling the texture's far corner alongside its centre would corroborate it
directly - the corner should be unwritten on flat frames.

### render_scale is 1.0, and the input measurement has been reading the wrong moment

Gated arm: luma 140, 10,799 frames, flat 22.78%.

    render_scale[0].x   flat [1,1]   normal [1,1]
    render_scale[1].x   flat [1,1]   normal [1,1]

Exactly one, zero spread, on both outcomes. `TexelFetchScale` therefore takes its
`temp_1 == 1.0f` early return and hands back the coordinate untouched, so the dynamic
resolution reading - a stale scale sending the taps into the unrendered part of the texture
- is dead. (The first attempt at this read the support buffer's first four floats, which are
the alpha-test field and are legitimately zero; render_scale lives at
`GraphicsRenderScaleOffset`.)

That leaves a contradiction, and it is the useful part of this result. The input carries a
picture, the coordinates are unscaled, the constants are right - and the output is uniform
white on a fifth of frames. Something measured here is not measuring what it claims, and the
candidate is specific:

**The input texture is sampled at present, not at the composite draw.** The blit is encoded
at the frame boundary, so it reads that texture's contents *after every pass in the frame*.
If anything writes the texture between the composite and present, the probe reports content
the composite never saw. Every "the input is not flat" result in this document - global and
local, pinned and unpinned - inherits that flaw, and none of them is evidence about what the
shader actually read.

Fixing it means sampling inside the frame, at the composite's own draw, which needs the
render encoder to end and restart - the same read-after-write split that is already known to
move the flash rate, so the probe would perturb what it measures. The way around that is to
sample the texture into a scratch copy at the start of the composite's pass rather than at
present, or to identify what writes that texture after the composite and check whether it
runs on flat frames. The second is cheaper and answers the aliasing question directly: the
per-storage writer census already exists and needs only to be restricted to the composite's
input and split by outcome.

### Nothing overwrites the input, so the contradiction is real

Gated arm: luma 139, 10,799 frames, flat 21.59%.

    input written after the composite read it   flat 0/2331   normal 4/8468

Zero on flat frames. The worry that the input probe was sampling at present and therefore
after some later writer does not apply: on the frames that go white, nothing touches that
texture between the composite's read and the frame boundary, so what the probe sampled is
what the shader read. Every earlier "the input is not flat" measurement stands.

Which sharpens the contradiction into something quite specific. On a white frame:

- the composite's input holds a picture - 19.15 distinct values among 25 *adjacent* texels;
- nothing overwrites it afterwards;
- the fetch coordinates are unscaled, `render_scale` being exactly 1.0 with zero spread;
- the Newton constant is exactly 2.0 with zero spread;
- and the output is a uniform white fill.

A shader reading that texture at those coordinates cannot produce that output. So the taps
are not returning the contents of that texture, which is precisely what the constant
injection experiment concluded long ago and what the comment at the scene-binding site still
records: the fetch returns something that is not in that texture's memory.

**The next target is the argument buffer, not the binding.** Everything called a "binding
check" in this document compared the values Ryujinx *intended* - `gpuAddress`, `nativePtr`,
`CanonicalPtr` - captured at the point of binding. What the GPU dereferences is the resource
id written into the Tier 2 argument buffer, and those are not the same object: a stale or
half-updated argument buffer hands the shader a different texture, and if that texture is
uniform - a cleared surface, an unwritten allocation - every tap returns the same value, the
weight sum is zero, and the frame goes white with a perfectly good input texture sitting
untouched in memory.

That reading survives every measurement in this document, and it is the only one that does.
Read the argument buffer's own bytes for the composite's texture slot at that draw, and
compare them by outcome against the resource id Ryujinx believes it wrote.

### The argument buffer moves the rate, in the wrong direction

Giving every argument buffer its own allocation instead of a range in the shared staging
ring - the candidate fix for "a reused staging range hands the shader someone else's
resource ids" - was measured on a gated arm:

    own allocation   luma 129, 7,799 frames, flat 34.79%
    baseline         luma 139, 10,799 frames, flat 21.59%

Thirteen points worse, roughly twenty-five standard errors. It is off by default now
(`RYUJINX_METAL_ARGBUF_OWN=1` to enable), but the size of the move is the result. This is
only the second intervention in the whole investigation to shift the rate substantially -
the read-after-write split was the first - and it says the argument buffer path is causally
connected to the fault rather than merely adjacent to it.

The direction is informative too. A fresh `MTLBuffer` per draw is the version that fails
*more*, which points at residency rather than at staleness: the encoder's `useResource`
declarations are built around the staging buffer, so a brand-new allocation can be
dereferenced while not resident, and the shader then reads garbage where the resource ids
should be. That is the same mechanism this experiment set out to test, arrived at from the
other side - and it fits the contradiction exactly, because garbage ids point the taps at
some other texture while the real input sits untouched and full of picture.

**Next, and this is now a fix rather than a probe:** check that the argument buffer itself
is declared to the encoder. If the staging buffer is resident only incidentally - because
something else in the frame declared it - then the correct fix is an explicit `useResource`
on whichever buffer backs the argument table, every time it changes, with
`MTLResourceUsageRead` and the fragment stage. Confirm first by counting how often the
backing buffer changes identity between draws, split by outcome.

### A real use-after-free, fixed, and it is not the flash

`Pipeline.DisposeRenderTemporaryBuffers()` runs on the line after `drawPrimitives`, and
`ScopedTemporaryBuffer.Dispose()` deleted the buffer outright when it came from the fallback
path rather than from a staging reservation. One of those buffers is the argument buffer
holding the resource ids the shader dereferences, so its `MTLBuffer` was destroyed before
the GPU ran the draw. That is a genuine use-after-free and it is fixed: deletion is now
deferred behind the fence of the command buffer that referenced the buffer
(`BufferManager.DeleteWhenComplete`).

It does not fix the flash, and the control says the use-after-free was never what made the
own-allocation arm bad either:

    baseline                              21.59%   (10,799 frames, luma 139)
    deferred delete                       23.66%   (10,799 frames, luma 139)
    own allocation, no fix                34.79%   ( 7,799 frames, luma 129)
    own allocation, deferred delete       32.67%   ( 7,799 frames, luma 132)

Forcing every argument buffer onto its own allocation still costs eleven points with the
lifetime bug repaired, so what that arm demonstrates is not premature deletion. The fallback
path is presumably rare on the default arm, which is why repairing it changes nothing there.

So the argument buffer path remains causally connected to the rate - two arms, thirteen and
eleven points - and the reason is still not identified. Residency is the remaining candidate:
a fresh `MTLBuffer` per draw is not covered by whatever declaration keeps the staging buffer
resident, and an argument table read while non-resident returns garbage ids, which points the
taps at another texture and produces exactly the uniform fill that is measured. The next
probe is to count `useResource` declarations naming the argument buffer itself, split by
outcome, and compare the two allocation strategies.

### Residency audit of the argument buffer: no gap found

Every site that writes a resource id into the argument table was checked against the
`AddResource` call that declares the same resource to the encoder. On the graphics path the
pairing holds: the texture and image branches each declare what they name, the sampler
branch writes sampler ids and correctly does not declare them (samplers need no residency),
and the array branch declares once per texture in the same loop that writes the ids. The
guard inside `AddResource` skips a null pointer, but `AddressForTexture` derives the id and
the pointer from the same `MTLTexture`, so an id cannot be written for a resource whose
pointer is null.

So the eleven-point cost of a per-draw allocation is not a missing `useResource` either, on
this reading of the code. What has not been done is to observe it rather than read it -
count the declarations actually issued per draw under each allocation strategy, split by
outcome, and see whether the counts differ. That is the next probe, and it is the last
untried thing on this axis.

### Residency declarations are identical by outcome

Counted rather than read: how many resources the composite's draw actually declares to the
encoder, split by outcome.

    composite residency declarations   flat 3.00   normal 3.00

Exactly three, on both, with no variance. The declaration count is not what differs on a
white frame, which closes the residency reading in its countable form and leaves the
argument buffer's eleven-point effect unexplained by anything measured so far: not premature
deletion, not a missing declaration by inspection, not a differing declaration count.

Note this arm sat on the menu for nine minutes before the drive-in's retries got it into the
game - the menu figures (normal 3.00 over 15,000-odd frames, flat 0 over 0) are the title
screen and mean nothing. Only the counts after `2527 draws/frame` appears are gameplay.

The gate voids this arm's *rate* (luma 75, dragged down by the nine menu minutes), and that
verdict is correct - 5.54% from this run must not be compared with anything. The declaration
count survives it: three-versus-three is a structural property of a draw, not a rate, and the
1,129 flat frames it averages over are gameplay frames, since flat frames do not occur on the
menu at all.

### The argument buffer is never overwritten, and that closes the CPU side

Every check before this compared the resource id Ryujinx *computed*. This one compares the
id still sitting in the argument buffer's memory when the frame ends against the one written
at the composite's draw - the bytes the GPU dereferences, not the intent behind them.

    argument buffer overwritten by frame end   flat 0/2,505   normal 0/17,814

Never, on either outcome. So on a frame that goes white:

- the argument buffer holds exactly the ids Ryujinx wrote, unmodified;
- those ids name a texture that holds a picture, across adjacent texels;
- nothing writes that texture between the composite's read and the frame boundary;
- the fetch coordinates are unscaled, `render_scale` being exactly 1.0;
- the Newton constant is exactly 2.0;
- the draw declares its three resources, the same three as on a good frame;
- and the result is a uniform fill.

**Everything Ryujinx puts in front of the GPU is correct on exactly the frames that fail.**
That is the strongest statement this investigation has been able to make, and it is now made
from measurements rather than from elimination. What remains is on the far side of the API:
either the driver misreads a correctly-formed argument table, or something in GPU-side
timing makes the fetch return stale memory despite correct descriptors - and Vulkan, going
through MoltenVK to the same driver on the same machine and save, never does it.

This is the point at which the ledger stops being an investigation and becomes a report.

The gate voids this arm's rate as well (luma 100, so 11.93% must not be compared with
anything). As with the residency count, the mismatch figure is not a rate: zero mismatches
across 2,505 genuine flat frames is zero whatever the scene's brightness, and flat frames
only occur in gameplay.

### Forcing a full rebind changes nothing

`UpdateAndBind` only runs for a set whose dirty flag is set, so a draw whose textures are
considered clean keeps whatever argument buffer was bound earlier - while
`RenderEncoderBindings.Clear()` at the top of the prepass has already released the list that
held it. That is a real hazard and it is not this fault:
`RYUJINX_METAL_FULL_REBIND=1`, which rebuilds and rebinds every set from current handles on
every draw, measures 22.32% on a gated arm (luma 141, 11,399 frames) against a 21-23%
baseline. The binding is not stale.

Every contained intervention on this axis has now been tried and measured:

    deferred deletion of temporary buffers   no effect   (23.66% vs 21.59%)
    argument buffer on its own allocation    worse       (34.79%, 32.67% with the fix)
    forced full rebind every draw            no effect   (22.32%)
    residency declarations at the draw       identical   (3.00 vs 3.00)
    argument buffer overwritten by frame end never       (0/2,505 flat)

The one intervention left on this axis is the one that cannot be done with a switch: taking
the composite's textures out of the argument buffer entirely and binding them directly. The
MSL declares them as `constant Textures &textures [[buffer(19)]]`, so this needs the MSL
declaration emitter, every access site, and the backend's binding path to change together,
plus a `CodeGenVersion` bump and a full retranslation. It is the only untried thing that
removes the indirection whose *allocation strategy alone* moves the rate by eleven points.

### A hole in every input measurement here: the composite may draw more than once

`NoteSceneBinding` keeps the *last* scene-class binding of the frame, and the per-frame
program signatures show programs repeating within a single frame - `7e5f0a` twice in the
sample above, `220ff4` three times in an earlier one. Nothing in this document establishes
that the composite draws exactly once per frame.

If it draws more than once, every input measurement recorded here describes the last
invocation only. "The input is not flat", globally and locally, and "nothing overwrites it
afterwards" would then be statements about one invocation while the white could be produced
by another - one that reads a different texture, or the same texture at a different point in
the frame.

This does not overturn anything measured about the *output* - the white is uniform, the
constants are exact, `render_scale` is 1.0, the argument buffer is never overwritten - but it
does mean the input side is less settled than the rest of this section reads.

**Check this first next session, before anything else:** count the composite's draws per
frame, split by outcome. It is a two-line change to the probe that already exists
(`IsTexelFetchComposite` identifies the program), and if the count is greater than one, the
input measurements need redoing per invocation rather than per frame.

### The composite draws exactly once on a white frame - the hole is closed

Gated arm: luma 142, 11,399 frames, flat 20.87%.

    composite draws per frame   flat 1.00   normal 0.93

Exactly one on every flat frame, with no variance. The worry raised in the previous section
- that `NoteSceneBinding` keeps the last invocation and the composite might draw several
times - does not apply on the frames that matter. Every input measurement in this document
describes the only invocation there was, and those conclusions stand.

The 0.93 on normal frames is the other half of the fact: on roughly seven percent of good
frames the composite does not draw at all, while it is never absent when the frame goes
white. It is always there to paint the white, which is consistent with everything else and
narrows nothing further by itself.

There is no switch for it. The MSL emitter produces texture types only in `SamplerType.cs`,
and `Declarations.cs` has exactly one path for them - into the `Textures` struct that becomes
the argument buffer. No alternative direct-binding path exists to be turned on, so the change
really is four things moving together: the declaration emitter, every access site, the
backend's binding path, and a `CodeGenVersion` bump with the full retranslation that implies.
Checked, not estimated.

### A symptom guard on the divide, and a verification trap

The composite's white is produced by a fast-reciprocal division: seed `0x7EF19FFF`, one
Newton step, and a weight sum of zero drives the product to overflow, clamp to 1.0, and get
multiplied by 3.5 into all three channels. `Program.GuardReciprocal` patches the emitted MSL
so the refined reciprocal is zero when the denominator is zero, which would make those frames
dark instead of blinding. It is a symptom guard, not a cure - it does not explain why the
weight sum reaches zero over an input texture that demonstrably holds a picture - but it is
the decisive test of the mechanism.

**It is not yet verified, and two runs were burned on a verification trap.** The obvious check
is to grep the `RYUJINX_SHADER_DIFF` dump for the patched expression. That can never work:
the dump is written by `ShaderTranslationDiff` inside the translator, upstream of the Metal
backend's patcher, so it always shows unpatched code. Both runs reported zero matches and a
baseline flat rate, which reads exactly like "the guard did nothing" when it may simply never
have been observed.

The patcher now logs `guard-rcp: patched N division(s), denominator temp_X` when it fires.
The first attempt also genuinely did not match, for a second reason worth keeping: it
required the seed line and the negation feeding it to be adjacent, and the emitted MSL puts a
guest-address comment between every statement and schedules unrelated temps in between. It
now matches them independently, and a dry run of exactly that logic against the dumped shader
finds seed `temp_302`, denominator `temp_297`, and one division to patch.

**Next session, first thing:** run one gated arm with `RYUJINX_METAL_GUARD_RCP=1` (the
default) and check the log for `guard-rcp:` before reading any rate. If it fires and the flat
frames go dark, the chain from the weight sum to the white is confirmed end to end and the
remaining work is upstream. If it fires and they stay white at luma 250, the divide is not
where the white is made and this document's central reading is wrong.

### The divide-by-zero reading is refuted

The guard fired - `guard-rcp: patched 1 division(s), denominator temp_297` - and the white
did not move:

    gated arm, guard active   luma 140, 11,399 frames, flat 22.19%
    flat frames               min 248  max 254  sd 1.9   luma 252

Clamping the reciprocal to zero when its denominator is zero changes nothing. The frames are
still uniform white at 252. **So the white is not produced by that division.** Whatever makes
these frames white, `temp_297` being zero is not the trigger.

This is the most load-bearing thing in the document and it is now false. The uniformity
result - min 248, max 254, sd 1.8 - was read as confirming the divide-by-zero derivation, and
it does not: uniform white is equally consistent with a fill arriving from somewhere else
entirely. Everything downstream that treated "the composite computes 0/0" as established
needs re-reading in that light, including the entire line of enquiry into what flattens its
input, which was chasing a condition that does not produce the symptom.

What survives, because it was measured directly rather than derived: the white is a uniform
fill and not a saturated picture; the composite draws exactly once on every white frame and
is never absent; its input holds a picture; nothing overwrites that input; `render_scale` is
1.0; the constants are exact; the argument buffer is never overwritten; and the argument
buffer's allocation strategy moves the rate by eleven points.

The open question is now blunter than it has been all day: **what writes the uniform white,
if not this division?** The composite is the last thing to touch the surface before present -
but "last writer" was established by a census that assumed the composite produces the white,
and that assumption is gone. Re-run the writer census without it.

The refutation survives the obvious objection to it. The first guard tested the denominator
against exactly zero, so it only ruled out an exact 0/0; a weight sum of 1e-30 would give the
bit-trick reciprocal about 1e30, overflow the product just the same, clamp to 1.0 and
multiply by 3.5. Re-run with a threshold - `!(fabs(temp_297) > 1e-8f)`, which also catches
NaN - the guard fires, nothing fails to link, and the result does not move:

    threshold guard   luma 141, 11,399 frames, flat 21.36%
    flat frames       min 246  max 254  sd 2.4   luma 251

So the division is not the source however small its denominator gets. That closes the
loophole and makes the refutation solid: whatever writes this uniform white, it is not this
shader's normalisation.

### The composite's output is a picture on the frames that present white

Sampled the composite's own colour attachment - twenty-five adjacent texels of what it
writes, not what it reads - against the presented surface on the same frames. Gated arm:
luma 141, 11,399 frames, flat 22.46%.

    composite output      flat 19.32 distinct of 25    normal 18.64 of 25
    presented surface     flat min 246 max 254 sd 2.3  (uniform white)

**On the very frames that present a uniform white, the composite writes a picture.** It does
not make the white. The white is introduced after the composite and before present.

That relocates the fault entirely. Every measurement in this document until now was aimed at
the composite - its input, its constants, its coordinates, its argument buffer, its
neighbourhood, its division - and it is not the culprit. It is simply the last shader anyone
had reason to suspect, and the divide-by-zero derivation gave that suspicion a mechanism that
turned out to be false.

The stage between the composite's output and the presented surface is where to look now, and
the earlier conclusion that present is innocent must be re-read: "FSR RCAS faithfully
reproduces an already-white input" was established by feeding it white, which shows it
propagates white rather than that its input was white. It was never shown that the surface
handed to present is white *before* present touches it - and this measurement says the
surface written by the composite is not.

**Next: sample the presented surface's own storage immediately before the present pass, and
enumerate every pass that touches it between the composite and present, split by outcome.**
The writer census exists; it needs re-running without the assumption that the composite is
the writer of interest.

The existing writer census cannot answer the new question as it stands. Its table prints
scene-sampling signatures - the `n=NN` program lists - because `slot.Writers` comes out empty:
what it captures is who *sampled* a scene-class texture, not who *wrote* the presented
storage. That distinction did not matter while the composite was the suspect and it is the
whole question now. `NoteAttachmentDraw(root, program)` is the right hook and it is already
called; what is missing is that the presented surface's root is not the root it is keyed on
for these frames.

The presented storage's age is clean and unchanged: `age=1` on flat 2,559 and normal 8,807,
so the surface shown was an attachment in the previous frame in essentially every case,
whatever the outcome.

### Why the census is empty, and what that implies

`Present(CAMetalDrawable drawable, Texture src, ...)` hands the correlator a real Ryujinx
`Texture`, so `src.CanonicalPtr` is a valid key. `NoteAttachmentDraw` is keyed on
`RenderTargets[0].CanonicalPtr` from the draw path. The two keys are the same kind of thing,
and the census still comes out empty - which means the presented storage **never appears as
the render target of a draw**.

Yet its age says it was an attachment one frame ago, on 2,559 flat frames and 8,807 normal
ones. Both can only be true if the presented surface is written by something that is not a
draw: a blit, a clear, or a compute dispatch. `NoteAttachmentDraw` fires from exactly one
site, per draw, so none of those are recorded.

That is the sharpest lead left, and it fits the shape of the fault better than anything the
composite offered: **a clear writes a uniform fill by definition.** A frame where the blit
that should overwrite the surface does not run - or runs before the clear rather than after -
presents exactly what is measured: min 246, max 254, a spread of eight, no structure at all.

**Next: record the non-draw writers.** Hook the blit and clear paths the way the draw path is
hooked, key them on the same `CanonicalPtr`, and split by outcome. If flat frames show a
clear with no subsequent blit, the fault is found - and unlike everything chased today, that
is a fault in ordering, which this backend has already been shown to get wrong once.

### One writer, and it is not the shader this document has been calling the composite

With attachment binds recorded as writers, the census works. Gated arm: luma 141, 11,399
frames, flat 20.38%. It reports exactly one signature for the presented storage:

    480117,attach     on every frame, flat and normal alike

No variant, no frame with a clear and no blit. The ordering hypothesis of the previous
section is dead: the presented surface has the same writer whatever the outcome.

What it exposes instead is worth more. **The program that writes the presented surface is
`480117`.** This document has spent the entire day on `9E7042ABC827EC13` - the one fragment
shader of 3,626 that uses a texel fetch - and called it "the composite" because the
divide-by-zero derivation made it the suspect. Those are not the same shader. `480117` is the
last entry in every scene-sampling signature; the texel-fetch shader is somewhere upstream.

That reconciles the result that relocated the fault: the texel-fetch shader writes a picture
on frames that present white because **it was never the thing writing the presented
surface**. Its output going to a different surface is not a contradiction, it is the
expected consequence of having identified the wrong shader.

**Next: dump and read `480117`.** It is the last writer of the surface that goes white, on
every frame, and nothing about it has been examined - not its inputs, not its constants, not
its arithmetic. `RYUJINX_SHADER_DIFF` names files by guest-code hash while the census names
programs by `DebugLabel` (XXH3 of the MSL sources), so the two have to be joined through
`Program.DumpSources`, which writes `{DebugLabel}-fragment.metal`.

### The real last writer is a pass-through blit

Dumped by firing the existing draw trace (`touch /tmp/ryujinx-metal-trace` during gameplay
writes every drawing program's MSL to `/tmp/ryujinx-metal-shaders`).
`480117a3b1123d65-fragment.metal` is 112 lines and contains no arithmetic worth the name:

    temp_7  = 1.0f / temp_0;                      // perspective divide
    temp_8  = temp_7 * temp_3;                    // u
    temp_9  = temp_7 * temp_6;                    // v
    temp_10 = tex_fp_t_tcb_8.sample(samp_fp_t_tcb_8, float2(temp_8, temp_9));
    out.color0 = temp_10;                         // straight out

One sample, written through unchanged. **It cannot manufacture white.** So a uniform white
output leaves exactly two possibilities, and they are cleanly separable:

1. **Its source texture is already uniform white** - the fault is upstream of it, and this
   blit is faithfully reproducing what it was given. This is the same shape as the earlier
   note that FSR RCAS reproduces a white input, and it is now the whole question rather than
   an aside.
2. **Its UVs are degenerate.** `temp_0` is the interpolated w; if it goes to zero or the
   attributes collapse, `u` and `v` are constant across the primitive, the sampler returns
   the same texel for every pixel, and the screen is a uniform fill of whatever colour that
   texel holds. That produces exactly the measured signature - no structure, tiny spread -
   without anything being wrong with the source texture at all.

Possibility 2 has never been considered anywhere in this document and it fits the
measurements as well as possibility 1 does. It is also cheap to separate: sample this
shader's source texture at present, the way the texel-fetch shader's input was sampled, and
compare its uniformity against the presented surface's. If the source has structure while the
output does not, the UVs are the fault and the whole search moves to the vertex stage that
produces `inAttr0` and `position.w`.

### The UVs are degenerate - the white is one texel, stretched over the screen

Gated arm: luma 140, 11,399 frames, flat 23.12%.

    blit source, flat frames    19.43 distinct of 25 adjacent texels   (a picture)
    blit output, same frames    min 247  max 254  sd 2.0               (a uniform fill)

The shader between them does one thing: `out.color0 = tex.sample(samp, float2(u, v))`. There
is no arithmetic that could turn a picture into a fill. So on a white frame the sampler is
returning the same texel for every pixel, which means `u` and `v` are constant across the
primitive - **the interpolated UVs collapse.**

That is the fault, and it explains every result in this document that looked correct and
useless. The source texture holds a picture, the constants are exact, `render_scale` is 1.0,
the argument buffer is never overwritten, residency is declared, nothing overwrites anything
- all true, all irrelevant, because the failure is in the interpolation feeding a
pass-through blit, and nobody had looked at a shader that does no work.

It also explains the colour. The white is not 255 and never was; it is 247-254, which is
simply the value of whichever bright texel the collapsed UV happens to land on.

**Next, and this is now a bug hunt rather than a search:** `u` and `v` come from
`in.inAttr0.xy * in.position.w` scaled by `1.0f / temp_0`, where `temp_0` is the interpolated
w. Read `480117a3b1123d65-vertex.metal`, which the same trace already dumped, and find what
makes its outputs degenerate on a fifth of frames - a zero or NaN `w`, a vertex buffer that
is not what it should be, or attributes that are not being written at all. The vertex stage
of this draw has never been examined.

### The UVs come from a vertex attribute

`480117a3b1123d65-vertex.metal` computes `out.outAttr0.xy` from `in.inAttr0.xyz` - a vertex
attribute read from a vertex buffer - after initialising both the position and the attribute
to zero. So a degenerate UV across the primitive means `in.inAttr0` is constant, and the
short list of ways that happens is:

- the vertex buffer bound for this draw does not hold what it should;
- the attribute is not being fetched at all, leaving the zero initialisation in place;
- the draw is issued with a vertex count or stride that makes every vertex read the same
  element.

Binding is the least likely of the three. `SetVertexBuffers` sits behind
`DirtyFlags.RenderPipeline`, so a stale binding was a real candidate - but
`RYUJINX_METAL_FULL_REBIND=1`, which sets that flag on every draw, measured 22.32% against a
21-23% baseline. Forcing the vertex buffers to be rebound every draw changes nothing, so what
is wrong is more likely the buffer's *contents* or the draw's parameters than which buffer is
attached.

**Next: capture `in.inAttr0` for this draw, split by outcome.** The vertex buffer is a
`BufferRef` like the constant buffers already sampled, and the same CPU-side read that
photographed `fp_c1` and `fp_c3` works on it. If it is constant on flat frames and varied on
normal ones, the fault is upstream in whatever fills that buffer; if it is varied on both,
the fetch itself is not happening and the fault is in the vertex descriptor or the draw call.

### The first vertex is identical by outcome, which does not settle it

Gated arm: luma 143, 11,399 frames, flat 20.79%. Reading the blit's vertex buffer at its
binding offset:

    cb31   flat [3.068894e-05, 0, 0.0078125, 0]   normal [3.068894e-05, 0, 0.0078125, 0]
           range flat and normal both zero-width on all four components

Bit-identical, no spread, on both outcomes. But this cannot decide the question and should
not be read as if it does. A degenerate UV means the attribute is the same *across the
vertices of the primitive*; reading the first vertex tells you nothing about whether the
second, third and fourth differ from it. A quad whose four vertices all carry vertex 0's UV
would produce exactly this measurement and exactly the fault.

**Next: read several vertices, not one.** Take four samples at the binding offset plus
multiples of the vertex stride and compare them to each other, split by outcome. If they are
identical to each other on flat frames and distinct on normal ones, the vertex data is
degenerate and the fault is upstream in whatever writes that buffer. If they are distinct on
both, the buffer is fine and the collapse happens in the fetch - the vertex descriptor's
stride or format, or a draw call whose parameters make every vertex read element zero.

The stride is what the probe is missing; it is available on the same `VertexBufferState` that
already yields the buffer and offset.

### The vertex data is not degenerate - the fetch is

Gated arm: luma 140, 11,399 frames, flat 21.77%. Four vertices read a stride apart from the
blit's vertex buffer and compared with each other:

    distinct vertices of four    flat 4.00    normal 4.00

All four differ, on white frames exactly as on good ones. So the buffer holds a proper quad
and nothing upstream has flattened it. Combined with the previous sections this closes the
loop tightly:

- the presented surface is written by a pass-through blit that cannot manufacture white;
- that blit's source texture holds a picture on the frames that present white;
- so the sampler is returning one texel for every pixel, and the UVs are constant;
- the attribute those UVs come from is *not* constant in memory - four distinct vertices.

**The collapse is therefore in the fetch, not the data.** Something between four distinct
vertices in a buffer and a constant attribute in the shader: the vertex descriptor's stride
or format for attribute 0, an index buffer that reads element zero four times, or draw
parameters - `vertexCount`, `baseVertex`, `firstIndex` - that make every invocation land on
the same element.

**Next:** record the vertex descriptor's stride and format for attribute 0, and the draw
call's parameters, split by outcome. All of them are on the encoder state at the same site
that already photographs the buffer, and every one is a small integer, so a difference will
be obvious rather than statistical. This is the last link in a chain that now runs
unbroken from the presented pixel back to the draw call.

### The stride is not it either

Gated arm: luma 140, 11,399 frames, flat 21.34%.

    fetch stride    flat 16.0, range [16,16]    normal 16.0

Sixteen bytes on every frame of both outcomes, with no spread. A zero stride would have made
every vertex read element zero and produced exactly this fault; it is not zero and it does not
vary. Note this also confirms the spread measurement was reading real vertices - it walked
0, 16, 32, 48 and found four distinct ones.

So within the fetch the remaining candidates are the attribute's format and offset in the
vertex descriptor, an index buffer that supplies element zero four times, and the draw call's
own parameters. All three are recordable at the same site and all are small integers.

Where this stands: the chain from the presented pixel back to the draw is unbroken and every
link is measured, not inferred. A pass-through blit writes the surface; its source holds a
picture on white frames; therefore its UVs are constant; the attribute behind them is four
distinct vertices in memory; the stride that walks them is 16 and constant. The collapse
happens between a correct buffer and a constant attribute, and three named places remain to
look.

### The draw parameters are identical - and they name the last suspect

Gated arm. One signature only, on both outcomes:

    blit draw   count=6 inst=1 first=0 indexed=1    flat 2,718   normal 8,680

Six indices, one instance, starting at zero. Two triangles - a fullscreen quad, which is what
a pass-through blit should be. The parameters do not vary, so they are not the collapse.

What they establish is that **the draw is indexed**, and that is the last candidate standing.
The vertex buffer holds four distinct vertices; the stride that walks them is 16; the draw
asks for six indices. If those six indices are all zero on a white frame, every invocation
fetches element zero, the attribute is constant across the primitive, the UVs collapse, and
the sampler returns one texel for the whole screen. That is the entire observed fault, and it
is consistent with every measurement in this document - a correct vertex buffer, a correct
stride, correct draw parameters, a correct source texture, and a uniform white frame.

**Next, and it is one measurement:** read the six indices for this draw and compare them by
outcome. The index buffer reaches the encoder the same way the vertex buffer does, so the
same CPU-side read that photographed four vertices works on it unchanged. If they are all
zero on flat frames and 0,1,2,0,2,3 on normal ones, the fault is found and the search moves
to whatever writes that index buffer.

### The index buffer is generated by this backend, from a quad-to-triangle pattern

    QuadsToTrisPattern = new IndexBufferPattern(_renderer, 4, 6, 0, [0, 1, 2, 0, 2, 3], 4, false);

Those are exactly the six indices predicted in the previous section. The blit draws a quad;
Metal has no quad primitive; so this backend generates an index buffer to turn four vertices
into two triangles. `count=6 inst=1 first=0 indexed=1` is that conversion, and it explains
why the draw is indexed at all when a fullscreen blit has no reason to be.

**That index buffer is this fork's own, generated per draw.** If it is stale, zero-filled or
recycled before the GPU reads it, every invocation fetches element zero - and everything
measured follows: a correct vertex buffer with four distinct vertices, a correct stride of
16, correct draw parameters, a correct source texture, and a screen filled with one texel.

Two things make this the strongest candidate the investigation has had. It is code only this
backend runs, which is the shape required by Vulkan never flashing on the same machine. And
it comes from the same allocator as the argument buffers, whose allocation strategy is the
one intervention that moved the rate by eleven points - the same class of hazard, in the same
place, reached from a completely different direction.

**Next:** read the six indices at the draw and compare them by outcome, then follow
`IndexBufferPattern` to how that buffer is allocated and how its lifetime is managed against
the command buffer. The deferred-deletion fix committed earlier covers `ScopedTemporaryBuffer`;
whether the pattern's buffer goes through the same path has not been checked.

### The index buffer's lifetime bug is real, and it is not the flash either

`IndexBufferPattern.GetRepeatingBuffer` grows its buffer by encoding a `CopyBuffer` from the
old one into the new and then calling `BufferManager.Delete` on the source - destroying it
while the GPU has not yet performed the copy. Fixed the same way as `ScopedTemporaryBuffer`,
with `DeleteWhenComplete`, at both sites.

Measured, and it does not move the flash:

    index pattern deferred deletion   luma 140, 11,399 frames, flat 22.37%
    today's gated baselines           20.4% - 23.7%

Squarely inside the range. The likely reason is the same as for the earlier lifetime fix: the
repeating buffer stops growing once it is large enough, so in steady gameplay the delete path
is almost never taken. Both fixes are correct and both are on paths that this workload
barely exercises.

**The measurement it was meant to shortcut is still outstanding.** Reading the six indices at
the draw, split by outcome, is the thing that confirms or kills the index hypothesis outright,
and a lifetime fix on a rarely-taken path cannot substitute for it. If they are 0,1,2,0,2,3
on white frames as well, the index buffer is innocent and the collapse is in the vertex
descriptor's attribute format or offset - the one remaining candidate in the fetch, and the
only link in the chain from presented pixel to draw call that has never been read.

### The blit does not use the generated index buffer at all

The probe that reads the six indices was placed in the quad-to-triangles branch - the path
that calls `GetIndexBufferPattern()` and `GetRepeatingBuffer`. On a gated arm (luma 140,
11,399 frames, flat 22.00%) it printed **nothing**: not one line, on either outcome. The hook
never fired.

So program `480117` does not go through that path. Its `indexed=1` comes from the guest's own
index buffer, bound normally, and this backend's quad conversion has nothing to do with it.
The previous section's reasoning - that the fullscreen blit is indexed *because* this backend
generates indices for it - was a wrong assumption dressed as a deduction, and the lifetime fix
committed alongside it, while a genuine bug, was never going to be relevant to this draw.

Recorded because the empty result is worth more than a number would have been: the hook was
checked for firing before its output was read, which is the discipline that has caught four
false negatives today.

**Next: hook the real index buffer.** It is bound through the ordinary index-buffer path, not
through `IndexBufferPattern`, and reading its first six entries by outcome is the same one
measurement - just in the right place. If they are 0,1,2,0,2,3 on white frames too, the fetch
collapse is in the vertex descriptor's attribute format or offset, which is then the only link
in the chain never read.

### The indices are correct on every white frame

Hooked in the right place this time - the guest's own index buffer, on the ordinary indexed
path - and the hook fired:

    blit indices [0,2,1,1,2,3]   flat 2,553   normal 8,844
    blit indices [0,0,0,0,0,0]   flat     0   normal     1

Two triangles of a quad, correct, on **every** flat frame. The all-zero case exists but
occurs once, on a normal frame, and never on a white one. The index buffer is innocent and
the index hypothesis is dead.

That empties the fetch of everything except one thing. The chain now reads, every link
measured: a pass-through blit writes the presented surface; its source texture holds a
picture on white frames; therefore its UVs are constant across the primitive; the vertex
buffer holds four distinct vertices; the stride walking them is 16; the draw asks for six
indices with correct values; and the attribute still arrives constant in the shader.

**The only unread link is the vertex descriptor** - the format and offset declared for
attribute 0, which is what turns bytes at a stride into a `float4` in `in.inAttr0`. A format
of zero size, an offset past the end of the stride, or a descriptor whose attribute is simply
absent all produce the same thing: the shader's zero-initialised attribute, unchanged, and
therefore constant. That initialisation is visible at the top of the vertex shader
(`out.outAttr0.x = 0.0f` before anything is computed), so a fetch that never happens is
indistinguishable from a fetch of zeros - which is exactly the observed fault.

Read `MTLVertexDescriptor`'s attribute 0 format, offset and bufferIndex at this draw, split
by outcome. It is the last link.

### The attribute probe measures pipeline creation, not the draw

    vertex attrib0 fmt=27 off=0 buf=0   flat 0, normal 46
    vertex attrib0 fmt=29 off=0 buf=0   flat 4, normal 14
    vertex attrib0 fmt=30 off=0 buf=0   flat 1, normal 8
    ... six rows, seventy-seven events in total

Seventy-seven, against 11,399 frames and 2,484 flat ones. `PipelineState` builds the vertex
descriptor when a *pipeline* is created and pipelines are cached, so these rows record which
frames happened to compile a pipeline - not which descriptor was in force at the blit's draw.
Splitting them by outcome is meaningless and the numbers must not be read as a result. Noted
here because the count made it obvious immediately; a probe firing 77 times where 11,399 were
expected is the same class of error as the four false negatives caught earlier today, and the
same check caught it.

The real measurement takes the descriptor of the pipeline *in use* at that draw. The blit's
pipeline is one specific cached object; reading its attribute 0 once and confirming it is
stable is enough to clear or convict it, and it does not need a per-frame split at all - a
descriptor that is wrong is wrong on every frame, which would make the fault constant rather
than intermittent. That argument alone makes the vertex descriptor an unlikely culprit, and
it is worth stating before spending another arm on it: **every remaining candidate in the
fetch is static, while the fault is intermittent at one frame in five.**

That mismatch is the most useful thing this section produces. The collapse is intermittent,
so whatever causes it must itself vary frame to frame - and of everything in the chain, the
only things that vary are the buffer contents (measured: correct), the indices (measured:
correct), and the draw parameters (measured: constant). The chain as drawn cannot produce an
intermittent fault, which means one of its links is being measured in a way that misses the
frames that matter - most plausibly the ones sampled at present rather than at the draw.

### The sampling-time hole is closed, and the contradiction is airtight

Armed the late-write tracker on the blit's *source* - the check that had only ever been wired
to the texel-fetch shader's input:

    source written after the blit read it   flat 1/2,496   normal 23/8,903

Essentially never, on either outcome. So the sample taken at present is what the blit read,
and "the source holds a picture on white frames" survives the objection raised against it.

Which makes the contradiction complete, with every link measured at the moment it matters:

- the blit's source holds a picture when the blit reads it - no late writers;
- the blit is `out.color0 = tex.sample(samp, float2(u, v))` and nothing else;
- its vertex buffer holds four distinct vertices, read at encode time, not at present;
- the stride walking them is 16, constant;
- the six indices are `0,2,1,1,2,3`, correct on every white frame;
- the draw is `count=6 inst=1 first=0 indexed=1`, one signature on both outcomes;
- and the output is a uniform fill on 22% of frames.

Everything this backend hands the GPU for that draw is correct on exactly the frames that
fail. That is now established at encode time, not inferred from a frame-end snapshot, which
was the last standing objection to it.

The remaining difference between this backend and MoltenVK, which never flashes on the same
machine and driver, is not in any of these values - it is in the shape of the command stream
around them: 610 render encoders per frame against 329, 5,283 `useResource` declarations
against 1,974, and store actions committed up front rather than deferred. Those are measured
and unexplained, and they are the only place left where the two backends still differ.

### MoltenVK-style deferred store actions break the picture

The machinery already existed - `_elideEmptyStore`, behind
`/tmp/ryujinx-metal-elide-empty-store`, defaulted off and never live in any measurement here.
Turning it on makes this backend defer its store actions the way MoltenVK does, which was one
of the three measured differences between them. The arm:

    deferred stores   luma 69, flat 86.49%   VOID

The gate rejects it, correctly, and the numbers say why: 86% of frames classified flat at a
mean luma of 69 is not a dark scene, it is a broken one. `FixupStoreActions` resolves an
`Unknown` store to `DontCare` when a pass drew nothing, and discarding those attachments
visibly destroys the rendering rather than merely changing when the store is decided.

So the resemblance to MoltenVK is superficial. MoltenVK defers the *decision* and then stores;
this code defers the decision and then sometimes discards. Copying the shape without the
policy makes things much worse, and the hot file must stay absent - it is, the run removes it.

That leaves the encoder count and the `useResource` volume as the two unexplained differences,
and both of them are downstream of the read-after-write split this fork turns on by default,
which is also the one change that ever halved the flash. Reducing them means undoing the only
thing that helped.

### Forcing the split before the blit changes nothing

`RYUJINX_METAL_SPLIT_BLIT=1` ends the render encoder before the blit that writes the presented
surface, whether or not `SamplesEarlierWrite()` notices the dependency - the worry being that
the split misses this draw and the blit then samples a texture still resident in tile memory
with no barrier, which on this hardware returns undefined content.

    forced split before the blit   luma 140, 11,399 frames, flat 21.64%
    today's gated baselines        20.4% - 23.7%

Inside the range. The blit is not reading tile-resident content without a barrier, and the
read-after-write split is not missing this draw.

That is the seventh intervention measured on a gated arm today, and the picture they make
together is consistent: **the flash rate does not respond to anything done at this draw.**
Deferred deletion, own allocation, forced full rebind, deferred stores, the reciprocal guard
in two forms, the index buffer's lifetime, and now a forced encoder split - one made it worse,
one broke the picture, and the rest did nothing measurable. The only change that has ever
moved it remains the read-after-write split itself, which halved it and is on by default.

### Full serialisation was already tried, and it reframes everything

An old result in this ledger, under-used all day: **full serialisation changed nothing.** If
ordering every pass against every other does not remove the flash, then no ordering
intervention will - which is consistent with today's seven, four of which were orderings of
one kind or another.

It also means the read-after-write split's 45% -> 24% was never about ordering. What else
does ending a pass do? It resolves the attachment out of tile memory into the texture. On
this hardware a render target's contents live in tile memory until the pass ends; a later
pass that samples that texture reads the *texture*, and whether the texture holds what the
tile held depends on the store having happened.

So the split's real effect is plausibly that it forces stores that would otherwise be
deferred or elided - and the residual 20% would be the frames where the blit samples a
texture whose contents are still, in some sense, not there. That is a different axis from
both ordering and from the values this document has spent the day measuring, and it explains
why every value came out correct: the bytes Ryujinx wrote are correct, the descriptors are
correct, and the texture the GPU actually reads is simply not the one those bytes went into
yet.

**This supersedes the "frame structure" direction proposed in the previous section.** The
encoder count is not interesting in itself; what matters is which of those encoders store
their attachment and which do not. `FixupStoreActions` already exists and already
distinguishes those cases - it is the code that turns an `Unknown` store into `DontCare` for
a zero-draw pass, and turning it on broke the picture entirely, which is itself evidence that
stores on this path are load-bearing in a way nobody has mapped.

Next: log, per frame and split by outcome, the store action actually resolved for the blit's
source texture's last writing pass. Not how many encoders there were - what happened to that
one attachment.

Refuted immediately, from data already in this document. The command-stream probe measured
Ryujinx's colour attachments at 116,576 `Store` against 960 `Unknown` per 120 frames, and the
Unknowns are the GUI's own Metal traffic - `_elideEmptyStore` is off, so this backend resolves
every attachment to `Store` unconditionally. Every pass stores. There is no population of
passes that skip the store, so "the contents have not landed" has nothing to stand on unless
the store itself is ineffective, which is a driver claim, and MoltenVK reaches the same driver
without flashing.

So the reframing above is wrong too, and the measurement it proposed would return a constant.
Recorded rather than deleted because the sequence matters: a direction was proposed from a
real observation, checked against data already collected, and discarded before spending an
arm on it. That check cost nothing and would have cost a ten-minute run and a
confidently-wrong conclusion.

What survives from it is narrower and still true: the read-after-write split halved the flash
and full serialisation did not, so the split's effect is not ordering *and* not stores. Those
two facts together are the sharpest unexplained thing in this document, and no hypothesis
offered today accounts for both.

### Full serialisation does reduce the flash - the old result was wrong

Re-measured under the gate, with the intervention verified to engage: 2,506 passes for 2,506
draws, one pass per draw, against the usual ~600.

    full serialisation   luma 119, 4,199 frames, flat 16.79%
    today's baselines    20.4% - 23.7%

About five points below the range, roughly eight standard errors. **So "full serialisation
changed nothing" is false.** That result predates the admissibility gate and the drive-in's
draw-count check, and today five arms turned out to be menus or dark scenes reporting a clean
zero - the suspicion that it was one of them was correct.

Two caveats, stated because this arm is weaker than the others: luma 119 sits at the low edge
of the gate, and a darker scene depresses the rate on its own; and one pass per draw is slow
enough that the window only reached 4,199 frames. The direction is real, the magnitude is
soft.

What it means is that the ordering axis was never properly closed, and the trend across the
three points is monotonic - no split 45%, read-after-write split 24%, full serialisation 17%.
More splitting, less flashing, and it does not reach zero. That last part is the important
one: **serialising every draw against every other still leaves one frame in six white**, so
ordering is not the whole fault even though it is part of it.

The section above that concluded "the split's effect is neither ordering nor stores" is
therefore half wrong: it is ordering, at least in part. The reasoning was sound and its
premise - the old full-serialisation result - was not. Corrected here rather than deleted,
because the same premise underpins the "sharpest unexplained thing" claim, which no longer
stands.

### What full serialisation cannot reach: ordering between command buffers

Splitting a pass ends an encoder inside one command buffer. Ordering *between* command
buffers is not affected by it at all - that is decided by the order they are committed to the
queue. And the command-stream probe already measured how many there are: **3,842 commits per
120 frames, thirty-two command buffers per frame** on this backend, against twenty-four on
MoltenVK.

`CommandBufferPool` hands command buffers to whichever thread rents one. If a thread encodes
work that is logically earlier and commits after the render thread's buffer, the GPU runs them
in commit order and the dependency is inverted - and no amount of intra-buffer splitting
touches that. It is exactly the shape needed to explain a residual that survives serialising
every draw against every other inside the buffer.

It also fits the one intervention that made things *worse*: giving argument buffers their own
allocation cost eleven points, and per-draw allocation churn changes which thread is doing
what and when buffers are rented and committed.

**Next: measure commit order against rental order.** `CommandBufferPool` knows both; recording
the frames where they disagree, split by outcome, is one counter and needs no new machinery.
If flat frames are the ones where a buffer committed out of rental order, the fault is found
and the fix is to commit in order - which is what MoltenVK gets for free, because
`kMVKQueueCountPerQueueFamily = 1` means Vulkan on this platform never has a second queue to
race with.

### Out-of-order commits happen, and they are not the flash

`CommandBufferPool` now stamps each buffer with a rental sequence and compares it against the
order they are committed. Gated arm: luma 139, 11,399 frames, flat 21.43%.

    out-of-order commits per frame   flat 0.54   normal 0.58

They happen - about once every two frames - and they are not correlated with the outcome; if
anything they are slightly rarer on white frames. So the residual that survives full
serialisation is not commit-order inversion between command buffers either.

Worth keeping the counter: half an inversion per frame is a real ordering hazard in general,
and it is now measured rather than assumed. It is simply not this fault.

That is the ninth gated intervention or measurement aimed at the ordering axis and its
neighbours today. The monotonic trend across split levels is real (45%, 24%, 17%) and
unexplained by any specific ordering violation that has been looked for: not read-after-write
within an encoder, not tile-memory residency, not stores, not cross-command-buffer commit
order. Something about ending passes reduces the fault without any of the named mechanisms
accounting for it.

---

## RESUME HERE (2026-08-16, superseding the block above)

**State.** Not fixed. Rate 20-24% on gated arms. Three real bugs fixed today, none of them
the flash. The earlier RESUME block and the divide-by-zero memory are both refuted - read
this one.

**The single most important open fact.** Splitting passes reduces the flash monotonically:

    no split                    45%
    read-after-write split      24%   (default on)
    full serialisation          17%   (verified engaged: 2,506 passes / 2,506 draws)

and **no named mechanism accounts for it.** Ordering within an encoder, tile-memory
residency, store actions, and cross-command-buffer commit order have each been measured and
excluded. Whatever ending a pass does that helps, it is not any of those. Explaining this is
worth more than another candidate: it is the only lever that has ever worked and nobody knows
why it works.

**What is established, each measured at the moment it matters.** The presented surface is
written by a pass-through blit (`480117a3b1123d65`) that samples one texture and writes it
out unchanged. On white frames: its source holds a picture (19 distinct values in 25 adjacent
texels), nothing writes that source after it reads it (1/2,496), its vertex buffer holds four
distinct vertices read at encode time, the stride is 16, the six indices are `0,2,1,1,2,3`,
the draw is `count=6 inst=1 first=0 indexed=1`, and the output is a uniform fill at min 247
max 254 sd 2.0. Everything this backend hands the GPU for that draw is correct on exactly the
frames that fail.

**Do not redo these.** Nine gated interventions and measurements, all null or worse:
deferred deletion of temporary buffers, argument buffers on their own allocation (+13 points,
worse), forced full rebind, MoltenVK-style deferred stores (breaks the picture), the
reciprocal guard in both an exact-zero and a threshold form, the index pattern buffer's
lifetime, a forced encoder split before the blit, and commit-order inversion (happens 0.5x
per frame, uncorrelated). The table is also in memory as `metal-flash-gated-interventions`.

**Method that must not be dropped.** Every arm goes through `tools/arm_valid.py` before its
number is quoted; it caught five false negatives today, four of them arms that were measuring
a menu screen. `drive_in.sh` verifies it reached gameplay by draws per frame (~2,500 against
~340 in menus). Any probe must be checked for firing before its output is read - that caught
two more.

### The one thing ending a pass changes that has not been excluded

Residency is per-encoder. Ending a pass forces every resource to be declared to the new
encoder again, and `UseRenderResources` deduplicates *within* an encoder via
`_applied.IsResident` - so the only way a resource gets re-declared is a new encoder. More
splits therefore means more frequent re-declaration, and that is the one consequence of
ending a pass that the nine measurements have not touched.

It is not the same as the forced-rebind arm, which was null: `RYUJINX_METAL_FULL_REBIND=1`
rebuilds *bindings* and argument buffers every draw, but the encoder is unchanged, so
`IsResident` still suppresses the repeat `useResource`. Rebinding and re-declaring residency
are different things and only the first has been tested.

**The experiment that separates it:** re-issue `useResource` for the bound set periodically
inside a long encoder, without ending the pass - clear `_applied`'s residency memory every N
draws. If that reproduces the split's benefit, the mechanism is residency going stale within
an encoder, which is a driver-behaviour claim but a testable one, and it would explain the
monotonic curve without any of the ordering mechanisms that have been excluded. If it does
nothing, the split's benefit is not residency either, and the list of things ending a pass
changes is then empty - which would itself be a strong statement.

### Residency re-declaration is not it either, and the list is now empty

`RYUJINX_METAL_REDECLARE=1` clears the per-encoder residency memory before every draw, so
every resource is declared again without the pass ending. Gated arm, and the isolation held -
passes stayed at 599 per frame, so no extra splitting crept in:

    residency re-declared every draw   luma 140, 11,399 frames, flat 22.13%
    today's baselines                  20.4% - 23.7%

Null. So splitting does not help because it re-issues `useResource`.

**That empties the list.** Ending a pass is known to do five things, and each has now been
measured: it orders work (full serialisation helps but specific ordering violations are all
excluded), it resolves stores (always `Store` here, unconditionally), it moves contents out
of tile memory (a consequence of the store, which always happens), it re-declares residency
(just excluded), and it rebuilds bindings and argument buffers (the forced-rebind arm was
null). Splitting reduces the flash from 45% to 24% to 17% and none of the five accounts for it.

That is where this stands and it is a genuine result, not a shrug: the only lever that works
does so through a mechanism that is not among the effects the API says it has. Either one of
the five is being measured wrongly - and each was checked for engagement, which is what caught
seven false negatives today - or ending a pass has a sixth effect on this hardware that is not
in the documentation. The second is worth taking seriously now that the first has been worked
through this thoroughly, and it is the kind of claim a driver report can be built on: not "our
frames go white" but "pass boundaries change the outcome and we can account for every
documented effect of them."

---

## The precise cross-backend comparison, 2026-08-16

Both backends watched with the same probe, both verified in gameplay, texture logging
uncapped this time (the earlier attempt hit a 4,000-line cap at different points in each
session and was worthless). Metal logged 15,819 descriptors, MoltenVK 12,998.

    descriptors seen only on Metal    330
    descriptors seen only on Vulkan     0
    descriptors seen on both           57

And at the scene's own size, which is what matters:

    METAL    800x448  fmt 92  usage 0x17   x35      RG11B10Float colour target
             800x448  fmt 70  usage 0x17   x28
             800x448  fmt 115 usage 0x17   x2
             800x448  fmt 250 usage 0x5    x9       depth
    VULKAN   800x448  fmt 250 usage 0x5    x9       depth only
             1600x896 fmt 252 usage 0x5    x1

**MoltenVK never creates a colour scene target through `newTextureWithDescriptor` at all.**
Only depth appears. Two things follow, and both are concrete differences that no measurement
in this document had reached:

- **MoltenVK almost certainly allocates its colour textures from an `MTLHeap`**, whose
  `newTextureWithDescriptor:` is a different selector this probe does not hook. Ryujinx-Metal
  was checked earlier and has no `MTLHeap` anywhere - that check established our side only,
  and the other side was never looked at.
- **The usage bits differ.** Ryujinx-Metal asks for `0x17` on scene targets -
  ShaderRead | ShaderWrite | RenderTarget | PixelFormatView - where MoltenVK's are `0x5`,
  ShaderRead | RenderTarget. On Apple GPUs both `ShaderWrite` and `PixelFormatView` disable
  lossless colour compression and change the layout the driver chooses. This backend is
  asking for a materially different texture than MoltenVK asks for, for the same surface.

That second one is testable directly and cheaply: drop the bits that are not needed and see
whether the flash rate moves. It is the first cross-backend difference found that is both
measured and under our control.

### Matching MoltenVK's usage bits does not help

`RYUJINX_METAL_LEAN_USAGE=2` drops `ShaderWrite` and `PixelFormatView` from scene-class
textures, leaving `ShaderRead | RenderTarget` - byte for byte the `0x5` MoltenVK asks for.

    scene textures at usage 0x5   luma 140, 11,399 frames, flat 23.44%
    today's baselines             20.4% - 23.7%

Top of the range, no improvement. So the usage difference is real and is not the fault, and
the compression and layout consequences it carries are not what separates the two backends.

**The other half of the finding survives and is the larger one.** MoltenVK created *no colour
scene target at all* through `newTextureWithDescriptor` across 12,998 logged descriptors -
only depth. A backend that renders the game must be creating colour targets somewhere, so it
is creating them through a selector this probe does not hook, and the obvious candidate is
`-[MTLHeap newTextureWithDescriptor:]`. The check done earlier in this document - "there is no
MTLHeap in the backend" - was run against *our* source and says nothing about MoltenVK's.

If MoltenVK places colour targets in a heap and this backend gives each one a standalone
allocation, that is a difference in how the memory itself is obtained, not in the flags on
it - and the one intervention that ever made the flash dramatically *worse* was changing an
allocation strategy, by eleven points.

**Next, and it is one probe:** hook `-[MTLHeap newTextureWithDescriptor:]` and
`-[MTLDevice newHeapWithDescriptor:]` in `tools/mtlspy.m`, re-run the Vulkan arm, and see what
appears. If MoltenVK's colour targets are heap-allocated, that is the first structural
difference in memory management ever found between the two, and it is reachable from our side.

### MoltenVK allocates heaps - 17,077 of them - and places its textures inside

Hooked `-[MTLDevice newHeapWithDescriptor:]` and the returned heap class's
`newTextureWithDescriptor:`. The Vulkan arm, in gameplay:

    heap class hooked        AGXG13XFamilyHeap
    heaps created            17,077
    textures from heaps           0

Seventeen thousand heaps and not one texture created through the selector this probe hooked.
That combination names the mechanism precisely: MoltenVK is placing its textures with
**`newTextureWithDescriptor:offset:`**, the variant that puts a texture at an offset inside an
existing allocation, which is how a `VkImage` is bound into a `VkDeviceMemory`. The hook was
on the wrong selector - the right one takes the offset.

The structural difference is nevertheless established, and it is the one this investigation
has been looking for since the command-stream comparison:

- **MoltenVK**: one heap per memory object, textures *placed* inside it at offsets.
- **Ryujinx-Metal**: every texture a standalone `Device.NewTexture`, no heap anywhere.

This is a difference in how the memory is obtained, not in the flags on it - and the usage-bit
arm has already shown the flags are not the fault. Placed heap resources behave differently
from standalone ones in exactly the areas this fault lives in: the driver's freedom to choose
a layout, when a resource's contents are defined, and what aliasing is permitted within the
allocation.

**Next: re-hook with the offset selector** (`newTextureWithDescriptor:offset:`, and its
buffer-backed sibling `-[MTLBuffer newTextureWithDescriptor:offset:bytesPerRow:]`) to confirm
the placement and read the descriptors MoltenVK actually uses. Then the question that matters:
whether giving this backend heap-placed scene targets changes the flash. That is a real change
rather than a flag flip, but it is now aimed at a measured difference rather than a guess.

### Correction: the descriptors are identical. Only the allocation differs.

Hooking `newTextureWithDescriptor:offset:` made MoltenVK's colour targets appear at once:
**6,270 heap-placed textures**, none buffer-placed. And at scene size they are the same
descriptors this backend uses, usage bits included:

    MoltenVK (placed in a heap)      800x448 fmt 92  usage 0x17   x35
                                     800x448 fmt 70  usage 0x17   x28
                                     800x448 fmt 115 usage 0x17   x2
    Ryujinx-Metal (standalone)       800x448 fmt 92  usage 0x17   x35
                                     800x448 fmt 70  usage 0x17   x28
                                     800x448 fmt 115 usage 0x17   x2

**The usage-bit difference reported in the previous section does not exist.** That comparison
put Metal's device textures against Vulkan's device textures, and Vulkan's device textures are
only depth - every colour target was inside a heap where the probe could not see it. The
`0x17` against `0x5` reading was an artifact of a blind spot, and the `LEAN_USAGE=2` arm that
came back null was testing a difference that was never there.

What survives is sharper for it. Same size, same format, same usage, same storage mode - and:

- **MoltenVK**: 17,077 heaps, textures placed inside them at offsets.
- **Ryujinx-Metal**: no heap anywhere, every texture its own `Device.NewTexture`.

Identical descriptors, different memory. That is now the only measured difference left between
a backend that flashes and one that does not.

---

## The root cause hypothesis: hazard-tracking granularity

Three pieces, two measured here and one quoted from Apple's headers, and they fit.

**Measured.** MoltenVK's heaps are `type 1` (`MTLHeapTypePlacement`) and `hazard 2`
(`MTLHazardTrackingModeTracked`) - 8,548 with `storageMode 0`, 5,123 with `storageMode 2`.
It does not accept the heap default; the default for a heap is *untracked*, and MoltenVK
explicitly opts in.

**Documented**, `MTLHeap.h:150-160`:

> When a resource on a hazard tracked heap is modified, reads and writes from any other
> resource on that heap will be delayed until the modification is complete. Similarly,
> modifying heap resources will be delayed until all in-flight reads and writes from
> resources suballocated on that heap have completed.

and `MTLResource.h:287-293`:

> Resources created from heaps are by default untracked, whereas resources created from the
> device are by default tracked.

**So the two backends track hazards at different granularities.** MoltenVK's textures sit in
tracked placement heaps, where touching any one of them stalls reads and writes of *every
other resource in that heap*. This backend's textures are standalone device allocations,
tracked individually and precisely.

Per-resource tracking is the more correct of the two and it is what exposes the fault. Whole-
heap tracking is massively conservative - MoltenVK does not need to detect a dependency for
it to be honoured, because the heap serialises against everything else it holds. That is a
large amount of accidental synchronisation that this backend does not get.

**It accounts for the evidence that nothing else has.**

- The monotonic curve. More splitting means more synchronisation, and the rate falls 45% ->
  24% -> 17%. MoltenVK gets far more synchronisation than any of those for free, and sits at
  0%. The curve is the same axis, seen from the other end.
- Why `MTLFence` did nothing. The fence was placed on the read-after-write dependencies
  `SamplesEarlierWrite()` detects. A dependency it fails to detect gets no fence - and those
  are exactly the ones a whole-heap barrier would catch and a precise tracker would miss.
- Why every value measured correct. Nothing is wrong with the bytes, the descriptors, the
  indices or the coordinates. The GPU reads a texture before another encoder's write to it
  has landed, and every CPU-side value is correct at the moment it is read.
- Why full serialisation still leaves 17%. It orders passes within a command buffer; heap
  tracking also covers dependencies across them.

**The test.** SharpMetal preview21 already binds `MTLHeap`, `MTLHeapDescriptor`,
`MTLHeapType.Placement`, `HazardTrackingMode`, `NewTexture(descriptor, offset)`,
`HeapTextureSizeAndAlign`, `MakeAliasable` and `MTLTexture.Heap`/`HeapOffset` - no new
bindings are needed. The change is one allocation site (`Texture.cs:95`, the only standalone
`Device.NewTexture` in the backend; the other four sites are views) plus a placement
suballocator and a lifetime change: heap memory is not reclaimed by releasing the texture, so
`DisposableTexture.Dispose` must call `MakeAliasable` first, driven off the existing `Auto`
refcount that already waits for the GPU.

If scene-class textures allocated from one tracked placement heap take the flash to zero, the
root cause is hazard-tracking granularity and the fix is either that, or finding the
dependencies `SamplesEarlierWrite()` misses and fencing them properly.

### Heap placement does not fix it - the hazard-granularity hypothesis is refuted

Implemented the allocator MoltenVK's behaviour pointed at: a `MTLHeapTypePlacement` heap with
`MTLHazardTrackingModeTracked` set explicitly, textures placed with
`newTextureWithDescriptor:offset:`, freed through `MakeAliasable` off the existing `Auto`
refcount. `RYUJINX_METAL_TEXTURE_HEAP=2` places every private colour texture at least 64x64.

    heap-placed textures   8,600   (logged, engagement proven)
    result                 luma 140, 11,399 frames, flat 21.77%
    baselines              20.4% - 23.7%

Inside the range. Replicating MoltenVK's allocation strategy, on the textures that matter,
with tracking explicitly enabled, changes nothing.

**Two false negatives had to be cleared first, and both were caught by checking engagement
rather than reading the number.** The first arm gated the heap on `IsSceneClass`, which
demands `Width >= 1000` while the repro runs at 800x448 - it placed nothing. The second
passed `=2` to an `Enabled` that compared against `"1"`, so the branch was dead. Both returned
a clean-looking 22%. Only the third arm logged placements, and only its number means anything.

So the difference that the precise comparison found - identical descriptors, different
allocation - is real and is not the cause. That closes the last measured difference between
the two backends.

What is left after it: MoltenVK and this backend now agree on texture size, format, usage
bits, storage mode, hazard tracking mode and allocation strategy, and one flashes while the
other does not. The remaining differences are in the command stream's shape - 610 encoders
per frame against 329, and store actions committed up front rather than deferred - and the
deferred-store arm broke the picture outright when tried. Nothing in the descriptor or the
memory accounts for it.

---

## The difference: MoltenVK issues 167 fence pairs per frame. This backend issues none.

Hooked `updateFence:afterStages:` and `waitForFence:beforeStages:` - the one form of
synchronisation this probe had been blind to. Both backends, same instrument, both in gameplay:

    VULKAN   fence up 20,094   wait 20,094   per 120 frames   (~167 pairs per frame)
    METAL    fence up      0   wait      0                     (none, ever)

MoltenVK expresses every Vulkan barrier it cannot express by ending an encoder as a real
GPU-side `MTLFence`, and it does so 167 times a frame. This backend issues zero and relies
entirely on Metal's automatic hazard tracking.

**That is the structural difference, and it is the axis every other measurement has been
pointing at.** More synchronisation means less flashing: no split 45%, read-after-write split
24%, full serialisation 17%, and MoltenVK - which fences 167 times a frame on top of its own
encoder splits - 0%. The curve and the fence count are the same story from both ends.

It also accounts for the two results that looked contradictory:

- **The `MTLFence` arm that did nothing.** It placed a fence only where
  `SamplesEarlierWrite()` detects a read-after-write, which fires a few hundred times per
  frame at most and only for dependencies this backend already knows about. MoltenVK fences
  every barrier the guest's Vulkan usage implies, whether or not any heuristic spotted it.
- **Why every value measured correct.** Nothing is wrong with the bytes. The GPU reads a
  texture before another encoder's write to it has completed, and no fence tells it to wait.

Automatic hazard tracking is supposed to cover this, and with argument buffers it covers only
what `useResource` declares. Both backends declare read and read-write and neither declares
write-only - so a write performed as a *render target* and read later through an argument
buffer is a dependency automatic tracking has no declaration for. MoltenVK does not depend on
it being covered; it fences.

**The test, and it is direct:** make the read-after-write split issue a fence at every split
instead of only ending the pass, and widen what counts as a dependency until the fence count
approaches MoltenVK's. `RYUJINX_METAL_RAW_FENCE=1` already exists and already pairs
`updateFence` on the writing encoder with `waitForFence` on the reading one - it was measured
once, before the admissibility gate existed, and that measurement should not be trusted.

### Matching MoltenVK's fence volume does not reach zero

`RYUJINX_METAL_RAW_FENCE=1`, with engagement proven by the same probe that counted MoltenVK's:

    fence pairs per 120 frames   this backend 21,928    MoltenVK 20,094

More than MoltenVK's, from the same instrument. And the flash does not go away:

    flat 461 / 2,999 = 15.4%   luma 107   flat frames min 250 max 254 sd 1.1

**The arm is VOID** - 2,999 frames is below the gate's floor, because fencing is slow enough
that the window covered far less time, and luma 107 is darker than the repro's 138, which
depresses the rate on its own. The number must not be quoted as 15.4%. What can be said is
that white frames still occur and still look identical: a uniform fill at min 250, max 254,
spread of four.

Read against the rest of the curve, 15.4% sits where full serialisation's 17% sits - both are
"much more synchronisation", both land in the mid teens, and neither reaches zero. So fence
volume alone is not what separates the backends either. Matching MoltenVK's count of barriers
is not the same as matching *which* dependencies they cover, and this backend still only
fences where `SamplesEarlierWrite()` looks.

**Next, and it needs a proper arm before anything is concluded:** re-run the fence arm long
enough to pass the gate, in the daylight scene. If it lands solidly below the 20-24% band the
direction is confirmed and the remaining work is widening which dependencies get a fence; if
it lands inside the band, fence volume is excluded and only the coverage question remains.

### The correlator cannot measure a fenced arm

A second fence arm with the sampling window stretched from 330s to 520s classified **exactly
2,999 frames again** - the same number, not a larger one. That is not a short run; it is the
correlator stopping.

The stale-slot drop was the obvious explanation and **the data in the same command refutes
it**: `dropped=0`. Nothing was discarded. The independent Metal-level probe shows the game
presented **6,000 frames** on that arm while the correlator classified 2,999 and stopped
reporting after its fifth interval - so the emulator kept running and the correlator simply
stopped classifying, for a reason not yet identified. It is not the drop path, and the
explanation first written here was wrong.

**So the fence hypothesis cannot be measured with the current correlator, and neither fence
arm's rate means anything** - not the 15.4%, not any number from them. The engagement figures
are still sound, because those come from the independent Metal-level probe: 21,928 and 23,429
fence pairs per 120 frames against MoltenVK's 20,094. Volume is matched; the effect is
unmeasured.

**What has to happen before this line can continue:** the correlator needs a classification
path that does not depend on the sampler's fence signalling in step with the frame - either a
longer ring, a timeout that classifies on the CPU after a fixed delay, or sampling through a
separate command buffer whose fence the intervention does not touch. Until then any fenced arm
will report a frozen 2,999 and look like a short run.
