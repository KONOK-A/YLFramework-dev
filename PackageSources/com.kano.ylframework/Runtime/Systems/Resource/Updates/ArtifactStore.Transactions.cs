using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <remarks>激活通过 journal 决策和完整闭包镜像恢复；版本清理按已登记归属逐项执行，不回滚已完成的删除。</remarks>
    public sealed partial class ArtifactStore
    {
        /// <summary>同步捕获并验证准备结果的内容，再提交完整闭包转换。</summary>
        internal IReadOnlyList<ResourceLocalSnapshot> Commit(PreparedUpdate update, ResourceDecryptionOptions decryption = null)
        {
            using var receipt = CaptureCommit(update, decryption);
            receipt.Validate();
            return CommitValidated(update, receipt);
        }

        /// <summary>恢复既有事务并核对来源及当前准备授权，随后在锁外捕获本次提交验证输入。</summary>
        internal ValidationReceipt CaptureCommit(PreparedUpdate update, ResourceDecryptionOptions decryption = null, ResourceContentBudget budget = null)
        {
            if (update == null || update.Root != RootPath)
            {
                throw new ArgumentException("PreparedUpdate belongs to another store.");
            }

            ResourceLocalSnapshot[] targets;
            ResourceLocalSnapshot[] sources;
            using (Enter(false))
            {
                RequireOwner();
                RecoverLocked();
                ReadTransition(Transition(update.Projects));
                var releases = CommitReleases(update);
                RequireFrom(update.Projects);
                targets = update.Projects.Select(x => releases.SingleOrDefault(y => y.ProjectId == x.ProjectId) is { } release
                    ? OpenRemoteVersion(release, false) : Open(x.ProjectId, x.ToDigest)).ToArray();
                sources = update.Projects.Select(x => Open(x.ProjectId, x.FromDigest)).ToArray();
            }
            return ValidationReceipt.Capture(this, update, targets, sources, decryption, budget);
        }

        // 普通提交读取当前持久准备授权而非旧句柄副本；显式回滚必须仍匹配当前完整 LKG。
        private ResourceRemoteRelease[] CommitReleases(PreparedUpdate update)
        {
            if (update.IsRollback)
            {
                var good = ReadIndex("last-known-good.json") ?? throw new ResourceException(ResourceError.StaleBase, "The rollback closure is no longer available.");
                var targets = ReadArray(good, "Projects").Cast<JObject>().ToDictionary(x => (string)x["ProjectId"], x => (string)x["CatalogDigest"], StringComparer.Ordinal);
                if (update.Projects.Any(x => targets[x.ProjectId] != x.ToDigest))
                {
                    throw new ResourceException(ResourceError.StaleBase, "The last-known-good closure changed before rollback.");
                }

                return Array.Empty<ResourceRemoteRelease>();
            }
            var preparedPath = PreparedPath(Transition(update.Projects));
            if (!File.Exists(preparedPath))
            {
                throw new InvalidDataException("PreparedUpdate was discarded or consumed; Check and Prepare again.");
            }

            var record = Read(preparedPath);
            ValidatePreparedRecord(record, preparedPath);
            var releases = ReadReleaseRecords(record, true);
            foreach (var release in releases)
            {
                CheckReleaseFloor(release);
            }

            return releases;
        }

        /// <summary>以已验证收据写入 journal、更新闭包镜像并落下 Committed 决策，再完成恢复收尾。</summary>
        /// <remarks>决策前后异常均可能留下待恢复状态；异常本身不等于目标已回滚或尚未提交。</remarks>
        internal IReadOnlyList<ResourceLocalSnapshot> CommitValidated(PreparedUpdate update, ValidationReceipt receipt)
        {
            receipt.Require(this, update);
            using var guard = Enter(false);
            RequireOwner();
            RecoverMetadata();
            RequireNoJournal();
            receipt.Consume();
            var transition = Transition(update.Projects);
            ReadTransition(transition);
            var releases = CommitReleases(update);
            var floorsBefore = ReadReleaseFloors();
            var floorsAfter = MergeFloors(floorsBefore.Cast<JObject>().Concat(releases.Select(x => x.Floor())));
            RequireFrom(update.Projects);
            receipt.RequireStable();
            var journal = new JObject
            {
                ["Version"] = 1,
                ["OwnerId"] = _ownerId,
                ["Phase"] = "Verified",
                ["Transition"] = transition,
                ["ActiveBefore"] = ValidIndex("active.json", false),
                ["GoodBefore"] = ValidIndex("last-known-good.json", false)
            };
            if (floorsAfter.Count != 0)
            {
                journal["Version"] = 2;
                journal["ReleaseFloorsBefore"] = floorsBefore;
                journal["ReleaseFloorsAfter"] = floorsAfter;
            }
            Write(JournalPath, journal, () =>
            {
                receipt.RequireStable();
                RequireFrom(update.Projects);
            });
            Checkpoint?.Invoke("JournalVerified");
            var from = WithFloors(TransitionIndex(transition, "From"), floorsAfter);
            var to = WithFloors(TransitionIndex(transition, "To"), floorsAfter);
            // LKG 是上一整个闭包，而不是各项目最近一次变化的版本。
            foreach (var item in update.Projects)
            {
                receipt.RequireStable();
                RequireJournal(journal);
                Write(Path.Combine(ProjectRoot(item.ProjectId), "last-known-good.json"), from, () =>
                {
                    receipt.RequireStable();
                    RequireJournal(journal);
                });
                Checkpoint?.Invoke("LastGoodWritten:" + item.ProjectId);
                RequireJournal(journal);
                Write(Path.Combine(ProjectRoot(item.ProjectId), "active.json"), to, () =>
                {
                    receipt.RequireStable();
                    RequireJournal(journal);
                });
                Checkpoint?.Invoke("ActiveWritten:" + item.ProjectId);
            }
            RequireJournal(journal);
            var committed = (JObject)journal.DeepClone();
            committed["Phase"] = "Committed";
            Checkpoint?.Invoke("BeforeCommitDecision");
            Write(JournalPath, committed, () =>
            {
                receipt.RequireStable();
                RequireJournal(journal);
            });
            Checkpoint?.Invoke("CommitDecided");
            receipt.RequireStable();
            RecoverLocked(false);
            return Select(false);
        }

        // 从已校验转换的 From 或 To 一侧生成完整项目索引。
        private JObject TransitionIndex(JArray transition, string field) => new()
        {
            ["Version"] = 1,
            ["OwnerId"] = _ownerId,
            ["Projects"] = new JArray(transition.Cast<JObject>().Select(x => new JObject
            {
                ["ProjectId"] = (string)x["ProjectId"],
                ["Platform"] = (string)x["Platform"],
                ["CatalogDigest"] = (string)x[field]
            }))
        };

        // 校验 journal 决策、完整转换与原索引，并拒绝降低已确认历史的门槛描述。
        private JObject ValidateJournal(JObject journal)
        {
            var version = ResourceCatalogCodec.Integer(journal, "Version");
            ResourceCatalogCodec.Fields(journal, version == 2 ? "Version,OwnerId,Phase,Transition,ActiveBefore,GoodBefore,ReleaseFloorsBefore,ReleaseFloorsAfter" : "Version,OwnerId,Phase,Transition,ActiveBefore,GoodBefore");
            if (version != 1 && version != 2 || ResourceCatalogCodec.Text(journal, "OwnerId") != _ownerId ||
                !new[] { "Verified", "Committed" }.Contains(ResourceCatalogCodec.Text(journal, "Phase")))
            {
                throw new InvalidDataException("Invalid commit journal identity/state.");
            }

            ReadTransition(journal["Transition"]);
            if (version == 2)
            {
                var before = ValidateFloors(journal["ReleaseFloorsBefore"]);
                var after = ValidateFloors(journal["ReleaseFloorsAfter"]);
                if (!JToken.DeepEquals(MergeFloors(before.Cast<JObject>().Concat(after.Cast<JObject>())), after))
                {
                    throw new InvalidDataException("Journal lowers confirmed release history.");
                }
            }
            foreach (var field in new[] { "ActiveBefore", "GoodBefore" })
            {
                if (journal[field]?.Type == JTokenType.Null)
                {
                    continue;
                }

                if (journal[field] is not JObject index)
                {
                    throw new InvalidDataException("Invalid journal index snapshot.");
                }

                ValidateIndex(index);
            }
            return journal;
        }

        // 每个受保护写入前复核根归属及正式 journal 仍与本次决策一致。
        private void RequireJournal(JObject expected)
        {
            RequireOwner();
            if (!File.Exists(JournalPath) || !JToken.DeepEquals(ValidateJournal(Read(JournalPath)), expected))
            {
                throw new InvalidDataException("Commit journal changed before the protected write.");
            }
        }

        // 存在任何 journal 占位时拒绝开始新的转换，要求先进入恢复边界。
        private void RequireNoJournal()
        {
            if (Exists(JournalPath))
            {
                throw new ResourceException(ResourceError.Busy, "A commit needs recovery at initialization or inside the activation barrier.");
            }
        }

        // Verified 恢复原指针；Committed 优先完成目标，目标文件失败时复验来源后恢复原闭包。
        private void RecoverLocked(bool refresh = true)
        {
            if (!ValidateOwner())
            {
                return;
            }

            RecoverMetadata();
            if (!File.Exists(JournalPath))
            {
                return;
            }

            var journal = ValidateJournal(Read(JournalPath));
            var transition = ReadTransition(journal["Transition"]);
            var committed = (string)journal["Phase"] == "Committed";
            var floorsBefore = (long)journal["Version"] == 2 ? ValidateFloors(journal["ReleaseFloorsBefore"]) : new JArray();
            var floorsAfter = (long)journal["Version"] == 2 ? ValidateFloors(journal["ReleaseFloorsAfter"]) : new JArray();
            var from = WithFloors(TransitionIndex(transition, "From"), floorsBefore);
            var to = WithFloors(TransitionIndex(transition, "To"), floorsAfter);
            try
            {
                OpenIndex(committed ? to : from, refresh);
            }
            catch (Exception error) when (committed && IsFileFailure(error))
            {
                OpenIndex(from, refresh);
                committed = false;
            }
            var active = committed ? to : journal["ActiveBefore"] as JObject;
            var good = committed ? WithFloors(TransitionIndex(transition, "From"), floorsAfter) : journal["GoodBefore"] as JObject;
            if (!committed && floorsBefore.Count != 0)
            {
                // 内容损坏可能使Before索引不可挂载，但不能丢弃其中仍有效的已确认历史。
                // From已完整复验；只恢复原门槛，不确认本次失败的发布序号。
                active = WithFloors(active == null ? from : (JObject)active.DeepClone(), floorsBefore);
                if (good != null)
                {
                    good = WithFloors((JObject)good.DeepClone(), floorsBefore);
                }
            }
            if (active != null)
            {
                OpenIndex(active, refresh);
            }

            if (good != null)
            {
                OpenIndex(good, refresh);
            }
            // 全部验证完成后才恢复镜像。每个镜像自身也携带完整闭包，journal 收尾后仍能识别混代。
            foreach (var project in Projects)
            {
                RestorePointer(project.ProjectId, "last-known-good.json", good, () => RequireJournal(journal));
                RestorePointer(project.ProjectId, "active.json", active, () => RequireJournal(journal));
            }
            RequireJournal(journal);
            if (committed)
            {
                RemovePreparedRecord(transition);
            }

            File.Delete(JournalPath);
            Checkpoint?.Invoke("Recovered");
        }

        // 只清理具备固定控制槽或正式记录归属依据的 next 候选，不把它们提升为决策。
        private void RecoverMetadata()
        {
            RequireOwner();
            RemoveCandidate(JournalPath + ".next", x => ValidateJournal(x));
            foreach (var project in Projects)
            {
                foreach (var name in new[] { "active.json", "last-known-good.json" })
                {
                    RemoveCandidate(Path.Combine(ProjectRoot(project.ProjectId), name) + ".next", x => ValidateIndex(x));
                }

                var versions = Path.Combine(ProjectRoot(project.ProjectId), "versions");
                ResourceLocalSnapshot.RejectLinks(versions);
                foreach (var version in Directory.GetDirectories(versions))
                {
                    var path = Path.Combine(version, VersionFile);
                    if (!File.Exists(path) || !Exists(path + ".next"))
                    {
                        continue;
                    }

                    ValidateVersionMarker(Read(path), project.ProjectId, Path.GetFileName(version));
                    RemoveCandidate(path + ".next", x => ValidateVersionMarker(x, project.ProjectId, Path.GetFileName(version)));
                }
            }
            ResourceLocalSnapshot.RejectLinks(PreparedRoot);
            foreach (var path in Directory.GetFiles(PreparedRoot, "*.json.next"))
            {
                // 已登记的正式记录为同名槽提供归属。首次发布中断的未知候选不进入 Prepared 枚举。
                if (File.Exists(path.Substring(0, path.Length - 5)))
                {
                    RemoveCandidate(path, x => ValidatePreparedRecord(x, path.Substring(0, path.Length - 5)));
                }
            }
        }

        // 删除有外部归属依据的未提交候选；完整候选仍须验证，不能删除异主有效记录。
        private void RemoveCandidate(string path, Action<JObject> validate)
        {
            if (!Exists(path))
            {
                return;
            }

            RequireOwner();
            ResourceLocalSnapshot.RejectLinks(path);
            if (!File.Exists(path))
            {
                throw new InvalidDataException("A replacement control slot is not a file.");
            }

            JObject candidate = null;
            try
            {
                candidate = Read(path);
            }
            catch (InvalidDataException) { /* 未提交且有归属依据的截断候选不是恢复决策。 */ }
            if (candidate != null)
            {
                validate(candidate); // 完整但属于其他 owner 的内容明确拒绝。
            }

            RequireOwner();
            File.Delete(path);
        }

        // 在已归属控制槽内恢复或删除单个镜像，保护回调仍在实际写入前复核。
        private void RestorePointer(string project, string name, JObject index, Action guard = null)
        {
            var path = Path.Combine(ProjectRoot(project), name);
            JObject previous = null;
            if (File.Exists(path))
            {
                try
                {
                    previous = Read(path);
                }
                catch (InvalidDataException) { /* 正式槽已由 root marker 归属；损坏字节不阻止已验证闭包修复它。 */ }
            }

            if (previous != null && (string)previous["OwnerId"] != _ownerId)
            {
                throw new InvalidDataException("Foreign ownership in an activation control slot.");
            }

            RequireOwner();
            guard?.Invoke();
            if (index == null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            if (JToken.DeepEquals(previous, index))
            {
                return;
            }

            var checkpoint = Checkpoint;
            try
            {
                Checkpoint = null;
                Write(path, index, guard);
            }
            finally
            {
                Checkpoint = checkpoint;
            }
        }

        // 验证准备记录的归属、完成状态、转换路径身份及可选远端授权。
        private void ValidatePreparedRecord(JObject record, string path)
        {
            var version = ResourceCatalogCodec.Integer(record, "Version");
            ResourceCatalogCodec.Fields(record, version == 2 ? "Version,OwnerId,State,Transition,Releases" : "Version,OwnerId,State,Transition");
            if (version != 1 && version != 2 || ResourceCatalogCodec.Text(record, "OwnerId") != _ownerId ||
                ResourceCatalogCodec.Text(record, "State") != "Complete" || PreparedPath(ReadTransition(record["Transition"])) != path)
            {
                throw new InvalidDataException("Prepared closure record ownership/identity mismatch.");
            }

            if (version == 2)
            {
                ReadReleaseRecords(record);
            }
        }

        /// <summary>显式删除该完整转换的持久准备记录，不删除版本文件或改写 active。</summary>
        /// <param name="update">属于本 Store 的准备身份；相同转换的其他旧句柄也将失去此准备记录。</param>
        /// <remarks>可能先清理已归属的 next 元数据；版本能否删除仍由独立清理流程重新判断。</remarks>
        public void DiscardPreparedUpdate(PreparedUpdate update)
        {
            if (update == null || update.Root != RootPath)
            {
                throw new ArgumentException("PreparedUpdate belongs to another store.");
            }

            using var guard = Enter(false);
            RequireOwner();
            RecoverMetadata();
            RequireNoJournal();
            RemovePreparedRecord(Transition(update.Projects));
        }

        // 按完整转换定位准备槽，复核归属后删除正式记录。
        private void RemovePreparedRecord(JArray transition)
        {
            var path = PreparedPath(transition);
            if (!File.Exists(path))
            {
                return;
            }

            ValidatePreparedRecord(Read(path), path);
            RequireOwner();
            File.Delete(path);
        }

        /// <summary>将当前闭包到完整 LKG 的转换包装为显式回滚身份，尚不提交指针。</summary>
        internal PreparedUpdate Rollback(IEnumerable<ResourceLocalSnapshot> current)
        {
            using var guard = Enter(false);
            RequireOwner();
            RecoverMetadata();
            RequireNoJournal();
            var from = Closure(current);
            var good = ReadIndex("last-known-good.json") ?? throw new ResourceException(ResourceError.AddressNotFound, "No last-known-good closure is available.");
            var entries = OpenIndex(good).Select(x => new UpdateProject(from[x.Document.ProjectId], x, Array.Empty<UpdateFile>())).ToList();
            if (entries.All(x => !x.IsChanged))
            {
                throw new ResourceException(ResourceError.AddressNotFound, "No distinct complete last-known-good version is available.");
            }

            return new PreparedUpdate(RootPath, entries, true);
        }

        /// <summary>合并运行中与持久保护后清理已归属的版本、staging 和下载分片，保留未知内容。</summary>
        internal VersionCleanupResult Cleanup(IEnumerable<ResourceLocalSnapshot> held)
        {
            using var guard = Enter(false);
            if (!ValidateOwner())
            {
                return new VersionCleanupResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            }

            RecoverMetadata();
            RequireNoJournal();
            var protectedPaths = new HashSet<string>(held.Select(x => x.DirectoryPath), StringComparer.OrdinalIgnoreCase);
            var deleted = new List<string>();
            var errors = new List<string>();
            foreach (var record in IndexProtectionRecords(true))
            {
                foreach (var protection in ProtectionPaths(record.data, record.reason))
                {
                    protectedPaths.Add(protection.path);
                }
            }

            foreach (var record in PreparedRecords())
            {
                foreach (var protection in ProtectionPaths(record, ArtifactProtection.PreparedFrom | ArtifactProtection.PreparedTo))
                {
                    protectedPaths.Add(protection.path);
                }
            }

            foreach (var mount in Projects)
            {
                var root = ProjectRoot(mount.ProjectId);
                CleanupParts(mount, errors);
                foreach (var path in Directory.GetDirectories(Path.Combine(root, "versions")))
                {
                    if (!File.Exists(Path.Combine(path, VersionFile)))
                    {
                        continue; // 未知目录不是本系统清理对象。
                    }

                    try
                    {
                        if (protectedPaths.Contains(path))
                        {
                            continue;
                        }

                        var marker = Read(Path.Combine(path, VersionFile));
                        ValidateVersionMarker(marker, mount.ProjectId, Path.GetFileName(path));
                        DeleteKnownDirectory(path, VersionFile, marker, RegisteredFiles(marker));
                        _verified.Remove(path);
                        deleted.Add(path);
                    }
                    catch (Exception error) when (IsFileFailure(error))
                    {
                        errors.Add(path + ": " + error.Message);
                    }
                }
                foreach (var path in Directory.GetDirectories(Path.Combine(root, "staging")))
                {
                    const string StageFile = ".ylr-stage.json";
                    if (!File.Exists(Path.Combine(path, StageFile)))
                    {
                        continue;
                    }

                    try
                    {
                        var marker = Read(Path.Combine(path, StageFile));
                        ValidateStage(marker, mount.ProjectId, Path.GetFileName(path));
                        DeleteKnownDirectory(path, StageFile, marker, RegisteredFiles(marker).Concat(new[] { VersionFile, VersionFile + ".next", StageFile + ".next" }));
                        deleted.Add(path);
                    }
                    catch (Exception error) when (IsFileFailure(error))
                    {
                        errors.Add(path + ": " + error.Message);
                    }
                }
            }
            return new VersionCleanupResult(deleted, protectedPaths, errors);
        }

        // 校验 staging 的 Store、项目平台、尝试目录身份及登记文件集合。
        private void ValidateStage(JObject marker, string project, string attempt)
        {
            ResourceCatalogCodec.Fields(marker, "Version,OwnerId,ProjectId,Platform,CatalogDigest,Attempt,Files");
            if (ResourceCatalogCodec.Integer(marker, "Version") != 1 || ResourceCatalogCodec.Text(marker, "OwnerId") != _ownerId ||
                ResourceCatalogCodec.Text(marker, "ProjectId") != project || ResourceCatalogCodec.Text(marker, "Platform") != _mounts[project].Platform ||
                ResourceCatalogCodec.Text(marker, "Attempt") != attempt)
            {
                throw new InvalidDataException("Unknown staging ownership.");
            }

            ResourceCatalogCodec.Hex(attempt, 32);
            ResourceCatalogCodec.Hex(ResourceCatalogCodec.Text(marker, "CatalogDigest"), 64);
            RegisteredFiles(marker);
        }

        // 预检整个已知文件集合后逐项删除并最后删标记；中途 IO 失败不恢复此前已删文件。
        private void DeleteKnownDirectory(string path, string markerName, JObject marker, IEnumerable<string> files)
        {
            RequireOwner();
            ResourceLocalSnapshot.RejectLinks(path);
            var allowed = new HashSet<string>(files.Append(markerName), StringComparer.Ordinal);
            // 两个调用方共享同一闭合文件集合判断；删除前仍完整预检，不边枚举边删。
            KnownDirectoryFiles(path, allowed).ToArray();
            // marker 最后删除。文件占用造成中断后，不要求残余目录重新成为完整可加载版本才能续清。
            foreach (var name in allowed.Where(x => x != markerName).OrderBy(x => x, StringComparer.Ordinal).Append(markerName))
            {
                RequireOwner();
                if (!JToken.DeepEquals(Read(Path.Combine(path, markerName)), marker))
                {
                    throw new InvalidDataException("Cleanup ownership changed.");
                }

                var file = Path.Combine(path, name);
                ResourceLocalSnapshot.RejectLinks(file);
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            Directory.Delete(path);
        }

        // 枚举普通且在白名单内的直属文件，未知条目或链接会中止枚举。
        private static IEnumerable<string> KnownDirectoryFiles(string path, HashSet<string> allowed)
        {
            ResourceLocalSnapshot.RejectLinks(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                ResourceLocalSnapshot.RejectLinks(entry);
                if (!File.Exists(entry) || !allowed.Contains(Path.GetFileName(entry)))
                {
                    throw new InvalidDataException("Owned cleanup encountered an unknown entry; nothing in this directory was removed.");
                }

                yield return entry;
            }
        }

        // 枚举完整指针镜像提供的保护记录，按调用方要求决定是否复验其版本内容。
        private IEnumerable<(JObject data, ArtifactProtection reason)> IndexProtectionRecords(bool verifyIndexes, Action<string> observe = null)
        {
            foreach (var name in new[] { "active.json", "last-known-good.json" })
            {
                var index = ReadIndex(name, observe);
                if (index == null)
                {
                    continue;
                }

                if (verifyIndexes)
                {
                    OpenIndex(index);
                }

                yield return (index, name == "active.json" ? ArtifactProtection.Active : ArtifactProtection.LastKnownGood);
            }
        }

        // 将索引或准备转换中的版本身份映射为具体路径及对应保护原因。
        private IEnumerable<(string path, ArtifactProtection reason)> ProtectionPaths(JObject data, ArtifactProtection reason)
        {
            if ((reason & ArtifactProtection.PreparedFrom) == 0)
            {
                foreach (JObject item in ReadArray(data, "Projects"))
                {
                    yield return (VersionPath((string)item["ProjectId"], (string)item["CatalogDigest"]), reason);
                }

                yield break;
            }
            foreach (JObject item in ReadTransition(data["Transition"]))
            {
                yield return (VersionPath((string)item["ProjectId"], (string)item["From"]), ArtifactProtection.PreparedFrom);
                yield return (VersionPath((string)item["ProjectId"], (string)item["To"]), ArtifactProtection.PreparedTo);
            }
        }
    }
}
