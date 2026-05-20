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
inline void ApplyGsplatEffect(inout float3 center, inout float3 scales, inout float4 rgba,
                              int effectType, float t, float intensity, float3 windDir,
                              float waveAmplitude, float waveFrequency, float waveSpeed, float blendScale,
                              float lightWaveAmplitude, float lightWaveFrequency, float lightWaveSpeed,
                              float glitterDensity, float dissolveDriftSpeed, float burnDuration)
{
    if (effectType == 0)
        return;

    float3 localPos    = center;
    float3 splatScales = scales;
    float4 splatColor  = rgba;

    if (effectType == 1) {
        float4 e = fractal2_effect(localPos, splatScales, splatColor, t, intensity);
        rgba = lerp(splatColor, e, intensity);
        float b = sin(t * 1.5f);
        center.y += b * 0.02f * intensity;
    }
    else if (effectType == 2) {
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
        center = windMotion(localPos, t, intensity, windDir);
        rgba.rgb = lerp(splatColor.rgb, splatColor.rgb + float3(0.02f,0.05f,0.08f) * intensity, 0.3f);
    }
    else if (effectType == 6) {
        center = localPos + waveAmplitude * noise2_vec(localPos * waveFrequency + t * waveSpeed);
        float4 e = sin3D_light_effect(localPos, t, lightWaveAmplitude, lightWaveFrequency, lightWaveSpeed);
        rgba = lerp(splatColor, float4(splatColor.rgb * e.rgb, splatColor.a), intensity);
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
        float ang = (localPos.y * 50.0f - 20.0f) * exp(-t);
        localPos.xz = mul(localPos.xz, rot2(ang));
        center = localPos * (1.0f - exp(-t) * 2.0f);
        float ss = smoothstep(0.3f, 0.7f, t + localPos.y - 2.0f);
        scales = lerp(float3(0.002f,0.002f,0.002f), splatScales, ss);
        rgba = splatColor * ((t * 0.5f + localPos.y - 0.5f) >= 0.0f ? 1.0f : 0.0f);
    }
    else if (effectType == 10) {
        float4 e = sin3D_light_effect(localPos, t, lightWaveAmplitude, lightWaveFrequency, lightWaveSpeed);
        rgba = lerp(splatColor, float4(splatColor.rgb * e.rgb, splatColor.a), intensity);
        float4 tw = twister_effect(localPos, splatScales, t);
        center = tw.xyz;
        scales = lerp(float3(0.002f,0.002f,0.002f), splatScales, pow(tw.w, 12.0f));
        float spin  = -t * 0.3f * (1.0f - tw.w);
        float4 spinQ = float4(0.0f, sin(spin * 0.5f), 0.0f, cos(spin * 0.5f));
        // quaternion output unused in this pipeline; rotation baked into center offset
    }
    else if (effectType == 11) {
        float4 e = sin3D_light_effect(localPos, t, lightWaveAmplitude, lightWaveFrequency, lightWaveSpeed);
        rgba = lerp(splatColor, float4(splatColor.rgb * e.rgb, splatColor.a), intensity);
        float4 rn = rain_effect(localPos, splatScales, t);
        center = rn.xyz;
        scales = lerp(float3(0.005f,0.005f,0.005f), splatScales, pow(rn.w, 30.0f));
        rgba.rgb = lerp(rgba.rgb, rgba.rgb * 0.85f + float3(0.05f,0.07f,0.10f), 0.25f * rn.w);
        rgba.a  *= saturate(rn.w);
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
            float4 e = sin3D_light_effect(localPos, t, lightWaveAmplitude, lightWaveFrequency, lightWaveSpeed);
            rgba = lerp(splatColor, float4(splatColor.rgb * e.rgb, splatColor.a), intensity);
            rgba.rgb = lerp(rgba.rgb, starColor * (1.0f + isLarge * 2.0f), glow);
            rgba.a   = alpha;
        }
    }
    else if (effectType == 14) {
        float4 e = sin3D_light_effect(localPos, t, lightWaveAmplitude, lightWaveFrequency, lightWaveSpeed);
        rgba = lerp(splatColor, float4(splatColor.rgb * e.rgb, splatColor.a), intensity);
        float3 hv        = hash3(localPos);
        float  startTime = hv.x * 100.0f;
        float  shouldOsc = (t >= startTime) ? 1.0f : 0.0f;
        float3 moveDir   = normalize(float3((hv.x - 0.5f) * 0.6f, -1.0f, (hv.z - 0.5f) * 0.6f));
        float  randSpeed = frac(sin(dot(center, float3(12.0f,78.0f,45.0f))) * 43758.0f);
        float  moveAmt   = t * dissolveDriftSpeed * randSpeed * shouldOsc;
        center += moveDir * moveAmt;
        float shrink = smoothstep(0.0f, 1.0f, saturate(moveAmt * 0.8f));
        scales = lerp(splatScales, float3(0.003f,0.003f,0.003f), shrink);
        if (hv.z < glitterDensity) {
            float glow = 0.0f;
            glow += sin(t * (5.0f + hv.x * 10.0f) + hv.x * 6.28318f) * 0.5f + 0.5f;
            glow += sin(t * (3.0f + hv.y *  8.0f) + hv.y * 6.28318f) * 0.5f + 0.5f;
            glow += sin(t * (2.0f + hv.z *  6.0f) + hv.z * 6.28318f) * 0.5f + 0.5f;
            glow = pow(glow / 3.0f, 2.0f);
            rgba.rgb = lerp(rgba.rgb, float3(1.5f,1.8f,2.0f) * glow, shrink);
            rgba.a  *= lerp(1.0f, 1.0f - smoothstep(0.7f, 1.0f, shrink), shouldOsc);
        } else {
            rgba.a *= lerp(1.0f, 1.0f - saturate(moveAmt * 0.5f), shouldOsc);
        }
    }
    else if (effectType == 15) {
        float3 hv         = hash3(localPos);
        float  localT     = t - hv.y * 0.8f;
        float  shouldBurn = (localT >= 0.0f) ? 1.0f : 0.0f;
        float  burnProg   = saturate(localT / burnDuration) * shouldBurn;
        if (shouldBurn > 0.5f) {
            float  glowCurve = pow(sin(burnProg * 3.14159f), 4.0f);
            rgba.rgb += float3(2.0f,1.0f,0.2f) * intensity * 2.0f * glowCurve;
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

#endif // WL_GSPLAT_EFFECTS_INCLUDED
