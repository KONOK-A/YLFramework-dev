using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Resource
{
    public sealed partial class ResourceSystem
    {
        private readonly List<SceneLoad> _sceneLoads = new();
        private readonly HashSet<SceneRecord> _sceneRecords = new();
        private static readonly HashSet<ResourceSystem> _sceneCleanup = new();
        private long _nextSceneSequence;
        private bool _isPumpingScenes;

        /// <summary>一次未结算的场景加载意图，持有 Bundle 准备、激活门控和身份观察窗口。</summary>
        private sealed class SceneLoad
        {
            internal SceneRequest Request;
            internal ResourceCatalog Catalog;
            internal ResourceAddress Address;
            internal string Path;
            internal string BundleId;
            internal IResourceSceneSource Source;
            internal ResourceRequest<BundleLease> Preparing;
            internal BundleLease Retention;
            internal bool HasStarted;
            internal float Deadline;
            internal float ActivationTimeout;
            internal int CompletedFrame = -1;
            internal readonly HashSet<int> Before = new();
            internal readonly Dictionary<int, Scene> Observed = new();
        }

        /// <summary>按冻结目录加载真实 Scene。框架内部逐次关联 native 操作，不合并重复 Additive 意图。</summary>
        /// <param name="address">目录中的场景地址，不接受子资产键。</param>
        /// <param name="mode">本次 Single 或 Additive 加载意图。</param>
        /// <param name="activateOnLoad">是否自动放行激活；为 false 时可显式 Activate，但仍受门控期限约束。</param>
        /// <param name="activationTimeoutSeconds">从 native 开始计时的有限门控期限，范围 (0, 300]；到期失败并放行激活，不承诺无限后台预加载。</param>
        /// <returns>可单次领取调用方场景租约的请求，真实场景由独立 SceneRecord 管理。</returns>
        public SceneRequest LoadSceneAsync(ResourceAddress address, LoadSceneMode mode = LoadSceneMode.Single,
            bool activateOnLoad = true, float activationTimeoutSeconds = 30)
        {
            CheckThread();
            var request = new SceneRequest(this, mode, ++_nextSceneSequence, activateOnLoad);
            if (!IsAvailable)
            {
                Fail(request, ResourceError.SessionUnavailable, "ResourceSystem is not available.", address);
                return request;
            }
            if (IsProjectBlocked(address.ProjectId))
            {
                Fail(request, ResourceError.Busy, "The project is inside its activation barrier.", address);
                return request;
            }
            if (mode != LoadSceneMode.Single && mode != LoadSceneMode.Additive ||
                float.IsNaN(activationTimeoutSeconds) || activationTimeoutSeconds <= 0 || activationTimeoutSeconds > 300)
            {
                Fail(request, ResourceError.InvalidConfiguration, "Scene mode and a finite activation deadline are required.", address);
                return request;
            }
            var load = new SceneLoad { Request = request, Address = address, ActivationTimeout = activationTimeoutSeconds };
            try
            {
                if (!address.IsValid || address.SubAsset != null || !_catalogs.TryGetValue(address.ProjectId, out load.Catalog))
                {
                    throw new ResourceException(ResourceError.AddressNotFound, "Unknown Scene project/address.");
                }

                if (load.Catalog.Snapshot != null)
                {
                    var descriptor = load.Catalog.Snapshot.Document.Assets.SingleOrDefault(x => x.Kind == "Scene" &&
                        (x.Address == address.Path || x.Aliases.Contains(address.Path)));
                    if (descriptor == null)
                    {
                        throw new ResourceException(ResourceError.AddressNotFound, "No Scene at this Catalog address.");
                    }

                    load.Path = descriptor.LoadName;
                    load.BundleId = descriptor.BundleId;
                    RequireHealthyBundle(load.Catalog.Snapshot, load.BundleId);
                }
                else
                {
                    var entry = load.Catalog.Resolve(address);
                    if (_sources[entry.Location.Source] is not IResourceSceneSource source)
                    {
                        throw new ResourceException(ResourceError.Unsupported, "This source does not support Scene loading.");
                    }

                    load.Source = source;
                    load.Path = source.ResolveScenePath(entry);
                }
            }
            catch (Exception error)
            {
                Fail(request, error is ResourceException failure ? failure.Kind : ResourceError.AddressNotFound, "Cannot resolve Scene: " + address, address, cause: error);
                return request;
            }
            Track(request);
            request.Attach(() => CancelSceneLoad(load), () => request.Native?.progress ?? (load.Preparing?.Progress ?? 0) * 0.1f);
            _sceneLoads.Add(load);
            RegisterSceneCleanup();
            PumpScenes();
            return request;
        }

        /// <summary>只读身份快照，包含已释放 caller Lease 的场景、取消 Single 的真实副作用和保守保活的歧义候选。</summary>
        /// <returns>当前登记的独立场景记录列表，记录本身会继续反映卸载状态。</returns>
        public IReadOnlyList<SceneRecord> GetSceneRecords()
        {
            CheckThread();
            return _sceneRecords.ToArray();
        }

        /// <summary>为本系统登记场景事件及统一清理驱动，保持原责任所有者可继续收尾。</summary>
        private void RegisterSceneCleanup()
        {
            if (_sceneCleanup.Add(this))
            {
                SceneManager.sceneLoaded += OnResourceSceneLoaded;
                SceneManager.sceneUnloaded += OnResourceSceneUnloaded;
                InstallInstanceCleanup();
            }
        }

        /// <summary>仅在场景加载和记录全部清空后解除事件订阅并检查系统排空。</summary>
        private void UnregisterSceneCleanup()
        {
            if (_sceneLoads.Count != 0 || _sceneRecords.Count != 0)
            {
                return;
            }

            _sceneCleanup.Remove(this);
            SceneManager.sceneLoaded -= OnResourceSceneLoaded;
            SceneManager.sceneUnloaded -= OnResourceSceneUnloaded;
            CheckDrain();
        }

        /// <summary>串行推进框架内部场景加载，先准备依赖图再启动原生操作。</summary>
        private void PumpScenes()
        {
            if (_isPumpingScenes)
            {
                return;
            }

            _isPumpingScenes = true;
            try
            {
                while (_sceneLoads.Count != 0)
                {
                    var load = _sceneLoads[0];
                    if (load.HasStarted)
                    {
                        break;
                    }

                    if (load.Request.IsDone || !IsSessionActive)
                    {
                        RemoveSceneLoad(load);
                        continue;
                    }
                    load.HasStarted = true;
                    if (load.Source != null)
                    {
                        StartNativeScene(load);
                    }
                    else
                    {
                        load.Preparing = AcquireBundleRequest(load.Address.ProjectId, load.BundleId, false, false,
                            new BundleLeaseOrigin(load.Address, sceneSequence: load.Request.LoadSequence));
                        load.Preparing.Completed += result =>
                        {
                            try
                            {
                                if (load.Request.IsDone)
                                {
                                    return;
                                }

                                load.Retention = result.TakeResult();
                                var paths = load.Retention.Bundle.GetAllScenePaths().Where(x => string.Equals(x, load.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
                                if (paths.Length != 1)
                                {
                                    throw new ResourceException(ResourceError.LoadFailed, "Catalog Scene has no unique native Bundle path.");
                                }

                                load.Path = paths[0];
                                StartNativeScene(load);
                            }
                            catch (Exception error)
                            {
                                FailSceneLoad(load, error);
                            }
                            finally
                            {
                                result.Dispose();
                                load.Preparing = null;
                                if (!load.Request.HasNativeStarted)
                                {
                                    RemoveSceneLoad(load);
                                    PumpScenes();
                                }
                            }
                        };
                    }
                    if (_sceneLoads.Contains(load))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _isPumpingScenes = false;
            }
            UnregisterSceneCleanup();
        }

        /// <summary>捕获加载前身份集合，启动真实场景加载并登记激活期限及完成时点。</summary>
        private void StartNativeScene(SceneLoad load)
        {
            try
            {
                if (load.Retention != null)
                {
                    RequireHealthyBundle(load.Retention);
                }

                if (load.Request.IsDone || !IsSessionActive)
                {
                    RemoveSceneLoad(load);
                    return;
                }
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    load.Before.Add(SceneManager.GetSceneAt(i).handle);
                }
                // 准备 Bundle 期间已完成的外部 Scene 属于 Before，不能残留为本次 native 的新身份候选。
                load.Observed.Clear();
                var parameters = new LoadSceneParameters(load.Request.Mode);
                var native = load.Source == null ? SceneManager.LoadSceneAsync(load.Path, parameters) : load.Source.LoadSceneAsync(load.Path, parameters);
                if (native == null)
                {
                    throw new InvalidOperationException("Unity did not return a Scene AsyncOperation.");
                }

                load.Request.Native = native;
                load.Request.HasNativeStarted = true;
                load.Deadline = Time.realtimeSinceStartup + load.ActivationTimeout;
                native.allowSceneActivation = load.Request.IsActivationRequested;
                native.completed += _ =>
                {
                    load.CompletedFrame = Time.frameCount;
                    load.Request.IsNativeCompleted = true;
                };
            }
            catch (Exception error)
            {
                FailSceneLoad(load, error);
                RemoveSceneLoad(load);
            }
        }

        /// <summary>取消未启动的准备；原生已开始时只放行激活，保留迟到结果结算责任。</summary>
        private void CancelSceneLoad(SceneLoad load)
        {
            if (load.Request.HasNativeStarted)
            {
                load.Request.IsActivationRequested = true;
                if (load.Request.Native != null)
                {
                    load.Request.Native.allowSceneActivation = true;
                }

                return; // 不撤销 native、不释放 pending Scene 图。
            }
            load.Preparing?.Dispose();
            RemoveSceneLoad(load);
            PumpScenes();
        }

        /// <summary>为仍等待的场景请求登记失败或失效终态并通知，不改写既有取消结果。</summary>
        private void FailSceneLoad(SceneLoad load, Exception error)
        {
            if (load.Request.IsDone)
            {
                return;
            }

            load.Request.SetFailure(IsSessionActive ? ResourceRequestStatus.Failed : ResourceRequestStatus.Invalidated,
                error as ResourceException ?? new ResourceException(ResourceError.LoadFailed, "Scene load failed: " + load.Address, error, load.Address));
            load.Request.Notify();
        }

        /// <summary>移除本次加载意图，归还其准备阶段仍持有的图根责任。</summary>
        private void RemoveSceneLoad(SceneLoad load)
        {
            _sceneLoads.Remove(load);
            load.Retention?.Dispose();
            load.Retention = null;
            UnregisterSceneCleanup();
        }

        /// <summary>记录当前加载窗口中同路径且不属于加载前集合的候选场景。</summary>
        private void OnResourceSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_sceneLoads.Count == 0)
            {
                return;
            }

            var load = _sceneLoads[0];
            if (!load.HasStarted || load.Before.Contains(scene.handle) || !string.Equals(scene.path, load.Path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            load.Observed[scene.handle] = scene;
        }

        /// <summary>标记匹配记录的卸载帧，保活责任留待事件结算后清理。</summary>
        private void OnResourceSceneUnloaded(Scene scene)
        {
            foreach (var record in _sceneRecords)
            {
                if (record.Handle == scene.handle && record.UnloadedFrame < 0)
                {
                    record.UnloadedFrame = Time.frameCount;
                }
            }
        }

        /// <summary>结算原生加载观察窗口，为真实候选建立记录并按唯一性决定租约交付。</summary>
        private void CompleteSceneLoad(SceneLoad load)
        {
            // completed 后留一帧事件结算窗，不能取 GetSceneByName/Path 的第一项。
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!load.Before.Contains(scene.handle) && string.Equals(scene.path, load.Path, StringComparison.OrdinalIgnoreCase))
                {
                    load.Observed[scene.handle] = scene;
                }
            }
            var candidates = load.Observed.Values.ToArray();
            var ambiguous = candidates.Length > 1;
            var results = new List<SceneRecord>();
            foreach (var scene in candidates)
            {
                BundleLease retention = null;
                if (load.Retention != null)
                {
                    retention = new BundleLease(_bundleRecords[(load.Catalog.CatalogDigest, load.BundleId)], false);
                }

                var record = new SceneRecord(this, scene, load.Catalog, load.Request, load.Address, scene.path,
                    ambiguous, retention);
                _sceneRecords.Add(record);
                results.Add(record);
            }
            load.Request.Scenes = results.AsReadOnly();
            if (ambiguous)
            {
                FailSceneLoad(load, new ResourceException(ResourceError.SceneIdentityAmbiguous,
                "Concurrent external same-path Scene loads cannot be attributed uniquely; candidates remain pinned until real unload.", address: load.Address));
            }
            else if (results.Count != 1 || !results[0].IsLoaded)
            {
                FailSceneLoad(load, new ResourceException(ResourceError.SceneUnloaded, "The native Scene completed without a surviving unique Scene.", address: load.Address));
            }
            else if (!load.Request.IsDone)
            {
                try
                {
                    if (load.Retention != null)
                    {
                        RequireHealthyBundle(load.Retention);
                    }
                }
                catch (Exception error)
                {
                    FailSceneLoad(load, error);
                }
                if (!load.Request.IsDone)
                {
                    load.Request.SetSuccess(new SceneLease(results[0]));
                }
            }
            var abandonedAdditive = load.Request.Status != ResourceRequestStatus.Succeeded && load.Request.Mode == LoadSceneMode.Additive && !ambiguous;
            load.Request.Native = null;
            RemoveSceneLoad(load);
            if (abandonedAdditive)
            {
                foreach (var record in results)
                {
                    TryUnloadScene(record);
                }
            }

            load.Request.Notify();
        }

        /// <summary>显式卸载当前 Lease 的真实身份。已 Dispose 的 Lease 拒绝，不按路径或复用 handle 查找替身。</summary>
        /// <param name="lease">本系统中尚未归还的调用方场景租约。</param>
        /// <returns>等待该场景卸载结算的独立请求，不消费传入租约。</returns>
        public ResourceRequest<bool> UnloadSceneAsync(SceneLease lease)
        {
            CheckThread();
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            return UnloadSceneAsync(lease.Record);
        }

        /// <summary>对独立 SceneRecord 显式卸载；用于 caller Lease 已释放或取消 Single 后的生命周期交接。</summary>
        /// <param name="record">本系统仍登记的非歧义场景记录。</param>
        /// <returns>用于等待该身份卸载结算的请求；门控或最后场景等限制以错误终态报告。</returns>
        /// <remarks>取消此请求只退出本消费者的卸载等待，不撤销已发出的原生卸载。</remarks>
        public ResourceRequest<bool> UnloadSceneAsync(SceneRecord record)
        {
            CheckThread();
            var request = new ResourceRequest<bool>();
            if (!IsAvailable)
            {
                return Fail(request, ResourceError.SessionUnavailable, "ResourceSystem is not available.");
            }

            if (record == null || !ReferenceEquals(record.Owner, this) || record.IsUnloaded || !_sceneRecords.Contains(record))
            {
                return Fail(request, ResourceError.SceneUnloaded, "The Scene identity is stale or belongs to another authority.");
            }

            if (record.IsAmbiguous)
            {
                return Fail(request, ResourceError.SceneIdentityAmbiguous, "An ambiguous external Scene candidate must be unloaded by its actual owner.");
            }

            var error = TryUnloadScene(record);
            if (error != null)
            {
                return Fail(request, error.Kind, error.Message, cause: error);
            }

            Track(request);
            record.UnloadWaiters.Add(request);
            request.Attach(() => record.UnloadWaiters.Remove(request), () => record.UnloadOperation?.progress ?? 0);
            return request;
        }

        /// <summary>复用在途卸载，或在激活门控和剩余场景条件允许时发起原生卸载。</summary>
        private ResourceException TryUnloadScene(SceneRecord record)
        {
            if (record.UnloadOperation != null || !record.IsLoaded)
            {
                return null;
            }

            if (_sceneLoads.Any(x => x.Request.Native != null && !x.Request.IsActivationRequested))
            {
                return new ResourceException(ResourceError.Busy, "Activate or cancel the gated Scene before unloading.");
            }

            var loaded = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isLoaded)
                {
                    loaded++;
                }
            }

            if (loaded <= 1)
            {
                return new ResourceException(ResourceError.Busy, "Unity cannot unload the last Scene; load a neutral Scene first. The Scene remains pinned.");
            }

            try
            {
                record.UnloadOperation = SceneManager.UnloadSceneAsync(record.Scene);
                if (record.UnloadOperation == null)
                {
                    return new ResourceException(ResourceError.LoadFailed, "Unity refused the Scene unload; ownership is retained.");
                }
            }
            catch (Exception error)
            {
                return new ResourceException(ResourceError.LoadFailed, "Unity Scene unload failed; ownership is retained.", error);
            }
            return null;
        }

        /// <summary>移除已卸载身份，归还独立图根后通知仍等待卸载的消费者。</summary>
        private void CollectScene(SceneRecord record)
        {
            record.IsUnloaded = true;
            _sceneRecords.Remove(record);
            var waiters = record.UnloadWaiters.ToArray();
            record.UnloadWaiters.Clear();
            foreach (var request in waiters)
            {
                request.SetSuccess(true);
            }

            record.UnloadOperation = null;
            record.Retention?.Dispose();
            record.Retention = null;
            UnregisterSceneCleanup();
            foreach (var request in waiters)
            {
                request.Notify();
            }
        }

        /// <summary>处理激活超时、原生迟到完成及卸载结算窗口，继续推进原系统场景队列。</summary>
        private static void TickSceneCleanup()
        {
            foreach (var system in _sceneCleanup.ToArray())
            {
                if (system._sceneLoads.Count != 0)
                {
                    var load = system._sceneLoads[0];
                    var native = load.Request.Native;
                    if (native != null)
                    {
                        if (!load.Request.IsActivationRequested && Time.realtimeSinceStartup >= load.Deadline)
                        {
                            load.Request.IsActivationRequested = true;
                            native.allowSceneActivation = true;
                            system.FailSceneLoad(load, new ResourceException(ResourceError.ActivationTimedOut,
                                "Scene activation deadline expired; native activation was released. Single replacement cannot be rolled back.", address: load.Address));
                        }
                        if (native.isDone && load.CompletedFrame < 0)
                        {
                            load.CompletedFrame = Time.frameCount;
                            load.Request.IsNativeCompleted = true;
                        }
                        if (load.CompletedFrame >= 0 && (!Application.isPlaying || Time.frameCount > load.CompletedFrame))
                        {
                            system.CompleteSceneLoad(load);
                        }
                    }
                }
                foreach (var record in system._sceneRecords.ToArray())
                {
                    if (record.IsLoaded)
                    {
                        continue;
                    }

                    if (record.UnloadedFrame < 0)
                    {
                        record.UnloadedFrame = Time.frameCount;
                    }

                    if (record.UnloadOperation != null && !record.UnloadOperation.isDone)
                    {
                        continue;
                    }

                    if (Application.isPlaying && Time.frameCount <= record.UnloadedFrame)
                    {
                        continue;
                    }

                    system.CollectScene(record);
                }
                system.PumpScenes();
                system.UnregisterSceneCleanup();
            }
        }

        /// <summary>结束本系统场景等待并尝试卸载可归属记录，未完成原生责任仍留待收尾。</summary>
        private void EndSceneSession()
        {
            foreach (var load in _sceneLoads.ToArray())
            {
                CancelSceneLoad(load);
            }
            // 不卸载歧义候选或其他 owner 的 Scene；最后场景等待 Unity 真正退出卸载，不制造替身场景。
            foreach (var record in _sceneRecords.ToArray())
            {
                if (!record.IsAmbiguous)
                {
                    TryUnloadScene(record);
                }
            }
        }
    }
}
