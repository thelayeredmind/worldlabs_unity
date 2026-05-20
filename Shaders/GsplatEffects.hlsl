// Ported from Assets/gsplat-unity-vfx (CocoLinux0101, MIT).
// Effects originally inspired by sparkjsdev/spark (three.js).
// Integrated into worldlabs_gaussian as a vertex-stage compute hook.

#ifndef WL_GSPLAT_EFFECTS_INCLUDED
#define WL_GSPLAT_EFFECTS_INCLUDED

inline float2x2 rot2(float a) {
    float s = sin(a);
    float c = cos(a);
    return float2x2(c, -s, s, c);
}

inline float4 quatMul(float4 q1, float4 q2)
{
    return float4(
        q1.w * q2.x + q1.x * q2.w + q1.y * q2.z - q1.z * q2.y,
        q1.w * q2.y - q1.x * q2.z + q1.y * q2.w + q1.z * q2.x,
        q1.w * q2.z + q1.x * q2.y - q1.y * q2.x + q1.z * q2.w,
        q1.w * q2.w - q1.x * q2.x - q1.y * q2.y - q1.z * q2.z
    );
}

inline float3 hash3(float3 p) {
    return frac(sin(p * 123.456f) * 123.456f);
}

inline float3 hash2_3(float3 p) {
    p = frac(p * 0.3183099f + 0.1f);
    p *= 17.0f;
    return frac(float3(p.x * p.y * p.z, p.x + p.y * p.z, p.x * p.y + p.z));
}

inline float3 noise2_vec(float3 p) {
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0f - 2.0f * f);
    float3 n000 = hash2_3(i + float3(0,0,0));
    float3 n100 = hash2_3(i + float3(1,0,0));
    float3 n010 = hash2_3(i + float3(0,1,0));
    float3 n110 = hash2_3(i + float3(1,1,0));
    float3 n001 = hash2_3(i + float3(0,0,1));
    float3 n101 = hash2_3(i + float3(1,0,1));
    float3 n011 = hash2_3(i + float3(0,1,1));
    float3 n111 = hash2_3(i + float3(1,1,1));
    float3 x0 = lerp(n000, n100, f.x);
    float3 x1 = lerp(n010, n110, f.x);
    float3 x2 = lerp(n001, n101, f.x);
    float3 x3 = lerp(n011, n111, f.x);
    float3 y0 = lerp(x0, x1, f.y);
    float3 y1 = lerp(x2, x3, f.y);
    return lerp(y0, y1, f.z);
}

inline float noise_scalar(float3 p) {
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0f - 2.0f * f);
    float3 h000 = hash3(i + float3(0,0,0));
    float3 h100 = hash3(i + float3(1,0,0));
    float3 h010 = hash3(i + float3(0,1,0));
    float3 h110 = hash3(i + float3(1,1,0));
    float3 h001 = hash3(i + float3(0,0,1));
    float3 h101 = hash3(i + float3(1,0,1));
    float3 h011 = hash3(i + float3(0,1,1));
    float3 h111 = hash3(i + float3(1,1,1));
    float n000 = dot(h000, f - float3(0,0,0));
    float n100 = dot(h100, f - float3(1,0,0));
    float n010 = dot(h010, f - float3(0,1,0));
    float n110 = dot(h110, f - float3(1,1,0));
    float n001 = dot(h001, f - float3(0,0,1));
    float n101 = dot(h101, f - float3(1,0,1));
    float n011 = dot(h011, f - float3(0,1,1));
    float n111 = dot(h111, f - float3(1,1,1));
    float nx00 = lerp(n000, n100, u.x);
    float nx10 = lerp(n010, n110, u.x);
    float nx01 = lerp(n001, n101, u.x);
    float nx11 = lerp(n011, n111, u.x);
    float nxy0 = lerp(nx00, nx10, u.y);
    float nxy1 = lerp(nx01, nx11, u.y);
    return lerp(nxy0, nxy1, u.z);
}

inline float3 windMotion(float3 pos, float t, float intensity, float3 windDir) {
    float3 dir = normalize(windDir);
    float sway = sin(t + dot(pos, dir) * 0.5f) * 0.1f;
    pos += dir * intensity * 0.5f + dir * sway * intensity;
    return pos;
}

inline float4 twister_effect(float3 pos, float3 scale, float t) {
    float h = hash2_3(pos).x;
    float s = smoothstep(0.0f, 8.0f, t * t * 0.1f - length(pos.xz) * 2.0f + 2.0f);
    if (length(scale) < 0.05f) pos.y = lerp(-10.0f, pos.y, pow(s, 2.0f * h));
    pos.xz = lerp(pos.xz * 0.5f, pos.xz, pow(s, 2.0f * h));
    float rotationTime = t * (1.0f - s) * 0.2f;
    float ang = rotationTime + pos.y * 20.0f * (1.0f - s) * exp(-length(pos.xz));
    pos.xz = mul(pos.xz, rot2(ang));
    return float4(pos, s * s * s * s);
}

inline float4 rain_effect(float3 pos, float3 scale, float t) {
    float3 h = hash2_3(pos);
    float s = pow(smoothstep(0.0f, 5.0f, t * t * 0.1f - length(pos.xz) * 2.0f + 1.0f), 0.5f + h.x);
    float y = pos.y;
    pos.y = min(-10.0f + s * 15.0f, pos.y);
    pos.xz = lerp(pos.xz * 0.3f, pos.xz, s);
    return float4(pos, smoothstep(-10.0f, y, pos.y));
}

inline float4 fractal2_effect(float3 center, float3 scales, float4 rgba, float t, float intensity) {
    float3 pos = center;
    float splatSize = length(scales);
    float3 p = pos * 0.65f;
    pos.y += 2.0f;
    float c = 0.0f;
    float l2 = length(p);
    float m = 100.0f;
    for (int i = 0; i < 10; ++i) {
        p = abs(p) / dot(p, p) - 0.8f;
        float l = length(p);
        c += exp(-1.0f * abs(l - l2) * (1.0f + sin(t * 1.5f + pos.y)));
        l2 = length(p);
        m = min(m, length(p));
    }
    c = smoothstep(0.3f, 0.5f, m + sin(t * 1.5f + pos.y * 0.5f)) + c * 0.1f;
    float alpha = rgba.a * exp(-20.0f * splatSize) * m * intensity;
    float3 outc = float3(length(rgba.rgb), length(rgba.rgb), length(rgba.rgb)) * float3(c, c * c, c * c * c) * intensity;
    return float4(outc, alpha);
}

inline float4 sin3D_light_effect(float3 p, float t, float amplitude, float frequency, float speed) {
    float m = exp(amplitude * length(sin(p * frequency + t * speed))) * 5.0f;
    return float4(m, m, m, 0.3f);
}

inline float4 disintegrate_effect(float3 pos, float t, float intensity) {
    float3 p = pos + (hash3(pos) * 2.0f - 1.0f) * intensity;
    float tt = smoothstep(-1.0f, 0.5f, -sin(t + -pos.y * 0.5f));
    p.xz = mul(p.xz, rot2(tt * 2.0f + p.y * 2.0f * tt));
    return float4(lerp(p, pos, tt), tt);
}

inline float4 flare_effect(float3 pos, float t) {
    float3 p = float3(0.0f, -1.5f, 0.0f);
    float tt = smoothstep(-1.0f, 0.5f, sin(t + hash3(pos).x));
    tt *= tt;
    p.x += sin(t * 2.0f) * tt; p.z += sin(t * 2.0f) * tt; p.y += sin(t) * tt;
    return float4(lerp(pos, p, tt), tt);
}

// Applies one of 16 vertex-stage effects in object/local space.
// center and scales are in object space. rgba is linear HDR color + opacity.
// effectType 0 = no-op (zero-cost branch).
// t is pre-scaled by the C# layer so duration directly controls the playback rate.
// lightWaveIntensity: blend weight of the light wave accent layer (0 = off)
// glowColor        : emissive colour for GlowDissolve burn
inline void ApplyGsplatEffect(inout float3 center, inout float3 scales, inout float4 rgba,
                              int effectType, float t, float intensity, float3 windDir,
                              float waveAmplitude, float waveFrequency, float blendScale,
                              float lightWaveAmplitude, float lightWaveFrequency, float lightWaveSpeed,
                              float glitterDensity, float burnDuration,
                              float3 glowColor)
{
    if (effectType == 0)
        return;

    float3 localPos    = center;
    float3 splatScales = scales;
    float4 splatColor  = rgba;

    if (effectType == 1) {
        // t drives the fractal animation period — duration controls speed
        float4 e = fractal2_effect(localPos, splatScales, splatColor, t, intensity);
        rgba = lerp(splatColor, e, intensity);
        center.y += sin(t * 1.5f) * 0.02f * intensity;
    }
    else if (effectType == 2) {
        // LightWave3D: intensity is the primary blend weight
        float4 e = sin3D_light_effect(localPos, t, lightWaveAmplitude, lightWaveFrequency, lightWaveSpeed);
        rgba = lerp(splatColor, float4(splatColor.rgb * e.rgb, splatColor.a), intensity);
    }
    else if (effectType == 3) {
        float4 e = flare_effect(localPos, t);
        center = e.xyz;
        rgba.rgb = lerp(splatColor.rgb, float3(1.0f,1.0f,1.0f), abs(e.w));
        rgba.a   = lerp(splatColor.a, 0.3f, abs(e.w));
    }
    else if (effectType == 4) {
        float4 e = disintegrate_effect(localPos, t, intensity);
        center = e.xyz;
        scales = lerp(float3(0.01f,0.01f,0.01f), scales, e.w);
    }
    else if (effectType == 5) {
        // t drives the sway oscillation — duration controls the sway period
        float3 dir = normalize(windDir);
        float sway = sin(t + dot(localPos, dir) * 0.5f) * 0.1f;
        center = localPos + dir * intensity * 0.5f + dir * sway * intensity;
        rgba.rgb = lerp(splatColor.rgb, splatColor.rgb + float3(0.02f,0.05f,0.08f) * intensity, 0.3f);
    }
    else if (effectType == 6) {
        center = localPos + waveAmplitude * noise2_vec(localPos * waveFrequency + t);
        scales = (blendScale < 0.1f) ? float3(0.001f, 0.001f, 0.001f) : splatScales * blendScale;
    }
    else if (effectType == 7) {
        float l = length(localPos.xz);
        float s = smoothstep(0.0f, 10.0f, t - 4.5f) * 10.0f;
        float border = abs(s - l - 0.5f);
        localPos *= 1.0f - 0.2f * exp(-20.0f * border);
        scales = lerp(splatScales, float3(0.001f,0.001f,0.001f), smoothstep(s - 0.5f, s, l + 0.5f));
        center = localPos + 0.1f * noise2_vec(localPos * 2.0f + t * 0.5f) * smoothstep(s - 0.5f, s, l + 0.5f);
        float at = atan2(localPos.x, localPos.z) / 3.14159265f;
        rgba *= step(at, t - 3.14159265f);
        rgba += float4(exp(-20.0f * border).xxx, 0.0f)
              + float4((exp(-50.0f * abs(t - at - 3.14159265f)) * 0.5f).xxx, 0.0f);
    }
    else if (effectType == 8) {
        float tt = t * t * 0.4f + 0.5f;
        float mulFactor = min(1.0f, 0.3f + max(0.0f, tt * 0.05f));
        localPos.xz *= mulFactor;
        float lxz = length(localPos.xz);
        float3 sA = lerp(float3(0,0,0), splatScales, saturate(min(tt - 7.0f - lxz * 2.5f, 1.0f)));
        float3 sB = lerp(float3(0,0,0), splatScales * 0.2f, saturate(min(tt - 1.0f - lxz * 2.0f, 1.0f)));
        scales = max(sA, sB);
        rgba = lerp(float4(0.3f,0.3f,0.3f,0.3f), splatColor, saturate(tt - lxz * 2.5f - 3.0f));
        center = localPos;
    }
    else if (effectType == 9) {
        // Unroll: starts wound up, unrolls over duration. t=0 → fully wound, t=6 → fully open.
        // Offset center into visible range from t=0 by biasing the exp decay.
        float ang = (localPos.y * 50.0f - 20.0f) * exp(-t);
        localPos.xz = mul(localPos.xz, rot2(ang));
        // Use (1 - exp(-t)) so at t=0 center=0 (splats at origin, visible) and expands outward
        center = localPos * (1.0f - exp(-t));
        float ss = smoothstep(0.0f, 1.0f, t * 0.3f + localPos.y * 0.5f);
        scales = lerp(float3(0.002f,0.002f,0.002f), splatScales, ss);
        rgba = splatColor;
    }
    else if (effectType == 10) {
        // Twister: t drives the time arc, intensity scales the spatial displacement
        float4 tw = twister_effect(localPos, splatScales, t);
        // lerp between original pos and twisted pos by intensity
        center = lerp(localPos, tw.xyz, intensity);
        scales = lerp(float3(0.002f,0.002f,0.002f), splatScales, pow(tw.w, 12.0f));
    }
    else if (effectType == 11) {
        // Rain: offset t so splats start falling from t=0 rather than needing a warmup.
        // Add a bias to s so most splats are already triggering at t=0.
        float3 hv = hash2_3(localPos);
        float tBiased = t + 4.0f; // shift into the active range of smoothstep
        float s = pow(smoothstep(0.0f, 5.0f, tBiased * tBiased * 0.1f - length(localPos.xz) * 2.0f + 1.0f), 0.5f + hv.x);
        float y = localPos.y;
        localPos.y = min(-10.0f + s * 15.0f, localPos.y);
        localPos.xz = lerp(localPos.xz * 0.3f, localPos.xz, s);
        float vis = smoothstep(-10.0f, y, localPos.y);
        center = localPos;
        scales = lerp(float3(0.005f,0.005f,0.005f), splatScales, pow(vis, 30.0f));
        rgba.rgb = lerp(splatColor.rgb, splatColor.rgb * 0.85f + float3(0.05f,0.07f,0.10f), 0.25f * vis);
        rgba.a  *= saturate(vis);
    }
    else if (effectType == 12) {
        float3 hv = hash3(localPos);
        if (hv.z < glitterDensity) {
            float glow = 0.0f;
            glow += sin(t * (5.0f + hv.x * 10.0f) + hv.x * 6.28318f) * 0.5f + 0.5f;
            glow += sin(t * (3.0f + hv.y *  8.0f) + hv.y * 6.28318f) * 0.5f + 0.5f;
            glow += sin(t * (2.0f + hv.z *  6.0f) + hv.z * 6.28318f) * 0.5f + 0.5f;
            glow = pow(glow / 3.0f, 2.0f);
            scales = float3(0.002f,0.002f,0.002f);
            rgba.rgb = lerp(rgba.rgb, float3(1.5f,1.8f,2.0f) * glow, glow);
        }
    }
    else if (effectType == 13) {
        float3 hv = hash3(localPos);
        if (hv.z < glitterDensity) {
            float isLarge  = step(0.9f, hv.y);
            float starSize = lerp(0.0008f, 0.002f, isLarge);
            scales = float3(starSize, starSize, starSize);
            float h = hv.x;
            float3 starColor;
            if      (h < 0.33f) starColor = lerp(float3(0.15f,0.05f,0.35f), float3(0.0f,0.7f,1.0f),   h * 3.0f);
            else if (h < 0.66f) starColor = lerp(float3(0.0f,0.7f,1.0f),   float3(1.0f,0.2f,0.6f),   (h - 0.33f) * 3.0f);
            else                starColor = lerp(float3(1.0f,0.2f,0.6f),   float3(1.0f,0.95f,0.8f),  (h - 0.66f) * 3.0f);
            float speed = 2.0f + hv.z * 4.0f;
            float glow  = (sin(t * speed + hv.x * 6.28318f) * 0.5f + 0.5f
                        +  sin(t * speed * 0.5f + hv.y * 6.28318f) * 0.5f + 0.5f) * 0.5f;
            glow = pow(glow, lerp(4.0f, 2.0f, isLarge));
            float lifetime   = 2.0f + hv.z * 3.0f;
            float age        = fmod(t + hv.y * lifetime, lifetime);
            float alpha      = (1.0f - age / lifetime) * lerp(1.0f, 0.6f, isLarge);
            // No light wave on GlitterGalaxy — colour is star-driven
            rgba.rgb = lerp(splatColor.rgb, starColor * (1.0f + isLarge * 2.0f), glow);
            rgba.a   = alpha;
        }
    }
    else if (effectType == 14) {
        // FlyingDissolve: stagger departure across first 60% of cycle (original used 0–100 range).
        // FlyingDissolve: t drives all timing. dissolveDriftSpeed removed — duration is the sole control.
        // Particles stagger departure in first 60% of t, drift ~1 unit over full duration.
        float3 hv14      = hash3(localPos);
        float  startT    = hv14.x * t * 0.6f;
        float  active    = (t >= startT) ? 1.0f : 0.0f;
        float3 moveDir   = normalize(float3((hv14.x - 0.5f) * 0.6f, -1.0f, (hv14.z - 0.5f) * 0.6f));
        float  randVar   = frac(sin(dot(center, float3(12.0f,78.0f,45.0f))) * 43758.0f);
        float  localT14  = max(0.0f, t - startT);
        // Scale move so a particle drifts ~1 unit over the full t range regardless of duration
        float  tMax      = 8.0f; // matches k_EffectTimeScale for FlyingDissolve
        float  moveAmt   = (localT14 / tMax) * (0.5f + randVar * 0.5f);
        center += moveDir * moveAmt * active;
        float shrink = smoothstep(0.0f, 1.0f, moveAmt);
        scales = lerp(splatScales, float3(0.003f,0.003f,0.003f), shrink * active);
        if (hv14.z < glitterDensity) {
            float glow = 0.0f;
            glow += sin(t * (5.0f + hv14.x * 10.0f) + hv14.x * 6.28318f) * 0.5f + 0.5f;
            glow += sin(t * (3.0f + hv14.y *  8.0f) + hv14.y * 6.28318f) * 0.5f + 0.5f;
            glow += sin(t * (2.0f + hv14.z *  6.0f) + hv14.z * 6.28318f) * 0.5f + 0.5f;
            glow = pow(glow / 3.0f, 2.0f);
            rgba.rgb = lerp(rgba.rgb, float3(1.5f,1.8f,2.0f) * glow, shrink * active);
            rgba.a  *= lerp(1.0f, 1.0f - smoothstep(0.7f, 1.0f, shrink), active);
        } else {
            rgba.a *= lerp(1.0f, 1.0f - moveAmt, active);
        }
    }
    else if (effectType == 15) {
        // GlowDissolve: burnDuration is a fraction [0,1] of the total cycle.
        // Splat stagger offset also bounded to burnDuration fraction so no idle gap.
        float3 hv15         = hash3(localPos);
        float  burnFrac     = saturate(burnDuration); // [0,1] fraction of full t range
        float  tMax15       = 8.0f;                   // k_EffectTimeScale for GlowDissolve
        float  burnT        = burnFrac * tMax15;       // absolute shader time for one burn
        float  startOffset  = hv15.y * burnT;
        float  localT15     = t - startOffset;
        float  shouldBurn   = (localT15 >= 0.0f) ? 1.0f : 0.0f;
        float  burnProg     = saturate(localT15 / max(burnT, 0.001f)) * shouldBurn;
        if (shouldBurn > 0.5f) {
            float glowCurve = pow(sin(burnProg * 3.14159f), 4.0f);
            rgba.rgb += glowColor * intensity * 2.0f * glowCurve;
            float shrink = smoothstep(0.5f, 1.0f, burnProg);
            scales = lerp(splatScales, float3(0.005f,0.005f,0.005f), shrink);
            rgba.a *= lerp(1.0f, 0.0f, smoothstep(0.6f, 1.0f, burnProg));
            center += normalize(hash3(center) - 0.5f) * burnProg * 0.05f;
        }
    }
    else if (effectType == 16) {
        float  dist    = length(localPos);
        float3 dir     = (dist > 0.0001f) ? normalize(localPos) : float3(0,1,0);
        float3 hv      = hash3(localPos * 10.0f);
        float  expAmt  = intensity * 2.0f;
        center += (dir + (hv * 2.0f - 1.0f) * 0.2f) * expAmt;
        if (expAmt > 1.2f) {
            float3 swirl    = float3(-dir.z, dir.y, dir.x);
            float3 drift    = swirl * sin(t + dot(localPos, float3(12.34f,56.78f,90.12f))) * 0.05f;
            float3 brownian = (hv * 2.0f - 1.0f) * 0.025f * sin(t * 3.0f + hv.x * 6.28f);
            center += drift + brownian;
            scales *= 0.85f + 0.15f * hv.y;
        }
        float shrinkT = smoothstep(0.0f, 0.8f, intensity);
        scales = lerp(scales, float3(0.001f,0.001f,0.001f), shrinkT);
        float density = (expAmt > 1.2f) ? 0.3f : 0.0f;
        if (hv.z < density) {
            float sparklePhase = dot(localPos, float3(12.9898f,78.233f,45.164f));
            float blink = smoothstep(0.5f, 1.0f, sin(intensity * 20.0f + sparklePhase));
            rgba.rgb = lerp(rgba.rgb, rgba.rgb * intensity * 2.0f, blink * shrinkT);
            rgba.a   = max(rgba.a, blink);
        } else {
            rgba.a *= 1.0f - intensity * 0.5f;
        }
    }
}

// Per-layer effect parameter struct. Must match GaussianSplatEffectLayer.ShaderParams (C#).
// Laid out as 5x float4 = 80 bytes, 16-byte aligned.
struct SplatEffectParams
{
    // float4 #0
    int   effectType;
    float effectTime;
    float intensity;
    float waveAmplitude;
    // float4 #1
    float waveFrequency;
    float blendScale;
    float lightWaveAmplitude;
    float lightWaveFrequency;
    // float4 #2
    float lightWaveSpeed;
    float glitterDensity;
    float burnDuration;
    float _pad0;
    // float4 #3
    float3 windDir;
    float  _pad1;
    // float4 #4
    float3 glowColor;
    float  _pad2;
};

// Each compute shader declares alongside its other per-kernel uniforms:
//   StructuredBuffer<SplatEffectParams> _EffectLayers;
//   uint _EffectLayerCount;

// Apply all active effect layers to a splat in sequence (Option A stacking).
// colorOverride.a >= 0 after the call means at least one layer replaced the DC colour.
#define GSPLAT_APPLY_EFFECTS(splat, colorOverride)                                         \
{                                                                                           \
    colorOverride = float4(0, 0, 0, -1);                                                   \
    for (uint _li = 0; _li < _EffectLayerCount; ++_li)                                    \
    {                                                                                       \
        SplatEffectParams _p = _EffectLayers[_li];                                         \
        if (_p.effectType == 0) continue;                                                  \
        float4 _rgba = (colorOverride.a >= 0)                                              \
            ? float4(colorOverride.rgb, splat.opacity)                                     \
            : float4(splat.sh.col.rgb, splat.opacity);                                     \
        ApplyGsplatEffect(splat.pos, splat.scale, _rgba,                                   \
            _p.effectType, _p.effectTime, _p.intensity, _p.windDir,                        \
            _p.waveAmplitude, _p.waveFrequency, _p.blendScale,                             \
            _p.lightWaveAmplitude, _p.lightWaveFrequency, _p.lightWaveSpeed,               \
            _p.glitterDensity, _p.burnDuration, _p.glowColor);                             \
        splat.opacity = _rgba.a;                                                            \
        colorOverride = float4(_rgba.rgb, 1.0f);                                           \
    }                                                                                       \
}

#endif // WL_GSPLAT_EFFECTS_INCLUDED
