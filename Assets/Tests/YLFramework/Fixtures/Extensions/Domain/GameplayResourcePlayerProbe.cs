using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Equipment;
using YLFramework.Mission;
using YLFramework.Resource;
using YLFramework.Skill;

namespace YLFramework.Tests.DomainAcceptance
{
    public sealed class GameplayResourceEquipment : IEquipment
    {
        public IEquippable Equipper
        {
            get; set;
        }
        public EquipmentConfigData EquipmentData
        {
            get; set;
        }
        public readonly List<string> Calls = new List<string>();
        public bool CallbackStateValid = true;

        public void OnLoaded()
        {
            CallbackStateValid &= EquipmentData && Equipper == null;
            Calls.Add("loaded");
        }

        public void OnEquip()
        {
            CallbackStateValid &= EquipmentData && Equipper != null && Equipper.Equipments.Contains(this);
            Calls.Add("equip");
        }

        public void OnUnequip()
        {
            CallbackStateValid &= EquipmentData && Equipper != null && !Equipper.Equipments.Contains(this);
            Calls.Add("unequip");
        }

        public void OnUnloaded()
        {
            CallbackStateValid &= EquipmentData && Equipper == null;
            Calls.Add("unloaded");
        }
    }

    public sealed class GameplayResourcePlayerProbe : MonoBehaviour, IEquippableSkill, IAddableBuff, IEquippable
    {
        public string projectId;
        public string contentHash;
        public string contentToken;

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 2;
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public string contentHash;
            public bool ledgerMeasured;
            public string resourceState;
            public int pid;
            public int leaseCount = -1;
            public int scopeCount = -1;
            public int requestCount = -1;
            public int pendingWorkCount = -1;
            public int drainingHandleCount = -1;
            public string skillAssembly;
            public string buffAssembly;
            public string missionAssembly;
            public string equipmentAssembly;
            public string probeAssembly;
            public string[] frameworkAssemblies;
            public string contentToken;
            public int definitionAssetCount;
            public bool fourRootsLoaded;
            public bool skillLifecycle;
            public bool buffLifecycle;
            public bool missionLifecycle;
            public bool rewardDefinitions;
            public bool equipmentLifecycle;
            public bool borrowedOwnership;
            public bool configsUnchanged;
            public bool independentRootSurvived;
            public bool managersClosed;
            public bool resourceReturned;
            public int skillCompletions;
            public int skillInterruptions;
            public int buffAdds;
            public int buffRemoves;
            public int missionCompletions;
            public string[] equipmentCallbacks;
        }

        private readonly Report _report = new Report();
        private ResourceStartup _startup;
        private ResourceSystem _resources;
        private Task _configLoads;
        private bool _ownsDomains;
        private bool _buffCallbacksValid = true;
        private string _runtimeError;

        public Dictionary<int, SkillBase> Skills { get; } = new Dictionary<int, SkillBase>();
        public List<BuffBase> Buffs { get; } = new List<BuffBase>();
        public List<IEquipment> Equipments { get; } = new List<IEquipment>();
        public bool CanRelease() => true;
        public bool CanAdd(int id) => true;
        public bool CanEquip(IEquipment equipment) => true;

        public void OnAddBuff(BuffBase buff)
        {
            _buffCallbacksValid &= ReferenceEquals(buff.Owner, this) && Buffs.Contains(buff);
            _report.buffAdds++;
        }

        public void OnRemoveBuff(BuffBase buff)
        {
            _buffCallbacksValid &= ReferenceEquals(buff.Owner, this) && !Buffs.Contains(buff);
            _report.buffRemoves++;
        }

        private IEnumerator Start()
        {
            Application.logMessageReceived += ObserveError;
            var work = Run();
            while (true)
            {
                bool more = false;
                try
                {
                    more = work.MoveNext();
                }
                catch (Exception error) { RecordFailure(error); }
                if (!more || _report.error != null)
                {
                    break;
                }
                yield return work.Current;
            }
            try
            {
                (work as IDisposable)?.Dispose();
            }
            catch (Exception error) { RecordFailure(error); }

            if (_ownsDomains)
            {
                foreach (Action close in new Action[] { () => SkillManager.CloseHost(this), () => BuffManager.CloseHost(this),
                    () => EquipmentManager.CloseHost(this), SkillManager.Shutdown, BuffManager.Shutdown, MissionManager.Shutdown, EquipmentManager.Shutdown })
                {
                    try
                    {
                        close();
                    }
                    catch (Exception error) { RecordFailure(error); }
                }
            }
            if (_configLoads != null)
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!_configLoads.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;
                if (!_configLoads.IsCompleted)
                    RecordFailure(new InvalidOperationException("Domain configuration tasks did not drain after shutdown."));
                else
                    _ = _configLoads.Exception;
            }
            if (_startup != null)
            {
                ResourceRequest<bool> shutdown = null;
                try
                {
                    shutdown = _startup.StopAsync();
                }
                catch (Exception error) { RecordFailure(error); }
                if (shutdown != null)
                {
                    using (shutdown)
                    {
                        float deadline = Time.realtimeSinceStartup + 10;
                        while (!shutdown.IsDone && Time.realtimeSinceStartup < deadline)
                        {
                            yield return null;
                        }
                        if (shutdown.Status != ResourceRequestStatus.Succeeded)
                        {
                            RecordFailure(new InvalidOperationException("Owned Resource shutdown failed or timed out: " + shutdown.Error));
                        }
                    }
                }
                try
                {
                    var diagnostics = _resources.GetDiagnostics();
                    _report.leaseCount = diagnostics.LeaseCount;
                    _report.scopeCount = diagnostics.ScopeCount;
                    _report.requestCount = diagnostics.RequestCount;
                    _report.pendingWorkCount = diagnostics.PendingWorkCount;
                    _report.drainingHandleCount = diagnostics.DrainingHandleCount;
                    _report.resourceState = diagnostics.State.ToString();
                    _report.ledgerMeasured = true;
                    Require(diagnostics.Errors.Count == 0, "Resource cleanup reported an error.");
                }
                catch (Exception error) { RecordFailure(error); }
            }
            try
            {
                Require(_runtimeError == null, _runtimeError);
                _report.managersClosed = _ownsDomains && SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount == 0 &&
                    SkillManager.HostCount + BuffManager.HostCount + BuffManager.EffectOwnerCount == 0 && EquipmentManager.GetConfig("Equipment") == null &&
                    Skills.Count + Buffs.Count + Equipments.Count == 0;
                Require(_report.managersClosed && _buffCallbacksValid && _report.buffAdds == 5 && _report.buffRemoves == 5, "Owned managers, host collections or paired Buff callbacks leaked.");
                _report.resourceReturned = _resources != null && _resources.State == ResourceSystemState.Closed && ResourceSystem.Current == null &&
                    _report.leaseCount == 0 && _report.scopeCount == 0 && _report.requestCount == 0 &&
                    _report.pendingWorkCount == 0 && _report.drainingHandleCount == 0;
                Require(_report.resourceReturned, "Owned Resource responsibilities or authority survived shutdown.");
                _report.frameworkAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name)
                    .Where(name => name.StartsWith("YLFramework", StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                Require(_report.frameworkAssemblies.SequenceEqual(new[] { "YLFramework.Buff", "YLFramework.Equipment", "YLFramework.Mission", "YLFramework.Runtime",
                    "YLFramework.Skill", "YLFramework.Tests.Domain.Probes" }), "The real Player loaded an unexpected YL assembly set.");
            }
            catch (Exception error) { RecordFailure(error); }
            Application.logMessageReceived -= ObserveError;
            Finish();
        }

        private IEnumerator Run()
        {
            Require(!Application.isEditor && Application.isBatchMode && Application.platform == RuntimePlatform.WindowsPlayer, "A real standalone Windows Player is required.");
            Require(Guid.TryParseExact(contentToken, "N", out _), "Missing new publication token.");
            _report.skillAssembly = typeof(SkillManager).Assembly.GetName().Name;
            _report.buffAssembly = typeof(BuffManager).Assembly.GetName().Name;
            _report.missionAssembly = typeof(MissionManager).Assembly.GetName().Name;
            _report.equipmentAssembly = typeof(EquipmentManager).Assembly.GetName().Name;
            _report.probeAssembly = GetType().Assembly.GetName().Name;
            _report.runtimeAssembly = _report.skillAssembly;
            Require(_report.skillAssembly == "YLFramework.Skill" && _report.buffAssembly == "YLFramework.Buff" &&
                _report.missionAssembly == "YLFramework.Mission" && _report.equipmentAssembly == "YLFramework.Equipment" &&
                _report.probeAssembly == "YLFramework.Tests.Domain.Probes", "All four domains must execute from their new Runtime assemblies.");
            Require(ResourceSystem.Current == null, "Do not take over an existing Resource authority.");
            Require(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount == 0 &&
                SkillManager.HostCount + BuffManager.HostCount + BuffManager.EffectOwnerCount == 0 && EquipmentManager.GetConfig("Equipment") == null, "Do not clear another domain owner.");
            _report.contentHash = ReadContentHash();
            Require(_report.contentHash == contentHash, "The deployed official Addressables bytes differ from the frozen build.");
            _ownsDomains = true;
            _startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            using (var initialized = _startup.StartAsync())
            {
                double startupDeadline = Time.realtimeSinceStartupAsDouble + 15;
                while (!initialized.IsDone && Time.realtimeSinceStartupAsDouble < startupDeadline)
                    yield return null;
                Require(initialized.Status == ResourceRequestStatus.Succeeded, initialized.Error?.ToString() ?? "Resource startup timed out.");
                _resources = initialized.TakeResult().Resources;
            }
            using var independentRequest = _resources.AcquireAsync<SkillsConfig>(projectId + "/skills");
            double loadDeadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!independentRequest.IsDone && Time.realtimeSinceStartupAsDouble < loadDeadline)
                yield return null;
            Require(independentRequest.Status == ResourceRequestStatus.Succeeded, independentRequest.Error?.ToString() ?? "Independent root loading timed out.");
            using var independent = independentRequest.TakeResult();
            _configLoads = Task.WhenAll(
                SkillManager.LoadConfigAsync(_resources, new ResourceAddress(projectId, "skills")),
                BuffManager.LoadConfigAsync(_resources, new ResourceAddress(projectId, "buffs")),
                MissionManager.LoadConfigAsync(_resources, new ResourceAddress(projectId, "missions")),
                EquipmentManager.LoadConfigAsync(_resources, new ResourceAddress(projectId, "equipment")));
            loadDeadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!_configLoads.IsCompleted && Time.realtimeSinceStartupAsDouble < loadDeadline)
                yield return null;
            Require(_configLoads.IsCompletedSuccessfully, _configLoads.Exception?.ToString() ?? "Domain configuration loading timed out.");
            var skills = SkillManager.GetConfig("Skills");
            var buffs = BuffManager.GetConfig("Buffs");
            var missions = MissionManager.GetConfig("Missions");
            var equipment = EquipmentManager.GetConfig("Equipment");
            Require(skills && buffs && missions && equipment && ReferenceEquals(skills, independent.Asset), "Formal LoadConfig did not load all four roots.");
            Require(skills.GetType().Assembly.GetName().Name == _report.skillAssembly && buffs.GetType().Assembly.GetName().Name == _report.buffAssembly &&
                missions.GetType().Assembly.GetName().Name == _report.missionAssembly && equipment.GetType().Assembly.GetName().Name == _report.equipmentAssembly, "The loaded roots do not belong to the four reported Runtime assemblies.");
            Require(skills.skills.Count == 1 && buffs.buffs.Count == 2 && missions.missions.Count == 2 && missions.parameters.Count == 1 && equipment.Equipments.Count == 1, "Published definition counts changed.");
            Require(skills.skills[0].id == 101 && skills.skills[0].duration == 0.05f && skills.skills[0].cooldownSeconds == 0.05f &&
                buffs.buffs[0].id == 201 && buffs.buffs[0].mode == BuffMode.Single && buffs.buffs[1].id == 202 && buffs.buffs[1].mode == BuffMode.Additive &&
                buffs.buffs.All(data => data.duration == 0.05f && data.effects.Count == 1 && data.effects[0].id == 301) &&
                equipment.Equipments[0].id == 401, "The actual official content differs from the authored scenario.");
            Require(skills.skills.All(data => data.parent == skills && data.description == contentToken) &&
                buffs.buffs.All(data => data.parent == buffs && data.description == contentToken && data.effects[0].description == contentToken) &&
                missions.missions.All(data => data.parent == missions && data.description == contentToken) &&
                missions.parameters[0].description == contentToken && equipment.Equipments[0].parent == equipment && equipment.Equipments[0].description == contentToken, "New content token or serialized parent links were lost.");
            var loaded = _resources.GetDiagnostics();
            Require(loaded.LeaseCount == 5 && loaded.Leases.Select(lease => lease.Key).Distinct(StringComparer.Ordinal).Count() == 4 &&
                loaded.Leases.Count(lease => lease.Key == projectId + "/skills") == 2 && loaded.RequestCount == 0,
                "Each manager must own its root lease independently of the caller's Skill root lease.");
            _report.contentToken = contentToken;
            _report.fourRootsLoaded = true;
            var definitions = new ScriptableObject[] { skills, buffs, missions, equipment }
                .Concat(skills.skills).Concat(buffs.buffs).Concat(buffs.buffs.SelectMany(data => data.effects))
                .Concat(missions.missions).Concat(equipment.Equipments).Distinct().ToArray();
            var types = new[] { typeof(SkillsConfig), typeof(SkillConfigData), typeof(BuffsConfig), typeof(BuffConfigData), typeof(EffectData),
                typeof(MissionsConfig), typeof(MissionConfigData), typeof(EquipmentsConfig), typeof(EquipmentConfigData) };
            _report.definitionAssetCount = definitions.Length;
            Require(_report.definitionAssetCount == 11 && definitions.All(asset => asset && types.Contains(asset.GetType())),
                "The deployed object graph lost a root/row/effect or retained an old Runtime type.");
            var before = definitions.Select(value => JsonUtility.ToJson(value)).ToArray();

            var skill = SkillManager.AddSkills(this, 101);
            Require(skill is ReceiverSkill && !ReferenceEquals(skill.SkillData, skills.skills[0]), "The loaded Skill did not create an independent runtime definition.");
            skill.SkillData.description = "runtime-skill";
            int releaseTicks = 0, cooldowns = 0;
            skill.Releasing += (_, __) => releaseTicks++;
            skill.ReleaseCompleted += _ => _report.skillCompletions++;
            skill.CooldownCompleted += _ => cooldowns++;
            skill.Interrupted += _ => _report.skillInterruptions++;
            Require(skill.Release() == ReleaseSkillStatus.Success && skill.IsReleasing && !skill.IsCooldown &&
                skill.Release() == ReleaseSkillStatus.Releasing, "Loaded Skill release state was not committed.");
            float deadline = Time.realtimeSinceStartup + 10;
            while (skill.IsReleasing || !skill.IsCooldown)
            {
                Require(Time.realtimeSinceStartup < deadline, "Real Skill duration/cooldown updates timed out.");
                yield return null;
            }
            Require(releaseTicks > 0 && cooldowns == 1 && _report.skillCompletions == 1, "Skill did not naturally complete and cool down through real Update.");
            Require(skill.Release() == ReleaseSkillStatus.Success, "Cooled Skill could not release again.");
            skill.Interrupt();
            Require(!skill.IsReleasing && !skill.IsCooldown && _report.skillInterruptions == 1 && _report.skillCompletions == 1, "Interruption must not masquerade as natural completion.");
            SkillManager.RemoveSkills(this, 101);
            Require(skill.Releaser == null && skill.SkillData == null, "Removed Skill retained its owner or clone.");
            var pooledSkill = SkillManager.AddSkills(this, 101);
            Require(ReferenceEquals(skill, pooledSkill) && pooledSkill.IsCooldown && !pooledSkill.IsReleasing && pooledSkill.ReleasedTimer == 0 &&
                pooledSkill.ReleaseCompleted == null && pooledSkill.Interrupted == null && pooledSkill.SkillData.description == contentToken, "Skill pooling retained state, callbacks or mutated definition data.");
            _report.skillLifecycle = true;

            var first = BuffManager.AddBuff(this, 201);
            var replacement = BuffManager.AddBuff(this, 201);
            Require(first is ReceiverBuff && replacement is ReceiverBuff && !ReferenceEquals(first, replacement) && Buffs.Count == 1 &&
                first.Owner == null && first.BuffData == null && _report.buffRemoves == 1, "Single Buff must replace and return the prior instance.");
            var layerA = BuffManager.AddBuff(this, 202, new object());
            var layerB = BuffManager.AddBuff(this, 202);
            Require(layerA is ReceiverBuff && layerB is ReceiverBuff && Buffs.Count == 3 && !ReferenceEquals(layerA, layerB) &&
                !ReferenceEquals(layerA.BuffData, buffs.buffs[1]) && !ReferenceEquals(layerA.BuffData.effects[0], layerB.BuffData.effects[0]), "Additive Buffs must have distinct runtime definitions and effects.");
            layerA.BuffData.description = "runtime-buff";
            layerA.BuffData.effects[0].description = "runtime-effect";
            Require(layerB.BuffData.effects[0].description == contentToken && BuffManager.ContainsEffect(this, 301), "A Buff layer mutated another layer or lost its effect index.");
            deadline = Time.realtimeSinceStartup + 10;
            while (Buffs.Count != 0)
            {
                Require(Time.realtimeSinceStartup < deadline, "Natural Buff expiry through real Update timed out.");
                yield return null;
            }
            Require(_report.buffAdds == 4 && _report.buffRemoves == 4 && !BuffManager.ContainsEffect(this, 301) &&
                replacement.Owner == null && layerA.Owner == null && layerB.Owner == null, "Natural Buff removal did not return all three live instances.");
            var pooledBuff = BuffManager.AddBuff(this, 201, 0f);
            Require(new[] { replacement, layerA, layerB }.Any(value => ReferenceEquals(value, pooledBuff)) && pooledBuff.ExistedTime == 0 &&
                pooledBuff.UserData == null && pooledBuff.BuffData.description == contentToken && pooledBuff.BuffData.effects[0].description == contentToken, "Buff pooling retained prior timers, user data or effects.");
            _report.buffLifecycle = true;

            var missionA = MissionManager.GetMission("Missions", 501);
            var missionB = MissionManager.GetMission("Missions", 502);
            Require(missionA is ReceiverMission && missionB is ReceiverMission && !ReferenceEquals(missionA.MissionConfig, missions.missions[0]) &&
                ReferenceEquals(missionA.MissionConfig.conditions[0].Param, missionB.MissionConfig.conditions[0].Param) &&
                !ReferenceEquals(missionA.MissionConfig.conditions[0].Param, missions.parameters[0]), "Loaded Missions did not share an isolated runtime parameter.");
            missionA.MissionConfig.description = "runtime-mission";
            Action<MissionBase> completed = mission => { if (mission.State == MissionState.Completed) { _report.missionCompletions++; } };
            missionA.StatusChanged += completed;
            missionB.StatusChanged += completed;
            MissionManager.SetFloat("Missions", "amount", 1);
            Require(MissionManager.GetFloat("Missions", "amount") == 0, "Idle-only parameter writes must be rejected by configuration.");
            missionA.State = missionB.State = MissionState.InProgress;
            MissionManager.SetFloat("Missions", "amount", 1);
            Require(missionA.State == MissionState.Completed && missionB.State == MissionState.InProgress && MissionManager.GetFloat("Missions", "amount") == 1, "Completing one Mission reset another Mission's shared parameter.");
            MissionManager.SetFloat("Missions", "amount", 0);
            Require(MissionManager.GetFloat("Missions", "amount") == 1, "The published onlySetLarger constraint was ignored.");
            MissionManager.SetFloat("Missions", "amount", 2);
            Require(missionB.State == MissionState.Completed && MissionManager.GetFloat("Missions", "amount") == 0 && _report.missionCompletions == 2, "The final Mission did not complete and reset the shared parameter.");
            // These are reward definitions only. No inventory, Save package or award side effect is claimed.
            Require(new[] { missionA, missionB }.All(mission => mission.MissionConfig.rewards.Count == 1 && mission.MissionConfig.rewards[0].id == 7 && mission.MissionConfig.rewards[0].count == 2), "Published reward definitions changed.");
            _report.rewardDefinitions = true;
            MissionManager.Reset("Missions");
            Require(missionA.State == MissionState.NotStarted && missionB.State == MissionState.NotStarted && missionA.StatusChanged == null &&
                missionB.StatusChanged == null && MissionManager.GetFloat("Missions", "amount") == 0, "Mission Reset retained state or business callbacks.");
            missionA.StatusChanged += completed;
            missionB.StatusChanged += completed;
            missionA.State = missionB.State = MissionState.InProgress;
            MissionManager.SetFloat("Missions", "amount", 2);
            Require(missionA.State == MissionState.Completed && missionB.State == MissionState.Completed && _report.missionCompletions == 4 &&
                MissionManager.GetFloat("Missions", "amount") == 0, "Reset did not rebind both real condition subscriptions.");
            _report.missionLifecycle = true;

            var owned = (GameplayResourceEquipment)EquipmentManager.CreateEquipment(401);
            Require(ReferenceEquals(owned.EquipmentData, equipment.Equipments[0]) && owned.Equipper == null, "Script Equipment must borrow its published definition.");
            Require(EquipmentManager.Equip(this, owned) == EquipState.Success, "Owned Equipment did not attach.");
            EquipmentManager.Unequip(owned, false);
            Require(Equipments.Count == 0 && owned.Equipper == null && owned.EquipmentData, "Detach without release lost Equipment ownership.");
            Require(EquipmentManager.Equip(this, owned) == EquipState.Success, "Detached owned Equipment could not reattach.");
            EquipmentManager.DestroyEquipment(owned);
            _report.equipmentCallbacks = owned.Calls.ToArray();
            Require(owned.CallbackStateValid && owned.Equipper == null && owned.EquipmentData == null && Equipments.Count == 0 &&
                _report.equipmentCallbacks.SequenceEqual(new[] { "loaded", "equip", "unequip", "equip", "unequip", "unloaded" }), "Equipment lifecycle order or committed callback state failed.");
            _report.equipmentLifecycle = true;
            var borrowed = new GameplayResourceEquipment { EquipmentData = equipment.Equipments[0] };
            Require(EquipmentManager.Equip(this, borrowed) == EquipState.Success, "Caller-owned Equipment did not attach.");
            EquipmentManager.CloseHost(this);
            Require(borrowed.CallbackStateValid && borrowed.Equipper == null && ReferenceEquals(borrowed.EquipmentData, equipment.Equipments[0]) &&
                borrowed.Calls.SequenceEqual(new[] { "equip", "unequip" }), "CloseHost acquired or unloaded caller-owned Equipment.");
            _report.borrowedOwnership = true;

            Require(definitions.Select(value => JsonUtility.ToJson(value)).SequenceEqual(before) && missions.parameters[0].Value == 0 &&
                missions.parameters[0].ValueChanged == null && missions.missions.All(data => data.conditions.All(condition => condition.Param == null && !condition.IsMet)),
                "Runtime operations polluted a published root, row, effect, parameter, condition or reward definition.");
            _report.configsUnchanged = true;
            SkillManager.CloseHost(this);
            BuffManager.CloseHost(this);
            Require(pooledSkill.Releaser == null && pooledSkill.SkillData == null && pooledBuff.Owner == null && pooledBuff.BuffData == null, "CloseHost retained pooled clones.");
            SkillManager.Shutdown();
            BuffManager.Shutdown();
            MissionManager.Shutdown();
            EquipmentManager.Shutdown();
            Require(missionA.MissionConfig == null && missionB.MissionConfig == null && missionA.StatusChanged == null && missionB.StatusChanged == null, "Mission shutdown retained clones or callbacks.");
            Require(independent.IsValid && independent.Asset && independent.Asset.skills[0].description == contentToken &&
                _resources.GetDiagnostics().LeaseCount == 1 && _resources.GetDiagnostics().Leases[0].Key == projectId + "/skills",
                "A manager consumed an independent root lease or leaked its own.");
            _report.independentRootSurvived = true;
            yield return null;
        }

        private static string ReadContentHash()
        {
            string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "aa"));
            Require(Directory.Exists(root), "The official local Addressables content is missing.");
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Require(files.Length != 0 && files.Any(path => Path.GetFileName(path) == "settings.json"), "The official runtime settings are missing.");
            var entries = files.Select(path =>
            {
                for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
                    Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "Do not read linked content as frozen Player evidence.");
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                return (Path: path.Substring(root.Length + 1).Replace('\\', '/'), Digest: digest);
            }).OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Path + "=" + entry.Digest);
            using var algorithm = SHA256.Create();
            return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).Replace("-", "").ToLowerInvariant();
        }

        private void ObserveError(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
            {
                _runtimeError = (_runtimeError ?? "") + type + ": " + message + "\n" + stack + "\n";
            }
        }

        private void RecordFailure(Exception error) => _report.error = _report.error == null ? error.ToString() : _report.error + "\n" + error;

        private void Finish()
        {
            bool persisted = false;
            try
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-ylFeatureResult");
                Require(index > 0 && index < args.Length - 1 && args.Count(value => value == "-ylFeatureResult") == 1, "The exact owned result argument is missing or duplicated.");
                Require(Path.IsPathFullyQualified(args[index + 1]), "The result must use an absolute owned path.");
                string path = Path.GetFullPath(args[index + 1]);
                string parent = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                Require(string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetFileName(path) == "result-1.json" || Path.GetFileName(path) == "result-2.json") && !File.Exists(path) && !Directory.Exists(path), "Do not overwrite or escape Player output.");
                for (var directory = new DirectoryInfo(parent); directory != null; directory = directory.Parent)
                {
                    Require((directory.Attributes & FileAttributes.ReparsePoint) == 0, "Do not write through linked Player output.");
                }
                _report.status = _report.error == null ? "Passed" : "Failed";
                _report.isEditor = Application.isEditor;
                _report.platform = Application.platform.ToString();
                _report.unityVersion = Application.unityVersion;
                _report.pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(JsonUtility.ToJson(_report, true));
                writer.Flush();
                persisted = true;
            }
            finally { Application.Quit(_report.error == null && persisted ? 0 : 1); }
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition)
            {
                throw new InvalidOperationException(reason);
            }
        }
    }
}
