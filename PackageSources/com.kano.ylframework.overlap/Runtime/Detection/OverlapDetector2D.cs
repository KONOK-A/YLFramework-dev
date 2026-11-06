using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Overlap
{
    /// <summary>Main-thread, owner-scene Union detector. Only complete observations commit contacts.</summary>
    [DisallowMultipleComponent]
    public sealed class OverlapDetector2D : MonoBehaviour
    {
        [SerializeField] private OverlapComposite2D composite = new OverlapComposite2D();
        [SerializeField] private OverlapSamplingMode samplingMode;
        [SerializeField] private float frequency = 10;
        [SerializeField] private int initialCapacity = 32;
        [SerializeField] private int maxCapacity = 256;
        [SerializeField] private bool growBuffer;
        [SerializeField] private bool runOnEnable = true;
        private Collider2D[] _buffer;
        private OverlapShape2D[] _worldShapes = Array.Empty<OverlapShape2D>();
        private readonly OverlapContactTracker<OverlapContact<Collider2D>> _tracker = new OverlapContactTracker<OverlapContact<Collider2D>>();
        private readonly List<OverlapContact<Collider2D>> _released = new List<OverlapContact<Collider2D>>();
        private OverlapFilter2D _filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = ~0 });
        private double _elapsed;
        private long _generation;
        private int _sceneHandle;
        private bool _filterChanged;
        private bool _configurationChanged;
        private Action<OverlapContact<Collider2D>> _entered;
        private Action<OverlapContact<Collider2D>> _stayed;
        private Action<OverlapContact<Collider2D>, OverlapExitReason> _exited;
        private Delegate[] _enterObservers = Array.Empty<Delegate>();
        private Delegate[] _stayObservers = Array.Empty<Delegate>();
        private Delegate[] _exitObservers = Array.Empty<Delegate>();

        public event Action<OverlapContact<Collider2D>> Entered
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
        public event Action<OverlapContact<Collider2D>> Stayed
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
        public event Action<OverlapContact<Collider2D>, OverlapExitReason> Exited
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

        public OverlapComposite2D Composite
        {
            get => composite;
            set
            {
                composite = value ?? throw new ArgumentNullException(nameof(value));
                _configurationChanged = true;
                _generation++;
            }
        }
        public OverlapFilter2D Filter
        {
            get => _filter;
            set
            {
                value.Validate();
                _filter = value;
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
        public string ConfigurationError { get; private set; } = string.Empty;
        public OverlapQueryResult2D LastQuery
        {
            get; private set;
        }
        /// <summary>A live read-only view of the last committed snapshot, never the partial query output.</summary>
        public ReadOnlyCollection<OverlapContact<Collider2D>> LastComplete => _tracker.View;

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
            // Unity normally calls OnDisable first. No second, invented owner-destroyed notification.
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
            if (!OverlapShape2D.IsFinite(frequency) || frequency <= 0)
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

        /// <summary>Returns true only for an uninterrupted complete commit. Stop blocks explicit sampling until StartDetection.</summary>
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
                if (!Prepare())
                {
                    return false;
                }
                long generation = _generation;
                var scene = gameObject.scene.GetPhysicsScene2D();
                while (true)
                {
                    _tracker.Begin();
                    bool complete = true;
                    int rawCount = 0;
                    for (int i = 0; i < composite.Count; i++)
                    {
                        var query = OverlapQuery2D.Query(scene, _worldShapes[i], _filter, _buffer);
                        if (query.Status == OverlapQueryStatus.Invalidated)
                        {
                            LastQuery = query;
                            return false;
                        }
                        rawCount = rawCount > int.MaxValue - query.RawCount ? int.MaxValue : rawCount + query.RawCount;
                        complete &= query.Status == OverlapQueryStatus.Complete;
                        for (int j = 0; j < query.Count; j++)
                        {
                            var collider = _buffer[j];
                            var body = collider.attachedRigidbody;
                            UnityEngine.Object target = _filter.GroupByRigidbody && body ? (UnityEngine.Object)body : collider;
                            int identity = target.GetInstanceID();
                            complete &= _tracker.Add(identity,
                                new OverlapContact<Collider2D>(collider, target, identity, collider.GetInstanceID()), _buffer.Length);
                        }
                    }
                    LastQuery = new OverlapQueryResult2D(_tracker.Pending.Count, rawCount,
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
                    _buffer = new Collider2D[size];
                }
                var reason = _filterChanged ? OverlapExitReason.FilterChanged :
                    _configurationChanged ? OverlapExitReason.Reconfigured : OverlapExitReason.Separated;
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
                if (composite == null)
                {
                    throw new InvalidOperationException("Composite cannot be null.");
                }
                int count = composite.Count;
                if (_worldShapes.Length < count)
                {
                    _worldShapes = new OverlapShape2D[count];
                }
                var matrix = transform.localToWorldMatrix;
                for (int i = 0; i < count; i++)
                {
                    _worldShapes[i] = composite.ToWorld(i, matrix);
                }
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
                LastQuery = new OverlapQueryResult2D(0, 0, OverlapQueryStatus.Invalidated);
                return false;
            }
            if (_buffer == null || _buffer.Length > maxCapacity || _buffer.Length < initialCapacity)
            {
                _buffer = new Collider2D[initialCapacity];
            }
            return true;
        }

        private void Dispatch(List<OverlapContact<Collider2D>> contacts, Delegate[] observers, long generation)
        {
            for (int i = 0; i < contacts.Count && generation == _generation; i++)
            {
                var contact = contacts[i];
                for (int j = 0; j < observers.Length && generation == _generation; j++)
                {
                    ((Action<OverlapContact<Collider2D>>)observers[j])(contact);
                }
            }
        }

        private void DispatchExits(List<OverlapContact<Collider2D>> contacts, OverlapExitReason reason,
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
                    else if (contact.Target is Collider2D collider && (!collider.enabled || !collider.gameObject.activeInHierarchy) ||
                        contact.Target is Rigidbody2D body && (!body.simulated || !body.gameObject.activeInHierarchy))
                    {
                        actual = OverlapExitReason.TargetDisabled;
                    }
                }
                for (int j = 0; j < observers.Length && generation == _generation; j++)
                {
                    ((Action<OverlapContact<Collider2D>, OverlapExitReason>)observers[j])(contact, actual);
                }
            }
        }

        private void Release(OverlapExitReason reason)
        {
            _generation++;
            _elapsed = 0;
            LastQuery = new OverlapQueryResult2D(0, 0, OverlapQueryStatus.Invalidated);
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
