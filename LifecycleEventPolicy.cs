namespace Emqo.KookBot_Unturned
{
    internal static class LifecycleEventPolicy
    {
        public static bool ShouldSendServerStop(KookBot_UnturnedConfiguration config, bool isFullyLoaded, bool hasMessageApi)
        {
            return isFullyLoaded
                && hasMessageApi
                && config?.IsGameToKookEnabled("ServerStop") == true;
        }
    }
}
