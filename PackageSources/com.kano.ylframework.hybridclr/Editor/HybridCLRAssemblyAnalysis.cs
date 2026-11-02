using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using YLFramework.Integrations.HybridCLR;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>从显式 DLL 输入读取的程序集身份、引用及原始文件指纹。</summary>
    public sealed class HybridCLRAssemblyRecord
    {
        /// <summary>已读取 DLL 的绝对路径。</summary>
        public string Path
        {
            get; internal set;
        }
        /// <summary>程序集的精确简单名称，不含扩展名。</summary>
        public string Name
        {
            get; internal set;
        }
        /// <summary>规范化的完整程序集身份，包含版本、culture 与公钥标记。</summary>
        public string Identity
        {
            get; internal set;
        }
        /// <summary>程序集 Module 的 D 格式 MVID。</summary>
        public string Mvid
        {
            get; internal set;
        }
        /// <summary>按序排列的完整程序集引用身份。</summary>
        public string[] References
        {
            get; internal set;
        }
        /// <summary>原始 DLL 的字节长度。</summary>
        public long Length
        {
            get; internal set;
        }
        /// <summary>原始 DLL 字节的小写十六进制 SHA256。</summary>
        public string Sha256
        {
            get; internal set;
        }
    }

    /// <summary>显式区分热更与 AOT 输入的程序集集合；分析结果保留稳定排序。</summary>
    public sealed class HybridCLRAssemblySet
    {
        /// <summary>分析后按依赖优先排列的热更程序集。</summary>
        public IReadOnlyList<HybridCLRAssemblyRecord> HotUpdate
        {
            get; internal set;
        }
        /// <summary>分析后按名称排列的 AOT 程序集。</summary>
        public IReadOnlyList<HybridCLRAssemblyRecord> Aot
        {
            get; internal set;
        }
    }

    /// <summary>在 Editor 读取 DLL 并分析明确输入的依赖，不把程序集加载进 AppDomain。</summary>
    public static class HybridCLRAssemblyAnalysis
    {
        /// <summary>通过官方引用及泛型分析收集热更集合需要补充的 AOT metadata。</summary>
        /// <param name="assemblies">包含完整热更闭包和可解析 AOT 输入的集合。</param>
        /// <param name="maximumIterations">官方泛型分析的最大迭代次数，范围为 1 至 20。</param>
        /// <returns>按名称排序的所需 AOT 记录；缺少显式输入时失败。</returns>
        /// <remarks>仅反映静态可见需求，不证明任意反射或动态泛型都能在原生基座执行。</remarks>
        public static IReadOnlyList<HybridCLRAssemblyRecord> CollectRequiredAotMetadata(HybridCLRAssemblySet assemblies, int maximumIterations = 10)
        {
            if (assemblies == null)
            {
                throw new ArgumentNullException(nameof(assemblies));
            }

            if (maximumIterations < 1 || maximumIterations > 20)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumIterations));
            }

            var hot = new HashSet<string>(assemblies.HotUpdate.Select(r => r.Name), StringComparer.Ordinal);
            var aot = assemblies.Aot.ToDictionary(r => r.Name, StringComparer.Ordinal);
            var collector = new global::HybridCLR.Meta.AssemblyReferenceDeepCollector(CreateResolver(assemblies), hot.OrderBy(n => n, StringComparer.Ordinal).ToList());
            var analyzer = new global::HybridCLR.CodeGen.AOT.AOTReferenceAnalyzer(new global::HybridCLR.CodeGen.AOT.AOTReferenceAnalyzer.Options
            {
                Collector = collector,
                MaxIterationCount = maximumIterations,
                ComputeAotAssembly = false
            });
            analyzer.Run();
            // 官方 cache 拥有 ModuleDefMD，且没有 Dispose 合同；只读它们，不释放借用的 module。
            var required = new HashSet<string>(collector.GetLoadedModulesExcludeRootAssemblies().Select(m => m.Assembly.Name.String), StringComparer.Ordinal);
            foreach (var module in collector.GetLoadedModules())
            {
                foreach (var reference in module.GetAssemblyRefs())
                {
                    if (!hot.Contains(reference.Name.String))
                    {
                        required.Add(reference.Name.String);
                    }
                }
            }

            foreach (var type in analyzer.AotGenericTypes)
            {
                required.Add(type.Type.Module.Assembly.Name.String);
            }

            foreach (var method in analyzer.AotGenericMethods)
            {
                required.Add(method.Method.Module.Assembly.Name.String);
            }

            required.ExceptWith(hot);
            foreach (var name in required)
            {
                if (!aot.ContainsKey(name))
                {
                    throw new InvalidDataException("Required metadata is absent from the explicit AOT input set: " + name);
                }
            }

            return Array.AsReadOnly(required.OrderBy(n => n, StringComparer.Ordinal).Select(n => aot[n]).ToArray());
        }

        // 将官方分析器的解析范围限定到已检查的热更及 AOT 输入。
        internal static global::HybridCLR.Mod.IAssemblyResolver CreateResolver(HybridCLRAssemblySet assemblies) =>
            new ExactAssemblyResolver(assemblies.HotUpdate.Concat(assemblies.Aot));

        /// <summary>只按精确简单名称返回本次输入路径，不搜索其他目录。</summary>
        private sealed class ExactAssemblyResolver : global::HybridCLR.Mod.IAssemblyResolver
        {
            private readonly Dictionary<string, string> _paths;
            /// <summary>建立本次分析允许解析的名称与路径映射。</summary>
            /// <param name="assemblies">名称唯一的显式程序集输入。</param>
            public ExactAssemblyResolver(IEnumerable<HybridCLRAssemblyRecord> assemblies) =>
                _paths = assemblies.ToDictionary(r => r.Name, r => r.Path, StringComparer.Ordinal);

            /// <summary>解析明确输入；要求报错或缺少 netstandard 时拒绝继续。</summary>
            /// <param name="assemblyName">待解析的精确简单名称。</param>
            /// <param name="throwExIfNotFind">找不到普通程序集时是否抛出异常。</param>
            /// <returns>已登记路径，或允许缺失时的空值。</returns>
            public string ResolveAssembly(string assemblyName, bool throwExIfNotFind)
            {
                if (_paths.TryGetValue(assemblyName, out var path))
                {
                    return path;
                }

                if (throwExIfNotFind || assemblyName == "netstandard")
                {
                    throw new InvalidDataException("Assembly is outside the checked compilation/AOT inputs: " + assemblyName);
                }

                return null;
            }
        }

        /// <summary>拒绝链接及超预算文件后读取托管 DLL 的身份、MVID、引用和指纹。</summary>
        /// <param name="path">显式 DLL 文件路径，可为相对路径。</param>
        /// <returns>对应读取字节的程序集记录，不代表该 DLL 能在目标基座执行。</returns>
        public static HybridCLRAssemblyRecord Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An explicit assembly path is required.", nameof(path));
            }

            var full = System.IO.Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException("Assembly input does not exist.", full);
            }

            for (var cursor = full; cursor != null; cursor = System.IO.Path.GetDirectoryName(cursor))
            {
                if ((File.GetAttributes(cursor) & System.IO.FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Linked assembly input is not supported: " + full);
                }
            }

            var bytes = File.ReadAllBytes(full);
            if (bytes.Length == 0 || bytes.LongLength > HybridCLRManifest.MaximumPayloadBytes)
            {
                throw new InvalidDataException("Assembly input is empty or oversized: " + full);
            }

            try
            {
                using var module = ModuleDefMD.Load(bytes);
                if (module.Assembly == null || !module.IsILOnly || module.Mvid == null)
                {
                    throw new InvalidDataException("A managed manifest assembly with an MVID is required: " + full);
                }

                return new HybridCLRAssemblyRecord
                {
                    Path = full,
                    Name = module.Assembly.Name.String,
                    Identity = new AssemblyName(module.Assembly.FullName).FullName,
                    Mvid = module.Mvid.Value.ToString("D"),
                    Length = bytes.LongLength,
                    Sha256 = HybridCLRManifest.Hash(bytes),
                    References = module.GetAssemblyRefs().Select(r => new AssemblyName(r.FullName).FullName)
                        .OrderBy(n => n, StringComparer.Ordinal).ToArray()
                };
            }
            catch (Exception exception) when (exception is BadImageFormatException || exception is IOException || exception is ArgumentException)
            {
                throw new InvalidDataException("Cannot read managed assembly metadata: " + full, exception);
            }
        }

        /// <summary>校验输入身份、热更依赖闭包及 AOT 不静态引用热更的边界，并生成稳定顺序。</summary>
        /// <param name="hotUpdatePaths">非空的显式热更 DLL 路径集合，不接受 Assembly-CSharp。</param>
        /// <param name="aotPaths">用于解析引用的 AOT DLL 路径集合。</param>
        /// <returns>热更依赖优先、AOT 按名称排序的已分析集合。</returns>
        public static HybridCLRAssemblySet Analyze(IEnumerable<string> hotUpdatePaths, IEnumerable<string> aotPaths)
            => Analyze(hotUpdatePaths, aotPaths, null);

        // 分析完整输入，仅对明确给出的 Unity AOT 身份别名放行绑定差异。
        internal static HybridCLRAssemblySet Analyze(IEnumerable<string> hotUpdatePaths, IEnumerable<string> aotPaths, IReadOnlyDictionary<string, string> aotIdentityAliases)
        {
            if (hotUpdatePaths == null)
            {
                throw new ArgumentNullException(nameof(hotUpdatePaths));
            }

            if (aotPaths == null)
            {
                throw new ArgumentNullException(nameof(aotPaths));
            }

            var hot = hotUpdatePaths.Select(Read).ToArray();
            var aot = aotPaths.Select(Read).ToArray();
            if (hot.Length == 0 || hot.Length > HybridCLRManifest.MaximumAssemblyCount)
            {
                throw new InvalidDataException("An explicit supported hot-update assembly set is required.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in hot.Concat(aot))
            {
                if (!names.Add(record.Name))
                {
                    throw new InvalidDataException("Duplicate or case-colliding assembly identity: " + record.Name);
                }
            }

            var all = hot.Concat(aot).ToDictionary(r => r.Name, StringComparer.Ordinal);
            var hotNames = new HashSet<string>(hot.Select(r => r.Name), StringComparer.Ordinal);
            foreach (var record in hot)
            {
                if (record.Name == "Assembly-CSharp" || record.Name == "Assembly-CSharp-firstpass")
                {
                    throw new InvalidDataException("Explicit asmdef or external DLL inputs are required; Assembly-CSharp is not supported.");
                }

                foreach (var reference in record.References)
                {
                    var identity = new AssemblyName(reference);
                    if (identity.Name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                        !all.TryGetValue(identity.Name, out var resolved))
                    {
                        throw new InvalidDataException("Missing, Editor-only or incompatible dependency: " + record.Name + " -> " + reference);
                    }

                    if (resolved.Identity != identity.FullName && (hotNames.Contains(identity.Name) || aotIdentityAliases == null ||
                        !aotIdentityAliases.TryGetValue(identity.FullName, out var bound) || bound != resolved.Identity))
                    {
                        throw new InvalidDataException("Incompatible dependency identity: " + record.Name + " -> " + reference + "; resolved=" + resolved.Identity);
                    }
                }
            }
            foreach (var record in aot)
            {
                if (record.References.Any(r => hotNames.Contains(new AssemblyName(r).Name)))
                {
                    throw new InvalidDataException("AOT statically references a hot-update assembly: " + record.Name);
                }
            }

            var remaining = hot.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
            var ordered = new List<HybridCLRAssemblyRecord>();
            var completed = new HashSet<string>(StringComparer.Ordinal);
            while (remaining.Count != 0)
            {
                var next = remaining.FirstOrDefault(r => r.References.Select(n => new AssemblyName(n).Name)
                    .Where(hotNames.Contains).All(completed.Contains));
                if (next == null)
                {
                    throw new InvalidDataException("Cyclic hot-update assembly dependencies.");
                }

                ordered.Add(next);
                completed.Add(next.Name);
                remaining.Remove(next);
            }
            return new HybridCLRAssemblySet { HotUpdate = ordered.AsReadOnly(), Aot = Array.AsReadOnly(aot.OrderBy(r => r.Name, StringComparer.Ordinal).ToArray()) };
        }
    }
}
