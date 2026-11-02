using System.Collections.Generic;
using UnityEngine;
using YLFramework.Excel;

namespace YLFramework.Tests.Extensions.Data
{
    public enum ExcelAssetState
    {
        Off,
        On
    }

    public sealed class ExcelAssetRowFixture : ScriptableObject
    {
        [ExcelScriptableObjectId] public int Id;
        public string Text;
        public ExcelAssetState State;
        public List<int> Numbers = new List<int>();
        public Object Reference;
        [ExcelIgnore] public ExcelAssetOwnerFixture Parent;
    }
}
