using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.SharpMetalExtensions;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class CounterEvent : ICounterEvent
    {
        private readonly CounterManager _manager;
        private readonly MultiFenceHolder _waitable = new();
        private readonly List<MTLBuffer> _buffers = [];

        private MTLBuffer _activeBuffer;
        // Slot of the manager's shared visibility buffer, or -1 when this counter owns a
        // buffer of its own (slot mode off, or the pool was exhausted).
        private int _activeSlot = -1;
        private readonly List<int> _slots = [];
        private EventHandler<ulong> _resultHandler;
        private double _divisor;

        internal bool ClearCounter { get; private set; }
        internal bool Completed { get; private set; }

        public bool Invalid { get; set; }

        public CounterEvent(CounterManager manager)
        {
            _manager = manager;
            _divisor = 1d;
            Completed = manager == null;
        }

        /// <summary>This counter accumulates into the manager's shared slot buffer.</summary>
        internal bool UsesSlotBuffer => _activeSlot >= 0;

        /// <summary>
        /// Give this counter a slot of the shared buffer the open pass already bound, so a
        /// counter change needs no new pass. The pass's command buffer has to wait on this
        /// counter too: from here on its draws accumulate into this slot.
        /// </summary>
        internal bool TryBindToSlotBuffer(CommandBufferScoped cbs, out ulong offset)
        {
            if (_activeBuffer.NativePtr == IntPtr.Zero)
            {
                if (!_manager.TryRentSlot(out int slot))
                {
                    offset = 0;

                    return false;
                }

                _activeSlot = slot;
                _slots.Add(slot);
                _activeBuffer = _manager.SlotBuffer;
                cbs.AddWaitable(_waitable);
            }
            else if (_activeSlot < 0)
            {
                // Already writing a buffer of its own; it cannot move mid-pass.
                offset = 0;

                return false;
            }

            offset = (ulong)_activeSlot * sizeof(ulong);

            return true;
        }

        internal ulong PrepareRenderPass(MTLRenderPassDescriptor descriptor, CommandBufferScoped cbs)
        {
            if (_activeBuffer.NativePtr == IntPtr.Zero)
            {
                if (_manager.TryRentSlot(out int slot))
                {
                    _activeSlot = slot;
                    _slots.Add(slot);
                    _activeBuffer = _manager.SlotBuffer;
                }
                else
                {
                    _activeSlot = -1;
                    _activeBuffer = _manager.RentResultBuffer();
                    _buffers.Add(_activeBuffer);
                }
            }

            // The same counter can span many Metal render encoders. macOS 26's
            // accumulate mode makes every encoder add to this one 64-bit result.
            descriptor.VisibilityResultBuffer = _activeBuffer;
            descriptor.SetVisibilityResultTypeAccumulate();

            // A result is only CPU-readable after every command buffer that wrote it
            // has completed. This also keeps the fence alive if the pool recycles its
            // command-buffer slot before the guest asks for the counter.
            cbs.AddWaitable(_waitable);

            return _activeSlot >= 0 ? (ulong)_activeSlot * sizeof(ulong) : 0;
        }

        internal void Reset()
        {
            // Do not clear a buffer that earlier commands will write later. Give the
            // post-reset interval a fresh result buffer and retain older buffers until
            // all their command buffers complete.
            _activeBuffer = default;
            _activeSlot = -1;
            ClearCounter = true;
        }

        internal void Complete(EventHandler<ulong> resultHandler, float divisor)
        {
            _resultHandler = resultHandler;
            _divisor = divisor > 0f ? divisor : 1d;
        }

        internal bool IsReady()
        {
            return _waitable.WaitForFences(false);
        }

        internal void Wait()
        {
            _waitable.WaitForFences();
        }

        internal ulong ReadResult()
        {
            if (_activeBuffer.NativePtr == IntPtr.Zero)
            {
                return 0;
            }

            if (_activeSlot >= 0)
            {
                return _manager.ReadSlot(_activeSlot);
            }

            long result = Marshal.ReadInt64(_activeBuffer.Contents);

            return result > 0 ? (ulong)result : 0;
        }

        internal void Signal(ulong result)
        {
            Completed = true;
            _resultHandler?.Invoke(this, result);
        }

        internal ulong ScaleResult(ulong result)
        {
            return _divisor == 1d ? result : (ulong)Math.Ceiling(result / _divisor);
        }

        internal void ReleaseBuffers()
        {
            foreach (MTLBuffer buffer in _buffers)
            {
                _manager.ReturnResultBuffer(buffer);
            }

            foreach (int slot in _slots)
            {
                _manager.ReturnSlot(slot);
            }

            _slots.Clear();
            _buffers.Clear();
            _activeBuffer = default;
            _activeSlot = -1;
        }

        public bool ReserveForHostAccess()
        {
            // Host conditional rendering is not implemented by the Metal pipeline;
            // the shared result is flushed and compared on the CPU instead.
            return false;
        }

        public void Flush()
        {
            if (!Completed && _manager != null)
            {
                _manager.FlushTo(this);
            }
        }

        public void Dispose()
        {
            // The counter queue owns result storage until GPU completion. CounterCache
            // may keep this lightweight event after completion, just like Vulkan.
        }
    }
}
