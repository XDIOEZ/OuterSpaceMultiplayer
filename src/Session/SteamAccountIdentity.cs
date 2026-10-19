using System;
using System.Linq;

namespace FreeWorldMultiplayer
{
    public sealed partial class MultiplayerSession
    {
        private string RequireLocalSteamAccount(TransportOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!options.UseSteam) return null;
            var accounts = transport as ITransportSteamIdentity;
            string account = accounts == null ? null : accounts.GetLocalSteamId();
            if (string.IsNullOrWhiteSpace(account))
                throw new InvalidOperationException("当前 Steam 账号尚未完成登录，无法绑定联机身份；请登录后重试，或选择 IP 直连。");
            return account;
        }

        private string RemoteSteamAccount(string peer)
        {
            // This capability is supplied by the live Steam P2P connection, never
            // by a nickname, a client payload or a TCP peer's self-reported ID.
            var accounts = transport as ITransportSteamIdentity;
            return accounts == null ? null : accounts.GetRemoteSteamId(peer);
        }

        private PlayerRecord FindSteamAccountPlayer(string account)
        {
            if (string.IsNullOrWhiteSpace(account)) return null;
            PlayerRecord[] matches = World.Players.Values.Where(p => p.SteamId == account).Take(2).ToArray();
            if (matches.Length > 1)
                throw new MessageRejectionException("该 Steam 账号在存档中存在多个玩家绑定，已拒绝自动选择，请由房主检查原存档。");
            return matches.Length == 0 ? null : matches[0];
        }

        private PlayerRecord ResolveRemoteSteamAccount(PlayerRecord credentialPlayer, string account)
        {
            PlayerRecord accountPlayer = FindSteamAccountPlayer(account);
            if (!string.IsNullOrWhiteSpace(account) && credentialPlayer != null)
            {
                if (!string.IsNullOrWhiteSpace(credentialPlayer.SteamId) && credentialPlayer.SteamId != account)
                    throw new MessageRejectionException("当前本机凭证已属于另一 Steam 账号，不能改绑或接管原资产。");
                if (accountPlayer != null && accountPlayer.PlayerId != credentialPlayer.PlayerId)
                    throw new MessageRejectionException("当前 Steam 账号与本机凭证分别绑定了不同玩家，不能自动合并资产。");
            }
            PlayerRecord player = accountPlayer ?? credentialPlayer;
            if (player != null && player.PlayerId == World.HostPlayerId)
                throw new MessageRejectionException("房主身份不能作为客人加入，请由房主继续该世界。");
            return player;
        }

        private string ResolveLocalHostSteamAccount(PlayerRecord host, TransportOptions options,
            Func<PlayerRecord, LocalIdentity> resolveLocalHostIdentity)
        {
            string account = RequireLocalSteamAccount(options);
            PlayerRecord accountPlayer = FindSteamAccountPlayer(account);
            if (accountPlayer != null && accountPlayer.PlayerId != host.PlayerId)
                throw new InvalidOperationException("当前 Steam 账号属于此世界的其他玩家，不能接管房主。");
            if (!string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(host.SteamId))
            {
                if (host.SteamId != account)
                    throw new InvalidOperationException("当前 Steam 账号不属于此世界房主，请切换为原房主账号。");
                return account;
            }
            // An old world without an account binding can only be claimed after
            // proving its original local credential. Never infer ownership.
            if (resolveLocalHostIdentity != null)
            {
                LocalIdentity resolved = resolveLocalHostIdentity(host);
                if (resolved != null) identity = resolved;
            }
            if (host.CredentialId != identity.CredentialId || !CryptoData.Equal(host.CredentialHash, CryptoData.Hash(identity.Secret)))
                throw new InvalidOperationException(account == null ? "本机凭证不属于该世界房主，不能按昵称接管。"
                    : "本机凭证不属于该世界房主；旧存档首次绑定 Steam 账号需要原房主凭证。");
            return account;
        }

        private void SaveWithSteamAccountBinding(PlayerRecord player, string account, bool newlyCreated = false)
        {
            string previous = player.SteamId;
            long previousGeneration = World.SaveGeneration;
            if (!string.IsNullOrWhiteSpace(account)) player.SteamId = account;
            try { SaveOrThrow(); }
            catch
            {
                // A failed first bind must not be picked up by a later autosave.
                // SaveStore restores its generation on a failed commit. If a
                // post-commit notification fails, retain the committed binding.
                if (World.SaveGeneration == previousGeneration)
                {
                    player.SteamId = previous;
                    if (newlyCreated && !string.IsNullOrWhiteSpace(account))
                    {
                        // The native starter assets already exist. A second device
                        // must not enroll the same account again before those assets
                        // and its binding have been committed in one generation.
                        try { LockNativeFault("Steam 新玩家的初始资产与账号绑定未成功保存，请关闭房间并从最后完整存档续开后重试。"); }
                        catch (Exception) { } // Lock fields are set before notification; retain the original save error.
                    }
                }
                throw;
            }
        }
    }
}
