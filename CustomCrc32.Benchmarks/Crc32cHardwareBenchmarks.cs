using BenchmarkDotNet.Attributes;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// The library's general-purpose fold against the CPU's single-purpose CRC instruction, both
/// computing CRC-32C over the same bytes and returning the same answer.
/// </summary>
/// <remarks>
/// <para>
/// This is the one comparison where the library is at a structural disadvantage, so it is
/// worth having: SSE4.2 does in one instruction what the fold does in two carry-less
/// multiplies and a shuffle. The catch is that it does it for exactly one polynomial.
/// </para>
/// <para>
/// The buffer is reused every invocation, so up to 1 MiB this is a pipeline measurement rather
/// than a streaming rate &mdash; the same caveat as
/// <see cref="FoldLaneBenchmarks"/>, and the reason
/// <see cref="StreamingBenchmarks"/> exists.
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
public class Crc32cHardwareBenchmarks
{
    [Params(4_096, 65_536, 1_048_576, 16_777_216)]
    public int ByteCount { get; set; }

    private byte[] _data = [];
    private Crc32cHardware _hardware = null!;

    [GlobalSetup]
    public void Setup()
    {
        if (!Crc32cHardware.IsSupported)
        {
            throw new NotSupportedException(
                "SSE4.2 CRC32 is unavailable on this machine, so there is nothing to compare against.");
        }

        _hardware = new Crc32cHardware();

        Random random = new(20250811);
        _data = new byte[ByteCount];
        random.NextBytes(_data);

        // Both hardware paths must land on the library's answer, which the test suite pins
        // against the published CRC-32C check value.
        uint expected = Crc32.Castagnoli.ComputeBytes(_data);
        Verify("single chain", HardwareSerial(), expected);
        Verify("three chains", HardwareInterleaved(), expected);
    }

    private void Verify(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{what} disagrees at {ByteCount} bytes: got 0x{actual:X8}, expected 0x{expected:X8}.");
        }
    }

    /// <summary>The shipping fold, which computes this for any parameter set.</summary>
    [Benchmark(Baseline = true, Description = "Fold (any polynomial)")]
    public uint Fold() => Crc32.Castagnoli.ComputeBytes(_data);

    /// <summary>The obvious use of the instruction, and a single dependency chain.</summary>
    [Benchmark(Description = "SSE4.2 CRC32, one chain")]
    public uint HardwareSerial() => _hardware.ComputeSerial(_data);

    /// <summary>Three independent chains, recombined per block.</summary>
    [Benchmark(Description = "SSE4.2 CRC32, three chains")]
    public uint HardwareInterleaved() => _hardware.ComputeInterleaved(_data);
}
