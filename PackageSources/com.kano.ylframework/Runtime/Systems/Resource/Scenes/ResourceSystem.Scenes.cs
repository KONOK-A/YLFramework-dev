using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private readonly SceneProvider _sceneProvider = new();
        private readonly HashSet<SceneLease> _sceneLeases = new();
        private long _nextSceneSequence;

        // Only the native submission is replaceable by lifecycle regression tests.
        internal Func<SceneInstance, AsyncOperation> SceneActivator;
        internal Func<Scene, UnloadSceneOptions, AsyncOperation> SceneUnloader;

        /// <summary>Load one scene resolved by an Addressables key with the official default SceneProvider.</summary>
        /// <remarks>
        /// Each call owns a separate load handle. Cancellation ends only this wait; an already started
        /// scene is activated and drained asynchronously. Single replacement cannot be rolled back.
        /// Custom scene providers are not supported and no global Addressables provider is replaced.
        /// </remarks>
        /// <param name="key">An Addressables key resolving to exactly one SceneInstance location.</param>
        /// <param name="mode">Additive by default; Single can replace existing scenes.</param>
        /// <param name="activateOnLoad">False returns a lease at the native activation gate.</param>
        /// <param name="priority">The native scene load priority.</param>
        /// <param name="cancellation">Cancels result delivery, not the native load or its cleanup.</param>
        /// <param name="activationTimeoutSeconds">Finite deferred-activation limit from submission, in (0, 300].</param>
        public ResourceRequest<SceneLease> LoadSceneAsync(string key, LoadSceneMode mode = LoadSceneMode.Additive,
            bool activateOnLoad = true, int priority = 100, CancellationToken cancellation = default,
            float activationTimeoutSeconds = 30) =>
            LoadSceneCore(key, mode, activateOnLoad, priority, cancellation, activationTimeoutSeconds);

        internal ResourceRequest<SceneLease> LoadSceneCore(string key, LoadSceneMode mode, bool activateOnLoad,
            int priority, CancellationToken cancellation, float activationTimeoutSeconds,
            Action<ResourceRequest<SceneLease>> onAdmitted = null)
        {
            CheckThread();
            var trace = BeginEvent(ResourceEventSource.Scene, key);
            trace = trace.WithLoad(trace.Id);
            try
            {
                RequireAvailable();
                if (string.IsNullOrEmpty(key))
                {
                    throw new ResourceException(ResourceError.InvalidAddress, "A scene key is required.");
                }
                if ((mode != LoadSceneMode.Single && mode != LoadSceneMode.Additive) ||
                    float.IsNaN(activationTimeoutSeconds) || activationTimeoutSeconds <= 0 || activationTimeoutSeconds > 300)
                {
                    throw new ResourceException(ResourceError.InvalidConfiguration, "A scene mode and a finite activation limit in (0, 300] are required.");
                }
            }
            catch (Exception error)
            {
                return Fail(new ResourceRequest<SceneLease> { EventTrace = trace }, error);
            }

            var request = Track(new ResourceRequest<SceneLease>(lease => lease.Dispose()) { EventTrace = trace }, key, operation: nameof(LoadSceneAsync));
            _businessStarted = true;
            request.Cancellation = cancellation;
            if (cancellation.IsCancellationRequested)
            {
                onAdmitted?.Invoke(request);
                request.Cancel();
                return request;
            }

            var owner = new SceneLease(this, request, key, mode, activateOnLoad, priority,
                activationTimeoutSeconds, cancellation, ++_nextSceneSequence);
            request.ValidateBeforeTake = () =>
            {
                if (!owner.IsValid)
                {
                    throw new ResourceException(ResourceError.SceneUnloaded,
                        "The scene ended before its lease could be taken: " + key);
                }
            };
            _sceneLeases.Add(owner);
            RegisterLease(owner, key, "SceneLease #" + owner.LoadSequence);
            MakeInternal(owner);
            onAdmitted?.Invoke(request);
            owner.Start();
            return request;
        }

        internal AsyncOperationHandle<SceneInstance> ProvideResourceScene(IResourceLocation location,
            LoadSceneMode mode, bool activateOnLoad, int priority) =>
            Addressables.ResourceManager.ProvideScene(_sceneProvider, location, new LoadSceneParameters(mode), activateOnLoad, priority);

        internal void ForgetResourceScene(SceneLease lease)
        {
            _sceneLeases.Remove(lease);
            UnregisterLease(lease);
        }

        internal SceneLease GetPendingSceneLease(ResourceRequest<SceneLease> request)
        {
            CheckThread();
            foreach (var lease in _sceneLeases)
            {
                if (lease.IsLoadingRequest(request))
                    return lease;
            }
            return null;
        }

        internal ResourceException SceneCleanupError
        {
            get
            {
                foreach (var lease in _sceneLeases)
                {
                    if (lease.Scope == null && lease.IsDisposed && lease.CleanupError != null)
                    {
                        return lease.CleanupError;
                    }
                }
                return null;
            }
        }

        internal void RetrySceneCleanup()
        {
            foreach (var lease in _sceneLeases)
            {
                if (lease.Scope == null && lease.IsDisposed)
                    lease.RetryCleanup();
            }
        }

        internal ResourceException CheckSceneUnload(SceneLease target)
        {
            foreach (var lease in _sceneLeases)
            {
                if (lease.HasActivationGate)
                {
                    return new ResourceException(ResourceError.Busy, "Activate or close the deferred scene before unloading another scene.");
                }
            }

            var remaining = 0;
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded)
                    continue;
                var pending = false;
                foreach (var lease in _sceneLeases)
                {
                    if (!ReferenceEquals(lease, target) && lease.HasPendingUnload && lease.Matches(scene))
                    {
                        pending = true;
                        break;
                    }
                }
                if (!pending)
                    remaining++;
            }
            return remaining > 1 ? null : new ResourceException(ResourceError.Busy,
                "Unity cannot unload the last scene. Load a non-Resource neutral scene and retry; the scene load handle remains owned.");
        }

        internal AsyncOperation SubmitResourceSceneUnload(Scene scene) => SceneUnloader == null
            ? SceneManager.UnloadSceneAsync(scene, UnloadSceneOptions.None)
            : SceneUnloader(scene, UnloadSceneOptions.None);
    }
}
