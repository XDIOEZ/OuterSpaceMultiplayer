using System;
using System.Collections.Generic;
using SmartAI.Core;
using SmartAI.Systems;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class PlayerBattlePose
        {
            internal Vector3 From, To;
            internal float FromAngle, ToAngle, Receipt, Duration, Progress;
        }
        private readonly Dictionary<long, PlayerBattlePose> playerBattlePoses = new Dictionary<long, PlayerBattlePose>();
        private long appliedNativeBattleRevision;
        private void QueuePlayerBattlePose(BaseShip ship, FleetBattleShip target, bool reset)
        {
            PlayerBattlePose pose; float now = Time.unscaledTime;
            if (!playerBattlePoses.TryGetValue(target.Uid, out pose))
            { playerBattlePoses.Add(target.Uid, pose = new PlayerBattlePose()); reset = true; }
            float interval = pose.Receipt <= 0 ? 0.1f : Mathf.Clamp(now - pose.Receipt, 0.025f, 0.25f);
            pose.From = reset ? new Vector3(target.X, target.Y, ship.transform.position.z) : ship.transform.position;
            pose.To = new Vector3(target.X, target.Y, ship.transform.position.z);
            pose.FromAngle = reset ? target.Rotation : ship.transform.eulerAngles.z; pose.ToAngle = target.Rotation;
            pose.Duration = interval; pose.Progress = reset ? 1 : 0; pose.Receipt = now;
            if (reset) ApplyPlayerBattlePose(ship, pose);
        }
        private void UpdatePlayerBattlePose(BaseShip ship)
        {
            PlayerBattlePose pose;
            if (!playerBattlePoses.TryGetValue(BattleSourceUid(ship), out pose) || Context.IsPaused()) return;
            pose.Progress = Mathf.Min(1, pose.Progress + Time.unscaledDeltaTime / pose.Duration);
            ApplyPlayerBattlePose(ship, pose);
        }
        private static void ApplyPlayerBattlePose(BaseShip ship, PlayerBattlePose pose)
        {
            Vector3 p = Vector3.Lerp(pose.From, pose.To, pose.Progress);
            float angle = Mathf.LerpAngle(pose.FromAngle, pose.ToAngle, pose.Progress);
            ship.transform.SetPositionAndRotation(p, Quaternion.Euler(0, 0, angle));
            Rigidbody2D body = ship.GetComponent<Rigidbody2D>();
            if (body != null) { body.simulated = false; body.position = p; body.rotation = angle; }
        }
        internal bool UpdateRemotePlayerBattleShieldAim(ShieldManager manager)
        {
            if (!HasNativePlayerBattle) return false;
            BaseShip ship = manager.Owner as BaseShip;
            if (ship == null || BattleShipOwner(BattleSourceUid(ship)) == Context.LocalPlayerId) return false;
            FleetBattleControl control;
            if (battlePilotControls.TryGetValue(BattleSourceUid(ship), out control)) manager.SetFocusPoint(new Vector2(control.X, control.Y));
            return true;
        }
    }
}
