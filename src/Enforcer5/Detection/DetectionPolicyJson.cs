using System.Text.Json;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The JSON form of a <see cref="DetectionPolicy"/>, used for the Redis cache
    /// (<c>detect:policy:{chat}</c>). A cache entry that does not read back is ignored.
    /// </summary>
    internal static class DetectionPolicyJson
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions();

        internal static string Serialize(DetectionPolicy policy) => JsonSerializer.Serialize(policy, Options);

        /// <summary>The policy, or null when <paramref name="json"/> is empty or not a policy.</summary>
        internal static DetectionPolicy Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var policy = JsonSerializer.Deserialize<DetectionPolicy>(json, Options);
                return policy?.Flood?.Counted == null || policy.Media?.Blocked == null || policy.Scripts == null ||
                       policy.TextLength == null || policy.NameLength == null || policy.Warns == null ||
                       policy.InlineBots == null || policy.StickerSets == null || policy.ChannelPosts?.Allowed == null ||
                       policy.Engine?.Signs == null
                    ? null
                    : policy;
            }
            catch (JsonException)
            {
                // A cache entry in an older shape: read from PostgreSQL instead.
                return null;
            }
        }
    }
}
