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
            // Fold the whole word in at once, then clock the register through the 32
            // rounds it owes, a byte per lookup. Folding first and shifting afterwards
            // is what makes this equivalent to feeding the word's four bytes in
            // descending significance.
            crc ^= word;
            crc = (crc << 8) ^ Table[crc >> 24];
            crc = (crc << 8) ^ Table[crc >> 24];
            crc = (crc << 8) ^ Table[crc >> 24];
            crc = (crc << 8) ^ Table[crc >> 24];
        }

        return crc ^ XorOut;
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
