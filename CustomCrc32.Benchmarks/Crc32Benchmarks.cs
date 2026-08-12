using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// Measures the shipping implementation against the two formulations it replaced: one bit at
/// a time, and one table lookup per byte. The table baselines are reimplemented here rather
/// than called through the library, because the library now folds automatically and there is
/// no supported way to ask it not to.
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
    private uint[] _forwardTable = [];
    private uint[] _reflectedTable = [];

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(20250811);

        _data = new uint[WordCount];
        for (int i = 0; i < _data.Length; i++)
        {
            _data[i] = (uint)random.NextInt64(uint.MinValue, (long)uint.MaxValue + 1);
        }

        _forwardTable = BuildTable(Crc32Parameters.Mpeg2.Polynomial, reflected: false);
        _reflectedTable = BuildTable(Crc32Parameters.IsoHdlc.Polynomial, reflected: true);

        // A speed comparison only means anything while the implementations still agree.
        Verify("forward table vs folded", TableForward(), FoldedForward());
        Verify("reflected table vs folded", TableReflected(), FoldedReflectedLittleEndian());
        Verify("forward bitwise vs folded", Bitwise(), FoldedForward());

        uint[] swapped = new uint[_data.Length];
        BinaryPrimitives.ReverseEndianness(_data, swapped);
        Verify("forward byte-order identity", FoldedForwardLittleEndian(), Crc32.Mpeg2.Compute(swapped));
        Verify("reflected byte-order identity", FoldedReflectedLittleEndian(), Crc32.IsoHdlc.Compute(swapped));
    }

    private void Verify(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{what} disagrees at {WordCount} words: got 0x{actual:X8}, expected 0x{expected:X8}.");
        }
    }

    private static uint[] BuildTable(uint polynomial, bool reflected)
    {
        uint[] table = new uint[256];
        uint reversed = 0;
        for (int i = 0; i < 32; i++)
        {
            reversed = (reversed << 1) | ((polynomial >> i) & 1);
        }

        for (uint index = 0; index < 256; index++)
        {
            uint entry = reflected ? index : index << 24;
            for (int round = 0; round < 8; round++)
            {
                entry = reflected
                    ? ((entry & 1) != 0 ? (entry >> 1) ^ reversed : entry >> 1)
                    : ((entry & 0x80000000) != 0 ? (entry << 1) ^ polynomial : entry << 1);
            }

            table[index] = entry;
        }

        return table;
    }

    /// <summary>The unoptimised formulation, clocking the register one bit at a time.</summary>
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

    /// <summary>One table lookup per byte, forward engine — what the library did before folding.</summary>
    [Benchmark(Description = "Table forward")]
    public uint TableForward()
    {
        uint crc = Crc32Parameters.Mpeg2.InitialValue;

        foreach (uint word in _data)
        {
            crc ^= word;
            crc = (crc << 8) ^ _forwardTable[crc >> 24];
            crc = (crc << 8) ^ _forwardTable[crc >> 24];
            crc = (crc << 8) ^ _forwardTable[crc >> 24];
            crc = (crc << 8) ^ _forwardTable[crc >> 24];
        }

        return crc ^ Crc32Parameters.Mpeg2.XorOut;
    }

    /// <summary>One table lookup per byte, reflected engine.</summary>
    [Benchmark(Description = "Table reflected")]
    public uint TableReflected()
    {
        uint crc = Crc32Parameters.IsoHdlc.InitialValue;

        foreach (uint word in _data)
        {
            crc ^= word;
            crc = (crc >> 8) ^ _reflectedTable[crc & 0xFF];
            crc = (crc >> 8) ^ _reflectedTable[crc & 0xFF];
            crc = (crc >> 8) ^ _reflectedTable[crc & 0xFF];
            crc = (crc >> 8) ^ _reflectedTable[crc & 0xFF];
        }

        return crc ^ Crc32Parameters.IsoHdlc.XorOut;
    }

    [Benchmark(Description = "Folded forward BE")]
    public uint FoldedForward() => Crc32.Mpeg2.Compute(_data);

    [Benchmark(Description = "Folded forward LE")]
    public uint FoldedForwardLittleEndian() => Crc32.Mpeg2.ComputeLittleEndian(_data);

    [Benchmark(Description = "Folded reflected BE")]
    public uint FoldedReflected() => Crc32.IsoHdlc.Compute(_data);

    [Benchmark(Description = "Folded reflected LE")]
    public uint FoldedReflectedLittleEndian() => Crc32.IsoHdlc.ComputeLittleEndian(_data);
}
