using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Resource
{
    /// <summary>显式Atlas合同：只读输入检查和native读回，不生成Atlas、不注册全局tag、不打包原工程图集。</summary>
    internal static class ResourceAtlasRules
    {
        /// <summary>要求影响构建的图集被显式选中，并检查打包开关、非变体与 Sprite 名称唯一性。</summary>
        internal static void Validate(IEnumerable<string> sources, IEnumerable<ResourceBuildAsset> assets)
        {
            var paths = new HashSet<string>(sources, StringComparer.Ordinal);
            var declared = new HashSet<string>(assets.Where(value => value.AssetType == typeof(SpriteAtlas)).Select(value => value.SourcePath), StringComparer.Ordinal);
            var spritePaths = new HashSet<string>(paths.Where(path => AssetImporter.GetAtPath(path) is TextureImporter importer &&
                importer.textureType == TextureImporterType.Sprite), StringComparer.Ordinal);
            if (declared.Count == 0 && spritePaths.Count == 0)
            {
                return;
            }

            var atlases = AssetDatabase.FindAssets("t:SpriteAtlas");
            if (atlases.Length > 16384)
            {
                throw new InvalidOperationException("SpriteAtlas inspection limit exceeded.");
            }

            foreach (var path in atlases.Select(AssetDatabase.GUIDToAssetPath).OrderBy(value => value, StringComparer.Ordinal))
            {
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                if (!atlas)
                {
                    throw new InvalidOperationException("Cannot inspect SpriteAtlas: " + path);
                }

                var explicitAtlas = declared.Contains(path);
                if (!explicitAtlas)
                {
                    if (paths.Contains(path) || EditorSettings.spritePackerMode != SpritePackerMode.Disabled && atlas.IsIncludeInBuild() && SpritePaths(atlas).Any(spritePaths.Contains))
                    {
                        throw new InvalidOperationException("SpriteAtlas can influence this build but is not explicitly selected: " + path +
                            ". Use an explicit Atlas with Include in Build disabled; transparent late binding is not supported.");
                    }

                    continue;
                }
                if (EditorSettings.spritePackerMode == SpritePackerMode.Disabled || atlas.isVariant || atlas.IsIncludeInBuild())
                {
                    throw new InvalidOperationException("Explicit SpriteAtlas requires an enabled SpritePacker, a non-variant Atlas and Include in Build disabled: " + path);
                }

                var sprites = Sprites(atlas);
                if (sprites.Length == 0 || sprites.GroupBy(value => value.name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                {
                    throw new InvalidOperationException("SpriteAtlas requires nonempty sprites with unique names for explicit GetSprite lookup: " + path);
                }
            }
        }

        /// <summary>展开图集实际选入的 Sprite，返回去重后的源文件路径。</summary>
        internal static IEnumerable<string> SpritePaths(SpriteAtlas atlas) => Sprites(atlas).Select(AssetDatabase.GetAssetPath).Distinct(StringComparer.Ordinal);

        /// <summary>展开直接 Sprite、纹理和递归目录选择，变体从主图集收集来源。</summary>
        private static Sprite[] Sprites(SpriteAtlas atlas)
        {
            if (atlas.isVariant)
            {
                atlas = atlas.GetMasterAtlas();
            }

            if (!atlas)
            {
                return Array.Empty<Sprite>();
            }

            var result = new HashSet<Sprite>();
            foreach (var packable in atlas.GetPackables())
            {
                if (!packable)
                {
                    throw new InvalidOperationException("SpriteAtlas contains a missing packable: " + AssetDatabase.GetAssetPath(atlas));
                }

                if (packable is Sprite sprite)
                {
                    result.Add(sprite);
                    continue;
                }
                var path = AssetDatabase.GetAssetPath(packable);
                var paths = AssetDatabase.IsValidFolder(path) ? AssetDatabase.FindAssets("t:Texture2D", new[] { path }).Select(AssetDatabase.GUIDToAssetPath) : new[] { path };
                foreach (var source in paths)
                {
                    foreach (var value in AssetDatabase.LoadAllAssetsAtPath(source).OfType<Sprite>())
                    {
                        result.Add(value);
                    }
                }
            }
            return result.ToArray();
        }

        /// <summary>对照源图集验证原生产物的 Sprite 数量、纹理引用和具名查找，并释放取得的克隆。</summary>
        internal static void ValidateReadback(SpriteAtlas atlas, string sourcePath)
        {
            var names = Sprites(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(sourcePath)).Select(value => value.name).ToArray();
            if (atlas.spriteCount != names.Length)
            {
                throw new InvalidOperationException("SpriteAtlas packed sprite count differs from its explicit inputs: " + sourcePath);
            }
            // 本机EditMode的Bundle克隆Sprite.texture可为null，而native Atlas的render data已持有真实纹理。
            // 直接验证native payload引用，不依赖Editor的Sprite显示状态；PlayMode另验正式消费者。
            using var data = new SerializedObject(atlas);
            var renderData = data.FindProperty("m_RenderDataMap");
            if (renderData == null || !renderData.isArray || renderData.arraySize != names.Length)
            {
                throw new InvalidOperationException("Unsupported SpriteAtlas native render-data layout: " + sourcePath);
            }

            for (var i = 0; i < renderData.arraySize; i++)
            {
                var reference = renderData.GetArrayElementAtIndex(i).FindPropertyRelative("second.texture");
                if (reference == null || reference.propertyType != SerializedPropertyType.ObjectReference ||
                    reference.objectReferenceValue is not Texture2D texture || !texture || texture.width <= 0 || texture.height <= 0)
                {
                    throw new InvalidOperationException("SpriteAtlas native packed texture cannot be read back: " + sourcePath);
                }
            }
            foreach (var name in names)
            {
                var sprite = atlas.GetSprite(name);
                try
                {
                    if (!sprite || sprite.rect.width <= 0 || sprite.rect.height <= 0)
                    {
                        throw new InvalidOperationException("SpriteAtlas packed Sprite cannot be read back: " + sourcePath + "#" + name);
                    }
                }
                finally
                {
                    if (sprite)
                    {
                        Object.DestroyImmediate(sprite);
                    }
                }
            }
        }
    }
}
