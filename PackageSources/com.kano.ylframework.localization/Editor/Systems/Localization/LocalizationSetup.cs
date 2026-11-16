using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using YLFramework.Localization;
using RuntimeGroup = YLFramework.Localization.LocalizationGroup;

namespace YLFramework.Editor.Localization
{
    /// <summary>复用官方窗口和 drawer，仅增加当前引用的定位及稳定身份复制。</summary>
    [InitializeOnLoad]
    public static class LocalizationSetup
    {
        static LocalizationSetup() => EditorApplication.contextualPropertyMenu += AddReferenceActions;

        [MenuItem("YLFramework/Localization/Official Settings")]
        private static void OpenSettings() => SettingsService.OpenProjectSettings("Project/Localization");

        [MenuItem("YLFramework/Localization/Official Tables")]
        private static void OpenTables() => EditorApplication.ExecuteMenuItem("Window/Asset Management/Localization Tables");

        /// <summary>只读检查当前组、官方引用、字体和同目标重复写入，不扫描或修复全项目。</summary>
        public static IReadOnlyList<string> ValidateGroup(RuntimeGroup group)
        {
            var errors = new List<string>();
            if (!group)
            {
                errors.Add("Select a LocalizationGroup.");
                return errors;
            }
            if (!group.Content || group.Content.transform == group.transform || !group.Content.transform.IsChildOf(group.transform))
                errors.Add("Content must be a dedicated child CanvasGroup, not the Panel/root CanvasGroup.");
            foreach (var root in new[] { group.LoadingRoot, group.FailureRoot })
                if (root && (root == group.gameObject || !root.transform.IsChildOf(group.transform) ||
                    (group.Content && (root.transform.IsChildOf(group.Content.transform) || group.Content.transform.IsChildOf(root.transform)))))
                    errors.Add(root.name + ": loading/error roots must not contain or belong to the business content.");
            var official = group.GetComponentsInChildren<LocalizeStringEvent>(true);
            foreach (var member in group.GetComponentsInChildren<LocalizationBehaviour>(true))
            {
                if (member.GetComponentInParent<RuntimeGroup>(true) != group)
                    continue;
                errors.AddRange(LocalizationReferenceAuthoring.ValidateObject(member));
                var stringField = member.GetType().GetField("_stringReference", BindingFlags.Instance | BindingFlags.NonPublic);
                var text = member.GetComponent<Text>();
                if (stringField != null)
                {
                    if (!(stringField.GetValue(member) is LocalizedString reference) || reference.IsEmpty)
                        errors.Add(member.name + ": choose a required String Table reference.");
                    var fontField = member.GetType().GetField("_fontReference", BindingFlags.Instance | BindingFlags.NonPublic);
                    var font = fontField?.GetValue(member) as LocalizedReference;
                    if ((font == null || font.IsEmpty) && text && !text.font)
                        errors.Add(member.name + ": no original font and no localized font reference.");
                    foreach (var writer in official)
                        for (var index = 0; index < writer.OnUpdateString.GetPersistentEventCount(); index++)
                        {
                            var target = writer.OnUpdateString.GetPersistentTarget(index) as Component;
                            var method = writer.OnUpdateString.GetPersistentMethodName(index);
                            if (target && target.gameObject == member.gameObject && (method == "set_text" || method == "SetText"))
                                errors.Add(member.name + ": duplicate text writer in official LocalizeStringEvent and YL binding.");
                        }
                }
                if (member is LocalizeSprite sprite && (sprite.SpriteReference == null || sprite.SpriteReference.IsEmpty))
                    errors.Add(member.name + ": choose a required Sprite Table reference.");
            }
            return errors;
        }

        [MenuItem("CONTEXT/LocalizationGroup/Validate Current Content")]
        private static void ValidateSelectedGroup(MenuCommand command)
        {
            var group = command.context as RuntimeGroup;
            var errors = ValidateGroup(group);
            if (errors.Count == 0)
                Debug.Log("Localization content configuration is valid.", group);
            else
                foreach (var error in errors)
                    Debug.LogWarning(error, group);
        }

        private static void AddReferenceActions(GenericMenu menu, SerializedProperty property)
        {
            var target = property.serializedObject.targetObject;
            if (!target || property.serializedObject.isEditingMultipleObjects)
                return;
            var name = property.propertyPath.Split('.')[0];
            FieldInfo field = null;
            for (var type = target.GetType(); type != null && field == null; type = type.BaseType)
                field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || !typeof(LocalizedReference).IsAssignableFrom(field.FieldType))
                return;
            var selected = field;
            menu.AddItem(new GUIContent("YL Localization/Copy Stable Excel Reference"), false, () =>
                Run(target, () => GUIUtility.systemCopyBuffer = LocalizationReferenceAuthoring.CopyReference((LocalizedReference)selected.GetValue(target))));
            menu.AddItem(new GUIContent("YL Localization/Locate Official Table"), false, () =>
                Run(target, () => LocalizationReferenceAuthoring.PingReference((LocalizedReference)selected.GetValue(target))));
        }

        private static void Run(UnityEngine.Object target, Action action)
        {
            if (!target)
                return;
            try
            {
                action();
            }
            catch (Exception error) { EditorUtility.DisplayDialog("Localization Reference", error.Message, "Close"); }
        }
    }
}
