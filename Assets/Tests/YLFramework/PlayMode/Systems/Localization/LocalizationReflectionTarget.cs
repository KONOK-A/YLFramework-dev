using UnityEngine;

namespace YLFramework.Tests.Extensions.Data
{
    public sealed class LocalizationReflectionTarget : MonoBehaviour
    {
        public string text;
        public string Text
        {
            get; set;
        }
    }
}
