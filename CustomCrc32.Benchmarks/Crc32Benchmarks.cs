using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// Measures <see cref="Crc32.Compute(ReadOnlySpan{uint})"/> against the bit-at-a-time
/// formulation of the same CRC, across input sizes chosen to sit at different levels of
/// the memory hierarchy.
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

        // A speed comparison between two implementations only means anything while they
        // still agree on the answer, so refuse to report numbers if they have diverged.
        uint bitwise = Bitwise();
        uint tableDriven = TableDriven();
        if (bitwise != tableDriven)
        {
            throw new InvalidOperationException(
                $"Implementations disagree at {WordCount} words: bitwise 0x{bitwise:X8}, table-driven 0x{tableDriven:X8}.");
        }

        // The little-endian path must agree with the big-endian one over swapped input.
        uint[] swapped = new uint[_data.Length];
        BinaryPrimitives.ReverseEndianness(_data, swapped);

        uint littleEndian = TableDrivenLittleEndian();
        uint expected = Crc32.Compute(swapped);
        if (littleEndian != expected)
        {
            throw new InvalidOperationException(
                $"Little-endian path disagrees at {WordCount} words: got 0x{littleEndian:X8}, expected 0x{expected:X8}.");
        }
    }

    /// <summary>
    /// The unoptimised formulation, clocking the register one bit at a time. It is the
    /// baseline because it is what the table lookup replaces.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Bitwise")]
    public uint Bitwise()
    {
        uint crc = Crc32.InitialValue;

        foreach (uint word in _data)
        {
            crc ^= word;

            for (int round = 0; round < 32; round++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ Crc32.Polynomial : crc << 1;
            }
        }

        return crc ^ Crc32.XorOut;
    }

    /// <summary>The shipping implementation: one table lookup per byte.</summary>
    [Benchmark(Description = "Table-driven (BE)")]
    public uint TableDriven() => Crc32.Compute(_data);

    /// <summary>
    /// The same, over the little-endian serialisation of the words. The difference against
    /// <see cref="TableDriven"/> is the cost of the per-word byte swap.
    /// </summary>
    [Benchmark(Description = "Table-driven (LE)")]
    public uint TableDrivenLittleEndian() => Crc32.ComputeLittleEndian(_data);
}
