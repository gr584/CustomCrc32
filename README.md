# CustomCrc32

[![Build and test](https://github.com/gr584/CustomCrc32/actions/workflows/ci.yml/badge.svg)](https://github.com/gr584/CustomCrc32/actions/workflows/ci.yml)

A configurable 32-bit CRC that takes input as **32-bit words** or as **bytes**, whichever your
data actually is. Any parameter set expressible in the Rocksoft/Williams model works,
including reflected variants, and twelve catalogued CRC-32s ship as named presets.

The word entry points are the unusual half: they let you checksum values you hold as `uint`
without materialising a byte buffer, choosing the serialisation order explicitly rather than
inheriting the host's. `ComputeBytes` covers the ordinary case — a file, a packet, a wire
message — at any length.

Runs at **~21 GB/s** on x86-64 with carry-less multiply, for every parameter set rather than
a privileged few, falling back to a table where the instructions are unavailable. Every entry
point is allocation-free and reads the caller's buffer in place.

```csharp
using CustomCrc32;

uint[] words = [0x12345678, 0x9ABCDEF0];

uint crc = Crc32.Mpeg2.Compute(words);      // 0x7D24A31B
uint zlib = Crc32.IsoHdlc.Compute(words);   // the usual "CRC-32"

// Data that is really bytes rather than words has its own entry point, taking any length.
uint ofFile = Crc32.IsoHdlc.ComputeBytes(File.ReadAllBytes("payload.bin"));
```

## Requirements

- .NET 8.0 or newer at runtime. All three projects target `net8.0`.
- **Building through `CustomCrc32.slnx` needs a newer SDK than 8.0.** The `.slnx` solution
  format postdates the 8.0 CLI, which cannot parse it; CI installs the 10.0 SDK for exactly
  that reason, and development is on 10.0.303. With an older SDK, build the individual
  `.csproj` files instead.
- No runtime package dependencies — the library is plain BCL code.
- Hardware acceleration engages automatically where `PCLMULQDQ` and `SSSE3` are available,
  which on x86-64 means anything from Westmere (2010) onwards. Elsewhere — including ARM —
  the table path runs instead and returns identical answers.

## Presets

| Preset       | Polynomial   | Init         | RefIn | RefOut | XorOut       | Check        |
| ------------ | ------------ | ------------ | ----- | ------ | ------------ | ------------ |
| `IsoHdlc`    | `0x04C11DB7` | `0xFFFFFFFF` | yes   | yes    | `0xFFFFFFFF` | `0xCBF43926` |
| `Mpeg2`      | `0x04C11DB7` | `0xFFFFFFFF` | no    | no     | `0x00000000` | `0x0376E6E7` |
| `Bzip2`      | `0x04C11DB7` | `0xFFFFFFFF` | no    | no     | `0xFFFFFFFF` | `0xFC891918` |
| `Castagnoli` | `0x1EDC6F41` | `0xFFFFFFFF` | yes   | yes    | `0xFFFFFFFF` | `0xE3069283` |
| `JamCrc`     | `0x04C11DB7` | `0xFFFFFFFF` | yes   | yes    | `0x00000000` | `0x340BC6D9` |
| `Cksum`      | `0x04C11DB7` | `0x00000000` | no    | no     | `0xFFFFFFFF` | `0x765E7680` |
| `Aixm`       | `0x814141AB` | `0x00000000` | no    | no     | `0x00000000` | `0x3010BF7F` |
| `Autosar`    | `0xF4ACFB13` | `0xFFFFFFFF` | yes   | yes    | `0xFFFFFFFF` | `0x1697D06A` |
| `Base91D`    | `0xA833982B` | `0xFFFFFFFF` | yes   | yes    | `0xFFFFFFFF` | `0x87315576` |
| `CdRomEdc`   | `0x8001801B` | `0x00000000` | yes   | yes    | `0x00000000` | `0x6EC2EDC4` |
| `Mef`        | `0x741B8CD7` | `0xFFFFFFFF` | yes   | yes    | `0x00000000` | `0xD2C22F51` |
| `Xfer`       | `0x000000AF` | `0x00000000` | no    | no     | `0x00000000` | `0xBD0BE338` |

*Check* is the CRC of the ASCII bytes `123456789`, the conventional cross-implementation
anchor. Every one is asserted by the test suite.

`IsoHdlc` is what "CRC-32" normally means — zlib, PNG, gzip, Ethernet, ZIP. `Castagnoli` is
CRC-32C, used by iSCSI, ext4, Btrfs and SCTP.

Two notes on the more surprising entries. `Cksum` is the polynomial POSIX `cksum` uses, but
that utility also appends the message length to the input before checksumming; this library
does not do that for you. And `Mpeg2` is the parameter set this project was originally
written around, kept as a preset.

Each name exists in two places: `Crc32Parameters.Mpeg2` is the parameter *values*, and
`Crc32.Mpeg2` is a ready-to-use shared instance.

## API

```csharp
public readonly record struct Crc32Parameters(
    uint Polynomial,      // normal (non-reversed) form, even for reflected variants
    uint InitialValue,
    bool ReflectInput,
    bool ReflectOutput,
    uint XorOut);

public sealed class Crc32
{
    public Crc32(Crc32Parameters parameters);

    public Crc32Parameters Parameters { get; }
    public uint InitialRegister { get; }

    // Shared preset instances — one per row of the table above.
    public static Crc32 IsoHdlc { get; }
    public static Crc32 Mpeg2 { get; }
    // …and ten more

    // One-shot over words. The suffix selects the byte order the words are serialised in.
    public uint Compute(ReadOnlySpan<uint> data);
    public uint ComputeLittleEndian(ReadOnlySpan<uint> data);

    // One-shot over bytes. Any length; no byte order to choose.
    public uint ComputeBytes(ReadOnlySpan<byte> data);

    // Streaming.
    public uint Append(uint register, ReadOnlySpan<uint> data);
    public uint AppendLittleEndian(uint register, ReadOnlySpan<uint> data);
    public uint AppendBytes(uint register, ReadOnlySpan<byte> data);
    public uint Finish(uint register);
}
```

An instance owns the 256-entry table derived from its parameters, so **construct one per
parameter set and reuse it**. Instances are immutable and safe to use concurrently. The
presets are shared instances, so `Crc32.Mpeg2.Compute(…)` allocates nothing — see
[allocation and copying](#allocation-and-copying).

Inputs are `ReadOnlySpan<uint>` or `ReadOnlySpan<byte>`, so a `Span<T>`, an array, a
stack-allocated buffer, a UTF-8 literal (`"…"u8`) or a collection expression all bind without
a cast.

**Which entry point:** if your data is genuinely *words* — values you hold as `uint` and are
choosing a serialisation for — use `Compute` or `ComputeLittleEndian`. If it is genuinely
*bytes* — a file, a packet, a wire message — use `ComputeBytes`. The distinction is not
cosmetic; see [endianness](#a-note-on-endianness) below.

### Allocation and copying

Every entry point is **allocation-free** and **zero-copy**: no call allocates on the heap, and
none copies the input buffer, at any length.

Measured with `GC.GetAllocatedBytesForCurrentThread` — a running total rather than a sample,
so the figure is exact: **0 bytes**, at every length tried, for both engines and on both sides
of the folding threshold, so the fold path and the pure-table path are each covered. The suite
asserts this for all twelve presets and all six entry points; see
[correctness](#correctness) for how.

**No heap allocation**, because nothing on the path constructs a reference type:

- The register is a `uint` threaded through by value — `Compute` is
  `Finish(Append(InitialRegister, data))`, with no wrapper object, builder or state bag. This
  is the payoff of the functional streaming API over a mutable accumulator type.
- `ReadOnlySpan<T>` is a ref struct, so slicing it and reinterpreting it with
  `MemoryMarshal.AsBytes` yields another struct on the stack, never a new buffer.
- `foreach` over a span uses that span's struct enumerator, so there is no `IEnumerator` to
  allocate.
- The fold constants are `Vector128<T>` fields stored inline in the instance and loaded
  straight into registers. Nothing boxes.

The only allocation in the type is **per instance, in the constructor**: the 256-entry lookup
table (1 KiB) and the object holding it. With a preset that has already happened in a static
initializer, so `Crc32.Mpeg2.Compute(…)` allocates nothing whatsoever.

**Zero-copy**, because blocks are consumed where they lie:

- `FoldBlocks` takes a `ref byte` into the caller's buffer and loads 128 bits at a time with
  `Vector128.LoadUnsafe`. Those loads are unaligned, so there is no alignment copy either.
- The byte-order swap is not a pass over the data. In the fold path it *is* the `pshufb` the
  block already goes through on its way into a register; in the tail it is a `bswap` on a word
  already in a register. Neither writes to memory — which is the same point made under
  [endianness](#a-note-on-endianness), and the reason never to pre-swap into a scratch array.
- The input is `ReadOnlySpan<…>` throughout, so it is not mutated in place either.

The one trap is at the **call site**, not in the library. Range-indexing an *array* copies;
range-indexing a *span* does not:

```csharp
crc.ComputeLittleEndian(data[..1000]);           // allocates a 4 KiB uint[] copy
crc.ComputeLittleEndian(data.AsSpan()[..1000]);  // free
```

This is pinned rather than merely observed: `Preset_WordEntryPoints_AllocateNothing` and
`Preset_ByteEntryPoints_AllocateNothing` assert it for all twelve presets, so an edit that
introduced a temporary buffer would fail the build — see [correctness](#correctness).

### Custom parameters

```csharp
var crc32 = new Crc32(new Crc32Parameters(
    Polynomial:    0x04C11DB7,
    InitialValue:  0xFFFFFFFF,
    ReflectInput:  false,
    ReflectOutput: false,
    XorOut:        0x00000000));

uint crc = crc32.Compute(words);
```

Give the polynomial in **normal form** even for a reflected variant — pass `0x04C11DB7`, not
`0xEDB88320`. The reversal is applied internally.

### Streaming

`Compute` is `Finish(Append(InitialRegister, data))`. Splitting those apart lets you
checksum a stream in pieces:

```csharp
uint register = crc32.InitialRegister;
while (TryReadChunk(out ReadOnlySpan<uint> chunk))
{
    register = crc32.Append(register, chunk);
}
uint crc = crc32.Finish(register);
```

`Append` returns the **raw register**, not a CRC — output reflection and the final XOR are
applied by `Finish`. Keep them apart: feeding a finished value back into `Append` gives a
wrong answer for any parameter set with a non-zero `XorOut` or mismatched reflection.

## A note on endianness

This section is about the **word** entry points. `ComputeBytes` has no endianness question at
all — a byte buffer already carries its own order — so if that is what you are using, skip to
[the next section](#when-the-data-is-really-bytes).

Where the input is typed as `uint` rather than as raw bytes, **the host machine's endianness
never enters into it**. The algorithm consumes each word bit by bit regardless of how that
word is laid out in memory. The choice between `Compute` and `ComputeLittleEndian` is about
the byte order your words will be *serialised* in, and the same call returns the same answer
on x64, ARM, or anything else.

- `Compute` — each word contributes its most significant byte first: `0x12345678` is
  checksummed as the bytes `12 34 56 78`.
- `ComputeLittleEndian` — least significant byte first: `78 56 34 12`.

Each engine folds a word naturally in one of those two directions and byte-swaps for the
other. Which is which flips with `ReflectInput`: the forward engine's natural direction is
big-endian, the reflected engine's is little-endian.

The swap is free either way. In the folding path it is absorbed into the `pshufb` that each
block already goes through, so it costs nothing at all; in the table path it is a single
`bswap` on the incoming word rather than on the CRC register, so it carries no dependency on
the previous iteration. Measured at every size and in both engines — see the benchmarks
below. Never pre-swap the buffer with `BinaryPrimitives.ReverseEndianness`; that allocates
and adds a pass.

### When the data is really bytes

If your data arrives as a **byte** buffer, use `ComputeBytes` and stop thinking about byte
order — a byte buffer already carries its own.

```csharp
byte[] packet = File.ReadAllBytes("payload.bin");

uint crc = Crc32.IsoHdlc.ComputeBytes(packet);        // the usual "CRC-32" of a file
uint check = Crc32.Mpeg2.ComputeBytes("123456789"u8); // 0x0376E6E7, the catalogue check value
```

Any length works, including lengths that are not a multiple of four, and a stream may be
split **anywhere** — `AppendBytes` has no alignment to respect, whereas the word entry points
can only express splits on a four-byte boundary:

```csharp
uint register = crc32.InitialRegister;
while (TryReadChunk(out ReadOnlySpan<byte> chunk))   // chunks of any size, even 1 byte
{
    register = crc32.AppendBytes(register, chunk);
}
uint crc = crc32.Finish(register);
```

**Do not reinterpret the buffer as `uint` instead.** `MemoryMarshal.Cast<byte, uint>` reads
each group of four bytes in the *host's* order, so the buffer `12 34 56 78` becomes
`0x78563412` on a little-endian machine and `0x12345678` on a big-endian one — silently a
different answer per platform, no error, and a trailing partial word cannot be represented at
all. Concretely, on a little-endian host the cast happens to line up with
`ComputeLittleEndian` and *not* with `Compute`, which is exactly the kind of accident that
survives testing on one architecture and fails on another. There is a test pinning that
relationship so the trap stays documented rather than rediscovered.

`ComputeBytes` is deliberately **not** an overload of `Compute`: overloading on
`ReadOnlySpan<byte>` alongside `ReadOnlySpan<uint>` would make collection-expression calls
like `Compute([])` ambiguous.

Performance is unaffected by the choice — the byte path folds through the same accelerated
kernel and runs at the same throughput, with 0–15 trailing bytes finished by the table.

## Implementation notes

There are two layers: a carry-less-multiply fold that does the bulk of the work where the
hardware allows, and a table that handles short inputs, the tail, and machines without the
instructions. Both are driven by the same parameter set and produce identical answers — one
test appends a single word per call and another a single byte per call, keeping every call
under the folding threshold, and both require the result to match the single-shot folded call.

All three entry points share one fold kernel. `Append`, `AppendLittleEndian` and `AppendBytes`
differ only in the byte permutation they hand it and in how they finish the tail, so the
byte API is not a second implementation to keep in step.

### Carry-less multiply folding

Where `PCLMULQDQ` and `SSSE3` are present, whole 128-bit blocks are folded with
`Pclmulqdq.CarrylessMultiply`. Rather than reduce modulo the polynomial at every step, the
accumulator is kept merely *congruent* to the message: folding a block ahead means
multiplying by x¹²⁸ and substituting that power's residue, which is what the precomputed
constants hold.

```
accumulator = clmul(accumulator.lo, K.lo) ^ clmul(accumulator.hi, K.hi) ^ next_block
```

The constants are derived from the polynomial at construction, so this works for arbitrary
parameters and not merely the presets. For folding *n* bits ahead:

- **Forward**: `K.lo = xⁿ mod P`, `K.hi = xⁿ⁺⁶⁴ mod P`.
- **Reflected**: `K.lo = reverse(xⁿ⁺³² mod P) << 1`, `K.hi = reverse(xⁿ⁻³² mod P) << 1`. The
  ±32 offset is the reflected register living at the bottom of the word rather than the top;
  the `<< 1` is the bit-reversal of a product landing one place over.

Four independent accumulators run at once, so the multiply's latency overlaps instead of
stalling a single chain, and they are combined with single-block constants at the end.

Loading a block needs at most one `pshufb`, and which permutation depends on both the engine
and the requested byte order — reflected plus little-endian is already in the right order and
moves nothing.

Finishing is where the two layers meet. Because the accumulator is congruent to the message,
running its four words through the *table* from a zero register lands on exactly the register
the table path would have reached, after which any leftover words are appended normally. That
keeps the reduction short and, more usefully, means there is only one definition of what the
register means.

A sanity check on the derivation: for polynomial `0x04C11DB7` reflected, it produces
`0x1751997d0` and `0xccaa009e` — the constants published for zlib.

### The table

Each instance derives a 256-entry table mapping a byte to the register state produced by
clocking it through eight rounds, so a whole byte folds in with one lookup.

There are two engines, selected by `ReflectInput`, because a reflected CRC is not the same
algorithm with different constants — it runs the register the other way:

|                | Forward (`ReflectInput: false`)         | Reflected (`ReflectInput: true`)      |
| -------------- | --------------------------------------- | ------------------------------------- |
| Register step  | `(r << 8) ^ table[r >> 24]`             | `(r >> 8) ^ table[r & 0xFF]`          |
| Table entry    | from `i << 24`, shifting left           | from `i`, shifting right              |
| Polynomial     | as given                                | bit-reversed once, at construction    |
| Start register | `InitialValue`                          | `Reverse(InitialValue)`               |
| Natural fold   | big-endian                              | little-endian                         |

A reflected CRC is run with the register held bit-reversed throughout, which is why the
initial value is reversed on the way in. On the way out, `ReflectOutput` asks for the
reversed register — which is what is already there — so `Finish` reverses only when
`ReflectInput != ReflectOutput`.

Each word is folded whole and then clocked through the 32 rounds it owes, a byte per lookup:

```csharp
register ^= word;
register = (register << 8) ^ table[register >> 24];   // ×4
```

That fold-then-shift order is what makes it bit-identical to the conventional byte-at-a-time
loop fed `b3, b2, b1, b0`.

<details>
<summary>Why word-at-a-time equals byte-at-a-time</summary>

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
tested, so no reduction occurs and `S⁸` degenerates to a plain `<<8`. Applying that identity
to each staggered term lines the two expansions up exactly. The reflected engine is the
mirror image, and the same argument holds with the shifts reversed.

Verified numerically across all four `ReflectInput`/`ReflectOutput` combinations with random
polynomials, initial values and final XORs before the implementation was written.
</details>

## Correctness

The test suite ([`CustomCrc32.Test`](CustomCrc32.Test/Crc32Tests.cs), NUnit, 276 tests) is
built on a single oracle: **Williams' model spelled out literally** — feed each byte in at
the top of the register, clock it one bit at a time, reflect on the way in and out where
asked. It is deliberately naive and structurally unlike the table-driven implementation, and
its own bit reversals are loop-based rather than the library's bit-twiddling version, so the
two are unlikely to share a mistake.

That oracle is anchored by the twelve published check values: running each preset's
parameters over `123456789` must reproduce the catalogued constant. Passing means the preset
constants *and* the reference model are both right — agreeing by accident on a 32-bit value
twelve times over is not a thing that happens.

Everything else is then checked against the oracle:

- Every preset, both byte orders, over inputs of length 0–40 words.
- **Arbitrary parameters** — 400 random parameter sets, both byte orders, at lengths spanning
  both sides of the folding threshold. This is the load-bearing test: the presets all have
  `RefIn == RefOut` and a bit-reversal-palindrome initial value, so only random parameters
  exercise mismatched reflection and asymmetric inits.
- **The two layers against each other** — appending one word at a time keeps every call below
  the folding threshold and so forces the table, while the single-shot call over the same data
  folds; they must agree.
- **Folding boundaries** — thirty lengths straddling the engagement threshold, the four-block
  unrolled loop, its remainder, and the sub-block tail.
- **Split invariance** — the same input broken at every seventh word must give the same answer
  as one pass, which it would not if the accumulator carried state the register cannot express.
- Streaming: chunked `Append`/`AppendLittleEndian` then `Finish` must equal the one-shot
  result, for every preset, including a zero-length chunk.
- `InitialRegister` is the reversed initial value when reflected and unchanged when not,
  pinned with `0x0000FFFF` — a value that would survive a no-op "reversal".
- Byte order: `ComputeLittleEndian(data)` equals `Compute(swapped)` for every preset, and
  differs from `Compute(data)` on an asymmetric word.
- All twelve presets produce distinct results for the same input.
- The fifteen MPEG-2 values this library produced before `Crc32Parameters` existed still
  hold, so the migration is known not to have moved any answers.

The byte path adds a stronger anchor than any of these, because it needs no oracle at all:
`ComputeBytes("123456789"u8)` is held directly against the twelve published check values,
which are *defined* over bytes. Beyond that it is checked at every length from 0 to 80 bytes
and around the larger fold boundaries, split at **every** offset in a 300-byte buffer rather
than on word boundaries, against byte-at-a-time appending, against `Compute` over the same
big-endian serialisation, on empty input, and over 400 random parameter sets. One further test pins the
`MemoryMarshal.Cast` relationship described above — on a little-endian host the cast agrees
with `ComputeLittleEndian` and disagrees with `Compute` — so the trap stays documented.

Twenty-four further tests check something no oracle can express: that the entry points
**allocate nothing**. Each warms the JIT and the presets' static initialiser, then requires a
further 128 calls to add exactly zero bytes to the thread's allocation total — across all
twelve presets, all six entry points, lengths either side of the folding threshold, and byte
lengths that leave a partial-word tail. `GC.GetAllocatedBytesForCurrentThread` is a running
total rather than a sample, so the assertion is exact and there is no tolerance to tune. See
[allocation and copying](#allocation-and-copying).

Mutation checks confirm the suite is not vacuous. Dropping the `<< 1` from the reflected fold
constant fails 50 tests; shifting the forward constant's exponent by one fails 36; flipping a
bit of the polynomial fails 7. Removing the initial-value reversal fails exactly the two tests
written for that gap, since — every preset having a palindromic initial value — nothing else
can catch it. On the byte path: using the wrong block permutation fails 73, reading the tail
words in the wrong byte order fails 31, and dropping the input byte from either engine's
single-byte step fails 36 and 26. And a `data.ToArray()` slipped into `AppendLittleEndian` —
a copy that moves no answer at all — fails the twelve word-allocation tests and **nothing
else**, which is exactly the gap those tests exist to close.

## Benchmarks

[`CustomCrc32.Benchmarks`](CustomCrc32.Benchmarks/) compares the shipping implementation
against the two formulations it replaced — one bit at a time, and one table lookup per byte —
across input sizes chosen to sit at different levels of the memory hierarchy. The table
baselines are reimplemented inside the benchmark rather than called through the library,
since the library now folds automatically and there is no supported way to ask it not to.
`GlobalSetup` throws if any two paths that should agree have diverged — a speed comparison
stops meaning anything once the baseline is wrong. That check covers `ComputeBytes` too: the
byte buffer holds the big-endian serialisation of the same words, so it must return what
`Compute` does.

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
iterations) — good enough for the headline ratios, but re-run with the default job before
quoting these anywhere.

Throughput, higher is better. *Table* is the previous implementation, kept here as the
baseline the fold replaced:

| WordCount        | Bitwise  | Table fwd | Table refl | Fold fwd BE | Fold fwd LE | Fold refl BE | Fold refl LE |
| ---------------- | -------- | --------- | ---------- | ----------- | ----------- | ------------ | ------------ |
| 16 (64 B)        | 111 MB/s | 663 MB/s  | 766 MB/s   | 2.46 GB/s   | 2.52 GB/s   | 2.55 GB/s    | 2.81 GB/s    |
| 256 (1 KiB)      | 38 MB/s  | 570 MB/s  | 642 MB/s   | 14.5 GB/s   | 14.3 GB/s   | 15.0 GB/s    | 15.1 GB/s    |
| 4,096 (16 KiB)   | 34 MB/s  | 566 MB/s  | 623 MB/s   | 21.1 GB/s   | 21.4 GB/s   | 21.6 GB/s    | 21.9 GB/s    |
| 65,536 (256 KiB) | 34 MB/s  | 559 MB/s  | 634 MB/s   | 21.0 GB/s   | 22.0 GB/s   | 22.2 GB/s    | 22.1 GB/s    |
| 262,144 (1 MiB)  | 34 MB/s  | 567 MB/s  | 636 MB/s   | 21.3 GB/s   | 21.5 GB/s   | 21.5 GB/s    | 21.5 GB/s    |

**Folding is ~38× the table** at steady state and ~620× the bitwise loop. It reaches roughly
5.7 bytes per cycle, which for a 4 KiB working set is bounded by carry-less multiply
throughput rather than memory.

**Small inputs still gain.** At 64 bytes — the smallest size measured, and only two blocks
above the engagement threshold — folding is around 4× the table. Below the threshold the
table runs instead, and the crossover was measured rather than assumed: at one block the two
are level, at two blocks folding is 1.9× ahead.

**The forward/reflected gap has closed.** With the table it was a consistent 15%, because the
forward engine's `r >> 24` index sat on the load-address dependency chain where the reflected
engine's `r & 0xFF` did not. Folding does not care: both are ~21.5 GB/s, and the table now
only reduces the accumulator and mops up the tail.

**The byte swap is still free**, in both engines and at every size. It is now a `pshufb` on
the loaded block rather than a `bswap` on a word, but the reasoning is unchanged — it does
not sit on the accumulator's dependency chain.

#### `ComputeBytes` costs nothing extra

Taken as a **separate run** from the table above, so read the columns against each other and
not against the figures above — a short job varies by about 10% between runs, and this one
happened to land faster across the board.

| WordCount        | Fold fwd BE | Fold fwd bytes | Fold refl BE | Fold refl bytes |
| ---------------- | ----------- | -------------- | ------------ | --------------- |
| 16 (64 B)        | 2.67 GB/s   | 2.48 GB/s      | 2.87 GB/s    | 2.66 GB/s       |
| 256 (1 KiB)      | 15.2 GB/s   | 15.2 GB/s      | 16.1 GB/s    | 16.5 GB/s       |
| 4,096 (16 KiB)   | 23.7 GB/s   | 23.9 GB/s      | 23.8 GB/s    | 23.9 GB/s       |
| 65,536 (256 KiB) | 23.9 GB/s   | 23.7 GB/s      | 24.2 GB/s    | 24.5 GB/s       |
| 262,144 (1 MiB)  | 23.1 GB/s   | 23.5 GB/s      | 22.8 GB/s    | 23.5 GB/s       |

From 1 KiB up the byte path is level with the word path — within ±3%, which is inside the
run-to-run spread of a short job. That is the expected result: the same fold kernel over the
same bytes, differing only in the permutation constant it is handed.

The 64-byte row looks like a 7% deficit but should not be read that way. At a ~24 ns mean the
short job reports a 99.9% confidence interval of ±7 to ±14 ns, so that column says nothing
either way; it is there for completeness, not as a finding.

### Going further

Nothing here is the ceiling.

**VPCLMULQDQ** folds 256 or 512 bits per instruction instead of 128 and would multiply this
again, but it needs Ice Lake or newer, and .NET 8 does not expose `Pclmulqdq.V256` at all —
that arrived later. This machine is Coffee Lake, so it was not an option to measure.

**`Sse42.Crc32`** is worth a mention and a warning. It is a single instruction computing a
CRC directly, but it is hardwired to the Castagnoli polynomial, so it could accelerate
`Crc32.Castagnoli` and nothing else. Given folding already reaches ~21 GB/s for *every*
parameter set, a special case for one preset did not seem worth the branch.

The `pshufb` on each load could be skipped in the reflected little-endian case, where the
permutation is the identity. On this microarchitecture shuffles and carry-less multiplies
compete for the same port, so it may be worth a little; it was left in for simplicity. Note
that this case is more common than it first looks: `ComputeBytes` uses the little-endian
permutation too, so every reflected preset — `IsoHdlc` and `Castagnoli` included — takes the
identity shuffle when checksumming a byte buffer.

## Project layout

```
CustomCrc32.slnx                  Solution (new-style XML format)
├── .github/workflows/ci.yml      Build and test on every push and pull request
├── CustomCrc32/                  Class library — the implementation
│   ├── Crc32.cs                  Engines, folding, presets, streaming API
│   └── Crc32Parameters.cs        Parameter model and preset values
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

The two solution-level commands need an SDK that understands `.slnx` — see
[Requirements](#requirements). Point them at the individual `.csproj` files on an older one.

Benchmarks must be run against a Release build; BenchmarkDotNet will refuse otherwise.

## Migrating from the hard-coded version

Before `Crc32Parameters`, `Crc32` was a static class fixed to CRC-32/MPEG-2.

| Before                            | After                              |
| --------------------------------- | ---------------------------------- |
| `Crc32.Compute(data)`             | `Crc32.Mpeg2.Compute(data)`        |
| `Crc32.ComputeLittleEndian(data)` | `Crc32.Mpeg2.ComputeLittleEndian(data)` |
| `Crc32.Polynomial`                | `Crc32Parameters.Mpeg2.Polynomial` |
| `Crc32.InitialValue`              | `Crc32Parameters.Mpeg2.InitialValue` |
| `Crc32.XorOut`                    | `Crc32Parameters.Mpeg2.XorOut`     |
| `Crc32.Compute(data, seed)`       | `Append` / `Finish` — see below    |

Results are unchanged; the old MPEG-2 values are still asserted by the test suite.

The seeded overload is the one real break. It used to double as both "resume from here" and
"here is your answer", which worked only because MPEG-2 has a zero `XorOut` and symmetric
reflection. That no longer holds in general, so resuming and finishing are now separate
operations:

```csharp
// before
uint crc = Crc32.Compute(tail, Crc32.Compute(head));

// after
uint register = Crc32.Mpeg2.Append(Crc32.Mpeg2.Append(Crc32.Mpeg2.InitialRegister, head), tail);
uint crc = Crc32.Mpeg2.Finish(register);
```
