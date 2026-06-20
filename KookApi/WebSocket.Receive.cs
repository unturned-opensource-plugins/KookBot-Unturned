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
    private void SetupHelloTimeoutIfNeeded(long generation)
    {
        bool isResuming;
        lock (_resumingLock)
        {
            isResuming = _isResuming;
        }

        if (!isResuming && _sessionId == null)
        {
            Logger.Log("⏱️ Setting up Hello packet timeout detection (6 seconds)...");

            CancellationTokenSource helloTimeoutCts;
            lock (_helloTimeoutLock)
            {
                _helloTimeoutCts?.Cancel();
                _helloTimeoutCts?.Dispose();
                _helloTimeoutCts = CreateLinkedTokenSource();
                helloTimeoutCts = _helloTimeoutCts;
            }

            _ = MonitorHelloTimeoutAsync(generation, helloTimeoutCts.Token);
        }
    }

    /// <summary>
    /// Main receive loop that processes incoming WebSocket messages.
    /// </summary>
    private async Task RunReceiveLoopAsync(byte[] buffer, long observedGeneration)
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            SetupHelloTimeoutIfNeeded(observedGeneration);

            try
            {
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    if (TryAdoptLatestConnectionGeneration(ref observedGeneration, "socket-not-open"))
                    {
                        continue;
                    }

                    break;
                }

                var messageText = await ReceiveMessageAsync(buffer);
                if (messageText != null)
                {
                    await ProcessMessageAsync(messageText);
                }
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                if (TryAdoptLatestConnectionGeneration(ref observedGeneration, "connection-closed-prematurely"))
                {
                    continue;
                }

                var swappedGeneration = await WaitForSocketSwapAsync(observedGeneration, "connection-closed-prematurely");
                if (swappedGeneration.HasValue)
                {
                    observedGeneration = swappedGeneration.Value;
                    continue;
                }

                Logger.LogError("🔌 WebSocket connection unexpectedly disconnected");
                await HandleReconnectAsync(observedGeneration);
                break; // 退出循环，让主循环重连
            }
            catch (WebSocketException ex)
            {
                if (TryAdoptLatestConnectionGeneration(ref observedGeneration, "websocket-exception"))
                {
                    continue;
                }

                var swappedGeneration = await WaitForSocketSwapAsync(observedGeneration, "websocket-exception");
                if (swappedGeneration.HasValue)
                {
                    observedGeneration = swappedGeneration.Value;
                    continue;
                }

                Logger.LogError($"❌ WebSocket error: {ex.WebSocketErrorCode} - {ex.Message}");
                await HandleReconnectAsync(observedGeneration);
                break; // 退出循环，让主循环重连
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (TryAdoptLatestConnectionGeneration(ref observedGeneration, "receive-exception"))
                {
                    continue;
                }

                var swappedGeneration = await WaitForSocketSwapAsync(observedGeneration, "receive-exception");
                if (swappedGeneration.HasValue)
                {
                    observedGeneration = swappedGeneration.Value;
                    continue;
                }

                Logger.LogError($"❌ Error occurred while receiving message: {ex.Message}");
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    await HandleReconnectAsync(observedGeneration);
                    break; // 退出循环，让主循环重连
                }
                await Task.Delay(1000, _cancellationTokenSource.Token);
            }
        }
    }

    /// <summary>
    /// Receives a complete message from the WebSocket.
    /// </summary>
    /// <returns>The message text, or null if no valid message was received.</returns>
    private async Task<string> ReceiveMessageAsync(byte[] buffer)
    {
        // 检查WebSocket是否有效
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            Logger.LogWarning("⚠️ WebSocket is not available for receiving");
            return null;
        }

        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);

        if (result.MessageType == WebSocketMessageType.Text)
        {
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        else if (result.MessageType == WebSocketMessageType.Binary)
        {
            byte[] decompressed = DecompressZlib(ms.ToArray());
            return Encoding.UTF8.GetString(decompressed);
        }

        return null;
    }

    private async Task ProcessMessageAsync(string message)
    {
        if (IsDebugEnabled)
        {
            Logger.Log("[DEBUG] Received message: " + message);
        }
            

        KookPayload payload;
        try
        {
            payload = JsonConvert.DeserializeObject<KookPayload>(message);
        }
        catch (Exception ex)
        {
            Logger.LogError("❌ Message parsing failed: " + ex.Message);
            return;
        }

        switch (payload.s)
        {
            case 0: // EVENT
                if (payload.sn.HasValue)
                {
                    // 使用Interlocked原子操作更新_lastSequenceNumber
                    Interlocked.Exchange(ref _lastSequenceNumber, payload.sn.Value);
                }

                // 统一处理 int 和 long 类型
                long? messageTypeValue = null;
                if (payload.d?.extra?.type is int intValue)
                {
                    messageTypeValue = intValue;
                }
                else if (payload.d?.extra?.type is long longValue)
                {
                    messageTypeValue = longValue;
                }

                if (messageTypeValue == 9 && payload.d?.content != null)
                {
                    await HandleTextMessageAsync(payload.d);
                }
                else if (payload.d?.extra?.type is string messageStringType)
                {
                    // ... 系统消息处理逻辑，例如 "guild_member_online" ...
                    if (messageStringType == "guild_member_online")
                    {
                        string userId = payload.d.extra?.body?.user_id ?? "Unknown User";
                        string guildId = payload.d.extra?.body?.guilds?.Length > 0 ? payload.d.extra.body.guilds[0] : "Unknown Guild";
                        Logger.Log($"ℹ️ KOOK System Message: User {userId} came online in guild {guildId}.");
                    }
                    else
                    {
                        Logger.Log($"ℹ️ Received KOOK event with string type: {messageStringType}");
                    }
                }
                else if (messageTypeValue != null)
                {
                    if (IsDebugEnabled)
                    {
                        Logger.Log($"DEBUG: Unprocessed message type: {messageTypeValue}");
                    }
                }
                else
                {
                    if (IsDebugEnabled)
                    {
                        Logger.Log("DEBUG: Unprocessed payload.d.extra.type type or value is null.");
                    }
                }


                break;

            case 1: // HELLO
                Logger.Log("👋 Received Hello package");

                if (payload.d != null)
                {
                    CancelHelloTimeoutMonitor();
                    _sessionId = payload.d.session_id;
                    Logger.Log($"✅ Session ID set: {_sessionId}");
                    var generation = Interlocked.Read(ref _connectionGeneration);
                    StartHeartbeat(generation);

                    bool isResuming;
                    lock (_resumingLock)
                    {
                        isResuming = _isResuming;
                    }

                    if (!isResuming)
                    {
                        await SendHeartbeatAsync(generation, CancellationToken.None);
                    }
                }
                else
                {
                    Logger.LogWarning("⚠️ Hello package received but payload.d is null");
                }
                break;

            case 3: // PONG - 心跳响应
                // 使用Interlocked原子操作更新_lastPongReceivedTicks
                Interlocked.Exchange(ref _lastPongReceivedTicks, DateTime.UtcNow.Ticks);
                CancelPongTimeoutMonitor();
                if (IsDebugEnabled)
                {
                    Logger.Log("[DEBUG] Received heartbeat response");
                }
                break;

            case 5: // RECONNECT
                Logger.Log("🔄 Received reconnection command, clear status...");
                await HandleReconnectPacketAsync();
                break;

            case 6: // RESUME_ACK
                Logger.Log("✅ Resume successful, connection has been restored");
                lock (_resumingLock)
                {
                    _isResuming = false;
                }
                Interlocked.Increment(ref _resumeSuccessCount);
                LogDebugState("resume-ack");
                break;
        }
    }

    /// <summary>
    /// 处理文本消息（统一处理，消除重复代码）
    /// </summary>
    private async Task HandleTextMessageAsync(PayloadData payloadData)
    {
        string id = payloadData.extra?.author?.id ?? "000000";
        string nickname = payloadData.extra?.author?.nickname ?? "KOOK用户";
        string channelId = payloadData.target_id;
        string content = payloadData.content;

        var config = Config;
        // 检查是否应该处理此消息
        if (id == KookBot_UnturnedPlugin.authid ||
            config == null ||
            !config.KookToGame ||
            channelId != config.ChannelId)
        {
            return;
        }

        // 处理指令或转发消息
        if (content.StartsWith("/"))
        {
            // Commands.Init 已在插件加载时调用，无需重复初始化
            await Commands.ExecuteAsync(nickname, content, id, config.ChannelId);
        }
        else
        {
            // 安全加固：输入验证和清理
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            // 限制消息长度，防止超长消息攻击
            if (content.Length > MaxMessageLength)
            {
                Logger.LogWarning($"⚠️ Message from {id} exceeded max length ({content.Length} > {MaxMessageLength}), truncating");
                content = content.Substring(0, MaxMessageLength) + "...";
            }

            // 清理昵称，防止富文本注入
            string sanitizedNickname = StringUtils.SanitizeRichText(nickname);

            // 清理消息内容，防止富文本注入
            string sanitizedContent = StringUtils.SanitizeRichText(content);

            // 格式化消息
            string formatted = config.EnableSync
                ? $"{config.MessagePrefix} {sanitizedNickname}: {sanitizedContent}"
                : $"{sanitizedNickname}: {sanitizedContent}";

            Logger.Log("🗨️ Forward to game: " + formatted);
            if (!MainThreadDispatcherGuard.TryQueue(() =>
            {
                try
                {
                    SDG.Unturned.ChatManager.serverSendMessage(
                        formatted,
                        UnityEngine.Color.white,
                        null,
                        null,
                        SDG.Unturned.EChatMode.GLOBAL,
                        null,
                        false // 禁用 Rich Text 以防止注入攻击
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError($"❌ Failed to send message to game: {ex.Message}");
                }
            }))
            {
                Logger.LogWarning($"⚠️ Main thread dispatcher is saturated, drop KOOK message from {sanitizedNickname}");
            }
        }
    }
    }
}
