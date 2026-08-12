using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CustomCrc32;

/// <summary>
/// A 32-bit CRC over 32-bit words, for any parameter set expressible as
/// <see cref="Crc32Parameters"/>.
/// </summary>
/// <remarks>
/// <para>
/// An instance owns the 256-entry lookup table derived from its parameters, so construct one
/// per parameter set and reuse it. The named presets are shared instances and are the usual
/// entry point: <c>Crc32.Mpeg2.Compute(words)</c>.
/// </para>
/// <para>
/// Instances are immutable and safe to use concurrently.
/// </para>
/// <para>
/// Because the input is typed as <see cref="uint"/> rather than as raw bytes, the host
/// machine's endianness never enters into it. <see cref="Compute(ReadOnlySpan{uint})"/> and
/// <see cref="ComputeLittleEndian(ReadOnlySpan{uint})"/> select the byte order the words are
/// treated as being <em>serialised</em> in, and return the same answer on every platform.
/// </para>
/// </remarks>
public sealed class Crc32
{
    /// <summary>Maps a leading (or trailing, when reflected) byte to eight rounds of register state.</summary>
    private readonly uint[] _table;

    /// <summary>True when the register is held bit-reversed, which is how a reflected CRC is run.</summary>
    private readonly bool _reflectInput;

    /// <summary>
    /// True when the register must be bit-reversed on the way out. The register is already
    /// reversed exactly when <see cref="_reflectInput"/> is set, so this is needed only where
    /// the two reflection settings disagree.
    /// </summary>
    private readonly bool _reverseOutput;

    private readonly uint _xorOut;

    /// <summary>Creates a CRC for the given parameter set, deriving its lookup table.</summary>
    public Crc32(Crc32Parameters parameters)
    {
        Parameters = parameters;
        _reflectInput = parameters.ReflectInput;
        _reverseOutput = parameters.ReflectInput != parameters.ReflectOutput;
        _xorOut = parameters.XorOut;
        _table = BuildTable(parameters.Polynomial, parameters.ReflectInput);

        // A reflected CRC runs with the register held bit-reversed, so the starting value
        // has to be carried into that domain too.
        InitialRegister = parameters.ReflectInput ? Reverse(parameters.InitialValue) : parameters.InitialValue;
    }

    /// <summary>The parameter set this instance implements.</summary>
    public Crc32Parameters Parameters { get; }

    /// <summary>
    /// The register state a streaming computation starts from. Pass this as the first
    /// <c>register</c> argument to <see cref="Append(uint, ReadOnlySpan{uint})"/>.
    /// </summary>
    /// <remarks>
    /// This is the raw register, which for a reflected CRC is the bit-reverse of
    /// <see cref="Crc32Parameters.InitialValue"/>. It is not generally the CRC of empty
    /// input &mdash; that is <c>Finish(InitialRegister)</c>.
    /// </remarks>
    public uint InitialRegister { get; }

    /// <summary>CRC-32/ISO-HDLC: zlib, PNG, gzip, Ethernet, ZIP.</summary>
    public static Crc32 IsoHdlc { get; } = new(Crc32Parameters.IsoHdlc);

    /// <summary>CRC-32/MPEG-2.</summary>
    public static Crc32 Mpeg2 { get; } = new(Crc32Parameters.Mpeg2);

    /// <summary>CRC-32/BZIP2.</summary>
    public static Crc32 Bzip2 { get; } = new(Crc32Parameters.Bzip2);

    /// <summary>CRC-32C, the Castagnoli polynomial.</summary>
    public static Crc32 Castagnoli { get; } = new(Crc32Parameters.Castagnoli);

    /// <summary>CRC-32/JAMCRC.</summary>
    public static Crc32 JamCrc { get; } = new(Crc32Parameters.JamCrc);

    /// <summary>CRC-32/CKSUM, the POSIX <c>cksum</c> polynomial.</summary>
    public static Crc32 Cksum { get; } = new(Crc32Parameters.Cksum);

    /// <summary>CRC-32/AIXM.</summary>
    public static Crc32 Aixm { get; } = new(Crc32Parameters.Aixm);

    /// <summary>CRC-32/AUTOSAR.</summary>
    public static Crc32 Autosar { get; } = new(Crc32Parameters.Autosar);

    /// <summary>CRC-32/BASE91-D.</summary>
    public static Crc32 Base91D { get; } = new(Crc32Parameters.Base91D);

    /// <summary>CRC-32/CD-ROM-EDC.</summary>
    public static Crc32 CdRomEdc { get; } = new(Crc32Parameters.CdRomEdc);

    /// <summary>CRC-32/MEF.</summary>
    public static Crc32 Mef { get; } = new(Crc32Parameters.Mef);

    /// <summary>CRC-32/XFER.</summary>
    public static Crc32 Xfer { get; } = new(Crc32Parameters.Xfer);

    /// <summary>
    /// Computes the CRC of <paramref name="data"/> serialised big-endian &mdash; each word
    /// contributes its most significant byte first, so 0x12345678 is checksummed as the
    /// bytes 12 34 56 78.
    /// </summary>
    public uint Compute(ReadOnlySpan<uint> data) => Finish(Append(InitialRegister, data));

    /// <summary>
    /// Computes the CRC of <paramref name="data"/> serialised little-endian &mdash; each word
    /// contributes its least significant byte first, so 0x12345678 is checksummed as the
    /// bytes 78 56 34 12.
    /// </summary>
    public uint ComputeLittleEndian(ReadOnlySpan<uint> data) =>
        Finish(AppendLittleEndian(InitialRegister, data));

    /// <summary>
    /// Folds a further run of big-endian words into a running register, so a stream can be
    /// checksummed in pieces. Start from <see cref="InitialRegister"/> and pass the result
    /// through <see cref="Finish(uint)"/> when the input is exhausted.
    /// </summary>
    /// <param name="register">The running register state.</param>
    /// <param name="data">The words to fold in, each consumed most significant byte first.</param>
    /// <returns>The updated register state, which is not yet a CRC.</returns>
    public uint Append(uint register, ReadOnlySpan<uint> data)
    {
        // The natural word fold consumes bytes in the direction the engine runs: most
        // significant first when forward, least significant first when reflected. So it is
        // the reflected engine that needs the swap to produce a big-endian reading.
        if (_reflectInput)
        {
            foreach (uint word in data)
            {
                register = FoldReflected(register, BinaryPrimitives.ReverseEndianness(word));
            }
        }
        else
        {
            foreach (uint word in data)
            {
                register = FoldForward(register, word);
            }
        }

        return register;
    }

    /// <summary>
    /// Folds a further run of little-endian words into a running register. The counterpart to
    /// <see cref="Append(uint, ReadOnlySpan{uint})"/>.
    /// </summary>
    /// <param name="register">The running register state.</param>
    /// <param name="data">The words to fold in, each consumed least significant byte first.</param>
    /// <returns>The updated register state, which is not yet a CRC.</returns>
    public uint AppendLittleEndian(uint register, ReadOnlySpan<uint> data)
    {
        if (_reflectInput)
        {
            foreach (uint word in data)
            {
                register = FoldReflected(register, word);
            }
        }
        else
        {
            foreach (uint word in data)
            {
                register = FoldForward(register, BinaryPrimitives.ReverseEndianness(word));
            }
        }

        return register;
    }

    /// <summary>
    /// Turns a register state into the final CRC by applying output reflection and the
    /// final XOR.
    /// </summary>
    public uint Finish(uint register) => (_reverseOutput ? Reverse(register) : register) ^ _xorOut;

    /// <summary>
    /// Folds one word into a forward (most-significant-bit-first) register: XOR the whole
    /// word in, then clock the register through the 32 rounds it owes, a byte per lookup.
    /// Folding first and shifting afterwards is what makes this equivalent to feeding the
    /// word's four bytes in descending significance.
    /// </summary>
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

    /// <summary>
    /// The mirror image of <see cref="FoldForward"/> for a reflected register, which is held
    /// bit-reversed and therefore clocked towards the least significant end.
    /// </summary>
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

    private static uint[] BuildTable(uint polynomial, bool reflected)
    {
        uint[] table = new uint[256];

        if (reflected)
        {
            // A reflected engine works in the bit-reversed domain throughout, so the
            // polynomial is reversed once here rather than per round.
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

    /// <summary>Reverses the order of the 32 bits in <paramref name="value"/>.</summary>
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
