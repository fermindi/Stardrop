using Stardrop.Models.Data;
using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stardrop.Utilities.Internal
{
    /// <summary>
    /// Combines the SMAPI log and the disk audit into health-latest.json, a file meant to be read by people and tools
    /// alike without opening Stardrop.
    /// </summary>
    internal static class HealthReportService
    {
        private static readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static HealthReport Build(string? gamePath, string? modsPath, string smapiLogPath)
        {
            var report = new HealthReport()
            {
                GeneratedAt = DateTime.Now,
                StardropVersion = Program.ApplicationVersion,
                GamePath = gamePath,
                ModsPath = modsPath,
                SmapiLogPath = smapiLogPath
            };

            var audit = ModFolderAuditor.Audit(modsPath, gamePath);
            report.InstalledModCount = audit.Manifests.Count;
            report.Issues.AddRange(audit.Issues);

            if (File.Exists(smapiLogPath))
            {
                report.SmapiLogLastWritten = File.GetLastWriteTime(smapiLogPath);
                report.SmapiLog = SmapiLogParser.Parse(smapiLogPath);
                report.SmapiLogIsStale = GetLatestModFolderChange(modsPath) > report.SmapiLogLastWritten;
            }
            else
            {
                report.Issues.Add(new HealthIssue()
                {
                    Severity = HealthSeverity.Info,
                    Category = "no-smapi-log",
                    Source = "smapi-log",
                    Path = smapiLogPath,
                    Message = "SMAPI-latest.txt was not found. Launch the game through SMAPI once to include log findings."
                });
            }

            if (report.SmapiLogIsStale)
            {
                report.Issues.Add(new HealthIssue()
                {
                    Severity = HealthSeverity.Info,
                    Category = "stale-smapi-log",
                    Source = "smapi-log",
                    Path = smapiLogPath,
                    Message = $"The Mods folder changed after the SMAPI log was written ({report.SmapiLogLastWritten:yyyy-MM-dd HH:mm}). Findings from the log may already be fixed; launch the game once to refresh them."
                });
            }

            if (report.SmapiLog is not null)
            {
                // The log only knows mods by display name, so the unique ID is filled in from the manifests on disk
                var idsByName = audit.Manifests
                    .Where(m => String.IsNullOrEmpty(m.Manifest.Name) is false)
                    .GroupBy(m => m.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Manifest.UniqueID, StringComparer.OrdinalIgnoreCase);

                foreach (var issue in report.SmapiLog.Issues.Where(i => i.UniqueId is null && i.ModName is not null))
                {
                    if (idsByName.TryGetValue(issue.ModName!, out var uniqueId))
                    {
                        issue.UniqueId = uniqueId;
                    }
                }

                report.Issues.AddRange(report.SmapiLog.Issues);

                if (String.IsNullOrEmpty(report.SmapiLog.LatestSmapiVersion) is false)
                {
                    report.Issues.Add(new HealthIssue()
                    {
                        Severity = HealthSeverity.Warning,
                        Category = "smapi-outdated",
                        Source = "smapi-log",
                        ModName = "SMAPI",
                        Message = $"SMAPI {report.SmapiLog.SmapiVersion} is installed and {report.SmapiLog.LatestSmapiVersion} is available."
                    });
                }

                if (report.SmapiLog.Updates.Count > 0)
                {
                    report.Issues.Add(new HealthIssue()
                    {
                        Severity = HealthSeverity.Info,
                        Category = "mod-updates",
                        Source = "smapi-log",
                        Message = $"{report.SmapiLog.Updates.Count} mods have updates available (see smapiLog.updates)."
                    });
                }
            }

            report.Issues = report.Issues
                .OrderBy(i => i.Severity)
                .ThenBy(i => i.Category, StringComparer.Ordinal)
                .ThenBy(i => i.ModName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            report.ErrorCount = report.Issues.Count(i => i.Severity == HealthSeverity.Error);
            report.WarningCount = report.Issues.Count(i => i.Severity == HealthSeverity.Warning);
            report.InfoCount = report.Issues.Count(i => i.Severity == HealthSeverity.Info);

            return report;
        }

        /// <summary>
        /// The newest change to the mod folder or its direct children. Adding, removing or renaming a mod touches one of
        /// those, so there is no need to walk every file.
        /// </summary>
        private static DateTime? GetLatestModFolderChange(string? modsPath)
        {
            if (String.IsNullOrEmpty(modsPath) || Directory.Exists(modsPath) is false)
            {
                return null;
            }

            var modsDirectory = new DirectoryInfo(modsPath);
            var latest = modsDirectory.LastWriteTime;
            foreach (var child in modsDirectory.EnumerateDirectories())
            {
                if (child.LastWriteTime > latest)
                {
                    latest = child.LastWriteTime;
                }
            }

            return latest;
        }

        public static string Write(HealthReport report, string? outputPath = null)
        {
            outputPath ??= Pathing.GetHealthReportPath();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Written beside the target first, so a reader never sees half a report
            var tempPath = outputPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(report, _jsonOptions));
            File.Move(tempPath, outputPath, overwrite: true);

            return outputPath;
        }

        /// <summary>
        /// Builds and writes the report off the UI thread. Failures are logged rather than surfaced, as the report is a
        /// side product of discovery and must never get in the way of it.
        /// </summary>
        public static void QueueGenerate()
        {
            var gamePath = Pathing.defaultGamePath;
            var modsPath = Pathing.defaultModPath;
            var logPath = Pathing.GetSmapiLatestLogPath();

            Task.Run(async () =>
            {
                await _writeLock.WaitAsync();
                try
                {
                    var report = Build(gamePath, modsPath, logPath);
                    var path = Write(report);
                    Program.helper.Log($"Health report written to {path}: {report.ErrorCount} errors, {report.WarningCount} warnings, {report.InfoCount} notes");
                }
                catch (Exception ex)
                {
                    Program.helper.Log($"Failed to generate the health report: {ex}", Helper.Status.Alert);
                }
                finally
                {
                    _writeLock.Release();
                }
            });
        }
    }
}
