using System;
using System.Collections.Generic;
using System.IO;

namespace PCL.Core.Minecraft.Packs;

public enum BundledLauncher { None, Lite, Light }

public sealed record PackExportOptions
{
    public BundledLauncher Launcher { get; init; }
    public string LightArchitecture { get; init; } = "x64";
    public bool IncludeVersionJava { get; init; }
    public bool VersionArgumentIndie { get; init; } = true;
    public bool IgnoredModpackMode { get; init; }

    public static PackExportOptions Parse(IReadOnlyDictionary<string, string> fields)
    {
        string Value(string key, string fallback = "") => fields.TryGetValue(key, out var value) ? value.Trim() : fallback;
        bool Flag(string key, bool fallback) => Value(key) switch
        {
            "1" => true, "0" => false,
            var text when bool.TryParse(text, out var result) => result,
            "" => fallback,
            _ => throw new InvalidDataException($"Invalid export option: {key}"),
        };
        var launcher = Flag("IncludeLauncher", false) ? BundledLauncher.Lite : BundledLauncher.None;
        if (Value("LauncherType") is { Length: > 0 } type)
        {
            if (!Enum.TryParse(type, true, out launcher) || !Enum.IsDefined(launcher))
                throw new InvalidDataException("Invalid launcher type.");
        }
        var architecture = Value("LightArchitecture", "x64");
        if (!architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) && !architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported PCL Light architecture.");
        return new PackExportOptions
        {
            Launcher = launcher,
            LightArchitecture = architecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase) ? "ARM64" : "x64",
            IncludeVersionJava = Flag("IncludeVersionJava", Flag("IncludeJava", false)),
            VersionArgumentIndie = Flag("VersionArgumentIndie", Flag("VersionIsolation", true)),
            IgnoredModpackMode = Flag("ModpackMode", false) || Flag("EnableModpackMode", false) || Flag("LivingModpackMode", false),
        };
    }
}
