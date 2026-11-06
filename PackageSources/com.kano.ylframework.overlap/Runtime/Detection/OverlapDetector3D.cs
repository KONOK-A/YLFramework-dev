using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Overlap
{
    /// <summary>Main-thread, owner-scene Union detector with complete-only contact diffs.</summary>
    [DisallowMultipleComponent]
    public sealed class OverlapDetector3D : MonoBehaviour
    {
        [SerializeField] private OverlapComposite3D composite = new OverlapComposite3D();
        [SerializeField] private OverlapSamplingMode samplingMode;
        [SerializeField] private float frequency = 10;
        [SerializeField] private int initialCapacity = 32;
        [SerializeField] private int maxCapacity = 256;
        [SerializeField] private bool growBuffer;
        [SerializeField] private bool runOnEnable = true;
        [SerializeField] private LayerMask layerMask = ~0;
        [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;
        [SerializeField] private Collider ignoredCollider;
        [SerializeField] private Transform ignoredRoot;
        [SerializeField] private bool groupByRigidbody;
        [SerializeField] private bool sortByDistance;
        [SerializeField] private float requestedBoundaryError = 0.01f;
        [SerializeField] private int maxSegments = 128;
        [SerializeField] private int maxParts = 128;
        [SerializeField] private int maxHierarchyDepth = 64;
        private OverlapQueryWorkspace3D _workspace;
        private Collider[] _buffer;
        private OverlapShape3D[] _worldShapes = Array.Empty<OverlapShape3D>();
        private readonly OverlapContactTracker<OverlapContact<Collider>> _tracker = new OverlapContactTracker<OverlapContact<Collider>>();
        private readonly List<OverlapContact<Collider>> _released = new List<OverlapContact<Collider>>();
        private OverlapFilter3D _committedFilter;
        private bool _hasCommittedFilter;
        private double _elapsed;
        private long _generation;
        private int _sceneHandle;
        private bool _filterChanged;
        private bool _configurationChanged;
        private Action<OverlapContact<Collider>> _entered;
        private Action<OverlapContact<Collider>> _stayed;
        private Action<OverlapContact<Collider>, OverlapExitReason> _exited;
        private Delegate[] _enterObservers = Array.Empty<Delegate>();
        private Delegate[] _stayObservers = Array.Empty<Delegate>();
        private Delegate[] _exitObservers = Array.Empty<Delegate>();

        public event Action<OverlapContact<Collider>> Entered
        {
            add
            {
                _entered += value;
                _enterObservers = _entered?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
            remove
            {
                _entered -= value;
                _enterObservers = _entered?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
        }
        public event Action<OverlapContact<Collider>> Stayed
        {
            add
            {
                _stayed += value;
                _stayObservers = _stayed?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
            remove
            {
                _stayed -= value;
                _stayObservers = _stayed?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
        }
        public event Action<OverlapContact<Collider>, OverlapExitReason> Exited
        {
            add
            {
                _exited += value;
                _exitObservers = _exited?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
            remove
            {
                _exited -= value;
                _exitObservers = _exited?.GetInvocationList() ?? Array.Empty<Delegate>();
            }
        }
        public OverlapComposite3D Composite
        {
            get => composite;
            set
            {
                composite = value ?? throw new ArgumentNullException(nameof(value));
                _configurationChanged = true;
                _generation++;
            }
        }
        public OverlapFilter3D Filter
        {
            get => new OverlapFilter3D(layerMask, triggerInteraction, ignoredCollider, ignoredRoot, groupByRigidbody, sortByDistance);
            set
            {
                layerMask = value.LayerMask;
                triggerInteraction = value.TriggerInteraction;
                ignoredCollider = value.IgnoredCollider;
                ignoredRoot = value.IgnoredRoot;
                groupByRigidbody = value.GroupByRigidbody;
                sortByDistance = value.SortByDistance;
                _filterChanged = true;
                _generation++;
            }
        }
        public OverlapSamplingMode SamplingMode
        {
            get => samplingMode;
            set
            {
                OverlapDetectionGeometry.ValidateMode(value);
                samplingMode = value;
                _elapsed = 0;
                _generation++;
            }
        }
        public float Frequency
        {
            get => frequency;
            set
            {
                OverlapDetectionGeometry.ValidateFrequency(value);
                frequency = value;
                _elapsed = 0;
                _generation++;
            }
        }
        public bool IsRunning
        {
            get; private set;
        }
        public bool IsSampling
        {
            get; private set;
        }
        public int BufferCapacity => _buffer == null ? 0 : _buffer.Length;
        public float RequestedBoundaryError => requestedBoundaryError;
        public int MaxSegments => maxSegments;
        public int MaxParts => maxParts;
        public int MaxHierarchyDepth => maxHierarchyDepth;
        public bool HasGeometryWorkspace => _workspace != null;
        public int GeometryPartCount => _workspace?.PartCount ?? 0;
        public int GeometryCookCount => _workspace?.CookCount ?? 0;
        public float GeneratedBoundaryError => _workspace?.GeneratedBoundaryError ?? 0;
        internal OverlapQueryWorkspace3D GeometryWorkspace => _workspace;
        public string ConfigurationError { get; private set; } = string.Empty;
        public OverlapQueryResult3D LastQuery
        {
            get; private set;
        }
        public ReadOnlyCollection<OverlapContact<Collider>> LastComplete => _tracker.View;

        public void ConfigureCapacity(int initialCapacity, int maxCapacity, bool growBuffer = false)
        {
            OverlapDetectionGeometry.ValidateCapacity(initialCapacity, maxCapacity);
            this.initialCapacity = initialCapacity;
            this.maxCapacity = maxCapacity;
            this.growBuffer = growBuffer;
            if (_buffer != null)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
            }
            _buffer = null;
            _generation++;
        }

        public void StartDetection()
        {
            if (!isActiveAndEnabled)
            {
                throw new InvalidOperationException("Enable the detector before starting detection.");
            }
            IsRunning = true;
            _elapsed = 0;
            _generation++;
        }

        public void ConfigureGeometryWorkspace(float requestedBoundaryError = 0.01f, int maxSegments = 128,
            int maxParts = 128, int maxHierarchyDepth = 64)
        {
            OverlapQueryWorkspace3D.ValidateConfiguration(requestedBoundaryError, maxSegments, maxParts, maxHierarchyDepth);
            this.requestedBoundaryError = requestedBoundaryError;
            this.maxSegments = maxSegments;
            this.maxParts = maxParts;
            this.maxHierarchyDepth = maxHierarchyDepth;
            _workspace?.Dispose();
            _workspace = null;
            _configurationChanged = true;
            _generation++;
        }

        public void Stop()
        {
            IsRunning = false;
            Release(OverlapExitReason.Stopped);
        }

        private void OnEnable()
        {
            _sceneHandle = gameObject.scene.handle;
            if (runOnEnable)
            {
                StartDetection();
            }
        }

        private void OnDisable()
        {
            IsRunning = false;
            Release(OverlapExitReason.Disabled);
        }

        private void OnDestroy()
        {
            try
            {
                IsRunning = false;
                Release(OverlapExitReason.Disabled);
            }
            finally
            {
                _entered = null;
                _stayed = null;
                _exited = null;
                _enterObservers = _stayObservers = _exitObservers = Array.Empty<Delegate>();
            }
        }

        private void OnValidate()
        {
            _generation++;
            _configurationChanged = true;
        }

        private void Update() => Tick(OverlapSamplingMode.Update, Time.deltaTime);
        private void FixedUpdate() => Tick(OverlapSamplingMode.FixedUpdate, Time.fixedDeltaTime);

        private void Tick(OverlapSamplingMode mode, float delta)
        {
            if (!IsRunning || samplingMode != mode)
            {
                return;
            }
            if (!OverlapShape3D.IsFinite(frequency) || frequency <= 0)
            {
                Sample();
                return;
            }
            double interval = 1d / frequency;
            _elapsed += delta;
            if (_elapsed >= interval)
            {
                _elapsed %= interval;
                Sample();
            }
        }

        public bool Sample()
        {
            if (IsSampling)
            {
                _generation++;
                return false;
            }
            if (!IsRunning || !isActiveAndEnabled)
            {
                return false;
            }
            IsSampling = true;
            try
            {
                if (_sceneHandle != gameObject.scene.handle)
                {
                    _sceneHandle = gameObject.scene.handle;
                    Release(OverlapExitReason.SceneChanged);
                    // Exit callbacks have returned; finish retirement before a new commit can release again.
                    _tracker.End();
                    if (!IsRunning || !isActiveAndEnabled)
                    {
                        return false;
                    }
                }
                long generation = _generation;
                if (!Prepare())
                {
                    return false;
                }
                if (generation != _generation || !IsRunning || !isActiveAndEnabled)
                {
                    _workspace?.Dispose();
                    _workspace = null;
                    LastQuery = new OverlapQueryResult3D(0, 0, OverlapQueryStatus.Invalidated);
                    return false;
                }
                var filter = Filter;
                var scene = gameObject.scene.GetPhysicsScene();
                while (true)
                {
                    _tracker.Begin();
                    bool complete = true;
                    int rawCount = 0;
                    for (int i = 0; i < composite.Count; i++)
                    {
                        var query = OverlapQuery3D.Query(scene, _worldShapes[i], filter, _buffer, _workspace);
                        rawCount = rawCount > int.MaxValue - query.RawCount ? int.MaxValue : rawCount + query.RawCount;
                        if (query.Status == OverlapQueryStatus.Invalidated)
                        {
                            LastQuery = new OverlapQueryResult3D(0, rawCount, OverlapQueryStatus.Invalidated, query.InvalidationReason);
                            return false;
                        }
                        complete &= query.Status == OverlapQueryStatus.Complete;
                        for (int j = 0; j < query.Count; j++)
                        {
                            var collider = _buffer[j];
                            var body = collider.attachedRigidbody;
                            UnityEngine.Object target = filter.GroupByRigidbody && body ? (UnityEngine.Object)body : collider;
                            int identity = target.GetInstanceID();
                            complete &= _tracker.Add(identity,
                                new OverlapContact<Collider>(collider, target, identity, collider.GetInstanceID()), _buffer.Length);
                        }
                    }
                    LastQuery = new OverlapQueryResult3D(_tracker.Pending.Count, rawCount,
                        complete ? OverlapQueryStatus.Complete : OverlapQueryStatus.MayBeTruncated);
                    if (complete)
                    {
                        break;
                    }
                    if (!growBuffer || _buffer.Length == maxCapacity)
                    {
                        return false;
                    }
                    int size = _buffer.Length <= maxCapacity / 2 ? _buffer.Length * 2 : maxCapacity;
                    Array.Clear(_buffer, 0, _buffer.Length);
                    _buffer = new Collider[size];
                }
                bool serializedFilterChanged = _configurationChanged && _hasCommittedFilter && !filter.Equals(_committedFilter);
                var reason = _filterChanged || serializedFilterChanged ? OverlapExitReason.FilterChanged :
                    _configurationChanged ? OverlapExitReason.Reconfigured : OverlapExitReason.Separated;
                _committedFilter = filter;
                _hasCommittedFilter = true;
                _filterChanged = _configurationChanged = false;
                _tracker.Commit();
                DispatchExits(_tracker.Exits, reason, generation, true);
                Dispatch(_tracker.Enters, _enterObservers, generation);
                Dispatch(_tracker.Stays, _stayObservers, generation);
                return _generation == generation;
            }
            finally
            {
                if (_buffer != null)
                {
                    Array.Clear(_buffer, 0, _buffer.Length);
                }
                _tracker.End();
                IsSampling = false;
            }
        }

        private bool Prepare()
        {
            ConfigurationError = string.Empty;
            try
            {
                OverlapDetectionGeometry.ValidateFrequency(frequency);
                OverlapDetectionGeometry.ValidateMode(samplingMode);
                OverlapDetectionGeometry.ValidateCapacity(initialCapacity, maxCapacity);
                OverlapQueryWorkspace3D.ValidateConfiguration(requestedBoundaryError, maxSegments, maxParts, maxHierarchyDepth);
                _ = Filter;
                if (composite == null)
                {
                    throw new InvalidOperationException("Composite cannot be null.");
                }
                int count = composite.Count;
                if (_worldShapes.Length < count)
                {
                    _worldShapes = new OverlapShape3D[count];
                }
                var matrix = transform.localToWorldMatrix;
                bool needsWorkspace = false;
                for (int i = 0; i < count; i++)
                {
                    _worldShapes[i] = composite.ToWorld(i, matrix);
                    needsWorkspace |= _worldShapes[i].NeedsWorkspace;
                }
                if (needsWorkspace && !Application.isPlaying)
                {
                    _workspace?.Dispose();
                    _workspace = null;
                    ConfigurationError = "RequiresPlayMode: curved 3D sampling does not create Edit-mode physics scenes.";
                    LastQuery = new OverlapQueryResult3D(0, 0, OverlapQueryStatus.Invalidated, OverlapQueryInvalidationReason.RequiresPlayMode);
                    return false;
                }
                if (!needsWorkspace || _workspace != null && (_workspace.RequestedBoundaryError != requestedBoundaryError ||
                    _workspace.MaxSegments != maxSegments || _workspace.MaxParts != maxParts || _workspace.MaxHierarchyDepth != maxHierarchyDepth))
                {
                    _workspace?.Dispose();
                    _workspace = null;
                }
                if (needsWorkspace && _workspace == null)
                    _workspace = new OverlapQueryWorkspace3D(requestedBoundaryError, maxSegments, maxParts, maxHierarchyDepth);
            }
            catch (ArgumentException error)
            {
                ConfigurationError = error.Message;
            }
            catch (InvalidOperationException error)
            {
                ConfigurationError = error.Message;
            }
            if (ConfigurationError.Length != 0)
            {
                LastQuery = new OverlapQueryResult3D(0, 0, OverlapQueryStatus.Invalidated,
                    OverlapQueryInvalidationReason.InvalidGeometry);
                return false;
            }
            if (_buffer == null || _buffer.Length > maxCapacity || _buffer.Length < initialCapacity)
            {
                _buffer = new Collider[initialCapacity];
            }
            return true;
        }

        private void Dispatch(List<OverlapContact<Collider>> contacts, Delegate[] observers, long generation)
        {
            for (int i = 0; i < contacts.Count && generation == _generation; i++)
            {
                var contact = contacts[i];
                for (int j = 0; j < observers.Length && generation == _generation; j++)
                {
                    ((Action<OverlapContact<Collider>>)observers[j])(contact);
                }
            }
        }

        private void DispatchExits(List<OverlapContact<Collider>> contacts, OverlapExitReason reason,
            long generation, bool inspectTarget)
        {
            var observers = _exitObservers;
            for (int i = 0; i < contacts.Count && generation == _generation; i++)
            {
                var contact = contacts[i];
                var actual = reason;
                if (inspectTarget)
                {
                    if (!contact.Target)
                    {
                        actual = OverlapExitReason.TargetDestroyed;
                    }
                    else if (contact.Target is Collider collider && (!collider.enabled || !collider.gameObject.activeInHierarchy) ||
                        contact.Target is Rigidbody body && !body.gameObject.activeInHierarchy)
                    {
                        actual = OverlapExitReason.TargetDisabled;
                    }
                }
                for (int j = 0; j < observers.Length && generation == _generation; j++)
                {
                    ((Action<OverlapContact<Collider>, OverlapExitReason>)observers[j])(contact, actual);
                }
            }
        }

        private void Release(OverlapExitReason reason)
        {
            _generation++;
            _workspace?.Dispose();
            _workspace = null;
            _elapsed = 0;
            _committedFilter = default;
            _hasCommittedFilter = false;
            LastQuery = new OverlapQueryResult3D(0, 0, OverlapQueryStatus.Invalidated);
            if (_tracker.View.Count == 0)
            {
                return;
            }
            bool wasSampling = IsSampling;
            IsSampling = true;
            _tracker.Release(_released);
            try
            {
                DispatchExits(_released, reason, _generation, false);
            }
            finally
            {
                _released.Clear();
                if (_buffer != null)
                {
                    Array.Clear(_buffer, 0, _buffer.Length);
                }
                if (!wasSampling)
                {
                    _tracker.End();
                }
                IsSampling = wasSampling;
            }
        }
    }
}
