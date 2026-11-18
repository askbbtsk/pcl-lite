namespace PCL;

public class VersionDataModel
{
    public string Changelog { get; set; }
    public string Sha256 { get; set; }
    /// <summary>与更新 exe 对应的 Sigstore bundle 地址。</summary>
    public string SignatureUrl { get; set; }
    public string Source { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; }
    public bool IsComplete { get; init; } = true;
    public Func<string, List<ModLoader.LoaderBase>> CreateDownloadLoaders { get; init; }
}
