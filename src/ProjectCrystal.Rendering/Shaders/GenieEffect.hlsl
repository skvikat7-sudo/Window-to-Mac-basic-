cbuffer GenieConstants : register(b0)
{
    float4 WinRect;       // (x, y, width, height) in overlay pixel space
    float4 IconRect;      // (x, y, width, height) of dock icon OR target rect in morph mode
    float4 OverlayParams; // (overlayW, overlayH, dockEdge [0=Bottom,1=Left,2=Right], progress [0..1])
    float4 EffectParams;  // (mode [0=Genie,1=Morph], opacity, sheenStrength, cornerRadius)
};

Texture2D WindowTex : register(t0);
SamplerState LinearClampSampler : register(s0);

struct VSOut
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float RoundedRectMask(float2 p, float2 size, float radius)
{
    float r = min(radius, min(size.x, size.y) * 0.5);
    float2 q = abs(p - size * 0.5) - (size * 0.5 - r);
    float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
    return 1.0 - smoothstep(-0.75, 0.75, d);
}

float4 PS(VSOut input) : SV_TARGET
{
    float overlayW = OverlayParams.x;
    float overlayH = OverlayParams.y;
    int dockEdge   = (int)(OverlayParams.z + 0.5);
    float progress = saturate(OverlayParams.w);
    int mode       = (int)(EffectParams.x + 0.5);
    float globalOp = EffectParams.y;
    float sheenAmt = EffectParams.z;
    float cornerR  = EffectParams.w;

    float2 pixelPos = input.uv * float2(overlayW, overlayH);

    // ---------------------------------------------------------
    // MODE 1: Smooth GPU Window Morph (Maximize / Restore / Snap)
    // ---------------------------------------------------------
    if (mode == 1)
    {
        float4 cur = lerp(WinRect, IconRect, progress);
        float2 localP = pixelPos - cur.xy;
        float2 uv = localP / max(float2(1.0, 1.0), cur.zw);

        // Soft drop shadow behind morphing window
        float shadowPad = 18.0;
        float2 shP = localP + float2(0.0, -4.0);
        float shMask = 0.0;
        if (shP.x > -shadowPad && shP.x < cur.z + shadowPad && shP.y > -shadowPad && shP.y < cur.w + shadowPad)
        {
            float2 q = abs(shP - cur.zw * 0.5) - (cur.zw * 0.5 - cornerR);
            float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - cornerR;
            shMask = exp(-max(d, 0.0) * 0.085) * 0.28 * globalOp;
        }

        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            return float4(0.0, 0.0, 0.0, shMask);

        float rMask = RoundedRectMask(localP, cur.zw, cornerR);
        float3 col = WindowTex.Sample(LinearClampSampler, uv).rgb;
        float midPulse = sin(progress * 3.14159265);
        col += sheenAmt * 0.06 * midPulse;

        float a = rMask * globalOp;
        float outA = max(a, shMask * (1.0 - a));
        float3 outRgb = col * a;
        return float4(outRgb, outA);
    }

    // ---------------------------------------------------------
    // MODE 0: Genuine macOS Genie Effect (Bottom / Left / Right)
    // ---------------------------------------------------------
    float px, py;
    float wx, wy, ww, wh;
    float ix, iy, iw, ih;

    if (dockEdge == 1) // Left dock: travel toward -X (mirror into +Y canonical)
    {
        px = pixelPos.y;
        py = overlayW - pixelPos.x;
        wx = WinRect.y;
        wy = overlayW - (WinRect.x + WinRect.z);
        ww = WinRect.w;
        wh = WinRect.z;
        ix = IconRect.y;
        iy = overlayW - (IconRect.x + IconRect.z);
        iw = IconRect.w;
        ih = IconRect.z;
    }
    else if (dockEdge == 2) // Right dock: travel toward +X
    {
        px = pixelPos.y;
        py = pixelPos.x;
        wx = WinRect.y;
        wy = WinRect.x;
        ww = WinRect.w;
        wh = WinRect.z;
        ix = IconRect.y;
        iy = IconRect.x;
        iw = IconRect.w;
        ih = IconRect.z;
    }
    else // Bottom dock: travel toward +Y
    {
        px = pixelPos.x;
        py = pixelPos.y;
        wx = WinRect.x;
        wy = WinRect.y;
        ww = WinRect.z;
        wh = WinRect.w;
        ix = IconRect.x;
        iy = IconRect.y;
        iw = IconRect.z;
        ih = IconRect.w;
    }

    // Two-phase macOS Genie timing:
    // Phase 1: Funnel pinches smoothly from window bottom toward exact dock icon
    // Phase 2: Window stretches and travels down the curved funnel into the icon
    float pinchT = smoothstep(0.0, 0.44, progress);
    float suckT  = smoothstep(0.18, 1.0, progress);

    float targetBotY = iy + ih * 0.88;
    float targetTopY = iy + ih * 0.14;

    float botEase = 1.0 - pow(1.0 - min(1.0, progress * 1.28), 2.15);
    float topEase = suckT * suckT * (1.28 - 0.28 * suckT);
    topEase = saturate(topEase);

    float botY = lerp(wy + wh, targetBotY, botEase);
    float topY = lerp(wy,      targetTopY, topEase);
    float spanY = max(2.0, botY - topY);

    // Funnel S-curve from window top to dock icon
    float funnelSpan = max(48.0, targetBotY - wy);
    float s = saturate((py - wy) / funnelSpan);
    float sCurve = s * s * (3.0 - 2.0 * s);
    float bend = lerp(sCurve, s * s * s, 0.22);

    float leftEdge  = lerp(wx,      ix,      bend * pinchT);
    float rightEdge = lerp(wx + ww, ix + iw, bend * pinchT);
    float spanX = max(2.0, rightEdge - leftEdge);

    float u = (px - leftEdge) / spanX;
    float v = (py - topY) / spanY;

    // Soft dynamic drop shadow following the curved silhouette
    float shMask = 0.0;
    if (u > -0.12 && u < 1.12 && v > -0.08 && v < 1.08)
    {
        float dx = max(0.0, max(-u, u - 1.0)) * spanX;
        float dy = max(0.0, max(-v, v - 1.0)) * spanY;
        float dist = sqrt(dx * dx + dy * dy);
        shMask = exp(-dist * 0.09) * 0.26 * (1.0 - progress * 0.75) * globalOp;
    }

    if (u < -0.02 || u > 1.02 || v < -0.02 || v > 1.02)
        return float4(0.0, 0.0, 0.0, shMask);

    float aaX = max(0.0015, 1.5 / spanX);
    float aaY = max(0.0015, 1.5 / spanY);
    float mask = smoothstep(0.0, aaX, u) * smoothstep(1.0, 1.0 - aaX, u)
               * smoothstep(0.0, aaY, v) * smoothstep(1.0, 1.0 - aaY, v);

    // Rounded window corners preserved while unfolding
    float2 approxLocal = float2(u * spanX, v * spanY);
    float curCorner = cornerR * (1.0 - 0.55 * progress);
    mask *= RoundedRectMask(approxLocal, float2(spanX, spanY), curCorner);

    // Map canonical (u, v) back to WindowTex UV
    float2 sampleUV;
    if (dockEdge == 1)
        sampleUV = float2(1.0 - v, u);
    else if (dockEdge == 2)
        sampleUV = float2(v, u);
    else
        sampleUV = float2(u, v);

    float3 col = WindowTex.Sample(LinearClampSampler, saturate(sampleUV)).rgb;

    // Subtle optical liquid sheen along the funnel bend
    float centerHighlight = pow(saturate(1.0 - abs(u - 0.5) * 2.0), 2.2);
    float funnelMotion = sin(progress * 3.14159265);
    col += float3(0.92, 0.96, 1.0) * (centerHighlight * funnelMotion * sheenAmt * 0.09);

    float endFade = 1.0 - smoothstep(0.84, 1.0, progress);
    float a = mask * endFade * globalOp;
    float outA = max(a, shMask * (1.0 - a));
    return float4(col * a, outA);
}
