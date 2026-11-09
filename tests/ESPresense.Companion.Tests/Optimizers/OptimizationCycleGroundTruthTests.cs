using ESPresense.Models;
using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>The fork's walk-point rules, as they sit inside upstream's optimization cycle.</summary>
[TestFixture]
public class OptimizationCycleGroundTruthTests
{
    private sealed class FakeOptimizer(string name, bool walk, OptimizationResults results) : IOptimizer
    {
        public string Name => name;
        public bool ScoredByWalkPoints => walk;
        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings) => results;
    }

    private sealed class FakeGate(IReadOnlySet<string> covered, bool canJudge, Func<string, bool> verdict) : IGroundTruthGate
    {
        public List<(string Optimizer, double? Absorption)> Checked { get; } = new();
        public IReadOnlySet<string> CoveredNodes => covered;
        public bool CanJudge => canJudge;

        public OptimizerWalkPointGate Check(string optimizerName, OptimizationResults results)
        {
            Checked.Add((optimizerName, results.Nodes.GetValueOrDefault("a")?.Absorption));
            return new OptimizerWalkPointGate(0.8, 0.7, 1.0, 1.2, verdict(optimizerName), null);
        }
    }

    private static readonly ConfigOptimization Optimization = new() { MinSamples = 1 };

    private static NodeSettings Settings(string id)
    {
        var s = new NodeSettings(id);
        // "a" already holds the value its pair-fit proposal would be stripped back to, so that proposal can still win.
        s.Calibration.Absorption = id == "a" ? 3.0 : 2.0;
        s.Calibration.RxAdjRssi = 0;
        s.Calibration.TxRefRssi = -59;
        return s;
    }

    /// <summary>Levels generated with absorption 3, so a proposal of 3 beats the incumbent 2 on every snapshot.</summary>
    private static OptimizationSnapshot Snapshot(bool groundTruth = false)
    {
        var nodes = new[] { Node("a", 0, 0), Node("b", 6, 0), Node("c", 0, 6), Node("d", 6, 7) };
        var os = new OptimizationSnapshot { IsGroundTruth = groundTruth };
        foreach (var rx in nodes)
        foreach (var tx in nodes)
        {
            if (rx.Id == tx.Id) continue;
            var d = rx.Location.DistanceTo(tx.Location);
            os.Measures.Add(new Measure { Rx = rx, Tx = tx, Rssi = -59 - 30 * Math.Log10(d), RssiRxAdj = 0, RefRssi = -59, Distance = d });
        }
        return os;
    }

    private static OptNode Node(string id, double x, double y) => new() { Id = id, Name = id, Location = new Point3D(x, y, 1) };

    private static OptimizationResults Proposal(double? absorption, double? rxAdj, params string[] ids)
    {
        var r = new OptimizationResults();
        foreach (var id in ids) r.Nodes[id] = new ProposedValues { Absorption = absorption, RxAdjRssi = rxAdj };
        return r;
    }

    private static OptimizationCycleResult Run(IGroundTruthGate? gate, params IOptimizer[] optimizers) =>
        OptimizationCycle.Evaluate(Snapshot(), new[] { Snapshot(), Snapshot() }, Optimization, optimizers, Settings, dryRun: true, DateTime.UtcNow, gate);

    private static readonly string[] All = { "a", "b", "c", "d" };

    [Test]
    public void PairFit_LosesAbsorptionOnNodesGroundTruthOwns_BeforeScoring()
    {
        var gate = new FakeGate(new HashSet<string> { "a" }, true, _ => true);
        var cycle = Run(gate, new FakeOptimizer("pair", false, Proposal(3.0, 0, All)));

        Assert.That(gate.Checked.Single().Absorption, Is.Null, "the gate must judge what may actually be applied");
        Assert.That(cycle.SelectedResults!.Nodes["a"].Absorption, Is.Null);
        Assert.That(cycle.SelectedResults.Nodes["b"].Absorption, Is.EqualTo(3.0));
    }

    [Test]
    public void WalkPointGate_RejectsPairProposalThatWinsNodeToNode()
    {
        var cycle = Run(new FakeGate(new HashSet<string>(), true, _ => false), new FakeOptimizer("pair", false, Proposal(3.0, 0, All)));

        Assert.That(cycle.Selected, Is.Null);
        Assert.That(cycle.Report.Proposals.Single().Decision, Is.EqualTo(OptimizerGateDecision.WalkPointGate));
    }

    [Test]
    public void WalkFit_IsAppliedOnTopOfPairProposal()
    {
        var gate = new FakeGate(new HashSet<string> { "a" }, true, _ => true);
        var cycle = Run(gate,
            new FakeOptimizer("pair", false, Proposal(3.0, 1, All)),
            new FakeOptimizer("walk", true, Proposal(2.6, null, "a")));

        var a = cycle.SelectedResults!.Nodes["a"];
        Assert.That(a.Absorption, Is.EqualTo(2.6), "walk fit owns this node's absorption");
        Assert.That(a.RxAdjRssi, Is.EqualTo(1), "and leaves its rxAdj to the pair fit");
        Assert.That(cycle.Report.Selected, Is.EqualTo("pair + walk"));
    }

    [Test]
    public void WalkFit_CanWorsenNodePairScore_AndStillApply()
    {
        // 9 is a terrible absorption node-to-node; only ground truth decides a walk-point fit.
        var cycle = Run(new FakeGate(new HashSet<string> { "a" }, true, _ => true), new FakeOptimizer("walk", true, Proposal(9.0, null, "a")));

        Assert.That(cycle.Selected?.Name, Is.EqualTo("walk"));
    }

    [Test]
    public void WalkFit_WithoutGroundTruthVerdict_IsNotApplied()
    {
        var cycle = Run(new FakeGate(new HashSet<string> { "a" }, false, _ => true), new FakeOptimizer("walk", true, Proposal(2.6, null, "a")));

        Assert.That(cycle.Selected, Is.Null);
        Assert.That(cycle.Report.Proposals.Single().Decision, Is.EqualTo(OptimizerGateDecision.WalkPointGate));
    }

    [Test]
    public void GroundTruthSnapshot_IsNotHeldToTheEverySnapshotGate()
    {
        // Walk measures that the proposal cannot beat must not count as a lost snapshot.
        var worse = Snapshot(groundTruth: true);
        foreach (var m in worse.Measures) m.Rssi = -59 - 20 * Math.Log10(m.Distance);

        var cycle = OptimizationCycle.Evaluate(Snapshot(), new[] { Snapshot(), Snapshot(), Snapshot(), Snapshot(), worse }, Optimization,
            new IOptimizer[] { new FakeOptimizer("pair", false, Proposal(3.0, 0, All)) }, Settings, dryRun: true, DateTime.UtcNow);

        Assert.That(cycle.Report.Proposals.Single().Gates.EverySnapshot!.Of, Is.EqualTo(4));
        Assert.That(cycle.Selected?.Name, Is.EqualTo("pair"));
    }
}
