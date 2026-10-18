using System;
using System.Collections.Generic;
using System.Linq;
using SmartAI;
using SmartAI.Core;
using SmartAI.Control;
using SmartAI.Data;
using SmartAI.Managers;
using SmartAI.Gameplay;
using SmartAI.AIC;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        internal sealed class FleetBattleControl
        {
            public float Throttle, Turn, Strafe, X, Y, Z;
            public bool Fire, Aim, Flag;
            public long Target = -1;
            public int Index;
            public string Point;
        }
        private readonly Dictionary<long, FleetBattleControl> battlePilotControls = new Dictionary<long, FleetBattleControl>();
        private readonly Dictionary<long, float> battlePilotSendTimes = new Dictionary<long, float>();
        private readonly HashSet<long> replicaManualPilots = new HashSet<long>();
        private bool readingReplicaBattleInput;
        private string BattleShipOwner(long shipUid)
        {
            FleetBattleShip ship;
            if (!nativeBattleShips.TryGetValue(shipUid, out ship)) return null;
            return ship.Fleet == nativePlayerBattle.AttackerFleetId ? nativePlayerBattle.AttackerPlayerId : nativePlayerBattle.DefenderPlayerId;
        }
        internal bool IsLocalReplicaShip(BaseShip ship)
        { return IsPlayerBattleReplica && ship != null && BattleShipOwner(BattleSourceUid(ship)) == Context.LocalPlayerId; }
        private void SendPlayerBattleInput(BaseShip ship, string operation, FleetBattleControl payload)
        {
            if (!IsLocalReplicaShip(ship) || applyingBattleInput || !PlayerBattleActive || Context.IsPaused()) return;
            long uid = BattleSourceUid(ship);
            Context.SubmitBattleInput(new PlayerFleetBattleInput { BattleId = nativePlayerBattle.BattleId, AuthorityEpoch = nativePlayerBattle.AuthorityEpoch,
                CommandId = Guid.NewGuid().ToString("N"), PlayerId = Context.LocalPlayerId,
                FleetId = nativeBattleShips[uid].Fleet, ShipId = uid, Operation = operation, NativeJson = NativeWire.Json(payload) });
        }
        private FleetBattleControl PilotControl(long uid)
        {
            FleetBattleControl control;
            if (!battlePilotControls.TryGetValue(uid, out control)) battlePilotControls.Add(uid, control = new FleetBattleControl());
            return control;
        }
        internal bool CaptureReplicaFlight(BaseShip ship, float throttle, float turn, float strafe)
        {
            if (!IsLocalReplicaShip(ship) || applyingBattleInput) return false;
            FleetBattleControl c = PilotControl(BattleSourceUid(ship)); c.Throttle = throttle; c.Turn = turn; c.Strafe = strafe; return true;
        }
        internal bool CaptureReplicaFire(BaseShip ship, Vector2 target, bool fire, ShipCore priority, bool aim)
        {
            if (!IsLocalReplicaShip(ship) || applyingBattleInput) return false;
            FleetBattleControl c = PilotControl(BattleSourceUid(ship)); c.X = target.x; c.Y = target.y; c.Fire = fire; c.Aim = aim;
            c.Target = priority is BaseShip ? BattleSourceUid((BaseShip)priority) : -1; return true;
        }
        internal bool UpdateNetworkPilot(PlayerInputController controller)
        {
            if (!HasNativePlayerBattle) return true;
            BaseShip ship = controller.GetComponent<BaseShip>();
            if (IsPlayerBattleReplica) return readingReplicaBattleInput;
            if (ship == null || BattleShipOwner(BattleSourceUid(ship)) == Context.LocalPlayerId) return true;
            if (!CanRunPlayerBattle) return false;
            FleetBattleControl c;
            if (!battlePilotControls.TryGetValue(BattleSourceUid(ship), out c)) { ship.SetFlightControls(0, 0, 0); return false; }
            applyingBattleInput = true;
            try { ship.SetFlightControls(c.Throttle, c.Turn, c.Strafe); if (ship.WeaponManager != null) ship.WeaponManager.UpdateLogic(new Vector2(c.X, c.Y), c.Fire, c.Target < 0 ? null : FindBattleShip(c.Target), c.Aim); }
            finally { applyingBattleInput = false; }
            return false;
        }
        internal bool UpdateNetworkShip(BaseShip ship)
        {
            if (!HasNativePlayerBattle) return true;
            if (!IsPlayerBattleReplica) return CanRunPlayerBattle;
            UpdatePlayerBattlePose(ship);
            if (IsLocalReplicaShip(ship) && PlayerBattleActive && !Context.IsPaused() && ship.IsPlayerSeat && !ship.IsDead())
            {
                long uid = BattleSourceUid(ship);
                InputManager input = InputManager.Instance;
                if (ship.ControlMode == ShipControlMode.Autonomous && input != null && input.IsPlayerTryingToControl())
                    ship.ReleaseAutonomousControlForPlayerInput();
                bool manual = ship.ControlMode == ShipControlMode.Manual && input != null && input.IsBattleControlInputAllowed && !ship.IsPaused();
                FleetBattleControl control = PilotControl(uid);
                control.Throttle = control.Turn = control.Strafe = 0; control.Fire = control.Aim = false; control.Flag = manual;
                if (!manual)
                {
                    // One neutral packet releases held acceleration/fire when UI focus changes.
                    // It does not take a tactical/autonomous ship back into Manual control.
                    if (replicaManualPilots.Remove(uid)) SendPlayerBattleInput(ship, "Pilot", control);
                    ship.VisualManager?.UpdateLogic(); return false;
                }
                // Run only the original local input translation. Every state/weapon write it
                // produces is intercepted below and forwarded to the one native simulator.
                readingReplicaBattleInput = true;
                try { ship.InputController.UpdateLogic(); }
                finally { readingReplicaBattleInput = false; }
                float previous;
                if (!battlePilotSendTimes.TryGetValue(uid, out previous) || Time.unscaledTime - previous >= 0.05f)
                { battlePilotSendTimes[uid] = Time.unscaledTime; SendPlayerBattleInput(ship, "Pilot", control); replicaManualPilots.Add(uid); }
            }
            ship.VisualManager?.UpdateLogic();
            return false;
        }
        internal bool InterceptReplicaDiscrete(BaseShip ship, string operation, FleetBattleControl payload, bool keepLocal)
        {
            if (!IsLocalReplicaShip(ship) || applyingBattleInput) return false;
            SendPlayerBattleInput(ship, operation, payload); return !keepLocal;
        }
        internal bool InterceptReplicaOrder(AIC_Brain brain, string operation, object[] args)
        {
            BaseShip ship = brain.GetComponent<BaseShip>();
            if (!IsLocalReplicaShip(ship) || applyingBattleInput) return false;
            var data = new FleetBattleControl();
            if (args.Length != 0)
            {
                if (args[0] is Vector3) { Vector3 p = (Vector3)args[0]; data.X = p.x; data.Y = p.y; data.Z = p.z; }
                else if (args[0] is BaseShip) data.Target = BattleSourceUid((BaseShip)args[0]);
                else if (args[0] is StrategicPoint) { StrategicPoint p = (StrategicPoint)args[0]; data.Point = p.SelectionName; data.X = p.transform.position.x; data.Y = p.transform.position.y; }
                else if (args[0] is bool) data.Flag = (bool)args[0];
            }
            if (operation == "IssueCaptureOrder") data.Flag = (bool)args[1];
            if (operation == "IssueAttackOrder" && args.Length > 2) data.Flag = (bool)args[2];
            SendPlayerBattleInput(ship, operation, data); return true;
        }
        internal void SendReplicaSeat(BaseShip ship) { if (IsLocalReplicaShip(ship)) SendPlayerBattleInput(ship, "Seat", new FleetBattleControl()); }
        internal bool IsLocalBattleInputShip(BaseShip ship)
        { return !HasNativePlayerBattle || BattleShipOwner(BattleSourceUid(ship)) == Context.LocalPlayerId; }
        internal bool InterceptPlayerBattleAutoPilot(BaseShip ship)
        {
            if (!HasNativePlayerBattle) return false;
            if (!IsLocalBattleInputShip(ship)) return true;
            if (IsLocalReplicaShip(ship)) SendPlayerBattleInput(ship, "Autopilot", new FleetBattleControl { Flag = ship.ControlMode == ShipControlMode.Manual });
            return false;
        }
        internal bool InterceptPlayerBattleRetreat()
        {
            if (!HasNativePlayerBattle) return false;
            foreach (BaseShip ship in FleetManager.Instance.GetPlayerShips().Where(s => s != null && !s.IsDead()))
            {
                if (IsPlayerBattleReplica) SendPlayerBattleInput(ship, "IssueRetreatOrder", new FleetBattleControl());
                else ship.Brain.IssueRetreatOrder(PlayerBattleRetreatPoint(ship));
            }
            return true;
        }
        private static Vector3 PlayerBattleRetreatPoint(BaseShip ship)
        {
            Type commands = typeof(BaseShip).Assembly.GetType("SmartAI.UI.Tactical.Commands.TacticalCommandUtils", true);
            return (Vector3)NativeWire.Call(commands, "ComputeRetreatPoint", ship, 420f);
        }
        public BridgeResult ExecutePlayerFleetBattleInput(PlayerFleetBattleRecord battle, PlayerFleetBattleInput input)
        {
            return Run(() =>
            {
                if (!HasNativePlayerBattle || IsPlayerBattleReplica || battle.BattleId != nativePlayerBattle.BattleId || !PlayerBattleActive
                    || input == null || input.BattleId != battle.BattleId || input.AuthorityEpoch != battle.AuthorityEpoch)
                    throw new NativeRejectionException("舰战控制已不属于本场有效模拟任期。");
                FleetBattleShip owned;
                if (!nativeBattleShips.TryGetValue(input.ShipId, out owned) || owned.Fleet != input.FleetId || BattleShipOwner(input.ShipId) != input.PlayerId)
                    throw new NativeRejectionException("只能控制本人真实舰队中的舰船。");
                BaseShip ship = FindBattleShip(input.ShipId);
                if (ship == null || ship.IsDead() || owned.Retreated) throw new NativeRejectionException("舰船已离场或不可控制。");
                FleetBattleControl c = NativeWire.Parse<FleetBattleControl>(input.NativeJson);
                if (c == null || !BattlePilotFiniteFields.Valid(c.X, c.Y, c.Z, c.Throttle, c.Turn, c.Strafe)
                    || Math.Abs(c.Throttle) > 1 || Math.Abs(c.Turn) > 1 || Math.Abs(c.Strafe) > 1 || c.Index < 0 || c.Index > 64
                    || input.Operation == "Wing" && c.Index > 2)
                    throw new NativeRejectionException("舰战控制参数无效。");
                BaseShip target = c.Target < 0 ? null : FindBattleShip(c.Target);
                applyingBattleInput = true;
                try
                {
                    switch (input.Operation)
                    {
                        case "Pilot":
                            if (c.Flag)
                            {
                                if (!ship.IsPlayerSeat)
                                {
                                    foreach (BaseShip other in FleetManager.Instance.GetAllShips().Values.Where(s => s != null && s != ship && BattleShipOwner(BattleSourceUid(s)) == input.PlayerId && s.IsPlayerSeat).ToList())
                                    { other.SetPlayerSeat(false); other.SetControlMode(ShipControlMode.Autonomous); battlePilotControls.Remove(BattleSourceUid(other)); }
                                    ship.SetPlayerSeat(true);
                                }
                                ship.SetControlMode(ShipControlMode.Manual);
                            }
                            battlePilotControls[input.ShipId] = c;
                            ship.SetFlightControls(c.Throttle, c.Turn, c.Strafe); break;
                        case "Seat":
                            foreach (BaseShip other in FleetManager.Instance.GetAllShips().Values.Where(s => s != null && BattleShipOwner(BattleSourceUid(s)) == input.PlayerId))
                            { other.SetPlayerSeat(other == ship); other.SetControlMode(other == ship ? ShipControlMode.Manual : ShipControlMode.Autonomous); }
                            ship.Brain.ClearOrder(); break;
                        case "Autopilot":
                            if (c.Flag) ship.SetControlMode(ShipControlMode.Autonomous);
                            else ship.ReleaseAutonomousControlForPlayerInput(); break;
                        case "Vent": ship.SetVenting(c.Flag); break;
                        case "Shield": ship.ToggleShield(c.Flag); break;
                        case "System": ship.SystemManager?.TryActivateSystem(); break;
                        case "Group": ship.WeaponManager?.SetSelectedGroup(c.Index); break;
                        case "AutoFire": ship.WeaponManager?.ToggleGroupAutofire(c.Index); break;
                        case "Wing": NativeWire.Set(ship.WingManager, "currentCommand", (CarrierCommand)c.Index); break;
                        case "Target": ship.SetPriorityTarget(target); break;
                        case "IssueMoveOrder": ship.SetControlMode(ShipControlMode.Autonomous); ship.Brain.IssueMoveOrder(new Vector3(c.X, c.Y, c.Z)); break;
                        case "AppendMoveWaypoint": ship.Brain.AppendMoveWaypoint(new Vector3(c.X, c.Y, c.Z)); break;
                        case "IssueRetreatOrder": ship.SetControlMode(ShipControlMode.Autonomous); ship.Brain.IssueRetreatOrder(PlayerBattleRetreatPoint(ship)); break;
                        case "IssueAttackOrder":
                            if (target == null || target.GetTeamType() == ship.GetTeamType()) throw new NativeRejectionException("攻击目标不是本场敌舰。");
                            ship.SetControlMode(ShipControlMode.Autonomous); ship.Brain.IssueAttackOrder(target, null, c.Flag); break;
                        case "IssueDefendOrder":
                            if (target == null || target.GetTeamType() != ship.GetTeamType()) throw new NativeRejectionException("护卫目标不是本场友舰。");
                            ship.SetControlMode(ShipControlMode.Autonomous); ship.Brain.IssueDefendOrder(target); break;
                        case "IssueCaptureOrder":
                            StrategicPoint point = UnityEngine.Object.FindObjectsOfType<StrategicPoint>().SingleOrDefault(p => p.SelectionName == c.Point && Vector2.Distance(p.transform.position, new Vector2(c.X, c.Y)) < 1);
                            if (point == null) throw new NativeRejectionException("本场战术据点已失效。");
                            ship.SetControlMode(ShipControlMode.Autonomous); ship.Brain.IssueCaptureOrder(point, c.Flag); break;
                        case "ClearOrder": ship.Brain.ClearOrder(c.Flag); break;
                        default: throw new NativeRejectionException("不支持的玩家舰战控制操作。");
                    }
                }
                finally { applyingBattleInput = false; }
            });
        }
    }
}
