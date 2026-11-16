using UnityEngine.Localization;
using YLFramework.DataAssets;

namespace YLFramework.Localization.Samples.Data
{
    /// <summary>业务行只引用官方表项；不保存第二份翻译正文，也不把业务 ID 当作翻译 Key ID。</summary>
    public sealed class LocalizedItemData : ScriptableObjectData
    {
        [LocalizedExcelReference] public LocalizedString title = new LocalizedString();
        [LocalizedExcelReference(AllowEmpty = true)] public LocalizedSprite picture = new LocalizedSprite();
    }
}
