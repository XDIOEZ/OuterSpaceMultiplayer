using System;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private sealed class RecoveryCode { public string CredentialId; public string Verifier; }
        public string GetLocalRecoveryCode()
        {
            return JsonData.Write(new RecoveryCode { CredentialId = identity.CredentialId, Verifier = CryptoData.Hash(identity.Secret) });
        }
        public CommandResult RebindOfflinePlayerCredential(string playerId, string recoveryCode)
        {
            string id = "rebind:" + Guid.NewGuid().ToString("N");
            if (!IsHost || !IsConnected || World == null || closing || nativeFault != null) return CommandResult.Fail(id, "只有正常开房的房主可重绑离线玩家凭证。");
            PlayerRecord player;
            if (playerId == null || !World.Players.TryGetValue(playerId, out player) || player.Online) return CommandResult.Fail(id, "请明确选择一个已存在且离线的 PlayerId。");
            RecoveryCode code;
            try
            {
                code = JsonData.Read<RecoveryCode>(recoveryCode);
                Guid parsed;
                if (code == null || !Guid.TryParseExact(code.CredentialId, "N", out parsed) || Convert.FromBase64String(code.Verifier).Length != 32) throw new InvalidOperationException("恢复码格式无效。");
            }
            catch (Exception ex) { return CommandResult.Fail(id, "无法读取恢复码：" + ex.Message); }
            if (World.Players.Values.Any(p => p.PlayerId != playerId && p.CredentialId == code.CredentialId)) return CommandResult.Fail(id, "新凭证已经绑定另一个玩家，不能合并或转移资产。");
            string previousId = player.CredentialId, previousHash = player.CredentialHash;
            var previousRevoked = World.RevokedCredentialIds.ToList();
            player.CredentialId = code.CredentialId; player.CredentialHash = code.Verifier;
            if (previousId != code.CredentialId && !World.RevokedCredentialIds.Contains(previousId)) World.RevokedCredentialIds.Add(previousId);
            World.RevokedCredentialIds.Remove(code.CredentialId);
            try
            {
                SaveOrThrow();
                var result = CommandResult.Ok(id); result.PlayerId = playerId; result.Saved = true; result.SaveGeneration = World.SaveGeneration;
                SetStatus("已为离线 PlayerId " + playerId + " 保存新凭证绑定；原资产保持原记录。");
                return result;
            }
            catch (Exception ex)
            {
                player.CredentialId = previousId; player.CredentialHash = previousHash; World.RevokedCredentialIds = previousRevoked;
                return CommandResult.Fail(id, "凭证重绑未成功保存，仍保留原绑定：" + ex.Message);
            }
        }
    }
}
