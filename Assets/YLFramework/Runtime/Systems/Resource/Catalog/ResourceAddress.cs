using System;

namespace YLFramework.Resource
{
    /// <summary>不可变逻辑地址；按 ordinal 匹配，不修正大小写或猜测外部项目名。</summary>
    public readonly struct ResourceAddress : IEquatable<ResourceAddress>
    {
        /// <summary>资源所属项目的稳定标识，不是 Unity 工程目录或 Bundle 名。</summary>
        public string ProjectId
        {
            get;
        }
        /// <summary>项目内登记的逻辑路径；不等同于来源的物理 Locator。</summary>
        public string Path
        {
            get;
        }
        /// <summary>可选子资产身份；主资产为 null，正式 Bundle 目录使用 localId 的固定十进制表示。</summary>
        public string SubAsset
        {
            get;
        }
        /// <summary>是否由构造器建立了项目和路径；不检查目录登记、资源存在或会话状态。</summary>
        public bool IsValid => ProjectId != null && Path != null;

        /// <summary>校验并原样保存地址三元组，不裁剪空白、不折叠路径或大小写。</summary>
        /// <param name="projectId">仅含允许 ASCII 字符的稳定项目标识。</param>
        /// <param name="path">使用正斜杠且不含空段、当前目录段或父目录段的逻辑路径。</param>
        /// <param name="subAsset">可选的精确子资产键；不是附加在 path 中解析的片段。</param>
        public ResourceAddress(string projectId, string path, string subAsset = null)
        {
            ValidateId(projectId, nameof(projectId));
            ValidatePath(path, nameof(path));
            if (subAsset != null)
            {
                ValidatePath(subAsset, nameof(subAsset));
            }

            ProjectId = projectId;
            Path = path;
            SubAsset = subAsset;
        }

        /// <summary>拒绝空标识、特殊目录名及约定字符集之外的字符。</summary>
        internal static void ValidateId(string value, string parameter)
        {
            if (string.IsNullOrEmpty(value) || value == "." || value == "..")
            {
                throw new ArgumentException("A nonempty stable identifier is required.", parameter);
            }

            foreach (var character in value)
            {
                if (!(character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
                      character >= '0' && character <= '9' || character == '_' || character == '-' || character == '.'))
                {
                    throw new ArgumentException("Identifiers only accept ASCII letters, digits, '.', '_' and '-'.", parameter);
                }
            }
        }

        /// <summary>验证精确路径的空白、控制字符和分段约束，不执行文件系统访问。</summary>
        internal static void ValidatePath(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.IndexOf('\\') >= 0)
            {
                throw new ArgumentException("An exact nonempty forward-slash address is required.", parameter);
            }

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    throw new ArgumentException("Control characters are not allowed.", parameter);
                }
            }

            foreach (var segment in value.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw new ArgumentException("Empty, '.' and '..' address segments are not allowed.", parameter);
                }
            }
        }

        /// <summary>按区分大小写的项目、路径和子资产三元组比较身份。</summary>
        public bool Equals(ResourceAddress other) => ProjectId == other.ProjectId && Path == other.Path && SubAsset == other.SubAsset;
        /// <summary>仅与相同地址类型按三元组比较。</summary>
        public override bool Equals(object obj) => obj is ResourceAddress other && Equals(other);
        /// <summary>为三元组生成集合散列值，不作为跨进程的持久摘要。</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                return ((ProjectId?.GetHashCode() ?? 0) * 397 ^ (Path?.GetHashCode() ?? 0)) * 397 ^ (SubAsset?.GetHashCode() ?? 0);
            }
        }
        /// <summary>判断两个地址是否具有相同的精确身份。</summary>
        public static bool operator ==(ResourceAddress left, ResourceAddress right) => left.Equals(right);
        /// <summary>判断两个地址的精确身份是否不同。</summary>
        public static bool operator !=(ResourceAddress left, ResourceAddress right) => !left.Equals(right);
        /// <summary>生成便于诊断的项目、路径及可选子资产文本，不提供反向解析协议。</summary>
        public override string ToString() => SubAsset == null ? $"{ProjectId}:{Path}" : $"{ProjectId}:{Path}#{SubAsset}";
    }
}
