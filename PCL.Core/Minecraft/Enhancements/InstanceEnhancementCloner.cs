using System;
using System.IO;

namespace PCL.Core.Minecraft.Enhancements;

/// <summary>Explicit clones keep declarations but never inherit disposable reports or unfinished transactions.</summary>
public static class InstanceEnhancementCloner
{
    public static void CopyDeclaration(string sourceDirectory, string destinationDirectory)
    {
        var source = new InstanceEnhancementStore(sourceDirectory);
        var declaration = source.ReadDeclaration();
        if (declaration.Status == EnhancementReadStatus.Missing) return;
        var destination = new InstanceEnhancementStore(destinationDirectory);
        if (declaration.Status == EnhancementReadStatus.Valid)
        {
            destination.SaveDeclaration(declaration.Value! with { InstanceId = Guid.NewGuid() });
            return;
        }
        // Preserve unknown user-authored bytes without making a copied identity active.
        // Never deserialize or guess the shape of a newer document.
        EnhancementPaths.RejectLinks(source.DeclarationPath);
        EnhancementPaths.RejectLinks(destination.DirectoryPath);
        Directory.CreateDirectory(destination.DirectoryPath);
        var preserved = Path.Combine(destination.DirectoryPath, "unrecognized-source-declaration.bin");
        File.Copy(source.DeclarationPath, preserved, overwrite: false);
    }
}
