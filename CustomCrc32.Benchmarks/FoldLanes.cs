using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// The library's fold kernel with the number of independent accumulator lanes made
/// configurable. <see cref="Compute4"/> is a transcription of the shipping
/// <c>Crc32.FoldBlocks</c>; one, two and eight lanes are that same kernel with the
/// interleaving removed or widened, so a difference between them is a difference in
/// dependency-chain structure and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Reimplemented here rather than called through the library for the same reason the table
/// baselines in <see cref="Crc32Benchmarks"/> are: the library folds by four and offers no
/// supported way to ask for anything else.
/// </para>
/// <para>
/// Nothing in this file is a proposed improvement. It exists to measure <em>why</em> four is
/// the number, and every variant is checked against <see cref="Crc32.ComputeBytes"/> — the
/// answer the test suite pins — before it is timed.
/// </para>
/// </remarks>
public sealed class FoldLanes
{
    private const int BytesPerBlock = 16;

    private readonly uint[] _table;
    private readonly bool _reflectInput;
    private readonly bool _reverseOutput;
    private readonly uint _xorOut;
    private readonly uint _initialRegister;

    /// <summary>
    /// Constants for folding one, two, four and eight blocks ahead. A lane advances by as many
    /// blocks as there are lanes, so each lane count needs its own.
    /// </summary>
    private readonly Vector128<ulong> _foldBy1;
    private readonly Vector128<ulong> _foldBy2;
    private readonly Vector128<ulong> _foldBy4;
    private readonly Vector128<ulong> _foldBy8;

    private readonly Vector128<byte> _shuffle;

    public FoldLanes(Crc32Parameters parameters)
    {
        _reflectInput = parameters.ReflectInput;
        _reverseOutput = parameters.ReflectInput != parameters.ReflectOutput;
        _xorOut = parameters.XorOut;
        _table = BuildTable(parameters.Polynomial, parameters.ReflectInput);
        _initialRegister = parameters.ReflectInput ? Reverse(parameters.InitialValue) : parameters.InitialValue;

        _foldBy1 = FoldConstants(128, parameters.Polynomial, parameters.ReflectInput);
        _foldBy2 = FoldConstants(256, parameters.Polynomial, parameters.ReflectInput);
        _foldBy4 = FoldConstants(512, parameters.Polynomial, parameters.ReflectInput);
        _foldBy8 = FoldConstants(1024, parameters.Polynomial, parameters.ReflectInput);

        // Bytes arrive already in message order, which is what the little-endian permutation
        // produces — the same choice AppendBytes makes, and the same reason.
        _shuffle = BlockShuffle(parameters.ReflectInput, bigEndian: false);
    }

    public static bool IsSupported => Pclmulqdq.IsSupported && Ssse3.IsSupported;

    /// <summary>
    /// True when this engine's block permutation moves nothing, which is the case for a
    /// reflected engine reading bytes. <see cref="Compute4Unshuffled"/> is only correct then.
    /// </summary>
    public bool ShuffleIsIdentity =>
        _shuffle == Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

    // Each entry point takes a whole number of blocks, so no tail handling is timed.

    public uint Compute1(ReadOnlySpan<byte> data) => Finish(FoldBlocks1(_initialRegister, data));

    public uint Compute2(ReadOnlySpan<byte> data) => Finish(FoldBlocks2(_initialRegister, data));

    public uint Compute4(ReadOnlySpan<byte> data) => Finish(FoldBlocks4(_initialRegister, data));

    public uint Compute8(ReadOnlySpan<byte> data) => Finish(FoldBlocks8(_initialRegister, data));

    public uint Compute4Unshuffled(ReadOnlySpan<byte> data) => Finish(FoldBlocks4Unshuffled(_initialRegister, data));

    private uint Finish(uint register) => (_reverseOutput ? Reverse(register) : register) ^ _xorOut;

    private Vector128<ulong> Seed(uint register) => _reflectInput
        ? Vector128.Create((ulong)register, 0UL)
        : Vector128.Create(0UL, (ulong)register << 32);

    /// <summary>
    /// One accumulator, so every fold waits on the one before it and the multiplier sits idle
    /// for most of its latency. This is the shape the interleaving exists to avoid.
    /// </summary>
    private uint FoldBlocks1(uint register, ReadOnlySpan<byte> data)
    {
        ref byte source = ref MemoryMarshal.GetReference(data);
        int blocks = data.Length / BytesPerBlock;

        Vector128<ulong> accumulator = LoadBlock(ref source, 0, _shuffle) ^ Seed(register);

        for (int next = 1; next < blocks; next++)
        {
            accumulator = FoldBlock(accumulator, _foldBy1, LoadBlock(ref source, next, _shuffle));
        }

        return ReduceAccumulator(accumulator);
    }

    private uint FoldBlocks2(uint register, ReadOnlySpan<byte> data)
    {
        ref byte source = ref MemoryMarshal.GetReference(data);
        int blocks = data.Length / BytesPerBlock;

        Vector128<ulong> accumulator = LoadBlock(ref source, 0, _shuffle) ^ Seed(register);
        int next = 1;

        if (blocks >= 2)
        {
            Vector128<ulong> second = LoadBlock(ref source, 1, _shuffle);

            for (next = 2; next + 1 < blocks; next += 2)
            {
                accumulator = FoldBlock(accumulator, _foldBy2, LoadBlock(ref source, next, _shuffle));
                second = FoldBlock(second, _foldBy2, LoadBlock(ref source, next + 1, _shuffle));
            }

            accumulator = FoldBlock(accumulator, _foldBy1, second);
        }

        for (; next < blocks; next++)
        {
            accumulator = FoldBlock(accumulator, _foldBy1, LoadBlock(ref source, next, _shuffle));
        }

        return ReduceAccumulator(accumulator);
    }

    /// <summary>What the library ships.</summary>
    private uint FoldBlocks4(uint register, ReadOnlySpan<byte> data)
    {
        ref byte source = ref MemoryMarshal.GetReference(data);
        int blocks = data.Length / BytesPerBlock;

        Vector128<ulong> accumulator = LoadBlock(ref source, 0, _shuffle) ^ Seed(register);
        int next = 1;

        if (blocks >= 4)
        {
            Vector128<ulong> second = LoadBlock(ref source, 1, _shuffle);
            Vector128<ulong> third = LoadBlock(ref source, 2, _shuffle);
            Vector128<ulong> fourth = LoadBlock(ref source, 3, _shuffle);

            for (next = 4; next + 3 < blocks; next += 4)
            {
                accumulator = FoldBlock(accumulator, _foldBy4, LoadBlock(ref source, next, _shuffle));
                second = FoldBlock(second, _foldBy4, LoadBlock(ref source, next + 1, _shuffle));
                third = FoldBlock(third, _foldBy4, LoadBlock(ref source, next + 2, _shuffle));
                fourth = FoldBlock(fourth, _foldBy4, LoadBlock(ref source, next + 3, _shuffle));
            }

            accumulator = FoldBlock(accumulator, _foldBy1, second);
            accumulator = FoldBlock(accumulator, _foldBy1, third);
            accumulator = FoldBlock(accumulator, _foldBy1, fourth);
        }

        for (; next < blocks; next++)
        {
            accumulator = FoldBlock(accumulator, _foldBy1, LoadBlock(ref source, next, _shuffle));
        }

        return ReduceAccumulator(accumulator);
    }

    /// <summary>
    /// Eight lanes. Past the point where the multiplier port saturates, so this measures what
    /// widening further does — nothing, once the bottleneck has moved.
    /// </summary>
    private uint FoldBlocks8(uint register, ReadOnlySpan<byte> data)
    {
        ref byte source = ref MemoryMarshal.GetReference(data);
        int blocks = data.Length / BytesPerBlock;

        Vector128<ulong> lane0 = LoadBlock(ref source, 0, _shuffle) ^ Seed(register);
        int next = 1;

        if (blocks >= 8)
        {
            Vector128<ulong> lane1 = LoadBlock(ref source, 1, _shuffle);
            Vector128<ulong> lane2 = LoadBlock(ref source, 2, _shuffle);
            Vector128<ulong> lane3 = LoadBlock(ref source, 3, _shuffle);
            Vector128<ulong> lane4 = LoadBlock(ref source, 4, _shuffle);
            Vector128<ulong> lane5 = LoadBlock(ref source, 5, _shuffle);
            Vector128<ulong> lane6 = LoadBlock(ref source, 6, _shuffle);
            Vector128<ulong> lane7 = LoadBlock(ref source, 7, _shuffle);

            for (next = 8; next + 7 < blocks; next += 8)
            {
                lane0 = FoldBlock(lane0, _foldBy8, LoadBlock(ref source, next, _shuffle));
                lane1 = FoldBlock(lane1, _foldBy8, LoadBlock(ref source, next + 1, _shuffle));
                lane2 = FoldBlock(lane2, _foldBy8, LoadBlock(ref source, next + 2, _shuffle));
                lane3 = FoldBlock(lane3, _foldBy8, LoadBlock(ref source, next + 3, _shuffle));
                lane4 = FoldBlock(lane4, _foldBy8, LoadBlock(ref source, next + 4, _shuffle));
                lane5 = FoldBlock(lane5, _foldBy8, LoadBlock(ref source, next + 5, _shuffle));
                lane6 = FoldBlock(lane6, _foldBy8, LoadBlock(ref source, next + 6, _shuffle));
                lane7 = FoldBlock(lane7, _foldBy8, LoadBlock(ref source, next + 7, _shuffle));
            }

            lane0 = FoldBlock(lane0, _foldBy1, lane1);
            lane0 = FoldBlock(lane0, _foldBy1, lane2);
            lane0 = FoldBlock(lane0, _foldBy1, lane3);
            lane0 = FoldBlock(lane0, _foldBy1, lane4);
            lane0 = FoldBlock(lane0, _foldBy1, lane5);
            lane0 = FoldBlock(lane0, _foldBy1, lane6);
            lane0 = FoldBlock(lane0, _foldBy1, lane7);
        }

        for (; next < blocks; next++)
        {
            lane0 = FoldBlock(lane0, _foldBy1, LoadBlock(ref source, next, _shuffle));
        }

        return ReduceAccumulator(lane0);
    }

    /// <summary>
    /// Four lanes with the block permutation dropped entirely. Correct only where
    /// <see cref="ShuffleIsIdentity"/> holds, which is the reflected engine reading bytes.
    /// Isolates what the <c>pshufb</c> costs when it is permuting nothing.
    /// </summary>
    private uint FoldBlocks4Unshuffled(uint register, ReadOnlySpan<byte> data)
    {
        ref byte source = ref MemoryMarshal.GetReference(data);
        int blocks = data.Length / BytesPerBlock;

        Vector128<ulong> accumulator = LoadBlockRaw(ref source, 0) ^ Seed(register);
        int next = 1;

        if (blocks >= 4)
        {
            Vector128<ulong> second = LoadBlockRaw(ref source, 1);
            Vector128<ulong> third = LoadBlockRaw(ref source, 2);
            Vector128<ulong> fourth = LoadBlockRaw(ref source, 3);

            for (next = 4; next + 3 < blocks; next += 4)
            {
                accumulator = FoldBlock(accumulator, _foldBy4, LoadBlockRaw(ref source, next));
                second = FoldBlock(second, _foldBy4, LoadBlockRaw(ref source, next + 1));
                third = FoldBlock(third, _foldBy4, LoadBlockRaw(ref source, next + 2));
                fourth = FoldBlock(fourth, _foldBy4, LoadBlockRaw(ref source, next + 3));
            }

            accumulator = FoldBlock(accumulator, _foldBy1, second);
            accumulator = FoldBlock(accumulator, _foldBy1, third);
            accumulator = FoldBlock(accumulator, _foldBy1, fourth);
        }

        for (; next < blocks; next++)
        {
            accumulator = FoldBlock(accumulator, _foldBy1, LoadBlockRaw(ref source, next));
        }

        return ReduceAccumulator(accumulator);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> LoadBlock(ref byte source, int block, Vector128<byte> shuffle) =>
        Ssse3.Shuffle(
            Vector128.LoadUnsafe(ref source, (nuint)(block * BytesPerBlock)),
            shuffle).AsUInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> LoadBlockRaw(ref byte source, int block) =>
        Vector128.LoadUnsafe(ref source, (nuint)(block * BytesPerBlock)).AsUInt64();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> FoldBlock(Vector128<ulong> accumulator, Vector128<ulong> constants, Vector128<ulong> next) =>
        Pclmulqdq.CarrylessMultiply(accumulator, constants, 0x00)
        ^ Pclmulqdq.CarrylessMultiply(accumulator, constants, 0x11)
        ^ next;

    private uint ReduceAccumulator(Vector128<ulong> accumulator)
    {
        Vector128<uint> words = accumulator.AsUInt32();
        uint register = 0;

        if (_reflectInput)
        {
            register = FoldReflected(register, words[0]);
            register = FoldReflected(register, words[1]);
            register = FoldReflected(register, words[2]);
            register = FoldReflected(register, words[3]);
        }
        else
        {
            register = FoldForward(register, words[3]);
            register = FoldForward(register, words[2]);
            register = FoldForward(register, words[1]);
            register = FoldForward(register, words[0]);
        }

        return register;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint FoldForward(uint register, uint word)
    {
        register ^= word;
        register = (register << 8) ^ _table[register >> 24];
        register = (register << 8) ^ _table[register >> 24];
        register = (register << 8) ^ _table[register >> 24];
        register = (register << 8) ^ _table[register >> 24];

        return register;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint FoldReflected(uint register, uint word)
    {
        register ^= word;
        register = (register >> 8) ^ _table[register & 0xFF];
        register = (register >> 8) ^ _table[register & 0xFF];
        register = (register >> 8) ^ _table[register & 0xFF];
        register = (register >> 8) ^ _table[register & 0xFF];

        return register;
    }

    private static Vector128<ulong> FoldConstants(int bitsAhead, uint polynomial, bool reflected) => reflected
        ? Vector128.Create(
            (ulong)Reverse(PowerOfX(bitsAhead + 32, polynomial)) << 1,
            (ulong)Reverse(PowerOfX(bitsAhead - 32, polynomial)) << 1)
        : Vector128.Create(
            (ulong)PowerOfX(bitsAhead, polynomial),
            PowerOfX(bitsAhead + 64, polynomial));

    private static uint PowerOfX(int exponent, uint polynomial)
    {
        uint value = 1;

        for (int i = 0; i < exponent; i++)
        {
            value = (value & 0x80000000) != 0 ? (value << 1) ^ polynomial : value << 1;
        }

        return value;
    }

    private static Vector128<byte> BlockShuffle(bool reflected, bool bigEndian) => (reflected, bigEndian) switch
    {
        (false, true) => Vector128.Create((byte)12, 13, 14, 15, 8, 9, 10, 11, 4, 5, 6, 7, 0, 1, 2, 3),
        (false, false) => Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0),
        (true, false) => Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
        (true, true) => Vector128.Create((byte)3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12),
    };

    private static uint[] BuildTable(uint polynomial, bool reflected)
    {
        uint[] table = new uint[256];

        if (reflected)
        {
            uint reversedPolynomial = Reverse(polynomial);

            for (uint index = 0; index < 256; index++)
            {
                uint entry = index;

                for (int round = 0; round < 8; round++)
                {
                    entry = (entry & 1) != 0 ? (entry >> 1) ^ reversedPolynomial : entry >> 1;
                }

                table[index] = entry;
            }
        }
        else
        {
            for (uint index = 0; index < 256; index++)
            {
                uint entry = index << 24;

                for (int round = 0; round < 8; round++)
                {
                    entry = (entry & 0x80000000) != 0 ? (entry << 1) ^ polynomial : entry << 1;
                }

                table[index] = entry;
            }
        }

        return table;
    }

    private static uint Reverse(uint value)
    {
        value = (value >> 16) | (value << 16);
        value = ((value & 0xFF00FF00) >> 8) | ((value & 0x00FF00FF) << 8);
        value = ((value & 0xF0F0F0F0) >> 4) | ((value & 0x0F0F0F0F) << 4);
        value = ((value & 0xCCCCCCCC) >> 2) | ((value & 0x33333333) << 2);
        value = ((value & 0xAAAAAAAA) >> 1) | ((value & 0x55555555) << 1);

        return value;
    }
}
