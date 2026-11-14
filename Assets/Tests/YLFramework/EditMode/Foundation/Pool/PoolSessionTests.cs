using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Pooling;

namespace YLFramework.Tests
{
    [Category("YL.Special.Session")]
    public class PoolSessionTests
    {
        public sealed class Item : IOfPool
        {
            public bool IsRecycled
            {
                get; set;
            }
            public int Value;
            public int Resets;
            public void OnAllocate()
            {
            }
            public void OnRecycle()
            {
                Value = 0;
                Resets++;
            }
        }

        private bool _configured;
        private bool _optionsEnabled;
        private EnterPlayModeOptions _options;
        private string _enabledLine;
        private string _optionsLine;
        private SceneSetup[] _scenes;
        private string _scenePath;
        private SafeObjectPool<Item> _oldSafe;
        private SafeObjectPool<Item> _currentSafe;
        private ObjectPool<Item> _explicit;
        private PoolLease<Item> _oldSafeLease;
        private PoolLease<Item> _currentSafeLease;
        private PoolLease<Item> _explicitLease;
        private PoolLease<List<Item>> _oldListLease;
        private PoolLease<Dictionary<string, Item>> _oldDictionaryLease;
        private PoolLease<List<Item>> _currentListLease;
        private PoolLease<Dictionary<string, Item>> _currentDictionaryLease;
        private List<Item> _oldRawList;
        private Dictionary<string, Item> _oldRawDictionary;
        private bool _returnedOldRaw;
        private const string Enabled = @"(?m)^  m_EnterPlayModeOptionsEnabled: [^\r\n]*";
        private const string Options = @"(?m)^  m_EnterPlayModeOptions: [^\r\n]*";
        private static string SettingsPath => global::YLFramework.Testing.PackageTestPaths.Physical(
            Path.Combine(Path.GetDirectoryName(Application.dataPath), "ProjectSettings/EditorSettings.asset"));

        [SetUp]
        public void SetUp()
        {
            Assert.That(Application.isBatchMode, Is.True, "只使用CLI拥有的独立宿主。");
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Assert.That(SceneManager.GetSceneAt(index).isDirty, Is.False, "不保存未知脏场景。");
            }
            _optionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _options = EditorSettings.enterPlayModeOptions;
            string settings = File.ReadAllText(SettingsPath);
            _enabledLine = ReadLine(settings, Enabled);
            _optionsLine = ReadLine(settings, Options);
            _scenes = EditorSceneManager.GetSceneManagerSetup().Where(value => !string.IsNullOrEmpty(value.path)).ToArray();
            _scenePath = "Temp/YLPool.Session." + Guid.NewGuid().ToString("N") + ".unity";
            Assert.That(File.Exists(_scenePath), Is.False);
            _configured = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, _scenePath), Is.True);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_configured)
            {
                yield break;
            }
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
            try
            {
                _oldListLease.Dispose();
                _oldDictionaryLease.Dispose();
                _currentListLease.Dispose();
                _currentDictionaryLease.Dispose();
                _oldSafeLease.Dispose();
                _currentSafeLease.Dispose();
                _explicitLease.Dispose();
                if (!_returnedOldRaw)
                {
                    ListPool<Item>.Recycle(_oldRawList);
                    DictionaryPool<string, Item>.Recycle(_oldRawDictionary);
                }
                _oldSafe?.Dispose();
                _currentSafe?.Dispose();
                _explicit?.Dispose();
                ListPool<Item>.Clear();
                DictionaryPool<string, Item>.Clear();
            }
            finally
            {
                EditorSettings.enterPlayModeOptions = _options;
                EditorSettings.enterPlayModeOptionsEnabled = _optionsEnabled;
                var text = File.ReadAllText(SettingsPath);
                ReadLine(text, Enabled);
                ReadLine(text, Options);
                var restored = Regex.Replace(Regex.Replace(text, Enabled, _ => _enabledLine), Options, _ => _optionsLine);
                if (restored != text)
                {
                    File.WriteAllText(SettingsPath, restored, new UTF8Encoding(false));
                }
                if (_scenes.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(_scenes);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_scenePath));
                _configured = false;
            }
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(_options));
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.EqualTo(_optionsEnabled));
            string disk = File.ReadAllText(SettingsPath);
            Assert.That(ReadLine(disk, Enabled), Is.EqualTo(_enabledLine));
            Assert.That(ReadLine(disk, Options), Is.EqualTo(_optionsLine));
        }

        private static string ReadLine(string text, string pattern)
        {
            var matches = Regex.Matches(text, pattern);
            Assert.That(matches.Count, Is.EqualTo(1));
            return matches[0].Value;
        }

        [UnityTest]
        public IEnumerator TwoRealPlaysIsolateIdleCachesAndLateBorrowersWithoutClosingExplicitPools()
        {
            yield return new EnterPlayMode(false);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload));
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(_scenePath));
            var idleList = ListPool<Item>.Allocate();
            var idleDictionary = DictionaryPool<string, Item>.Allocate();
            _oldRawList = ListPool<Item>.Allocate();
            _oldRawDictionary = DictionaryPool<string, Item>.Allocate();
            _oldListLease = ListPool<Item>.Rent(out var oldList);
            _oldDictionaryLease = DictionaryPool<string, Item>.Rent(out var oldDictionary);
            oldList.Add(new Item());
            oldDictionary.Add("leased", new Item());
            _oldRawList.Add(new Item());
            _oldRawDictionary.Add("raw", new Item());
            idleList.RecycleToPool();
            idleDictionary.RecycleToPool();
            _oldSafe = SafeObjectPool<Item>.Instance;
            _oldSafeLease = _oldSafe.Rent(out var oldSafeValue);
            oldSafeValue.Value = 17;
            _explicit = new ObjectPool<Item>(() => new Item(), reset: value => value.OnRecycle());
            _explicitLease = _explicit.Rent(out var explicitValue);
            explicitValue.Value = 29;
            yield return new ExitPlayMode();
            yield return new EnterPlayMode(false);
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(_scenePath));
            Assert.That(oldList.Count + oldDictionary.Count + _oldRawList.Count + _oldRawDictionary.Count, Is.EqualTo(4),
                "新会话不得强行清理尚在借用的集合。");
            _currentListLease = ListPool<Item>.Rent(out var currentList);
            _currentDictionaryLease = DictionaryPool<string, Item>.Rent(out var currentDictionary);
            Assert.That(currentList, Is.Not.SameAs(idleList));
            Assert.That(currentDictionary, Is.Not.SameAs(idleDictionary));
            _currentSafe = SafeObjectPool<Item>.Instance;
            Assert.That(_currentSafe, Is.Not.SameAs(_oldSafe));
            _currentSafeLease = _currentSafe.Rent(out var currentSafeValue);
            currentSafeValue.Value = 41;
            _oldSafeLease.Dispose();
            _oldSafeLease.Discard();
            _oldSafe.Dispose();
            Assert.That(oldSafeValue.Value, Is.Zero);
            Assert.That(oldSafeValue.Resets, Is.EqualTo(1));
            Assert.That(currentSafeValue.Value, Is.EqualTo(41));
            Assert.That(SafeObjectPool<Item>.Instance, Is.SameAs(_currentSafe));
            _oldRawList.RecycleToPool();
            _oldRawDictionary.RecycleToPool();
            _returnedOldRaw = true;
            _oldListLease.Dispose();
            _oldDictionaryLease.Dispose();
            Assert.That(oldList.Count + oldDictionary.Count + _oldRawList.Count + _oldRawDictionary.Count, Is.Zero);
            var freshList = ListPool<Item>.Allocate();
            var freshDictionary = DictionaryPool<string, Item>.Allocate();
            Assert.That(freshList, Is.Not.SameAs(oldList).And.Not.SameAs(_oldRawList));
            Assert.That(freshDictionary, Is.Not.SameAs(oldDictionary).And.Not.SameAs(_oldRawDictionary));
            freshList.RecycleToPool();
            freshDictionary.RecycleToPool();
            Assert.That(explicitValue.Value, Is.EqualTo(29), "显式实例池不自动接入Unity会话。");
            _explicitLease.Dispose();
            var next = _explicit.Rent(out var reused);
            Assert.That(reused, Is.SameAs(explicitValue));
            next.Dispose();
            yield return new ExitPlayMode();
        }
    }
}
