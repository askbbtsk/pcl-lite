using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.InstanceMigration;

/// <summary>Changes exactly the PCL INI Java-preference value, preserving unrelated text, BOM and line endings.</summary>
public static class InstanceJavaPreferenceEditor
{
    public static byte[] WithJavaPreference(byte[] original, string serializedPreference)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (string.IsNullOrWhiteSpace(serializedPreference) || serializedPreference.Contains('\r') || serializedPreference.Contains('\n'))
            throw new ArgumentException("Invalid serialized Java preference.", nameof(serializedPreference));
        var encoding = (Encoding)new UTF8Encoding(false, true); var offset = 0;
        if (original.Length >= 4 && ((original[0] == 0xff && original[1] == 0xfe && original[2] == 0 && original[3] == 0)
            || (original[0] == 0 && original[1] == 0 && original[2] == 0xfe && original[3] == 0xff)))
            throw new InvalidDataException("UTF-32 INI editing is not supported.");
        if (original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3;
        else if (original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (original.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        var text = encoding.GetString(original, offset, original.Length - offset);
        const string key = "VersionArgumentJavaSelect:";
        var matches = Regex.Matches(text, @"(^|\r\n|\n|\r)VersionArgumentJavaSelect:[^\r\n]*");
        string changed;
        if (matches.Count > 0)
            changed = Regex.Replace(text, @"(^|\r\n|\n|\r)VersionArgumentJavaSelect:[^\r\n]*",
                match => match.Groups[1].Value + key + serializedPreference);
        else
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : "\r\n";
            changed = text + (text.Length == 0 || text.EndsWith('\r') || text.EndsWith('\n') ? "" : newline) + key + serializedPreference + newline;
        }
        var payload = encoding.GetBytes(changed);
        return original.Take(offset).Concat(payload).ToArray();
    }
}
