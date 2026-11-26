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

public sealed class AnimatedPanelMotion
{
    private SpringFloat _progress = new(0f);
    private Action? _onClosed;

    public float Opacity => Math.Clamp(_progress.Value, 0f, 1f);
    public float Scale { get; private set; } = 1.0f;
    public float OffsetY { get; private set; } = 0f;
    public bool IsClosing { get; private set; }
    public bool IsOpenOrOpening => _progress.Target > 0.5f;

    public void SnapOpen()
    {
        IsClosing = false;
        _onClosed = null;
        _progress.Value = 1f;
        _progress.Target = 1f;
        _progress.Velocity = 0f;
        UpdateDerived(0f);
    }

    public void BeginOpen(float startOffsetY = -12f)
    {
        IsClosing = false;
        _onClosed = null;
        if (_progress.Value < 0.14f) _progress.Value = 0.14f;
        _progress.Target = 1f;
        UpdateDerived(startOffsetY);
    }

    public void BeginClose(Action onClosed, float endOffsetY = -10f)
    {
        IsClosing = true;
        _onClosed = onClosed;
        _progress.Target = 0f;
        UpdateDerived(endOffsetY);
    }

    public bool Step(float dtSeconds, float speedScale, bool instant, float slideDistance = -12f)
    {
        float clampedSpeed = Math.Clamp(speedScale, 0.4f, 1.8f);
        // Nearly critically damped reveal (zero jarring bounce when popups open)
        bool moving = _progress.Step(dtSeconds, 440f * clampedSpeed, 40f * MathF.Sqrt(clampedSpeed), instant);
        UpdateDerived(slideDistance);

        if (IsClosing && _progress.Value <= 0.02f && !moving)
        {
            IsClosing = false;
            var cb = _onClosed;
            _onClosed = null;
            cb?.Invoke();
        }
        return moving;
    }

    private void UpdateDerived(float slideDistance)
    {
        float t = Math.Clamp(_progress.Value, 0f, 1.0f);
        Scale = 0.97f + 0.03f * t;
        OffsetY = (1f - t) * slideDistance;
    }
}
