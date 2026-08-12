using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CustomCrc32;

/// <summary>
/// CRC-32 over 32-bit words, computed most-significant-bit-first with polynomial
/// 0x04C11DB7, initial value 0xFFFFFFFF and no final XOR. Input is neither reflected
/// on the way in nor on the way out, so each word contributes its most significant
/// byte first &mdash; the big-endian ordering.
/// </summary>
/// <remarks>
/// These parameters are the ones catalogued as CRC-32/MPEG-2, whose check value
/// (the CRC of the ASCII bytes "123456789") is 0x0376E6E7.
/// <para>
/// Because the input is typed as <see cref="uint"/> rather than as raw bytes, the
/// host machine's endianness never enters into it: the algorithm consumes each word
/// from bit 31 down to bit 0 regardless of how that word is laid out in memory.
/// </para>
/// </remarks>
public static class Crc32
{
    /// <summary>The generator polynomial, in normal (non-reversed) form.</summary>
    public const uint Polynomial = 0x04C11DB7;

    /// <summary>The register's starting value, and therefore the CRC of an empty input.</summary>
    public const uint InitialValue = 0xFFFFFFFF;

    /// <summary>The value XOR-ed into the register to produce the final CRC.</summary>
    public const uint XorOut = 0x00000000;

    /// <summary>
    /// Maps a leading byte to the register state produced by clocking it through
    /// eight rounds, so a whole byte can be folded in with one lookup.
    /// </summary>
    private static readonly uint[] Table = BuildTable();

    /// <summary>
    /// Computes the CRC of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The words to checksum, each consumed most significant byte first.</param>
    /// <returns>The CRC, or <see cref="InitialValue"/> when <paramref name="data"/> is empty.</returns>
    public static uint Compute(ReadOnlySpan<uint> data) => Compute(data, InitialValue);

    /// <summary>
    /// Continues a CRC over a further run of words, so a stream can be checksummed in
    /// pieces. Passing the result of one call as the <paramref name="seed"/> of the next
    /// yields the same answer as a single call over the concatenated input.
    /// </summary>
    /// <param name="data">The words to checksum, each consumed most significant byte first.</param>
    /// <param name="seed">The register state to resume from, normally a previous return value.</param>
    /// <returns>The CRC of everything folded in so far.</returns>
    public static uint Compute(ReadOnlySpan<uint> data, uint seed)
    {
        uint crc = seed;

        foreach (uint word in data)
        {
            crc = Fold(crc, word);
        }

        return crc ^ XorOut;
    }

    /// <summary>
    /// Computes the CRC of the <em>little-endian</em> serialisation of
    /// <paramref name="data"/>: each word contributes its least significant byte first,
    /// so 0x12345678 is checksummed as the bytes 78 56 34 12.
    /// </summary>
    /// <remarks>
    /// Use this when the words are values you intend to write out least significant byte
    /// first. It is exactly <see cref="Compute(ReadOnlySpan{uint})"/> over the same words
    /// byte-swapped, but performs the swap a word at a time rather than materialising a
    /// reversed copy of the input.
    /// </remarks>
    /// <param name="data">The words to checksum, each consumed least significant byte first.</param>
    /// <returns>The CRC, or <see cref="InitialValue"/> when <paramref name="data"/> is empty.</returns>
    public static uint ComputeLittleEndian(ReadOnlySpan<uint> data) =>
        ComputeLittleEndian(data, InitialValue);

    /// <summary>
    /// Continues a little-endian CRC over a further run of words. The counterpart to
    /// <see cref="Compute(ReadOnlySpan{uint}, uint)"/>, and chains the same way.
    /// </summary>
    /// <param name="data">The words to checksum, each consumed least significant byte first.</param>
    /// <param name="seed">The register state to resume from, normally a previous return value.</param>
    /// <returns>The CRC of everything folded in so far.</returns>
    public static uint ComputeLittleEndian(ReadOnlySpan<uint> data, uint seed)
    {
        uint crc = seed;

        foreach (uint word in data)
        {
            // The byte swap acts on the incoming word rather than on the register, so it
            // sits off the dependency chain the four lookups form and overlaps with them.
            crc = Fold(crc, BinaryPrimitives.ReverseEndianness(word));
        }

        return crc ^ XorOut;
    }

    /// <summary>
    /// Folds one word into the register: XOR the whole word in, then clock the register
    /// through the 32 rounds it owes, a byte per lookup. Folding first and shifting
    /// afterwards is what makes this equivalent to feeding the word's four bytes in
    /// descending significance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Fold(uint crc, uint word)
    {
        crc ^= word;
        crc = (crc << 8) ^ Table[crc >> 24];
        crc = (crc << 8) ^ Table[crc >> 24];
        crc = (crc << 8) ^ Table[crc >> 24];
        crc = (crc << 8) ^ Table[crc >> 24];

        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint register = index << 24;

            for (int round = 0; round < 8; round++)
            {
                register = (register & 0x80000000) != 0
                    ? (register << 1) ^ Polynomial
                    : register << 1;
            }

            table[index] = register;
        }

        return table;
    }
}
