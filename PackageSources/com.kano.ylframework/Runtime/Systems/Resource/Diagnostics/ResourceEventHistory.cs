using System.Diagnostics;
#if UNITY_EDITOR
using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
#endif

namespace YLFramework.Resource
{
    internal enum ResourceEventSource
    {
        Acquire, ScopeLoad, ScopeAcquire, Image, Batch, Instantiate, Rent, ScopeInstantiate, ScopeRent,
        Scene, ScopeScene, SceneActivate, DownloadSize, Download, CatalogCheck, CatalogApply
    }

    internal enum ResourceEventPhase
    {
        Request, Locate, Provide, Delivery, SceneLocate, SceneLoad, SceneUnload, SceneCleanup
    }
    internal enum ResourceEventRole
    {
        None, Caller, ScopeClose, Session
    }
    internal enum ResourceEventKind
    {
        Terminal, ClaimInvalidated, Work, Release
    }

    internal struct ResourceEventTrace
    {
#if UNITY_EDITOR
        internal long SessionId, Generation, Id, LoadId, WorkId, Started;
        internal int DiagnosticId;
        internal ResourceEventSource Source;
        internal ResourceEventPhase Phase;
        internal ResourceEventRole Role;
        internal ulong KeyFingerprint, OwnerFingerprint;
#else
        internal long Id => 0;
        internal long LoadId => 0;
#endif

        internal ResourceEventTrace WithRole(ResourceEventRole role)
        {
            var trace = this;
#if UNITY_EDITOR
            trace.Role = role;
#endif
            return trace;
        }

        internal ResourceEventTrace WithLoad(long loadId)
        {
            var trace = this;
#if UNITY_EDITOR
            trace.LoadId = loadId;
#endif
            return trace;
        }

        internal ResourceEventTrace WithKeys(string[] frozen)
        {
            var trace = this;
#if UNITY_EDITOR
            if (ResourceEventHistory.Accepts(this))
                trace.KeyFingerprint = ResourceEventHistory.FingerprintKeys(frozen);
#endif
            return trace;
        }

        internal ResourceEventTrace StartWork(ResourceEventPhase phase)
        {
#if UNITY_EDITOR
            if (ResourceEventHistory.Accepts(this))
            {
                var trace = this;
                trace.WorkId = ResourceEventHistory.NextId();
                trace.Started = Stopwatch.GetTimestamp();
                trace.Phase = phase;
                return trace;
            }
#endif
            return default;
        }

        [Conditional("UNITY_EDITOR")]
        internal void Record(ResourceEventKind kind, ResourceRequestStatus status, ResourceError? error = null, bool late = false)
        {
#if UNITY_EDITOR
            ResourceEventHistory.Record(this, kind, status, error, late);
#endif
        }
    }

    public sealed partial class ResourceSystem
    {
#if UNITY_EDITOR
        private long _eventSession;
#endif
        partial void StartEventSession()
        {
#if UNITY_EDITOR
            _eventSession = ResourceEventHistory.StartSession();
#endif
        }

        internal ResourceEventTrace BeginEvent(ResourceEventSource source, string key = null, string owner = null)
        {
#if UNITY_EDITOR
            return ResourceEventHistory.Begin(_eventSession, source, key, owner);
#else
            return default;
#endif
        }
    }

#if UNITY_EDITOR
    internal readonly struct ResourceEventRow
    {
        internal readonly long Sequence, SessionId, RequestId, LoadId, WorkId;
        internal readonly int DiagnosticId;
        internal readonly ResourceEventSource Source;
        internal readonly ResourceEventPhase Phase;
        internal readonly ResourceEventKind Kind;
        internal readonly ResourceRequestStatus Status;
        internal readonly ResourceError? Error;
        internal readonly bool Late;
        internal readonly double ElapsedMilliseconds;
        internal readonly ResourceEventRole Role;
        internal readonly ulong KeyFingerprint, OwnerFingerprint;

        internal ResourceEventRow(long sequence, ResourceEventTrace trace, ResourceEventKind kind,
            ResourceRequestStatus status, ResourceError? error, bool late)
        {
            Sequence = sequence;
            SessionId = trace.SessionId;
            RequestId = trace.Id;
            DiagnosticId = trace.DiagnosticId;
            LoadId = trace.LoadId;
            WorkId = trace.WorkId;
            Source = trace.Source;
            Phase = trace.Phase;
            Kind = kind;
            Role = trace.Role;
            Status = status;
            Error = error;
            Late = late;
            ElapsedMilliseconds = (Stopwatch.GetTimestamp() - trace.Started) * 1000d / Stopwatch.Frequency;
            KeyFingerprint = trace.KeyFingerprint;
            OwnerFingerprint = trace.OwnerFingerprint;
        }
    }

    [InitializeOnLoad]
    internal static class ResourceEventHistory
    {
        internal const string NextPlayFlag = "YLFramework.Resource.RecordNextPlay";
        private const int Capacity = 256;
        private static long _session, _generation, _nextId, _sequence;
        private static bool _armed, _hasSession;
        private static HMACSHA256 _hmac;
        private static ResourceEventRow[] _rows;
        private static int _first, _count;
        internal static bool IsRecording { get; private set; }
        internal static long Dropped { get; private set; }
        internal static int Count => _count;
        internal static long Revision { get; private set; }

        static ResourceEventHistory()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    SessionState.EraseBool(NextPlayFlag);
                    _hasSession = _armed = false;
                    SetRecording(false);
                    ResetSession();
                }
            };
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void EnterPlay()
        {
            _armed = SessionState.GetBool(NextPlayFlag, false);
            SessionState.EraseBool(NextPlayFlag);
            _hasSession = false;
            IsRecording = false;
            ResetSession();
        }

        internal static long StartSession()
        {
            _hasSession = true;
            ResetSession();
            _session++;
            IsRecording = _armed;
            return _session;
        }

        internal static void SetRecording(bool enabled)
        {
            _armed = enabled;
            if (enabled == IsRecording)
                return;
            Revision++;
            _generation++;
            IsRecording = enabled;
        }

        internal static void Clear()
        {
            Revision++;
            _generation++;
            _first = _count = 0;
            _sequence = Dropped = 0;
            _rows = null;
        }

        private static void ResetSession()
        {
            Clear();
            _hmac?.Dispose();
            _hmac = null;
        }

        internal static long NextId() => ++_nextId;

        internal static ResourceEventTrace Begin(long session, ResourceEventSource source, string key, string owner)
        {
            if (!IsRecording || !_hasSession || session != _session)
                return default;
            return new ResourceEventTrace
            {
                SessionId = session, Generation = _generation, Id = NextId(), Source = source,
                Started = Stopwatch.GetTimestamp(), KeyFingerprint = Fingerprint(key), OwnerFingerprint = Fingerprint(owner)
            };
        }

        internal static bool Accepts(ResourceEventTrace trace) =>
            IsRecording && trace.Id != 0 && trace.Generation == _generation && trace.SessionId == _session;

        private static ulong Fingerprint(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;
            _hmac ??= new HMACSHA256();
            var digest = _hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
            var fingerprint = BitConverter.ToUInt64(digest, 0);
            return fingerprint == 0 ? 1UL : fingerprint;
        }

        internal static ulong FingerprintKeys(string[] keys)
        {
            if (keys == null || keys.Length == 0)
                return 0;
            var joined = new StringBuilder();
            foreach (var key in keys)
                joined.Append(key?.Length ?? -1).Append(':').Append(key);
            return Fingerprint(joined.ToString());
        }

        internal static void Record(ResourceEventTrace trace, ResourceEventKind kind, ResourceRequestStatus status,
            ResourceError? error, bool late)
        {
            if (!Accepts(trace))
                return;
            Revision++;
            _rows ??= new ResourceEventRow[Capacity];
            _rows[(_first + _count) % Capacity] = new ResourceEventRow(++_sequence, trace, kind, status, error, late);
            if (_count < Capacity)
                _count++;
            else
            {
                _first = (_first + 1) % Capacity;
                Dropped++;
            }
        }

        internal static ResourceEventRow[] Read()
        {
            if (_count == 0)
                return Array.Empty<ResourceEventRow>();
            var copy = new ResourceEventRow[_count];
            int firstPart = Math.Min(_count, Capacity - _first);
            Array.Copy(_rows, _first, copy, 0, firstPart);
            Array.Copy(_rows, 0, copy, firstPart, _count - firstPart);
            return copy;
        }
    }
#endif
}
