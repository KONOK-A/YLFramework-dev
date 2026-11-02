using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Core;
using YLFramework.UI;

namespace YLFramework.Editor.Core
{
    /// <summary>只读检查正式 UPM 模板；修改产品资产的作者命令仅属于开发仓库。</summary>
    public static class UIPanelAssetMigration
    {
        /// <summary>默认 UI 根资产路径。</summary>
        public const string BaseRootPath = "Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab";
        /// <summary>完整框架模板路径。</summary>
        public const string TemplatePath = "Packages/com.kano.ylframework/Prefabs/YLFramework.prefab";
        /// <summary>默认提示资产路径。</summary>
        public const string TipsPath = "Packages/com.kano.ylframework/Prefabs/UITips.prefab";
        /// <summary>完整模板的提示 Variant 路径。</summary>
        public const string TipsVariantPath = "Packages/com.kano.ylframework/Prefabs/UITipsOverlay.prefab";
        /// <summary>正式取色窗口资产路径。</summary>
        public const string ColorPath = "Packages/com.kano.ylframework/Prefabs/ColorSelector.prefab";

        [MenuItem("YLFramework/Core/UI Panel/核验资产迁移")]
        private static void ValidateMenu() => Debug.Log(ValidateAssets());

        /// <summary>校验组件、层映射和提示身份，不写入包、场景或 Prefab。</summary>
        public static string ValidateAssets()
        {
            var basic = Required(BaseRootPath);
            var template = Required(TemplatePath);
            var tips = Required(TipsPath);
            var variant = Required(TipsVariantPath);
            var color = Required(ColorPath);
            foreach (var prefab in new[] { basic, template, tips, variant, color })
            {
                foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
                {
                    foreach (var component in transform.GetComponents<Component>())
                    {
                        if (!component)
                        {
                            throw new InvalidOperationException("Package asset contains a missing script: " + prefab.name);
                        }
                    }
                }
            }
            var contexts = template.GetComponentsInChildren<UIRootContext>(true);
            if (!template.GetComponent<YLFrameworkRoot>() || contexts.Length != 1 || !basic.GetComponent<UIRootContext>() ||
                !color.GetComponent<ColorSelectorWindow>() || !tips.GetComponent<UITipsItem>())
            {
                throw new InvalidOperationException("Required YL UI template components are missing; reinstall the package.");
            }
            foreach (var context in new[] { basic.GetComponent<UIRootContext>(), contexts[0] })
            {
                if (!context.GetComponent<Canvas>() || !context.GetComponent<CanvasScaler>() || !context.GetComponent<GraphicRaycaster>())
                {
                    throw new InvalidOperationException("The UI root is missing native Canvas components.");
                }
                var layers = new SerializedObject(context).FindProperty("uiLayers");
                if (layers.arraySize == 0)
                {
                    throw new InvalidOperationException("The UI root has no explicit layer mapping.");
                }
                for (int i = 0; i < layers.arraySize; i++)
                {
                    var layer = layers.GetArrayElementAtIndex(i);
                    var root = layer.FindPropertyRelative("root").objectReferenceValue as Transform;
                    var mask = layer.FindPropertyRelative("maskImage").objectReferenceValue as Image;
                    if (!root || !root.IsChildOf(context.transform) ||
                        (layer.FindPropertyRelative("enableMask").boolValue && (!mask || !mask.transform.IsChildOf(root))))
                    {
                        throw new InvalidOperationException("The UI layer or mask is outside its root.");
                    }
                }
            }
            var presenters = template.GetComponentsInChildren<UITipsPresenter>(true);
            if (presenters.Length != 1 || !presenters[0].transform.IsChildOf(contexts[0].transform) ||
                new SerializedObject(presenters[0]).FindProperty("uiTipItemPrefab").objectReferenceValue != variant ||
                PrefabUtility.GetPrefabAssetType(variant) != PrefabAssetType.Variant || PrefabUtility.GetCorrespondingObjectFromSource(variant) != tips)
            {
                throw new InvalidOperationException("The tips Variant or presenter binding does not match the shipped template.");
            }
            return "UI Panel package assets validated read-only. This is not PlayMode, input or Player acceptance.";
        }

        private static GameObject Required(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path) ??
            throw new InvalidOperationException("Required package asset is missing; reinstall YLFramework: " + path);
    }
}
