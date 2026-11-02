using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private const int BundleConcurrency = 4;
        private readonly Dictionary<(string, string), BundleRecord> _bundleRecords = new();
        private readonly HashSet<BundleLease> _publicBundleLeases = new();
        private readonly Queue<BundleRecord> _bundleQueue = new();
        private int _runningBundles;
        private bool _isPumpingBundles;
        private bool _shouldPumpAgain;
        private int _nativeBundleLoads;
        private int _nativeBundleUnloads;

        /// <summary>一个 Bundle 图节点从依赖等待到生产和回收的内部状态。</summary>
        internal enum BundleState
        {
            /// <summary>等待依赖节点完成。</summary>
            Waiting,
            /// <summary>依赖已就绪，等待异步并发槽。</summary>
            Queued,
            /// <summary>正在准备输入或执行原生加载。</summary>
            Loading,
            /// <summary>原生 Bundle 已通过交付检查。</summary>
            Ready,
            /// <summary>依赖、内容或原生生产失败。</summary>
            Failed,
            /// <summary>节点已回收，依赖入边已归还。</summary>
            Released
        }
        /// <summary>分离图根请求的终态准备和完成通知。</summary>
        internal sealed class BundleWaiter
        {
            internal Action Prepare;
            internal Action Notify;
        }
        /// <summary>单个快照 Bundle 的共享节点，持有输入、原生操作、图根及依赖入边账本。</summary>
        internal sealed class BundleRecord
        {
            internal readonly ResourceSystem Owner;
            internal readonly ResourceLocalSnapshot Snapshot;
            internal readonly ResourceCatalogBundle Descriptor;
            internal readonly List<BundleRecord> Dependencies = new();
            internal BundleState State;
            internal AssetBundle Bundle;
            internal AssetBundleCreateRequest Native;
            internal ResourceBundleInput Input;
            internal ResourceBundleEnvelopeHeader Header;
            internal ResourceContentReservation Reservation;
            internal ContentWork InputWork;
            internal Exception CleanupError;
            internal bool IsCollecting;
            internal bool OccupiesSlot;
            internal bool IsInputRegistered;
            internal bool IsAdmissionInFlight;
            internal ResourceException Error;
            // 总引用由图根租约和依赖者入边组成；RootPins 是其中的图根部分。
            internal int References;
            internal int RootPins;
            internal Action<AsyncOperation> NativeCompletion;
            internal readonly List<BundleWaiter> Waiters = new();
            internal bool IsDone => State == BundleState.Ready || State == BundleState.Failed;
            /// <summary>为冻结快照中的 Bundle 建立待依赖就绪的节点。</summary>
            internal BundleRecord(ResourceSystem owner, ResourceLocalSnapshot snapshot, ResourceCatalogBundle descriptor)
            {
                Owner = owner;
                Snapshot = snapshot;
                Descriptor = descriptor;
            }
        }

        /// <summary>取得明确 Catalog BundleId 的低层图租约，不加载 Scene 或创建实例。</summary>
        /// <param name="projectId">已挂载快照的项目标识。</param>
        /// <param name="bundleId">快照中的逻辑 Bundle 标识。</param>
        /// <returns>调用方负责归还的图根租约；其依赖闭包由同一系统保活。</returns>
        /// <remarks>同步入口不等待在途图；原生 Bundle 只供租约有效期间借读，不得自行 Unload。</remarks>
        public BundleLease AcquireBundle(string projectId, string bundleId)
        {
            using var request = AcquireBundleRequest(projectId, bundleId, true, true);
            return request.TakeResult();
        }
        /// <summary>异步取得一份独立 Bundle 图根租约，共享图节点和原生生产。</summary>
        /// <param name="projectId">已挂载快照的项目标识。</param>
        /// <param name="bundleId">快照中的逻辑 Bundle 标识。</param>
        /// <returns>成功后可单次领取图根租约的请求；取消只归还本请求的图根责任。</returns>
        public ResourceRequest<BundleLease> AcquireBundleAsync(string projectId, string bundleId) => AcquireBundleRequest(projectId, bundleId, false, true);

        /// <summary>验证依赖闭包并取得独立图根，先登记全部入边再驱动同步或异步生产。</summary>
        internal ResourceRequest<BundleLease> AcquireBundleRequest(string projectId, string bundleId, bool synchronous, bool publicLease,
            BundleLeaseOrigin origin = default)
        {
            CheckThread();
            var request = new ResourceRequest<BundleLease>(value => value.Dispose());
            if (!IsAvailable)
            {
                return Fail(request, ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            if (IsProjectBlocked(projectId))
            {
                return Fail(request, ResourceError.Busy, "The project is inside its activation barrier.");
            }

            if (projectId == null || !_catalogs.TryGetValue(projectId, out var catalog) || catalog.Snapshot == null ||
                bundleId == null || !catalog.Snapshot.Bundles.ContainsKey(bundleId))
            {
                return Fail(request, ResourceError.AddressNotFound, "Unknown mounted project/Bundle identity.");
            }

            var snapshot = catalog.Snapshot;
            try
            {
                RequireHealthyBundle(snapshot, bundleId);
            }
            catch (ResourceException error)
            {
                request.SetFailure(ResourceRequestStatus.Failed, error);
                request.Notify();
                return request;
            }
            var closure = new List<ResourceCatalogBundle>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(bundleId);
            while (pending.Count != 0)
            {
                var id = pending.Pop();
                if (!visited.Add(id))
                {
                    continue;
                }

                var descriptor = snapshot.Bundles[id];
                closure.Add(descriptor);
                foreach (var dependency in descriptor.Dependencies)
                {
                    pending.Push(dependency);
                }
            }
            if (synchronous && closure.Any(x => _bundleRecords.TryGetValue((snapshot.Document.CatalogDigest, x.LogicalId), out var existing) && !existing.IsDone))
            {
                return Fail(request, ResourceError.InProgress, "The Bundle graph is already in flight; use AcquireBundleAsync.");
            }

            // 先建立全部节点和依赖入边，再运行任何可能即时失败/通知的 IO。
            var added = new List<BundleRecord>();
            try
            {
                foreach (var descriptor in closure)
                {
                    var key = (snapshot.Document.CatalogDigest, descriptor.LogicalId);
                    if (_bundleRecords.ContainsKey(key))
                    {
                        continue;
                    }

                    added.Add(new BundleRecord(this, snapshot, descriptor) { Header = ResourceContentValidation.Inspect(snapshot, descriptor) });
                }
                var tickets = _contentBudget.Reserve(added.Count(x => x.Header != null));
                var index = 0;
                foreach (var value in added)
                {
                    if (value.Header != null)
                    {
                        value.Reservation = tickets[index++];
                    }

                    _bundleRecords.Add((snapshot.Document.CatalogDigest, value.Descriptor.LogicalId), value);
                }
            }
            catch (Exception error)
            {
                foreach (var value in added)
                {
                    var key = (snapshot.Document.CatalogDigest, value.Descriptor.LogicalId);
                    if (_bundleRecords.TryGetValue(key, out var current) && ReferenceEquals(current, value))
                    {
                        _bundleRecords.Remove(key);
                    }

                    value.Reservation?.Dispose();
                }
                RetainContentFailureOwners(error);
                request.SetFailure(ResourceRequestStatus.Failed, ResourceContentValidation.Failure(error, "The Bundle closure could not be admitted."));
                request.Notify();
                return request;
            }
            foreach (var value in added)
            {
                foreach (var dependency in value.Descriptor.Dependencies)
                {
                    var child = _bundleRecords[(snapshot.Document.CatalogDigest, dependency)];
                    child.References++;
                    value.Dependencies.Add(child);
                }
            }

            var record = _bundleRecords[(snapshot.Document.CatalogDigest, bundleId)];
            var lease = new BundleLease(record, publicLease, origin);
            if (publicLease)
            {
                _publicBundleLeases.Add(lease);
            }

            Track(request);
            var waiter = new BundleWaiter();
            waiter.Prepare = () =>
            {
                if (request.IsDone)
                {
                    return;
                }

                ResourceException failure = record.Error;
                if (record.State == BundleState.Ready && IsSessionActive)
                {
                    try
                    {
                        RequireHealthyBundle(snapshot, bundleId);
                    }
                    catch (ResourceException error)
                    {
                        failure = error;
                    }
                }

                if (record.State == BundleState.Ready && IsSessionActive && failure == null)
                {
                    request.SetSuccess(lease);
                }
                else
                {
                    request.SetFailure(IsSessionActive ? ResourceRequestStatus.Failed : ResourceRequestStatus.Invalidated,
                        failure ?? new ResourceException(ResourceError.SessionUnavailable, "The Bundle producing session ended."));
                    lease.Dispose();
                }
            };
            waiter.Notify = request.Notify;
            request.Attach(() =>
            {
                record.Waiters.Remove(waiter);
                lease.Dispose();
            }, () => record.Native?.progress ?? (record.IsDone ? 1 : 0));
            if (record.IsDone)
            {
                waiter.Prepare();
                waiter.Notify();
            }
            else
            {
                record.Waiters.Add(waiter);
                if (synchronous)
                {
                    DriveSynchronousBundles(added);
                }
                else
                {
                    PumpBundles();
                }
            }
            return request;
        }

        /// <summary>同步驱动本次新建 Bundle 节点并传播依赖失败。</summary>
        private void DriveSynchronousBundles(List<BundleRecord> added)
        {
            // 只驱动本次冷图，不等待/接管异步队列；观察者重入也复用同一 records 和 native 权威。
            var pending = new HashSet<BundleRecord>(added);
            while (pending.Count != 0)
            {
                var advanced = false;
                foreach (var record in pending.ToArray())
                {
                    if (record.State != BundleState.Released && !record.Dependencies.All(x => x.IsDone))
                    {
                        continue;
                    }

                    pending.Remove(record);
                    advanced = true;
                    if (record.State == BundleState.Released)
                    {
                        continue;
                    }

                    var failed = record.Dependencies.FirstOrDefault(x => x.State == BundleState.Failed);
                    if (failed != null)
                    {
                        FinishBundle(record, null, DependencyFailure(record, failed));
                    }
                    else
                    {
                        StartBundle(record, true);
                    }
                }
                if (!advanced)
                {
                    throw new InvalidOperationException("A validated cold Bundle DAG could not make synchronous progress.");
                }
            }
            CheckDrain();
        }

        /// <summary>将依赖节点失败包装为当前节点的资源错误并保留原始原因。</summary>
        private static ResourceException DependencyFailure(BundleRecord record, BundleRecord failed) => new(failed.Error?.Kind ?? ResourceError.LoadFailed,
            $"Bundle '{record.Descriptor.LogicalId}' dependency '{failed.Descriptor.LogicalId}' failed.", failed.Error);

        /// <summary>传播依赖失败并按并发上限启动就绪节点，重入只请求再次推进。</summary>
        private void PumpBundles()
        {
            if (_isPumpingBundles)
            {
                _shouldPumpAgain = true;
                return;
            }
            _isPumpingBundles = true;
            try
            {
                do
                {
                    _shouldPumpAgain = false;
                    foreach (var record in _bundleRecords.Values.ToArray())
                    {
                        if (record.State != BundleState.Waiting)
                        {
                            continue;
                        }

                        var failed = record.Dependencies.FirstOrDefault(x => x.State == BundleState.Failed);
                        if (failed != null)
                        {
                            FinishBundle(record, null, DependencyFailure(record, failed));
                            _shouldPumpAgain = true;
                        }
                        else if (record.Dependencies.All(x => x.State == BundleState.Ready))
                        {
                            record.State = BundleState.Queued;
                            _bundleQueue.Enqueue(record);
                        }
                    }
                    while (_bundleQueue.Count != 0 && _runningBundles < BundleConcurrency)
                    {
                        var record = _bundleQueue.Dequeue();
                        if (record.State != BundleState.Queued)
                        {
                            continue;
                        }

                        StartBundle(record, false);
                    }
                } while (_shouldPumpAgain);
            }
            finally
            {
                _isPumpingBundles = false;
            }
            CheckDrain();
        }

        /// <summary>完成内容准入及密钥捕获，接管输入准备并启动后续原生生产。</summary>
        private void StartBundle(BundleRecord record, bool synchronous)
        {
            if (!IsAvailable)
            {
                FinishBundle(record, null, new ResourceException(ResourceError.SessionUnavailable, "The Bundle producing session ended."));
                _shouldPumpAgain = true;
                return;
            }
            byte[] key = null;
            record.IsAdmissionInFlight = true;
            record.State = BundleState.Loading;
            try
            {
                RequireHealthyBundle(record.Snapshot, record.Descriptor.LogicalId);
                if (record.Header != null)
                {
                    key = ResourceContentValidation.CopyKey(_configuration.Decryption, record.Snapshot.Document.ProjectId, record.Header.KeyId);
                }

                if (!IsAvailable || record.References == 0)
                {
                    throw new OperationCanceledException("The Bundle input no longer has an owner.");
                }

                if (synchronous)
                {
                    var ownedKey = key;
                    key = null;
                    AttachBundleInput(record, OpenBundleInput(record, ownedKey, default));
                    StartNativeBundle(record, true);
                    _shouldPumpAgain = true;
                }
                else
                {
                    record.OccupiesSlot = true;
                    _runningBundles++;
                    var ownedKey = key;
                    record.InputWork = StartContentWork(token => OpenBundleInput(record, ownedKey, token), (input, error) =>
                    {
                        record.InputWork = null;
                        try
                        {
                            if (input != null)
                            {
                                AttachBundleInput(record, input);
                            }

                            if (error == null && (!IsAvailable || record.References == 0))
                            {
                                error = new OperationCanceledException("The Bundle input no longer has an owner.");
                            }

                            if (error is OperationCanceledException && record.References > 0 && IsAvailable && input == null)
                            {
                                // 旧私有IO已实际结束；后来显式加入的owner不继承它的取消终态。
                                record.Reservation?.PrepareRetry();
                                if (record.OccupiesSlot)
                                {
                                    record.OccupiesSlot = false;
                                    _runningBundles--;
                                }
                                record.State = BundleState.Waiting;
                                PumpBundles();
                                return;
                            }
                            if (error != null)
                            {
                                RetainContentFailureOwners(error);
                                FinishBundle(record, null, error);
                            }
                            else
                            {
                                StartNativeBundle(record, false);
                            }
                        }
                        catch (Exception cause)
                        {
                            FinishBundle(record, null, cause);
                        }
                        PumpBundles();
                    }, snapshots: new[] { record.Snapshot });
                    key = null;
                }
            }
            catch (Exception error)
            {
                FinishBundle(record, null, error);
                _shouldPumpAgain = true;
            }
            finally
            {
                if (key != null)
                {
                    Array.Clear(key, 0, key.Length);
                }

                record.IsAdmissionInFlight = false;
                CollectBundle(record);
            }
        }

        /// <summary>核验并打开 Bundle 输入，转交文件所有权且在结束时擦除密钥副本。</summary>
        private static ResourceBundleInput OpenBundleInput(BundleRecord record, byte[] key, CancellationToken cancellation)
        {
            FileStream file = null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                file = record.Snapshot.OpenVerified(record.Descriptor, cancellation);
                record.Reservation?.Allocate();
                var input = ResourceBundleInput.Open(file, ResourceContentValidation.Context(record.Snapshot, record.Descriptor), key, record.Reservation, cancellation);
                file = null;
                return input;
            }
            catch (Exception error)
            {
                try
                {
                    file?.Dispose();
                }
                catch (Exception cleanup)
                {
                    throw new ResourceContentCleanupException(file, new AggregateException(error, cleanup));
                }
                throw;
            }
            finally
            {
                if (key != null)
                {
                    Array.Clear(key, 0, key.Length);
                }
            }
        }

        /// <summary>将输入交给原图节点保管，并将后台认证故障排入主线程处理队列。</summary>
        private void AttachBundleInput(BundleRecord record, ResourceBundleInput input)
        {
            record.Input = input;
            record.IsInputRegistered = true;
            _liveContentInputs++;
            RegisterContentCleanup();
            input.SetFaultNotification(error => _contentFaults.Enqueue((record, error)));
        }

        /// <summary>拒绝外部占用的原生身份，并从已接管输入启动 Unity Bundle 加载。</summary>
        private void StartNativeBundle(BundleRecord record, bool synchronous)
        {
            RequireHealthyBundle(record.Snapshot, record.Descriptor.LogicalId);
            if (AssetBundle.GetAllLoadedAssetBundles().Any(x => x && x.name == record.Descriptor.NativeName))
            {
                throw new ResourceException(ResourceError.Busy, "The native Bundle identity is owned outside this graph: " + record.Descriptor.NativeName);
            }

            var path = Path.Combine(record.Snapshot.DirectoryPath, record.Descriptor.RelativeFile);
            if (synchronous)
            {
                _nativeBundleLoads++;
                var bundle = record.Input.IsEncrypted ? AssetBundle.LoadFromStream(record.Input.Stream, 0, ResourceBundleEnvelope.NativeReadBufferSize) : AssetBundle.LoadFromFile(path);
                FinishBundle(record, bundle, null);
            }
            else
            {
                record.Native = record.Input.IsEncrypted ? AssetBundle.LoadFromStreamAsync(record.Input.Stream, 0, ResourceBundleEnvelope.NativeReadBufferSize) : AssetBundle.LoadFromFileAsync(path);
                if (record.Native == null)
                {
                    throw new InvalidOperationException("Unity did not return an AssetBundleCreateRequest.");
                }

                _nativeBundleLoads++;
                record.NativeCompletion = _ => CompleteNativeBundle(record);
                record.Native.completed += record.NativeCompletion;
                if (record.Native.isDone)
                {
                    CompleteNativeBundle(record);
                }
            }
        }

        /// <summary>移除原生完成订阅，提取结果并结算图节点终态。</summary>
        private void CompleteNativeBundle(BundleRecord record)
        {
            if (record.Native == null)
            {
                return;
            }

            record.Native.completed -= record.NativeCompletion;
            record.NativeCompletion = null;
            AssetBundle bundle = null;
            Exception error = null;
            try
            {
                bundle = record.Native.assetBundle;
            }
            catch (Exception cause)
            {
                error = cause;
            }
            record.Native = null;
            FinishBundle(record, bundle, error);
            PumpBundles();
        }

        /// <summary>核对内容、原生身份和会话，准备全部图根消费者后再回收与通知。</summary>
        private void FinishBundle(BundleRecord record, AssetBundle bundle, Exception cause)
        {
            if (record.State == BundleState.Released && !bundle)
            {
                return;
            }

            record.Bundle = bundle;
            if (record.OccupiesSlot)
            {
                record.OccupiesSlot = false;
                _runningBundles--;
            }
            if (cause == null)
            {
                try
                {
                    RequireHealthyBundle(record.Snapshot, record.Descriptor.LogicalId);
                }
                catch (Exception error)
                {
                    cause = error;
                }
            }

            if (cause == null && (!bundle || bundle.name != record.Descriptor.NativeName || bundle.isStreamedSceneAssetBundle != record.Descriptor.IsScene))
            {
                cause = new InvalidDataException("Unity Bundle result/native identity mismatch: " + record.Descriptor.LogicalId);
            }

            if (cause == null && !IsSessionActive)
            {
                cause = new ResourceException(ResourceError.SessionUnavailable, "The Bundle producing session ended.");
            }

            record.Error = cause == null ? null : ResourceContentValidation.Failure(cause,
                $"Bundle '{record.Descriptor.LogicalId}' failed in '{record.Snapshot.Document.ProjectId}'/{record.Snapshot.Document.CatalogDigest}.");
            record.State = record.Error == null ? BundleState.Ready : BundleState.Failed;
            var waiters = record.Waiters.ToArray();
            record.Waiters.Clear();
            foreach (var waiter in waiters)
            {
                waiter.Prepare();
            }

            CollectBundle(record);
            foreach (var waiter in waiters)
            {
                waiter.Notify();
            }
        }

        /// <summary>归还一个公共或内部图根引用，尝试回收无主闭包并检查关闭进度。</summary>
        internal void ReleaseBundle(BundleLease lease, BundleRecord record)
        {
            if (lease.IsPublic)
            {
                _publicBundleLeases.Remove(lease);
            }

            record.RootPins--;
            record.References--;
            CollectBundle(record);
            CheckDrain();
        }

        /// <summary>回收无引用且无在途操作的节点，清理成功后才归还依赖入边。</summary>
        private void CollectBundle(BundleRecord first)
        {
            var collect = new Stack<BundleRecord>();
            collect.Push(first);
            while (collect.Count != 0)
            {
                var record = collect.Pop();
                if (record.References != 0 || record.Native != null || record.State == BundleState.Released || record.IsCollecting || record.CleanupError != null || record.IsAdmissionInFlight)
                {
                    continue;
                }

                if (record.InputWork != null)
                {
                    record.InputWork.Cancel();
                    continue;
                }
                // 先卸载依赖者，再归还依赖入边；不能用 Unload(false) 留下无主对象。
                record.IsCollecting = true;
                try
                {
                    if (record.Bundle)
                    {
                        record.Bundle.Unload(true);
                        _nativeBundleUnloads++;
                    }
                    record.Bundle = null;
                    record.Input?.Dispose();
                    record.Input = null;
                    record.Reservation?.Dispose();
                    record.Reservation = null;
                    if (record.IsInputRegistered)
                    {
                        record.IsInputRegistered = false;
                        _liveContentInputs--;
                    }
                }
                catch (Exception error)
                {
                    record.CleanupError = error;
                    RetainContentFailureOwners(error);
                    Debug.LogException(new ResourceException(ResourceError.LoadFailed, "Bundle cleanup failed; the original input, budget and dependency ownership remain retained.", error));
                    continue;
                }
                finally
                {
                    record.IsCollecting = false;
                }
                var key = (record.Snapshot.Document.CatalogDigest, record.Descriptor.LogicalId);
                if (_bundleRecords.TryGetValue(key, out var current) && ReferenceEquals(current, record))
                {
                    _bundleRecords.Remove(key);
                }

                record.State = BundleState.Released;
                foreach (var dependency in record.Dependencies)
                {
                    dependency.References--;
                    collect.Push(dependency);
                }
                record.Dependencies.Clear();
            }
        }

        /// <summary>要求冻结快照的平台及 Unity 兼容版本与当前运行环境完全匹配。</summary>
        private static void ValidateSnapshotPlatform(ResourceLocalSnapshot snapshot)
        {
#if UNITY_EDITOR
            var platform = UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString();
#else
            var platform = Application.platform switch
            {
                RuntimePlatform.WindowsPlayer => IntPtr.Size == 8 ? "StandaloneWindows64" : "StandaloneWindows",
                RuntimePlatform.LinuxPlayer => "StandaloneLinux64",
                RuntimePlatform.OSXPlayer => "StandaloneOSX",
                RuntimePlatform.Android => "Android",
                RuntimePlatform.IPhonePlayer => "iOS",
                _ => null
            };
#endif
            if (platform == null || snapshot.Document.Platform != platform || snapshot.Document.UnityCompatibility != Application.unityVersion)
            {
                throw new ResourceException(ResourceError.Unsupported, "The explicit local snapshot does not match this Unity runtime/platform.");
            }
        }
    }
}
