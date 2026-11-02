using System;

namespace YLFramework.Resource
{
    /// <summary>由已选择目录提供的内容绑定上下文，用于核对文件角色及项目、平台和逻辑身份，不从密文头自建信任。</summary>
    internal readonly struct ResourceBundleContext
    {
        internal string ProjectId
        {
            get;
        }
        internal string Platform
        {
            get;
        }
        internal string UnityCompatibility
        {
            get;
        }
        internal string FileRole
        {
            get;
        }
        internal string LogicalId
        {
            get;
        }

        /// <summary>校验有界身份文本与 Bundle/Manifest 角色关系，再保存供封装校验和派生使用的上下文。</summary>
        internal ResourceBundleContext(string projectId, string platform, string unityCompatibility, string fileRole, string logicalId)
        {
            CheckText(projectId, nameof(projectId));
            CheckText(platform, nameof(platform));
            CheckText(unityCompatibility, nameof(unityCompatibility));
            CheckText(logicalId, nameof(logicalId));
            if (fileRole != "Bundle" && fileRole != "Manifest" || fileRole == "Manifest" && logicalId != "manifest")
            {
                throw new ArgumentException("Invalid resource content role/logical identity.", nameof(fileRole));
            }

            ProjectId = projectId;
            Platform = platform;
            UnityCompatibility = unityCompatibility;
            FileRole = fileRole;
            LogicalId = logicalId;
        }

        /// <summary>拒绝默认结构体，避免在缺少明确上下文时检查或解密内容。</summary>
        internal void RequireValid()
        {
            if (ProjectId == null)
            {
                throw new ArgumentException("An explicit resource content context is required.");
            }
        }

        /// <summary>要求非空白、无首尾空白和控制字符的有界精确文本，不推断兼容性。</summary>
        private static void CheckText(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value != value.Trim())
            {
                throw new ArgumentException("An exact bounded content identity is required.", parameter);
            }

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    throw new ArgumentException("Control characters are not allowed.", parameter);
                }
            }
        }
    }
}
