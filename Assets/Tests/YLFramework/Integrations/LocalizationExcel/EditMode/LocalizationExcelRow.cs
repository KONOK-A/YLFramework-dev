using UnityEngine.Localization;
using UnityEngine;
using YLFramework.DataAssets;
using YLFramework.Localization;

namespace YLFramework.Tests.Localization.Excel
{
    public class LocalizationExcelBaseRow : ScriptableObjectData
    {
        [SerializeField, LocalizedExcelReference(AllowEmpty = true)] private LocalizedString _inheritedTitle = new LocalizedString();
        public LocalizedString InheritedTitle => _inheritedTitle;
    }

    public sealed class LocalizationExcelRow : LocalizationExcelBaseRow
    {
        [LocalizedExcelReference] public LocalizedString title = new LocalizedString();
        [LocalizedExcelReference(AllowEmpty = true)] public LocalizedSprite image = new LocalizedSprite();
    }
}
