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
public class KookWebSocketClient
{
    private const int ReceiveBufferSize = 8192;  // WebSocket receive buffer size
    private const int MaxMessageLength = 500;  // Maximum message length from Kook

    private string _gatewayUrl;
    private readonly string _botToken;
    private readonly IConfigurationProvider _configProvider;
    private ClientWebSocket _webSocket;
    private CancellationTokenSource _cancellationTokenSource;
    private string _sessionId;
    private long _lastSequenceNumber;
    private volatile bool _isResuming = false;
    private volatile bool _isReceiving = false;
    private CancellationTokenSource _heartbeatLoopCts;
    private Task _heartbeatLoopTask;
    private long _lastPongReceivedTicks;  // 使用Ticks避免DateTime竞态条件
    private readonly SemaphoreSlim _reconnectSemaphore = new(1, 1);
    private Message _kookMessageApi;
    private readonly Random _random = new Random();
    private readonly object _randomLock = new object();  // Lock for Random thread safety
    private CancellationTokenSource _pongTimeoutCts;
    private CancellationTokenSource _helloTimeoutCts;
    private readonly object _pongTimeoutLock = new object();  // Lock for CTS operations
    private readonly object _helloTimeoutLock = new object();
    private readonly object _resumingLock = new object();  // Lock for _isResuming state changes
    private readonly object _receivingLock = new object();  // Lock for _isReceiving state changes
    private long _connectionGeneration;
    private long _reconnectAttemptCount;
    private long _resumeAttemptCount;
    private long _resumeSuccessCount;
    private long _helloTimeoutTriggerCount;
    private int _activeHeartbeatLoops;
    private int _activeHelloTimeoutMonitors;
    private int _activePongTimeoutMonitors;
    private const int BaseHeartbeatIntervalSeconds = 30; // 基础心跳间隔 30 秒
    private const int HeartbeatRandomOffsetMs = 2000; // 心跳随机偏移 +/- 5 秒 (5000毫秒)
    private const int HeartbeatPongTimeoutSeconds = 6; // PONG 超时 6 秒
    private const int HelloTimeoutSeconds = 6; // HELLO 包超时 6 秒
    private const int PingTestDelay1Ms = 2000; // 心跳测试延迟 2秒
    private const int PingTestDelay2Ms = 4000; // 心跳测试延迟 4秒
    private const int ResumeResponseWaitMs = 5000; // Resume 响应等待 5秒
    private const int ResumeRetryDelay1Ms = 8000; // Resume 重试延迟 8秒
    private const int ResumeRetryDelay2Ms = 16000; // Resume 重试延迟 16秒
    private const int InfiniteRetryInitialDelayMs = 1000; // 无限重试初始延迟 1秒
    private const int InfiniteRetryMaxDelayMs = 60000; // 无限重试最大延迟 60秒

    private bool IsDebugEnabled => _configProvider?.IsDebugEnabled ?? false;
    private KookBot_UnturnedConfiguration Config => _configProvider?.GetConfiguration();

    public KookWebSocketClient(string botToken, IConfigurationProvider configProvider = null)
    {
        _botToken = botToken;
        _configProvider = configProvider ?? new DefaultConfigurationProvider(() => KookBot_UnturnedPlugin.Instance?.Configuration?.Instance);
        _kookMessageApi = new Message(_botToken);
        _lastPongReceivedTicks = DateTime.UtcNow.Ticks;  // 初始化为当前时间
    }

    public async Task StartAsync()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        ResetConnectionDiagnostics();
        Logger.Log("🚀 WebSocket client starting...");
        try
        {
            await ConnectWithRetryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError($"❌ WebSocket client fatal error: {ex.Message}");
            if (ex.InnerException != null)
            {
                Logger.LogError($"Inner exception: {ex.InnerException.Message}");
            }
        }
    }

    public async Task StopAsync()
    {
        var heartbeatTask = _heartbeatLoopTask;
        try
        {
            _cancellationTokenSource?.Cancel();
            CancelHelloTimeoutMonitor();
            CancelHeartbeatLoop();
            await CloseWebSocketAsync();
            if (heartbeatTask != null)
            {
                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _reconnectSemaphore?.Dispose();  // 释放 SemaphoreSlim
        }
    }

    private async Task ConnectWithRetryAsync()
    {
        int attempt = 0;
        const int maxAttempts = 3; // Initial connection attempts

        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                // 在这里获取 Gateway URL
                _gatewayUrl = await _kookMessageApi.GetGatewayAsync();

                // 检查 Gateway URL 是否成功获取
                if (string.IsNullOrEmpty(_gatewayUrl))
                {
                    Logger.LogError("❌ Unable to obtain Gateway URL, will retry...");
                    await InfiniteRetryAsync();
                    attempt = 0;
                    // InfiniteRetryAsync 成功后 _gatewayUrl 已设置，继续连接
                    if (string.IsNullOrEmpty(_gatewayUrl))
                        continue;
                }

                Logger.Log("🔄 Get Websocket URL: " + _gatewayUrl);

                // Step 2: 连接Gateway
                var generation = await ConnectWebSocketAsync();

                // Step 3: 等待Hello包并开始接收事件
                await StartReceivingAsync(generation);

                // 如果 StartReceivingAsync 返回，说明接收循环已停止，需要重新连接
                Logger.LogWarning("⚠️ Receive loop stopped, attempting to reconnect...");
                attempt = 0; // 重置重试计数

                // 防止紧密循环：重连前等待
                await Task.Delay(2000, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError($"❌ Connect Failed (Try {attempt + 1}/{maxAttempts}): {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.LogError($"Inner exception: {ex.InnerException.Message}");
                }

                if (attempt < maxAttempts - 1)
                {
                    attempt++;
                    int delay = (int)Math.Pow(2, attempt) * 1000; // Exponential backoff: 2s, 4s
                    Logger.Log($"⏰ {delay / 1000} seconds until next connection attempt...");
                    try
                    {
                        await Task.Delay(delay, _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                else
                {
                    // Reached max initial attempts, enter infinite retry for Gateway
                    await InfiniteRetryAsync();
                    attempt = 0;
                }
            }
        }
    }

    private async Task<long> ConnectWebSocketAsync()
    {
        await CloseWebSocketAsync();

        var generation = Interlocked.Increment(ref _connectionGeneration);
        ClientWebSocket newWebSocket = null;

        try
        {
            newWebSocket = new ClientWebSocket();
            await newWebSocket.ConnectAsync(new Uri(_gatewayUrl), _cancellationTokenSource.Token);
            _webSocket = newWebSocket;

            Logger.Log("✅ KOOK gateway connection successful: " + _gatewayUrl);
            LogDebugState($"connect:generation-{generation}");
            return generation;
        }
        catch
        {
            newWebSocket?.Dispose();
            throw;
        }
    }

    private async Task StartReceivingAsync(long expectedGeneration)
    {
        // 防止多个接收循环同时运行 - 使用 lock 进行原子操作
        lock (_receivingLock)
        {
            if (_isReceiving)
            {
                Logger.LogWarning("⚠️ Receive loop is already running, skipping duplicate start");
                return;
            }
            _isReceiving = true;
        }
        try
        {
            var buffer = new byte[ReceiveBufferSize];
            await RunReceiveLoopAsync(buffer, expectedGeneration);
        }
        catch (Exception ex)
        {
            Logger.LogError($"❌ Receive loop error: {ex.Message}");
        }
        finally
        {
            lock (_receivingLock)
            {
                _isReceiving = false;
            }
        }
    }

    /// <summary>
    /// Sets up Hello packet timeout detection if this is a fresh connection.
    /// </summary>
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

    private void StartHeartbeat(long generation)
    {
        CancelHeartbeatLoop();
        // 使用Interlocked原子操作初始化_lastPongReceivedTicks
        Interlocked.Exchange(ref _lastPongReceivedTicks, DateTime.UtcNow.Ticks);

        int baseIntervalMs = BaseHeartbeatIntervalSeconds * 1000; // 30秒转换为毫秒
                                                                  // Random.Next(minValue, maxValue) 是左闭右开区间，所以 maxValue 要 +1
        int randomOffsetMs;
        lock (_randomLock)
        {
            randomOffsetMs = _random.Next(-HeartbeatRandomOffsetMs, HeartbeatRandomOffsetMs + 1); // -5000 到 +5000 毫秒
        }
        int adjustedInterval = baseIntervalMs + randomOffsetMs;

        // 确保间隔不会过短，例如至少 1 秒
        if (adjustedInterval < 1000) adjustedInterval = 1000;

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



    private async Task MonitorHelloTimeoutAsync(long generation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeHelloTimeoutMonitors);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(HelloTimeoutSeconds), cancellationToken);
            if (IsCurrentConnectionGeneration(generation) && _sessionId == null)
            {
                Interlocked.Increment(ref _helloTimeoutTriggerCount);
                Logger.LogError("❌ No Hello packet received within 6 seconds, triggering a complete reconnection...");
                await HandleReconnectAsync(generation);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError($"❌ Hello timeout detection error occurred: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeHelloTimeoutMonitors);
        }
    }

    private async Task RunHeartbeatLoopAsync(long generation, int intervalMs, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeHeartbeatLoops);
        try
        {
            while (!cancellationToken.IsCancellationRequested && !(_cancellationTokenSource?.IsCancellationRequested ?? true))
            {
                await Task.Delay(intervalMs, cancellationToken);
                if (!IsCurrentConnectionGeneration(generation))
                {
                    break;
                }

                await SendHeartbeatAsync(generation, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError($"❌ Heartbeat loop failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeHeartbeatLoops);
        }
    }

    private void CancelPongTimeoutMonitor()
    {
        lock (_pongTimeoutLock)
        {
            _pongTimeoutCts?.Cancel();
            _pongTimeoutCts?.Dispose();
            _pongTimeoutCts = null;
        }
    }

    private void CancelHelloTimeoutMonitor()
    {
        lock (_helloTimeoutLock)
        {
            _helloTimeoutCts?.Cancel();
            _helloTimeoutCts?.Dispose();
            _helloTimeoutCts = null;
        }
    }

    private void CancelHeartbeatLoop()
    {
        _heartbeatLoopCts?.Cancel();
        _heartbeatLoopCts?.Dispose();
        _heartbeatLoopCts = null;
        _heartbeatLoopTask = null;
    }

    private CancellationTokenSource CreateLinkedTokenSource()
    {
        return _cancellationTokenSource == null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
    }

    private bool IsCurrentConnectionGeneration(long generation)
    {
        return generation == Interlocked.Read(ref _connectionGeneration);
    }

    private bool TryAdoptLatestConnectionGeneration(ref long observedGeneration, string reason)
    {
        var latestGeneration = Interlocked.Read(ref _connectionGeneration);
        if (latestGeneration == observedGeneration || _webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            return false;
        }

        observedGeneration = latestGeneration;
        if (IsDebugEnabled)
        {
            Logger.Log($"[DEBUG] Receive loop adopted connection generation {observedGeneration} after {reason}");
        }

        return true;
    }

    private async Task<long?> WaitForSocketSwapAsync(long observedGeneration, string reason)
    {
        if (!IsResumingConnection())
        {
            return null;
        }

        try
        {
            await Task.Delay(250, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return TryAdoptLatestConnectionGeneration(ref observedGeneration, $"{reason}:wait")
            ? observedGeneration
            : null;
    }

    private bool IsResumingConnection()
    {
        lock (_resumingLock)
        {
            return _isResuming;
        }
    }

    private void SetResuming(bool isResuming)
    {
        lock (_resumingLock)
        {
            _isResuming = isResuming;
        }
    }

    private void ResetConnectionDiagnostics()
    {
        Interlocked.Exchange(ref _connectionGeneration, 0);
        Interlocked.Exchange(ref _reconnectAttemptCount, 0);
        Interlocked.Exchange(ref _resumeAttemptCount, 0);
        Interlocked.Exchange(ref _resumeSuccessCount, 0);
        Interlocked.Exchange(ref _helloTimeoutTriggerCount, 0);
        Interlocked.Exchange(ref _activeHeartbeatLoops, 0);
        Interlocked.Exchange(ref _activeHelloTimeoutMonitors, 0);
        Interlocked.Exchange(ref _activePongTimeoutMonitors, 0);
    }

    private void LogDebugState(string reason)
    {
        if (!IsDebugEnabled)
        {
            return;
        }

        Logger.Log(
            $"[DEBUG] WebSocket diagnostics ({reason}): generation={Interlocked.Read(ref _connectionGeneration)}, " +
            $"reconnects={Interlocked.Read(ref _reconnectAttemptCount)}, resumeAttempts={Interlocked.Read(ref _resumeAttemptCount)}, " +
            $"resumeSuccesses={Interlocked.Read(ref _resumeSuccessCount)}, helloTimeouts={Interlocked.Read(ref _helloTimeoutTriggerCount)}, " +
            $"receiving={_isReceiving}, activeHeartbeatLoops={Volatile.Read(ref _activeHeartbeatLoops)}, " +
            $"activeHelloTimeouts={Volatile.Read(ref _activeHelloTimeoutMonitors)}, activePongTimeouts={Volatile.Read(ref _activePongTimeoutMonitors)}");
    }

    private async Task SendMessageAsync(string message)
    {
        var cancellationTokenSource = _cancellationTokenSource;
        if (cancellationTokenSource == null)
        {
            return;
        }

        if (_webSocket?.State == WebSocketState.Open)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationTokenSource.Token);
        }
    }

    private async Task CloseWebSocketAsync()
    {
        try
        {
            CancelHeartbeatLoop();
            CancelHelloTimeoutMonitor();
            CancelPongTimeoutMonitor();

            // 释放WebSocket
            var webSocket = _webSocket;
            _webSocket = null;
            if (webSocket != null)
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                webSocket.Dispose();
            }

            // 不释放主CTS - 它在StartAsync中创建，在StopAsync中释放
            // 这样可以避免在ConnectWebSocketAsync中使用已释放的CTS
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error closing WebSocket: {ex.Message}");
        }
    }

    public class KookPayload
    {
        public int s { get; set; }
        public string t { get; set; }
        public long? sn { get; set; }
        public PayloadData d { get; set; }
    }

    public class PayloadData
    {
        public string session_id { get; set; }
        public string content { get; set; }
        public ExtraData extra { get; set; }

        // 新增字段，匹配JSON结构
        public string channel_type { get; set; }
        public string target_id { get; set; }
        public string author_id { get; set; }
        public string msg_id { get; set; }
        public long msg_timestamp { get; set; }
        public string nonce { get; set; }
        public int from_type { get; set; }
    }

    public class ExtraData
    {
        public AuthorData author { get; set; }

        // 将 type 的类型改为 'object'，以同时处理整数和字符串
        public object type { get; set; }
        public string code { get; set; }
        public string guild_id { get; set; }
        public int guild_type { get; set; }
        public string channel_name { get; set; }
        public string visible_only { get; set; }
        public string[] mention { get; set; }
        public string[] mention_no_at { get; set; }
        public bool mention_all { get; set; }
        public string[] mention_roles { get; set; }
        public bool mention_here { get; set; }
        public string[] nav_channels { get; set; }
        public KMarkdownData kmarkdown { get; set; }
        public string[] emoji { get; set; }
        public string preview_content { get; set; }
        public string last_msg_content { get; set; }
        public int send_msg_device { get; set; }
        public BodyData body { get; set; }
    }
    public class KMarkdownData
    {
        public string raw_content { get; set; }
        public MentionPart[] mention_part { get; set; }
        public MentionRolePart[] mention_role_part { get; set; }
        public ChannelPart[] channel_part { get; set; }
        public string[] spl { get; set; }
    }
    public class MentionPart
    {
        public string id { get; set; }
        public string username { get; set; }
        public string full_name { get; set; }
        public string avatar { get; set; }
    }
    public class MentionRolePart
    {
        // 根据实际返回内容调整
        public string role_id { get; set; }
        public string role_name { get; set; }
    }
    public class ChannelPart
    {
        // 根据实际返回内容调整
        public string channel_id { get; set; }
        public string channel_name { get; set; }
    }
    public class BodyData
    {
        public string user_id { get; set; }
        public long event_time { get; set; }
        public string[] guilds { get; set; }
    }
    public class AuthorData
    {
        public string id { get; set; }
        public string username { get; set; }
        public string identify_num { get; set; }
        public string nickname { get; set; }

        public string avatar { get; set; }
        public string vip_avatar { get; set; }
        public string banner { get; set; }
        public bool online { get; set; }
        public string os { get; set; }
        public int status { get; set; }
        public bool is_vip { get; set; }
        public bool vip_amp { get; set; }
        public bool bot { get; set; }
        public bool is_sys { get; set; }

        public string[] roles { get; set; }
        public object[] nameplate { get; set; }
        public DecorationsIdMap decorations_id_map { get; set; }
    }
    public class DecorationsIdMap
    {
        public int? join_voice { get; set; }
        public int? background { get; set; }
        public int? avatar_border { get; set; }
        public int? nameplate { get; set; }
        public int[] nameplates { get; set; }
    }

    private static byte[] DecompressZlib(byte[] zlibData)
    {
        // 跳过Zlib头部的前两个字节
        using var compressedStream = new MemoryStream(zlibData, 2, zlibData.Length - 2);
        using var deflateStream = new System.IO.Compression.DeflateStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
        using var resultStream = new MemoryStream();
        deflateStream.CopyTo(resultStream);
        return resultStream.ToArray();
    }
}
}
