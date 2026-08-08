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

        internal ulong PrepareRenderPass(MTLRenderPassDescriptor descriptor, CommandBufferScoped cbs)
        {
            if (_activeBuffer.NativePtr == IntPtr.Zero)
            {
                _activeBuffer = _manager.RentResultBuffer();
                _buffers.Add(_activeBuffer);
            }

            // The same counter can span many Metal render encoders. macOS 26's
            // accumulate mode makes every encoder add to this one 64-bit result.
            descriptor.VisibilityResultBuffer = _activeBuffer;
            descriptor.SetVisibilityResultTypeAccumulate();

            // A result is only CPU-readable after every command buffer that wrote it
            // has completed. This also keeps the fence alive if the pool recycles its
            // command-buffer slot before the guest asks for the counter.
            cbs.AddWaitable(_waitable);

            return 0;
        }

        internal void Reset()
        {
            // Do not clear a buffer that earlier commands will write later. Give the
            // post-reset interval a fresh result buffer and retain older buffers until
            // all their command buffers complete.
            _activeBuffer = default;
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

            _buffers.Clear();
            _activeBuffer = default;
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
