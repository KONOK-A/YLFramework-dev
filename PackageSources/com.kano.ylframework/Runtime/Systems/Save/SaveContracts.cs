using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace YLFramework.Save
{
    /// <summary>一个槽中的主模型；Id 与槽号一致，额外对象仍通过同一存储引擎保存。</summary>
    public interface ISaveModel
    {
        /// <summary>主模型所属的非负槽号；创建和加载时必须与实际槽号一致。</summary>
        int Id
        {
            get; set;
        }
    }

    /// <summary>文件读取的来源或失败分类，与是否命中对象缓存分别记录。</summary>
    public enum SaveReadStatus
    {
        /// <summary>成功读取主文件，或缓存保留的来源为主文件。</summary>
        Success,
        /// <summary>主文件缺失或内容无效，采用有效备份；未自动修复主文件。</summary>
        RecoveredFromBackup,
        /// <summary>主文件与备份均不存在。</summary>
        Missing,
        /// <summary>主备均未成功读取且不全为缺失；存储引擎会将此状态转为读取异常。</summary>
        Failed
    }

    /// <summary>读取来源随缓存保留；RecoveredFromBackup 仅表示采用备份值，未自动修盘。</summary>
    /// <typeparam name="T">读取的引用类型。</typeparam>
    public sealed class SaveReadResult<T> where T : class
    {
        /// <summary>读取到的原实例，主备均缺失时为 null；缓存命中不会复制对象。</summary>
        public T Value
        {
            get;
        }
        /// <summary>本次读取或缓存最初加载时保留的文件来源状态。</summary>
        public SaveReadStatus Status
        {
            get;
        }
        /// <summary>是否直接返回了存储引擎已持有的对象实例。</summary>
        public bool IsCached
        {
            get;
        }
        /// <summary>组合对象、来源与缓存标记，不执行文件读写或备份修复。</summary>
        internal SaveReadResult(T value, SaveReadStatus status, bool isCached = false)
        {
            Value = value;
            Status = status;
            IsCached = isCached;
        }
    }

    /// <summary>现有数据需要显式备份恢复，禁止直接写入覆盖时抛出的异常。</summary>
    public sealed class SaveRecoveryRequiredException : IOException
    {
        /// <summary>需要调用方显式恢复的主文件路径。</summary>
        public string FilePath
        {
            get;
        }
        /// <summary>记录被保护的文件路径与可选失败详情。</summary>
        /// <param name="path">需要恢复的主文件路径。</param>
        /// <param name="detail">可附加的读取失败原因；允许为 null。</param>
        public SaveRecoveryRequiredException(string path, string detail = null)
            : base("Explicit backup recovery is required before writing: " + path + " | " + detail)
        {
            FilePath = path;
        }
    }

    /// <summary>索引版本不等于当前格式时抛出，不隐式导入或转换历史格式。</summary>
    public sealed class SaveVersionException : IOException
    {
        /// <summary>从索引读取到的不受支持的版本号。</summary>
        public int Version
        {
            get;
        }
        /// <summary>记录实际版本，并在错误消息中给出当前期望版本。</summary>
        /// <param name="version">索引携带的实际版本号。</param>
        public SaveVersionException(int version) : base("Unsupported save index version " + version + ". Expected " + SaveStore.FormatVersion + ". Historical formats are not supported.")
        {
            Version = version;
        }
    }

    /// <summary>槽元数据与当前选择的 JSON 索引，使用固定字段名独立于对象序列化配置保存。</summary>
    internal sealed class SaveIndex
    {
        /// <summary>必须显式存在且等于当前引擎格式的索引版本。</summary>
        [JsonProperty(Required = Required.Always)] public int DataVersion = SaveStore.FormatVersion;
        /// <summary>以槽号为键的元数据表，键必须与对应条目的 SaveId 一致。</summary>
        [JsonProperty(Required = Required.Always)] public Dictionary<int, SaveManager.SaveItem> SaveItemDict = new Dictionary<int, SaveManager.SaveItem>();
        /// <summary>可空的当前槽号；JSON 保留既有 CurrentSaveID 键，非空时必须属于索引。</summary>
        [JsonProperty("CurrentSaveID")] public int? CurrentSaveId;

        /// <summary>复制索引容器但共享条目引用，供调用方替换待改条目后再提交。</summary>
        internal SaveIndex Copy()
        {
            return new SaveIndex
            {
                DataVersion = DataVersion,
                CurrentSaveId = CurrentSaveId,
                SaveItemDict = new Dictionary<int, SaveManager.SaveItem>(SaveItemDict)
            };
        }

        /// <summary>校验当前版本、槽身份与当前选择，不尝试修复或删除无效数据。</summary>
        internal void Validate()
        {
            if (DataVersion != SaveStore.FormatVersion)
            {
                throw new SaveVersionException(DataVersion);
            }

            if (SaveItemDict == null)
            {
                throw new InvalidDataException("SaveItemDict is null.");
            }

            foreach (var pair in SaveItemDict)
            {
                if (pair.Key < 0 || pair.Value == null || pair.Value.SaveId != pair.Key)
                {
                    throw new InvalidDataException("Invalid slot identity: " + pair.Key);
                }
            }

            if (CurrentSaveId.HasValue && !SaveItemDict.ContainsKey(CurrentSaveId.Value))
            {
                throw new InvalidDataException("The selected slot is not present in the index.");
            }
        }
    }

    /// <summary>统一存档入口的排序与槽元数据契约。</summary>
    public static partial class SaveManager
    {
        /// <summary>获取槽列表时使用的排序方式，名称排序以空字符串替代 null。</summary>
        public enum SaveOrder
        {
            /// <summary>保持引擎返回的当前枚举顺序，不保证按槽号排列。</summary>
            None,
            /// <summary>按创建时间从早到晚排列。</summary>
            CreateTime,
            /// <summary>按创建时间从晚到早排列。</summary>
            CreateTimeDescending,
            /// <summary>按最近保存时间从早到晚排列。</summary>
            SaveTime,
            /// <summary>按最近保存时间从晚到早排列。</summary>
            SaveTimeDescending,
            /// <summary>按槽号从小到大排列。</summary>
            SaveId,
            /// <summary>按槽号从大到小排列。</summary>
            SaveIdDescending,
            /// <summary>按显示名称升序排列，使用字符串默认比较规则。</summary>
            Name,
            /// <summary>按显示名称降序排列，使用字符串默认比较规则。</summary>
            NameDescending
        }

        /// <summary>槽元数据。直接修改仅作用于内存；持久化使用 RenameSaveItem / SetSaveItemNote。</summary>
        [Serializable, JsonObject(MemberSerialization.OptIn)]
        public class SaveItem
        {
            [JsonProperty("SaveID", Required = Required.Always)] private int _saveId;
            [JsonProperty("CreateTime", Required = Required.Always)] private DateTime _createTime;
            [JsonProperty("LastSaveTime", Required = Required.Always)] private DateTime _lastSaveTime;
            /// <summary>不可通过本条目修改的槽号；持久化仍使用既有 SaveID 键。</summary>
            public int SaveId => _saveId;
            /// <summary>创建条目时记录的时间，引擎新建槽时使用本地时间。</summary>
            public DateTime CreateTime => _createTime;
            /// <summary>内存中的最近保存时间；冷启动时还会用对象文件修改时间向后校准。</summary>
            public DateTime LastSaveTime => _lastSaveTime;
            /// <summary>可空显示名称；直接赋值只改内存，持久化应走管理器的重命名入口。</summary>
            [JsonProperty("DisplayName", NullValueHandling = NullValueHandling.Ignore)]
            public string DisplayName
            {
                get; set;
            }
            /// <summary>可空备注；直接赋值只改内存，持久化应走管理器的备注入口。</summary>
            [JsonProperty("Note", NullValueHandling = NullValueHandling.Ignore)]
            public string Note
            {
                get; set;
            }

            /// <summary>创建槽元数据，并将初始最近保存时间设为创建时间。</summary>
            /// <param name="saveId">槽号，合法性由索引或存储引擎校验。</param>
            /// <param name="createTime">条目创建时间。</param>
            [JsonConstructor]
            public SaveItem(int saveId, DateTime createTime)
            {
                _saveId = saveId;
                _createTime = createTime;
                _lastSaveTime = createTime;
            }
            /// <summary>更新本条目的内存保存时间并返回自身，不单独写入索引。</summary>
            /// <param name="lastSaveTime">要记录的保存时间。</param>
            public SaveItem UpdateSaveTime(DateTime lastSaveTime)
            {
                _lastSaveTime = lastSaveTime;
                return this;
            }
            /// <summary>更新本条目的内存显示名称并返回自身，不单独写入索引。</summary>
            /// <param name="displayName">新的显示名称，允许为 null。</param>
            public SaveItem Rename(string displayName)
            {
                DisplayName = displayName;
                return this;
            }
            /// <summary>更新本条目的内存备注并返回自身，不单独写入索引。</summary>
            /// <param name="note">新的备注，允许为 null。</param>
            public SaveItem SetNote(string note)
            {
                Note = note;
                return this;
            }
            /// <summary>复制元数据值，隔离提交索引前的条目修改。</summary>
            internal SaveItem Copy() => new SaveItem(SaveId, CreateTime) { _lastSaveTime = LastSaveTime, DisplayName = DisplayName, Note = Note };
        }
    }
}
