namespace CustomCrc32;

/// <summary>
/// The values that define a 32-bit CRC, following the Rocksoft/Williams model. Width is
/// implicitly 32; the remaining five degrees of freedom are captured here.
/// </summary>
/// <param name="Polynomial">
/// The generator polynomial in normal (non-reversed) form, with the implicit x³² term
/// omitted. Give the normal form even for reflected variants &mdash; the reversal is applied
/// internally.
/// </param>
/// <param name="InitialValue">The register's starting value, before any input is folded in.</param>
/// <param name="ReflectInput">Whether each input byte is fed least significant bit first.</param>
/// <param name="ReflectOutput">Whether the final register is bit-reversed before <paramref name="XorOut"/>.</param>
/// <param name="XorOut">The value XOR-ed into the register to produce the final CRC.</param>
/// <remarks>
/// The <c>Check</c> value quoted on each preset is the CRC of the ASCII bytes
/// <c>123456789</c>, the conventional cross-implementation anchor. Every one of them is
/// asserted by the test suite.
/// </remarks>
public readonly record struct Crc32Parameters(
    uint Polynomial,
    uint InitialValue,
    bool ReflectInput,
    bool ReflectOutput,
    uint XorOut)
{
    /// <summary>
    /// CRC-32/ISO-HDLC &mdash; what "CRC-32" usually means: zlib, PNG, gzip, Ethernet, ZIP.
    /// Check <c>0xCBF43926</c>.
    /// </summary>
    public static Crc32Parameters IsoHdlc { get; } =
        new(0x04C11DB7, 0xFFFFFFFF, ReflectInput: true, ReflectOutput: true, 0xFFFFFFFF);

    /// <summary>CRC-32/MPEG-2. Check <c>0x0376E6E7</c>.</summary>
    public static Crc32Parameters Mpeg2 { get; } =
        new(0x04C11DB7, 0xFFFFFFFF, ReflectInput: false, ReflectOutput: false, 0x00000000);

    /// <summary>CRC-32/BZIP2, also known as CRC-32/AAL5 and CRC-32/DECT-B. Check <c>0xFC891918</c>.</summary>
    public static Crc32Parameters Bzip2 { get; } =
        new(0x04C11DB7, 0xFFFFFFFF, ReflectInput: false, ReflectOutput: false, 0xFFFFFFFF);

    /// <summary>
    /// CRC-32C &mdash; the Castagnoli polynomial, catalogued as CRC-32/ISCSI. Used by iSCSI,
    /// ext4, Btrfs and SCTP, and the variant x86's SSE4.2 CRC instructions implement.
    /// Check <c>0xE3069283</c>.
    /// </summary>
    public static Crc32Parameters Castagnoli { get; } =
        new(0x1EDC6F41, 0xFFFFFFFF, ReflectInput: true, ReflectOutput: true, 0xFFFFFFFF);

    /// <summary>CRC-32/JAMCRC &mdash; ISO-HDLC without the final XOR. Check <c>0x340BC6D9</c>.</summary>
    public static Crc32Parameters JamCrc { get; } =
        new(0x04C11DB7, 0xFFFFFFFF, ReflectInput: true, ReflectOutput: true, 0x00000000);

    /// <summary>
    /// CRC-32/CKSUM &mdash; the POSIX <c>cksum</c> utility. Note that <c>cksum</c> additionally
    /// appends the message length before checksumming, which this type does not do for you.
    /// Check <c>0x765E7680</c>.
    /// </summary>
    public static Crc32Parameters Cksum { get; } =
        new(0x04C11DB7, 0x00000000, ReflectInput: false, ReflectOutput: false, 0xFFFFFFFF);

    /// <summary>CRC-32/AIXM, used in aeronautical information exchange. Check <c>0x3010BF7F</c>.</summary>
    public static Crc32Parameters Aixm { get; } =
        new(0x814141AB, 0x00000000, ReflectInput: false, ReflectOutput: false, 0x00000000);

    /// <summary>CRC-32/AUTOSAR, from the AUTOSAR automotive standard. Check <c>0x1697D06A</c>.</summary>
    public static Crc32Parameters Autosar { get; } =
        new(0xF4ACFB13, 0xFFFFFFFF, ReflectInput: true, ReflectOutput: true, 0xFFFFFFFF);

    /// <summary>CRC-32/BASE91-D. Check <c>0x87315576</c>.</summary>
    public static Crc32Parameters Base91D { get; } =
        new(0xA833982B, 0xFFFFFFFF, ReflectInput: true, ReflectOutput: true, 0xFFFFFFFF);

    /// <summary>CRC-32/CD-ROM-EDC, the error detection code in CD-ROM sectors. Check <c>0x6EC2EDC4</c>.</summary>
    public static Crc32Parameters CdRomEdc { get; } =
        new(0x8001801B, 0x00000000, ReflectInput: true, ReflectOutput: true, 0x00000000);

    /// <summary>CRC-32/MEF, from Metro Ethernet Forum service OAM. Check <c>0xD2C22F51</c>.</summary>
    public static Crc32Parameters Mef { get; } =
        new(0x741B8CD7, 0xFFFFFFFF, ReflectInput: true, ReflectOutput: true, 0x00000000);

    /// <summary>CRC-32/XFER. Check <c>0xBD0BE338</c>.</summary>
    public static Crc32Parameters Xfer { get; } =
        new(0x000000AF, 0x00000000, ReflectInput: false, ReflectOutput: false, 0x00000000);
}
