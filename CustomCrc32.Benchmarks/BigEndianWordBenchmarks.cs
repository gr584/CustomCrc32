#if NET10_0_OR_GREATER

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using HashingCrc32 = System.IO.Hashing.Crc32;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// The library's own reason to exist, measured against the nearest thing
/// <c>System.IO.Hashing</c> can do: checksumming words held as <c>uint</c> in the byte order a
/// big-endian machine would have written them, from a little-endian host.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SystemIoHashingBenchmarks"/> compares the two over bytes, where they are level.
/// This is the case where they are not comparable at all, because the package has no word entry
/// point and no byte-order option &mdash; every input it accepts is a
/// <c>ReadOnlySpan&lt;byte&gt;</c>, a <c>byte[]</c> or a <c>Stream</c>, and its parameter set
/// varies polynomial, initial value, final XOR and reflection, none of which is byte order. So
/// the caller has to produce the big-endian byte stream, and the question is only what that
/// costs.
/// </para>
/// <para>
/// <see cref="HashingNoSwap"/> is the control, and it computes the wrong CRC on purpose. It
/// reads the words exactly as the host laid them out, which is the little-endian
/// serialisation, so it answers <see cref="Crc32.ComputeLittleEndian"/> instead. It is here
/// because it is the speed the package would run at if the byte order happened to be right:
/// the gap between it and everything below it is the swap and nothing else.
/// </para>
/// <para>
/// The three honest routes are ordered by how much memory they need beyond the input.
/// <see cref="HashingSwapToBuffer"/> takes a second buffer the size of the input, allocated
/// once in setup rather than per invocation, which is the friendliest possible framing.
/// <see cref="HashingSwapChunked"/> holds the extra memory to a fixed 8 KiB and streams
/// through it. <see cref="HashingSwapInPlace"/> needs nothing extra at all, by swapping the
/// caller's own array, hashing, and swapping it back &mdash; correct in the single-threaded
/// case measured here, and unusable if anything else can read the array meanwhile.
/// </para>
/// <para>
/// The buffer is reused every invocation, so up to 1 MiB this is a pipeline measurement rather
/// than a streaming rate. That framing flatters the copying routes: their extra traffic lands
/// in cache at the smaller sizes and has to reach memory at 16 MiB.
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
public class BigEndianWordBenchmarks
{
    /// <summary>Scratch large enough to keep the fold in its stride, small enough to stay in L1.</summary>
    private const int ChunkWords = 2_048;

    [Params(1_024, 16_384, 262_144, 4_194_304)]
    public int WordCount { get; set; }

    private uint[] _words = [];
    private uint[] _swapped = [];
    private uint[] _chunk = [];
    private HashingCrc32 _incremental = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(20250811);
        _words = new uint[WordCount];
        for (int i = 0; i < WordCount; i++)
        {
            _words[i] = (uint)random.NextInt64();
        }

        _swapped = new uint[WordCount];
        _chunk = new uint[ChunkWords];
        _incremental = new HashingCrc32();

        // Three routes to one answer, and a fourth that deliberately reaches a different one.
        uint expected = FoldBigEndian();
        Verify("swap into a second buffer", HashingSwapToBuffer(), expected);
        Verify("swap through 8 KiB of scratch", HashingSwapChunked(), expected);
        Verify("swap in place and back", HashingSwapInPlace(), expected);

        uint control = HashingNoSwap();
        if (control != Crc32.IsoHdlc.ComputeLittleEndian(_words))
        {
            throw new InvalidOperationException(
                "The control is meant to be the little-endian reading of the same words; it is not.");
        }

        if (control == expected)
        {
            throw new InvalidOperationException(
                "The control agreed with the big-endian answer, so this input cannot tell the " +
                "two serialisations apart and measures nothing.");
        }
    }

    private void Verify(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{what} disagrees at {WordCount} words: got 0x{actual:X8}, expected 0x{expected:X8}.");
        }
    }

    [Benchmark(Baseline = true, Description = "ComputeBigEndian")]
    public uint FoldBigEndian() => Crc32.IsoHdlc.ComputeBigEndian(_words);

    /// <summary>Swap the whole input into a second buffer, then hash it in one call.</summary>
    [Benchmark(Description = "System.IO.Hashing, swapped into a second buffer")]
    public uint HashingSwapToBuffer()
    {
        BinaryPrimitives.ReverseEndianness(_words, _swapped);

        return HashingCrc32.HashToUInt32(MemoryMarshal.AsBytes<uint>(_swapped));
    }

    /// <summary>Swap through a fixed 8 KiB of scratch, appending each piece as it is produced.</summary>
    [Benchmark(Description = "System.IO.Hashing, swapped through 8 KiB of scratch")]
    public uint HashingSwapChunked()
    {
        _incremental.Reset();

        ReadOnlySpan<uint> remaining = _words;
        while (!remaining.IsEmpty)
        {
            int take = Math.Min(remaining.Length, ChunkWords);
            BinaryPrimitives.ReverseEndianness(remaining[..take], _chunk);
            _incremental.Append(MemoryMarshal.AsBytes(_chunk.AsSpan(0, take)));
            remaining = remaining[take..];
        }

        return _incremental.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Swap the caller's own array, hash it, and swap it back. No buffer beyond the input, at
    /// the price of the input not being a <c>ReadOnlySpan</c>, not being shared, and not being
    /// readable by anything else while this runs.
    /// </summary>
    [Benchmark(Description = "System.IO.Hashing, swapped in place and back")]
    public uint HashingSwapInPlace()
    {
        BinaryPrimitives.ReverseEndianness(_words, _words);
        uint crc = HashingCrc32.HashToUInt32(MemoryMarshal.AsBytes<uint>(_words));
        BinaryPrimitives.ReverseEndianness(_words, _words);

        return crc;
    }

    /// <summary>
    /// The control. Hashes the host's bytes as they lie, which is the little-endian
    /// serialisation, so the value is not the big-endian CRC the others compute.
    /// </summary>
    [Benchmark(Description = "System.IO.Hashing, no swap (computes the little-endian CRC)")]
    public uint HashingNoSwap() => HashingCrc32.HashToUInt32(MemoryMarshal.AsBytes<uint>(_words));
}

#endif
