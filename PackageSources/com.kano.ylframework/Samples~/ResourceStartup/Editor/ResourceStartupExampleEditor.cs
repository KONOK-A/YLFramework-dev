using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Samples.Resource.Editor
{
    /// <summary>样例的显式配置、构建、内置部署与运行入口，不在绘制 Inspector 时自动执行业务。</summary>
    [CustomEditor(typeof(ResourceStartupExample))]
    public sealed class ResourceStartupExampleEditor : UnityEditor.Editor
    {
        private string _developmentKeyEnvironmentVariable = ResourceEditorOperations.ContentKeyEnvironmentVariable;

        /// <summary>展示样例状态与操作按钮，仅在用户选择对应操作时执行构建、部署或运行。</summary>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var example = (ResourceStartupExample)target;
            EditorGUILayout.LabelField("Result", example.Result);
            EditorGUILayout.HelpBox("在生成的 Project.asset 选择 ContentEncryption=None 或 Aes256GcmHkdfSegmentedV1；加密时填写非秘密 ContentKeyId 并显式保存，再构建/发布。" +
                "\n生产环境由应用注入 ContentKeyProvider。下面仅供非生产开发：进程环境变量须为32字节key的Base64；这里只装配provider，不保存或显示key。" +
                "\nBuild选中确切发布并重置UseBuiltIn，请明确切换Mode=Bundles。内置预检/部署不需key；部署后保存自己的Scene。进入Play或域重载后重新注入，再用原 Run Selected Mode。", MessageType.Info);
            _developmentKeyEnvironmentVariable = EditorGUILayout.TextField("Development Key Variable", _developmentKeyEnvironmentVariable);
            if (GUILayout.Button("Inject Development Environment Key Provider"))
            {
                UseDevelopmentContentKeyFromEnvironment(example, _developmentKeyEnvironmentVariable);
            }

            EditorGUILayout.LabelField("Content Key Provider", example.ContentKeyProvider == null ? "Not injected" : "Injected (runtime only)");
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Build And Publish Example Bundle"))
                {
                    Build(example);
                }

                if (GUILayout.Button("Preflight Selected Built-In Release"))
                {
                    Debug.Log(PreflightBuiltIn(example).ToString());
                }

                if (GUILayout.Button("Deploy Selected Release To Built-In"))
                {
                    var deployment = DeployBuiltIn(example);
                    Debug.Log("Built-in=" + deployment.RelativeDirectory + " / repeat=" + deployment.IsRepeat + "; select Bundles and save the example scene explicitly.");
                }
                if (GUILayout.Button("Recover Built-In Deployment"))
                {
                    var recovered = ResourceBuiltInDeployer.Recover(Project(example.projectId), Target(example.platform));
                    Debug.Log(recovered == null ? "No installed deployment to confirm; a pending private stage was aborted if present. Preflight before deploying again." :
                        "Confirmed built-in=" + recovered.RelativeDirectory);
                }
            }
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Run Selected Mode"))
                {
                    example.Begin(CreateConfiguration(example));
                }

                if (GUILayout.Button("Stop / Retry Shutdown"))
                {
                    example.StopExample();
                }
            }
        }

        /// <summary>查找唯一匹配稳定标识的正式项目，拒绝缺失或重复身份以免操作其他项目。</summary>
        /// <param name="id">样例明确选择的项目标识。</param>
        /// <returns>借读的项目配置资产，不转移其销毁责任。</returns>
        private static ResourceProject Project(string id)
        {
            var matches = AssetDatabase.FindAssets("t:ResourceProject")
                .Select(x => AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(x)))
                .Where(x => x && x.projectId == id).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException("Exactly one formal ResourceProject must match the sample ProjectId.");
            }

            return matches[0];
        }

        /// <summary>按样例显式模式装配模拟或 Bundle 配置，不启动会话，也不在失败时切换来源。</summary>
        /// <param name="example">提供模式、项目及确切发布选择的样例组件。</param>
        /// <returns>模拟模式下的正式项目配置，或样例所选快照的 Bundle 配置。</returns>
        public static ResourceConfiguration CreateConfiguration(ResourceStartupExample example) =>
            example.mode == ResourceLoadMode.EditorSimulation
                ? ResourceEditorSimulation.CreateConfiguration(example.mode, new[] { Project(example.projectId) })
                : example.CreateBundleConfiguration();

        /// <summary>仅供非生产开发，显式绑定当前Project/KeyId与进程变量名；Play/域重载后重新调用，不持久化密钥。</summary>
        /// <param name="example">接收运行时密钥提供者的样例组件，其项目须唯一匹配正式定义。</param>
        /// <param name="environmentVariable">含规范 Base64 编码的 32 字节密钥的进程环境变量名，不是密钥本身。</param>
        public static void UseDevelopmentContentKeyFromEnvironment(ResourceStartupExample example, string environmentVariable)
        {
            var project = Project(example.projectId);
            example.ContentKeyProvider = ResourceEditorOperations.ContentKeyFromEnvironment(project.projectId, project.contentKeyId, environmentVariable);
        }

        /// <summary>按当前活动平台构建并本地发布所选项目，将样例指向成功发布的确切非内置快照。</summary>
        /// <param name="example">提供项目、应用兼容标识及可选内容密钥来源，并接收发布选择的样例组件。</param>
        /// <returns>本次成功发布的版本；未确认发布成功时抛出构建或发布阶段的错误。</returns>
        /// <remarks>不切换或同步样例的 mode、platform；调用方须确保平台匹配并显式保存场景，构建发布目录不会在停止样例时删除。</remarks>
        public static ResourceRelease Build(ResourceStartupExample example)
        {
            var report = ResourceEditorOperations.BuildAndPublish(Project(example.projectId), example.appCompatibility, provider: example.ContentKeyProvider);
            var release = report.Release ?? throw new InvalidOperationException(report.Summary, report.BuildError ?? report.PublicationError);
            Undo.RecordObject(example, "Select explicit sample release");
            example.snapshotDirectory = release.DirectoryPath;
            example.catalogDigest = release.Catalog.CatalogDigest;
            example.useBuiltIn = false;
            EditorUtility.SetDirty(example);
            EditorSceneManager.MarkSceneDirty(example.gameObject.scene);
            return release;
        }

        /// <summary>将所选平台文本转换为构建目标并校验文本往返一致，供确切发布定位使用。</summary>
        /// <param name="platform">样例保存的平台文本。</param>
        /// <returns>与输入文本往返一致的构建目标。</returns>
        private static BuildTarget Target(string platform) => Enum.TryParse<BuildTarget>(platform, out var target) && target.ToString() == platform
            ? target : throw new InvalidOperationException("Select an explicit supported build platform.");

        /// <summary>只读预检样例明确选择的本地发布及其内置部署目标，不取密钥或执行部署。</summary>
        /// <param name="example">以项目、平台、兼容标识和摘要共同选择确切发布的样例组件。</param>
        /// <returns>所选版本的部署计划，不代表内置文件已经写入。</returns>
        public static ResourceBuiltInPlan PreflightBuiltIn(ResourceStartupExample example) => ResourceBuiltInDeployer.Preflight(
            ResourcePublisher.ReadVersion(Project(example.projectId), Target(example.platform), example.appCompatibility, example.catalogDigest));

        /// <summary>只部署Inspector已选择的确切发布；不保存用户Scene、不启动或切换运行会话。</summary>
        /// <param name="example">提供发布身份并在部署成功后启用 useBuiltIn 的样例组件。</param>
        /// <returns>正式部署器确认的内置部署结果，不代表 Player 已构建或资源会话已启动。</returns>
        public static ResourceBuiltInDeployment DeployBuiltIn(ResourceStartupExample example)
        {
            var result = ResourceBuiltInDeployer.Deploy(PreflightBuiltIn(example));
            Undo.RecordObject(example, "Use explicitly deployed built-in resources");
            example.useBuiltIn = true;
            EditorUtility.SetDirty(example);
            EditorSceneManager.MarkSceneDirty(example.gameObject.scene);
            return result;
        }

        /// <summary>将用户的创建菜单操作转交独立样例创建入口。</summary>
        [MenuItem("YLFramework/Samples/Create Resource Startup Example")]
        private static void CreateMenu() => Create();

        /// <summary>用户显式创建独立示例。只新建GUID，不改已有Scene/资源；脏场景拒绝而非自动保存。</summary>
        /// <returns>本次新建的样例根目录，包含内容资产、Project.asset 与 Example.unity。</returns>
        /// <remarks>
        /// 创建后切换到新场景，但不构建、发布、部署或启动会话；运行前仍需明确选择模式与配置。
        /// 生成目录由使用方管理，停止运行只结算样例运行时责任，不删除这些资产。
        /// </remarks>
        public static string Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Create the example in Edit Mode.");
            }

            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("Save or discard dirty scenes before creating the sample.");
                }
            }

            var folder = AssetDatabase.GenerateUniqueAssetPath("Assets/YLResourceStartupExample");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            AssetDatabase.CreateFolder(folder, "Content");
            var content = folder + "/Content";
            File.WriteAllText(content + "/Message.txt", "Hello from ResourceStartup", new UTF8Encoding(false));
            WriteTone(content + "/Tone.wav");
            AssetDatabase.ImportAsset(content + "/Message.txt", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(content + "/Tone.wav", ImportAssetOptions.ForceSynchronousImport);
            var data = CreateInstance<ResourceStartupExampleData>();
            data.text = "Explicit compiled sample type";
            AssetDatabase.CreateAsset(data, content + "/Data.asset");
            var project = CreateInstance<ResourceProject>();
            project.projectId = "startup-example-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            project.displayName = "Resource Startup Example";
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(content) });
            project.groups.Add(new ResourceGroup
            {
                id = "startup-content",
                packing = ResourcePacking.Together,
                selections = new List<ResourceSelection>
                {
                    new() { guid = AssetDatabase.AssetPathToGUID(content + "/Message.txt"), address = "message" },
                    new() { guid = AssetDatabase.AssetPathToGUID(content + "/Data.asset"), address = "data" },
                    new() { guid = AssetDatabase.AssetPathToGUID(content + "/Tone.wav"), address = "tone" }
                }
            });
            AssetDatabase.CreateAsset(project, folder + "/Project.asset");
            AssetDatabase.SaveAssetIfDirty(project);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Resource Startup Example", typeof(AudioListener));
            var example = go.AddComponent<ResourceStartupExample>();
            example.projectId = project.projectId;
            example.platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            EditorSceneManager.SaveScene(scene, folder + "/Example.unity");
            Selection.activeObject = example;
            return folder;
        }

        /// <summary>生成一秒单声道 PCM 测试音，并在退出时关闭本次输出流。</summary>
        /// <param name="path">本次新建内容目录中的 WAV 输出路径。</param>
        private static void WriteTone(string path)
        {
            const int Rate = 8000;
            using var output = new BinaryWriter(File.Create(path));
            output.Write(Encoding.ASCII.GetBytes("RIFF"));
            output.Write(36 + Rate * 2);
            output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            output.Write(16);
            output.Write((short)1);
            output.Write((short)1);
            output.Write(Rate);
            output.Write(Rate * 2);
            output.Write((short)2);
            output.Write((short)16);
            output.Write(Encoding.ASCII.GetBytes("data"));
            output.Write(Rate * 2);
            for (var i = 0; i < Rate; i++)
            {
                output.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / Rate) * 4096));
            }
        }
    }
}
