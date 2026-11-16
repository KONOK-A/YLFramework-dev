using System;
using UnityEngine.Localization;

namespace YLFramework.Localization
{
    internal interface ILocalizationBinding : IDisposable
    {
        bool IsReady
        {
            get;
        }
        bool IsDisposed
        {
            get;
        }
        Exception Error
        {
            get;
        }
        int CommitOrder
        {
            get;
        }
        void Refresh(Locale locale);
        void Suspend();
        void Commit();
    }
}
