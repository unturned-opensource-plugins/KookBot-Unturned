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
        #region 辅助方法

        private static string GetDeathCauseText(EDeathCause cause)
        {
            return cause switch
            {
                EDeathCause.BLEEDING => "失血过多",
                EDeathCause.BONES => "骨折致死",
                EDeathCause.FOOD => "饥饿",
                EDeathCause.WATER => "脱水",
                EDeathCause.GUN => "枪击",
                EDeathCause.MELEE => "近战武器",
                EDeathCause.ZOMBIE => "僵尸攻击",
                EDeathCause.ANIMAL => "动物攻击",
                EDeathCause.SUICIDE => "自杀",
                EDeathCause.KILL => "被管理员处决",
                EDeathCause.INFECTION => "感染",
                EDeathCause.PUNCH => "拳击",
                EDeathCause.BREATH => "窒息",
                EDeathCause.ROADKILL => "车祸",
                EDeathCause.VEHICLE => "载具事故",
                EDeathCause.GRENADE => "手榴弹",
                EDeathCause.BURNING => "燃烧",
                EDeathCause.FREEZING => "冻死",
                EDeathCause.SENTRY => "哨戒炮",
                EDeathCause.ACID => "酸液",
                EDeathCause.BOULDER => "巨石",
                EDeathCause.BURNER => "燃烧器",
                EDeathCause.SPIT => "酸液攻击",
                EDeathCause.CHARGE => "冲撞",
                EDeathCause.SPLASH => "溅射伤害",
                EDeathCause.LANDMINE => "地雷",
                EDeathCause.ARENA => "竞技场",
                _ => cause.ToString()
            };
        }

        private static string GetLimbText(ELimb limb)
        {
            return limb switch
            {
                ELimb.LEFT_ARM => "左臂",
                ELimb.LEFT_HAND => "左手",
                ELimb.LEFT_LEG => "左腿",
                ELimb.LEFT_FOOT => "左脚",
                ELimb.RIGHT_ARM => "右臂",
                ELimb.RIGHT_HAND => "右手",
                ELimb.RIGHT_LEG => "右腿",
                ELimb.RIGHT_FOOT => "右脚",
                ELimb.LEFT_BACK => "左后背",
                ELimb.LEFT_FRONT => "左前胸",
                ELimb.RIGHT_BACK => "右后背",
                ELimb.RIGHT_FRONT => "右前胸",
                ELimb.SPINE => "脊椎",
                ELimb.SKULL => "头部",
                _ => limb.ToString()
            };
        }

        /// <summary>
        /// 安全获取玩家名称 - 从多个来源尝试获取
        /// </summary>
        private static string GetPlayerName(UnturnedPlayer player)
        {
            if (player == null)
                return "Unknown Player";

            try
            {
                if (!string.IsNullOrEmpty(player.CharacterName))
                    return player.CharacterName;
                if (!string.IsNullOrEmpty(player.DisplayName))
                    return player.DisplayName;
                if (player.SteamName != null)
                    return player.SteamName;
                return player.CSteamID.ToString();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to get player name: {ex.Message}");
                return player.CSteamID.ToString();
            }
        }

        /// <summary>
        /// 安全获取凶手名称
        /// </summary>
        private static string GetKillerName(CSteamID murderer)
        {
            try
            {
                var killer = UnturnedPlayer.FromCSteamID(murderer);
                if (killer != null)
                {
                    return GetPlayerName(killer);
                }
            }
            catch
            {
                // 对于环境死亡（如窒息），凶手信息可能不可用，静默处理
            }
            return null;
        }

        #endregion
    }
}
