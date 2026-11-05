using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>图编辑器共享上下文，按宿主实例、层索引或资产 GUID 解析当前配置，并保存浏览和选择状态。</summary>
    internal class Context
    {

        #region 字段

        private RuntimeFsmController _runtimeFsmController;

        /// <summary>上下文保存的节点选择记录，清空选择时一并清除。</summary>
        public List<FsmStateNodeData> SelectNodes = new List<FsmStateNodeData>();

        /// <summary>上下文保存的过渡选择记录。</summary>
        public FsmTransitionData SelectTransition;

        /// <summary>上下文中的过渡预览标记，与 GraphView 自身的连线交互状态分开保存。</summary>
        public bool IsPreviewingTransition = false;
        /// <summary>上下文记录的连线来源节点。</summary>
        public FsmStateNodeData FromState = null;
        /// <summary>上下文记录的悬停节点。</summary>
        public FsmStateNodeData HoverState = null;

        private FsmController _fsmController;

        //private LayoutMatrix4x4Info _matrixInfo;

        private static Context _instance;

        //private List<FSMStateNodeData> current_show_state_node_data = new List<FSMStateNodeData>();
        //private List<FSMTransitionData> current_show_transition_data = new List<FSMTransitionData>();



        #endregion

        #region Layers

        private List<string> _emptyLayers = new List<string>();

        /// <summary>当前配置的浏览祖先路径；没有配置时返回复用的空列表。</summary>
        public List<string> Layers
        {
            get
            {

                if (RuntimeFsmController != null)
                {
                    return RuntimeFsmController.layers;
                }

                return _emptyLayers;
            }
        }

        /// <summary>当前图层的直接父节点名称，基础层为空字符串。</summary>
        public string LayerParent
        {
            get
            {
                if (Layers.Count == 0)
                {
                    return string.Empty;
                }

                return Layers[Layers.Count - 1];
            }
        }

        /// <summary>请求当前配置追加浏览层级，不改变运行时执行状态。</summary>
        /// <param name="layer">要进入的子状态机名称。</param>
        public void AddLayer(string layer)
        {
            if (RuntimeFsmController == null)
            {
                return;
            }

            RuntimeFsmController.AddLayer(layer);
        }

        /// <summary>从当前配置浏览路径中移除指定范围。</summary>
        /// <param name="index">移除起始下标。</param>
        /// <param name="count">移除层级数量。</param>
        public void RemoveLayer(int index, int count)
        {
            if (RuntimeFsmController == null)
            {
                return;
            }

            RuntimeFsmController.RemoveLayer(index, count);
        }

        /// <summary>仅在浏览路径末项与指定名称一致时返回上一层。</summary>
        /// <param name="name">预期的当前末层名称。</param>
        public void RemoveLast(string name)
        {
            if (RuntimeFsmController == null)
            {
                return;
            }

            int index = RuntimeFsmController.layers.Count - 1;
            if (index >= 0 && index < RuntimeFsmController.layers.Count)
            {
                string last = RuntimeFsmController.layers[index];
                if (!last.Equals(name))
                {
                    return;
                }

                RuntimeFsmController.RemoveLayer(RuntimeFsmController.layers.Count - 1, 1);
            }
        }

        #endregion

        #region 属性

        /// <summary>优先解析宿主当前索引的配置；索引越界归零，没有有效宿主配置时按资产 GUID 加载。</summary>
        public RuntimeFsmController RuntimeFsmController
        {
            get
            {
                if (!IsEmpty(RuntimeFsmControllers))
                {
                    if (FsmControllerIndex < 0 || FsmControllerIndex >= RuntimeFsmControllers.Count)
                    {
                        FsmControllerIndex = 0;
                    }

                    RuntimeFsmController controller = RuntimeFsmControllers[FsmControllerIndex];

                    if (controller != null && !string.IsNullOrEmpty(controller.originGuid) && _runtimeFsmControllerGuid != controller.originGuid)
                    {
                        EditorPrefs.SetString("YL.AssetFSMRuntimeFSMControllerGUID", controller.originGuid);
                        _runtimeFsmControllerGuid = controller.originGuid;
                        //Debug.LogFormat("保存的GUID:{0}", _runtimeFSMControllerGUID);
                    }

                    return controller;
                }

                if (_runtimeFsmController == null)
                {
                    string path = AssetDatabase.GUIDToAssetPath(RuntimeFsmControllerGuid);
                    _runtimeFsmController = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
                }

                return _runtimeFsmController;
            }
            internal set
            {
                if (_runtimeFsmController == value)
                {
                    return;
                }

                _runtimeFsmController = value;
            }
        }

        /// <summary>由偏好设置中的 GameObject 实例标识解析的宿主，不创建场景对象。</summary>
        public FsmController FsmController
        {
            get
            {

                if (_fsmController == null)
                {

                    UnityEngine.Object obj = null;

#if UNITY_6000_2_OR_NEWER
                    obj = EditorUtility.EntityIdToObject(FsmControllerInstanceId);
#else
                    obj = EditorUtility.InstanceIDToObject(FsmControllerInstanceId);
#endif

                    GameObject gameObj = obj as GameObject;
                    if (gameObj == null)
                    {
                        return null;
                    }

                    _fsmController = gameObj.GetComponent<FsmController>();
                }

                return _fsmController;
            }
        }


        private List<RuntimeFsmController> _currentFsmControllers = new List<RuntimeFsmController>();

        /// <summary>取得宿主复用的配置列表，或无宿主时由当前资产组成的复用列表，不是稳定快照。</summary>
        public List<RuntimeFsmController> RuntimeFsmControllers
        {
            get
            {
                if (FsmController != null)
                {
                    return FsmController.RuntimeFsmController;
                }
                _currentFsmControllers.Clear();

                if (_runtimeFsmController == null)
                {
                    string path = AssetDatabase.GUIDToAssetPath(RuntimeFsmControllerGuid);
                    _runtimeFsmController = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
                }

                if (_runtimeFsmController != null)
                {
                    _currentFsmControllers.Add(_runtimeFsmController);
                }

                return _currentFsmControllers;
            }
        }

        /// <summary>当前编辑器域中按需创建的共享图上下文。</summary>
        public static Context Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new Context();
                }

                return _instance;
            }
        }

        /// <summary>保存在编辑器偏好中的宿主 GameObject 实例标识；赋值时同步刷新宿主缓存。</summary>
        internal int FsmControllerInstanceId
        {
            get
            {
                return EditorPrefs.GetInt("YL.AssetFSMControllerInstanceID", 0);
            }
            set
            {
                EditorPrefs.SetInt("YL.AssetFSMControllerInstanceID", value);

                UnityEngine.Object obj = null;

#if UNITY_6000_2_OR_NEWER
                obj = EditorUtility.EntityIdToObject(FsmControllerInstanceId);
#else
                obj = EditorUtility.InstanceIDToObject(FsmControllerInstanceId);
#endif

                GameObject gameObj = obj as GameObject;
                if (gameObj != null && gameObj.GetComponent<FsmController>() != null)
                {
                    _fsmController = gameObj.GetComponent<FsmController>();
                }
                else
                {
                    _fsmController = null;
                }
            }
        }

        private string _runtimeFsmControllerGuid;

        /// <summary>偏好设置保存的源配置 GUID；赋值时尝试加载该资产供宿主失效后继续显示。</summary>
        internal string RuntimeFsmControllerGuid
        {
            get
            {
                return EditorPrefs.GetString("YL.AssetFSMRuntimeFSMControllerGUID", string.Empty);
            }
            set
            {
                //Debug.LogFormat("修改GUID:{0}",value);
                EditorPrefs.SetString("YL.AssetFSMRuntimeFSMControllerGUID", value);
                string path = AssetDatabase.GUIDToAssetPath(value);
                RuntimeFsmController = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(path);
            }
        }

        /// <summary>宿主配置列表中的选择下标；变更时尝试刷新对应源资产 GUID。</summary>
        internal int FsmControllerIndex
        {
            get
            {
                return EditorPrefs.GetInt("FSMControllerIndex", 0);
            }
            set
            {
                if (FsmControllerIndex == value)
                {
                    return;
                }

                EditorPrefs.SetInt("FSMControllerIndex", value);

                RefreshRuntimeFsmControllerGuid();
            }
        }

        #endregion

        #region 方法

        /// <summary>仅由共享入口构造上下文，资产和宿主在查询时按需解析。</summary>
        private Context()
        {
        }

        /// <summary>清除上下文节点与过渡选择，同时清空 Unity 的活动对象选择。</summary>
        public void ClearSelections()
        {
            this.SelectNodes.Clear();
            SelectTransition = null;
            Selection.activeObject = null;
            //Selection.activeObject = this.RuntimeFSMController;
        }

        /// <summary>
        /// 取得当前浏览父层的节点定义，不改变运行时当前状态。
        /// </summary>
        /// <returns>配置缓存中的节点列表；没有配置或对应父层时为 null。</returns>
        public List<FsmStateNodeData> GetCurrentShowStateNodeData()
        {

            if (RuntimeFsmController != null)
            {
                return RuntimeFsmController.GetCurrentShowStateNodeData(LayerParent);
            }

            return null;
        }

        /// <summary>取得当前浏览父层中端点有效且层级一致的过渡。</summary>
        /// <returns>配置缓存中的过渡列表；没有配置或匹配项时为 null。</returns>
        public List<FsmTransitionData> GetCurrentShowTransitionData()
        {
            if (RuntimeFsmController != null)
            {
                return RuntimeFsmController.GetCurrentShowTransitionData(LayerParent);
            }

            return null;
        }


        /// <summary>判断配置列表是否为空或只包含失效配置引用。</summary>
        /// <param name="controllers">待检查的配置列表。</param>
        /// <returns>不存在任何有效配置时为 true。</returns>
        public bool IsEmpty(List<RuntimeFsmController> controllers)
        {
            if (controllers == null || controllers.Count == 0)
            {
                return true;
            }

            foreach (var item in controllers)
            {
                if (item != null)
                {
                    return false;
                }
            }

            return true;
        }




        #endregion

        #region 运行时刷新选中的GUID

        private List<RuntimeFsmControllerLayer> _tempLayers = new List<RuntimeFsmControllerLayer>();

        /// <summary>运行时反射读取宿主源层定义，保留所选层的源资产 GUID，使宿主销毁后仍可显示源配置。</summary>
        public void RefreshRuntimeFsmControllerGuid()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (FsmController == null)
            {
                return;
            }

            if (IsEmpty(FsmController.RuntimeFsmController))
            {
                return;
            }

            // 活动列表指向克隆，源资产身份需从宿主的序列化层定义取得，不能用克隆路径反查。
            Type type = typeof(FsmController);
            FieldInfo fieldInfo = type.GetField("runtimeFsmControllerLayers", BindingFlags.NonPublic | BindingFlags.Instance);
            List<RuntimeFsmControllerLayer> layers = fieldInfo.GetValue(FsmController) as List<RuntimeFsmControllerLayer>;

            if (layers == null || layers.Count == 0)
            {
                return;
            }

            _tempLayers.Clear();
            foreach (var layer in layers)
            {
                if (layer.controller == null)
                {
                    continue;
                }

                _tempLayers.Add(layer);
            }

            if (FsmControllerIndex < 0 || FsmControllerIndex >= _tempLayers.Count)
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(_tempLayers[FsmControllerIndex].controller);
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                return;
            }

            RuntimeFsmControllerGuid = guid;

        }

        #endregion

    }
}
