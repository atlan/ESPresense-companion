using TextExtensions;
using YamlDotNet.Serialization;
using ESPresense.Extensions;

namespace ESPresense.Models
{
    public partial class Config
    {
        [YamlMember(Alias = "mqtt")]
        public ConfigMqtt Mqtt { get; set; } = new();

        [YamlMember(Alias = "gps")]
        public ConfigGps Gps { get; set; } = new();

        [YamlMember(Alias = "map")]
        public ConfigMap Map { get; set; } = new();

        [YamlMember(Alias = "timeout")]
        public int Timeout { get; set; } = 30;

        [YamlMember(Alias = "away_timeout")]
        public int AwayTimeout { get; set; } = 120;

        // Retention policy for inactive devices (duration string, e.g., "30d", "720h")
        [YamlMember(Alias = "device_retention")]
        public string DeviceRetention { get; set; } = "30d";

        [YamlIgnore]
        public TimeSpan DeviceRetentionTimeSpan => DeviceRetention.TryParseDurationString(out var ts, DurationUnit.Days) ? ts : TimeSpan.FromDays(30);

        [YamlMember(Alias = "optimization")]
        public ConfigOptimization Optimization { get; set; } = new();

        [YamlMember(Alias = "locators")]
        public ConfigLocators Locators { get; set; } = new();

        [YamlMember(Alias = "filtering")]
        public ConfigFiltering Filtering { get; set; } = new();

        [YamlMember(Alias = "history")]
        public ConfigHistory History { get; set; } = new();

        [YamlMember(Alias = "floors")]
        public ConfigFloor[] Floors { get; set; } = Array.Empty<ConfigFloor>();

        [YamlMember(Alias = "nodes")]
        public ConfigNode[] Nodes { get; set; } = Array.Empty<ConfigNode>();

        [YamlMember(Alias = "devices")]
        public ConfigDevice[] Devices { get; set; } = Array.Empty<ConfigDevice>();

        [YamlMember(Alias = "exclude_devices")]
        public ConfigDevice[] ExcludeDevices { get; set; } = Array.Empty<ConfigDevice>();
    }

    public partial class ConfigLocators
    {
        /// <summary>
        /// How many confidence points the cross-floor contrast may shift the floor decision by.
        /// See <see cref="ESPresense.Locators.FloorContrast"/>. Default 20 rather than 0 because the
        /// measurement is unusually clear - floor detection 87.9 % to 96.5 % over 32 walk points,
        /// monotone from 5 to 30, with position and room accuracy untouched. Set to 0 to decide the
        /// storey from same-floor nodes alone.
        ///
        /// Sits on the locator section rather than inside nadaraya_watson because it applies to every
        /// scenario regardless of which multilateralizer produced it - anything else leaves the other
        /// locators voting on the floor without the evidence.
        /// </summary>
        [YamlMember(Alias = "floor_contrast_weight")]
        public double FloorContrastWeight { get; set; } = 20;


        [YamlMember(Alias = "nadaraya_watson")]
        public NadarayaWatsonConfig NadarayaWatson { get; set; } = new();

        [YamlMember(Alias = "nelder_mead")]
        public NelderMeadConfig NelderMead { get; set; } = new();

        [YamlMember(Alias = "bfgs")]
        public BfgsConfig Bfgs { get; set; } = new();

        [YamlMember(Alias = "mle")]
        public MleConfig Mle { get; set; } = new();

        [YamlMember(Alias = "multi_floor")]
        public MultiFloorConfig MultiFloor { get; set; } = new();

        [YamlMember(Alias = "nearest_node")]
        public NearestNodeConfig NearestNode { get; set; } = new();
    }

    public partial class NadarayaWatsonConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "bandwidth")]
        public double Bandwidth { get; set; } = 0.5;

        [YamlMember(Alias = "kernel")]
        public string Kernel { get; set; } = "inverse_square";

        /// <summary>
        /// Reject readings that cannot all be true at once before estimating a position - see
        /// <see cref="ESPresense.Locators.ConsistencyFilter"/>. Measured over 32 walk points: median
        /// error 1.96 m to 1.86 m and room hit rate 57 % to 62 %, with every point still scored.
        ///
        /// It was measured once BEFORE the cross-floor contrast existed and rejected then, because it
        /// and a corrected device reference level were fixing the same thing and together came out
        /// worse. Worth knowing when reading the git history: the same change can be right and wrong
        /// depending on what else is in place, which is the whole reason the benchmark exists.
        /// </summary>
        [YamlMember(Alias = "consistency_filter")]
        public bool ConsistencyFilter { get; set; } = false;

        /// <summary>
        /// Slack on the triangle inequality, in metres plus a share of the node separation. Swept
        /// from 0.25 m to 4 m: 1.5 m is the optimum on both median error and room hit rate. Tighter
        /// starts discarding honest readings (room falls to 52 % at 0.5 m), looser stops acting at all.
        /// </summary>
        [YamlMember(Alias = "consistency_tolerance_m")]
        public double ConsistencyToleranceM { get; set; } = ESPresense.Locators.ConsistencyFilter.DefaultToleranceM;

        [YamlMember(Alias = "consistency_tolerance_fraction")]
        public double ConsistencyToleranceFraction { get; set; } = ESPresense.Locators.ConsistencyFilter.DefaultToleranceFraction;

        /// <summary>
        /// Gewicht der Messunsicherheit im Konsistenz-Spielraum, in Standardabweichungen.
        /// 0 = aus (Default) und der Filter verhaelt sich exakt wie bisher. Ob eine
        /// varianzabhaengige Toleranz die feste schlaegt, beantwortet der Walk-Punkt-Pruefstand.
        /// </summary>
        [YamlMember(Alias = "consistency_variance_weight")]
        public double ConsistencyVarianceWeight { get; set; } = ESPresense.Locators.ConsistencyFilter.DefaultVarianceWeight;

    }

    public partial class NelderMeadConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class BfgsConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class MleConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class MultiFloorConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class NearestNodeConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        // Don't fall back to a node whose distance to the device is above this (metres). Unset or <= 0: no limit.
        [YamlMember(Alias = "max_distance")]
        public double? MaxDistance { get; set; }
    }

    public partial class ConfigMap
    {
        [YamlMember(Alias = "flip_x")]
        public bool FlipX { get; set; } = false;

        [YamlMember(Alias = "flip_y")]
        public bool FlipY { get; set; } = true;

        [YamlMember(Alias = "wall_thickness")]
        public double WallThickness { get; set; } = 0.1;  // Default wall thickness in meters

        [YamlMember(Alias = "wall_color")]
        public string? WallColor { get; set; }  // Optional wall color, defaults to room color if not set

        [YamlMember(Alias = "wall_opacity")]
        public double? WallOpacity { get; set; }  // Optional wall opacity, defaults to 0.35 if not set
    }

    public partial class ConfigOptimization
    {
        [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = false;
        [YamlMember(Alias = "optimizer")] public string Optimizer { get; set; } = "per_node_absorption"; // The only mode; the removed legacy and global_absorption modes fall back to it
        [YamlMember(Alias = "sample_interval_secs", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? SampleIntervalSecs { get; set; }
        [YamlMember(Alias = "training_window_mins", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? TrainingWindowMins { get; set; }
        [YamlMember(Alias = "optimization_interval_secs", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? OptimizationIntervalSecs { get; set; }
        [YamlMember(Alias = "validation_fraction")] public double ValidationFraction { get; set; } = 0.2;
        [YamlMember(Alias = "minimum_improvement")] public double MinimumImprovement { get; set; } = 0; // How far a proposal's overall score must beat the current calibration's

        // Legacy timing keys. Keep these as deserialization fallbacks for existing installations.
        [YamlMember(Alias = "interval_secs", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? IntervalSecs { get; set; }
        [YamlMember(Alias = "keep_snapshot_mins", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? KeepSnapshotMins { get; set; }
        [YamlMember(Alias = "min_samples")] public int MinSamples { get; set; } = 5; // Minimum holdout measurements a candidate must be evaluated on before it can be applied
        [YamlMember(Alias = "limits")] public Dictionary<string, double> Limits { get; set; } = new();
        [YamlMember(Alias = "weights")] public Dictionary<string, double> Weights { get; set; } = new();

        // Same-floor node pairs to exclude from calibration entirely (e.g. two nodes separated by
        // an unusually attenuating wall) - see SpatialUtils.IsExcludedPair. Entries are
        // "node_id_a:node_id_b"; order and case don't matter.
        [YamlMember(Alias = "excluded_pairs")] public string[] ExcludedPairs { get; set; } = Array.Empty<string>();
        [YamlIgnore] public int EffectiveSampleIntervalSecs => Math.Max(1, SampleIntervalSecs ?? 30);
        [YamlIgnore] public int EffectiveTrainingWindowMins => Math.Max(1, TrainingWindowMins ?? KeepSnapshotMins ?? 30);
        [YamlIgnore] public int EffectiveOptimizationIntervalSecs => Math.Max(1, OptimizationIntervalSecs ?? IntervalSecs ?? 900);
        [YamlIgnore] public double EffectiveValidationFraction => Math.Clamp(ValidationFraction, 0.1, 0.5);

        [YamlIgnore] public double AbsorptionMin => Limits.TryGetValue("absorption_min", out var val) ? val : 2;
        [YamlIgnore] public double AbsorptionMax => Limits.TryGetValue("absorption_max", out var val) ? val : 4;
        [YamlIgnore] public double TxRefRssiMin => Limits.TryGetValue("tx_ref_rssi_min", out var val) ? val : -70;
        [YamlIgnore] public double TxRefRssiMax => Limits.TryGetValue("tx_ref_rssi_max", out var val) ? val : -50;
        [YamlIgnore] public double RxAdjRssiMin => Limits.TryGetValue("rx_adj_rssi_min", out var val) ? val : -5;
        [YamlIgnore] public double RxAdjRssiMax => Limits.TryGetValue("rx_adj_rssi_max", out var val) ? val : 30;

        /// <summary>
        /// Prueft eine Kandidaten-Kalibrierung zusaetzlich gegen die Walk-Punkte, bevor sie
        /// angewandt wird.
        ///
        /// Warum das noetig ist: das bisherige Accept-Gate bewertet ausschliesslich, wie gut die
        /// Knoten UNTEREINANDER zusammenpassen. Eine Aenderung kann diese Zahl verbessern und die
        /// tatsaechliche Ortung verschlechtern, und genau das ist unbemerkt passiert - die Guete fiel
        /// von r 0,88 auf 0,49, ohne dass irgendetwas Alarm schlug. Die Walk-Punkte sind die einzige
        /// Bodenwahrheit im System; wer sie hat, sollte gegen sie pruefen.
        ///
        /// Greift nur, wenn Walk-Punkte MIT aufgezeichneten Pegeln vorliegen - nur dann lassen sich
        /// die Distanzen mit der Kandidaten-Kalibrierung neu rechnen. Ohne sie ist der Vergleich
        /// blind und der Kandidat wird durchgelassen.
        /// </summary>
        [YamlMember(Alias = "walk_point_gate")] public bool WalkPointGate { get; set; } = true;

        /// <summary>Um so viele Prozentpunkte darf die Raumtrefferquote fallen, bevor abgelehnt wird.</summary>
        [YamlIgnore] public double WalkGateRoomDrop => Limits.TryGetValue("walk_gate_room_drop", out var val) ? val : 0.02;

        /// <summary>Um so viele Meter darf der Medianfehler wachsen, bevor abgelehnt wird.</summary>
        [YamlIgnore] public double WalkGateMedianToleranceM => Limits.TryGetValue("walk_gate_median_tolerance_m", out var val) ? val : 0.15;

        [YamlIgnore] public double CorrelationWeight => Weights.TryGetValue("correlation", out var val) ? val : 0.5;
        [YamlIgnore] public double RmseWeight => Weights.TryGetValue("rmse", out var val) ? val : 0.5;

        // Regularization strength pulling PerNodeAbsorptionRxTx's per-node absorption fit toward
        // AbsorptionTarget. Default 4: a 0.5 deviation from the target costs as much as a 1 dB residual.
        [YamlIgnore] public double AbsorptionPenaltyWeight => Weights.TryGetValue("absorption_penalty", out var val) ? val : 4;

        /// <summary>
        /// Where the regularization pulls absorption to. Null means "the middle of the limits",
        /// which is what it has always done implicitly - and which is a trap, because the limits are
        /// meant to say what is POSSIBLE while this says what is LIKELY. Measured on a real
        /// installation 2026-07-26: limits 2.5..4.8 put the target at 3.65 while all 18 nodes fitted
        /// to 4.06-4.59, so the regularization pulled every single node downwards, against the data.
        /// Setting it explicitly, or letting <see cref="PerNodeAbsorptionRxTx"/> seed it from the
        /// fleet's own median, decouples the two meanings.
        /// </summary>
        [YamlIgnore] public double? AbsorptionTarget => Weights.TryGetValue("absorption_target", out var val) ? val : null;


        /// <summary>
        /// Huber transition in dB for the "db" objective. Below it residuals are squared, above it
        /// they grow linearly, so one contradictory pair stops outvoting a roomful of good ones.
        /// 6 dB is roughly the spread of ordinary RSSI noise on this hardware.
        /// </summary>
        [YamlIgnore] public double HuberDeltaDb => Weights.TryGetValue("huber_delta_db", out var val) ? val : 6.0;
    }

    public partial class ConfigHistory
    {
        [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = false;

        // SQLite file; relative paths are under <config dir>/.storage. Read once at startup.
        [YamlMember(Alias = "path")]
        public string Path { get; set; } = "history.db";

        [YamlMember(Alias = "expire_after")] public string ExpireAfter { get; set; } = "24h";

        [YamlIgnore]
        public TimeSpan ExpireAfterTimeSpan => ExpireAfter.TryParseDurationString(out var ts) ? ts : TimeSpan.FromHours(24);
    }

    public partial class ConfigWeighting
    {
        [YamlMember(Alias = "algorithm")]
        public string Algorithm { get; set; } = "gaussian";

        [YamlMember(Alias = "props")] public Dictionary<string, double> Props { get; set; } = new();
    }

    public partial class ConfigGps
    {
        [YamlMember(Alias = "latitude")]
        public double? Latitude { get; set; }
        [YamlMember(Alias = "longitude")]
        public double? Longitude { get; set; }
        [YamlMember(Alias = "elevation")]
        public double? Elevation { get; set; }
        [YamlMember(Alias = "rotation")]
        public double? Rotation { get; set; } // Degrees clockwise from North
        [YamlMember(Alias = "report")]
        public bool Report { get; set; } = false;
    }

    public partial class ConfigMqtt
    {
        public string? Host { get; set; }

        public int? Port { get; set; }

        public bool? Ssl { get; set; }

        [YamlMember(Alias = "insecure")]
        public bool Insecure { get; set; }

        [YamlMember(Alias = "ca_cert_path")]
        public string? CaCertPath { get; set; }

        public string? Username { get; set; }

        // Never serialize the broker password to API responses or MCP clients. Config only leaves through
        // System.Text.Json (Newtonsoft is confined to the files NewtonsoftConventionTests allows, none of
        // which serialize Config). YamlDotNet has its own attributes, so YAML round-tripping is unaffected.
        // Logging is not covered: Serilog's {@} destructuring ignores JSON attributes, so never {@}-log a Config.
        [System.Text.Json.Serialization.JsonIgnore]
        public string? Password { get; set; }

        [YamlMember(Alias = "client_id")]
        public string ClientId { get; set; } = "espresense-companion";

        [YamlMember(Alias = "discovery_topic")]
        public string DiscoveryTopic { get; set; } = "homeassistant";

        /// <summary>
        /// When true the companion subscribes and processes messages but never publishes; each publish
        /// is logged at Information level instead. Defaults to false. Replaces the deprecated heuristic
        /// of a <see cref="ClientId"/> containing "read", which still enables read-only mode but logs a warning.
        /// </summary>
        [YamlMember(Alias = "read_only")]
        public bool ReadOnly { get; set; }
    }

    public partial class ConfigDevice
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public partial class ConfigFloor
    {
        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "bounds")]
        public double[][]? Bounds { get; set; }

        [YamlMember(Alias = "rooms")]
        public ConfigRoom[]? Rooms { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public partial class ConfigRoom
    {
        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "points")]
        public double[][]? Points { get; set; }

        [YamlMember(Alias = "color")]
        public string? Color { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public partial class ConfigNode
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "point")]
        public double[]? Point { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        // Written by the Companion UI when a node is placed inside a room polygon on the map,
        // but never read anywhere in the backend until now - an explicit, authoritative "this
        // node lives in this room" signal that's strictly more reliable than either geometric
        // polygon containment or name-matching the node against room names.
        [YamlMember(Alias = "room")]
        public string? Room { get; set; }

        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; } = true;

        [YamlMember(Alias = "stationary")]
        public bool Stationary { get; set; } = true;

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public partial class ConfigFiltering
    {
        [YamlMember(Alias = "process_noise")]
        public double ProcessNoise { get; set; } = 0.01;

        [YamlMember(Alias = "measurement_noise")]
        public double MeasurementNoise { get; set; } = 0.1;

        [YamlMember(Alias = "max_velocity")]
        public double MaxVelocity { get; set; } = 0.5;

        [YamlMember(Alias = "smoothing_weight")]
        public double SmoothingWeight { get; set; } = 0.7;

        [YamlMember(Alias = "motion_sigma")]
        public double MotionSigma { get; set; } = 2.0;
    }
}
