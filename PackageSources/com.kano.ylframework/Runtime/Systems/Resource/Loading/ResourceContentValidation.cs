using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;

namespace YLFramework.Resource
{
    /// <summary>获取应用 key 的调用边界与后台纯 IO/认证分开；输入句柄保持至提交或取消真正收尾。</summary>
    internal sealed class ResourceContentValidation : IDisposable
    {
        /// <summary>一份已打开内容文件及其认证上下文，密钥副本可由同组输入共享。</summary>
        private sealed class Input
        {
            internal ResourceCatalogFile File;
            internal ResourceBundleContext Context;
            internal ResourceBundleEnvelopeHeader Header;
            internal FileStream Stream;
            internal byte[] Key;
        }

        private readonly List<Input> _inputs = new();
        private readonly Dictionary<(string, string), byte[]> _keys = new();
        private readonly bool _representativesOnly;
        private ResourceContentReservation _reservation;
        private bool _isDisposed;
        private bool _hasStartedDisposal;
        private bool _isValidated;
        internal bool HasEncryptedContent => _inputs.Any(x => x.Header != null);
        internal bool HasContentFiles => _inputs.Count != 0;

        /// <summary>确定验证全量内容或每个项目密钥的代表输入，不在构造时打开文件。</summary>
        private ResourceContentValidation(bool representativesOnly) => this._representativesOnly = representativesOnly;

        /// <summary>打开内容输入、检查封装并从应用复制密钥，必要时取得一份串行验证预算。</summary>
        internal static ResourceContentValidation Capture(IEnumerable<ResourceLocalSnapshot> snapshots, ResourceDecryptionOptions options,
            bool representativesOnly = false, ResourceContentBudget budget = null)
        {
            var result = new ResourceContentValidation(representativesOnly);
            try
            {
                foreach (var snapshot in snapshots)
                {
                    foreach (var file in ContentFiles(snapshot))
                    {
                        var input = new Input { File = file, Context = Context(snapshot, file), Stream = snapshot.OpenContentFile(file) };
                        result._inputs.Add(input);
                        var header = ResourceBundleEnvelope.Inspect(input.Stream, input.Context);
                        if (header == null)
                        {
                            if (representativesOnly)
                            {
                                input.Stream.Dispose();
                                input.Stream = null;
                                result._inputs.Remove(input);
                            }
                            continue;
                        }
                        input.Header = header;
                        if (input.Stream.Length != file.Length)
                        {
                            throw new InvalidDataException("Content delivery length changed: " + file.RelativeFile);
                        }

                        var pair = (snapshot.Document.ProjectId, header.KeyId);
                        if (!result._keys.TryGetValue(pair, out var key))
                        {
                            key = CopyKey(options, pair.Item1, pair.Item2);
                            result._keys.Add(pair, key);
                        }
                        input.Key = key;
                        if (representativesOnly)
                        {
                            var previous = result._inputs.FirstOrDefault(x => !ReferenceEquals(x, input) && ReferenceEquals(x.Key, key));
                            if (previous != null)
                            {
                                var discard = previous.File.Length <= file.Length ? input : previous;
                                discard.Stream.Dispose();
                                discard.Stream = null;
                                result._inputs.Remove(discard);
                            }
                        }
                    }
                }

                if (result.HasEncryptedContent)
                {
                    if (options.MaxBufferedBytes < ResourceDecryptStream.DataBufferBytes)
                    {
                        throw new ResourceException(ResourceError.ContentBufferUnavailable, "The configured content buffer budget cannot hold one authenticated input.");
                    }

                    if (budget != null)
                    {
                        result._reservation = budget.Reserve(1)[0];
                    }
                }
                return result;
            }
            catch (Exception error)
            {
                try
                {
                    result.Dispose();
                }
                catch (Exception cleanup)
                {
                    throw new ResourceException(Failure(error, "Content validation capture failed.").Kind,
                        "Content validation capture and cleanup failed; unfinished ownership is retained in the cause.",
                        new ResourceContentCleanupException(result, new AggregateException(error, cleanup)));
                }
                throw Failure(error, "Content validation capture failed.");
            }
        }

        /// <summary>在持有的输入上核验文件哈希及认证内容，支持后台取消和检查点通知。</summary>
        internal void Validate(CancellationToken cancellation = default, Action<string> checkpoint = null)
        {
            if (_hasStartedDisposal)
            {
                throw new ObjectDisposedException(nameof(ResourceContentValidation));
            }

            if (_inputs.Count == 0)
            {
                _isValidated = true;
                return;
            }
            _reservation?.Allocate();
            foreach (var input in _inputs)
            {
                cancellation.ThrowIfCancellationRequested();
                checkpoint?.Invoke("ContentShaStarted");
                try
                {
                    ResourceLocalSnapshot.VerifyContentFile(input.Stream, input.File, cancellation);
                }
                finally
                {
                    checkpoint?.Invoke("ContentShaFinished");
                }
                if (input.Header == null)
                {
                    continue;
                }

                checkpoint?.Invoke("ContentAuthenticationStarted");
                try
                {
                    if (_representativesOnly)
                    {
                        using var probe = ResourceBundleEnvelope.OpenDecrypt(input.Stream, input.Context, input.Key, true, cancellation);
                    }
                    else
                    {
                        ResourceBundleEnvelope.ValidateAll(input.Stream, input.Context, input.Key, cancellation);
                    }
                }
                catch (Exception error)
                {
                    throw Failure(error, "Content authentication failed for " + input.File.RelativeFile + ".");
                }
                finally
                {
                    checkpoint?.Invoke("ContentAuthenticationFinished");
                }
            }
            _isValidated = true;
        }

        /// <summary>在提交前要求验证已经完成，且原输入仍可读、长度未变。</summary>
        internal void RequireStable()
        {
            if (_hasStartedDisposal || !_isValidated)
            {
                throw new InvalidOperationException("Content validation has not completed or was released.");
            }

            foreach (var input in _inputs)
            {
                if (!input.Stream.CanRead || input.Stream.Length != input.File.Length)
                {
                    throw new InvalidDataException("A validated content input changed before commit: " + input.File.RelativeFile);
                }
            }
        }

        /// <summary>枚举需要内容验证的 Bundle 和清单文件，不包含其他支持文件。</summary>
        internal static IEnumerable<ResourceCatalogFile> ContentFiles(ResourceLocalSnapshot snapshot) => snapshot.Document.Bundles
            .Cast<ResourceCatalogFile>().Concat(snapshot.Document.SupportFiles.Where(x => x.RelativeFile == snapshot.Document.ManifestFile));

        /// <summary>由冻结快照及文件身份构造认证绑定上下文。</summary>
        internal static ResourceBundleContext Context(ResourceLocalSnapshot snapshot, ResourceCatalogFile file) => new(
            snapshot.Document.ProjectId, snapshot.Document.Platform, snapshot.Document.UnityCompatibility,
            file is ResourceCatalogBundle ? "Bundle" : "Manifest", file is ResourceCatalogBundle bundle ? bundle.LogicalId : "manifest");

        /// <summary>短暂打开文件检查内容封装头，并将失败归类为资源域错误。</summary>
        internal static ResourceBundleEnvelopeHeader Inspect(ResourceLocalSnapshot snapshot, ResourceCatalogFile file)
        {
            try
            {
                using var stream = snapshot.OpenContentFile(file);
                return ResourceBundleEnvelope.Inspect(stream, Context(snapshot, file));
            }
            catch (Exception error)
            {
                var identity = file is ResourceCatalogBundle bundle ? bundle.LogicalId + " (" + file.RelativeFile + ")" : file.RelativeFile;
                throw Failure(error, "Content header validation failed for " + identity + ".");
            }
        }

        /// <summary>显式向应用复制 32 字节密钥；失败时清空临时副本并隐藏敏感异常原文。</summary>
        internal static byte[] CopyKey(ResourceDecryptionOptions options, string project, string keyId)
        {
            if (options?.KeyProvider == null)
            {
                throw new ResourceException(ResourceError.ContentKeyUnavailable, "An explicit content key provider is required for " + project + "/" + keyId + ".");
            }

            var key = new byte[32];
            try
            {
                bool found;
                try
                {
                    found = options.KeyProvider.TryCopyKey(project, keyId, key);
                }
                catch (Exception error)
                {
                    throw new ResourceException(ResourceError.ContentKeyUnavailable, "The application content key provider failed for " + project + "/" + keyId + ".",
                        new ProviderFailure(error));
                }
                if (!found)
                {
                    throw new ResourceException(ResourceError.ContentKeyUnavailable, "Content key is unavailable for " + project + "/" + keyId + ".");
                }

                return key;
            }
            catch
            {
                Array.Clear(key, 0, key.Length);
                throw;
            }
        }

        /// <summary>保留诊断因果对象，但常规Exception/历史日志不能串出应用provider的敏感原文。</summary>
        private sealed class ProviderFailure : Exception
        {
            internal Exception OriginalCause
            {
                get;
            }
            /// <summary>私下保存应用异常，不将它挂入常规 InnerException 链。</summary>
            internal ProviderFailure(Exception cause) : base("Application key provider failure; sensitive cause retained privately.") => OriginalCause = cause;
        }

        /// <summary>保留已有资源错误，或依据封装、认证和取消原因映射为资源域错误。</summary>
        internal static ResourceException Failure(Exception cause, string message)
        {
            if (cause is ResourceException resource)
            {
                return resource;
            }

            var kind = cause is PlatformNotSupportedException || cause is InvalidDataException || cause is OverflowException
                ? ResourceError.ContentFormatUnsupported : ResourceError.LoadFailed;
            for (var current = cause; current != null; current = current.InnerException)
            {
                if (current is CryptographicException)
                {
                    kind = ResourceError.ContentAuthenticationFailed;
                    break;
                }
            }

            if (cause is OperationCanceledException)
            {
                kind = ResourceError.Canceled;
            }

            return new ResourceException(kind, message, cause, source: ResourceSourceId.AssetBundle);
        }

        /// <summary>清空密钥副本并尝试关闭全部输入，只有全部成功后才归还预算。</summary>
        /// <remarks>关闭失败的输入仍被保留，可再次尝试释放，但不能再进行验证或提交检查。</remarks>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _hasStartedDisposal = true;
            foreach (var key in _keys.Values)
            {
                Array.Clear(key, 0, key.Length);
            }

            _keys.Clear();
            List<Exception> failures = null;
            foreach (var input in _inputs)
            {
                if (input.Stream == null)
                {
                    continue;
                }

                try
                {
                    input.Stream.Dispose();
                    input.Stream = null;
                }
                catch (Exception error)
                {
                    (failures ??= new()).Add(error);
                }
            }
            if (failures != null)
            {
                if (failures.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(failures[0]).Throw();
                }

                throw new AggregateException("Content input cleanup failed; unresolved inputs and budget remain owned.", failures);
            }
            _reservation?.Dispose();
            _reservation = null;
            _inputs.Clear();
            _isDisposed = true;
        }
    }
}
