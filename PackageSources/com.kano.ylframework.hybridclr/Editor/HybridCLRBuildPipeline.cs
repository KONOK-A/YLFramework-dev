using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using dnlib.DotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using Settings = HybridCLR.Settings.HybridCLRSettings;
using SettingsUtil = HybridCLR.SettingsUtil;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>一个实际执行阶段的结果，保留原始异常与 Unity 错误日志。</summary>
    public sealed class HybridCLRBuildStageResult
    {
        /// <summary>阶段的稳定标识，不是本地化显示名称。</summary>
        public string Name
        {
            get; internal set;
        }
        /// <summary>阶段失败的原始异常，成功时为空。</summary>
        public Exception Error
        {
            get; internal set;
        }
        /// <summary>该阶段是否没有失败异常，不代表后续阶段已执行。</summary>
        public bool HasSucceeded => Error == null;
        /// <summary>该阶段捕获的 Error、Exception 与 Assert 日志。</summary>
        public IReadOnlyList<string> Diagnostics { get; internal set; } = Array.Empty<string>();
    }

    /// <summary>显式构建或发布的阶段报告，未出现的阶段不得视为已完成。</summary>
    public sealed class HybridCLRBuildReport
    {
        private readonly List<HybridCLRBuildStageResult> _stages = new List<HybridCLRBuildStageResult>();
        /// <summary>按执行顺序记录的阶段结果。</summary>
        public IReadOnlyList<HybridCLRBuildStageResult> Stages => _stages.AsReadOnly();
        /// <summary>是否至少执行了一个阶段且所有已记录阶段均成功。</summary>
        public bool HasSucceeded => _stages.Count != 0 && _stages.TrueForAll(s => s.HasSucceeded);
        /// <summary>本项目拟交付的代码清单，生成后仍可能在后续阶段失败。</summary>
        public HybridCLRManifest Manifest
        {
            get; internal set;
        }
        /// <summary>资源流程返回的发布版本，须结合阶段错误判断是否完成。</summary>
        public ResourceRelease Release
        {
            get; internal set;
        }
        /// <summary>正式 Resource 构建及发布的原始报告。</summary>
        public ResourceEditorBuildReport ResourceBuild
        {
            get; internal set;
        }
        /// <summary>显式内置部署的结果，未执行部署时为空。</summary>
        public ResourceBuiltInDeployment Deployment
        {
            get; internal set;
        }
        /// <summary>真实 Player 构建的 Unity 报告；单独成功不等于最终基线已冻结。</summary>
        public BuildReport PlayerBuild
        {
            get; internal set;
        }
        /// <summary>已读取验证或新冻结的基线根目录，不单独证明整个流程成功。</summary>
        public string BaselineDirectory
        {
            get; internal set;
        }
        /// <summary>包含已执行阶段、原始错误及现有制品身份的文本摘要。</summary>
        public string Summary => string.Join("\n", _stages.Select(s => s.Name + ": " + (s.HasSucceeded ? "OK" : s.Error.ToString()))) +
            (Manifest == null ? "" : "\nCodePackageId=" + Manifest.CodePackageId) +
            (Release == null ? "" : "\nCatalogDigest=" + Release.Catalog.CatalogDigest) +
            (BaselineDirectory == null ? "" : "\nBaseline=" + BaselineDirectory);
        // 追加真实返回的阶段结果，不替尚未执行的阶段预填成功。
        internal void Add(string name, Exception error = null, IReadOnlyList<string> diagnostics = null) =>
            _stages.Add(new HybridCLRBuildStageResult { Name = name, Error = error, Diagnostics = diagnostics ?? Array.Empty<string>() });
    }

    /// <summary>在 Editor 主线程编排新基座或冻结基座上的补丁发布，不自动安装或切换官方设置。</summary>
    [InitializeOnLoad]
    public static class HybridCLRBuildPipeline
    {
        private const string OwnerFile = "hybridclr-owner.json";
        private static readonly int _mainThread;
        private static bool _isBusy;

        // 由 Editor 初始化记录允许执行构建编排的主线程。
        static HybridCLRBuildPipeline() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        /// <summary>严格检查保存状态、输入归属及当前原生构建环境，不生成制品。</summary>
        /// <param name="profile">已显式保存的构建配置及其 Resource 配方、项目。</param>
        /// <returns>仅包含 Preflight 阶段的报告；通过不等于 Player 或补丁可用。</returns>
        public static HybridCLRBuildReport Validate(HybridCLRBuildProfile profile)
        {
            var report = new HybridCLRBuildReport();
            Run(report, "Preflight", () => Preflight(profile));
            return report;
        }

        /// <summary>生成全局原生能力、发布项目资源并构建真实 Player，最后冻结最终 AOT 基线。</summary>
        /// <param name="profile">直接拥有新基座且未绑定 BaseProfile 的已保存配置。</param>
        /// <param name="options">与配置及 enabled scenes 精确匹配、输出目录全新或为空的 Player 选项。</param>
        /// <param name="cancellation">在阶段开始前检查并传入资源流程的取消令牌。</param>
        /// <param name="provider">应用注入的可选资源内容密钥提供者，不写入 Profile。</param>
        /// <returns>截至失败或最终基线冻结的实际阶段报告。</returns>
        /// <remarks>取消不强制中断同步原生步骤，也不回滚此前已完成的生成、发布或部署。</remarks>
        public static HybridCLRBuildReport BuildBasePlayer(HybridCLRBuildProfile profile, BuildPlayerOptions options,
            CancellationToken cancellation = default, IResourceContentKeyProvider provider = null)
        {
            var report = Validate(profile);
            if (!report.HasSucceeded)
            {
                return report;
            }

            _isBusy = true;
            try
            {
                HybridCLRBuildInputs inputs = null;
                if (!Run(report, "PlayerPreflight", () =>
                {
                    if (profile.baseProfile)
                    {
                        throw new InvalidDataException("Build the native Player through its base owner; a bound project may publish patches only.");
                    }

                    ValidatePlayerOptions(profile, options);
                    inputs = HybridCLRBuildInputs.Capture(profile);
                }, cancellation))
                {
                    return report;
                }

                var workspace = Path.GetFullPath("Temp/YLHybridCLR/" + Guid.NewGuid().ToString("N"));
                HybridCLRAssemblySet compiled = null;
                HybridCLRAssemblySet prepared = null;
                HybridCLRManifest registration = null;
                if (!Run(report, "Compile", () => compiled = Compile(profile, workspace), cancellation))
                {
                    return report;
                }

                if (!Run(report, "Il2CppDef", global::HybridCLR.Commands.Il2CppDefGeneratorCommand.GenerateIl2CppDef, cancellation))
                {
                    return report;
                }

                if (!Run(report, "LinkXml", () => GenerateLink(compiled), cancellation))
                {
                    return report;
                }

                if (!Run(report, "EmptyMethodBridge", () =>
                {
                    profile = inputs.Reload();
                    global::HybridCLR.Commands.MethodBridgeGeneratorCommand.GenerateEmptyMethodBridgeAndReversePInvokeWrapper(profile.recipe.target);
                }, cancellation))
                {
                    return report;
                }

                using (HybridCLRPlayerBuildGuard.Enter(profile, true))
                {
                    if (!Run(report, "StripAot", () => global::HybridCLR.Commands.StripAOTDllCommand.GenerateStripedAOTDlls(profile.recipe.target), cancellation))
                    {
                        return report;
                    }
                }

                if (!Run(report, "FullMethodBridge", () =>
                {
                    profile = inputs.Reload();
                    prepared = AnalyzeStripped(profile, compiled.HotUpdate);
                    GenerateBridge(prepared, profile.development, SettingsUtil.GeneratedCppDir + "/MethodBridge.inc");
                }, cancellation))
                {
                    return report;
                }

                if (!Run(report, "Manifest", () =>
                {
                    // 基座冻结全局登记，资源交付只投影当前项目，不裁掉其他项目的原生能力证据。
                    registration = CreateManifestForSet(profile, prepared);
                    report.Manifest = CreateManifest(profile, prepared);
                }, cancellation))
                {
                    return report;
                }

                if (!Run(report, "PayloadCommit", () => CommitPayloads(profile, report.Manifest, prepared), cancellation))
                {
                    return report;
                }

                if (!Run(report, "ResourcePublish", () =>
                {
                    profile = inputs.Reload();
                    PublishResources(report, profile, cancellation, provider);
                    profile = inputs.Reload();
                }, cancellation))
                {
                    return report;
                }

                if (!Run(report, "Deploy", () =>
                {
                    report.Deployment = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(report.Release), cancellation);
                    profile = inputs.Reload();
                }, cancellation))
                {
                    return report;
                }

                using (HybridCLRPlayerBuildGuard.Enter(profile, false))
                {
                    if (!Run(report, "Player", () =>
                    {
                        report.PlayerBuild = BuildPipeline.BuildPlayer(options);
                        if (report.PlayerBuild == null || report.PlayerBuild.summary.result != BuildResult.Succeeded)
                        {
                            throw new InvalidOperationException("The explicit base Player build did not succeed.");
                        }

                        ValidatePlayerOutputs(options.locationPathName, report.PlayerBuild.summary.outputPath);
                        profile = inputs.Reload();
                    }, cancellation))
                    {
                        return report;
                    }
                }

                Run(report, "Baseline", () =>
                {
                    profile = inputs.Reload();
                    var final = AnalyzeStripped(profile, compiled.HotUpdate);
                    VerifyFinalAot(prepared, final, registration);
                    var baseline = new HybridCLRBuildBaseline
                    {
                        ProfileGuid = ProfileGuid(profile),
                        SettingsFingerprint = SettingsFingerprint(profile),
                        PlayerBuildHash = PlayerFingerprint(options.locationPathName),
                        NativeRequirements = NativeRequirements(final),
                        InitialManifest = registration
                    };
                    var sources = final.Aot.Select(a => new KeyValuePair<string, string>("Aot/" + a.Name + ".dll", a.Path))
                        .Concat(final.HotUpdate.Select(a => new KeyValuePair<string, string>("HotUpdate/" + a.Name + ".dll", a.Path)))
                        .Concat(HybridCLRBuildBaseline.GeneratedNames.Select(n => new KeyValuePair<string, string>("Generated/" + n,
                            n == "link.xml" ? LinkPath() : SettingsUtil.GeneratedCppDir + "/" + n)));
                    baseline.Freeze(profile.baselineDirectory, sources);
                    report.BaselineDirectory = baseline.Root;
                }, cancellation);
                return report;
            }
            finally
            {
                _isBusy = false;
            }
        }

        /// <summary>按持久冻结基线检查项目热更闭包并发布补丁，不裁剪 AOT、重建原生桥或构建 Player。</summary>
        /// <param name="profile">拥有或显式一层绑定完整基线的已保存项目配置。</param>
        /// <param name="cancellation">阶段前及资源发布流程使用的取消令牌。</param>
        /// <param name="provider">应用注入的可选资源内容密钥提供者。</param>
        /// <returns>基线读取、编译、兼容检查及已执行发布阶段的报告。</returns>
        /// <remarks>超出冻结登记的 metadata 或原生需求须建立新基座；本入口不部署也不修改冻结基线。</remarks>
        public static HybridCLRBuildReport PublishPatch(HybridCLRBuildProfile profile, CancellationToken cancellation = default,
            IResourceContentKeyProvider provider = null)
        {
            var report = Validate(profile);
            if (!report.HasSucceeded)
            {
                return report;
            }

            _isBusy = true;
            try
            {
                HybridCLRBuildBaseline baseline = null;
                HybridCLRAssemblySet compiled = null;
                HybridCLRAssemblySet patch = null;
                if (!Run(report, "ReadBaseline", () =>
                {
                    baseline = HybridCLRBuildBaseline.Read(profile.baselineDirectory);
                    ValidateBaselineBinding(profile, baseline, SettingsFingerprint(profile));
                    report.BaselineDirectory = baseline.Root;
                }, cancellation))
                {
                    return report;
                }

                if (!Run(report, "Compile", () => compiled = Compile(profile, Path.GetFullPath("Temp/YLHybridCLR/" + Guid.NewGuid().ToString("N"))), cancellation))
                {
                    return report;
                }

                if (!Run(report, "PatchCompatibility", () =>
                {
                    var selected = SelectAssemblies(profile, compiled);
                    patch = HybridCLRAssemblyAnalysis.Analyze(selected.HotUpdate.Select(a => a.Path),
                        baseline.Files.Where(f => f.Path.StartsWith("Aot/", StringComparison.Ordinal)).Select(f => HybridCLRBuildBaseline.Child(baseline.Root, f.Path)), UnityAotAliases());
                    ValidatePatchSet(baseline, patch);
                    var checker = new global::HybridCLR.HotUpdate.MissingMetadataChecker(baseline.Root + "/Aot", patch.HotUpdate.Select(a => a.Name));
                    foreach (var assembly in patch.HotUpdate)
                    {
                        if (!checker.Check(assembly.Path))
                        {
                            throw new InvalidDataException("Patch references missing frozen AOT metadata: " + assembly.Name);
                        }
                    }

                    report.Manifest = CreateManifest(profile, patch);
                }, cancellation))
                {
                    return report;
                }

                if (!Run(report, "PayloadCommit", () => CommitPayloads(profile, report.Manifest, patch), cancellation))
                {
                    return report;
                }

                Run(report, "ResourcePublish", () => PublishResources(report, profile, cancellation, provider), cancellation);
                return report;
            }
            finally
            {
                _isBusy = false;
            }
        }

        // 在阶段边界响应取消，捕获同步异常及 Unity 错误日志，并始终解绑日志观察。
        private static bool Run(HybridCLRBuildReport report, string name, Action action, CancellationToken cancellation = default)
        {
            var messages = new List<string>();
            // 仅收集会使当前阶段失败的日志，保留堆栈供报告定位。
            void Capture(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    messages.Add(message + "\n" + stack);
                }
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                Application.logMessageReceived += Capture;
                action();
                if (messages.Count != 0)
                {
                    throw new InvalidOperationException("Unity reported errors during " + name + ":\n" + string.Join("\n", messages));
                }

                report.Add(name, diagnostics: messages.AsReadOnly());
                return true;
            }
            catch (Exception exception)
            {
                report.Add(name, exception, messages.AsReadOnly());
                return false;
            }
            finally
            {
                Application.logMessageReceived -= Capture;
            }
        }

        // 拒绝繁忙或未保存的 Editor 输入，再检查目录归属、基座绑定与官方环境。
        private static void Preflight(HybridCLRBuildProfile profile)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
            {
                throw new InvalidOperationException("HybridCLR build operations require the Unity Editor main thread.");
            }

            if (!profile)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (_isBusy || BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("A settled Editor without another active build is required.");
            }

            if (EditorUtility.scriptCompilationFailed)
            {
                throw new InvalidOperationException("Resolve existing Unity script compilation errors first.");
            }

            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(profile)) || EditorUtility.IsDirty(profile))
            {
                throw new InvalidOperationException("Save the selected HybridCLR profile first.");
            }

            var input = ResourceEditorOperations.ResolveRecipe(profile.recipe);
            if (EditorUtility.IsDirty(profile.recipe) || EditorUtility.IsDirty(profile.recipe.project))
            {
                throw new InvalidOperationException("Save the selected Resource recipe and project first.");
            }

            if (input.Action != ResourceBuildAction.BuildAndPublish || input.GenerateReferences)
            {
                throw new InvalidDataException("Use BuildAndPublish without generated C# address references.");
            }

            ValidateInputPaths(profile);
            var owner = ResolveBaseProfile(profile);
            if (EditorUtility.IsDirty(owner) || EditorUtility.IsDirty(owner.recipe) || EditorUtility.IsDirty(owner.recipe.project))
            {
                throw new InvalidOperationException("Save the explicitly bound native base configuration first.");
            }

            ValidateEnvironment(profile);
            ValidateRoots(profile.hotUpdateAssemblies, ConfiguredNames());
            LinkPath();
        }

        // 核对当前 Windows64、IL2CPP、Framework、精确包来源和已安装原生工具链，不代为配置。
        internal static void ValidateEnvironment(HybridCLRBuildProfile profile)
        {
            if (profile.recipe.target != BuildTarget.StandaloneWindows64 || EditorUserBuildSettings.activeBuildTarget != profile.recipe.target ||
                profile.development != EditorUserBuildSettings.development || Application.platform != RuntimePlatform.WindowsEditor)
            {
                throw new InvalidDataException("Only the already-active Windows64 target with the exact Development profile is supported.");
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64) ||
                !Directory.Exists(EditorApplication.applicationContentsPath + "/il2cpp"))
            {
                throw new InvalidOperationException("Install Windows IL2CPP build support first.");
            }

            if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) != ScriptingImplementation.IL2CPP ||
                PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Standalone) != ApiCompatibilityLevel.NET_4_6)
            {
                throw new InvalidOperationException("Explicitly configure the receiving project for IL2CPP and .NET Framework; this operation never switches PlayerSettings.");
            }

            var package = PackageInfo.GetAllRegisteredPackages().Single(p => p.name == "com.code-philosophy.hybridclr");
            var installer = new global::HybridCLR.Installer.InstallerController();
            ValidateSupportedVersions(package.version, installer.PackageVersion, installer.InstalledLibil2cppVersion);
            if (!installer.HasInstalledHybridCLR())
            {
                throw new InvalidOperationException("Explicitly install the local official HybridCLR runtime first.");
            }

            var declared = JObject.Parse(File.ReadAllText("Packages/manifest.json"))["dependencies"]?["com.code-philosophy.hybridclr"]?.Value<string>();
            var locked = JObject.Parse(File.ReadAllText("Packages/packages-lock.json"))["dependencies"]?["com.code-philosophy.hybridclr"];
            ValidateGitProvenance(package.version, package.source.ToString(), declared, locked?.Value<string>("source"),
                locked?.Value<string>("version"), locked?.Value<string>("hash"), package.git?.revision, package.git?.hash);
            if (!Settings.instance.enable || Settings.instance.useGlobalIl2cpp)
            {
                throw new InvalidOperationException("Enable the officially installed local HybridCLR runtime explicitly.");
            }

            using var kits = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            ValidateNativeToolchain(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio/Installer/vswhere.exe"), kits?.GetValue("KitsRoot10") as string);
        }

        // 要求解析包、Installer 包版本与本地 libil2cpp 版本均精确为 9.0.3。
        internal static void ValidateSupportedVersions(string resolvedVersion, string declaredVersion, string installedVersion)
        {
            if (resolvedVersion != "9.0.3" || declaredVersion != "9.0.3" || installedVersion != "9.0.3")
            {
                throw new InvalidOperationException("The official package and explicitly installed local runtime must match exactly 9.0.3.");
            }
        }

        // 交叉验证 manifest、UPM lock 与可用的 PackageInfo，拒绝同版本号但来源不同的包。
        internal static void ValidateGitProvenance(string version, string source, string declared, string lockSource,
            string lockVersion, string lockHash, string apiRevision, string apiHash)
        {
            const string Url = "https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3";
            const string Hash = "6cadd24d101f4cbd00cd80190106f6621132d24d";
            // Unity 启动时 PackageInfo.git 可以为空；实际 UPM lock 与 manifest 仍必须给出完整精确来源。
            if (version != "9.0.3" || source != "Git" || declared != Url || lockSource != "git" || lockVersion != Url || lockHash != Hash ||
                !string.IsNullOrEmpty(apiRevision) && apiRevision != "v9.0.3" || !string.IsNullOrEmpty(apiHash) && apiHash != Hash)
            {
                throw new InvalidOperationException("The resolved manifest/lock/package/API provenance is not the exact supported official Git tag.");
            }
        }

        // 只发现并检查既有 x64 C++ 工具与 SDK 库，不启动安装。
        internal static void ValidateNativeToolchain(string vswherePath, string sdkRoot)
        {
            if (!File.Exists(vswherePath) || string.IsNullOrEmpty(sdkRoot) || !Directory.Exists(Path.Combine(sdkRoot, "Lib")))
            {
                throw new InvalidOperationException("Windows IL2CPP requires Visual Studio C++ tools and a Windows 10/11 SDK; no automatic installation is attempted.");
            }

            var libraries = Path.Combine(sdkRoot, "Lib");
            if (!Directory.GetDirectories(libraries).Any(d => File.Exists(Path.Combine(d, "um/x64/kernel32.lib")) && File.Exists(Path.Combine(d, "ucrt/x64/ucrt.lib"))))
            {
                throw new InvalidOperationException("The Windows SDK lacks x64 UM/UCRT libraries.");
            }

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vswherePath,
                "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (process == null)
            {
                throw new InvalidOperationException("Visual Studio C++ discovery could not start.");
            }

            if (!process.WaitForExit(30000))
            {
                process.Kill();
                throw new InvalidOperationException("Visual Studio C++ discovery timed out.");
            }
            var installation = process.StandardOutput.ReadToEnd().Trim();
            if (process.ExitCode != 0 || string.IsNullOrEmpty(installation) || !Directory.Exists(Path.Combine(installation, "VC/Tools/MSVC")))
            {
                throw new InvalidOperationException("No supported x64 Visual Studio C++ toolchain was found.");
            }
        }

        // 从官方设置读取非保留热更名单，拒绝空项、大小写碰撞和隐式默认程序集。
        internal static string[] ConfiguredNames()
        {
            var names = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved.ToArray();
            if (names.Length == 0 || names.Length > HybridCLRManifest.MaximumAssemblyCount || names.Any(string.IsNullOrWhiteSpace) ||
                names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            {
                throw new InvalidDataException("Configure a nonempty, unique hot-update assembly set in official HybridCLRSettings.");
            }

            foreach (var name in names)
            {
                _ = new ResourceAddress(name, "assembly");
                if (name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass")
                {
                    throw new InvalidDataException("Use explicit asmdefs or external DLLs, not Assembly-CSharp.");
                }
            }
            return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        // 在全新工作区编译官方全局集合，复制显式外部 DLL，并拒绝 AOT 到热更的静态引用。
        private static HybridCLRAssemblySet Compile(HybridCLRBuildProfile profile, string workspace)
        {
            var output = workspace + "/Compiled";
            if (Directory.Exists(workspace) || File.Exists(workspace))
            {
                throw new IOException("Compilation staging must be new.");
            }

            Directory.CreateDirectory(output);
            var names = ConfiguredNames();
            var playerGraph = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            var sourceNames = names.Where(n => playerGraph.Any(a => a.name == n)).ToArray();
            var editorOnly = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Where(a => names.Contains(a.name) && !sourceNames.Contains(a.name)).ToArray();
            if (editorOnly.Length != 0)
            {
                throw new InvalidDataException("An Editor-only assembly cannot be hot-update input: " + editorOnly[0].name);
            }

            var settings = new ScriptCompilationSettings
            {
                group = BuildTargetGroup.Standalone,
                target = profile.recipe.target,
                options = profile.development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None
            };
            var result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
            if (result.assemblies == null || result.assemblies.Count == 0)
            {
                throw new InvalidDataException("CompilePlayerScripts did not report successful assembly outputs.");
            }

            var returned = result.assemblies.Select(p => Path.IsPathRooted(p) || File.Exists(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(output, p))).ToArray();
            if (returned.Any(p => !Within(p, output)))
            {
                throw new InvalidDataException("A compiler result escaped its fresh staging directory.");
            }

            var hot = VerifyCompiledOutputs(returned, sourceNames).ToList();
            var external = ExternalCandidates(names).ToArray();
            foreach (var assembly in external)
            {
                if (hot.Any(h => string.Equals(h.Name, assembly.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("An asmdef and an external DLL both claim the same assembly: " + assembly.Name);
                }

                var path = workspace + "/External/" + assembly.Name + ".dll";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(assembly.Path, path, false);
                var staged = HybridCLRAssemblyAnalysis.Read(path);
                if (staged.Sha256 != assembly.Sha256 || staged.Identity != assembly.Identity)
                {
                    throw new InvalidDataException("External DLL changed during staging.");
                }

                hot.Add(staged);
            }
            if (!names.SequenceEqual(hot.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("Configured hot-update input is missing or ambiguous.");
            }

            foreach (var path in returned)
            {
                var assembly = HybridCLRAssemblyAnalysis.Read(path);
                if (!names.Contains(assembly.Name) && assembly.References.Any(r => names.Contains(new AssemblyName(r).Name)))
                {
                    throw new InvalidDataException("AOT script assembly statically references hot-update code: " + assembly.Name);
                }
            }
            return new HybridCLRAssemblySet { HotUpdate = hot.AsReadOnly(), Aot = Array.Empty<HybridCLRAssemblyRecord>() };
        }

        // 仅接纳本轮编译器明确返回且名称唯一的输出，不能用遗留 DLL 补齐成功。
        internal static IReadOnlyList<HybridCLRAssemblyRecord> VerifyCompiledOutputs(IEnumerable<string> returnedAssemblies, IEnumerable<string> expectedNames)
        {
            if (returnedAssemblies == null || expectedNames == null)
            {
                throw new InvalidDataException("Missing compiler results.");
            }

            var expected = expectedNames.ToArray();
            if (expected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length)
            {
                throw new InvalidDataException("Duplicate expected compiler identities.");
            }

            var candidates = returnedAssemblies.Select(HybridCLRAssemblyAnalysis.Read).ToArray();
            var output = new List<HybridCLRAssemblyRecord>();
            foreach (var name in expected)
            {
                var matches = candidates.Where(r => r.Name == name).ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidDataException("Compiler did not report exactly one valid output for: " + name);
                }

                output.Add(matches[0]);
            }
            return output.AsReadOnly();
        }

        // 在官方显式外部目录及 Windows64 子目录查找唯一 DLL，不扩展到未知来源。
        internal static IEnumerable<HybridCLRAssemblyRecord> ExternalCandidates(string[] names)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declared in Settings.instance.externalHotUpdateAssembliyDirs ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(declared) || declared.StartsWith("\\\\", StringComparison.Ordinal) || declared.StartsWith("//", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("External hot-update directories must be explicit local paths.");
                }

                var root = Path.GetFullPath(declared);
                HybridCLRBuildBaseline.RejectLinks(root);
                if (!Directory.Exists(root))
                {
                    throw new DirectoryNotFoundException(root);
                }

                foreach (var directory in new[] { root + "/StandaloneWindows64", root })
                {
                    if (!Directory.Exists(directory) || !visited.Add(directory))
                    {
                        continue;
                    }

                    HybridCLRBuildBaseline.RejectLinks(directory);
                    foreach (var file in Directory.GetFiles(directory).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dll.bytes", StringComparison.OrdinalIgnoreCase)))
                    {
                        var record = HybridCLRAssemblyAnalysis.Read(file);
                        if (!names.Contains(record.Name))
                        {
                            continue;
                        }

                        if (!found.Add(record.Name))
                        {
                            throw new InvalidDataException("Duplicate external assembly identity: " + record.Name);
                        }

                        yield return record;
                    }
                }
            }
        }

        // 检查可执行 Player 模式、场景和互不重叠的新输出，保护已有 Player 与基线。
        internal static void ValidatePlayerOptions(HybridCLRBuildProfile profile, BuildPlayerOptions options)
        {
#if UNITY_EDITOR_WIN
            ValidateExecutableBuildMode(EditorUserBuildSettings.buildScriptsOnly, UnityEditor.WindowsStandalone.UserBuildSettings.createSolution);
#endif
            if (options.target != profile.recipe.target || options.targetGroup != BuildTargetGroup.Standalone ||
                ((options.options & BuildOptions.Development) != 0) != profile.development ||
                (options.options & (BuildOptions.BuildScriptsOnly | BuildOptions.IncludeTestAssemblies)) != 0 || options.subtarget != (int)StandaloneBuildSubtarget.Player)
            {
                throw new InvalidDataException("Explicit Player options must match the supported base profile, not a scripts-only/test/server build.");
            }

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (options.scenes == null || scenes.Length == 0 || !options.scenes.SequenceEqual(scenes) || scenes.Any(s => !File.Exists(s)))
            {
                throw new InvalidDataException("The explicit base scene list must equal the official Strip AOT enabled-scene list.");
            }

            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("Save your own scenes explicitly before building a base.");
                }
            }

            if (string.IsNullOrWhiteSpace(options.locationPathName) || !options.locationPathName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Use an explicit new Windows Player executable destination.");
            }

            var output = Path.GetDirectoryName(Path.GetFullPath(options.locationPathName));
            HybridCLRBuildBaseline.RejectLinks(output);
            if (Directory.Exists(output) && Directory.GetFileSystemEntries(output).Length != 0)
            {
                throw new IOException("Player output must be new or empty; existing Players are not overwritten.");
            }

            if (Within(output, profile.generatedAssetDirectory) || Within(output, profile.baselineDirectory) ||
                Within(profile.generatedAssetDirectory, output) || Within(profile.baselineDirectory, output))
            {
                throw new InvalidDataException("Player, payload and baseline outputs must be separate.");
            }

            if (Directory.Exists(profile.baselineDirectory) || File.Exists(profile.baselineDirectory))
            {
                throw new IOException("Build a new base into a new immutable baseline directory.");
            }
        }

        // 解析官方 Assets 相对 link.xml 输出，拒绝链接及逃出 Assets 的路径。
        private static string LinkPath()
        {
            var relative = Settings.instance.outputLinkFile;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p == "." || p == ".." || p.Length == 0))
            {
                throw new InvalidDataException("The official link.xml output must be an explicit Assets-relative path.");
            }

            var path = Path.GetFullPath(Path.Combine(Application.dataPath, relative));
            HybridCLRBuildBaseline.RejectLinks(path);
            if (!Within(path, Application.dataPath) || Path.GetFileName(path) != "link.xml")
            {
                throw new InvalidDataException("The official linker output must be an Assets link.xml file.");
            }

            return path;
        }

        // 依据全局热更输入生成官方裁剪保留文件，并同步导入。
        private static void GenerateLink(HybridCLRAssemblySet compiled)
        {
            var analyzer = new global::HybridCLR.CodeGen.Link.LinkAnalyzer(HybridCLRAssemblyAnalysis.CreateResolver(compiled));
            var types = analyzer.CollectRefs(compiled.HotUpdate.Select(a => a.Name).ToList(), Settings.instance.linkPreserveLevel);
            new global::HybridCLR.CodeGen.Link.LinkXmlWriter().Write(LinkPath(), types);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        // 用真实 Player 裁剪目录建立 AOT 集合，不以编译前引用程序集冒充最终输入。
        private static HybridCLRAssemblySet AnalyzeStripped(HybridCLRBuildProfile profile, IReadOnlyList<HybridCLRAssemblyRecord> hot)
        {
            var directory = SettingsUtil.GetAssembliesPostIl2CppStripDir(profile.recipe.target);
            if (!Directory.Exists(directory) || Directory.GetFiles(directory, "*.dll").Length == 0)
            {
                throw new InvalidDataException("The actual Player strip output is absent.");
            }

            return HybridCLRAssemblyAnalysis.Analyze(hot.Select(a => a.Path), Directory.GetFiles(directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal), UnityAotAliases());
        }

        // 结合裁剪 AOT、热更回调及 calli 需求生成完整原生桥接。
        private static void GenerateBridge(HybridCLRAssemblySet assemblies, bool development, string path)
        {
            var resolver = HybridCLRAssemblyAnalysis.CreateResolver(assemblies);
            var collector = new global::HybridCLR.Meta.AssemblyReferenceDeepCollector(resolver, assemblies.Aot.Select(a => a.Name).ToList());
            var analyzer = new global::HybridCLR.CodeGen.MethodBridge.MethodBridgeAnalyzer(new global::HybridCLR.CodeGen.MethodBridge.MethodBridgeAnalyzer.Options
            {
                Collector = collector,
                MaxIterationCount = Math.Min(20, Settings.instance.maxMethodBridgeGenericIteration)
            });
            analyzer.Run();
            var cache = new global::HybridCLR.Mod.AssemblyCache(resolver);
            var hot = assemblies.HotUpdate.Select(a => a.Name).ToList();
            var callbacks = new global::HybridCLR.CodeGen.MethodBridge.MonoPInvokeCallbackAnalyzer(cache, hot);
            callbacks.Run();
            var calli = new global::HybridCLR.CodeGen.MethodBridge.CalliAnalyzer(cache, hot);
            calli.Run();
            new global::HybridCLR.CodeGen.MethodBridge.Generator(new global::HybridCLR.CodeGen.MethodBridge.Generator.Options
            {
                Development = development,
                OutputFile = path,
                GenericMethods = analyzer.GenericMethods,
                ReversePInvokeMethods = callbacks.ReversePInvokeMethods,
                CalliMethodSignatures = calli.CalliMethodSignatures.ToArray()
            }).Generate();
        }

        // 将项目入口投影为热更依赖闭包后生成交付清单。
        private static HybridCLRManifest CreateManifest(HybridCLRBuildProfile profile, HybridCLRAssemblySet assemblies)
            => CreateManifestForSet(profile, SelectAssemblies(profile, assemblies));

        // 为明确程序集集合生成代码及所需 metadata 描述，并封口为规范清单。
        private static HybridCLRManifest CreateManifestForSet(HybridCLRBuildProfile profile, HybridCLRAssemblySet assemblies)
        {
            var hotNames = new HashSet<string>(assemblies.HotUpdate.Select(a => a.Name), StringComparer.Ordinal);
            // 保留完整 DLL 指纹，仅热更描述携带集合内直接依赖。
            HybridCLRPayload Payload(HybridCLRAssemblyRecord record, bool aot) => new HybridCLRPayload
            {
                Name = record.Name,
                Identity = record.Identity,
                Mvid = record.Mvid,
                Length = record.Length,
                Sha256 = record.Sha256,
                Address = HybridCLRManifest.PayloadAddress(record.Name, aot),
                References = record.References,
                Dependencies = aot ? Array.Empty<string>() : record.References.Select(r => new AssemblyName(r).Name).Where(hotNames.Contains).ToArray()
            };
            var manifest = new HybridCLRManifest
            {
                ProjectId = profile.recipe.project.projectId,
                AppContentCompatibilityId = profile.recipe.appContentCompatibilityId,
                UnityVersion = Application.unityVersion,
                Development = profile.development,
                HotUpdateAssemblies = assemblies.HotUpdate.Select(a => Payload(a, false)).ToArray(),
                AotMetadata = HybridCLRAssemblyAnalysis.CollectRequiredAotMetadata(assemblies, Math.Min(20, Settings.instance.maxGenericReferenceIteration)).Select(a => Payload(a, true)).ToArray()
            };
            manifest.Seal();
            return manifest;
        }

        // 确认最终 Player 的 AOT 身份与字节未偏离已准备并发布的 SuperSet 输入。
        private static void VerifyFinalAot(HybridCLRAssemblySet prepared, HybridCLRAssemblySet final, HybridCLRManifest manifest)
        {
            if (!prepared.Aot.Select(a => a.Identity).SequenceEqual(final.Aot.Select(a => a.Identity)))
            {
                throw new InvalidDataException("The final Player AOT set differs from the prepared base.");
            }

            foreach (var original in prepared.Aot)
            {
                var actual = final.Aot.Single(a => a.Name == original.Name);
                if (actual.Sha256 != original.Sha256 || actual.Mvid != original.Mvid)
                {
                    throw new InvalidDataException("Final AOT bytes differ from the prepared SuperSet inputs; compatibility was not established: " + original.Name);
                }
            }
            foreach (var payload in manifest.AotMetadata)
            {
                var actual = final.Aot.Single(a => a.Name == payload.Name);
                if (actual.Identity != payload.Identity || actual.Sha256 != payload.Sha256 || actual.Mvid != payload.Mvid)
                {
                    throw new InvalidDataException("Published metadata does not match the final Player: " + payload.Name);
                }
            }
        }

        // 补丁必须是全局登记、已登记 metadata 和冻结原生需求的子集，而非仅在目录中存在同名文件。
        internal static void ValidatePatchSet(HybridCLRBuildBaseline baseline, HybridCLRAssemblySet patch)
        {
            var registered = new HashSet<string>(baseline.InitialManifest.HotUpdateAssemblies.Select(a => a.Name), StringComparer.Ordinal);
            if (patch.HotUpdate.Any(a => !registered.Contains(a.Name)))
            {
                throw new InvalidDataException("An assembly outside the frozen global registration requires a new base.");
            }

            var required = HybridCLRAssemblyAnalysis.CollectRequiredAotMetadata(patch, Math.Min(20, Settings.instance.maxGenericReferenceIteration));
            if (required.Any(a => !baseline.InitialManifest.AotMetadata.Any(m => m.Name == a.Name)))
            {
                throw new InvalidDataException("New AOT metadata dependencies require a new base.");
            }

            var allowed = new HashSet<string>(baseline.NativeRequirements, StringComparer.Ordinal);
            if (NativeRequirements(patch).Any(r => !allowed.Contains(r)))
            {
                throw new InvalidDataException("New AOT generic/native bridge requirements need a new base; this static check does not certify arbitrary dynamic generic use.");
            }
        }

        // 解析精确项目入口；空集合保留官方全量语义，非空非法项不得静默回退。
        internal static string[] ValidateRoots(string[] roots, IEnumerable<string> configured)
        {
            var all = new HashSet<string>(configured, StringComparer.Ordinal);
            if (roots == null || roots.Length == 0)
            {
                return all.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in roots)
            {
                if (string.IsNullOrEmpty(name) || !all.Contains(name) || !unique.Add(name))
                {
                    throw new InvalidDataException("Project assembly roots must be unique exact names from the official registration: " + name);
                }
            }

            return roots.ToArray();
        }

        // 沿实际引用展开项目热更闭包，保留原有顺序和可用 AOT 集合。
        internal static HybridCLRAssemblySet SelectAssemblies(HybridCLRBuildProfile profile, HybridCLRAssemblySet assemblies)
        {
            if (!profile)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (assemblies == null)
            {
                throw new ArgumentNullException(nameof(assemblies));
            }

            var all = assemblies.HotUpdate.ToDictionary(a => a.Name, StringComparer.Ordinal);
            var pending = new Stack<string>(ValidateRoots(profile.hotUpdateAssemblies, all.Keys));
            var selected = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                var name = pending.Pop();
                if (!selected.Add(name))
                {
                    continue;
                }

                foreach (var reference in all[name].References)
                {
                    var dependency = new AssemblyName(reference).Name;
                    if (all.ContainsKey(dependency))
                    {
                        pending.Push(dependency);
                    }
                }
            }
            return new HybridCLRAssemblySet { HotUpdate = Array.AsReadOnly(assemblies.HotUpdate.Where(a => selected.Contains(a.Name)).ToArray()), Aot = assemblies.Aot };
        }

        // 解析唯一直接基座拥有者，拒绝自引用、链式绑定及目录或构建身份不匹配。
        internal static HybridCLRBuildProfile ResolveBaseProfile(HybridCLRBuildProfile profile)
        {
            if (!profile || !profile.recipe || !profile.recipe.project)
            {
                throw new InvalidDataException("Select a profile with a Resource recipe and project.");
            }

            if (!profile.baseProfile)
            {
                return profile;
            }

            var owner = profile.baseProfile;
            if (owner == profile || owner.baseProfile || !owner.recipe || !owner.recipe.project)
            {
                throw new InvalidDataException("A native base binding must point directly to a different, unbound owner profile.");
            }

            if (owner.development != profile.development || owner.recipe.target != profile.recipe.target ||
                owner.recipe.appContentCompatibilityId != profile.recipe.appContentCompatibilityId ||
                string.IsNullOrWhiteSpace(owner.baselineDirectory) || string.IsNullOrWhiteSpace(profile.baselineDirectory) ||
                !Path.GetFullPath(owner.baselineDirectory).TrimEnd('\\', '/').Equals(Path.GetFullPath(profile.baselineDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Bound projects must use the owner's exact target, Development, compatibility id and native baseline directory.");
            }

            ProfileGuid(owner);
            return owner;
        }

        // 按直接拥有者验证冻结基线，同时保留当前项目自己的发布身份。
        internal static void ValidateBaselineBinding(HybridCLRBuildProfile profile, HybridCLRBuildBaseline baseline, string fingerprint)
        {
            var owner = ResolveBaseProfile(profile);
            baseline.InitialManifest.Validate(owner.recipe.project.projectId, profile.recipe.appContentCompatibilityId, Application.unityVersion, profile.development);
            if (baseline.ProfileGuid != ProfileGuid(owner) || baseline.SettingsFingerprint != fingerprint)
            {
                throw new InvalidDataException("The explicit base owner/tool/settings identity changed; build a new base with a new compatibility id.");
            }
        }

        // 收集静态可见的 AOT 泛型、反向回调、calli 与 P/Invoke 需求，生成稳定比较集合。
        private static string[] NativeRequirements(HybridCLRAssemblySet assemblies)
        {
            var hot = assemblies.HotUpdate.Select(a => a.Name).ToList();
            var resolver = HybridCLRAssemblyAnalysis.CreateResolver(assemblies);
            var collector = new global::HybridCLR.Meta.AssemblyReferenceDeepCollector(resolver, hot);
            var analyzer = new global::HybridCLR.CodeGen.AOT.AOTReferenceAnalyzer(new global::HybridCLR.CodeGen.AOT.AOTReferenceAnalyzer.Options
            {
                Collector = collector,
                MaxIterationCount = Math.Min(20, Settings.instance.maxGenericReferenceIteration),
                ComputeAotAssembly = false
            });
            analyzer.Run();
            var cache = new global::HybridCLR.Mod.AssemblyCache(resolver);
            var callbacks = new global::HybridCLR.CodeGen.MethodBridge.MonoPInvokeCallbackAnalyzer(cache, hot);
            callbacks.Run();
            var calli = new global::HybridCLR.CodeGen.MethodBridge.CalliAnalyzer(cache, hot);
            calli.Run();
            var requirements = analyzer.AotGenericTypes.Select(NativeTypeRequirement).Concat(analyzer.AotGenericMethods.Select(m => "method:" + MethodRequirement(m.Method) + "|" + SignatureArguments(m.KlassInst) + "|" + SignatureArguments(m.MethodInst)))
                .Concat(callbacks.ReversePInvokeMethods.Select(m => "reverse:" + MethodRequirement(m.Method) + "|" +
                    (m.GenerationAttribute == null ? "" : string.Join(";", m.GenerationAttribute.ConstructorArguments.Select(a => a.ToString())))))
                .Concat(calli.CalliMethodSignatures.Select(s => "calli:" + MethodSignature(s.MethodSig).ToString(Formatting.None) + "|" + s.Callvention));
            foreach (var module in collector.GetLoadedModulesOfRootAssemblies())
            {
                requirements = requirements.Concat(module.GetTypes().SelectMany(t => t.Methods).Where(m => m.IsPinvokeImpl)
                    .Select(m => "pinvoke:" + MethodRequirement(m) + "|" + m.ImplMap.Module.Name + "|" + m.ImplMap.Name + "|" + m.ImplMap.Attributes));
            }

            return requirements.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }

        // 保留嵌套泛型实参及其程序集范围，防止同名类型被误判为同一原生需求。
        internal static string NativeTypeRequirement(global::HybridCLR.Meta.GenericClass type) =>
            "type:" + TypeSignature(type.ToTypeSig()).ToString(Formatting.None);

        // 拒绝仅脚本或 Visual Studio 工程导出，避免将其报告为可执行基座。
        internal static void ValidateExecutableBuildMode(bool scriptsOnly, bool createSolution)
        {
            if (scriptsOnly || createSolution)
            {
                throw new InvalidDataException("A native executable base requires Build Scripts Only=false and Windows Create Solution=false; configure them explicitly before building.");
            }
        }

        // 在 Unity 报告之外确认精确输出 exe 及 GameAssembly.dll 实际存在。
        internal static void ValidatePlayerOutputs(string requested, string actual)
        {
            if (string.IsNullOrEmpty(actual) || Path.GetFullPath(requested) != Path.GetFullPath(actual) || !File.Exists(requested) ||
                !File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requested)), "GameAssembly.dll")))
            {
                throw new InvalidDataException("BuildReport success is not a native Player until the exact requested executable and GameAssembly.dll exist.");
            }
        }

        // 将有序泛型实参编码为保留完整类型结构的稳定描述。
        private static string SignatureArguments(IEnumerable<TypeSig> arguments) =>
            new JArray((arguments ?? Array.Empty<TypeSig>()).Select(t => TypeSignature(t))).ToString(Formatting.None);

        // 组合声明类型、名称及签名，避免仅按方法名比较原生需求。
        private static string MethodRequirement(MethodDef method) => new JObject
        {
            ["declaringType"] = TypeReference(method.DeclaringType),
            ["name"] = method.Name.String,
            ["signature"] = MethodSignature(method.MethodSig)
        }.ToString(Formatting.None);

        // 记录类型的完整名称与定义程序集，缺少范围时拒绝构建需求描述。
        private static JObject TypeReference(ITypeDefOrRef type) => new JObject
        {
            ["assembly"] = type.DefinitionAssembly?.FullName ?? throw new InvalidDataException("A native type requirement has no assembly scope."),
            ["name"] = type.FullName
        };

        // 有界编码调用约定、泛型元数、返回值及含 sentinel 的参数结构。
        private static JObject MethodSignature(MethodSig signature, int depth = 0)
        {
            if (signature == null || depth > 64)
            {
                throw new InvalidDataException("Missing or deeply nested native method signature.");
            }

            return new JObject
            {
                ["convention"] = (int)signature.CallingConvention,
                ["arity"] = signature.GenParamCount,
                ["return"] = TypeSignature(signature.RetType, depth + 1),
                ["parameters"] = new JArray(signature.Params.Select(t => TypeSignature(t, depth + 1))),
                ["sentinelParameters"] = new JArray((signature.ParamsAfterSentinel ?? Array.Empty<TypeSig>()).Select(t => TypeSignature(t, depth + 1)))
            };
        }

        // 有界展开类型签名及修饰、数组或泛型所有者，拒绝无法准确表示的叶节点。
        private static JObject TypeSignature(TypeSig type, int depth = 0)
        {
            if (type == null || depth > 64)
            {
                throw new InvalidDataException("Missing or deeply nested native type signature.");
            }

            var value = new JObject { ["kind"] = (int)type.ElementType };
            switch (type)
            {
                case GenericInstSig generic:
                    value["definition"] = TypeSignature(generic.GenericType, depth + 1);
                    value["arguments"] = new JArray(generic.GenericArguments.Select(t => TypeSignature(t, depth + 1)));
                    break;
                case TypeDefOrRefSig reference:
                    value["type"] = TypeReference(reference.TypeDefOrRef);
                    break;
                case GenericVar variable:
                    value["number"] = variable.Number;
                    if (variable.OwnerType != null)
                    {
                        value["owner"] = TypeReference(variable.OwnerType);
                    }

                    break;
                case GenericMVar variable:
                    value["number"] = variable.Number;
                    if (variable.OwnerMethod != null)
                    {
                        value["owner"] = TypeReference(variable.OwnerMethod.DeclaringType);
                        value["methodToken"] = variable.OwnerMethod.MDToken.Raw;
                    }
                    break;
                case FnPtrSig pointer:
                    if (!(pointer.Signature is MethodSig method))
                    {
                        throw new InvalidDataException("Unsupported native function-pointer signature.");
                    }

                    value["method"] = MethodSignature(method, depth + 1);
                    break;
                default:
                    if (type.Next == null)
                    {
                        throw new InvalidDataException("Unsupported leaf native type signature: " + type.ElementType);
                    }

                    value["next"] = TypeSignature(type.Next, depth + 1);
                    if (type is ModifierSig modifier)
                    {
                        value["modifier"] = TypeReference(modifier.Modifier);
                    }

                    if (type is ArraySig array)
                    {
                        value["rank"] = array.Rank;
                        value["sizes"] = new JArray(array.Sizes);
                        value["lowerBounds"] = new JArray(array.LowerBounds);
                    }
                    if (type is ValueArraySig fixedArray)
                    {
                        value["size"] = fixedArray.Size;
                    }

                    if (type is ModuleSig module)
                    {
                        value["module"] = module.Index;
                    }

                    break;
            }
            return value;
        }

        // 仅从当前 Unity 安装的 Framework 引用及原生实现建立明确身份别名。
        internal static IReadOnlyDictionary<string, string> UnityAotAliases()
        {
            var referenceRoot = EditorApplication.applicationContentsPath + "/UnityReferenceAssemblies/unity-4.8-api";
            var implementationRoot = EditorApplication.applicationContentsPath + "/MonoBleedingEdge/lib/mono/unityaot-win32";
            if (!Directory.Exists(referenceRoot) || !Directory.Exists(implementationRoot))
            {
                throw new InvalidOperationException("The supported Unity Framework reference/native profiles are missing.");
            }

            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(referenceRoot, "*.dll", SearchOption.AllDirectories))
            {
                var reference = HybridCLRAssemblyAnalysis.Read(path);
                var implementationPath = Path.Combine(implementationRoot, reference.Name + ".dll");
                if (!File.Exists(implementationPath))
                {
                    continue;
                }

                var implementation = HybridCLRAssemblyAnalysis.Read(implementationPath);
                var expected = new AssemblyName(reference.Identity);
                var actual = new AssemblyName(implementation.Identity);
                // 仅接纳已安装 Unity 官方 reference/native profile 的同名同版本同 culture 重定向；热更之间仍完全匹配。
                if (reference.Identity == implementation.Identity || expected.Name != actual.Name || expected.Version != actual.Version ||
                    (expected.CultureName ?? "") != (actual.CultureName ?? ""))
                {
                    continue;
                }

                if (aliases.TryGetValue(reference.Identity, out var prior) && prior != implementation.Identity)
                {
                    throw new InvalidDataException("Ambiguous installed framework identity binding.");
                }

                aliases[reference.Identity] = implementation.Identity;
            }
            return aliases;
        }

        // 固定影响全局原生能力的工具、构建和官方登记设置，不纳入项目入口子集。
        internal static string SettingsFingerprint(HybridCLRBuildProfile profile)
        {
            var settings = Settings.instance;
            var description = new
            {
                Unity = Application.unityVersion,
                Version = "9.0.3",
                Target = profile.recipe.target.ToString(),
                Development = profile.development,
                Api = PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Standalone).ToString(),
                Backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone).ToString(),
                Stripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone).ToString(),
                Defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone).Split(';').OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                Hot = ConfiguredNames(),
                Preserved = SettingsUtil.HotUpdateAssemblyNamesIncludePreserved.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                settings.enable,
                settings.useGlobalIl2cpp,
                settings.linkPreserveLevel,
                settings.maxGenericReferenceIteration,
                settings.maxMethodBridgeGenericIteration,
                settings.outputLinkFile,
                settings.externalHotUpdateAssembliyDirs
            };
            return HybridCLRManifest.Hash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(description)));
        }

        // 对实际 Player 输出的相对路径、长度和字节摘要整体计算基座指纹。
        private static string PlayerFingerprint(string executable)
        {
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("The base Player executable is missing.", executable);
            }

            var root = Path.GetDirectoryName(Path.GetFullPath(executable));
            var files = HybridCLRBuildBaseline.SafeFiles(root).OrderBy(p => p, StringComparer.Ordinal).Select(p => new
            {
                Path = p.Substring(root.Length + 1).Replace('\\', '/'),
                Length = new FileInfo(p).Length,
                Sha256 = HybridCLRManifest.Hash(File.ReadAllBytes(p))
            }).ToArray();
            if (!files.Any(f => f.Path == "GameAssembly.dll"))
            {
                throw new InvalidDataException("No native IL2CPP GameAssembly.dll was produced.");
            }

            return HybridCLRManifest.Hash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(files)));
        }

        // 先核对规范生成地址，再调用正式 Resource 流程并保留其首个原始错误。
        internal static void PublishResources(HybridCLRBuildReport report, HybridCLRBuildProfile profile, CancellationToken cancellation, IResourceContentKeyProvider provider)
        {
            var map = ResourceBuildMap.Create(profile.recipe.project);
            var expected = report.Manifest.HotUpdateAssemblies.Concat(report.Manifest.AotMetadata).Select(a => a.Address).Append(HybridCLRManifest.ManifestPath).ToArray();
            var sourceRoot = profile.generatedAssetDirectory.TrimEnd('/') + "/";
            foreach (var address in expected)
            {
                if (!map.Assets.Any(a => a.Address == new ResourceAddress(profile.recipe.project.projectId, address) && a.SourcePath == sourceRoot + address))
                {
                    throw new InvalidDataException("The saved Resource Project must explicitly select the generated canonical address: " + address);
                }
            }

            if (map.Assets.Any(a => a.SourcePath.StartsWith(sourceRoot, StringComparison.Ordinal) && !expected.Contains(a.Address.Path)))
            {
                throw new InvalidDataException("The Resource recipe selects undeclared generated files.");
            }

            report.ResourceBuild = ResourceEditorOperations.ExecuteRecipe(profile.recipe, cancellation, provider);
            report.Release = report.ResourceBuild.Release;
            var error = report.ResourceBuild.BuildError ?? report.ResourceBuild.PublicationError ?? report.ResourceBuild.GenerationError;
            if (error != null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            if (report.Release == null)
            {
                throw new InvalidOperationException("Resource publication was not confirmed.");
            }
        }

        // 检查自有 Assets 生成根及独立持久基线，并在写入前验证现有归属。
        internal static void ValidateInputPaths(HybridCLRBuildProfile profile)
        {
            if (!profile || string.IsNullOrWhiteSpace(profile.generatedAssetDirectory) || !profile.generatedAssetDirectory.StartsWith("Assets/", StringComparison.Ordinal) ||
                profile.generatedAssetDirectory.Contains('\\') || profile.generatedAssetDirectory.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
            {
                throw new InvalidDataException("Use an explicit owned Assets-relative payload directory.");
            }

            if (string.IsNullOrWhiteSpace(profile.baselineDirectory) || profile.baselineDirectory.StartsWith("\\\\", StringComparison.Ordinal) || profile.baselineDirectory.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Use an explicit local persistent baseline directory.");
            }

            var baseline = Path.GetFullPath(profile.baselineDirectory);
            var generated = Path.GetFullPath(profile.generatedAssetDirectory);
            foreach (var reserved in new[] { "Assets", "Packages", "Library", "Temp", ".git", ".kilo", Path.GetTempPath() })
            {
                if (Within(baseline, reserved))
                {
                    throw new InvalidDataException("Baseline must be persistent and outside Assets/Packages/Library/Temp/configuration: " + baseline);
                }
            }

            if (Within(generated, baseline) || Within(baseline, generated))
            {
                throw new InvalidDataException("Payload and baseline roots overlap.");
            }

            HybridCLRBuildBaseline.RejectLinks(baseline);
            HybridCLRBuildBaseline.RejectLinks(generated);
            ReadOwner(profile);
        }

        /// <summary>生成根的 Profile 归属与已提交文件指纹，不用于认领未知用户文件。</summary>
        private sealed class Owner
        {
            /// <summary>生成归属回执的格式版本。</summary>
            public int SchemaVersion = 1;
            /// <summary>拥有此生成根的已保存 Profile GUID。</summary>
            public string ProfileGuid;
            /// <summary>上次提交的规范代码文件及指纹。</summary>
            public HybridCLRBaselineFile[] Files = Array.Empty<HybridCLRBaselineFile>();
        }

        // 只接纳本配置的完整归属记录及已知目录元数据，拒绝未知或被外部修改的文件。
        private static Owner ReadOwner(HybridCLRBuildProfile profile)
        {
            var root = Path.GetFullPath(profile.generatedAssetDirectory);
            var files = HybridCLRBuildBaseline.SafeFiles(root);
            var folders = new[] { "hybridclr", "hybridclr/assemblies", "hybridclr/metadata" };
            if (Directory.Exists(root) && Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Any(p => !folders.Contains(p.Substring(root.Length + 1).Replace('\\', '/'))))
            {
                throw new InvalidDataException("Generated directory contains an unknown subdirectory.");
            }

            var path = Path.Combine(root, OwnerFile);
            if (!File.Exists(path))
            {
                if (files.Any(f => !IsPayloadDirectoryMeta(root, f)))
                {
                    throw new InvalidDataException("Generated directory contains unowned files; nothing was overwritten.");
                }

                return new Owner { ProfileGuid = ProfileGuid(profile) };
            }
            if (new FileInfo(path).Length > 1024 * 1024)
            {
                throw new InvalidDataException("Oversized output ownership record.");
            }

            Owner owner;
            try
            {
                owner = HybridCLRBuildBaseline.ReadObject(File.ReadAllText(path)).ToObject<Owner>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Invalid output ownership record.", exception);
            }
            if (owner == null || owner.SchemaVersion != 1 || owner.ProfileGuid != ProfileGuid(profile) || owner.Files == null || owner.Files.Length > 257)
            {
                throw new InvalidDataException("The generated directory belongs to another or incomplete profile.");
            }

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path, path + ".meta" };
            foreach (var record in owner.Files)
            {
                if (record == null || !IsPayloadPath(record.Path) || !HybridCLRBuildBaseline.IsHash(record.Sha256) ||
                    record.Length <= 0 || record.Length > HybridCLRManifest.MaximumPayloadBytes)
                {
                    throw new InvalidDataException("Invalid owned payload record.");
                }

                var file = HybridCLRBuildBaseline.Child(root, record.Path);
                if (!expected.Add(file) || !File.Exists(file) || new FileInfo(file).Length != record.Length || HybridCLRManifest.Hash(File.ReadAllBytes(file)) != record.Sha256)
                {
                    throw new InvalidDataException("An owned generated payload is missing or was modified outside this pipeline: " + record.Path);
                }

                expected.Add(file + ".meta");
            }
            foreach (var file in files)
            {
                if (!expected.Contains(file) && !IsPayloadDirectoryMeta(root, file))
                {
                    throw new InvalidDataException("Unknown generated file: " + file);
                }
            }
            return owner;
        }

        // 识别允许预先创建的规范代码目录元数据，不扩大可认领范围。
        private static bool IsPayloadDirectoryMeta(string root, string file) => file.EndsWith(".meta", StringComparison.Ordinal) &&
            Directory.Exists(file.Substring(0, file.Length - 5)) && new[] { "hybridclr.meta", "hybridclr/assemblies.meta", "hybridclr/metadata.meta" }
                .Contains(file.Substring(root.Length + 1).Replace('\\', '/'));

        // 识别受管清单及固定两类 DLL 资源路径。
        private static bool IsPayloadPath(string path) => path == HybridCLRManifest.ManifestPath || path != null &&
            (path.StartsWith("hybridclr/assemblies/", StringComparison.Ordinal) || path.StartsWith("hybridclr/metadata/", StringComparison.Ordinal)) &&
            path.EndsWith(".dll.bytes", StringComparison.Ordinal) && path.Split('/').Length == 3;

        // 复核输入后更新自有文件及归属，最后写 manifest；跨文件失败不承诺自动回滚。
        internal static void CommitPayloads(HybridCLRBuildProfile profile, HybridCLRManifest manifest, HybridCLRAssemblySet assemblies)
        {
            ValidateInputPaths(profile);
            manifest.Validate(profile.recipe.project.projectId, profile.recipe.appContentCompatibilityId, Application.unityVersion, profile.development);
            var root = Path.GetFullPath(profile.generatedAssetDirectory);
            var owner = ReadOwner(profile);
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var inputs = assemblies.HotUpdate.Concat(assemblies.Aot).ToDictionary(a => a.Name, StringComparer.Ordinal);
            foreach (var payload in manifest.HotUpdateAssemblies.Concat(manifest.AotMetadata))
            {
                var bytes = File.ReadAllBytes(inputs[payload.Name].Path);
                HybridCLRManifest.ValidatePayload(payload, bytes);
                payloads.Add(payload.Address, bytes);
            }
            var descriptor = Encoding.UTF8.GetBytes(manifest.ToJson());
            if (descriptor.Length > HybridCLRManifest.MaximumManifestBytes)
            {
                throw new InvalidDataException("Generated manifest exceeds its byte budget.");
            }

            payloads.Add(HybridCLRManifest.ManifestPath, descriptor);
            var next = new Owner
            {
                ProfileGuid = owner.ProfileGuid,
                Files = payloads.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                new HybridCLRBaselineFile { Path = p.Key, Length = p.Value.LongLength, Sha256 = HybridCLRManifest.Hash(p.Value) }).ToArray()
            };
            Directory.CreateDirectory(root);
            foreach (var file in payloads.Where(p => p.Key != HybridCLRManifest.ManifestPath))
            {
                WriteOwned(root, file.Key, file.Value);
            }

            foreach (var obsolete in owner.Files.Where(f => !payloads.ContainsKey(f.Path)))
            {
                if (!AssetDatabase.DeleteAsset(profile.generatedAssetDirectory.TrimEnd('/') + "/" + obsolete.Path))
                {
                    throw new IOException("Could not remove a verified obsolete owned payload: " + obsolete.Path);
                }
            }

            WriteOwned(root, OwnerFile, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(next)));
            WriteOwned(root, HybridCLRManifest.ManifestPath, descriptor);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        // 单文件持久写入后原子替换，内容相同时不改写且保留既有 .meta 身份。
        internal static void WriteOwned(string root, string relative, byte[] bytes, Action<string, string> replace = null, Action<int> backoff = null)
        {
            var target = HybridCLRBuildBaseline.Child(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var previous = File.Exists(target) ? File.ReadAllBytes(target) : null;
            if (previous != null && previous.SequenceEqual(bytes))
            {
                return;
            }

            var pending = target + ".pending-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (previous == null)
            {
                File.Move(pending, target);
                return;
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (replace == null)
                    {
                        File.Replace(pending, target, null);
                    }
                    else
                    {
                        replace(pending, target);
                    }

                    return;
                }
                catch (IOException error) when (Application.platform == RuntimePlatform.WindowsEditor && (uint)error.HResult == 0x80070497u)
                {
                    // 与 Resource Publisher 一致：1175 保留原名；仅在两边未变化时有限重试，绝不吞掉部分提交类错误。
                    HybridCLRBuildBaseline.RejectLinks(target);
                    HybridCLRBuildBaseline.RejectLinks(pending);
                    if (!File.Exists(target) || !File.Exists(pending) || !File.ReadAllBytes(target).SequenceEqual(previous) || !File.ReadAllBytes(pending).SequenceEqual(bytes))
                    {
                        throw new InvalidOperationException("Owned payload changed after atomic replacement failed; retry refused.", error);
                    }

                    if (attempt >= 5)
                    {
                        File.Delete(pending);
                        throw;
                    }
                    var delay = attempt == 0 ? 10 : 25 << (attempt - 1);
                    if (backoff == null)
                    {
                        Thread.Sleep(delay);
                    }
                    else
                    {
                        backoff(delay);
                    }
                }
            }
        }

        // 要求 Profile 已保存为有稳定 GUID 的资产，作为归属与基座拥有者身份。
        private static string ProfileGuid(HybridCLRBuildProfile profile)
        {
            var path = AssetDatabase.GetAssetPath(profile);
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(guid))
            {
                throw new InvalidDataException("A saved profile with a stable GUID is required.");
            }

            return guid;
        }

        // 按规范绝对路径判断相等或目录包含，避免前缀同名造成边界误判。
        private static bool Within(string candidate, string directory)
        {
            var full = Path.GetFullPath(candidate).TrimEnd('\\', '/');
            var root = Path.GetFullPath(directory).TrimEnd('\\', '/');
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
