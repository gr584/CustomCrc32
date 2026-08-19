using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Crc32ParameterSet = System.IO.Hashing.Crc32ParameterSet;
using HashingCrc32 = System.IO.Hashing.Crc32;

namespace CustomCrc32.Benchmarks;

/// <summary>
/// This library against <c>System.IO.Hashing</c>, which as of 11.0 takes a configurable
/// parameter set and so covers the same ground for the first time.
/// </summary>
/// <remarks>
/// <para>
/// Until 11.0 the comparison was not worth drawing: <c>System.IO.Hashing.Crc32</c> computed
/// ISO-HDLC and nothing else, so there was no overlap to measure outside a single preset.
/// <c>Crc32ParameterSet.Create</c> now takes a polynomial, an initial value, a final XOR and a
/// reflection flag &mdash; near enough the Rocksoft model <see cref="Crc32Parameters"/> uses,
/// differing in that input and output reflection collapse into one flag and that the initial
/// value is the register to start from rather than the catalogue's unreflected <c>Init</c>.
/// Every one of this library's twelve presets maps onto it, so the two really are answering
/// the same question; <see cref="Create"/> handles the two differences.
/// </para>
/// <para>
/// Three parameter sets are measured, chosen to separate things that would otherwise be
/// confounded. ISO-HDLC is run through the package's parameterless entry point and through a
/// hand-built parameter set describing the identical CRC, which isolates what the configurable
/// overload costs. CRC-32C is run through the package's own preset and through a hand-built
/// equivalent, which is what would expose a shortcut to SSE4.2's <c>CRC32</c> instruction if
/// one existed: the instruction computes this polynomial and no other, so a special case could
/// only show up on the preset. AUTOSAR is then run as a polynomial neither side has any
/// special case for, which is where a general implementation has to show it is general.
/// </para>
/// <para>
/// This class is why the benchmark project targets net10.0 and nothing lower, while the
/// library and the tests also build for net8.0. The package ships its vectorised
/// implementation in the net10.0 and net11.0 assets alone; a net8.0 consumer resolves the
/// netstandard2.0 asset, which carries no intrinsics, and timing against that would measure
/// the target framework rather than the code.
/// </para>
/// <para>
/// The buffer is reused every invocation, so up to 1 MiB this is a pipeline measurement rather
/// than a streaming rate &mdash; the same caveat as <see cref="FoldLaneBenchmarks"/>, and the
/// reason <see cref="StreamingBenchmarks"/> exists.
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: false)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SystemIoHashingBenchmarks
{
    private const string IsoHdlcCategory = "ISO-HDLC";
    private const string CastagnoliCategory = "CRC-32C";
    private const string AutosarCategory = "AUTOSAR";

    [Params(4_096, 65_536, 1_048_576, 16_777_216)]
    public int ByteCount { get; set; }

    private byte[] _data = [];

    /// <summary>
    /// ISO-HDLC built by hand rather than taken from <c>Crc32ParameterSet.Crc32</c>, so that
    /// the parameterless comparison above it is the only thing that differs.
    /// </summary>
    private Crc32ParameterSet _isoHdlc = null!;

    private Crc32ParameterSet _castagnoli = null!;
    private Crc32ParameterSet _autosar = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(20250811);
        _data = new byte[ByteCount];
        random.NextBytes(_data);

        _isoHdlc = Create(Crc32Parameters.IsoHdlc);
        _castagnoli = Create(Crc32Parameters.Castagnoli);
        _autosar = Create(Crc32Parameters.Autosar);

        // A speed comparison only means anything while the implementations still agree, and
        // agreement is the whole claim being made about the parameter sets above.
        Verify("ISO-HDLC, package default", HashingIsoHdlcDefault(), FoldIsoHdlc());
        Verify("ISO-HDLC, built parameter set", HashingIsoHdlcCreated(), FoldIsoHdlc());
        Verify("CRC-32C, package preset", HashingCastagnoliPreset(), FoldCastagnoli());
        Verify("CRC-32C, built parameter set", HashingCastagnoliCreated(), FoldCastagnoli());
        Verify("AUTOSAR, built parameter set", HashingAutosar(), FoldAutosar());
    }

    /// <summary>
    /// The package's parameter set for one of this library's. The polynomial's normal form and
    /// the final XOR mean the same thing to both; the reflection flag and the initial value
    /// need handling.
    /// </summary>
    /// <remarks>
    /// The package carries one reflection flag where this library carries two, which no
    /// catalogued CRC-32 sets differently. Its initial value is the register to start from,
    /// where this library follows the catalogue and states <c>Init</c> in the unreflected
    /// domain, so a reflected set needs the bit reversal applying first. Every preset used
    /// here initialises to <c>0xFFFFFFFF</c>, a bit-reversal palindrome, so the reversal is a
    /// no-op for these three cases &mdash; it is here because a helper that takes arbitrary
    /// parameters should not be silently wrong for the ones nobody passed it yet. The test
    /// suite covers the distinction properly.
    /// </remarks>
    private static Crc32ParameterSet Create(Crc32Parameters parameters)
    {
        if (parameters.ReflectInput != parameters.ReflectOutput)
        {
            throw new NotSupportedException(
                $"{nameof(Crc32ParameterSet)} has one reflection flag and cannot express " +
                "input and output reflection differing.");
        }

        return Crc32ParameterSet.Create(
            parameters.Polynomial,
            parameters.ReflectInput ? Reverse(parameters.InitialValue) : parameters.InitialValue,
            parameters.XorOut,
            parameters.ReflectInput);
    }

    private static uint Reverse(uint value)
    {
        uint reversed = 0;

        for (int bit = 0; bit < 32; bit++)
        {
            reversed = (reversed << 1) | ((value >> bit) & 1);
        }

        return reversed;
    }

    private void Verify(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{what} disagrees at {ByteCount} bytes: got 0x{actual:X8}, expected 0x{expected:X8}.");
        }
    }

    [BenchmarkCategory(IsoHdlcCategory)]
    [Benchmark(Baseline = true, Description = "Fold")]
    public uint FoldIsoHdlc() => Crc32.IsoHdlc.ComputeBytes(_data);

    /// <summary>The package's original entry point, which computes this CRC and no other.</summary>
    [BenchmarkCategory(IsoHdlcCategory)]
    [Benchmark(Description = "System.IO.Hashing, parameterless")]
    public uint HashingIsoHdlcDefault() => HashingCrc32.HashToUInt32(_data);

    /// <summary>The same CRC reached through the configurable overload.</summary>
    [BenchmarkCategory(IsoHdlcCategory)]
    [Benchmark(Description = "System.IO.Hashing, built parameter set")]
    public uint HashingIsoHdlcCreated() => HashingCrc32.HashToUInt32(_isoHdlc, _data);

    [BenchmarkCategory(CastagnoliCategory)]
    [Benchmark(Baseline = true, Description = "Fold")]
    public uint FoldCastagnoli() => Crc32.Castagnoli.ComputeBytes(_data);

    /// <summary>
    /// The package's own CRC-32C preset, and the only place a shortcut to SSE4.2's
    /// <c>CRC32</c> instruction could plausibly hide.
    /// </summary>
    [BenchmarkCategory(CastagnoliCategory)]
    [Benchmark(Description = "System.IO.Hashing, package preset")]
    public uint HashingCastagnoliPreset() =>
        HashingCrc32.HashToUInt32(Crc32ParameterSet.Crc32C, _data);

    [BenchmarkCategory(CastagnoliCategory)]
    [Benchmark(Description = "System.IO.Hashing, built parameter set")]
    public uint HashingCastagnoliCreated() => HashingCrc32.HashToUInt32(_castagnoli, _data);

    [BenchmarkCategory(AutosarCategory)]
    [Benchmark(Baseline = true, Description = "Fold")]
    public uint FoldAutosar() => Crc32.Autosar.ComputeBytes(_data);

    /// <summary>A polynomial with no preset and no instruction behind it on either side.</summary>
    [BenchmarkCategory(AutosarCategory)]
    [Benchmark(Description = "System.IO.Hashing, built parameter set")]
    public uint HashingAutosar() => HashingCrc32.HashToUInt32(_autosar, _data);
}
