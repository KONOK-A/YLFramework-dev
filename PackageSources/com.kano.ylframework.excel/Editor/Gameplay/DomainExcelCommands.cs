#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Equipment;
using YLFramework.Foundation;
using YLFramework.Mission;
using YLFramework.Skill;
using YLFramework.Resource;
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
        /// <summary>预览 skills 工作表，对全部类型化暂存行校验技能脚本，未变化的非法行也不能提交。</summary>
        /// <param name="root">已保存、无未保存改动的技能配表根，须在非 Play 模式使用。</param>
        /// <param name="path">包含 skills 工作表的 .xlsx 路径。</param>
        /// <returns>须由调用方确认、取消或释放的一次性计划；领域校验异常可直接抛出。</returns>
        public static ExcelImportPlan PreviewImport(SkillsConfig root, string path)
        {
            Clean(root);
            var rows = new List<SkillConfigData>();
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.skills, root, Folder(SkillsConfigController.GetSkillConfigDataPath(root)),
                "skills", data => data.parent = root, DataType<SkillConfigData>(root.typeScriptGuid), data =>
                {
                    RequireType<SkillBase>(data.skillClassFullName, data.id);
                    rows.Add(data);
                });
            try
            {
                if (plan.CanCommit)
                    plan.AddValidation(() =>
                    {
                        Clean(root);
                        foreach (var data in rows)
                            RequireType<SkillBase>(data.skillClassFullName, data.id);
                    });
                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }
        /// <summary>预览 buffs 工作表，对全部暂存行校验 Buff 脚本，并在提交前复核真实效果资产。</summary>
        /// <param name="root">已保存、无未保存改动的 Buff 配表根，须在非 Play 模式使用。</param>
        /// <param name="path">包含 buffs 工作表的 .xlsx 路径。</param>
        /// <returns>须由调用方确认、取消或释放的一次性计划；领域校验异常可直接抛出。</returns>
        /// <remarks>效果列由既有转换器解析为真实 EffectData 引用，不导入或创建全局效果库。</remarks>
        public static ExcelImportPlan PreviewImport(BuffsConfig root, string path)
        {
            Clean(root);
            var folder = BuffsConfigController.GetBuffConfigDataPath(root);
            var rows = new Dictionary<BuffConfigData, int[]>();
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.buffs, root, Folder(folder), "buffs", data => data.parent = root,
                DataType<BuffConfigData>(root.typeScriptGuid), data =>
                {
                    RequireType<BuffBase>(data.buffClassFullName, data.id);
                    if (data.effects == null || data.effects.Any(effect => !effect))
                        throw new InvalidOperationException($"Buff {data.id} 的效果引用无效。");
                    rows.Add(data, data.effects.Select(effect => effect.id).ToArray());
                });
            try
            {
                if (plan.CanCommit)
                    plan.AddValidation(() =>
                    {
                        Clean(root);
                        foreach (var entry in rows)
                        {
                            RequireType<BuffBase>(entry.Key.buffClassFullName, entry.Key.id);
                            if (entry.Key.effects.Any(effect => !effect) || !entry.Key.effects.Select(effect => effect.id).SequenceEqual(entry.Value))
                                throw new InvalidOperationException($"Buff {entry.Key.id} 的效果资产在预览后变化。");
                        }
                    });
                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }
        /// <summary>预览 missions 工作表的全部暂存行，并在预览及提交前复核当前参数定义。</summary>
        /// <param name="root">已保存、无未保存改动且已有所需参数定义的任务配表根。</param>
        /// <param name="path">包含 missions 工作表的 .xlsx 路径。</param>
        /// <returns>须由调用方确认、取消或释放的一次性计划；领域校验异常可直接抛出。</returns>
        /// <remarks>条件必须引用根中已有参数；参数定义不随本计划导入。</remarks>
        public static ExcelImportPlan PreviewImport(MissionsConfig root, string path)
        {
            Clean(root);
            var folder = "Assets/YLFrameworkData/Mission/" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(root));
            var rows = new List<MissionConfigData>();
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.missions, root, Folder(folder), "missions", data => data.parent = root,
                DataType<MissionConfigData>(root.typeScriptGuid), data =>
                {
                    ValidateMission(root, data);
                    rows.Add(data);
                });
            try
            {
                if (plan.CanCommit)
                    plan.AddValidation(() =>
                    {
                        Clean(root);
                        foreach (var data in rows)
                            ValidateMission(root, data);
                    });
                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }

        /// <summary>预览全部类型化装备行并同步暂存脚本 GUID，在提交前复核外部资产与跨根 ID。</summary>
        /// <param name="root">已保存、无未保存改动的装备配表根，使用本根独占的条目目录。</param>
        /// <param name="path">包含 equipments 工作表的 .xlsx 路径。</param>
        /// <returns>一次性计划；可提交时已登记提交前跨根 ID 复核，工作簿错误可阻止提交。</returns>
        /// <remarks>领域约束错误可直接抛出；资源地址检查不加载 Prefab，也不验证其装备组件。</remarks>
        public static ExcelImportPlan PreviewImport(EquipmentsConfig root, string path)
        {
            Clean(root);
            var rows = new List<EquipmentConfigData>();
            // 即使目录尚不存在，也明确选择本根的独占条目目录；预览/取消不创建目录。
            var plan = ExcelAuthoring.PreviewScriptableObjects(path, root.Equipments, root,
                EquipmentsConfigController.GetEquipmentConfigDataPath(root), "equipments", data => data.parent = root,
                DataType<EquipmentConfigData>(root.typeScriptGuid), data =>
                {
                    var type = data.equipmentType == EquipmentType.Script ? AssemblyTool.GetType(data.equipmentClassFullName) : null;
                    data.equipmentClassGuid = YLFramework.Editor.Equipment.ScriptBindController.IsEquipmentType(type) ? YLFramework.Editor.Equipment.ScriptBindController.GetScriptGuid(type) : "";
                    ValidateEquipment(data);
                    rows.Add(data);
                });
            try
            {
                if (plan.CanCommit)
                {
                    var ids = new HashSet<int>(rows.Select(data => data.id));
                    plan.AddValidation(() =>
                    {
                        Clean(root);
                        foreach (var data in rows)
                            ValidateEquipment(data);
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

        private static void ValidateMission(MissionsConfig root, MissionConfigData data)
        {
            RequireType<MissionBase>(data.missionTypeClassFullName, data.id);
            if (data.conditions == null)
                throw new InvalidOperationException($"任务 {data.id} 的条件必须为数组。");
            foreach (var condition in data.conditions)
                if (condition == null || !root.parameters.Exists(parameter => parameter != null && parameter.name == condition.paramName))
                    throw new InvalidOperationException($"任务 {data.id} 的参数不存在: {condition?.paramName}，请先显式导入参数。");
        }

        private static void ValidateEquipment(EquipmentConfigData data)
        {
            if (data.equipmentType == EquipmentType.Script)
            {
                if (!YLFramework.Editor.Equipment.ScriptBindController.IsEquipmentType(AssemblyTool.GetType(data.equipmentClassFullName)))
                    throw new InvalidOperationException($"装备 {data.id} 脚本类型无效: {data.equipmentClassFullName}");
            }
            else if (data.equipmentType == EquipmentType.Asset)
                _ = new ResourceAddress(data.projectName, data.assetName);
            else
                throw new InvalidOperationException($"装备 {data.id} equipmentType 无效: {data.equipmentType}");
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
                if (plan.CanCommit)
                    plan.AddValidation(() =>
                    {
                        Clean(root);
                        ValidateParameterReferences(root, plan);
                    });
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
        // 参数复用公共预览，计划负责两阶段只读校验；成功后才保存根并刷新缓存。
        [MenuItem("Assets/YLFramework/Domain Excel/Preview Import Mission Parameters")]
        private static void ImportParameters()
        {
            var root = SelectedRoot() as MissionsConfig;
            if (!root)
                throw new InvalidOperationException("请选择YL Mission配表。");
            var path = EditorUtility.OpenFilePanel("预览params工作表", string.Empty, "xlsx");
            if (path.Length == 0)
                return;
            var window = ExcelAuthoringCommands.ShowPreview(PreviewParameters(root, path));
            window.Applied += () =>
            {
                EditorUtility.SetDirty(root);
                AssetDatabase.SaveAssetIfDirty(root);
                root.ClearCache();
            };
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

    }
}
#endif
