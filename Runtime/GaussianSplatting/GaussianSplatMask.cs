// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [CreateAssetMenu(fileName = "GaussianSplatMask", menuName = "Gaussian Splat/Splat Mask")]
    public class GaussianSplatMask : ScriptableObject
    {
        [Serializable]
        public class Selection
        {
            public int[] splatIndices = Array.Empty<int>();
        }

        [Serializable]
        public class Entry
        {
            public string label = "Selection";
            public Selection selection = new();
            [Range(0f, 1f)] public float weight = 1f;
        }

        public List<Entry> entries = new();
    }
}
