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
