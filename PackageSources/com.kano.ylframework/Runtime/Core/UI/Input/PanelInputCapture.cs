using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YLFramework.Core
{
    /// <summary>记录控件的按压与拖动身份，在 EventSystem 处理前清理跨阻挡阶段或跨会话的旧捕获。</summary>
    /// <remarks>只撤销匹配的指针状态，不重置整个输入模块，也不改业务控件 enabled。</remarks>
    [DefaultExecutionOrder(-10000)]
    internal sealed class PanelInputCapture : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        /// <summary>手势接受时的会话、设备和指针快照；不依赖可被模块复用的 eventData 当前身份。</summary>
        private sealed class Capture
        {
            internal Panel Panel;
            internal object Session;
            internal object Epoch;
            internal PointerEventData Data;
            internal BaseInputModule Module;
            internal object ButtonControl;
            internal object Device;
            internal int PointerId;
            internal int TouchId;
            internal PointerEventData.InputButton Button;
            internal RaycastResult PressRaycast;
            internal Vector2 Position;
            internal Vector2 PressPosition;
            internal Canvas Canvas;
            internal RenderMode RenderMode;
            internal Camera Camera;
            internal Rect CameraViewport;
            internal Matrix4x4 CameraView;
            internal Matrix4x4 CameraProjection;
            internal int ScreenWidth;
            internal int ScreenHeight;
            internal bool IsInvalid;
            internal bool IsCleaned;
        }
        private readonly Dictionary<long, Capture> _captures = new Dictionary<long, Capture>();
        private Capture _primaryCapture;
        private object _epoch = new object();
        private Panel _framePanel;
        private object _frameSession;
        private object _frameEpoch;
        private bool _isFrameBlocked;
        private int _frame = -1;

        /// <summary>更换捕获轮次并尝试撤销全部旧手势，使同一 Session 恢复显示后也不能延续旧按压。</summary>
        internal void Invalidate()
        {
            _epoch = new object();
            foreach (var capture in new List<Capture>(_captures.Values))
            {
                capture.IsInvalid = true;
                Cancel(capture);
            }
        }

        /// <summary>按指针与按钮记录手势，并以帧前快照检查事件接受期间是否已被阻挡或换会话。</summary>
        internal void Record(PointerEventData data)
        {
            var panel = GetComponentInParent<Panel>(true);
            var canvas = _framePanel && _framePanel.Root ? _framePanel.Root.Canvas : null;
            var camera = data.pointerCurrentRaycast.module ? data.pointerCurrentRaycast.module.eventCamera : null;
            var capture = new Capture
            {
                Panel = _framePanel,
                Session = _frameSession,
                Epoch = _frameEpoch,
                Data = data,
                Module = data.currentInputModule,
                Button = data.button,
                PointerId = data.pointerId,
                ButtonControl = InputSystemBridge.GetPointerButton(data),
                Device = InputSystemBridge.GetPointerDevice(data),
                TouchId = InputSystemBridge.GetTouchId(data),
                PressRaycast = data.pointerCurrentRaycast,
                Position = data.position,
                PressPosition = data.pressPosition,
                Canvas = canvas,
                RenderMode = canvas ? canvas.renderMode : default,
                Camera = camera,
                CameraViewport = camera ? camera.pixelRect : default,
                CameraView = camera ? camera.worldToCameraMatrix : default,
                CameraProjection = camera ? camera.projectionMatrix : default,
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height
            };
            capture.IsInvalid = _frame != Time.frameCount || _isFrameBlocked || !panel || panel != _framePanel ||
                !ReferenceEquals(panel.Session, _frameSession) || !ReferenceEquals(_epoch, _frameEpoch) || panel.IsInputBlocked;
            _captures[Key(capture.PointerId, capture.Button)] = capture;
            if (capture.Button == PointerEventData.InputButton.Left)
            {
                _primaryCapture = capture;
            }

            if (capture.IsInvalid)
            {
                Cancel(capture, true);
            }
        }

        /// <summary>保留有符号指针编号的位模式，并将按钮编码到同一捕获键。</summary>
        private static long Key(int pointer, PointerEventData.InputButton button) => ((long)(uint)pointer << 2) | (uint)button;
        /// <summary>确认面板仍加载、可接收输入且会话和捕获轮次均与接受时一致。</summary>
        private bool IsValid(Capture capture) => !capture.IsInvalid && capture.Panel && capture.Panel.IsLoaded &&
            !capture.Panel.IsInputBlocked && ReferenceEquals(capture.Panel.Session, capture.Session) &&
            ReferenceEquals(capture.Epoch, _epoch) && HasSameGeometry(capture);

        /// <summary>坐标映射变化后旧按压不能在新映射下继续；只检查仍持有的手势，不轮询无捕获控件的相机矩阵。</summary>
        private static bool HasSameGeometry(Capture capture)
        {
            if (!capture.Canvas || !capture.Panel.Root || capture.Panel.Root.Canvas != capture.Canvas ||
                capture.Canvas.renderMode != capture.RenderMode || Screen.width != capture.ScreenWidth || Screen.height != capture.ScreenHeight)
            {
                return false;
            }

            var raycaster = capture.PressRaycast.module;
            if (!ReferenceEquals(raycaster, null) && !raycaster)
            {
                return false;
            }
            var camera = raycaster ? raycaster.eventCamera : null;
            if (camera != capture.Camera)
            {
                return false;
            }
            if (ReferenceEquals(capture.Camera, null))
            {
                return true;
            }

            return capture.Camera && capture.Camera.pixelRect.Equals(capture.CameraViewport) &&
                capture.Camera.worldToCameraMatrix.Equals(capture.CameraView) && capture.Camera.projectionMatrix.Equals(capture.CameraProjection);
        }

        /// <summary>先向模块撤销精确捕获，匹配成功后才对原标准控件执行取消收尾。</summary>
        private void Cancel(Capture capture, bool acceptingDown = false)
        {
            capture.IsInvalid = true;
            var data = capture.Data;
            // 先撤销原生捕获，再调用标准控件取消。回调内 Close/复用不得对新 Session 重放旧收尾。
            if (!InputSystemBridge.CancelPointer(capture.Module, data, capture.PointerId, capture.Button, capture.Device, capture.TouchId, gameObject, acceptingDown))
            {
                return;
            }

            FinishControls(capture);
        }

        /// <summary>以原按钮快照结束标准控件的按压、拖动和惯性，避免重复收尾或影响后来的主手势。</summary>
        private void FinishControls(Capture capture)
        {
            if (capture.IsCleaned)
            {
                return;
            }

            capture.IsCleaned = true;
            // 同一标准控件已有后来的主手势时，旧设备退出不能收尾新手势。
            if (capture.Button == PointerEventData.InputButton.Left && !ReferenceEquals(_primaryCapture, capture))
            {
                return;
            }
            // Input System 的三个鼠标按钮共享 eventData；收尾不能借用最后处理按钮的可变 button。
            var cancelData = new PointerEventData(capture.Module ? capture.Module.GetComponent<EventSystem>() : null)
            {
                pointerId = capture.PointerId,
                button = capture.Button,
                position = capture.Position,
                pressPosition = capture.PressPosition,
                pointerPressRaycast = capture.PressRaycast
            };
            bool Current() => capture.Panel && ReferenceEquals(capture.Panel.Session, capture.Session);
            foreach (var control in GetComponents<UIBehaviour>())
            {
                if (!Current())
                {
                    break;
                }

                if (control is ScrollRect scroll)
                {
                    scroll.OnEndDrag(cancelData);
                    if (Current())
                    {
                        scroll.StopMovement();
                    }
                }
                else if (control is Scrollbar scrollbar)
                {
                    scrollbar.OnPointerUp(cancelData);
                }
                else if (control is InputField input)
                {
                    input.OnEndDrag(cancelData);
                }
                else if (control is Selectable selectable)
                {
                    selectable.OnPointerUp(cancelData);
                }
            }
        }

        /// <summary>接收标准按下事件并登记当前手势，不触发业务点击。</summary>
        public void OnPointerDown(PointerEventData data) => Record(data);
        /// <summary>拖动期间复核记录，失效时撤销原捕获而非继续向旧会话传递手势。</summary>
        internal void Drag(PointerEventData data)
        {
            if (_captures.TryGetValue(Key(data.pointerId, data.button), out var capture) && !IsValid(capture))
            {
                Cancel(capture);
            }
        }
        /// <summary>抬起时取消已失效的原手势并移除匹配记录，不保留给后续会话。</summary>
        public void OnPointerUp(PointerEventData data)
        {
            long key = Key(data.pointerId, data.button);
            if (!_captures.TryGetValue(key, out var capture))
            {
                return;
            }

            if (!IsValid(capture))
            {
                Cancel(capture);
            }

            Forget(key, capture);
        }
        /// <summary>正常拖动结束时遗忘有效记录；已失效记录保留给取消路径继续收尾。</summary>
        internal void EndDrag(PointerEventData data)
        {
            long key = Key(data.pointerId, data.button);
            if (_captures.TryGetValue(key, out var capture) && !capture.IsInvalid)
            {
                Forget(key, capture);
            }
        }
        /// <summary>组件停用时撤销全部捕获并清空本地手势索引。</summary>
        private void OnDisable()
        {
            Invalidate();
            _captures.Clear();
            _primaryCapture = null;
        }
        /// <summary>仅移除键下仍为原快照的记录，避免旧收尾删除同键后来的手势。</summary>
        private void Forget(long key, Capture capture)
        {
            if (!_captures.TryGetValue(key, out var current) || !ReferenceEquals(current, capture))
            {
                return;
            }

            _captures.Remove(key);
            if (ReferenceEquals(_primaryCapture, capture))
            {
                _primaryCapture = null;
            }
        }
        /// <summary>在输入模块前检查设备、数据复用及按压存活，再准备本帧接受事件所需的会话快照。</summary>
        private void Update()
        {
            foreach (var pair in new List<KeyValuePair<long, Capture>>(_captures))
            {
                var capture = pair.Value;
                // 移除设备时模块可能已清空/回收 eventData，只收尾原控件，不再写它的可变原生镜像。
                if (InputSystemBridge.IsPointerDeviceRemoved(capture.Device) || !capture.Module || !capture.Module.isActiveAndEnabled)
                {
                    capture.IsInvalid = true;
                    FinishControls(capture);
                    Forget(pair.Key, capture);
                    continue;
                }
                // eventData 可由池复用，不能根据它的新身份清理外部手势。
                if (capture.Data.pointerId != capture.PointerId || !ReferenceEquals(InputSystemBridge.GetPointerDevice(capture.Data), capture.Device) ||
                    InputSystemBridge.GetTouchId(capture.Data) != capture.TouchId)
                {
                    Forget(pair.Key, capture);
                    continue;
                }
                if (!IsValid(capture))
                {
                    Cancel(capture);
                }

                if (!InputSystemBridge.PointerStillPressed(capture.Module, capture.ButtonControl, capture.PointerId, capture.Button))
                {
                    Forget(pair.Key, capture);
                }
            }
            _framePanel = GetComponentInParent<Panel>(true);
            _frameSession = _framePanel ? _framePanel.Session : null;
            _frameEpoch = _epoch;
            _isFrameBlocked = !_framePanel || _framePanel.IsInputBlocked;
            _frame = Time.frameCount;
        }
    }
}
