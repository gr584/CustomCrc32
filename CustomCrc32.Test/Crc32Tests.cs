using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;

namespace CustomCrc32.Test;

[TestFixture]
public class Crc32Tests
{
    /// <summary>A preset, paired with the check value published for it in the CRC catalogue.</summary>
    public sealed record Preset(string Name, Crc32 Instance, Crc32Parameters Parameters, uint CheckValue)
    {
        public override string ToString() => Name;
    }

    private static IEnumerable<Preset> Presets()
    {
        yield return new("IsoHdlc", Crc32.IsoHdlc, Crc32Parameters.IsoHdlc, 0xCBF43926);
        yield return new("Mpeg2", Crc32.Mpeg2, Crc32Parameters.Mpeg2, 0x0376E6E7);
        yield return new("Bzip2", Crc32.Bzip2, Crc32Parameters.Bzip2, 0xFC891918);
        yield return new("Castagnoli", Crc32.Castagnoli, Crc32Parameters.Castagnoli, 0xE3069283);
        yield return new("JamCrc", Crc32.JamCrc, Crc32Parameters.JamCrc, 0x340BC6D9);
        yield return new("Cksum", Crc32.Cksum, Crc32Parameters.Cksum, 0x765E7680);
        yield return new("Aixm", Crc32.Aixm, Crc32Parameters.Aixm, 0x3010BF7F);
        yield return new("Autosar", Crc32.Autosar, Crc32Parameters.Autosar, 0x1697D06A);
        yield return new("Base91D", Crc32.Base91D, Crc32Parameters.Base91D, 0x87315576);
        yield return new("CdRomEdc", Crc32.CdRomEdc, Crc32Parameters.CdRomEdc, 0x6EC2EDC4);
        yield return new("Mef", Crc32.Mef, Crc32Parameters.Mef, 0xD2C22F51);
        yield return new("Xfer", Crc32.Xfer, Crc32Parameters.Xfer, 0xBD0BE338);
    }

    // ---------------------------------------------------------------- presets

    /// <summary>
    /// Anchors the whole fixture. Each preset's parameters, run through the reference model,
    /// must reproduce the check value published for that CRC. Passing means the preset
    /// constants and the reference model are both right, since agreeing by accident on a
    /// 32-bit value twelve times over is not a thing that happens.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_ReferenceModelOverCheckString_MatchesPublishedCheckValue(Preset preset)
    {
        uint actual = ReferenceModel(preset.Parameters, "123456789"u8);

        Assert.That(actual, Is.EqualTo(preset.CheckValue));
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_Instance_ExposesMatchingParameters(Preset preset)
    {
        Assert.That(preset.Instance.Parameters, Is.EqualTo(preset.Parameters));
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_Compute_MatchesReferenceModelOverBigEndianBytes(Preset preset)
    {
        Random random = new(20250811);

        for (int length = 0; length <= 40; length++)
        {
            uint[] data = RandomWords(random, length);

            Assert.That(
                preset.Instance.ComputeBigEndian(data),
                Is.EqualTo(ReferenceModel(preset.Parameters, ToBigEndianBytes(data))),
                $"{preset.Name}, length {length}");
        }
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_ComputeLittleEndian_MatchesReferenceModelOverLittleEndianBytes(Preset preset)
    {
        Random random = new(20250811);

        for (int length = 0; length <= 40; length++)
        {
            uint[] data = RandomWords(random, length);

            Assert.That(
                preset.Instance.ComputeLittleEndian(data),
                Is.EqualTo(ReferenceModel(preset.Parameters, ToLittleEndianBytes(data))),
                $"{preset.Name}, length {length}");
        }
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_EmptyInput_EqualsFinishOfInitialRegister(Preset preset)
    {
        Assert.That(
            preset.Instance.ComputeBigEndian([]),
            Is.EqualTo(preset.Instance.Finish(preset.Instance.InitialRegister)));
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_AppendInChunksThenFinish_MatchesSinglePassCompute(Preset preset)
    {
        uint[] data = RandomWords(new Random(4242), 33);

        uint register = preset.Instance.InitialRegister;
        foreach (uint[] chunk in new[] { data[..7], data[7..7], data[7..20], data[20..] })
        {
            register = preset.Instance.AppendBigEndian(register, chunk);
        }

        Assert.That(preset.Instance.Finish(register), Is.EqualTo(preset.Instance.ComputeBigEndian(data)));
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_AppendLittleEndianInChunksThenFinish_MatchesSinglePassCompute(Preset preset)
    {
        uint[] data = RandomWords(new Random(4242), 33);

        uint register = preset.Instance.InitialRegister;
        foreach (uint[] chunk in new[] { data[..11], data[11..29], data[29..] })
        {
            register = preset.Instance.AppendLittleEndian(register, chunk);
        }

        Assert.That(
            preset.Instance.Finish(register),
            Is.EqualTo(preset.Instance.ComputeLittleEndian(data)));
    }

    // --------------------------------------------------------- accelerated path

    /// <summary>
    /// Pins the definition rather than the value, which is whatever this machine happens to
    /// offer. The fold needs both instruction sets, so reporting support on the strength of
    /// only one would promise acceleration on a machine that cannot shuffle a block into the
    /// engine's working order.
    /// </summary>
    [Test]
    public void IsHardwareAccelerated_RequiresBothInstructionSets()
    {
        Assert.That(
            Crc32.IsHardwareAccelerated,
            Is.EqualTo(Pclmulqdq.IsSupported && Ssse3.IsSupported));
    }

    /// <summary>
    /// Lengths straddling every boundary the folding path has: the threshold below which it
    /// declines to engage, the four-block unrolled loop, its remainder, and the tail of words
    /// that do not fill a block.
    /// </summary>
    private static readonly int[] FoldingBoundaryLengths =
        [15, 16, 17, 18, 19, 20, 23, 24, 27, 28, 31, 32, 33, 35, 36, 39, 40, 47, 48, 63, 64, 65, 127, 128, 129, 255, 256, 257, 1000, 1023];

    [TestCaseSource(nameof(Presets))]
    public void Preset_LengthsAroundFoldingBoundaries_MatchReferenceModel(Preset preset)
    {
        Random random = new(777);

        foreach (int length in FoldingBoundaryLengths)
        {
            uint[] data = RandomWords(random, length);

            Assert.That(
                preset.Instance.ComputeBigEndian(data),
                Is.EqualTo(ReferenceModel(preset.Parameters, ToBigEndianBytes(data))),
                $"{preset.Name}, big-endian, length {length}");

            Assert.That(
                preset.Instance.ComputeLittleEndian(data),
                Is.EqualTo(ReferenceModel(preset.Parameters, ToLittleEndianBytes(data))),
                $"{preset.Name}, little-endian, length {length}");
        }
    }

    /// <summary>
    /// Pits the two implementations against each other directly. Appending one word at a
    /// time keeps every call under the folding threshold, forcing the table path, whereas the
    /// single-shot call over the same data folds. They must agree.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_FoldedResult_MatchesWordAtATimeAppend(Preset preset)
    {
        uint[] data = RandomWords(new Random(2024), 257);

        uint bigEndian = preset.Instance.InitialRegister;
        uint littleEndian = preset.Instance.InitialRegister;
        foreach (uint word in data)
        {
            bigEndian = preset.Instance.AppendBigEndian(bigEndian, [word]);
            littleEndian = preset.Instance.AppendLittleEndian(littleEndian, [word]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preset.Instance.Finish(bigEndian), Is.EqualTo(preset.Instance.ComputeBigEndian(data)));
            Assert.That(preset.Instance.Finish(littleEndian), Is.EqualTo(preset.Instance.ComputeLittleEndian(data)));
        }
    }

    /// <summary>
    /// Folding must not depend on where the caller chooses to split the stream, which it
    /// would if the accumulator carried state the register cannot express.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_ArbitrarySplitPoints_ProduceTheSameResult(Preset preset)
    {
        uint[] data = RandomWords(new Random(90210), 300);
        uint expected = preset.Instance.ComputeBigEndian(data);

        for (int split = 0; split <= data.Length; split += 7)
        {
            uint register = preset.Instance.AppendBigEndian(preset.Instance.InitialRegister, data[..split]);
            register = preset.Instance.AppendBigEndian(register, data[split..]);

            Assert.That(preset.Instance.Finish(register), Is.EqualTo(expected), $"{preset.Name}, split at {split}");
        }
    }

    // -------------------------------------------------------------- byte buffers

    /// <summary>
    /// The strongest test in the fixture, because it involves no oracle of mine at all. The
    /// catalogue check value is defined as the CRC of the ASCII <em>bytes</em> 123456789, so
    /// <see cref="Crc32.ComputeBytes"/> can be held directly against a published constant.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_ComputeBytes_OverCheckString_ReturnsPublishedCheckValue(Preset preset)
    {
        Assert.That(preset.Instance.ComputeBytes("123456789"u8), Is.EqualTo(preset.CheckValue));
    }

    /// <summary>
    /// Every length from empty to five blocks. This sweeps the sub-threshold case, the fold
    /// threshold itself, whole and partial blocks, and all four possible byte tails.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_ComputeBytes_MatchesReferenceModel(Preset preset)
    {
        Random random = new(31415926);

        for (int length = 0; length <= 80; length++)
        {
            byte[] data = RandomBytes(random, length);

            Assert.That(
                preset.Instance.ComputeBytes(data),
                Is.EqualTo(ReferenceModel(preset.Parameters, data)),
                $"{preset.Name}, length {length}");
        }
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_ComputeBytes_LengthsAroundFoldingBoundaries_MatchReferenceModel(Preset preset)
    {
        Random random = new(2718281);

        foreach (int length in (int[])[31, 32, 33, 63, 64, 65, 95, 96, 127, 128, 129, 255, 257, 511, 1021, 1022, 1023, 1024])
        {
            byte[] data = RandomBytes(random, length);

            Assert.That(
                preset.Instance.ComputeBytes(data),
                Is.EqualTo(ReferenceModel(preset.Parameters, data)),
                $"{preset.Name}, length {length}");
        }
    }

    /// <summary>
    /// The byte API's distinguishing property: a byte stream has no alignment, so it may be
    /// split anywhere at all &mdash; including offsets that cut a word in half, which the
    /// word-based overloads cannot express.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_AppendBytes_ArbitrarySplitPoints_ProduceTheSameResult(Preset preset)
    {
        byte[] data = RandomBytes(new Random(8675309), 300);
        uint expected = preset.Instance.ComputeBytes(data);

        for (int split = 0; split <= data.Length; split++)
        {
            uint register = preset.Instance.AppendBytes(preset.Instance.InitialRegister, data[..split]);
            register = preset.Instance.AppendBytes(register, data[split..]);

            Assert.That(preset.Instance.Finish(register), Is.EqualTo(expected), $"{preset.Name}, split at {split}");
        }
    }

    /// <summary>
    /// Pits the folded byte path against the byte-at-a-time one. Appending a single byte per
    /// call stays far below the folding threshold and exercises only the tail loop, so this
    /// catches a tail step that disagrees with the vectorised bulk.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_FoldedBytes_MatchByteAtATimeAppend(Preset preset)
    {
        byte[] data = RandomBytes(new Random(4815162), 1029);

        uint register = preset.Instance.InitialRegister;
        foreach (byte value in data)
        {
            register = preset.Instance.AppendBytes(register, [value]);
        }

        Assert.That(preset.Instance.Finish(register), Is.EqualTo(preset.Instance.ComputeBytes(data)));
    }

    /// <summary>
    /// Ties the new API to the already-verified one: over a whole number of words, checksumming
    /// the big-endian serialisation byte by byte is by definition the same job as
    /// <see cref="Crc32.ComputeBigEndian"/>.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_ComputeBytes_OverBigEndianSerialisation_EqualsComputeBigEndian(Preset preset)
    {
        Random random = new(161803);

        for (int length = 0; length <= 40; length++)
        {
            uint[] words = RandomWords(random, length);

            Assert.That(
                preset.Instance.ComputeBytes(ToBigEndianBytes(words)),
                Is.EqualTo(preset.Instance.ComputeBigEndian(words)),
                $"{preset.Name}, length {length}");
        }
    }

    /// <summary>
    /// Pins the exact relationship the documentation warns about. Reinterpreting a byte buffer
    /// as words reads each group of four in the host's order, so on a little-endian host it is
    /// the <em>little-endian</em> overload that reproduces the byte-order CRC &mdash; and the
    /// big-endian one that quietly does not. That host-dependence is precisely why
    /// <see cref="Crc32.ComputeBytes"/> exists.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_ComputeBytes_EqualsReinterpretedWords_OnALittleEndianHost(Preset preset)
    {
        Assert.That(BitConverter.IsLittleEndian, Is.True, "this test describes little-endian hosts only");

        byte[] data = RandomBytes(new Random(112358), 256);
        ReadOnlySpan<uint> reinterpreted = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preset.Instance.ComputeBytes(data), Is.EqualTo(preset.Instance.ComputeLittleEndian(reinterpreted)));
            Assert.That(preset.Instance.ComputeBytes(data), Is.Not.EqualTo(preset.Instance.ComputeBigEndian(reinterpreted)));
        }
    }

    [TestCaseSource(nameof(Presets))]
    public void Preset_ComputeBytes_EmptyInput_EqualsFinishOfInitialRegister(Preset preset)
    {
        Assert.That(
            preset.Instance.ComputeBytes(ReadOnlySpan<byte>.Empty),
            Is.EqualTo(preset.Instance.Finish(preset.Instance.InitialRegister)));
    }

    /// <summary>
    /// Random parameter sets over random byte lengths. As with the word API, this is what
    /// covers mismatched input and output reflection, which no preset exercises.
    /// </summary>
    [Test]
    public void ArbitraryParameters_ComputeBytes_MatchReferenceModel()
    {
        Random random = new(20260811);

        for (int trial = 0; trial < 400; trial++)
        {
            Crc32Parameters parameters = new(
                Polynomial: RandomWord(random),
                InitialValue: RandomWord(random),
                ReflectInput: random.Next(2) == 0,
                ReflectOutput: random.Next(2) == 0,
                XorOut: RandomWord(random));

            // Spans both sides of the 32-byte folding threshold and every tail remainder.
            byte[] data = RandomBytes(random, random.Next(0, 200));

            Assert.That(
                new Crc32(parameters).ComputeBytes(data),
                Is.EqualTo(ReferenceModel(parameters, data)),
                $"{parameters} over {data.Length} bytes");
        }
    }

    // ------------------------------------------------------- arbitrary parameters

    /// <summary>
    /// The presets only exercise parameter sets where input and output reflection agree, and
    /// where the initial value is a bit-reversal palindrome. Random parameters cover the rest,
    /// in particular mismatched reflection and asymmetric initial values.
    /// </summary>
    [Test]
    public void ArbitraryParameters_MatchReferenceModel()
    {
        Random random = new(19700101);

        for (int trial = 0; trial < 400; trial++)
        {
            Crc32Parameters parameters = new(
                Polynomial: RandomWord(random),
                InitialValue: RandomWord(random),
                ReflectInput: random.Next(2) == 0,
                ReflectOutput: random.Next(2) == 0,
                XorOut: RandomWord(random));

            Crc32 crc32 = new(parameters);
            // Spans both sides of the folding threshold, so random parameters exercise the
            // accelerated path too and not only the table.
            uint[] data = RandomWords(random, random.Next(0, 140));
            string context = $"{parameters} over [{string.Join(", ", data.Select(w => $"0x{w:X8}"))}]";

            Assert.That(
                crc32.ComputeBigEndian(data),
                Is.EqualTo(ReferenceModel(parameters, ToBigEndianBytes(data))),
                $"big-endian: {context}");

            Assert.That(
                crc32.ComputeLittleEndian(data),
                Is.EqualTo(ReferenceModel(parameters, ToLittleEndianBytes(data))),
                $"little-endian: {context}");
        }
    }

    [Test]
    public void ArbitraryParameters_AreExposedUnchanged()
    {
        Crc32Parameters parameters = new(0xDEADBEEF, 0x0BADF00D, ReflectInput: true, ReflectOutput: false, 0x12345678);

        Assert.That(new Crc32(parameters).Parameters, Is.EqualTo(parameters));
    }

    [Test]
    public void ReflectedInitialRegister_IsBitReverseOfInitialValue()
    {
        // 0x0000FFFF reverses to 0xFFFF0000, so this would survive a no-op "reversal".
        Crc32Parameters parameters = new(0x04C11DB7, 0x0000FFFF, ReflectInput: true, ReflectOutput: true, 0);

        Assert.That(new Crc32(parameters).InitialRegister, Is.EqualTo(0xFFFF0000u));
    }

    [Test]
    public void ForwardInitialRegister_IsInitialValueUnchanged()
    {
        Crc32Parameters parameters = new(0x04C11DB7, 0x0000FFFF, ReflectInput: false, ReflectOutput: false, 0);

        Assert.That(new Crc32(parameters).InitialRegister, Is.EqualTo(0x0000FFFFu));
    }

    // ------------------------------------------------------------ known values

    /// <summary>
    /// Known MPEG-2 answers pinned as literals rather than derived from the reference model,
    /// so a regression still surfaces if implementation and oracle ever drifted together.
    /// </summary>
    [TestCaseSource(nameof(KnownMpeg2BigEndianValues))]
    public uint Mpeg2_ComputeBigEndian_KnownInput_ReturnsExpectedCrc(uint[] data) => Crc32.Mpeg2.ComputeBigEndian(data);

    private static IEnumerable<TestCaseData> KnownMpeg2BigEndianValues()
    {
        yield return new TestCaseData(Array.Empty<uint>()).Returns(0xFFFFFFFFu);
        yield return new TestCaseData(new uint[] { 0x00000000 }).Returns(0xC704DD7Bu);
        yield return new TestCaseData(new uint[] { 0xFFFFFFFF }).Returns(0x00000000u);
        yield return new TestCaseData(new uint[] { 0x12345678 }).Returns(0xDF8A8A2Bu);
        yield return new TestCaseData(new uint[] { 0x78563412 }).Returns(0xAD37D056u);
        yield return new TestCaseData(new uint[] { 0x9ABCDEF0 }).Returns(0x25D59E18u);
        yield return new TestCaseData(new uint[] { 0x12345678, 0x9ABCDEF0 }).Returns(0x7D24A31Bu);
        yield return new TestCaseData(new uint[] { 1, 2, 3, 4 }).Returns(0x955AE3FDu);
    }

    [TestCaseSource(nameof(KnownMpeg2LittleEndianValues))]
    public uint Mpeg2_ComputeLittleEndian_KnownInput_ReturnsExpectedCrc(uint[] data) =>
        Crc32.Mpeg2.ComputeLittleEndian(data);

    private static IEnumerable<TestCaseData> KnownMpeg2LittleEndianValues()
    {
        yield return new TestCaseData(Array.Empty<uint>()).Returns(0xFFFFFFFFu);
        yield return new TestCaseData(new uint[] { 0x00000000 }).Returns(0xC704DD7Bu);
        yield return new TestCaseData(new uint[] { 0xFFFFFFFF }).Returns(0x00000000u);
        yield return new TestCaseData(new uint[] { 0x12345678 }).Returns(0xAD37D056u);
        yield return new TestCaseData(new uint[] { 0x9ABCDEF0 }).Returns(0x5768C465u);
        yield return new TestCaseData(new uint[] { 0x12345678, 0x9ABCDEF0 }).Returns(0x170FFA3Du);
        yield return new TestCaseData(new uint[] { 1, 2, 3, 4 }).Returns(0xE56072A5u);
    }

    // ------------------------------------------------------------- byte order

    [Test]
    public void Compute_ConsumesEachWordMostSignificantByteFirst()
    {
        Assert.That(
            Crc32.Mpeg2.ComputeBigEndian([0x12345678]),
            Is.EqualTo(ReferenceModel(Crc32Parameters.Mpeg2, [0x12, 0x34, 0x56, 0x78])));
    }

    [Test]
    public void ComputeLittleEndian_ConsumesEachWordLeastSignificantByteFirst()
    {
        Assert.That(
            Crc32.Mpeg2.ComputeLittleEndian([0x12345678]),
            Is.EqualTo(ReferenceModel(Crc32Parameters.Mpeg2, [0x78, 0x56, 0x34, 0x12])));
    }

    [TestCaseSource(nameof(Presets))]
    public void ComputeLittleEndian_EqualsComputeOverByteSwappedWords(Preset preset)
    {
        uint[] data = RandomWords(new Random(31337), 37);
        uint[] swapped = new uint[data.Length];
        BinaryPrimitives.ReverseEndianness(data, swapped);

        Assert.That(preset.Instance.ComputeLittleEndian(data), Is.EqualTo(preset.Instance.ComputeBigEndian(swapped)));
    }

    [Test]
    public void ComputeLittleEndian_DiffersFromCompute_ForAsymmetricInput()
    {
        Assert.That(
            Crc32.Mpeg2.ComputeLittleEndian([0x12345678]),
            Is.Not.EqualTo(Crc32.Mpeg2.ComputeBigEndian([0x12345678])));
    }

    // -------------------------------------------------------------- allocation

    /// <summary>
    /// Lengths that put both layers to work: below the folding threshold the table path
    /// handles the whole input, above it the fold path does the bulk and leaves a tail.
    /// </summary>
    private static readonly int[] AllocationLengths = [0, 1, 7, 8, 9, 33, 129, 1023];

    /// <summary>
    /// Calls made before measuring. Enough to have JITted the method and run the static
    /// initialiser that builds the presets, both of which allocate quite legitimately.
    /// </summary>
    private const int AllocationWarmupCalls = 256;

    /// <summary>Calls the measurement spans. The counter is exact, so one stray byte fails.</summary>
    private const int AllocationMeasuredCalls = 128;

    /// <summary>
    /// Keeps the measured call from being optimised away: a store to a static field cannot be
    /// discarded, however dead the value is. Never read.
    /// </summary>
    private static uint _allocationSink;

    /// <summary>
    /// The word entry points must put nothing on the heap. The register is a bare
    /// <see cref="uint"/> threaded through by value and the input span is read where it lies,
    /// so a call has nothing legitimate to allocate. A scratch buffer for the byte swap, a
    /// stray <c>ToArray</c>, or an interface-typed enumerator would each show up here and
    /// nowhere else in this fixture, every other test of which only checks the answer.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_WordEntryPoints_AllocateNothing(Preset preset)
    {
        Random random = new(31337);

        foreach (int length in AllocationLengths)
        {
            uint[] data = RandomWords(random, length);
            uint seed = preset.Instance.InitialRegister;

            AssertAllocatesNothing(
                $"{preset.Name}, ComputeBigEndian, {length} words",
                () => preset.Instance.ComputeBigEndian(data));

            AssertAllocatesNothing(
                $"{preset.Name}, ComputeLittleEndian, {length} words",
                () => preset.Instance.ComputeLittleEndian(data));

            AssertAllocatesNothing(
                $"{preset.Name}, AppendBigEndian, {length} words",
                () => preset.Instance.AppendBigEndian(seed, data));

            AssertAllocatesNothing(
                $"{preset.Name}, AppendLittleEndian, {length} words",
                () => preset.Instance.AppendLittleEndian(seed, data));
        }
    }

    /// <summary>
    /// The same of the byte entry points, at lengths that leave a partial word for the
    /// single-byte tail loop as well as lengths that divide evenly.
    /// </summary>
    [TestCaseSource(nameof(Presets))]
    public void Preset_ByteEntryPoints_AllocateNothing(Preset preset)
    {
        Random random = new(31337);

        foreach (int words in AllocationLengths)
        {
            foreach (int length in new[] { words * sizeof(uint), (words * sizeof(uint)) + 3 })
            {
                byte[] data = RandomBytes(random, length);
                uint seed = preset.Instance.InitialRegister;

                AssertAllocatesNothing(
                    $"{preset.Name}, ComputeBytes, {length} bytes",
                    () => preset.Instance.ComputeBytes(data));

                AssertAllocatesNothing(
                    $"{preset.Name}, AppendBytes, {length} bytes",
                    () => preset.Instance.AppendBytes(seed, data));
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> until the JIT has settled, then requires a further
    /// run of calls to add nothing whatsoever to this thread's allocation total.
    /// </summary>
    /// <remarks>
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> is a running total rather than a
    /// sample, so the comparison is exact and wants no tolerance; it is also per-thread, so
    /// nothing another test or a background thread does can leak into it. The delegate and
    /// the closure behind it are allocated by the caller before this method is entered, and
    /// invoking a delegate allocates nothing, so neither falls inside the measurement.
    /// </remarks>
    private static void AssertAllocatesNothing(string what, Func<uint> operation)
    {
        for (int i = 0; i < AllocationWarmupCalls; i++)
        {
            _allocationSink ^= operation();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < AllocationMeasuredCalls; i++)
        {
            _allocationSink ^= operation();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(
            allocated,
            Is.Zero,
            $"{what}: allocated {allocated} bytes over {AllocationMeasuredCalls} calls");
    }

    // ------------------------------------------------------------- distinctness

    [Test]
    public void Presets_ProduceDistinctResults_ForTheSameInput()
    {
        uint[] data = RandomWords(new Random(8675309), 16);

        uint[] results = Presets().Select(preset => preset.Instance.ComputeBigEndian(data)).ToArray();

        Assert.That(results, Is.Unique);
    }

    // ------------------------------------------------------------------ oracle

    /// <summary>
    /// Williams' model spelled out literally: feed each byte in at the top of the register,
    /// clock it one bit at a time, reflect on the way in and out where asked. Deliberately
    /// naive and structurally unlike the table-driven implementation, so the two are unlikely
    /// to share a mistake. Its own bit reversals are loop-based rather than the library's
    /// bit-twiddling version, for the same reason.
    /// </summary>
    private static uint ReferenceModel(Crc32Parameters parameters, ReadOnlySpan<byte> data)
    {
        uint crc = parameters.InitialValue;

        foreach (byte raw in data)
        {
            byte value = parameters.ReflectInput ? ReverseByte(raw) : raw;
            crc ^= (uint)value << 24;

            for (int round = 0; round < 8; round++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ parameters.Polynomial : crc << 1;
            }
        }

        if (parameters.ReflectOutput)
        {
            crc = ReverseWord(crc);
        }

        return crc ^ parameters.XorOut;
    }

    private static byte ReverseByte(byte value)
    {
        int reversed = 0;

        for (int bit = 0; bit < 8; bit++)
        {
            reversed = (reversed << 1) | ((value >> bit) & 1);
        }

        return (byte)reversed;
    }

    private static uint ReverseWord(uint value)
    {
        uint reversed = 0;

        for (int bit = 0; bit < 32; bit++)
        {
            reversed = (reversed << 1) | ((value >> bit) & 1);
        }

        return reversed;
    }

    // ----------------------------------------------------------------- helpers

    private static uint RandomWord(Random random) =>
        (uint)random.NextInt64(uint.MinValue, (long)uint.MaxValue + 1);

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] data = new byte[length];
        random.NextBytes(data);

        return data;
    }

    private static uint[] RandomWords(Random random, int length)
    {
        uint[] data = new uint[length];

        for (int i = 0; i < length; i++)
        {
            data[i] = RandomWord(random);
        }

        return data;
    }

    private static byte[] ToBigEndianBytes(ReadOnlySpan<uint> data)
    {
        byte[] bytes = new byte[data.Length * sizeof(uint)];

        for (int i = 0; i < data.Length; i++)
        {
            bytes[i * 4 + 0] = (byte)(data[i] >> 24);
            bytes[i * 4 + 1] = (byte)(data[i] >> 16);
            bytes[i * 4 + 2] = (byte)(data[i] >> 8);
            bytes[i * 4 + 3] = (byte)data[i];
        }

        return bytes;
    }

    private static byte[] ToLittleEndianBytes(ReadOnlySpan<uint> data)
    {
        byte[] bytes = new byte[data.Length * sizeof(uint)];

        for (int i = 0; i < data.Length; i++)
        {
            bytes[i * 4 + 0] = (byte)data[i];
            bytes[i * 4 + 1] = (byte)(data[i] >> 8);
            bytes[i * 4 + 2] = (byte)(data[i] >> 16);
            bytes[i * 4 + 3] = (byte)(data[i] >> 24);
        }

        return bytes;
    }
}
