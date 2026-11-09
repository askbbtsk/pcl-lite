using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.Packs;

/// <summary>Registers provenance and game mutations together instead of using an unjournaled callback.</summary>
public static class PackUpdateDurableTransaction
{
    public static string StoragePath(string gameRoot) => Path.Combine(Path.GetFullPath(gameRoot), "PCL", "Enhancements", "PackTransactions");

    public static int Recover(string gameRoot, string metadataRoot)
    {
        var storage = StoragePath(gameRoot);
        if (!Directory.Exists(storage)) return 0;
        return DurableFileTransaction.Recover(storage, Roots(gameRoot, metadataRoot));
    }

    public static void Apply(PackUpdatePlan plan, PackBaselineStore store, string instancePath,
        CancellationToken token = default, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plan.Actions);
        if (plan.Actions.Any(action => action is null || !Enum.IsDefined(action.Kind)))
            throw new InvalidDataException("Invalid pack update action.");
        if (plan.Actions.Any(action => action.Kind == PackUpdateActionKind.RemoveAuthorFile
            && (action.PayloadPath is not null || action.ExpectedSha256 is not null)))
            throw new InvalidDataException("A pack removal cannot also install a payload.");
        var metadata = store.PrepareTransaction(instancePath, plan.Target);
        var changes = new List<TransactionFileChange>();
        foreach (var action in plan.Actions.Where(action => action.Kind != PackUpdateActionKind.PreservePersonalFile))
        {
            var removed = action.Kind == PackUpdateActionKind.RemoveAuthorFile;
            if (!removed && (action.ExpectedSha256 is null || action.PayloadPath is null))
                throw new InvalidDataException("Pack update needs a verified payload: " + action.DestinationPath);
            changes.Add(new("game", action.DestinationPath, action.ExpectedCurrentSha256,
                removed ? null : action.ExpectedSha256, removed ? null : action.PayloadPath));
        }
        changes.Add(metadata);
        DurableFileTransaction.Apply(StoragePath(plan.GameRoot), Roots(plan.GameRoot, store.TransactionMetadataRoot),
            changes, token, progress);
    }

    private static IReadOnlyDictionary<string, string> Roots(string gameRoot, string metadataRoot)
        => new Dictionary<string, string> { ["game"] = gameRoot, ["metadata"] = metadataRoot };
}
