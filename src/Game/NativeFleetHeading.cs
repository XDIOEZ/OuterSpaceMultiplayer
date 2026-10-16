using System;
using System.Reflection;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private delegate GalaxyPlayerFleetMotion FleetLocalMotion(AirShipFleetInfoBean view);
        private delegate Vector3 VoyageHeading(AirShipMoveBeanBase voyage);
        private static readonly FleetLocalMotion fleetLocalMotion = (FleetLocalMotion)BindFleetDelegate(
            typeof(FleetLocalMotion), "get_LocalMotion", Type.EmptyTypes);
        private static readonly FieldInfo fleetHeadingVoyage = NativeWire.Field(typeof(AirShipFleetInfoBean), "movebean");
        private static readonly VoyageHeading voyageHeading = BindVoyageHeading();

        private static VoyageHeading BindVoyageHeading()
        {
            MethodInfo method = typeof(AirShipMoveBeanBase).GetMethod("getCurAngle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(typeof(AirShipMoveBeanBase).FullName, "getCurAngle");
            return (VoyageHeading)Delegate.CreateDelegate(typeof(VoyageHeading), method);
        }

        private static float NativeFleetHeading(AirShipFleetInfoBean view)
        {
            // Local motion publishes its actual inertial heading to the Bean. A
            // background voyage publishes its position through getCurPos instead;
            // its saved Bean angle is only refreshed on arrival or a scope handoff.
            GalaxyPlayerFleetMotion local = fleetLocalMotion(view);
            if (local != null && local.IsActive) return (float)view.bean.pos.angle;
            // getMoveBean can lazily initialize a route, so read only the existing wrapper.
            AirShipMoveBeanBase voyage = (AirShipMoveBeanBase)fleetHeadingVoyage.GetValue(view);
            if (voyage != null && !voyage.IsStopMove() && !voyage.isMoveOk())
                return voyageHeading(voyage).z; // Cached native degrees; never advances the voyage.
            return (float)view.bean.pos.angle;
        }
    }
}
