using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Resource
{
    /// <summary>显式创建 Unity SpriteAtlas 并登记官方 Entry；不维护旧 Project 规则或私有图集运行时。</summary>
    public static class ResourceAtlasGenerator
    {
        public static SpriteAtlas Create(string assetPath, IEnumerable<Object> sources, AddressableAssetGroup group, string address)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                BuildPipeline.isBuildingPlayer || !group || !group.Settings || group.ReadOnly)
            {
                throw new InvalidOperationException("Use a settled Editor and an explicit writable official group.");
            }
            ResourceBuildOperation.ValidateAssetPath(assetPath);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".spriteatlasv2", StringComparison.Ordinal) ||
                !AssetDatabase.IsValidFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/')) ||
                File.Exists(assetPath) || File.Exists(assetPath + ".meta") || Directory.Exists(assetPath))
            {
                throw new IOException("Choose a new .spriteatlasv2 in an existing Assets folder; existing outputs are not adopted.");
            }
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("An exact official address is required.", nameof(address));
            }
            var entries = new List<AddressableAssetEntry>();
            group.Settings.GetAllAssets(entries, false);
            if (entries.Any(entry => entry.address == address || entry.guid == address))
            {
                throw new InvalidOperationException("Official Addressables key is already occupied: " + address);
            }
            var packables = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
            if (packables.Length == 0 || packables.Any(value => !value || !EditorUtility.IsPersistent(value) ||
                !(value is Sprite) && !(value is Texture2D) && !AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(value))))
            {
                throw new ArgumentException("Select persisted Sprite textures, Sprites or source folders.", nameof(sources));
            }
            if (EditorSettings.spritePackerMode == SpritePackerMode.Disabled)
            {
                throw new InvalidOperationException("Enable Unity SpritePacker explicitly before creating an atlas.");
            }
            ValidateSpriteNames(packables);
            var authoring = new SpriteAtlasAsset();
            try
            {
                authoring.Add(packables);
                SpriteAtlasAsset.Save(authoring, assetPath);
            }
            finally
            {
                Object.DestroyImmediate(authoring);
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            if (!(AssetImporter.GetAtPath(assetPath) is SpriteAtlasImporter importer))
            {
                throw new InvalidDataException("Unity did not assign a Sprite Atlas V2 importer: " + assetPath);
            }

            // V2 stores Include in Build in the importer, not in the transient authoring object.
            if (importer.includeInBuild)
            {
                importer.includeInBuild = false;
                importer.SaveAndReimport();
            }
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(assetPath);
            if (!atlas || atlas.IsIncludeInBuild())
            {
                throw new InvalidDataException("Unity did not import an Addressables-only SpriteAtlas: " + assetPath);
            }
            Undo.RecordObjects(new Object[] { group.Settings, group }, "Register Addressables Atlas");
            group.Settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(assetPath), group).SetAddress(address);
            return atlas;
        }

        private static void ValidateSpriteNames(IEnumerable<Object> packables)
        {
            var names = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            var visitedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var packable in packables)
            {
                var path = AssetDatabase.GetAssetPath(packable);
                if (packable is Sprite sprite)
                {
                    CheckName(sprite);
                }
                else if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (var guid in AssetDatabase.FindAssets("t:Texture2D t:Sprite", new[] { path }))
                    {
                        CheckPath(AssetDatabase.GUIDToAssetPath(guid));
                    }
                }
                else
                {
                    CheckPath(path);
                }
            }

            void CheckPath(string path)
            {
                if (!visitedPaths.Add(path)) return;
                foreach (var sprite in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>()) CheckName(sprite);
            }

            void CheckName(Sprite sprite)
            {
                if (names.TryGetValue(sprite.name, out var existing) && existing != sprite)
                {
                    throw new InvalidOperationException($"Sprites in an atlas must have unique names: '{sprite.name}' " +
                        $"({AssetDatabase.GetAssetPath(existing)} / {AssetDatabase.GetAssetPath(sprite)}).");
                }
                names[sprite.name] = sprite;
            }
        }
    }
}
