using Ryujinx.Common.Logging;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal;
using SharpMetal.ObjectiveCCore;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Captures a GPU trace of a frame that actually came out white.
    ///
    /// Two things made the existing capture path unproductive, and neither was a limit
    /// of the tool:
    ///
    ///   - The frame was random. Flat frames are about 40% of them at the reproducing
    ///     save, so most traces were of healthy frames and nothing recorded which kind
    ///     had been caught. This classifies the captured frame and throws the trace away
    ///     unless it was flat, retrying until one lands - so every file that survives is
    ///     a picture of the fault.
    ///
    ///   - The scope never closed. Present calls OnPresentBegin (which stopped the
    ///     capture) before EndScope, so the scope was still open when the trace was
    ///     finalised, and what came out covered the tail of a frame - the UI and the
    ///     minimap, with the scene already drawn. Here the scope is opened immediately
    ///     before the pass that samples the scene texture and closed when that pass ends,
    ///     both well before present, so the trace contains the failing read and little
    ///     else. That also keeps it small: whole-frame queue captures ran to hundreds of
    ///     megabytes and the tools crashed while finalising them.
    ///
    /// Classifying costs one GPU sync per attempt, which is affordable here on evidence
    /// rather than hope: the CPU-sampling FlashGuard measured 35% flat frames while
    /// doing exactly that every frame, so the sync does not close the race. A per-frame
    /// *log* does, which is why nothing here logs per frame.
    ///
    /// RYUJINX_METAL_CAPTURE_FLAT=1, and the process must be launched with
    /// METAL_CAPTURE_ENABLED=1 for GPU trace documents to be permitted.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class CaptureHunter
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_FLAT") == "1";

        private const int GridSide = 5;
        private const int Pixels = GridSide * GridSide;
        private const int BytesPerPixel = 4;

        // FlashGuard's calibrated criterion, so "flat" here means what it means
        // everywhere else in this investigation.
        private const float SaturatedLuma = 235f;
        private const int SaturatedNeeded = 6;

        // Give the picture time to be a picture. Attempts during load-in would capture a
        // legitimately white fade and report success.
        private static readonly int _startAfterFrames =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_METAL_CAPTURE_AFTER"), out int after) && after > 0
                ? after
                : 900;

        private static MTLCommandQueue _queue;
        private static MTLBuffer _samples;
        private static MTLCaptureScope _scope;

        private static bool _capturing;
        private static bool _scopeOpen;
        private static bool _armedThisFrame;
        private static bool _done;
        private static int _attempts;
        private static int _frame;
        private static string _outputPath;

        public static void Init(MTLDevice device, MTLCommandQueue queue)
        {
            if (!Enabled)
            {
                return;
            }

            _queue = queue;
            _samples = device.NewBuffer(Pixels * BytesPerPixel, MTLResourceOptions.ResourceStorageModeShared);

            bool supported = MTLCaptureManager.SharedCaptureManager()
                .SupportsDestination(MTLCaptureDestination.GPUTraceDocument);

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                supported
                    ? $"capture hunter armed: keeping the first flat frame after {_startAfterFrames} frames"
                    : "capture hunter: GPU trace documents unavailable - relaunch with METAL_CAPTURE_ENABLED=1");

            if (!supported)
            {
                _done = true;
            }
        }

        /// <summary>
        /// Called before the pass that samples the scene texture opens, with no pass
        /// currently open so starting a capture is legal. Starts the trace and opens the
        /// scope around that one pass.
        /// </summary>
        public static void OnSceneSamplingPassBegin()
        {
            if (!Enabled || _done || !_armedThisFrame || _capturing)
            {
                return;
            }

            MTLCaptureManager manager = MTLCaptureManager.SharedCaptureManager();

            _outputPath = $"/tmp/ryujinx-flat-{_attempts:D3}.gputrace";

            try
            {
                if (Directory.Exists(_outputPath))
                {
                    Directory.Delete(_outputPath, true);
                }
            }
            catch (IOException)
            {
                // A leftover we cannot remove just means the next attempt picks a new name.
            }

            NSString outputString = StringHelper.NSString(_outputPath);
            NSURL outputUrl = new(ObjectiveCRuntime.IntPtr_objc_msgSend(
                new ObjectiveCClass("NSURL"), (Selector)"fileURLWithPath:", outputString.NativePtr));

            _scope = manager.NewCaptureScope(_queue);

            MTLCaptureDescriptor descriptor = new()
            {
                CaptureObject = _scope,
                Destination = MTLCaptureDestination.GPUTraceDocument,
                OutputURL = outputUrl,
            };

            NSError error = new(IntPtr.Zero);

            if (!manager.StartCapture(descriptor, ref error))
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"capture hunter: start failed - {(error.NativePtr != IntPtr.Zero ? error.LocalizedDescription.ToString() : "unknown")}");

                _done = true;

                return;
            }

            _capturing = true;
            _scope.BeginScope();
            _scopeOpen = true;
        }

        /// <summary>
        /// Called when a render pass ends. Closes the scope on the first pass end after
        /// it was opened, so the trace holds that pass and nothing after it.
        /// </summary>
        public static void OnPassEnd()
        {
            if (!_scopeOpen)
            {
                return;
            }

            _scope.EndScope();
            _scopeOpen = false;
        }

        /// <summary>
        /// Encodes the classification samples. Called at present, before the command
        /// buffer is committed.
        /// </summary>
        public static void SamplePresentSource(CommandBufferScoped cbs, Texture src)
        {
            if (!Enabled || _done || !_armedThisFrame || src == null || _samples.NativePtr == IntPtr.Zero)
            {
                return;
            }

            MTLTexture tex = src.GetHandle(cbs);

            if (tex.NativePtr == IntPtr.Zero)
            {
                return;
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
                    _samples, (ulong)(i * BytesPerPixel), BytesPerPixel, BytesPerPixel);
            }
        }

        /// <summary>
        /// Called after the frame's command buffer has completed. Decides whether the
        /// frame that was just captured was flat, keeps the trace if it was, and arms the
        /// next attempt if it was not.
        /// </summary>
        public static unsafe void Decide()
        {
            if (!Enabled || _done)
            {
                return;
            }

            _frame++;

            if (_capturing)
            {
                if (_scopeOpen)
                {
                    // The pass never ended before present. Close it here rather than
                    // finalise a trace with an open scope, which is what produced traces
                    // covering only a frame's tail.
                    _scope.EndScope();
                    _scopeOpen = false;
                }

                MTLCaptureManager.SharedCaptureManager().StopCapture();
                _capturing = false;
                _attempts++;

                byte* p = (byte*)_samples.Contents;
                int saturated = 0;
                double lumaSum = 0;

                for (int i = 0; i < Pixels; i++)
                {
                    byte* px = p + i * BytesPerPixel;
                    double luma = (px[0] + px[1] + px[1] + px[2]) * 0.25;

                    lumaSum += luma;

                    if (luma >= SaturatedLuma)
                    {
                        saturated++;
                    }
                }

                if (saturated >= SaturatedNeeded)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu,
                        $"capture hunter: KEPT a flat frame after {_attempts} attempts " +
                        $"(saturated {saturated}/{Pixels}, mean luma {lumaSum / Pixels:F0}): {_outputPath}");

                    _done = true;

                    return;
                }

                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"capture hunter: attempt {_attempts} was a normal frame " +
                    $"(saturated {saturated}/{Pixels}, mean luma {lumaSum / Pixels:F0}), discarding");

                try
                {
                    if (Directory.Exists(_outputPath))
                    {
                        Directory.Delete(_outputPath, true);
                    }
                }
                catch (IOException)
                {
                    // Leaving one behind is harmless; the name carries the attempt number.
                }
            }

            // Arm the next attempt. One frame at a time: capturing across a present
            // exhausts the layer's drawable pool and wedges the frame pipeline.
            _armedThisFrame = _frame >= _startAfterFrames;
        }

        public static bool WantsSyncThisFrame => Enabled && !_done && _armedThisFrame;
    }
}
