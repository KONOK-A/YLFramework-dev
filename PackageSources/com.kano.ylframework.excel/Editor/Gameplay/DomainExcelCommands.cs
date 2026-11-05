#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Equipment;
using YLFramework.Foundation;
using YLFramework.Mission;
using YLFramework.Skill;
using YLFramework.Resource;
using Newtonsoft.Json.Linq;
using YLFramework.Editor.Buff;
using YLFramework.Editor.Equipment;
using YLFramework.Editor.Skill;
using YLFramework.Integrations.Excel.Editor;

namespace YLFramework.Integrations.DomainExcel.Editor
{
    /// <summary>将领域定义的作者流程接到可选 Excel 后端，普通 Editor 不反向引用此程序集。</summary>
    /// <remarks>
    /// 由 DomainExcel 包依赖完整 Excel 后端；导入只修改配置定义，不创建或更新运行实例。
    /// 脚本类型可解析或资源地址合法不表示运行时已接入；任务与参数分开处理，不提供跨工作表事务。
    /// </remarks>
    public static class DomainExcelCommands
    {
        /// <summary>预览 skills 工作表的定义变更，并校验新增或更新行的技能脚本类型。</summary>
        /// <param name="root">已保存、无未保存改动的技能配表根，须在非 Play 模式使用。</param>
        /// <param name="path">包含 skills 工作表的 .xlsx 路径。</param>
        /// <returns>须由调用方确认、取消或释放的一次性计划；领域校验异常可直接抛出。</returns>
        public static ExcelImportPlan PreviewImport(SkillsConfig root, string path)
        {
            Clean(root);
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.skills, root, Folder(SkillsConfigController.GetSkillConfigDataPath(root)), "skills", data => data.parent = root, DataType<SkillConfigData>(root.typeScriptGuid));
            return ValidateRows<SkillBase>(plan, "skillClassFullName");
        }
        /// <summary>预览 buffs 工作表的定义变更，并校验新增或更新行的 Buff 脚本类型。</summary>
        /// <param name="root">已保存、无未保存改动的 Buff 配表根，须在非 Play 模式使用。</param>
        /// <param name="path">包含 buffs 工作表的 .xlsx 路径。</param>
        /// <returns>须由调用方确认、取消或释放的一次性计划；领域校验异常可直接抛出。</returns>
        /// <remarks>效果列由既有转换器解析为真实 EffectData 引用，不导入或创建全局效果库。</remarks>
        public static ExcelImportPlan PreviewImport(BuffsConfig root, string path)
        {
            Clean(root);
            var folder = BuffsConfigController.GetBuffConfigDataPath(root);
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.buffs, root, Folder(folder), "buffs", data => data.parent = root, DataType<BuffConfigData>(root.typeScriptGuid));
            return ValidateRows<BuffBase>(plan, "buffClassFullName");
        }
        /// <summary>预览 missions 工作表的定义变更，检查变更行的任务脚本及条件参数引用。</summary>
        /// <param name="root">已保存、无未保存改动且已有所需参数定义的任务配表根。</param>
        /// <param name="path">包含 missions 工作表的 .xlsx 路径。</param>
        /// <returns>须由调用方确认、取消或释放的一次性计划；领域校验异常可直接抛出。</returns>
        /// <remarks>条件必须引用根中已有参数；参数定义不随本计划导入。</remarks>
        public static ExcelImportPlan PreviewImport(MissionsConfig root, string path)
        {
            Clean(root);
            var folder = "Assets/YLFrameworkData/Mission/" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(root));
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.missions, root, Folder(folder), "missions", data => data.parent = root, DataType<MissionConfigData>(root.typeScriptGuid));
            return ValidateRows<MissionBase>(plan, "missionTypeClassFullName", root);
        }

        /// <summary>预览装备定义并同步暂存脚本 GUID，检查变更行模式、脚本或地址及跨根 ID 冲突。</summary>
        /// <param name="root">已保存、无未保存改动的装备配表根，使用本根独占的条目目录。</param>
        /// <param name="path">包含 equipments 工作表的 .xlsx 路径。</param>
        /// <returns>一次性计划；可提交时已登记提交前跨根 ID 复核，工作簿错误可阻止提交。</returns>
        /// <remarks>领域约束错误可直接抛出；资源地址检查不加载 Prefab，也不验证其装备组件。</remarks>
        public static ExcelImportPlan PreviewImport(EquipmentsConfig root, string path)
        {
            Clean(root);
            // 即使目录尚不存在，也明确选择本根的独占条目目录；预览/取消不创建目录。
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.Equipments, root,
                EquipmentsConfigController.GetEquipmentConfigDataPath(root), "equipments", data => data.parent = root,
                DataType<EquipmentConfigData>(root.typeScriptGuid), data =>
                {
                    var type = data.equipmentType == EquipmentType.Script ? AssemblyTool.GetType(data.equipmentClassFullName) : null;
                    data.equipmentClassGuid = YLFramework.Editor.Equipment.ScriptBindController.IsEquipmentType(type) ? YLFramework.Editor.Equipment.ScriptBindController.GetScriptGuid(type) : "";
                });
            try
            {
                foreach (var change in plan.Changes.Where(change => change.After != null))
                {
                    var row = JObject.Parse(change.After.Substring(change.After.IndexOf(':') + 1));
                    var mode = row["equipmentType"].ToObject<EquipmentType>();
                    if (mode == EquipmentType.Script)
                    {
                        var name = (string)row["equipmentClassFullName"];
                        if (!YLFramework.Editor.Equipment.ScriptBindController.IsEquipmentType(AssemblyTool.GetType(name)))
                            throw new InvalidOperationException($"装备 {change.Key} 脚本类型无效: {name}");
                    }
                    else if (mode == EquipmentType.Asset)
                    {
                        try
                        {
                            _ = new ResourceAddress((string)row["projectName"], (string)row["assetName"]);
                        }
                        catch (ArgumentException error)
                        {
                            throw new InvalidOperationException($"装备 {change.Key} 资源地址无效。", error);
                        }
                    }
                    else
                        throw new InvalidOperationException($"装备 {change.Key} equipmentType 无效: {mode}");
                }
                if (plan.CanCommit)
                {
                    var ids = new HashSet<int>(root.Equipments.Where(data => data).Select(data => data.id));
                    foreach (var change in plan.Changes)
                    {
                        var id = int.Parse(change.Key, CultureInfo.InvariantCulture);
                        if (change.Kind == ExcelChangeKind.Delete)
                            ids.Remove(id);
                        else
                            ids.Add(id);
                    }
                    plan.AddValidation(() =>
                    {
                        foreach (var other in EquipmentsConfigController.GetAllEquipmentsConfig().Where(config => config != root))
                            foreach (var data in other.Equipments.Where(data => data))
                                if (ids.Contains(data.id))
                                    throw new InvalidOperationException($"装备 ID {data.id} 与配表 '{other.name}' 冲突。");
                    });
                }
                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }

        // 以已保存装备根的 GUID 生成编辑器偏好键，避免资产改名导致最近路径丢失。
        private static string EquipmentWorkbookKey(EquipmentsConfig root)
        {
            if (!root || !AssetDatabase.IsMainAsset(root))
                throw new InvalidOperationException("装备配表必须是已保存的主资产。");
            return "YLFramework.Equipment.Excel." + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(root));
        }
        /// <summary>读取按装备根 GUID 记住的本机工作簿路径，不校验文件是否仍存在。</summary>
        /// <param name="root">已保存的装备主资产根。</param>
        /// <returns>最近记录的路径；没有记录时为空字符串。</returns>
        public static string GetEquipmentWorkbook(EquipmentsConfig root) => EditorPrefs.GetString(EquipmentWorkbookKey(root), "");
        // 仅在编辑器偏好中记住完整工作簿路径，不将本机路径写入配置资产。
        private static void RememberEquipmentWorkbook(EquipmentsConfig root, string path) => EditorPrefs.SetString(EquipmentWorkbookKey(root), Path.GetFullPath(path));
        /// <summary>导出装备定义到单一 equipments 工作表，成功后记住工作簿路径。</summary>
        /// <param name="root">已保存、无未保存改动且绑定数据类型有效的装备配表根。</param>
        /// <param name="path">父目录已存在的 .xlsx 输出路径，将替换整个工作簿。</param>
        public static void ExportEquipment(EquipmentsConfig root, string path)
        {
            Clean(root);
            ExcelAuthoring.ScriptableObjectToExcel(root.Equipments, path, "equipments", DataType<EquipmentConfigData>(root.typeScriptGuid));
            RememberEquipmentWorkbook(root, path);
        }
        // 在系统文件浏览器中定位当前装备根记住的工作簿。
        [MenuItem("Assets/YLFramework/Domain Excel/Open Equipment Workbook Folder")]
        private static void OpenEquipmentWorkbook() => EditorUtility.RevealInFinder(GetEquipmentWorkbook((EquipmentsConfig)SelectedRoot()));
        // 只有选中装备根且最近工作簿仍存在时才启用定位菜单。
        [MenuItem("Assets/YLFramework/Domain Excel/Open Equipment Workbook Folder", true)]
        private static bool HasEquipmentWorkbook() => SelectedRoot() is EquipmentsConfig root && File.Exists(GetEquipmentWorkbook(root));

        // 仅校验新增或更新差异中的领域脚本及可选任务参数引用，失败时释放暂存计划。
        private static ExcelImportPlan ValidateRows<T>(ExcelImportPlan plan, string classField, MissionsConfig missions = null)
        {
            try
            {
                foreach (var change in plan.Changes.Where(change => change.After != null))
                {
                    // B差异是原字段状态：conditions为JSON数组，不再是导出器生成的字符串。
                    var row = JObject.Parse(change.After.Substring(change.After.IndexOf(':') + 1));
                    RequireType<T>((string)row[classField], int.Parse(change.Key, System.Globalization.CultureInfo.InvariantCulture));
                    if (!missions)
                        continue;
                    if (!(row["conditions"] is JArray conditions))
                        throw new InvalidOperationException($"任务 {change.Key} 的条件必须为数组。");
                    foreach (var condition in conditions)
                    {
                        var name = (string)condition["param_name"];
                        if (!missions.parameters.Exists(parameter => parameter.name == name))
                            throw new InvalidOperationException($"任务 {change.Key} 的参数不存在: {name}，请先显式导入参数。");
                    }
                }
                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }

        // 参数单独工作簿/事务，不以连续两次覆盖冒充多sheet原子导入导出。
        /// <summary>将任务参数定义导出到独立工作簿的 params 工作表，不同时导出任务条目。</summary>
        /// <param name="values">待导出的任务参数定义集合。</param>
        /// <param name="path">父目录已存在的 .xlsx 输出路径，将替换整个工作簿。</param>
        public static void ExportParameters(IEnumerable<MissionParameter> values, string path) => ExcelAuthoring.ListToExcel(values, path, "params");
        /// <summary>按参数名称为键预览 params 工作表对定义列表的整表替换。</summary>
        /// <param name="path">包含 params 工作表的 .xlsx 路径。</param>
        /// <param name="values">确认后替换内容的参数定义列表。</param>
        /// <returns>须由调用方确认、取消或释放的一次性列表计划。</returns>
        /// <remarks>此重载不知所属根，不检查任务条件引用、不保存根资产，也不刷新运行时管理器。</remarks>
        public static ExcelImportPlan PreviewParameters(string path, List<MissionParameter> values) => ExcelAuthoring.PreviewList(path, values, "params");
        // 为根参数列表创建计划，并拒绝会让当前任务条件失去参数的变更。
        private static ExcelImportPlan PreviewParameters(MissionsConfig root, string path)
        {
            Clean(root);
            var plan = PreviewParameters(path, root.parameters);
            try
            {
                ValidateParameterReferences(root, plan);
                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }
        // 计算应用差异后的参数名集合，拒绝当前任务仍引用将被移除的名称。
        private static void ValidateParameterReferences(MissionsConfig root, ExcelImportPlan plan)
        {
            var names = new HashSet<string>(root.parameters.Select(parameter => parameter.name), StringComparer.Ordinal);
            foreach (var change in plan.Changes)
            {
                if (change.Kind == ExcelChangeKind.Delete)
                    names.Remove(change.Key);
                else
                    names.Add(change.Key);
            }
            foreach (var data in root.missions)
                foreach (var condition in data.conditions)
                    if (!names.Contains(condition.paramName))
                        throw new InvalidOperationException($"仍有任务引用参数: {condition.paramName}，请用普通作者工具改名/同步条件后再导入。");
        }

        // 为选中的领域根显示条目导入预览，并在成功后刷新作者缓存及装备最近路径。
        [MenuItem("Assets/YLFramework/Domain Excel/Preview Import Entries")]
        private static void ImportSelected()
        {
            var root = SelectedRoot();
            if (!root)
                throw new InvalidOperationException("请选择YL Skill/Buff/Mission/Equipment配表或条目。");
            var path = EditorUtility.OpenFilePanel("预览领域Excel导入", string.Empty, "xlsx");
            if (path.Length == 0)
                return;
            var plan = root is SkillsConfig skill ? PreviewImport(skill, path) : root is BuffsConfig buff ? PreviewImport(buff, path) : root is EquipmentsConfig equipment ? PreviewImport(equipment, path) : PreviewImport((MissionsConfig)root, path);
            var window = ExcelAuthoringCommands.ShowPreview(plan);
            window.Applied += () =>
            {
                Invalidate(root);
                if (root is EquipmentsConfig applied)
                    RememberEquipmentWorkbook(applied, path);
            };
        }
        // 按领域选择对应工作表导出定义条目，不把任务参数并入同次导出。
        [MenuItem("Assets/YLFramework/Domain Excel/Export Entries")]
        private static void ExportSelected()
        {
            var root = SelectedRoot();
            if (!root)
                throw new InvalidOperationException("请选择YL Skill/Buff/Mission/Equipment配表或条目。");
            var path = SavePath(root.name);
            if (path.Length == 0)
                return;
            if (root is SkillsConfig skill)
                ExcelAuthoring.ScriptableObjectToExcel(skill.skills, path, "skills", DataType<SkillConfigData>(skill.typeScriptGuid));
            else if (root is BuffsConfig buff)
                ExcelAuthoring.ScriptableObjectToExcel(buff.buffs, path, "buffs", DataType<BuffConfigData>(buff.typeScriptGuid));
            else if (root is MissionsConfig mission)
                ExcelAuthoring.ScriptableObjectToExcel(mission.missions, path, "missions", DataType<MissionConfigData>(mission.typeScriptGuid));
            else if (root is EquipmentsConfig equipment)
                ExportEquipment(equipment, path);
        }
        // 为任务根创建独立参数预览窗口，交由窗口确认保存。
        [MenuItem("Assets/YLFramework/Domain Excel/Preview Import Mission Parameters")]
        private static void ImportParameters()
        {
            var root = SelectedRoot() as MissionsConfig;
            if (!root)
                throw new InvalidOperationException("请选择YL Mission配表。");
            var path = EditorUtility.OpenFilePanel("预览params工作表", string.Empty, "xlsx");
            if (path.Length == 0)
                return;
            var window = ScriptableObject.CreateInstance<ParameterPreview>();
            window.Root = root;
            window.Plan = PreviewParameters(root, path);
            window.titleContent = new GUIContent("Mission Parameters Preview");
            window.ShowUtility();
        }
        // 将所选任务根的参数定义导出到单独工作簿。
        [MenuItem("Assets/YLFramework/Domain Excel/Export Mission Parameters")]
        private static void ExportMissionParameters()
        {
            var root = SelectedRoot() as MissionsConfig;
            if (!root)
                throw new InvalidOperationException("请选择YL Mission配表。");
            var path = SavePath(root.name + ".params");
            if (path.Length != 0)
                ExportParameters(root.parameters, path);
        }

        // 从选中的领域根或条目的 parent 解析作者操作目标。
        private static ScriptableObject SelectedRoot() => Selection.activeObject switch
        {
            SkillsConfig root => root,
            BuffsConfig root => root,
            MissionsConfig root => root,
            EquipmentsConfig root => root,
            SkillConfigData data => data.parent,
            BuffConfigData data => data.parent,
            MissionConfigData data => data.parent,
            EquipmentConfigData data => data.parent,
            _ => null
        };
        // 拒绝未保存或仍有脏改动的根，以及正在或即将进入 Play 的作者操作。
        private static void Clean(ScriptableObject root)
        {
            if (!root || !EditorUtility.IsPersistent(root) || EditorUtility.IsDirty(root))
                throw new InvalidOperationException("领域配表必须是已保存且无未保存改动的资产。");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("退出Play后才能导入领域配表。");
        }
        // 已有领域目录使用独立条目模式，目录不存在则选择后端的根内子资产模式。
        private static string Folder(string path) => AssetDatabase.IsValidFolder(path) ? path : null;
        // 将可选脚本 GUID 解析为具体数据类型，未绑定时使用领域默认类型。
        private static Type DataType<T>(string guid) where T : ScriptableObject
        {
            if (string.IsNullOrEmpty(guid))
                return typeof(T);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
            var type = script ? script.GetClass() : null;
            if (type == null || type.IsAbstract || !typeof(T).IsAssignableFrom(type))
                throw new InvalidOperationException($"领域配表数据类型绑定失效: {guid}");
            return type;
        }
        // 要求领域脚本可解析、可赋给目标基类型且具备公开无参构造，不在此创建实例。
        private static void RequireType<T>(string name, int id)
        {
            var type = string.IsNullOrEmpty(name) ? null : AssemblyTool.GetType(name);
            if (type == null || type.IsAbstract || !typeof(T).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException($"领域脚本类型无效: ID={id}, class={name}");
        }
        // 选择整本工作簿的输出路径，并提示任务参数须独立导出以免覆盖条目表。
        private static string SavePath(string name)
        {
            var path = EditorUtility.SaveFilePanel("导出领域工作簿", string.Empty, name, "xlsx");
            if (path.Length != 0 && File.Exists(path) && !EditorUtility.DisplayDialog("覆盖整个工作簿", "旧sheet及格式将被替换。Mission参数请导出为独立.params.xlsx文件。\n" + path, "覆盖", "取消"))
                return string.Empty;
            return path;
        }
        // 仅清除已应用领域根的作者查询缓存，不重建管理器的运行实例。
        private static void Invalidate(ScriptableObject root)
        {
            if (root is SkillsConfig skill)
                skill.ClearCache();
            else if (root is BuffsConfig buff)
                buff.ClearCache();
            else if (root is MissionsConfig mission)
                mission.ClearCache();
            else if (root is EquipmentsConfig equipment)
                equipment.ClearCache();
        }

        /// <summary>独立确认任务参数定义变更，并在提交前复核当前任务引用的作者窗口。</summary>
        /// <remarks>列表计划成功后再保存根资产，后续保存不在计划的提交或回滚范围内。</remarks>
        private sealed class ParameterPreview : EditorWindow
        {
            internal MissionsConfig Root;
            internal ExcelImportPlan Plan;
            private Vector2 _scroll;
            private string _error;
            // 展示参数差异，确认时复核根和条件引用，再提交列表并保存根资产。
            private void OnGUI()
            {
                if (Plan == null)
                {
                    EditorGUILayout.HelpBox("请关闭并重新预览。", MessageType.Info);
                    return;
                }
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var error in Plan.Errors)
                    EditorGUILayout.HelpBox(error, MessageType.Error);
                if (_error != null)
                    EditorGUILayout.HelpBox(_error, MessageType.Error);
                foreach (var change in Plan.Changes)
                {
                    EditorGUILayout.LabelField(change.Kind + ": " + change.Key, EditorStyles.boldLabel);
                    EditorGUILayout.SelectableLabel((change.Before ?? "<new>") + "\n=>\n" + (change.After ?? "<deleted>"), EditorStyles.textArea, GUILayout.MinHeight(60));
                }
                EditorGUILayout.EndScrollView();
                using (new EditorGUI.DisabledScope(!Plan.CanCommit || _error != null))
                    if (GUILayout.Button("确认导入参数") && EditorUtility.DisplayDialog("确认参数差异", "仅导入所列参数差异，不导入任务。", "导入", "取消"))
                    {
                        try
                        {
                            // 再查当前任务引用，不能让预览期间新增的条件引用变成悬空参数。
                            Clean(Root);
                            ValidateParameterReferences(Root, Plan);
                            if (Plan.Commit(true) == ExcelCommitResult.Applied)
                            {
                                EditorUtility.SetDirty(Root);
                                AssetDatabase.SaveAssetIfDirty(Root);
                                Root.ClearCache();
                                Close();
                            }
                        }
                        catch (Exception error)
                        {
                            _error = error.Message;
                        }
                    }
                if (GUILayout.Button("取消"))
                    Close();
            }
            // 关闭或停用窗口时释放计划，取消尚未处理的参数导入。
            private void OnDisable()
            {
                Plan?.Dispose();
                Plan = null;
            }
        }
    }
}
#endif
