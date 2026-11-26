// ============================================================================
// PROJECT CRYSTAL - Glossy Apple-Style Liquid Glass Optical Shader (SM 5.0)
// Ultra-clear crystal interior, razor-sharp curved meniscus refraction,
// controlled optical magnification, spectral prism edge dispersion,
// studio glossy specular lip highlights, internal caustic bounce,
// and clean premultiplied-alpha ambient shadows.
// ============================================================================

Texture2D DesktopTexture : register(t0);
Texture2D WallpaperFallback : register(t1);
SamplerState LinearClamp : register(s0);

struct GlassShapeGPU
{
    float4 Rect;        // x, y, width, height (in window-local pixels)
    float4 Params;      // x: cornerRadius, y: squirclePower, z: hoverGlow, w: bevelScale
    float4 Tint;        // rgb: custom tint override, w: shapeOpacity (0..1 animation alpha)
};

cbuffer GlassConstants : register(b0)
{
    float2 WindowSize;
    float2 WindowOrigin;
    float2 DesktopSize;
    float2 MousePos;

    float Transparency;
    float RefractionIntensity;
    float DistortionStrength;
    float Magnification;

    float BlurRadius;
    float ChromaticAberration;
    float TintOpacity;
    float ReflectionIntensity;

    float EdgeLighting;
    float Depth;
    float TimeSeconds;
    float IsDarkAppearance;

    float4 GlobalTintColor;
    int ShapeCount;
    float HighVisibilityEdges;
    float2 Padding;

    GlassShapeGPU Shapes[16];
};

struct VSOut
{
    float4 Pos : SV_POSITION;
    float2 UV  : TEXCOORD0;
};

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    o.UV = float2((id << 1) & 2, id & 2);
    o.Pos = float4(o.UV * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float smoothMax(float a, float b, float k)
{
    float h = clamp(0.5 + 0.5 * (a - b) / max(k, 0.001), 0.0, 1.0);
    return lerp(b, a, h) + k * h * (1.0 - h);
}

float sdSquircleBox(float2 p, float2 halfSize, float radius, float power)
{
    float r = min(radius, min(halfSize.x, halfSize.y));
    float2 inner = max(halfSize - r, float2(0.0, 0.0));
    float2 q = abs(p) - inner;
    float2 clampedQ = max(q, float2(0.0, 0.0));
    float pExp = clamp(power, 2.0, 4.0);
    float cornerDist = pow(pow(clampedQ.x, pExp) + pow(clampedQ.y, pExp), 1.0 / pExp);
    float interior = min(smoothMax(q.x, q.y, max(3.5, r * 0.65)), 0.0);
    return cornerDist + interior - r;
}

float evalShapeSdf(float2 px, GlassShapeGPU s)
{
    float2 center = s.Rect.xy + s.Rect.zw * 0.5;
    float2 halfSize = max(s.Rect.zw * 0.5, float2(1.0, 1.0));
    return sdSquircleBox(px - center, halfSize, s.Params.x, s.Params.y);
}

float sdSmoothBevelBox(float2 px, GlassShapeGPU s)
{
    float2 center = s.Rect.xy + s.Rect.zw * 0.5;
    float2 halfSize = max(s.Rect.zw * 0.5, float2(1.0, 1.0));
    float r = min(s.Params.x, min(halfSize.x, halfSize.y));
    float2 q = abs(px - center) - (halfSize - r);
    return length(max(q, float2(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - r;
}

float crystalHeight(float d, float bevelWidth)
{
    if (d >= 0.0) return 0.0;
    float t = saturate((-d) / max(bevelWidth, 1.0));
    return sin(t * 1.5707963);
}

float3 sampleDesktopFiltered(float2 uv, float blurPx, float2 texelSize)
{
    uv = clamp(uv, texelSize * 0.5, 1.0 - texelSize * 0.5);
    if (blurPx <= 0.65)
    {
        return DesktopTexture.SampleLevel(LinearClamp, uv, 0).rgb;
    }

    static const float2 offsets[4] = {
        float2(-0.7071, -0.7071),
        float2( 0.7071, -0.7071),
        float2(-0.7071,  0.7071),
        float2( 0.7071,  0.7071)
    };

    float3 sum = DesktopTexture.SampleLevel(LinearClamp, uv, 0).rgb * 1.4;
    float2 radiusUV = texelSize * blurPx;

    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float2 sampleUV = clamp(uv + offsets[i] * radiusUV, texelSize * 0.5, 1.0 - texelSize * 0.5);
        sum += DesktopTexture.SampleLevel(LinearClamp, sampleUV, 0).rgb * 0.65;
    }
    return sum * 0.25;
}

float4 PS(VSOut input) : SV_TARGET
{
    float2 px = input.UV * WindowSize;
    float2 texel = 1.0 / max(DesktopSize, float2(1.0, 1.0));

    if (ShapeCount <= 0)
        return float4(0, 0, 0, 0);

    int bestIdx = 0;
    float minD = 1e6;
    float maxCoverage = 0.0;
    bool foundInside = false;
    float shadowAccum = 0.0;

    [loop]
    for (int i = 0; i < ShapeCount; i++)
    {
        if (Shapes[i].Tint.w <= 0.005 || Shapes[i].Rect.z <= 1.0 || Shapes[i].Rect.w <= 1.0)
            continue;

        float d = evalShapeSdf(px, Shapes[i]);
        float sAlpha = saturate(Shapes[i].Tint.w);
        maxCoverage = max(maxCoverage, smoothstep(0.75, -0.75, d) * sAlpha);

        if (d <= 0.0)
        {
            bestIdx = i;
            minD = d;
            foundInside = true;
        }
        else if (!foundInside && d < minD)
        {
            minD = d;
            bestIdx = i;
        }

        if (d > -0.5)
        {
            float2 shadowPx = px - float2(0.0, 4.5 * Depth);
            float dShad = evalShapeSdf(shadowPx, Shapes[i]);
            float spread = max(6.0, 15.0 * Depth);
            float sMask = smoothstep(spread, -1.5, dShad);
            shadowAccum = max(shadowAccum, sMask * sAlpha);
        }
    }

    if (foundInside)
        shadowAccum = 0.0;

    GlassShapeGPU shape = Shapes[bestIdx];
    float shapeAlpha = saturate(shape.Tint.w);
    if (shapeAlpha <= 0.005 && maxCoverage <= 0.001)
        return float4(0, 0, 0, 0);

    float coverage = maxCoverage;

    // Outside crystal: clean, refined drop shadow
    if (coverage <= 0.001)
    {
        float shadowAlpha = shadowAccum * 0.22 * saturate(Depth);
        return float4(0.0, 0.0, 0.0, shadowAlpha);
    }

    // 1. Tight C1-smooth meniscus lip + gentle optical dome for crisp crystal lens behavior
    float minDim = min(shape.Rect.z, shape.Rect.w);
    float maxCornerBevel = max(5.0, min(shape.Params.x * 0.85, minDim * 0.28));
    float bevelWidth = clamp(minDim * 0.22 * max(0.35, Depth) * max(0.35, shape.Params.w), 5.0, min(18.0, maxCornerBevel));
    float thinBarScale = clamp(minDim / 52.0, 0.48, 1.0);

    float eps = 1.4;
    float hR = crystalHeight(sdSmoothBevelBox(px + float2(eps, 0.0), shape), bevelWidth);
    float hL = crystalHeight(sdSmoothBevelBox(px - float2(eps, 0.0), shape), bevelWidth);
    float hU = crystalHeight(sdSmoothBevelBox(px + float2(0.0, eps), shape), bevelWidth);
    float hD = crystalHeight(sdSmoothBevelBox(px - float2(0.0, eps), shape), bevelWidth);

    float2 bevelGrad = float2(hR - hL, hU - hD) / (2.0 * eps);
    float2 bevelSlope = bevelGrad * bevelWidth * 0.78;

    float2 shapeCenter = shape.Rect.xy + shape.Rect.zw * 0.5;
    float2 shapeHalf = max(shape.Rect.zw * 0.5, float2(1.0, 1.0));
    float2 rel = (px - shapeCenter) / shapeHalf;

    // Subtle interior convex crystal curvature + interactive Liquid-Motion droplet ripple near hovered cursor
    float domeFalloff = saturate(1.0 - 0.30 * dot(rel, rel));
    float2 domeSlope = -rel * 0.20 * domeFalloff;

    float edgeTaper = smoothstep(0.0, bevelWidth * 1.25, -minD);
    float2 cursorVec = px - MousePos;
    float cursorDist = length(cursorVec);
    float cursorMask = exp(-(cursorDist * cursorDist) / (58.0 * 58.0)) * saturate(shape.Params.z * 2.2) * edgeTaper;
    float2 cursorLensSlope = (cursorDist > 0.5) ? (-(cursorVec / cursorDist) * (cursorDist / 58.0) * cursorMask * 0.55) : float2(0.0, 0.0);

    float distStrength = DistortionStrength * (0.65 + 0.35 * Depth);
    float2 rawN = bevelSlope * (1.20 * distStrength) + (domeSlope + cursorLensSlope) * (0.42 * distStrength);
    float rawMag = length(rawN);
    float2 nXY = (rawMag > 1.85) ? (rawN * (1.85 / rawMag)) : rawN;
    float3 N = normalize(float3(-nXY.x, -nXY.y, 1.0));
    float3 V = float3(0.0, 0.0, 1.0);

    // 2. Genuine Screen-Space Refraction & Magnification
    float2 baseDesktopPx = WindowOrigin + px;
    float2 baseUV = baseDesktopPx / DesktopSize;
    float2 centerDesktopUV = (WindowOrigin + shapeCenter) / DesktopSize;

    float effectiveMag = lerp(1.0, Magnification, 0.35 + 0.65 * edgeTaper);
    float2 magnifiedUV = centerDesktopUV + (baseUV - centerDesktopUV) / max(0.60, effectiveMag);

    float refractPx = min(30.0, 19.0 * RefractionIntensity * (0.55 + 0.45 * Depth)) * thinBarScale;
    float2 refractedUV = magnifiedUV + (-nXY * refractPx) * texel;

    // 3. Spectral Dispersion strictly on curved meniscus gradient (keeps text crisp!)
    float curvatureMag = length(nXY);
    float dispersionPx = ChromaticAberration * (1.25 * curvatureMag + 0.05);
    float2 dispDir = (curvatureMag > 0.002) ? normalize(nXY) : float2(0.0, 0.0);
    float2 dispUV = dispDir * dispersionPx * texel;

    // Preserve optical sharpness near crystal edges; gentle micro-diffusion only if BlurRadius > 0.65
    float interiorBlur = BlurRadius * lerp(0.15, 1.0, edgeTaper);
    float3 refractedG = sampleDesktopFiltered(refractedUV, interiorBlur, texel);
    float3 refractedR = sampleDesktopFiltered(refractedUV - dispUV, interiorBlur > 1.8 ? interiorBlur : 0.0, texel);
    float3 refractedB = sampleDesktopFiltered(refractedUV + dispUV, interiorBlur > 1.8 ? interiorBlur : 0.0, texel);
    float3 refractedColor = float3(refractedR.r, refractedG.g, refractedB.b);

    // Protect against pure-black MPO / DRM / capture-excluded black patches by softly blending ambient wallpaper frost
    float rawPeak = max(refractedColor.r, max(refractedColor.g, refractedColor.b));
    if (rawPeak < 0.025)
    {
        float3 wpSample = WallpaperFallback.SampleLevel(LinearClamp, clamp(refractedUV, texel, 1.0 - texel), 0).rgb;
        float blackMask = smoothstep(0.025, 0.002, rawPeak);
        refractedColor = lerp(refractedColor, wpSample * 0.42 + float3(0.05, 0.07, 0.11), blackMask * 0.55);
    }

    // Subtle crystal vibrancy lift (optical glass boosts color clarity rather than washing to grey)
    float luma = dot(refractedColor, float3(0.299, 0.587, 0.114));
    refractedColor = lerp(float3(luma, luma, luma), refractedColor, 1.12);

    // 4. Crisp Internal Rim Caustic Bounce (tightened to outer rim so center stays crystal clear!)
    float rimMask = 1.0 - saturate(-minD / max(bevelWidth * 0.85, 1.0));
    float2 bounceUV = refractedUV + (bevelSlope * 11.0 * texel);
    float3 bounceSample = DesktopTexture.SampleLevel(LinearClamp, clamp(bounceUV, texel, 1.0 - texel), 0).rgb;
    refractedColor = lerp(refractedColor, bounceSample * 1.18 + 0.035, rimMask * rimMask * 0.28 * ReflectionIntensity);

    // 5. Ultra-Transparent Adaptive Crystal Body + Ambient Sky Reflection (prevents flat black slab over dark windows!)
    float bgLuma = dot(refractedColor, float3(0.299, 0.587, 0.114));
    float darkFactor = saturate(1.0 - bgLuma * 4.5);
    float3 activeTint = (length(shape.Tint.rgb) > 0.002) ? shape.Tint.rgb : GlobalTintColor.rgb;
    float3 adaptiveBase = lerp(float3(0.97, 0.99, 1.0), float3(0.24, 0.31, 0.42), IsDarkAppearance * 0.65);
    float3 glassTint = lerp(adaptiveBase, activeTint, 0.54);

    float darkLift = darkFactor * (0.065 + 0.055 * ReflectionIntensity);
    float minCrystalPresence = lerp(0.025, 0.20, darkFactor) * (1.0 - Transparency * 0.15);
    float bodyTintMix = clamp(max(TintOpacity + (1.0 - Transparency) * 0.45, minCrystalPresence), 0.0, 0.54);
    float3 crystalBody = lerp(refractedColor, glassTint, bodyTintMix);

    // Full-height studio reflection glaze (stronger near top-left Studio light, subtly luminous across bottom too)
    float skySlope = saturate(0.75 - rel.y * 0.42);
    float3 studioGlaze = lerp(float3(0.76, 0.87, 1.0), float3(0.96, 0.98, 1.0), 0.55) * (skySlope * skySlope * darkLift);
    crystalBody += studioGlaze;

    // 6. Glossy Liquid Specular Coat & Razor-Sharp Fresnel Rim (NO diffuse gray spotlight!)
    float NdotV = saturate(dot(N, V));
    float fresnel = pow(1.0 - NdotV, 3.2);

    // Studio key light from top-left + subtle diagonal glossy reflection sweep across curved meniscus
    float3 keyLight = normalize(float3(-0.48, -0.82, 0.48));
    float3 fillLight = normalize(float3(0.55, 0.78, 0.38));
    float keySpec = pow(saturate(dot(N, normalize(keyLight + V))), 60.0);
    float fillSpec = pow(saturate(dot(N, normalize(fillLight + V))), 32.0);

    // Subtle glossy surface sheen band along top curved bevel + dynamic subtle studio wave shimmer
    float glossSweep = smoothstep(0.65, -0.85, rel.y) * smoothstep(bevelWidth * 1.45, 0.0, -minD) * 0.28;
    float shimmerWave = 0.5 + 0.5 * sin(TimeSeconds * 0.72 + (px.x * 0.008) - (px.y * 0.012));
    float causticShimmer = pow(shimmerWave, 5.0) * smoothstep(bevelWidth * 1.1, 0.0, -minD) * 0.14;

    // Cursor-reactive tight rim glint (only affects curved bevel normals, zero milky middle blob!)
    float2 mouseDelta = px - MousePos;
    float mouseProximity = exp(-length(mouseDelta) * 0.016);
    float3 mouseDir = normalize(float3(-mouseDelta.x * 0.02, -mouseDelta.y * 0.02, 1.0));
    float mouseRimSpec = pow(saturate(dot(N, normalize(mouseDir + V))), 32.0) * mouseProximity * curvatureMag * (0.45 + 0.55 * shape.Params.z);

    // Dual razor-thin cut-crystal catchlights (outer edge + inner secondary prismatic bounce)
    float outerLip = smoothstep(1.5, 0.0, -minD) * (0.72 + 0.32 * HighVisibilityEdges);
    float innerRim = smoothstep(2.6, 4.2, -minD) * smoothstep(6.2, 4.2, -minD) * 0.22;
    float topLip = saturate(-N.y * 0.78 + 0.38) * outerLip;

    float3 specularHighlights =
        float3(1.0, 1.0, 1.0) * ((keySpec * 0.80 + fillSpec * 0.28 + mouseRimSpec * 0.68 + glossSweep + causticShimmer) * ReflectionIntensity) +
        float3(0.95, 0.98, 1.0) * ((fresnel * 0.50 * ReflectionIntensity) + (outerLip * 0.36 + topLip * 0.52 + innerRim) * EdgeLighting);

    float hoverLift = shape.Params.z * 0.055;
    float3 finalRgb = crystalBody + specularHighlights + float3(hoverLift, hoverLift * 1.04, hoverLift * 1.08);

    // Clean premultiplied alpha output with soft floating shadow depth
    float outAlpha = saturate(coverage + shadowAccum * 0.25 * saturate(Depth) * (1.0 - coverage));
    float3 premultRgb = finalRgb * coverage;

    return float4(saturate(premultRgb), outAlpha);
}
