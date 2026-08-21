// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MARS.Cli;

/// <summary>
/// Small hand-rolled option parser. MARS deliberately has no command-line package
/// dependency: the assembly is meant to drop into a managed ProteoWizard tree, and every
/// package it drags along is one more thing that has to be vetted there.
/// </summary>
public sealed class CommandLineArgs
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();
    // Every name any command has asked about, whether it was supplied or not. This is the
    // set of options the running command understands, and it maintains itself: a new option
    // is recognized by the act of reading it, so there is no second list to keep in sync.
    private readonly HashSet<string> _queried = new(StringComparer.OrdinalIgnoreCase);

    private CommandLineArgs()
    {
    }

    public string Command { get; private set; } = string.Empty;

    public IReadOnlyList<string> Positional => _positional;

    public static CommandLineArgs Parse(string[] args)
    {
        var parsed = new CommandLineArgs();
        int start = 0;

        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            parsed.Command = args[0];
            start = 1;
        }

        for (int i = start; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) && !arg.StartsWith("-", StringComparison.Ordinal))
            {
                parsed._positional.Add(arg);
                continue;
            }

            string name = arg.TrimStart('-');
            string? value = null;

            int equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (!parsed._options.TryGetValue(name, out List<string>? values))
            {
                values = new List<string>();
                parsed._options[name] = values;
            }

            values.Add(value ?? "true");
        }

        return parsed;
    }

    public bool Has(params string[] names)
    {
        Query(names);
        foreach (string name in names)
        {
            if (_options.ContainsKey(name)) return true;
        }

        return false;
    }

    public bool Flag(params string[] names)
    {
        Query(names);
        foreach (string name in names)
        {
            if (_options.TryGetValue(name, out List<string>? values))
            {
                return values.Count == 0 ||
                       !string.Equals(values[^1], "false", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public string? String(params string[] names)
    {
        Query(names);
        foreach (string name in names)
        {
            if (_options.TryGetValue(name, out List<string>? values) && values.Count > 0)
                return values[^1];
        }

        return null;
    }

    public IReadOnlyList<string> Strings(params string[] names)
    {
        Query(names);
        var all = new List<string>();
        foreach (string name in names)
        {
            if (_options.TryGetValue(name, out List<string>? values)) all.AddRange(values);
        }

        return all;
    }

    public double? Double(params string[] names)
    {
        string? text = String(names);
        if (text is null) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new FormatException($"Option --{names[0]} expects a number, got '{text}'.");
        return value;
    }

    public int? Int(params string[] names)
    {
        string? text = String(names);
        if (text is null) return null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new FormatException($"Option --{names[0]} expects an integer, got '{text}'.");
        return value;
    }

    private void Query(string[] names)
    {
        foreach (string name in names) _queried.Add(name);
    }

    /// <summary>Options the command does not understand; almost always a typo.</summary>
    public IReadOnlyList<string> UnknownOptions() =>
        _options.Keys.Where(k => !_queried.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Fails the run if any supplied option is one this command does not understand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once each command has read its options and before it starts work, so a typo
    /// costs a second rather than the length of a run. It has to be a refusal rather than a
    /// warning: a mistyped --tolerance-ppm does not stop MARS, it silently calibrates against
    /// the 0.3 Th default, and on a high-resolution instrument that is a wide enough window
    /// to admit mostly wrong matches. The run finishes, writes corrected files and reports
    /// plausible numbers, all of them meaningless.
    /// </para>
    /// <para>
    /// A misplaced call would be worse than none - an option read after it has not been
    /// queried yet and would be rejected while valid - so
    /// <c>EveryDocumentedOptionSurvivesTheUnknownOptionCheck</c> passes each command its full
    /// documented option set and asserts nothing is rejected.
    /// </para>
    /// </remarks>
    public void RejectUnknown()
    {
        IReadOnlyList<string> unknown = UnknownOptions();
        if (unknown.Count == 0) return;

        var message = new System.Text.StringBuilder();
        foreach (string name in unknown)
        {
            if (message.Length > 0) message.Append("; ");
            message.Append($"Unknown option --{name}");
            if (Closest(name) is string suggestion) message.Append($". Did you mean --{suggestion}?");
        }

        message.Append($" (mars {Command} --help lists the options)");
        throw new UnknownOptionException(message.ToString());
    }

    /// <summary>The nearest option this command does understand, if one is near enough.</summary>
    private string? Closest(string name)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string candidate in _queried)
        {
            int distance = Distance(name, candidate);
            if (distance < bestDistance) (best, bestDistance) = (candidate, distance);
        }

        // A third of the length, so short options do not suggest each other: --threads and
        // --report are five edits apart and neither is a plausible typo for the other.
        return bestDistance <= Math.Max(1, name.Length / 3) ? best : null;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Expands file arguments and glob patterns into a sorted, de-duplicated list. Shells
    /// that do not expand wildcards (cmd.exe) and shells that do both end up here.
    /// </summary>
    public static List<string> ResolveMzMLFiles(IEnumerable<string> patterns, string? directory)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pattern in patterns)
        {
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                string folder = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : ".";
                string mask = Path.GetFileName(pattern);
                if (Directory.Exists(folder))
                {
                    foreach (string match in Directory.EnumerateFiles(folder, mask))
                    {
                        if (MARS.Pwiz.SpectrumSources.IsReadable(match))
                            files.Add(Path.GetFullPath(match));
                    }
                }
            }
            else if (File.Exists(pattern))
            {
                files.Add(Path.GetFullPath(pattern));
            }
            else
            {
                throw new FileNotFoundException($"File not found: {pattern}");
            }
        }

        if (!string.IsNullOrEmpty(directory))
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Directory not found: {directory}");
            // Every format MARS can read, not just mzML - a directory of .raw is as
            // legitimate an input as a directory of converted files.
            foreach (string match in Directory.EnumerateFiles(directory))
            {
                if (MARS.Pwiz.SpectrumSources.IsReadable(match))
                    files.Add(Path.GetFullPath(match));
            }
        }

        var sorted = files.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }
}
