using System;

namespace YLFramework.Localization
{
    /// <summary>本地化图片的资源定位信息，仅保存目录地址，不持有图片租约。</summary>
    [Serializable]
    public class ImageInfo
    {
        /// <summary>图片所在 YL Resource 项目的显式标识。</summary>
        public string projectName;
        /// <summary>图片在资源目录中的逻辑地址。</summary>
        public string imageName;
        /// <summary>可选子资产键；为空时使用主资产地址。</summary>
        public string subAsset;
    }

    /// <summary>本地化条目内容的解释方式，由文本或图片绑定分别消费。</summary>
    public enum MultiLanguageType
    {
        /// <summary>使用条目的文本内容，可包含格式占位符。</summary>
        String,
        /// <summary>使用资源目录中的图片定位信息。</summary>
        Image,
        /// <summary>使用条目的图片 URL，由 ResourceImage 负责取得图片。</summary>
        NetworkImage
    }

    /// <summary>保留原多语言条目字段布局；图片项目名在YL中表示显式Resource ProjectId。</summary>
    [Serializable]
    public class LanguageInfo
    {
        /// <summary>同语言内的唯一条目 ID，用于跨语言定位对应内容。</summary>
        public int id;
        /// <summary>决定文本、资源图片或网络图片的取值方式。</summary>
        public MultiLanguageType type;
        /// <summary>文本内容；字符串条目中 null 表示不可用，空串仍是有效内容。</summary>
        public string str;
        /// <summary>资源图片的项目、地址和可选子资产键。</summary>
        public ImageInfo image;
        /// <summary>网络图片定位字符串，直接交给 ResourceImage，不在条目中下载。</summary>
        public string url;
        /// <summary>作者备注，不参与文本格式化或图片加载。</summary>
        public string notes;
    }
}
