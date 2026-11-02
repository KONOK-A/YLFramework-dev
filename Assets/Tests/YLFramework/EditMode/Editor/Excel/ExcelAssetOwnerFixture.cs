using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Tests.Extensions.Data
{
    public sealed class ExcelAssetOwnerFixture : ScriptableObject
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("Rows")]
        public List<ExcelAssetRowFixture> rows = new List<ExcelAssetRowFixture>();
    }
}
