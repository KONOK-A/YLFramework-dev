using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace YLFramework.Editor.Help
{
    /// <summary>项目模块共用的只读帮助窗口，展示通过 <see cref="HelpRegistry"/> 注册的模块内容。</summary>
    /// <remarks>窗口只读取模块提供的展示数据，不执行示例代码；多个模块共用一个窗口实例，内容在切换到该模块时创建。</remarks>
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, sourceNamespace: "Project.HelpKit.Editor",
        sourceAssembly: "Project.HelpKit.Editor", sourceClassName: "HelpWindow")]
    public sealed class HelpWindow : EditorWindow
    {
        internal const string MenuPath = "YLFramework/帮助";
        private static readonly string[] _tabs = { "使用指南", "API 参考", "关于" };
        private static readonly string[] _memberGroups = { "构造函数", "属性", "事件", "字段与常量", "枚举值", "方法", "扩展方法" };
        [FormerlySerializedAs("mModuleId"), SerializeField] private string moduleId = "";
        [FormerlySerializedAs("mSearch"), SerializeField] private string search = "";
        [FormerlySerializedAs("mGuideId"), SerializeField] private string guideId = "";
        [FormerlySerializedAs("mApiId"), SerializeField] private string apiId = "";
        [FormerlySerializedAs("mAboutId"), SerializeField] private string aboutId = "";
        [FormerlySerializedAs("mMemberSearch"), SerializeField] private string memberSearch = "";
        [FormerlySerializedAs("mApiCategory"), SerializeField] private string apiCategory = "";
        [FormerlySerializedAs("mTab"), SerializeField] private int tab;
        [FormerlySerializedAs("mSidebarWidth"), SerializeField] private float sidebarWidth = 230;
        [FormerlySerializedAs("mCollapsedGroups"), SerializeField] private List<string> collapsedGroups = new List<string>();
        [FormerlySerializedAs("mInitialGroupsSet"), SerializeField] private bool hasInitializedGroups;
        private Vector2 _navScroll;
        private Vector2 _readerScroll;
        private HelpApiType[] _index = Array.Empty<HelpApiType>();
        private HelpApiType[] _visibleTypes = Array.Empty<HelpApiType>();
        private HelpPage[] _visibleTopics = Array.Empty<HelpPage>();
        private HelpPage[] _visibleAbout = Array.Empty<HelpPage>();
        private readonly Dictionary<string, HelpApiMember[]> _visibleMembers = new Dictionary<string, HelpApiMember[]>();
        private HelpRegistration[] _registrations = Array.Empty<HelpRegistration>();
        private HelpRegistration _registration;
        private HelpModule _module;
        private bool _needsReload;
        private string[] _shortNamespaces = Array.Empty<string>();
        private string _filterKey;
        private string _revealMemberKey;
        private string _memberFilterKey;
        private HelpApiMember[] _memberFiltered = Array.Empty<HelpApiMember>();
        [NonSerialized] private GUIStyle _title;
        [NonSerialized] private GUIStyle _headerTitle;
        [NonSerialized] private GUIStyle _heading;
        [NonSerialized] private GUIStyle _subheading;
        [NonSerialized] private GUIStyle _body;
        [NonSerialized] private GUIStyle _small;
        [NonSerialized] private GUIStyle _nav;
        [NonSerialized] private GUIStyle _selectedNav;
        [NonSerialized] private GUIStyle _link;
        [NonSerialized] private GUIStyle _code;
        [NonSerialized] private GUIStyle _signature;
        [NonSerialized] private GUIStyle _popup;
        [NonSerialized] private GUIStyle _tabButton;
        [NonSerialized] private GUIStyle _tabCurrent;
        [NonSerialized] private GUIStyle _searchField;
        [NonSerialized] private GUIStyle _button;
        [NonSerialized] private GUIStyle _navGroup;
        [NonSerialized] private GUIStyle _apiGroup;
        [NonSerialized] private GUIStyle _detailFoldout;
        [NonSerialized] private GUIStyle _notice;
        [NonSerialized] private GUIStyle _tag;
        [NonSerialized] private GUIStyle _sectionTitle;
        [NonSerialized] private GUIStyle _sectionFoldout;
        [NonSerialized] private GUIStyle _memberName;
        [NonSerialized] private GUIStyle _memberBrief;
        [NonSerialized] private GUIStyle _memberSummary;
        [NonSerialized] private GUIStyle _badge;
        [NonSerialized] private GUIStyle _chip;
        [NonSerialized] private GUIStyle _chipSelected;
        [NonSerialized] private GUIStyle _codeWrap;
        [NonSerialized] private GUIStyle _bodyClamp;
        [NonSerialized] private GUIStyle _smallLink;
        [NonSerialized] private GUIStyle _memberBox;
        [NonSerialized] private GUIStyle _memberBoxHover;
        [NonSerialized] private GUIStyle _memberBoxExpanded;
        [NonSerialized] private GUIStyle _memberDetailBox;
        [NonSerialized] private GUIStyle _detailHeading;
        private HelpPalette _palette;
        private Font _monoFont;
        private Texture2D _memberTexture;
        private Texture2D _memberHoverTexture;
        private Texture2D _memberExpandedTexture;
        private Texture2D _detailTexture;
        private string _hoverBlockKey;
        private bool _isDarkTheme;
        private float _readingWidth;
        private readonly HelpSearchHighlight _highlights = new HelpSearchHighlight();
        private readonly HashSet<string> _searchCollapsed = new HashSet<string>();
        private readonly HashSet<string> _detailExpanded = new HashSet<string>();
        private readonly HashSet<string> _apiSections = new HashSet<string>();
        private readonly HashSet<string> _apiSummaryExpanded = new HashSet<string>();
        private string _apiStateId;
        private string _overloadKey;
        private readonly Dictionary<string, Vector2> _codeScroll = new Dictionary<string, Vector2>();
        private readonly Stack<Navigation> _history = new Stack<Navigation>();
        private string _copiedKey;
        private double _copyUntil;
        private bool _revealInNav;
        private float _navViewportHeight;

        /// <summary>返回导航使用的位置快照，保存页签、选中项、全局搜索、目录折叠与滚动位置。</summary>
        private struct Navigation
        {
            internal int Tab;
            internal string Guide;
            internal string Api;
            internal string About;
            internal string Search;
            internal string[] Collapsed;
            internal string[] SearchCollapsed;
            internal Vector2 Reader;
            internal Vector2 Nav;
        }

        /// <summary>从公共菜单打开窗口，保留当前模块选择。</summary>
        [MenuItem(MenuPath)]
        private static void OpenMenu() => Open();

        /// <summary>打开公共窗口；可指定模块标识作为模块菜单的快捷入口。</summary>
        /// <param name="moduleId">已注册的模块标识；null 保留当前选择，未注册标识回退到第一个模块。</param>
        /// <returns>已显示的公共帮助窗口，重复打开时复用现有窗口。</returns>
        public static HelpWindow Open(string moduleId = null)
        {
            var window = GetWindow<HelpWindow>();
            if (moduleId != null)
            {
                window.SelectModule(moduleId);
            }

            window.minSize = new Vector2(440, 360);
            window.Show();
            return window;
        }

        /// <summary>恢复窗口入口状态并订阅注册变化，将模块内容留到加载流程重建。</summary>
        private void OnEnable()
        {
            titleContent = new GUIContent("项目帮助");
            minSize = new Vector2(440, 360);
            HelpRegistry.Changed += RegistryChanged;
            _registration = null;
            _needsReload = true;
        }

        /// <summary>将模块快照标记为过期并请求重绘，不在注册通知栈中建立内容。</summary>
        private void RegistryChanged()
        {
            _needsReload = true;
            Repaint();
        }

        /// <summary>切换当前模块；与当前模块不同时清空阅读状态与返回历史。</summary>
        /// <remarks>未注册的标识由加载流程回退到第一个已注册模块。</remarks>
        internal void SelectModule(string id)
        {
            if (moduleId != id)
            {
                ResetReading();
            }

            moduleId = id;
            _needsReload = true;
            LoadModule();
            Repaint();
        }

        /// <summary>清除模块相关的搜索、选中项、展开状态、滚动缓存和返回历史。</summary>
        private void ResetReading()
        {
            search = guideId = apiId = aboutId = "";
            tab = 0;
            _navScroll = _readerScroll = Vector2.zero;
            collapsedGroups.Clear();
            hasInitializedGroups = false;
            _searchCollapsed.Clear();
            _detailExpanded.Clear();
            _codeScroll.Clear();
            _history.Clear();
            memberSearch = "";
            apiCategory = "";
            _apiStateId = null;
            _overloadKey = null;
            _apiSections.Clear();
            _apiSummaryExpanded.Clear();
            _visibleMembers.Clear();
            _revealMemberKey = null;
            _memberFilterKey = null;
            _copiedKey = null;
            _filterKey = null;
            _revealInNav = true;
            _highlights.Clear();
        }

        /// <summary>按注册快照惰性加载选中模块及 API 索引；同一注册项复用内容，新注册项清除旧缓存。</summary>
        private void LoadModule()
        {
            if (!_needsReload)
            {
                return;
            }

            _needsReload = false;
            _registrations = HelpRegistry.GetModules();
            // 请求的模块未注册时回退到第一个模块；注册表为空时没有可加载的内容。
            var next = _registrations.FirstOrDefault(module => module.Id == moduleId) ?? _registrations.FirstOrDefault();
            // 注册项未变且内容已加载时直接复用，避免重复创建模块。
            if (next == _registration && (next != null || _module == null))
            {
                return;
            }
            // 实际选中的模块与请求标识不同（首次加载或回退）时按切换模块重置阅读状态。
            if (next?.Id != moduleId)
            {
                ResetReading();
            }

            _registration = next;
            moduleId = next?.Id ?? "";
            _module = next?.Create();
            _index = _module == null ? Array.Empty<HelpApiType>() : HelpApiIndex.Build(_module);
            _shortNamespaces = _index.Select(entry => entry.Type.Namespace).Where(name => !string.IsNullOrEmpty(name)).Distinct().ToArray();
            titleContent = new GUIContent(next == null ? "项目帮助" : next.DisplayName + " 帮助");
            _filterKey = null;
            _history.Clear();
            _detailExpanded.Clear();
            _codeScroll.Clear();
            _searchCollapsed.Clear();
            _copiedKey = null;
            memberSearch = "";
            apiCategory = "";
            _apiStateId = null;
            _overloadKey = null;
            _visibleMembers.Clear();
            _revealMemberKey = null;
            _memberFilterKey = null;
            _apiSections.Clear();
            _apiSummaryExpanded.Clear();
            _highlights.Clear();
            tab = Mathf.Clamp(tab, 0, 2);
            if (!hasInitializedGroups)
            {
                collapsedGroups.AddRange((_module?.CollapsedGuideGroups ?? Array.Empty<string>()).Select(group => "guide:" + group));
                collapsedGroups.AddRange((_module?.CollapsedApiGroups ?? Array.Empty<string>()).Select(group => "api:" + group));
                hasInitializedGroups = true;
            }
        }

        /// <summary>退订注册变化，销毁窗口自有字体与纹理，并丢弃依赖它们的样式和高亮缓存。</summary>
        private void OnDisable()
        {
            HelpRegistry.Changed -= RegistryChanged;
            // 动态字体与成员块纹理由当前窗口创建和销毁；GUIStyle 是窗口副本，不修改 EditorStyles 的共享样式。
            if (_monoFont)
            {
                DestroyImmediate(_monoFont);
            }

            _monoFont = null;
            if (_memberTexture)
            {
                DestroyImmediate(_memberTexture);
            }

            if (_memberHoverTexture)
            {
                DestroyImmediate(_memberHoverTexture);
            }

            if (_memberExpandedTexture)
            {
                DestroyImmediate(_memberExpandedTexture);
            }

            if (_detailTexture)
            {
                DestroyImmediate(_detailTexture);
            }

            _memberTexture = _memberHoverTexture = _memberExpandedTexture = _detailTexture = null;
            _body = null;
            _tabCurrent = null;
            _tag = null;
            _palette = null;
            _highlights.Clear();
        }

        /// <summary>在复制提示有效期间驱动重绘，到期后清除按钮反馈。</summary>
        private void OnInspectorUpdate()
        {
            if (_copiedKey == null)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup >= _copyUntil)
            {
                _copiedKey = null;
            }

            Repaint();
        }

        /// <summary>在样式缺失或皮肤变化时重建窗口样式副本及自有背景纹理。</summary>
        private void EnsureStyles()
        {
            var dark = EditorGUIUtility.isProSkin;
            // 样式字段是 NonSerialized：域重载后会变 null 需要重建；皮肤切换时配色也要更新。
            if (_body != null && _headerTitle != null && _notice != null && _isDarkTheme == dark)
            {
                return;
            }

            _isDarkTheme = dark;
            _palette = new HelpPalette(dark);
            if (!_monoFont)
            {
                _monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Microsoft YaHei UI" }, 13);
            }

            _body = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                wordWrap = true,
                richText = false,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 3, 6)
            };
            _body.normal.textColor = dark ? new Color(0.86f, 0.87f, 0.88f) : new Color(0.17f, 0.18f, 0.20f);
            _title = new GUIStyle(_body) { fontSize = 24, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 3, 10) };
            _title.normal.textColor = dark ? new Color(0.96f, 0.97f, 0.98f) : new Color(0.10f, 0.11f, 0.13f);
            _headerTitle = new GUIStyle(_title)
            {
                fontSize = 20,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0)
            };
            _heading = new GUIStyle(_body) { fontSize = 18, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 20, 8) };
            _heading.normal.textColor = dark ? new Color(0.92f, 0.93f, 0.95f) : new Color(0.12f, 0.13f, 0.15f);
            _subheading = new GUIStyle(_body) { fontSize = 15, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 12, 5) };
            _subheading.normal.textColor = dark ? new Color(0.88f, 0.89f, 0.91f) : new Color(0.15f, 0.16f, 0.18f);
            _small = new GUIStyle(_body) { fontSize = 12, padding = new RectOffset(0, 0, 1, 3) };
            _small.normal.textColor = _palette.Muted;
            _nav = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 13,
                richText = false,
                padding = new RectOffset(22, 10, 0, 0),
                clipping = TextClipping.Clip,
                wordWrap = false
            };
            _nav.normal.textColor = dark ? new Color(0.82f, 0.83f, 0.85f) : new Color(0.22f, 0.23f, 0.25f);
            _selectedNav = new GUIStyle(_nav) { fontStyle = FontStyle.Bold };
            _selectedNav.normal.textColor = dark ? Color.white : new Color(0.06f, 0.13f, 0.20f);
            _link = new GUIStyle(EditorStyles.linkLabel) { wordWrap = true, richText = false, fontSize = 14, alignment = TextAnchor.MiddleLeft };
            _code = new GUIStyle(EditorStyles.label)
            {
                font = _monoFont,
                fontSize = 13,
                wordWrap = false,
                richText = false,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0)
            };
            _code.normal.textColor = dark ? new Color(0.88f, 0.89f, 0.90f) : new Color(0.12f, 0.14f, 0.16f);
            _signature = new GUIStyle(_body) { font = _monoFont, fontSize = 13, padding = new RectOffset(0, 0, 2, 4) };
            _signature.normal.textColor = dark ? new Color(0.84f, 0.88f, 0.94f) : new Color(0.12f, 0.20f, 0.32f);
            _navGroup = new GUIStyle(EditorStyles.foldout)
            {
                fontSize = 13,
                richText = false,
                fixedHeight = 0,
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold
            };
            _apiGroup = new GUIStyle(_navGroup) { fontSize = 18, fontStyle = FontStyle.Bold };
            _detailFoldout = new GUIStyle(_navGroup) { fontSize = 15, fontStyle = FontStyle.Bold };
            _popup = new GUIStyle(EditorStyles.popup)
            {
                fixedHeight = 0,
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 20, 4, 4)
            };
            // label 基础样式不自带 Toolbar 皮肤变体查找，纯自绘背景；名称保持唯一避免命中皮肤样式。
            _tabButton = new GUIStyle(EditorStyles.label)
            {
                fixedHeight = 0,
                fixedWidth = 0,
                fontSize = 13,
                name = "HelpKitTabButton",
                alignment = TextAnchor.MiddleCenter,
                richText = false,
                wordWrap = false,
                padding = new RectOffset(6, 6, 4, 4)
            };
            _tabButton.normal.textColor = dark ? new Color(0.72f, 0.73f, 0.75f) : new Color(0.35f, 0.36f, 0.38f);
            _tabButton.hover.textColor = dark ? new Color(0.90f, 0.91f, 0.93f) : new Color(0.16f, 0.17f, 0.20f);
            _tabCurrent = new GUIStyle(_tabButton) { fontStyle = FontStyle.Bold };
            _tabCurrent.normal.textColor = dark ? new Color(0.97f, 0.98f, 1f) : new Color(0.09f, 0.15f, 0.23f);
            _searchField = new GUIStyle(EditorStyles.toolbarSearchField)
            {
                fixedHeight = 0,
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(Mathf.Max(20, EditorStyles.toolbarSearchField.padding.left), 6, 4, 4)
            };
            _button = new GUIStyle(GUI.skin.button)
            {
                fixedHeight = 0,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 4, 4)
            };
            _notice = new GUIStyle(EditorStyles.helpBox) { fixedHeight = 0, richText = false, padding = new RectOffset(14, 12, 10, 10) };
            _tag = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(7, 7, 1, 2) };
            _tag.normal.textColor = dark ? new Color(0.72f, 0.75f, 0.79f) : new Color(0.35f, 0.38f, 0.42f);
            _sectionTitle = new GUIStyle(_body) { fontSize = 16, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 6, 4) };
            _sectionTitle.normal.textColor = dark ? new Color(0.90f, 0.91f, 0.93f) : new Color(0.14f, 0.15f, 0.17f);
            _sectionFoldout = new GUIStyle(EditorStyles.foldout)
            {
                fixedHeight = 0,
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                richText = false
            };
            _sectionFoldout.normal.textColor = dark ? new Color(0.88f, 0.89f, 0.92f) : new Color(0.16f, 0.17f, 0.20f);
            _memberName = new GUIStyle(EditorStyles.label)
            {
                font = _monoFont,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                richText = false,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0)
            };
            _memberName.normal.textColor = dark ? new Color(0.93f, 0.94f, 0.96f) : new Color(0.10f, 0.12f, 0.15f);
            _memberBrief = new GUIStyle(_memberName) { fontStyle = FontStyle.Normal, fontSize = 13 };
            _memberBrief.normal.textColor = _palette.Muted;
            _memberSummary = new GUIStyle(_body)
            {
                fontSize = 13,
                wordWrap = false,
                richText = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 0, 0, 0)
            };
            _memberSummary.normal.textColor = dark ? new Color(0.66f, 0.68f, 0.71f) : new Color(0.38f, 0.40f, 0.44f);
            _badge = new GUIStyle(_small)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                richText = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(6, 6, 1, 1)
            };
            _badge.normal.textColor = dark ? new Color(0.74f, 0.76f, 0.80f) : new Color(0.36f, 0.39f, 0.44f);
            _chip = new GUIStyle(EditorStyles.label)
            {
                fixedHeight = 0,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(8, 8, 3, 3)
            };
            _chip.normal.textColor = dark ? new Color(0.80f, 0.81f, 0.84f) : new Color(0.22f, 0.24f, 0.27f);
            _chip.hover.textColor = dark ? new Color(0.94f, 0.95f, 0.97f) : new Color(0.10f, 0.12f, 0.16f);
            _chipSelected = new GUIStyle(_chip) { fontStyle = FontStyle.Bold };
            _chipSelected.normal.textColor = dark ? Color.white : new Color(0.06f, 0.13f, 0.20f);
            _codeWrap = new GUIStyle(_code) { wordWrap = true };
            _bodyClamp = new GUIStyle(_body) { clipping = TextClipping.Clip };
            _smallLink = new GUIStyle(EditorStyles.linkLabel) { fontSize = 13, wordWrap = false, richText = false };
            // 成员信息块：3 个状态各一张九宫格圆角纹理（底色、细边框、左侧强调线）。
            if (_memberTexture)
            {
                DestroyImmediate(_memberTexture);
            }

            if (_memberHoverTexture)
            {
                DestroyImmediate(_memberHoverTexture);
            }

            if (_memberExpandedTexture)
            {
                DestroyImmediate(_memberExpandedTexture);
            }

            if (_detailTexture)
            {
                DestroyImmediate(_detailTexture);
            }

            const int BoxSize = 12, BoxRadius = 4, BoxSlice = 4;
            _memberTexture = HelpTexture.RoundedBox(BoxSize, BoxRadius, _palette.MemberBg, _palette.MemberBorder, _palette.MemberAccent);
            _memberHoverTexture = HelpTexture.RoundedBox(BoxSize, BoxRadius, _palette.MemberHoverBg, _palette.MemberHoverBorder, _palette.MemberHoverAccent);
            _memberExpandedTexture = HelpTexture.RoundedBox(BoxSize, BoxRadius, _palette.MemberExpandedBg, _palette.MemberExpandedBorder, _palette.MemberExpandedAccent);
            _memberBox = new GUIStyle { padding = new RectOffset(12, 12, 8, 10), border = new RectOffset(BoxSlice, BoxSlice, BoxSlice, BoxSlice) };
            _memberBox.normal.background = _memberTexture;
            _memberBoxHover = new GUIStyle(_memberBox);
            _memberBoxHover.normal.background = _memberHoverTexture;
            _memberBoxExpanded = new GUIStyle(_memberBox);
            _memberBoxExpanded.normal.background = _memberExpandedTexture;
            // 详情内嵌区：无强调线的圆角浅底层，与外层成员块形成两级层次。
            _detailTexture = HelpTexture.RoundedBox(BoxSize, BoxRadius, _palette.DetailInsetBg, _palette.DetailInsetBorder);
            _memberDetailBox = new GUIStyle { padding = new RectOffset(14, 12, 11, 12), border = new RectOffset(BoxSlice, BoxSlice, BoxSlice, BoxSlice) };
            _memberDetailBox.normal.background = _detailTexture;
            _detailHeading = new GUIStyle(_body) { fontSize = 14, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 12, 4) };
            _detailHeading.normal.textColor = dark ? new Color(0.90f, 0.91f, 0.94f) : new Color(0.14f, 0.15f, 0.18f);
            _highlights.Clear();
        }

        /// <summary>准备窗口数据并组织标题、目录与阅读区域，按当前 IMGUI 事件处理交互。</summary>
        private void OnGUI()
        {
            EnsureStyles();
            LoadModule();
            // 悬停态（页签、目录行、分隔条）需要鼠标移动时重绘。
            if (Event.current.type == EventType.MouseMove)
            {
                Repaint();
            }

            var header = new HelpHeaderLayout(position.width);
            if (position.width < 360 || position.height < header.BodyY + 36)
            {
                GUI.Label(new Rect(6, 6, Mathf.Max(0, position.width - 12), Mathf.Max(0, position.height - 12)),
                    "窗口较小，请放大或调整停靠布局。", _body);
                return;
            }
            if (Event.current.type == EventType.KeyDown && Event.current.alt && Event.current.keyCode == KeyCode.LeftArrow && _history.Count != 0)
            {
                GoBack();
            }

            DrawHeader(header);
            UpdateResults();
            var body = new Rect(0, header.BodyY, position.width, Mathf.Max(0, position.height - header.BodyY));
            sidebarWidth = ClampSidebar(sidebarWidth, body.width);
            var nav = new Rect(body.x, body.y, sidebarWidth, body.height);
            var splitter = new Rect(nav.xMax, body.y, 6, body.height);
            var reading = new Rect(splitter.xMax, body.y, Mathf.Max(0, body.width - splitter.xMax), body.height);
            DrawSplitter(splitter, body.width);
            DrawNavigation(nav);
            DrawReader(reading);
        }

        /// <summary>限制侧栏宽度：下限 132；窗口够宽时为正文区保留至少 240 像素。</summary>
        internal static float ClampSidebar(float width, float windowWidth) => Mathf.Clamp(width, 132, Mathf.Max(132, windowWidth - 240));

        /// <summary>绘制模块选择、返回和全局搜索入口；条件变化时终止旧布局事件以待重绘。</summary>
        private void DrawHeader(HelpHeaderLayout layout)
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, layout.BodyY), _palette.HeaderBg);
            HelpDraw.Rule(new Rect(0, layout.BodyY - 1, position.width, 1), _palette.HeaderRule);
            EditorGUI.DrawRect(new Rect(layout.Title.x - 8, layout.Title.y + 4, 3, layout.Title.height - 8), _palette.TabAccent);
            GUI.Label(layout.Title, "项目帮助", _headerTitle);
            using (new EditorGUI.DisabledScope(_registrations.Length == 0))
            {
                var selected = Array.FindIndex(_registrations, module => module.Id == moduleId);
                var next = EditorGUI.Popup(layout.Module, selected,
                    _registrations.Select(module => module.DisplayName).ToArray(), _popup);
                if (next >= 0 && next != selected)
                {
                    SelectModule(_registrations[next].Id);
                    GUIUtility.ExitGUI();
                }
            }
            using (new EditorGUI.DisabledScope(_history.Count == 0))
            {
                if (GUI.Button(layout.Back, new GUIContent("返回", "返回上一阅读位置（Alt+左箭头）"), _button))
                {
                    GoBack();
                }
            }

            DrawTabs(layout);
            GUI.SetNextControlName("ProjectHelpSearch");
            var search = GUI.TextField(layout.Search, this.search, _searchField);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(this.search)))
            {
                if (GUI.Button(layout.Clear, "清空", _button))
                {
                    search = "";
                }
            }

            if (search != this.search)
            {
                this.search = search;
                _searchCollapsed.Clear();
                _navScroll = _readerScroll = Vector2.zero;
                _filterKey = null;
                _revealInNav = true;
                _highlights.Clear();
                // 过滤条件变化会改变后续布局与控件数量，终止本次事件后重绘。
                Repaint();
                GUIUtility.ExitGUI();
            }
            var scope = tab == 0 ? "搜索全部指南：标题、分组与正文；标题命中优先。" :
                tab == 1 ? "搜索类型与成员：名称命中优先，支持 ActionKit.Sequence 限定名。" : "搜索关于信息、来源与说明。";
            GUI.Label(layout.Scope, new GUIContent(scope + " 空格分词同时匹配。"), _small);
        }

        /// <summary>绘制指南、API 与关于页签，通过统一导航记录切换前的位置。</summary>
        private void DrawTabs(HelpHeaderLayout layout)
        {
            EditorGUI.DrawRect(layout.Tabs, _palette.TabBarBg);
            for (var index = 1; index < _tabs.Length; index++)
            {
                var edge = layout.Tab(index);
                EditorGUI.DrawRect(new Rect(edge.x, edge.y + 7, 1, edge.height - 14), _palette.HeaderRule);
            }
            for (var index = 0; index < _tabs.Length; index++)
            {
                var rect = layout.Tab(index);
                var current = index == tab;
                var inner = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 3);
                if (current)
                {
                    EditorGUI.DrawRect(inner, _palette.TabSelectedBg);
                }
                else if (rect.Contains(Event.current.mousePosition))
                {
                    EditorGUI.DrawRect(inner, _palette.TabHoverBg);
                }

                if (current)
                {
                    EditorGUI.DrawRect(new Rect(rect.x + 3, rect.yMax - 3, rect.width - 6, 2), _palette.TabAccent);
                }

                if (GUI.Button(rect, _tabs[index], current ? _tabCurrent : _tabButton) && !current)
                {
                    Navigate(index, index == 0 ? guideId : index == 1 ? apiId : aboutId, false);
                }
            }
        }

        /// <summary>按过滤键更新页面、类型与成员结果，并修正已被过滤掉的当前选中项。</summary>
        private void UpdateResults()
        {
            // 只在模块、页签或搜索词变化时重建过滤结果，避免每次 OnGUI 都扫描全部条目。
            var key = moduleId + "\n" + tab + "\n" + search;
            if (_filterKey == key)
            {
                return;
            }

            _filterKey = key;
            _highlights.Clear();
            _highlights.SetQuery(search);
            var terms = HelpSearch.Tokenize(search);
            var guides = _module?.Guides ?? Array.Empty<HelpPage>();
            var about = _module?.About ?? Array.Empty<HelpPage>();
            if (terms.Length == 0)
            {
                _visibleTopics = guides.ToArray();
                _visibleTypes = _index.ToArray();
                _visibleAbout = about.ToArray();
                _visibleMembers.Clear();
            }
            else
            {
                _visibleTopics = Rank(guides, terms);
                _visibleTypes = Rank(_index, terms);
                _visibleAbout = Rank(about, terms);
                BuildVisibleMembers(terms);
            }
            if (tab == 0 && !_visibleTopics.Any(topic => topic.Id == guideId))
            {
                guideId = _visibleTopics.FirstOrDefault()?.Id ?? "";
                _revealInNav = true;
            }
            if (tab == 1 && !_visibleTypes.Any(type => type.Id == apiId))
            {
                apiId = _visibleTypes.FirstOrDefault()?.Id ?? "";
                _revealInNav = true;
            }
            if (tab == 2 && !_visibleAbout.Any(page => page.Id == aboutId))
            {
                aboutId = _visibleAbout.FirstOrDefault()?.Id ?? "";
                _revealInNav = true;
            }
            if (_revealInNav)
            {
                RevealSelectedGroup();
            }
        }

        /// <summary>按加权分数过滤页面；同分保持原有顺序，避免结果抖动。</summary>
        private static HelpPage[] Rank(IEnumerable<HelpPage> pages, string[] terms)
        {
            var fields = new List<HelpSearchField>();
            return pages.Select((page, index) => (page, index, score: PageScore(page, terms, fields)))
                .Where(item => item.score >= 0)
                .OrderByDescending(item => item.score).ThenBy(item => item.index)
                .Select(item => item.page).ToArray();
        }

        /// <summary>复用字段缓冲计算单页得分，避免把上一页的搜索字段带入当前评分。</summary>
        private static int PageScore(HelpPage page, string[] terms, List<HelpSearchField> fields)
        {
            fields.Clear();
            page.AppendSearchFields(fields);
            return HelpSearch.Score(fields, terms);
        }

        /// <summary>按加权分数过滤类型；类型字段与全部成员字段共同参与匹配。</summary>
        private static HelpApiType[] Rank(IEnumerable<HelpApiType> types, string[] terms)
        {
            var fields = new List<HelpSearchField>();
            return types.Select((type, index) => (type, index, score: TypeScore(type, terms, fields)))
                .Where(item => item.score >= 0)
                .OrderByDescending(item => item.score).ThenBy(item => item.index)
                .Select(item => item.type).ToArray();
        }

        /// <summary>复用字段缓冲计算类型与其成员的联合搜索得分。</summary>
        private static int TypeScore(HelpApiType type, string[] terms, List<HelpSearchField> fields)
        {
            fields.Clear();
            type.AppendSearchFields(fields, true);
            return HelpSearch.Score(fields, terms);
        }

        /// <summary>收集每个可见类型中命中剩余查询词的成员，供导航列出成员级结果。</summary>
        private void BuildVisibleMembers(string[] terms)
        {
            _visibleMembers.Clear();
            var fields = new List<HelpSearchField>();
            var context = new List<HelpSearchField>();
            var single = new string[1];
            foreach (var type in _visibleTypes)
            {
                // 类型名、限定名与声明能覆盖的词不再作为成员结果，避免纯类型查询刷出全部成员。
                context.Clear();
                type.AppendSearchFields(context, false, false);
                var memberTerms = new List<string>();
                foreach (var term in terms)
                {
                    single[0] = term;
                    if (HelpSearch.Score(context, single) < 0)
                    {
                        memberTerms.Add(term);
                    }
                }
                if (memberTerms.Count == 0)
                {
                    continue;
                }

                var remaining = memberTerms.ToArray();
                var matched = new List<(HelpApiMember Entry, int Score, int Index)>();
                for (var index = 0; index < type.Entries.Length; index++)
                {
                    fields.Clear();
                    type.Entries[index].AppendSearchFields(fields, type.Name);
                    var score = HelpSearch.Score(fields, remaining);
                    if (score >= 0)
                    {
                        matched.Add((type.Entries[index], score, index));
                    }
                }
                if (matched.Count == 0)
                {
                    continue;
                }

                _visibleMembers[type.Id] = matched.OrderByDescending(item => item.Score).ThenBy(item => item.Index)
                    .Select(item => item.Entry).ToArray();
            }
        }

        /// <summary>按当前页签分组展示过滤结果，搜索 API 时追加可跳转的成员行。</summary>
        private void DrawNavigation(Rect rect)
        {
            if (rect.width < 24 || rect.height < 24)
            {
                return;
            }

            _navViewportHeight = Mathf.Max(1, rect.height - 40);
            EditorGUI.DrawRect(rect, _palette.NavBg);
            GUILayout.BeginArea(new Rect(rect.x + 4, rect.y + 5, rect.width - 8, rect.height - 10));
            _navScroll = EditorGUILayout.BeginScrollView(_navScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            var count = tab == 0 ? _visibleTopics.Length : tab == 1 ? _visibleTypes.Length : _visibleAbout.Length;
            var searching = !string.IsNullOrWhiteSpace(search);
            var memberCount = 0;
            if (searching && tab == 1)
            {
                foreach (var pair in _visibleMembers)
                {
                    memberCount += pair.Value.Length;
                }
            }

            GUILayout.Label(!searching ? $"{count} 项" :
                memberCount > 0 ? $"找到 {count} 个类型、{memberCount} 个成员匹配" : $"找到 {count} 项匹配结果", _small);
            GUILayout.Space(2);
            HelpDraw.Rule(GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true)), _palette.NavRule);
            GUILayout.Space(5);
            if (tab == 0)
            {
                foreach (var group in _visibleTopics.Select(topic => topic.Group).Distinct())
                {
                    var topics = _visibleTopics.Where(topic => topic.Group == group).ToArray();
                    if (topics.Length == 0 || !DrawGroup("guide:" + group, group, topics.Length, _navGroup))
                    {
                        continue;
                    }

                    var stripe = 0;
                    foreach (var topic in topics)
                    {
                        DrawNavItem(topic.Id, topic.Title, topic.Summary, guideId, stripe++, searching ? "指南" : null);
                    }
                }
            }
            else if (tab == 1)
            {
                foreach (var group in _visibleTypes.Select(type => type.Group).Distinct())
                {
                    var types = _visibleTypes.Where(type => type.Group == group).ToArray();
                    if (!DrawGroup("api:" + group, group, types.Length, _navGroup))
                    {
                        continue;
                    }

                    var stripe = 0;
                    foreach (var type in types)
                    {
                        DrawNavItem(type.Id, type.Name, type.Summary, apiId, stripe++, searching ? "类型" : null);
                        if (!searching || !_visibleMembers.TryGetValue(type.Id, out var members))
                        {
                            continue;
                        }

                        foreach (var member in members)
                        {
                            var memberTitle = member.Name == ".ctor" ? type.Name + "()" : type.Name + "." + member.Name;
                            var memberType = type;
                            var memberEntry = member;
                            DrawNavItem(type.Id + "::" + member.Signature, memberTitle, member.Signature, "", stripe++, member.Group, 14f,
                                () => JumpToMember(memberType, memberEntry));
                        }
                    }
                }
            }
            else
            {
                var stripe = 0;
                foreach (var page in _visibleAbout)
                {
                    DrawNavItem(page.Id, page.Title, page.Summary, aboutId, stripe++, searching ? "关于" : null);
                }
            }
            if (count == 0)
            {
                GUILayout.Label("没有找到结果。可尝试类型名、方法名或中文功能词；清空搜索可恢复全部条目。", _body);
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>绘制目录或 API 分组标题，维护当前搜索上下文下的折叠状态并返回是否展开。</summary>
        private bool DrawGroup(string key, string title, int count, GUIStyle style)
        {
            // 搜索期间使用独立的折叠集合：搜索结果中的展开/折叠不会覆盖用户手动设置的组状态。
            var searching = !string.IsNullOrWhiteSpace(search);
            var open = searching ? !_searchCollapsed.Contains(key) : !collapsedGroups.Contains(key);
            GUILayout.Space(style == _apiGroup ? 14 : 6);
            var rect = GUILayoutUtility.GetRect(0, Mathf.Max(28, style.lineHeight + 6), GUILayout.ExpandWidth(true));
            if (style == _navGroup)
            {
                EditorGUI.DrawRect(rect, _palette.NavGroupBg);
            }

            var label = new GUIContent(title + "  " + count, title);
            if (style == _apiGroup)
            {
                DrawHighlight(EditorGUI.IndentedRect(rect), label.text, style);
            }

            var next = EditorGUI.Foldout(rect, open, label, true, style);
            if (style == _navGroup)
            {
                HelpDraw.Rule(new Rect(rect.x, rect.yMax - 1, rect.width, 1), _palette.NavGroupRule);
            }
            else
            {
                HelpDraw.Rule(new Rect(rect.x, rect.yMax - 1, rect.width, 1), _palette.Separator);
            }

            if (next != open)
            {
                if (searching)
                {
                    if (next)
                    {
                        _searchCollapsed.Remove(key);
                    }
                    else
                    {
                        _searchCollapsed.Add(key);
                    }
                }
                else
                {
                    if (next)
                    {
                        collapsedGroups.Remove(key);
                    }
                    else
                    {
                        collapsedGroups.Add(key);
                    }
                }
                Repaint();
                GUIUtility.ExitGUI();
            }
            return open;
        }

        /// <summary>绘制可点击目录行，处理选中定位；自定义点击用于成员跳转，否则导航到页面或类型。</summary>
        private void DrawNavItem(string id, string title, string summary, string selected, int stripe,
            string tag = null, float indent = 0f, Action click = null)
        {
            var rect = GUILayoutUtility.GetRect(0, 29, GUILayout.ExpandWidth(true));
            var current = id == selected;
            _palette.DrawStripe(rect, stripe);
            if (current)
            {
                EditorGUI.DrawRect(rect, _palette.NavSelected);
            }
            else if (rect.Contains(Event.current.mousePosition))
            {
                EditorGUI.DrawRect(rect, _palette.NavHover);
            }

            if (current)
            {
                EditorGUI.DrawRect(new Rect(rect.x, rect.y + 3, 3, rect.height - 6), _palette.NavSelectedAccent);
            }
            // 导航后把选中项滚入可视区域，只做一次；之后的滚动完全交给用户。
            if (current && _revealInNav && Event.current.type == EventType.Repaint)
            {
                if (rect.y < _navScroll.y)
                {
                    _navScroll.y = rect.y;
                }
                else if (rect.yMax > _navScroll.y + _navViewportHeight)
                {
                    _navScroll.y = rect.yMax - _navViewportHeight;
                }

                _revealInNav = false;
                Repaint();
            }
            // 结果来源标签复用导航行：整行可点，标签前缀在文本层，不改变行高与配色。
            var label = tag == null ? title : "[" + tag + "] " + title;
            var tooltip = title + "\n" + summary;
            if (GUI.Button(rect, new GUIContent("", tooltip), GUIStyle.none))
            {
                if (click != null)
                {
                    click();
                }
                else if (!current)
                {
                    Navigate(tab, id, false);
                }
            }
            GUI.Label(new Rect(rect.x + indent, rect.y, Mathf.Max(0, rect.width - indent), rect.height),
                new GUIContent(label, tooltip), current ? _selectedNav : _nav);
        }

        /// <summary>从搜索结果跳到具体成员：切换类型、展开该成员并滚动到它的信息块。</summary>
        private void JumpToMember(HelpApiType type, HelpApiMember member)
        {
            _history.Push(new Navigation
            {
                Tab = tab,
                Guide = guideId,
                Api = apiId,
                About = aboutId,
                Search = search,
                Nav = _navScroll,
                Reader = _readerScroll,
                Collapsed = collapsedGroups.ToArray(),
                SearchCollapsed = _searchCollapsed.ToArray()
            });
            tab = 1;
            apiId = type.Id;
            _apiStateId = type.Id;
            memberSearch = "";
            apiCategory = "";
            _overloadKey = type.Id + "\n#" + member.Name;
            var key = type.Id + "\n" + member.Signature;
            _detailExpanded.Add(key);
            _revealMemberKey = key;
            _readerScroll = Vector2.zero;
            _filterKey = null;
            _revealInNav = true;
            _highlights.Clear();
            Repaint();
            GUIUtility.ExitGUI();
        }

        /// <summary>以 hotControl 持有一次分隔条拖动，在正文宽度约束内更新侧栏宽度。</summary>
        private void DrawSplitter(Rect rect, float windowWidth)
        {
            var hovered = rect.Contains(Event.current.mousePosition);
            EditorGUI.DrawRect(new Rect(rect.x + 2, rect.y, 1, rect.height), hovered ? _palette.TabAccent : _palette.Separator);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
            var id = GUIUtility.GetControlID("ProjectHelpSplitter".GetHashCode(), FocusType.Passive);
            var evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                GUIUtility.hotControl = id;
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && GUIUtility.hotControl == id)
            {
                sidebarWidth = ClampSidebar(evt.mousePosition.x, windowWidth);
                evt.Use();
                Repaint();
                GUIUtility.ExitGUI();
            }
            else if (evt.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                evt.Use();
            }
        }

        /// <summary>在阅读滚动区展示当前页面或类型，并在可用宽度变化时失效高亮几何。</summary>
        private void DrawReader(Rect rect)
        {
            if (rect.width < 48 || rect.height < 24)
            {
                return;
            }

            var width = Mathf.Max(80, rect.width - 44);
            // 阅读宽度变化后字符的像素位置失效，丢弃高亮几何缓存。
            if (_readingWidth != width)
            {
                _readingWidth = width;
                _highlights.ClearGeometry();
            }
            GUILayout.BeginArea(new Rect(rect.x + 16, rect.y + 6, rect.width - 22, rect.height - 12));
            _readerScroll = EditorGUILayout.BeginScrollView(_readerScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            if (tab == 0)
            {
                var topic = _visibleTopics.FirstOrDefault(entry => entry.Id == guideId);
                if (topic != null)
                {
                    DrawPage(topic);
                }
                else
                {
                    DrawEmpty();
                }
            }
            else if (tab == 1)
            {
                var type = _visibleTypes.FirstOrDefault(entry => entry.Id == apiId);
                if (type != null)
                {
                    DrawType(type);
                }
                else
                {
                    DrawEmpty();
                }
            }
            else
            {
                var page = _visibleAbout.FirstOrDefault(entry => entry.Id == aboutId);
                if (page != null)
                {
                    DrawPage(page);
                }
                else
                {
                    DrawEmpty();
                }
            }
            GUILayout.Space(20);
            if (tab != 2 && _module != null && _module.About.Length != 0 && DrawLink("关于", "查看模块信息", false))
            {
                Navigate(2, _module.About[0].Id, true);
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>分别提示尚无模块、缺少关于页或当前搜索无匹配，不编造替代内容。</summary>
        private void DrawEmpty()
        {
            if (_module == null)
            {
                DrawText("暂无已注册的帮助模块", _title, false);
                DrawText("模块提供方注册内容后即可在这里查阅。", _body, false);
                return;
            }
            if (tab == 2 && _module.About.Length == 0)
            {
                DrawText("该模块未提供关于信息", _title, false);
                return;
            }
            DrawText("没有匹配内容", _title, false);
            DrawText("可清空搜索，或减少关键词。搜索按名称、说明与正文加权，空格分词且所有词都需要匹配。", _body, false);
        }

        /// <summary>按内容块顺序绘制静态页面与精选 API；链接只在用户点击后打开。</summary>
        private void DrawPage(HelpPage page)
        {
            DrawText(tab == 0 ? "使用指南  ›  " + page.Group : "关于", _small);
            DrawText(page.Title, _title);
            DrawText(page.Summary, _body);
            if (!string.IsNullOrWhiteSpace(page.Summary))
            {
                GUILayout.Space(4);
            }

            for (var i = 0; i < page.Blocks.Length; i++)
            {
                var block = page.Blocks[i];
                if (block.Kind == HelpBlockKind.Link)
                {
                    if (DrawLink(block.Title, block.Url))
                    {
                        Application.OpenURL(block.Url);
                    }

                    GUILayout.Space(4);
                    continue;
                }
                DrawText(block.Title, _heading);
                if (block.Kind == HelpBlockKind.Code)
                {
                    DrawText(block.Context, _body);
                    if (block.Content.Length != 0)
                    {
                        DrawCode(page.Id + ":" + i, block.Content);
                    }
                }
                else if (block.Kind == HelpBlockKind.Notice)
                {
                    DrawNotice(block.Content);
                }
                else
                {
                    DrawText(block.Content, _body);
                    GUILayout.Space(6);
                }
            }
            var related = _index.Where(entry => page.FeaturedTypes.Contains(entry.Type)).ToArray();
            if (related.Length == 0)
            {
                return;
            }

            DrawText("相关 API", _heading);
            foreach (var type in related)
            {
                if (DrawLink(type.Name, type.Summary))
                {
                    Navigate(1, type.Id, true);
                }
            }
        }

        /// <summary>组织类型声明、真实说明、继承入口和成员浏览器，切换类型时重置局部筛选。</summary>
        private void DrawType(HelpApiType type)
        {
            // 切换类型时只重置该类型浏览器的筛选状态；展开状态按类型/成员标识保留。
            if (_apiStateId != type.Id)
            {
                _apiStateId = type.Id;
                apiCategory = "";
                memberSearch = "";
                _overloadKey = null;
            }
            DrawText("API 参考  ›  " + type.Group, _small);
            DrawText(type.Name, _title);
            DrawText("命名空间：" + (type.Type.Namespace ?? "（全局）"), _small);
            DrawTypeSummary(type);
            DrawCompactCode(type.Id + ":declaration", type.Declaration);
            DrawInheritance(type);
            DrawTypeNotes(type);
            DrawMemberBrowser(type);
            DrawRelatedGuides(type);
        }

        /// <summary>类型摘要：超过三行时折叠显示，展开状态按类型标识保存。</summary>
        private void DrawTypeSummary(HelpApiType type)
        {
            if (string.IsNullOrWhiteSpace(type.Summary))
            {
                DrawText("未提供摘要说明。", _small, false);
                return;
            }
            var limit = _body.lineHeight * 3;
            if (_body.CalcHeight(new GUIContent(type.Summary), _readingWidth) <= limit + 1f)
            {
                DrawText(type.Summary, _body);
                GUILayout.Space(2);
                return;
            }
            if (_apiSummaryExpanded.Contains(type.Id))
            {
                DrawText(type.Summary, _body);
                if (DrawSmallLink("收起说明"))
                {
                    _apiSummaryExpanded.Remove(type.Id);
                    Repaint();
                    GUIUtility.ExitGUI();
                }
                return;
            }
            var rect = GUILayoutUtility.GetRect(_readingWidth, limit, GUILayout.Width(_readingWidth));
            DrawHighlight(rect, type.Summary, _bodyClamp);
            EditorGUI.SelectableLabel(rect, type.Summary, _bodyClamp);
            if (DrawSmallLink("展开全部说明"))
            {
                _apiSummaryExpanded.Add(type.Id);
                Repaint();
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>紧凑只读代码条：高度随内容换行，不嵌套滚动条，保留复制。</summary>
        private void DrawCompactCode(string key, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var inner = Mathf.Max(40, _readingWidth - 20);
            var textHeight = Mathf.Max(_codeWrap.lineHeight, _codeWrap.CalcHeight(new GUIContent(text), inner));
            var block = GUILayoutUtility.GetRect(_readingWidth, textHeight + 44, GUILayout.Width(_readingWidth));
            EditorGUI.DrawRect(block, _palette.CodeBg);
            HelpDraw.Outline(block, _palette.CodeBorder);
            var header = new Rect(block.x + 1, block.y + 1, block.width - 2, 24);
            EditorGUI.DrawRect(header, _palette.CodeHeaderBg);
            HelpDraw.Rule(new Rect(header.x, header.yMax, header.width, 1), _palette.CodeBorder);
            var tag = new GUIContent("C# · 只读");
            var tagSize = _tag.CalcSize(tag);
            var tagRect = new Rect(block.x + 10, block.y + 3, tagSize.x + 10, 20);
            EditorGUI.DrawRect(tagRect, _palette.CodeTagBg);
            GUI.Label(tagRect, tag, _tag);
            var copied = _copiedKey == key && EditorApplication.timeSinceStartup < _copyUntil;
            if (GUI.Button(new Rect(block.xMax - 66, block.y + 2, 56, 22), copied ? "已复制" : "复制", _button))
            {
                EditorGUIUtility.systemCopyBuffer = text;
                _copiedKey = key;
                _copyUntil = EditorApplication.timeSinceStartup + 1.6;
                ShowNotification(new GUIContent("已复制到剪贴板"));
                Repaint();
            }
            var view = new Rect(block.x + 10, header.yMax + 8, inner, textHeight);
            DrawHighlight(view, text, _codeWrap);
            EditorGUI.SelectableLabel(view, text, _codeWrap);
            GUILayout.Space(10);
        }

        /// <summary>可折叠的信息区；默认折叠，展开状态按稳定键保存。</summary>
        private bool DrawSection(string key, string title, int count)
        {
            var open = _apiSections.Contains(key);
            GUILayout.Space(12);
            var rect = GUILayoutUtility.GetRect(0, Mathf.Max(26, _sectionFoldout.lineHeight + 6), GUILayout.ExpandWidth(true));
            var label = count > 0 ? title + "  " + count : title;
            var next = EditorGUI.Foldout(rect, open, new GUIContent(label, label), true, _sectionFoldout);
            HelpDraw.Rule(new Rect(rect.x, rect.yMax - 2, rect.width, 1), _palette.Separator);
            if (next != open)
            {
                if (next)
                {
                    _apiSections.Add(key);
                }
                else
                {
                    _apiSections.Remove(key);
                }

                Repaint();
                GUIUtility.ExitGUI();
            }
            return open;
        }

        /// <summary>展示基类与接口；只有已进入当前模块索引的类型才提供内部跳转。</summary>
        private void DrawInheritance(HelpApiType type)
        {
            // 排除 object、ValueType 和 Enum：它们是类、结构体与枚举的隐式基类型，列出只会干扰阅读。
            var bases = type.Type.GetInterfaces().Concat(type.Type.BaseType == null ? Type.EmptyTypes : new[] { type.Type.BaseType })
                .Where(contract => contract != typeof(object) && contract != typeof(ValueType) && contract != typeof(Enum)).Distinct().ToArray();
            if (bases.Length == 0 || !DrawSection(type.Id + ":bases", "继承与接口", bases.Length))
            {
                return;
            }

            DrawText("本页展示当前类型声明的成员；继承成员可从下面的基类与接口查阅。", _body);
            foreach (var contract in bases)
            {
                var definition = contract.IsGenericType ? contract.GetGenericTypeDefinition() : contract;
                var entry = _index.FirstOrDefault(item => item.Type == definition);
                var name = HelpApiFormatter.FormatType(contract, _shortNamespaces);
                if (entry == null)
                {
                    DrawText(name, _body);
                }
                else if (DrawLink(name, entry.Summary))
                {
                    Navigate(1, entry.Id, true);
                }
            }
        }

        /// <summary>在确有说明时展示可折叠的类型参数与备注区，不从声明推断文档。</summary>
        private void DrawTypeNotes(HelpApiType type)
        {
            var parameters = type.Help.TypeParameters;
            var remarks = type.Help.Remarks;
            if (parameters.Length == 0 && string.IsNullOrWhiteSpace(remarks))
            {
                return;
            }

            if (!DrawSection(type.Id + ":notes", "类型参数与备注", parameters.Length))
            {
                return;
            }

            if (parameters.Length != 0)
            {
                DrawText("类型参数", _subheading);
                foreach (var parameter in parameters)
                {
                    DrawParameterRow(_readingWidth, parameter.Name, "", parameter.Description);
                }

                GUILayout.Space(4);
            }
            if (!string.IsNullOrWhiteSpace(remarks))
            {
                DrawText("备注", _subheading);
                DrawText(remarks, _body);
                GUILayout.Space(6);
            }
        }

        /// <summary>将类型索引中已关联的页面列为指南导航入口。</summary>
        private void DrawRelatedGuides(HelpApiType type)
        {
            if (type.RelatedPages.Length == 0 || !DrawSection(type.Id + ":related", "相关指南", type.RelatedPages.Length))
            {
                return;
            }

            foreach (var topic in type.RelatedPages)
            {
                if (DrawLink(topic.Title, topic.Summary))
                {
                    Navigate(0, topic.Id, true);
                }
            }
        }

        /// <summary>绘制当前类型的局部搜索与分类，将筛选后的同名成员归为重载组。</summary>
        private void DrawMemberBrowser(HelpApiType type)
        {
            GUILayout.Space(18);
            DrawText("成员", _sectionTitle);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("ProjectHelpMemberSearch");
            var search = GUILayout.TextField(memberSearch, _searchField, GUILayout.MinWidth(60));
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(memberSearch)))
            {
                if (GUILayout.Button("清空", _button, GUILayout.Width(54), GUILayout.Height(26)))
                {
                    search = "";
                }
            }

            GUILayout.EndHorizontal();
            if (search != memberSearch)
            {
                // 过滤只改变可见行；已展开的成员详情按稳定键保留，重新可见时继续展开。
                memberSearch = search;
                Repaint();
                GUIUtility.ExitGUI();
            }
            DrawText("搜索范围：当前类型的成员；名称命中优先，其次说明与签名；空格分词同时匹配（不改动全局搜索范围）。", _small, false);
            var groups = _memberGroups.Where(group => type.Entries.Any(entry => entry.Group == group)).ToArray();
            DrawCategoryChips(type, groups);
            if (type.Entries.Length == 0)
            {
                DrawText("没有额外声明的成员；可从类型声明和相关接口了解其契约。", _body);
                return;
            }
            var entries = FilteredMembers(type);
            if (entries.Length == 0)
            {
                DrawText("没有匹配的成员。清空搜索或切换分类可恢复全部成员。", _body, false);
                return;
            }
            foreach (var overloads in entries.GroupBy(entry => entry.Name))
            {
                var items = overloads.ToArray();
                if (items.Length > 1)
                {
                    DrawOverloadGroup(type, overloads.Key, items);
                }
                else
                {
                    DrawMemberRow(type, items[0], 0f);
                }
            }
        }

        /// <summary>当前类型的成员筛选：分类过滤后按加权分数排序；同分保持原顺序，空查询保持原顺序。</summary>
        private HelpApiMember[] FilteredMembers(HelpApiType type)
        {
            var filterKey = type.Id + "\n" + memberSearch + "\n" + apiCategory;
            if (_memberFilterKey == filterKey)
            {
                return _memberFiltered;
            }

            _memberFilterKey = filterKey;
            IEnumerable<HelpApiMember> candidates = type.Entries;
            if (!string.IsNullOrEmpty(apiCategory))
            {
                candidates = candidates.Where(entry => entry.Group == apiCategory);
            }

            var terms = HelpSearch.Tokenize(memberSearch);
            if (terms.Length == 0)
            {
                _memberFiltered = candidates.ToArray();
                return _memberFiltered;
            }
            var fields = new List<HelpSearchField>();
            var matched = new List<(HelpApiMember Entry, int Score, int Index)>();
            var index = 0;
            foreach (var entry in candidates)
            {
                fields.Clear();
                entry.AppendSearchFields(fields, type.Name);
                var score = HelpSearch.Score(fields, terms);
                if (score >= 0)
                {
                    matched.Add((entry, score, index));
                }

                index++;
            }
            _memberFiltered = matched.OrderByDescending(item => item.Score).ThenBy(item => item.Index)
                .Select(item => item.Entry).ToArray();
            return _memberFiltered;
        }

        /// <summary>成员分类芯片：只显示实际存在的类别与真实数量。</summary>
        private void DrawCategoryChips(HelpApiType type, string[] groups)
        {
            var labels = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("", "全部  " + type.Entries.Length) };
            foreach (var group in groups)
            {
                labels.Add(new KeyValuePair<string, string>(group, group + "  " + type.Entries.Count(entry => entry.Group == group)));
            }

            const float Gap = 6f;
            var sizes = labels.Select(label => _chip.CalcSize(new GUIContent(label.Value))).ToArray();
            var chipHeight = Mathf.Max(22f, _chip.lineHeight + 6);
            var rows = 1;
            var used = 0f;
            foreach (var size in sizes)
            {
                var chipWidth = size.x + 16;
                if (used > 0 && used + Gap + chipWidth > _readingWidth)
                {
                    rows++;
                    used = 0;
                }
                used += (used > 0 ? Gap : 0) + chipWidth;
            }
            var total = rows * chipHeight + (rows - 1) * Gap;
            var area = GUILayoutUtility.GetRect(_readingWidth, total, GUILayout.Width(_readingWidth));
            var x = area.x;
            var y = area.y;
            used = 0;
            for (var index = 0; index < labels.Count; index++)
            {
                var chipWidth = sizes[index].x + 16;
                if (used > 0 && used + Gap + chipWidth > _readingWidth)
                {
                    y += chipHeight + Gap;
                    x = area.x;
                    used = 0;
                }
                var rect = new Rect(x, y, chipWidth, chipHeight);
                var current = (labels[index].Key ?? "") == (apiCategory ?? "");
                if (current)
                {
                    EditorGUI.DrawRect(rect, _palette.ChipSelectedBg);
                }
                else if (rect.Contains(Event.current.mousePosition))
                {
                    EditorGUI.DrawRect(rect, _palette.ChipHoverBg);
                }
                else
                {
                    EditorGUI.DrawRect(rect, _palette.ChipBg);
                }

                HelpDraw.Outline(rect, _palette.ChipBorder);
                if (GUI.Button(rect, new GUIContent(labels[index].Value, "只显示该分类的成员"), current ? _chipSelected : _chip) && !current)
                {
                    apiCategory = labels[index].Key ?? "";
                    Repaint();
                    GUIUtility.ExitGUI();
                }
                x += chipWidth + Gap;
                used += (used > 0 ? Gap : 0) + chipWidth;
            }
            GUILayout.Space(10);
        }

        /// <summary>成员信息块：圆角弱轮廓 + 左侧强调线；顶部摘要可点击，展开详情仍在同一块内。</summary>
        private void DrawMemberRow(HelpApiType type, HelpApiMember entry, float indent)
        {
            var key = type.Id + "\n" + entry.Signature;
            var expanded = _detailExpanded.Contains(key);
            var blockWidth = Mathf.Max(120f, _readingWidth - indent);
            var style = expanded ? _memberBoxExpanded : _hoverBlockKey == key ? _memberBoxHover : _memberBox;
            var inner = Mathf.Max(60f, blockWidth - style.padding.horizontal);
            var display = DisplayName(type, entry);
            var brief = DescribeBrief(entry.Member);
            var badges = MemberBadges(type, entry);
            var nameHeight = Mathf.Max(18f, _memberName.lineHeight);
            var summaryHeight = Mathf.Max(16f, _memberSummary.lineHeight);
            GUILayout.BeginVertical(style, GUILayout.Width(blockWidth));
            var header = GUILayoutUtility.GetRect(inner, nameHeight + summaryHeight + 2, GUILayout.Width(inner));
            if (_revealMemberKey == key && Event.current.type == EventType.Repaint)
            {
                // 从搜索结果跳转：把目标成员滚到阅读区顶部附近，只做一次。
                _readerScroll = new Vector2(_readerScroll.x, Mathf.Max(0f, header.y - 10f));
                _revealMemberKey = null;
                Repaint();
            }
            if (GUI.Button(header, new GUIContent("", entry.Signature), GUIStyle.none))
            {
                // 多个成员详情可同时展开；再次点击同一成员只收起它自己。
                if (expanded)
                {
                    _detailExpanded.Remove(key);
                }
                else
                {
                    _detailExpanded.Add(key);
                }

                Repaint();
                GUIUtility.ExitGUI();
            }
            var badgesWidth = DrawBadges(header, header.y, badges);
            var lineWidth = Mathf.Max(40f, header.width - badgesWidth);
            var nameWidth = Mathf.Min(_memberName.CalcSize(new GUIContent(display)).x, lineWidth);
            GUI.Label(new Rect(header.x, header.y, nameWidth, nameHeight), new GUIContent(display, entry.Signature), _memberName);
            if (!string.IsNullOrEmpty(brief) && lineWidth > nameWidth + 10)
            {
                GUI.Label(new Rect(header.x + nameWidth + 6, header.y, lineWidth - nameWidth - 6, nameHeight),
                    new GUIContent(brief, entry.Signature), _memberBrief);
            }

            var summary = string.IsNullOrWhiteSpace(entry.Help.Summary) ? "未提供摘要说明。" : entry.Help.Summary;
            GUI.Label(new Rect(header.x, header.y + nameHeight, inner, summaryHeight), new GUIContent(summary, summary), _memberSummary);
            if (expanded)
            {
                // 摘要与详情之间：略强于普通边框的低对比分隔线，上下留出间距。
                GUILayout.Space(6);
                var divider = GUILayoutUtility.GetRect(inner, 1, GUILayout.Width(inner));
                EditorGUI.DrawRect(new Rect(divider.x + 2, divider.y, Mathf.Max(0, divider.width - 4), 1), _palette.MemberDivider);
                // 详情区在块内再水平内缩 4px，与外层边框和摘要文字都拉开距离。
                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                GUILayout.Space(4);
                GUILayout.BeginVertical(_memberDetailBox, GUILayout.Width(inner - 8));
                DrawMemberDetail(type, entry, Mathf.Max(60f, inner - 8 - _memberDetailBox.padding.horizontal));
                GUILayout.EndVertical();
                GUILayout.Space(4);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                var block = GUILayoutUtility.GetLastRect();
                var hovered = block.Contains(Event.current.mousePosition);
                if (hovered && _hoverBlockKey != key)
                {
                    _hoverBlockKey = key;
                    Repaint();
                }
                else if (!hovered && _hoverBlockKey == key)
                {
                    _hoverBlockKey = null;
                    Repaint();
                }
            }
            GUILayout.Space(6);
        }

        /// <summary>同名重载归组信息块：显示数量，展开后按块列出具体重载。</summary>
        private void DrawOverloadGroup(HelpApiType type, string name, HelpApiMember[] items)
        {
            var key = type.Id + "\n#" + name;
            var open = _overloadKey == key;
            var style = open ? _memberBoxExpanded : _hoverBlockKey == key ? _memberBoxHover : _memberBox;
            var inner = Mathf.Max(60f, _readingWidth - style.padding.horizontal);
            var display = name == ".ctor" ? type.Name : name;
            GUILayout.BeginVertical(style, GUILayout.Width(_readingWidth));
            var row = GUILayoutUtility.GetRect(inner, Mathf.Max(26f, _detailFoldout.lineHeight + 4), GUILayout.Width(inner));
            var label = new GUIContent(display + "  ·  " + items.Length + " 个重载", "展开选择具体的重载");
            var next = EditorGUI.Foldout(row, open, label, true, _detailFoldout);
            if (next != open)
            {
                _overloadKey = next ? key : null;
                Repaint();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                var block = GUILayoutUtility.GetLastRect();
                var hovered = block.Contains(Event.current.mousePosition);
                if (hovered && _hoverBlockKey != key)
                {
                    _hoverBlockKey = key;
                    Repaint();
                }
                else if (!hovered && _hoverBlockKey == key)
                {
                    _hoverBlockKey = null;
                    Repaint();
                }
            }
            GUILayout.Space(6);
            if (!open)
            {
                return;
            }

            foreach (var item in items)
            {
                DrawMemberRow(type, item, 16f);
            }
        }

        /// <summary>展示可复制声明及实际提供的参数、返回值和备注；缺失说明只提示缺失。</summary>
        private void DrawMemberDetail(HelpApiType type, HelpApiMember entry, float inner)
        {
            var help = entry.Help;
            var head = GUILayoutUtility.GetRect(inner, Mathf.Max(20f, _small.lineHeight), GUILayout.Width(inner));
            GUI.Label(new Rect(head.x, head.y, Mathf.Max(0, head.width - 60), head.height), "C# 签名", _small);
            var copyKey = type.Id + "\n" + entry.Signature + ":copy";
            var copied = _copiedKey == copyKey && EditorApplication.timeSinceStartup < _copyUntil;
            if (GUI.Button(new Rect(head.xMax - 52, head.y, 52, Mathf.Max(20f, head.height)), copied ? "已复制" : "复制", _button))
            {
                EditorGUIUtility.systemCopyBuffer = entry.Signature;
                _copiedKey = copyKey;
                _copyUntil = EditorApplication.timeSinceStartup + 1.6;
                ShowNotification(new GUIContent("已复制签名"));
                Repaint();
            }
            // 完整签名放进轻量 code 区域，和下面的 XML 正文样式区分开。
            var codeInner = Mathf.Max(40f, inner - 18);
            var signatureHeight = Mathf.Max(_codeWrap.lineHeight, _codeWrap.CalcHeight(new GUIContent(entry.Signature), codeInner));
            var codeArea = GUILayoutUtility.GetRect(inner, signatureHeight + 18, GUILayout.Width(inner));
            EditorGUI.DrawRect(codeArea, _palette.CodeBg);
            HelpDraw.Outline(codeArea, _palette.CodeBorder);
            DrawSelectable(new Rect(codeArea.x + 9, codeArea.y + 9, codeInner, signatureHeight), entry.Signature, _codeWrap);
            GUILayout.Space(4);
            // 摘要行已经在成员块顶部显示同一段 XML summary，这里不再重复渲染。
            var hasContent = false;
            if (help.TypeParameters.Length != 0)
            {
                DrawTextAt("类型参数", _detailHeading, inner, false);
                foreach (var parameter in help.TypeParameters)
                {
                    DrawParameterRow(inner, parameter.Name, "", parameter.Description);
                }

                hasContent = true;
            }
            if (help.Parameters.Length != 0)
            {
                DrawTextAt("参数", _detailHeading, inner, false);
                foreach (var parameter in help.Parameters)
                {
                    DrawParameterRow(inner, parameter.Name, ParameterType(entry.Member, parameter.Name), parameter.Description);
                }

                hasContent = true;
            }
            if (!string.IsNullOrWhiteSpace(help.Returns))
            {
                DrawTextAt(entry.Member is PropertyInfo ? "属性值" : "返回值", _detailHeading, inner, false);
                DrawTextAt(help.Returns, _body, inner);
                hasContent = true;
            }
            if (!string.IsNullOrWhiteSpace(help.Remarks))
            {
                DrawTextAt("备注与注意事项", _detailHeading, inner, false);
                DrawTextAt(help.Remarks, _body, inner);
                hasContent = true;
            }
            if (!hasContent)
            {
                DrawTextAt("未提供参数、返回值或备注说明。", _small, inner, false);
            }
        }

        /// <summary>名称 / 类型 / 说明的对齐布局；窄窗口改为上下排列。</summary>
        private void DrawParameterRow(float width, string name, string type, string description)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(description))
            {
                return;
            }

            const float Gap = 10f;
            if (width < 340f)
            {
                DrawTextAt(string.IsNullOrWhiteSpace(type) ? name : name + "  " + type, _code, width);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    DrawTextAt(description, _body, Mathf.Max(40, width - 14));
                }

                GUILayout.Space(3);
                return;
            }
            var nameWidth = Mathf.Clamp(width * 0.22f, 60f, 150f);
            var typeWidth = Mathf.Clamp(width * 0.30f, 80f, 220f);
            var descriptionWidth = Mathf.Max(40f, width - nameWidth - typeWidth - Gap * 2);
            var nameHeight = Mathf.Max(_code.lineHeight, _code.CalcHeight(new GUIContent(name ?? ""), nameWidth));
            var typeHeight = Mathf.Max(_code.lineHeight, _code.CalcHeight(new GUIContent(type ?? ""), typeWidth));
            var descriptionHeight = Mathf.Max(_body.lineHeight, _body.CalcHeight(new GUIContent(description ?? ""), descriptionWidth));
            var height = Mathf.Max(nameHeight, Mathf.Max(typeHeight, descriptionHeight));
            var row = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width));
            if (!string.IsNullOrWhiteSpace(name))
            {
                DrawSelectable(new Rect(row.x, row.y, nameWidth, height), name, _code);
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                DrawSelectable(new Rect(row.x + nameWidth + Gap, row.y, typeWidth, height), type, _code);
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                DrawSelectable(new Rect(row.x + nameWidth + typeWidth + Gap * 2, row.y, descriptionWidth, height), description, _body);
            }

            GUILayout.Space(2);
        }

        /// <summary>为构造函数显示所属类型名，其他成员保留元数据名称。</summary>
        private static string DisplayName(HelpApiType type, HelpApiMember entry) => entry.Name == ".ctor" ? type.Name : entry.Name;

        /// <summary>仅从编译元数据生成成员标题旁的参数或类型简表，不读取运行时成员值。</summary>
        private string DescribeBrief(MemberInfo member)
        {
            if (member is ConstructorInfo constructor)
            {
                return "(" + BriefParameters(constructor) + ")";
            }

            if (member is MethodInfo method)
            {
                return (method.IsGenericMethod
                ? "<" + string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name)) + ">" : "") +
                "(" + BriefParameters(method) + ")";
            }

            if (member is PropertyInfo property)
            {
                var propertyType = HelpApiFormatter.FormatType(property.PropertyType, _shortNamespaces);
                var index = property.GetIndexParameters();
                return index.Length == 0 ? ": " + propertyType : "[" + string.Join(", ", index.Select(parameter =>
                    HelpApiFormatter.FormatType(parameter.ParameterType, _shortNamespaces) + " " + parameter.Name)) + "] : " + propertyType;
            }
            if (member is FieldInfo field)
            {
                return field.DeclaringType != null && field.DeclaringType.IsEnum
                ? "= " + field.GetRawConstantValue()
                : ": " + HelpApiFormatter.FormatType(field.FieldType, _shortNamespaces);
            }

            if (member is EventInfo signal)
            {
                return ": " + HelpApiFormatter.FormatType(signal.EventHandlerType, _shortNamespaces);
            }

            return "";
        }

        /// <summary>为成员标题列出参数修饰符、类型和名称，省略默认值以压缩展示。</summary>
        private string BriefParameters(MethodBase method)
        {
            var extension = method.IsDefined(typeof(ExtensionAttribute), false);
            return string.Join(", ", method.GetParameters().Select((parameter, index) =>
                (extension && index == 0 ? "this " : "") +
                (parameter.IsDefined(typeof(ParamArrayAttribute), false) ? "params " :
                    parameter.ParameterType.IsByRef ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref " : "") +
                HelpApiFormatter.FormatType(parameter.ParameterType, _shortNamespaces) + " " + parameter.Name));
        }

        /// <summary>按真实参数名称取得带引用修饰符的显示类型；文档名称无对应声明时留空。</summary>
        private string ParameterType(MemberInfo member, string name)
        {
            var parameters = member is ConstructorInfo constructor ? constructor.GetParameters() :
                member is MethodInfo method ? method.GetParameters() :
                member is PropertyInfo property ? property.GetIndexParameters() : Array.Empty<ParameterInfo>();
            var parameter = parameters.FirstOrDefault(item => item.Name == name);
            if (parameter == null)
            {
                return "";
            }

            var prefix = parameter.ParameterType.IsByRef ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref " : "";
            return prefix + HelpApiFormatter.FormatType(parameter.ParameterType, _shortNamespaces);
        }

        /// <summary>按成员元数据生成静态性、访问级别和常量等标签，并标记全局查询中的成员命中。</summary>
        private string[] MemberBadges(HelpApiType type, HelpApiMember entry)
        {
            var member = entry.Member;
            var badges = new List<string>();
            var isEnumValue = member is FieldInfo enumField && enumField.DeclaringType.IsEnum;
            var isStatic = member is MethodBase callable ? callable.IsStatic :
                member is FieldInfo field ? field.IsStatic :
                member is PropertyInfo property ? (property.GetGetMethod(true) ?? property.GetSetMethod(true))?.IsStatic == true :
                member is EventInfo signal && signal.GetAddMethod(true) is MethodInfo add && add.IsStatic;
            if (isStatic && !isEnumValue)
            {
                badges.Add("static");
            }

            var accessibility = member is MethodBase method ? AccessBadge(method.IsPublic, method.IsFamily, method.IsFamilyOrAssembly) :
                member is FieldInfo declared ? AccessBadge(declared.IsPublic, declared.IsFamily, declared.IsFamilyOrAssembly) :
                member is PropertyInfo declaredProperty ? PropertyAccess(declaredProperty) :
                member is EventInfo declaredEvent && declaredEvent.GetAddMethod(true) is MethodInfo handler
                    ? AccessBadge(handler.IsPublic, handler.IsFamily, handler.IsFamilyOrAssembly) : "";
            if (accessibility.Length != 0)
            {
                badges.Add(accessibility);
            }

            if (!isEnumValue && member is FieldInfo constant && constant.IsLiteral)
            {
                badges.Add("const");
            }
            else if (!isEnumValue && member is FieldInfo readonlyField && readonlyField.IsInitOnly)
            {
                badges.Add("readonly");
            }

            if (type.MemberMatches(entry, search))
            {
                badges.Add("匹配");
            }

            return badges.ToArray();
        }

        /// <summary>只为受保护访问级别生成标签，公开成员不额外占用标签空间。</summary>
        private static string AccessBadge(bool isPublic, bool isFamily, bool isFamilyOrAssembly) => isPublic ? "" :
            isFamilyOrAssembly ? "protected internal" : isFamily ? "protected" : "";

        /// <summary>优先按公开访问器确定属性标签；没有公开访问器时使用首个访问器的级别。</summary>
        private static string PropertyAccess(PropertyInfo property)
        {
            var accessors = property.GetAccessors(true);
            var primary = accessors.FirstOrDefault(method => method.IsPublic) ?? accessors.FirstOrDefault();
            return primary == null ? "" : AccessBadge(primary.IsPublic, primary.IsFamily, primary.IsFamilyOrAssembly);
        }

        /// <summary>从成员标题行右侧排布标签并返回占用宽度，供名称区域避让。</summary>
        private float DrawBadges(Rect row, float y, string[] badges)
        {
            if (badges.Length == 0)
            {
                return 0;
            }

            var height = Mathf.Max(16f, _badge.lineHeight + 3);
            var total = 0f;
            var x = row.xMax;
            foreach (var badge in badges)
            {
                var width = _badge.CalcSize(new GUIContent(badge)).x + 12;
                x -= width;
                var rect = new Rect(x, y, width, height);
                EditorGUI.DrawRect(rect, _palette.BadgeBg);
                GUI.Label(rect, new GUIContent(badge, badge), _badge);
                x -= 5;
                total += width + 5;
            }
            return total;
        }

        /// <summary>绘制短文本链接按钮并返回点击结果，不自行执行导航。</summary>
        private bool DrawSmallLink(string label)
        {
            var size = _smallLink.CalcSize(new GUIContent(label));
            var rect = GUILayoutUtility.GetRect(_readingWidth, Mathf.Max(20f, _smallLink.lineHeight), GUILayout.Width(_readingWidth));
            return GUI.Button(new Rect(rect.x, rect.y, Mathf.Min(size.x + 4, rect.width), rect.height),
                new GUIContent(label, label), _smallLink);
        }

        /// <summary>按当前阅读宽度绘制文本，供页面和类型正文共用。</summary>
        private void DrawText(string text, GUIStyle style, bool highlight = true) => DrawTextAt(text, style, _readingWidth, highlight);

        /// <summary>按给定宽度绘制可选择文本；正文自动换行且换行后对齐内容起点。</summary>
        private void DrawTextAt(string text, GUIStyle style, float width, bool highlight = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var height = Mathf.Max(style.lineHeight, style.CalcHeight(new GUIContent(text), width));
            var rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width));
            DrawSelectable(rect, text, style, highlight);
        }

        /// <summary>将全局搜索词同步给高亮缓存，并在目标文字下绘制匹配背景。</summary>
        private void DrawHighlight(Rect rect, string text, GUIStyle style)
        {
            _highlights.SetQuery(search);
            _highlights.Draw(rect, text, style, _isDarkTheme);
        }

        /// <summary>在给定区域绘制可选择文本，可选在缩进后的文字区域叠加搜索高亮。</summary>
        private void DrawSelectable(Rect rect, string text, GUIStyle style, bool highlight = true)
        {
            if (highlight)
            {
                DrawHighlight(EditorGUI.IndentedRect(rect), text, style);
            }

            EditorGUI.SelectableLabel(rect, text, style);
        }

        /// <summary>按正文高度绘制带图标和边框的提示块，空内容不占用布局。</summary>
        private void DrawNotice(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var padding = _notice.padding;
            var width = Mathf.Max(1, _readingWidth - padding.horizontal - 36);
            var height = Mathf.Max(24, _body.CalcHeight(new GUIContent(text), width));
            var rect = GUILayoutUtility.GetRect(_readingWidth, height + padding.vertical, GUILayout.Width(_readingWidth));
            EditorGUI.DrawRect(rect, _palette.NoticeBg);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 3, rect.height), _palette.NoticeAccent);
            HelpDraw.Outline(new Rect(rect.x + 1, rect.y, rect.width - 1, rect.height), _palette.NoticeBorder);
            GUI.Label(new Rect(rect.x + padding.left, rect.y + padding.top, 24, 24),
                EditorGUIUtility.IconContent("console.infoicon"), GUIStyle.none);
            DrawSelectable(new Rect(rect.x + padding.left + 34, rect.y + padding.top, width, height), text, _body);
            GUILayout.Space(10);
        }

        /// <summary>按阅读宽度绘制可换行链接并返回点击状态，具体打开或导航由调用方决定。</summary>
        private bool DrawLink(string label, string tooltip, bool highlight = true)
        {
            var height = Mathf.Max(22, _link.CalcHeight(new GUIContent(label), _readingWidth));
            var rect = GUILayoutUtility.GetRect(_readingWidth, height, GUILayout.Width(_readingWidth));
            if (highlight)
            {
                DrawHighlight(rect, label, _link);
            }

            return GUI.Button(rect, new GUIContent(label, tooltip), _link);
        }

        /// <summary>绘制只读代码块及复制反馈，按稳定键保留各块独立的滚动位置。</summary>
        private void DrawCode(string key, string text)
        {
            var label = new GUIContent(text);
            var codeWidth = _code.CalcSize(label).x + 16;
            var textHeight = Mathf.Max(_code.lineHeight, _code.CalcHeight(label, codeWidth));
            var codeHeight = textHeight + 20;
            // 高度按文本估算并限制在 96~384；横向需要滚动时额外留出滚动条的空间。
            var height = Mathf.Clamp(codeHeight + 46 + (codeWidth > _readingWidth - 24 ? 16 : 0), 96, 384);
            var block = GUILayoutUtility.GetRect(_readingWidth, height, GUILayout.Width(_readingWidth));
            EditorGUI.DrawRect(block, _palette.CodeBg);
            HelpDraw.Outline(block, _palette.CodeBorder);
            var header = new Rect(block.x, block.y, block.width, 30);
            EditorGUI.DrawRect(header, _palette.CodeHeaderBg);
            HelpDraw.Rule(new Rect(header.x + 1, header.yMax - 1, header.width - 2, 1), _palette.CodeBorder);
            var tag = new GUIContent("C# · 只读");
            var tagSize = _tag.CalcSize(tag);
            var tagRect = new Rect(block.x + 10, block.y + 5, tagSize.x + 10, 20);
            EditorGUI.DrawRect(tagRect, _palette.CodeTagBg);
            GUI.Label(tagRect, tag, _tag);
            var copied = _copiedKey == key && EditorApplication.timeSinceStartup < _copyUntil;
            if (GUI.Button(new Rect(block.xMax - 76, block.y + 3, 66, 24), copied ? "已复制" : "复制", _button))
            {
                EditorGUIUtility.systemCopyBuffer = text;
                _copiedKey = key;
                _copyUntil = EditorApplication.timeSinceStartup + 1.6;
                ShowNotification(new GUIContent("已复制到剪贴板"));
                Repaint();
            }
            var view = new Rect(block.x + 10, header.yMax + 6, block.width - 20, Mathf.Max(1, block.height - header.height - 12));
            _codeScroll.TryGetValue(key, out var scroll);
            var content = new Rect(0, 0, Mathf.Max(view.width - 16, codeWidth), Mathf.Max(view.height - 16, codeHeight));
            scroll = GUI.BeginScrollView(view, scroll, content);
            DrawSelectable(new Rect(10, 8, content.width - 20, textHeight), text, _code);
            GUI.EndScrollView();
            _codeScroll[key] = scroll;
            GUILayout.Space(10);
        }

        /// <summary>记录当前位置后切换页签或条目，可按链接语义清除全局搜索并回到正文顶部。</summary>
        private void Navigate(int tab, string id, bool clearSearch)
        {
            _history.Push(new Navigation
            {
                Tab = this.tab,
                Guide = guideId,
                Api = apiId,
                About = aboutId,
                Search = search,
                Nav = _navScroll,
                Reader = _readerScroll,
                Collapsed = collapsedGroups.ToArray(),
                SearchCollapsed = _searchCollapsed.ToArray()
            });
            if (this.tab != tab)
            {
                _navScroll = Vector2.zero;
            }

            this.tab = tab;
            if (tab == 0)
            {
                guideId = id;
            }
            else if (tab == 1)
            {
                apiId = id;
            }
            else
            {
                aboutId = id;
            }

            if (clearSearch)
            {
                search = "";
                _searchCollapsed.Clear();
            }
            _readerScroll = Vector2.zero;
            _filterKey = null;
            _revealInNav = true;
            _highlights.Clear();
            Repaint();
            GUIUtility.ExitGUI();
        }

        /// <summary>恢复最近的导航位置快照；调用方须先确认历史栈非空。</summary>
        private void GoBack()
        {
            var previous = _history.Pop();
            tab = previous.Tab;
            guideId = previous.Guide;
            apiId = previous.Api;
            aboutId = previous.About;
            search = previous.Search;
            _navScroll = previous.Nav;
            _readerScroll = previous.Reader;
            _filterKey = null;
            collapsedGroups = previous.Collapsed.ToList();
            _searchCollapsed.Clear();
            _searchCollapsed.UnionWith(previous.SearchCollapsed);
            // 滚动位置随历史一起还原，不再自动滚动到选中项。
            _revealInNav = false;
            _revealMemberKey = null;
            _highlights.Clear();
            Repaint();
            GUIUtility.ExitGUI();
        }

        /// <summary>在当前普通或搜索折叠集合中展开选中条目所在的目录组。</summary>
        private void RevealSelectedGroup()
        {
            var group = tab == 0 ? _visibleTopics.FirstOrDefault(topic => topic.Id == guideId)?.Group :
                tab == 1 ? _visibleTypes.FirstOrDefault(type => type.Id == apiId)?.Group : null;
            if (group == null)
            {
                return;
            }

            var key = (tab == 0 ? "guide:" : "api:") + group;
            if (string.IsNullOrWhiteSpace(search))
            {
                collapsedGroups.Remove(key);
            }
            else
            {
                _searchCollapsed.Remove(key);
            }
        }
    }
}
