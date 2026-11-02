using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YLFramework.Core;
using Object = UnityEngine.Object;
using YLFramework.UI;
using YLFramework.Editor.Foundation;

namespace YLFramework.Editor.Core
{
    /// <summary>集成后显式执行的 UI Panel 资产迁移。不会自动运行、保存场景或改写非 UI 内容。</summary>
    /// <remarks>逐资产直接保存，不建立 Undo 或整批回滚；后续失败不会撤销此前已保存的资产。</remarks>
    public static class UIPanelAssetAuthoring
    {
        /// <summary>基础 UI 根的既有 Prefab 路径，迁移时保留该资产身份。</summary>
        public const string BaseRootPath = UIPanelAssetMigration.BaseRootPath;
        /// <summary>包含唯一框架宿主和实际 UI 根的完整模板路径。</summary>
        public const string TemplatePath = UIPanelAssetMigration.TemplatePath;
        /// <summary>作为提示默认配置来源的原始 UITips Prefab 路径。</summary>
        public const string TipsPath = UIPanelAssetMigration.TipsPath;
        /// <summary>完整模板使用的提示 Variant 路径；缺失时从原始提示创建独立资产身份。</summary>
        public const string TipsVariantPath = UIPanelAssetMigration.TipsVariantPath;
        /// <summary>需要显式写入 Panel 默认配置的取色窗口 Prefab 路径。</summary>
        public const string ColorPath = UIPanelAssetMigration.ColorPath;
        private static readonly string[] _fixturePanelPaths =
        {
            "Assets/Tests/YLFramework/Fixtures/Core/YLCoreSmokeModule/YLCoreSmokePanel.prefab",
            "Assets/Tests/YLFramework/Fixtures/Core/YLCoreResourceModule/YLCoreSmokePanel.prefab"
        };

        /// <summary>从菜单执行只读资产预检并输出结果，不启动迁移。</summary>
        private static void ValidateMenu() => Debug.Log(ValidateAssets());

        /// <summary>只读预检现有资产、根和提示引用；允许尚未迁移，不把结构存在当成实例化验收通过。</summary>
        public static string ValidateAssets()
        {
            var basic = Required(BaseRootPath);
            var template = Required(TemplatePath);
            var tips = Required(TipsPath);
            var color = Required(ColorPath);
            foreach (var prefab in new[] { basic, template, tips, color })
            {
                Objects(prefab).ToArray();
            }

            if (!color.GetComponent<ColorSelectorWindow>())
            {
                throw new InvalidOperationException("取色 Prefab 根不是正式 ColorSelectorWindow。");
            }

            if (!basic.GetComponent<Canvas>() || !basic.GetComponent<CanvasScaler>() || !basic.GetComponent<GraphicRaycaster>())
            {
                throw new InvalidOperationException("基础根缺少既有 Canvas/CanvasScaler/GraphicRaycaster，停止以免覆盖未知结构。");
            }

            var host = template.GetComponent<YLFrameworkRoot>();
            var contexts = template.GetComponentsInChildren<UIRootContext>(true);
            if (!host || contexts.Length != 1)
            {
                throw new InvalidOperationException("完整模板必须有唯一宿主和唯一实际 UI 根。");
            }

            var context = contexts[0];
            if (!context.GetComponent<Canvas>() || !context.GetComponent<CanvasScaler>() || !context.GetComponent<GraphicRaycaster>())
            {
                throw new InvalidOperationException("完整模板原生 Canvas 组件不完整。");
            }

            ValidateLayers(context, false);
            if (new SerializedObject(context).FindProperty("uiLayers").arraySize <= 4)
            {
                throw new InvalidOperationException("完整模板需要明确 Layer4 承载 Tips Variant，不自动扩层。");
            }

            var presenters = template.GetComponentsInChildren<UITipsPresenter>(true);
            if (presenters.Length != 1 || !tips.GetComponent<UITipsItem>())
            {
                throw new InvalidOperationException("Tips 承载不明确，停止自动替换引用。");
            }

            if (!presenters[0].transform.IsChildOf(context.transform))
            {
                throw new InvalidOperationException("Tips Presenter 不属于模板 UI 根，停止迁移。");
            }

            var current = new SerializedObject(presenters[0]).FindProperty("uiTipItemPrefab").objectReferenceValue;
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(TipsVariantPath);
            if (current != tips && (!variant || current != variant))
            {
                throw new InvalidOperationException("模板 Tips 使用自定义来源，请先人工确认，不覆盖其绑定。");
            }

            if (variant && PrefabUtility.GetPrefabAssetType(variant) != PrefabAssetType.Variant)
            {
                throw new InvalidOperationException("UITipsOverlay 路径已存在非 Variant，停止覆盖。");
            }

            if (variant)
            {
                Objects(variant).ToArray();
                if (PrefabUtility.GetCorrespondingObjectFromSource(variant) != tips)
                {
                    throw new InvalidOperationException("现有 Tips Variant 不是原 UITips 的直接 Variant，停止改变来源。");
                }
            }
            return "UI Panel 资产预检完成。基础根：" + (basic.GetComponent<UIRootContext>() ? "已有根组件" : "待补显式 Layer0") +
                "；完整模板：" + (PrefabUtility.GetCorrespondingObjectFromSource(context.gameObject) == basic ? "已引用基础根" : "待转换基础根实例") +
                "；Tips Variant：" + (variant ? "已存在，仍需核验配置" : "待创建并由 Unity 生成 meta") +
                "。此结果不是 PlayMode、输入或 Player 验收。";
        }

        /// <summary>显式、可重复执行；先预检，再依次保存基础根、提示源与 Variant、取色窗口和完整模板。</summary>
        [MenuItem("YLFramework/Core/UI Panel/执行资产迁移")]
        public static void MigrateAssets()
        {
            EnsureCanMigrate();
            ValidateAssets();
            MigrateBase();
            MigrateTips();
            MigratePanel(ColorPath);
            MigrateTemplate();
            Debug.Log(ValidateAssets() + " 请核查资产 diff、生成的 meta、旧 fileID 和实例化结果，再执行引擎验收。");
        }

        /// <summary>仓库测试夹具的独立显式入口；正式框架导出不包含 Tests，不依赖该入口运行。</summary>
        [MenuItem("YLFramework/Core/UI Panel/迁移测试 Panel 夹具")]
        public static void MigrateTestFixtures()
        {
            EnsureCanMigrate();
            foreach (var path in _fixturePanelPaths)
            {
                var fixture = Required(path);
                Objects(fixture).ToArray();
                if (!fixture.GetComponent<Panel>())
                {
                    throw new InvalidOperationException("测试 Panel 脚本未导入或类型不匹配，请先完成测试程序集导入：" + path);
                }
            }
            foreach (var path in _fixturePanelPaths)
            {
                MigratePanel(path);
            }

            Debug.Log("测试 Panel 七字段已显式保存，仍需重载/实例化和运行测试验证。");
        }
        /// <summary>拒绝在运行、编译、导入或 Prefab Stage 编辑期间执行，不替用户关闭或保存编辑状态。</summary>
        private static void EnsureCanMigrate()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(YLFrameworkRoot).Assembly);
            ListenerInitOnLoad.RequireLocalSourcePackage(package);
            if (Application.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating || PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                throw new InvalidOperationException("请退出 Play，等待编译/导入结束并关闭 Prefab Stage 后执行资产迁移。");
            }
        }

        /// <summary>取得指定既有 Prefab，缺失即失败，不创建占位资产替代原身份。</summary>
        private static GameObject Required(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path) ??
            throw new InvalidOperationException("缺少现有资产，不创建替代身份：" + path);

        /// <summary>在已加载的迁移内容中绑定同对象原生组件；这里只改内存数据，不保存资产。</summary>
        private static void BindRoot(UIRootContext context)
        {
            var serialized = new SerializedObject(context);
            serialized.FindProperty("canvas").objectReferenceValue = context.GetComponent<Canvas>();
            serialized.FindProperty("graphicRaycaster").objectReferenceValue = context.GetComponent<GraphicRaycaster>();
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        /// <summary>校验既有层容器及必需遮罩的子树归属，不推断缺失绑定或扩充层映射。</summary>
        private static void ValidateLayers(UIRootContext context, bool allowEmpty)
        {
            var layers = new SerializedObject(context).FindProperty("uiLayers");
            if (!allowEmpty && layers.arraySize == 0)
            {
                throw new InvalidOperationException("完整模板没有既有层映射，停止猜测。");
            }

            for (int i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.GetArrayElementAtIndex(i);
                var root = layer.FindPropertyRelative("root").objectReferenceValue as Transform;
                var image = layer.FindPropertyRelative("maskImage").objectReferenceValue as Image;
                if (!root || !root.IsChildOf(context.transform))
                {
                    throw new InvalidOperationException("层容器丢失或跨根。");
                }

                if (layer.FindPropertyRelative("enableMask").boolValue && (!image || !image.transform.IsChildOf(root)))
                {
                    throw new InvalidOperationException("模态层遮罩丢失或不属于层容器。");
                }
            }
        }
        /// <summary>独立加载基础根，必要时补根组件和唯一 Layer0，核验保存后始终卸载迁移内容。</summary>
        private static void MigrateBase()
        {
            var identity = CaptureIdentity(BaseRootPath);
            var contents = PrefabUtility.LoadPrefabContents(BaseRootPath);
            try
            {
                var context = contents.GetComponent<UIRootContext>();
                if (!context)
                {
                    context = contents.AddComponent<UIRootContext>();
                }

                BindRoot(context);
                var serialized = new SerializedObject(context);
                var layers = serialized.FindProperty("uiLayers");
                if (layers.arraySize == 0)
                {
                    if (contents.transform.Find("Layer0"))
                    {
                        throw new InvalidOperationException("已有未绑定 Layer0，请先核查，不能重复创建。");
                    }

                    var layer = new GameObject("Layer0", typeof(RectTransform));
                    layer.transform.SetParent(contents.transform, false);
                    Stretch((RectTransform)layer.transform);
                    layers.arraySize = 1;
                    var entry = layers.GetArrayElementAtIndex(0);
                    entry.FindPropertyRelative("root").objectReferenceValue = layer.transform;
                    entry.FindPropertyRelative("enableMask").boolValue = false;
                    entry.FindPropertyRelative("maskImage").objectReferenceValue = null;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                ValidateLayers(context, false);
                SaveChecked(contents, BaseRootPath, identity);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
        /// <summary>为本次新建的层容器设置铺满父级的锚点、零边距和单位缩放。</summary>
        private static void Stretch(RectTransform transform)
        {
            transform.anchorMin = Vector2.zero;
            transform.anchorMax = Vector2.one;
            transform.offsetMin = transform.offsetMax = Vector2.zero;
            transform.localScale = Vector3.one;
        }
        /// <summary>先保存提示源默认配置，再更新既有直接 Variant 或在预览场景中创建它，不改变其他来源。</summary>
        private static void MigrateTips()
        {
            var sourceIdentity = CaptureIdentity(TipsPath);
            var sourceContents = PrefabUtility.LoadPrefabContents(TipsPath);
            try
            {
                ConfigureTips(sourceContents.GetComponent<UITipsItem>(), false);
                SaveChecked(sourceContents, TipsPath, sourceIdentity);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(sourceContents);
            }
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(TipsVariantPath);
            if (existing)
            {
                if (PrefabUtility.GetCorrespondingObjectFromSource(existing) != Required(TipsPath))
                {
                    throw new InvalidOperationException("现有 Tips Variant 不是原 UITips 的直接 Variant，不改变其来源。");
                }

                var identity = CaptureIdentity(TipsVariantPath);
                var contents = PrefabUtility.LoadPrefabContents(TipsVariantPath);
                try
                {
                    MigratePanelConfigOverridePaths(contents);
                    ConfigureTips(contents.GetComponent<UITipsItem>(), true);
                    SaveChecked(contents, TipsVariantPath, identity);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
                return;
            }
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(Required(TipsPath), preview);
                ConfigureTips(instance.GetComponent<UITipsItem>(), true);
                if (!PrefabUtility.SaveAsPrefabAsset(instance, TipsVariantPath))
                {
                    throw new InvalidOperationException("Tips Variant 保存失败。");
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }
        /// <summary>向指定既有 Panel Prefab 显式写入框架默认值，保存时核对其旧身份与缩放配置。</summary>
        private static void MigratePanel(string path)
        {
            var identity = CaptureIdentity(path);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                WriteSettings(contents.GetComponent<Panel>(), PanelConfig.Default);
                SaveChecked(contents, path, identity);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
        /// <summary>将七字段值写入已加载的 Panel 序列化数据，不依赖结构体零值或隐式字段默认。</summary>
        private static void WriteSettings(Panel panel, PanelConfig value)
        {
            var serialized = new SerializedObject(panel);
            var settings = serialized.FindProperty("settings");
            settings.FindPropertyRelative("layer").intValue = value.layer;
            settings.FindPropertyRelative("priority").intValue = value.priority;
            settings.FindPropertyRelative("modal").boolValue = value.modal;
            settings.FindPropertyRelative("coversRoot").boolValue = value.coversRoot;
            settings.FindPropertyRelative("participatesInTop").boolValue = value.participatesInTop;
            settings.FindPropertyRelative("instanceMode").enumValueIndex = (int)value.instanceMode;
            settings.FindPropertyRelative("cacheOnClose").boolValue = value.cacheOnClose;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>只迁移传入 Prefab 内容中 Panel 目标的覆盖路径，不在此保存资产。</summary>
        // FormerlySerializedAs 能恢复值，但旧 Variant 路径不一定被视作新字段的有效 override。
        internal static bool MigratePanelConfigOverridePaths(GameObject prefab)
        {
            return SerializedFieldMigration.MigratePrefabOverrides(prefab, target => target is Panel);
        }
        /// <summary>提示源设为非 Top 的 Multiple；覆盖版仅保留 Layer4 配置，并关闭提示 Graphic 的射线目标。</summary>
        private static void ConfigureTips(UITipsItem tip, bool overlay)
        {
            if (!tip)
            {
                throw new InvalidOperationException("Tips Variant 根缺少 UITipsItem。");
            }

            if (!overlay)
            {
                WriteSettings(tip, new PanelOverrides { ParticipatesInTop = false, InstanceMode = PanelInstanceMode.Multiple }.ApplyTo(PanelConfig.Default));
            }
            else
            {
                var serialized = new SerializedObject(tip);
                var settings = serialized.FindProperty("settings");
                foreach (var field in new[] { "priority", "modal", "coversRoot", "participatesInTop", "instanceMode", "cacheOnClose" })
                {
                    var property = settings.FindPropertyRelative(field);
                    if (property.prefabOverride)
                    {
                        PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
                    }
                }
                serialized.Update();
                serialized.FindProperty("settings").FindPropertyRelative("layer").intValue = 4;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var graphic in tip.GetComponentsInChildren<Graphic>(true))
            {
                graphic.raycastTarget = false;
            }
        }
        /// <summary>把完整模板中的实际根关联到基础 Prefab，保留匹配配置与额外层级，再绑定宿主和提示 Variant。</summary>
        private static void MigrateTemplate()
        {
            var identity = CaptureIdentity(TemplatePath);
            var contents = PrefabUtility.LoadPrefabContents(TemplatePath);
            try
            {
                var context = contents.GetComponentsInChildren<UIRootContext>(true).Single();
                bool active = context.gameObject.activeSelf;
                context.gameObject.SetActive(false);
                if (!context.GetComponent<AutomaticAdaptationCanvasScaler>())
                {
                    var scaler = context.GetComponent<CanvasScaler>();
                    var resolution = scaler.referenceResolution;
                    var match = scaler.matchWidthOrHeight;
                    var adaptation = context.gameObject.AddComponent<AutomaticAdaptationCanvasScaler>();
                    adaptation.Configure(resolution, false, match);
                }
                var basic = Required(BaseRootPath);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(context.gameObject);
                if (source != basic)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(context.gameObject))
                    {
                        throw new InvalidOperationException("完整模板根已有不同 Prefab 来源，不自动解包或重定向。");
                    }
                    // ByHierarchy 保留根和匹配组件，其余层、Tips、Camera 作为 added override 保留。
                    // 匹配组件记录原值 override，特别保护两份预设的 CanvasScaler/AutomaticAdaptation 配置差异。
                    PrefabUtility.ConvertToPrefabInstance(context.gameObject, basic, new ConvertToPrefabInstanceSettings
                    {
                        objectMatchMode = ObjectMatchMode.ByHierarchy,
                        recordPropertyOverridesOfMatches = true,
                        componentsNotMatchedBecomesOverride = true,
                        gameObjectsNotMatchedBecomesOverride = true,
                        changeRootNameToAssetName = false
                    }, InteractionMode.AutomatedAction);
                    context = contents.GetComponentsInChildren<UIRootContext>(true).Single();
                }
                BindRoot(context);
                ValidateLayers(context, false);
                var host = new SerializedObject(contents.GetComponent<YLFrameworkRoot>());
                host.FindProperty("panelRoot").objectReferenceValue = context;
                host.ApplyModifiedPropertiesWithoutUndo();
                var presenter = new SerializedObject(context.GetComponentsInChildren<UITipsPresenter>(true).Single());
                presenter.FindProperty("uiTipItemPrefab").objectReferenceValue = Required(TipsVariantPath);
                presenter.ApplyModifiedPropertiesWithoutUndo();
                context.gameObject.SetActive(active);
                SaveChecked(contents, TemplatePath, identity);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>保存前捕获的资产 GUID、对象 fileID、层级键与缩放配置，供写入后核验使用。</summary>
        private sealed class Identity
        {
            internal string Guid;
            internal Dictionary<long, string> Objects;
            internal Dictionary<string, long> Keys;
            internal float[] Scaler;
            internal float[] Adaptation;
        }
        /// <summary>枚举包含 inactive 节点的全部对象与组件，遇到 Missing Script 立即拒绝继续迁移。</summary>
        private static IEnumerable<Object> Objects(GameObject root)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                yield return transform.gameObject;
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (!component)
                    {
                        throw new InvalidOperationException("资产有 Missing Script，迁移前必须先解决。");
                    }
                    yield return component;
                }
            }
        }
        /// <summary>记录能取得本地身份的对象及类型，供保存后检查旧 fileID 是否仍对应原类型。</summary>
        private static Dictionary<long, string> FileIds(GameObject root)
        {
            var ids = new Dictionary<long, string>();
            foreach (var obj in Objects(root))
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long id))
                {
                    ids[id] = obj.GetType().FullName;
                }
            }

            return ids;
        }
        /// <summary>从既有资产捕获对象身份及第一组缩放配置，后续保存不得静默替换这些数据。</summary>
        private static Identity CaptureIdentity(string path)
        {
            var prefab = Required(path);
            var scaler = prefab.GetComponentInChildren<CanvasScaler>(true);
            var adaptation = prefab.GetComponentInChildren<AutomaticAdaptationCanvasScaler>(true);
            return new Identity
            {
                Guid = AssetDatabase.AssetPathToGUID(path),
                Objects = FileIds(prefab),
                Keys = KeyedObjects(prefab).ToDictionary(pair => pair.Key, pair => LocalId(pair.Value)),
                Scaler = scaler ? ScaleValues(scaler) : null,
                Adaptation = adaptation ? AdaptationValues(adaptation) : null
            };
        }
        /// <summary>用相对层级路径、对象类型和同类型组件序号生成匹配键，不依赖保存后可能变化的 fileID。</summary>
        private static IEnumerable<KeyValuePair<string, Object>> KeyedObjects(GameObject root)
        {
            foreach (var obj in Objects(root))
            {
                var component = obj as Component;
                var transform = component ? component.transform : ((GameObject)obj).transform;
                int index = component ? Array.IndexOf(component.GetComponents(component.GetType()), component) : 0;
                yield return new KeyValuePair<string, Object>(AnimationUtility.CalculateTransformPath(transform, root.transform) +
                    "|" + obj.GetType().FullName + "|" + index, obj);
            }
        }
        /// <summary>要求对象具有非零本地 fileID，无法证明身份时不继续生成映射。</summary>
        private static long LocalId(Object obj)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long id) || id == 0)
            {
                throw new InvalidOperationException("无法读取资产对象身份：" + obj);
            }

            return id;
        }
        /// <summary>直接读取适配组件的序列化配置，避免属性访问承接旧值而改变待保存证据。</summary>
        private static float[] AdaptationValues(AutomaticAdaptationCanvasScaler adaptation)
        {
            var serialized = new SerializedObject(adaptation);
            var resolution = serialized.FindProperty("referenceResolution").vector2Value;
            return new[] { resolution.x, resolution.y, serialized.FindProperty("automaticMatch").boolValue ? 1f : 0f,
                serialized.FindProperty("manualMatch").floatValue };
        }
        /// <summary>按固定字段顺序记录原生 CanvasScaler 配置，供迁移后精确恢复和比较。</summary>
        private static float[] ScaleValues(CanvasScaler scaler) => new[] { (float)scaler.uiScaleMode, scaler.referencePixelsPerUnit,
            scaler.scaleFactor, scaler.referenceResolution.x, scaler.referenceResolution.y, (float)scaler.screenMatchMode,
            scaler.matchWidthOrHeight, (float)scaler.physicalUnit, scaler.fallbackScreenDPI, scaler.defaultSpriteDPI, scaler.dynamicPixelsPerUnit };
        /// <summary>恢复旧缩放配置后保存，按需保留本地身份并复检 GUID/fileID；复检发生在落盘之后，不提供回滚。</summary>
        private static void SaveChecked(GameObject contents, string path, Identity before)
        {
            var scaler = contents.GetComponentInChildren<CanvasScaler>(true);
            if (before.Scaler != null)
            {
                if (!scaler)
                {
                    throw new InvalidOperationException("迁移丢失既有 CanvasScaler，拒绝保存：" + path);
                }
                // ExecuteInEditMode 的适配组件可能在加载内容时更新 match；资产写入仍保留原生序列化配置。
                var values = before.Scaler;
                scaler.uiScaleMode = (CanvasScaler.ScaleMode)values[0];
                scaler.referencePixelsPerUnit = values[1];
                scaler.scaleFactor = values[2];
                scaler.referenceResolution = new Vector2(values[3], values[4]);
                scaler.screenMatchMode = (CanvasScaler.ScreenMatchMode)values[5];
                scaler.matchWidthOrHeight = values[6];
                scaler.physicalUnit = (CanvasScaler.Unit)values[7];
                scaler.fallbackScreenDPI = values[8];
                scaler.defaultSpriteDPI = values[9];
                scaler.dynamicPixelsPerUnit = values[10];
                if (!ScaleValues(scaler).SequenceEqual(values))
                {
                    throw new InvalidOperationException("无法保留 CanvasScaler 配置。");
                }
            }
            if (before.Adaptation != null)
            {
                var adaptation = contents.GetComponentInChildren<AutomaticAdaptationCanvasScaler>(true);
                if (!adaptation)
                {
                    throw new InvalidOperationException("迁移丢失既有适配组件：" + path);
                }

                var serialized = new SerializedObject(adaptation);
                var values = before.Adaptation;
                serialized.FindProperty("referenceResolution").vector2Value = new Vector2(values[0], values[1]);
                serialized.FindProperty("automaticMatch").boolValue = values[2] != 0;
                serialized.FindProperty("manualMatch").floatValue = values[3];
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            if (!PrefabUtility.SaveAsPrefabAsset(contents, path))
            {
                throw new InvalidOperationException("Prefab 保存失败：" + path);
            }

            PreserveLocalIds(path, before);
            var after = FileIds(Required(path));
            if (AssetDatabase.AssetPathToGUID(path) != before.Guid || before.Objects.Any(pair => !after.TryGetValue(pair.Key, out var type) || type != pair.Value))
            {
                throw new InvalidOperationException("保存后 GUID/fileID 核验失败；迁移尚未验收，请检查该资产 diff，不要继续集成：" + path);
            }
        }

        /// <summary>按旧层级键恢复嵌套对象的本地身份，必要时改写文本 Prefab 并同步重导入，不改带 GUID 的来源引用。</summary>
        private static void PreserveLocalIds(string path, Identity before)
        {
            var objects = KeyedObjects(Required(path)).ToDictionary(pair => pair.Key, pair => pair.Value);
            var replacements = new Dictionary<long, long>();
            var fullPath = PhysicalPath(path);
            var text = File.ReadAllText(fullPath);
            var declarations = new StringBuilder();
            foreach (var pair in before.Keys)
            {
                if (!objects.TryGetValue(pair.Key, out var obj))
                {
                    throw new InvalidOperationException("迁移丢失既有对象：" + pair.Key);
                }

                long current = LocalId(obj);
                if (current == pair.Value)
                {
                    continue;
                }

                replacements.Add(current, pair.Value);
                if (Regex.IsMatch(text, @"(?m)^--- !u!\d+ &" + current + @"(?: stripped)?\r?$"))
                {
                    continue;
                }

                var source = PrefabUtility.GetCorrespondingObjectFromSource(obj);
                var handle = PrefabUtility.GetPrefabInstanceHandle(obj);
                if (!source || !handle)
                {
                    throw new InvalidOperationException("仅支持保留转换后的嵌套 Prefab 对象身份：" + pair.Key);
                }

                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long sourceId);
                int classId = obj is GameObject ? 1 : obj is RectTransform ? 224 : obj is Transform ? 4 : obj is Canvas ? 223 : obj is MonoBehaviour ? 114 :
                    throw new InvalidOperationException("不能安全恢复此类嵌套对象身份：" + obj.GetType());
                declarations.Append("--- !u!").Append(classId).Append(" &").Append(current).Append(" stripped\n")
                    .Append(classId == 114 ? "MonoBehaviour" : obj.GetType().Name).Append(":\n")
                    .Append("  m_CorrespondingSourceObject: {fileID: ").Append(sourceId).Append(", guid: ").Append(guid).Append(", type: 3}\n")
                    .Append("  m_PrefabInstance: {fileID: ").Append(LocalId(handle)).Append("}\n  m_PrefabAsset: {fileID: 0}\n");
                if (obj is MonoBehaviour behaviour)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(MonoScript.FromMonoBehaviour(behaviour), out string scriptGuid, out long scriptId);
                    declarations.Append("  m_GameObject: {fileID: ").Append(LocalId(behaviour.gameObject)).Append("}\n")
                        .Append("  m_Enabled: ").Append(behaviour.enabled ? 1 : 0).Append("\n  m_EditorHideFlags: 0\n")
                        .Append("  m_Script: {fileID: ").Append(scriptId).Append(", guid: ").Append(scriptGuid).Append(", type: 3}\n")
                        .Append("  m_Name: \n  m_EditorClassIdentifier: \n");
                }
            }
            if (replacements.Count == 0)
            {
                return;
            }

            if (!text.StartsWith("%YAML 1.1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("身份保留仅支持已核验的文本 Prefab：" + path);
            }
            // Unity 2022.3 会重新生成匹配组件的嵌套 fileID；显式 stripped 记录保留外部引用，不能改带 GUID 的来源引用。
            text += declarations;
            string Replace(Match match) => match.Groups[1].Value +
                (replacements.TryGetValue(long.Parse(match.Groups[2].Value), out long old) ? old.ToString() : match.Groups[2].Value) + match.Groups[3].Value;
            text = Regex.Replace(text, @"(?m)(^--- !u!\d+ &)(-?\d+)([^\r\n]*)", Replace);
            text = Regex.Replace(text, @"(\{fileID:\s*)(-?\d+)(\s*\})", Replace);
            File.WriteAllText(fullPath, text, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static string PhysicalPath(string assetPath)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return package == null ? Path.GetFullPath(assetPath) : Path.Combine(package.resolvedPath, assetPath.Substring(package.assetPath.Length + 1));
        }
    }
}
