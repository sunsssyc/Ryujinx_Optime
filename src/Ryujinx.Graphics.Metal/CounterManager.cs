using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Implements the guest SamplesPassed counter with Metal visibility queries.
    /// Metal's visibility-result accumulation across render encoders was added in
    /// macOS 26; older systems keep the conservative compatibility fallback in
    /// <see cref="MetalRenderer.ReportCounter"/>.
    /// </summary>
    [SupportedOSPlatform("macos")]
    class CounterManager : IDisposable
    {
        private const int MaximumPooledBuffers = 128;

        /// <summary>
        /// Slots in the shared visibility buffer. A pass descriptor carries one visibility
        /// buffer, so a counter with a buffer of its own can only start at a pass boundary -
        /// which is why reporting one used to end the render pass, 14 to 28 passes a frame
        /// in the user's scenes. Every counter taking a slot of one buffer instead lets a
        /// report switch the offset on the encoder that is already open. The hardware
        /// honours only the low 15 bits of a visibility offset (the per-draw coverage probe
        /// found this the hard way), so the slot count must stay far below 32768.
        ///
        /// MEASURED: no frame time. Standing still at a 34 fps spot, hot-switched
        /// on/off/on over 93 windows of 120 frames: 34.57 fps on against 34.52 off, a
        /// difference of 0.06 with a 2SE threshold of 0.48, while the passes it removes
        /// are real (341 -> 330 per frame, counter splits 13.8 -> 0). The 45.8us-per-pass
        /// regression that predicted about 4% is an average over passes that store a full
        /// 1440p attachment; the passes a counter splits off sit on the same targets back
        /// to back and evidently keep most of their attachments on tile. Pass count is a
        /// correlate of frame time, not a currency - do not price a pass saving from the
        /// average again. Kept because it is the behaviour Metal is designed for and costs
        /// nothing, not because it is faster.
        /// </summary>
        private const int SlotCount = 1024;
        private const int SlotBytes = sizeof(ulong);

        /// <summary>
        /// RYUJINX_METAL_COUNTER_IN_PASS=0 restores a result buffer per counter, and
        /// /tmp/ryujinx-metal-counter-in-pass holding 0 or 1 switches it while the game runs
        /// - which is the only honest way to compare the two, since frame rate drifts by
        /// more than this is worth between processes. Both directions are safe at any time:
        /// a counter already holding a slot keeps it until its readers finish, and a counter
        /// that cannot get one falls back to a buffer of its own and the pass split that
        /// goes with it.
        /// </summary>
        private static readonly bool _slotsDefault =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_COUNTER_IN_PASS") != "0";
        private static bool _slotsEnabled = _slotsDefault;

        public static void RefreshToggle()
        {
            try
            {
                string text = System.IO.File.Exists("/tmp/ryujinx-metal-counter-in-pass")
                    ? System.IO.File.ReadAllText("/tmp/ryujinx-metal-counter-in-pass").Trim()
                    : null;

                _slotsEnabled = text switch
                {
                    "1" => true,
                    "0" => false,
                    _ => _slotsDefault,
                };
            }
            catch (System.IO.IOException)
            {
                // Raced with the writer; the next frame picks it up.
            }
        }

        private MTLBuffer _slotBuffer;
        private readonly Queue<int> _freeSlots = new();
        private bool _passBoundSlotBuffer;

        private static long _inPassSwitches, _passEndFallbacks, _slotExhaustions;

        /// <summary>The shared buffer, or a null handle when slot mode is off or unavailable.</summary>
        internal MTLBuffer SlotBuffer => _slotBuffer;

        private readonly MTLDevice _device;
        private readonly MetalRenderer _renderer;
        private readonly Queue<CounterEvent> _events = new();
        private readonly Stack<MTLBuffer> _bufferPool = new();
        private readonly object _lock = new();

        private CounterEvent _current;
        private ulong _accumulatedCounter;
        private bool _disposed;

        public bool SupportsSamplesPassed { get; }

        public CounterManager(MTLDevice device, MetalRenderer renderer)
        {
            _device = device;
            _renderer = renderer;
            bool disabledForDiagnostics =
                Environment.GetEnvironmentVariable("RYUJINX_METAL_DISABLE_SAMPLES_PASSED") == "1";

            SupportsSamplesPassed = OperatingSystem.IsMacOSVersionAtLeast(26) && !disabledForDiagnostics;

            // The buffer is allocated whenever counters are supported, so the toggle can be
            // flipped at any point without needing an allocation on the render thread.
            if (SupportsSamplesPassed)
            {
                _slotBuffer = _device.NewBuffer(SlotCount * SlotBytes, MTLResourceOptions.ResourceStorageModeShared);

                unsafe
                {
                    new Span<ulong>((void*)_slotBuffer.Contents, SlotCount).Clear();
                }

                for (int i = 0; i < SlotCount; i++)
                {
                    _freeSlots.Enqueue(i);
                }
            }

            if (SupportsSamplesPassed)
            {
                _current = new CounterEvent(this);
                Logger.Info?.PrintMsg(LogClass.Gpu, "Metal SamplesPassed counters use accumulated hardware visibility queries.");
            }
            else
            {
                Logger.Warning?.PrintMsg(
                    LogClass.Gpu,
                    disabledForDiagnostics
                        ? "Metal hardware SamplesPassed counters are disabled for diagnostics; using the compatibility result."
                        : "Metal hardware SamplesPassed counters require macOS 26; using the compatibility result on this system.");
            }
        }

        public ulong PrepareRenderPass(MTLRenderPassDescriptor descriptor, CommandBufferScoped cbs)
        {
            ulong offset = _current.PrepareRenderPass(descriptor, cbs);

            _passBoundSlotBuffer = _slotBuffer.NativePtr != IntPtr.Zero && _current.UsesSlotBuffer;

            return offset;
        }

        /// <summary>The pass being encoded bound the shared slot buffer, so a counter change can stay inside it.</summary>
        internal bool PassBoundSlotBuffer => _passBoundSlotBuffer;

        internal void NotePassEnded()
        {
            _passBoundSlotBuffer = false;
        }

        /// <summary>
        /// The offset the encoder should switch to so that subsequent draws count into the
        /// current counter, or false when that counter is not in the shared buffer (slot mode
        /// off, or the pool was exhausted) and the caller must end the pass instead.
        /// </summary>
        internal bool TryGetCurrentSlotOffset(CommandBufferScoped cbs, out ulong offset)
        {
            lock (_lock)
            {
                if (!_passBoundSlotBuffer || _disposed)
                {
                    offset = 0;

                    return false;
                }

                return _current.TryBindToSlotBuffer(cbs, out offset);
            }
        }

        internal static void NoteInPassSwitch() => System.Threading.Interlocked.Increment(ref _inPassSwitches);
        internal static void NotePassEndFallback() => System.Threading.Interlocked.Increment(ref _passEndFallbacks);

        /// <summary>Counter switches that stayed inside the pass, and those that had to end it.</summary>
        public static string TakeStats()
        {
            long inPass = System.Threading.Interlocked.Exchange(ref _inPassSwitches, 0);
            long ends = System.Threading.Interlocked.Exchange(ref _passEndFallbacks, 0);

            if (inPass + ends == 0)
            {
                return string.Empty;
            }

            long exhausted = System.Threading.Interlocked.Read(ref _slotExhaustions);

            return $" counter switches ({(_slotsEnabled ? "in-pass on" : "off")}): {inPass} in pass, {ends} ended the pass" +
                   (exhausted != 0 ? $", {exhausted} slot exhaustions" : string.Empty) + ".";
        }

        /// <summary>A slot of the shared buffer, zeroed; false when slot mode is off or the pool is empty.</summary>
        internal bool TryRentSlot(out int slot)
        {
            if (!_slotsEnabled || _slotBuffer.NativePtr == IntPtr.Zero || _freeSlots.Count == 0)
            {
                if (_slotsEnabled && _slotBuffer.NativePtr != IntPtr.Zero)
                {
                    System.Threading.Interlocked.Increment(ref _slotExhaustions);
                }

                slot = -1;

                return false;
            }

            slot = _freeSlots.Dequeue();

            // Only a slot whose readers have all completed is on the free list, so this
            // cannot race the GPU accumulating into it.
            unsafe
            {
                ((ulong*)_slotBuffer.Contents)[slot] = 0;
            }

            return true;
        }

        internal void ReturnSlot(int slot)
        {
            if (slot >= 0 && !_disposed)
            {
                _freeSlots.Enqueue(slot);
            }
        }

        internal ulong ReadSlot(int slot)
        {
            unsafe
            {
                return ((ulong*)_slotBuffer.Contents)[slot];
            }
        }

        public CounterEvent Report(EventHandler<ulong> resultHandler, float divisor)
        {
            lock (_lock)
            {
                CounterEvent result = _current;
                result.Complete(resultHandler, divisor);
                _events.Enqueue(result);
                _current = new CounterEvent(this);

                return result;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _current.Reset();
            }
        }

        public void Update()
        {
            lock (_lock)
            {
                while (_events.TryPeek(out CounterEvent evt) && evt.IsReady())
                {
                    _events.Dequeue();
                    Consume(evt);
                }
            }
        }

        public void FlushTo(CounterEvent target)
        {
            lock (_lock)
            {
                if (_disposed || target.Completed)
                {
                    return;
                }
            }

            // A query may still belong to the command buffer currently being encoded.
            // Submit it before waiting; waiting on an uncommitted MTLCommandBuffer can
            // otherwise block forever.
            if (!target.IsReady())
            {
                _renderer.FlushAllCommands();
            }

            lock (_lock)
            {
                while (_events.TryDequeue(out CounterEvent evt))
                {
                    evt.Wait();
                    Consume(evt);

                    if (ReferenceEquals(evt, target))
                    {
                        break;
                    }
                }
            }
        }

        internal MTLBuffer RentResultBuffer()
        {
            lock (_lock)
            {
                MTLBuffer buffer = _bufferPool.Count != 0
                    ? _bufferPool.Pop()
                    : _device.NewBuffer(sizeof(ulong), MTLResourceOptions.ResourceStorageModeShared);

                Marshal.WriteInt64(buffer.Contents, 0);

                return buffer;
            }
        }

        internal void ReturnResultBuffer(MTLBuffer buffer)
        {
            if (buffer.NativePtr == IntPtr.Zero)
            {
                return;
            }

            if (!_disposed && _bufferPool.Count < MaximumPooledBuffers)
            {
                _bufferPool.Push(buffer);
            }
            else
            {
                buffer.Dispose();
            }
        }

        private void Consume(CounterEvent evt)
        {
            if (evt.ClearCounter)
            {
                _accumulatedCounter = 0;
            }

            _accumulatedCounter += evt.ScaleResult(evt.ReadResult());

            try
            {
                evt.Signal(_accumulatedCounter);
            }
            finally
            {
                evt.ReleaseBuffers();
            }
        }

        public void Dispose()
        {
            if (!SupportsSamplesPassed || _disposed)
            {
                return;
            }

            // Submit every encoder that can still reference a visibility buffer before
            // releasing it. The newly rented empty command buffer is retired by the
            // pipeline immediately afterwards during renderer shutdown.
            _renderer.FlushAllCommands();

            lock (_lock)
            {
                while (_events.TryDequeue(out CounterEvent evt))
                {
                    evt.Wait();
                    Consume(evt);
                }

                _current.Wait();
                _current.ReleaseBuffers();

                _disposed = true;

                while (_bufferPool.TryPop(out MTLBuffer buffer))
                {
                    buffer.Dispose();
                }

                _freeSlots.Clear();

                if (_slotBuffer.NativePtr != IntPtr.Zero)
                {
                    _slotBuffer.Dispose();
                    _slotBuffer = default;
                }
            }
        }
    }
}
