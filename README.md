# CustomCrc32

A CRC-32 implementation for input arriving as **32-bit words** rather than bytes, computed
most-significant-bit-first with polynomial `0x04C11DB7`, initial value `0xFFFFFFFF` and no
final XOR.

These parameters are the ones catalogued as **CRC-32/MPEG-2**. They are *not* the zlib /
PNG / gzip variant that "CRC-32" usually refers to — see
[Using a different variant](#using-a-different-variant) if that is what you were after.

## Parameters

| Property   | Value        | Notes                                                      |
| ---------- | ------------ | ---------------------------------------------------------- |
| Width      | 32 bits      |                                                            |
| Polynomial | `0x04C11DB7` | Normal (non-reversed) form                                 |
| Init       | `0xFFFFFFFF` | Also the CRC of empty input                                |
| RefIn      | false        | Input is not reflected; each word is consumed MSB-first    |
| RefOut     | false        | Output is not reflected                                    |
| XorOut     | `0x00000000` | A no-op, retained in the source to document the parameter  |
| Check      | `0x0376E6E7` | CRC of the ASCII bytes `123456789`                         |

The check value is the standard cross-implementation anchor for this parameter set and is
asserted by the test suite.

## Requirements

- .NET 8.0 SDK or newer. All three projects target `net8.0`; the repository has been built
  and tested with SDK 10.0.303.
- No runtime package dependencies — the library is plain BCL code.

## API

`CustomCrc32.Crc32` is a static class.

```csharp
public static class Crc32
{
    public const uint Polynomial   = 0x04C11DB7;
    public const uint InitialValue = 0xFFFFFFFF;
    public const uint XorOut       = 0x00000000;

    // Big-endian: each word contributes its most significant byte first.
    public static uint Compute(ReadOnlySpan<uint> data);
    public static uint Compute(ReadOnlySpan<uint> data, uint seed);

    // Little-endian: each word contributes its least significant byte first.
    public static uint ComputeLittleEndian(ReadOnlySpan<uint> data);
    public static uint ComputeLittleEndian(ReadOnlySpan<uint> data, uint seed);
}
```

The parameter is `ReadOnlySpan<uint>`, so a `Span<uint>`, a `uint[]`, a stack-allocated
buffer or a collection expression all bind without a cast.

- **`Compute(data)`** — one-shot, big-endian. Returns `InitialValue` for empty input.
- **`Compute(data, seed)`** — resumes from a previous register state, so a stream can be
  checksummed in pieces. Because `XorOut` is zero, feeding one call's result in as the next
  call's seed gives exactly the same answer as a single pass over the concatenated input.
- **`ComputeLittleEndian(…)`** — the same, over the little-endian serialisation of the
  words: `0x12345678` is checksummed as the bytes `78 56 34 12`. Use this when the words
  are values you intend to write out least significant byte first. Both overloads behave
  and chain exactly like their big-endian counterparts.

Which one you want depends on the byte order your words will be *serialised* in, not on the
byte order of the machine you are running on — see
[A note on endianness](#a-note-on-endianness).

## Usage

```csharp
using CustomCrc32;

uint[] words = [0x12345678, 0x9ABCDEF0];

uint bigEndian    = Crc32.Compute(words);              // 0x7D24A31B
uint littleEndian = Crc32.ComputeLittleEndian(words);  // 0x170FFA3D
```

Checksumming a stream in chunks:

```csharp
uint crc = Crc32.InitialValue;
while (TryReadChunk(out ReadOnlySpan<uint> chunk))
{
    crc = Crc32.Compute(chunk, crc);
}
// crc now equals Crc32.Compute(everything)
```

Known values, all covered by tests:

| Input                                              | `Compute` (BE) | `ComputeLittleEndian` |
| -------------------------------------------------- | -------------- | --------------------- |
| *(empty)*                                          | `0xFFFFFFFF`   | `0xFFFFFFFF`          |
| `[0x00000000]`                                     | `0xC704DD7B`   | `0xC704DD7B`          |
| `[0xFFFFFFFF]`                                     | `0x00000000`   | `0x00000000`          |
| `[0x12345678]`                                     | `0xDF8A8A2B`   | `0xAD37D056`          |
| `[0x9ABCDEF0]`                                     | `0x25D59E18`   | `0x5768C465`          |
| `[0x12345678, 0x9ABCDEF0]`                         | `0x7D24A31B`   | `0x170FFA3D`          |
| `[0x00000001, 0x00000002, 0x00000003, 0x00000004]` | `0x955AE3FD`   | `0xE56072A5`          |

The two columns coincide only where every word is a byte-palindrome. Note
`Compute([0x78563412]) == ComputeLittleEndian([0x12345678]) == 0xAD37D056`, which is the
byte-swap identity the implementation relies on.

## A note on endianness

The input is typed as `uint`, not as raw bytes, so **the host machine's endianness never
enters into it**. The algorithm consumes each word from bit 31 down to bit 0 regardless of
how that word happens to be laid out in memory. "Big-endian" here describes the *bit
ordering within each word* — the most significant byte contributes first — which is
inherent to an MSB-first CRC and is what makes this equivalent to feeding the word's four
bytes in descending significance.

So the choice between `Compute` and `ComputeLittleEndian` is about **the byte order your
words will be serialised in**, not about the machine you are running on. The same call
returns the same answer on x64, ARM, or anything else.

`ComputeLittleEndian` swaps each word as it is folded in, using a single `bswap` on the
incoming word. Because the swap acts on the input rather than on the CRC register, it sits
off the dependency chain the four table lookups form and overlaps with them — the benchmark
below shows it costs nothing measurable. It is therefore always preferable to pre-swapping
the buffer with `BinaryPrimitives.ReverseEndianness`, which allocates and makes an extra
pass.

### The one case this does not cover

If your data actually arrives as a **byte** buffer that you are reinterpreting as `uint`
(via `MemoryMarshal.Cast` or similar), neither method is the right tool, and the cast is a
trap: a big-endian buffer `12 34 56 78` read through `MemoryMarshal.Cast<byte, uint>` on a
little-endian host yields `0x78563412` — silently the wrong word, with no error and a wrong
CRC at the end.

For that case the correct fix is not to cast at all, but to process the bytes directly with
the same table:

```csharp
crc = (crc << 8) ^ Table[((crc >> 24) ^ value) & 0xFF];
```

That removes the endianness question entirely and handles buffers whose length is not a
multiple of four, which the word-based API structurally cannot. No such overload ships
today — ask if you need one. It would want a distinct name (`ComputeBytes`) rather than an
overload of `Compute`, because overloading on `ReadOnlySpan<byte>` alongside
`ReadOnlySpan<uint>` makes collection-expression calls like `Compute([])` ambiguous.

## Implementation notes

`Crc32` is table-driven. A 256-entry table maps a leading byte to the register
state produced by clocking it through eight rounds, so a whole byte folds in with one
lookup. The table is built once in a static initialiser (the CLR guarantees that is
thread-safe and runs at most once).

Each word is processed as:

```csharp
crc ^= word;
crc = (crc << 8) ^ Table[crc >> 24];   // ×4
```

That is, **fold the entire word in first, then clock the register through the 32 rounds it
owes**, a byte per lookup. The fold-then-shift order is what makes this bit-identical to
the conventional byte-at-a-time loop fed `b3, b2, b1, b0`.

Both public entry points share that step as a private `Fold` helper; `ComputeLittleEndian`
differs only in passing `BinaryPrimitives.ReverseEndianness(word)` instead of `word`.

<details>
<summary>Why the two are equivalent</summary>

Let `S` be the one-bit shift-and-reduce step; it is linear over GF(2). The byte-at-a-time
loop XORs each byte in at bit 24 and applies `S⁸`, which unrolls to:

```
S³²(crc) ^ S³²(b3<<24) ^ S²⁴(b2<<24) ^ S¹⁶(b1<<24) ^ S⁸(b0<<24)
```

The word-at-a-time version XORs the whole word in and applies `S³²`:

```
S³²(crc) ^ S³²(b3<<24) ^ S³²(b2<<16) ^ S³²(b1<<8) ^ S³²(b0)
```

These agree because `S⁸(x<<16) = x<<24` for any byte `x`: the occupied bits start at
positions 16–23, and across those eight shifts the top bit is never set at the moment it is
tested, so no polynomial reduction occurs and `S⁸` degenerates to a plain `<<8`. Applying
that identity to each staggered term lines the two expansions up exactly.

This was also confirmed numerically before the implementation was written, and is pinned by
tests.
</details>

## Correctness

The test suite ([`CustomCrc32.Test`](CustomCrc32.Test/Crc32Tests.cs), NUnit, 29 tests) uses
a two-layer oracle rather than trusting hand-computed constants:

1. A deliberately naive **bit-at-a-time reference over bytes** with the parameters written
   out as literals rather than read from `Crc32`, so a wrong constant in the implementation
   cannot propagate into the expected value and hide itself. That reference is anchored to
   the published check value `0x0376E6E7` for `123456789`.
2. Each shipping method is then cross-checked against that reference over 65 random inputs
   (lengths 0–64 words, fixed seed) — expanded big-endian for `Compute`, little-endian for
   `ComputeLittleEndian` — plus the literal known values above.

Also covered, for both orderings: empty input, empty input with a seed, the default overload
agreeing with an explicit `InitialValue` seed, and seed-chaining matching a single pass over
the concatenation. Byte-order specifically is pinned from three directions — a word and its
byte-swap must not collide, `ComputeLittleEndian` must differ from `Compute` on an
asymmetric word, and `ComputeLittleEndian(data)` must equal `Compute(swapped)` over a random
buffer.

A mutation check confirms the suite is not vacuous — flipping one bit of the polynomial
(`0x04C11DB7` → `0x04C11DB6`) fails 7 of the then-14 tests.

## Benchmarks

[`CustomCrc32.Benchmarks`](CustomCrc32.Benchmarks/) uses BenchmarkDotNet to compare the
shipping table-driven implementation against the bit-at-a-time formulation of the same CRC,
across input sizes chosen to sit at different levels of the memory hierarchy. Bitwise is the
baseline, since it is what the table lookup replaces.

`GlobalSetup` throws if the implementations disagree — a speed comparison stops meaning
anything once the baseline has diverged. It checks both that bitwise matches table-driven,
and that `ComputeLittleEndian(data)` matches `Compute(swapped)`.

`ThroughputColumn` is a custom `IColumn` adding a GB/s column derived from the mean and the
`WordCount` parameter. BenchmarkDotNet has no built-in throughput column, and
`OperationsPerInvoke` cannot be used here because it requires a compile-time constant.

```
dotnet run -c Release --project CustomCrc32.Benchmarks
```

Extra arguments are forwarded to BenchmarkDotNet (`-- --job short`, `--filter`, …).

### Indicative results

Intel Xeon E-2174G @ 3.80 GHz (4 physical / 8 logical cores), Windows 11 25H2, .NET 8.0.30
X64 RyuJIT x86-64-v3, BenchmarkDotNet 0.15.8. **Taken with `--job short`** (3 warmup + 3
iterations) — good enough for the headline ratio, but re-run with the default job before
quoting these anywhere.

| WordCount        | Bitwise    | Table-driven (BE) | Table-driven (LE) | Speedup |
| ---------------- | ---------- | ----------------- | ----------------- | ------- |
| 16 (64 B)        | 114 MB/s   | 661 MB/s          | 643 MB/s          | 5.8×    |
| 256 (1 KiB)      | 38 MB/s    | 564 MB/s          | 564 MB/s          | 15×     |
| 4,096 (16 KiB)   | 33 MB/s    | 559 MB/s          | 558 MB/s          | 17×     |
| 65,536 (256 KiB) | 34 MB/s    | 560 MB/s          | 561 MB/s          | 16×     |
| 262,144 (1 MiB)  | 34 MB/s    | 552 MB/s          | 561 MB/s          | 16×     |

Steady state is ~560 MB/s and flat across sizes: the 1 KiB table fits in L1 and the input
streams past, so the work is compute-bound rather than memory-bound. The smallest row is
dominated by per-call overhead, which is why bitwise looks relatively better there.

**The little-endian byte swap is free.** BE and LE sit within run-to-run noise of each other
at every size from 256 words up — at 1 MiB the LE column even comes out marginally ahead,
which is noise, not a real win. Only the 16-word row shows a gap (≈3%), where per-call
overhead dominates anyway. This is the expected result: the `bswap` acts on the incoming
word, not on the CRC register, so it has no dependency on the previous iteration and
overlaps with the four serially-dependent table lookups that set the pace.

~560 MB/s is about the expected ceiling for one-lookup-per-byte. **Slicing-by-8** — consuming
eight bytes per round from a larger table — typically reaches 2–3 GB/s and would be the next
step if throughput matters.

Hardware CRC instructions are not a drop-in here, for a subtler reason than the polynomial:
they all compute **reflected** CRCs. `Sse42.Crc32` on x86 is Castagnoli-only, so it is out
regardless. ARM's `System.Runtime.Intrinsics.Arm.Crc32` does implement `0x04C11DB7` — but in
reflected form, whereas this library is MSB-first. Bridging that needs a bit-reversal of the
input and output, which ARM can do with `RBIT` but x86 cannot do cheaply. Slicing-by-8 is the
portable answer.

## Project layout

```
CustomCrc32.slnx                  Solution (new-style XML format)
├── CustomCrc32/                  Class library — the implementation
│   └── Crc32.cs
├── CustomCrc32.Test/             NUnit test suite
│   └── Crc32Tests.cs
└── CustomCrc32.Benchmarks/       BenchmarkDotNet console app
    ├── Crc32Benchmarks.cs
    ├── ThroughputColumn.cs
    └── Program.cs
```

Package versions: NUnit 4.6.1, NUnit3TestAdapter 6.2.0, Microsoft.NET.Test.Sdk 18.8.1,
coverlet.collector 10.0.1, BenchmarkDotNet 0.15.8.

## Commands

```bash
dotnet build CustomCrc32.slnx                              # build
dotnet test  CustomCrc32.slnx                              # run tests
dotnet run -c Release --project CustomCrc32.Benchmarks     # run benchmarks
```

Benchmarks must be run against a Release build; BenchmarkDotNet will refuse otherwise.

## Using a different variant

The parameters live as constants at the top of
[`Crc32.cs`](CustomCrc32/Crc32.cs), and the table is derived from `Polynomial` at startup,
so switching polynomial or initial value is a one-line change. Two caveats:

- **Reflected variants need different code, not just different constants.** The common
  zlib / PNG / gzip CRC-32 is *reflected* (`RefIn`/`RefOut` true, polynomial `0xEDB88320` in
  reversed form, `XorOut` `0xFFFFFFFF`). That runs the register in the opposite direction —
  `crc = (crc >> 8) ^ Table[(crc ^ byte) & 0xFF]`, with the table built by shifting right and
  testing the low bit. If you need that variant, .NET already ships it as
  `System.IO.Hashing.Crc32`.
- **A non-zero `XorOut` breaks seed-chaining.** The `Compute(data, seed)` continuation relies
  on the returned value being the raw register state, which holds only while `XorOut` is zero.

If you want the parameters configurable at runtime instead of fixed, the natural shape is a
`Crc32Parameters` record with a per-parameter-set cached table — ask and it can be
restructured that way.
