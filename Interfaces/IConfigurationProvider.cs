namespace Emqo.KookBot_Unturned.Interfaces
{
    /// <summary>
    /// Provides access to plugin configuration for dependency injection.
    /// Allows components to be decoupled from the plugin singleton.
    /// </summary>
    public interface IConfigurationProvider
    {
        /// <summary>
        /// Gets the current configuration instance.
        /// </summary>
        KookBot_UnturnedConfiguration GetConfiguration();

        /// <summary>
        /// Gets whether debug logging is enabled.
        /// </summary>
        bool IsDebugEnabled { get; }

        /// <summary>
        /// Gets the bot token for Kook API authentication.
        /// </summary>
        string BotToken { get; }

        /// <summary>
        /// Gets the channel ID for message forwarding.
        /// </summary>
        string ChannelId { get; }

        /// <summary>
        /// Gets the chat moderation configuration.
        /// </summary>
        ChatModerationConfig ModerationConfig { get; }

        /// <summary>
        /// Checks if a specific game-to-Kook event is enabled.
        /// </summary>
        bool IsGameToKookEnabled(string eventName);

        /// <summary>
        /// Checks if a specific command is enabled.
        /// </summary>
        bool IsCommandEnabled(string commandName);
    }
}
