using System.Security.Cryptography;

namespace AgenticForge.Domain;

/// <summary>Content digests for artifacts, formatted <c>sha256:&lt;lowercase hex&gt;</c> as stored in <see cref="ArtifactRef.Digest"/>.</summary>
public static class ArtifactDigest
{
    public const string Prefix = "sha256:";

    public static string Compute(ReadOnlySpan<byte> content) => Prefix + Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Compute(Stream content) => Prefix + Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>True when the artifact has a digest and the content hashes to it. A missing digest cannot be verified, so it is false.</summary>
    public static bool Verify(ArtifactRef artifact, ReadOnlySpan<byte> content)
        => artifact.Digest is { } expected
           && CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.ASCII.GetBytes(expected.ToLowerInvariant()),
               System.Text.Encoding.ASCII.GetBytes(Compute(content)));
}
