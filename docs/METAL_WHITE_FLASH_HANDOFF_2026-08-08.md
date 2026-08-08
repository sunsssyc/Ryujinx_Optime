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
