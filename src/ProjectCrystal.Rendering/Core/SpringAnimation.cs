namespace ProjectCrystal.Rendering.Core;

public static class EasingCurves
{
    public static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    public static float EaseOutQuint(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv * inv * inv;
    }

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / Math.Max(0.0001f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

public struct SpringFloat
{
    public float Value;
    public float Velocity;
    public float Target;

    public SpringFloat(float initial)
    {
        Value = initial;
        Target = initial;
        Velocity = 0f;
    }

    public bool Step(float dtSeconds, float stiffness = 340f, float damping = 28f, bool instant = false)
    {
        if (instant || dtSeconds <= 0f)
        {
            bool changed = MathF.Abs(Value - Target) > 0.0005f;
            Value = Target;
            Velocity = 0f;
            return changed;
        }

        dtSeconds = Math.Min(dtSeconds, 0.033f);
        // Sub-stepped semi-implicit Euler stays strictly stable even at AnimationSpeed = 2x
        const int subSteps = 4;
        float subDt = dtSeconds * 0.25f;
        for (int i = 0; i < subSteps; i++)
        {
            float force = (Target - Value) * stiffness;
            float accel = force - Velocity * damping;
            Velocity += accel * subDt;
            Value += Velocity * subDt;
        }

        if (MathF.Abs(Target - Value) < 0.0015f && MathF.Abs(Velocity) < 0.015f)
        {
            Value = Target;
            Velocity = 0f;
            return false;
        }
        return true;
    }
}

public enum PanelMotionPreset
{
    Default,
    MacPopover,
    MacContextMenu,
    MacLaunchpad,
    MacSlideRight,
    MacMissionControl,
    MacWindowPop
}

public readonly record struct PanelMotionProfile(
    float MinScale,
    float AnchorX,
    float AnchorY,
    float OpenStiffness,
    float OpenDamping,
    float CloseStiffness,
    float CloseDamping,
    float DefaultSlideX,
    float DefaultSlideY);

public sealed class AnimatedPanelMotion
{
    private SpringFloat _progress = new(0f);
    private Action? _onClosed;
    private PanelMotionProfile _profile = GetProfile(PanelMotionPreset.Default);
    private float _activeSlideX;
    private float _activeSlideY = -12f;

    public float Opacity => Math.Clamp(_progress.Value, 0f, 1f);
    public float Scale { get; private set; } = 1.0f;
    public float OffsetX { get; private set; } = 0f;
    public float OffsetY { get; private set; } = 0f;
    public float AnchorX => _profile.AnchorX;
    public float AnchorY => _profile.AnchorY;
    public bool IsClosing { get; private set; }
    public bool IsOpenOrOpening => _progress.Target > 0.5f;

    public static PanelMotionProfile GetProfile(PanelMotionPreset preset) => preset switch
    {
        PanelMotionPreset.MacPopover => new(0.86f, 0.86f, 0.02f, 430f, 28.5f, 560f, 44f, 0f, -10f),
        PanelMotionPreset.MacContextMenu => new(0.84f, 0.06f, 0.02f, 480f, 31.0f, 580f, 46f, 0f, -6f),
        PanelMotionPreset.MacLaunchpad => new(0.87f, 0.50f, 0.50f, 410f, 28.0f, 520f, 42f, 0f, 20f),
        PanelMotionPreset.MacSlideRight => new(0.93f, 1.00f, 0.16f, 400f, 29.0f, 520f, 42f, 42f, 0f),
        PanelMotionPreset.MacMissionControl => new(1.07f, 0.50f, 0.50f, 390f, 30.0f, 480f, 40f, 0f, 0f),
        PanelMotionPreset.MacWindowPop => new(0.88f, 0.50f, 0.58f, 410f, 28.5f, 520f, 42f, 0f, 26f),
        _ => new(0.97f, 0.50f, 0.50f, 440f, 40.0f, 440f, 40.0f, 0f, -12f)
    };

    public void SetPreset(PanelMotionPreset preset)
    {
        _profile = GetProfile(preset);
        _activeSlideX = _profile.DefaultSlideX;
        _activeSlideY = _profile.DefaultSlideY;
    }

    public void SnapOpen()
    {
        IsClosing = false;
        _onClosed = null;
        _progress.Value = 1f;
        _progress.Target = 1f;
        _progress.Velocity = 0f;
        _activeSlideX = 0f;
        _activeSlideY = 0f;
        UpdateDerived();
    }

    public void BeginOpen(float? startOffsetY = null)
    {
        IsClosing = false;
        _onClosed = null;
        _activeSlideX = _profile.DefaultSlideX;
        _activeSlideY = startOffsetY ?? _profile.DefaultSlideY;
        if (_progress.Value < 0.14f) _progress.Value = 0.14f;
        _progress.Target = 1f;
        UpdateDerived();
    }

    public void BeginClose(Action onClosed, float? endOffsetY = null)
    {
        IsClosing = true;
        _onClosed = onClosed;
        _activeSlideX = _profile.DefaultSlideX;
        _activeSlideY = endOffsetY ?? _profile.DefaultSlideY;
        _progress.Target = 0f;
        UpdateDerived();
    }

    public bool Step(float dtSeconds, float speedScale, bool instant, float? slideDistance = null)
    {
        if (slideDistance.HasValue)
            _activeSlideY = slideDistance.Value;

        float clampedSpeed = Math.Clamp(speedScale, 0.4f, 1.8f);
        float stiffness = (IsClosing ? _profile.CloseStiffness : _profile.OpenStiffness) * clampedSpeed;
        float damping = (IsClosing ? _profile.CloseDamping : _profile.OpenDamping) * MathF.Sqrt(clampedSpeed);
        bool moving = _progress.Step(dtSeconds, stiffness, damping, instant);
        UpdateDerived();

        if (IsClosing && _progress.Value <= 0.02f && !moving)
        {
            IsClosing = false;
            var cb = _onClosed;
            _onClosed = null;
            cb?.Invoke();
        }
        return moving;
    }

    private void UpdateDerived()
    {
        float raw = Math.Clamp(_progress.Value, 0f, 1.08f);
        float t = Math.Clamp(raw, 0f, 1.0f);
        Scale = _profile.MinScale + (1.0f - _profile.MinScale) * raw;
        OffsetX = (1f - t) * _activeSlideX;
        OffsetY = (1f - t) * _activeSlideY;
    }
}
