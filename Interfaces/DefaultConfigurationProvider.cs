using System;

namespace Emqo.KookBot_Unturned.Interfaces
{
    /// <summary>
    /// Default implementation of IConfigurationProvider that wraps the plugin singleton.
    /// </summary>
    public class DefaultConfigurationProvider : IConfigurationProvider
    {
        private readonly Func<KookBot_UnturnedConfiguration> _configurationGetter;

        public DefaultConfigurationProvider(Func<KookBot_UnturnedConfiguration> configurationGetter)
        {
            _configurationGetter = configurationGetter ?? throw new ArgumentNullException(nameof(configurationGetter));
        }

        public KookBot_UnturnedConfiguration GetConfiguration()
        {
            return _configurationGetter();
        }

        public bool IsDebugEnabled => GetConfiguration()?.Debug ?? false;

        public string BotToken => GetConfiguration()?.BotToken;

        public string ChannelId => GetConfiguration()?.ChannelId;

        public ChatModerationConfig ModerationConfig => GetConfiguration()?.Moderation;

        public bool IsGameToKookEnabled(string eventName)
        {
            return GetConfiguration()?.IsGameToKookEnabled(eventName) ?? false;
        }

        public bool IsCommandEnabled(string commandName)
        {
            return GetConfiguration()?.IsCommandEnabled(commandName) ?? false;
        }
    }
}
