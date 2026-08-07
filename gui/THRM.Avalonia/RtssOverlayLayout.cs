using System.Globalization;
using System.Text;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace THRM.Avalonia;

internal sealed record RtssOverlayLayoutStatus
{
    public bool Supported { get; init; }
    public bool Installed { get; init; }
    public string InstallPath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public string LayoutPath { get; init; } = string.Empty;
    public string LayoutName { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public string AnchorState { get; init; } = "missing";
    public int AnchorIndex { get; init; } = -1;
    public int LayerCount { get; init; }
}

internal static class RtssOverlayLayout
{
    private const string AnchorName = "THRM Anchor";
    private const string AnchorConfirmed = "confirmed";
    private const string AnchorCandidate = "candidate";
    private const string AnchorNeedsLast = "needs_last";
    private const string AnchorMissing = "missing";
    private static readonly Encoding LayoutEncoding = Encoding.Latin1;

    public static RtssOverlayLayoutStatus Inspect()
    {
        var status = new RtssOverlayLayoutStatus
        {
            Supported = OperatingSystem.IsWindows(),
        };
        if (!OperatingSystem.IsWindows())
        {
            return status;
        }

        var installPath = FindInstallPath();
        status = status with
        {
            Installed = !string.IsNullOrWhiteSpace(installPath),
            InstallPath = installPath,
        };
        if (!status.Installed)
        {
            return status;
        }

        var configPath = Path.Combine(installPath, "Plugins", "Client", "OverlayEditor.cfg");
        status = status with { ConfigPath = configPath };
        string config;
        try
        {
            config = File.ReadAllText(configPath, LayoutEncoding);
        }
        catch (IOException)
        {
            return status;
        }
        catch (UnauthorizedAccessException)
        {
            return status;
        }

        var layoutName = ReadSettingsValue(config, "Layout");
        status = status with { LayoutName = layoutName };
        if (string.IsNullOrWhiteSpace(layoutName)
            || !TryResolveLayoutPath(Path.Combine(installPath, "Plugins", "Client", "Overlays"), layoutName, out var layoutPath))
        {
            return status;
        }

        status = status with { LayoutPath = layoutPath };
        try
        {
            var inspection = InspectLayout(File.ReadAllText(layoutPath, LayoutEncoding));
            return status with
            {
                AnchorState = inspection.State,
                AnchorIndex = inspection.AnchorIndex,
                LayerCount = inspection.LayerCount,
            };
        }
        catch (IOException)
        {
            return status;
        }
        catch (UnauthorizedAccessException)
        {
            return status;
        }
    }

    public static RtssOverlayLayoutStatus CreateAnchor()
    {
        var status = Inspect();
        if (!status.Supported)
        {
            throw new PlatformNotSupportedException("RTSS OverlayEditor anchoring is available on Windows only.");
        }

        if (!status.Installed || string.IsNullOrWhiteSpace(status.LayoutPath))
        {
            throw new InvalidOperationException("RTSS or its active OverlayEditor layout could not be found.");
        }

        if (status.AnchorState == AnchorConfirmed)
        {
            return status;
        }

        var layout = File.ReadAllText(status.LayoutPath, LayoutEncoding);
        var updated = ConfigureAnchor(layout);
        var backup = BackupLayout(status.LayoutPath, layout);
        try
        {
            WriteLayoutAtomically(status.LayoutPath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not update the RTSS layout. The original was backed up to {backup}.", ex);
        }

        return Inspect() with { BackupPath = backup };
    }

    public static void SelfCheck()
    {
        const string layout = "[General]\r\nLayers=1\r\n[Layer0]\r\nName=CPU\r\nText=CPU Temp\r\nPositionX=0\r\nPositionY=-4\r\n";
        var updated = ConfigureAnchor(layout);
        var result = InspectLayout(updated);
        if (result.State != AnchorConfirmed
            || result.AnchorIndex != 1
            || result.LayerCount != 2
            || !updated.Contains("Name=THRM Anchor\r\n", StringComparison.Ordinal)
            || !updated.Contains("PositionX=0\r\nPositionY=-5\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RTSS layout append self-check failed.");
        }

        const string candidate = "[General]\nLayers=2\n[Layer0]\nName=CPU\nText=CPU Temp\n[Layer1]\nName=\nText=\nSize=1\n";
        result = InspectLayout(ConfigureAnchor(candidate));
        if (result.State != AnchorConfirmed || result.AnchorIndex != 1)
        {
            throw new InvalidOperationException("RTSS layout candidate self-check failed.");
        }

        var root = Path.Combine(Path.GetTempPath(), "thrm-rtss-layout-self-check");
        if (TryResolveLayoutPath(root, ".." + Path.DirectorySeparatorChar + "outside.ovl", out _))
        {
            throw new InvalidOperationException("RTSS layout path self-check failed.");
        }
    }

    private static RtssOverlayLayoutStatusInspection InspectLayout(string data)
    {
        var inspection = ParseLayout(data);
        var layers = inspection.Layers.OrderBy(layer => layer.Index).ToList();
        var layerCount = Math.Max(inspection.DeclaredLayers, layers.Count);
        var anchorIndex = -1;
        var candidateIndex = -1;
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            if (layer.Size != 1 || !string.IsNullOrWhiteSpace(layer.Text))
            {
                continue;
            }

            if (string.Equals(layer.Name.Trim(), AnchorName, StringComparison.OrdinalIgnoreCase))
            {
                anchorIndex = index;
                break;
            }

            if (candidateIndex < 0)
            {
                candidateIndex = index;
            }
        }

        if (anchorIndex >= 0)
        {
            return new RtssOverlayLayoutStatusInspection(
                anchorIndex == layers.Count - 1 && (layerCount == 0 || anchorIndex == layerCount - 1)
                    ? AnchorConfirmed
                    : AnchorNeedsLast,
                anchorIndex,
                layerCount);
        }

        if (candidateIndex >= 0)
        {
            return new RtssOverlayLayoutStatusInspection(
                candidateIndex == layers.Count - 1 && (layerCount == 0 || candidateIndex == layerCount - 1)
                    ? AnchorCandidate
                    : AnchorNeedsLast,
                candidateIndex,
                layerCount);
        }

        return new RtssOverlayLayoutStatusInspection(AnchorMissing, -1, layerCount);
    }

    private static string ConfigureAnchor(string data)
    {
        var inspection = ParseLayout(data);
        var exact = new List<int>();
        var candidates = new List<int>();
        for (var index = 0; index < inspection.Layers.Count; index++)
        {
            var layer = inspection.Layers[index];
            if (layer.Size != 1 || !string.IsNullOrWhiteSpace(layer.Text))
            {
                continue;
            }

            candidates.Add(index);
            if (string.Equals(layer.Name.Trim(), AnchorName, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(index);
            }
        }

        if (exact.Count > 1 || (exact.Count == 0 && candidates.Count > 1))
        {
            throw new InvalidOperationException("Multiple empty one-percent RTSS layers were found, so THRM cannot safely choose an anchor.");
        }

        return exact.Count == 1
            ? MarkAndMoveAnchor(data, inspection, exact[0])
            : candidates.Count == 1
                ? MarkAndMoveAnchor(data, inspection, candidates[0])
                : AppendAnchor(data, inspection);
    }

    private static string AppendAnchor(string data, RtssLayoutInspection inspection)
    {
        if (inspection.LayersLine < 0)
        {
            throw new InvalidOperationException("The RTSS layout does not contain a valid [General] Layers setting.");
        }

        var newIndex = Math.Max(inspection.DeclaredLayers, inspection.Layers.Count == 0 ? 0 : inspection.Layers.Max(layer => layer.Index) + 1);
        var (positionX, positionY) = DefaultAnchorPosition(inspection.Layers);
        var lines = data.Split('\n').ToList();
        var raw = lines[inspection.LayersLine];
        var bare = raw.TrimEnd('\r');
        var indentationLength = bare.Length - bare.TrimStart(' ', '\t').Length;
        lines[inspection.LayersLine] = bare[..indentationLength]
            + $"Layers={newIndex + 1}"
            + (raw.EndsWith('\r') ? "\r" : string.Empty);

        var newline = data.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var anchor = new[]
        {
            $"[Layer{newIndex}]",
            $"Name={AnchorName}",
            "Text=",
            $"PositionX={positionX}",
            $"PositionY={positionY}",
            "ExtentX=0",
            "ExtentY=0",
            "ExtentOrigin=0",
            "Size=1",
        };
        return string.Join("\n", lines).TrimEnd('\r', '\n') + newline + string.Join(newline, anchor) + newline;
    }

    private static string MarkAndMoveAnchor(string data, RtssLayoutInspection inspection, int target)
    {
        if (target < 0 || target >= inspection.Layers.Count)
        {
            throw new InvalidOperationException("The RTSS anchor layer index is invalid.");
        }

        var lines = data.Split('\n').ToList();
        var sections = new List<RtssLayerSection>();
        var firstLine = int.MaxValue;
        var lastLine = 0;
        for (var index = 0; index < inspection.Layers.Count; index++)
        {
            var layer = inspection.Layers[index];
            if (layer.StartLine < 0 || layer.EndLine <= layer.StartLine || layer.EndLine > lines.Count)
            {
                throw new InvalidOperationException("The RTSS layout contains an invalid layer boundary.");
            }

            firstLine = Math.Min(firstLine, layer.StartLine);
            lastLine = Math.Max(lastLine, layer.EndLine);
            sections.Add(new RtssLayerSection
            {
                Index = layer.Index,
                IsTarget = index == target,
                Lines = lines.GetRange(layer.StartLine, layer.EndLine - layer.StartLine),
            });
        }

        for (var index = 1; index < inspection.Layers.Count; index++)
        {
            if (inspection.Layers[index - 1].EndLine != inspection.Layers[index].StartLine)
            {
                throw new InvalidOperationException("The RTSS layout has content between layers, so it cannot be safely reordered.");
            }
        }

        sections = sections.OrderBy(section => section.Index).ToList();
        if (sections.Zip(sections.Skip(1)).Any(pair => pair.First.Index == pair.Second.Index))
        {
            throw new InvalidOperationException("The RTSS layout contains duplicate layer indexes.");
        }

        var targetPosition = sections.FindIndex(section => section.IsTarget);
        if (targetPosition < 0)
        {
            throw new InvalidOperationException("The RTSS anchor layer could not be located.");
        }

        var targetSection = sections[targetPosition];
        MarkAnchor(targetSection);
        sections.RemoveAt(targetPosition);
        sections.Add(targetSection);
        for (var index = 0; index < sections.Count; index++)
        {
            var ending = sections[index].Lines[0].EndsWith('\r') ? "\r" : string.Empty;
            sections[index].Lines[0] = $"[Layer{index}]{ending}";
        }

        var updated = lines.Take(firstLine).ToList();
        foreach (var section in sections)
        {
            updated.AddRange(section.Lines);
        }

        updated.AddRange(lines.Skip(lastLine));
        return string.Join("\n", updated);
    }

    private static void MarkAnchor(RtssLayerSection section)
    {
        for (var index = 1; index < section.Lines.Count; index++)
        {
            var raw = section.Lines[index];
            var bare = raw.TrimEnd('\r');
            var equals = bare.IndexOf('=');
            if (equals < 0 || !string.Equals(bare[..equals].Trim(), "Name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var indentationLength = bare.Length - bare.TrimStart(' ', '\t').Length;
            section.Lines[index] = bare[..indentationLength]
                + $"Name={AnchorName}"
                + (raw.EndsWith('\r') ? "\r" : string.Empty);
            return;
        }

        section.Lines.Insert(1, $"Name={AnchorName}" + (section.Lines[0].EndsWith('\r') ? "\r" : string.Empty));
    }

    private static (int X, int Y) DefaultAnchorPosition(IEnumerable<RtssOverlayLayer> layers)
    {
        var xCounts = new Dictionary<int, int>();
        var minY = 0;
        var hasText = false;
        foreach (var layer in layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Text))
            {
                continue;
            }

            minY = hasText ? Math.Min(minY, layer.PositionY) : layer.PositionY;
            hasText = true;
            xCounts[layer.PositionX] = xCounts.GetValueOrDefault(layer.PositionX) + 1;
        }

        if (!hasText)
        {
            return (0, 1);
        }

        var positionX = 0;
        var maximumCount = 0;
        foreach (var (x, count) in xCounts.OrderBy(pair => pair.Key))
        {
            if (count > maximumCount
                || (count == maximumCount && (Math.Abs(x) < Math.Abs(positionX)
                    || (Math.Abs(x) == Math.Abs(positionX) && x < positionX))))
            {
                positionX = x;
                maximumCount = count;
            }
        }

        return (positionX, minY - 1);
    }

    private static RtssLayoutInspection ParseLayout(string data)
    {
        var lines = data.Split('\n');
        var result = new RtssLayoutInspection();
        var current = -1;
        var section = string.Empty;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current >= 0)
                {
                    result.Layers[current].EndLine = lineIndex;
                }

                section = line[1..^1];
                if (section.StartsWith("Layer", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(section.AsSpan("Layer".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerIndex)
                    && layerIndex >= 0)
                {
                    current = result.Layers.Count;
                    result.Layers.Add(new RtssOverlayLayer
                    {
                        Index = layerIndex,
                        StartLine = lineIndex,
                    });
                    continue;
                }

                current = -1;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (current < 0 && string.Equals(section, "General", StringComparison.OrdinalIgnoreCase)
                && string.Equals(key, "Layers", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var declaredLayers)
                && declaredLayers >= 0)
            {
                result.DeclaredLayers = declaredLayers;
                result.LayersLine = lineIndex;
            }

            if (current < 0)
            {
                continue;
            }

            var layer = result.Layers[current];
            if (string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase))
            {
                layer.Name = value;
            }
            else if (string.Equals(key, "Text", StringComparison.OrdinalIgnoreCase))
            {
                layer.Text = value;
            }
            else if (string.Equals(key, "Size", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                layer.Size = size;
            }
            else if (string.Equals(key, "PositionX", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var positionX))
            {
                layer.PositionX = positionX;
            }
            else if (string.Equals(key, "PositionY", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var positionY))
            {
                layer.PositionY = positionY;
            }
        }

        if (current >= 0)
        {
            result.Layers[current].EndLine = lines.Length;
        }

        return result;
    }

    private static string ReadSettingsValue(string data, string wanted)
    {
        var inSettings = false;
        foreach (var raw in data.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSettings = string.Equals(line, "[Settings]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSettings)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator >= 0 && string.Equals(line[..separator].Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return Unquote(line[(separator + 1)..].Trim());
            }
        }

        return string.Empty;
    }

    private static string Unquote(string value) => value.Length >= 2
        && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static bool TryResolveLayoutPath(string overlayDirectory, string layoutName, out string layoutPath)
    {
        layoutPath = string.Empty;
        try
        {
            var root = Path.GetFullPath(overlayDirectory);
            var candidate = Path.GetFullPath(Path.Combine(root, layoutName));
            var relative = Path.GetRelativePath(root, candidate);
            if (relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return false;
            }

            layoutPath = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string BackupLayout(string path, string data)
    {
        var stem = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var bytes = LayoutEncoding.GetBytes(data);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1 ? stem : $"{stem}-{suffix}";
            try
            {
                using var backup = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                backup.Write(bytes);
                backup.Flush(flushToDisk: true);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Same-second export: use the next suffix rather than overwriting a backup.
            }
        }
    }

    private static void WriteLayoutAtomically(string path, string data)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".thrm-rtss-{Guid.NewGuid():N}.ovl");
        try
        {
            var bytes = LayoutEncoding.GetBytes(data);
            using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                temporary.Write(bytes);
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string FindInstallPath()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, Environment.GetEnvironmentVariable("RTSS_INSTALL_PATH"));
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                AddCandidate(candidates, Path.Combine(root, "RivaTuner Statistics Server"));
            }
        }

        foreach (var registryPath in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS",
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RivaTuner Statistics Server",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RTSS",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RivaTuner Statistics Server",
                 })
        {
            AddRegistryCandidates(candidates, Registry.LocalMachine, registryPath);
        }

        foreach (var registryPath in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS",
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RivaTuner Statistics Server",
                 })
        {
            AddRegistryCandidates(candidates, Registry.CurrentUser, registryPath);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var path = Path.GetFullPath(candidate);
                if (File.Exists(Path.Combine(path, "Plugins", "Client", "OverlayEditor.cfg")))
                {
                    return path;
                }
            }
            catch (ArgumentException)
            {
                // Skip an invalid registry value and continue with the other sources.
            }
            catch (NotSupportedException)
            {
                // Skip an invalid registry value and continue with the other sources.
            }
        }

        return string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryCandidates(ICollection<string> candidates, RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var valueName in new[] { "InstallLocation", "DisplayIcon", "UninstallString" })
            {
                if (key.GetValue(valueName) is not string value || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                AddCandidate(candidates, valueName == "InstallLocation" ? value : ExecutableDirectory(value));
            }
        }
        catch (IOException)
        {
            // Registry discovery is optional.
        }
        catch (UnauthorizedAccessException)
        {
            // Registry discovery is optional.
        }
    }

    private static void AddCandidate(ICollection<string> candidates, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            candidates.Add(value.Trim());
        }
    }

    private static string ExecutableDirectory(string command)
    {
        var value = command.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 0)
            {
                value = value[1..end];
            }
        }
        else
        {
            var end = value.IndexOfAny([' ', '\t']);
            if (end > 0)
            {
                value = value[..end];
            }
        }

        value = value.Trim().Trim('"');
        if (value.EndsWith(",0", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2];
        }

        return Path.GetDirectoryName(value) ?? string.Empty;
    }

    private sealed class RtssLayoutInspection
    {
        public List<RtssOverlayLayer> Layers { get; } = [];
        public int DeclaredLayers { get; set; }
        public int LayersLine { get; set; } = -1;
    }

    private sealed class RtssOverlayLayer
    {
        public int Index { get; init; }
        public string Name { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public int Size { get; set; } = 100;
        public int PositionX { get; set; }
        public int PositionY { get; set; }
        public int StartLine { get; init; }
        public int EndLine { get; set; }
    }

    private sealed class RtssLayerSection
    {
        public int Index { get; init; }
        public bool IsTarget { get; init; }
        public required List<string> Lines { get; init; }
    }

    private readonly record struct RtssOverlayLayoutStatusInspection(string State, int AnchorIndex, int LayerCount);
}
