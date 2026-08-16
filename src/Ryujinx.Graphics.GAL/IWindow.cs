using Ryujinx.Common.Configuration;
using System;

namespace Ryujinx.Graphics.GAL
{
    public interface IWindow
    {
        void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback);

        /// <summary>Diagnostic: the game's true final render target for this frame.</summary>
        void NoteGameFinalTarget(ITexture texture) { }

        /// <summary>Diagnostic: a view of the game's final render target.</summary>
        void NoteGameFinalTargetView(ITexture texture) { }

        void SetSize(int width, int height);

        void ChangeVSyncMode(VSyncMode vSyncMode);

        void SetAntiAliasing(AntiAliasing antialiasing);
        void SetScalingFilter(ScalingFilter type);
        void SetScalingFilterLevel(float level);
        void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled);
    }
}
