using UnityEngine;

namespace YLFramework.Tests.DataBatch
{
    public sealed class DataPlayerTextSink : MonoBehaviour
    {
        public string text
        {
            get; set;
        }
        public int Enables
        {
            get; private set;
        }
        private void OnEnable() => Enables++;
    }
}
