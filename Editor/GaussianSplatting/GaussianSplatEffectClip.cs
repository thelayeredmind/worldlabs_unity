// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Timeline
{
    /// <summary>
    /// Timeline clip that drives a GaussianSplatEffectLayer.
    /// Clip length in Timeline maps to the effect's full duration arc — stretching the clip
    /// stretches the effect proportionally. The layer's own duration field sets the reference
    /// scale; clip length controls how fast or slow it plays relative to that.
    /// </summary>
    [System.Serializable]
    public class GaussianSplatEffectClip : PlayableAsset, ITimelineClipAsset
    {
        public ClipCaps clipCaps => ClipCaps.Blending | ClipCaps.Looping | ClipCaps.SpeedMultiplier;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            return ScriptPlayable<GaussianSplatEffectBehaviour>.Create(graph);
        }
    }

    public class GaussianSplatEffectBehaviour : PlayableBehaviour
    {
        GaussianSplatEffectLayer m_Layer;

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            if (m_Layer == null)
                m_Layer = playerData as GaussianSplatEffectLayer;
            if (m_Layer == null) return;

            double clipDuration = playable.GetDuration();
            if (clipDuration <= 0) return;

            // Drive m_EffectTime in [0, duration] — SetEffectParams scales to shader space.
            // Clip length in Timeline controls playback speed: longer clip = slower effect.
            float effectTime = (float)(playable.GetTime() / clipDuration) * m_Layer.duration;
            m_Layer.SetTime(effectTime);
        }

        public override void OnBehaviourPause(Playable playable, FrameData info)
        {
            // Freeze at end-of-clip so one-shot effects hold their final state.
            if (m_Layer != null)
                m_Layer.SetTime(m_Layer.duration);
        }
    }

    [TrackColor(0.2f, 0.6f, 0.9f)]
    [TrackClipType(typeof(GaussianSplatEffectClip))]
    [TrackBindingType(typeof(GaussianSplatEffectLayer))]
    public class GaussianSplatEffectTrack : TrackAsset { }
}
