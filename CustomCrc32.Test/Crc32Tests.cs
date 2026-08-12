using System.Buffers.Binary;

namespace CustomCrc32.Test;

[TestFixture]
public class Crc32Tests
{
    /// <summary>
    /// The CRC of the ASCII bytes "123456789", the value catalogued for this parameter
    /// set. It anchors <see cref="ReferenceBitwise"/> to a published constant, which in
    /// turn lets the reference stand in as an oracle for the rest of the fixture.
    /// </summary>
    private const uint PublishedCheckValue = 0x0376E6E7;

    [Test]
    public void ReferenceBitwise_OverCheckString_MatchesPublishedCheckValue()
    {
        uint actual = ReferenceBitwise("123456789"u8);

        Assert.That(actual, Is.EqualTo(PublishedCheckValue));
    }

    [Test]
    public void Compute_EmptyInput_ReturnsInitialValue()
    {
        uint actual = Crc32.Compute([]);

        Assert.That(actual, Is.EqualTo(Crc32.InitialValue));
    }

    [TestCaseSource(nameof(KnownValues))]
    public uint Compute_KnownInput_ReturnsExpectedCrc(uint[] data) => Crc32.Compute(data);

    private static IEnumerable<TestCaseData> KnownValues()
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

    [Test]
    public void Compute_ConsumesEachWordMostSignificantByteFirst()
    {
        // A word and its byte-swap must not collide, and each must agree with the
        // reference fed that word's bytes in descending significance.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Crc32.Compute([0x12345678]), Is.EqualTo(ReferenceBitwise([0x12, 0x34, 0x56, 0x78])));
            Assert.That(Crc32.Compute([0x78563412]), Is.EqualTo(ReferenceBitwise([0x78, 0x56, 0x34, 0x12])));
            Assert.That(Crc32.Compute([0x12345678]), Is.Not.EqualTo(Crc32.Compute([0x78563412])));
        }
    }

    [Test]
    public void Compute_MatchesByteReference_ForRandomInput()
    {
        Random random = new(20250811);

        for (int length = 0; length <= 64; length++)
        {
            uint[] data = new uint[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (uint)random.NextInt64(uint.MinValue, (long)uint.MaxValue + 1);
            }

            uint actual = Crc32.Compute(data);
            uint expected = ReferenceBitwise(ToBigEndianBytes(data));

            Assert.That(actual, Is.EqualTo(expected), $"length {length}: [{string.Join(", ", data.Select(w => $"0x{w:X8}"))}]");
        }
    }

    [Test]
    public void Compute_WithInitialValueSeed_MatchesDefaultOverload()
    {
        uint[] data = [0x12345678, 0x9ABCDEF0, 0xDEADBEEF];

        Assert.That(Crc32.Compute(data, Crc32.InitialValue), Is.EqualTo(Crc32.Compute(data)));
    }

    [Test]
    public void Compute_SeededWithPreviousResult_MatchesSinglePassOverConcatenation()
    {
        uint[] head = [0x12345678, 0x9ABCDEF0];
        uint[] tail = [0xDEADBEEF, 0x0BADF00D, 0x00000000];

        uint chained = Crc32.Compute(tail, Crc32.Compute(head));
        uint singlePass = Crc32.Compute([.. head, .. tail]);

        Assert.That(chained, Is.EqualTo(singlePass));
    }

    [Test]
    public void Compute_EmptyInputWithSeed_ReturnsSeedUnchanged()
    {
        const uint seed = 0xDEADBEEF;

        Assert.That(Crc32.Compute([], seed), Is.EqualTo(seed));
    }

    [Test]
    public void ComputeLittleEndian_EmptyInput_ReturnsInitialValue()
    {
        uint actual = Crc32.ComputeLittleEndian([]);

        Assert.That(actual, Is.EqualTo(Crc32.InitialValue));
    }

    [TestCaseSource(nameof(KnownLittleEndianValues))]
    public uint ComputeLittleEndian_KnownInput_ReturnsExpectedCrc(uint[] data) =>
        Crc32.ComputeLittleEndian(data);

    private static IEnumerable<TestCaseData> KnownLittleEndianValues()
    {
        yield return new TestCaseData(Array.Empty<uint>()).Returns(0xFFFFFFFFu);
        yield return new TestCaseData(new uint[] { 0x00000000 }).Returns(0xC704DD7Bu);
        yield return new TestCaseData(new uint[] { 0xFFFFFFFF }).Returns(0x00000000u);
        yield return new TestCaseData(new uint[] { 0x12345678 }).Returns(0xAD37D056u);
        yield return new TestCaseData(new uint[] { 0x9ABCDEF0 }).Returns(0x5768C465u);
        yield return new TestCaseData(new uint[] { 0x12345678, 0x9ABCDEF0 }).Returns(0x170FFA3Du);
        yield return new TestCaseData(new uint[] { 1, 2, 3, 4 }).Returns(0xE56072A5u);
    }

    [Test]
    public void ComputeLittleEndian_ConsumesEachWordLeastSignificantByteFirst()
    {
        Assert.That(
            Crc32.ComputeLittleEndian([0x12345678]),
            Is.EqualTo(ReferenceBitwise([0x78, 0x56, 0x34, 0x12])));
    }

    [Test]
    public void ComputeLittleEndian_MatchesByteReference_ForRandomInput()
    {
        Random random = new(20250811);

        for (int length = 0; length <= 64; length++)
        {
            uint[] data = new uint[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (uint)random.NextInt64(uint.MinValue, (long)uint.MaxValue + 1);
            }

            uint actual = Crc32.ComputeLittleEndian(data);
            uint expected = ReferenceBitwise(ToLittleEndianBytes(data));

            Assert.That(actual, Is.EqualTo(expected), $"length {length}: [{string.Join(", ", data.Select(w => $"0x{w:X8}"))}]");
        }
    }

    [Test]
    public void ComputeLittleEndian_EqualsBigEndianOverByteSwappedWords()
    {
        Random random = new(19700101);
        uint[] data = new uint[37];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (uint)random.NextInt64(uint.MinValue, (long)uint.MaxValue + 1);
        }

        uint[] swapped = new uint[data.Length];
        BinaryPrimitives.ReverseEndianness(data, swapped);

        Assert.That(Crc32.ComputeLittleEndian(data), Is.EqualTo(Crc32.Compute(swapped)));
    }

    [Test]
    public void ComputeLittleEndian_DiffersFromBigEndian_ForAsymmetricInput()
    {
        // A word that is not a palindrome in bytes must checksum differently under the
        // two orderings, or the swap is not happening at all.
        Assert.That(Crc32.ComputeLittleEndian([0x12345678]), Is.Not.EqualTo(Crc32.Compute([0x12345678])));
    }

    [Test]
    public void ComputeLittleEndian_SeededWithPreviousResult_MatchesSinglePassOverConcatenation()
    {
        uint[] head = [0x12345678, 0x9ABCDEF0];
        uint[] tail = [0xDEADBEEF, 0x0BADF00D, 0x00000000];

        uint chained = Crc32.ComputeLittleEndian(tail, Crc32.ComputeLittleEndian(head));
        uint singlePass = Crc32.ComputeLittleEndian([.. head, .. tail]);

        Assert.That(chained, Is.EqualTo(singlePass));
    }

    [Test]
    public void ComputeLittleEndian_WithInitialValueSeed_MatchesDefaultOverload()
    {
        uint[] data = [0x12345678, 0x9ABCDEF0, 0xDEADBEEF];

        Assert.That(
            Crc32.ComputeLittleEndian(data, Crc32.InitialValue),
            Is.EqualTo(Crc32.ComputeLittleEndian(data)));
    }

    /// <summary>
    /// A deliberately naive bit-at-a-time CRC over bytes, serving as the oracle. The
    /// parameters are written out as literals rather than read from <see cref="Crc32"/>
    /// so that a wrong constant in the implementation cannot propagate into the expected
    /// value and hide itself.
    /// </summary>
    private static uint ReferenceBitwise(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte value in data)
        {
            crc ^= (uint)value << 24;

            for (int round = 0; round < 8; round++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }

        return crc;
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
