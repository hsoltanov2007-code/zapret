using Northpass.Engine.Native;
namespace Northpass.Tests;
public sealed class NorthpassSplitMetricsTests
{
    public const string Line = "NORTHPASS_SPLIT_METRICS protocol=4 proposed=2 accepted=1 rejected=1 lab_reinjections=2 send_failures=0 dropped_known=0 kernel_loss_unknown=1 reconstruction_unknown=1 trace_packets=3 latency_us=100.5";
    [Fact]
    public void LabSendIsDistinctFromEndpointReconstructionAndUnknownKernelLoss()
    {
        var p = NorthpassSplitMetrics.Parse(Line);
        Assert.Equal(2ul, p.LabReinjections); Assert.True(p.ReconstructionUnknown && p.KernelLossUnknown); Assert.Equal(100.5, p.ProcessingLatencyMicroseconds);
    }
    [Theory]
    [InlineData("protocol=4", "protocol=3")]
    [InlineData("accepted=1", "accepted=3")]
    [InlineData("rejected=1", "rejected=2")]
    [InlineData("lab_reinjections=2", "lab_reinjections=17")]
    [InlineData("kernel_loss_unknown=1", "kernel_loss_unknown=0")]
    [InlineData("reconstruction_unknown=1", "reconstruction_unknown=0")]
    [InlineData("latency_us=100.5", "latency_us=NaN")]
    [InlineData("trace_packets=3", "trace_packets=-1")]
    [InlineData("trace_packets=3", "accepted=1")]
    public void InvalidFramesCannotFabricateSuccess(string from, string to) => Assert.Throws<InvalidDataException>(() => NorthpassSplitMetrics.Parse(Line.Replace(from, to)));
    [Fact]
    public void NormalAdapterDoesNotExposeExperimentalStrategies()
    {
        var p = NativeCatalog.Loopback(55000, "tcp"); p.StrategyId = "northpass-split";
        Assert.Throws<InvalidDataException>(() => NativeEngine.CreateStartInfo(new(Path.GetFullPath("NorthpassCore.exe"), p)));
    }
}
