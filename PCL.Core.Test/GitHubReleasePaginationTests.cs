using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;

namespace PCL.Core.Test;

[TestClass]
public class GitHubReleasePaginationTests
{
    [TestMethod]
    public void ReadsTheNextLinkInsteadOfGuessingThePageNumber()
    {
        const string next = "https://api.github.com/repositories/42/releases?per_page=100&page=7";
        Assert.AreEqual(next, GitHubReleasePagination.Next(
            [$"<https://api.github.com/repositories/42/releases?page=1>; rel=\"prev\", <{next}>; rel=\"next\""]));
        Assert.IsNull(GitHubReleasePagination.Next([$"<{next}>; rel=\"prev\""]));
    }

    [TestMethod]
    public void RejectsUnexpectedPaginationDestinations()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            GitHubReleasePagination.Next(["<https://example.com/releases?page=2>; rel=\"next\""]));
    }
}
