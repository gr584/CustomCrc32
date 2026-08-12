using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// Measures the table-driven engines against the bit-at-a-time formulation of the same CRC,
/// across input sizes chosen to sit at different levels of the memory hierarchy.
/// </summary>
public class Crc32Benchmarks
{
    /// <summary>
    /// Input length in 32-bit words: 64 B, 1 KiB, 16 KiB, 256 KiB and 1 MiB respectively.
    /// The small end shows per-call overhead, the large end shows steady-state throughput
    /// once the input no longer fits in cache.
    /// </summary>
    [Params(16, 256, 4_096, 65_536, 262_144)]
    public int WordCount { get; set; }

    private uint[] _data = [];

    [GlobalSetup]
    public void Setup()
    {
        // Fixed seed so every run measures the same bytes. The data is random because a
        // CRC's cost is data-independent but its table lookups are not obviously so, and
        // degenerate input (all zeroes) would keep one table entry permanently hot.
        Random random = new(20250811);

        _data = new uint[WordCount];
        for (int i = 0; i < _data.Length; i++)
        {
            _data[i] = (uint)random.NextInt64(uint.MinValue, (long)uint.MaxValue + 1);
        }

        // A speed comparison between implementations only means anything while they still
        // agree on the answer, so refuse to report numbers if they have diverged.
        Verify("forward table vs bitwise", Forward(), Bitwise());

        uint[] swapped = new uint[_data.Length];
        BinaryPrimitives.ReverseEndianness(_data, swapped);
        Verify("little-endian identity", ForwardLittleEndian(), Crc32.Mpeg2.Compute(swapped));
        Verify("reflected little-endian identity", ReflectedLittleEndian(), Crc32.IsoHdlc.Compute(swapped));
    }

    private void Verify(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{what} disagrees at {WordCount} words: got 0x{actual:X8}, expected 0x{expected:X8}.");
        }
    }

    /// <summary>
    /// The unoptimised formulation, clocking the register one bit at a time. It is the
    /// baseline because it is what the table lookup replaces.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Bitwise")]
    public uint Bitwise()
    {
        Crc32Parameters parameters = Crc32Parameters.Mpeg2;
        uint crc = parameters.InitialValue;

        foreach (uint word in _data)
        {
            crc ^= word;

            for (int round = 0; round < 32; round++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ parameters.Polynomial : crc << 1;
            }
        }

        return crc ^ parameters.XorOut;
    }

    /// <summary>The forward (non-reflected) engine: one table lookup per byte.</summary>
    [Benchmark(Description = "Forward BE")]
    public uint Forward() => Crc32.Mpeg2.Compute(_data);

    /// <summary>The forward engine with the per-word byte swap.</summary>
    [Benchmark(Description = "Forward LE")]
    public uint ForwardLittleEndian() => Crc32.Mpeg2.ComputeLittleEndian(_data);

    /// <summary>
    /// The reflected engine, which clocks the register the other way. The natural fold
    /// direction for a reflected CRC is little-endian, so this variant carries the byte swap.
    /// </summary>
    [Benchmark(Description = "Reflected BE")]
    public uint Reflected() => Crc32.IsoHdlc.Compute(_data);

    /// <summary>The reflected engine folding in its natural direction, with no swap.</summary>
    [Benchmark(Description = "Reflected LE")]
    public uint ReflectedLittleEndian() => Crc32.IsoHdlc.ComputeLittleEndian(_data);
}
