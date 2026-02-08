using System.Threading.Tasks;
using Rocket.Unturned.Player;
using Steamworks;

namespace Emqo.KookBot_Unturned.Detectors
{
    /// <summary>
    /// Interface for message detection plugins.
    /// Implementations can detect spam, forbidden words, or other violations.
    /// </summary>
    public interface IMessageDetector
    {
        /// <summary>
        /// Unique name for this detector.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Whether this detector is currently enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Evaluate a message for violations.
        /// </summary>
        /// <param name="player">The player who sent the message.</param>
        /// <param name="message">The message content.</param>
        /// <returns>Detection result indicating if a violation was found.</returns>
        Task<DetectionResult> DetectAsync(UnturnedPlayer player, string message);

        /// <summary>
        /// Synchronously evaluate a message for violations (for blocking chat events).
        /// </summary>
        /// <param name="player">The player who sent the message.</param>
        /// <param name="message">The message content.</param>
        /// <returns>Detection result indicating if a violation was found.</returns>
        DetectionResult DetectSync(UnturnedPlayer player, string message);

        /// <summary>
        /// Initialize the detector with configuration.
        /// </summary>
        /// <param name="config">The moderation configuration.</param>
        void Initialize(ChatModerationConfig config);

        /// <summary>
        /// Update the detector configuration (for hot reload).
        /// </summary>
        /// <param name="config">The new moderation configuration.</param>
        void UpdateConfig(ChatModerationConfig config);

        /// <summary>
        /// Clean up resources when shutting down.
        /// </summary>
        void Shutdown();

        /// <summary>
        /// Clean up player-specific data when they disconnect.
        /// </summary>
        /// <param name="steamId">The player's Steam ID.</param>
        void CleanupPlayerData(CSteamID steamId);
    }
}
