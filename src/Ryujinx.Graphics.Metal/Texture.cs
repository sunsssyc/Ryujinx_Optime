using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class Texture : TextureBase, ITexture
    {
        private Auto<DisposableTexture> _identitySwizzleHandle;
        private readonly bool _identityIsDifferent;

        // FrameProbe identifies writers by native pointer. A view gets its own
        // MTLTexture pointer, so without this map a write through a view looks like a
        // write to a texture nobody ever presents - which is how "nobody writes the
        // presented image" got reported once and had to be retracted.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr> _viewRoots = new();

        public IntPtr ViewRootPtr { get; private set; }

        /// <summary>
        /// One identity per underlying resource, set unconditionally at construction: the
        /// storage this texture was created from, or its own storage when it is not a
        /// view. Ownership comparisons must use this. Comparing MTLTexture handles does
        /// not work - a view, the identity view and the swizzled view of one resource all
        /// carry different pointers, so a write through any of them looks like a write to
        /// a texture nobody touches. That failure produced three separate "nothing writes
        /// this" conclusions in the white-flash investigation, each of them wrong.
        /// </summary>
        public IntPtr CanonicalPtr { get; private set; }

        public static IntPtr ResolveViewRoot(IntPtr native)
        {
            IntPtr current = native;

            for (int depth = 0; depth < 8; depth++)
            {
                if (!_viewRoots.TryGetValue(current, out IntPtr parent) || parent == current)
                {
                    break;
                }

                current = parent;
            }

            return current;
        }

        public Texture(MTLDevice device, MetalRenderer renderer, Pipeline pipeline, TextureCreateInfo info) : base(device, renderer, pipeline, info)
        {
            MTLPixelFormat pixelFormat = FormatTable.GetFormat(Info.Format);

            MTLTextureDescriptor descriptor = new()
            {
                PixelFormat = pixelFormat,
                Usage = GetTextureUsage(Info, pixelFormat),
                SampleCount = (ulong)Info.Samples,
                TextureType = Info.Target.Convert(),
                Width = (ulong)Info.Width,
                Height = (ulong)Info.Height,
                MipmapLevelCount = (ulong)Info.Levels
            };

            if (info.Target == Target.Texture3D)
            {
                descriptor.Depth = (ulong)Info.Depth;
            }
            else if (info.Target != Target.Cubemap)
            {
                if (info.Target == Target.CubemapArray)
                {
                    descriptor.ArrayLength = (ulong)(Info.Depth / 6);
                }
                else
                {
                    descriptor.ArrayLength = (ulong)Info.Depth;
                }
            }

            MTLTextureSwizzleChannels swizzle = GetSwizzle(info, descriptor.PixelFormat);

            _identitySwizzleHandle = new Auto<DisposableTexture>(new DisposableTexture(Device.NewTexture(descriptor)));

            // A freshly created texture holds undefined content, and the elision
            // experiment proved the flash IS undefined content crystallised by the first
            // Load/Store pass. This logs every creation of the scene-shaped class with the
            // probe's frame number, so creations can be correlated against flash frames
            // directly: damage at creation frame N should display at N+1.
            if (PresentProbe.Enabled && Info.Width >= 1500 && Info.Width <= 1700 &&
                pixelFormat == MTLPixelFormat.RG11B10Float)
            {
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"texcreate f={PresentProbe.Frame} {Info.Width}x{Info.Height} RG11B10Float " +
                    $"handle=0x{GetHandle().NativePtr:X}");
            }

            // A new MTLTexture holds undefined content, and on this driver undefined tile
            // data reads as near-white - the exact constant the flash carries. The scene
            // texture the composite samples was created at level load and never drawn
            // directly; its birth content circulates through the cache's alias sync. Zero
            // it at birth so no undefined content exists to circulate. Render-target
            // colour textures only, and the cost is one fill per creation.
            // RYUJINX_METAL_NO_CLEAR_INIT=1 restores the old behaviour.
            if (!_noClearInit && !Info.Format.IsDepthOrStencil &&
                (GetHandle().Usage & MTLTextureUsage.RenderTarget) != 0 &&
                Info.Target == Target.Texture2D && Info.Levels == 1 && Info.Samples <= 1)
            {
                ClearToZeroAtBirth(pipeline);
            }

            if (SwizzleIsIdentity(swizzle))
            {
                MtlTextureAuto = _identitySwizzleHandle;
            }
            else
            {
                MTLTexture identityTexture = _identitySwizzleHandle.GetUnsafe().Value;
                SetHandle(CreateDefaultView(identityTexture, swizzle, descriptor), _identitySwizzleHandle);
                _identityIsDifferent = true;
            }

            MtlFormat = pixelFormat;

            // Not a view: its own storage is the canonical identity.
            CanonicalPtr = _identitySwizzleHandle.GetUnsafe().Value.NativePtr;

            StainOnCreate();

            descriptor.Dispose();
        }

        /// <summary>
        /// Fills a newly created scene-sized RG11B10Float texture with a constant, so that
        /// whether anything ever writes it can be answered positively instead of by
        /// enumerating hooks.
        ///
        /// The composite samples one of these and reads the scene out of it on ordinary
        /// frames, yet CopyTo, the render blit and SetData all report never touching it.
        /// A negative assembled from hooks is not proof - this file records the same
        /// conclusion being overturned once already, when four hook types with positive
        /// controls agreed nothing wrote a texture and staining it green showed otherwise.
        ///
        /// 0x55 repeated packs to a constant that is neither the scene nor white, and a
        /// byte fill is enough to place it. Read the screen after:
        ///
        ///   ordinary frames still show the scene -> a writer exists and is unhooked
        ///   flat frames show the stain           -> a flat frame reads this texture before
        ///                                           anything has written it
        ///
        /// RYUJINX_METAL_STAIN_SCENE=1, off by default - it destroys the image of any
        /// texture it touches until something overwrites it.
        /// </summary>
        private static readonly bool _stainScene =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_STAIN_SCENE") == "1";

        private void StainOnCreate()
        {
            if (!_stainScene || Info.Width != 1600 || MtlFormat != MTLPixelFormat.RG11B10Float)
            {
                return;
            }

            int bytes = Info.Width * Info.Height * 4;
            MemoryOwner<byte> stain = MemoryOwner<byte>.Rent(bytes);
            stain.Span.Fill(0x55);

            Logger.Warning?.PrintMsg(LogClass.Gpu,
                $"stain: filling new {Info.Width}x{Info.Height} {MtlFormat} 0x{CanonicalPtr:X} with 0x55");

            SetData(stain);
        }

        /// <summary>
        /// Textures were previously created with MTLTextureUsage.Unknown, which the
        /// validation layer rejects outright ("usage must be set", thousands of hits
        /// per frame) and which leaves rendering into them, writing them as images
        /// and reinterpreting them through views formally undefined - a systemic
        /// source of intermittent empty/stale content. Declare every usage the
        /// emulator can exercise; guest textures are freely reused as render
        /// targets, storage images and reinterpreted views.
        /// </summary>
        private static MTLTextureUsage GetTextureUsage(TextureCreateInfo info, MTLPixelFormat pixelFormat)
        {
            MTLTextureUsage usage = MTLTextureUsage.ShaderRead | MTLTextureUsage.PixelFormatView;

            if (info.Format.IsDepthOrStencil)
            {
                // Depth/stencil formats are renderable but not shader-writable.
                usage |= MTLTextureUsage.RenderTarget;
            }
            else if (!info.IsCompressed)
            {
                // Compressed formats can be neither rendered to nor written from
                // shaders on Metal; other color formats may be used both ways.
                usage |= MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderWrite;
            }

            return usage;
        }

        private static readonly bool _noClearInit =
            Environment.GetEnvironmentVariable("RYUJINX_METAL_NO_CLEAR_INIT") == "1";

        /// <summary>
        /// Writes zeros over the whole of a newly created colour target through a render
        /// pass clear, so its first Load brings in defined content instead of tile
        /// garbage. Uses its own descriptor and encoder: the pipeline's current pass
        /// state must not be disturbed by texture creation.
        /// </summary>
        private void ClearToZeroAtBirth(Pipeline pipeline)
        {
            using MTLRenderPassDescriptor descriptor = new();

            MTLRenderPassColorAttachmentDescriptor attachment = descriptor.ColorAttachments.Object(0);
            attachment.Texture = GetHandle();
            attachment.LoadAction = MTLLoadAction.Clear;
            attachment.StoreAction = MTLStoreAction.Store;
            attachment.ClearColor = new MTLClearColor { red = 0, green = 0, blue = 0, alpha = 0 };

            pipeline.EndCurrentPass();

            MTLRenderCommandEncoder encoder = pipeline.CommandBuffer.RenderCommandEncoder(descriptor);
            encoder.EndEncoding();
        }

        public Texture(MTLDevice device, MetalRenderer renderer, Pipeline pipeline, TextureCreateInfo info, MTLTexture sourceTexture, int firstLayer, int firstLevel) :
            this(device, renderer, pipeline, info, sourceTexture, null, firstLayer, firstLevel)
        {
        }

        private Texture(
            MTLDevice device,
            MetalRenderer renderer,
            Pipeline pipeline,
            TextureCreateInfo info,
            Auto<DisposableTexture> sourceTexture,
            int firstLayer,
            int firstLevel) :
            this(device, renderer, pipeline, info, sourceTexture.GetUnsafe().Value, sourceTexture, firstLayer, firstLevel)
        {
        }

        private Texture(
            MTLDevice device,
            MetalRenderer renderer,
            Pipeline pipeline,
            TextureCreateInfo info,
            MTLTexture sourceTexture,
            Auto<DisposableTexture> sourceTextureAuto,
            int firstLayer,
            int firstLevel) : base(device, renderer, pipeline, info)
        {
            MTLPixelFormat pixelFormat = FormatTable.GetFormat(Info.Format);

            if (info.DepthStencilMode == DepthStencilMode.Stencil)
            {
                pixelFormat = pixelFormat switch
                {
                    MTLPixelFormat.Depth32FloatStencil8 => MTLPixelFormat.X32Stencil8,
                    MTLPixelFormat.Depth24UnormStencil8 => MTLPixelFormat.X24Stencil8,
                    _ => pixelFormat
                };
            }

            MTLTextureType textureType = Info.Target.Convert();
            NSRange levels;
            levels.location = (ulong)firstLevel;
            levels.length = (ulong)Info.Levels;
            NSRange slices;
            slices.location = (ulong)firstLayer;
            slices.length = textureType == MTLTextureType.Type3D ? 1 : (ulong)info.GetDepthOrLayers();

            MTLTextureSwizzleChannels swizzle = GetSwizzle(info, pixelFormat);

            MTLTexture identityTexture = sourceTexture.NewTextureView(pixelFormat, textureType, levels, slices);
            _identitySwizzleHandle = sourceTextureAuto != null
                ? new Auto<DisposableTexture>(new DisposableTexture(identityTexture), null, sourceTextureAuto)
                : new Auto<DisposableTexture>(new DisposableTexture(identityTexture));

            if (SwizzleIsIdentity(swizzle))
            {
                MtlTextureAuto = _identitySwizzleHandle;
            }
            else
            {
                MTLTexture swizzledTexture = sourceTexture.NewTextureView(pixelFormat, textureType, levels, slices, swizzle);

                if (sourceTextureAuto != null)
                {
                    SetHandle(swizzledTexture, sourceTextureAuto);
                }
                else
                {
                    SetHandle(swizzledTexture);
                }

                _identityIsDifferent = true;
            }

            MtlFormat = pixelFormat;
            FirstLayer = firstLayer;
            FirstLevel = firstLevel;
            CanonicalPtr = sourceTexture.NativePtr;

            // Instrument-only bookkeeping: keep it off the normal path entirely so the
            // probe cannot change the behaviour it is measuring. HdrPassProbe needs the
            // same map: comparing raw handles instead of view roots is what made writes
            // through a view look like writes to a texture nobody touches.
            if (FrameProbe.Enabled || HdrPassProbe.Enabled)
            {
                ViewRootPtr = ResolveViewRoot(sourceTexture.NativePtr);

                if (ViewRootPtr != IntPtr.Zero)
                {
                    if (identityTexture.NativePtr != IntPtr.Zero)
                    {
                        _viewRoots[identityTexture.NativePtr] = ViewRootPtr;
                    }

                    MTLTexture handle = GetIdentityHandle();

                    if (handle.NativePtr != IntPtr.Zero)
                    {
                        _viewRoots[handle.NativePtr] = ViewRootPtr;
                    }
                }
            }
        }

        public void PopulateRenderPassAttachment(MTLRenderPassColorAttachmentDescriptor descriptor, CommandBufferScoped cbs)
        {
            descriptor.Texture = GetIdentityHandle(cbs);
        }

        public override MTLTexture GetIdentityHandle()
        {
            return Valid ? _identitySwizzleHandle.GetUnsafe().Value : new MTLTexture(IntPtr.Zero);
        }

        public override MTLTexture GetIdentityHandle(CommandBufferScoped cbs)
        {
            return Valid ? _identitySwizzleHandle.Get(cbs).Value : new MTLTexture(IntPtr.Zero);
        }

        private MTLTexture CreateDefaultView(MTLTexture texture, MTLTextureSwizzleChannels swizzle, MTLTextureDescriptor descriptor)
        {
            NSRange levels;
            levels.location = 0;
            levels.length = (ulong)Info.Levels;
            NSRange slices;
            slices.location = 0;
            slices.length = Info.Target == Target.Texture3D ? 1 : (ulong)Info.GetDepthOrLayers();

            return texture.NewTextureView(descriptor.PixelFormat, descriptor.TextureType, levels, slices, swizzle);
        }

        private bool SwizzleIsIdentity(MTLTextureSwizzleChannels swizzle)
        {
            return swizzle.red == MTLTextureSwizzle.Red &&
                   swizzle.green == MTLTextureSwizzle.Green &&
                   swizzle.blue == MTLTextureSwizzle.Blue &&
                   swizzle.alpha == MTLTextureSwizzle.Alpha;
        }

        private MTLTextureSwizzleChannels GetSwizzle(TextureCreateInfo info, MTLPixelFormat pixelFormat)
        {
            MTLTextureSwizzle swizzleR = Info.SwizzleR.Convert();
            MTLTextureSwizzle swizzleG = Info.SwizzleG.Convert();
            MTLTextureSwizzle swizzleB = Info.SwizzleB.Convert();
            MTLTextureSwizzle swizzleA = Info.SwizzleA.Convert();

            if (info.Format == Format.R5G5B5A1Unorm ||
                info.Format == Format.R5G5B5X1Unorm ||
                info.Format == Format.R5G6B5Unorm)
            {
                (swizzleB, swizzleR) = (swizzleR, swizzleB);
            }
            else if (pixelFormat == MTLPixelFormat.ABGR4Unorm || info.Format == Format.A1B5G5R5Unorm)
            {
                MTLTextureSwizzle tempB = swizzleB;
                MTLTextureSwizzle tempA = swizzleA;

                swizzleB = swizzleG;
                swizzleA = swizzleR;
                swizzleR = tempA;
                swizzleG = tempB;
            }

            return new MTLTextureSwizzleChannels
            {
                red = swizzleR,
                green = swizzleG,
                blue = swizzleB,
                alpha = swizzleA
            };
        }

        private static readonly System.Collections.Generic.HashSet<(string, Format, Format)> _loggedCopyGaps = [];

        // These texture-to-texture copy variants are not implemented yet. They were
        // silently dropped before, leaving destination textures with stale or empty
        // content (missing baked foliage pages, frozen gloom in TOTK). Log each
        // distinct case once so real-game requirements are visible in session logs.
        private static void LogUnimplementedCopy(string kind, TextureBase src, TextureBase dst)
        {
            lock (_loggedCopyGaps)
            {
                if (_loggedCopyGaps.Add((kind, src.Info.Format, dst.Info.Format)))
                {
                    Logger.Warning?.PrintMsg(
                        LogClass.Gpu,
                        $"Unimplemented texture copy ({kind}): {src.Info.Format} {src.Info.Target} -> {dst.Info.Format} {dst.Info.Target}");
                }
            }
        }

        public void CopyTo(ITexture destination, int firstLayer, int firstLevel)
        {
            OpRing.NoteTexCopy(GetHandle().NativePtr, (destination as Texture)?.GetHandle().NativePtr ?? IntPtr.Zero);
            HdrPassProbe.NoteSceneCopy(this, destination as Texture);
            // The flat white 1920x1080 surface is not written by any render pass - it
            // never appears in the per-target pass census - so it has to arrive by copy.
            // This names the copies that land on a full resolution destination.
            if (HdrPassProbe.Enabled && destination is Texture dstTex && dstTex.Width >= 1900)
            {
                HdrPassProbe.NoteCopy(Width, Height, MtlFormat.ToString(),
                    dstTex.Width, dstTex.Height, dstTex.MtlFormat.ToString());
            }

            HdrPassProbe.NoteNonRenderWrite(destination as Texture, "copy");
            HdrPassProbe.NoteCopyInto(destination as Texture);

            CommandBufferScoped cbs = Pipeline.Cbs;

            TextureBase src = this;
            TextureBase dst = (TextureBase)destination;

            if (!Valid || !dst.Valid)
            {
                return;
            }

            MTLTexture srcImage = GetIdentityHandle(cbs);
            MTLTexture dstImage = dst.GetIdentityHandle(cbs);

            if (!dst.Info.Target.IsMultisample && Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("ms-to-nonms", src, dst);
            }
            else if (dst.Info.Target.IsMultisample && !Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("nonms-to-ms", src, dst);
            }
            else if (dst.Info.BytesPerPixel != Info.BytesPerPixel)
            {
                LogUnimplementedCopy("incompatible-bpp", src, dst);
            }
            else if (src.Info.Format.IsDepthOrStencil != dst.Info.Format.IsDepthOrStencil)
            {
                LogUnimplementedCopy("depth-color", src, dst);
            }
            else
            {
                TextureCopy.Copy(
                    cbs,
                    srcImage,
                    dstImage,
                    src.Info,
                    dst.Info,
                    0,
                    firstLayer,
                    0,
                    firstLevel);
            }
        }

        public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel)
        {
            OpRing.NoteTexCopy(GetHandle().NativePtr, (destination as Texture)?.GetHandle().NativePtr ?? IntPtr.Zero);
            HdrPassProbe.NoteSceneCopy(this, destination as Texture);
            // The flat white 1920x1080 surface is not written by any render pass - it
            // never appears in the per-target pass census - so it has to arrive by copy.
            // This names the copies that land on a full resolution destination.
            if (HdrPassProbe.Enabled && destination is Texture dstTex && dstTex.Width >= 1900)
            {
                HdrPassProbe.NoteCopy(Width, Height, MtlFormat.ToString(),
                    dstTex.Width, dstTex.Height, dstTex.MtlFormat.ToString());
            }

            HdrPassProbe.NoteNonRenderWrite(destination as Texture, "copyLayer");
            HdrPassProbe.NoteCopyInto(destination as Texture);

            CommandBufferScoped cbs = Pipeline.Cbs;

            TextureBase src = this;
            TextureBase dst = (TextureBase)destination;

            if (!Valid || !dst.Valid)
            {
                return;
            }

            MTLTexture srcImage = GetIdentityHandle(cbs);
            MTLTexture dstImage = dst.GetIdentityHandle(cbs);

            if (!dst.Info.Target.IsMultisample && Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("ms-to-nonms", src, dst);
            }
            else if (dst.Info.Target.IsMultisample && !Info.Target.IsMultisample)
            {
                LogUnimplementedCopy("nonms-to-ms", src, dst);
            }
            else if (dst.Info.BytesPerPixel != Info.BytesPerPixel)
            {
                LogUnimplementedCopy("incompatible-bpp", src, dst);
            }
            else if (src.Info.Format.IsDepthOrStencil != dst.Info.Format.IsDepthOrStencil)
            {
                LogUnimplementedCopy("depth-color", src, dst);
            }
            else
            {
                TextureCopy.Copy(
                    cbs,
                    srcImage,
                    dstImage,
                    src.Info,
                    dst.Info,
                    srcLayer,
                    dstLayer,
                    srcLevel,
                    dstLevel,
                    1,
                    1);
            }
        }

        public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter)
        {
            OpRing.NoteTexCopy(GetHandle().NativePtr, (destination as Texture)?.GetHandle().NativePtr ?? IntPtr.Zero);
            HdrPassProbe.NoteSceneCopy(this, destination as Texture);
            // The flat white 1920x1080 surface is not written by any render pass - it
            // never appears in the per-target pass census - so it has to arrive by copy.
            // This names the copies that land on a full resolution destination.
            if (HdrPassProbe.Enabled && destination is Texture dstTex && dstTex.Width >= 1900)
            {
                HdrPassProbe.NoteCopy(Width, Height, MtlFormat.ToString(),
                    dstTex.Width, dstTex.Height, dstTex.MtlFormat.ToString());
            }

            HdrPassProbe.NoteNonRenderWrite(destination as Texture, "blitScaled");
            HdrPassProbe.NoteCopyInto(destination as Texture);

            if (!Renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Metal doesn't currently support scaled blit on background thread.");

                return;
            }

            Texture dst = (Texture)destination;

            bool isDepthOrStencil = dst.Info.Format.IsDepthOrStencil;

            Pipeline.Blit(this, dst, srcRegion, dstRegion, isDepthOrStencil, linearFilter);
        }

        public void CopyTo(BufferRange range, int layer, int level, int stride)
        {
            CommandBufferScoped cbs = Pipeline.Cbs;

            int outSize = Info.GetMipSize(level);
            int hostSize = GetBufferDataLength(outSize);

            int offset = range.Offset;

            Auto<DisposableBuffer> autoBuffer = Renderer.BufferManager.GetBuffer(range.Handle, true);
            MTLBuffer mtlBuffer = autoBuffer.Get(cbs, range.Offset, outSize).Value;

            if (PrepareOutputBuffer(cbs, hostSize, mtlBuffer, out MTLBuffer copyToBuffer, out BufferHolder tempCopyHolder))
            {
                offset = 0;
            }

            CopyFromOrToBuffer(cbs, copyToBuffer, GetIdentityHandle(cbs), hostSize, true, layer, level, 1, 1, singleSlice: true, offset, stride);

            if (tempCopyHolder != null)
            {
                CopyDataToOutputBuffer(cbs, tempCopyHolder, autoBuffer, hostSize, range.Offset);
                tempCopyHolder.Dispose();
            }
        }

        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            return new Texture(Device, Renderer, Pipeline, info, _identitySwizzleHandle, firstLayer, firstLevel);
        }

        private void CopyDataToBuffer(Span<byte> storage, ReadOnlySpan<byte> input)
        {
            if (NeedsD24S8Conversion())
            {
                FormatConverter.ConvertD24S8ToD32FS8(storage, input);
                return;
            }

            input.CopyTo(storage);
        }

        private ReadOnlySpan<byte> GetDataFromBuffer(ReadOnlySpan<byte> storage, int size, Span<byte> output)
        {
            if (NeedsD24S8Conversion())
            {
                if (output.IsEmpty)
                {
                    output = new byte[GetBufferDataLength(size)];
                }

                FormatConverter.ConvertD32FS8ToD24S8(output, storage);
                return output;
            }

            return storage;
        }

        private bool PrepareOutputBuffer(CommandBufferScoped cbs, int hostSize, MTLBuffer target, out MTLBuffer copyTarget, out BufferHolder copyTargetHolder)
        {
            if (NeedsD24S8Conversion())
            {
                copyTargetHolder = Renderer.BufferManager.Create(hostSize);
                copyTarget = copyTargetHolder.GetBuffer().Get(cbs, 0, hostSize).Value;

                return true;
            }

            copyTarget = target;
            copyTargetHolder = null;

            return false;
        }

        private void CopyDataToOutputBuffer(CommandBufferScoped cbs, BufferHolder hostData, Auto<DisposableBuffer> copyTarget, int hostSize, int dstOffset)
        {
            if (NeedsD24S8Conversion())
            {
                Renderer.HelperShader.ConvertD32S8ToD24S8(cbs, hostData, copyTarget, hostSize / (2 * sizeof(int)), dstOffset);
            }
        }

        private bool NeedsD24S8Conversion()
        {
            return FormatTable.IsD24S8(Info.Format) && MtlFormat == MTLPixelFormat.Depth32FloatStencil8;
        }

        public void CopyFromOrToBuffer(
            CommandBufferScoped cbs,
            MTLBuffer buffer,
            MTLTexture image,
            int size,
            bool to,
            int dstLayer,
            int dstLevel,
            int dstLayers,
            int dstLevels,
            bool singleSlice,
            int offset = 0,
            int stride = 0)
        {
            MTLBlitCommandEncoder blitCommandEncoder = cbs.Encoders.EnsureBlitEncoder();

            bool is3D = Info.Target == Target.Texture3D;
            int width = Math.Max(1, Info.Width >> dstLevel);
            int height = Math.Max(1, Info.Height >> dstLevel);
            int depth = is3D && !singleSlice ? Math.Max(1, Info.Depth >> dstLevel) : 1;
            int layers = dstLayers;
            int levels = dstLevels;

            for (int oLevel = 0; oLevel < levels; oLevel++)
            {
                int level = oLevel + dstLevel;
                int mipSize = Info.GetMipSize2D(level);

                int mipSizeLevel = GetBufferDataLength(is3D && !singleSlice
                    ? Info.GetMipSize(level)
                    : mipSize * dstLayers);

                int endOffset = offset + mipSizeLevel;

                if ((uint)endOffset > (uint)size)
                {
                    break;
                }

                for (int oLayer = 0; oLayer < layers; oLayer++)
                {
                    int layer = !is3D ? dstLayer + oLayer : 0;
                    int z = is3D ? dstLayer + oLayer : 0;

                    if (to)
                    {
                        blitCommandEncoder.CopyFromTexture(
                            image,
                            (ulong)layer,
                            (ulong)level,
                            new MTLOrigin { z = (ulong)z },
                            new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 },
                            buffer,
                            (ulong)offset,
                            (ulong)Info.GetMipStride(level),
                            (ulong)mipSize
                        );
                    }
                    else
                    {
                        blitCommandEncoder.CopyFromBuffer(
                            buffer,
                            (ulong)offset,
                            (ulong)Info.GetMipStride(level),
                            (ulong)mipSize,
                            new MTLSize { width = (ulong)width, height = (ulong)height, depth = 1 },
                            image,
                            (ulong)(layer + oLayer),
                            (ulong)level,
                            new MTLOrigin { z = (ulong)z }
                        );
                    }

                    offset += mipSize;
                }

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (Info.Target == Target.Texture3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }
        }

        private ReadOnlySpan<byte> GetData(CommandBufferPool cbp, PersistentFlushBuffer flushBuffer)
        {
            int size = 0;

            for (int level = 0; level < Info.Levels; level++)
            {
                size += Info.GetMipSize(level);
            }

            size = GetBufferDataLength(size);

            Span<byte> result = flushBuffer.GetTextureData(cbp, this, size);

            return GetDataFromBuffer(result, size, result);
        }

        private ReadOnlySpan<byte> GetData(CommandBufferPool cbp, PersistentFlushBuffer flushBuffer, int layer, int level)
        {
            int size = GetBufferDataLength(Info.GetMipSize(level));

            Span<byte> result = flushBuffer.GetTextureData(cbp, this, size, layer, level);

            return GetDataFromBuffer(result, size, result);
        }

        private static readonly bool _logTexReadback =
            System.Environment.GetEnvironmentVariable("RYUJINX_METAL_LOG_READBACK") == "1";

        public PinnedSpan<byte> GetData()
        {
            if (_logTexReadback)
            {
                Ryujinx.Common.Logging.Logger.Warning?.PrintMsg(
                    Ryujinx.Common.Logging.LogClass.Gpu,
                    $"readback tex target={Info.Target} fmt={Info.Format} {Info.Width}x{Info.Height}x{Info.Depth} levels={Info.Levels}");
            }

            BackgroundResource resources = Renderer.BackgroundResources.Get();

            if (Renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                Renderer.FlushAllCommands();

                return PinnedSpan<byte>.UnsafeFromSpan(GetData(Renderer.CommandBufferPool, resources.GetFlushBuffer()));
            }

            return PinnedSpan<byte>.UnsafeFromSpan(GetData(resources.GetPool(), resources.GetFlushBuffer()));
        }

        public PinnedSpan<byte> GetData(int layer, int level)
        {
            BackgroundResource resources = Renderer.BackgroundResources.Get();

            if (Renderer.CommandBufferPool.OwnedByCurrentThread)
            {
                Renderer.FlushAllCommands();

                return PinnedSpan<byte>.UnsafeFromSpan(GetData(Renderer.CommandBufferPool, resources.GetFlushBuffer(), layer, level));
            }

            return PinnedSpan<byte>.UnsafeFromSpan(GetData(resources.GetPool(), resources.GetFlushBuffer(), layer, level));
        }

        public void SetData(MemoryOwner<byte> data)
        {
            OpRing.NoteSetData(GetHandle().NativePtr);
            HdrPassProbe.NoteSceneCopy(null, this);
            HdrPassProbe.NoteNonRenderWrite(this, "upload");

            CommandBufferScoped cbs = Pipeline.Cbs;
            MTLBlitCommandEncoder blitCommandEncoder = Pipeline.GetOrCreateBlitEncoder();

            Span<byte> dataSpan = data.Memory.Span;

            BufferHolder buffer = Renderer.BufferManager.Create(dataSpan.Length);
            buffer.SetDataUnchecked(0, dataSpan);
            MTLBuffer mtlBuffer = buffer.GetBuffer(false).Get(cbs).Value;
            MTLTexture image = GetIdentityHandle(cbs);

            int width = Info.Width;
            int height = Info.Height;
            int depth = Info.Depth;
            int levels = Info.Levels;
            int layers = Info.GetLayers();
            bool is3D = Info.Target == Target.Texture3D;

            int offset = 0;

            for (int level = 0; level < levels; level++)
            {
                int mipSize = Info.GetMipSize2D(level);
                int endOffset = offset + mipSize;

                if ((uint)endOffset > (uint)dataSpan.Length)
                {
                    // Truncated upload: without this the staging BufferHolder (and
                    // its device buffer sized for the full upload) leaked.
                    buffer.Dispose();

                    return;
                }

                for (int layer = 0; layer < layers; layer++)
                {
                    blitCommandEncoder.CopyFromBuffer(
                        mtlBuffer,
                        (ulong)offset,
                        (ulong)Info.GetMipStride(level),
                        (ulong)mipSize,
                        new MTLSize { width = (ulong)width, height = (ulong)height, depth = is3D ? (ulong)depth : 1 },
                        image,
                        (ulong)layer,
                        (ulong)level,
                        new MTLOrigin()
                    );

                    offset += mipSize;
                }

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (is3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }

            // Cleanup
            buffer.Dispose();
        }

        private void SetData(ReadOnlySpan<byte> data, int layer, int level, int layers, int levels, bool singleSlice)
        {
            int bufferDataLength = GetBufferDataLength(data.Length);

            using BufferHolder bufferHolder = Renderer.BufferManager.Create(bufferDataLength);

            // TODO: loadInline logic

            CommandBufferScoped cbs = Pipeline.Cbs;

            CopyDataToBuffer(bufferHolder.GetDataStorage(0, bufferDataLength), data);

            MTLBuffer buffer = bufferHolder.GetBuffer().Get(cbs).Value;
            MTLTexture image = GetIdentityHandle(cbs);

            CopyFromOrToBuffer(cbs, buffer, image, bufferDataLength, false, layer, level, layers, levels, singleSlice);
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            OpRing.NoteSetData(GetHandle().NativePtr);
            HdrPassProbe.NoteSceneCopy(null, this);
            SetData(data.Memory.Span, layer, level, 1, 1, singleSlice: true);

            data.Dispose();
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            OpRing.NoteSetData(GetHandle().NativePtr);
            HdrPassProbe.NoteSceneCopy(null, this);
            CommandBufferScoped cbs = Pipeline.Cbs;
            MTLBlitCommandEncoder blitCommandEncoder = Pipeline.GetOrCreateBlitEncoder();

            ulong bytesPerRow = (ulong)Info.GetMipStride(level);
            ulong bytesPerImage = 0;
            MTLTexture image = GetIdentityHandle(cbs);

            if (image.TextureType == MTLTextureType.Type3D)
            {
                bytesPerImage = bytesPerRow * (ulong)Info.Height;
            }

            Span<byte> dataSpan = data.Memory.Span;

            BufferHolder buffer = Renderer.BufferManager.Create(dataSpan.Length);
            buffer.SetDataUnchecked(0, dataSpan);
            MTLBuffer mtlBuffer = buffer.GetBuffer(false).Get(cbs).Value;

            blitCommandEncoder.CopyFromBuffer(
                mtlBuffer,
                0,
                bytesPerRow,
                bytesPerImage,
                new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
                image,
                (ulong)layer,
                (ulong)level,
                new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y }
            );

            // Cleanup
            buffer.Dispose();
        }

        private int GetBufferDataLength(int length)
        {
            if (NeedsD24S8Conversion())
            {
                return length * 2;
            }

            return length;
        }

        public void SetStorage(BufferRange buffer)
        {
            throw new NotImplementedException();
        }

        public override void Release()
        {
            if (TryInvalidate())
            {
                if (_identityIsDifferent)
                {
                    _identitySwizzleHandle.Dispose();
                }

                DisposeHandle();
            }
        }
    }
}
