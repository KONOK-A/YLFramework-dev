using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace YLFramework.Overlap
{
    /// <summary>Main-thread, owner-scene Union detector. Only complete observations commit contacts.</summary>
    [DisallowMultipleComponent]
    public sealed class OverlapDetector2D : MonoBehaviour
    {
        private enum AreaSource
        {
            Composite, Positions, Transforms
        }
        [SerializeField] private OverlapComposite2D composite = new OverlapComposite2D();
        [SerializeField] private AreaSource areaSource;
        [SerializeField] private Vector3[] areaPositions = Array.Empty<Vector3>();
        [SerializeField] private Transform[] areaTransforms = Array.Empty<Transform>();
        [SerializeField] private OverlapSamplingMode samplingMode;
        [SerializeField] private float frequency = 10;
        [SerializeField] private int initialCapacity = 32;
        [SerializeField] private int maxCapacity = 256;
        [SerializeField] private bool growBuffer;
        [SerializeField] private bool runOnEnable = true;
        [SerializeField] private UnityEvent<OverlapContact<Collider2D>> enteredEvent = new UnityEvent<OverlapContact<Collider2D>>();
        [SerializeField] private UnityEvent<OverlapContact<Collider2D>> stayedEvent = new UnityEvent<OverlapContact<Collider2D>>();
        [SerializeField] private UnityEvent<OverlapContact<Collider2D>, OverlapExitReason> exitedEvent = new UnityEvent<OverlapContact<Collider2D>, OverlapExitReason>();
        [SerializeField] private ContactFilter2D contactFilter = new ContactFilter2D { useLayerMask = true, layerMask = ~0 };
        [SerializeField] private Collider2D ignoredCollider;
        [SerializeField] private Transform ignoredRoot;
        [SerializeField] private bool groupByRigidbody;
        [SerializeField] private bool sortByDistance;
        [SerializeField] private int maxShapesPerCollider = 256;
        [SerializeField] private int maxVerticesPerShape = 4096;
        [SerializeField] private float geometryTolerance = 0.00001f;
        private OverlapQueryWorkspace2D _workspace;
        private Collider2D[] _buffer;
        private OverlapShape2D[] _worldShapes = Array.Empty<OverlapShape2D>();
        private int _worldShapeCount;
        private readonly OverlapContactTracker<OverlapContact<Collider2D>> _tracker = new OverlapContactTracker<OverlapContact<Collider2D>>();
        private readonly List<OverlapContact<Collider2D>> _released = new List<OverlapContact<Collider2D>>();
        // This records the committed policy, not a second source of current configuration.
        private OverlapFilter2D _committedFilter;
        private bool _hasCommittedFilter;
        private double _elapsed;
        private long _generation;
        private int _serializedNotificationDepth;
        private OverlapExitReason? _deferredRelease;
        private bool _clearObserversAfterNotification;
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

        /// <summary>同一已提交 Enter 的序列化适配，先于 CLR 监听。一次 UnityEvent 通知是不可拆分的监听者批次。</summary>
        /// <remarks>批内停止/停用先更新运行意图，在批次结束后才清空快照和通知 Exit；旧代次的 CLR 监听与后续接触不再派发。</remarks>
        public UnityEvent<OverlapContact<Collider2D>> EnteredEvent => enteredEvent;
        public UnityEvent<OverlapContact<Collider2D>> StayedEvent => stayedEvent;
        public UnityEvent<OverlapContact<Collider2D>, OverlapExitReason> ExitedEvent => exitedEvent;

        public OverlapComposite2D Composite
        {
            get => composite;
            set
            {
                composite = value ?? throw new ArgumentNullException(nameof(value));
                areaSource = AreaSource.Composite;
                areaPositions = Array.Empty<Vector3>();
                areaTransforms = Array.Empty<Transform>();
                _configurationChanged = true;
                _generation++;
            }
        }
        public OverlapFilter2D Filter
        {
            get => new OverlapFilter2D(contactFilter, ignoredCollider, ignoredRoot, groupByRigidbody, sortByDistance);
            set
            {
                value.Validate();
                contactFilter = value.ContactFilter;
                ignoredCollider = value.IgnoredCollider;
                ignoredRoot = value.IgnoredRoot;
                groupByRigidbody = value.GroupByRigidbody;
                sortByDistance = value.SortByDistance;
                _filterChanged = true;
                _generation++;
            }
        }

        /// <summary>以复制的局部节点链替代 Composite；相邻节点转换为世界 XY AABB，不闭合。有限退化对跳过；非有限值或溢出使本轮无效。</summary>
        public void SetAreaPositions(Vector3[] positions)
        {
            areaPositions = (Vector3[])(positions ?? throw new ArgumentNullException(nameof(positions))).Clone();
            areaTransforms = Array.Empty<Transform>();
            areaSource = AreaSource.Positions;
            _configurationChanged = true;
            _generation++;
        }

        /// <summary>借用节点 Transform 的实时世界坐标，相邻对含缺失节点时跳过；不接管其生命周期。</summary>
        public void SetAreaTransforms(Transform[] transforms)
        {
            areaTransforms = (Transform[])(transforms ?? throw new ArgumentNullException(nameof(transforms))).Clone();
            areaPositions = Array.Empty<Vector3>();
            areaSource = AreaSource.Transforms;
            _configurationChanged = true;
            _generation++;
        }

        internal bool UsesArea => areaSource != AreaSource.Composite;

        internal void ValidateAreaNodes()
        {
            if (areaSource == AreaSource.Positions)
            {
                var matrix = transform.localToWorldMatrix;
                for (int i = 0; i < areaPositions.Length; i++)
                {
                    OverlapShape3D.ValidateVector(areaPositions[i], "Area local node");
                    OverlapShape3D.ValidateVector(matrix.MultiplyPoint3x4(areaPositions[i]), "Area world node");
                }
            }
            else if (areaSource == AreaSource.Transforms)
            {
                for (int i = 0; i < areaTransforms.Length; i++)
                    if (areaTransforms[i])
                        OverlapShape3D.ValidateVector(areaTransforms[i].position, "Area world node");
            }
        }

        internal int InputShapeCount
        {
            get
            {
                switch (areaSource)
                {
                    case AreaSource.Composite:
                        return (composite ?? throw new InvalidOperationException("Composite cannot be null.")).Count;
                    case AreaSource.Positions:
                        return Math.Max(0, (areaPositions ?? throw new InvalidOperationException("Area positions cannot be null.")).Length - 1);
                    case AreaSource.Transforms:
                        return Math.Max(0, (areaTransforms ?? throw new InvalidOperationException("Area transforms cannot be null.")).Length - 1);
                    default:
                        throw new InvalidOperationException("Unknown Area node source.");
                }
            }
        }

        // Runtime 和 Inspector 共用输入转换；缺节点保留原相邻对索引，不跨段连接。
        internal bool TryGetWorldShape(int index, out OverlapShape2D shape)
        {
            if (index < 0 || index >= InputShapeCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (!UsesArea)
            {
                shape = composite.ToWorld(index, transform.localToWorldMatrix);
                return true;
            }
            shape = default;
            if (areaSource == AreaSource.Transforms && (!areaTransforms[index] || !areaTransforms[index + 1]))
                return false;
            var matrix = transform.localToWorldMatrix;
            var from = areaSource == AreaSource.Positions ? matrix.MultiplyPoint3x4(areaPositions[index]) : areaTransforms[index].position;
            var to = areaSource == AreaSource.Positions ? matrix.MultiplyPoint3x4(areaPositions[index + 1]) : areaTransforms[index + 1].position;
            OverlapShape3D.ValidateVector(from, "Area start");
            OverlapShape3D.ValidateVector(to, "Area end");
            var size = new Vector2(Mathf.Abs(to.x - from.x), Mathf.Abs(to.y - from.y));
            OverlapShape3D.ValidateVector(size, "Area bounds");
            if (size.x == 0 || size.y == 0)
                return false;
            shape = OverlapShape2D.Box(from * 0.5f + to * 0.5f, size);
            return true;
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
        public int MaxShapesPerCollider => maxShapesPerCollider;
        public int MaxVerticesPerShape => maxVerticesPerShape;
        public float GeometryTolerance => geometryTolerance;
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

        public void ConfigureGeometryWorkspace(int maxShapesPerCollider = 256, int maxVerticesPerShape = 4096,
            float geometryTolerance = 0.00001f)
        {
            OverlapQueryWorkspace2D.ValidateConfiguration(maxShapesPerCollider, maxVerticesPerShape, geometryTolerance);
            this.maxShapesPerCollider = maxShapesPerCollider;
            this.maxVerticesPerShape = maxVerticesPerShape;
            this.geometryTolerance = geometryTolerance;
            _workspace?.Dispose();
            _workspace = null;
            _configurationChanged = true;
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

        /// <summary>停止后续采样并释放联系人；若在 UnityEvent 内调用，释放/Exit 延后到当前监听者批次末尾。</summary>
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
                if (_serializedNotificationDepth != 0)
                    _clearObserversAfterNotification = true;
                else
                    ClearObservers();
            }
        }

        private void OnValidate()
        {
            _generation++;
            _configurationChanged = true;
        }

        private void Update() => Tick(samplingMode == OverlapSamplingMode.UnscaledUpdate ? OverlapSamplingMode.UnscaledUpdate :
            OverlapSamplingMode.Update, samplingMode == OverlapSamplingMode.UnscaledUpdate ? Time.unscaledDeltaTime : Time.deltaTime);
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
                var filter = Filter;
                var scene = gameObject.scene.GetPhysicsScene2D();
                Vector2 reference = transform.position;
                while (true)
                {
                    _tracker.Begin();
                    bool complete = true;
                    int rawCount = 0;
                    for (int i = 0; i < _worldShapeCount; i++)
                    {
                        var query = OverlapQuery2D.Query(scene, _worldShapes[i], filter, _buffer, _workspace);
                        rawCount = rawCount > int.MaxValue - query.RawCount ? int.MaxValue : rawCount + query.RawCount;
                        if (query.Status == OverlapQueryStatus.Invalidated)
                        {
                            LastQuery = new OverlapQueryResult2D(0, rawCount, OverlapQueryStatus.Invalidated,
                                query.InvalidationReason);
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
                                new OverlapContact<Collider2D>(collider, target, identity, collider.GetInstanceID(), collider.ClosestPoint(reference)), _buffer.Length);
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
                bool serializedFilterChanged = _configurationChanged && _hasCommittedFilter && !filter.Equals(_committedFilter);
                var reason = _filterChanged || serializedFilterChanged ? OverlapExitReason.FilterChanged :
                    _configurationChanged ? OverlapExitReason.Reconfigured : OverlapExitReason.Separated;
                _committedFilter = filter;
                _hasCommittedFilter = true;
                _filterChanged = _configurationChanged = false;
                _tracker.Commit();
                DispatchExits(_tracker.Exits, reason, generation, true);
                Dispatch(_tracker.Enters, enteredEvent, _enterObservers, generation);
                Dispatch(_tracker.Stays, stayedEvent, _stayObservers, generation);
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
                Filter.Validate();
                OverlapQueryWorkspace2D.ValidateConfiguration(maxShapesPerCollider, maxVerticesPerShape, geometryTolerance);
                int count = InputShapeCount;
                ValidateAreaNodes();
                if (_worldShapes.Length < count)
                {
                    _worldShapes = new OverlapShape2D[count];
                }
                bool needsWorkspace = false;
                _worldShapeCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (TryGetWorldShape(i, out var shape))
                    {
                        _worldShapes[_worldShapeCount++] = shape;
                        needsWorkspace |= shape.Kind == OverlapShapeKind2D.Sector;
                    }
                }
                if (!needsWorkspace || _workspace != null && (_workspace.MaxShapesPerCollider != maxShapesPerCollider ||
                    _workspace.MaxVerticesPerShape != maxVerticesPerShape || _workspace.GeometryTolerance != geometryTolerance))
                {
                    _workspace?.Dispose();
                    _workspace = null;
                }
                if (needsWorkspace && _workspace == null)
                {
                    _workspace = new OverlapQueryWorkspace2D(maxShapesPerCollider, maxVerticesPerShape, geometryTolerance);
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

        private void Dispatch(List<OverlapContact<Collider2D>> contacts, UnityEvent<OverlapContact<Collider2D>> serialized, Delegate[] observers, long generation)
        {
            for (int i = 0; i < contacts.Count && generation == _generation; i++)
            {
                var contact = contacts[i];
                _serializedNotificationDepth++;
                try
                {
                    serialized.Invoke(contact);
                }
                finally { EndSerializedNotification(); }
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
                _serializedNotificationDepth++;
                try
                {
                    exitedEvent.Invoke(contact, actual);
                }
                finally { EndSerializedNotification(); }
                for (int j = 0; j < observers.Length && generation == _generation; j++)
                {
                    ((Action<OverlapContact<Collider2D>, OverlapExitReason>)observers[j])(contact, actual);
                }
            }
        }

        private void EndSerializedNotification()
        {
            if (--_serializedNotificationDepth != 0)
                return;
            var reason = _deferredRelease;
            _deferredRelease = null;
            bool clear = _clearObserversAfterNotification;
            _clearObserversAfterNotification = false;
            try
            {
                if (reason.HasValue)
                    Release(reason.Value);
            }
            finally
            {
                if (clear)
                    ClearObservers();
            }
        }

        private void ClearObservers()
        {
            _entered = null;
            _stayed = null;
            _exited = null;
            _enterObservers = _stayObservers = _exitObservers = Array.Empty<Delegate>();
        }

        private void Release(OverlapExitReason reason)
        {
            _generation++;
            if (_serializedNotificationDepth != 0)
            {
                // UnityEvent cannot interrupt its internal invocation list. Keep its snapshot atomic before Exit.
                _deferredRelease ??= reason;
                return;
            }
            _workspace?.Dispose();
            _workspace = null;
            _elapsed = 0;
            _committedFilter = default;
            _hasCommittedFilter = false;
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
