using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace YLFramework.Tests.ResourceEditor
{
    internal sealed class ResourceAddressablesGeneratorFixture : IDisposable
    {
        internal const string InputPrefix = "Assets/YLGeneratorInputs-";
        internal const string SettingsRelativePath = "/Settings/GeneratorSettings.asset";
        internal const string FirstContent = "official generator content";
        internal AddressableAssetSettings Settings
        {
            get;
        }

        internal ResourceAddressablesGeneratorFixture(string root, string[] addresses, string[] labels = null, bool persisted = false)
        {
            RequireInputRoot(root);
            if (persisted)
            {
                Assert.That(AssetDatabase.IsValidFolder(root + "/Settings"), Is.False);
                Assert.That(AssetDatabase.CreateFolder(root, "Settings"), Is.Not.Empty);
            }
            Settings = AddressableAssetSettings.Create(persisted ? root + "/Settings" : "", "GeneratorSettings", false, persisted);
            var group = Settings.CreateGroup("GeneratorInputs", true, false, false, null, typeof(BundledAssetGroupSchema));
            for (int index = 0; index < addresses.Length; index++)
            {
                var path = InputPath(root, index);
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(path), Is.Not.Null, "Only imported test assets may become official Entries.");
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(guid, Has.Length.EqualTo(32));
                Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(path));
                var entry = Settings.CreateOrMoveEntry(guid, group, false, false);
                Assert.That(entry, Is.Not.Null);
                entry.SetAddress(addresses[index], false);
            }
            foreach (var label in labels ?? Array.Empty<string>())
            {
                Settings.AddLabel(label, false);
                foreach (var entry in group.entries)
                    entry.SetLabel(label, true, false, false);
            }
            if (persisted)
                Save(Settings);
        }

        internal static string InputPath(string root, int index) => root + "/Input" + index + ".txt";

        internal static void CreateInputs(string root)
        {
            Assert.That(root.StartsWith(InputPrefix, StringComparison.Ordinal), Is.True);
            Assert.That(Path.GetDirectoryName(root).Replace('\\', '/'), Is.EqualTo("Assets"));
            Assert.That(Directory.Exists(root) || File.Exists(root) || File.Exists(root + ".meta"), Is.False, "Never adopt an existing generator input.");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(root)), Is.Not.Empty);
            for (int index = 0; index < 8; index++)
            {
                var path = InputPath(root, index);
                File.WriteAllText(path, index == 0 ? FirstContent : "generator input " + index, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(path), Is.Not.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Has.Length.EqualTo(32));
            }
        }

        internal static void Save(AddressableAssetSettings settings)
        {
            foreach (var group in settings.groups.Where(group => group))
            {
                EditorUtility.SetDirty(group);
                AssetDatabase.SaveAssetIfDirty(group);
            }
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        internal static string Fingerprint(string root)
        {
            RequireInputRoot(root);
            using var hash = SHA256.Create();
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Concat(new[] { root + ".meta" })
                .OrderBy(path => path, StringComparer.Ordinal);
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", files.Select(path =>
                path + ":" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path))))))));
        }

        internal static void DeleteInputs(string root, string fingerprint)
        {
            Assert.That(Fingerprint(root), Is.EqualTo(fingerprint), "Refuse to delete replaced or externally edited generator inputs.");
            Assert.That(AssetDatabase.DeleteAsset(root), Is.True);
        }

        private static void RequireInputRoot(string root)
        {
            Assert.That(root.StartsWith(InputPrefix, StringComparison.Ordinal) && Path.GetDirectoryName(root).Replace('\\', '/') == "Assets", Is.True);
            Assert.That(Directory.Exists(root), Is.True);
            Assert.That((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, Is.True);
            var pending = new System.Collections.Generic.Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(entry);
                    Assert.That((attributes & FileAttributes.ReparsePoint) == 0, Is.True, "Generator inputs cannot follow links.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                }
            }
        }

        internal static bool Publish(string directory, string source)
        {
            var writer = typeof(YLFramework.Editor.Resource.ResourceCodeGenerator).Assembly.GetType("YLFramework.Editor.Resource.ResourceReferenceOutput", true);
            var method = writer.GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            try
            {
                return (bool)method.Invoke(null, new object[] { directory, source });
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        public void Dispose()
        {
            if (!Settings || AssetDatabase.Contains(Settings))
                return;
            var owned = Settings.groups.Where(group => group).SelectMany(group => group.Schemas.Cast<UnityEngine.Object>())
                .Concat(Settings.DataBuilders.Cast<UnityEngine.Object>()).Concat(Settings.groups.Cast<UnityEngine.Object>())
                .Append(Settings).Where(value => value && !AssetDatabase.Contains(value)).Distinct().ToArray();
            foreach (var value in owned)
                UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
