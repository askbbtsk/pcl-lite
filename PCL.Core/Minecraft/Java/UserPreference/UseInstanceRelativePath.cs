namespace PCL.Core.Minecraft.Java.UserPreference;

/// <summary>A portable Java path relative to the instance directory, not the launcher directory.</summary>
public record UseInstanceRelativePath(string RelativePath) : JavaPreference;
