using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;

namespace ProjectCrystal.Rendering.Core;

public sealed class GpuDeviceContext : IDisposable
{
    public object DeviceLock { get; } = new();

    public ID3D11Device D3DDevice { get; }
    public ID3D11DeviceContext D3DContext { get; }
    public IDXGIFactory2 DxgiFactory { get; }
    public IDCompositionDevice DCompDevice { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public IDWriteFactory DWriteFactory { get; }

    public ID3D11VertexShader GlassVertexShader { get; }
    public ID3D11PixelShader GlassPixelShader { get; }
    public ID3D11VertexShader GenieVertexShader { get; }
    public ID3D11PixelShader GeniePixelShader { get; }
    public ID3D11SamplerState LinearClampSampler { get; }
    public ID3D11Buffer GlassConstantBuffer { get; }
    public ID3D11Buffer GenieConstantBuffer { get; }
    public ID3D11BlendState PremultipliedBlendState { get; }

    public string AdapterName { get; }
    public double AdapterMemoryMb { get; }

    private ID3D11Texture2D? _fallbackTex;
    private ID3D11ShaderResourceView? _fallbackSrv;
    public int FallbackWidth { get; private set; } = 1920;
    public int FallbackHeight { get; private set; } = 1080;

    public GpuDeviceContext()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0 },
            out ID3D11Device d3dDevice,
            out ID3D11DeviceContext d3dContext);

        D3DDevice = d3dDevice;
        D3DContext = d3dContext;

        using var multithread = D3DDevice.QueryInterfaceOrNull<ID3D11Multithread>();
        multithread?.SetMultithreadProtected(true);

        using var dxgiDevice = D3DDevice.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var desc = adapter.Description;
        AdapterName = desc.Description.Trim('\0', ' ');
        AdapterMemoryMb = Math.Round((double)desc.DedicatedVideoMemory / (1024 * 1024) + (double)desc.SharedSystemMemory / (1024 * 1024) * 0.25, 0);

        DxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        DCompDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        D2DDevice = D2DFactory.CreateDevice(dxgiDevice);
        DWriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();

        string shaderPath = LocateShaderPath("LiquidGlass.hlsl");
        string hlslSource = File.ReadAllText(shaderPath);
        var vsBlob = Compiler.Compile(hlslSource, "VS", shaderPath, "vs_5_0");
        var psBlob = Compiler.Compile(hlslSource, "PS", shaderPath, "ps_5_0");
        GlassVertexShader = D3DDevice.CreateVertexShader(vsBlob.Span);
        GlassPixelShader = D3DDevice.CreatePixelShader(psBlob.Span);

        string geniePath = LocateShaderPath("GenieEffect.hlsl");
        string genieSource = File.ReadAllText(geniePath);
        var gvsBlob = Compiler.Compile(genieSource, "VS", geniePath, "vs_5_0");
        var gpsBlob = Compiler.Compile(genieSource, "PS", geniePath, "ps_5_0");
        GenieVertexShader = D3DDevice.CreateVertexShader(gvsBlob.Span);
        GeniePixelShader = D3DDevice.CreatePixelShader(gpsBlob.Span);

        LinearClampSampler = D3DDevice.CreateSamplerState(new SamplerDescription
        {
            Filter = Vortice.Direct3D11.Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        int cbSize = (Marshal.SizeOf<GlassConstantsGpu>() + 15) & ~15;
        GlassConstantBuffer = D3DDevice.CreateBuffer(new BufferDescription
        {
            ByteWidth = cbSize,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        GenieConstantBuffer = D3DDevice.CreateBuffer(new BufferDescription
        {
            ByteWidth = 64, // 4 x float4
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        var blendDesc = new Vortice.Direct3D11.BlendDescription();
        blendDesc.RenderTarget[0] = new Vortice.Direct3D11.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Vortice.Direct3D11.Blend.One,
            DestinationBlend = Vortice.Direct3D11.Blend.InverseSourceAlpha,
            BlendOperation = Vortice.Direct3D11.BlendOperation.Add,
            SourceBlendAlpha = Vortice.Direct3D11.Blend.One,
            DestinationBlendAlpha = Vortice.Direct3D11.Blend.InverseSourceAlpha,
            BlendOperationAlpha = Vortice.Direct3D11.BlendOperation.Add,
            RenderTargetWriteMask = Vortice.Direct3D11.ColorWriteEnable.All
        };
        PremultipliedBlendState = D3DDevice.CreateBlendState(blendDesc);

        CreateInitialFallbackBackdrop();
        CrystalLogger.Info("GPU", $"Initialized Direct3D11 + DirectComposition + Direct2D1 on '{AdapterName}'.");
    }

    public void UpdateFallbackBackdrop(byte[] bgraPixels, int width, int height)
    {
        lock (DeviceLock)
        {
            _fallbackSrv?.Dispose();
            _fallbackTex?.Dispose();
            FallbackWidth = width;
            FallbackHeight = height;

            var texDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource
            };
            unsafe
            {
                fixed (byte* p = bgraPixels)
                {
                    var sub = new SubresourceData((nint)p, width * 4);
                    _fallbackTex = D3DDevice.CreateTexture2D(texDesc, sub);
                }
            }
            _fallbackSrv = D3DDevice.CreateShaderResourceView(_fallbackTex);
        }
    }

    public ID3D11ShaderResourceView GetFallbackSrv() => _fallbackSrv!;

    private void CreateInitialFallbackBackdrop()
    {
        const int w = 512, h = 512;
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float ty = y / (float)h;
            for (int x = 0; x < w; x++)
            {
                float tx = x / (float)w;
                byte b = (byte)Math.Clamp((int)(110 + 100 * (1 - ty) + 25 * MathF.Sin(tx * 4f)), 0, 255);
                byte g = (byte)Math.Clamp((int)(55 + 85 * tx * (1 - ty) + 30 * ty), 0, 255);
                byte r = (byte)Math.Clamp((int)(30 + 90 * tx + 40 * ty), 0, 255);
                int idx = (y * w + x) * 4;
                px[idx] = b; px[idx + 1] = g; px[idx + 2] = r; px[idx + 3] = 255;
            }
        }
        UpdateFallbackBackdrop(px, w, h);
    }

    private static string LocateShaderPath(string fileName)
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Shaders", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Shaders", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "ProjectCrystal.Rendering", "Shaders", fileName)
        };
        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        throw new FileNotFoundException($"Could not locate {fileName} shader file.");
    }

    public static Vector4 ParseHexColor(string hex, float alpha = 1f)
    {
        try
        {
            var c = ColorTranslator.FromHtml(hex);
            return new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }
        catch
        {
            return new Vector4(0.88f, 0.94f, 1.0f, alpha);
        }
    }

    public void Dispose()
    {
        lock (DeviceLock)
        {
            _fallbackSrv?.Dispose();
            _fallbackTex?.Dispose();
            PremultipliedBlendState.Dispose();
            GenieConstantBuffer.Dispose();
            GeniePixelShader.Dispose();
            GenieVertexShader.Dispose();
            GlassConstantBuffer.Dispose();
            LinearClampSampler.Dispose();
            GlassPixelShader.Dispose();
            GlassVertexShader.Dispose();
            DWriteFactory.Dispose();
            D2DDevice.Dispose();
            D2DFactory.Dispose();
            DCompDevice.Dispose();
            DxgiFactory.Dispose();
            D3DContext.Dispose();
            D3DDevice.Dispose();
        }
    }
}
