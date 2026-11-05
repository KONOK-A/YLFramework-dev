using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;

namespace YLFramework.Tests.DataBatch
{
    public class DataSourceOwnershipTests
    {
        [TestCase("Packages/com.kano.ylframework.data-assets/Runtime/Systems/DataAssets/ScriptableObjectData.cs", "YLFramework.DataAssets")]
        [TestCase("Packages/com.kano.ylframework.data-assets/Editor/Systems/DataAssets/ConfigsWindow.cs", "YLFramework.DataAssets.Editor")]
        [TestCase("Packages/com.kano.ylframework.localization/Runtime/Systems/Localization/LocalizeString.cs", "YLFramework.Localization")]
        [TestCase("Packages/com.kano.ylframework.localization/Editor/Systems/Localization/LocalizationConfigsWindow.cs", "YLFramework.Localization.Editor")]
        [TestCase("Packages/com.kano.ylframework.save/Runtime/Systems/Save/SaveManager.cs", "YLFramework.Save")]
        [TestCase("Assets/Tests/YLFramework/EditMode/Systems/Save/SaveFacadeContractsTests.cs", "YLFramework.Save.Tests.EditMode")]
        [TestCase("Assets/Tests/YLFramework/Fixtures/Save/Runtime/SavePlayerProbe.cs", "YLFramework.Save.Acceptance")]
        [TestCase("Assets/Tests/YLFramework/EditMode/Systems/DataAssets/DataAssetIdentityTests.cs", "YLFramework.Tests.EditMode")]
        [TestCase("Assets/Tests/YLFramework/PlayMode/Systems/DataAssets/DataAssetResourceTests.cs", "YLFramework.Tests.PlayMode")]
        [TestCase("Assets/Tests/YLFramework/EditMode/Editor/DataAssets/DataAssetAuthoringTests.cs", "YLFramework.Tests.EditMode")]
        [TestCase("Assets/Tests/YLFramework/Fixtures/Authoring/Data/Runtime/DataPlayerProbe.cs", "YLFramework.Tests.Data.Probes")]
        [TestCase("Assets/Tests/YLFramework/Fixtures/Authoring/Data/Editor/DataMainAcceptance.cs", "YLFramework.Tests.Tools.Editor")]
        [TestCase("Assets/Unrelated/User.cs", null)]
        public void ExplicitSourceAuditUsesTheActualRepositoryTestPrefixes(string path, string expected)
        {
            var entry = Type.GetType("YLFramework.Tests.DataBatch.DataMainAcceptance, YLFramework.Tests.Tools.Editor", true);
            var method = entry.GetMethod("ExpectedSourceOwner", BindingFlags.Static | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Selection and expected ownership need one explicit path contract.");
            Assert.That(method.Invoke(null, new object[] { path }), Is.EqualTo(expected), path);
            if (expected == null)
            {
                return;
            }
            string physical = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            var owner = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.sourceFiles.Any(source =>
                Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(source)) == physical));
            Assert.That(owner.name, Is.EqualTo(expected), path);
            if (path == "Packages/com.kano.ylframework.save/Runtime/Systems/Save/SaveManager.cs")
            {
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo("ff5957d84032533449f21601f372570a"));
            }
        }
    }
}
