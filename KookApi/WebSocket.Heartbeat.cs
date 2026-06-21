using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned.KookApi
{
    public partial class KookWebSocketClient
    {
        private void StartHeartbeat(long generation)
        {
            CancelHeartbeatLoop();
            // 使用Interlocked原子操作初始化_lastPongReceivedTicks
            Interlocked.Exchange(ref _lastPongReceivedTicks, DateTime.UtcNow.Ticks);

            // Random.Next(minValue, maxValue) 是左闭右开区间，所以 maxValue 要 +1
            int randomOffsetMs;
            lock (_randomLock)
            {
                randomOffsetMs = _random.Next(-HeartbeatRandomOffsetMs, HeartbeatRandomOffsetMs + 1);
            }
            int adjustedInterval = WebSocketProtocolPolicy.CalculateHeartbeatIntervalMs(BaseHeartbeatIntervalSeconds, randomOffsetMs);

            var heartbeatLoopCts = CreateLinkedTokenSource();
            _heartbeatLoopCts = heartbeatLoopCts;
            _heartbeatLoopTask = RunHeartbeatLoopAsync(generation, adjustedInterval, heartbeatLoopCts.Token);

            if (IsDebugEnabled)
            {
                Logger.Log($"💓 Heartbeat timer started, interval: {adjustedInterval}ms");
            }
        }

        private async Task SendHeartbeatAsync(long generation, CancellationToken cancellationToken, bool trackPongTimeout = true)
        {
            if (!IsCurrentConnectionGeneration(generation) || _webSocket?.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                // 取消之前可能存在的PONG超时检测任务，并安全地创建新的 CTS
                if (trackPongTimeout)
                {
                    CancellationToken pongToken;
                    lock (_pongTimeoutLock)
                    {
                        _pongTimeoutCts?.Cancel();
                        _pongTimeoutCts?.Dispose();
                        _pongTimeoutCts = new CancellationTokenSource();
                        pongToken = _pongTimeoutCts.Token;  // 在 lock 内获取 Token
                    }

                    // Start PONG timeout detection without Task.Run - use async pattern directly
                    _ = MonitorHeartbeatTimeoutAsync(generation, pongToken);
                }

                // 使用Interlocked原子操作读取_lastSequenceNumber
                var sn = Interlocked.Read(ref _lastSequenceNumber);
                var heartbeat = JsonConvert.SerializeObject(new { s = 2, sn = sn });
                await SendMessageAsync(heartbeat);
                if (IsDebugEnabled)
                {
                    Logger.Log("[DEBUG] Send heartbeat packet");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ Send HeartBeat Failed: {ex.Message}");
                await HandleReconnectAsync(generation);
            }
        }

        private async Task MonitorHeartbeatTimeoutAsync(long generation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activePongTimeoutMonitors);
            try
            {
                // 使用新的CancellationTokenSource的Token
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatPongTimeoutSeconds), cancellationToken);

                // 如果代码执行到这里，说明任务没有被取消（即在规定时间内没有收到PONG）
                // 再次确认_lastPongReceivedTicks，以防万一，但CTS是主要控制方式
                var lastPongTicks = Interlocked.Read(ref _lastPongReceivedTicks);
                var lastPong = new DateTime(lastPongTicks);
                if (IsCurrentConnectionGeneration(generation) &&
                    DateTime.UtcNow - lastPong > TimeSpan.FromSeconds(HeartbeatPongTimeoutSeconds))
                {
                    Logger.Log("⚠️ Heartbeat timeout, start reconnection process...");
                    await HandleHeartbeatTimeoutAsync(generation);
                }
            }
            catch (OperationCanceledException)
            {
                // 任务被取消，表示收到了PONG，这是正常情况
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ PONG timeout detection error occurred: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activePongTimeoutMonitors);
            }
        }

        private async Task HandleHeartbeatTimeoutAsync(long generation)
        {
            if (!IsCurrentConnectionGeneration(generation))
            {
                return;
            }

            await _reconnectSemaphore.WaitAsync();
            try
            {
                if (!IsCurrentConnectionGeneration(generation))
                {
                    return;
                }

                // Step 5: 先发两次心跳ping测试连接
                if (await TestConnectionWithPingsAsync(generation))
                {
                    Logger.Log("✅ Connection test successful, restored to normal");
                    return;
                }

                // Step 6: 尝试Resume
                if (await TryResumeAsync(generation))
                {
                    Logger.Log("✅ Resume successful");
                    return;
                }

                // Step 7: Resume失败，关闭连接让主循环处理重连
                Logger.Log("❌ Resume failed, closing connection to trigger reconnect...");
                await CloseWebSocketAsync();
            }
            finally
            {
                _reconnectSemaphore.Release();
            }
        }
    }
}
