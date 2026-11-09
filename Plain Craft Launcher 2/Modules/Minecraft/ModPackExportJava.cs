using System.IO;
using PCL.Core.App;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.Minecraft.Packs;

namespace PCL;

internal static class ModPackExportJava
{
    public static InstanceJavaBundle? Find(McInstance instance)
    {
        string? preferred = ModJava.GetInstanceJavaPreference(instance) switch
        {
            ExistingJava java => java.JavaExePath,
            UseInstanceRelativePath java => PackExportContent.ResolveInstanceJava(instance.PathInstance, java.RelativePath),
            UseRelativePath java => Path.GetFullPath(Path.Combine(Basics.ExecutableDirectory, java.RelativePath)),
            _ => null,
        };
        var recommended = instance.JsonObject?["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 0;
        var compatible = instance.JsonObject?["compatibleJavaMajors"] as JsonArray;
        return InstanceJavaBundler.Find(instance.PathInstance, preferred, compatible: java =>
            compatible is { Count: > 0 } ? compatible.Any(value => value?.GetValue<int>() == java.MajorVersion)
                : recommended <= 0 || java.MajorVersion >= recommended);
    }
}
