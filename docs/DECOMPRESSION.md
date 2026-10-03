# LZX Decompression

LZX decompression reconstructs the input from stored coding trees, literal
tokens, and backward-reference match tokens. Match search, cost estimation,
and block selection are encoder operations and are not part of decoding.

This document describes the framed LZX payload, without container headers,
external dictionaries, or Intel E8 translation. The corresponding encoding
process is explained in [Compression](COMPRESSION.md).

## Glossary

- **Aligned block** - A compressed block that uses an eight-symbol aligned
  tree for the low three bits of applicable distance footers.
- **Block** - A coding unit with a type and decoded length. Its boundaries are
  independent of record boundaries.
- **Canonical Huffman code** - A Huffman code reconstructed from code lengths
  and symbol order without storing an explicit tree topology.
- **History window (`W`)** - The retained byte range available to backward
  references. Its size is `2^w` bytes.
- **Literal** - A token representing one byte directly.
- **Match** - A token that copies 2 through 257 bytes from earlier history
  using a source distance.
- **Position slot** - A repeat-distance selector (slots 0–2) or a
  new-distance range (slots 3 and above). For nonrepeat slots, any footer bits
  select the exact distance within that range.
- **Pretree** - A 20-symbol Huffman alphabet used to encode changes in
  main-tree and secondary-tree code lengths.
- **Record** - A length-prefixed compressed payload representing up to
  32 KiB of decoded output.
- **Repeat distances (`R0`, `R1`, `R2`)** - Three remembered source distances
  that matches can select without encoding a new distance.
- **Raw, verbatim, and aligned** - The three block types. Raw blocks store
  source bytes directly; verbatim and aligned blocks encode tokens with Huffman
  trees.

## Processing stages

Records and blocks advance independently:

- Records delimit compressed payloads and represent up to 32 KiB of output.
- Blocks select raw, verbatim, or aligned coding and carry their decoded length.

Each record contains new blocks or a continuation of the preceding record's
unfinished block. Record processing ends when its declared decoded length is
produced, independently of block completion.

The total decoded length and history-window exponent are supplied before
decoding. Neither is obtained from a block header. The encoder's partition
size and token-selection strategy are not decoder parameters.

## Initial state

The window size is `W = 2^w`, with exponent `w` between 15 and 21.
It determines the number of distance slots and main-tree symbols:

| Exponent | Window | Position slots `S` | Main symbols |
| ---: | ---: | ---: | ---: |
| 15 | 32 KiB | 30 | 496 |
| 16 | 64 KiB | 32 | 512 |
| 17 | 128 KiB | 34 | 528 |
| 18 | 256 KiB | 36 | 544 |
| 19 | 512 KiB | 38 | 560 |
| 20 | 1 MiB | 42 | 592 |
| 21 | 2 MiB | 50 | 656 |

The initial output position is zero, with no valid preceding history.
Repeat distances are initialized to `(R0, R1, R2) = (1, 1, 1)`. Previous
main-tree and secondary length-tree lengths are zero. No block is active.

History, repeat distances, tree lengths, and unfinished block progress persist
across records. The record-local bit accumulator is reset at each boundary.

## Record framing

Record length fields are unsigned big-endian 16-bit values.
Lengths count payload bytes, excluding the header.

```text
ordinary record:
    compressed_length:u16be | payload

extended record:
    FF | decoded_length:u16be | compressed_length:u16be | payload
```

An ordinary record represents exactly 32,768 decoded bytes. Its first header
byte cannot be `FF`, because that selects the extended header. Its compressed
length is therefore between 1 and `0xFEFF`.

An extended record supplies its decoded length, from 1 through 32,768, and
a compressed length from 1 through 65,535. The encoder uses this header for
the final record, including a full final record.

The `FF` byte identifies the header form rather than an end token. Output
completion is determined by the expected total decoded length.

Five zero bytes follow the final payload. Completion accepts either this
trailer or no trailing bytes. Other trailing data is invalid. Empty input
has an empty canonical encoding, with no records.

Each payload is bounded by its compressed length. Bit-reader lookahead is
restricted to that payload and does not consume subsequent records.

## Bit ordering

Coded data is a sequence of little-endian 16-bit words, read most-significant
bit first within each word:

```text
word = lo | (hi << 8)
consume bit 15, then bit 14, ... then bit 0
```

Bytes `34 12` form word `0x1234`, whose first eight consumed bits are
`00010010`.

Fields crossing words retain their logical most-significant-bit-first order.
This bit ordering is separate from big-endian record headers and the
little-endian integer fields of raw blocks.

The first bit of the first nonempty record is the translation-present flag.
The untransformed stream described here uses a zero flag. A set flag indicates
Intel E8 translation, which requires a separate decoding operation outside
this document's scope. The flag is not repeated by later blocks or records.

At a coded-data record boundary, unused alignment bits are discarded and the
bit reader is reset for the next payload. The active block's trees and
remaining decoded length are retained.

## Block headers

When the active block has no remaining decoded bytes, the next fields are:

| Field | Width | Meaning |
| --- | ---: | --- |
| Type | 3 bits | Verbatim, aligned, or raw |
| Decoded length | 24 bits | 1 through `0xFFFFFF` output bytes |

The decoded length is stored most-significant bits first and excludes headers
and padding.

| Type | Coding |
| ---: | --- |
| 1 | Verbatim: trees and tokens; direct distance footer bits |
| 2 | Aligned: trees and tokens; some distance bits use an aligned tree |
| 3 | Raw: stored repeat distances and original bytes |

Other types are invalid. A block can end within a record or continue into
subsequent records. A record header does not imply a new block header.

## Huffman code reconstruction

Decoding uses four alphabets:

| Tree | Symbols | Represents |
| --- | ---: | --- |
| Main | `256 + 8*S` | Literals and match length/slot combinations |
| Length | 249 | Additional match lengths |
| Aligned | 8 | Low three bits of a distance footer |
| Pretree | 20 | Instructions describing code lengths |

Code lengths and symbol order are sufficient to reconstruct the canonical
Huffman codes; no explicit tree topology is stored.

Let `count[b]` be the number of codes of length `b`, excluding zero lengths.
With `next[1] = 0`, initial code values are:

```text
next[b] = (next[b - 1] + count[b - 1]) << 1
```

The recurrence applies to lengths 2 through 16. Symbols are processed in
increasing index order. A symbol of length `b` receives `next[b]`, after which
that value is incremented. Code values are ordered by length and, within each
length, by symbol index.

Length zero denotes an absent symbol. Main and secondary code lengths are
at most 16 bits. Pretree lengths occupy four-bit fields; aligned lengths
occupy three-bit fields.

A nonempty tree requires valid, complete prefix-code space. An empty tree can
represent an unused alphabet but cannot be used for symbol decoding.

## Code-length decoding

An aligned block begins with eight three-bit aligned lengths, from which the
aligned tree is reconstructed. Verbatim blocks omit this description.

Both compressed block types then describe three length lists:

1. Main-tree literal symbols, indices 0 through 255.
2. The remaining main-tree match symbols.
3. Secondary length symbols, indices 0 through 248.

Each list begins with twenty four-bit lengths defining its pretree. That
pretree decodes the list's length instructions. The main tree is reconstructed
after both main lists, followed by the secondary tree.

### Length deltas and runs

Lengths are encoded relative to the previous compressed block.
For the first block, those previous lengths are all zero.

| Pretree symbol | Additional fields | Result |
| ---: | --- | --- |
| 0–16 | None | One length: `(old_length - symbol) mod 17` |
| 17 | 4 direct bits `n` | `n + 4` zero lengths |
| 18 | 5 direct bits `n` | `n + 20` zero lengths |
| 19 | 1 direct bit `n`, then delta symbol `d` | `n + 4` copies of `(old_length - d) mod 17` |

For symbol 19, the delta is between 0 and 16. The resulting length is computed
once from the first entry's previous length and repeated four or five times.
The delta is not applied separately to each destination's previous length.

A previous length of 5 and delta 2 produce length 3. A previous length of 0
and delta 14 also produce length 3 through modulo-17 subtraction.

Length decoding ends when the list is filled. A run extending beyond the list
is invalid. Decoded lengths become the reference for the next compressed
block. Raw blocks leave these arrays unchanged.

## Token decoding

Main-tree symbols `m < 256` represent literal bytes. The decoded byte is
written to output and history, advancing the output position by one.

Other symbols encode a position slot and primary length:

```text
t = m - 256
slot = t >> 3
length_header = t & 7
```

Primary length values 0 through 6 represent match lengths 2 through 8:

```text
match_length = length_header + 2
```

Primary length value 7 requires a secondary symbol `f`:

```text
match_length = 9 + f
```

Secondary symbols 0 through 248 represent match lengths 9 through 257.
The decoded distance and length define the subsequent history-copy operation.

A valid token fits within the remaining decoded lengths of both the active
block and current record. These counters exclude headers, distance fields,
and padding.

## Match distance decoding

### Position slots

Slots 0–2 select repeat distances `R0`, `R1`, and `R2`. Slots 3 and above
identify new-distance ranges; any footer bits select the distance within the
range. The slot tables are:

```text
extra[s] = 0,                        for s < 4
extra[s] = min(floor(s / 2) - 1, 17), for s >= 4

base[0] = 0
base[s + 1] = base[s] + 2^extra[s]
```

| Slot | Footer bits | Meaning |
| ---: | ---: | --- |
| 0 | 0 | Use `R0` |
| 1 | 0 | Use `R1` |
| 2 | 0 | Use `R2` |
| 3 | 0 | New distance 1 |
| 4 | 1 | New distances 2–3 |
| 5 | 1 | New distances 4–5 |
| 6 | 2 | New distances 6–9 |
| 7 | 2 | New distances 10–13 |
| 8 | 3 | New distances 14–21 |

For a nonrepeat slot:

```text
distance = base[slot] - 2 + footer
```

Slot 3 has a zero footer. Valid slots are below the window's slot count `S`.

### Verbatim and aligned footers

Verbatim blocks store all `extra[slot]` footer bits directly.

Aligned blocks divide footers containing at least three bits:

- Fewer than three bits: the footer is read directly.
- Exactly three bits: one aligned-tree symbol represents the footer.
- More than three bits: high bits are read directly, followed by an
  aligned-tree symbol representing the low three bits.

In the last case, `footer = (high << 3) | low`.
Repeat slots have no footer and consume no aligned symbol.

### Repeat-distance updates

Each match updates the repeat-distance state from its previous values:

| Slot | Selected distance | Updated `(R0, R1, R2)` |
| ---: | --- | --- |
| 0 | `R0` | `(R0, R1, R2)` |
| 1 | `R1` | `(R1, R0, R2)` |
| 2 | `R2` | `(R2, R1, R0)` |
| 3 or greater | New distance `D` | `(D, R0, R1)` |

Slot 2 exchanges `R0` and `R2`, leaving `R1` unchanged.

## History copying

Let `p` be the number of bytes already produced and `D` the distance.
Without an external dictionary, a valid match has
`1 <= D <= min(W, p)`.

Copy match bytes in increasing output order:

```text
byte = history[(p - D) mod W]
history[p mod W] = byte
output byte
p = p + 1
```

Sequential copying supports overlapping matches. Given preceding output
`ABC`, a six-byte match at distance three references `ABC`, then references
the newly produced bytes, resulting in `ABCABCABC`.

Copying from a fixed source snapshot does not preserve this behavior when the
match length exceeds its distance. Literals and raw bytes update the same
history window. Window wraparound does not reset coding state.

## Raw-block decoding

Following a raw block header, 1–16 zero bits align the subsequent fields to
the next word boundary. An already aligned header is followed by a complete
padding word.

Three little-endian 32-bit values replace `R0`, `R1`, and `R2`. The following
raw bytes are copied according to the block's decoded length and added to
history.

If raw data spans a record boundary, copying continues from the next record
without another block header or repeated distance fields.

An odd-length raw block has one zero padding byte before the next block's
coded data. That byte does not count toward decoded output. If output is
already complete, there is no next block header to read.

Raw blocks replace repeat-distance state without modifying previous main and
secondary tree lengths.

## Completion and validation

Each produced byte decrements the remaining decoded lengths of the record
and block. Block completion permits a new block header; record completion
permits a new record header. Neither boundary resets history.

At the expected total output length, a complete stream has no decoded bytes
remaining in an active block. Completion validates the optional five-zero-byte
trailer. No end-of-stream Huffman symbol is used.

Truncated headers, impossible lengths, invalid trees, overflowing length runs,
out-of-range slots, and matches outside valid history cannot be decoded safely.
The same applies to a token that exceeds its block, record, or expected total
length. Stored raw-block distances are validated before use as copy distances.

Lookahead and alignment bytes are excluded from decoded output. Output-size
bounds limit expansion from overlapping matches, which can produce decoded
data substantially larger than the compressed payload.
