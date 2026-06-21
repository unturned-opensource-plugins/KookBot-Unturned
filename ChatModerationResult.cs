namespace Emqo.KookBot_Unturned
{
    internal class ChatModerationResult
    {
        public bool IsAllowed { get; set; }
        public bool WasAutoMute { get; set; }
        public string DenyReason { get; set; }
        public string BlockReason { get; set; }
        public MuteInfo AppliedMute { get; set; }
    }
}
