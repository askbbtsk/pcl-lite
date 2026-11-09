using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Minecraft.PrismImport;

/// <summary>Applies supported additive patch operations to an actually installed target, preserving declared order.</summary>
public static class PrismPatchComposer
{
    public static JsonObject Apply(JsonObject installed, PrismReadResult source)
    {
        if (!source.CanImport) throw new InvalidDataException(string.Join(Environment.NewLine, source.BlockingIssues));
        var target = (JsonObject)installed.DeepClone();
        var active = source.Components.Where(component => !component.Disabled).ToArray();
        foreach (var patch in source.Patches.Where(patch => active.Any(component => component.Uid == patch.Uid))
                     .OrderBy(patch => Array.FindIndex(active, component => component.Uid == patch.Uid)))
        {
            var original = patch.Original;
            if (original["libraries"] is not null) throw new InvalidDataException("Replacing a component library list is not mapped.");
            if (original["requires"] is JsonArray requirements)
                foreach (var required in requirements.OfType<JsonObject>())
                {
                    var uid = required["uid"]?.ToString(); var exact = required["equals"]?.ToString();
                    if (uid is null || !active.Any(component => component.Uid == uid && (exact is null || component.Version == exact)))
                        throw new InvalidDataException("Unsatisfied component requirement: " + uid);
                    if (required.Any(pair => pair.Key is not ("uid" or "equals" or "suggests")))
                        throw new InvalidDataException("Unknown component requirement operator: " + uid);
                }
            if (original["conflicts"] is JsonArray conflicts)
                foreach (var conflict in conflicts.OfType<JsonObject>())
                {
                    var uid = conflict["uid"]?.ToString(); var exact = conflict["equals"]?.ToString();
                    if (uid is null) throw new InvalidDataException("Unknown component conflict.");
                    if (active.Any(component => component.Uid == uid && (exact is null || component.Version == exact)))
                        throw new InvalidDataException("Active component conflict: " + uid);
                }
            if (original["assetIndex"] is JsonObject assetIndex && assetIndex["id"] is { } assetId
                && !InstanceMigration.InstanceMigrationPlanner.ValidFileName(assetId.ToString()))
                throw new InvalidDataException("Unsafe patch asset index identifier.");
            foreach (var property in new[] { "mainClass", "assetIndex" })
                if (original[property] is { } value) target[property] = value.DeepClone();
            if (original["+libraries"] is JsonArray libraries)
            {
                if (target["libraries"] is not JsonArray targetLibraries) target["libraries"] = targetLibraries = [];
                foreach (var raw in libraries)
                {
                    if (raw is not JsonObject library) throw new InvalidDataException("Invalid added library.");
                    ValidateLibrary(library);
                    var copy = (JsonObject)library.DeepClone();
                    if (copy["MMC-hint"] is { } hint) { copy["hint"] = hint.DeepClone(); copy.Remove("MMC-hint"); }
                    targetLibraries.Add(copy);
                }
            }
            AppendArguments(target, "jvm", original["+jvmArgs"]);
            AppendArguments(target, "game", original["+minecraftArguments"]);
            if (original["+tweakers"] is JsonArray tweakers)
                foreach (var tweak in tweakers)
                {
                    AppendArguments(target, "game", new JsonArray("--tweakClass", tweak?.ToString()
                        ?? throw new InvalidDataException("Invalid tweaker.")));
                }
            if (original["compatibleJavaMajors"] is JsonArray majors)
            {
                var requested = majors.Select(value => value?.GetValue<int>() ?? 0).Where(value => value > 0).Distinct().ToArray();
                if (requested.Length == 0) throw new InvalidDataException("Invalid compatible Java majors.");
                var previous = target["compatibleJavaMajors"] as JsonArray;
                if (previous is not null) requested = requested.Intersect(previous.Select(value => value!.GetValue<int>())).ToArray();
                if (requested.Length == 0) throw new InvalidDataException("Component Java compatibility sets have no intersection.");
                target["compatibleJavaMajors"] = new JsonArray(requested.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            }
        }
        return target;
    }

    private static void AppendArguments(JsonObject target, string kind, JsonNode? additions)
    {
        if (additions is null) return;
        if (additions is not JsonArray values) throw new InvalidDataException("Patch arguments must be arrays.");
        // Legacy argument strings cannot be tokenized losslessly by a new importer.
        if (target["minecraftArguments"] is not null && kind == "game")
            throw new InvalidDataException("Adding game arguments to a legacy command string requires a dedicated adapter.");
        if (target["arguments"] is not JsonObject arguments) target["arguments"] = arguments = new();
        if (arguments[kind] is not JsonArray array) arguments[kind] = array = [];
        foreach (var value in values) array.Add(value?.DeepClone() ?? throw new InvalidDataException("Null patch argument."));
    }

    private static void ValidateLibrary(JsonObject library)
    {
        var name = library["name"]?.ToString() ?? "";
        var coordinate = name.Split(':');
        if (coordinate.Length is < 3 or > 4 || coordinate.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
                || part.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '+' or '@')))
            || coordinate[0].Split('.').Any(part => part.Length == 0))
            throw new InvalidDataException("Unsafe or unsupported patch library coordinate: " + name);
        if (library["downloads"] is JsonObject downloads)
        {
            if (downloads["artifact"]?["path"] is { } artifactPath) PackUpdatePlanner.ValidateRelativePath(artifactPath.ToString());
            if (downloads["classifiers"] is JsonObject classifiers)
                foreach (var classifier in classifiers)
                    if (classifier.Value?["path"] is { } path) PackUpdatePlanner.ValidateRelativePath(path.ToString());
        }
    }
}
