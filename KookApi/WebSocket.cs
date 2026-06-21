using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Rocket.Core.Logging;
using Emqo.KookBot_Unturned;
using Emqo.KookBot_Unturned.Interfaces;

namespace Emqo.KookBot_Unturned.KookApi
{
    public partial class KookWebSocketClient
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
        private const int HeartbeatRandomOffsetMs = 2000; // 心跳随机偏移 +/- 2 秒
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
                        int delay = WebSocketProtocolPolicy.CalculateInitialReconnectDelayMs(attempt); // Exponential backoff: 2s, 4s
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
    }
}
