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

        private readonly MTLDevice _device;
        private bool _capturing;
        private bool _supportChecked;
        private bool _supported;
        private string _outputPath;

        public bool DrawTraceActive => _capturing;

        public FrameCapture(MTLDevice device)
        {
            _device = device;
        }

        /// <summary>
        /// Called once per presented frame, after the present was queued. Starts a
        /// capture when the trigger file exists and stops it one full frame later.
        /// </summary>
        public void ProcessPresent()
        {
            if (_capturing)
            {
                MTLCaptureManager.SharedCaptureManager().StopCapture();
                _capturing = false;

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Metal frame capture finished: {_outputPath}");

                return;
            }

            if (!File.Exists(TriggerPath))
            {
                return;
            }

            try
            {
                File.Delete(TriggerPath);
            }
            catch (IOException)
            {
                // If the trigger cannot be deleted, do not capture endlessly.
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

            _outputPath = $"/tmp/ryujinx-metal-{DateTime.Now:yyyyMMdd-HHmmss}.gputrace";

            NSString outputString = StringHelper.NSString(_outputPath);
            NSURL outputUrl = new(ObjectiveCRuntime.IntPtr_objc_msgSend(new ObjectiveCClass("NSURL"), (Selector)"fileURLWithPath:", outputString.NativePtr));

            MTLCaptureDescriptor descriptor = new()
            {
                CaptureObject = _device,
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
