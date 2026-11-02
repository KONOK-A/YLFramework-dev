using UnityEngine;
using UnityEngine.EventSystems;

namespace YLFramework.Core
{
    /// <summary>把拖动生命周期转交给捕获记录；只附加到原本可拖动的控件，不把普通 Button 变成拖动目标。</summary>
    internal sealed class PanelDragCapture : MonoBehaviour, IInitializePotentialDragHandler, IDragHandler, IEndDragHandler
    {
        private PanelInputCapture _capture;
        private PanelInputCapture Capture => _capture ? _capture : (_capture = GetComponent<PanelInputCapture>());
        /// <summary>在模块确定拖动目标时记录手势，即使该控件不是本次按压接收者。</summary>
        public void OnInitializePotentialDrag(PointerEventData data) => Capture.Record(data);
        /// <summary>拖动中检查原捕获是否仍可使用，失效时交由捕获组件取消。</summary>
        public void OnDrag(PointerEventData data) => Capture.Drag(data);
        /// <summary>通知捕获组件结束正常拖动记录，不替代业务控件自己的结束处理。</summary>
        public void OnEndDrag(PointerEventData data) => Capture.EndDrag(data);
    }
}
