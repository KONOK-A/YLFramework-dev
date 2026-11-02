using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace YLFramework.Editor.Buff
{
    /// <summary>显式迁移旧 Buff 项目设置，保留字节备份并校对重载结果，不自动回滚失败操作。</summary>
    internal static class BuffSettingsMigration
    {
        internal const string MenuPath = "YLFramework/Buff/Migrate Legacy Project Settings";

        /// <summary>检查旧文件与目标占用后备份、移动并重载；无旧文件时返回 null，否则返回备份路径。</summary>
        internal static string Migrate()
        {
            string previousPath = BuffSystemSettings.PreviousFilePath;
            string currentPath = BuffSystemSettings.CurrentFilePath;
            if (!File.Exists(previousPath))
            {
                return null;
            }

            if (File.Exists(currentPath))
            {
                throw new IOException("Refusing conflicting Buff settings. Keep both files for manual review: " +
                    previousPath + " and " + currentPath);
            }

            byte[] original = File.ReadAllBytes(previousPath);
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(previousPath);
            try
            {
                if (objects.Length != 1 || !objects[0] || objects[0].GetType() != typeof(BuffSystemSettings))
                {
                    throw new IOException("Refusing incompatible legacy Buff settings: " + previousPath);
                }

                // 只序列化字段；Buff 集合 getter 可能同步资产并保存设置，验证时不触发它们。
                string snapshot = EditorJsonUtility.ToJson(objects[0]);
                if (!File.ReadAllBytes(previousPath).SequenceEqual(original))
                {
                    throw new IOException("Legacy Buff settings changed during validation: " + previousPath);
                }

                string backupPath = previousPath + "." + Guid.NewGuid().ToString("N") + ".bak";
                try
                {
                    using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        backup.Write(original, 0, original.Length);
                        backup.Flush(true);
                    }

                    if (!File.ReadAllBytes(backupPath).SequenceEqual(original))
                    {
                        throw new IOException("Buff settings backup failed byte verification: " + backupPath);
                    }

                    // Unity 反序列化后重新核对实际路径，再以不覆盖目标的方式移动文件。
                    previousPath = BuffSystemSettings.PreviousFilePath;
                    currentPath = BuffSystemSettings.CurrentFilePath;
                    if (!File.ReadAllBytes(previousPath).SequenceEqual(original))
                    {
                        throw new IOException("Legacy Buff settings changed before the move: " + previousPath);
                    }
                    File.Move(previousPath, currentPath);

                    if (!File.ReadAllBytes(currentPath).SequenceEqual(original))
                    {
                        throw new IOException("Migrated Buff settings failed byte verification: " + currentPath);
                    }

                    BuffSettingsProvider.Instance.Reload();
                    var reloaded = BuffSystemSettings.Reload();
                    if (EditorJsonUtility.ToJson(reloaded) != snapshot ||
                        !File.ReadAllBytes(currentPath).SequenceEqual(original) ||
                        !File.ReadAllBytes(backupPath).SequenceEqual(original))
                    {
                        throw new IOException("Migrated Buff settings fields or object references did not reload unchanged.");
                    }

                    return backupPath;
                }
                catch (IOException exception)
                {
                    throw new IOException("Buff settings migration stopped without automatic rollback. Inspect " +
                        previousPath + " and " + currentPath + ". Backup candidate (if created): " + backupPath +
                        ". " + exception.Message, exception);
                }
            }
            finally
            {
                foreach (var loadedObject in objects)
                {
                    if (loadedObject)
                    {
                        UnityEngine.Object.DestroyImmediate(loadedObject);
                    }
                }
            }
        }

        /// <summary>从菜单执行迁移，并展示备份位置或需人工检查的失败信息。</summary>
        [MenuItem(MenuPath)]
        private static void MigrateFromMenu()
        {
            try
            {
                string backupPath = Migrate();
                EditorUtility.DisplayDialog("Buff Settings Migration", backupPath == null
                    ? "No legacy Buff settings found. No files were changed."
                    : "Buff settings migrated and reloaded. Keep the verified backup:\n" + backupPath, "OK");
            }
            catch (IOException exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Buff Settings Migration Failed", exception.Message, "OK");
            }
        }
    }
}
