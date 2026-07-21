using System.Reflection;
using System.Text.RegularExpressions;
using ESPresense.Models;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ESPresense.Services;

public class ConfigLoader : BackgroundService
{
    private readonly IDeserializer _deserializer;
    private Task _toWait;
    private DateTime _lastModified;
    private readonly string _configPath;
    public string ConfigPath => _configPath;
    public Config? Config { get; private set; }

    public ConfigLoader(string configDir)
    {
        _configPath = Path.Combine(configDir, "config.yaml");
        _deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _toWait = Load();
    }

    private async Task Load()
    {
        try
        {
            var fi = new FileInfo(_configPath);

            if (!fi.Exists)
            {
                await using var example = Assembly.GetExecutingAssembly().GetManifestResourceStream("ESPresense.config.example.yaml") ?? throw new Exception("Could not find embedded config.example.yaml");
                await using var newConfig = File.Create(_configPath);
                await example.CopyToAsync(newConfig);
            }

            if (_lastModified == fi.LastWriteTimeUtc)
                return;

            Log.Information("Loading " + _configPath);

            var reader = await File.ReadAllTextAsync(_configPath);
            Config = FixIds(_deserializer.Deserialize<Config>(reader));
            // Assign/normalize room colors with adjacency-aware algorithm
            Utils.ColorAssigner.AssignRoomColors(Config);
            ConfigChanged?.Invoke(this, Config);
            _lastModified = fi.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            Log.Error($"Error reading config, ignoring... {ex}");
        }
    }

    private Config FixIds(Config? c)
    {
        Config config = c ?? new Config();

        foreach (var device in config.Devices ?? Enumerable.Empty<ConfigDevice>())
            device.Id ??= device.GetId();

        foreach (var node in config.Nodes ?? Enumerable.Empty<ConfigNode>())
            node.Id ??= node.GetId();

        foreach (var floor in config.Floors ?? Enumerable.Empty<ConfigFloor>())
            floor.Id ??= floor.GetId();

        foreach (var room in config.Floors?.SelectMany(a => a.Rooms ?? Enumerable.Empty<ConfigRoom>()) ?? Enumerable.Empty<ConfigRoom>())
        {
            room.Id ??= room.GetId();
        }

        // Colors now handled in AssignRoomColors()

        return config;
    }

    private static readonly HashSet<string> ProtectedSections = new(StringComparer.OrdinalIgnoreCase);

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public async Task SaveSectionAsync(string sectionName, object value)
    {
        if (ProtectedSections.Contains(sectionName))
            throw new InvalidOperationException($"Section '{sectionName}' cannot be saved via API");

        var sectionYaml = _serializer.Serialize(
            new Dictionary<string, object> { { sectionName, value } }
        ).TrimEnd('\r', '\n');

        var text = await File.ReadAllTextAsync(_configPath);

        // Line-based section replacement. The previous regex approach
        // (^section:.*(\n([ \t]+.*)|\n\s*)*) had two real-world corruption modes:
        // its greedy trailing \n\s* consumed the newline AND the indentation of the first line
        // after a BLANK line inside the section, leaving the rest of the old section behind
        // dedented to column 0 (invalid YAML, happened live on a locators save), and it could
        // glue a following top-level comment onto the serialized section's last line.
        // A section here spans from its "name:" line through all following indented, blank or
        // column-0 SEQUENCE-ITEM lines ("- ..." - YamlDotNet serializes top-level list items at
        // column 0, and a bare item can only belong to the current section; treating it as a new
        // section made a second save duplicate every floors entry live). It ends at the first
        // other column-0 line (next top-level key OR full-line comment - comments stay with
        // whatever follows them).
        var lines = text.Split('\n').ToList();
        var start = lines.FindIndex(l => l.StartsWith(sectionName + ":"));

        string replaced;
        if (start >= 0)
        {
            var end = start + 1;
            while (end < lines.Count &&
                   (lines[end].Length == 0 || lines[end][0] == ' ' || lines[end][0] == '\t' || lines[end][0] == '-' || lines[end].TrimEnd('\r').Length == 0))
                end++;
            // Leave trailing blank lines to the remainder so section spacing is preserved.
            while (end - 1 > start && lines[end - 1].TrimEnd('\r').Length == 0)
                end--;

            var newLines = lines.Take(start)
                .Concat(sectionYaml.Split('\n'))
                .Concat(lines.Skip(end));
            replaced = string.Join('\n', newLines);
        }
        else
        {
            replaced = text.TrimEnd() + "\n\n" + sectionYaml + "\n";
        }

        await File.WriteAllTextAsync(_configPath, replaced);
    }

    public event EventHandler<Config>? ConfigChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _toWait;
            _toWait = Load();
            await Task.Delay(1000, stoppingToken);
        }
    }

    public async Task<Config> ConfigAsync(CancellationToken ct = default)
    {
        while (true)
        {
            await _toWait;
            if (Config != null)
                return Config;
            ct.ThrowIfCancellationRequested();
        }
    }
}
