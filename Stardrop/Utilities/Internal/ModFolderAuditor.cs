using Stardrop.Models.Data;
using Stardrop.Models.SMAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Stardrop.Utilities.Internal
{
    /// <summary>
    /// Looks for install mistakes on disk that SMAPI either skips quietly or never sees at all, as Stardrop only links
    /// folders holding a manifest.json into the selected mods folder.
    /// </summary>
    internal static class ModFolderAuditor
    {
        // Folders that ship with the game or SMAPI and are never mods
        private static readonly HashSet<string> _gameFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mods", "Content", "smapi-internal", "save-backups", "downloads", "runtimes" };

        // Archive tool leftovers that hold no mod content
        private static readonly HashSet<string> _junkFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__MACOSX", ".DS_Store" };

        public record InstalledManifest(Manifest Manifest, string FolderPath);

        public class AuditResult
        {
            public List<InstalledManifest> Manifests { get; } = new List<InstalledManifest>();
            public List<HealthIssue> Issues { get; } = new List<HealthIssue>();
        }

        public static AuditResult Audit(string? modsPath, string? gamePath)
        {
            var result = new AuditResult();

            if (String.IsNullOrEmpty(modsPath) is false && Directory.Exists(modsPath))
            {
                foreach (var directory in SafeEnumerate(new DirectoryInfo(modsPath)))
                {
                    WalkModFolder(directory, result);
                }

                CheckDuplicateIds(result);
                CheckDependencies(result);
            }

            if (String.IsNullOrEmpty(gamePath) is false && Directory.Exists(gamePath))
            {
                CheckGameRoot(new DirectoryInfo(gamePath), modsPath, result);
            }

            return result;
        }

        /// <summary>Returns whether the folder (or anything below it) holds a mod.</summary>
        private static bool WalkModFolder(DirectoryInfo directory, AuditResult result)
        {
            // SMAPI ignores dotted folders, which is how mods are commonly disabled by hand
            if (directory.Name.StartsWith('.'))
            {
                return false;
            }

            if (_junkFolders.Contains(directory.Name))
            {
                result.Issues.Add(DiskIssue(HealthSeverity.Warning, "junk-folder", directory.FullName, directory.Name, "Leftover folder from an archive tool (usually a zip made on macOS). It holds no mod and can be deleted."));
                return false;
            }

            var manifestFile = directory.GetFiles("manifest.json").FirstOrDefault();
            if (manifestFile is not null)
            {
                ReadManifest(manifestFile, result);
                return true;
            }

            // Children report into a scratch result, as their issues only stand on their own when this folder turns
            // out to be a container of mods. Otherwise the whole folder is reported once below instead
            var childResult = new AuditResult();
            var holdsMod = false;
            foreach (var child in SafeEnumerate(directory))
            {
                holdsMod |= WalkModFolder(child, childResult);
            }

            result.Manifests.AddRange(childResult.Manifests);
            result.Issues.AddRange(holdsMod ? childResult.Issues : childResult.Issues.Where(i => i.Category == "junk-folder"));

            if (holdsMod)
            {
                return true;
            }

            // Nothing below this folder is a mod, so it is reported once here rather than for each subfolder
            var hasFiles = HasAnyFile(directory);
            result.Issues.Add(hasFiles
                ? DiskIssue(HealthSeverity.Error, "missing-manifest", directory.FullName, directory.Name, "Folder has files but no manifest.json, so SMAPI cannot load it. The mod is likely incomplete or was extracted one level too deep or too shallow.")
                : DiskIssue(HealthSeverity.Warning, "empty-folder", directory.FullName, directory.Name, "Empty folder (or only empty subfolders). It can be deleted."));

            return false;
        }

        private static void ReadManifest(FileInfo manifestFile, AuditResult result)
        {
            var folder = manifestFile.DirectoryName!;
            Manifest? manifest = null;
            try
            {
                manifest = ManifestParser.GetData(File.ReadAllText(manifestFile.FullName));
            }
            catch (Exception ex)
            {
                result.Issues.Add(DiskIssue(HealthSeverity.Error, "invalid-manifest", folder, Path.GetFileName(folder), $"manifest.json could not be read: {ex.Message}"));
                return;
            }

            if (manifest is null || String.IsNullOrWhiteSpace(manifest.UniqueID))
            {
                result.Issues.Add(DiskIssue(HealthSeverity.Error, "invalid-manifest", folder, Path.GetFileName(folder), "manifest.json has no UniqueID, so SMAPI will not load it."));
                return;
            }

            if (manifest.ContentPackFor is null && String.IsNullOrWhiteSpace(manifest.EntryDll))
            {
                result.Issues.Add(DiskIssue(HealthSeverity.Error, "invalid-manifest", folder, manifest.Name, "manifest.json sets neither EntryDll nor ContentPackFor.", manifest.UniqueID));
            }
            else if (String.IsNullOrWhiteSpace(manifest.EntryDll) is false && File.Exists(Path.Combine(folder, manifest.EntryDll)) is false)
            {
                result.Issues.Add(DiskIssue(HealthSeverity.Error, "missing-dll", folder, manifest.Name, $"manifest.json points to {manifest.EntryDll}, which is not in the folder.", manifest.UniqueID));
            }

            result.Manifests.Add(new InstalledManifest(manifest, folder));
        }

        private static void CheckDuplicateIds(AuditResult result)
        {
            foreach (var group in result.Manifests.GroupBy(m => m.Manifest.UniqueID, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var first = group.First();
                result.Issues.Add(new HealthIssue()
                {
                    Severity = HealthSeverity.Error,
                    Category = "duplicate-id",
                    Source = "disk",
                    ModName = first.Manifest.Name,
                    UniqueId = first.Manifest.UniqueID,
                    Path = first.FolderPath,
                    Message = $"Installed {group.Count()} times. SMAPI skips every copy of a duplicated ID: {String.Join(" | ", group.Select(m => m.FolderPath))}"
                });
            }
        }

        private static void CheckDependencies(AuditResult result)
        {
            var installed = new HashSet<string>(result.Manifests.Select(m => m.Manifest.UniqueID), StringComparer.OrdinalIgnoreCase);
            foreach (var (manifest, folder) in result.Manifests)
            {
                var required = new List<string>();
                if (manifest.ContentPackFor is not null && String.IsNullOrWhiteSpace(manifest.ContentPackFor.UniqueID) is false)
                {
                    required.Add(manifest.ContentPackFor.UniqueID);
                }
                if (manifest.Dependencies is not null)
                {
                    required.AddRange(manifest.Dependencies.Where(d => d is not null && d.IsRequired && String.IsNullOrWhiteSpace(d.UniqueID) is false).Select(d => d.UniqueID));
                }

                // SMAPI's own bundled mods aren't always in the mod folder being audited
                foreach (var missing in required.Where(id => installed.Contains(id) is false && id.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase) is false).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    result.Issues.Add(DiskIssue(HealthSeverity.Error, "missing-dependency", folder, manifest.Name, $"Requires {missing}, which is not installed.", manifest.UniqueID));
                }
            }
        }

        /// <summary>Catches mods extracted into the game folder instead of the mod folder.</summary>
        private static void CheckGameRoot(DirectoryInfo gameDirectory, string? modsPath, AuditResult result)
        {
            foreach (var directory in SafeEnumerate(gameDirectory))
            {
                if (_gameFolders.Contains(directory.Name) || (modsPath is not null && Pathing.IsSameOrUnder(directory.FullName, modsPath)))
                {
                    continue;
                }

                var manifestFile = directory.GetFiles("manifest.json").FirstOrDefault()
                    ?? SafeEnumerate(directory).Select(d => d.GetFiles("manifest.json").FirstOrDefault()).FirstOrDefault(f => f is not null);
                if (manifestFile is null)
                {
                    continue;
                }

                Manifest? manifest = null;
                try
                {
                    manifest = ManifestParser.GetData(File.ReadAllText(manifestFile.FullName));
                }
                catch
                {
                    // Reported as misplaced either way
                }

                result.Issues.Add(DiskIssue(HealthSeverity.Error, "outside-mods-folder", manifestFile.DirectoryName!, manifest?.Name ?? directory.Name, "A mod sits in the game folder instead of the Mods folder, so it is never loaded. Move it into Mods.", manifest?.UniqueID));
            }
        }

        private static bool HasAnyFile(DirectoryInfo directory)
        {
            try
            {
                return directory.EnumerateFiles("*", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return true;
            }
        }

        private static IEnumerable<DirectoryInfo> SafeEnumerate(DirectoryInfo directory)
        {
            try
            {
                return directory.GetDirectories();
            }
            catch (Exception ex)
            {
                Program.helper?.Log($"Health report could not read {directory.FullName}: {ex.Message}", Helper.Status.Warning);
                return Array.Empty<DirectoryInfo>();
            }
        }

        private static HealthIssue DiskIssue(HealthSeverity severity, string category, string path, string? modName, string message, string? uniqueId = null)
        {
            return new HealthIssue()
            {
                Severity = severity,
                Category = category,
                Source = "disk",
                ModName = modName,
                UniqueId = uniqueId,
                Path = path,
                Message = message
            };
        }
    }
}
