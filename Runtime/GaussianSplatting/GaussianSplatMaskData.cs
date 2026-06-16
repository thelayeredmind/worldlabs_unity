// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Hidden sub-asset that stores a GaussianSplatMask entry's splat index list as raw bytes.
    // Kept out of the main ScriptableObject to avoid expensive reimport on add/delete.
    public class GaussianSplatMaskData : ScriptableObject
    {
        public byte[] bytes;
    }
}
