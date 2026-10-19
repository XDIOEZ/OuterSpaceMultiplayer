using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Script.FairyZGame.FairyGame.SchedulingAlloc;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        internal bool IsBuildingConfigurationEntityInMap(long mapId, long uid)
        {
            FWStarMapInfo map;
            return Maps.TryGetValue(mapId, out map) && map.starSpread != null
                && replicator.Beans(map.starSpread).Any(bean => NativeWire.Uid(bean) == uid);
        }
    }
    // Explicit settings only. Neither a remote method name nor a remote field path is executed.
    internal static class NativeBuildingCommands
    {
        internal sealed class Settings
        {
            public string Schema = "BuildingSettings.V1";
            public string Operation;
            public List<long> Targets = new List<long>();
            public long Reference = -1;
            public List<long> Roles = new List<long>();
            public List<string> Values = new List<string>();
            public List<sbyte> Qualities = new List<sbyte>();
            public int Number, OtherNumber;
            public float Durability;
            public sbyte Priority;
            public bool Flag;
        }
        internal static bool Bypass { get { GameBridge b = GameBridge.Active; return b == null || b.Applying || b.Executing || b.Seeding; } }
        internal static void Submit(Settings settings, Action confirmed = null)
        {
            GameBridge bridge = GameBridge.Active;
            if (settings.Targets.Count == 0) throw new InvalidOperationException("没有可配置的建筑或区域。");
            var command = new GameCommand { Kind = CommandKind.ConfigureBuilding, MapId = bridge.CurrentMapId,
                TargetEntityId = settings.Targets[0], PayloadJson = NativeWire.Json(settings) };
            if (confirmed != null) bridge.Context.RegisterCommandCallback(command.CommandId, result => { if (result.Success && result.Applied) confirmed(); });
            CommandResult result = bridge.Context.SubmitCommand(command);
            if (!result.Success) throw new InvalidOperationException("建筑设置未提交：" + result.Error);
        }
        private static T Scene<T>(GameBridge bridge, long uid) where T : class
        { return bridge.FindSceneObject(uid) as T ?? throw new InvalidOperationException("建筑对象已不存在或类型不匹配：" + uid); }
        private static T Bean<T>(GameBridge bridge, long uid) where T : class
        { return bridge.FindPersistentBean(uid) as T ?? throw new InvalidOperationException("建筑数据已不存在或类型不匹配：" + uid); }
        internal static bool TryExecute(GameBridge bridge, GameCommand command)
        {
            if (command.Kind != CommandKind.ConfigureBuilding || JObject.Parse(command.PayloadJson ?? "{}").Value<string>("Schema") != "BuildingSettings.V1") return false;
            Settings data = NativeWire.Parse<Settings>(command.PayloadJson);
            if (data.Targets == null || data.Targets.Count == 0 || data.Targets.Count > 512 || data.Targets[0] != command.TargetEntityId || data.Targets.Distinct().Count() != data.Targets.Count)
                throw new InvalidOperationException("无效的建筑配置目标。");
            if (data.Values == null || data.Values.Count > 4096 || data.Values.Any(v => v == null || v.Length > 256) || data.Roles == null || data.Roles.Count > 4096 || data.Qualities == null || data.Qualities.Count > 64)
                throw new InvalidOperationException("建筑配置数据超出界限。");
            foreach (long uid in data.Targets)
            {
                if (!bridge.IsBuildingConfigurationEntityInMap(command.MapId, uid)) throw new InvalidOperationException("建筑配置目标不在当前权威地图中：" + uid);
                FWBaseBean ownedCommand = bridge.FindPersistentBean(uid) as FWBaseBean;
                if ((ownedCommand is FWPlanBean || ownedCommand is FWAreaZoneBean) && !NativeGroundCommandOwnership.CanEdit(bridge, command.PlayerId, uid))
                    throw new InvalidOperationException("只能修改自己发布的命令或区域。");
                if (!bridge.Context.HasPermission(command.PlayerId, command.MapId, uid, Permission.UseBuilding)) throw new InvalidOperationException("没有该建筑的配置权限：" + uid);
            }
            if ((data.Operation == "Paste" || data.Operation == "BindStore") && data.Reference >= 0 && !bridge.IsBuildingConfigurationEntityInMap(command.MapId, data.Reference))
                throw new InvalidOperationException("配置来源或绑定区域不在当前权威地图中：" + data.Reference);
            // Resolve and validate every target before mutating any of them.
            switch (data.Operation)
            {
                case "Wall": foreach (long uid in data.Targets) Bean<FWWallBean>(bridge, uid); break;
                case "Trough":
                    foreach (long uid in data.Targets)
                    {
                        FWTroughBean trough = Bean<FWTroughBean>(bridge, uid);
                        if (data.Number < 0 || data.OtherNumber < 0 || data.Number > trough.getMaxSupply(AnimalDef.SUBTYPES.TYPE_EAT_VEGA) || data.OtherNumber > trough.getMaxSupply(AnimalDef.SUBTYPES.TYPE_EAT_MEAT)) throw new InvalidOperationException("食槽储备设置超过原版容量。");
                    }
                    foreach (string id in data.Values)
                    { T_FoodDef food = T_FoodDef.getDataID(id); if (food == null || (food.troughType != T_FoodDef.TroughTypes._槽料素食 && food.troughType != T_FoodDef.TroughTypes._槽料肉食)) throw new InvalidOperationException("食槽配置包含不适用食物：" + id); }
                    break;
                case "StoreBox": foreach (long uid in data.Targets) Bean<FWBox>(bridge, uid); ValidateStore(data); break;
                case "StoreZone": foreach (long uid in data.Targets) Bean<FWAreaZoneBean>(bridge, uid); ValidateStore(data); break;
                case "AnimalZone": foreach (long uid in data.Targets) Bean<FWAnimalAreaZoneBean>(bridge, uid); ValidateStore(data); break;
                case "FuelProduct": foreach (long uid in data.Targets) Scene<Produce_BaseInFire>(bridge, uid); ValidateFuel(data); break;
                case "FuelElectric": foreach (long uid in data.Targets) Scene<Appliance_Activemachine_FuleBase>(bridge, uid); ValidateFuel(data); break;
                case "BindStore":
                    foreach (long uid in data.Targets) Scene<Produce_Base>(bridge, uid);
                    if (data.Reference >= 0) Bean<FWAreaZoneBean>(bridge, data.Reference);
                    break;
                case "BedAssign":
                    foreach (long uid in data.Targets) Scene<Furniture_Bed>(bridge, uid);
                    foreach (long uid in data.Roles)
                        if (!bridge.Context.HasPermission(command.PlayerId, command.MapId, uid, Permission.ControlOwnHero)) throw new InvalidOperationException("不能给其他玩家的雇员分配床位。");
                    foreach (long uid in data.Roles) Scene<BaseHero>(bridge, uid);
                    break;
                case "BedMode": foreach (long uid in data.Targets) Scene<Furniture_Bed>(bridge, uid); ValidateBedMode(data.Number); break;
                case "MedicalBedMode": foreach (long uid in data.Targets) Scene<Medice_Bed>(bridge, uid); ValidateBedMode(data.Number); break;
                case "Temperature":
                    foreach (long uid in data.Targets)
                    {
                        var region = Scene<Templer_Base>(bridge, uid).templerBean.getDef().tempRegion;
                        if (data.Number < region.region0 || data.Number > region.region1) throw new InvalidOperationException("温度设置超过原版设施范围。");
                    }
                    break;
                case "Plant":
                    foreach (long uid in data.Targets) Bean<FWPlantAreaZoneBean>(bridge, uid);
                    if (data.Values.Count != 1 || (data.Values[0] != "-1" && Map_PlantDef.getDataID(data.Values[0]) == null)) throw new InvalidOperationException("种植配置不存在。"); break;
                case "RefuseRoles": foreach (long uid in data.Targets) Bean<FWProductBaseBean>(bridge, uid); break;
                case "Priority":
                    foreach (long uid in data.Targets) if (MapManager.instance.planManager.getPlanGraphByUID(uid) == null) throw new InvalidOperationException("任务已不存在。"); break;
                case "Tidy": foreach (long uid in data.Targets) Scene<Furniture_Box>(bridge, uid); break;
                case "ElectricLink": foreach (long uid in data.Targets) Scene<IAppliance>(bridge, uid); break;
                case "Paste":
                    FWBuildBean source = Bean<FWBuildBean>(bridge, data.Reference);
                    if (!bridge.Context.HasPermission(command.PlayerId, command.MapId, data.Reference, Permission.UseBuilding)) throw new InvalidOperationException("没有复制来源的建筑权限。");
                    foreach (long uid in data.Targets)
                    { BaseBuild target = Scene<BaseBuild>(bridge, uid); if (target.buildBean.id != source.id || !LocalCoreHandle.hasCopyPasteFct(target)) throw new InvalidOperationException("不能向不同类型的建筑粘贴配置。"); }
                    break;
                default: throw new InvalidOperationException("未识别的建筑设置操作。");
            }
            foreach (long uid in data.Targets)
            {
                switch (data.Operation)
                {
                    case "Wall": Bean<FWWallBean>(bridge, uid).rounded = data.Flag; Scene<BaseBuild>(bridge, uid).repaint(); break;
                    case "Trough":
                        FWTroughBean trough = Bean<FWTroughBean>(bridge, uid); trough.configIds = data.Values.ToList(); trough.vegetarianMaxValue = data.Number; trough.meatMaxValue = data.OtherNumber; break;
                    case "StoreBox": ApplyStore(data, Bean<FWBox>(bridge, uid)); break;
                    case "StoreZone": case "AnimalZone":
                        FWAreaZoneBean zone = Bean<FWAreaZoneBean>(bridge, uid); ApplyStore(data, zone);
                        if (data.Operation == "AnimalZone") ((FWAnimalAreaZoneBean)zone).propagation = data.Flag;
                        LocalCoreHandle.Zone_SetStoreSetting_LN(zone); break;
                    case "FuelProduct": LocalCoreHandle.send_ProductFuleConfig(Scene<Produce_BaseInFire>(bridge, uid), data.Values.ToList()); break;
                    case "FuelElectric": LocalCoreHandle.send_ElectricFuleConfig(Scene<Appliance_Activemachine_FuleBase>(bridge, uid), data.Values.ToList()); break;
                    case "BindStore": LocalCoreHandle.Product_BindStore(new List<Produce_Base> { Scene<Produce_Base>(bridge, uid) }, data.Reference < 0 ? null : Bean<FWAreaZoneBean>(bridge, data.Reference)); break;
                    case "BedAssign": LocalCoreHandle.Bed_AssignRevise_LN(Scene<Furniture_Bed>(bridge, uid), data.Roles.Select(id => Scene<BaseHero>(bridge, id)).ToList()); break;
                    case "BedMode": LocalCoreHandle.Bed_ModeRevise_LN(Scene<Furniture_Bed>(bridge, uid), checked((short)data.Number)); break;
                    case "MedicalBedMode": LocalCoreHandle.MedicalBed_ModeRevise_LN(Scene<Medice_Bed>(bridge, uid), checked((short)data.Number)); break;
                    case "Temperature": LocalCoreHandle.Templer_AdjustTemper_LN(Scene<Templer_Base>(bridge, uid), data.Number); break;
                    case "Plant": LocalCoreHandle.Zone_SetPlantID_LN(Bean<FWPlantAreaZoneBean>(bridge, uid), data.Values[0]); break;
                    case "RefuseRoles": Bean<FWProductBaseBean>(bridge, uid).setRefuseRoles(data.Roles.ToList()); break;
                    case "Priority": LocalCoreHandle.Plan_DoPrior_LN(MapManager.instance.planManager.getPlanGraphByUID(uid), data.Flag); break;
                    case "Tidy":
                        Furniture_Box box = Scene<Furniture_Box>(bridge, uid);
                        if (data.Flag) StoreTidyupRecord.addRecord(new StoreTidyupRecord(new List<Furniture_Box> { box })); else StoreTidyupRecord.removeTidyupPlan(box); break;
                    case "ElectricLink": Scene<IAppliance>(bridge, uid).findNearEletricLine(); break;
                    case "Paste": Paste(bridge, data.Reference, uid); break;
                }
            }
            return true;
        }
        private static void ValidateBedMode(int mode)
        { if (mode != FWBedBean.MODE_Normal && mode != FWBedBean.MODE_Prisoner) throw new InvalidOperationException("不支持的床位用途。"); }
        private static void ValidateStore(Settings data)
        { if (data.Priority < StorePacker.PRIORITY_0 || data.Priority > StorePacker.PRIORITY_4 || Single.IsNaN(data.Durability) || Single.IsInfinity(data.Durability) || data.Durability < 0 || data.Durability > 1) throw new InvalidOperationException("仓储筛选配置无效。"); }
        private static void ValidateFuel(Settings data)
        { foreach (string id in data.Values) if (!Dialog_Bv_FuleConfig.getFules().Any(f => f.GlobalID == id)) throw new InvalidOperationException("燃料配置不存在：" + id); }
        private static void ApplyStore(Settings data, FWAreaZoneBean zone)
        { zone.priority = data.Priority; zone.durability = data.Durability; zone.qualitys = data.Qualities.ToList(); zone.setConfigIds(data.Values.ToList()); }
        private static void ApplyStore(Settings data, FWBox box)
        { box.priority = data.Priority; box.durability = data.Durability; box.qualitys = data.Qualities.ToList(); box.configIds = data.Values.ToList(); LocalCoreHandle.Box_SetStoreSetting_LN(box); }
        private static void Paste(GameBridge bridge, long sourceUid, long targetUid)
        {
            FWBuildBean previous = LocalCoreHandle.copyFrom;
            FWBuildBean source = NativeWire.Unpack<FWBuildBean>(NativeWire.Pack(Bean<FWBuildBean>(bridge, sourceUid)));
            BaseBuild target = Scene<BaseBuild>(bridge, targetUid);
            if (source is FWTroughBean)
            {
                // The original clipboard includes current food stock. Only configuration may copy.
                var from = (FWTroughBean)source; var to = (FWTroughBean)target.buildBean;
                from.meatValue = to.meatValue; from.vegetarianValue = to.vegetarianValue;
            }
            try { LocalCoreHandle.copyFrom = source; LocalCoreHandle.doPaste(new List<BaseBuild> { target }, () => { }); }
            finally { LocalCoreHandle.copyFrom = previous; }
        }
    }

    internal static class NativeBuildingCommandHooks
    {
        private static GameBridge Bridge { get { return GameBridge.Active; } }
        internal static void Install(Harmony harmony)
        {
            string[] names = { "Product_BindStore", "Bed_AssignRevise_LN", "Bed_ModeRevise_LN", "MedicalBed_ModeRevise_LN", "Templer_AdjustTemper_LN", "send_ProductFuleConfig", "send_ElectricFuleConfig", "Zone_SetPlantID_LN", "Plan_DoPrior_LN" };
            foreach (MethodInfo method in typeof(LocalCoreHandle).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => names.Contains(m.Name)))
                harmony.Patch(method, new HarmonyMethod(typeof(NativeBuildingCommandHooks), nameof(CorePrefix)));
            Patch(harmony, typeof(ViewBuild_Fctions), "onSwitch_WallSet", nameof(WallPrefix));
            Patch(harmony, typeof(ViewBuild_Fctions), "onEvent_Tidyup", nameof(TidyPrefix));
            Patch(harmony, typeof(ViewBuild_Fctions), "onEvent_ElectricLine", nameof(LinkPrefix));
            Patch(harmony, typeof(Dialog_AnimalTroughSetting), "onEvent_Contrim", nameof(TroughPrefix));
            Patch(harmony, typeof(StorePacker), "dataToBox", nameof(StoreBoxPrefix));
            Patch(harmony, typeof(StorePacker), "dataToArea", nameof(StoreZonePrefix));
            Patch(harmony, typeof(StorePacker_Animal), "dataToAnimalArea", nameof(StoreAnimalPrefix));
            Patch(harmony, typeof(Dialog_ProductVisa), "onEvent_Save", nameof(VisaPrefix));
            Patch(harmony, typeof(LocalCoreHandle), "doPaste", nameof(PastePrefix));
        }
        private static void Patch(Harmony harmony, Type type, string method, string prefix)
        {
            MethodInfo target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
            // Inherited MethodInfo can retain the derived ReflectedType. Harmony requires
            // the method obtained from its actual declaring type, including its overload.
            target = AccessTools.DeclaredMethod(target.DeclaringType, target.Name, target.GetParameters().Select(p => p.ParameterType).ToArray())
                ?? throw new MissingMethodException(target.DeclaringType.FullName, target.Name);
            harmony.Patch(target, new HarmonyMethod(typeof(NativeBuildingCommandHooks), prefix));
        }
        private static NativeBuildingCommands.Settings Single(string operation, long uid)
        { return new NativeBuildingCommands.Settings { Operation = operation, Targets = new List<long> { uid } }; }
        private static List<long> Selected(object ui)
        { return ((List<BaseBuild>)NativeWire.Get(ui, "selectBuilds")).Select(b => b.getUID()).ToList(); }
        private static bool CorePrefix(MethodBase __originalMethod, object[] __args)
        {
            if (NativeBuildingCommands.Bypass) return true;
            string name = __originalMethod.Name;
            NativeBuildingCommands.Settings data;
            if (name == "Product_BindStore")
                data = new NativeBuildingCommands.Settings { Operation = "BindStore", Targets = ((List<Produce_Base>)__args[0]).Select(p => p.getUID()).ToList(), Reference = __args[1] == null ? -1 : NativeWire.Uid(__args[1]) };
            else if (name == "Plan_DoPrior_LN")
            {
                var plans = __args[0] as List<BasePlanGraph> ?? new List<BasePlanGraph> { (BasePlanGraph)__args[0] };
                data = new NativeBuildingCommands.Settings { Operation = "Priority", Targets = plans.Select(p => p.rolePlan.planBean.getUID()).ToList(), Flag = (bool)__args[1] };
            }
            else
            {
                data = Single("", __args[0] is BaseBuild ? ((BaseBuild)__args[0]).getUID() : NativeWire.Uid(__args[0]));
                switch (name)
                {
                    case "Bed_AssignRevise_LN": data.Operation = "BedAssign"; data.Roles = ((List<BaseHero>)__args[1]).Select(h => h.getUID()).ToList(); break;
                    case "Bed_ModeRevise_LN": data.Operation = "BedMode"; data.Number = Convert.ToInt32(__args[1]); break;
                    case "MedicalBed_ModeRevise_LN": data.Operation = "MedicalBedMode"; data.Number = Convert.ToInt32(__args[1]); break;
                    case "Templer_AdjustTemper_LN": data.Operation = "Temperature"; data.Number = (int)__args[1]; break;
                    case "send_ProductFuleConfig": data.Operation = "FuelProduct"; data.Values = ((List<string>)__args[1]).ToList(); break;
                    case "send_ElectricFuleConfig": data.Operation = "FuelElectric"; data.Values = ((List<string>)__args[1]).ToList(); break;
                    case "Zone_SetPlantID_LN": data.Operation = "Plant"; data.Values.Add((string)__args[1]); break;
                    default: throw new InvalidOperationException("建筑配置补丁入口未实现：" + name);
                }
            }
            NativeBuildingCommands.Submit(data); return false;
        }
        // The declared callbacks can also run on other ViewBuild_Fctions_Base subclasses.
        private static bool WallPrefix(object __instance, bool flag)
        { if (NativeBuildingCommands.Bypass) return true; NativeBuildingCommands.Submit(new NativeBuildingCommands.Settings { Operation = "Wall", Targets = Selected(__instance), Flag = flag }); return false; }
        private static bool TidyPrefix(object __instance, bool flag)
        { if (NativeBuildingCommands.Bypass) return true; NativeBuildingCommands.Submit(new NativeBuildingCommands.Settings { Operation = "Tidy", Targets = Selected(__instance), Flag = flag }); return false; }
        private static bool LinkPrefix(object __instance)
        { if (NativeBuildingCommands.Bypass) return true; NativeBuildingCommands.Submit(new NativeBuildingCommands.Settings { Operation = "ElectricLink", Targets = Selected(__instance) }); return false; }
        private static bool TroughPrefix(Dialog_AnimalTroughSetting __instance)
        {
            if (NativeBuildingCommands.Bypass) return true;
            var data = Single("Trough", __instance.troughBean.getUID());
            foreach (var item in (List<Dialog_AnimalTroughSettingItem>)NativeWire.Get(__instance, "items")) data.Values.AddRange(item.getSelectIds());
            data.Number = ((Dialog_AnimalTroughSettingStore)NativeWire.Get(__instance, "storeVage")).curValue;
            data.OtherNumber = ((Dialog_AnimalTroughSettingStore)NativeWire.Get(__instance, "storeMeat")).curValue;
            NativeBuildingCommands.Submit(data); __instance.closeDialog(); return false;
        }
        private static NativeBuildingCommands.Settings Store(StorePacker packer, string operation, long uid)
        { var data = Single(operation, uid); data.Values = packer.configIds.ToList(); data.Qualities = packer.qualitys.ToList(); data.Durability = packer.durability; data.Priority = packer.priority; return data; }
        private static bool StoreBoxPrefix(StorePacker __instance, FWBox boxBean, ref bool __result)
        { if (NativeBuildingCommands.Bypass) return true; NativeBuildingCommands.Submit(Store(__instance, "StoreBox", boxBean.getUID())); __result = false; return false; }
        private static bool StoreZonePrefix(StorePacker __instance, FWAreaZoneBean zoneBean, ref bool __result)
        { if (NativeBuildingCommands.Bypass) return true; NativeBuildingCommands.Submit(Store(__instance, "StoreZone", zoneBean.getUID())); __result = false; return false; }
        private static bool StoreAnimalPrefix(StorePacker_Animal __instance, FWAnimalAreaZoneBean zoneBean, ref bool __result)
        { if (NativeBuildingCommands.Bypass) return true; var data = Store(__instance, "AnimalZone", zoneBean.getUID()); data.Flag = __instance.propagation; NativeBuildingCommands.Submit(data); __result = false; return false; }
        private static bool VisaPrefix(Dialog_ProductVisa __instance)
        {
            if (NativeBuildingCommands.Bypass) return true;
            var product = (FWProductBaseBean)NativeWire.Get(__instance, "productBean");
            var data = Single("RefuseRoles", product.getUID()); data.Roles = ((HashSet<long>)NativeWire.Get(__instance, "refuseRoles")).ToList();
            NativeBuildingCommands.Submit(data);
            __instance.setDialogCallInt(null); // Original close callback directly copies the old local shared list.
            __instance.closeDialog(); return false;
        }
        private static bool PastePrefix(List<BaseBuild> builds, callback _callback)
        {
            if (NativeBuildingCommands.Bypass) return true;
            if (LocalCoreHandle.copyFrom == null) throw new InvalidOperationException("请先复制同类建筑的配置。");
            NativeBuildingCommands.Submit(new NativeBuildingCommands.Settings { Operation = "Paste", Targets = builds.Where(b => b.buildBean.id == LocalCoreHandle.copyFrom.id).Select(b => b.getUID()).ToList(), Reference = LocalCoreHandle.copyFrom.getUID() }, () => { if (_callback != null) _callback(); });
            return false;
        }
    }
}
