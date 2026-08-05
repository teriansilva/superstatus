using System.Security.Cryptography;

namespace SuperStatus.Services.Providers.Heartbeat
{
    /// <summary>
    /// Epic #271 / #320 Phase 2b. The ping token is the heartbeat endpoint's only
    /// credential, so it must be unguessable. 128 bits of CSPRNG randomness as lowercase
    /// hex (URL-safe, no padding) — 32 chars.
    /// </summary>
    public static class HeartbeatToken
    {
        public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }
}
