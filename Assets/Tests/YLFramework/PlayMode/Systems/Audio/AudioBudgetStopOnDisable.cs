using UnityEngine;
using YLFramework.Audio;

namespace YLFramework.Tests.Audio
{
    public sealed class AudioBudgetStopOnDisable : MonoBehaviour
    {
        public AudioHandle Other;
        [global::UnityEngine.Serialization.FormerlySerializedAs("Calls")]
        public int calls;
        [global::UnityEngine.Serialization.FormerlySerializedAs("DisableCache")]
        public bool disableCache;

        private void OnDisable()
        {
            calls++;
            if (disableCache && calls == 1)
            {
                AudioKit.SoundCacheCapacity = 0;
            }

            Other.Stop();
        }
    }
}
