using System;
using System.Collections.Generic;
using YLFramework.Pooling;


namespace YLFramework.Core
{

    /// <summary>管理 Module 内 Model 的类型与 ID 索引、自动编号及注册失败回滚，不使用 Model 对象池。</summary>
    internal class ModelManager
    {
        /// <summary>建立数据归属后执行初始化；失败时解除注册并恢复传入时的业务 ID。</summary>
        public static void AddModel(Module module, Model model)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            module.EnsureCanAddObjects();
            Type type = model.GetType();
            if (model.Module != null)
            {
                if (!ReferenceEquals(model.Module, module))
                {
                    throw new InvalidOperationException("Model is already owned by another Module.");
                }

                if (model.IsInitializing || model.IsExiting)
                {
                    throw new InvalidOperationException("Cannot register a Model during its lifecycle callback.");
                }

                if (ReferenceEquals(GetModel(module, type, model.Id), model))
                {
                    return;
                }

                throw new InvalidOperationException("Model owner and registration do not match.");
            }
            if (module.CreatingModels.Contains(type))
            {
                throw new InvalidOperationException("Cannot register a Model while its constructor is running.");
            }

            module.Models.TryGetValue(type, out var models);
            long originalId = model.Id;
            long id = originalId;
            if (id == -1)
            {
                do
                {
                    id = module.NextModelId;
                    module.NextModelId = id == long.MaxValue ? 0 : id + 1;
                } while (models != null && models.ContainsKey(id));
            }
            if (models != null && models.ContainsKey(id))
            {
                throw new InvalidOperationException($"Model {type.FullName} with ID {id} is already registered.");
            }

            if (models == null)
            {
                models = new Dictionary<long, Model>();
                module.Models.Add(type, models);
            }
            model.Id = id;
            models.Add(id, model);
            model.Module = module;
            model.Registration = new object();
            model.IsInitializing = true;
            try
            {
                model.OnInit();
                model.IsInitializing = false;
            }
            catch (Exception initializationError)
            {
                model.IsInitializing = false;
                try
                {
                    ExitModel(module, model);
                }
                catch (Exception cleanupError)
                {
                    StartUpModuleRequest.AttachCleanupError(initializationError, cleanupError);
                }
                model.Id = originalId;
                throw;
            }
        }

        /// <summary>仅退出归属与索引均匹配的实例；初始化期间拒绝移除，重复退出不再执行钩子。</summary>
        public static void RemoveModel(Module module, Model model)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            if (model == null || !ReferenceEquals(model.Module, module))
            {
                return;
            }

            Type type = model.GetType();
            if (!ReferenceEquals(GetModel(module, type, model.Id), model))
            {
                return;
            }

            if (model.IsExiting)
            {
                return;
            }

            if (model.IsInitializing)
            {
                throw new InvalidOperationException("Cannot remove a Model during initialization.");
            }

            ExitModel(module, model);
        }

        /// <summary>在旧归属仍有效时调用退出钩子，并在 finally 中清除匹配索引与注册身份。</summary>
        private static void ExitModel(Module module, Model model)
        {
            model.IsExiting = true;
            try
            {
                model.OnExit();
            }
            finally
            {
                Type type = model.GetType();
                if (module.Models.TryGetValue(type, out var models) &&
                    models.TryGetValue(model.Id, out var registered) && ReferenceEquals(registered, model))
                {
                    models.Remove(model.Id);
                    if (models.Count == 0)
                    {
                        module.Models.Remove(type);
                    }
                }
                model.Module = null;
                model.Registration = null;
                model.IsExiting = false;
            }
        }

        /// <summary>查询本模块精确类型的唯一实例，不创建；缺失返回 null，多实例要求调用方指定 ID。</summary>
        public static Model GetModel(Module module, Type modelType)
        {
            ValidateModelType(modelType);
            if (module.Models.ContainsKey(modelType) && module.Models[modelType].Count > 1)
            {
                throw new Exception(string.Format("数据:{0}存在多份，请根据id查询!", modelType.FullName));
            }

            if (module.Models.ContainsKey(modelType) && module.Models[modelType].Count == 1)
            {
                foreach (var item in module.Models[modelType].Values)
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>按本模块的精确类型与业务 ID 查询，不存在时返回 null。</summary>
        public static Model GetModel(Module module, Type modelType, long id)
        {
            ValidateModelType(modelType);
            if (module.Models.ContainsKey(modelType) && module.Models[modelType].ContainsKey(id))
            {
                return module.Models[modelType][id];
            }

            return null;
        }



        /// <summary>用临时列表保存本模块数据快照并逐个移除；退出异常会中断后续处理，但仍归还列表。</summary>
        public static void ClearModel(Module module)
        {

            List<Model> tempModels = ListPool<Model>.Allocate();
            try
            {
                foreach (var item in module.Models.Values)
                {
                    foreach (var model in item.Values)
                    {
                        tempModels.Add(model);
                    }
                }

                foreach (var item in tempModels)
                {
                    RemoveModel(module, item);
                }
            }
            finally
            {
                ListPool<Model>.Recycle(tempModels);
            }

        }

        /// <summary>快照筛选指定类型及派生类型的数据后逐个移除，所有临时列表均在 finally 归还。</summary>
        public static void ClearModel(Module module, Type modelType)
        {
            ValidateModelType(modelType);
            List<Type> tempModelKeys = ListPool<Type>.Allocate();
            try
            {
                foreach (var key in module.Models.Keys)
                {
                    if (key == modelType || key.IsSubclassOf(modelType))
                    {
                        tempModelKeys.Add(key);
                    }
                }

                List<Model> tempModels = ListPool<Model>.Allocate();
                try
                {
                    foreach (var key in tempModelKeys)
                    {
                        if (!module.Models.ContainsKey(key))
                        {
                            continue;
                        }

                        Dictionary<long, Model> models = module.Models[key];
                        foreach (Model item in models.Values)
                        {
                            tempModels.Add(item);
                        }

                    }

                    foreach (var item in tempModels)
                    {
                        RemoveModel(module, item);
                    }
                }
                finally
                {
                    ListPool<Model>.Recycle(tempModels);
                }
            }
            finally
            {
                ListPool<Type>.Recycle(tempModelKeys);
            }


        }

        /// <summary>复制本模块中指定类型及派生类型的注册实例，不改变任何实例的归属。</summary>
        public static List<Model> GetModels(Module module, Type modelType)
        {
            ValidateModelType(modelType);
            List<Model> models = new List<Model>();

            foreach (var item in module.Models.Keys)
            {
                if (item == modelType || item.IsSubclassOf(modelType))
                {
                    foreach (var model in module.Models[item].Values)
                    {
                        models.Add(model);
                    }
                }
            }


            return models;
        }

        /// <summary>复用本模块精确类型的唯一实例，缺失时构造并初始化；退出中拒绝复用。</summary>
        public static Model LoadModel(Module module, Type modelType)
        {
            ValidateModelType(modelType);

            Model model = GetModel(module, modelType);
            if (model != null)
            {
                if (model.IsExiting)
                {
                    throw new InvalidOperationException("Cannot load a Model during exit.");
                }

                return model;
            }
            module.EnsureCanAddObjects();
            if (modelType.IsAbstract || modelType.ContainsGenericParameters || modelType.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new ArgumentException("Model type must be concrete and have a public parameterless constructor.", nameof(modelType));
            }

            if (!module.CreatingModels.Add(modelType))
            {
                throw new InvalidOperationException("Recursive Model construction is not allowed.");
            }

            try
            {
                model = (Model)Activator.CreateInstance(modelType);
            }
            finally
            {
                module.CreatingModels.Remove(modelType);
            }
            AddModel(module, model);
            return model;
        }

        /// <summary>以泛型具体类型加载本模块的唯一数据实例，不向其他模块回退。</summary>
        public static T LoadModel<T>(Module module) where T : Model
        {
            return LoadModel(module, typeof(T)) as T;
        }

        /// <summary>拒绝空类型及非 Model 类型，不在查询入口要求类型可构造。</summary>
        private static void ValidateModelType(Type modelType)
        {
            if (modelType == null)
            {
                throw new ArgumentNullException(nameof(modelType));
            }

            if (!typeof(Model).IsAssignableFrom(modelType))
            {
                throw new ArgumentException("Type must inherit from Model.", nameof(modelType));
            }
        }

    }

}
