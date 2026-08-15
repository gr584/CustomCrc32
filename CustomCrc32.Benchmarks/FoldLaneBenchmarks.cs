using BenchmarkDotNet.Attributes;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// The same fold kernel at one, two, four and eight accumulator lanes, over a buffer that is
/// read repeatedly and so stays resident in cache. That residency is the point: with memory
/// out of the way, what remains is the pipeline, which is what the lane count exists to feed.
/// </summary>
/// <remarks>
/// These numbers are therefore an upper bound rather than a streaming rate — see
/// <see cref="StreamingBenchmarks"/> for the same kernel over data it is meeting for the
/// first time.
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
public class FoldLaneBenchmarks
{
    /// <summary>
    /// Input length in bytes: 4 KiB, 64 KiB, 1 MiB and 16 MiB — L1, L2, L3 and past it. The
    /// buffer is reused every invocation, so only the largest is not served from cache.
    /// </summary>
    [Params(4_096, 65_536, 1_048_576, 16_777_216)]
    public int ByteCount { get; set; }

    private byte[] _data = [];
    private FoldLanes _lanes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _lanes = new FoldLanes(Crc32Parameters.IsoHdlc);

        Random random = new(20250811);
        _data = new byte[ByteCount];
        random.NextBytes(_data);

        // A speed comparison only means anything while the implementations still agree, and
        // the library's own answer is the one the test suite pins.
        uint expected = Crc32.IsoHdlc.ComputeBytes(_data);
        Verify("1 lane", Lanes1(), expected);
        Verify("2 lanes", Lanes2(), expected);
        Verify("4 lanes", Lanes4(), expected);
        Verify("8 lanes", Lanes8(), expected);

        if (!_lanes.ShuffleIsIdentity)
        {
            throw new InvalidOperationException(
                "Expected the reflected engine's byte permutation to be the identity.");
        }

        Verify("4 lanes unshuffled", Lanes4Unshuffled(), expected);
    }

    private void Verify(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{what} disagrees at {ByteCount} bytes: got 0x{actual:X8}, expected 0x{expected:X8}.");
        }
    }

    [Benchmark(Baseline = true, Description = "1 lane")]
    public uint Lanes1() => _lanes.Compute1(_data);

    [Benchmark(Description = "2 lanes")]
    public uint Lanes2() => _lanes.Compute2(_data);

    [Benchmark(Description = "4 lanes (shipping)")]
    public uint Lanes4() => _lanes.Compute4(_data);

    [Benchmark(Description = "8 lanes")]
    public uint Lanes8() => _lanes.Compute8(_data);

    /// <summary>
    /// Four lanes without the identity <c>pshufb</c>. Not a lane-count question at all, but it
    /// belongs beside them: it removes one micro-operation from the same contended port, which
    /// is the clearest evidence of what the four-lane kernel is actually bounded by.
    /// </summary>
    [Benchmark(Description = "4 lanes, shuffle removed")]
    public uint Lanes4Unshuffled() => _lanes.Compute4Unshuffled(_data);
}
