// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        // Pass 0 — GSP-CULL-02: alpha-blend with per-pixel stencil overdraw cap
        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off

            // Stencil buffer acts as a per-pixel draw counter.
            // Splats render back-to-front; the farthest N layers pass, nearer ones are discarded.
            // Ref [_StencilOverdrawCap]: compare against the cap value set at runtime.
            // Comp Greater: pass fragment when stencil < Ref (i.e. count not yet reached).
            // Pass IncrSat: increment stencil on write, saturate at 255.
            // Fail Keep: keep stencil unchanged when cap is exceeded (fragment discarded).
            Stencil
            {
                Ref [_StencilOverdrawCap]
                Comp Greater
                Pass IncrSat
                Fail Keep
            }

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "Packages/com.worldlabs.gaussian-splatting/Shaders/GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
uint _OptimizeForQuest;
half _AlphaDiscardThreshold;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	v2f o = (v2f)0;
    instID = _OrderBuffer[instID];

	SplatViewData view = _SplatViewData[instID];

	float4 centerClipPos = view.pos;

	// Respect compute-side contribution cull or behind-camera before any recalc
	if (centerClipPos.w <= 0)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
		return o;
	}

	// Need to recalculate here for Quest (Why tho?)
	if (_OptimizeForQuest) {
		SplatData splat = LoadSplatData(instID);
		float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
	    centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
	}

	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

		// is this splat selected?
		if (_SplatBitsValid)
		{
			uint wordIdx = instID / 32;
			uint bitIdx = instID & 31;
			uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
			if (selVal & (1 << bitIdx))
			{
				o.col.a = -1;
			}
		}
	}
    return o;
}

half4 frag (v2f i) : SV_Target
{
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}

    if (alpha < _AlphaDiscardThreshold)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
    return res;
}

ENDCG
        }

        // Pass 1 — GSP-CULL-01: opaque front-to-back experiment
        // ZWrite On + Blend Off: depth buffer rejects occluded fragments for free.
        // Output looks wrong (no transparency accumulation) but measures overdraw lower bound.
        //
        // Fixes vs original:
        //   - ZTest [_ZTest]: platform-aware (LEqual on DX/PCVR, GEqual on Vulkan/Quest reversed-Z)
        //   - Vertex-side opacity cull: primitives with peak alpha < threshold emit NaN → no rasterization
        //   - Tight quad sizing: quad shrunk to radius where gaussian * opacity = threshold → no fragment ever below threshold
        //   - Fragment discard removed: enables hardware early-Z on Adreno (discard disables it)
        Pass
        {
            ZWrite On
            ZTest [_ZTest]
            Blend Off
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "Packages/com.worldlabs.gaussian-splatting/Shaders/GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
uint _OptimizeForQuest;
half _AlphaDiscardThreshold;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	v2f o = (v2f)0;
    instID = _OrderBuffer[instID];

	SplatViewData view = _SplatViewData[instID];

	float4 centerClipPos = view.pos;

	if (centerClipPos.w <= 0)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	if (_OptimizeForQuest) {
		SplatData splat = LoadSplatData(instID);
		float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
	    centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
	}

	if (centerClipPos.w <= 0)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	o.col.r = f16tof32(view.color.x >> 16);
	o.col.g = f16tof32(view.color.x);
	o.col.b = f16tof32(view.color.y >> 16);
	o.col.a = f16tof32(view.color.y);

	// Vertex-side cull: if peak alpha (at Gaussian center) is below threshold,
	// no fragment from this primitive can ever exceed threshold — skip entirely.
	float opacity = max(o.col.a, 0);
	if (opacity < _AlphaDiscardThreshold)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	// Tight quad: shrink to the radius where gaussian(r) * opacity == threshold.
	// All rasterized fragments are guaranteed alpha >= threshold — no discard needed.
	float r_tight = sqrt(-log(_AlphaDiscardThreshold / opacity));

	uint idx = vtxID;
	float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
	quadPos *= min(r_tight, 2.0);

	o.pos = quadPos;

	float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
	o.vertex = centerClipPos;
	o.vertex.xy += deltaScreenPos * centerClipPos.w;

    return o;
}

// No discard: tight quads guarantee every rasterized fragment has alpha >= threshold.
// Without discard, Adreno hardware early-Z can reject occluded fragments before
// running the fragment shader — this is the key to the ~19ms overdraw saving.
half4 frag (v2f i) : SV_Target
{
	float power = -dot(i.pos, i.pos);
	half alpha = saturate(exp(power) * max(i.col.a, 0));
    return half4(i.col.rgb, 1);
}

ENDCG
        }

        // Pass 2 — Depth Z-Prepass for GSP-CULL-03 (depth proximity transparency)
        // Draws all splats and records the nearest-splat NDC depth per pixel into a R32F color RT.
        // BlendOp [_DepthBlendOp] is Max on reversed-Z (Vulkan/Quest) or Min on conventional-Z (DX/PCVR):
        //   reversed-Z: near=1, far=0 → Max keeps the closest (highest) depth value
        //   conventional-Z: near=0, far=1 → Min keeps the closest (lowest) depth value
        // Draw order does not matter because the blend op finds the nearest per pixel automatically.
        // Vertex uses tight quads + vertex-side cull (same as Pass 1) so no discard is needed.
        Pass
        {
            ZWrite Off
            BlendOp [_DepthBlendOp]
            Blend One One
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "Packages/com.worldlabs.gaussian-splatting/Shaders/GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
uint _OptimizeForQuest;
half _AlphaDiscardThreshold;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	v2f o = (v2f)0;
    instID = _OrderBuffer[instID];

	SplatViewData view = _SplatViewData[instID];

	float4 centerClipPos = view.pos;

	if (centerClipPos.w <= 0)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	if (_OptimizeForQuest) {
		SplatData splat = LoadSplatData(instID);
		float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
	    centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
	}

	if (centerClipPos.w <= 0)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	o.col.r = f16tof32(view.color.x >> 16);
	o.col.g = f16tof32(view.color.x);
	o.col.b = f16tof32(view.color.y >> 16);
	o.col.a = f16tof32(view.color.y);

	float opacity = max(o.col.a, 0);
	if (opacity < _AlphaDiscardThreshold)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	float r_tight = sqrt(-log(_AlphaDiscardThreshold / opacity));

	uint idx = vtxID;
	float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
	quadPos *= min(r_tight, 2.0);

	o.pos = quadPos;

	float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
	o.vertex = centerClipPos;
	o.vertex.xy += deltaScreenPos * centerClipPos.w;

    return o;
}

// Output NDC depth as color into the R32F prepass RT.
// BlendOp Min/Max retains the nearest-splat depth per pixel across all draw calls.
float4 frag (v2f i) : SV_Target
{
	// i.vertex.z is the post-perspective NDC depth [0,1] (reversed or not depending on platform)
	return float4(i.vertex.z, 0, 0, 0);
}

ENDCG
        }

        // Pass 3 — GSP-CULL-03: depth-proximity transparent accumulation
        // Alpha-blends splats back-to-front (same as Pass 0) but rejects fragments that are
        // more than _ProximityDepthRange behind the front surface established by Pass 2.
        // This preserves correct alpha blending for visible layers while eliminating deep overdraw.
        // Combines with the stencil overdraw cap for a hard per-pixel layer ceiling.
        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            ZTest Always
            Cull Off

            Stencil
            {
                Ref [_StencilOverdrawCap]
                Comp Greater
                Pass IncrSat
                Fail Keep
            }

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "Packages/com.worldlabs.gaussian-splatting/Shaders/GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
uint _OptimizeForQuest;
half _AlphaDiscardThreshold;
float _ProximityDepthRange;

Texture2D<float> _GaussianPrepassDepth;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	v2f o = (v2f)0;
    instID = _OrderBuffer[instID];

	SplatViewData view = _SplatViewData[instID];

	float4 centerClipPos = view.pos;

	if (centerClipPos.w <= 0)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	if (_OptimizeForQuest) {
		SplatData splat = LoadSplatData(instID);
		float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
	    centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
	}

	if (centerClipPos.w <= 0)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	o.col.r = f16tof32(view.color.x >> 16);
	o.col.g = f16tof32(view.color.x);
	o.col.b = f16tof32(view.color.y >> 16);
	o.col.a = f16tof32(view.color.y);

	float opacity = max(o.col.a, 0);
	if (opacity < _AlphaDiscardThreshold)
	{
		o.vertex = asfloat(0x7fc00000);
		return o;
	}

	float r_tight = sqrt(-log(_AlphaDiscardThreshold / opacity));

	uint idx = vtxID;
	float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
	quadPos *= min(r_tight, 2.0);

	o.pos = quadPos;

	float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
	o.vertex = centerClipPos;
	o.vertex.xy += deltaScreenPos * centerClipPos.w;

	if (_SplatBitsValid)
	{
		uint wordIdx = instID / 32;
		uint bitIdx = instID & 31;
		uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
		if (selVal & (1 << bitIdx))
			o.col.a = -1;
	}

    return o;
}

half4 frag (v2f i) : SV_Target
{
	// Proximity cull: reject fragments too far behind the nearest splat surface.
	// Uses integer texel coordinates (i.vertex.xy is pixel position in the scaled RT).
	float frontDepth = _GaussianPrepassDepth.Load(int3((int2)i.vertex.xy, 0));

	// On reversed-Z (Vulkan/Quest): higher depth = closer to camera.
	// Fragment is "behind" when its depth is lower (farther) than frontDepth.
	// On conventional-Z (DX/PCVR): fragment is "behind" when its depth is higher.
#if defined(UNITY_REVERSED_Z)
	float behindDist = frontDepth - i.vertex.z;
#else
	float behindDist = i.vertex.z - frontDepth;
#endif
	if (behindDist > _ProximityDepthRange)
		discard;

	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}

	// Tight quads mean alpha is always >= threshold here; no discard needed.
    return half4(i.col.rgb * alpha, alpha);
}

ENDCG
        }
    }
}
