using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <remarks>下载分片以源 URL、摘要和归属记录绑定，尚未提升为可挂载的正式版本。</remarks>
    public sealed partial class ArtifactStore
    {
        // 清理已验证归属的分片及候选记录，未知条目保留并计入错误说明。
        private void CleanupParts(ResourceProjectMount project, List<string> errors)
        {
            var root = System.IO.Path.Combine(ProjectRoot(project.ProjectId), "parts");
            ResourceLocalSnapshot.RejectLinks(root);
            if (File.Exists(root))
            {
                errors.Add("Partial directory is occupied by an unknown file.");
                return;
            }
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var metadata in Directory.GetFiles(root, "*.json"))
            {
                try
                {
                    var record = Read(metadata);
                    ValidatePartial(record, metadata, project);
                    var part = System.IO.Path.ChangeExtension(metadata, ".part");
                    var next = metadata + ".next";
                    ValidatePartialFiles(metadata, record, project);
                    RequireOwner();
                    if (!JToken.DeepEquals(Read(metadata), record))
                    {
                        throw new InvalidDataException("Partial record changed during cleanup.");
                    }

                    if (File.Exists(part))
                    {
                        File.Delete(part);
                    }

                    if (File.Exists(next))
                    {
                        File.Delete(next);
                    }

                    File.Delete(metadata); // 最后删归属凭据；中途失败仍可安全重试。
                }
                catch (Exception error) when (IsFileFailure(error))
                {
                    errors.Add(System.IO.Path.GetFileName(metadata) + ": " + error.Message);
                }
            }

            foreach (var next in Directory.GetFiles(root, "*.json.next"))
            {
                try
                {
                    var metadata = next.Substring(0, next.Length - ".next".Length);
                    if (Exists(metadata) || Exists(System.IO.Path.ChangeExtension(metadata, ".part")))
                    {
                        continue;
                    }

                    ValidatePartial(Read(next), metadata, project);
                    RequireOwner();
                    File.Delete(next);
                }
                catch (Exception error) when (IsFileFailure(error))
                {
                    errors.Add(System.IO.Path.GetFileName(next) + ": " + error.Message);
                }
            }

            foreach (var unknown in Directory.GetFileSystemEntries(root))
            {
                errors.Add("Unknown download entry preserved: " + System.IO.Path.GetFileName(unknown));
            }
        }

        // 核对分片的归属、源 URL、内容身份及其派生文件名，不验证分片内容 hash。
        private void ValidatePartial(JObject data, string path, ResourceProjectMount project)
        {
            ResourceCatalogCodec.Fields(data, "Version,OwnerId,State,ProjectId,Platform,CatalogDigest,File,Length,Sha256,Source,ETag");
            if (ResourceCatalogCodec.Integer(data, "Version") != 1 || ResourceCatalogCodec.Text(data, "OwnerId") != _ownerId ||
                ResourceCatalogCodec.Text(data, "State") != "Partial" || ResourceCatalogCodec.Text(data, "ProjectId") != project.ProjectId ||
                ResourceCatalogCodec.Text(data, "Platform") != project.Platform || ResourceCatalogCodec.Integer(data, "Length") < 0)
            {
                throw new InvalidDataException("Partial cleanup ownership mismatch.");
            }

            var digest = ResourceCatalogCodec.Text(data, "CatalogDigest");
            var sha = ResourceCatalogCodec.Text(data, "Sha256");
            ResourceCatalogCodec.Hex(digest, 64);
            ResourceCatalogCodec.Hex(sha, 64);
            var etag = ResourceCatalogCodec.Text(data, "ETag", true);
            if (etag != null && !ResourceDownloadResponse.StrongETag(etag))
            {
                throw new InvalidDataException("Invalid partial validator.");
            }

            var file = ResourceCatalogCodec.Text(data, "File");
            if (data["Source"]?.Type != JTokenType.String || ((string)data["Source"]).Length > 4096)
            {
                throw new InvalidDataException("Invalid partial source bound.");
            }

            var source = (string)data["Source"];
            if (file == "." || file == ".." || file.Length > 128 || file.Any(x => !(x <= 127 && (char.IsLetterOrDigit(x) || x == '_' || x == '-' || x == '.'))) ||
                !Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || source != uri.AbsoluteUri || source.IndexOf('%') >= 0 ||
                !uri.AbsolutePath.EndsWith("/" + digest + "/" + file, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid partial target identity.");
            }

            var token = ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(source + ":" + sha)).Substring(0, 32);
            if (System.IO.Path.GetFileName(path) != token + ".json")
            {
                throw new InvalidDataException("Partial record filename mismatch.");
            }
        }

        // 检查数据路径及 next 记录，允许 ETag 更新但不允许更换目标归属。
        private void ValidatePartialFiles(string metadata, JObject record, ResourceProjectMount project)
        {
            var part = System.IO.Path.ChangeExtension(metadata, ".part");
            ResourceLocalSnapshot.RejectLinks(part);
            if (Directory.Exists(part))
            {
                throw new InvalidDataException("Partial data path is occupied by an unknown directory.");
            }

            var next = metadata + ".next";
            if (!Exists(next))
            {
                return;
            }

            var candidate = Read(next);
            ValidatePartial(candidate, metadata, project);
            var current = (JObject)record.DeepClone();
            current.Remove("ETag");
            candidate.Remove("ETag");
            if (!JToken.DeepEquals(current, candidate))
            {
                throw new InvalidDataException("Partial candidate changed target ownership.");
            }
        }

        /// <summary>parts只保存Partial记录。即使长度完整也必须实际hash；它从来不是可挂载版本或Complete凭据。</summary>
        internal sealed class DownloadPart
        {
            private readonly ArtifactStore _store;
            private readonly JObject _identity;
            private readonly string _metadata;
            internal string Path
            {
                get;
            }
            internal string Validator
            {
                get; private set;
            }
            internal long Expected => (long)_identity["Length"];
            internal long Length
            {
                get
                {
                    Require();
                    return File.Exists(Path) ? new FileInfo(Path).Length : 0;
                }
            }

            /// <summary>创建或重新打开身份完全匹配的分片记录，拒绝认领未登记数据。</summary>
            internal DownloadPart(ArtifactStore store, ResourceRemoteRelease release, string file, long length, string sha256)
            {
                store.RequireOwner();
                this._store = store;
                ResourceCatalogCodec.Hex(sha256, 64);
                var source = release.Source.Resolve(release.CatalogDigest + "/" + file).AbsoluteUri;
                var token = ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(source + ":" + sha256)).Substring(0, 32);
                Path = System.IO.Path.Combine(store.ProjectRoot(release.ProjectId), "parts", token + ".part");
                _metadata = System.IO.Path.ChangeExtension(Path, ".json");
                _identity = new JObject
                {
                    ["Version"] = 1,
                    ["OwnerId"] = store._ownerId,
                    ["State"] = "Partial",
                    ["ProjectId"] = release.ProjectId,
                    ["Platform"] = release.Platform,
                    ["CatalogDigest"] = release.CatalogDigest,
                    ["File"] = file,
                    ["Length"] = length,
                    ["Sha256"] = sha256,
                    ["Source"] = source
                };
                ResourceLocalSnapshot.RejectLinks(Path);
                ResourceLocalSnapshot.RejectLinks(_metadata);
                if (!File.Exists(_metadata))
                {
                    if (Exists(Path) || Exists(_metadata + ".next"))
                    {
                        throw new InvalidDataException("Unregistered download partial; refusing adoption.");
                    }

                    store.Write(_metadata, Record(null));
                }
                else
                {
                    var current = store.Read(_metadata);
                    Validate(current);
                    Validator = (string)current["ETag"];
                    store.RemoveCandidate(_metadata + ".next", Validate);
                }
                if (Length > Expected)
                {
                    Reset();
                }
            }

            // 将当前强校验器附加到固定分片身份，生成可持久写入的记录副本。
            private JObject Record(string validator)
            {
                var data = (JObject)_identity.DeepClone();
                data["ETag"] = validator == null ? JValue.CreateNull() : new JValue(validator);
                return data;
            }

            // 要求记录与本分片身份完全一致，且非空 ETag 满足强校验器格式。
            private void Validate(JObject data)
            {
                var etag = ResourceCatalogCodec.Text(data, "ETag", true);
                if (etag != null && !ResourceDownloadResponse.StrongETag(etag) || !JToken.DeepEquals(data, Record(etag)))
                {
                    throw new InvalidDataException("Download partial ownership or target identity mismatch.");
                }
            }

            // 每次使用前复核 Store、数据路径及正式分片记录未发生归属变化。
            private void Require()
            {
                _store.RequireOwner();
                ResourceLocalSnapshot.RejectLinks(Path);
                if (!JToken.DeepEquals(_store.Read(_metadata), Record(Validator)))
                {
                    throw new InvalidDataException("Partial ownership changed during transfer.");
                }
            }

            /// <summary>先清除持久 ETag，再截断已归属数据，使下一次请求从零开始。</summary>
            internal void Reset()
            {
                Require();
                _store.Write(_metadata, Record(null));
                Validator = null;
                ResourceLocalSnapshot.RejectLinks(Path);
                using var output = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None);
                output.Flush(true);
            }

            /// <summary>按已确认偏移打开覆写或追加流并更新强 ETag；返回流由调用方关闭。</summary>
            internal FileStream OpenWrite(long offset, string validator)
            {
                Require();
                if (offset != 0 && (offset != Length || validator != Validator))
                {
                    throw new InvalidDataException("Partial changed before append.");
                }

                var strong = ResourceDownloadResponse.StrongETag(validator) ? validator : null;
                if (offset == 0)
                {
                    _store.Write(_metadata, Record(null));
                    Validator = null;
                }
                ResourceLocalSnapshot.RejectLinks(Path);
                var output = new FileStream(Path, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None);
                try
                {
                    if (Validator != strong)
                    {
                        _store.Write(_metadata, Record(strong));
                        Validator = strong;
                    }
                    return output;
                }
                catch
                {
                    output.Dispose();
                    throw;
                }
            }

            /// <summary>实际读取完整分片并验证预期长度和 SHA256，不将 Partial 自动提升为正式版本。</summary>
            internal void Verify(CancellationToken cancellation = default)
            {
                Require();
                using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                _store.Checkpoint?.Invoke("PartShaStarted");
                try
                {
                    ResourceLocalSnapshot.VerifyContentFile(input, new ResourceCatalogFile(new JObject
                    {
                        ["RelativeFile"] = _identity["File"],
                        ["Length"] = Expected,
                        ["Sha256"] = _identity["Sha256"]
                    }), cancellation);
                }
                finally
                {
                    _store.Checkpoint?.Invoke("PartShaFinished");
                }
            }
        }
    }
}
