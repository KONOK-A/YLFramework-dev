#if YL_LOCALIZATION_PACKAGE
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace YLFramework.Tests.Localization
{
    // 仅替换慢加载边界；格式化、变量、表项解析及本地化事件仍运行官方实现。
    public sealed class LocalizationFixture : IDisposable, ITableProvider, ILocalesProvider
    {
        private readonly LocalizationSettings _previous;
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        private readonly Dictionary<string, LocalizationTable> _tables = new Dictionary<string, LocalizationTable>();
        private readonly List<Action> _pending = new List<Action>();
        private bool _isDisposed;
        public readonly FixtureLocalizationSettings Settings;
        public readonly Locale English;
        public readonly Locale French;
        public readonly string Strings;
        public readonly string Assets;
        public bool DelayTables;
        public bool DelayAssetTables;
        public int PendingTableLoads => _pending.Count;
        public List<Locale> Locales { get; } = new List<Locale>();

        public LocalizationFixture(bool delayInitialization = false)
        {
            _previous = LocalizationSettings.GetInstanceDontCreateDefault();
            Settings = ScriptableObject.CreateInstance<FixtureLocalizationSettings>();
            Settings.DelayInitialization = delayInitialization;
            LocalizationSettings.Instance = Settings;
            English = Own(Locale.CreateLocale("en"));
            French = Own(Locale.CreateLocale("fr"));
            Locales.Add(English);
            Locales.Add(French);
            Settings.SetAvailableLocales(this);
            Settings.GetStringDatabase().TableProvider = this;
            Settings.GetAssetDatabase().TableProvider = this;
            Settings.SetSelectedLocale(English);
            Strings = "YL-strings-" + Guid.NewGuid().ToString("N");
            Assets = "YL-assets-" + Guid.NewGuid().ToString("N");
            var strings = Own(ScriptableObject.CreateInstance<SharedTableData>());
            strings.TableCollectionName = Strings;
            var assets = Own(ScriptableObject.CreateInstance<SharedTableData>());
            assets.TableCollectionName = Assets;
            foreach (var locale in Locales)
            {
                var table = Own(ScriptableObject.CreateInstance<StringTable>());
                table.SharedData = strings;
                table.LocaleIdentifier = locale.Identifier;
                table.AddEntry("name", locale == English ? "Sword" : "Epee");
                table.AddEntry("empty", "{empty}").IsSmart = true;
                table.AddEntry("untranslated", "");
                table.AddEntry("count", "{count:N0}").IsSmart = true;
                _tables.Add(Strings + ":" + locale.Identifier.Code, table);
                var assetTable = Own(ScriptableObject.CreateInstance<AssetTable>());
                assetTable.SharedData = assets;
                assetTable.LocaleIdentifier = locale.Identifier;
                _tables.Add(Assets + ":" + locale.Identifier.Code, assetTable);
            }
        }

        public T Own<T>(T value) where T : UnityEngine.Object
        {
            _owned.Add(value);
            return value;
        }

        public StringTable StringTable(Locale locale) => (StringTable)_tables[Strings + ":" + locale.Identifier.Code];
        public AssetTable AssetTable(Locale locale) => (AssetTable)_tables[Assets + ":" + locale.Identifier.Code];
        public void SetAsset<T>(Locale locale, string key, T value) where T : UnityEngine.Object
        {
            AssetTable(locale).AddEntry(key, "").SetAssetOverride(value);
        }
        public Locale GetLocale(LocaleIdentifier id) => Locales.Find(locale => locale.Identifier == id);
        public void AddLocale(Locale locale) => Locales.Add(locale);
        public bool RemoveLocale(Locale locale) => Locales.Remove(locale);

        public AsyncOperationHandle<TTable> ProvideTableAsync<TTable>(string tableCollectionName, Locale locale)
            where TTable : LocalizationTable
        {
            _tables.TryGetValue(tableCollectionName + ":" + locale.Identifier.Code, out var table);
            if (!DelayTables && !(DelayAssetTables && table is AssetTable))
                return Addressables.ResourceManager.CreateCompletedOperation(table as TTable,
                    table ? null : "Fixture table missing: " + tableCollectionName);
            var operation = new DelayedTable<TTable>();
            var handle = Addressables.ResourceManager.StartOperation(operation, default);
            _pending.Add(() => operation.Finish(table as TTable));
            return handle;
        }

        public void CompletePending()
        {
            var pending = _pending.ToArray();
            _pending.Clear();
            foreach (var complete in pending) complete();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            CompletePending();
            Settings.CompleteInitialization();
            for (var i = _owned.Count - 1; i >= 0; i--)
                if (_owned[i] is GameObject go && go) UnityEngine.Object.DestroyImmediate(go);
            ((IDisposable)Settings).Dispose();
            Settings.ReleaseInitialization();
            LocalizationSettings.Instance = _previous;
            for (var i = _owned.Count - 1; i >= 0; i--)
                if (_owned[i]) UnityEngine.Object.DestroyImmediate(_owned[i]);
            UnityEngine.Object.DestroyImmediate(Settings);
        }

        private sealed class DelayedTable<T> : AsyncOperationBase<T> where T : LocalizationTable
        {
            protected override void Execute() { }
            public void Finish(T table) => Complete(table, table != null, table ? null : "Fixture table missing");
        }
    }

    public sealed class FixtureLocalizationSettings : LocalizationSettings
    {
        private AsyncOperationHandle<LocalizationSettings> _initialization;
        private InitializationOperation _pending;
        public bool DelayInitialization;

        public override AsyncOperationHandle<LocalizationSettings> GetInitializationOperation()
        {
            if (!_initialization.IsValid())
            {
                if (DelayInitialization)
                {
                    _pending = new InitializationOperation(this);
                    _initialization = Addressables.ResourceManager.StartOperation(_pending, default);
                }
                else _initialization = Addressables.ResourceManager.CreateCompletedOperation<LocalizationSettings>(this, null);
            }
            return _initialization;
        }

        public void CompleteInitialization(bool success = true)
        {
            var pending = _pending;
            _pending = null;
            pending?.Finish(success);
        }

        public void ReleaseInitialization()
        {
            if (_initialization.IsValid()) Addressables.Release(_initialization);
            _initialization = default;
        }

        private sealed class InitializationOperation : AsyncOperationBase<LocalizationSettings>
        {
            private readonly LocalizationSettings _settings;
            public InitializationOperation(LocalizationSettings settings) => _settings = settings;
            protected override void Execute() { }
            public void Finish(bool success) => Complete(success ? _settings : null, success,
                success ? null : "Fixture initialization failed");
        }
    }
}
#endif
