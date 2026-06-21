using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Emqo.KookBot_Unturned.KookApi;
using Emqo.KookBot_Unturned.Interfaces;
using Emqo.KookBot_Unturned.Utilities;
using Rocket.Core.Logging;
using Rocket.Unturned.Player;
using Rocket.Unturned.Events;
using SDG.Unturned;
using Steamworks;
using Rocket.Unturned;
using System.Collections.Generic;
using System.Linq;
using Rocket.Unturned.Chat;

namespace Emqo.KookBot_Unturned
{
    public static partial class Events
    {
        #region PvP 事件

        private static void OnPlayerDamaged(ref DamagePlayerParameters parameters, ref bool shouldAllow)
        {
            PvpEventRuntime.Received();

            // 高频事件：不要走 CheckEventEnabled/ValidateEventPrerequisites，避免 PlayerDamaged=false
            // 或 KOOK 配置缺失时每次伤害都刷 Rocket 日志。
            if (!shouldAllow || !IsEventEnabledFast("PlayerDamaged"))
            {
                PvpEventRuntime.Disabled();
                MaybeLogDiagnostics("PlayerDamaged:disabled");
                return;
            }

            try
            {
                var copiedParameters = parameters; // 复制结构体

                // 非玩家伤害/环境伤害/离线边界：不要假设 victim/killer 一定完整。
                if (copiedParameters.player == null || copiedParameters.killer == CSteamID.Nil)
                {
                    PvpEventRuntime.InvalidPlayer();
                    MaybeLogDiagnostics("PlayerDamaged:invalid-player");
                    return;
                }

                if (!TryResolveVictim(copiedParameters.player, out var victim, out var victimSteamId) ||
                    !TryResolvePlayer(copiedParameters.killer, out var attacker))
                {
                    PvpEventRuntime.InvalidPlayer();
                    MaybeLogDiagnostics("PlayerDamaged:lookup-null");
                    return;
                }

                if (victimSteamId == copiedParameters.killer || victim.CSteamID == attacker.CSteamID)
                {
                    PvpEventRuntime.SelfDamageSkipped();
                    MaybeLogDiagnostics("PlayerDamaged:self");
                    return;
                }

                var victimHealth = SafeGetHealth(victim);
                var isFatalOrLikelyFatal = victimHealth > 0 && victimHealth <= copiedParameters.damage;
                if (copiedParameters.damage < PvpEventRuntime.MinDamageToReport && !isFatalOrLikelyFatal)
                {
                    PvpEventRuntime.LowDamageSkipped();
                    MaybeLogDiagnostics("PlayerDamaged:low-damage");
                    return;
                }

                // 在同步上下文中获取所有需要的数据
                var victimName = GetPlayerName(victim);
                var attackerName = GetPlayerName(attacker);
                var limbText = GetLimbText(copiedParameters.limb);
                var now = DateTimeOffset.UtcNow;
                var throttleKey = $"{copiedParameters.killer}:{victimSteamId}:{copiedParameters.limb}";

                if (!isFatalOrLikelyFatal && !PvpEventRuntime.ShouldSend(throttleKey, now))
                {
                    PvpEventRuntime.Throttled();
                    MaybeLogDiagnostics("PlayerDamaged:throttled");
                    return;
                }

                // Fire and forget with exception handling. Queued is counted only after KookSendQueue reserves a send slot.
                SafeFireAndForget(OnPlayerDamagedAsync(copiedParameters, victimName, attackerName, victimHealth, limbText), "PlayerDamaged");
            }
            catch (Exception ex)
            {
                PvpEventRuntime.Failed();
                PvpEventRuntime.LogErrorRateLimited($"Error in OnPlayerDamaged: {ex.Message}", ex, ShouldLogDebug);
                MaybeLogDiagnostics("PlayerDamaged:error");
            }
        }

        private static async Task OnPlayerDamagedAsync(DamagePlayerParameters copiedParameters, string victimName, string attackerName, float victimHealth, string limbText)
        {
            try
            {
                if (copiedParameters.killer == CSteamID.Nil) return;

                // Re-check runtime switch so /event set PlayerDamaged false stops already scheduled sends too.
                if (!IsEventEnabledFast("PlayerDamaged") || !ValidateEventPrerequisites()) return;

                var pvpFields = new List<(string, string)>
                {
                    ("攻击者", attackerName),
                    ("受害者", victimName),
                    ("伤害", $"{copiedParameters.damage}"),
                    ("部位", limbText)
                };

                if (victimHealth <= copiedParameters.damage)
                {
                    pvpFields.Add(("结果", "致命一击"));
                }

                var pvpCard = KookApi.KookCardFactory.BuildGenericEventCard("⚔️", "PvP 事件", pvpFields, DateTimeOffset.Now, "warning");
                var sent = await SendBoundedKookMessageAsync(
                    async () => await _message.CreateMessageAsync(10, _channelId, pvpCard),
                    "PlayerDamaged");
                if (sent && ShouldLogDebug)
                {
                    Logger.Log($"📤 PvP event sent to KOOK: {attackerName} -> {victimName} ({copiedParameters.damage} damage)");
                }
            }
            catch (Exception ex)
            {
                PvpEventRuntime.Failed();
                PvpEventRuntime.LogErrorRateLimited($"Error sending PvP event: {ex.Message}", ex, ShouldLogDebug);
                MaybeLogDiagnostics("PlayerDamaged:send-error");
            }
        }

        private static bool IsEventEnabledFast(string eventName)
        {
            if (!_isEnabled)
            {
                return false;
            }

            var config = Config;
            return config != null && config.IsGameToKookEnabled(eventName);
        }

        private static bool TryResolveVictim(SDG.Unturned.Player player, out UnturnedPlayer unturnedPlayer, out CSteamID steamId)
        {
            unturnedPlayer = null;
            steamId = CSteamID.Nil;

            if (player == null)
            {
                return false;
            }

            try
            {
                steamId = player.channel?.owner?.playerID?.steamID ?? CSteamID.Nil;
                unturnedPlayer = UnturnedPlayer.FromPlayer(player);
                if (unturnedPlayer != null)
                {
                    if (steamId == CSteamID.Nil)
                    {
                        steamId = unturnedPlayer.CSteamID;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                PvpEventRuntime.LogErrorRateLimited($"Failed to resolve PvP victim: {ex.Message}", ex, ShouldLogDebug);
            }

            return steamId != CSteamID.Nil && TryResolvePlayer(steamId, out unturnedPlayer);
        }

        private static bool TryResolvePlayer(CSteamID steamId, out UnturnedPlayer player)
        {
            player = null;
            if (steamId == CSteamID.Nil)
            {
                return false;
            }

            try
            {
                player = UnturnedPlayer.FromCSteamID(steamId);
                if (player != null)
                {
                    return true;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Unturned/Rocket 内部可能正在遍历 Provider.clients；走快照兜底，避免高频报错。
                PvpEventRuntime.LogErrorRateLimited($"Player lookup raced with clients collection update: {ex.Message}", ex, ShouldLogDebug);
            }
            catch (Exception ex)
            {
                PvpEventRuntime.LogErrorRateLimited($"Failed to resolve PvP player {steamId}: {ex.Message}", ex, ShouldLogDebug);
            }

            try
            {
                var clients = Provider.clients?.ToList();
                if (clients == null)
                {
                    return false;
                }

                for (var i = 0; i < clients.Count; i++)
                {
                    var client = clients[i];
                    if (client?.playerID?.steamID != steamId || client.player == null)
                    {
                        continue;
                    }

                    player = UnturnedPlayer.FromPlayer(client.player);
                    return player != null;
                }
            }
            catch (Exception ex)
            {
                PvpEventRuntime.LogErrorRateLimited($"Failed to resolve PvP player from Provider.clients snapshot: {ex.Message}", ex, ShouldLogDebug);
            }

            return false;
        }

        private static float SafeGetHealth(UnturnedPlayer player)
        {
            try
            {
                return player?.Health ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        #endregion
    }
}
