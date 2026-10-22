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

        // Everything this counter holds until its readers finish, and the part of it that
        // counts towards the result: what was written since the last reset.
        private readonly List<MTLBuffer> _buffers = [];
        private readonly List<(int Slot, bool PerPass)> _slots = [];
        private readonly List<MTLBuffer> _readBuffers = [];
        private readonly List<(int Slot, bool PerPass)> _readSlots = [];

        private MTLBuffer _activeBuffer;
        // Slot the encoder is counting into, or -1 when this counter writes a buffer of its
        // own (slot mode off, or the pool was exhausted).
        private int _activeSlot = -1;
        // The active slot is in the per-pass buffer: it belongs to one pass and is never
        // reused by the next one (see CounterManager.PerPassEnabled).
        private bool _activePerPass;
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
        internal bool UsesSlotBuffer => _activeSlot >= 0 && !_activePerPass;

        /// <summary>This counter's open pass counts into a slot of the per-pass buffer.</summary>
        internal bool UsesPerPassSlot => _activeSlot >= 0 && _activePerPass;

        private void Hold(int slot, bool perPass)
        {
            _slots.Add((slot, perPass));
            _readSlots.Add((slot, perPass));
        }

        private void Hold(MTLBuffer buffer)
        {
            _buffers.Add(buffer);
            _readBuffers.Add(buffer);
        }

        /// <summary>
        /// Give this counter a slot of the shared buffer the open pass already bound, so a
        /// counter change needs no new pass. The pass's command buffer has to wait on this
        /// counter too: from here on its draws accumulate into this slot.
        /// </summary>
        internal bool TryBindToSlotBuffer(CommandBufferScoped cbs, out ulong offset)
        {
            if (_activeBuffer.NativePtr == IntPtr.Zero || _activePerPass)
            {
                if (!_manager.TryRentSlot(out int slot))
                {
                    offset = 0;

                    return false;
                }

                _activeSlot = slot;
                _activePerPass = false;
                Hold(slot, false);
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

        /// <summary>
        /// The per-pass counterpart of <see cref="TryBindToSlotBuffer"/>: a counter that starts
        /// (or restarts after a reset) inside a pass bound to the per-pass buffer takes a slot of
        /// its own there for the rest of that pass.
        /// </summary>
        internal bool TryBindToPerPassSlot(CommandBufferScoped cbs, out ulong offset)
        {
            // PrepareRenderPass rents a fresh per-pass slot for every pass, so an active
            // per-pass slot here is this pass's own and can be kept.
            if (_activeBuffer.NativePtr == IntPtr.Zero || !_activePerPass || _activeSlot < 0)
            {
                if (!_manager.TryRentPerPassSlot(out int slot))
                {
                    offset = 0;

                    return false;
                }

                _activeSlot = slot;
                _activePerPass = true;
                Hold(slot, true);
                _activeBuffer = _manager.PerPassBuffer;
                cbs.AddWaitable(_waitable);
            }

            offset = (ulong)_activeSlot * sizeof(ulong);

            return true;
        }

        internal ulong PrepareRenderPass(MTLRenderPassDescriptor descriptor, CommandBufferScoped cbs)
        {
            if (_manager.PerPassEnabled)
            {
                // A slot for this pass alone. Metal orders every encoder that writes a tracked
                // resource after the previous one that wrote it, so one result accumulated by
                // every pass chained all of them end to end: no pass's vertex work could start
                // before the previous pass's fragment work had finished (a Metal System Trace
                // showed 0% overlap). Distinct slots of an untracked buffer leave nothing to
                // order; the passes' counts are summed when the result is read.
                if (_manager.TryRentPerPassSlot(out int slot))
                {
                    _activeSlot = slot;
                    _activePerPass = true;
                    _activeBuffer = _manager.PerPassBuffer;
                    Hold(slot, true);
                }
                else
                {
                    _activeSlot = -1;
                    _activePerPass = false;
                    _activeBuffer = _manager.RentResultBuffer();
                    Hold(_activeBuffer);
                }

                descriptor.VisibilityResultBuffer = _activeBuffer;
                descriptor.SetVisibilityResultTypeReset();
            }
            else
            {
                if (_activeBuffer.NativePtr == IntPtr.Zero || _activePerPass)
                {
                    if (_manager.TryRentSlot(out int slot))
                    {
                        _activeSlot = slot;
                        _activeBuffer = _manager.SlotBuffer;
                        Hold(slot, false);
                    }
                    else
                    {
                        _activeSlot = -1;
                        _activeBuffer = _manager.RentResultBuffer();
                        Hold(_activeBuffer);
                    }

                    _activePerPass = false;
                }

                // The same counter can span many Metal render encoders. macOS 26's
                // accumulate mode makes every encoder add to this one 64-bit result.
                descriptor.VisibilityResultBuffer = _activeBuffer;
                descriptor.SetVisibilityResultTypeAccumulate();
            }

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
            // all their command buffers complete; only what is written from here on counts.
            _activeBuffer = default;
            _activeSlot = -1;
            _activePerPass = false;
            _readBuffers.Clear();
            _readSlots.Clear();
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
            ulong sum = 0;

            foreach ((int slot, bool perPass) in _readSlots)
            {
                sum += perPass ? _manager.ReadPerPassSlot(slot) : _manager.ReadSlot(slot);
            }

            foreach (MTLBuffer buffer in _readBuffers)
            {
                long value = Marshal.ReadInt64(buffer.Contents);
                sum += value > 0 ? (ulong)value : 0;
            }

            return sum;
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

            foreach ((int slot, bool perPass) in _slots)
            {
                if (perPass)
                {
                    _manager.ReturnPerPassSlot(slot);
                }
                else
                {
                    _manager.ReturnSlot(slot);
                }
            }

            _slots.Clear();
            _buffers.Clear();
            _readSlots.Clear();
            _readBuffers.Clear();
            _activeBuffer = default;
            _activeSlot = -1;
            _activePerPass = false;
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
