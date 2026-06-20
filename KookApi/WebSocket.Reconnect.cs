using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Rocket.Core.Logging;
using System.IO;
using System.IO.Compression;
using Emqo.KookBot_Unturned.KookApi;
using Rocket.Unturned.Chat;
using Emqo.KookBot_Unturned;
using Emqo.KookBot_Unturned.Interfaces;
using Emqo.KookBot_Unturned.Utilities;
using Rocket.Core.Plugins;
using System.Collections.Generic;

namespace Emqo.KookBot_Unturned.KookApi
{
    public partial class KookWebSocketClient
    {
    private async Task<bool> TestConnectionWithPingsAsync(long generation)
    {
        Logger.Log("🏓 Start connection testing...");

        for (int i = 0; i < 2; i++)
        {
            try
            {
                if (!IsCurrentConnectionGeneration(generation))
                {
                    return false;
                }

                await SendHeartbeatAsync(generation, CancellationToken.None, trackPongTimeout: false);

                int delay = i == 0 ? 2000 : 4000; // 间隔2s, 4s
                await Task.Delay(delay, _cancellationTokenSource.Token);

                // 使用Interlocked原子操作读取_lastPongReceivedTicks
                var lastPongTicks = Interlocked.Read(ref _lastPongReceivedTicks);
                var lastPong = new DateTime(lastPongTicks);
                if (DateTime.UtcNow - lastPong < TimeSpan.FromSeconds(1))
                {
                    return true; // 连接正常
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ Connection test failed: {ex.Message}");
            }
        }

        return false;
    }

    private async Task<bool> TryResumeAsync(long generation)
    {
        Logger.Log("🔄 Attempt to Resume Connection...");

        for (int i = 0; i < 2; i++)
        {
            try
            {
                if (!IsCurrentConnectionGeneration(generation))
                {
                    return false;
                }

                SetResuming(true);
                Interlocked.Increment(ref _resumeAttemptCount);
                if (!await TryGetGatewayUrlForResumeAsync(i))
                {
                    SetResuming(false);
                    continue;
                }

                var resumeGeneration = await ConnectWebSocketAsync();
                await SendResumePayloadAsync();

                if (await WaitForResumeAckAsync(resumeGeneration))
                {
                    return true;
                }

                SetResuming(false);
                await CloseWebSocketAsync();
            }
            catch (Exception ex)
            {
                SetResuming(false);
                Logger.LogError($"❌ Resume 失败 (尝试 {i + 1}/2): {ex.Message}");
                await CloseWebSocketAsync();
            }

            await Task.Delay(i == 0 ? ResumeRetryDelay1Ms : ResumeRetryDelay2Ms);
        }

        return false;
    }

    /// <summary>
    /// Attempts to get a new Gateway URL for resume operation.
    /// </summary>
    /// <returns>True if successful, false if should retry.</returns>
    private async Task<bool> TryGetGatewayUrlForResumeAsync(int attemptIndex)
    {
        _gatewayUrl = await _kookMessageApi.GetGatewayAsync();
        if (string.IsNullOrEmpty(_gatewayUrl))
        {
            Logger.LogError("❌Unable to obtain a new Gateway URL during Resume attempt.");
            await Task.Delay(attemptIndex == 0 ? ResumeRetryDelay1Ms : ResumeRetryDelay2Ms);
            return false;
        }

        if (IsDebugEnabled)
        {
            Logger.Log($"[DEBUG] Resume attempts to obtain new Gateway URL: {_gatewayUrl}");
        }

        return true;
    }

    /// <summary>
    /// Sends the resume payload to the WebSocket.
    /// </summary>
    private async Task SendResumePayloadAsync()
    {
        SetResuming(true);

        var resumePayload = new
        {
            s = 4, // RESUME
            d = new
            {
                token = _botToken,
                session_id = _sessionId,
                sn = Interlocked.Read(ref _lastSequenceNumber)
            }
        };

        await SendMessageAsync(JsonConvert.SerializeObject(resumePayload));

        if (IsDebugEnabled)
        {
            Logger.Log("[DEBUG] Resume request sent");
        }
    }

    /// <summary>
    /// Waits for RESUME_ACK response.
    /// </summary>
    /// <returns>True if resume was acknowledged, false otherwise.</returns>
    private async Task<bool> WaitForResumeAckAsync(long expectedGeneration)
    {
        await Task.Delay(ResumeResponseWaitMs, _cancellationTokenSource.Token);

        bool isResuming;
        lock (_resumingLock)
        {
            isResuming = _isResuming;
        }

        if (IsCurrentConnectionGeneration(expectedGeneration) &&
            _webSocket != null &&
            _webSocket.State == WebSocketState.Open &&
            !isResuming)
        {
            if (IsDebugEnabled)
            {
                Logger.Log("[DEBUG] Resume successful, connection has been restored");
            }
            return true;
        }

        if (IsDebugEnabled)
        {
            Logger.Log("[DEBUG] Resume attempt failed: RESUME-ACK was not received within the specified time or WebSocket was not in an open state.");
        }

        return false;
    }

    private async Task HandleReconnectAsync(long generation)
    {
        if (!IsCurrentConnectionGeneration(generation))
        {
            return;
        }

        // 使用同一个信号量保护所有重连操作，防止任务堆积
        if (!await _reconnectSemaphore.WaitAsync(0))
        {
            Logger.Log("Reconnection already in progress, skipping...");
            return;
        }

        try
        {
            if (!IsCurrentConnectionGeneration(generation))
            {
                return;
            }

            Interlocked.Increment(ref _reconnectAttemptCount);
            // 关闭当前连接，让 ConnectWithRetryAsync 的主循环检测到并重连
            await CloseWebSocketAsync();
            LogDebugState("reconnect-requested");
        }
        finally
        {
            _reconnectSemaphore.Release();
        }
    }

    private async Task HandleReconnectPacketAsync()
    {
        // Step 8: 收到reconnect包，清空状态
        _sessionId = null;
        Interlocked.Exchange(ref _lastSequenceNumber, 0);
        lock (_resumingLock)
        {
            _isResuming = false;
        }

        await HandleReconnectAsync(Interlocked.Read(ref _connectionGeneration));
    }

    private async Task InfiniteRetryAsync()
    {
        int delay = InfiniteRetryInitialDelayMs; // Initial delay 1 second
        const int maxDelay = InfiniteRetryMaxDelayMs; // Max delay 60 seconds

        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                Logger.Log($"🔄 Infinite retry to obtain Gateway URL, retrying in {delay / 1000} seconds...");
                await Task.Delay(delay, _cancellationTokenSource.Token);

                // 重新获取 Gateway URL
                _gatewayUrl = await _kookMessageApi.GetGatewayAsync();

                if (string.IsNullOrEmpty(_gatewayUrl))
                {
                    Logger.LogError("❌ Failed to obtain Gateway URL, will retry.");
                    delay = Math.Min(delay * 2, maxDelay); // Exponential backoff
                    continue;
                }

                // 成功获取 Gateway URL，返回让调用方处理连接和接收
                Logger.Log("✅ Successfully obtained Gateway URL.");
                return;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ Gateway acquisition failed in infinite retry: {ex.Message}");
                delay = Math.Min(delay * 2, maxDelay); // Exponential backoff
            }
        }
    }
    }
}
