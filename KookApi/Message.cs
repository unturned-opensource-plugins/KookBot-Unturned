using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Emqo.KookBot_Unturned.KookApi
{
    public class Message
    {
        private readonly string _baseUrl = "https://www.kookapp.cn/api/v3";
        private readonly string _botToken;
        private const int MaxRetries = 3;  // 最大重试次数

        public Message(string botToken)
        {
            _botToken = botToken;
        }

        public async Task<string> CreateMessageAsync(int type, string channelId, object content)
        {
            var url = $"{_baseUrl}/message/create";

            string contentString;

            // 根据消息类型和内容类型处理 content
            if (type == 10) // 卡片消息
            {
                if (content is string stringContent)
                {
                    // 如果传入的是字符串，假设已经是 JSON 格式
                    contentString = stringContent;
                }
                else
                {
                    // 如果传入的是对象，序列化为 JSON
                    contentString = JsonConvert.SerializeObject(content);
                }
            }
            else // 文本消息 (type=1) 或 KMarkdown 消息 (type=9)
            {
                contentString = content?.ToString() ?? "";
            }

            // 创建消息请求对象
            var messageRequest = new
            {
                type = type,
                target_id = channelId,
                content = contentString
            };

            // 序列化为 JSON
            var postData = JsonConvert.SerializeObject(messageRequest);
            var postDataBytes = Encoding.UTF8.GetBytes(postData);

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = postDataBytes.Length;
            request.Headers.Add("Authorization", $"Bot {_botToken}");
            // Add timeout protection (30 seconds)
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;

            // 写入请求体
            using (var requestStream = await request.GetRequestStreamAsync())
            {
                await requestStream.WriteAsync(postDataBytes, 0, postDataBytes.Length);
            }

            // 获取响应
            try
            {
                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return await reader.ReadToEndAsync();
                }
            }
            catch (WebException ex)
            {
                // 尝试读取错误响应
                if (ex.Response != null)
                {
                    using (var errorStream = ex.Response.GetResponseStream())
                    using (var reader = new StreamReader(errorStream, Encoding.UTF8))
                    {
                        var errorResponse = await reader.ReadToEndAsync();
                        throw new Exception($"HTTP Error {(ex.Response as HttpWebResponse)?.StatusCode}: {errorResponse}", ex);
                    }
                }
                throw;
            }
        }



        public async Task<string> GetGatewayAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var request = (HttpWebRequest)WebRequest.Create($"{_baseUrl}/gateway/index");
                request.Method = "GET";
                request.Headers.Add("Authorization", $"Bot {_botToken}");
                request.Timeout = 30000;
                request.ReadWriteTimeout = 30000;

                using var response = (HttpWebResponse)await request.GetResponseAsync();
                using var reader = new StreamReader(response.GetResponseStream());
                var body = await reader.ReadToEndAsync();
                var json = JObject.Parse(body);
                return json["data"]?["url"]?.ToString();
            });
        }

        public async Task<string> GetMeAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var request = (HttpWebRequest)WebRequest.Create($"{_baseUrl}/user/me");
                request.Method = "GET";
                request.Headers.Add("Authorization", $"Bot {_botToken}");
                request.Timeout = 30000;
                request.ReadWriteTimeout = 30000;

                using var response = (HttpWebResponse)await request.GetResponseAsync();
                using var reader = new StreamReader(response.GetResponseStream());
                var body = await reader.ReadToEndAsync();
                var json = JObject.Parse(body);
                return json["data"]?["id"]?.ToString();
            });
        }

        private static async Task<string> ReadErrorResponseAsync(WebException ex)
        {
            if (ex.Response is HttpWebResponse errorResponse)
            {
                using var errorStream = errorResponse.GetResponseStream();
                if (errorStream != null)
                {
                    using var reader = new StreamReader(errorStream, Encoding.UTF8);
                    var errorBody = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(errorBody))
                    {
                        return errorBody;
                    }
                }

                return !string.IsNullOrWhiteSpace(errorResponse.StatusDescription)
                    ? errorResponse.StatusDescription
                    : ex.Message;
            }

            return ex.Message;
        }

        /// <summary>
        /// 执行HTTP请求，带重试机制
        /// </summary>
        private async Task<string> ExecuteWithRetryAsync(Func<Task<string>> requestFunc)
        {
            int retryCount = 0;
            while (retryCount < MaxRetries)
            {
                try
                {
                    return await requestFunc();
                }
                catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout ||
                                              ex.Status == WebExceptionStatus.ConnectFailure ||
                                              ex.Status == WebExceptionStatus.ReceiveFailure)
                {
                    retryCount++;
                    if (retryCount >= MaxRetries)
                        throw;

                    // 指数退避: 1s, 2s, 4s
                    var delayMs = (int)Math.Pow(2, retryCount - 1) * 1000;
                    await Task.Delay(delayMs);
                }
                catch (WebException ex)
                {
                    // Check for rate limiting (429 Too Many Requests)
                    var statusCode = (ex.Response as HttpWebResponse)?.StatusCode;
                    if (statusCode == (HttpStatusCode)429)
                    {
                        retryCount++;
                        if (retryCount >= MaxRetries)
                        {
                            var details = await ReadErrorResponseAsync(ex);
                            throw new Exception($"HTTP Error 429 (Rate Limited): {details}", ex);
                        }

                        // Exponential backoff for rate limiting: 2s, 4s, 8s
                        var delayMs = (int)Math.Pow(2, retryCount) * 1000;
                        await Task.Delay(delayMs);
                        continue;
                    }

                    // 非重试异常，直接抛出
                    var details2 = await ReadErrorResponseAsync(ex);
                    throw new Exception($"HTTP Error {(int?)statusCode} {statusCode}: {details2}", ex);
                }
            }

            throw new Exception("Max retries exceeded");
        }
    }
}
