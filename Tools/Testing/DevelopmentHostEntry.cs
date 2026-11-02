#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace YLFramework.Testing
{
    /// <summary>Author-only ownership gate; discovery, execution and XML remain owned by the shared UTF adapter.</summary>
    public static class DevelopmentHostEntry
    {
        public static void Execute()
        {
            var arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-ylTestRequest");
            if (index < 0 || index + 1 >= arguments.Length)
            {
                throw new InvalidOperationException("An explicit owned author-host request is required.");
            }
            string path = Path.GetFullPath(arguments[index + 1]);
            var request = JObject.Parse(File.ReadAllText(path));
            var profile = ValidateInvocation(request, path);
            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["purpose"] = "YLTesting.DevelopmentHost",
                ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["isBatchMode"] = Application.isBatchMode,
                ["unityVersion"] = Application.unityVersion,
                ["sourceRoot"] = Text(profile, "sourceRoot")
            };
            foreach (string field in new[] { "sourceProfile", "hostId", "workspaceId", "resultId", "inputHash", "action" })
            {
                report[field] = Text(request, field);
            }
            using (var stream = new FileStream(Path.Combine(Text(request, "output"), "entry.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(report.ToString());
            }
            Editor.YLTestEntry.Execute();
        }

        private static JObject ValidateInvocation(JObject request, string requestPath)
        {
            if (request["schemaVersion"]?.Type != JTokenType.Integer || (long)request["schemaVersion"] != 1 ||
                Text(request, "purpose") != "YLTesting.DevelopmentHost" || !Application.isBatchMode ||
                Text(request, "sourceProfile") != "YLFrameworkDev-6000.3.27f1_672d562a42a7" ||
                !new[] { "Discover", "Run" }.Contains(Text(request, "action")) ||
                Text(request, "expectedUnityVersion") != Application.unityVersion || Application.unityVersion != "6000.3.27f1" ||
                request["assemblies"] is not JArray assemblies || assemblies.Count != 1 || assemblies[0].Type != JTokenType.String ||
                (string)assemblies[0] != "YLFramework.DevelopmentHost.Tests" || Text(request, "category") != "" ||
                request["excludedCategories"] is not JArray excluded || excluded.Count != 0 || Text(request, "sortingLayer") != "" ||
                request["interactiveDesktop"]?.Type != JTokenType.Boolean || (bool)request["interactiveDesktop"] ||
                !Regex.IsMatch(Text(request, "inputHash"), "^[0-9a-f]{64}$"))
            {
                throw new InvalidOperationException("The exact batch author profile and unexcluded UTF selection are required.");
            }
            foreach (string field in new[] { "disableDomainReload", "prepareSceneTemplates", "prepareSessionScene" })
            {
                if (request[field] != null && (request[field].Type != JTokenType.Boolean || (bool)request[field]))
                {
                    throw new InvalidOperationException("Author boundary tests must not prepare or save scene/settings state.");
                }
            }
            string project = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
            string workspace = Directory.GetParent(project)?.Parent?.FullName;
            string output = Path.GetFullPath(Text(request, "output"));
            string result = Path.GetDirectoryName(output);
            RejectLinks(project); RejectLinks(output); RejectLinks(requestPath);
            var hostOwner = ReadOwner(project, "Host");
            var resultOwner = ReadOwner(result, "Result");
            var workspaceOwner = JObject.Parse(File.ReadAllText(Path.Combine(workspace ?? "", ".yl-workspace.json")));
            var inputs = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-inputs.json")));
            var profile = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-development-host.json")));
            if (workspaceOwner["schemaVersion"]?.Type != JTokenType.Integer || (long)workspaceOwner["schemaVersion"] != 1 ||
                Text(workspaceOwner, "purpose") != "YLTesting.Workspace" || Text(workspaceOwner, "root") != workspace ||
                Text(workspaceOwner, "id") != Text(request, "workspaceId") || Text(hostOwner, "workspaceId") != Text(request, "workspaceId") ||
                Text(hostOwner, "id") != Text(request, "hostId") || project != Path.Combine(workspace, "Hosts", Text(hostOwner, "key")) ||
                Text(resultOwner, "workspaceId") != Text(request, "workspaceId") || Text(resultOwner, "id") != Text(request, "resultId") ||
                !Regex.IsMatch(Path.GetFileName(result), "^[0-9a-f]{32}$") ||
                result != Path.Combine(workspace, "Results", Text(resultOwner, "key"), Path.GetFileName(result)) ||
                output != Path.Combine(result, Text(request, "action")) || requestPath != Path.Combine(output, "request.json") ||
                inputs["schemaVersion"]?.Type != JTokenType.Integer || (long)inputs["schemaVersion"] != 1 || Text(inputs, "purpose") != "YLTesting.HostInputs" ||
                Text(inputs, "hostId") != Text(request, "hostId") || Text(inputs, "inputHash") != Text(request, "inputHash") ||
                profile["schemaVersion"]?.Type != JTokenType.Integer || (long)profile["schemaVersion"] != 1 || Text(profile, "purpose") != "YLTesting.DevelopmentHost" ||
                Text(profile, "hostId") != Text(request, "hostId") || Text(profile, "workspaceId") != Text(request, "workspaceId") ||
                Text(profile, "sourceProfile") != Text(request, "sourceProfile") || Text(profile, "unityVersion") != Application.unityVersion ||
                Text(profile, "productVersion") != "6000.3.27f1_672d562a42a7" || Text(profile, "sourceRoot") != Text(workspaceOwner, "repository") ||
                Path.GetFullPath(Text(profile, "sourceRoot")) == project)
            {
                throw new InvalidOperationException("Author source/workspace/host/result/input identity mismatch.");
            }
            if (inputs["files"] is not JArray files || files.Count == 0)
            {
                throw new InvalidOperationException("The owned author host has no staged inputs.");
            }
            foreach (var file in files.Cast<JObject>())
            {
                string relative = Text(file, "path");
                string staged = Path.GetFullPath(Path.Combine(project, relative));
                if (Path.IsPathRooted(relative) || !staged.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A staged input escapes its host.");
                }
                RejectLinks(staged);
                // Unity may upgrade its owned settings on import. Product/test/scene/manifest inputs must stay byte-identical.
                if (relative.StartsWith("PackageSources/", StringComparison.Ordinal) || relative.StartsWith("Assets/Tests/", StringComparison.Ordinal) ||
                    relative.StartsWith("Assets/Scenes/", StringComparison.Ordinal) || relative == "Packages/manifest.json" || relative == ".yl-development-host.json")
                {
                    using var stream = File.OpenRead(staged);
                    using var sha = SHA256.Create();
                    string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    if (!string.Equals(actual, Text(file, "sha256"), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The owned author input changed: " + relative);
                    }
                }
            }
            return profile;
        }

        private static JObject ReadOwner(string path, string kind)
        {
            var owner = JObject.Parse(File.ReadAllText(Path.Combine(path, ".yl-owner.json")));
            if (owner["schemaVersion"]?.Type != JTokenType.Integer || (long)owner["schemaVersion"] != 1 || Text(owner, "purpose") != "YLTesting.Directory" ||
                Text(owner, "kind") != kind || Text(owner, "path") != path || !Regex.IsMatch(Text(owner, "id"), "^[0-9a-f]{32}$") ||
                !Regex.IsMatch(Text(owner, "workspaceId"), "^[0-9a-f]{32}$") || !Regex.IsMatch(Text(owner, "key"), "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$"))
            {
                throw new InvalidOperationException("Invalid author directory owner.");
            }
            return owner;
        }

        private static string Text(JObject value, string field)
        {
            if (value[field]?.Type != JTokenType.String)
            {
                throw new InvalidOperationException("Expected a string identity: " + field);
            }
            return (string)value[field];
        }

        private static void RejectLinks(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Linked author paths are refused.");
                }
            }
        }
    }
}
#endif
