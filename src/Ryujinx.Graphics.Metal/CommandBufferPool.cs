using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Metal.SharpMetalExtensions;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class CommandBufferPool : IDisposable
    {
        private static readonly bool _waitEveryCommit =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_WAIT_EVERY_COMMIT") == "1";

        public const int MaxCommandBuffers = 16;

        private readonly int _totalCommandBuffers;
        private readonly int _totalCommandBuffersMask;
        private readonly MTLCommandQueue _queue;
        private readonly Thread _owner;
        private IEncoderFactory _defaultEncoderFactory;

        public bool OwnedByCurrentThread => _owner == Thread.CurrentThread;

        [SupportedOSPlatform("macos")]
        private struct ReservedCommandBuffer
        {
            public bool InUse;
            public bool InConsumption;
            public int SubmissionCount;
            public MTLCommandBuffer CommandBuffer;
            public CommandBufferEncoder Encoders;
            public FenceHolder Fence;

            public List<IAuto> Dependants;
            public List<MultiFenceHolder> Waitables;

            public void Use(MTLCommandQueue queue, IEncoderFactory stateManager)
            {
                MTLCommandBufferDescriptor descriptor = new();
#if DEBUG
                descriptor.ErrorOptions = MTLCommandBufferErrorOption.EncoderExecutionStatus;
#endif

                CommandBuffer = queue.CommandBuffer(descriptor);

                // The command buffer is autoreleased and nothing drains a pool on
                // this thread; take ownership (released in WaitAndDecrementRef).
                // The descriptor is owned (+1 from new) and was leaked per rent.
                ObjcOwnership.Retain(CommandBuffer.NativePtr);
                descriptor.Dispose();

                Fence = new FenceHolder(CommandBuffer);

                Encoders.Initialize(CommandBuffer, stateManager);

                InUse = true;
            }

            public void Initialize()
            {
                Dependants = [];
                Waitables = [];
                Encoders = new CommandBufferEncoder();
            }
        }

        private readonly ReservedCommandBuffer[] _commandBuffers;

        private readonly int[] _queuedIndexes;
        private int _queuedIndexesPtr;
        private int _queuedCount;
        private int _inUseCount;
        private readonly long[] _rentSeq;
        private long _rentCounter;
        private long _lastCommittedRentSeq;
        private long _commitCounter;
        private readonly long[] _commitSeq = new long[64];

        /// <summary>The rental sequence stamped on this command buffer when it was rented.</summary>
        public long RentSeqOf(int cbIndex) => (uint)cbIndex < (uint)_rentSeq.Length ? _rentSeq[cbIndex] : -1;

        /// <summary>The commit sequence, or 0 if not yet committed since its rental.</summary>
        public long CommitSeqOf(int cbIndex) => (uint)cbIndex < (uint)_commitSeq.Length ? _commitSeq[cbIndex] : -1;

        public CommandBufferPool(MTLCommandQueue queue, bool isLight = false)
        {
            _queue = queue;
            _owner = Thread.CurrentThread;

            _totalCommandBuffers = isLight ? 2 : MaxCommandBuffers;
            _totalCommandBuffersMask = _totalCommandBuffers - 1;

            _commandBuffers = new ReservedCommandBuffer[_totalCommandBuffers];

            _queuedIndexes = new int[_totalCommandBuffers];
            _rentSeq = new long[_totalCommandBuffers];
            _queuedIndexesPtr = 0;
            _queuedCount = 0;
        }

        public void Initialize(IEncoderFactory encoderFactory)
        {
            _defaultEncoderFactory = encoderFactory;

            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                _commandBuffers[i].Initialize();
                WaitAndDecrementRef(i);
            }
        }

        public void AddDependant(int cbIndex, IAuto dependant)
        {
            dependant.IncrementReferenceCount();
            _commandBuffers[cbIndex].Dependants.Add(dependant);
        }

        public void AddWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddInUseWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddWaitable(int cbIndex, MultiFenceHolder waitable)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];
            if (waitable.AddFence(cbIndex, entry.Fence))
            {
                entry.Waitables.Add(waitable);
            }
        }

        public bool IsFenceOnRentedCommandBuffer(FenceHolder fence)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse && entry.Fence == fence)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public FenceHolder GetFence(int cbIndex)
        {
            return _commandBuffers[cbIndex].Fence;
        }

        public int GetSubmissionCount(int cbIndex)
        {
            return _commandBuffers[cbIndex].SubmissionCount;
        }

        private int FreeConsumed(bool wait)
        {
            int freeEntry = 0;

            while (_queuedCount > 0)
            {
                int index = _queuedIndexes[_queuedIndexesPtr];

                ref ReservedCommandBuffer entry = ref _commandBuffers[index];

                if (wait || !entry.InConsumption || entry.Fence.IsSignaled())
                {
                    WaitAndDecrementRef(index);

                    wait = false;
                    freeEntry = index;

                    _queuedCount--;
                    _queuedIndexesPtr = (_queuedIndexesPtr + 1) % _totalCommandBuffers;
                }
                else
                {
                    break;
                }
            }

            return freeEntry;
        }

        public CommandBufferScoped ReturnAndRent(CommandBufferScoped cbs)
        {
            Return(cbs);
            return Rent();
        }

        public CommandBufferScoped Rent()
        {
            lock (_commandBuffers)
            {
                int cursor = FreeConsumed(_inUseCount + _queuedCount == _totalCommandBuffers);

                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[cursor];

                    if (!entry.InUse && !entry.InConsumption)
                    {
                        entry.Use(_queue, _defaultEncoderFactory);

                        _inUseCount++;
                        _rentSeq[cursor] = ++_rentCounter;
                        if ((uint)cursor < (uint)_commitSeq.Length) { _commitSeq[cursor] = 0; }

                        return new CommandBufferScoped(this, entry.CommandBuffer, entry.Encoders, cursor);
                    }

                    cursor = (cursor + 1) & _totalCommandBuffersMask;
                }
            }

            throw new InvalidOperationException($"Out of command buffers (In use: {_inUseCount}, queued: {_queuedCount}, total: {_totalCommandBuffers})");
        }

        public void Return(CommandBufferScoped cbs)
        {
            // Ensure the encoder is committed.
            cbs.Encoders.EndCurrentPass();

            lock (_commandBuffers)
            {
                int cbIndex = cbs.CommandBufferIndex;

                ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

                Debug.Assert(entry.InUse);
                Debug.Assert(entry.CommandBuffer.NativePtr == cbs.CommandBuffer.NativePtr);
                entry.InUse = false;
                entry.InConsumption = true;
                entry.SubmissionCount++;
                _inUseCount--;

                MTLCommandBuffer commandBuffer = entry.CommandBuffer;
                // Commit order against rental order. Splitting passes only orders work
                // inside one command buffer; between them the GPU follows commit order, and
                // a thread that encodes logically-earlier work but commits later inverts
                // the dependency. Thirty-two command buffers a frame here against
                // MoltenVK's twenty-four, and MoltenVK cannot have this problem at all -
                // kMVKQueueCountPerQueueFamily = 1 leaves it nothing to race with.
                long seq = _rentSeq[cbs.CommandBufferIndex];

                if (seq < _lastCommittedRentSeq)
                {
                    UploadCorrelator.NoteOutOfOrderCommit();
                }

                _lastCommittedRentSeq = seq;

                if ((uint)cbs.CommandBufferIndex < (uint)_commitSeq.Length)
                {
                    _commitSeq[cbs.CommandBufferIndex] = ++_commitCounter;
                }

                commandBuffer.Commit();

                // The one ordering experiment never run: make the whole GPU synchronous.
                // Intra-command-buffer serialisation left 17%; this also closes ordering
                // BETWEEN the ~32 command buffers a frame (and across threads). If the white
                // survives this, it is not an ordering fault of any kind.
                // RYUJINX_METAL_WAIT_EVERY_COMMIT=1.
                if (_waitEveryCommit)
                {
                    commandBuffer.WaitUntilCompleted();
                }

                int ptr = (_queuedIndexesPtr + _queuedCount) % _totalCommandBuffers;
                _queuedIndexes[ptr] = cbIndex;
                _queuedCount++;
            }
        }

        private void WaitAndDecrementRef(int cbIndex)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

            if (entry.InConsumption)
            {
                if (!entry.Fence.IsSignaled())
                {
                    entry.Fence.Wait();
                }

                entry.InConsumption = false;

                // The pool already requests EncoderExecutionStatus on every command
                // buffer; this is where the answer is finally read. A buffer that failed
                // used to wedge silently - fences never signalling, presents frozen, no
                // message anywhere - which is precisely how the store-action experiment's
                // intermittent stall presented. One line here names the violation instead.
                if (entry.CommandBuffer.Status == MTLCommandBufferStatus.Error)
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu,
                        $"command buffer failed: {StringHelper.String(entry.CommandBuffer.Error.LocalizedDescription)}");
                }

                // The buffer is complete, which is the only state in which these two are
                // defined. They are what separates "the GPU is saturated" from "the GPU is
                // starving" - the distinction every remaining performance decision needs
                // and that no counter here has ever measured.
                if (GpuTimeline.Enabled)
                {
                    GpuTimeline.Note(entry.CommandBuffer.GetGpuStartTime(), entry.CommandBuffer.GetGpuEndTime());
                }
            }

            foreach (IAuto dependant in entry.Dependants)
            {
                dependant.DecrementReferenceCount(cbIndex);
            }

            foreach (MultiFenceHolder waitable in entry.Waitables)
            {
                waitable.RemoveFence(cbIndex);
                waitable.RemoveBufferUses(cbIndex);
            }

            entry.Dependants.Clear();
            entry.Waitables.Clear();
            entry.Fence?.Dispose();

            // Balance the retain taken in Use(). The FenceHolder holds its own
            // retain for waiters that outlive this slot.
            ObjcOwnership.Release(entry.CommandBuffer.NativePtr);
            entry.CommandBuffer = default;
        }

        public void Dispose()
        {
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                WaitAndDecrementRef(i);
            }
        }
    }
}
