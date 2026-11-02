using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using YLFramework.Singletons;

namespace YLFramework.Core
{
    /// <summary>
    /// 运行时为 EventSystem 补充输入模块，并以反射方式访问 InputSystem，
    /// 避免框架程序集在未安装 InputSystem 包的工程产生编译依赖。
    /// </summary>
    internal static class InputSystemBridge
    {
        private const string InputSystemAssembly = "Unity.InputSystem";
        private const string InputSystemModuleTypeName = "UnityEngine.InputSystem.UI.InputSystemUIInputModule, " + InputSystemAssembly;
        private const string MouseTypeName = "UnityEngine.InputSystem.Mouse, " + InputSystemAssembly;
        private const string Vector2ControlTypeName = "UnityEngine.InputSystem.Controls.Vector2Control, " + InputSystemAssembly;

        private static bool _hasWarnedAboutInputModuleFallback;
        private static bool _hasWarnedAboutMouseReflection;
        private static readonly FieldInfo _activeSystems = typeof(EventSystem).GetField("m_EventSystems", BindingFlags.NonPublic | BindingFlags.Static);

        // UGUI 没有公开 active 系统枚举 API。仅只读现有小集合，避免自建系统服务时每帧全场景扫描。
        internal static EventSystem FindExternalSystem(EventSystem owned)
        {
            if (_activeSystems == null)
            {
                throw new MissingFieldException(typeof(EventSystem).FullName, "m_EventSystems");
            }

            var systems = (List<EventSystem>)_activeSystems.GetValue(null);
            foreach (var candidate in systems)
            {
                if (candidate && candidate != owned && candidate.isActiveAndEnabled)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>停用自有系统；与外部模块共享默认 actions 时，在自身 OnDisable 期间临时保护其所有权。</summary>
        internal static void DisableOwnedSystem(GameObject owned)
        {
#if ENABLE_INPUT_SYSTEM
            foreach (var module in owned.GetComponents<BaseInputModule>())
            {
                var type = module.GetType();
                if (type.FullName != "UnityEngine.InputSystem.UI.InputSystemUIInputModule" || !module.isActiveAndEnabled)
                {
                    continue;
                }

                var asset = type.GetProperty("actionsAsset").GetValue(module) as UnityEngine.Object;
                var systems = (List<EventSystem>)_activeSystems.GetValue(null);
                bool shared = false;
                foreach (var system in systems)
                {
                    if (system && system.gameObject != owned)
                    {
                        foreach (var other in system.GetComponents<BaseInputModule>())
                        {
                            if (other.isActiveAndEnabled && other.GetType() == type &&
                                (UnityEngine.Object)type.GetProperty("actionsAsset").GetValue(other) == asset)
                            {
                                shared = true;
                            }
                        }
                    }
                }

                if (!shared)
                {
                    continue;
                }
                // 1.14 的 UnassignActions 无条件 Dispose static defaultActions，即使另一 active 模块仍引用它。
                // 先完成自身 PointerExit 回调，再临时摘下默认资源所有权；仅模块 OnDisable 期间保护共享资产。
                var defaultsField = type.GetField("defaultActions", BindingFlags.Static | BindingFlags.NonPublic);
                var resetPointers = type.GetMethod("ResetPointers", BindingFlags.Instance | BindingFlags.NonPublic);
                if (defaultsField == null || resetPointers == null)
                {
                    throw new MissingMemberException(type.FullName, "defaultActions/ResetPointers");
                }

                resetPointers.Invoke(module, null);
                var defaults = defaultsField.GetValue(null);
                if (defaults == null)
                {
                    continue;
                }

                defaultsField.SetValue(null, null);
                try
                {
                    module.enabled = false;
                }
                finally
                {
                    if (defaultsField.GetValue(null) == null)
                    {
                        defaultsField.SetValue(null, defaults);
                    }
                    else
                    {
                        Debug.LogError("[InputSystemBridge] Default UI actions changed during owned module shutdown; retained the new owner's defaults.");
                    }
                }
            }
#endif
            owned.SetActive(false);
        }

        /// <summary>从扩展指针数据解析鼠标按钮、笔尖或对应触点的按压控件，无法取得时返回 null。</summary>
        internal static object GetPointerButton(PointerEventData data)
        {
#if ENABLE_INPUT_SYSTEM
            var device = data.GetType().GetProperty("device")?.GetValue(data);
            if (device == null)
            {
                return null;
            }

            var buttonName = data.button == PointerEventData.InputButton.Right ? "rightButton" :
                data.button == PointerEventData.InputButton.Middle ? "middleButton" : "leftButton";
            var button = device.GetType().GetProperty(buttonName)?.GetValue(device);
            if (button != null)
            {
                return button;
            }

            button = device.GetType().GetProperty("tip")?.GetValue(device);
            if (button != null)
            {
                return button;
            }

            var touches = device.GetType().GetProperty("touches")?.GetValue(device) as IEnumerable;
            var touchId = data.GetType().GetProperty("touchId")?.GetValue(data);
            if (touches != null && touchId != null)
            {
                foreach (var touch in touches)
                {
                    var id = touch.GetType().GetProperty("touchId").GetValue(touch);
                    if (Equals(id.GetType().GetMethod("ReadValue").Invoke(id, null), touchId))
                    {
                        return touch.GetType().GetProperty("press").GetValue(touch);
                    }
                }
            }
#endif
            return null;
        }

        internal static object GetPointerDevice(PointerEventData data) => data.GetType().GetProperty("device")?.GetValue(data);
        internal static int GetTouchId(PointerEventData data) => (int?)data.GetType().GetProperty("touchId")?.GetValue(data) ?? 0;

        /// <summary>按 Input System 的 added 状态确认设备移除；Legacy 无设备对象不视为已移除。</summary>
        internal static bool IsPointerDeviceRemoved(object device)
        {
            if (device == null)
            {
                return false; // Legacy PointerEventData 没有 InputDevice。
            }

            var added = device.GetType().GetProperty("added");
            if (added == null || added.PropertyType != typeof(bool))
            {
                throw new InvalidOperationException("Input System 设备存活布局不匹配，不能确认旧捕获已结束。");
            }

            return !(bool)added.GetValue(device);
        }

        /// <summary>经字段类型核验的 Input System 指针状态访问表，用于精确写回嵌套值类型。</summary>
        private sealed class PointerAdapter
        {
            internal readonly FieldInfo States;
            internal readonly FieldInfo Length;
            internal readonly FieldInfo First;
            internal readonly FieldInfo Additional;
            internal readonly FieldInfo EventData;
            internal readonly FieldInfo[] Buttons;
            internal readonly FieldInfo[] Objects;
            internal readonly FieldInfo Dragging;
            internal readonly FieldInfo IgnoreClick;
            internal readonly FieldInfo ClickCount;
            internal readonly FieldInfo ClickTime;
            internal readonly FieldInfo SameObject;
            internal readonly FieldInfo PressRaycast;
            /// <summary>解析并校验当前模块的内部布局，成员缺失或类型变化时拒绝建立适配器。</summary>
            internal PointerAdapter(Type moduleType)
            {
                FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                    throw new MissingFieldException(type.FullName, name);
                States = Field(moduleType, "m_PointerStates");
                Length = Field(States.FieldType, "length");
                First = Field(States.FieldType, "firstValue");
                Additional = Field(States.FieldType, "additionalValues");
                var pointer = First.FieldType;
                EventData = Field(pointer, "eventData");
                Buttons = new[] { Field(pointer, "leftButton"), Field(pointer, "rightButton"), Field(pointer, "middleButton") };
                var button = Buttons[0].FieldType;
                Objects = new[] { Field(button, "m_PressObject"), Field(button, "m_RawPressObject"), Field(button, "m_LastPressObject"), Field(button, "m_DragObject") };
                Dragging = Field(button, "m_Dragging");
                IgnoreClick = Field(button, "m_IgnoreNextClick");
                ClickCount = Field(button, "m_ClickCount");
                ClickTime = Field(button, "m_ClickTime");
                SameObject = Field(button, "m_ClickedOnSameGameObject");
                PressRaycast = Field(button, "m_PressRaycast");
                if (!States.FieldType.IsValueType || !pointer.IsValueType || !button.IsValueType ||
                    Additional.FieldType.GetElementType() != pointer || Length.FieldType != typeof(int) ||
                    !typeof(PointerEventData).IsAssignableFrom(EventData.FieldType) || Array.Exists(Buttons, member => member.FieldType != button) ||
                    Array.Exists(Objects, member => member.FieldType != typeof(GameObject)) || SameObject.FieldType != typeof(bool) ||
                    ClickCount.FieldType != typeof(int) || ClickTime.FieldType != typeof(float) ||
                    Dragging.FieldType != typeof(bool) || IgnoreClick.FieldType != typeof(bool) || PressRaycast.FieldType != typeof(RaycastResult))
                {
                    throw new InvalidOperationException("Input System 指针布局不匹配；需要核实实际包版本和 IL2CPP 保留结果，不能回退为业务字段覆盖。");
                }
            }
        }
        private static PointerAdapter _pointerAdapter;
        private static FieldInfo _pendingInputFieldActivation;

        /// <summary>清除 InputField 已排队的下一帧激活，防止门禁生效后旧选择重新激活输入框。</summary>
        internal static void CancelPendingInputFieldActivation(UnityEngine.UI.InputField input)
        {
            var field = _pendingInputFieldActivation ?? (_pendingInputFieldActivation = typeof(UnityEngine.UI.InputField).GetField("m_ShouldActivateNextUpdate", BindingFlags.NonPublic | BindingFlags.Instance));
            if (field == null || field.FieldType != typeof(bool))
            {
                throw new InvalidOperationException("UGUI InputField 延迟激活布局不匹配，不能完成门禁收尾。");
            }

            field.SetValue(input, false);
        }

        /// <summary>只撤销匹配设备、触点、按钮及目标的捕获，返回是否已找到可清理的原状态。</summary>
        // 对应 Input System 1.14.0 的 ButtonState。所有值类型逐层写回，物理 isPressed/framePressState/pressTime 保留。
        internal static bool CancelPointer(BaseInputModule module, PointerEventData data, int pointerId,
            PointerEventData.InputButton button, object device, int touchId, GameObject target, bool acceptingDown = false)
        {
            if (!module || data.pointerId != pointerId || !ReferenceEquals(GetPointerDevice(data), device) || GetTouchId(data) != touchId)
            {
                return false;
            }

            bool matched = false;
            if (module.GetType().FullName == "UnityEngine.InputSystem.UI.InputSystemUIInputModule")
            {
                var adapter = _pointerAdapter ?? (_pointerAdapter = new PointerAdapter(module.GetType()));
                object states = adapter.States.GetValue(module);
                int length = (int)adapter.Length.GetValue(states);
                var additional = (Array)adapter.Additional.GetValue(states);
                for (int i = 0; i < length; i++)
                {
                    object pointer = i == 0 ? adapter.First.GetValue(states) : additional.GetValue(i - 1);
                    if (!ReferenceEquals(adapter.EventData.GetValue(pointer), data))
                    {
                        continue;
                    }

                    var field = adapter.Buttons[(int)button];
                    object state = field.GetValue(pointer);
                    bool owns = acceptingDown;
                    foreach (var member in adapter.Objects)
                    {
                        owns |= (GameObject)member.GetValue(state) == target;
                    }

                    if (!owns)
                    {
                        continue;
                    }

                    foreach (var member in adapter.Objects)
                    {
                        member.SetValue(state, null);
                    }

                    adapter.Dragging.SetValue(state, false);
                    adapter.IgnoreClick.SetValue(state, true);
                    adapter.SameObject.SetValue(state, false);
                    adapter.ClickCount.SetValue(state, 0);
                    adapter.ClickTime.SetValue(state, 0f);
                    adapter.PressRaycast.SetValue(state, default(RaycastResult));
                    field.SetValue(pointer, state);
                    if (i == 0)
                    {
                        adapter.First.SetValue(states, pointer);
                    }
                    else
                    {
                        additional.SetValue(pointer, i - 1);
                    }

                    adapter.States.SetValue(module, states);
                    matched = true;
                    break;
                }
            }
            else if (!(module is StandaloneInputModule))
            {
                throw new InvalidOperationException("Panel 捕获取消仅支持 StandaloneInputModule 和经版本校验的 InputSystemUIInputModule；请配置标准输入模块。");
            }

            if (data.button == button && (acceptingDown || data.pointerPress == target || data.rawPointerPress == target || data.pointerDrag == target))
            {
                data.eligibleForClick = false;
                data.pointerPress = null;
                data.rawPointerPress = null;
                data.pointerClick = null;
                data.pointerDrag = null;
                data.dragging = false;
                data.clickCount = 0;
                data.clickTime = 0;
                data.pointerPressRaycast = default;
                var property = typeof(PointerEventData).GetProperty("lastPress");
                var lastPress = property?.GetSetMethod(true);
                if (lastPress == null || property.PropertyType != typeof(GameObject))
                {
                    throw new InvalidOperationException("UGUI PointerEventData.lastPress 布局不匹配，不能完成捕获取消。");
                }

                lastPress.Invoke(data, new object[] { null });
                matched = true;
            }
            return matched;
        }

        /// <summary>读取原按压控件或 Legacy 指针的真实按压状态；无法查询的自定义模块不臆造释放。</summary>
        internal static bool PointerStillPressed(BaseInputModule module, object buttonControl, int pointerId, PointerEventData.InputButton button)
        {
            if (buttonControl != null)
            {
                var device = buttonControl.GetType().GetProperty("device").GetValue(buttonControl);
                if (!(bool)device.GetType().GetProperty("added").GetValue(device))
                {
                    return false;
                }

                return (bool)buttonControl.GetType().GetProperty("isPressed").GetValue(buttonControl);
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (module is StandaloneInputModule)
            {
                if (pointerId < 0)
                {
                    return module.input.GetMouseButton((int)button);
                }

                for (int i = 0; i < module.input.touchCount; i++)
                {
                    var touch = module.input.GetTouch(i);
                    if (touch.fingerId == pointerId)
                    {
                        return touch.phase != TouchPhase.Ended && touch.phase != TouchPhase.Canceled;
                    }
                }
                return false;
            }
#endif
            // 自定义输入模块没有公开捕获状态时，等待其真实 Up/Drag 取消，不臆造释放或清全局输入。
            return true;
        }

        /// <summary>随执行会话重置警告与反射缓存，不跨运行沿用指针适配器和鼠标访问结果。</summary>
        static InputSystemBridge() => SingletonSessionState.RegisterSessionReset(() =>
        {
            _hasWarnedAboutInputModuleFallback = _hasWarnedAboutMouseReflection = false;
            _pointerAdapter = null;
            _pendingInputFieldActivation = null;
#if ENABLE_INPUT_SYSTEM
            _hasInitializedMouseReflection = false;
            _mouseCurrentProperty = _mousePositionProperty = null;
            _readValueMethod = null;
#endif
        });

#if ENABLE_INPUT_SYSTEM
        private static bool _hasInitializedMouseReflection;
        private static PropertyInfo _mouseCurrentProperty;
        private static PropertyInfo _mousePositionProperty;
        private static MethodInfo _readValueMethod;
#endif

        /// <summary>
        /// 为框架自建 EventSystem 补充缺少的输入模块。
        /// 仅供自建系统配置；已有模块不替换。Both 优先 Input System，只有 Legacy 可用时才允许回退。
        /// </summary>
        public static void EnsureEventSystemInputModule(EventSystem eventSystem)
        {
            if (!eventSystem)
            {
                throw new ArgumentNullException(nameof(eventSystem));
            }

            if (eventSystem.GetComponent<BaseInputModule>() != null)
            {
                return;
            }

#if ENABLE_INPUT_SYSTEM
            if (TryAddInputSystemModule(eventSystem))
            {
                return;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            eventSystem.gameObject.AddComponent<StandaloneInputModule>();
#else
            throw new InvalidOperationException("Input System-only UI requires an available InputSystemUIInputModule; no Legacy fallback is permitted.");
#endif
        }

        /// <summary>
        /// 反射读取 InputSystem 的鼠标位置；无设备或读取失败时明确返回 false，不把零坐标当有效输入。
        /// </summary>
        public static bool TryGetMousePosition(out Vector2 position)
        {
            position = default;
#if ENABLE_INPUT_SYSTEM
            if (!EnsureMouseReflection())
            {
                return false;
            }

            try
            {
                var current = _mouseCurrentProperty.GetValue(null);
                if (current == null)
                {
                    return false;
                }

                var positionControl = _mousePositionProperty.GetValue(current);
                if (positionControl == null)
                {
                    return false;
                }

                if (!(_readValueMethod.Invoke(positionControl, null) is Vector2 result))
                {
                    return false;
                }

                position = result;
                return true;
            }
            catch (Exception e)
            {
                WarnMouseReflectionOnce(e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>按当前运行模式销毁本次尝试创建的组件，供输入模块安装失败时收尾。</summary>
        private static void DestroyComponent(Component component)
        {
            if (component == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(component);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        /// <summary>每轮执行最多报告一次 Input System 模块不可用及其回退限制。</summary>
        private static void WarnInputModuleFallbackOnce(string reason)
        {
            if (_hasWarnedAboutInputModuleFallback)
            {
                return;
            }

            _hasWarnedAboutInputModuleFallback = true;
            Debug.LogWarning($"[InputSystemBridge] InputSystem 输入模块不可用（{reason}）。仅启用 Legacy 时允许回退 StandaloneInputModule。");
        }

        /// <summary>每轮执行最多报告一次鼠标反射失败，不把失败结果伪装为有效零坐标。</summary>
        private static void WarnMouseReflectionOnce(string reason)
        {
            if (_hasWarnedAboutMouseReflection)
            {
                return;
            }

            _hasWarnedAboutMouseReflection = true;
            Debug.LogWarning($"[InputSystemBridge] 反射读取 InputSystem 鼠标位置失败（{reason}），本次没有有效鼠标输入。");
        }

#if ENABLE_INPUT_SYSTEM
        /// <summary>反射安装 Input System UI 模块，必要时分配默认 actions；失败则销毁本次组件并报告原因。</summary>
        private static bool TryAddInputSystemModule(EventSystem eventSystem)
        {
            var moduleType = Type.GetType(InputSystemModuleTypeName);
            if (moduleType == null)
            {
                WarnInputModuleFallbackOnce("找不到类型 InputSystemUIInputModule");
                return false;
            }

            Component module = null;
            try
            {
                module = eventSystem.gameObject.AddComponent(moduleType);
                var assignDefaultActions = moduleType.GetMethod("AssignDefaultActions", BindingFlags.Public | BindingFlags.Instance);
                if (assignDefaultActions == null)
                {
                    DestroyComponent(module);
                    WarnInputModuleFallbackOnce("找不到方法 AssignDefaultActions");
                    return false;
                }

                // 新模块 OnEnable 已配置默认 actions；只在 inactive 对象未初始化时补齐，避免重复分配引用。
                var asset = moduleType.GetProperty("actionsAsset", BindingFlags.Public | BindingFlags.Instance);
                if (asset == null)
                {
                    throw new MissingMemberException(moduleType.FullName, "actionsAsset");
                }

                if (asset.GetValue(module) == null)
                {
                    assignDefaultActions.Invoke(module, null);
                }

                return true;
            }
            catch (Exception e)
            {
                DestroyComponent(module);
                WarnInputModuleFallbackOnce(e.Message);
                return false;
            }
        }

        /// <summary>每轮执行只解析一次鼠标位置访问成员；缺失时保留不可用结果供调用方明确返回失败。</summary>
        private static bool EnsureMouseReflection()
        {
            if (_hasInitializedMouseReflection)
            {
                return _mouseCurrentProperty != null && _mousePositionProperty != null && _readValueMethod != null;
            }

            _hasInitializedMouseReflection = true;

            var mouseType = Type.GetType(MouseTypeName);
            _mouseCurrentProperty = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _mousePositionProperty = mouseType?.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);

            var controlType = Type.GetType(Vector2ControlTypeName);
            _readValueMethod = controlType?.GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

            if (_mouseCurrentProperty == null || _mousePositionProperty == null || _readValueMethod == null)
            {
                WarnMouseReflectionOnce("反射成员缺失");
                return false;
            }

            return true;
        }
#endif
    }
}
