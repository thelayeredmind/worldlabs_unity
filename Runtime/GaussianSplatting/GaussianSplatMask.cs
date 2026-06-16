// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [CreateAssetMenu(fileName = "GaussianSplatMask", menuName = "Gaussian Splat/Splat Mask")]
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

            // Runtime array, populated from dataAsset.bytes in OnAfterDeserialize. Never serialized.
            [NonSerialized] public int[] splatIndices = Array.Empty<int>();
        }

        public List<Entry> entries = new();

        public void OnBeforeSerialize() { }

        public void OnAfterDeserialize()
        {
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                if (entry.dataAsset != null && entry.dataAsset.bytes != null)
                    entry.splatIndices = BytesToIndices(entry.dataAsset.bytes);
                else if (entry.legacySplatIndices != null && entry.legacySplatIndices.Length > 0)
                    entry.splatIndices = entry.legacySplatIndices;
                else
                    entry.splatIndices = Array.Empty<int>();
            }
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
