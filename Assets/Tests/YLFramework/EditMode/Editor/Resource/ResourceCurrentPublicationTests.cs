using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Testing.EditorFixtures;

namespace YLFramework.Tests.ResourceBuild
{
    [Category("YL.Special.ResourceCurrent")]
    public sealed class ResourceCurrentPublicationTests
    {
        [Test]
        public void ExplicitAcceptanceProbeIsAbsentFromOrdinaryPlayerCompilation()
        {
            const string ProbeAssembly = "YLFramework.Tests.ResourceCurrent.Player";
            var editor = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor);
            Assert.That(editor.Count(assembly => assembly.name == ProbeAssembly), Is.EqualTo(1));
            var player = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies);
            Assert.That(player.Any(assembly => assembly.name == ProbeAssembly), Is.False,
                "An ordinary Player must not compile the acceptance probe without its explicit build symbol.");
        }

        [Test]
        public void ResourceAuthorAndWindowSurfaceHasOnlyTheCurrentSerializedFieldContract()
        {
            var types = typeof(ResourceProject).Assembly.GetTypes().Where(type => type.Namespace != null && type.Namespace.StartsWith("YLFramework.Editor.Resource", StringComparison.Ordinal));
            var readers = types.SelectMany(type => type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .SelectMany(field => field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>()
                    .Select(attribute => type.FullName + "." + field.Name + ":" + attribute.oldName))).ToArray();
            Assert.That(readers, Is.Empty, "Current asset values and real publication/Player acceptance must precede retiring the old author readers.");
        }

        [Test]
        public void MaintainedAuthorAssetBytesUseCurrentFieldsAndKeepTheirExplicitNativeSourceIdentity()
        {
            const string Root = "Assets/Tests/YLFramework/Fixtures/";
            var paths = new[]
            {
                "CommonSystem/ResourceProject.asset", "Core/YLCoreResourceModule/ResourceProject.asset",
                "ResourceMultiDirectory-20261018/Legacy/LegacyProject.asset", "ResourceMultiDirectory-20261018/Legacy/Content/GeneratedAtlases/AutomaticAtlases.asset",
                "ResourceMultiDirectory-20261018/Modern/ModernProject.asset", "ResourceMultiDirectory-20261018/Modern/ModernRecipe.asset",
                "ResourceMultiDirectory-20261018/Modern/Generated/OwnedAtlases/AutomaticAtlases.asset",
                "WorkbenchUX1-20261017/WorkbenchProject.asset", "WorkbenchUX1-20261017/SameDisplayNameProject.asset",
                "WorkbenchUX2To5-20261017/WorkbenchProject.asset", "WorkbenchUX2To5-20261017/MismatchProject.asset",
                "WorkbenchUX2To5-20261017/WorkbenchRecipe.asset", "WorkbenchUX2To5-20261017/PartialFailureRecipe.asset",
                "WorkbenchUX2To5-20261017/Content/GeneratedAtlases/AutomaticAtlases.asset",
                "WorkbenchPolish-20261018/WorkbenchProject.asset", "WorkbenchPolish-20261018/PartialFailureRecipe.asset",
                "WorkbenchFinalUI-20261018/FinalUIProject.asset", "WorkbenchFinalUI-20261018/Content/GeneratedAtlases/AutomaticAtlases.asset"
            }.Select(path => Root + path).ToArray();
            var output = Path.Combine(Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT"), "ResourceAuthorSave");
            Assert.That(Directory.Exists(output), Is.False);
            Directory.CreateDirectory(output);
            var evidence = new JArray();
            foreach (var path in paths)
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                var meta = File.ReadAllBytes(path + ".meta");
                var bytes = File.ReadAllBytes(path);
                var yaml = System.Text.Encoding.UTF8.GetString(bytes);
                Assert.That(guid, Does.Match("^[0-9a-f]{32}$"), path);
                Assert.That(System.Text.Encoding.UTF8.GetString(meta), Does.Contain("guid: " + guid));
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(yaml, @"(?m)^[ \t]+(?:- )?(?!m_)[A-Z][A-Za-z0-9_]*:"), Is.False,
                    "The source assets must have been officially saved before removing their old readers: " + path);
                var objectType = yaml.Contains("guid: 7331c0230f1378c48bb33ed06489830c") ? typeof(ResourceProject)
                    : yaml.Contains("guid: efbfe2b778a7a3642844022cb04d1672") ? typeof(ResourceBuildRecipe)
                    : yaml.Contains("guid: b8d754bdbc05bfb45bb058ffdf82c0bc") ? typeof(ResourceAtlasConfiguration) : null;
                Assert.That(objectType, Is.Not.Null, path);
                foreach (var field in objectType.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => !field.IsNotSerialized))
                {
                    Assert.That(yaml, Does.Contain("\n  " + field.Name + ":"), path);
                }
                var destination = Path.Combine(output, path.Substring(Root.Length));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, bytes);
                File.WriteAllBytes(destination + ".meta", meta);
                evidence.Add(new JObject { ["path"] = path, ["guid"] = guid, ["sha256"] = ResourceCatalogCodec.Digest(bytes), ["metaSha256"] = ResourceCatalogCodec.Digest(meta) });
            }
            File.WriteAllText(Path.Combine(output, "saved-assets.json"), evidence.ToString());
            Assert.That(paths.Any(path => path.Contains("NamespaceMigration") || path.Contains("ResourceV1~")), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentAuthoringAssetsAndWindowFieldsRoundTripWithoutLegacyKeys(bool value)
        {
            var folder = "Assets/__YLResourceAuthor_" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.IsValidFolder(folder) || Directory.Exists(folder), Is.False);
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            try
            {
                AssetDatabase.CreateFolder(folder, "Content");
                var project = ScriptableObject.CreateInstance<ResourceProject>();
                project.projectId = "resource-author-" + Guid.NewGuid().ToString("N");
                project.displayName = "Current Resource author";
                project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(folder + "/Content"), addressPrefix = "current" });
                project.groups.Add(new ResourceGroup { id = "all", packing = ResourcePacking.Together });
                AssetDatabase.CreateAsset(project, folder + "/Project.asset");
                var atlas = ResourceAtlasGenerator.CreateConfiguration(project, folder, "Atlases");
                var atlasPath = AssetDatabase.GetAssetPath(atlas);
                atlas.rules.Add(new ResourceAtlasRule
                {
                    id = "current-rule",
                    groupId = "all",
                    grouping = ResourceAtlasGrouping.PerFolder,
                    address = "atlases/current",
                    sources = new UnityEngine.Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder + "/Content") },
                    allowRotation = value,
                    tightPacking = !value,
                    readable = value,
                    mipMaps = !value,
                    srgb = value,
                    padding = 8,
                    maxTextureSize = 512,
                    filterMode = FilterMode.Point
                });
                EditorUtility.SetDirty(atlas);
                AssetDatabase.SaveAssetIfDirty(atlas);
                var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
                recipe.project = project;
                recipe.forceRebuild = value;
                recipe.generateReferences = !value;
                recipe.action = ResourceBuildAction.BuildAndPublish;
                recipe.appContentCompatibilityId = "current-author-app";
                recipe.referenceDirectory = folder + "/References";
                AssetDatabase.CreateAsset(recipe, folder + "/Recipe.asset");
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssetIfDirty(recipe);
                var saved = new ScriptableObject[] { recipe, atlas, project }.Select(asset => new
                {
                    Asset = asset,
                    Path = AssetDatabase.GetAssetPath(asset),
                    Guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)),
                    Meta = File.ReadAllBytes(AssetDatabase.GetAssetPath(asset) + ".meta"),
                    Values = AuthorValues(asset)
                }).ToArray();
                foreach (var item in saved)
                {
                    Resources.UnloadAsset(item.Asset);
                }
                foreach (var path in new[] { folder + "/Project.asset", folder + "/Recipe.asset", atlasPath })
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                project = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
                recipe = AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(folder + "/Recipe.asset");
                foreach (var item in saved)
                {
                    Assert.That(AuthorValues(AssetDatabase.LoadAssetAtPath<ScriptableObject>(item.Path)), Is.EqualTo(item.Values), item.Path);
                    Assert.That(AssetDatabase.AssetPathToGUID(item.Path), Is.EqualTo(item.Guid));
                    CollectionAssert.AreEqual(item.Meta, File.ReadAllBytes(item.Path + ".meta"));
                }
                Assert.That(recipe.project, Is.SameAs(project));
                Assert.That(recipe.forceRebuild, Is.EqualTo(value));
                Assert.That(recipe.generateReferences, Is.EqualTo(!value));
                Assert.That(project.automaticAtlases.rules.Single().readable, Is.EqualTo(value));
                Assert.That(project.automaticAtlases.rules.Single().mipMaps, Is.EqualTo(!value));
                var editorAssembly = typeof(ResourceProject).Assembly;
                foreach (var name in new[] { "YLFramework.Editor.Resource.ResServiceEditorWindow", "YLFramework.Editor.Resource.ResourceReleaseSigningWindow" })
                {
                    var type = editorAssembly.GetType(name, true);
                    var window = ScriptableObject.CreateInstance(type);
                    var copy = ScriptableObject.CreateInstance(type);
                    try
                    {
                        using var serialized = new SerializedObject(window);
                        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            .Where(field => field.GetCustomAttribute<SerializeField>() != null))
                        {
                            var property = serialized.FindProperty(field.Name);
                            Assert.That(property, Is.Not.Null, field.Name);
                            if (field.FieldType == typeof(bool))
                            {
                                property.boolValue = value;
                            }
                            else if (field.FieldType == typeof(string))
                            {
                                property.stringValue = "current-" + field.Name;
                            }
                            else if (field.FieldType == typeof(ResourceProject))
                            {
                                property.objectReferenceValue = project;
                            }
                            else if (field.FieldType == typeof(ResourceBuildRecipe))
                            {
                                property.objectReferenceValue = recipe;
                            }
                        }
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        var json = EditorJsonUtility.ToJson(window);
                        EditorJsonUtility.FromJsonOverwrite(json, copy);
                        Assert.That(EditorJsonUtility.ToJson(copy), Is.EqualTo(json), name);
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(copy);
                        UnityEngine.Object.DestroyImmediate(window);
                    }
                }
            }
            finally
            {
                Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
            }
        }

        [Test]
        public void CurrentProjectAndRecipePublishTwoVersionsConsumedByTwoIndependentPlayers()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying, Is.True);
            var output = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
            Assert.That(Directory.Exists(output), Is.True, "Use the existing owned native result directory.");
            var assets = "Assets/__YLResourceCurrent_" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.IsValidFolder(assets) || Directory.Exists(assets), Is.False);
            Assert.That(Directory.Exists("Assets/StreamingAssets") || File.Exists("Assets/StreamingAssets.meta"), Is.False,
                "This producer does not adopt or remove an existing deployment root.");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(assets)), Is.Not.Empty);
            string workspace = null;
            string releases = null;
            string deploymentWorkspace = null;
            var evidence = Path.Combine(output, "ResourceCurrentPublication");
            Assert.That(Directory.Exists(evidence), Is.False);
            Directory.CreateDirectory(evidence);
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "rc" + Guid.NewGuid().ToString("N").Substring(0, 8);
            project.displayName = "Current Resource publication";
            project.compression = ResourceCompression.Lz4;
            try
            {
                AssetDatabase.CreateFolder(assets, "Content");
                var textPath = assets + "/Content/entry.txt";
                File.WriteAllText(textPath, "current resource first");
                AssetDatabase.ImportAsset(textPath, ImportAssetOptions.ForceSynchronousImport);
                project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(assets + "/Content") });
                project.groups.Add(new ResourceGroup
                {
                    id = "current",
                    packing = ResourcePacking.Together,
                    selections = { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(textPath), address = "entry.txt" } }
                });
                var projectPath = assets + "/Project.asset";
                AssetDatabase.CreateAsset(project, projectPath);
                var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
                recipe.project = project;
                recipe.target = BuildTarget.StandaloneWindows64;
                recipe.action = ResourceBuildAction.BuildAndPublish;
                recipe.forceRebuild = false;
                recipe.generateReferences = false;
                recipe.appContentCompatibilityId = "resource-current-v2-controls";
                var recipePath = assets + "/Recipe.asset";
                AssetDatabase.CreateAsset(recipe, recipePath);
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssetIfDirty(recipe);
                var projectGuid = AssetDatabase.AssetPathToGUID(projectPath);
                var recipeGuid = AssetDatabase.AssetPathToGUID(recipePath);
                var projectValues = AuthorValues(project);
                var recipeValues = AuthorValues(recipe);
                Resources.UnloadAsset(recipe);
                Resources.UnloadAsset(project);
                AssetDatabase.ImportAsset(projectPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(recipePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                project = AssetDatabase.LoadAssetAtPath<ResourceProject>(projectPath);
                recipe = AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(recipePath);
                Assert.That(AuthorValues(project), Is.EqualTo(projectValues));
                Assert.That(AuthorValues(recipe), Is.EqualTo(recipeValues));
                Assert.That(AssetDatabase.AssetPathToGUID(projectPath), Is.EqualTo(projectGuid));
                Assert.That(AssetDatabase.AssetPathToGUID(recipePath), Is.EqualTo(recipeGuid));
                Assert.That(recipe.project, Is.SameAs(project));
                Assert.That(recipe.forceRebuild || recipe.generateReferences, Is.False);
                var newWorkspace = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, recipe.target));
                Assert.That(Directory.Exists(newWorkspace), Is.False, "Never adopt a previous build workspace.");
                workspace = newWorkspace;
                var firstBuild = ResourceBundleBuilder.Build(project, recipe.target);
                var newReleases = ResourcePublisher.GetReleaseRoot(firstBuild);
                Assert.That(Directory.Exists(newReleases), Is.False, "Never adopt a previous publication root.");
                releases = newReleases;
                var first = ResourcePublisher.Publish(firstBuild, recipe.appContentCompatibilityId);
                File.WriteAllText(textPath, "current resource second");
                AssetDatabase.ImportAsset(textPath, ImportAssetOptions.ForceSynchronousImport);
                project = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(projectGuid));
                recipe = AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(AssetDatabase.GUIDToAssetPath(recipeGuid));
                var second = ResourcePublisher.Publish(ResourceBundleBuilder.Build(project, recipe.target), recipe.appContentCompatibilityId);
                Assert.That(second.Catalog.CatalogDigest, Is.Not.EqualTo(first.Catalog.CatalogDigest));
                foreach (var pair in new[] { (release: first, name: "first"), (release: second, name: "second") })
                {
                    var destination = Path.Combine(evidence, pair.name);
                    Directory.CreateDirectory(destination);
                    foreach (var file in Directory.GetFiles(pair.release.DirectoryPath))
                    {
                        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                    }
                }
                foreach (var path in new[] { projectPath, recipePath })
                {
                    File.Copy(path, Path.Combine(evidence, Path.GetFileName(path)));
                    File.Copy(path + ".meta", Path.Combine(evidence, Path.GetFileName(path) + ".meta"));
                }
                var deployment = ResourceBuiltInDeployer.Preflight(first);
                Assert.That(Directory.Exists(deployment.WorkspacePath), Is.False, "Never adopt another deployment workspace.");
                deploymentWorkspace = deployment.WorkspacePath;
                ResourceBuiltInDeployer.Deploy(deployment);
                StandaloneFixturePlayer.BuildAndRun(evidence, assets, "YLFramework.Runtime", second, _ =>
                {
                    var type = Type.GetType("YLFramework.Tests.ResourceBuild.ResourceCurrentPlayerProbe, YLFramework.Tests.ResourceCurrent.Player", true);
                    var probe = new GameObject("CurrentResourcePlayer").AddComponent(type);
                    type.GetField("projectId").SetValue(probe, project.projectId);
                    type.GetField("fromDigest").SetValue(probe, first.Catalog.CatalogDigest);
                    type.GetField("targetDigest").SetValue(probe, second.Catalog.CatalogDigest);
                    type.GetField("compatibility").SetValue(probe, recipe.appContentCompatibilityId);
                }, new[] { "YL_RESOURCE_CURRENT_ACCEPTANCE" });
                foreach (var run in new[] { 1, 2 })
                {
                    var report = JObject.Parse(File.ReadAllText(Path.Combine(evidence, "StandalonePlayer-" + run + ".json")));
                    Assert.That((string)report["probeAssembly"], Is.EqualTo("YLFramework.Tests.ResourceCurrent.Player"));
                    foreach (var field in new[] { "assemblies", "probeReferences" })
                    {
                        var names = ((JArray)report[field]).Values<string>().ToArray();
                        Assert.That(names, Is.Not.Empty);
                        Assert.That(names.Any(name => name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                            name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            string.Equals(name, "nunit.framework", StringComparison.OrdinalIgnoreCase)), Is.False, field);
                    }
                    foreach (var field in new[] { "currentTextLoaded", "updatedTextLoaded", "rolledBack", "versionTwoWithEmptyFloors" })
                    {
                        Assert.That((bool)report[field], Is.True, field);
                    }
                }
                var playerRoot = File.ReadAllText(Path.Combine(evidence, "standalone-player-location.txt"));
                var managed = Path.Combine(playerRoot, "P_Data/Managed");
                var builtAssemblies = Directory.GetFiles(managed, "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
                Assert.That(builtAssemblies, Does.Contain("YLFramework.Tests.ResourceCurrent.Player"));
                Assert.That(builtAssemblies.Any(name => name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                    name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(name, "nunit.framework", StringComparison.OrdinalIgnoreCase)), Is.False,
                    "The actual dedicated Player cannot ship Editor, NUnit or TestRunner assemblies.");
            }
            finally
            {
                if (Directory.Exists("Assets/StreamingAssets"))
                {
                    CollectionAssert.IsSubsetOf(Directory.GetFileSystemEntries("Assets/StreamingAssets").Select(Path.GetFileName),
                        new[] { "YLFrameworkResources", "YLFrameworkResources.meta" });
                    var root = "Assets/StreamingAssets/YLFrameworkResources";
                    if (Directory.Exists(root))
                    {
                        CollectionAssert.IsSubsetOf(Directory.GetFileSystemEntries(root).Select(Path.GetFileName),
                            new[] { project.projectId, project.projectId + ".meta", "ylr-owner.json~" });
                    }
                    Assert.That(AssetDatabase.DeleteAsset("Assets/StreamingAssets"), Is.True);
                }
                foreach (var path in new[] { workspace, releases, deploymentWorkspace }.Where(path => path != null && Directory.Exists(path)))
                {
                    Directory.Delete(path, true);
                }
                Assert.That(AssetDatabase.DeleteAsset(assets), Is.True);
            }
        }

        private static string AuthorValues(UnityEngine.Object asset)
        {
            Assert.That(asset != null, Is.True, "Author cold reads must not retain a destroyed Unity wrapper.");
            var json = JObject.Parse(EditorJsonUtility.ToJson(asset));
            foreach (var reference in json.DescendantsAndSelf().OfType<JObject>().Where(item => item["instanceID"] != null).ToArray())
            {
                var id = (int)reference["instanceID"];
                reference["instanceID"] = id == 0 ? "null" : GlobalObjectId.GetGlobalObjectIdSlow(EditorUtility.InstanceIDToObject(id)).ToString();
            }
            return json.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
