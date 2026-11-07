using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCL.Plugin.SDK;

/// <summary>Validates and evaluates the bounded SemVer ranges accepted by plugin dependencies.</summary>
public static class PluginVersionRange
{
    private const int MaximumRangeLength = 256;
    private const int MaximumConstraints = 16;

    /// <summary>Returns whether a range is a supported exact version, comparator set, caret, tilde, or wildcard.</summary>
    public static bool IsValid(string? range) => TryParseRange(range, out _);

    /// <summary>Returns whether a value is a SemVer 2.0 version string.</summary>
    public static bool IsValidVersion(string? version) => TryParseVersion(version, out _);

    /// <summary>Checks a plugin version against an exact version or bounded SemVer comparator set.</summary>
    public static bool Satisfies(string? version, string? range)
    {
        if (!TryParseVersion(version, out var candidate) || !TryParseRange(range, out var constraints)) return false;
        return constraints.All(constraint => constraint.Matches(candidate));
    }

    private static bool TryParseRange(string? range, out List<Constraint> constraints)
    {
        constraints = [];
        if (string.IsNullOrWhiteSpace(range) || range.Length > MaximumRangeLength) return false;
        range = range.Trim();
        if (range == "*") return true;

        var tokens = range.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > MaximumConstraints) return false;
        if (tokens.Length == 1 && !tokens[0].StartsWith('>') && !tokens[0].StartsWith('<') &&
            !tokens[0].StartsWith('=') && !tokens[0].StartsWith('^') && !tokens[0].StartsWith('~'))
        {
            if (!TryParseVersion(tokens[0], out var exact)) return false;
            constraints.Add(new(Comparison.Equal, exact));
            return true;
        }

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token is ">" or ">=" or "<" or "<=" or "=")
            {
                if (++index >= tokens.Length) return false;
                token += tokens[index];
            }
            if (token.StartsWith('^') || token.StartsWith('~'))
            {
                if (constraints.Count != 0 || tokens.Length != 1 ||
                    !TryParseVersion(token[1..], out var baseVersion) || !TryGetUpperBound(baseVersion,
                        token[0] == '^', out var upperBound)) return false;
                constraints.Add(new(Comparison.GreaterOrEqual, baseVersion));
                constraints.Add(new(Comparison.Less, upperBound));
                return true;
            }

            var comparison = Comparison.Equal;
            if (token.StartsWith(">=", StringComparison.Ordinal)) { comparison = Comparison.GreaterOrEqual; token = token[2..]; }
            else if (token.StartsWith("<=", StringComparison.Ordinal)) { comparison = Comparison.LessOrEqual; token = token[2..]; }
            else if (token.StartsWith('>')) { comparison = Comparison.Greater; token = token[1..]; }
            else if (token.StartsWith('<')) { comparison = Comparison.Less; token = token[1..]; }
            else if (token.StartsWith('=')) token = token[1..];

            if (!TryParseVersion(token, out var version)) return false;
            constraints.Add(new(comparison, version));
            if (constraints.Count > MaximumConstraints) return false;
        }
        return constraints.Count > 0;
    }

    private static bool TryGetUpperBound(SemanticVersion version, bool caret, out SemanticVersion upper)
    {
        upper = default;
        try
        {
            if (!caret)
                upper = new(version.Major, checked(version.Minor + 1), 0, []);
            else if (version.Major > 0)
                upper = new(checked(version.Major + 1), 0, 0, []);
            else if (version.Minor > 0)
                upper = new(0, checked(version.Minor + 1), 0, []);
            else
                upper = new(0, 0, checked(version.Patch + 1), []);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static bool TryParseVersion(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            if (value.IndexOf('+', plus + 1) >= 0 || !IsIdentifierList(value[(plus + 1)..], allowLeadingZero: true))
                return false;
            value = value[..plus];
        }

        string[] prerelease = [];
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            if (!IsIdentifierList(value[(dash + 1)..], allowLeadingZero: false)) return false;
            prerelease = value[(dash + 1)..].Split('.');
            value = value[..dash];
        }

        var numbers = value.Split('.');
        if (numbers.Length != 3 ||
            !TryParseCoreNumber(numbers[0], out var major) ||
            !TryParseCoreNumber(numbers[1], out var minor) ||
            !TryParseCoreNumber(numbers[2], out var patch)) return false;
        version = new(major, minor, patch, prerelease);
        return true;
    }

    private static bool TryParseCoreNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0 && (value.Length == 1 || value[0] != '0') &&
               int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsIdentifierList(string value, bool allowLeadingZero)
    {
        if (value.Length == 0) return false;
        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0 || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                return false;
            if (!allowLeadingZero && part.Length > 1 && part.All(char.IsAsciiDigit) && part[0] == '0') return false;
        }
        return true;
    }

    private static int Compare(SemanticVersion left, SemanticVersion right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = left.Patch.CompareTo(right.Patch);
        if (result != 0) return result;
        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
            return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
        for (var index = 0; index < Math.Min(left.Prerelease.Length, right.Prerelease.Length); index++)
        {
            var first = left.Prerelease[index];
            var second = right.Prerelease[index];
            var firstNumeric = first.All(char.IsAsciiDigit);
            var secondNumeric = second.All(char.IsAsciiDigit);
            if (firstNumeric && secondNumeric)
                result = first.Length != second.Length ? first.Length.CompareTo(second.Length) :
                    string.Compare(first, second, StringComparison.Ordinal);
            else if (firstNumeric != secondNumeric) result = firstNumeric ? -1 : 1;
            else result = string.Compare(first, second, StringComparison.Ordinal);
            if (result != 0) return result;
        }
        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string[] Prerelease);
    private enum Comparison { Equal, Greater, GreaterOrEqual, Less, LessOrEqual }

    private readonly record struct Constraint(Comparison Comparison, SemanticVersion Version)
    {
        public bool Matches(SemanticVersion candidate)
        {
            var result = Compare(candidate, Version);
            return Comparison switch
            {
                Comparison.Equal => result == 0,
                Comparison.Greater => result > 0,
                Comparison.GreaterOrEqual => result >= 0,
                Comparison.Less => result < 0,
                Comparison.LessOrEqual => result <= 0,
                _ => false
            };
        }
    }
}
