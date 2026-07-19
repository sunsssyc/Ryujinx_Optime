using Ryujinx.Common.Logging;
using SharpMetal;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Trigger-file driven programmatic GPU capture. Touch the trigger file while a
    /// game is running and the next full frame is written as a .gputrace document
    /// (openable in Xcode), together with a per-draw log of that frame. The Metal
    /// runtime only allows GPU trace documents when the process was launched with
    /// METAL_CAPTURE_ENABLED=1.
    /// </summary>
    [SupportedOSPlatform("macos")]
    class FrameCapture
    {
        private const string TriggerPath = "/tmp/ryujinx-metal-capture";
        private const string TraceTriggerPath = "/tmp/ryujinx-metal-trace";

        private readonly MTLCommandQueue _queue;
        private bool _capturing;
        private int _traceFramesRemaining;
        private bool _supportChecked;
        private bool _supported;
        private string _outputPath;

        public bool DrawTraceActive => _capturing || _traceFramesRemaining > 0;

        public FrameCapture(MTLCommandQueue queue)
        {
            _queue = queue;
        }

        /// <summary>
        /// Called at the start of presentation, before the drawable is touched.
        /// Ends an active capture here so the capture window never includes drawable
        /// acquisition or presentation: capturing across a present exhausted the
        /// layer's drawable pool and wedged the frame pipeline (observed with both
        /// device- and queue-scoped captures).
        /// </summary>
        public void OnPresentBegin()
        {
            if (_capturing)
            {
                MTLCaptureManager.SharedCaptureManager().StopCapture();
                _capturing = false;

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Metal frame capture finished: {_outputPath}");
            }
        }

        /// <summary>
        /// Called once per presented frame, after the present was queued. Starts a
        /// capture when the trigger file exists; it ends at the next OnPresentBegin.
        /// The draw-trace trigger logs the draws of the next frames without touching
        /// MTLCaptureManager at all, so it cannot disturb the frame pipeline.
        /// </summary>
        public void ProcessPresent()
        {
            if (_traceFramesRemaining > 0 && --_traceFramesRemaining == 0)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Metal draw trace finished.");
            }

            if (_traceFramesRemaining == 0 && TryConsumeTrigger(TraceTriggerPath))
            {
                _traceFramesRemaining = 3;

                Logger.Warning?.PrintMsg(LogClass.Gpu, "Metal draw trace started for 3 frames.");

                return;
            }

            if (!TryConsumeTrigger(TriggerPath))
            {
                return;
            }

            MTLCaptureManager manager = MTLCaptureManager.SharedCaptureManager();

            if (!_supportChecked)
            {
                _supportChecked = true;
                _supported = manager.SupportsDestination(MTLCaptureDestination.GPUTraceDocument);

                if (!_supported)
                {
                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        "Metal GPU trace capture is not available. Launch with METAL_CAPTURE_ENABLED=1 to enable it.");
                }
            }

            if (!_supported)
            {
                return;
            }

            StartGpuTrace(manager);
        }

        private static bool TryConsumeTrigger(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // If the trigger cannot be deleted, do not fire endlessly.
                return false;
            }

            return true;
        }

        private void StartGpuTrace(MTLCaptureManager manager)
        {

            _outputPath = $"/tmp/ryujinx-metal-{DateTime.Now:yyyyMMdd-HHmmss}.gputrace";

            NSString outputString = StringHelper.NSString(_outputPath);
            NSURL outputUrl = new(ObjectiveCRuntime.IntPtr_objc_msgSend(new ObjectiveCClass("NSURL"), (Selector)"fileURLWithPath:", outputString.NativePtr));

            MTLCaptureDescriptor descriptor = new()
            {
                // Capture only the main command queue: a whole-device capture also
                // records the background queue and has been observed to wedge the
                // frame pipeline entirely.
                CaptureObject = _queue,
                Destination = MTLCaptureDestination.GPUTraceDocument,
                OutputURL = outputUrl,
            };

            NSError error = new(IntPtr.Zero);

            if (manager.StartCapture(descriptor, ref error))
            {
                _capturing = true;

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Metal frame capture started: {_outputPath}");
            }
            else
            {
                string message = error.NativePtr != IntPtr.Zero ? error.LocalizedDescription.ToString() : "unknown error";

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Metal frame capture failed to start: {message}");
            }
        }
    }
}
