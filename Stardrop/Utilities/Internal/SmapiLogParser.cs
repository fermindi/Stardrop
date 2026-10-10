using Stardrop.Models.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stardrop.Utilities.Internal
{
    /// <summary>
    /// Reads SMAPI-latest.txt into a summary of what went wrong. Kept free of any UI or settings access so it can be
    /// run against any log, including ones handed over from another machine.
    /// </summary>
    internal static class SmapiLogParser
    {
        // [08:34:07 ERROR SMAPI] message
        private static readonly Regex _entryPattern = new Regex(@"^\[(?<time>\d{2}:\d{2}:\d{2}) (?<level>TRACE|DEBUG|INFO|WARN|ERROR|ALERT)\s+(?<source>[^\]]+)\] ?(?<message>.*)$", RegexOptions.Compiled);
        private static readonly Regex _logStartedPattern = new Regex(@"^Log started at (?<date>\S+) UTC", RegexOptions.Compiled);
        private static readonly Regex _loadedCountPattern = new Regex(@"^Loaded (?<count>\d+) (?<kind>mods|content packs):", RegexOptions.Compiled);
        private static readonly Regex _smapiUpdatePattern = new Regex(@"^You can update SMAPI to (?<version>\S+?):", RegexOptions.Compiled);
        private static readonly Regex _modUpdatePattern = new Regex(@"^\s+(?<name>.+) (?<latest>\S+): (?<url>\S+) \(you have (?<installed>[^)]+)\)$", RegexOptions.Compiled);
        private static readonly Regex _skippedModPattern = new Regex(@"^- (?<name>.+?) because (?<reason>.+)$", RegexOptions.Compiled);
        private static readonly Regex _updateCheckErrorPattern = new Regex(@"^\s+(?<name>[^:]+): (?<reason>.+)$", RegexOptions.Compiled);

        private enum Section { None, SkippedMods, SaveSerializer, PatchedGameCode, ModUpdates, Other }

        private record Entry(string Level, string Source, string Message, List<string> Continuation);

        public static SmapiLogSummary? Parse(string logPath)
        {
            if (File.Exists(logPath) is false)
            {
                return null;
            }

            // SMAPI keeps the file open while the game runs
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            return Parse(ReadLines(reader));
        }

        private static IEnumerable<string> ReadLines(StreamReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                yield return line;
            }
        }

        public static SmapiLogSummary Parse(IEnumerable<string> lines)
        {
            var summary = new SmapiLogSummary();
            var section = Section.None;
            var issuesByKey = new Dictionary<string, HealthIssue>();

            foreach (var entry in GroupEntries(lines))
            {
                var isSmapi = entry.Source.Equals("SMAPI", StringComparison.OrdinalIgnoreCase);
                var message = entry.Message;
                var trimmed = message.Trim();

                if (isSmapi)
                {
                    // SMAPI indents everything belonging to a report section, so an unindented line closes it
                    var indent = message.Length - message.TrimStart().Length;
                    if (section != Section.None && indent < 3)
                    {
                        section = Section.None;
                    }

                    if (TryParseSectionHeader(trimmed, out var header))
                    {
                        section = header;
                        continue;
                    }

                    if (section != Section.None && section != Section.ModUpdates)
                    {
                        ParseSectionLine(summary, section, trimmed);
                        continue;
                    }

                    if (section == Section.ModUpdates && _modUpdatePattern.Match(message) is { Success: true } update)
                    {
                        summary.Updates.Add(new HealthUpdate()
                        {
                            ModName = update.Groups["name"].Value.Trim(),
                            LatestVersion = update.Groups["latest"].Value,
                            InstalledVersion = update.Groups["installed"].Value,
                            Url = update.Groups["url"].Value
                        });
                        continue;
                    }

                    if (ParseSmapiMessage(summary, entry, trimmed))
                    {
                        continue;
                    }
                }

                if (entry.Level is "ERROR" or "WARN")
                {
                    AddLogIssue(issuesByKey, entry, isSmapi);
                }
            }

            summary.Issues.AddRange(issuesByKey.Values);
            return summary;
        }

        /// <summary>
        /// Folds the lines that follow an entry without a prefix of their own (stack traces, wrapped lists) into it.
        /// </summary>
        private static IEnumerable<Entry> GroupEntries(IEnumerable<string> lines)
        {
            Entry? current = null;
            foreach (var line in lines)
            {
                var match = _entryPattern.Match(line);
                if (match.Success)
                {
                    if (current is not null)
                    {
                        yield return current;
                    }

                    current = new Entry(match.Groups["level"].Value, match.Groups["source"].Value.Trim(), match.Groups["message"].Value, new List<string>());
                }
                else if (current is not null && String.IsNullOrWhiteSpace(line) is false)
                {
                    current.Continuation.Add(line);
                }
            }

            if (current is not null)
            {
                yield return current;
            }
        }

        private static bool TryParseSectionHeader(string trimmed, out Section section)
        {
            section = trimmed switch
            {
                "Skipped mods" => Section.SkippedMods,
                "Changed save serializer" => Section.SaveSerializer,
                "Patched game code" => Section.PatchedGameCode,
                _ => Section.None
            };

            if (section == Section.None && trimmed.StartsWith("You can update ") && trimmed.EndsWith(" mods:"))
            {
                section = Section.ModUpdates;
            }

            // The remaining report sections (direct console access, bypassed safety checks, etc.) are only listed
            // so their bullet points don't get mistaken for loose warnings
            if (section == Section.None && IsOtherReportHeader(trimmed))
            {
                section = Section.Other;
            }

            return section != Section.None;
        }

        private static bool IsOtherReportHeader(string trimmed)
        {
            return trimmed is "Direct console access" or "Bypassed safety checks" or "No update keys" or "Missing dependencies"
                or "Broken code" or "Unofficial updates" or "Rewritten for compatibility" or "Using SMAPI internals";
        }

        private static void ParseSectionLine(SmapiLogSummary summary, Section section, string trimmed)
        {
            if (trimmed.StartsWith("- ") is false)
            {
                return;
            }

            switch (section)
            {
                case Section.SkippedMods:
                    var skipped = _skippedModPattern.Match(trimmed);
                    summary.Issues.Add(new HealthIssue()
                    {
                        Severity = HealthSeverity.Error,
                        Category = "skipped-mod",
                        Source = "smapi-log",
                        ModName = skipped.Success ? skipped.Groups["name"].Value : trimmed[2..],
                        Message = skipped.Success ? $"SMAPI skipped this mod because {skipped.Groups["reason"].Value}" : trimmed
                    });
                    break;
                case Section.SaveSerializer:
                    summary.SaveSerializerMods.Add(trimmed[2..]);
                    break;
                case Section.PatchedGameCode:
                    summary.GamePatcherMods.Add(trimmed[2..]);
                    break;
            }
        }

        /// <summary>Handles the one-off SMAPI messages worth keeping. Returns whether the entry was consumed.</summary>
        private static bool ParseSmapiMessage(SmapiLogSummary summary, Entry entry, string trimmed)
        {
            var details = Program.gameDetailsPattern.Match(trimmed);
            if (summary.SmapiVersion is null && details.Success)
            {
                summary.SmapiVersion = details.Groups["smapiVersion"].Value;
                summary.GameVersion = details.Groups["gameVersion"].Value;
                summary.System = details.Groups["system"].Value;
                return true;
            }

            var started = _logStartedPattern.Match(trimmed);
            if (started.Success)
            {
                if (DateTime.TryParse(started.Groups["date"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                {
                    summary.LogStartedUtc = date;
                }
                return true;
            }

            var loaded = _loadedCountPattern.Match(trimmed);
            if (loaded.Success)
            {
                var count = Int32.Parse(loaded.Groups["count"].Value);
                if (loaded.Groups["kind"].Value == "mods")
                {
                    summary.LoadedModCount = count;
                }
                else
                {
                    summary.LoadedContentPackCount = count;
                }
                return true;
            }

            var smapiUpdate = _smapiUpdatePattern.Match(trimmed);
            if (smapiUpdate.Success)
            {
                summary.LatestSmapiVersion = smapiUpdate.Groups["version"].Value;
                return true;
            }

            if (trimmed.StartsWith("Got update-check errors for some mods"))
            {
                foreach (var line in entry.Continuation)
                {
                    var error = _updateCheckErrorPattern.Match(line);
                    if (error.Success is false)
                    {
                        continue;
                    }

                    summary.Issues.Add(new HealthIssue()
                    {
                        Severity = HealthSeverity.Info,
                        Category = "update-check-failed",
                        Source = "smapi-log",
                        ModName = error.Groups["name"].Value.Trim(),
                        Message = error.Groups["reason"].Value.Trim()
                    });
                }
                return true;
            }

            return false;
        }

        private static void AddLogIssue(Dictionary<string, HealthIssue> issuesByKey, Entry entry, bool isSmapi)
        {
            var message = entry.Message.Trim();
            var hasException = entry.Continuation.Any(l => l.TrimStart().StartsWith("at ")) || message.Contains("Exception");

            var key = $"{entry.Level}|{entry.Source}|{message}";
            if (issuesByKey.TryGetValue(key, out var existing))
            {
                existing.Count++;
                return;
            }

            issuesByKey[key] = new HealthIssue()
            {
                Severity = entry.Level == "ERROR" ? HealthSeverity.Error : HealthSeverity.Warning,
                Category = hasException ? "exception" : (entry.Level == "ERROR" ? "log-error" : "log-warning"),
                Source = "smapi-log",
                ModName = isSmapi ? null : entry.Source,
                Message = hasException && entry.Continuation.Count > 0 ? $"{message}{Environment.NewLine}{entry.Continuation[0].Trim()}" : message
            };
        }
    }
}
