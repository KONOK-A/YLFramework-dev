using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Editor.Common;

namespace YLFramework.Core.Editor.Tests
{
    public class CoreSerializedPropertyTests
    {
        [Serializable]
        public class Data
        {
            public int number = 1;
            public string text = "original";
        }
        [Serializable]
        public class OtherData
        {
            public int number = 7;
            public int text = 8;
        }
        public class Fixture : ScriptableObject
        {
            public int number = 1;
            public string text = "original";
            public UnityEngine.Object reference;
            public Texture2D texture;
            public Data data = new Data();
            public OtherData other = new OtherData();
            public int[] array = { 1, 2 };
        }

        private Fixture _first;
        private Fixture _second;
        private string _clipboard;

        [SetUp]
        public void SetUp()
        {
            _first = ScriptableObject.CreateInstance<Fixture>();
            _second = ScriptableObject.CreateInstance<Fixture>();
            _clipboard = EditorGUIUtility.systemCopyBuffer;
        }

        [TearDown]
        public void TearDown()
        {
            EditorGUIUtility.systemCopyBuffer = _clipboard;
            Undo.ClearUndo(_first);
            Undo.ClearUndo(_second);
            UnityEngine.Object.DestroyImmediate(_first);
            UnityEngine.Object.DestroyImmediate(_second);
        }

        [Test]
        public void NullObjectReferenceHasReadableTextAndRoundTripsWithoutDereferencing()
        {
            using var source = new SerializedObject(_first);
            using var destination = new SerializedObject(_second);
            var property = source.FindProperty("reference");
            Assert.That(property.GetValueText(), Is.EqualTo("None"));
            property.CopyToClipboard();
            Assert.That(destination.FindProperty("reference").IsCanPaste(), Is.True);
            destination.FindProperty("reference").PasteFromClipboard();
            Assert.That(_second.reference, Is.Null);
        }

        [Test]
        public void TypedObjectReferenceRejectsIncompatibleObjectsButKeepsValidReferences()
        {
            using var source = new SerializedObject(_first);
            using var destination = new SerializedObject(_second);
            _first.reference = _first;
            source.Update();
            source.FindProperty("reference").CopyToClipboard();
            var property = destination.FindProperty("texture");
            Assert.That(property.IsCanPaste(), Is.False);
            Assert.Throws<InvalidOperationException>(() => property.PasteFromClipboard());
            Assert.That(_second.texture, Is.Null);
            Assert.That(destination.hasModifiedProperties, Is.False);
            var texture = new Texture2D(1, 1);
            try
            {
                _first.reference = texture;
                source.Update();
                source.FindProperty("reference").CopyToClipboard();
                Assert.That(property.IsCanPaste(), Is.True);
                property.PasteFromClipboard();
                Assert.That(_second.texture, Is.SameAs(texture));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [TestCase("UnityEditor.MissingClipboard61", "SetSerializedProperty", typeof(void), true)]
        [TestCase("UnityEditor.Clipboard", "SetSerializedProperty", typeof(bool), true)]
        [TestCase("UnityEditor.Clipboard", "SetSerializedProperty", typeof(void), false)]
        public void MissingOrChangedNativeApiIsRejectedWithVersionAndMemberFeedback(string typeName, string name, Type result, bool correctParameter)
        {
            var resolve = typeof(SerializedPropertyExtensions).GetMethod("GetClipboardMethod", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(resolve, Is.Not.Null);
            var failure = Assert.Catch(() => resolve.Invoke(null, new object[]
                { typeName, name, result, new[] { correctParameter ? typeof(SerializedProperty) : typeof(string) } }));
            Assert.That(failure.GetBaseException(), Is.TypeOf<NotSupportedException>());
            Assert.That(failure.GetBaseException().Message, Does.Contain(Application.unityVersion).And.Contain(name));
        }

        [Test]
        public void IncompatibleScalarAndPartiallyCompatibleStructAreRejectedBeforeAnyWrite()
        {
            _first.data.number = 42;
            using var source = new SerializedObject(_first);
            using var destination = new SerializedObject(_second);
            foreach (var pair in new[] { new[] { "number", "text" }, new[] { "data", "other" } })
            {
                source.FindProperty(pair[0]).CopyToClipboard();
                var property = destination.FindProperty(pair[1]);
                Assert.That(property.IsCanPaste(), Is.False);
                Assert.Throws<InvalidOperationException>(() => property.PasteFromClipboard());
                Assert.That(destination.hasModifiedProperties, Is.False);
                Assert.That(_second.text, Is.EqualTo("original"));
                Assert.That(_second.other.number, Is.EqualTo(7));
                Assert.That(_second.other.text, Is.EqualTo(8));
            }
        }

        [Test]
        public void MalformedNativePayloadCannotChangeTargetOrPendingEdits()
        {
            using var serialized = new SerializedObject(_first);
            serialized.FindProperty("text").stringValue = "pending";
            var property = serialized.FindProperty("number");
            foreach (string payload in new[] { "GenericPropertyJSON:{", "GenericPropertyJSON:{}" })
            {
                EditorGUIUtility.systemCopyBuffer = payload;
                Assert.That(property.IsCanPaste(), Is.False);
                Assert.Throws<InvalidOperationException>(() => property.PasteFromClipboard());
                Assert.That(_first.number, Is.EqualTo(1));
                Assert.That(_first.text, Is.EqualTo("original"));
                Assert.That(serialized.FindProperty("text").stringValue, Is.EqualTo("pending"));
                Assert.That(EditorGUIUtility.systemCopyBuffer, Is.EqualTo(payload));
            }
        }

        [Test]
        [Category("YL.NativeClipboard")]
        public void MixedCopyAndPasteAreExplicitlyRejectedWithoutChangingEitherObject()
        {
            _second.number = 2;
            using var single = new SerializedObject(_first);
            using var multiple = new SerializedObject(new UnityEngine.Object[] { _first, _second });
            var mixed = multiple.FindProperty("number");
            Assert.That(mixed.hasMultipleDifferentValues, Is.True);
            single.FindProperty("number").CopyToClipboard();
            string before = EditorGUIUtility.systemCopyBuffer;
            const string expected = "GenericPropertyJSON:{\"name\":\"number\",\"type\":0,\"val\":1}";
            ObserveClipboardRead("mixed-before", before, expected);
            Assert.That(before, Is.EqualTo(expected), "Native copy did not establish the owned clipboard baseline before mixed rejection.");
            Assert.Throws<InvalidOperationException>(() => mixed.CopyToClipboard());
            string after = EditorGUIUtility.systemCopyBuffer;
            ObserveClipboardRead("mixed-after", after, expected);
            Assert.That(after, Is.EqualTo(before));
            Assert.That(mixed.IsCanPaste(), Is.False);
            Assert.Throws<InvalidOperationException>(() => mixed.PasteFromClipboard());
            Assert.That(_first.number, Is.EqualTo(1));
            Assert.That(_second.number, Is.EqualTo(2));
            Assert.That(multiple.hasModifiedProperties, Is.False);
        }

        [Test]
        [Category("YL.NativeClipboard")]
        public void NativeClipboardCopyAfterMalformedHistoryEstablishesImmediateOwnedBaseline()
        {
            var clipboard = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Clipboard");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var has = clipboard.GetMethod("HasSerializedProperty", flags, null, Type.EmptyTypes, null);
            var copy = clipboard.GetMethod("SetSerializedProperty", flags, null, new[] { typeof(SerializedProperty) }, null);
            Assert.That(has, Is.Not.Null);
            Assert.That(copy, Is.Not.Null);
            using var source = new SerializedObject(_first);
            const string SeedPayload = "GenericPropertyJSON:{}";
            const string Expected = "GenericPropertyJSON:{\"name\":\"number\",\"type\":0,\"val\":1}";
            EditorGUIUtility.systemCopyBuffer = SeedPayload;
            bool hasPayload = (bool)has.Invoke(null, null);
            var state = clipboard.GetField("m_State", flags).GetValue(null);
            string nativeSnapshot = (string)state.GetType().GetField("m_RawContents", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(state);
            string snapshotObservation = CaptureClipboardRead("native-has-snapshot", nativeSnapshot, SeedPayload);
            EditorGUIUtility.systemCopyBuffer = string.Empty;
            string cleared = EditorGUIUtility.systemCopyBuffer;
            string clearedObservation = CaptureClipboardRead("native-cleared", cleared, string.Empty);
            copy.Invoke(null, new object[] { source.FindProperty("number") });
            string actual = EditorGUIUtility.systemCopyBuffer;
            string copyObservation = CaptureClipboardRead("native-copy", actual, Expected);
            TestContext.Out.WriteLine(snapshotObservation);
            TestContext.Out.WriteLine(clearedObservation);
            TestContext.Out.WriteLine(copyObservation);
            // NUnit 3.5 has no Assert.Multiple; booleans also keep unknown clipboard text out of failure output.
            Assert.That(new[]
            {
                hasPayload,
                nativeSnapshot == SeedPayload,
                cleared == string.Empty,
                actual == Expected
            }, Is.All.True, "Native clipboard checks in order: Has=true, cached owned payload, empty cleared buffer, copied owned number=1.");
        }

        [Test]
        public void NativeClipboardParserRoundTripsOwnedScalarWithoutSystemClipboard()
        {
            var assembly = typeof(UnityEditor.Editor).Assembly;
            var parser = assembly.GetType("UnityEditor.ClipboardParser");
            var json = assembly.GetType("UnityEditor.Json");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var dictionary = typeof(System.Collections.Generic.Dictionary<string, object>);
            var deserialize = json.GetMethod("Deserialize", flags, null, new[] { typeof(string) }, null);
            var serialize = json.GetMethod("Serialize", flags, null, new[] { typeof(object), typeof(bool), typeof(string) }, null);
            var parse = parser.GetMethod("ParseGenericSerializedProperty", flags, null, new[] { typeof(SerializedProperty), dictionary }, null);
            var write = parser.GetMethod("WriteGenericSerializedProperty", flags, null, new[] { typeof(SerializedProperty) }, null);
            Assert.That(deserialize, Is.Not.Null);
            Assert.That(serialize, Is.Not.Null);
            Assert.That(parse, Is.Not.Null);
            Assert.That(write, Is.Not.Null);
            using var destination = new SerializedObject(_second);
            var property = destination.FindProperty("number");
            var expected = deserialize.Invoke(null, new object[] { "{\"name\":\"number\",\"type\":0,\"val\":42}" });
            parse.Invoke(null, new[] { property, expected });
            Assert.That(property.intValue, Is.EqualTo(42));
            Assert.That(_second.number, Is.EqualTo(1), "Parser control must not apply the staged buffer.");
            var encoded = write.Invoke(null, new object[] { property });
            var text = (string)serialize.Invoke(null, new[] { encoded, false, "" });
            var actual = (System.Collections.Generic.Dictionary<string, object>)deserialize.Invoke(null, new object[] { text });
            Assert.That(actual["val"], Is.EqualTo(42L));
            Assert.That(actual["name"], Is.EqualTo("number"));
        }

        private static string CaptureClipboardRead(string phase, string value, string ownedValue)
        {
            bool owned = value == ownedValue;
            string state = value == string.Empty ? "Empty" : owned ? "KnownExpected" : "Other";
            int length = owned || value == string.Empty ? value.Length : -1;
            string native = "not-windows";
#if UNITY_EDITOR_WIN
            var window = GetOpenClipboardWindow();
            GetWindowThreadProcessId(window, out uint process);
            native = $"sequence={GetClipboardSequenceNumber()} openWindow={window.ToInt64()} openPid={process}";
#endif
            return $"YL_CLIPBOARD_READ phase={phase} state={state} ownedLength={length} {native}";
        }

        internal static void ObserveClipboardRead(string phase, string value, string ownedValue)
        {
            bool owned = value == ownedValue;
            int length = owned || value == string.Empty ? value.Length : -1;
            string native = "not-windows";
#if UNITY_EDITOR_WIN
            var window = GetOpenClipboardWindow();
            GetWindowThreadProcessId(window, out uint process);
            native = $"sequence={GetClipboardSequenceNumber()} openWindow={window.ToInt64()} openPid={process}";
#endif
            TestContext.Out.WriteLine($"YL_CLIPBOARD_READ phase={phase} owned={owned} empty={value == string.Empty} ownedLength={length} {native}");
        }

#if UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetOpenClipboardWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
#endif

        [Test]
        public void SameValueMultiObjectPasteSupportsActualUndoAndRedo()
        {
            using var source = new SerializedObject(_first);
            source.FindProperty("number").intValue = 42;
            source.FindProperty("number").CopyToClipboard();
            using var multiple = new SerializedObject(new UnityEngine.Object[] { _first, _second });
            Undo.IncrementCurrentGroup();
            var destination = multiple.FindProperty("number");
            Assert.That(destination.IsCanPaste(), Is.True);
            destination.PasteFromClipboard();
            Undo.FlushUndoRecordObjects();
            Assert.That(_first.number, Is.EqualTo(42));
            Assert.That(_second.number, Is.EqualTo(42));
            Undo.PerformUndo();
            Assert.That(_first.number, Is.EqualTo(1));
            Assert.That(_second.number, Is.EqualTo(1));
            Undo.PerformRedo();
            Assert.That(_first.number, Is.EqualTo(42));
            Assert.That(_second.number, Is.EqualTo(42));
            Undo.IncrementCurrentGroup();
        }

        [TestCase("data")]
        [TestCase("array")]
        public void NativeStructuredPayloadRoundTripsCompletely(string path)
        {
            _first.data.number = 42;
            _first.data.text = "copied";
            _first.array = new[] { 3, 4, 5 };
            using var source = new SerializedObject(_first);
            using var destination = new SerializedObject(_second);
            source.FindProperty(path).CopyToClipboard();
            Assert.That(destination.FindProperty(path).IsCanPaste(), Is.True);
            destination.FindProperty(path).PasteFromClipboard();
            if (path == "data")
            {
                Assert.That(_second.data.number, Is.EqualTo(42));
                Assert.That(_second.data.text, Is.EqualTo("copied"));
            }
            else
            {
                Assert.That(_second.array, Is.EqualTo(new[] { 3, 4, 5 }));
            }
        }

        [TestCase("data")]
        [TestCase("array")]
        public void SameValueMultiObjectStructuredPasteChangesEveryTargetAndSupportsUndo(string path)
        {
            using var source = new SerializedObject(_first);
            source.FindProperty("data.number").intValue = 42;
            source.FindProperty("data.text").stringValue = "copied";
            source.FindProperty("array").arraySize = 3;
            for (int i = 0; i < 3; i++)
            {
                source.FindProperty("array").GetArrayElementAtIndex(i).intValue = i + 3;
            }

            source.FindProperty(path).CopyToClipboard();
            using var multiple = new SerializedObject(new UnityEngine.Object[] { _first, _second });
            Undo.IncrementCurrentGroup();
            var property = multiple.FindProperty(path);
            Assert.That(property.IsCanPaste(), Is.True);
            property.PasteFromClipboard();
            Undo.FlushUndoRecordObjects();
            foreach (var target in new[] { _first, _second })
            {
                if (path == "data")
                {
                    Assert.That(target.data.number, Is.EqualTo(42));
                    Assert.That(target.data.text, Is.EqualTo("copied"));
                }
                else
                {
                    Assert.That(target.array, Is.EqualTo(new[] { 3, 4, 5 }));
                }
            }
            Undo.PerformUndo();
            foreach (var target in new[] { _first, _second })
            {
                Assert.That(target.data.number, Is.EqualTo(1));
                Assert.That(target.data.text, Is.EqualTo("original"));
                Assert.That(target.array, Is.EqualTo(new[] { 1, 2 }));
            }
            Undo.IncrementCurrentGroup();
        }

        [Test]
        public void PrefabInstancePasteRecordsOverrideAndUndoRedoWithoutChangingAsset()
        {
            string folder = "Assets/Tests/YLFramework/EditMode/Editor/Core/YLClipboardTest_" + Guid.NewGuid().ToString("N");
            string path = folder + "/Clipboard.prefab";
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            BoxCollider collider = null;
            try
            {
                AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode/Editor/Core", Path.GetFileName(folder));
                var go = EditorUtility.CreateGameObjectWithHideFlags("Clipboard", HideFlags.None, typeof(BoxCollider));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                PrefabUtility.SaveAsPrefabAsset(go, path);
                byte[] before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                string guid = AssetDatabase.AssetPathToGUID(path);
                instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), scene);
                collider = instance.GetComponent<BoxCollider>();
                using var source = new SerializedObject(go.GetComponent<BoxCollider>());
                source.FindProperty("m_Center").vector3Value = new Vector3(2, 3, 4);
                source.FindProperty("m_Center").CopyToClipboard();
                using var destination = new SerializedObject(collider);
                Undo.IncrementCurrentGroup();
                destination.FindProperty("m_Center").PasteFromClipboard();
                Undo.FlushUndoRecordObjects();
                destination.Update();
                Assert.That(collider.center, Is.EqualTo(new Vector3(2, 3, 4)));
                Assert.That(destination.FindProperty("m_Center").prefabOverride, Is.True);
                Undo.PerformUndo();
                destination.Update();
                Assert.That(collider.center, Is.EqualTo(Vector3.zero));
                Assert.That(destination.FindProperty("m_Center").prefabOverride, Is.False);
                Undo.PerformRedo();
                destination.Update();
                Assert.That(collider.center, Is.EqualTo(new Vector3(2, 3, 4)));
                Assert.That(destination.FindProperty("m_Center").prefabOverride, Is.True);
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            }
            finally
            {
                if (collider)
                {
                    Undo.ClearUndo(collider);
                }

                if (instance)
                {
                    Undo.ClearUndo(instance);
                }

                Undo.IncrementCurrentGroup();
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        [Category("YL.Special.CoreSession")]
        public void CurrentUIAcceptancePrefabKeepsBothObjectReferencesAndIdentity()
        {
            var type = System.Reflection.Assembly.Load("YLFramework.UI.Acceptance").GetType("YLFramework.Core.Tests.UIAcceptanceEntry", true);
            Assert.That(type, Is.Not.Null);
            foreach (var name in new[] { "rootPrefab", "colorPrefab" })
            {
                var field = type.GetField(name);
                Assert.That(field, Is.Not.Null);
                Assert.That(field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>(), Is.Empty);
            }
            string folder = "Assets/Tests/YLFramework/EditMode/Editor/Core/CurrentAuthoring-" + Guid.NewGuid().ToString("N");
            string path = folder + "/Current.prefab";
            GameObject host = null;
            GameObject contents = null;
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                AssetDatabase.CreateFolder("Assets/Tests/YLFramework/EditMode/Editor/Core", Path.GetFileName(folder));
                host = new GameObject("Current Acceptance Authoring");
                host.SetActive(false);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, scene);
                var component = host.AddComponent(type);
                var colorReference = new GameObject("Referenced Color");
                colorReference.transform.SetParent(host.transform, false);
                using (var serialized = new SerializedObject(component))
                {
                    serialized.FindProperty("rootPrefab").objectReferenceValue = host;
                    serialized.FindProperty("colorPrefab").objectReferenceValue = colorReference;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                Assert.That(PrefabUtility.SaveAsPrefabAsset(host, path), Is.Not.Null);
                byte[] meta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"));
                string guid = AssetDatabase.AssetPathToGUID(path);
                string yaml = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                foreach (var name in new[] { "rootPrefab", "colorPrefab" })
                {
                    Assert.That(yaml, Does.Contain("\n  " + name + ":"));
                }
                var ids = System.Text.RegularExpressions.Regex.Matches(yaml, @"(?m)^--- !u!\d+ &(-?\d+)")
                    .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value).ToArray();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                contents = PrefabUtility.LoadPrefabContents(path);
                using (var serialized = new SerializedObject(contents.GetComponent(type)))
                {
                    Assert.That(serialized.FindProperty("rootPrefab").objectReferenceValue, Is.EqualTo(contents));
                    Assert.That(serialized.FindProperty("colorPrefab").objectReferenceValue, Is.EqualTo(contents.transform.Find("Referenced Color").gameObject));
                }
                Assert.That(PrefabUtility.SaveAsPrefabAsset(contents, path), Is.Not.Null);
                PrefabUtility.UnloadPrefabContents(contents);
                contents = null;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                using (var serialized = new SerializedObject(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent(type)))
                {
                    var target = (Component)serialized.targetObject;
                    Assert.That(serialized.FindProperty("rootPrefab").objectReferenceValue, Is.EqualTo(target.gameObject));
                    Assert.That(serialized.FindProperty("colorPrefab").objectReferenceValue,
                        Is.EqualTo(target.transform.Find("Referenced Color").gameObject));
                }
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.EqualTo(meta));
                Assert.That(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), @"(?m)^--- !u!\d+ &(-?\d+)")
                    .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value), Is.EqualTo(ids));
            }
            finally
            {
                if (contents)
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
                if (host)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
                EditorSceneManager.ClosePreviewScene(scene);
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
        }
    }
}
