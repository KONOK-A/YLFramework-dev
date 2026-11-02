using UnityEngine;

namespace YLFramework.Core.Tests
{
    public sealed class YLCoreSmokeSceneEntry : MonoBehaviour
    {
        public int StartCount
        {
            get; private set;
        }
        public YLCoreSmokeModule ModuleAtStart
        {
            get; private set;
        }
        public YLCoreSmokeModule ModuleAtAwake
        {
            get; private set;
        }
        public ModuleState StateAtAwake
        {
            get; private set;
        }

        private void Awake()
        {
            ModuleAtAwake = YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>();
            if (ModuleAtAwake != null)
            {
                StateAtAwake = ModuleAtAwake.State;
            }
        }

        private void Start()
        {
            StartCount++;
            ModuleAtStart = YLFramework.Core.ModuleManager.GetModule<YLCoreSmokeModule>();
        }
    }
}
