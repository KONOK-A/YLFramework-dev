using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace YLFramework.Resource
{
    /// <summary>一个规范资产条目及其显式别名；别名解析回本条目，不产生第二份资产身份。</summary>
    public sealed class ResourceCatalogEntry
    {
        /// <summary>目录内唯一的规范资产标识；正式发布目录由 GUID 与 localId 组成。</summary>
        public string AssetId
        {
            get;
        }
        /// <summary>条目的规范逻辑地址，包含可选子资产键。</summary>
        public ResourceAddress Address
        {
            get;
        }
        /// <summary>该条目声明的来源、精确定位与已编译类型。</summary>
        public ResourceLocation Location
        {
            get;
        }
        /// <summary>解析到本条目的只读别名副本；挂入目录时要求项目与子资产键和规范地址一致。</summary>
        public IReadOnlyList<ResourceAddress> Aliases
        {
            get;
        }
        /// <summary>显式引用的依赖节点标识；目录验证引用，不在解析地址时执行加载。</summary>
        public IReadOnlyList<string> Dependencies
        {
            get;
        }
        internal ResourceLocalSnapshot Snapshot;
        internal ResourceCatalogAsset Descriptor;

        /// <summary>保存一份规范资产描述并复制输入集合；跨条目的唯一性与图约束由目录构造器校验。</summary>
        public ResourceCatalogEntry(string assetId, ResourceAddress address, ResourceLocation location,
            IEnumerable<ResourceAddress> aliases = null, IEnumerable<string> dependencies = null)
        {
            ResourceAddress.ValidateId(assetId, nameof(assetId));
            if (!address.IsValid)
            {
                throw new ArgumentException("A valid address is required.", nameof(address));
            }

            AssetId = assetId;
            Address = address;
            Location = location ?? throw new ArgumentNullException(nameof(location));
            Aliases = ResourceCatalog.Copy(aliases);
            Dependencies = ResourceCatalog.Copy(dependencies);
        }
    }

    /// <summary>内存依赖图中的有界节点；描述直接依赖，不自行执行依赖加载。</summary>
    public sealed class ResourceDependencyNode
    {
        /// <summary>图内唯一且按精确文本引用的节点标识。</summary>
        public string Id
        {
            get;
        }
        /// <summary>直接依赖节点标识的只读副本。</summary>
        public IReadOnlyList<string> Dependencies
        {
            get;
        }
        /// <summary>校验节点标识并复制直接依赖；缺失节点、重复边与环由所属目录检查。</summary>
        public ResourceDependencyNode(string id, IEnumerable<string> dependencies = null)
        {
            ResourceAddress.ValidateId(id, nameof(id));
            Id = id;
            Dependencies = ResourceCatalog.Copy(dependencies);
        }
    }

    /// <summary>只读内存目录快照；只解析登记的精确地址，并拒绝仅大小写不同的地址冲突。</summary>
    /// <remarks>同一来源定位、声明类型及子资产键只能有一个规范条目；额外入口须登记为别名。</remarks>
    public sealed class ResourceCatalog
    {
        internal const int MaximumItems = 16384;
        private readonly Dictionary<ResourceAddress, ResourceCatalogEntry> _addresses = new();
        /// <summary>本次内存目录实例的随机身份，不是发布内容的稳定摘要。</summary>
        public Guid SnapshotId { get; } = Guid.NewGuid();
        /// <summary>本目录所有规范地址与别名共同所属的项目。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>规范资产条目的只读集合，不另列别名条目。</summary>
        public IReadOnlyList<ResourceCatalogEntry> Entries
        {
            get;
        }
        /// <summary>已经检查引用与无环约束的内存依赖节点集合。</summary>
        public IReadOnlyList<ResourceDependencyNode> DependencyNodes
        {
            get;
        }
        /// <summary>关联正式发布快照的目录字节摘要；纯内存目录为 null。</summary>
        public string CatalogDigest => Snapshot?.Document.CatalogDigest;
        /// <summary>按规范 AssetId 描述的逻辑组；不等同于 Bundle，也不在读取时预加载。</summary>
        public IReadOnlyList<ResourceCatalogGroup> Groups { get; internal set; } = Array.Empty<ResourceCatalogGroup>();
        internal ResourceLocalSnapshot Snapshot;

        /// <summary>复制输入并建立规范地址与别名索引，同时校验资产、来源身份和依赖图冲突。</summary>
        public ResourceCatalog(string projectId, IEnumerable<ResourceCatalogEntry> entries,
            IEnumerable<ResourceDependencyNode> dependencyNodes = null)
        {
            ResourceAddress.ValidateId(projectId, nameof(projectId));
            ProjectId = projectId;
            Entries = Copy(entries ?? throw new ArgumentNullException(nameof(entries)));
            DependencyNodes = Copy(dependencyNodes);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var foldedAddresses = new HashSet<(string, string)>(new FoldedAddressComparer());
            var locations = new HashSet<(ResourceLocation, string)>();
            foreach (var entry in Entries)
            {
                if (entry == null || !ids.Add(entry.AssetId))
                {
                    throw new ArgumentException("Null or duplicate canonical asset identity.", nameof(entries));
                }

                if (!locations.Add((entry.Location, entry.Address.SubAsset)))
                {
                    throw new ArgumentException($"Duplicate source identity: {entry.Location}; use an explicit alias.", nameof(entries));
                }

                AddAddress(entry.Address, entry, foldedAddresses);
                foreach (var alias in entry.Aliases)
                {
                    AddAddress(alias, entry, foldedAddresses);
                }
            }
            ValidateGraph();
        }

        /// <summary>在集合数量上限内复制枚举，避免调用方后续修改原集合影响目录。</summary>
        internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> values)
        {
            var copy = new List<T>();
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (copy.Count == MaximumItems)
                    {
                        throw new ArgumentException("The in-memory catalog collection limit was exceeded.");
                    }

                    copy.Add(value);
                }
            }

            return copy.AsReadOnly();
        }

        /// <summary>登记指向同一条目的地址，并拒绝跨项目、子资产键变化与大小写折叠冲突。</summary>
        private void AddAddress(ResourceAddress address, ResourceCatalogEntry entry, HashSet<(string, string)> folded)
        {
            if (!address.IsValid || address.ProjectId != ProjectId ||
                address.SubAsset != entry.Address.SubAsset || !folded.Add((address.Path, address.SubAsset)))
            {
                throw new ArgumentException($"Invalid, duplicate or case-folded address conflict: {address}.");
            }

            if (_addresses.Count >= MaximumItems)
            {
                throw new ArgumentException("The catalog address limit was exceeded.");
            }

            _addresses.Add(address, entry);
        }

        /// <summary>仅用于发现地址大小写冲突，不改变实际解析时的精确匹配规则。</summary>
        private sealed class FoldedAddressComparer : IEqualityComparer<(string, string)>
        {
            /// <summary>忽略大小写比较路径与子资产键。</summary>
            public bool Equals((string, string) left, (string, string) right) =>
                StringComparer.OrdinalIgnoreCase.Equals(left.Item1, right.Item1) && StringComparer.OrdinalIgnoreCase.Equals(left.Item2, right.Item2);
            /// <summary>为同样忽略大小写的地址对生成散列值。</summary>
            public int GetHashCode((string, string) value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item1) ^
                (value.Item2 == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item2));
        }

        /// <summary>检查依赖节点和资产引用的完整性、边数上限及无环约束，并在失败时给出实际环路径。</summary>
        private void ValidateGraph()
        {
            var nodes = new Dictionary<string, ResourceDependencyNode>(StringComparer.Ordinal);
            foreach (var node in DependencyNodes)
            {
                if (node == null || nodes.ContainsKey(node.Id))
                {
                    throw new ArgumentException("Null or duplicate dependency node.");
                }
                else
                {
                    nodes.Add(node.Id, node);
                }
            }

            var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var edges = 0;
            foreach (var node in DependencyNodes)
            {
                incoming.Add(node.Id, node.Dependencies.Count);
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dependency in node.Dependencies)
                {
                    if (dependency == null || !nodes.ContainsKey(dependency) || !unique.Add(dependency))
                    {
                        throw new ArgumentException($"Missing or duplicate dependency of '{node.Id}': '{dependency}'.");
                    }

                    if (++edges > MaximumItems * 4)
                    {
                        throw new ArgumentException("The catalog edge limit was exceeded.");
                    }

                    if (!dependents.TryGetValue(dependency, out var list))
                    {
                        dependents.Add(dependency, list = new List<string>());
                    }

                    list.Add(node.Id);
                }
            }
            foreach (var entry in Entries)
            {
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dependency in entry.Dependencies)
                {
                    if (dependency == null || !nodes.ContainsKey(dependency) || !unique.Add(dependency))
                    {
                        throw new ArgumentException($"Missing or duplicate dependency of '{entry.AssetId}': '{dependency}'.");
                    }
                }
            }
            // Kahn 遍历避免长链递归溢出；剩余节点中沿依赖边给出真实环路径。
            var ready = new Queue<string>();
            foreach (var pair in incoming)
            {
                if (pair.Value == 0)
                {
                    ready.Enqueue(pair.Key);
                }
            }

            var visited = 0;
            while (ready.Count != 0)
            {
                var id = ready.Dequeue();
                visited++;
                if (dependents.TryGetValue(id, out var list))
                {
                    foreach (var dependent in list)
                    {
                        if (--incoming[dependent] == 0)
                        {
                            ready.Enqueue(dependent);
                        }
                    }
                }
            }
            if (visited == nodes.Count)
            {
                return;
            }

            string cursor = null;
            foreach (var pair in incoming)
            {
                if (pair.Value > 0)
                {
                    cursor = pair.Key;
                    break;
                }
            }

            var path = new List<string>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            while (!positions.ContainsKey(cursor))
            {
                positions.Add(cursor, path.Count);
                path.Add(cursor);
                foreach (var dependency in nodes[cursor].Dependencies)
                {
                    if (incoming[dependency] > 0)
                    {
                        cursor = dependency;
                        break;
                    }
                }
            }
            var cycle = path.GetRange(positions[cursor], path.Count - positions[cursor]);
            cycle.Add(cursor);
            throw new ArgumentException("Dependency cycle: " + string.Join(" -> ", cycle));
        }

        /// <summary>将规范地址或显式别名解析为唯一条目；未登记地址直接报错，不猜测来源路径。</summary>
        public ResourceCatalogEntry Resolve(ResourceAddress address)
        {
            if (!_addresses.TryGetValue(address, out var entry))
            {
                throw new ArgumentException($"Unknown catalog address: {address}.", nameof(address));
            }

            return entry;
        }
    }
}
