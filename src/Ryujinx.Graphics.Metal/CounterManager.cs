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
            return _current.PrepareRenderPass(descriptor, cbs);
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
            }
        }
    }
}
