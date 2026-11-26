using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    /// <summary>Owns one scene load handle until that exact native scene has actually ended.</summary>
    /// <remarks>Dispose only requests asynchronous cleanup. Failed cleanup remains owned and can be retried with UnloadAsync.</remarks>
    public sealed class SceneLease : IDisposable, IResourceLease
    {
        private readonly int _priority;
        private readonly bool _activateOnLoad;
        private readonly float _activationTimeoutSeconds;
        private readonly CancellationToken _cancellation;
        private readonly List<ResourceRequest<bool>> _activationWaiters = new();
        private readonly List<ResourceRequest<bool>> _unloadWaiters = new();
        private readonly List<(Scene Scene, int Frame)> _earlyUnloadEvents = new();
        private ResourceRequest<SceneLease> _loadRequest;
        private readonly ResourceEventTrace _trace;
        private ResourceEventTrace _locateWork;
        private ResourceEventTrace _loadWork;
        private ResourceEventTrace _unloadWork;
        private bool _loadWaitEnded;
        private bool _loadObservedLate;
        private AsyncOperationHandle<IList<IResourceLocation>> _locations;
        private AsyncOperationHandle<SceneInstance> _loadHandle;
        private IDisposable _tick;
        private Scene _scene;
        private AsyncOperation _activation;
        private AsyncOperation _unload;
        private bool _ownsLocations;
        private bool _ownsLoadHandle;
        private bool _nativeStarted;
        private bool _loadReady;
        private bool _hasScene;
        private bool _activationRequested;
        private bool _activationStarted;
        private bool _activated;
        private bool _closeRequested;
        private bool _roundFailed;
        private bool _unloadSubmitting;
        private bool _releasedLoadHandle;
        private bool _releaseFailed;
        private bool _pumping;
        private int _unloadedFrame = -1;
        private int _unloadCompletedFrame = -1;
        private float _activationDeadline;

        internal ResourceSystem System
        {
            get;
        }
        internal ResourceScope Scope
        {
            get; set;
        }
        ResourceSystem IResourceLease.System => System;
        ResourceScope IResourceLease.Scope
        {
            get => Scope; set => Scope = value;
        }

        /// <summary>The requested Addressables key, preserved as supplied and not used to find a replacement scene.</summary>
        public string Key
        {
            get;
        }
        /// <summary>The load mode of this independent scene intent.</summary>
        public LoadSceneMode Mode
        {
            get;
        }
        /// <summary>The owning system's load generation, not merely a reusable native integer handle.</summary>
        public long LoadSequence
        {
            get;
        }
        /// <summary>Cleanup has been requested or an external unload has finished; this does not imply settlement.</summary>
        public bool IsDisposed
        {
            get; private set;
        }
        /// <summary>All loading, activation and native unload obligations have ended and the load handle has been returned.</summary>
        public bool IsSettled
        {
            get; private set;
        }
        /// <summary>The most recent load, activation or cleanup failure. An unload failure retains the owner for retry.</summary>
        public ResourceException LastError
        {
            get; private set;
        }

        /// <summary>Whether the live scene identity can still be borrowed, including a not-yet-activated scene.</summary>
        public bool IsValid
        {
            get
            {
                System.CheckThread();
                return !IsDisposed && !IsSettled && System.IsSessionActive && _loadReady && _hasScene &&
                    _unloadedFrame < 0 && _scene.IsValid() && (!_activated || _scene.isLoaded);
            }
        }

        /// <summary>Borrow this load's actual Scene. Deferred scenes can be valid without being loaded/activated yet.</summary>
        public Scene Scene
        {
            get
            {
                System.CheckThread();
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(SceneLease));
                if (!System.IsSessionActive)
                    throw Error(ResourceError.SessionUnavailable, "The scene belongs to an ended resource session.");
                if (!IsValid)
                    throw Error(ResourceError.SceneUnloaded, "The leased scene identity is no longer available.");
                return _scene;
            }
        }

        internal bool HasActivationGate => _nativeStarted && !_activateOnLoad && !_activationStarted && !IsSettled && _unloadedFrame < 0;
        internal bool HasPendingUnload => _unloadSubmitting || _unload != null;
        internal bool Matches(Scene scene) => _hasScene && _unloadedFrame < 0 && _scene == scene;
        internal ResourceException CleanupError => _roundFailed ? LastError : null;
        internal bool IsLoadingRequest(ResourceRequest<SceneLease> request) => _loadRequest != null && ReferenceEquals(_loadRequest, request);

        internal void ReleaseActivationGateForCleanup()
        {
            if (!HasActivationGate)
                return;
            if (_roundFailed)
            {
                _roundFailed = false;
                LastError = null;
            }
            _activationRequested = true;
        }

        internal ResourceSceneDiagnostics CaptureDiagnostics()
        {
            var loaded = _hasScene && _scene.IsValid() && _scene.isLoaded;
            var status = IsSettled ? "Settled" : LastError != null ? "Failed" : _closeRequested ? "Closing" :
                loaded && _activated ? "Loaded" : _activationStarted ? "Activating" : _loadReady ? "Awaiting activation" : "Loading";
            return new ResourceSceneDiagnostics(LoadSequence, Key, Scope?.OwnerLabel, status,
                _hasScene && _scene.IsValid() ? _scene.name : null, loaded, LastError?.ToString());
        }

        internal void RetryCleanup()
        {
            System.CheckThread();
            if (!_roundFailed || _releaseFailed || IsSettled)
                return;
            _roundFailed = false;
            LastError = null;
            BeginClose();
        }

        internal SceneLease(ResourceSystem system, ResourceRequest<SceneLease> request, string key,
            LoadSceneMode mode, bool activateOnLoad, int priority, float activationTimeoutSeconds,
            CancellationToken cancellation, long sequence)
        {
            System = system;
            _loadRequest = request;
            _trace = request.EventTrace;
            Key = key;
            Mode = mode;
            LoadSequence = sequence;
            _activateOnLoad = activateOnLoad;
            _activationRequested = activateOnLoad;
            _priority = priority;
            _activationTimeoutSeconds = activationTimeoutSeconds;
            _cancellation = cancellation;
        }

        internal void Start()
        {
            _loadRequest.Attach(BeginClose, ReadLoadProgress);
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _tick = System.AddTick(Pump);
            try
            {
                _locateWork = _trace.StartWork(ResourceEventPhase.SceneLocate);
                _locations = Addressables.LoadResourceLocationsAsync(Key, typeof(SceneInstance));
                _ownsLocations = true;
            }
            catch (Exception error)
            {
                _locateWork.Record(ResourceEventKind.Work, ResourceRequestStatus.Failed, ResourceError.LoadFailed, _loadWaitEnded);
                BeginClose();
                FailLoad(Error(ResourceError.LoadFailed, "Scene location lookup could not start.", error));
                Settle();
            }
        }

        /// <summary>Activate a deferred scene and observe actual native activation with an independent waiter.</summary>
        /// <remarks>Canceling this waiter does not close the lease or reverse activation.</remarks>
        public ResourceRequest<bool> ActivateAsync()
        {
            System.CheckThread();
            var request = new ResourceRequest<bool> { EventTrace = System.BeginEvent(ResourceEventSource.SceneActivate, Key, Scope?.OwnerLabel).WithLoad(_trace.LoadId) };
            if (IsDisposed)
                return ResourceSystem.Fail(request, new ObjectDisposedException(nameof(SceneLease)));
            if (!System.IsSessionActive)
                return ResourceSystem.Fail(request, Error(ResourceError.SessionUnavailable, "The scene session ended."));
            if (_unloadedFrame >= 0)
                return ResourceSystem.Fail(request, Error(ResourceError.SceneUnloaded, "The scene has been unloaded."));
            if (_activated)
            {
                request.SetSuccess(true);
                return request;
            }
            _roundFailed = false;
            LastError = null;
            _activationWaiters.Add(request);
            request.Attach(() => _activationWaiters.Remove(request), () => _activation?.progress ?? 0);
            _activationRequested = true;
            return request;
        }

        /// <summary>Request or retry cleanup and independently observe its real completion.</summary>
        /// <remarks>Null/throwing native submission and last-scene rejection do not return the load handle.</remarks>
        public ResourceRequest<bool> UnloadAsync()
        {
            System.CheckThread();
            if (IsSettled)
                return ResourceSystem.Success(true);
            if (_releaseFailed)
                return ResourceSystem.Failure<bool>(LastError);
            if (_roundFailed)
            {
                _roundFailed = false;
                LastError = null;
            }
            var request = new ResourceRequest<bool>();
            _unloadWaiters.Add(request);
            request.Attach(() => _unloadWaiters.Remove(request), () => _unload?.progress ?? 0);
            BeginClose();
            return request;
        }

        ResourceRequest<bool> IResourceLease.CloseAsync() => UnloadAsync();

        /// <summary>Begin the same asynchronous cleanup without waiting or discarding a failed owner.</summary>
        public void Dispose()
        {
            System.CheckThread();
            BeginClose();
        }

        private void BeginClose()
        {
            if (IsSettled || _closeRequested)
                return;
            _loadWaitEnded |= _loadRequest?.IsDone == true;
            IsDisposed = true;
            _closeRequested = true;
            _activationRequested = true;
            System.MakeInternal(this);
        }

        private float ReadLoadProgress() => _ownsLoadHandle ? _loadHandle.PercentComplete :
            _ownsLocations ? _locations.PercentComplete * 0.1f : 0;

        private void Pump()
        {
            System.CheckThread();
            if (IsSettled || _pumping)
                return;
            _pumping = true;
            try
            {
                if (!System.IsSessionActive)
                {
                    BeginClose();
                    if (_loadRequest != null)
                    {
                        FailLoad(Error(ResourceError.SessionUnavailable, "The scene session ended while loading."), ResourceRequestStatus.Invalidated);
                    }
                }
                else if (_loadRequest != null && !_loadRequest.IsDone && _cancellation.IsCancellationRequested)
                {
                    _loadRequest.Cancel();
                }
                if (_loadRequest != null && _loadRequest.IsDone)
                {
                    BeginClose();
                    _loadRequest = null;
                }

                if (_ownsLocations)
                {
                    if (!_locations.IsDone)
                        return;
                    ResolveAndStart();
                    if (IsSettled)
                        return;
                }
                if (!_ownsLoadHandle)
                    return;

                if (!_activationRequested && Time.realtimeSinceStartup >= _activationDeadline)
                {
                    BeginClose();
                    LastError = Error(ResourceError.ActivationTimedOut,
                        "Deferred scene activation timed out. Activation will be released and drained; Single replacement cannot be rolled back.");
                    FailLoad(LastError);
                }
                if (!_loadReady)
                {
                    if (!_loadHandle.IsDone)
                        return;
                    ObserveLoad();
                    if (IsSettled)
                        return;
                }

                if (_unloadedFrame >= 0)
                {
                    // EditMode draining has no advancing Time.frameCount, but still runs off the event stack.
                    if ((Time.frameCount > _unloadedFrame || !Application.isPlaying) && (_unload == null || _unload.isDone) &&
                        (_activation == null || _activation.isDone) && !_scene.IsValid() && !_scene.isLoaded)
                    {
                        Settle();
                    }
                    return;
                }
                if (_roundFailed || _releaseFailed)
                    return;
                if (_activationRequested && !_activated)
                {
                    AdvanceActivation();
                    if (!_activated || _roundFailed)
                        return;
                }
                if (_closeRequested && _activated)
                {
                    AdvanceUnload();
                }
            }
            finally
            {
                _pumping = false;
            }
        }

        private void ResolveAndStart()
        {
            var locations = _locations;
            var locateObservedLate = _loadWaitEnded;
            _locations = default;
            _ownsLocations = false;
            try
            {
                if (_locateWork.Id != 0)
                {
                    var succeeded = locations.Status == AsyncOperationStatus.Succeeded;
                    _locateWork.Record(ResourceEventKind.Work, succeeded ? ResourceRequestStatus.Succeeded : ResourceRequestStatus.Failed,
                        succeeded ? (ResourceError?)null : ResourceError.LoadFailed, locateObservedLate);
                }
                if (_closeRequested)
                {
                    return;
                }
                if (locations.Status != AsyncOperationStatus.Succeeded)
                {
                    throw Error(ResourceError.LoadFailed, "Scene location lookup failed.", locations.OperationException);
                }
                if (locations.Result == null || locations.Result.Count == 0)
                {
                    throw Error(ResourceError.AddressNotFound, "No scene location exists at this address.");
                }
                if (!ResourceSystem.HasOneDistinctLocation(locations.Result))
                {
                    throw Error(ResourceError.SceneIdentityAmbiguous, "The address resolves to multiple scene locations.");
                }
                var location = locations.Result[0];
                // Packed catalogs identify the bundled asset provider, not ISceneProvider. The latter is
                // selected separately by ProvideScene; Editor asset-database locations use SceneProvider.
                bool officialLocation = location != null &&
                    (location.ProviderId == typeof(SceneProvider).FullName ||
                     System.UsesSignedContent && ResourceContentCatalog.IsBuiltInScene(location) ||
                     Addressables.ResourceManager.ResourceProviders.Any(provider =>
                         provider.ProviderId == location.ProviderId && provider.GetType() == typeof(BundledAssetProvider)));
                if (!officialLocation)
                {
                    throw Error(ResourceError.Unsupported, "The scene requires official default scene/bundled asset data providers.");
                }
                // Register native intent before entering provider/Unity code, including synchronous callbacks.
                _nativeStarted = true;
                _activationDeadline = Time.realtimeSinceStartup + _activationTimeoutSeconds;
                _loadWork = _trace.StartWork(ResourceEventPhase.SceneLoad);
                _loadHandle = System.ProvideResourceScene(location, Mode, _activateOnLoad, _priority);
                if (!_loadHandle.IsValid())
                    throw new InvalidOperationException("SceneProvider returned an invalid scene load handle.");
                _ownsLoadHandle = true;
            }
            catch (Exception error)
            {
                if (_nativeStarted)
                    _loadWork.Record(ResourceEventKind.Work, ResourceRequestStatus.Failed, ResourceError.LoadFailed, _loadWaitEnded);
                BeginClose();
                FailLoad(error as ResourceException ?? Error(ResourceError.LoadFailed, "Scene loading could not start.", error));
            }
            finally
            {
                ReleaseSceneHandle(locations, _locateWork, locateObservedLate);
                if (!_ownsLoadHandle)
                    Settle();
            }
        }

        private void ObserveLoad()
        {
            _loadReady = true;
            _scene = _loadHandle.Result.Scene;
            _hasScene = _scene != default;
            foreach (var unloaded in _earlyUnloadEvents)
            {
                if (unloaded.Scene == _scene)
                {
                    _unloadedFrame = unloaded.Frame;
                    break;
                }
            }
            _earlyUnloadEvents.Clear();

            _loadObservedLate = _loadWaitEnded;
            var succeeded = _loadHandle.Status == AsyncOperationStatus.Succeeded;
            _loadWork.Record(ResourceEventKind.Work, succeeded ? ResourceRequestStatus.Succeeded : ResourceRequestStatus.Failed,
                succeeded ? (ResourceError?)null : ResourceError.LoadFailed, _loadObservedLate);
            if (!succeeded)
            {
                BeginClose();
                FailLoad(Error(ResourceError.LoadFailed, "Scene loading failed.", _loadHandle.OperationException));
                if (!_hasScene)
                    Settle();
                return;
            }
            if (!_hasScene || !_scene.IsValid() || _unloadedFrame >= 0 || (_activateOnLoad && !_scene.isLoaded))
            {
                BeginClose();
                FailLoad(Error(ResourceError.SceneUnloaded, "The completed scene did not survive until delivery."));
                if (!_hasScene)
                    Settle();
                return;
            }

            _activated = _activateOnLoad && _scene.isLoaded;
            if (!_closeRequested && _loadRequest != null && System.IsAvailable)
            {
                var request = _loadRequest;
                _loadRequest = null;
                System.MakeExternal(this);
                request.SetSuccess(this);
                request.NotifyCompletion();
            }
            else
            {
                BeginClose();
                if (_loadRequest != null)
                {
                    FailLoad(Error(ResourceError.SessionUnavailable, "The resource service stopped accepting scene results."), ResourceRequestStatus.Invalidated);
                }
            }
        }

        private void AdvanceActivation()
        {
            if (!_activationStarted)
            {
                _activationStarted = true;
                try
                {
                    var instance = _loadHandle.Result;
                    _activation = System.SceneActivator == null ? instance.ActivateAsync() : System.SceneActivator(instance);
                    if (_activation == null)
                        throw new InvalidOperationException("Scene activation returned no native operation.");
                }
                catch (Exception error)
                {
                    _activationStarted = false;
                    FailActivation(Error(ResourceError.LoadFailed, "Scene activation failed; ownership is retained.", error));
                    return;
                }
            }
            if (!_activation.isDone)
                return;
            if (!_scene.IsValid() || !_scene.isLoaded)
            {
                if (_scene.IsValid())
                {
                    // A completed operation cannot activate the still-valid deferred scene on retry.
                    _activation = null;
                    _activationStarted = false;
                }
                FailActivation(Error(ResourceError.SceneUnloaded, "Native activation ended without a loaded scene; ownership is retained until its unload event."));
                return;
            }
            _activated = true;
            CompleteWaiters(_activationWaiters, null);
        }

        private void FailActivation(ResourceException error)
        {
            if (_closeRequested)
            {
                _trace.StartWork(ResourceEventPhase.SceneCleanup).Record(ResourceEventKind.Work,
                    ResourceRequestStatus.Failed, error.Kind, _loadWaitEnded);
            }
            FailRound(error);
        }

        private void AdvanceUnload()
        {
            if (_unload != null)
            {
                if (!_unload.isDone)
                    return;
                if (_unloadCompletedFrame < 0)
                {
                    _unloadCompletedFrame = Time.frameCount;
                    return;
                }
                if (Application.isPlaying && Time.frameCount <= _unloadCompletedFrame)
                    return;
                if (_scene.IsValid() && _scene.isLoaded)
                {
                    _unload = null;
                    _unloadCompletedFrame = -1;
                    FailRound(Error(ResourceError.LoadFailed, "The native unload ended but the scene is still loaded; ownership is retained."));
                }
                else
                {
                    FailRound(Error(ResourceError.LoadFailed, "The native unload ended without its sceneUnloaded evidence; ownership is retained."));
                }
                return;
            }
            if (!_scene.IsValid() || !_scene.isLoaded)
            {
                // A scene in an external unload is not proof of its actual end. Await its event.
                return;
            }
            var failure = System.CheckSceneUnload(this);
            if (failure != null)
            {
                _trace.StartWork(ResourceEventPhase.SceneCleanup).Record(ResourceEventKind.Work,
                    ResourceRequestStatus.Failed, failure.Kind, _loadWaitEnded);
                FailRound(failure);
                return;
            }
            _unloadWork = _trace.StartWork(ResourceEventPhase.SceneUnload);
            _unloadSubmitting = true;
            try
            {
                _unload = System.SubmitResourceSceneUnload(_scene);
                if (_unload == null)
                {
                    FailRound(Error(ResourceError.LoadFailed, "Unity refused the scene unload; ownership is retained for retry."));
                }
            }
            catch (Exception error)
            {
                FailRound(Error(ResourceError.LoadFailed, "Native scene unload threw; ownership is retained for retry.", error));
            }
            finally
            {
                _unloadSubmitting = false;
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (IsSettled || !_nativeStarted)
                return;
            if (!_loadReady)
            {
                _earlyUnloadEvents.Add((scene, Time.frameCount));
            }
            else if (_hasScene && _scene == scene && _unloadedFrame < 0)
            {
                // Events only record evidence. Never release a handle or notify users on this stack.
                _unloadedFrame = Time.frameCount;
            }
        }

        private void FailLoad(ResourceException error, ResourceRequestStatus status = ResourceRequestStatus.Failed)
        {
            LastError = error;
            _loadWaitEnded = true;
            var request = _loadRequest;
            _loadRequest = null;
            if (request == null || request.IsDone)
                return;
            request.SetFailure(status, error);
            request.NotifyCompletion();
        }

        private void FailRound(ResourceException error)
        {
            if (_unloadWork.Id != 0)
            {
                _unloadWork.Record(ResourceEventKind.Work, ResourceRequestStatus.Failed, error.Kind, _loadWaitEnded);
                _unloadWork = default;
            }
            LastError = error;
            _roundFailed = true;
            var activation = _activationWaiters.ToArray();
            var unload = _unloadWaiters.ToArray();
            _activationWaiters.Clear();
            _unloadWaiters.Clear();
            SetWaiters(activation, error);
            SetWaiters(unload, error);
            foreach (var request in activation)
                request.NotifyCompletion();
            foreach (var request in unload)
                request.NotifyCompletion();
        }

        private void Settle()
        {
            if (IsSettled || _releaseFailed)
                return;
            _unloadWork.Record(ResourceEventKind.Work, ResourceRequestStatus.Succeeded, late: _loadWaitEnded);
            _unloadWork = default;
            var cleanup = _trace.StartWork(ResourceEventPhase.SceneCleanup);
            if (_ownsLoadHandle && !_releasedLoadHandle)
            {
                _releasedLoadHandle = true;
                try
                {
                    // H is the initial ProvideScene reference, not an extra guard pin or an AA unload handle.
                    ReleaseSceneHandle(_loadHandle, _loadWork, _loadObservedLate);
                    _ownsLoadHandle = false;
                    _loadHandle = default;
                }
                catch (Exception error)
                {
                    cleanup.Record(ResourceEventKind.Work, ResourceRequestStatus.Failed, ResourceError.LoadFailed, _loadWaitEnded);
                    _releaseFailed = true;
                    FailRound(Error(ResourceError.LoadFailed, "Returning the scene load handle failed; its owner is retained for diagnosis.", error));
                    return;
                }
            }
            IsDisposed = true;
            IsSettled = true;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _tick?.Dispose();
            _tick = null;
            _earlyUnloadEvents.Clear();
            System.ForgetResourceScene(this);
            cleanup.Record(ResourceEventKind.Work, ResourceRequestStatus.Succeeded, late: _loadWaitEnded);
            CompleteWaiters(_activationWaiters, Error(ResourceError.SceneUnloaded, "The scene ended before activation could be observed."));
            CompleteWaiters(_unloadWaiters, null);
        }

        private static void ReleaseSceneHandle(AsyncOperationHandle handle, ResourceEventTrace work, bool observedLate)
        {
            try
            {
                Addressables.ResourceManager.Release(handle);
            }
            catch
            {
                work.Record(ResourceEventKind.Release, ResourceRequestStatus.Failed, ResourceError.LoadFailed, observedLate);
                throw;
            }
            work.Record(ResourceEventKind.Release, ResourceRequestStatus.Succeeded, late: observedLate);
        }

        private static void CompleteWaiters(List<ResourceRequest<bool>> waiters, ResourceException error)
        {
            var snapshot = waiters.ToArray();
            waiters.Clear();
            SetWaiters(snapshot, error);
            foreach (var request in snapshot)
                request.NotifyCompletion();
        }

        private static void SetWaiters(ResourceRequest<bool>[] requests, ResourceException error)
        {
            foreach (var request in requests)
            {
                if (error == null)
                    request.SetSuccess(true);
                else
                    request.SetFailure(ResourceRequestStatus.Failed, error);
            }
        }

        private ResourceException Error(ResourceError kind, string message, Exception cause = null) =>
            new(kind, message + " Key: " + Key, cause);
    }
}
