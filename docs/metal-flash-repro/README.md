# Standalone Metal reproducer for the TOTK white flash

No emulator, no game. It reproduces only the *shape* of the workload the flash
lives in, so the question can be asked in seconds instead of in ten-minute
daylight windows against a save file.

```
clang -fobjc-arc -framework Foundation -framework Metal -O2 flashrepro.m -o flashrepro
FRAMES=3000 ./flashrepro
```

A frame counts FLAT when the composite's output is uniform near-white while the
scene target demonstrably holds a constant, using FlashGuard's own calibrated
criterion (25-point grid, at least 6 samples over luma 235). Exit status is 1 if
any frame is flat, so it works as a bisection oracle.

`constant N` in the output is the harness self-check: if it is ever zero the
composite never read the constant at all, and the flat count means nothing.

## Knobs

| Variable | Default | What it models |
|---|---|---|
| `FRAMES` | 2000 | frames to run |
| `PASSES` | 200 | render passes per frame (the real frame is ~197) |
| `DRAWS` | 12 | draws per pass (~12.8 real) |
| `W` `H` | 1600x896 | the scene target, gameplay resolution |
| `FMT` | `rg11b10` | also `rgba16f`, `rgba8` |
| `USAGE` | `full` | `full` adds PixelFormatView+ShaderWrite as the backend does, which takes the surface off the compressed path; `lean` omits them |
| `ARGBUF` | 1 | bind through a Tier 2 argument buffer + useResource, as the backend does |
| `REVISIT` | 1 | alternate attachments so passes revisit them |
| `ALIAS` | 0 | write some scene passes through an RGBA8Unorm view of the same storage - the guest genuinely aliases it that way |
| `DEPTH` | 0 | attach depth to every pass |
| `CHURN` | 0 | allocate and drop N scene-sized textures per frame (dynamic resolution) |
| `TRIS` | 0 | N scattered triangles per scene draw, to load the tiler toward a partial render |
| `SPLITCB` | 1 | split each frame across N command buffers, as auto-flush does |
| `INFLIGHT` | 4 | frames in flight; results are read off the fence, never waited on |

## Result so far: does not reproduce

Eleven configurations, about ten thousand frames, on Apple M1 Max / macOS 26.5:

```
plain shape (3000 frames)                        flat 0
ALIAS=1 / DEPTH=1 / CHURN=3 and their combinations   flat 0
TRIS=20000 / TRIS=100000                         flat 0
SPLITCB=4                                        flat 0
TRIS=100000 SPLITCB=4 DEPTH=1 ALIAS=1 CHURN=2    flat 0
```

The harness self-check passes in every run (the composite reads the constant on
essentially every frame), so these are real negatives, not a broken instrument.

**This weakens the "generic driver read fault" reading.** A plain Metal program
holding every property we had identified - persistent uncompressed RG11B10Float
target, ~200 passes a frame, argument buffers, explicit texel fetches, a
format-aliased view of the same storage, depth attachments, allocation churn,
tens of millions of triangles a frame, split command buffers, frames in flight -
never once returns white. So either the model is still missing a condition, or
the fault involves emulator state rather than the workload shape.

The sharpest remaining fork, and the next thing to run:

  Record what the composite draw's argument buffer actually contains - the
  MTLResourceID handed to the shader - per frame slot, classify the frame a few
  presents later, and split by outcome.

  - flat frames carry a different resource id -> the binding is wrong, it is
    ours, and it is fixable
  - identical on both -> the driver really does return white for a correctly
    bound, correctly filled texture, and the gap is in this reproducer

Until that fork is resolved, "driver bug" is a hypothesis with one positive
experiment behind it (constant injection in the emulator) and one negative
(this program), not a conclusion.
