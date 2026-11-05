using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine.TestTools;

namespace YLFramework.Tests
{
    [Category("YL.Module.Movement2D")]
    public class Movement2DVisibilityTests
    {
        [TestCase("YLFramework.Character2D", "YLFramework.Character2D.CheckGround")]
        [TestCase("YLFramework.Character2D", "YLFramework.Character2D.CheckWall")]
        [TestCase("YLFramework.Character2D", "YLFramework.Character2D.CheckTop")]
        public void ImplementationTypesAreNotPartOfThePublicContract(string assemblyName, string fullName)
        {
            var type = System.Reflection.Assembly.Load(assemblyName).GetType(fullName);
            Assert.That(type, Is.Not.Null, fullName);
            Assert.That(type.IsVisible, Is.False, fullName);
            Assert.That(type.IsPublic, Is.False, fullName);
        }

        [UnityTest]
        public IEnumerator NonFriendConsumerCanUsePublicContractsButNotMotorImplementation()
        {
            var folder = Path.Combine(Path.GetTempPath(), "kilo", "YLFramework.ScopeWitness-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(folder));
            try
            {
                foreach (var negative in new[] { true, false })
                {
                    var source = Path.Combine(folder, negative ? "Private.cs" : "Public.cs");
                    var output = Path.ChangeExtension(source, ".dll");
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(source), negative
                        ? "class Consumer { YLFramework.Character2D.CheckGround implementation; }"
                        : "class Consumer { YLFramework.Core.Module owner; YLFramework.Core.Panel panel; " +
                          "YLFramework.Audio.IAudioClipProvider provider; YLFramework.Resource.ResourceSystem resources; " +
                          "YLFramework.Actions.IAction action; YLFramework.Character2D.CharacterController2D motor; }");
                    var builder = new AssemblyBuilder(output, new[] { source })
                    {
                        flags = AssemblyBuilderFlags.EditorAssembly,
                        additionalReferences = new[] { typeof(YLFramework.Core.Module).Assembly.Location,
                            typeof(YLFramework.Character2D.CharacterController2D).Assembly.Location }
                    };
                    builder.excludeReferences = builder.defaultReferences.Where(path =>
                        Path.GetFileName(path).StartsWith("YLFramework", StringComparison.Ordinal) ||
                        Path.GetFileName(path).EndsWith("TestRunner.dll", StringComparison.Ordinal)).ToArray();
                    CompilerMessage[] messages = null;
                    builder.buildFinished += (_, result) => messages = result;
                    Assert.That(builder.Build(), Is.True);
                    while (builder.status != AssemblyBuilderStatus.Finished)
                    {
                        yield return null;
                    }
                    Assert.That(messages, Is.Not.Null);
                    var errors = messages.Where(message => message.type == CompilerMessageType.Error).ToArray();
                    if (negative)
                    {
                        Assert.That(errors.Any(error => error.message.Contains("CS0122") && error.message.Contains("CheckGround")),
                            Is.True, string.Join("\n", messages.Select(message => message.message)));
                    }
                    else
                    {
                        Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(error => error.message)));
                    }
                    Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(output)), Is.EqualTo(!negative));
                }
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(folder), true);
            }
        }
    }
}
