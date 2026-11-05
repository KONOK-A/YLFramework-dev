using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using YLFramework.Singletons;
using YLFramework.Events;
using YLFramework.Resource;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Localization
{
    /// <summary>显式配置登记与文本查询；只在Unity主线程使用，不扫描资产或自动创建宿主。</summary>
    public static class LocalizationManager
    {
        /// <summary>保存请求语言的 PlayerPrefs 键，存储值为 SystemLanguage 的整数值。</summary>
        public const string LanguagePreferenceKey = "YLFramework.Localization.Language";
        private static readonly List<LocalizationConfigHandle> _registrations = new();
        private static readonly HashSet<LocalizationConfigHandle> _owned = new();
        private static readonly EventChannel _changed = new();
        private static SystemLanguage _requested = SystemLanguage.Unknown;
        private static bool _hasRequested;
        private static bool _isClosing;
        /// <summary>语言设置、配置登记变化或显式刷新时的同步通知；监听者负责退订。</summary>
        public static IEventSource Changed => _changed;

        /// <summary>将本地化责任域接入会话重置、应用退出及编辑器退出运行状态的收尾通知。</summary>
        static LocalizationManager()
        {
            SingletonSessionState.RegisterSessionReset(Shutdown);
            Application.quitting += Shutdown;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                {
                    Shutdown();
                }
            };
#endif
        }

        /// <summary>按登记顺序返回当前句柄可借读的去重根快照，不转移根的资源所有权。</summary>
        public static IReadOnlyList<MultiLanguageConfigs> Configs
        {
            get
            {
                var result = new List<MultiLanguageConfigs>();
                foreach (var handle in _registrations)
                {
                    if (handle.Config && !result.Contains(handle.Config))
                    {
                        result.Add(handle.Config);
                    }
                }

                return result;
            }
        }

        /// <summary>汇总已登记语言及显示名的快照；同语言采用最先遇到的名称，空名称使用枚举名。</summary>
        public static IReadOnlyDictionary<SystemLanguage, string> Languages
        {
            get
            {
                var result = new Dictionary<SystemLanguage, string>();
                foreach (var root in Configs)
                {
                    foreach (var item in root.Languages)
                    {
                        if (item && !result.ContainsKey(item.language))
                        {
                            result.Add(item.language, string.IsNullOrEmpty(item.displayName) ? item.language.ToString() : item.displayName);
                        }
                    }
                }

                return result;
            }
        }

        /// <summary>用户请求的语言，首次读取有效偏好值，否则使用系统语言；不代表已有对应条目。</summary>
        public static SystemLanguage RequestedLanguage
        {
            get
            {
                if (!_hasRequested)
                {
                    var stored = PlayerPrefs.GetInt(LanguagePreferenceKey, -1);
                    _requested = stored >= 0 && Enum.IsDefined(typeof(SystemLanguage), stored) ? (SystemLanguage)stored : Application.systemLanguage;
                    _hasRequested = true;
                }
                return _requested;
            }
        }

        /// <summary>当前可用语言；设置保存请求语言，实际条目仍可逐项回退。</summary>
        /// <remarks>无已登记候选语言时读取为 Unknown；设置 Unknown 会删除偏好并重新采用系统语言。</remarks>
        public static SystemLanguage Language
        {
            get
            {
                var available = RegisteredLanguages();
                foreach (var candidate in LocalizationFallback.GetCandidates(RequestedLanguage, available))
                {
                    if (available.Contains(candidate))
                    {
                        return candidate;
                    }
                }

                return SystemLanguage.Unknown;
            }
            set
            {
                if (!Enum.IsDefined(typeof(SystemLanguage), value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                if (_hasRequested && _requested == value)
                {
                    return;
                }

                _requested = value;
                _hasRequested = value != SystemLanguage.Unknown;
                if (value == SystemLanguage.Unknown)
                {
                    PlayerPrefs.DeleteKey(LanguagePreferenceKey);
                }
                else
                {
                    PlayerPrefs.SetInt(LanguagePreferenceKey, (int)value);
                }

                PlayerPrefs.Save();
                Refresh();
            }
        }

        /// <summary>校验并登记外部已加载的根，返回独立登记句柄；外部仍负责资产保活。</summary>
        /// <param name="config">供借读的共享本地化根，不创建运行时副本。</param>
        /// <returns>由调用方释放的登记句柄。</returns>
        public static LocalizationConfigHandle AddConfig(MultiLanguageConfigs config)
        {
            EnsureOpen();
            if (!config)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var handle = new LocalizationConfigHandle();
            _owned.Add(handle);
            try
            {
                handle.Attach(config);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            return handle;
        }

        /// <summary>句柄拥有请求和根Lease；Dispose取消在途并移除自身登记，不影响共享根的其他owner。</summary>
        /// <param name="resources">负责取得根资产的资源系统，不由本地化管理器关闭。</param>
        /// <param name="address">本地化根在资源目录中的地址。</param>
        /// <returns>由调用方持有至使用结束的加载与登记句柄。</returns>
        public static LocalizationConfigHandle LoadConfigAsync(ResourceSystem resources, ResourceAddress address)
        {
            EnsureOpen();
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            var handle = new LocalizationConfigHandle();
            _owned.Add(handle);
            try
            {
                handle.Load(resources, address);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            return handle;
        }

        /// <summary>校验加入候选根后的完整配置集合，再登记句柄并同步通知显示刷新。</summary>
        internal static void Register(LocalizationConfigHandle handle)
        {
            EnsureOpen();
            var roots = new List<MultiLanguageConfigs>(Configs);
            if (!roots.Contains(handle.Config))
            {
                roots.Add(handle.Config);
            }

            Validate(roots);
            _registrations.Add(handle);
            Refresh();
        }

        /// <summary>移除句柄的持有记录；确实移除有效登记时通知显示刷新。</summary>
        internal static void Unregister(LocalizationConfigHandle handle)
        {
            _owned.Remove(handle);
            if (_registrations.Remove(handle))
            {
                Refresh();
            }
        }

        /// <summary>借读当前资产对象名匹配的第一个已登记根，未找到时返回空引用。</summary>
        /// <param name="name">根的资产对象名，不是显示名。</param>
        public static MultiLanguageConfigs GetConfig(string name)
        {
            foreach (var root in Configs)
            {
                if (root.name == name)
                {
                    return root;
                }
            }

            return null;
        }

        /// <summary>释放引用指定根的全部已登记句柄，由资源取得的根随各自租约归还。</summary>
        public static void RemoveConfig(MultiLanguageConfigs config)
        {
            foreach (var handle in _registrations.ToArray())
            {
                if (handle.Config == config)
                {
                    handle.Dispose();
                }
            }
        }

        /// <summary>按名称找到第一个根后释放该根的全部登记，不处理仍未完成的其他加载。</summary>
        public static void RemoveConfig(string name) => RemoveConfig(GetConfig(name));

        /// <summary>以请求语言为起点逐条回退查询，借读首个可用条目；全部缺失时返回空引用。</summary>
        public static LanguageInfo GetLanguageInfo(int id) => GetLanguageInfo(id, RequestedLanguage);

        /// <summary>按指定语言的候选顺序查询条目，所选语言缺项时继续回退，不限定条目类型。</summary>
        /// <param name="id">跨配置按语言校验唯一性的条目 ID。</param>
        /// <param name="language">本次查询的起始语言，不更改全局偏好。</param>
        /// <returns>首个可用条目的借读引用，全部候选均无可用项时为空引用。</returns>
        public static LanguageInfo GetLanguageInfo(int id, SystemLanguage language)
        {
            var roots = Configs;
            return LocalizationFallback.Find(language, RegisteredLanguages(), candidate =>
            {
                LanguageInfo found = null;
                foreach (var root in roots)
                {
                    var entry = root.GetLanguageConfigData(candidate)?.GetLanguageInfo(id);
                    if (entry == null)
                    {
                        continue;
                    }

                    if (found != null)
                    {
                        throw new InvalidOperationException($"Localization '{root.name}', language={candidate}: duplicate ID {id} across configs.");
                    }

                    found = entry;
                }
                return IsUsable(found) ? found : null;
            });
        }

        /// <summary>判断条目能否作为查询结果；文本允许空串，图片仅检查定位字段，不验证加载结果。</summary>
        private static bool IsUsable(LanguageInfo entry)
        {
            if (entry == null)
            {
                return false;
            }

            switch (entry.type)
            {
                case MultiLanguageType.String:
                    return entry.str != null;
                case MultiLanguageType.Image:
                    return entry.image != null && !string.IsNullOrWhiteSpace(entry.image.projectName) && !string.IsNullOrWhiteSpace(entry.image.imageName);
                case MultiLanguageType.NetworkImage:
                    return !string.IsNullOrWhiteSpace(entry.url);
                default:
                    return false;
            }
        }

        /// <summary>查询回退条目并按当前文化格式化文本；结果缺失或不是文本时警告并返回空串。</summary>
        /// <param name="id">文本条目的业务 ID。</param>
        /// <param name="args">格式参数；为空或无参数时直接返回原文本，不进行格式化。</param>
        /// <exception cref="FormatException">传入参数时文本格式不合法，异常包含语言、ID 与配置上下文。</exception>
        public static string GetString(int id, params object[] args)
        {
            var entry = GetLanguageInfo(id);
            if (entry == null || entry.type != MultiLanguageType.String)
            {
                Debug.LogWarning($"Localization language={RequestedLanguage}, ID={id}: missing text; configs={ConfigNames()}.");
                return string.Empty;
            }
            try
            {
                return args == null || args.Length == 0 ? entry.str : string.Format(CultureInfo.CurrentCulture, entry.str, args);
            }
            catch (FormatException error)
            {
                throw new FormatException($"Localization language={RequestedLanguage}, ID={id}, configs={ConfigNames()}: invalid text format.", error);
            }
        }

        /// <summary>校验根、语言资产、语言枚举及同语言 ID 唯一性，不验证图片资源或文本格式。</summary>
        /// <param name="roots">本次需要联合校验的配置根集合。</param>
        /// <exception cref="InvalidOperationException">配置引用、语言或条目索引不合法，或跨根 ID 冲突。</exception>
        public static void Validate(IEnumerable<MultiLanguageConfigs> roots)
        {
            var keys = new HashSet<(SystemLanguage, int)>();
            foreach (var root in roots)
            {
                if (!root)
                {
                    throw new InvalidOperationException("Localization: missing root config.");
                }

                if (root.Languages == null)
                {
                    throw new InvalidOperationException($"Localization '{root.name}': language list is null.");
                }

                foreach (var language in root.Languages)
                {
                    if (!language)
                    {
                        throw new InvalidOperationException($"Localization '{root.name}': missing language config.");
                    }

                    if (language.language == SystemLanguage.Unknown || !Enum.IsDefined(typeof(SystemLanguage), language.language))
                    {
                        throw new InvalidOperationException($"Localization '{root.name}/{language.name}': invalid language {language.language}.");
                    }

                    root.GetLanguageConfigData(language.language);
                    language.GetLanguageInfo(int.MinValue);
                    foreach (var entry in language.languagesInfo)
                    {
                        if (!keys.Add((language.language, entry.id)))
                        {
                            throw new InvalidOperationException($"Localization '{root.name}/{language.name}', language={language.language}: duplicate ID {entry.id} across configs.");
                        }
                    }
                }
            }
        }

        /// <summary>同步逐监听发布刷新通知并记录通知异常，不重新加载配置或切换物体活动状态。</summary>
        public static void Refresh()
        {
            try
            {
                _changed.TriggerAll();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        /// <summary>关闭本域，拒绝释放回调中的新登记/加载；所有旧owner均尝试归还，收尾清事件。</summary>
        /// <exception cref="AggregateException">原生retention归还失败，其他owner仍已逐一尝试释放。</exception>
        public static void Shutdown()
        {
            if (_isClosing)
            {
                return;
            }

            _isClosing = true;
            List<Exception> errors = null;
            try
            {
                _changed.ResetOwner();
                foreach (var handle in new List<LocalizationConfigHandle>(_owned))
                {
                    try
                    {
                        handle.Dispose();
                    }
                    catch (Exception error)
                    {
                        (errors ??= new List<Exception>()).Add(error);
                    }
                }
            }
            finally
            {
                _owned.Clear();
                _registrations.Clear();
                _changed.ResetOwner();
                _hasRequested = false;
                _requested = SystemLanguage.Unknown;
                _isClosing = false;
            }
            if (errors != null)
            {
                throw new AggregateException("Localization shutdown could not release every native retention cleanly.", errors);
            }
        }

        /// <summary>在收尾屏障期间拒绝新登记和加载，避免释放回调重新引入责任。</summary>
        private static void EnsureOpen()
        {
            if (_isClosing)
            {
                throw new InvalidOperationException("Localization is shutting down; new ownership is not accepted.");
            }
        }

        /// <summary>按根登记及语言列表顺序收集去重语言，供语言选择和条目回退使用。</summary>
        private static List<SystemLanguage> RegisteredLanguages()
        {
            var result = new List<SystemLanguage>();
            foreach (var root in Configs)
            {
                foreach (var item in root.Languages)
                {
                    if (item && !result.Contains(item.language))
                    {
                        result.Add(item.language);
                    }
                }
            }

            return result;
        }

        /// <summary>汇总当前可借读根的资产对象名，为查询与格式错误提供定位上下文。</summary>
        internal static string ConfigNames()
        {
            var names = new List<string>();
            foreach (var root in Configs)
            {
                names.Add(root.name);
            }

            return string.Join(", ", names);
        }
    }
}
