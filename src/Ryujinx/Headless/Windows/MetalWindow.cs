using Ryujinx.Common.Configuration;
using Ryujinx.Input.HLE;
using Ryujinx.SDL3.Common;
using SharpMetal.QuartzCore;
using System.Runtime.Versioning;
using SDL;
using static SDL.SDL3;

namespace Ryujinx.Headless
{
    [SupportedOSPlatform("macos")]
    class MetalWindow : WindowBase
    {
        private CAMetalLayer _caMetalLayer;

        public CAMetalLayer GetLayer()
        {
            return _caMetalLayer;
        }

        public MetalWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode, 
            bool ignoreControllerApplet)
            : base(inputManager, glLogLevel, aspectRatio, enableMouse, hideCursorMode, ignoreControllerApplet) { }

        public override SDL_WindowFlags WindowFlags => SDL_WindowFlags.SDL_WINDOW_METAL;

        protected override void InitializeWindowRenderer()
        {
            void CreateLayer()
            {
                _caMetalLayer = new CAMetalLayer(SDL_Metal_GetLayer(SDL_Metal_CreateView(WindowHandle)));
            }

            if (SDL3Driver.MainThreadDispatcher != null)
            {
                SDL3Driver.MainThreadDispatcher(CreateLayer);
            }
            else
            {
                CreateLayer();
            }
        }

        protected override void InitializeRenderer() { }

        protected override void FinalizeWindowRenderer() { }

        protected override void SwapBuffers() { }
    }
}
