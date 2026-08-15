using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// CRC-32C computed with SSE4.2's dedicated <c>CRC32</c> instruction, in the two shapes worth
/// comparing against the library's fold: one dependency chain, and three independent ones
/// recombined at the end.
/// </summary>
/// <remarks>
/// <para>
/// This is not something the library could adopt wholesale. The instruction is hardwired to
/// the Castagnoli polynomial, so it can only ever produce CRC-32C &mdash; it would accelerate
/// <see cref="Crc32.Castagnoli"/> and none of the other eleven presets, let alone an arbitrary
/// parameter set. It lives here to measure what that special case would be worth.
/// </para>
/// <para>
/// The single-chain form is the obvious way to use the instruction and the one most code
/// reaches for. It is also latency-bound, for exactly the reason the fold interleaves four
/// accumulators: each <c>CRC32</c> waits on the previous result, so the unit idles for most of
/// its latency. The three-chain form splits the input into fixed chunks, runs a chain over
/// each and recombines them, which is what the instruction can actually sustain.
/// </para>
/// <para>
/// Recombination is ordinary CRC linearity: appending <em>n</em> zero bytes to a register is a
/// GF(2) linear operator, so the operator for a whole chunk is built once at construction and
/// applied per block. Correctness is not taken on trust &mdash; both entry points are checked
/// against <see cref="Crc32.ComputeBytes"/> before they are timed.
/// </para>
/// </remarks>
public sealed class Crc32cHardware
{
    /// <summary>Castagnoli, bit-reversed &mdash; the domain a reflected CRC runs in.</summary>
    private const uint ReflectedPolynomial = 0x82F63B78;

    /// <summary>
    /// Bytes per chain in the interleaved form, and a multiple of eight so each chain is a
    /// whole number of 64-bit steps. Recombining costs a fixed amount per block whatever the
    /// chunk size, so this has to be large enough for that cost to disappear against the CRC
    /// instructions it is joining &mdash; at 1 KiB the three chains run 384 steps between
    /// recombinations, which puts it under a fifth of the block.
    /// </summary>
    private const int ChunkBytes = 1024;

    private const int BlockBytes = ChunkBytes * 3;

    /// <summary>Operators for appending one and two chunks' worth of zero bytes.</summary>
    private readonly uint[] _shiftOneChunk;
    private readonly uint[] _shiftTwoChunks;

    public Crc32cHardware()
    {
        _shiftOneChunk = ZeroesOperator(ChunkBytes);
        _shiftTwoChunks = ZeroesOperator(ChunkBytes * 2);
    }

    /// <summary>True where the CRC instruction and its 64-bit form are both available.</summary>
    public static bool IsSupported => Sse42.X64.IsSupported;

    /// <summary>One chain, eight bytes per step. Every step waits on the one before it.</summary>
    public uint ComputeSerial(ReadOnlySpan<byte> data)
    {
        ref byte source = ref MemoryMarshal.GetReference(data);
        ulong crc = 0xFFFFFFFFUL;
        int offset = 0;

        for (; offset + sizeof(ulong) <= data.Length; offset += sizeof(ulong))
        {
            crc = Sse42.X64.Crc32(crc, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset)));
        }

        return FinishTail(ref source, (uint)crc, offset, data.Length);
    }

    /// <summary>
    /// Three chains over adjacent chunks, recombined per block. The chains are independent, so
    /// their latencies overlap the way the fold's four accumulators do.
    /// </summary>
    public uint ComputeInterleaved(ReadOnlySpan<byte> data)
    {
        ref byte source = ref MemoryMarshal.GetReference(data);
        uint crc = 0xFFFFFFFF;
        int offset = 0;

        while (offset + BlockBytes <= data.Length)
        {
            // Only the first chain carries the running register in; the other two start from
            // zero and are shifted into place when the block is recombined.
            ulong first = crc;
            ulong second = 0;
            ulong third = 0;

            for (int i = 0; i < ChunkBytes; i += sizeof(ulong))
            {
                first = Sse42.X64.Crc32(
                    first, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset + i)));
                second = Sse42.X64.Crc32(
                    second, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset + ChunkBytes + i)));
                third = Sse42.X64.Crc32(
                    third, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset + (ChunkBytes * 2) + i)));
            }

            crc = Apply(_shiftTwoChunks, (uint)first) ^ Apply(_shiftOneChunk, (uint)second) ^ (uint)third;
            offset += BlockBytes;
        }

        ulong tail = crc;
        for (; offset + sizeof(ulong) <= data.Length; offset += sizeof(ulong))
        {
            tail = Sse42.X64.Crc32(tail, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset)));
        }

        return FinishTail(ref source, (uint)tail, offset, data.Length);
    }

    /// <summary>Consumes the last few bytes one at a time and applies the final XOR.</summary>
    private static uint FinishTail(ref byte source, uint crc, int offset, int length)
    {
        for (; offset < length; offset++)
        {
            crc = Sse42.Crc32(crc, Unsafe.Add(ref source, offset));
        }

        return crc ^ 0xFFFFFFFF;
    }

    // ------------------------------------------------------- GF(2) operators
    //
    // A register is a 32-bit vector over GF(2) and appending zero bytes is a linear map on it,
    // so the map is a 32x32 matrix held as its columns: mat[i] is the image of bit i. This is
    // the operator zlib's crc32_combine builds, kept as a matrix here rather than applied on
    // the spot, because the same shift is wanted once per block.

    /// <summary>The operator for appending <paramref name="bytes"/> zero bytes.</summary>
    private static uint[] ZeroesOperator(int bytes)
    {
        uint[] odd = new uint[32];
        uint[] even = new uint[32];

        // One zero bit: the polynomial in column 0, and a shift in the rest.
        odd[0] = ReflectedPolynomial;
        uint row = 1;
        for (int n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        Square(even, odd);  // two bits
        Square(odd, even);  // four bits

        // Squaring again gives one whole byte, after which the byte count is consumed bit by
        // bit, composing in whichever powers it names.
        uint[] result = Identity();
        int remaining = bytes;

        while (true)
        {
            Square(even, odd);
            if ((remaining & 1) != 0)
            {
                result = Compose(even, result);
            }

            remaining >>= 1;
            if (remaining == 0)
            {
                break;
            }

            Square(odd, even);
            if ((remaining & 1) != 0)
            {
                result = Compose(odd, result);
            }

            remaining >>= 1;
            if (remaining == 0)
            {
                break;
            }
        }

        return result;
    }

    private static uint[] Identity()
    {
        uint[] matrix = new uint[32];
        for (int i = 0; i < 32; i++)
        {
            matrix[i] = 1u << i;
        }

        return matrix;
    }

    /// <summary>
    /// Applies a matrix to a register: the XOR of the columns its set bits select.
    /// </summary>
    /// <remarks>
    /// Deliberately branchless, and not as a micro-optimisation. Selecting columns with an
    /// <c>if</c> puts thirty-two unpredictable branches on the bits of a CRC value, which
    /// mispredict about half the time and cost more than the whole block of CRC instructions
    /// they are recombining. Worse for a benchmark, the damage scales with input size: over a
    /// small buffer the predictor memorises the handful of values that recur, so the cost only
    /// appears once the input outgrows it, and the shape of the result then says more about
    /// the branch predictor than about the instruction under test.
    /// </remarks>
    private static uint Apply(uint[] matrix, uint vector)
    {
        uint sum = 0;

        for (int i = 0; i < 32; i++, vector >>= 1)
        {
            // 0xFFFFFFFF when the bit is set, 0 when it is not.
            sum ^= matrix[i] & (uint)-(int)(vector & 1);
        }

        return sum;
    }

    private static void Square(uint[] destination, uint[] source)
    {
        for (int i = 0; i < 32; i++)
        {
            destination[i] = Apply(source, source[i]);
        }
    }

    /// <summary>The matrix applying <paramref name="second"/> and then <paramref name="first"/>.</summary>
    private static uint[] Compose(uint[] first, uint[] second)
    {
        uint[] result = new uint[32];

        for (int i = 0; i < 32; i++)
        {
            result[i] = Apply(first, second[i]);
        }

        return result;
    }
}
