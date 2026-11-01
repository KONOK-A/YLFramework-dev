using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using YLFramework.Core;

namespace YLFramework.Editor.Core
{
    /// <summary>生成独立 YL Module 源码和程序集定义的编辑器窗口，不修改资源项目或场景。</summary>
    /// <remarks>仅向新的模块目录写入文件；生成脚本不等于启动模块，运行前仍需由应用完成资源接入。</remarks>
    public class CreateModuleCommand : EditorWindow
    {
        /// <summary>在 Project 视图的 Assets/Create 下打开生成窗口的菜单路径。</summary>
        public const string MenuPath = "Assets/Create/YLFramework/Core/Module";
        /// <summary>新模块目录的父路径；每个模块在其下生成源码和独立 asmdef。</summary>
        public const string OutputRoot = "Assets/YLFramework/Examples/Core/Modules";
        /// <summary>生成源码及其程序集定义使用的命名空间。</summary>
        public const string ExampleNamespace = "YLFramework.Core.Examples";

        private const string ModuleTemplate = @"
using YLFramework.Core;

namespace YLFramework.Core.Examples
{
    /// <summary>业务模块入口；资源会话由应用先行启动，本脚本不负责初始化全局资源服务。</summary>
    public class {ModuleName} : global::YLFramework.Core.SingletonModule<{ModuleName}>
    {
        // ProjectName 是正式 YL Resource ProjectId，不是外部项目名或 BundleName。
        // 应用先通过 ResourceConfiguration.Create 和 ResourceStartup 显式启动，选择 bindCore:true，
        // StartAsync 成功后再启动本 Module；Module 不自行初始化资源或覆盖全局 Provider。
        // 完整配置、同一业务的 Simulation/Bundle 和 Core/Audio 可运行例见 Samples/ResourceStartup。
        // Audio 需要时使用同一 startup.AudioProviders[{ProjectName}]，未选择Audio则不装配。
        // 退出先关闭自己的 Module/播放并归还 Lease/Scene 等责任，再调用 startup.StopAsync。
        // Busy 时保持原会话并在真实 owner 归还后重试，不关闭外部会话或强制释放他人责任。
        /// <summary>资源项目标识，须与应用启动的资源配置保持一致。</summary>
        public override string ProjectName => {ProjectName};
        /// <summary>是否作为基础模块；基础模块由 ModuleManager.Shutdown 统一结束。</summary>
        public override bool IsBaseModule => {BaseModule};
        /// <summary>模块启动时自动加载的默认场景标识；空字符串表示不自动加载场景。</summary>
        public override string DefaultStartUpScene => {DefaultStartUpScene};

        /// <summary>模块资源和默认场景准备就绪后调用，在此开始本模块的业务。</summary>
        public override void OnStart()
        {
            base.OnStart();
            global::UnityEngine.Debug.Log(""OnStart:"" + this.ModuleName);
        }
    }
}
";

        private string _moduleName = "YLExampleModule";
        private string _projectName = "YLExampleModule";
        private bool _isBaseModule;
        private string _defaultStartUpScene = "";

        /// <summary>通过创建菜单打开固定尺寸的模块参数窗口。</summary>
        [MenuItem(MenuPath)]
        private static void CreateModuleItem()
        {
            var window = GetWindowWithRect<CreateModuleCommand>(new Rect(0, 0, 420, 280), true, "YL Core Module");
            window.Show();
        }

        /// <summary>绘制参数并响应生成按钮，成功后刷新 AssetDatabase 并关闭窗口。</summary>
        private void OnGUI()
        {
            // IMGUI 会随界面事件反复调用这里；只有按钮点击才进入生成分支。
            EditorGUILayout.LabelField("Output", OutputRoot);
            _moduleName = EditorGUILayout.TextField(new GUIContent("ModuleName", "C# class name"), _moduleName);
            _projectName = EditorGUILayout.TextField(new GUIContent("ProjectName", "YL Resource ProjectId；须由应用显式初始化并绑定 Core Provider"), _projectName);
            _isBaseModule = EditorGUILayout.Toggle(new GUIContent("IsBaseModule", "基础模块不能通过普通 Close 关闭；使用 ModuleManager.Shutdown 统一结束"), _isBaseModule);
            _defaultStartUpScene = EditorGUILayout.TextField("DefaultStartUpScene", _defaultStartUpScene);
            EditorGUILayout.HelpBox("生成独立 YL Module 和 Runtime asmdef。命名加载前由应用初始化 YL Resource 并绑定 Core Provider；生成器不修改资源项目或场景。已有目录不会被覆盖。", MessageType.Info);
            if (!GUILayout.Button("Create YL Module"))
            {
                return;
            }

            try
            {
                CreateModule(_moduleName, _projectName, _isBaseModule, _defaultStartUpScene);
                AssetDatabase.Refresh();
                Close();
            }
            catch (ArgumentException exception)
            {
                ShowNotification(new GUIContent(exception.Message));
            }
            catch (IOException exception)
            {
                ShowNotification(new GUIContent(exception.Message));
            }
        }

        /// <summary>在新的模块目录中生成脚本和仅引用 YLFramework.Runtime 的独立程序集定义。</summary>
        /// <param name="moduleName">模块类名，须为非关键字 ASCII 标识符，且不能与生成成员重名。</param>
        /// <param name="projectName">正式 YL Resource 项目标识，不能为 null、空字符串或全空白。</param>
        /// <param name="baseModule">是否生成基础模块；该配置影响运行时的关闭方式。</param>
        /// <param name="defaultStartUpScene">默认场景标识；为 null 或空字符串时生成 string.Empty。</param>
        /// <remarks>
        /// 此方法只写文件，不刷新 AssetDatabase 或启动模块；菜单窗口在生成成功后负责刷新。
        /// 已有输出或孤立 .meta 不会被接管；写入途中失败可能留下部分文件，应先检查残留内容再重试。
        /// </remarks>
        /// <exception cref="ArgumentException">类名不合法、与生成成员重名，或资源项目标识为空白。</exception>
        /// <exception cref="IOException">目标路径已被占用，或创建目录、写入文件时发生 I/O 错误。</exception>
        public static void CreateModule(string moduleName, string projectName, bool baseModule, string defaultStartUpScene)
        {
            // 先在内存准备源码和程序集定义，全部就绪后再写磁盘，避免输入错误时留下输出。
            if (string.IsNullOrWhiteSpace(projectName))
            {
                throw new ArgumentException("ProjectName must not be empty.", nameof(projectName));
            }

            // 类名还用作目录名，限制完整标识符也阻止路径分隔符混入输出路径。
            if (string.IsNullOrEmpty(moduleName) || !Regex.IsMatch(moduleName, @"\A[A-Za-z_][A-Za-z0-9_]*\z") ||
                Regex.IsMatch(moduleName, @"\A(__arglist|__makeref|__reftype|__refvalue|abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|virtual|void|volatile|while)\z"))
            {
                throw new ArgumentException("ModuleName must be a non-keyword ASCII C# identifier.", nameof(moduleName));
            }

            // 类中不能声明与类名同名的普通成员；模板已有这四个成员，所以语法合法的类名仍可能与生成内容冲突。
            if (moduleName == "ProjectName" || moduleName == "IsBaseModule" || moduleName == "DefaultStartUpScene" || moduleName == "OnStart")
            {
                throw new ArgumentException("ModuleName conflicts with a generated member.", nameof(moduleName));
            }

            // 孤立 .meta 也视为占用，避免把旧的 Unity 资源身份当作空目录接管。
            string directory = OutputRoot + "/" + moduleName;
            if (Directory.Exists(directory) || File.Exists(directory) || File.Exists(directory + ".meta"))
            {
                throw new IOException("YL module output already exists: " + directory);
            }

            // 类名填作标识符，项目/场景名则转义为字符串字面量，例如 A"B 生成 "A\"B"。
            // 只扫描原模板一次，回调返回的用户数据不再参与占位符展开。
            string content = Regex.Replace(ModuleTemplate, @"\{(ModuleName|ProjectName|BaseModule|DefaultStartUpScene)\}", match =>
            {
                switch (match.Groups[1].Value)
                {
                    case "ModuleName":
                        return moduleName;
                    case "ProjectName":
                        return JsonConvert.SerializeObject(projectName);
                    case "BaseModule":
                        return baseModule ? "true" : "false";
                    default:
                        return string.IsNullOrEmpty(defaultStartUpScene) ? "string.Empty" : JsonConvert.SerializeObject(defaultStartUpScene);
                }
            });
            // namespace 组织类型名，asmdef 划分编译单元；这里显式引用 Runtime 以使用模块基类。
            string assembly = JsonConvert.SerializeObject(new
            {
                name = "YLFramework.Core.Examples." + moduleName,
                rootNamespace = ExampleNamespace,
                references = new[] { "YLFramework.Runtime" },
                // Assembly-CSharp 等预定义程序集不自动引用它；按需在业务 asmdef 中显式引用。
                autoReferenced = false
            }, Formatting.Indented);

            // 早期检查后路径仍可能被占用，CreateNew 在实际写入时拒绝覆盖。
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(directory + "/" + moduleName + ".cs", FileMode.CreateNew))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }

            // .asmdef 写入失败不会撤销已写的 .cs，异常向调用方传播。
            using (var stream = new FileStream(directory + "/" + moduleName + ".asmdef", FileMode.CreateNew))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(assembly);
            }
        }
    }
}
