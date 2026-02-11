using System;

namespace Emqo.KookBot_Unturned.Detectors
{
    /// <summary>
    /// Result of a message detection check.
    /// </summary>
    public class DetectionResult
    {
        /// <summary>
        /// Whether a violation was detected.
        /// </summary>
        public bool IsViolation { get; set; }

        /// <summary>
        /// Reason for the violation (shown to the player).
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// Name of the detector that found the violation.
        /// </summary>
        public string DetectorName { get; set; }

        /// <summary>
        /// Whether the player should be automatically muted.
        /// </summary>
        public bool ShouldAutoMute { get; set; }

        /// <summary>
        /// Duration of the auto-mute if applicable.
        /// </summary>
        public TimeSpan? AutoMuteDuration { get; set; }

        private static readonly DetectionResult _allowed = new() { IsViolation = false };

        /// <summary>
        /// Create a result indicating no violation.
        /// </summary>
        public static DetectionResult Allowed() => _allowed;

        /// <summary>
        /// Create a result indicating a violation.
        /// </summary>
        /// <param name="detectorName">Name of the detector.</param>
        /// <param name="reason">Reason for the violation.</param>
        /// <param name="shouldAutoMute">Whether to auto-mute the player.</param>
        /// <param name="autoMuteDuration">Duration of the auto-mute.</param>
        public static DetectionResult Violation(string detectorName, string reason, bool shouldAutoMute = false, TimeSpan? autoMuteDuration = null)
        {
            return new DetectionResult
            {
                IsViolation = true,
                DetectorName = detectorName,
                Reason = reason,
                ShouldAutoMute = shouldAutoMute,
                AutoMuteDuration = autoMuteDuration
            };
        }
    }
}
