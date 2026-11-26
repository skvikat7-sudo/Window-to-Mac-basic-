using Vortice.Direct3D11;

namespace ProjectCrystal.Native.Capture;

public interface IDesktopBackdropSource : IDisposable
{
    bool IsLiveCaptureActive { get; }
    long FrameSequence { get; }
    int TextureWidth { get; }
    int TextureHeight { get; }
    string BackendStatusText { get; }

    /// <summary>
    /// Returns the latest ShaderResourceView for the desktop backdrop on the shared D3D11 device.
    /// Must only be accessed inside lock(SyncRoot) or immediately copied on the render thread.
    /// </summary>
    object SyncRoot { get; }
    ID3D11ShaderResourceView? BackdropSrv { get; }
    ID3D11Texture2D? BackdropTexture { get; }
}
