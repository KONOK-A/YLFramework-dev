using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

// 只记录真实测试树和载体输入模式，不改测试断言或替代 Unity Test Runner。
public static class Stage9ReceptionTests
{
    private static readonly string[] TestAssemblies =
    {
        "YLFramework.Tests.EditMode", "YLFramework.Core.Tests.EditMode", "YLFramework.Foundation.Tests.EditMode",
        "YLFramework.Core.Editor.Tests", "YLFramework.Tests.PlayMode", "YLFramework.Core.Tests.PlayMode", "YLFramework.Foundation.Tests.PlayMode"
    };

    private static readonly string[] HostOnly =
    {
        "YLFramework.Tests.DevelopmentHostBoundaryTests",
        "YLFramework.Tests.DomainMainAcceptanceInputTests",
        "YLFramework.Tests.ExportUnityPackageTests",
        "YLFramework.Core.Tests.CoreDefaultResourceTests",
        "YLFramework.Core.Tests.CoreIdentityTests.SerializedDictionaryHasOneFoundationOwnerAndLegacyTypesExit",
        "YLFramework.Core.Tests.CoreCoexistencePlayModeTests",
        "YLFramework.Core.Editor.Tests.CoreEditorIdentityTests",
        "YLFramework.Tests.Extensions.Equipment.EquipmentSettingsAdmissionTests"
    };

    private static readonly string[] Historical =
    {
        "YLFramework.Tests.Extensions.Data.DataDomainReloadSessionTests",
        "YLFramework.Tests.DomainMainAcceptanceTests",
        "YLFramework.Tests.ResourceBuild.ResourceContentDeliveryTests.PreE04OwnedCatalogIsConsumedWithoutRebuildingOrRewritingItsBytes"
    };

    private static readonly string[] PlatformBound =
    {
        "YLFramework.Tests.ResourceBuild.ResourceContentTests.UnsupportedBackendDoesNotImportCngOrFallBackToPlaintext"
    };

    private static readonly string[] Excel =
    {
        "YLFramework.Tests.Extensions.Data.ExcelAuthoringTests",
        "YLFramework.Tests.Extensions.Data.ExcelDataAssetsBindingTests",
        "YLFramework.Tests.Extensions.Data.ExcelAssetAuthoringTests"
    };

    private static TestRunnerApi _api;
    private static string Project => Path.GetFullPath(Application.dataPath + "/..");
    private static JObject Owner => JObject.Parse(File.ReadAllText(Path.Combine(Project, "stage9-owner.json")));
    private static string Run => Argument("-s9-run");

    public static void TestsDiscover() => Discover((string)Owner["Delivery"], Run);
    public static void TestsLegacy() => ConfigureInput(0);
    public static void TestsLegacyNoReload() => ConfigureInput(0, true);
    public static void TestsNew() => ConfigureInput(1);
    public static void TestsBoth() => ConfigureInput(2);

    public static void TestsDOTween()
    {
        VerifyReceiver();
        var wrapper = Path.Combine(Project, "Assets/YLFramework/Integrations/DOTween/Runtime");
        var dll = Path.Combine(Project, "Assets/Plugins/Demigiant/DOTween/DOTween.dll");
        if (Argument("-s9-optional-integration") != "DOTween")
        {
            throw new InvalidOperationException("Explicit installed DOTween wrapper and exact local 1.2.825 dependency required.");
        }

        if (!Regex.IsMatch(Run ?? "", "^[A-Za-z0-9-]+$") || !Application.isBatchMode ||
            EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorUtility.scriptCompilationFailed || Enumerable.Range(0, EditorSceneManager.sceneCount)
                .Any(index => EditorSceneManager.GetSceneAt(index).isDirty))
        {
            throw new InvalidOperationException("New idle optional setup required.");
        }

        var output = Path.Combine((string)Owner["Delivery"], Path.GetFileName(Project) + "-dotween-" + Run + ".json");
        var inputs = new[] { "DOTweenAction.cs", "YLFramework.Actions.DOTween.asmdef", "csc.rsp" }
            .Select(name => Path.Combine(wrapper, name)).Concat(new[] { dll }).ToArray();
        foreach (var path in inputs.SelectMany(path => new[] { path, path + ".meta" }).Concat(new[] { output }))
        {
            for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                    (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Linked optional setup path rejected: " + cursor);
                }
            }
        }
        if (!inputs.All(path => File.Exists(path) && File.Exists(path + ".meta")) || new FileInfo(dll).Length != 177664 ||
            FileHash(dll) != "d74bfc3f37cd97d90f7f08a9a73e204c5eae66e77be24b12378a4e99f1690843")
        {
            throw new InvalidOperationException("Explicit installed DOTween wrapper and exact local 1.2.825 dependency required.");
        }
        if (File.Exists(output) || Directory.Exists(output))
        {
            throw new IOException("Never overwrite optional setup evidence.");
        }

        var before = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone);
        var symbols = before.Split(';').Where(value => !string.IsNullOrEmpty(value)).ToList();
        if (!symbols.Contains("DOTWEEN"))
        {
            symbols.Add("DOTWEEN");
        }
        PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone, string.Join(";", symbols));
        var after = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone);
        if (!after.Split(';').Contains("DOTWEEN"))
        {
            throw new InvalidOperationException("DOTWEEN must be enabled before restarting this receiving Editor.");
        }
        File.WriteAllText(output, new JObject
        {
            ["Project"] = Project,
            ["Unity"] = Application.unityVersion,
            ["PackageSha256"] = Owner["PackageSha256"],
            ["OptionalIntegration"] = "DOTween",
            ["Before"] = before,
            ["After"] = after,
            ["WrapperAsmdefSha256"] = FileHash(Path.Combine(wrapper, "YLFramework.Actions.DOTween.asmdef")),
            ["DependencySha256"] = FileHash(dll),
            ["VendorVersion"] = "1.2.825",
            ["LocalDependencyCopy"] = true,
            ["VendorRedistributionAuthorized"] = false,
            ["RestartRequired"] = true,
            ["CompiledAfterRestart"] = false
        }.ToString(Formatting.Indented));
    }

    public static void TestsDomainExcel()
    {
        VerifyReceiver();
        if (Argument("-s9-optional-integration") != "DomainExcel" ||
            !File.Exists("Assets/YLFramework/Integrations/Excel/Editor/YLFramework.Excel.Editor.asmdef") ||
            !File.Exists("Assets/YLFramework/Integrations/DomainExcel/Editor/YLFramework.Domain.Excel.Editor.asmdef"))
        {
            throw new InvalidOperationException("Explicit installed Excel and DomainExcel closures required before enabling their macro.");
        }
        var output = Path.Combine((string)Owner["Delivery"], Path.GetFileName(Project) + "-domain-excel-" + Run + ".json");
        if (File.Exists(output) || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed)
        {
            throw new InvalidOperationException("New idle optional setup required.");
        }
        var before = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone);
        var symbols = before.Split(';').Where(value => !string.IsNullOrEmpty(value)).ToList();
        if (!symbols.Contains("YL_DOMAIN_EXCEL"))
        {
            symbols.Add("YL_DOMAIN_EXCEL");
        }
        PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone, string.Join(";", symbols));
        File.WriteAllText(output, new JObject
        {
            ["Project"] = Project,
            ["Unity"] = Application.unityVersion,
            ["OptionalIntegration"] = "DomainExcel",
            ["Before"] = before,
            ["After"] = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone),
            ["RestartRequired"] = true,
            ["CompiledAfterRestart"] = false
        }.ToString(Formatting.Indented));
    }

    public static void TestsAnalyzerPrepare()
    {
        VerifyAnalyzerReceiver();
        var paths = new[]
        {
            "Assets/AnalyzerConsumers", "Assets/IdeDefaultProbe.cs", "Assets/Assembly-CSharp.ruleset"
        };
        if (Directory.Exists(AnalyzerRoot) || File.Exists(AnalyzerRoot) || paths.Any(path =>
                Directory.Exists(path) || File.Exists(path) || File.Exists(path + ".meta")))
        {
            throw new IOException("Never adopt an existing Analyzer acceptance root or input.");
        }

        Directory.CreateDirectory(AnalyzerRoot);
        var owner = new JObject
        {
            ["Purpose"] = "Stage9-owned-analyzer-inputs",
            ["Project"] = Project,
            ["Unity"] = Application.unityVersion,
            ["PackageSha256"] = Owner["PackageSha256"],
            ["AnalyzerSha256"] = FileHash(AnalyzerDll),
            ["ExternalEditor"] = EditorPrefs.GetString("kScriptsDefaultApp", ""),
            ["DefaultRuleSetSha256"] = File.Exists("Assets/Default.ruleset") ? FileHash("Assets/Default.ruleset") : null
        };
        File.WriteAllText(Path.Combine(AnalyzerRoot, "owner.json"), owner.ToString(Formatting.Indented));
        Directory.CreateDirectory("Assets/AnalyzerConsumers");
        File.WriteAllText("Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.asmdef", new JObject
        {
            ["name"] = AnalyzerAssembly,
            ["references"] = new JArray("YLFramework.Runtime"),
            ["autoReferenced"] = true
        }.ToString(Formatting.Indented));
        File.WriteAllText("Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs", AnalyzerProbeSource("IdeDiagnosticProbe"));
        File.WriteAllText("Assets/IdeDefaultProbe.cs", AnalyzerProbeSource("IdeDefaultProbe"));
        var rules = new Dictionary<string, string> { ["YL0008"] = "Warning", ["YL0009"] = "Warning", ["YL0010"] = "Warning" };
        var asmdefRuleSet = ApplyAnalyzerRules(AnalyzerAssembly, "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.asmdef", rules);
        var predefinedRuleSet = ApplyAnalyzerRules("Assembly-CSharp", null, rules);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var inputs = new[]
        {
            "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.asmdef", "Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs",
            "Assets/IdeDefaultProbe.cs", asmdefRuleSet, predefinedRuleSet
        };
        owner["Inputs"] = JArray.FromObject(inputs.Select(path => new
        {
            Path = Path.GetRelativePath(Project, Path.GetFullPath(path)).Replace('\\', '/'),
            Sha256 = FileHash(path),
            Guid = AssetDatabase.AssetPathToGUID(path),
            MetaSha256 = FileHash(path + ".meta")
        }));
        if (owner["Inputs"].Any(input => string.IsNullOrEmpty((string)input["Guid"])))
        {
            throw new InvalidOperationException("Every Analyzer acceptance input must have an imported identity.");
        }
        File.WriteAllText(Path.Combine(AnalyzerRoot, "owner.json"), owner.ToString(Formatting.Indented));
        SaveAnalyzerReceipt(new
        {
            Prepared = true,
            Project,
            Unity = Application.unityVersion,
            Compiling = EditorApplication.isCompiling,
            Inputs = owner["Inputs"]
        });
    }

    public static void TestsAnalyzerInspect()
    {
        var owner = RequireAnalyzerInputs();
        var graph = new List<object>();
        foreach (var kind in new[] { AssembliesType.Editor, AssembliesType.Player })
        {
            var assemblies = CompilationPipeline.GetAssemblies(kind);
            foreach (var name in new[] { "Assembly-CSharp", AnalyzerAssembly })
            {
                var assembly = assemblies.Single(item => item.name == name);
                var analyzers = assembly.compilerOptions.RoslynAnalyzerDllPaths.Where(path =>
                    Path.GetFileName(path) == "YLFramework.Analyzers.dll").ToArray();
                var ruleSet = assembly.compilerOptions.RoslynAnalyzerRulesetPath;
                var expected = name == AnalyzerAssembly ? "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset" : "Assets/Assembly-CSharp.ruleset";
                if (analyzers.Length != 1 || FileHash(analyzers[0]) != (string)owner["AnalyzerSha256"] ||
                    string.IsNullOrEmpty(ruleSet) || Path.GetFullPath(ruleSet) != Path.GetFullPath(expected) ||
                    !assembly.assemblyReferences.Any(reference => reference.name == "YLFramework.Runtime"))
                {
                    throw new InvalidOperationException("The real compilation graph lost its exact Analyzer, Runtime reference or local ruleset: " + name);
                }
                graph.Add(new
                {
                    Kind = kind.ToString(),
                    Assembly = name,
                    Analyzer = analyzers.Single(),
                    RuleSet = ruleSet,
                    Sources = assembly.sourceFiles
                });
            }
        }
        SaveAnalyzerReceipt(new
        {
            Passed = true,
            Project,
            Unity = Application.unityVersion,
            CompilationFailed = EditorUtility.scriptCompilationFailed,
            Graph = graph
        });
    }

    public static void TestsAnalyzerVisualStudioProjects() => GenerateAnalyzerProjects(false);
    public static void TestsAnalyzerRiderProjects() => GenerateAnalyzerProjects(true);
    public static void TestsAnalyzerAsmdefError() => SetAnalyzerPolicy(true, "Error");
    public static void TestsAnalyzerDefaultError() => SetAnalyzerPolicy(false, "Error");
    public static void TestsAnalyzerAsmdefNone() => SetAnalyzerPolicy(true, "None");
    public static void TestsAnalyzerDefaultNone() => SetAnalyzerPolicy(false, "None");
    public static void TestsAnalyzerAsmdefInherit() => SetAnalyzerPolicy(true, null);
    public static void TestsAnalyzerDefaultInherit() => SetAnalyzerPolicy(false, null);

    private static void SetAnalyzerPolicy(bool custom, string action)
    {
        RequireAnalyzerInputs();
        if (File.Exists(Path.Combine(AnalyzerRoot, Run + ".json")))
        {
            throw new IOException("Never overwrite an Analyzer policy experiment.");
        }
        var assembly = custom ? AnalyzerAssembly : "Assembly-CSharp";
        var asmdef = custom ? "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.asmdef" : null;
        var path = custom ? "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset" : "Assets/Assembly-CSharp.ruleset";
        var before = File.ReadAllBytes(path);
        var beforeHash = FileHash(path);
        var metaHash = FileHash(path + ".meta");
        var applied = ApplyAnalyzerRules(assembly, asmdef, new Dictionary<string, string> { ["YL0008"] = action });
        if (Path.GetFullPath(applied) != Path.GetFullPath(path) || FileHash(path + ".meta") != metaHash)
        {
            throw new InvalidOperationException("Policy application must preserve its exact owned leaf identity.");
        }
        SaveAnalyzerReceipt(new
        {
            PolicyWritten = true,
            DiagnosticsVerified = false,
            Project,
            Unity = Application.unityVersion,
            PackageSha256 = Owner["PackageSha256"],
            Assembly = assembly,
            RuleSet = path,
            Rule = "YL0008",
            Action = action ?? "Inherit",
            BeforeSha256 = beforeHash,
            BeforeBytesBase64 = Convert.ToBase64String(before),
            AfterSha256 = FileHash(path),
            MetaSha256 = metaHash,
            OwnerSha256 = FileHash(Path.Combine(AnalyzerRoot, "owner.json"))
        });
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
    }

    private static void GenerateAnalyzerProjects(bool rider)
    {
        var owner = RequireAnalyzerInputs();
        var targetsPath = Path.Combine(AnalyzerRoot, "ide-targets.json");
        var targets = JObject.Parse(File.ReadAllText(targetsPath));
        if ((string)targets["Purpose"] != "Stage9-analyzer-ide-targets" || (string)targets["Project"] != Project ||
            (string)targets["Unity"] != Application.unityVersion || (string)targets["PackageSha256"] != (string)owner["PackageSha256"])
        {
            throw new InvalidOperationException("IDE generation requires this Analyzer owner's explicit target identity.");
        }
        var snapshot = Path.Combine(AnalyzerRoot, Run + "-files");
        if (Directory.Exists(snapshot) || File.Exists(snapshot) || File.Exists(Path.Combine(AnalyzerRoot, Run + ".json")))
        {
            throw new IOException("Never overwrite a previous IDE generation snapshot.");
        }
        object generator;
        string executable = null;
        if (rider)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Packages.Rider.Editor.RiderScriptEditor"))
                .Single(candidate => candidate != null);
            type.GetMethod("SyncSolution", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            generator = type.GetField("m_ProjectGeneration", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        }
        else
        {
            executable = (string)targets["VisualStudioExecutable"];
            if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
                Path.GetFileName(executable) != "devenv.exe" || System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileMajorPart != 17)
            {
                throw new InvalidOperationException("The declared Visual Studio 2022 executable is unavailable or has another major version.");
            }
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Microsoft.Unity.VisualStudio.Editor.VisualStudioEditor"))
                .Single(candidate => candidate != null);
            var adapter = Activator.CreateInstance(type);
            var arguments = new object[] { executable, true, null };
            var found = (bool)type.GetMethod("TryGetVisualStudioInstallationForPath", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(adapter, arguments);
            if (!found)
            {
                throw new InvalidOperationException("The declared Visual Studio installation was not discovered.");
            }
            generator = arguments[2].GetType().GetProperty("ProjectGenerator").GetValue(arguments[2]);
            generator.GetType().GetMethod("Sync").Invoke(generator, null);
        }
        var solution = Path.GetFullPath((string)generator.GetType().GetMethod("SolutionFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(generator, null));
        if (Path.GetDirectoryName(solution) != Project || !File.Exists(solution))
        {
            throw new InvalidOperationException("The selected generator did not return this project's actual solution.");
        }
        Directory.CreateDirectory(snapshot);
        File.Copy(solution, Path.Combine(snapshot, Path.GetFileName(solution)));
        var projects = new List<object>();
        foreach (var name in new[] { "Assembly-CSharp", AnalyzerAssembly })
        {
            var path = Path.Combine(Project, name + ".csproj");
            var document = XDocument.Load(path);
            var analyzers = document.Descendants().Where(element => element.Name.LocalName == "Analyzer")
                .Select(element => (string)element.Attribute("Include")).Where(value => Path.GetFileName(value) == "YLFramework.Analyzers.dll").ToArray();
            var ruleset = document.Descendants().Single(element => element.Name.LocalName == "CodeAnalysisRuleSet").Value;
            var expected = name == AnalyzerAssembly ? "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset" : "Assets/Assembly-CSharp.ruleset";
            var probe = name == AnalyzerAssembly ? "Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs" : "Assets/IdeDefaultProbe.cs";
            var includes = document.Descendants().Where(element => element.Name.LocalName == "Compile")
                .Select(element => (string)element.Attribute("Include")).Where(value => !string.IsNullOrEmpty(value))
                .Select(value => Path.GetFullPath(Path.Combine(Project, value))).ToArray();
            if (analyzers.Length != 1 || FileHash(Path.Combine(Project, analyzers[0])) != (string)owner["AnalyzerSha256"] ||
                Path.GetFullPath(Path.Combine(Project, ruleset)) != Path.GetFullPath(expected) || !includes.Contains(Path.GetFullPath(probe)))
            {
                throw new InvalidOperationException("The generated IDE project lost its Analyzer, local policy or actual probe: " + name);
            }
            File.Copy(path, Path.Combine(snapshot, Path.GetFileName(path)));
            projects.Add(new
            {
                Assembly = name,
                Path = path,
                Sha256 = FileHash(path),
                Analyzer = analyzers.Single(),
                RuleSet = ruleset,
                Probe = probe
            });
        }
        RequireAnalyzerInputs();
        SaveAnalyzerReceipt(new
        {
            Passed = true,
            Project,
            Unity = Application.unityVersion,
            Generator = generator.GetType().FullName,
            PolicyExperiment = Argument("-s9-analyzer-policy"),
            Executable = executable,
            TargetsSha256 = FileHash(targetsPath),
            Solution = solution,
            SolutionSha256 = FileHash(solution),
            Snapshot = snapshot,
            Projects = projects,
            DefaultEditorUnchanged = true,
            IdeLaunched = false
        });
    }

    private const string AnalyzerAssembly = "Analyzer.Acceptance.Consumer";
    private const string AnalyzerDll = "Assets/YLFramework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll";
    private static string AnalyzerRoot => Path.Combine((string)Owner["Delivery"], "Analyzer-" + Path.GetFileName(Project));

    private static void VerifyAnalyzerReceiver()
    {
        VerifyReceiver();
        if (string.IsNullOrEmpty(Run) || !Regex.IsMatch(Run, "^[A-Za-z0-9-]+$") ||
            !Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || EditorUtility.scriptCompilationFailed ||
            Enumerable.Range(0, EditorSceneManager.sceneCount).Any(index => EditorSceneManager.GetSceneAt(index).isDirty))
        {
            throw new InvalidOperationException("Analyzer setup requires this owned, idle, compiling-clean batch Editor and no dirty scenes.");
        }
        var rejectLinks = typeof(YLFramework.Resource.ResourceLocalSnapshot).GetMethod("RejectLinks", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var path in new[] { AnalyzerRoot, "Assets/AnalyzerConsumers", "Assets/IdeDefaultProbe.cs", "Assets/Assembly-CSharp.ruleset", "Assets/Default.ruleset", AnalyzerDll })
        {
            rejectLinks.Invoke(null, new object[] { Path.GetFullPath(path) });
        }
    }

    private static JObject RequireAnalyzerInputs()
    {
        VerifyAnalyzerReceiver();
        var owner = JObject.Parse(File.ReadAllText(Path.Combine(AnalyzerRoot, "owner.json")));
        if ((string)owner["Purpose"] != "Stage9-owned-analyzer-inputs" || (string)owner["Project"] != Project ||
            (string)owner["Unity"] != Application.unityVersion || (string)owner["PackageSha256"] != (string)Owner["PackageSha256"] ||
            (string)owner["AnalyzerSha256"] != FileHash(AnalyzerDll) ||
            (string)owner["ExternalEditor"] != EditorPrefs.GetString("kScriptsDefaultApp", "") ||
            (string)owner["DefaultRuleSetSha256"] != (File.Exists("Assets/Default.ruleset") ? FileHash("Assets/Default.ruleset") : null))
        {
            throw new InvalidOperationException("Analyzer ownership, global editor selection or default policy changed.");
        }
        var expected = new[]
        {
            "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.asmdef", "Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs",
            "Assets/IdeDefaultProbe.cs", "Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset", "Assets/Assembly-CSharp.ruleset"
        };
        if (!owner["Inputs"].Select(input => (string)input["Path"]).SequenceEqual(expected))
        {
            throw new InvalidOperationException("Analyzer input paths must remain the exact owned five-file set.");
        }
        JObject policy = null;
        var policyRun = Argument("-s9-analyzer-policy");
        if (policyRun != null)
        {
            var method = Argument("-executeMethod");
            var allowed = new[] { "TestsAnalyzerInspect", "TestsAnalyzerVisualStudioProjects", "TestsAnalyzerRiderProjects" };
            if (!Regex.IsMatch(policyRun, "^[A-Za-z0-9-]+$") || !allowed.Any(name => method == "Stage9ReceptionTests." + name))
            {
                throw new InvalidOperationException("An explicit policy snapshot is read/generation-only.");
            }
            policy = JObject.Parse(File.ReadAllText(Path.Combine(AnalyzerRoot, policyRun + ".json")));
            if ((bool?)policy["PolicyWritten"] != true || (string)policy["Project"] != Project ||
                (string)policy["Unity"] != Application.unityVersion || (string)policy["PackageSha256"] != (string)owner["PackageSha256"] ||
                (string)policy["OwnerSha256"] != FileHash(Path.Combine(AnalyzerRoot, "owner.json")) ||
                !expected.Skip(3).Contains((string)policy["RuleSet"]))
            {
                throw new InvalidOperationException("The selected policy belongs to another input/owner snapshot.");
            }
        }
        foreach (var input in owner["Inputs"])
        {
            var path = (string)input["Path"];
            var sha = (string)input["Sha256"];
            if (policy != null && path == (string)policy["RuleSet"])
            {
                if ((string)policy["BeforeSha256"] != sha)
                {
                    throw new InvalidOperationException("The selected policy was not derived from the immutable baseline.");
                }
                sha = (string)policy["AfterSha256"];
            }
            if (FileHash(path) != sha || FileHash(path + ".meta") != (string)input["MetaSha256"] ||
                AssetDatabase.AssetPathToGUID(path) != (string)input["Guid"])
            {
                throw new InvalidOperationException("Analyzer acceptance input identity changed: " + path);
            }
        }
        return owner;
    }

    private static string ApplyAnalyzerRules(string assembly, string asmdef, IReadOnlyDictionary<string, string> changes)
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(item => item.GetType("YLFramework.Editor.Core.Analyzers.AnalyzerRuleSetFile"))
            .Single(item => item != null);
        var preview = type.GetMethod("Preview", flags).Invoke(null, new object[] { Project, assembly, asmdef, changes });
        var properties = BindingFlags.Instance | BindingFlags.NonPublic;
        var blocked = (string)preview.GetType().GetProperty("BlockedReason", properties).GetValue(preview);
        if (blocked != null)
        {
            throw new InvalidOperationException(blocked);
        }
        type.GetMethod("Apply", flags).Invoke(null, new[] { preview });
        return Path.GetRelativePath(Project, (string)preview.GetType().GetProperty("TargetPath", properties).GetValue(preview)).Replace('\\', '/');
    }

    private static string AnalyzerProbeSource(string name) =>
        "using System.Collections;\nusing UnityEngine;\nusing YLFramework.Core;\nusing YLFramework.Audio;\nusing YLFramework.DependencyInjection;\n" +
        "public sealed class " + name + "\n{\n    [Inject] public static object Bad;\n" +
        "    public void Load(Module owner) { owner.LoadView<Panel>(\"name\", loadViewArgs: null); }\n" +
        "    public IEnumerator Run(AudioPlayRequest request) { yield return request; }\n}\n";

    private static void SaveAnalyzerReceipt(object value)
    {
        if (string.IsNullOrEmpty(Run) || !Regex.IsMatch(Run, "^[A-Za-z0-9-]+$"))
        {
            throw new InvalidOperationException("A new explicit Analyzer run identity is required.");
        }
        var path = Path.Combine(AnalyzerRoot, Run + ".json");
        if (File.Exists(path))
        {
            throw new IOException("Never overwrite Analyzer acceptance evidence.");
        }
        File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented));
        Debug.Log("STAGE9_ANALYZER_EVIDENCE " + path);
    }

    [InitializeOnLoadMethod]
    private static void RegisterRunnerSceneSetup()
    {
        var profilePath = Argument("-s9-test-profile");
        var discoveryPath = Argument("-s9-test-discovery");
        if (string.IsNullOrEmpty(discoveryPath))
        {
            return;
        }

        VerifyReceiver();
        TestRunnerApi.RegisterTestCallback(new RunnerSceneSetup(profilePath, discoveryPath));
    }

    private sealed class RunnerSceneSetup : ICallbacks
    {
        private readonly string _profilePath;
        private readonly string _discoveryPath;

        public RunnerSceneSetup(string profilePath, string discoveryPath)
        {
            _profilePath = profilePath;
            _discoveryPath = discoveryPath;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            VerifyReceiver();
            var discovery = JObject.Parse(File.ReadAllText(_discoveryPath));
            if (FileHash(_discoveryPath) != Argument("-s9-test-discovery-sha") || (string)discovery["Schema"] != "Stage9TestDiscovery-v2")
            {
                throw new InvalidOperationException("The exact discovery proof changed before the official runner started.");
            }
            var currentBinding = CaptureTestBinding(CompilationPipeline.GetAssemblies(AssembliesType.Editor));
            foreach (var property in currentBinding.Properties())
            {
                if (!JToken.DeepEquals(property.Value, discovery[property.Name]))
                {
                    throw new InvalidOperationException("Current compiled test input differs from discovery: " + property.Name);
                }
            }
            var validation = Path.Combine((string)Owner["Delivery"], Path.GetFileName(Project) + "-tests-input-validation-" + Run + ".json");
            if (File.Exists(validation))
            {
                throw new IOException("Never overwrite runner input validation.");
            }
            File.WriteAllText(validation, new JObject
            {
                ["Passed"] = true,
                ["OwnerSha256"] = currentBinding["OwnerSha256"],
                ["PackageSha256"] = currentBinding["PackageSha256"],
                ["DiscoverySha256"] = FileHash(_discoveryPath),
                ["CompilationSha256"] = currentBinding["CompilationSha256"]
            }.ToString(Formatting.Indented));
            if (string.IsNullOrEmpty(_profilePath) || Argument("-testPlatform") != "EditMode")
            {
                return;
            }
            var delivery = (string)Owner["Delivery"];
            var prefix = Path.GetFileName(Project) + "-tests-profile-";
            var name = Path.GetFileNameWithoutExtension(_profilePath);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Regex.IsMatch(name.Substring(prefix.Length), "^[A-Za-z0-9-]+$") ||
                Path.GetFullPath(_profilePath) != Path.Combine(delivery, name + ".json"))
            {
                throw new InvalidOperationException("Only this receiver's explicit test profile may prepare scenes.");
            }

            var profile = JObject.Parse(File.ReadAllText(_profilePath));
            var scenePath = (string)profile["BootstrapScene"];
            if ((string)profile["Project"] != Project || (string)profile["Unity"] != Application.unityVersion ||
                scenePath != "Assets/Stage9TestsBootstrap-" + name.Substring(prefix.Length) + ".unity" ||
                FileHash(scenePath) != (string)profile["BootstrapSha256"])
            {
                throw new InvalidOperationException("The owned empty test scene no longer matches its profile.");
            }

            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var current = EditorSceneManager.GetSceneAt(i);
                if (current.isDirty || (!string.IsNullOrEmpty(current.path) && current.path != scenePath))
                {
                    throw new InvalidOperationException("Never replace dirty or unknown scenes in test setup.");
                }

                var roots = current.GetRootGameObjects();
                if (roots.Length == 0)
                {
                    continue;
                }

                // UTF 1.6 的 EditMode bootstrap 是默认 camera/light；拒绝额外对象或组件。
                var camera = roots.Where(root => root.GetComponent<Camera>()).ToArray();
                var light = roots.Where(root => root.GetComponent<Light>()).ToArray();
                if (!string.IsNullOrEmpty(current.path) || roots.Length != 2 ||
                    roots.Any(root => root.transform.childCount != 0) || camera.Length != 1 || light.Length != 1 ||
                    camera[0] == light[0] || !camera[0].GetComponent<AudioListener>() ||
                    camera[0].GetComponents<Component>().Any(component => !component ||
                        (component is not Transform && component is not Camera && component is not AudioListener)) ||
                    light[0].GetComponents<Component>().Any(component => !component ||
                        (component is not Transform && component is not Light)))
                {
                    throw new InvalidOperationException("Only replace the known clean official runner bootstrap.");
                }
            }

            // 回调在两代官方 runner 的建场景之后执行；保留已保存场景和空 active runner 的原测试拓扑。
            var saved = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (saved.isDirty || saved.rootCount != 0)
            {
                throw new InvalidOperationException("The profile bootstrap must remain a clean empty saved scene.");
            }

            var runner = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(runner);
            Debug.Log("STAGE9_TEST_SCENES_CONFIGURED " + scenePath);
            var costRoot = Argument("-s9-cost-root");
            if (!string.IsNullOrEmpty(costRoot))
            {
                var selection = JObject.Parse(File.ReadAllText(Argument("-s9-test-selection")));
                var selected = ((JArray)selection["Expected"]).Select(row => (string)row["Name"]).ToArray();
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("YLFramework.Tests.ResourceBuild.ResourceCostEvidence"))
                    .Single(candidate => candidate != null);
                var scope = Argument("-s9-cost-scope");
                var purpose = Argument("-s9-cost-purpose");
                var acquiredEnvironment = false;
                try
                {
                    type.GetMethod("UseLoopbackEnvironment").Invoke(null, new object[] { costRoot, scope, purpose });
                    acquiredEnvironment = true;
                    type.GetMethod("BeginRun").Invoke(null, new object[] { costRoot, Argument("-s9-cost-run"), selected, scope, purpose });
                }
                catch
                {
                    if (acquiredEnvironment)
                    {
                        type.GetMethod("RestoreLoopbackEnvironment").Invoke(null, null);
                    }
                    throw;
                }
            }
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            TestRunnerApi.UnregisterTestCallback(this);
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }
    }

    public static void Discover(string delivery, string run)
    {
        var root = Path.GetFullPath(delivery);
        var rootOwner = JObject.Parse(File.ReadAllText(Path.Combine(root, "owner.json")));
        var source = Project == (string)rootOwner["repository"];
        if ((string)rootOwner["purpose"] != "YLFramework-Stage9-independent-delivery" || (string)rootOwner["root"] != root ||
            (source && (Project != @"G:\Unity\Projects\CGJ2024" || Application.unityVersion != "2022.3.62f3c1")))
        {
            throw new InvalidOperationException("Wrong test-discovery ownership.");
        }

        if (!source)
        {
            VerifyReceiver();
        }

        var optional = Argument("-s9-optional-integration");
        var expectedAssemblies = ExpectedTestAssemblies(optional);

        if (!Regex.IsMatch(run ?? "", "^[A-Za-z0-9-]+$"))
        {
            throw new ArgumentException("Explicit discovery run identity required.");
        }

        var output = Path.Combine(root, (source ? "Source" : Path.GetFileName(Project)) + "-tests-discovery-" + run + ".json");
        if (File.Exists(output) || EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed)
        {
            throw new InvalidOperationException("Never overwrite discovery or inspect a failed/playing Editor.");
        }

        var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
        foreach (var name in expectedAssemblies.Concat(new[] { "YLFramework.UI.Acceptance", "YLFramework.Core.Tests.Fixtures.Editor",
            "YLFramework.Behavior.Acceptance", "YLFramework.Behavior.Acceptance.Editor" }))
        {
            if (graph.Count(a => a.name == name) != 1)
            {
                throw new InvalidOperationException("Required test/helper assembly is absent: " + name);
            }
        }

        var inputBinding = source ? null : CaptureTestBinding(graph);

        _api = ScriptableObject.CreateInstance<TestRunnerApi>();
        _api.RetrieveTestList(TestMode.EditMode, edit =>
        {
            var editRows = Leaves(edit, null, expectedAssemblies).ToArray();
            _api.RetrieveTestList(TestMode.PlayMode, play =>
            {
                var playRows = Leaves(play, null, expectedAssemblies).ToArray();
                var all = editRows.Concat(playRows).ToArray();
                var missing = expectedAssemblies.Where(name => !all.Any(row => (string)row["Assembly"] == name)).ToArray();
                var excluded = HostOnly.Concat(Historical).Concat(PlatformBound).Concat(Excel)
                    .Concat(new[] { "YLFramework.Tests.Actions.DOTween" }).Select(Regex.Escape);
                var filter = "^(?!(?:" + string.Join("|", excluded) + @")(?:[.(]|$)).*";
                if (!source && !JToken.DeepEquals(inputBinding, CaptureTestBinding(CompilationPipeline.GetAssemblies(AssembliesType.Editor))))
                {
                    throw new InvalidOperationException("Source/compiled graph changed while retrieving the official test tree.");
                }
                var proof = new JObject
                {
                    ["Schema"] = "Stage9TestDiscovery-v2",
                    ["Passed"] = missing.Length == 0,
                    ["Project"] = Project,
                    ["Unity"] = Application.unityVersion,
                    ["SourceHost"] = source,
                    ["OptionalIntegration"] = optional,
                    ["ReceiverFilter"] = filter,
                    ["MissingTestAssemblies"] = JArray.FromObject(missing),
                    ["EditMode"] = new JArray(editRows),
                    ["PlayMode"] = new JArray(playRows),
                    ["Compilation"] = JArray.FromObject(graph.Select(a => new { a.name, a.defines, a.sourceFiles, References = a.assemblyReferences.Select(r => r.name).ToArray() }))
                };
                if (inputBinding != null)
                {
                    foreach (var property in inputBinding.Properties())
                    {
                        proof[property.Name] = property.Value;
                    }
                }
                File.WriteAllText(output, proof.ToString(Formatting.Indented));
                var completedApi = _api;
                _api = null;
                Debug.Log("STAGE9_TEST_DISCOVERY " + output + " / Edit=" + editRows.Length + " Play=" + playRows.Length);
                // 1.3.9 在调用回调后才移除 TestListJob.update，不能在该栈内销毁 API 或退出。
                EditorApplication.delayCall += () =>
                {
                    UnityEngine.Object.DestroyImmediate(completedApi);
                    if (!source && !Environment.GetCommandLineArgs().Contains("-runTests"))
                    {
                        EditorApplication.Exit(missing.Length == 0 ? 0 : 4);
                    }
                };
            });
        });
    }

    public static void TestsCoexistence()
    {
        VerifyReceiver();
        var output = Path.Combine((string)Owner["Delivery"], Path.GetFileName(Project) + "-coexistence-" + Run + ".json");
        if (File.Exists(output) || Directory.Exists(output) || EditorApplication.isPlayingOrWillChangePlaymode ||
            EditorApplication.isCompiling || EditorApplication.isUpdating || EditorUtility.scriptCompilationFailed)
        {
            throw new InvalidOperationException("New idle coexistence witness required.");
        }
        var graph = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
        var required = new[]
        {
            "YLFramework.Excel.Editor", "YLFramework.Domain.Excel.Editor", "YLFramework.Actions.DOTween",
            "YLFramework.HybridCLR", "YLFramework.HybridCLR.Editor", "YLFramework.HybridCLR.Sample.Editor",
            "YLFramework.Domain.Excel.Tests", "YLFramework.Actions.DOTween.Tests.EditMode", "YLFramework.Actions.DOTween.Tests.PlayMode",
            "YLFramework.HybridCLR.Tests.EditMode", "YLFramework.HybridCLR.Tests.PlayMode"
        };
        foreach (var name in required)
        {
            if (graph.Count(assembly => assembly.name == name) != 1)
            {
                throw new InvalidOperationException("Coexistence assembly missing or ambiguous: " + name);
            }
        }
        if (!graph.Single(assembly => assembly.name == "YLFramework.Actions.DOTween").defines.Contains("DOTWEEN") ||
            !graph.Single(assembly => assembly.name == "YLFramework.Domain.Excel.Editor").defines.Contains("YL_DOMAIN_EXCEL") ||
            !graph.Single(assembly => assembly.name == "YLFramework.HybridCLR").defines.Contains("YL_HYBRIDCLR_9_0_3"))
        {
            throw new InvalidOperationException("All three version/macro constraints must actually compile together.");
        }
        var sdk = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
            .Single(package => package.name == "com.code-philosophy.hybridclr");
        var sdkLock = JObject.Parse(File.ReadAllText("Packages/packages-lock.json"))["dependencies"][sdk.name];
        if (sdk.version != "9.0.3" || (string)sdkLock["version"] != "https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3" ||
            (string)sdkLock["hash"] != "6cadd24d101f4cbd00cd80190106f6621132d24d")
        {
            throw new InvalidOperationException("Exact official HybridCLR Git revision required, without native installation.");
        }
        var fixtures = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "YLFramework.HybridCLR.Tests.EditMode")
            .GetType("YLFramework.Tests.Integrations.HybridCLR.HybridCLRTestFixtures", true);
        fixtures.GetMethod("Prepare", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        File.WriteAllText(output, new JObject
        {
            ["Passed"] = true,
            ["Project"] = Project,
            ["Unity"] = Application.unityVersion,
            ["PackageSha256"] = Owner["PackageSha256"],
            ["HybridCLRVersion"] = sdk.version,
            ["HybridCLRCommit"] = sdkLock["hash"],
            ["RequiredAssemblies"] = JArray.FromObject(required),
            ["Compilation"] = JArray.FromObject(graph.Where(assembly => required.Contains(assembly.name))
                .Select(assembly => new { assembly.name, assembly.defines, assembly.sourceFiles, References = assembly.assemblyReferences.Select(reference => reference.name).ToArray() })),
            ["PreparedInThisEngine"] = true,
            ["NativeInstalled"] = false,
            ["NativeValidated"] = false,
            ["VendorRedistributionAuthorized"] = false
        }.ToString(Formatting.Indented));
    }

    private static string[] ExpectedTestAssemblies(string optional)
    {
        switch (optional)
        {
            case null:
            case "Excel":
                return TestAssemblies;
            case "DomainExcel":
                return TestAssemblies.Concat(new[] { "YLFramework.Domain.Excel.Tests" }).ToArray();
            case "DOTween":
                return TestAssemblies.Concat(new[]
                {
                    "YLFramework.Actions.DOTween.Tests.EditMode", "YLFramework.Actions.DOTween.Tests.PlayMode"
                }).ToArray();
            case "HybridCLR":
                return TestAssemblies.Concat(new[] { "YLFramework.HybridCLR.Tests.EditMode", "YLFramework.HybridCLR.Tests.PlayMode" }).ToArray();
            default:
                throw new InvalidOperationException("Unknown explicit optional test scope.");
        }
    }

    private static IEnumerable<JObject> Leaves(ITestAdaptor node, string assembly, string[] expectedAssemblies)
    {
        if (node.IsTestAssembly)
        {
            assembly = Path.GetFileName(node.Name);
            if (assembly.EndsWith(".dll", StringComparison.Ordinal))
            {
                assembly = assembly.Substring(0, assembly.Length - 4);
            }
        }

        if (!node.IsSuite && expectedAssemblies.Contains(assembly))
        {
            var name = node.FullName;
            var scope = assembly == "YLFramework.Actions.DOTween.Tests.EditMode" ||
                assembly == "YLFramework.Actions.DOTween.Tests.PlayMode" ? "optional-dotween" :
                assembly == "YLFramework.HybridCLR.Tests.EditMode" || assembly == "YLFramework.HybridCLR.Tests.PlayMode" ? "optional-hybridclr" :
                assembly == "YLFramework.Domain.Excel.Tests" ? "optional-domainexcel" :
                Matches(name, Historical) ? "historical-bound" : Matches(name, HostOnly) ? "source-host" :
                Matches(name, PlatformBound) ? "platform-not-applicable" : Matches(name, Excel) ? "optional-excel" : "receiver";
            yield return new JObject
            {
                ["Name"] = name,
                ["Assembly"] = assembly,
                ["Scope"] = scope,
                ["RunState"] = node.RunState.ToString(),
                ["SkipReason"] = node.SkipReason
            };
        }

        foreach (var child in node.Children)
        {
            foreach (var row in Leaves(child, assembly, expectedAssemblies))
            {
                yield return row;
            }
        }
    }

    private static bool Matches(string name, string[] prefixes) => prefixes.Any(p => name == p ||
        name.StartsWith(p + ".", StringComparison.Ordinal) || name.StartsWith(p + "(", StringComparison.Ordinal));

    private static void ConfigureInput(int mode, bool disableDomainReload = false)
    {
        VerifyReceiver();
        var profilePath = Path.Combine((string)Owner["Delivery"], Path.GetFileName(Project) + "-tests-profile-" + Run + ".json");
        if (File.Exists(profilePath))
        {
            throw new IOException("Never overwrite a recorded test profile.");
        }

        for (var i = 0; i < EditorSceneManager.sceneCount; i++)
        {
            if (EditorSceneManager.GetSceneAt(i).isDirty)
            {
                throw new InvalidOperationException("Never change input while an unowned scene is dirty.");
            }
        }

        var settings = new SerializedObject(Resources.FindObjectsOfTypeAll<PlayerSettings>().Single());
        var property = settings.FindProperty("activeInputHandler");
        if (property == null)
        {
            throw new InvalidOperationException("This Editor exposes no known input-handler setting.");
        }

        property.intValue = mode;
        settings.ApplyModifiedPropertiesWithoutUndo();
        PlayerSettings.companyName = Path.GetFileName((string)Owner["Delivery"]);
        PlayerSettings.productName = Path.GetFileName(Project) + "-Tests";
        EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
        EditorSettings.enterPlayModeOptionsEnabled = disableDomainReload;
        EditorSettings.enterPlayModeOptions = disableDomainReload ? EnterPlayModeOptions.DisableDomainReload : EnterPlayModeOptions.None;
        if (SortingLayer.layers.All(layer => layer.id == 0))
        {
            using var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").Single());
            var layers = tags.FindProperty("m_SortingLayers");
            if (layers.arraySize != 1 || layers.GetArrayElementAtIndex(0).FindPropertyRelative("uniqueID").intValue != 0)
            {
                throw new InvalidOperationException("Only seed the known default sorting-layer layout in an owned receiver.");
            }

            layers.InsertArrayElementAtIndex(1);
            var layer = layers.GetArrayElementAtIndex(1);
            layer.FindPropertyRelative("name").stringValue = "Map";
            layer.FindPropertyRelative("uniqueID").intValue = 271027;
            layer.FindPropertyRelative("locked").boolValue = false;
            tags.ApplyModifiedPropertiesWithoutUndo();
        }
        if (!AssetDatabase.IsValidFolder("Assets/StreamingAssets"))
        {
            AssetDatabase.CreateFolder("Assets", "StreamingAssets");
        }

        // 新工程首次 NewScene 会生成 SceneTemplateSettings；在保护快照之前完成此初始化。
        var scenePath = "Assets/Stage9TestsBootstrap-" + Run + ".unity";
        if (File.Exists(scenePath))
        {
            throw new IOException("Never overwrite a receiving test bootstrap scene.");
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!EditorSceneManager.SaveScene(scene, scenePath))
        {
            throw new IOException("Could not save the new owned empty test bootstrap.");
        }

        File.WriteAllText(profilePath, new JObject
        {
            ["Schema"] = "Stage9TestProfile-v2",
            ["OwnerSha256"] = FileHash(Path.Combine(Project, "stage9-owner.json")),
            ["PackageSha256"] = Owner["PackageSha256"],
            ["ClassifierSha256"] = ClassifierHash(),
            ["Project"] = Project,
            ["Unity"] = Application.unityVersion,
            ["InputMode"] = mode,
            ["Company"] = PlayerSettings.companyName,
            ["Product"] = PlayerSettings.productName,
            ["SpritePackerMode"] = EditorSettings.spritePackerMode.ToString(),
            ["EnterPlayModeOptionsEnabled"] = EditorSettings.enterPlayModeOptionsEnabled,
            ["EnterPlayModeOptions"] = EditorSettings.enterPlayModeOptions.ToString(),
            ["SortingLayers"] = JArray.FromObject(SortingLayer.layers.Select(layer => new { layer.name, layer.id })),
            ["BootstrapScene"] = scenePath,
            ["BootstrapSha256"] = FileHash(scenePath),
            ["HostOnly"] = JArray.FromObject(HostOnly),
            ["Historical"] = JArray.FromObject(Historical),
            ["PlatformBound"] = JArray.FromObject(PlatformBound),
            ["Excel"] = JArray.FromObject(Excel)
        }.ToString(Formatting.Indented));
        Debug.Log("STAGE9_TEST_INPUT_CONFIGURED " + mode + "; restart before discovering/running tests.");
    }

    private static string FileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static string TextHash(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    private static string ClassifierHash() => TextHash(new JObject
    {
        ["HostOnly"] = JArray.FromObject(HostOnly),
        ["Historical"] = JArray.FromObject(Historical),
        ["PlatformBound"] = JArray.FromObject(PlatformBound),
        ["Excel"] = JArray.FromObject(Excel)
    }.ToString(Formatting.None));

    private static JObject CaptureTestBinding(UnityEditor.Compilation.Assembly[] graph)
    {
        VerifyReceiver();
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(Path.Combine(Project, "Assets"));
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Linked test input rejected: " + entry);
                }
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }
                if (!new[] { ".cs", ".asmdef", ".asmref", ".rsp" }.Contains(Path.GetExtension(entry)))
                {
                    continue;
                }
                paths.Add(entry.Substring(Project.Length + 1).Replace('\\', '/'));
                if (File.Exists(entry + ".meta"))
                {
                    paths.Add((entry + ".meta").Substring(Project.Length + 1).Replace('\\', '/'));
                }
            }
        }
        foreach (var relative in new[] { "stage9-owner.json", "Packages/manifest.json", "Packages/packages-lock.json", "ProjectSettings/ProjectSettings.asset", "ProjectSettings/EditorSettings.asset" })
        {
            paths.Add(relative);
        }
        var files = new JArray(paths.Select(relative => new JObject { ["Path"] = relative, ["Sha256"] = FileHash(Path.Combine(Project, relative)) }));
        var assemblies = new JArray(graph.OrderBy(assembly => assembly.name, StringComparer.Ordinal).Select(assembly => new JObject
        {
            ["Name"] = assembly.name,
            ["Defines"] = JArray.FromObject(assembly.defines.OrderBy(value => value, StringComparer.Ordinal)),
            ["Sources"] = JArray.FromObject(assembly.sourceFiles.Select(value => value.Replace('\\', '/')).OrderBy(value => value, StringComparer.Ordinal)),
            ["References"] = JArray.FromObject(assembly.assemblyReferences.Select(value => value.name).OrderBy(value => value, StringComparer.Ordinal)),
            ["OutputSha256"] = FileHash(assembly.outputPath)
        }));
        var defines = graph.SelectMany(assembly => assembly.defines).Distinct().ToArray();
        var legacy = defines.Contains("ENABLE_LEGACY_INPUT_MANAGER");
        var modern = defines.Contains("ENABLE_INPUT_SYSTEM");
        return new JObject
        {
            ["OwnerSha256"] = FileHash(Path.Combine(Project, "stage9-owner.json")),
            ["PackageSha256"] = Owner["PackageSha256"],
            ["InputFiles"] = files,
            ["CompilationSha256"] = TextHash(assemblies.ToString(Formatting.None)),
            ["ClassifierSha256"] = ClassifierHash(),
            ["InputMode"] = legacy ? (modern ? 2 : 0) : (modern ? 1 : -1),
            ["EnterPlayModeOptionsEnabled"] = EditorSettings.enterPlayModeOptionsEnabled,
            ["EnterPlayModeOptions"] = EditorSettings.enterPlayModeOptions.ToString()
        };
    }

    private static void VerifyReceiver()
    {
        var owner = Owner;
        var root = (string)owner["Delivery"];
        var expected = (string)owner["UnityVersion"];
        if ((string)owner["Purpose"] != "Stage9-independent-receiver" || (string)owner["Project"] != Project ||
            !Project.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            (expected != "2022.3.62f3c1" && expected != "6000.3.27f1") || Application.unityVersion != expected)
        {
            throw new InvalidOperationException("Only an owned receiving Editor may configure test input.");
        }
    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
