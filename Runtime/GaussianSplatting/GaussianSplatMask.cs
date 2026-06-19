// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatMask : ScriptableObject, ISerializationCallbackReceiver
    {
        [Serializable]
        public class Entry
        {
            public string label = "Selection";
            [Range(0f, 1f)] public float weight = 1f;

            // Binary sub-asset: indices encoded as raw little-endian bytes (4 bytes per index).
            public GaussianSplatMaskData dataAsset;

            // Legacy field — populated on old assets, migrated to dataAsset by the editor on next save.
            [HideInInspector] public int[] legacySplatIndices;

            // Cached runtime array. Populated lazily on first access, or set directly by the editor.
            [NonSerialized] int[] m_SplatIndices;
            [NonSerialized] bool m_SplatIndicesLoaded;

            public int[] splatIndices
            {
                get
                {
                    if (!m_SplatIndicesLoaded)
                    {
                        if (dataAsset != null && dataAsset.bytes != null && dataAsset.bytes.Length > 0)
                            m_SplatIndices = BytesToIndices(dataAsset.bytes);
                        else if (legacySplatIndices != null && legacySplatIndices.Length > 0)
                            m_SplatIndices = legacySplatIndices;
                        else
                            m_SplatIndices = Array.Empty<int>();
                        m_SplatIndicesLoaded = true;
                    }
                    return m_SplatIndices;
                }
                set
                {
                    m_SplatIndices = value;
                    m_SplatIndicesLoaded = true;
                }
            }

            public void InvalidateCache() { m_SplatIndicesLoaded = false; }
        }

        public List<Entry> entries = new();

        public void OnBeforeSerialize() { }

        // Invalidate caches so splatIndices re-reads from dataAsset on next access.
        public void OnAfterDeserialize()
        {
            foreach (var entry in entries)
                entry?.InvalidateCache();
        }

        public static int[] BytesToIndices(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Array.Empty<int>();
            int count = bytes.Length / 4;
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = bytes[i * 4] | (bytes[i * 4 + 1] << 8) | (bytes[i * 4 + 2] << 16) | (bytes[i * 4 + 3] << 24);
            return result;
        }

        public static byte[] IndicesToBytes(int[] indices)
        {
            if (indices == null || indices.Length == 0) return Array.Empty<byte>();
            var bytes = new byte[indices.Length * 4];
            for (int i = 0; i < indices.Length; i++)
            {
                int v = indices[i];
                bytes[i * 4]     = (byte)v;
                bytes[i * 4 + 1] = (byte)(v >> 8);
                bytes[i * 4 + 2] = (byte)(v >> 16);
                bytes[i * 4 + 3] = (byte)(v >> 24);
            }
            return bytes;
        }
    }
}
