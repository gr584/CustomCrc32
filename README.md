# CustomCrc32

A configurable 32-bit CRC for input arriving as **32-bit words** rather than bytes. Any
parameter set expressible in the Rocksoft/Williams model works, including reflected variants,
and twelve catalogued CRC-32s ship as named presets.

Runs at **~21 GB/s** on x86-64 with carry-less multiply, for every parameter set rather than
a privileged few, falling back to a table where the instructions are unavailable.

```csharp
using CustomCrc32;

uint[] words = [0x12345678, 0x9ABCDEF0];

uint crc = Crc32.Mpeg2.Compute(words);      // 0x7D24A31B
uint zlib = Crc32.IsoHdlc.Compute(words);   // the usual "CRC-32"
```

## Requirements

- .NET 8.0 SDK or newer. All three projects target `net8.0`; built and tested with SDK 10.0.303.
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

    // One-shot. The suffix selects the byte order the words are serialised in.
    public uint Compute(ReadOnlySpan<uint> data);
    public uint ComputeLittleEndian(ReadOnlySpan<uint> data);

    // Streaming.
    public uint Append(uint register, ReadOnlySpan<uint> data);
    public uint AppendLittleEndian(uint register, ReadOnlySpan<uint> data);
    public uint Finish(uint register);
}
```

An instance owns the 256-entry table derived from its parameters, so **construct one per
parameter set and reuse it**. Instances are immutable and safe to use concurrently. The
presets are shared instances, so `Crc32.Mpeg2.Compute(…)` allocates nothing.

The input parameter is `ReadOnlySpan<uint>`, so a `Span<uint>`, a `uint[]`, a stack-allocated
buffer or a collection expression all bind without a cast.

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

The input is typed as `uint`, not as raw bytes, so **the host machine's endianness never
enters into it**. The algorithm consumes each word bit by bit regardless of how that word is
laid out in memory. The choice between `Compute` and `ComputeLittleEndian` is about the byte
order your words will be *serialised* in, and the same call returns the same answer on x64,
ARM, or anything else.

- `Compute` — each word contributes its most significant byte first: `0x12345678` is
  checksummed as the bytes `12 34 56 78`.
- `ComputeLittleEndian` — least significant byte first: `78 56 34 12`.

Each engine folds a word naturally in one of those two directions and byte-swaps for the
other. Which is which flips with `ReflectInput`: the forward engine's natural direction is
big-endian, the reflected engine's is little-endian. The swap is a single `bswap` acting on
the incoming word rather than on the CRC register, so it carries no dependency on the
previous iteration and costs nothing measurable — see the benchmarks below. Never pre-swap
the buffer with `BinaryPrimitives.ReverseEndianness`; that allocates and adds a pass.

### The one case this does not cover

If your data actually arrives as a **byte** buffer that you are reinterpreting as `uint` (via
`MemoryMarshal.Cast` or similar), neither method is the right tool, and the cast is a trap: a
big-endian buffer `12 34 56 78` read through `MemoryMarshal.Cast<byte, uint>` on a
little-endian host yields `0x78563412` — silently the wrong word, no error, wrong CRC.

The correct fix is not to cast at all but to process bytes directly, which also handles
lengths that are not a multiple of four. No byte-oriented overload ships today — ask if you
need one. It would want a distinct name (`ComputeBytes`) rather than an overload, because
overloading on `ReadOnlySpan<byte>` alongside `ReadOnlySpan<uint>` makes collection-expression
calls like `Compute([])` ambiguous.

## Implementation notes

There are two layers: a carry-less-multiply fold that does the bulk of the work where the
hardware allows, and a table that handles short inputs, the tail, and machines without the
instructions. Both are driven by the same parameter set and produce identical answers — a
test appends one word at a time, keeping every call under the folding threshold, and requires
the result to match the single-shot folded call.

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

The test suite ([`CustomCrc32.Test`](CustomCrc32.Test/Crc32Tests.cs), NUnit, 155 tests) is
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

Mutation checks confirm the suite is not vacuous. Dropping the `<< 1` from the reflected fold
constant fails 50 tests; shifting the forward constant's exponent by one fails 36; flipping a
bit of the polynomial fails 7. Removing the initial-value reversal fails exactly the two tests
written for that gap, since — every preset having a palindromic initial value — nothing else
can catch it.

## Benchmarks

[`CustomCrc32.Benchmarks`](CustomCrc32.Benchmarks/) compares the shipping implementation
against the two formulations it replaced — one bit at a time, and one table lookup per byte —
across input sizes chosen to sit at different levels of the memory hierarchy. The table
baselines are reimplemented inside the benchmark rather than called through the library,
since the library now folds automatically and there is no supported way to ask it not to.
`GlobalSetup` throws if any two paths that should agree have diverged — a speed comparison
stops meaning anything once the baseline is wrong.

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
compete for the same port, so it may be worth a little; it was left in for simplicity.

## Project layout

```
CustomCrc32.slnx                  Solution (new-style XML format)
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
