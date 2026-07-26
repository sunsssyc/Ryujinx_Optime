using Ryujinx.Common.Logging;
using SharpMetal.Metal;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Rolling in-memory record of recent draws, for catching single frame artefacts
    /// in the act. Enabled by RYUJINX_METAL_DRAW_RING=1; records are appended with no
    /// allocation and no I/O (per frame logging is known to perturb the very races
    /// under study), and the whole ring is dumped to a file when the trigger file
    /// appears - an external watcher touches it the moment it sees a bad frame, and
    /// the ring still holds that frame's draws.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class DrawRing
    {
        private const string TriggerPath = "/tmp/ryujinx-draw-ring-dump";
        private const int Capacity = 1 << 15;

        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_DRAW_RING") == "1";

        // Fragment programs whose constant buffer heads are captured per draw.
        private static readonly string[] _watch =
            (Environment.GetEnvironmentVariable("RYUJINX_METAL_RING_WATCH") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private struct Entry
        {
            public ulong Seq;
            public int Frame;
            public string Program;
            public IntPtr Rt0;
            public int RtW;
            public int RtH;
            public byte Topology;
            public int Count;
            public int Instances;
            public ulong Cb1A;
            public ulong Cb1B;
            public ulong Cb4A;
            public ulong Cb4B;
            public bool Watched;
            public IntPtr Tex0;
            public IntPtr Tex1;
            public IntPtr Tex2;
            public IntPtr Tex3;
        }

        private struct ReadbackEntry
        {
            public int Frame;
            public int Offset;
            public int Size;
            public ulong Head;
        }

        private static readonly ReadbackEntry[] _readbacks = new ReadbackEntry[Capacity];
        private static int _rbCursor;

        private static readonly Entry[] _entries = new Entry[Capacity];
        private static int _cursor;
        private static int _frame;
        private static string _frameWall = string.Empty;

        public static void OnPresent()
        {
            _frame++;
            _frameWall = DateTime.Now.ToString("HH:mm:ss.fff");

            if (File.Exists(TriggerPath))
            {
                try
                {
                    File.Delete(TriggerPath);
                }
                catch (IOException)
                {
                    return;
                }

                Dump();
            }
        }

        /// <summary>
        /// Records a guest buffer readback: the game drives per frame CPU decisions
        /// (exposure, visibility, paging) off these, so one stale result explains one
        /// broken frame. Head is the first 8 bytes of the returned data.
        /// </summary>
        public static void RecordReadback(int offset, int size, ulong head)
        {
            ref ReadbackEntry entry = ref _readbacks[_rbCursor & (Capacity - 1)];
            _rbCursor++;

            entry.Frame = _frame;
            entry.Offset = offset;
            entry.Size = size;
            entry.Head = head;
        }

        public static unsafe void Record(EncoderState state, Ryujinx.Graphics.GAL.PrimitiveTopology topology, int count, int instances)
        {
            Program program = state.RenderProgram;
            Texture rt0 = state.RenderTargets[0];

            ref Entry entry = ref _entries[_cursor & (Capacity - 1)];
            _cursor++;

            entry.Seq = Ryujinx.Graphics.GAL.DrawDiagnostics.BindSequence;
            entry.Frame = _frame;
            entry.Program = program?.DebugLabel;
            entry.Rt0 = rt0 != null ? rt0.GetHandle().NativePtr : IntPtr.Zero;
            entry.RtW = rt0?.Width ?? 0;
            entry.RtH = rt0?.Height ?? 0;
            entry.Topology = (byte)topology;
            entry.Count = count;
            entry.Instances = instances;
            entry.Watched = false;

            entry.Tex0 = entry.Tex1 = entry.Tex2 = entry.Tex3 = IntPtr.Zero;

            // Fullscreen passes: record which textures are actually bound, so a flash
            // frame can be compared against neighbours - a different handle means the
            // alias/pool resolution picked another texture, the same handle means the
            // content itself was wrong.
            if (count <= 6 && instances == 1)
            {
                int found = 0;

                for (int i = 0; i < state.TextureRefs.Length && found < 4; i++)
                {
                    TextureBase storage = state.TextureRefs[i].Storage;

                    if (storage != null)
                    {
                        IntPtr handle = storage.GetHandle().NativePtr;

                        switch (found++)
                        {
                            case 0: entry.Tex0 = handle; break;
                            case 1: entry.Tex1 = handle; break;
                            case 2: entry.Tex2 = handle; break;
                            case 3: entry.Tex3 = handle; break;
                        }
                    }
                }
            }

            if (_watch.Length != 0 && entry.Program != null && Array.IndexOf(_watch, entry.Program) >= 0)
            {
                entry.Watched = true;
                (entry.Cb1A, entry.Cb1B) = ReadUniformHead(state, 20); // fp_c1
                (entry.Cb4A, entry.Cb4B) = ReadUniformHead(state, 23); // fp_c4
            }
        }

        private static unsafe (ulong, ulong) ReadUniformHead(EncoderState state, int binding)
        {
            if ((uint)binding >= (uint)state.UniformBufferRefs.Length)
            {
                return (0, 0);
            }

            BufferRef bufferRef = state.UniformBufferRefs[binding];

            if (bufferRef.Buffer == null)
            {
                return (0, 0);
            }

            MTLBuffer buffer = bufferRef.Buffer.GetUnsafe().Value;

            if (buffer.NativePtr == IntPtr.Zero)
            {
                return (0, 0);
            }

            int offset = bufferRef.Range?.Offset ?? 0;

            if ((ulong)(offset + 16) > buffer.Length)
            {
                return (0, 0);
            }

            ulong* data = (ulong*)((byte*)buffer.Contents + offset);

            return (data[0], data[1]);
        }

        private static void Dump()
        {
            string path = $"/tmp/ryujinx-draw-ring-{DateTime.Now:HHmmss}.txt";

            try
            {
                StringBuilder text = new();

                text.AppendLine($"# dumped at frame {_frame} wall {_frameWall}");

                int rbStart = Math.Max(0, _rbCursor - Capacity);

                for (int i = rbStart; i < _rbCursor; i++)
                {
                    ref ReadbackEntry rb = ref _readbacks[i & (Capacity - 1)];

                    text.AppendLine($"rb f={rb.Frame} off=0x{rb.Offset:X} size={rb.Size} head={rb.Head:X16}");
                }

                int start = Math.Max(0, _cursor - Capacity);

                for (int i = start; i < _cursor; i++)
                {
                    ref Entry entry = ref _entries[i & (Capacity - 1)];

                    text.Append($"f={entry.Frame} s={entry.Seq} p={entry.Program} rt=0x{entry.Rt0:X}/{entry.RtW}x{entry.RtH} " +
                        $"topo={entry.Topology} n={entry.Count} inst={entry.Instances}");

                    if (entry.Watched)
                    {
                        text.Append($" c1={entry.Cb1A:X16}{entry.Cb1B:X16} c4={entry.Cb4A:X16}{entry.Cb4B:X16}");
                    }

                    if (entry.Tex0 != IntPtr.Zero)
                    {
                        text.Append($" tex={entry.Tex0:X},{entry.Tex1:X},{entry.Tex2:X},{entry.Tex3:X}");
                    }

                    text.AppendLine();
                }

                File.WriteAllText(path, text.ToString());

                Logger.Warning?.PrintMsg(LogClass.Gpu, $"draw ring dumped: {path}");
            }
            catch (Exception exception)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"draw ring dump failed: {exception.Message}");
            }
        }
    }
}
