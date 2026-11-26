using System.Numerics;
using System.Runtime.InteropServices;

namespace ProjectCrystal.Rendering.Core;

public sealed class GlassShapeItem
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float CornerRadius { get; set; } = 18f;
    public float SquirclePower { get; set; } = 3.2f;
    public float HoverGlow { get; set; } = 0f;
    public float BevelScale { get; set; } = 1.0f;
    public Vector4 TintColorAndOpacity { get; set; } = new(0f, 0f, 0f, 1.0f);
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GlassShapeGpu
{
    public Vector4 Rect;
    public Vector4 Params;
    public Vector4 Tint;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public unsafe struct GlassConstantsGpu
{
    public Vector2 WindowSize;
    public Vector2 WindowOrigin;

    public Vector2 DesktopSize;
    public Vector2 MousePos;

    public float Transparency;
    public float RefractionIntensity;
    public float DistortionStrength;
    public float Magnification;

    public float BlurRadius;
    public float ChromaticAberration;
    public float TintOpacity;
    public float ReflectionIntensity;

    public float EdgeLighting;
    public float Depth;
    public float TimeSeconds;
    public float IsDarkAppearance;

    public Vector4 GlobalTintColor;

    public int ShapeCount;
    public float HighVisibilityEdges;
    public Vector2 Padding;

    public fixed float ShapesRaw[16 * 12]; // 16 * 3 float4 = 192 floats
}
