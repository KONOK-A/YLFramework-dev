using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Tests
{
    [Explicit("主Editor专属保护验收，使用DomainMainAcceptance的Protection集合显式运行。")]
    [Category("YL.Special.MainEditor")]
    public sealed class DomainMainAcceptanceTests
    {
        private static Type Entry => Type.GetType("YLFramework.Tests.DomainMain.DomainMainAcceptance, YLFramework.Tests.Tools.Editor", true);

        [Test]
        public void TestRunnerEntryUsesAnOwnedEmptySceneInsteadOfOriginalAutoRunHosts()
        {
            string scene = SessionState.GetString("YLFramework.Domain.MainAcceptance.TestScene", "");
            Assert.That(scene,
                Does.StartWith("Assets/Tests/YLFramework/Fixtures/Extensions/Domain/Main-"));
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(scene), Is.Not.Null);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(scene)), Does.Not.Contain("GameObject:"));
        }

        [Test]
        public void SaveAssetsKeepsAnUnownedMaterialDirtyAndItsDiskBytesUnchanged()
        {
            Assert.That(SessionState.GetBool("YLFramework.Domain.MainAcceptance.Protection", false), Is.True);
            string path = "Assets/DomainProtectionProbe-" + Guid.NewGuid().ToString("N") + ".mat";
            var material = new Material(Shader.Find("Sprites/Default"));
            string evidence = (string)Entry.GetField("Evidence").GetRawConstantValue();
            try
            {
                AssetDatabase.CreateAsset(material, path);
                byte[] original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(evidence + "/protected-probe-" + AssetDatabase.AssetPathToGUID(path) + ".json"),
                    JsonUtility.ToJson(new ProbeReceipt { path = path, guid = AssetDatabase.AssetPathToGUID(path) }));
                material.color = Color.red;
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(original));
                Assert.That(EditorUtility.IsDirty(material), Is.True);
                Assert.That(material.color, Is.EqualTo(Color.red));
            }
            finally
            {
                if (AssetDatabase.LoadMainAssetAtPath(path))
                {
                    AssetDatabase.DeleteAsset(path);
                }
                else if (material)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }
        }

        [Test]
        public void OwnershipRejectsExistingUserContentAndUnrelatedNewPaths()
        {
            var own = Entry.GetMethod("OwnPath");
            Assert.That(() => own.Invoke(null, new object[] { "Assets/Games" }), Throws.TypeOf<TargetInvocationException>());
            Assert.That(() => own.Invoke(null, new object[] { "Assets/NotDomainOwned-" + Guid.NewGuid().ToString("N") }), Throws.TypeOf<TargetInvocationException>());
        }

        [Test] public void UnownedIntegerMapChangesAreRejectedBeforeMutation() => CheckMap(false, true);
        [Test] public void UnownedStringMapChangesAreRejectedBeforeMutation() => CheckMap(true, true);
        [Test] public void OwnedIntegerMapKeyMayBeCreatedWithoutChangingOtherKeys() => CheckMap(false, false);
        [Test] public void OwnedStringMapKeyMayBeCreatedWithoutChangingOtherKeys() => CheckMap(true, false);

        [Test]
        public void ScheduledFailureRecoveryRetainsItsOwnerUntilUtfIsInactive()
        {
            var defer = Entry.GetMethod("DeferRunRestore", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(defer, Is.Not.Null, "已调度异常必须复用UTF终态恢复队列。");
            string output = SessionState.GetString("YLFramework.Domain.MainAcceptance.Output", "");
            string settings = SessionState.GetString("YLFramework.Domain.MainAcceptance.Settings", "");
            Assert.That(output, Is.Not.Empty);
            Assert.That(settings, Is.Not.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/settings-restored.json")), Is.False);
            defer.Invoke(null, null);
            Entry.GetMethod("FinishTests", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.That(SessionState.GetString("YLFramework.Domain.MainAcceptance.Output", ""), Is.EqualTo(output));
            Assert.That(SessionState.GetString("YLFramework.Domain.MainAcceptance.Settings", ""), Is.EqualTo(settings));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output + "/settings-restored.json")), Is.False, "UTF仍活跃时不可发布恢复或清owner。");
        }

        private static void CheckMap(bool strings, bool conflict)
        {
            var method = Entry.GetMethod("ValidateUnownedMap", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "Restore缺少写入前的非Standalone键冲突检查。");
            var type = Entry.GetNestedType("Map", BindingFlags.NonPublic);
            var snapshot = Activator.CreateInstance(type);
            type.GetField("keys").SetValue(snapshot, new[] { "Android" });
            type.GetField("strings").SetValue(snapshot, strings);
            type.GetField("texts").SetValue(snapshot, strings ? new[] { "original" } : null);
            type.GetField("numbers").SetValue(snapshot, strings ? null : new[] { 1 });
            var fixture = ScriptableObject.CreateInstance<DomainMapGuardFixture>();
            fixture.numbers = new[] { new DomainMapNumber { first = "Android", second = conflict ? 2 : 1 }, new DomainMapNumber { first = "Standalone", second = 2 } };
            fixture.texts = new[] { new DomainMapText { first = "Android", second = conflict ? "user-change" : "original" }, new DomainMapText { first = "Standalone", second = "owned-define" } };
            try
            {
                string before = EditorJsonUtility.ToJson(fixture);
                using var serialized = new SerializedObject(fixture);
                var args = new[] { (object)serialized.FindProperty(strings ? "texts" : "numbers"), snapshot };
                if (conflict)
                {
                    Assert.That(() => method.Invoke(null, args), Throws.TypeOf<TargetInvocationException>().With.InnerException.TypeOf<InvalidOperationException>());
                }
                else
                {
                    Assert.That(() => method.Invoke(null, args), Throws.Nothing);
                }

                Assert.That(EditorJsonUtility.ToJson(fixture), Is.EqualTo(before), "冲突预检不可先覆盖未保存的其他平台值。");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
            }
        }

        [Serializable]
        private sealed class ProbeReceipt
        {
            public string path;
            public string guid;
        }
    }

    public sealed class DomainMainAcceptanceInputTests
    {
        [TestCase("OriginalGame")]
        [TestCase("UnknownScope")]
        public void InvalidScopeIsRejectedBeforeAcquiringEditorOwnership(string scope)
        {
            var entry = Type.GetType("YLFramework.Tests.DomainMain.DomainMainAcceptance, YLFramework.Tests.Tools.Editor", true);
            const string Key = "YLFramework.Domain.MainAcceptance";
            string output = SessionState.GetString(Key + ".Output", "");
            string settings = SessionState.GetString(Key + ".Settings", "");
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            bool reloadEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var reload = EditorSettings.enterPlayModeOptions;

            var error = Assert.Throws<TargetInvocationException>(() => entry.GetMethod("RunTests")
                .Invoke(null, new object[] { "PlayMode", "", scope, false }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>(), "Reject scope before Begin checks editor ownership or a run name.");
            Assert.That(((ArgumentException)error.InnerException).ParamName, Is.EqualTo("scope"));
            Assert.That(SessionState.GetString(Key + ".Output", ""), Is.EqualTo(output));
            Assert.That(SessionState.GetString(Key + ".Settings", ""), Is.EqualTo(settings));
            Assert.That(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle, Is.EqualTo(scene));
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.EqualTo(reloadEnabled));
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(reload));
        }
    }

    [Serializable]
    public struct DomainMapNumber
    {
        public string first;
        public int second;
    }
    [Serializable]
    public struct DomainMapText
    {
        public string first;
        public string second;
    }
    public sealed class DomainMapGuardFixture : ScriptableObject
    {
        public DomainMapNumber[] numbers;
        public DomainMapText[] texts;
    }
}
