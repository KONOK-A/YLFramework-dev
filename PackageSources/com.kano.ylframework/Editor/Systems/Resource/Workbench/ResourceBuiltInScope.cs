using System;
using System.IO;
using System.Text;
using YLFramework.Resource;

namespace YLFramework.Editor.Resource
{
    /// <summary>根据同一部署计划描述确认范围，只生成说明，不创建目录、锁或部署记录。</summary>
    internal static class ResourceBuiltInScope
    {
        /// <summary>提炼身份、写入范围和恢复约束供确认窗口展示，不将预检计划视为执行凭据。</summary>
        internal static ResourceConfirmationSummary Summarize(ResourceProject project, ResourceBuiltInPlan plan) => new()
        {
            Fields = new[] { ("资源项目", project.displayName + " / " + plan.Source.Document.ProjectId), ("构建平台", plan.Source.Document.Platform),
                ("所选版本", plan.Source.Document.CatalogDigest.Substring(0, 16)), ("来源", "本地发布目录"), ("目标", "Assets/StreamingAssets 内置资源目录"),
                ("资源文件", plan.Files.Count + " 个 · " + ResourceWorkbenchGui.Bytes(plan.PayloadBytes)),
                ("部署预检", plan.IsRepeat ? "重复部署：已有相同版本，执行前重新检查" : "通过：将写入所选版本，执行前重新检查") },
            Effects = new[] { "写入内置版本目录、部署凭据及必要控制文件；不会自动切换正在运行的资源版本。",
                "使用 Assets 之外的部署工作目录，可能写入暂存、锁和恢复记录。", "新目录会生成 .meta，并触发 Unity 资源导入；不会接管已有的未知内容。" },
            Notice = "只操作当前确认版本，不代表磁盘空间已检查。遇到身份冲突或未完成事务会拒绝执行；需要恢复时请使用独立恢复入口。",
            Details = new ResourceConfirmationDetails("ProjectId=" + plan.Source.Document.ProjectId + "\nProjectGuid=" + plan.ProjectGuid +
                "\nPlatform=" + plan.Source.Document.Platform + "\nUnity=" + plan.Source.Document.UnityCompatibility + "\nApp=" + plan.Source.Document.AppContentCompatibilityId +
                "\nCatalogDigest=" + plan.Source.Document.CatalogDigest,
                "只读来源=" + plan.Source.DirectoryPath + "\n内置目标=" + plan.DirectoryPath + "\n外部工作目录=" + plan.WorkspacePath +
                "\n工作目录控制文件：ylr-owner.json~ / deploy.lock / journal.json / journal.json.next / 私有 stage。" +
                "\n内置目录控制文件：ylr-owner.json~；版本记录：ylr-deployment.json~；完整交付内容位于 payload~。" +
                "\n本次新目录生成 .meta；payload~ 不导入、不生成内部 .meta。\n源文件（" + plan.Files.Count + "）：\n" + string.Join("\n", plan.Files),
                "PayloadBytes=" + plan.PayloadBytes + "\nIsRepeat=" + plan.IsRepeat +
                "\n执行前重新检查，并在取得锁后再次验证；必要时触发 AssetDatabase 同步导入。\n未检查磁盘容量，复制逻辑字节不是磁盘峰值。" +
                "\n提交未确认时，请使用“恢复内置部署事务”，由事务记录决定恢复步骤。不清理其他内容，不覆盖旧部署，不修改发布索引，也不切换运行版本或最近可用版本。")
        };

        /// <summary>列出部署可能涉及的工作目录、控制记录、元数据及导入影响，不据复制量承诺磁盘峰值。</summary>
        internal static string Describe(ResourceBuiltInPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var catalog = plan.Source.Document;
            var root = ResourceBuiltInDeployer.ResourcesRoot;
            var project = Path.Combine(root, catalog.ProjectId);
            var platform = Path.Combine(project, catalog.Platform);
            var text = new StringBuilder(plan.ToString());
            text.Append("\nProjectGuid=").Append(plan.ProjectGuid);
            text.Append("\n\n本次可能写入的位置（执行前和取得锁后均会重新验证）：");
            text.Append("\n1. 部署使用的外部工作目录：").Append(plan.WorkspacePath);
            text.Append("\n   ").Append(ResourceBuiltInLayout.OwnerFile).Append(" / deploy.lock / journal.json / journal.json.next / 私有 stage 及其登记文件。");
            text.Append("\n2. StreamingAssets 内需要创建的目录（不覆盖已登记父目录及其 .meta）：\n   ").Append(root).Append("\n   ").Append(project).Append("\n   ").Append(platform).Append("\n   ").Append(plan.DirectoryPath);
            text.Append("\n   新根、项目和平台目录的控制记录：").Append(ResourceBuiltInLayout.OwnerFile);
            text.Append("\n   版本记录：").Append(ResourceBuiltInLayout.ReceiptFile).Append("；payload~ 包含完整 Catalog、完成记录、Bundle 及配套文件。");
            text.Append("\n3. 本次新目录的 .meta 和 AssetDatabase 同步导入；payload~ 本身及内部文件不导入、不生成内部 .meta。");
            text.Append("\n4. 源版本只读；不覆盖或删除旧部署，不切换运行版本或最近可用版本，不上传或切换平台。");
            text.Append("\n\n源交付文件逻辑字节=").Append(plan.PayloadBytes).Append("；当前检查是否为重复部署=").Append(plan.IsRepeat);
            text.Append("\n复制逻辑字节不是磁盘峰值，还可能有 stage、控制记录、meta 和文件系统开销。未检查磁盘容量，请另行确认空间足够。");
            text.Append("\n如果重新检查仍确认相同版本已完整部署，则不再复制。环境变化后只在以上本资源项目登记范围内检查。提交未确认时，请使用‘恢复内置部署事务’，不要手工猜测journal步骤或删除其他内容。");
            return text.ToString();
        }
    }
}
