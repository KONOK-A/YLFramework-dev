using System;
using UnityEngine;

namespace YLFramework.Tests
{
    public sealed class InstanceLifecycleProbe : MonoBehaviour
    {
        public static Action<GameObject> OnAwake;
        public static Action<GameObject> OnEnabled;
        public static Action<GameObject> OnDisabled;
        public static Action<GameObject> OnDestroyed;
        private void Awake() => OnAwake?.Invoke(gameObject);
        private void OnEnable() => OnEnabled?.Invoke(gameObject);
        private void OnDisable() => OnDisabled?.Invoke(gameObject);
        private void OnDestroy() => OnDestroyed?.Invoke(gameObject);
        public static void ResetCallbacks() => OnAwake = OnEnabled = OnDisabled = OnDestroyed = null;
    }
}
