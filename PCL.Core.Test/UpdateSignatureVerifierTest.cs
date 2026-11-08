using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;
using Sigstore.Verification;

namespace PCL.Core.Test;

[TestClass]
public class UpdateSignatureVerifierTest
{
    [TestMethod]
    public void PolicyPinsIssuerAndMainBranchSubject()
    {
        var policy = UpdateSignatureVerifier.CreatePolicy();

        Assert.AreEqual(UpdateSignatureVerifier.OidcIssuer, policy.ExpectedOidcIssuer);
        Assert.IsInstanceOfType<CertificateIdentityMatcher.ExactMatch>(policy.CertificateIdentityMatcher);
        Assert.AreEqual(UpdateSignatureVerifier.ExpectedSignerSubject,
            ((CertificateIdentityMatcher.ExactMatch)policy.CertificateIdentityMatcher).Expected);
    }

    [TestMethod]
    public async Task MissingBundleFailsClosed()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "pcl-signature-test-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tempPath, "artifact");
        try
        {
            var error = await UpdateSignatureVerifier.VerifyAsync(tempPath, tempPath + ".sigstore.json");

            Assert.AreEqual("发布方未提供 Sigstore 签名文件。", error);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
