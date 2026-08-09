using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Tests one falsifiable claim: flat (white) frames are the frames in which a
    /// guest-to-host upload (SetData) or a texture-to-texture copy landed on a
    /// scene-sized texture mid-frame - the shared layer resurrecting stale guest
    /// memory into a surface the frame had already rendered.
    ///
    /// Per frame it accumulates, in memory only: how many SetData/CopyTo writes hit
    /// large textures, whether the written texture had already been a colour
    /// attachment this same frame (the resurrection signature), and which shapes.
    /// At present it samples 25 pixels of the present source into a slot of a ring
    /// buffer - same grid and threshold FlashGuard's calibrated detector uses - and
    /// classifies the slot several presents later when its command buffer has long
    /// completed. Nothing is waited on, and nothing is logged per frame; a per-frame
    /// present log is known to close the race window, a per-frame GPU sync is known
    /// (from the CPU-sampling FlashGuard era, 35% flat with it active) not to.
    ///
    /// The join is a 2x2 table reported once per interval: P(big write | flat) vs
    /// P(big write | normal), plus the same split per shape. Hypothesis dead if the
    /// columns match; culprit named by shape if they do not.
    ///
    /// RYUJINX_METAL_UPLOAD_CORR=1 enables. No MSL is touched, so no CodeGenVersion
    /// bump is due.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class UploadCorrelator
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_UPLOAD_CORR") == "1";

        private const int GridSide = 5;
        private const int Pixels = GridSide * GridSide;
        private const int BytesPerPixel = 4;
        private const int Slots = 8;

        // FlashGuard's calibrated flat-frame criterion (300 captured frames of real
        // gameplay): a flat frame has >= 8 of 25 samples saturated, an ordinary one
        // at most 4. Same numbers so the labels mean the same thing they meant there.
        private const float SaturatedLuma = 235f;
        private const int SaturatedNeeded = 6;

        // Below this a texture is a shadow tile, LUT or UI atlas, not a stage of the
        // scene chain. The scene runs 1600x896 at full dynamic resolution and stays
        // far above this even scaled down; the 260x260 background-readback texture
        // stays below it.
        private const int MinPixels = 512 * 512;

        private struct Slot
        {
            public bool Valid;
            public FenceHolder Fence;
            public long Frame;
            public int Uploads;
            public int BigUploads;
            public int BigUploadsOntoRt;
            public int BigCopies;
            public int BigCopiesOntoRt;
            public ulong[] Shapes;
            public int ShapeCount;
        }

        private static MTLBuffer _buf;
        private static readonly Slot[] _slots = new Slot[Slots];

        // Accumulators for the frame currently being encoded. Everything here runs on
        // the render thread (SetData uses the main pipeline, attachments bind at
        // encoder creation, present is the boundary), so plain fields suffice.
        private static readonly HashSet<IntPtr> _attachedThisFrame = new();
        private static readonly ulong[] _frameShapes = new ulong[8];
        private static int _frameShapeCount;
        private static int _frameUploads;
        private static int _frameBigUploads;
        private static int _frameBigUploadsOntoRt;
        private static int _frameBigCopies;
        private static int _frameBigCopiesOntoRt;

        private static long _frame;
        private static long _dropped;

        // The join. "With" means at least one big upload/copy that frame.
        private static long _flatFrames;
        private static long _normalFrames;
        private static long _flatWithUpload;
        private static long _normalWithUpload;
        private static long _flatWithUploadOntoRt;
        private static long _normalWithUploadOntoRt;
        private static long _flatWithCopy;
        private static long _normalWithCopy;
        private static long _flatWithCopyOntoRt;
        private static long _normalWithCopyOntoRt;

        // Shape key: width<<40 | height<<16 | (kind: 1 upload, 2 copy)<<8 | ontoRt.
        private static readonly Dictionary<ulong, (long Flat, long Normal)> _shapeStats = new();

        private const int ReportInterval = 600;

        public static void Init(MTLDevice device)
        {
            if (!Enabled)
            {
                return;
            }

            _buf = device.NewBuffer(Slots * Pixels * BytesPerPixel, MTLResourceOptions.ResourceStorageModeShared);

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"uploadcorr armed: ring={Slots} grid={GridSide}x{GridSide} satLuma={SaturatedLuma} need={SaturatedNeeded} minPixels={MinPixels}");
        }

        public static void NoteAttachment(Texture target)
        {
            if (!Enabled || target == null)
            {
                return;
            }

            _attachedThisFrame.Add(target.CanonicalPtr);
        }

        public static void NoteUpload(Texture target)
        {
            Note(target, isCopy: false);
        }

        public static void NoteCopyIn(Texture target)
        {
            Note(target, isCopy: true);
        }

        private static void Note(Texture target, bool isCopy)
        {
            if (!Enabled || target == null)
            {
                return;
            }

            if (!isCopy)
            {
                _frameUploads++;
            }

            long pixels = (long)target.Info.Width * target.Info.Height;

            if (pixels < MinPixels)
            {
                return;
            }

            bool ontoRt = _attachedThisFrame.Contains(target.CanonicalPtr);

            if (isCopy)
            {
                _frameBigCopies++;

                if (ontoRt)
                {
                    _frameBigCopiesOntoRt++;
                }
            }
            else
            {
                _frameBigUploads++;

                if (ontoRt)
                {
                    _frameBigUploadsOntoRt++;
                }
            }

            ulong shape = ((ulong)(uint)target.Info.Width << 40)
                | ((ulong)(uint)target.Info.Height << 16)
                | ((isCopy ? 2ul : 1ul) << 8)
                | (ontoRt ? 1ul : 0ul);

            for (int i = 0; i < _frameShapeCount; i++)
            {
                if (_frameShapes[i] == shape)
                {
                    return;
                }
            }

            if (_frameShapeCount < _frameShapes.Length)
            {
                _frameShapes[_frameShapeCount++] = shape;
            }
        }

        /// <summary>
        /// Called at present, after the present blit is encoded and before the command
        /// buffer is committed. Consumes any ring slots whose command buffer has
        /// completed, then records this frame's samples and accumulator snapshot.
        /// </summary>
        public static unsafe void OnPresent(CommandBufferScoped cbs, Texture src)
        {
            if (!Enabled || _buf.NativePtr == IntPtr.Zero || src == null)
            {
                return;
            }

            // 1. Classify every slot whose fence already signalled. Never waits.
            for (int i = 0; i < Slots; i++)
            {
                ref Slot slot = ref _slots[i];

                if (slot.Valid && slot.Fence.IsSignaled())
                {
                    Classify(ref slot, i);
                }
            }

            MTLTexture tex = src.GetHandle(cbs);

            if (tex.NativePtr != IntPtr.Zero)
            {
                int idx = (int)(_frame % Slots);
                ref Slot mine = ref _slots[idx];

                if (mine.Valid)
                {
                    // The fence never signalled in a whole ring revolution. Drop the
                    // stale sample rather than wait for it.
                    mine.Fence.Put();
                    mine.Valid = false;
                    _dropped++;
                }

                MTLBlitCommandEncoder blit = cbs.Encoders.EnsureBlitEncoder();

                for (int i = 0; i < Pixels; i++)
                {
                    blit.CopyFromTexture(
                        tex, 0, 0,
                        new MTLOrigin
                        {
                            x = (ulong)(src.Width * (i % GridSide + 1) / (GridSide + 1)),
                            y = (ulong)(src.Height * (i / GridSide + 1) / (GridSide + 1)),
                            z = 0,
                        },
                        new MTLSize { width = 1, height = 1, depth = 1 },
                        _buf, (ulong)((idx * Pixels + i) * BytesPerPixel), BytesPerPixel, BytesPerPixel);
                }

                mine.Fence = cbs.GetFence();
                mine.Fence.Get();
                mine.Frame = _frame;
                mine.Uploads = _frameUploads;
                mine.BigUploads = _frameBigUploads;
                mine.BigUploadsOntoRt = _frameBigUploadsOntoRt;
                mine.BigCopies = _frameBigCopies;
                mine.BigCopiesOntoRt = _frameBigCopiesOntoRt;
                mine.Shapes ??= new ulong[8];
                Array.Copy(_frameShapes, mine.Shapes, _frameShapeCount);
                mine.ShapeCount = _frameShapeCount;
                mine.Valid = true;
            }

            // 2. Reset the frame accumulators. The attachment set is per frame too.
            _attachedThisFrame.Clear();
            _frameShapeCount = 0;
            _frameUploads = 0;
            _frameBigUploads = 0;
            _frameBigUploadsOntoRt = 0;
            _frameBigCopies = 0;
            _frameBigCopiesOntoRt = 0;

            _frame++;

            if (_frame % ReportInterval == 0)
            {
                Report();
            }
        }

        private static unsafe void Classify(ref Slot slot, int index)
        {
            byte* p = (byte*)_buf.Contents + index * Pixels * BytesPerPixel;
            int saturated = 0;

            for (int i = 0; i < Pixels; i++)
            {
                byte* px = p + i * BytesPerPixel;

                if ((px[0] + px[1] + px[1] + px[2]) * 0.25f >= SaturatedLuma)
                {
                    saturated++;
                }
            }

            bool flat = saturated >= SaturatedNeeded;

            if (flat)
            {
                _flatFrames++;

                if (slot.BigUploads > 0)
                {
                    _flatWithUpload++;
                }

                if (slot.BigUploadsOntoRt > 0)
                {
                    _flatWithUploadOntoRt++;
                }

                if (slot.BigCopies > 0)
                {
                    _flatWithCopy++;
                }

                if (slot.BigCopiesOntoRt > 0)
                {
                    _flatWithCopyOntoRt++;
                }
            }
            else
            {
                _normalFrames++;

                if (slot.BigUploads > 0)
                {
                    _normalWithUpload++;
                }

                if (slot.BigUploadsOntoRt > 0)
                {
                    _normalWithUploadOntoRt++;
                }

                if (slot.BigCopies > 0)
                {
                    _normalWithCopy++;
                }

                if (slot.BigCopiesOntoRt > 0)
                {
                    _normalWithCopyOntoRt++;
                }
            }

            for (int i = 0; i < slot.ShapeCount; i++)
            {
                (long f, long n) = _shapeStats.TryGetValue(slot.Shapes[i], out (long Flat, long Normal) v) ? (v.Flat, v.Normal) : (0L, 0L);
                _shapeStats[slot.Shapes[i]] = flat ? (f + 1, n) : (f, n + 1);
            }

            slot.Fence.Put();
            slot.Valid = false;
        }

        private static void Report()
        {
            StringBuilder sb = new();

            sb.Append($"uploadcorr: classified={_flatFrames + _normalFrames} flat={_flatFrames} normal={_normalFrames} dropped={_dropped}");
            sb.Append($" | upload: flat {_flatWithUpload}/{_flatFrames}, normal {_normalWithUpload}/{_normalFrames}");
            sb.Append($" | uploadOntoRT: flat {_flatWithUploadOntoRt}, normal {_normalWithUploadOntoRt}");
            sb.Append($" | copy: flat {_flatWithCopy}/{_flatFrames}, normal {_normalWithCopy}/{_normalFrames}");
            sb.Append($" | copyOntoRT: flat {_flatWithCopyOntoRt}, normal {_normalWithCopyOntoRt}");

            foreach (KeyValuePair<ulong, (long Flat, long Normal)> pair in _shapeStats)
            {
                int width = (int)(pair.Key >> 40);
                int height = (int)((pair.Key >> 16) & 0xFFFFFF);
                bool isCopy = ((pair.Key >> 8) & 0xFF) == 2;
                bool ontoRt = (pair.Key & 0xFF) != 0;

                sb.Append($"\n  {(isCopy ? "copy" : "upload")}{(ontoRt ? "+RT" : "")} {width}x{height}: flat {pair.Value.Flat}, normal {pair.Value.Normal}");
            }

            Logger.Warning?.PrintMsg(LogClass.Gpu, sb.ToString());
        }
    }
}
