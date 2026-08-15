using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// Every case here checksums 1 GiB of input in 1 MiB calls, so the times are directly
/// comparable. The only thing that varies is whether those calls walk 1024 distinct
/// megabytes — each arriving from memory for the first time — or re-read a single resident
/// megabyte 1024 times.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FoldLaneBenchmarks"/> reuses one buffer, which is what isolates the pipeline.
/// This is the other half: what a caller checksumming a real file or stream actually sees,
/// where the fold competes with the cost of fetching the data at all.
/// </para>
/// <para>
/// <see cref="ColdRead"/> is the roofline. It touches every byte with plain loads and four
/// independent XOR chains, so nothing bounds it but how fast one core can pull lines from
/// memory. No kernel can beat it, and how close the folds come says whether compute still
/// matters at this size.
/// </para>
/// <para>
/// This set allocates 1 GiB and takes several minutes. Narrow the run with
/// <c>--filter *FoldLaneBenchmarks*</c> to skip it.
/// </para>
/// </remarks>
public class StreamingBenchmarks
{
    private const int ChunkBytes = 1 << 20;

    /// <summary>Total input per invocation. A single value, but named so the throughput column finds it.</summary>
    [Params(1 << 30)]
    public int ByteCount { get; set; }

    private int Chunks => ByteCount / ChunkBytes;

    private byte[] _data = [];
    private FoldLanes _lanes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _lanes = new FoldLanes(Crc32Parameters.IsoHdlc);
        _data = GC.AllocateUninitializedArray<byte>(ByteCount);

        // Fill every page so no measured run takes a first-touch fault.
        Random random = new(20250811);
        for (int offset = 0; offset < ByteCount; offset += ChunkBytes)
        {
            random.NextBytes(_data.AsSpan(offset, ChunkBytes));
        }

        uint expected = 0;
        for (int i = 0; i < Chunks; i++)
        {
            expected ^= Crc32.IsoHdlc.ComputeBytes(_data.AsSpan(i * ChunkBytes, ChunkBytes));
        }

        Verify("1 lane", Cold1(), expected);
        Verify("2 lanes", Cold2(), expected);
        Verify("4 lanes", Cold4(), expected);
        Verify("8 lanes", Cold8(), expected);
        Verify("4 lanes unshuffled", Cold4Unshuffled(), expected);
    }

    private static void Verify(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{what} disagrees: got 0x{actual:X8}, expected 0x{expected:X8}.");
        }
    }

    [Benchmark(Baseline = true, Description = "cold, 1 lane")]
    public uint Cold1()
    {
        uint sink = 0;
        for (int i = 0; i < Chunks; i++)
        {
            sink ^= _lanes.Compute1(_data.AsSpan(i * ChunkBytes, ChunkBytes));
        }

        return sink;
    }

    [Benchmark(Description = "cold, 2 lanes")]
    public uint Cold2()
    {
        uint sink = 0;
        for (int i = 0; i < Chunks; i++)
        {
            sink ^= _lanes.Compute2(_data.AsSpan(i * ChunkBytes, ChunkBytes));
        }

        return sink;
    }

    [Benchmark(Description = "cold, 4 lanes (shipping)")]
    public uint Cold4()
    {
        uint sink = 0;
        for (int i = 0; i < Chunks; i++)
        {
            sink ^= _lanes.Compute4(_data.AsSpan(i * ChunkBytes, ChunkBytes));
        }

        return sink;
    }

    [Benchmark(Description = "cold, 8 lanes")]
    public uint Cold8()
    {
        uint sink = 0;
        for (int i = 0; i < Chunks; i++)
        {
            sink ^= _lanes.Compute8(_data.AsSpan(i * ChunkBytes, ChunkBytes));
        }

        return sink;
    }

    [Benchmark(Description = "cold, 4 lanes, shuffle removed")]
    public uint Cold4Unshuffled()
    {
        uint sink = 0;
        for (int i = 0; i < Chunks; i++)
        {
            sink ^= _lanes.Compute4Unshuffled(_data.AsSpan(i * ChunkBytes, ChunkBytes));
        }

        return sink;
    }

    /// <summary>The roofline: read every byte, fold nothing.</summary>
    [Benchmark(Description = "cold, pure read (roofline)")]
    public uint ColdRead()
    {
        Vector128<ulong> a = default, b = default, c = default, d = default;
        int blocksPerChunk = ChunkBytes / 16;

        for (int i = 0; i < Chunks; i++)
        {
            ref byte source = ref MemoryMarshal.GetReference(_data.AsSpan(i * ChunkBytes, ChunkBytes));

            for (int block = 0; block + 3 < blocksPerChunk; block += 4)
            {
                a ^= Vector128.LoadUnsafe(ref source, (nuint)(block * 16)).AsUInt64();
                b ^= Vector128.LoadUnsafe(ref source, (nuint)((block + 1) * 16)).AsUInt64();
                c ^= Vector128.LoadUnsafe(ref source, (nuint)((block + 2) * 16)).AsUInt64();
                d ^= Vector128.LoadUnsafe(ref source, (nuint)((block + 3) * 16)).AsUInt64();
            }
        }

        Vector128<ulong> total = (a ^ b) ^ (c ^ d);

        return (uint)(total[0] ^ total[1]);
    }

    /// <summary>
    /// The control: identical call count, chunk size and kernel, but the same resident
    /// megabyte every time. The gap against <see cref="Cold4"/> is the cost of the data
    /// actually having to arrive.
    /// </summary>
    [Benchmark(Description = "hot 1 MiB x1024, 4 lanes")]
    public uint Hot4()
    {
        uint sink = 0;
        for (int i = 0; i < Chunks; i++)
        {
            sink ^= _lanes.Compute4(_data.AsSpan(0, ChunkBytes));
        }

        return sink;
    }
}
