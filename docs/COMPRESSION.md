# LZX Compression

LZX represents input as literal tokens and backward-reference match tokens.
The token sequence is encoded using canonical Huffman codes and serialized
into length-prefixed records.

An illustrative token sequence for `ABCABCABC` is:

```text
literal 'A'
literal 'B'
literal 'C'
copy 6 bytes from 3 bytes back
```

Match copying supports overlapping source and destination ranges. Bytes
produced earlier in a match can be referenced by subsequent bytes in the same
match. The example describes token semantics, not a specific parser result.

This document describes the compression algorithm and framed LZX payload.
Container headers, asset serialization, external dictionaries, and Intel E8
translation are outside its scope. The inverse operation is described in
[Decompression](DECOMPRESSION.md).

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

```text
input bytes
    -> match search
    -> token selection
    -> block selection
    -> tree and token serialization
    -> record framing
```

Token selection is not unique: different choices can produce valid streams
with different bytes. Match search and token selection form the input
representation; block selection, serialization, and framing encode it.

Three size parameters are used:

| Size | Purpose |
| --- | --- |
| History window `W` | Maximum backward-reference range |
| 32 KiB input interval / decoded record | Input submission and output framing |
| Compression partition `P` | Input-buffer compaction interval |

The window size is `W = 2^w`, where the exponent `w` is between 15 and 21.
An exponent of 16 corresponds to a 64 KiB window. The encoder and decoder use
the same exponent; it is not stored in the stream.

For this strategy, the partition size is positive and aligned to 32 KiB. It
affects search-state maintenance and can change token selection, but the
decoder does not use it.

Block and record boundaries are independent. A block uses one coding
configuration. A record contains compressed data representing up to 32 KiB
of decoded output. A block can span several records, and a record can contain
several blocks.

## Tokens and repeat distances

A literal represents one input byte. A match represents a backward-copy
operation with a length of 2 through 257 bytes and a source distance.

The repeat-distance state consists of `R0`, `R1`, and `R2`, initialized to
`(1, 1, 1)`. A match can select a stored distance instead of encoding a new
distance.

The token's encoded distance `E` distinguishes repeat selections from new
distances. New distance `D` is represented as `D + 2`:

| `E` | Copy distance | Updated `(R0, R1, R2)` |
| ---: | --- | --- |
| 0 | `R0` | `(R0, R1, R2)` |
| 1 | `R1` | `(R1, R0, R2)` |
| 2 | `R2` | `(R2, R1, R0)` |
| 3 or greater | `E - 2` | `(E - 2, R0, R1)` |

Each update is evaluated from the previous state. Literal tokens do not
modify repeat distances.

The parser and serializer maintain separate repeat-distance states. The parser
state includes pending tokens that have not yet been serialized.

## Match search

### Search index

Input positions are indexed by their first two bytes, interpreted as a
little-endian key. Each of the 65,536 keys identifies a binary search tree
ordered by the subsequent bytes.

At input position `p`, the search inserts `p` as the new root and traverses
the previous tree. Let `B` denote the logical position at the start of retained
byte storage. Candidates at or before the following cutoff are expired:

```text
expired = max(p - W + 4, B - 1)
```

The window term bounds the backward-reference range. The storage-base term
excludes bytes discarded during compaction from tail-index maintenance.

Byte comparisons extend the initial two-byte match to at most 257 bytes.
When a candidate increases the maximum match length, its encoded distance is
recorded for each newly covered length. Traversal order therefore affects
distance selection for equal-length candidates.

The traversal retains common-prefix lengths for both tree directions. A newly
improved match of at least 50 bytes can terminate traversal by attaching the
candidate's remaining subtrees directly.

### Repeat-distance candidates

After the ordinary search, candidate sequences at distances `R0`, `R1`, and
`R2` are compared in that order, up to the maximum tree-match length.

Repeat selectors replace ordinary distances for the lengths they cover.
Later selectors replace only lengths longer than those covered by earlier
selectors. When `R0` matches more than 50 bytes, the other two checks are
skipped.

The returned match length is bounded by available input and by:

```text
32,767 - (p mod 32,768)
```

This bound prevents matches from crossing record boundaries and reserves the
last byte of a full decoded interval for a literal. A result below two bytes
is treated as no match.

### Search-index maintenance

Positions skipped by a selected match are inserted into the search index for
subsequent searches. Insertion comparisons are limited to 50 bytes.

For a new distance-one match (`E = 3`) longer than 16 bytes, only the next
position is inserted. Otherwise, every skipped position is inserted.

At the end of a full submitted interval, its final 50 positions are removed
from search roots as needed. They are reinserted after the next interval
provides additional lookahead bytes.

## Token selection

Token selection follows three paths:

- No usable match: a literal token is appended.
- Maximum match length of at least 50 bytes: the match is selected directly.
- Shorter match: candidate sequences are evaluated by bounded path search.

Selection accounts for estimated sequence cost, not only match length.
A shorter match can reduce the total estimated cost of subsequent tokens.

### Bounded path search

Each reachable position retains one decision node containing estimated cost,
predecessor, incoming token, and repeat distances. Alternative repeat states
at the same position are not all retained. The search is therefore a bounded
heuristic rather than a globally optimal parse.

The initial node has cost zero. Transitions are evaluated for a literal first,
then for matches in increasing length order. A destination node is replaced
only by a strictly lower-cost transition. Equal-cost transitions preserve the
earlier path.

For length `L` and position slot `s`, costs are:

```text
literal_cost = main_cost[literal]

match_cost = main_cost[256 + 8*s + min(L - 2, 7)] + extra_bits[s]

if (L >= 9)
{
    match_cost += length_cost[L - 9]
}
```

A position slot selects a repeat distance or identifies a new-distance range.
For nonrepeat slots, any footer bits select the distance within that range.
The mapping is described in
[Decompression](DECOMPRESSION.md#match-distance-decoding).

The cost estimate treats distance footers as direct bits, including when the
serialized block uses aligned coding.

The search traverses reachable positions and extends the frontier with
available matches. Matches longer than two bytes can extend it. A two-byte
match extends it only when its encoded distance is below `0x800`. This condition
restricts frontier extension, not all two-byte transitions.

At a later position, expansion terminates when a match exceeds 50 bytes or its
endpoint reaches relative position `0xEFD`. That match is appended to the
retained path leading to its start.

The selected sequence is reconstructed by traversing predecessor links
backward from the endpoint and reversing the tokens. The final node supplies
the updated parser repeat-distance state.

### Cost model

Initial cost estimates are eight bits per literal, nine bits per main match
symbol, and six bits per secondary length symbol.

Selected tokens provide symbol frequencies. Huffman trees built from these
frequencies supply updated code-length estimates. Symbols absent from the
estimated trees receive nonzero fallback costs:

| Symbol | Fallback cost |
| --- | ---: |
| Literal | 11 bits |
| Main match symbol | 12 bits |
| Secondary length symbol | 8 bits |

Two-byte matches in slots 34 and above receive a main-symbol cost of 100 bits,
penalizing distant short matches. These slots are absent from smaller windows.

Frequency tallies use unsigned 16-bit counts. A tally with one active symbol
receives a dummy symbol with frequency one: index 1 if the active symbol is 0,
otherwise index 0. The dummy count is retained during subsequent cumulative
updates.

The initial ordinary refresh threshold is 10,000 pending tokens. When an
optimized path reaches the threshold, pending frequencies are recounted or
extended with the uncounted suffix. Costs are rebuilt, and the threshold
advances by `0x1000` once per path, even if several thresholds were crossed.
Costs are also rebuilt after each submitted input interval. Literal and
direct-match paths do not independently trigger an in-interval refresh.

### Initial reparse

The initial pass provides frequency estimates for a second parse of the same
input. The reparse procedure is:

1. Select a statistical prefix of the pending tokens.
2. Recount its symbols and rebuild costs.
3. Clear search links and pending tokens.
4. Reset parser repeat distances to `(1, 1, 1)`.
5. Parse the already submitted bytes again.

Initial reparsing occurs at most once, before the first compaction or after
the final partial input interval. The subsequent cost-refresh threshold is
the selected prefix's token count.

An optimized path can trigger an early reparse at `0xFE00` pending tokens or
`0x7E00` pending matches. Literal and direct-match paths instead use the
capacity limits described below.

## Block selection

Block boundaries allow the coding configuration to change with the token
distribution. Separate blocks can therefore use different coding trees.

### Distribution-based splitting

Split selection compares main-symbol histograms in windows of `0x400` tokens.
Counts are mapped to logarithmic buckets to reduce the influence of their
absolute magnitudes:

```text
bucket(0) = 0
bucket(c) = floor(log2(c)) + 1, for c > 0

difference(A, B) = sum over symbols of
                  abs(bucket(A[symbol])^2 - bucket(B[symbol])^2)
```

Counts in these windows are at most `0x400`. The score excludes secondary
length symbols and direct distance bits.

Split search is disabled below `0x1800` pending tokens or after four
statistical splits. The split count is reset when emitted output crosses a
record boundary.

Let `difference(a, b)` compare windows starting at token indices `a` and
`b`. The scan advances `before` from `0x800` in `0x400` steps while
`before < token_count - 0x1000`, with `after = before + 0x800`.
All three scores below exceed `0x578` for a region to qualify:

```text
difference(before,         after - 0x400)
difference(before - 0x400, after)
difference(before - 0x800, after + 0x400)
```

For a qualifying region, boundaries are scored from `after - 0x600` up to,
but excluding, `after + 0x200`, in `0x40` steps. Each score compares the
windows immediately before and after the boundary.

Equal scores retain the earliest boundary. The selected prefix ends at the
first qualifying region's boundary whose score exceeds `0x6A3` and whose
position is at least `0x1000` tokens from the pending sequence's start.
If no region qualifies, the prefix contains the entire pending sequence.

### Capacity-driven output

Direct-token paths end a block at `0xFFF8` pending tokens, or when a new match
raises the pending match count to `0x7FF8`.

Capacity for an optimized path is reserved before any of its tokens are
committed. Its complete token count is checked against both buffer limits,
conservatively counting every path token as a possible match. Pending prefixes
are emitted until sufficient capacity is available.

After prefix emission, the suffix remains pending and costs are rebuilt from
the emitted prefix. If the suffix has fewer than `0x1000` tokens, the next
refresh threshold is `0x1000`; otherwise, it is the suffix's token count plus
`0x1000`.

### Block type selection

Coding trees and token statistics determine the compressed-size estimate:

```text
estimated_bits = 1200
               + sum(main_frequency[s] * main_length[s])
               + sum(length_frequency[s] * length_length[s])
               + sum(distance_extra_bits for each match)
```

A one-symbol main or length alphabet contributes an additional bit for its
dummy symbol. The fixed 1200-bit term estimates tree-description overhead;
it is not the measured header length.

A raw block is selected when its decoded size is no greater than
`ceil(estimated_bits / 8)` and its source starts within retained input history.
The retained-history condition remains part of this strategy even when a
separate copy of the complete input is available.

For compressed blocks, let `M` be the total match count. Histogram `H` counts
`E & 7` only for encoded distances with `E > 15`, and `T = sum(H)`.
Aligned mode is selected when `M >= 100` and `max(H) > floor(T / 5)`.
Otherwise, verbatim mode is selected.

Verbatim mode stores distance footer bits directly. Aligned mode Huffman-codes
the lowest three bits of applicable footers.

## Huffman code construction

Symbol frequencies determine Huffman code lengths. Canonical assignment then
determines the codes from those lengths without storing an explicit tree
topology.

Nonzero-frequency symbols are inserted into a min-heap in increasing symbol
order. An empty alphabet has all-zero lengths. A single-symbol alphabet
receives the dummy symbol described above.

Each iteration removes the two minimum-frequency nodes and combines them under
a parent with their summed frequency. Internal nodes receive increasing indices
after the alphabet's symbols. Leaves are recorded in removal order.

Heap ties select the left child when child frequencies are equal. A parent
whose frequency equals the selected child's remains in place. These rules
determine the ordering used for length assignment.

Leaf depths above 16 are clamped to 16. For code-length counts `count[b]`,
occupied code space is:

```text
occupied = sum(count[b] * 2^(16 - b)), for b = 1 through 16
```

While occupied space exceeds `2^16`, one length-16 code and one code from the
deepest populated shorter length are removed. Two codes are added at the next
length. Each iteration preserves the leaf count and reduces occupied space
by one unit.

Lengths are assigned from 16 down to 1 to leaves in removal order. Canonical
codes are then assigned in increasing symbol order within each length.

## Tree and token serialization

Each block begins with a three-bit type and 24-bit decoded length.
Aligned blocks additionally contain eight three-bit aligned-tree lengths.

Main and secondary lengths are transmitted in three lists: the 256 literal
lengths, the remaining main match lengths, and the 249 secondary lengths.
Each list uses a separate 20-symbol pretree.

A pretree encodes differences from the previous block's code lengths:

```text
delta = (previous_length - new_length) mod 17
```

The encoder counts following equal entries, excluding the current entry.
At least four following entries permit a run instruction:

- Zero lengths: symbol 17 for 4–19 entries, or symbol 18 for 20–51 entries.
- Nonzero lengths: symbol 19 for four or five entries, followed by the first
  entry's delta.

The emitted count is derived from following entries, leaving an equal entry
for a subsequent instruction. Shorter runs use individual deltas. The pretree
is built from instruction frequencies; its twenty four-bit lengths precede the
encoded instructions. New lengths are retained for the next block.

The [decompression description](DECOMPRESSION.md#code-length-decoding)
defines the instruction fields and inverse formulas.

For a compressed block, each token is serialized as a main symbol, an optional
secondary length symbol, and a distance footer. In aligned mode, a footer with
at least three bits uses the aligned tree for its low three bits and direct
bits for the remaining portion.

For a raw block, 1–16 zero padding bits align the following fields to a word
boundary. Three little-endian 32-bit repeat distances precede the source bytes.
The stored distances reflect the state after the replaced tokens, not
necessarily the state before them. Previous Huffman lengths remain unchanged.
An odd-length raw block is followed by one padding byte before a subsequent
block's bitstream resumes.

## Record framing

Coded fields are packed most-significant bit first into 16-bit words. Each
word is stored low byte first. Record lengths are big-endian 16-bit integers.

One zero translation flag precedes the first block of a nonempty stream. It
is not repeated per block or record.

After 32,768 decoded bytes, pending coded bits are zero-padded to a word
boundary and the record is framed. Matches do not cross records. Raw blocks
can continue in subsequent records without repeated block headers.

```text
ordinary record:
    compressed_length:u16be | payload

final record:
    FF | decoded_length:u16be | compressed_length:u16be | payload
```

The final record uses an extended header even when its decoded length is
32 KiB. Five zero bytes follow its payload. Empty input produces neither
records nor a trailer.

A record boundary clears the bit accumulator, not search history, repeat
distances, coding lengths, or an unfinished block.

## Input-buffer compaction

The input buffer contains a history window, a partition, and padded lookahead.
After a full input interval, compaction occurs when the submitted endpoint
minus the storage base reaches `W + P`.

A pending initial reparse precedes the first compaction. The retained `W`
bytes move from physical offset `P` to the buffer's start, and the logical
storage base advances by `P`.

Search positions remain logical input positions. Bytes outside copied and
newly submitted ranges remain unchanged, including stale lookahead. Searches
can compare padded storage, but returned match lengths are bounded by
submitted input and record limits.

For aligned parameters and full input intervals, compaction endpoints are
`W + k*P` for positive `k`. Compaction modifies retained storage without
resetting the compressed stream or creating block or record boundaries.
