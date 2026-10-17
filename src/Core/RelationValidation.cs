using System;
using System.Collections.Generic;
using System.IO;

namespace FreeWorldMultiplayer
{
    // Numeric JSON enum values are not automatically constrained by Json.NET.
    // All game/UI/permission code must see one of these three defined states.
    internal static class RelationValidation
    {
        internal static bool IsDefined(Relation relation)
        {
            return relation == Relation.Neutral ||
                relation == Relation.Friendly ||
                relation == Relation.Hostile;
        }

        internal static void RequireDefined(Relation relation)
        {
            if (!IsDefined(relation))
                throw new InvalidDataException("玩家关系值无效，已拒绝变更。");
        }

        internal static void ValidateSavedRelations(IList<RelationshipRecord> relationships)
        {
            if (relationships == null)
                throw new InvalidDataException("联机存档缺少玩家关系表，未修改存档。");
            foreach (RelationshipRecord item in relationships)
            {
                if (item == null || !IsDefined(item.Relation))
                    throw new InvalidDataException("联机存档包含无效玩家关系，未加载或修改该存档。");
            }
        }
    }
}
