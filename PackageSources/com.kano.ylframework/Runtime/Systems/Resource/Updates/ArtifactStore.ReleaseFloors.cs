using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <remarks>ReleaseFloor 按项目与频道记录最高已确认序号及摘要；内容回滚不降低历史门槛。</remarks>
    public sealed partial class ArtifactStore
    {
        // 验证历史条目的有界集合、挂载范围及每个项目频道的唯一身份。
        private JArray ValidateFloors(JToken token)
        {
            if (token is not JArray floors || floors.Count > 4096)
            {
                throw new InvalidDataException("Invalid release history bound.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in floors)
            {
                if (value is not JObject item)
                {
                    throw new InvalidDataException("Invalid release history entry.");
                }

                ResourceCatalogCodec.Fields(item, "ProjectId,Platform,Channel,Sequence,CatalogDigest");
                var project = ResourceCatalogCodec.Text(item, "ProjectId");
                var channel = ResourceCatalogCodec.Text(item, "Channel");
                ResourceCatalogCodec.Id(channel);
                if (!_mounts.TryGetValue(project, out var mount) || ResourceCatalogCodec.Text(item, "Platform") != mount.Platform ||
                    ResourceCatalogCodec.Integer(item, "Sequence") < 1 || !seen.Add(project + ":" + channel))
                {
                    throw new InvalidDataException("Release history scope/sequence mismatch.");
                }

                ResourceCatalogCodec.Hex(ResourceCatalogCodec.Text(item, "CatalogDigest"), 64);
            }
            return floors;
        }

        // 各项目频道保留最高序号；与当前保留项同序号但异摘要时拒绝合并。
        private JArray MergeFloors(IEnumerable<JObject> entries)
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var key = (string)entry["ProjectId"] + ":" + (string)entry["Channel"];
                if (result.TryGetValue(key, out var previous))
                {
                    if ((long)previous["Sequence"] == (long)entry["Sequence"] && (string)previous["CatalogDigest"] != (string)entry["CatalogDigest"])
                    {
                        throw new InvalidDataException("The same confirmed release sequence has conflicting digests.");
                    }

                    if ((long)previous["Sequence"] >= (long)entry["Sequence"])
                    {
                        continue;
                    }
                }
                result[key] = (JObject)entry.DeepClone();
            }
            return ValidateFloors(new JArray(result.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value)));
        }

        // active与LKG都携带最高已确认历史。显式回滚仅改变内容指针，不降低门槛。
        // 这不是防本地篡改的安全存储；首次安装、状态丢失/回滚及合法旧发布冻结仍有明确限制。
        // 从可读且结构有效的 active/LKG 镜像合并历史，不要求其中内容版本仍能挂载。
        private JArray ReadReleaseFloors()
        {
            var entries = new List<JObject>();
            if (!ValidateOwner())
            {
                return new JArray();
            }

            foreach (var project in Projects)
            {
                foreach (var name in new[] { "active.json", "last-known-good.json" })
                {
                    var path = Path.Combine(ProjectRoot(project.ProjectId), name);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    JObject index;
                    try
                    {
                        index = ValidateIndex(Read(path));
                    }
                    catch (InvalidDataException error)
                    {
                        _recoveryNotes[path] = name + ": " + error.Message;
                        continue;
                    }
                    if ((long)index["Version"] == 2)
                    {
                        entries.AddRange(ValidateFloors(index["ReleaseFloors"]).Cast<JObject>());
                    }
                }
            }

            return MergeFloors(entries);
        }

        // 拒绝低于已确认序号的发布，以及同序号却绑定不同 Catalog 的发布。
        private void CheckReleaseFloor(ResourceRemoteRelease release)
        {
            var floor = ReadReleaseFloors().Cast<JObject>().SingleOrDefault(x => (string)x["ProjectId"] == release.ProjectId && (string)x["Channel"] == release.Channel);
            if (floor != null && (release.Sequence < (long)floor["Sequence"] || release.Sequence == (long)floor["Sequence"] && release.CatalogDigest != (string)floor["CatalogDigest"]))
            {
                throw new InvalidDataException("Remote release is older than confirmed history or conflicts at the same sequence.");
            }
        }

        /// <summary>判断准备身份携带的发布是否已被同频道历史覆盖，用于识别重复激活。</summary>
        internal bool HasConfirmedReleases(PreparedUpdate update)
        {
            if (update.Releases.Count == 0)
            {
                return true;
            }

            var floors = ReadReleaseFloors().Cast<JObject>().ToArray();
            return update.Releases.All(release => floors.Any(x => (string)x["ProjectId"] == release.ProjectId && (string)x["Channel"] == release.Channel &&
                ((long)x["Sequence"] > release.Sequence || (long)x["Sequence"] == release.Sequence && (string)x["CatalogDigest"] == release.CatalogDigest)));
        }

        // 将非空历史副本写入索引并升级其格式，不创建独立的安全存储。
        private JObject WithFloors(JObject index, JArray floors)
        {
            ValidateFloors(floors);
            if (floors.Count == 0)
            {
                return index;
            }

            index["Version"] = 2;
            index["ReleaseFloors"] = floors.DeepClone();
            return index;
        }
    }
}
