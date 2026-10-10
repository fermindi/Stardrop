using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Stardrop.Models.Data
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HealthSeverity { Error, Warning, Info }

    public class HealthIssue
    {
        public HealthSeverity Severity { get; set; }

        /// <summary>Short machine-friendly label, such as <c>skipped-mod</c> or <c>missing-manifest</c>.</summary>
        public string Category { get; set; }

        /// <summary>Where the issue was found: <c>smapi-log</c> or <c>disk</c>.</summary>
        public string Source { get; set; }

        public string? ModName { get; set; }
        public string? UniqueId { get; set; }
        public string? Path { get; set; }
        public string Message { get; set; }

        /// <summary>How many times an identical log entry was seen, as some mods repeat the same warning every tick.</summary>
        public int Count { get; set; } = 1;
    }

    public class HealthUpdate
    {
        public string ModName { get; set; }
        public string InstalledVersion { get; set; }
        public string LatestVersion { get; set; }
        public string Url { get; set; }
    }

    public class SmapiLogSummary
    {
        public string? SmapiVersion { get; set; }
        public string? GameVersion { get; set; }
        public string? System { get; set; }
        public DateTime? LogStartedUtc { get; set; }
        public string? LatestSmapiVersion { get; set; }
        public int? LoadedModCount { get; set; }
        public int? LoadedContentPackCount { get; set; }
        public List<string> SaveSerializerMods { get; set; } = new List<string>();
        public List<string> GamePatcherMods { get; set; } = new List<string>();
        public List<HealthUpdate> Updates { get; set; } = new List<HealthUpdate>();

        /// <summary>Folded into <see cref="HealthReport.Issues"/>, so left out of the file to avoid listing them twice.</summary>
        [JsonIgnore]
        public List<HealthIssue> Issues { get; set; } = new List<HealthIssue>();
    }

    public class HealthReport
    {
        public DateTime GeneratedAt { get; set; }
        public string StardropVersion { get; set; }
        public string? GamePath { get; set; }
        public string? ModsPath { get; set; }
        public string? SmapiLogPath { get; set; }

        /// <summary>When SMAPI last wrote its log, so a reader can tell whether the log predates changes on disk.</summary>
        public DateTime? SmapiLogLastWritten { get; set; }

        /// <summary>Whether the mod folder changed after SMAPI last wrote its log, meaning log findings may already be fixed.</summary>
        public bool SmapiLogIsStale { get; set; }

        public SmapiLogSummary? SmapiLog { get; set; }
        public int InstalledModCount { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int InfoCount { get; set; }

        /// <summary>Every issue from both the log and the disk audit, most severe first.</summary>
        public List<HealthIssue> Issues { get; set; } = new List<HealthIssue>();
    }
}
