using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Companion.Tests;

public class ConfigSaveTests
{
    private string _tempDir = null!;
    private string _configPath = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "espresense-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yaml");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public async Task SaveSectionAsync_ReplacesOptimizationSection()
    {
        var original = @"mqtt:
  host: localhost
  port: 1883

optimization:
  enabled: false
  interval_secs: 60

locators:
  nearest_node:
    enabled: true
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("optimization", new ConfigOptimization
        {
            Enabled = true,
            IntervalSecs = 3600,
            Optimizer = "per_node_absorption"
        });

        var result = await File.ReadAllTextAsync(_configPath);

        // Optimization section was replaced
        Assert.That(result, Does.Contain("enabled: true"));
        Assert.That(result, Does.Contain("interval_secs: 3600"));
        Assert.That(result, Does.Contain("per_node_absorption"));

        // Other sections preserved
        Assert.That(result, Does.Contain("mqtt:"));
        Assert.That(result, Does.Contain("host: localhost"));
        Assert.That(result, Does.Contain("locators:"));
        Assert.That(result, Does.Contain("nearest_node:"));
    }

    [Test]
    public async Task SaveSectionAsync_PreservesCommentsBefore()
    {
        var original = @"# Main MQTT config
mqtt:
  host: localhost

# Optimization settings
optimization:
  enabled: false

# Locator config
locators:
  nearest_node:
    enabled: true
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("optimization", new ConfigOptimization { Enabled = true });

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("# Main MQTT config"));
        Assert.That(result, Does.Contain("# Locator config"));
    }

    [Test]
    public async Task SaveSectionAsync_AppendsWhenSectionMissing()
    {
        var original = @"mqtt:
  host: localhost
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("optimization", new ConfigOptimization { Enabled = true });

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("mqtt:"));
        Assert.That(result, Does.Contain("optimization:"));
        Assert.That(result, Does.Contain("enabled: true"));
    }

    [Test]
    public async Task SaveSectionAsync_RejectsProtectedSection()
    {
        await File.WriteAllTextAsync(_configPath, "map:\n  flip_x: false\n");
        var loader = new ConfigLoader(_tempDir);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.SaveSectionAsync("map", new ConfigMap()));
    }

    [Test]
    public async Task SaveSectionAsync_HandlesScalarValues()
    {
        var original = @"timeout: 30
away_timeout: 120
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("timeout", 60);

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("timeout: 60"));
        Assert.That(result, Does.Contain("away_timeout: 120"));
    }

    [Test]
    public async Task SaveSectionAsync_RoundTripsOptimizationWithLimits()
    {
        var original = @"optimization:
  enabled: true
  optimizer: legacy
  interval_secs: 60
  limits:
    absorption_min: 2.5
    absorption_max: 3.5
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        var opt = new ConfigOptimization
        {
            Enabled = true,
            Optimizer = "per_node_absorption",
            IntervalSecs = 3600,
            Limits = new Dictionary<string, double>
            {
                { "absorption_min", 2.0 },
                { "absorption_max", 4.0 }
            }
        };

        await loader.SaveSectionAsync("optimization", opt);

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("per_node_absorption"));
        Assert.That(result, Does.Contain("interval_secs: 3600"));
        Assert.That(result, Does.Contain("absorption_min: 2"));
        Assert.That(result, Does.Contain("absorption_max: 4"));
    }

    [Test]
    public async Task SaveSectionAsync_ReplacesSectionContainingBlankLines()
    {
        // Regression: the old regex-based replacement had a greedy \n\s* that, at a BLANK line
        // inside the section, also consumed the indentation of the following line - the rest of
        // the old section stayed behind dedented to column 0 and corrupted the file into invalid
        // YAML (happened live on a locators section with blank lines between sub-blocks).
        var original = @"mqtt:
  host: localhost

locators:
  nadaraya_watson:
    enabled: true
    bandwidth: 1.0

  nelder_mead:
    enabled: true

  nearest_node:
    enabled: true
    max_distance: 2

# Floors comment
floors:
  - id: ground
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("locators", new ConfigLocators());

        var result = await File.ReadAllTextAsync(_configPath);
        var lines = result.Split('\n');

        // No dedented leftovers of the old section content
        Assert.That(lines, Has.None.EqualTo("nelder_mead:"));
        Assert.That(result.Split("locators:").Length - 1, Is.EqualTo(1), "exactly one locators section");

        // Following comment + section preserved on their own lines
        Assert.That(lines, Does.Contain("# Floors comment"));
        Assert.That(lines, Does.Contain("floors:"));

        // Result must be parseable YAML
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
        Assert.DoesNotThrow(() => deserializer.Deserialize<object>(result));
    }

    [Test]
    public async Task SaveSectionAsync_ReplacesSectionWithColumnZeroSequenceItems()
    {
        // Regression: YamlDotNet serializes top-level list items at COLUMN 0 ("floors:\n- id: x").
        // The line scan treated a column-0 "- ..." as the start of the next section, so a SECOND
        // save replaced only the bare "floors:" line and left the previous save's items behind -
        // live this duplicated every floor after add-floor followed by delete-floor.
        var original = @"mqtt:
  host: localhost

floors:
- id: ground
  name: Ground
  bounds:
  - - 0
    - 0
    - 0
  - - 5
    - 5
    - 3
- id: first
  name: First

# Nodes comment
nodes:
- id: kitchen
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("floors", new[]
        {
            new ConfigFloor { Id = "ground", Name = "Ground", Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 5.0, 5, 3 } } }
        });

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result.Split("- id: ground").Length - 1, Is.EqualTo(1), "ground floor exactly once");
        Assert.That(result, Does.Not.Contain("- id: first"), "removed floor must not survive");
        Assert.That(result.Split('\n'), Does.Contain("# Nodes comment"));
        Assert.That(result, Does.Contain("- id: kitchen"), "following nodes section preserved");

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
        Assert.DoesNotThrow(() => deserializer.Deserialize<object>(result));
    }
}
