using System;

namespace YLFramework.Resource
{
    /// <summary>YL 自己取得的责任，不代表官方全部引用，也不接受任意外部 IDisposable。</summary>
    internal interface IResourceLease : IDisposable
    {
        ResourceSystem System
        {
            get;
        }
        ResourceScope Scope
        {
            get; set;
        }
        bool IsDisposed
        {
            get;
        }
        bool IsSettled
        {
            get;
        }
        ResourceRequest<bool> CloseAsync();
    }

    /// <summary>仅为本方已持有的资产建立热索引，不增持、不映射官方 Catalog。</summary>
    internal interface IResourceAssetLease : IResourceLease
    {
        string Key
        {
            get;
        }
        Type AssetType
        {
            get;
        }
    }
}
