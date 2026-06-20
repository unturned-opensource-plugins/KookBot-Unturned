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
    }
}
