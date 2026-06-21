using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned.KookApi
{
    public partial class KookWebSocketClient
    {
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

    }
}
