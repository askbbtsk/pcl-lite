using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Sigstore.Bundle;
using Sigstore.Crypto;
using Sigstore.Fulcio;
using Sigstore.Rekor;
using Sigstore.Time;
using Sigstore.Tuf;
using Sigstore.Verification;

namespace PCL.Core.App.Updates;

/// <summary>
/// Verifies launcher update artifacts against the project's pinned GitHub Actions identity.
/// </summary>
public static class UpdateSignatureVerifier
{
    public const string OidcIssuer = "https://token.actions.githubusercontent.com";
    public const string ExpectedSignerSubject = "repo:askbbtsk/pcl-lite:ref:refs/heads/main";
    public const string ExpectedWorkflowIdentity =
        "https://github.com/askbbtsk/pcl-lite/.github/workflows/release-publish.yml@refs/heads/main";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Lazy<Verifier> _Verifier = new(_CreateVerifier, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns null only if the sidecar bundle is valid for the exact pinned release workflow identity.
    /// </summary>
    public static async Task<string?> VerifyAsync(string artifactPath, string bundlePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(artifactPath)) return "更新文件不存在。";
        if (!File.Exists(bundlePath)) return "发布方未提供 Sigstore 签名文件。";

        try
        {
            var bundle = await File.ReadAllTextAsync(bundlePath, cancellationToken).ConfigureAwait(false);
            var artifact = await File.ReadAllBytesAsync(artifactPath, cancellationToken).ConfigureAwait(false);
            var result = await _Verifier.Value.VerifyAsync(bundle, artifact, CreatePolicy(), cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
                return $"签名校验未通过。签名身份：{result.SignerIdentity.Subject}";
            if (!string.Equals(result.SignerIdentity.SubjectAlternativeNameUri,
                    ExpectedWorkflowIdentity, StringComparison.Ordinal))
                return $"签名证书身份不匹配：{result.SignerIdentity.SubjectAlternativeNameUri ?? "未知"}";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "签名文件无效、内容与签名不一致，或 Sigstore 验证服务暂不可用。";
        }
    }

    internal static VerificationPolicy CreatePolicy() =>
        VerificationPolicy.ForExact(OidcIssuer, ExpectedSignerSubject);

    private static Verifier _CreateVerifier()
    {
        var pipeline = new VerificationPipeline(
            new BundleParser(),
            new CertificateVerifier(),
            new TransparencyLogVerifier(),
            new SignatureVerifier(),
            new DefaultSystemClock(),
            NullLogger<VerificationPipeline>.Instance);

        return new Verifier(
            pipeline,
            new TufClient(HttpClient, NullLogger<TufClient>.Instance),
            NullLogger<Verifier>.Instance);
    }
}
