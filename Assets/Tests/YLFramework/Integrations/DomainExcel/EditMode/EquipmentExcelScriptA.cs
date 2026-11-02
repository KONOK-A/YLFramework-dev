#if UNITY_EDITOR && YL_DOMAIN_EXCEL_PACKAGE
using YLFramework.Equipment;
namespace YLFramework.Domain.Excel.Tests
{
    public class EquipmentExcelScriptA : IEquipment
    {
        public IEquippable Equipper
        {
            get; set;
        }
        public EquipmentConfigData EquipmentData
        {
            get; set;
        }
        public void OnLoaded()
        {
        }
        public void OnUnloaded()
        {
        }
        public void OnEquip()
        {
        }
        public void OnUnequip()
        {
        }
    }
}
#endif
