using System;
using UnityEngine;

namespace YLFramework.Audio
{
    /// <summary>由调用方保活的 Clip 及其随机抽选相对权重，不转移资源所有权。</summary>
    [Serializable]
    public struct AudioWeightedClip
    {
        /// <summary>由调用方保活的候选 Clip；空引用和已销毁对象不参与抽选。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Clip")]
        public AudioClip clip;

        /// <summary>有限非负的相对权重；零表示排除此条目。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Weight")]
        public float weight;

        /// <summary>保存 Clip 和权重但不在此处校验；抽选器会校验所有条目。</summary>
        /// <param name="clip">由调用方保活的候选 Clip。</param>
        /// <param name="weight">相对权重，默认为一。</param>
        public AudioWeightedClip(AudioClip clip, float weight = 1f)
        {
            this.clip = clip;
            this.weight = weight;
        }
    }
}
