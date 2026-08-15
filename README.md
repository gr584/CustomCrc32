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
a privileged few, falling back to a table where the instructions are unavailable. That figure
is for data already in cache; over a stream arriving from memory it is ~15 GB/s, which is
[most of what the memory system will give one core](#streaming-versus-resident-data). Every
entry point is allocation-free and reads the caller's buffer in place.

```csharp
using CustomCrc32;

uint[] words = [0x12345678, 0x9ABCDEF0];

uint crc = Crc32.Mpeg2.ComputeBigEndian(words);      // 0x7D24A31B
uint zlib = Crc32.IsoHdlc.ComputeBigEndian(words);   // the usual "CRC-32"

// Data that is really bytes rather than words has its own entry point, taking any length.
uint ofFile = Crc32.IsoHdlc.ComputeBytes(File.ReadAllBytes("payload.bin"));
```

## Requirements

- .NET 8.0 or newer at runtime. The library and its tests target `net8.0`; the benchmark
  project additionally builds for `net10.0`, for [one comparison](#against-systemiohashing)
  whose dependency ships its accelerated code no lower.
- **Building through `CustomCrc32.slnx` needs a newer SDK than 8.0.** The `.slnx` solution
  format postdates the 8.0 CLI, which cannot parse it; CI installs the 10.0 SDK for exactly
  that reason, and development is on 10.0.303. With an older SDK, build the individual
  `.csproj` files instead.
- No runtime package dependencies — the library is plain BCL code.
- Hardware acceleration engages automatically where `PCLMULQDQ` and `SSSE3` are available,
  which on x86-64 means anything from Westmere (2010) onwards. Elsewhere — including ARM —
  the table path runs instead and returns identical answers. `Crc32.IsHardwareAccelerated`
  reports which of the two you are getting, for logging rather than for branching.

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
does not do that for you. And `Mpeg2` shares `IsoHdlc`'s polynomial but runs unreflected with
no final XOR, which is why the two give completely different answers over the same input.

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

    // True where the carry-less multiply fold is available. Reports speed, not behaviour —
    // the answer is identical either way.
    public static bool IsHardwareAccelerated { get; }

    // Shared preset instances — one per row of the table above.
    public static Crc32 IsoHdlc { get; }
    public static Crc32 Mpeg2 { get; }
    // …and ten more

    // One-shot over words. The suffix selects the byte order the words are serialised in.
    public uint ComputeBigEndian(ReadOnlySpan<uint> data);
    public uint ComputeLittleEndian(ReadOnlySpan<uint> data);

    // One-shot over bytes. Any length; no byte order to choose.
    public uint ComputeBytes(ReadOnlySpan<byte> data);

    // Streaming.
    public uint AppendBigEndian(uint register, ReadOnlySpan<uint> data);
    public uint AppendLittleEndian(uint register, ReadOnlySpan<uint> data);
    public uint AppendBytes(uint register, ReadOnlySpan<byte> data);
    public uint Finish(uint register);
}
```

An instance owns the 256-entry table derived from its parameters, so **construct one per
parameter set and reuse it**. Instances are immutable and safe to use concurrently. The
presets are shared instances, so `Crc32.Mpeg2.ComputeBigEndian(…)` allocates nothing — see
[allocation and copying](#allocation-and-copying).

Inputs are `ReadOnlySpan<uint>` or `ReadOnlySpan<byte>`, so a `Span<T>`, an array, a
stack-allocated buffer, a UTF-8 literal (`"…"u8`) or a collection expression all bind without
a cast.

**Which entry point:** if your data is genuinely *words* — values you hold as `uint` and are
choosing a serialisation for — use `ComputeBigEndian` or `ComputeLittleEndian`. If it is
genuinely *bytes* — a file, a packet, a wire message — use `ComputeBytes`. The distinction is
not cosmetic; see [endianness](#a-note-on-endianness) below.

### Allocation and copying

Every entry point is **allocation-free** and **zero-copy**: no call allocates on the heap, and
none copies the input buffer, at any length.

Measured with `GC.GetAllocatedBytesForCurrentThread` — a running total rather than a sample,
so the figure is exact: **0 bytes**, at every length tried, for both engines and on both sides
of the folding threshold, so the fold path and the pure-table path are each covered. The suite
asserts this for all twelve presets and all six entry points; see
[correctness](#correctness) for how.

**No heap allocation**, because nothing on the path constructs a reference type:

- The register is a `uint` threaded through by value — `ComputeBigEndian` is
  `Finish(AppendBigEndian(InitialRegister, data))`, with no wrapper object, builder or state
  bag. This is the payoff of the functional streaming API over a mutable accumulator type.
- `ReadOnlySpan<T>` is a ref struct, so slicing it and reinterpreting it with
  `MemoryMarshal.AsBytes` yields another struct on the stack, never a new buffer.
- `foreach` over a span uses that span's struct enumerator, so there is no `IEnumerator` to
  allocate.
- The fold constants are `Vector128<T>` fields stored inline in the instance and loaded
  straight into registers. Nothing boxes.

The only allocation in the type is **per instance, in the constructor**: the 256-entry lookup
table (1 KiB) and the object holding it. With a preset that has already happened in a static
initializer, so `Crc32.Mpeg2.ComputeBigEndian(…)` allocates nothing whatsoever.

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

uint crc = crc32.ComputeBigEndian(words);
```

Give the polynomial in **normal form** even for a reflected variant — pass `0x04C11DB7`, not
`0xEDB88320`. The reversal is applied internally.

### Streaming

`ComputeBigEndian` is `Finish(AppendBigEndian(InitialRegister, data))`. Splitting those apart
lets you checksum a stream in pieces:

```csharp
uint register = crc32.InitialRegister;
while (TryReadChunk(out ReadOnlySpan<uint> chunk))
{
    register = crc32.AppendBigEndian(register, chunk);
}
uint crc = crc32.Finish(register);
```

`AppendBigEndian` returns the **raw register**, not a CRC — output reflection and the final
XOR are applied by `Finish`. Keep them apart: feeding a finished value back into
`AppendBigEndian` gives a wrong answer for any parameter set with a non-zero `XorOut` or
mismatched reflection.

## A note on endianness

This section is about the **word** entry points. `ComputeBytes` has no endianness question at
all — a byte buffer already carries its own order — so if that is what you are using, skip to
[the next section](#when-the-data-is-really-bytes).

Where the input is typed as `uint` rather than as raw bytes, **the host machine's endianness
never enters into it**. The algorithm consumes each word bit by bit regardless of how that
word is laid out in memory. The choice between `ComputeBigEndian` and `ComputeLittleEndian`
is about the byte order your words will be *serialised* in, and the same call returns the
same answer on x64, ARM, or anything else.

- `ComputeBigEndian` — each word contributes its most significant byte first: `0x12345678` is
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
`ComputeLittleEndian` and *not* with `ComputeBigEndian`, which is exactly the kind of accident
that survives testing on one architecture and fails on another. There is a test pinning that
relationship so the trap stays documented rather than rediscovered.

`ComputeBytes` is deliberately **not** an overload of `ComputeBigEndian`: overloading on
`ReadOnlySpan<byte>` alongside `ReadOnlySpan<uint>` would make collection-expression calls
like `ComputeBigEndian([])` ambiguous.

Performance is unaffected by the choice — the byte path folds through the same accelerated
kernel and runs at the same throughput, with 0–15 trailing bytes finished by the table.

## Implementation notes

There are two layers: a carry-less-multiply fold that does the bulk of the work where the
hardware allows, and a table that handles short inputs, the tail, and machines without the
instructions. Both are driven by the same parameter set and produce identical answers — one
test appends a single word per call and another a single byte per call, keeping every call
under the folding threshold, and both require the result to match the single-shot folded call.

All three entry points share one fold kernel. `AppendBigEndian`, `AppendLittleEndian` and
`AppendBytes` differ only in the byte permutation they hand it and in how they finish the
tail, so the byte API is not a second implementation to keep in step.

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
stalling a single chain, and they are combined with single-block constants at the end — see
[four accumulator lanes](#four-accumulator-lanes) for why four, and why four statements in a
row run faster than one.

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

### Four accumulator lanes

The inner loop of `FoldBlocks` is four ordinary statements in a row:

```csharp
accumulator = FoldBlock(accumulator, _foldByFourBlocks, LoadBlock(ref source, next,     shuffle));
second      = FoldBlock(second,      _foldByFourBlocks, LoadBlock(ref source, next + 1, shuffle));
third       = FoldBlock(third,       _foldByFourBlocks, LoadBlock(ref source, next + 2, shuffle));
fourth      = FoldBlock(fourth,      _foldByFourBlocks, LoadBlock(ref source, next + 3, shuffle));
```

Nothing there is parallel in any sense C# expresses. No threads, no wider vectors, and four
times as much work per iteration. It is nevertheless about **three times faster** than folding
one block at a time — and the reason is what these statements *do not* do.

**Read what each line depends on.** No statement reads a value another statement in the same
iteration writes. `second` does not wait on `accumulator`; `third` waits on neither. The
dependency edges run *vertically*, through iterations — `accumulator` this time round needs
`accumulator` from the last — and never *horizontally* within one. They are four disjoint
chains threaded through a shared loop body.

That is what makes the difference, because `PCLMULQDQ` is **pipelined**: it takes around seven
cycles to produce a result, but a new one can be started every cycle. Processors execute out
of order, issuing any instruction whose inputs are ready rather than waiting its turn in
program order — so the second line's multiply starts the cycle after the first line's, not
seven cycles later. The four loads are likewise independent and run ahead of all of it.

The contrast is in the same file. The remainder loop just below folds one block at a time, and
each of its iterations must wait for the previous result: about ten cycles per block — the
multiply, then the two XORs hanging off it — with the multiplier idle for eight of them. Four
lanes retire four blocks in those same ten cycles.

**Why four and not sixteen.** The gain stops because the bottleneck moves. On this
microarchitecture `PCLMULQDQ` and `PSHUFB` issue to the *same* execution port, so every block
costs three slots on it: one permutation and two multiplies. Four lanes demand twelve slots
per iteration against a ten-cycle chain — just past the point where the port rather than the
latency is the limit. Three lanes would leave it partly idle; eight cannot make a saturated
port go faster. Four is the smallest lane count that fills it.

Both halves of that are measured rather than assumed — [fold lanes](#fold-lanes) below. Two
lanes come in within a few percent of 2.00×, the signature of a loop still bound by latency
with room to spare. Eight lanes stay within 3% of four either way on cached data, and lose at
4 KiB where the longer merge is amortised over fewer blocks. And removing the identity
`pshufb` — one of the three port slots, doing no work at all — is worth 16–20%, which a
latency-bound loop would not have noticed.

**Recombining the lanes.** After the loop, lane *i* holds blocks *i*, *i*+4, *i*+8, … folded
together with the four-block constant, and the four lanes sit exactly one block apart in the
message. Each merge step multiplies by x¹²⁸ and adds the next lane:

```
accumulator ← A₀·x¹²⁸ + A₁
accumulator ← A₀·x²⁵⁶ + A₁·x¹²⁸ + A₂
accumulator ← A₀·x³⁸⁴ + A₁·x²⁵⁶ + A₂·x¹²⁸ + A₃
```

That last line is precisely the residue of the interleaved message: each of lane 0's blocks
sits three block positions ahead of lane 3's, lane 1's two, lane 2's one. The staggering is
undone exactly, and nothing is reduced to 32 bits along the way — the accumulator stays merely
congruent to the message right up to the final reduction.

Two details fall out of this shape. The incoming register seeds **lane 0 only**, because it
conceptually prepends to the message, and block 0 is where the message starts. And the
`blocks >= 4` guard is a *seeding* requirement, not a loop requirement: between four and seven
blocks the loop body never executes at all, but the lanes are still seeded, still merged, and
the by-one remainder loop still mops up — which is why the boundary tests cover those lengths
specifically.

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

The test suite ([`CustomCrc32.Test`](CustomCrc32.Test/Crc32Tests.cs), NUnit, 375 tests) is
built on two independent oracles. The first is **Williams' model spelled out literally** — feed
each byte in at the top of the register, clock it one bit at a time, reflect on the way in and
out where asked. It is deliberately naive and structurally unlike the table-driven
implementation, and its own bit reversals are loop-based rather than the library's
bit-twiddling version, so the two are unlikely to share a mistake. The second is
[`System.IO.Hashing`](#a-second-oracle), described below.

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
- Streaming: chunked `AppendBigEndian`/`AppendLittleEndian` then `Finish` must equal the
  one-shot result, for every preset, including a zero-length chunk.
- `InitialRegister` is the reversed initial value when reflected and unchanged when not,
  pinned with `0x0000FFFF` — a value that would survive a no-op "reversal".
- Byte order: `ComputeLittleEndian(data)` equals `ComputeBigEndian(swapped)` for every preset,
  and differs from `ComputeBigEndian(data)` on an asymmetric word.
- All twelve presets produce distinct results for the same input.
- Fifteen MPEG-2 answers pinned as literals rather than derived from the oracle, so a
  regression still surfaces if implementation and oracle ever drifted together.

The byte path adds a stronger anchor than any of these, because it needs no oracle at all:
`ComputeBytes("123456789"u8)` is held directly against the twelve published check values,
which are *defined* over bytes. Beyond that it is checked at every length from 0 to 80 bytes
and around the larger fold boundaries, split at **every** offset in a 300-byte buffer rather
than on word boundaries, against byte-at-a-time appending, against `ComputeBigEndian` over
the same big-endian serialisation, on empty input, and over 400 random parameter sets. One
further test pins the `MemoryMarshal.Cast` relationship described above — on a little-endian
host the cast agrees with `ComputeLittleEndian` and disagrees with `ComputeBigEndian` — so the
trap stays documented.

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

### A second oracle

Ninety-eight of those tests check this library against `System.IO.Hashing` rather than against
the reference model. Its 11.0 pre-release takes a configurable parameter set, so for the first
time there is a second implementation covering the same ground rather than one preset of it.

It makes an unusually good oracle. It was written by other people, from the catalogue rather
than from this code — and on this `net8.0` target it resolves the package's `netstandard2.0`
asset, which carries no hardware intrinsics at all. So these tests hold a carry-less multiply
fold against an implementation that does not fold. Where the machine lacks `PCLMULQDQ` or
`SSSE3` they still pass, having compared two non-folding implementations instead, so every
assertion message carries `Crc32.IsHardwareAccelerated` and a failure says which path was
actually running.

The second oracle is anchored exactly as the first is: the package, driven through a parameter
set built from this library's, must reproduce all twelve published check values. Passing means
the parameter models really do line up, which everything else here depends on. Then, for every
preset:

- `ComputeBytes` at every length from 0 to 80 bytes, and at eighteen lengths around the larger
  fold boundaries.
- **Large buffers** — 4 KiB, 16 KiB, 64 KiB and 256 KiB, with tails of 1, 37 and 63 bytes.
  These run the four-block unrolled loop thousands of times rather than the handful the rest of
  the suite manages, which is as close as the tests come to what the benchmarks measure.
- Both word entry points over 0–40 words, and at the same thirty lengths straddling the
  engagement threshold, the unrolled loop, its remainder and the sub-block tail.
- **Both incremental paths against each other** — `AppendBytes` and the package's `Append` over
  thirteen chunks that align to neither words nor blocks, compared after every one.
- **Arbitrary parameters** — the random sets from the test above, restricted to those the
  package can express.

Mutation confirms they are not decorative: making the four-block fold constant off by one
fails 73 of the 98.

<details>
<summary>The one place the two parameter models disagree</summary>

`Crc32ParameterSet` collapses input and output reflection into a single flag, so a set
reflecting one but not the other has no equivalent — those are skipped, and the reference
model remains the only oracle covering them. That much was evident from the signature.

The other difference was not, and the random-parameter test is what found it. **For a
reflected set, the package's `initialValue` is the register to start from, not the catalogue's
`Init`.** This library follows the catalogue, where `Init` is stated in the unreflected domain
and a reflected engine starts from its bit reversal — the distinction
[`InitialRegister`](#api) exists to express. Handing `Init` over unreversed disagrees on every
reflected input.

Every catalogued CRC-32 hides this. All seven reflected presets here initialise to
`0xFFFFFFFF` or `0x00000000`, and a bit-reversal palindrome cannot tell the two conventions
apart — which is why the twelve check values pass under either reading, and why nothing short
of random parameters was going to surface it. Verified against Williams' model directly across
20,000 random sets: this library matches it on all of them, the package on all of them once
the reversal is applied and essentially none of them without.

Neither convention is wrong; they are different parameterisations that agree on every CRC
anyone has catalogued. The translation is applied where the parameter sets are built, and
`ReflectedInitialValue_IsTakenInTheReflectedDomain` pins it so a change of convention on either
side surfaces as one clear failure rather than as noise across the cross-checks.
</details>

One caveat worth stating plainly: `System.IO.Hashing` 11.0 declares `net10.0` as its lowest
supported target and warns when referenced from `net8.0`. The `netstandard2.0` asset that
resolves instead is wanted here rather than merely tolerated, for the independence described
above, so the warning is suppressed in the test project — but not taken on trust. The
check-value anchor runs on that exact target framework, so an unsupported configuration that
stopped working would fail the suite rather than quietly weaken an oracle.

## Benchmarks

[`CustomCrc32.Benchmarks`](CustomCrc32.Benchmarks/) holds five sets, each answering a
different question:

- **`Crc32Benchmarks`** — the fold against two slower formulations, one bit at a time and one
  table lookup per byte, across input sizes at different levels of the memory hierarchy.
- **`FoldLaneBenchmarks`** — the fold kernel at one, two, four and eight accumulator lanes
  over a cache-resident buffer, which is what pins down [why four](#four-accumulator-lanes).
- **`StreamingBenchmarks`** — 1 GiB in 1 MiB calls, each megabyte met for the first time,
  against a pure-read roofline and a resident-buffer control. This is the streaming rate, and
  it is not the same number as the one above.
- **`Crc32cHardwareBenchmarks`** — the fold against SSE4.2's dedicated `CRC32` instruction,
  both producing CRC-32C, which is [the one comparison](#against-the-dedicated-crc-instruction)
  where the library is at a structural disadvantage.
- **`SystemIoHashingBenchmarks`** — the fold against `System.IO.Hashing` 11.0, which gained a
  configurable parameter set and so [covers the same ground](#against-systemiohashing) for the
  first time. Built for `net10.0` only, for the reason given there.

Baselines that the library does not expose are reimplemented inside the benchmark — the table
formulations in the first set, the other lane counts in `FoldLanes` — since the library folds
by four automatically and offers no supported way to ask for anything else. Every one of them
is checked before it is timed: `GlobalSetup` throws if any two paths that should agree have
diverged, because a speed comparison stops meaning anything once a baseline is wrong. The lane
variants are held against `Crc32.IsoHdlc.ComputeBytes`, the answer the test suite pins; the
byte buffer in the first set holds the big-endian serialisation of the same words, so it must
return what `ComputeBigEndian` does.

`ThroughputColumn` is a custom `IColumn` adding a GB/s column derived from the mean and
whichever length parameter the case declares — `WordCount` or `ByteCount`. BenchmarkDotNet has
no built-in throughput column, and `OperationsPerInvoke` cannot be used here because it
requires a compile-time constant.

```
dotnet run -c Release -f net8.0 --project CustomCrc32.Benchmarks
```

The project multi-targets, so `dotnet run` needs to be told which framework. Everything below
was measured on `net8.0` except [the `System.IO.Hashing` comparison](#against-systemiohashing),
which only exists in the `net10.0` build:

```
dotnet run -c Release -f net10.0 --project CustomCrc32.Benchmarks
```

Extra arguments are forwarded to BenchmarkDotNet (`-- --job short`, `--filter`, …). Every
class runs by default, which includes the streaming set — that one allocates **1 GiB** and
takes several minutes, so narrow the run when you do not want it:

```
dotnet run -c Release -f net8.0 --project CustomCrc32.Benchmarks -- --filter '*FoldLaneBenchmarks*'
```

### Indicative results

Intel Xeon E-2174G @ 3.80 GHz (4 physical / 8 logical cores), Windows 11 25H2, .NET 8.0.30
X64 RyuJIT x86-64-v3, BenchmarkDotNet 0.15.8. **Taken with `--job short`** (3 warmup + 3
iterations) — good enough for the headline ratios, but re-run with the default job before
quoting these anywhere.

Throughput, higher is better. *Table* is one lookup per byte — the same formulation the
library still runs for short inputs, for the tail, and on machines without the instructions:

| WordCount        | Bitwise  | Table fwd | Table refl | Fold fwd BE | Fold fwd LE | Fold refl BE | Fold refl LE |
| ---------------- | -------- | --------- | ---------- | ----------- | ----------- | ------------ | ------------ |
| 16 (64 B)        | 111 MB/s | 663 MB/s  | 766 MB/s   | 2.46 GB/s   | 2.52 GB/s   | 2.55 GB/s    | 2.81 GB/s    |
| 256 (1 KiB)      | 38 MB/s  | 570 MB/s  | 642 MB/s   | 14.5 GB/s   | 14.3 GB/s   | 15.0 GB/s    | 15.1 GB/s    |
| 4,096 (16 KiB)   | 34 MB/s  | 566 MB/s  | 623 MB/s   | 21.1 GB/s   | 21.4 GB/s   | 21.6 GB/s    | 21.9 GB/s    |
| 65,536 (256 KiB) | 34 MB/s  | 559 MB/s  | 634 MB/s   | 21.0 GB/s   | 22.0 GB/s   | 22.2 GB/s    | 22.1 GB/s    |
| 262,144 (1 MiB)  | 34 MB/s  | 567 MB/s  | 636 MB/s   | 21.3 GB/s   | 21.5 GB/s   | 21.5 GB/s    | 21.5 GB/s    |

**Folding is ~38× the table** at steady state and ~620× the bitwise loop. It reaches roughly
5.7 bytes per cycle, which for a 4 KiB working set is bounded not by memory but by the
execution port the carry-less multiplies share with the block permutation — about three slots
on it per block, which [fold lanes](#fold-lanes) below takes apart.

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

### Fold lanes

`FoldLaneBenchmarks`, default job, same machine as above. The buffer is reused every
invocation, so everything up to 1 MiB is served from cache and the pipeline is what is being
measured. Throughput, higher is better:

| Input             | 1 lane    | 2 lanes    | 4 lanes (shipping) | 8 lanes    | 4 lanes, no shuffle |
| ----------------- | --------- | ---------- | ------------------ | ---------- | ------------------- |
| 4 KiB (L1)        | 6.82 GB/s | 13.32 GB/s | 20.36 GB/s         | 19.74 GB/s | 23.79 GB/s          |
| 64 KiB (L2)       | 6.92 GB/s | 14.49 GB/s | 23.86 GB/s         | 23.88 GB/s | 28.63 GB/s          |
| 1 MiB (L3)        | 7.28 GB/s | 14.56 GB/s | 22.79 GB/s         | 23.48 GB/s | 26.54 GB/s          |
| 16 MiB (past L3)  | 6.64 GB/s | 11.44 GB/s | 15.06 GB/s         | 16.08 GB/s | 16.40 GB/s          |

**Four lanes are worth roughly 3–3.5× over one**, cache-resident, for four statements that
look sequential and do four times the work per iteration. That is the whole case for the
interleaving, and it is the largest single factor in the fold's speed after the choice to fold
at all.

**Two lanes land within a few percent of 2.00×** at every size. That number is the most
informative one in the table: near-perfect linear scaling means the loop is still bound by
*latency* at two lanes, with the multiplier port idle enough to absorb a second chain for
free. Nothing else in the table scales that cleanly.

**Eight lanes are not the improvement four more chains would suggest** — 3% slower than four
at 4 KiB, where the seven-deep serial merge is amortised over only 256 blocks; a tie at
64 KiB; and 3% ahead at 1 MiB, where hiding memory latency starts to matter more than feeding
the port. The port is already full at four, so widening past it only lengthens the prologue
and epilogue. Register pressure is not the reason it stops paying: eight accumulators, two
constants and a mask still fit the sixteen `xmm` registers without spilling.

**The identity `pshufb` costs 16–20%**, and this is the clearest evidence for what the kernel
is bound by. That shuffle permutes nothing at all in the reflected byte case; removing it
removes one micro-operation from the contended port and nothing else. A latency-bound loop
would not have noticed. See [going further](#going-further).

Past L3 the ordering changes character — the 16 MiB row is partly a memory measurement, which
is what the next section is about.

### Streaming versus resident data

Every table above reuses one buffer. That is the right way to isolate the pipeline, and the
wrong way to predict what a caller checksumming a file will see. `StreamingBenchmarks` does
the other experiment: 1 GiB in 1 MiB calls, each megabyte met for the first time, against a
resident-buffer control doing identical work.

| Case (1 GiB of input, 1 MiB calls) | Mean      | Throughput | vs 1 lane |
| ---------------------------------- | --------- | ---------- | --------- |
| cold, 1 lane                       | 172.03 ms | 6.24 GB/s  | 1.00×     |
| cold, 2 lanes                      | 95.14 ms  | 11.29 GB/s | 1.81×     |
| **cold, 4 lanes (shipping)**       | 72.97 ms  | 14.72 GB/s | 2.36×     |
| cold, 8 lanes                      | 69.10 ms  | 15.54 GB/s | 2.49×     |
| cold, 4 lanes, shuffle removed     | 67.16 ms  | 15.99 GB/s | 2.56×     |
| *cold, pure read (roofline)*       | 57.80 ms  | 18.58 GB/s | 2.98×     |
| *hot 1 MiB ×1024, 4 lanes*         | 47.33 ms  | 22.69 GB/s | 3.63×     |

The control is the check that these two experiments are measuring the same kernel: at 46.2 µs
per megabyte it lands within 0.4% of the 1 MiB row in the lane table above.

**The lane advantage compresses by about a third.** Four lanes over one is 3.0–3.4×
cache-resident but **2.36×** on data arriving from memory. The pipeline figures are an upper
bound, not a streaming rate, and this is the number to quote for a file or a socket.

**The kernel is largely memory-bound while streaming.** It reaches 79% of a pure-read
roofline — a loop that touches the same gigabyte and folds nothing. That caps every remaining
compute optimisation at ~1.26× no matter how clever the kernel becomes. The clearest way to
see it: the *hot* case at 22.69 GB/s beats even the *cold roofline* at 18.58 GB/s, so on cold
data the fold is finishing early and waiting for lines to arrive.

That roofline is itself well below DDR4-2666's ~41 GB/s theoretical, because one core can only
keep so many line fills outstanding. It is a per-core limit rather than a platform one, which
is why the answer is not "add a faster kernel" but "use more cores", if 15 GB/s is ever the
constraint.

**Eight lanes edge ahead only here** (2.49× against 2.36×), and only because more loads in
flight partially hide memory latency — the same effect visible in the 16 MiB row of the lane
table. It is a memory effect, not a pipeline one, and worth about 5%.

For most callers none of this is the bottleneck: at ~15 GB/s the fold already outruns any
single NVMe drive, so a CRC over a file read from disk is not what the program is waiting for.

### Against the dedicated CRC instruction

SSE4.2 has a `CRC32` instruction that does in one step what the fold does in two carry-less
multiplies and a shuffle — but only for the Castagnoli polynomial. `Crc32cHardwareBenchmarks`
puts it against the fold over the same bytes, both returning the same CRC-32C. This is the
one comparison where the library is at a structural disadvantage, which is what makes it
worth running.

| Input             | Fold (any polynomial) | SSE4.2, one chain | SSE4.2, three chains |
| ----------------- | --------------------- | ----------------- | -------------------- |
| 4 KiB (L1)        | **21.38 GB/s**        | 12.43 GB/s        | 19.43 GB/s           |
| 64 KiB (L2)       | **23.93 GB/s**        | 12.17 GB/s        | 23.20 GB/s           |
| 1 MiB (L3)        | **22.80 GB/s**        | 12.17 GB/s        | 22.06 GB/s           |
| 16 MiB (past L3)  | **15.10 GB/s**        | 10.05 GB/s        | 10.23 GB/s           |

**The dedicated instruction does not beat the fold.** Interleaved three ways it reaches
parity — 3% behind at 64 KiB and at 1 MiB, close enough to call level — and past L3 the fold
is clearly ahead.

**Used the obvious way it loses by half.** A single chain sits at 12.2 GB/s at every
cache-resident size, which is precisely the latency bound: eight bytes per `CRC32`, three
cycles of latency, 2.67 bytes per cycle. That is the same lesson as
[four accumulator lanes](#four-accumulator-lanes), arrived at from the other direction. The
instruction is pipelined, and one dependency chain wastes most of it — being a single-purpose
unit does not exempt it.

Two caveats on reading the table. The three-chain figure is a **floor rather than a ceiling**:
it reaches about 5.1 bytes per cycle against a theoretical 8, because recombining the chains
costs real time. Recombining with `PCLMULQDQ` instead of a GF(2) matrix, or using larger
chunks, would close some of that, and a maximally tuned CRC-32C could plausibly edge past the
fold in cache. It would not change the cold-data column, where memory binds. And the 16 MiB
three-chain measurement is noisy — σ of 152 µs against a 1,640 µs mean — so treat it as
approximate.

<details>
<summary>A trap this benchmark walked into first</summary>

The three-chain figures were initially *worse* as the input grew: 14.09 GB/s at 4 KiB falling
to 3.55 GB/s at 16 MiB. No instruction behaves that way, so the fault was in the measurement.

Recombining the chains selects columns of a GF(2) matrix by the bits of a CRC value. Written
as `if ((vector & 1) != 0)` that is thirty-two unpredictable branches per call, twice per
block, mispredicting about half the time — costing more than the CRC instructions being
recombined. The size dependence is the tell: over a small buffer the predictor memorises the
handful of values that recur every invocation, so the cost only appears once the input
outgrows what it can hold, and the result then describes the branch predictor rather than the
instruction under test.

Making the selection branchless removed the size dependence entirely, which is what confirmed
the diagnosis. Both the fix and the reason are commented in `Crc32cHardware.cs` so it does not
come back.
</details>

### Against `System.IO.Hashing`

`System.IO.Hashing` 11.0 — pre-release at the time of writing — gives `Crc32` a configurable
parameter set for the first time. Until then the type computed ISO-HDLC and nothing else, so
there was no overlap worth timing outside a single preset. `Crc32ParameterSet.Create` now takes
a polynomial, an initial value, a final XOR and a reflection flag: near enough the Rocksoft
model this library uses, differing in [two ways](#a-second-oracle) that all twelve presets map
across cleanly. Run through it, each returns its catalogued check value, so the two really are
answering the same question.

`SystemIoHashingBenchmarks` runs both over the same buffer. Measured on .NET 10.0.11, default
job, same machine as every table above — sizes across the top this time, because there are
eight series. Throughput, higher is better:

| Series                                       | 4 KiB (L1)     | 64 KiB (L2)    | 1 MiB (L3)     | 16 MiB (past L3) |
| -------------------------------------------- | -------------- | -------------- | -------------- | ---------------- |
| **Fold, ISO-HDLC**                           | 21.31 GB/s     | **23.70 GB/s** | **22.91 GB/s** | 15.23 GB/s       |
| **Fold, CRC-32C**                            | 21.51 GB/s     | 23.68 GB/s     | 22.84 GB/s     | 15.25 GB/s       |
| **Fold, AUTOSAR**                            | 21.38 GB/s     | 23.57 GB/s     | 22.90 GB/s     | 15.21 GB/s       |
| `System.IO.Hashing`, ISO-HDLC, parameterless | 21.78 GB/s     | 22.42 GB/s     | 22.52 GB/s     | **15.60 GB/s**   |
| `System.IO.Hashing`, ISO-HDLC, built set     | 22.04 GB/s     | 22.36 GB/s     | 22.44 GB/s     | 15.54 GB/s       |
| `System.IO.Hashing`, CRC-32C, package preset | 21.95 GB/s     | 22.38 GB/s     | 22.46 GB/s     | 15.54 GB/s       |
| `System.IO.Hashing`, CRC-32C, built set      | **22.25 GB/s** | 22.37 GB/s     | 22.49 GB/s     | 15.57 GB/s       |
| `System.IO.Hashing`, AUTOSAR, built set      | 22.21 GB/s     | 22.36 GB/s     | 22.48 GB/s     | 15.46 GB/s       |

**The two are level.** No difference in that table exceeds 6%, and the lead changes hands
with the working set: the package is ~2% ahead at 4 KiB and ~2% ahead past L3, the fold ~6%
ahead at 64 KiB and ~2% at 1 MiB. That is the expected result rather than a surprising one.
Both are carry-less multiply folds, and the ceiling they are pressed against is the port the
multiplies contend for, not anything either author chose — [fold lanes](#fold-lanes) works out
where that ceiling comes from. Two independent implementations arriving within a few percent
is the strongest evidence available that the number is the machine's and not the code's.

**Neither implementation cares which polynomial it is given.** Reading down the fold's three
rows, the spread across ISO-HDLC, CRC-32C and AUTOSAR is under 1% at every size; the package's
five rows spread by at most 2.2%, and only at 4 KiB where the measurement is tightest. A
general CRC-32 has no reason to be slower for an unusual polynomial, and neither library is.

**Configurability costs nothing on either side.** The package's parameterless entry point and a
hand-built parameter set describing the identical CRC land within 1.2% of each other, in both
directions across the four sizes. Taking the general API is not paying for generality.

**The package does not reach for SSE4.2 either.** Its CRC-32C preset — the one parameter set
where the hardware instruction applies, and the only place a special case could hide — matches
its hand-built equivalent to within 1.4%, and matches its own ISO-HDLC and AUTOSAR rows just as
closely. It folds CRC-32C like everything else. That is independent corroboration of
[the previous section](#against-the-dedicated-crc-instruction), reached by a different route:
given the instruction, a second implementation also declined to use it.

**Both are allocation-free.** `MemoryDiagnoser` reports zero bytes for all eight series.

Worth keeping the gaps in proportion: the largest here is 6%, while [skipping the identity
`pshufb`](#going-further) is already measured at 16–20% cache-resident on this library's own
kernel. Both sides are nearer each other than either is to its own remaining headroom, so
these columns are not the basis on which to choose.

The honest summary for a caller: if you are on .NET 10 or newer, can take a pre-release
dependency, and want one of the CRCs `Crc32ParameterSet` can express, the BCL now does this at
the same speed with nothing third-party involved. What is still here and not there is a
`net8.0` target, a stable version, twelve named presets against two, `ReflectInput` and
`ReflectOutput` as separate parameters, word-oriented entry points that take
`ReadOnlySpan<uint>` with the byte order named at the call site, and an incremental API that
threads a bare `uint` register rather than an object to allocate and reset.

<details>
<summary>Why this one benchmark builds for <code>net10.0</code></summary>

The package ships its vectorised implementation in its `net10.0` and `net11.0` assets only. A
`net8.0` consumer silently resolves the `netstandard2.0` asset instead, which contains no
hardware intrinsics at all — no `Pclmulqdq`, no `Vector128`, nothing that would fold.

Nothing warns you. The build succeeds, the answers are correct, and the throughput column
quietly reports a different algorithm than the one you meant to measure. Timing against it
would have produced a flattering result for this library that said nothing about either
implementation, only about which asset NuGet picked.

Hence `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` on the benchmark project, the
package referenced under `net10.0` alone, and `SystemIoHashingBenchmarks` behind
`#if NET10_0_OR_GREATER` so a `net8.0` run cannot include it by accident. Everything else in
this README stays reproducible on `net8.0`. The runtime change is not itself doing anything to
the figures: the fold's rows above sit within about 1% of the same measurements taken on
.NET 8.0.30 in the previous section.
</details>

### Going further

Nothing here is the ceiling.

**VPCLMULQDQ** folds 256 or 512 bits per instruction instead of 128 and would multiply this
again, but it needs Ice Lake or newer, and .NET 8 does not expose `Pclmulqdq.V256` at all —
that arrived later. This machine is Coffee Lake, so it was not an option to measure.

**`Sse42.Crc32`** is measured rather than assumed, and it is the one door that turns out to be
closed. It computes a CRC in a single instruction, but is hardwired to the Castagnoli
polynomial, so it could accelerate `Crc32.Castagnoli` and none of the other eleven presets —
let alone an arbitrary parameter set. What it buys for that narrowing is nothing: properly
interleaved it draws level with the fold and no better, and past L3 it loses. A second kernel
and a recombination step, for one preset, to stand still. See
[against the dedicated CRC instruction](#against-the-dedicated-crc-instruction).

**Skipping the identity `pshufb`** is the one clearly unclaimed win left, and it is no longer
a guess. Where the permutation is the identity — the reflected engine reading bytes or
little-endian words — the shuffle moves nothing, yet still costs a slot on the port it shares
with the carry-less multiplies. Removing it measures **16–20% cache-resident** and **~9%
streaming**, the gap between the two being the part that hides behind memory stalls. See
[fold lanes](#fold-lanes) and [streaming](#streaming-versus-resident-data).

This case is more common than it first looks: `ComputeBytes` takes the little-endian
permutation too, so every reflected preset — `IsoHdlc` and `Castagnoli` included — is folding
through an identity shuffle when checksumming a byte buffer. It is still in place for
simplicity, since removing it means a second kernel or a branch in the inner loop, and the
streaming figure is where the honest payoff lies.

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
    ├── Crc32Benchmarks.cs           Fold against the bitwise and table formulations
    ├── FoldLanes.cs                 The fold kernel with the lane count made configurable
    ├── FoldLaneBenchmarks.cs        One, two, four and eight lanes, cache-resident
    ├── StreamingBenchmarks.cs       1 GiB cold, against a pure-read roofline
    ├── Crc32cHardware.cs            CRC-32C via SSE4.2, one chain and three
    ├── Crc32cHardwareBenchmarks.cs  The fold against the dedicated CRC instruction
    ├── SystemIoHashingBenchmarks.cs The fold against System.IO.Hashing 11.0 (net10.0 only)
    ├── ThroughputColumn.cs
    └── Program.cs
```

Package versions: NUnit 4.6.1, NUnit3TestAdapter 6.2.0, Microsoft.NET.Test.Sdk 18.8.1,
coverlet.collector 10.0.1, BenchmarkDotNet 0.15.8, System.IO.Hashing
11.0.0-preview.7.26381.103 — the last in the test project, where it is
[a second oracle](#a-second-oracle), and in the benchmarks' `net10.0` build, where it is
[the thing being measured against](#against-systemiohashing).

## Commands

```bash
dotnet build CustomCrc32.slnx                                       # build
dotnet test  CustomCrc32.slnx                                       # run tests
dotnet run -c Release -f net8.0 --project CustomCrc32.Benchmarks    # run benchmarks
```

The two solution-level commands need an SDK that understands `.slnx` — see
[Requirements](#requirements). Point them at the individual `.csproj` files on an older one.

Benchmarks must be run against a Release build; BenchmarkDotNet will refuse otherwise. The
benchmark project multi-targets, so `dotnet run` needs `-f` — use `-f net10.0` for
[the `System.IO.Hashing` comparison](#against-systemiohashing), `-f net8.0` for the rest.
