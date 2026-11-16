using System;
using System.Threading;

namespace PCL.Core.UI;

/// <summary>
/// Workers publish progress without posting UI callbacks. The UI consumes at its
/// own refresh rate, retaining at most one message regardless of worker count.
/// </summary>
public sealed class LatestStatusProgress : IProgress<string>
{
    private string? _message;

    public void Report(string value) => Interlocked.Exchange(ref _message, value);

    public string? TakeLatest() => Interlocked.Exchange(ref _message, null);
}
