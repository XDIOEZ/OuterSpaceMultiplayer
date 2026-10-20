using System;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private void ReceiveCommittedPersonal(PlayerUpdate update)
        {
            PlayerRecord player;
            if (update == null || string.IsNullOrEmpty(update.PlayerId)
                || !World.Players.TryGetValue(update.PlayerId, out player))
                throw new MessageRejectionException("个人进度差量属于未知玩家。");
            if (update.BaseRevision < 0 || update.BaseRevision == long.MaxValue
                || update.Revision != update.BaseRevision + 1)
                throw new MessageRejectionException("个人进度差量版本格式无效。");
            // A late standalone broadcast can duplicate an already received map
            // transaction. Never apply it again or move PersonalRevision back.
            if (update.Revision <= player.PersonalRevision) return;
            if (update.BaseRevision != player.PersonalRevision)
                throw new MessageRejectionException("个人进度差量缺少前序版本，请重新加入房间恢复完整基准。");
            Require(bridge.ApplyPlayerUpdate(update), "同步个人进度字段差量");
            player.PersonalRevision = update.Revision;
        }
    }
}
