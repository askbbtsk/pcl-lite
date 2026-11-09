using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PCL;

public static partial class ModLocalComp
{
    public partial class LocalCompFile
    {
        /// <summary>Every metadata dialect present, rather than the preferred single parsing dialect.</summary>
        public IReadOnlyList<string> DeclaredLoaders { get; private set; } = Array.Empty<string>();

        private static IReadOnlyList<string> ReadDeclaredLoaders(ZipArchive archive)
        {
            var loaders = new List<string>();
            if (archive.GetEntry("fabric.mod.json") is not null) loaders.Add("Fabric");
            if (archive.GetEntry("quilt.mod.json") is not null) loaders.Add("Quilt");
            if (archive.GetEntry("META-INF/neoforge.mods.toml") is not null) loaders.Add("NeoForge");
            if (archive.GetEntry("META-INF/mods.toml") is not null || archive.GetEntry("mcmod.info") is not null)
                loaders.Add("Forge");
            return loaders.AsReadOnly();
        }

        internal bool LoaderMetadataIsCurrent()
        {
            try
            {
                if (!isLoaded || _jijExpectedLastModified is null || _jijExpectedSize is null) return false;
                var file = new FileInfo(path);
                return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0
                    && file.LastWriteTimeUtc.Ticks == _jijExpectedLastModified && file.Length == _jijExpectedSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }
}
