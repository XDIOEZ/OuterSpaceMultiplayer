using System;
using System.Collections;

namespace FreeWorldMultiplayer
{
    internal sealed partial class NativeMapReplicator
    {
        // These lists contain persistent UID entities with their own native scene manager.
        // Treating them as generic spread fields replaces data but leaves the old scene alive.
        private static bool IsGroundContainer(string collection)
        {
            return collection == "airDrop" || collection == "chests" || collection == "rocks"
                || collection == "pdas" || collection == "detectors";
        }

        private static bool GroundContainerLocationChanged(object before, object after)
        {
            FWCellUidItem previous = before as FWCellUidItem, next = after as FWCellUidItem;
            if (previous == null || next == null)
                throw new InvalidOperationException("Ground container is missing its persistent cell identity.");
            return previous.cellX != next.cellX || previous.cellY != next.cellY || previous.id != next.id;
        }

        private static SpriteObject GroundContainerObject(long uid)
        {
            return MapManager.instance == null ? null : MapManager.instance.elementManager.getElement(uid);
        }

        private static void RemoveGroundContainerProjection(string collection, FWCellUidItem bean)
        {
            Block_ElementManager manager = MapManager.instance.elementManager;
            Block_ElementBase element = manager.getElement(bean.getUID());
            Block_ElementGroup group = manager.getGroup(bean.cellX, bean.cellY);
            IList projections = (IList)NativeWire.Get(manager, collection == "airDrop" ? "airdrops" : collection);
            if (element == null || group == null || !projections.Contains(element))
                throw new InvalidOperationException("Container removal references a missing native projection: " + bean.getUID());
            // Native removeChest/removeAirdrop also run Stub_PlanRemove, which mutates
            // persistent plans. Plans are a separate operation in this same transaction;
            // a replica (or a container relocation) must remove only this scene object.
            group.removeElement(element);
            projections.Remove(element);
        }
    }
}
