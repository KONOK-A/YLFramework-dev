using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Audio
{
    /// <summary>素材定位方式；ExternalProvider 必须由调用方显式提供后端，不自动注册外部资源服务。</summary>
    public enum AudioClipSource
    {
        /// <summary>直接借用条目中的 Clip，由调用方保活。</summary>
        Clip,
        /// <summary>仅保留旧 Resources 定位值供显式迁移；运行时不选择此模式。</summary>
        Resources,
        /// <summary>仅保留旧 AssetBundle 定位值供显式迁移；运行时不选择此模式。</summary>
        AssetBundle,
        /// <summary>使用调用方显式传入的 Provider 和条目的完整 key。</summary>
        ExternalProvider,
        /// <summary>使用已初始化 ResourceSystem 中的 ProjectId 与逻辑地址。</summary>
        Resource
    }
    /// <summary>组内抽选规则；Weighted 的零权重排除条目。</summary>
    public enum AudioClipSelectionMode
    {
        /// <summary>在可选条目间均匀抽选，忽略权重。</summary>
        Uniform,
        /// <summary>按有限非负相对权重抽选，零权重条目不参与。</summary>
        Weighted
    }

    /// <summary>直接借用的 Clip 或 ProjectId/逻辑地址。旧 Resources/AssetBundle 枚举值仅供显式迁移读取。</summary>
    [Serializable]
    public sealed class AudioClipEntry
    {
        /// <summary>明确的素材定位方式；默认直接 Clip。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Source")]
        public AudioClipSource source;
        /// <summary>仅 Clip 模式使用，调用方负责保活，不自动卸载。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Clip")]
        public AudioClip clip;
        /// <summary>Resource 模式的逻辑地址；ExternalProvider 模式为该后端的完整 key。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Key")]
        public string key;
        /// <summary>Resource 模式的稳定资源项目身份，不从旧 BundleName 或外部项目名称推断。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ProjectId")]
        public string projectId;
        /// <summary>保留既有序列化值供一次性显式转换；运行时不使用或推断其含义。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("BundleName")]
        [HideInInspector] public string bundleName;
        /// <summary>Weighted 模式的有限非负相对权重；0 排除，Uniform 忽略。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Weight")]
        public float weight = 1f;
    }

    /// <summary>仅负责选曲，不声明音量控制通道或播放所有权。</summary>
    [Serializable]
    public sealed class AudioClipGroup
    {
        /// <summary>区分大小写的组名；同库内重名使该组选择失败。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Name")]
        public string name;
        /// <summary>均匀或加权抽选一次。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Selection")]
        public AudioClipSelectionMode selection;
        /// <summary>直接 Clip 与明确命名定位可混用的候选。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Entries")]
        public List<AudioClipEntry> entries = new List<AudioClipEntry>();
    }

    /// <summary>一次抽选的不可变定位快照；不会在加载完成后再次随机。</summary>
    public readonly struct AudioClipSelection
    {
        /// <summary>直接素材快照；命名模式为 null。</summary>
        public AudioClip Clip
        {
            get;
        }
        /// <summary>命名资源身份快照；直接模式为 default。</summary>
        public AudioClipAddress Address
        {
            get;
        }
        /// <summary>本次选择是否使用显式 provider。</summary>
        public bool IsNamed => Address.Provider != null;
        /// <summary>保存本次选曲的直接引用或命名地址，不在此处取得资源。</summary>
        internal AudioClipSelection(AudioClip clip, AudioClipAddress address)
        {
            Clip = clip;
            Address = address;
        }
    }

    /// <summary>可共享的具名素材组。直接 Clip 永远借用，命名定位沿用正式 Audio provider 与租约。</summary>
    [CreateAssetMenu(menuName = "YLFramework/Audio/Clip Library")]
    public sealed class AudioClipLibrary : ScriptableObject
    {
        /// <summary>序列化素材组；不包含控制通道、音源或播放资源租约。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("Groups")]
        public List<AudioClipGroup> groups = new List<AudioClipGroup>();

        /// <summary>精确按组名抽选一次。缺组、重名、非法权重或定位失败返回 false，不创建播放/通道。</summary>
        /// <remarks>均匀模式忽略 Weight；空 Clip/空 key 为不可选条目。非法枚举、负数/非有限加权权重使整组失败。
        /// ExternalProvider 只使用显式传入的实例；Resource 必须先显式初始化 ResourceSystem。旧配置须显式迁移。</remarks>
        /// <param name="groupName">区分大小写的素材组名，不是播放控制通道名。</param>
        /// <param name="selection">成功时的定位快照；失败时为默认值。</param>
        /// <param name="externalProvider">仅供 ExternalProvider 条目使用的显式后端。</param>
        public bool TrySelect(string groupName, out AudioClipSelection selection, IAudioClipProvider externalProvider = null)
        {
            selection = default;
            if (string.IsNullOrWhiteSpace(groupName) || groups == null)
            {
                return false;
            }

            AudioClipGroup group = null;
            foreach (var candidate in groups)
            {
                if (candidate == null || !string.Equals(candidate.name, groupName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (group != null)
                {
                    return false;
                }

                group = candidate;
            }
            if (group == null || group.entries == null || (uint)group.selection > (uint)AudioClipSelectionMode.Weighted)
            {
                return false;
            }

            var weighted = group.selection == AudioClipSelectionMode.Weighted;
            double total = 0;
            foreach (var entry in group.entries)
            {
                if (entry == null)
                {
                    continue;
                }

                if ((uint)entry.source > (uint)AudioClipSource.Resource ||
                    weighted && (!AudioSoundOptions.IsFinite(entry.weight) || entry.weight < 0))
                {
                    return false;
                }

                if (CanSelect(entry, externalProvider))
                {
                    total += weighted ? entry.weight : 1;
                }
            }
            if (total <= 0)
            {
                return false;
            }

            var point = UnityEngine.Random.value * total;
            AudioClipEntry selected = null;
            foreach (var entry in group.entries)
            {
                if (entry == null || !CanSelect(entry, externalProvider))
                {
                    continue;
                }

                var weight = weighted ? entry.weight : 1;
                if (weight <= 0)
                {
                    continue;
                }

                selected = entry;
                point -= weight;
                if (point < 0)
                {
                    break;
                }
            }
            if (selected == null)
            {
                return false;
            }

            if (selected.source == AudioClipSource.Clip)
            {
                selection = new AudioClipSelection(selected.clip, default);
            }
            else
            {
                IAudioClipProvider provider = externalProvider;
                if (selected.source == AudioClipSource.Resource)
                {
                    provider = ResourceSystem.Current.GetAudioClipProvider(selected.projectId);
                }

                selection = new AudioClipSelection(null, new AudioClipAddress(provider, selected.key));
            }
            return true;
        }

        /// <summary>判断条目能否定位有效候选；命名模式只检查定位前提，不加载 Clip。</summary>
        private static bool CanSelect(AudioClipEntry entry, IAudioClipProvider externalProvider)
        {
            if (entry.source == AudioClipSource.Clip)
            {
                return entry.clip;
            }

            if (string.IsNullOrWhiteSpace(entry.key))
            {
                return false;
            }

            if (entry.source == AudioClipSource.ExternalProvider)
            {
                return externalProvider != null;
            }

            if (entry.source != AudioClipSource.Resource || !string.IsNullOrEmpty(entry.bundleName))
            {
                return false;
            }

            var system = ResourceSystem.Current;
            if (system == null || !system.IsAvailable)
            {
                return false;
            }

            try
            {
                _ = new ResourceAddress(entry.projectId, entry.key);
                system.GetAudioClipProvider(entry.projectId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (ResourceException)
            {
                return false;
            }
        }
    }
}
