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
        private static Task<bool> SendBoundedKookMessageAsync(Func<Task> sendOperation, string context)
        {
            return KookSendQueue.SendAsync(sendOperation, context, ShouldLogDebug, MaybeLogDiagnostics);
        }

        private static void CancelKookSends()
        {
            KookSendQueue.Cancel();
        }

        private static void ResetKookSendCancellation()
        {
            KookSendQueue.ResetCancellation();
        }

        private static void ResetRuntimeState()
        {
            EventDeduplicationStore.Reset();
            KookSendQueue.ResetMetrics();
            PvpEventRuntime.Reset();
            Interlocked.Exchange(ref _lastDiagnosticsLogTicks, 0);
        }

        private static void MaybeLogDiagnostics(string reason)
        {
            if (!ShouldLogDebug)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var nowTicks = now.Ticks;
            var previousTicks = Interlocked.Read(ref _lastDiagnosticsLogTicks);
            if (previousTicks != 0 && nowTicks - previousTicks < DiagnosticsLogIntervalMs * TimeSpan.TicksPerMillisecond)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastDiagnosticsLogTicks, nowTicks, previousTicks) != previousTicks)
            {
                return;
            }

            var send = KookSendQueue.Capture();
            var pvp = PvpEventRuntime.Capture();
            Logger.Log(
                $"[DEBUG] Events diagnostics ({reason}): pendingSends={send.Pending}, " +
                $"sendSuccess={send.Success}, sendCanceled={send.Canceled}, " +
                $"sendDropped={send.Dropped}, dedupeCache={EventDeduplicationStore.Count}, " +
                $"pvpReceived={pvp.Received}, pvpQueued={pvp.Queued}, " +
                $"pvpSent={pvp.Sent}, pvpCanceled={pvp.Canceled}, pvpFailed={pvp.Failed}, " +
                $"pvpBacklogDrop={pvp.BacklogDrop}, pvpDisabled={pvp.Disabled}, " +
                $"pvpInvalid={pvp.InvalidPlayer}, pvpSelf={pvp.SelfDamageSkipped}, " +
                $"pvpLowDamage={pvp.LowDamageSkipped}, pvpThrottled={pvp.Throttled}");
        }

    }
}
