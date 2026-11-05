using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace YLFramework.Testing
{
    // Shared by the PowerShell controllers and their Unity probes; names are compared, never executed.
    public static class PackageCatalogContract
    {
        public const string HostPath = "YLTesting/PackageCatalog.json";

        public static JObject Load(JObject request, string project, Func<string, JObject> readJson)
        {
            string digest = Text(request, "catalogSha256");
            string path = Path.Combine(project, HostPath);
            if (!Regex.IsMatch(digest, @"\A[0-9a-f]{64}\z") || !File.Exists(path)) { throw Error("Missing typed package catalog identity."); }
            foreach (string input in new[] { path, Path.Combine(project, ".yl-inputs.json") })
            {
                for (string current = input; current != null; current = Path.GetDirectoryName(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw Error("Linked package catalog/input identity is refused."); }
                }
            }
            using var hash = SHA256.Create();
            string actual = string.Concat(hash.ComputeHash(File.ReadAllBytes(path)).Select(value => value.ToString("x2")));
            var inputs = readJson(Path.Combine(project, ".yl-inputs.json"));
            var rows = inputs["files"] as JArray;
            if (inputs["schemaVersion"]?.Type != JTokenType.Integer || (long)inputs["schemaVersion"] != 1 || Text(inputs, "purpose") != "YLTesting.HostInputs" ||
                Text(inputs, "hostId") != Text(request, "hostId") || Text(inputs, "inputHash") != Text(request, "inputHash") ||
                !Regex.IsMatch(Text(request, "inputHash"), @"\A[0-9a-f]{64}\z") || actual != digest || rows == null || rows.OfType<JObject>().Count(row => (string)row["path"] == HostPath &&
                row["sha256"]?.Type == JTokenType.String && string.Equals((string)row["sha256"], digest, StringComparison.OrdinalIgnoreCase)) != 1)
            {
                throw Error("The package catalog is not bound to the owned host input identity.");
            }
            var catalog = readJson(path);
            Validate(catalog);
            return catalog;
        }

        public static void Validate(JObject catalog)
        {
            Fields(catalog, "schemaVersion", "packages");
            if (catalog["schemaVersion"]?.Type != JTokenType.Integer || (long)catalog["schemaVersion"] != 1 ||
                catalog["packages"] is not JArray packages || packages.Count == 0) { throw Error("Invalid package catalog schema."); }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in packages)
            {
                if (token is not JObject package) { throw Error("Catalog packages must be objects."); }
                Fields(package, "id", "assemblies", "requiredFiles", "samples");
                if (!Regex.IsMatch(Text(package, "id"), @"\Acom\.kano\.ylframework(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)?\z") || !ids.Add(Text(package, "id"))) { throw Error("Invalid/duplicate catalog package."); }
            }
            if (!ids.Contains("com.kano.ylframework")) { throw Error("The catalog must include the main package."); }
            foreach (var package in packages.Cast<JObject>())
            {
                if (package["assemblies"] is not JArray assemblies || assemblies.Count == 0) { throw Error("Catalog assemblies must be explicit."); }
                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in assemblies)
                {
                    if (token is not JObject assembly) { throw Error("Catalog assemblies must be objects."); }
                    Fields(assembly, "name", "path", "player", "defineConstraints", "versionDefines");
                    string name = Text(assembly, "name"), path = Relative(Text(assembly, "path"));
                    if (!Regex.IsMatch(name, @"\AYLFramework\.[A-Za-z0-9_.]+\z") || !names.Add(name) || assembly["player"]?.Type != JTokenType.Boolean ||
                        !Regex.IsMatch(path, @"\A(?:Runtime|Editor|Samples~)/") || !path.EndsWith("/" + name + ".asmdef", StringComparison.Ordinal) ||
                        !directories.Add(path.Substring(0, path.LastIndexOf('/'))) || assembly["defineConstraints"] is not JArray constraints || assembly["versionDefines"] is not JArray versions)
                    {
                        throw Error("Invalid catalog assembly identity/path/player policy.");
                    }
                    var defines = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var define in constraints)
                    {
                        if (define.Type != JTokenType.String || !Regex.IsMatch((string)define, @"\A[A-Z][A-Z0-9_]*\z") || !defines.Add((string)define)) { throw Error("Invalid catalog define constraint."); }
                    }
                    var versionDefines = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var value in versions)
                    {
                        if (value is not JObject version) { throw Error("Invalid catalog version define."); }
                        Fields(version, "name", "expression", "define");
                        if ((!ids.Contains(Text(version, "name")) && Text(version, "name") != "com.code-philosophy.hybridclr") ||
                            !new[] { "0.0.0", "[9.0.3,9.0.4)" }.Contains(Text(version, "expression")) ||
                            !defines.Contains(Text(version, "define")) || !versionDefines.Add(Text(version, "define"))) { throw Error("Unknown/unsupported catalog package condition."); }
                    }
                    if (defines.Any(define => define != "DOTWEEN" && !versionDefines.Contains(define))) { throw Error("A catalog define has no explicit supported prerequisite/profile."); }
                }
                foreach (string field in new[] { "requiredFiles", "samples" })
                {
                    if (package[field] is not JArray paths) { throw Error("Catalog payload paths must be arrays."); }
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var value in paths)
                    {
                        if (value.Type != JTokenType.String || !seen.Add(Relative((string)value)) ||
                            (field == "samples" && !((string)value).StartsWith("Samples~/", StringComparison.Ordinal))) { throw Error("Invalid catalog payload/sample path."); }
                    }
                }
            }
        }

        public static JArray Definitions(JObject catalog, JArray packages, string profile, string dotweenMode)
        {
            Validate(catalog);
            if (!new[] { "Package", "DOTween", "Excel", "ExcelGameplay", "HybridCLR", "OptionalAdapterFixture" }.Contains(profile) ||
                (profile != "DOTween" && !string.IsNullOrEmpty(dotweenMode)) || (profile == "DOTween" && !new[] { "Required", "Absent" }.Contains(dotweenMode))) { throw Error("Unknown/ambiguous package smoke profile."); }
            var declared = ((JArray)catalog["packages"]).Cast<JObject>().ToDictionary(row => Text(row, "id"), StringComparer.Ordinal);
            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in packages)
            {
                if (token is not JObject package || !declared.ContainsKey(Text(package, "name")) ||
                    !Regex.IsMatch(Text(package, "version"), @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z") || versions.ContainsKey(Text(package, "name"))) { throw Error("Unknown/duplicate package plan input."); }
                versions.Add(Text(package, "name"), Text(package, "version"));
            }
            if (versions.Count == 0) { throw Error("The package plan must be nonempty."); }
            if (profile == "HybridCLR") { versions.Add("com.code-philosophy.hybridclr", "9.0.3"); }
            var result = new JArray();
            foreach (var package in packages.Cast<JObject>())
            {
                foreach (var assembly in ((JArray)declared[Text(package, "name")]["assemblies"]).Cast<JObject>())
                {
                    var defines = new HashSet<string>(StringComparer.Ordinal);
                    if (profile == "DOTween" && dotweenMode == "Required") { defines.Add("DOTWEEN"); }
                    foreach (var version in ((JArray)assembly["versionDefines"]).Cast<JObject>())
                    {
                        if (versions.TryGetValue(Text(version, "name"), out string installed) &&
                            (Text(version, "expression") == "0.0.0" ? !installed.StartsWith("0.0.0-", StringComparison.Ordinal) : installed == "9.0.3"))
                        {
                            defines.Add(Text(version, "define"));
                        }
                    }
                    string path = Text(assembly, "path");
                    result.Add(new JObject
                    {
                        ["name"] = Text(assembly, "name"), ["package"] = Text(package, "name"), ["path"] = path,
                        ["directory"] = path.Substring(0, path.LastIndexOf('/')), ["player"] = (bool)assembly["player"],
                        ["enabled"] = !path.StartsWith("Samples~/", StringComparison.Ordinal) && ((JArray)assembly["defineConstraints"]).All(value => defines.Contains((string)value))
                    });
                }
            }
            return result;
        }

        public static JArray Plan(JObject catalog, JArray packages, string profile, string dotweenMode)
        {
            var result = Definitions(catalog, packages, profile, dotweenMode);
            foreach (var package in packages.Cast<JObject>())
            {
                string id = Text(package, "name");
                if (package["files"] is not JArray files) { throw Error("Missing complete package payload inventory."); }
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in files)
                {
                    if (value is not JObject file || !paths.Add(Relative(Text(file, "path")))) { throw Error("Duplicate/invalid package payload path."); }
                }
                var owners = result.Cast<JObject>().Where(row => Text(row, "package") == id).ToArray();
                var asmdefs = new HashSet<string>(owners.Select(row => Text(row, "path")), StringComparer.Ordinal);
                if (!asmdefs.IsSubsetOf(paths) || paths.Any(path => path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) && !asmdefs.Contains(path))) { throw Error("Missing/undeclared package assembly payload."); }
                var declaration = ((JArray)catalog["packages"]).Cast<JObject>().Single(row => Text(row, "id") == id);
                if (((JArray)declaration["requiredFiles"]).Any(path => !paths.Contains((string)path))) { throw Error("Missing catalog-required package payload."); }
                foreach (var owner in owners) { owner["sources"] = new JArray(); }
                foreach (string path in paths.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var owner = owners.Where(row => path.StartsWith(Text(row, "directory") + "/", StringComparison.Ordinal)).OrderByDescending(row => Text(row, "directory").Length).FirstOrDefault();
                    if (owner == null) { throw Error("Package source has no declared assembly owner: " + path); }
                    ((JArray)owner["sources"]).Add(path);
                }
                if (owners.Any(owner => ((JArray)owner["sources"]).Count == 0)) { throw Error("Required assembly has an empty source payload."); }
            }
            return result;
        }

        public static void AssertClosure(JObject catalog, JObject manifests, IEnumerable<string> entryPackages)
        {
            Validate(catalog);
            var known = new HashSet<string>(((JArray)catalog["packages"]).Cast<JObject>().Select(row => Text(row, "id")), StringComparer.Ordinal);
            var roots = entryPackages?.ToArray();
            if (roots == null || roots.Length == 0 || roots.Distinct(StringComparer.Ordinal).Count() != roots.Length || roots.Any(root => !known.Contains(root))) { throw Error("Unknown/empty/duplicate package entry root selection."); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(roots);
            while (queue.Count != 0)
            {
                string id = queue.Dequeue();
                if (!seen.Add(id)) { continue; }
                if (!known.Contains(id) || manifests[id] is not JObject manifest || Text(manifest, "name") != id || manifest["dependencies"] is not JObject dependencies) { throw Error("Missing/unknown package dependency closure member."); }
                foreach (var dependency in dependencies.Properties())
                {
                    if (dependency.Value.Type != JTokenType.String) { throw Error("Invalid package dependency version."); }
                    if (!dependency.Name.StartsWith("com.kano.", StringComparison.Ordinal)) { continue; }
                    if (id == "com.kano.ylframework" || dependency.Name == id || !known.Contains(dependency.Name) || manifests[dependency.Name] is not JObject target ||
                        (string)dependency.Value != Text(target, "version")) { throw Error("Main/optional package dependency direction or exact version differs."); }
                    queue.Enqueue(dependency.Name);
                }
            }
            if (!seen.SetEquals(manifests.Properties().Select(property => property.Name))) { throw Error("Package selection exceeds its exact entry dependency closure."); }
        }

        public static void AssertGraph(JArray plan, IEnumerable<string> editor, IEnumerable<string> player)
        {
            foreach (var graph in new[] { (names: editor, player: false), (names: player, player: true) })
            {
                var actual = graph.names.Where(name => name.StartsWith("YLFramework.", StringComparison.Ordinal)).ToArray();
                var expected = plan.Cast<JObject>().Where(row => (bool)row["enabled"] && (!graph.player || (bool)row["player"])).Select(row => Text(row, "name"));
                if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count() || !new HashSet<string>(expected, StringComparer.Ordinal).SetEquals(actual)) { throw Error("The actual product assembly graph differs from the explicit catalog plan."); }
            }
        }

        public static string Text(JObject value, string field)
        {
            if (value[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value[field])) { throw Error("A typed package catalog string is required: " + field); }
            return (string)value[field];
        }

        private static void Fields(JObject value, params string[] fields)
        {
            if (value.Properties().Count() != fields.Length || fields.Any(field => value.Property(field, StringComparison.Ordinal) == null)) { throw Error("Unknown/missing package catalog fields."); }
        }

        private static string Relative(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || Regex.IsMatch(path, "[\\\\:\\x00-\\x1f<>\"|?*]") || path.Split('/').Any(part =>
                part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase))) { throw Error("Unsafe package catalog/payload path."); }
            return path;
        }

        private static InvalidOperationException Error(string message) => new InvalidOperationException(message);
    }
}
