# Deobfuscating the protocol between versions

> State as of 19/08/2026, second pass. This document is the starting point for building the
> complete pipeline: what is done, what is measured, what does not work and why, how the
> only one who has solved it solves it, and what architecture I propose.
>
> **What has changed in this pass.** Stages 3 and 4 are no longer a plan: they are written, they run
> and they are measured. And measuring them, two premises of the previous version fell, both in §4:
> Snowbot does not read the pseudocode of one obfuscated client against another obfuscated one —it has an
> UNobfuscated build, and that changes everything—, and the assemblies Cpp2IL leaves in `cpp2il_out` carry no
> code, only signatures. In exchange a vein has appeared that was not in sight: the obfuscator lets
> names slip inside the classes it does rename, and they are names that describe what they do.

---

## 1. The problem

Ankama **rotates the protocol's three-letter names in every patch**. The `jsd` that today takes
an actor off the map will be called `xqr` tomorrow, and today's `xqr` will be something else.

Consequences, in order of severity:

1. **Captures expire.** A Wireshark capture from today serves forever as a reference of
   *shape*, but it can no longer be crossed with the new client: the opcodes no longer match.
2. **What is not captured now is not captured later.** When the client updates, version
   3.6.10.10 can no longer connect to Ankama's servers. Everything not recorded is
   lost.
3. **The emulator goes mute.** It has `"jsd"`, `"ktm"`, `"jru"` hard-coded in hundreds of
   places. Without a mapping, porting it to the next patch is redoing it.

The question to answer: **can the mapping from one version to the
next be automated?**

---

## 2. What works, and is measured

### 2.1 Extracting the protocol from the client — SOLVED

`Jondo.Unity.ProtocolBuilder` takes the whole protocol out of the client, without starting it and without
connecting to anything:

```
protocolbuilder proto <path to Ankama.Dofus.Protocol.Game.dll> output.proto
```

| version | messages | fields | enums |
|---|---|---|---|
| 3.6.10.10 Game | 2,169 | 6,186 | 550 |
| 3.6.10.10 Connection | 37 | 92 | 19 |
| 3.6.4.3 Game | 2,169 | 6,165 | 527 |

With **field numbers and types**. It is in `datos/protocolo_3.6.10.10.proto` (207 KB),
`datos/protocolo_conexion_3.6.10.10.proto` and `datos/protocolo_3.6.4.3.proto`.

**Where it comes from.** The client is Unity with IL2CPP. The serialised protobuf descriptor **is not
anywhere** (see §3), but it is not needed: protobuf's C# generator leaves in each class
a constant with each field's number, right before the field that uses it, and **Cpp2IL dumps the
classes whole**. The names are rotated; the numbers and the types are intact.

```
class jsd : IMessage<jsd>, IBufferMessage
    const int epvu = 1, epvw = 2, epvy = 3      ← the field numbers
    static MessageParser<jsd> epvs               ← the parser
    UnknownFieldSet epvt
    lbo epvv, Int64 epvx, lbo epvz               ← one field per number
```

**And the dump was already done.** MelonLoader carries Cpp2IL inside and leaves its output in:

```
<client>/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/
```

Both clients have it: the 3.6.10.10 one in `C:\Jondo 3.6.10.10\Cliente 3.6.10.10` and the
3.6.4.3 one in `C:\Jondo\DofusClient`.

### 2.2 The `oneof`s — SOLVED

242 messages came out mismatched on the first attempt. They were `oneof`s: protobuf keeps all their
cases in **a single `Object` field** plus an enum with whichever is set, so the numbers do not
match the backing fields. **With the properties they do match**, and they also carry the right
type of each case. Zero mismatched, and 128 fields that used to be lost.

### 2.3 Measured facts about the rotation

Comparing 3.6.4.3 with 3.6.10.10:

- **0 of 2,169 messages keep their name.** Absolutely all of them rotate.
- **The field numbers are NOT shuffled.** `hez -> les` pairs cleanly: fields 1..4 with the same
  types. 93% of the messages number 1..N in a row in both versions.
- **The shapes are almost kept**: the histograms of fields per message are almost identical
  (194 vs 176 empty, 462 vs 470 with one field, 556 vs 574 with two...).

> **Corrected (§2.6).** From here it was concluded that «the protocol is practically the same; the only thing
> that changes is the names». It is false, and the histogram is exactly the argument that deceives: the
> silhouette of the pile being the same says nothing about whether each message stays the same. Measured message by
> message, a patch with rotation loses **690 of the 760 unique shapes**. The names change and
> the protocol changes, and the second is what does the damage.

### 2.4 The obfuscator lets names slip — SOLVED, and it is the good vein

Ankama does not obfuscate the whole client. What Unity needs by name —the `MonoBehaviour`s, the
serialised fields, the namespaces, the interface implementations— stays as it is.
Measured on 3.6.10.10's `Core.dll`:

| | readable | of | |
|---|---:|---:|---|
| types | 3,042 | 9,442 | 32 % |
| methods | 15,759 | 110,811 | 14 % |
| fields | 19,930 | 52,408 | 38 % |

And there is a second layer, better still: **a class with a rotated name usually keeps inside it
names that give it away**. They are the `async` state machines, the lambdas and the accessors, which
the compiler names `<OriginalName>d__31` and the obfuscator does not touch because the runtime looks them up
by name.

```
ehl                          ← the class's name says nothing
ehl+<WaitProcessMapComplementaryInfo>d__31::MoveNext      ← but this does
ehl+<WaitForDroppingObjects>d__20::MoveNext
```

**377 obfuscated classes in `Core.dll` keep at least one name like that.** `jss` is touched by `ehl`, so
`jss` is the map's complementary information. Which is exactly what it is.

### 2.5 The matcher's ceiling, measured in two ways

`protocolbuilder probar` rotates the current protocol's names and asks the matcher to
rebuild the correspondence. With the whole answer known:

```
2,169 messages, with the names shuffled
matched right    : 1,481  (68.3 %)
matched WRONG    : 0
ambiguous        : 688

of the 254 the emulator uses that are in the protocol: 154 (60.6 %)
```

Two readings to keep in mind:

- **§3.3's 11.3 % is not what the next patch will give.** It is what the jump from 3.6.4.3
  to 3.6.10.10 gives, which is many patches and a protocol that really changed. A normal patch
  looks more like 68 %, because it changes little more than the names.
- **Zero matched wrong.** The matcher does not make things up: when it is not clear it leaves it ambiguous. That
  means what it produces can be used without reviewing, and that the work is in the 688 ambiguous ones.

And the number that really decides, which is the one §7 asks for: even in the good case, **100 of the 254
messages the emulator uses are left without a pair**. Those are the work of stages 3 and 4.

> **Corrected (§2.6).** Here it said that «a normal patch looks more like 68 %». There is no such thing as a normal
> patch: there are two kinds and they look nothing alike. Without rotation it comes out at 71 %, which is this
> ceiling; with rotation it comes out at 11 %, and there is no middle ground.

### 2.6 Eight real patches, measured one by one

The old clients are still on Ankama's CDN (§2.7), so the 3.6.4.3 → 3.6.10.10 chain can
be walked whole. `protocolbuilder cadena` walks it and measures each jump separately.

This is worth more than everything measured so far, and for a specific reason: **the matcher does not look at
the names at any moment** —only field numbers, field kinds and neighbourhood; the name is the
dictionary key and nothing else—. So a message that has the same name in two versions is a
known answer the matcher **has not been able to copy**. Free ground truth, over real
patches and not over names shuffled by hand.

```
jump                 names  shapes    seeds  rot  │  matched  doubt   solo │   right  WRONG
3.6.4.3 →3.6.5.4      2,169   2,169       760    no  │    1,540    629      0 │   1,540      0
3.6.5.4 →3.6.6.5      1,384   1,470        74    yes │      223  1,305    641 │      —      —
3.6.6.5 →3.6.6.6      2,167   2,167       728    no  │    1,511    656      0 │   1,511      0
3.6.6.6 →3.6.7.7      1,384   1,483        66    yes │      232  1,329    606 │      —      —
3.6.7.7 →3.6.8.8      1,373   1,465        72    yes │      249  1,307    622 │      —      —
3.6.8.8 →3.6.9.9      1,382   1,477        77    yes │      261  1,296    624 │      —      —
3.6.9.9 →3.6.10.10    2,169   2,169       753    no  │    1,574    595      0 │   1,574      0
```

**1. Ankama does NOT rotate in every patch.** Three of the seven jumps keep all 2,169 names one by
one. There are five obfuscation generations in eight versions: `{4.3, 5.4}`, `{6.5, 6.6}`, `{7.7}`,
`{8.8}`, `{9.9, 10.10}`. When it does not rotate, **the mapping is the identity and there is no work to do**.
Checking it costs a second and it is the first thing to look at on patch day.

**2. Zero wrong matches, now over real patches.** In the three jumps without
rotation: out of 6,505 messages, **4,625 right (71.1 %) and 0 wrong**. The real 71.1 % confirms §2.5's synthetic
68.3 %, so the ceiling was well measured; and «it does not make things up» stops being a property
observed in a lab experiment.

**3. The rotation's damage saturates on the first one.** A jump with rotation matches 223–261. The
direct jump, which crosses **four** rotations, matches 245. Crossing four costs the same as
crossing one. So §3.3's 11.3 % was never «it is many patches»: **it is what a
single rotation costs**, and that is all.

**4. Why, in one number.** The seeds —shapes that point to a single message in both
versions— drop from ~750 to ~70 on rotating. Ten times fewer. The matcher seeds with that and then
waters; with seventy seeds no amount of watering will do. And it is not that the rotation touches the shapes: it is that
a patch that rotates is also a patch that changes the protocol, and the shapes it loses are
precisely the distinctive ones. Pure selection — a shape is unique because the message has many
fields, and a fifteen-field message is the one most likely to have one of them touched.

**5. Chaining is much WORSE than jumping in one go.** This was the hypothesis and it is refuted:

```
3.6.4.3 → 3.6.10.10        in one go    along the chain
  pairs                            245             12
  of the emulator's 254             20              1
```

The chain is an intersection: only what survives **every** jump gets through, and it takes only one
jump with rotation leaving 11 % for four in a row to leave nothing. It stays as a measuring
instrument, not as a strategy.

**Consequence.** For a patch with rotation, the structure gives 11 % and that is where it ends: it is not a matter
of tuning the matcher or of getting more versions. What is left is the code index and the
model, that is, stages 3 and 4. They stop being an improvement and become the way.

### 2.7 The old clients can still be downloaded

`protocolbuilder bajar 3.6.4.3 3.6.10.10 clientes` fetches the chain's eight clients.

- Ankama still serves the old manifests:
  `cytrus.cdn.ankama.com/dofus/releases/dofus3/windows/6.0_<version>.manifest`. Of the eleven
  3.6.x versions only 3.6.8.9 and 3.6.9.11 give 403, which must be on another branch.
- The **list** of versions is not in the live `cytrus.json`, which only carries today's (3.5 KB).
  It is in [dofera/cytrus](https://github.com/dofera/cytrus), which merges it every minute instead of
  overwriting it and keeps the ~200 versions published since 3.0.1.1.
- The manifest is a five-table FlatBuffer; the schema is in
  [dofusdude/ankabuffer](https://github.com/dofusdude/ankabuffer) and the CDN layout in
  [ledouxm/cytrus-v6](https://github.com/ledouxm/cytrus-v6). `Cytrus.cs` reads it by hand —five tables
  do not justify dragging in Google's generator— and requests the packages **by range**, so of the
  ~12 GB of a client only the **183 MB** needed are downloaded: `GameAssembly.dll`,
  `global-metadata.dat` and `UnityPlayer.dll`.
- Verified: all three come out **identical byte for byte** to the installed client, and our own dump gives
  the same 245 pairs as MelonLoader's. The CDN clients are interchangeable with the
  installed one.

A downloaded client does not carry `cpp2il_out` —MelonLoader leaves it on starting—, so `Dumper.cs`
rebuilds it with the same library and the same format. The whole dump is written (69 MB per
version) and not only the protocol assembly: the reader needs to see the siblings to resolve
`IMessage`, and with half the folder no class looks like a message.

---

## 3. What does not work, and why

### 3.1 The serialised descriptor is not in the client

Three dead ends, documented so nobody repeats them:

- **There is no base64** in `global-metadata.dat` (38 MB). The 12,471 candidate strings: none
  parse as `FileDescriptorProto`.
- **Not raw either.** The `.proto`s that show up are Unity package paths —
  `...\PackageCache\com.ankama.dofus.protocol.game@cc9f1e...` — because *"protocol"* contains
  *".proto"*. A silly coincidence.
- **Nor in `GameAssembly.dll`** (110 MB): zero `.proto` file names.

### 3.1b The `cpp2il_out` assemblies are hollow

This invalidated half the architecture of this document's previous version, so it goes measured.
The `.dll`s Cpp2IL leaves **do not carry the methods' code**: they carry the signatures and little else.

| | methods | with a body over 16 bytes | total IL |
|---|---:|---:|---:|
| `cpp2il_out/Core.dll` | 110,811 | **0** | 214 KB |
| `cpp2il_out/Ankama.Dofus.Protocol.Game.dll` | 55,930 | **0** | 112 KB |

The median body is two bytes, which is a `ret`. The game's code is compiled to machine code
inside `GameAssembly.dll` (110 MB) and can only be seen by lifting it.

Cpp2IL's `dll_il_recovery` output format was also tried: it recovers nothing in this version,
it comes out just as hollow. What **does** work is using `Cpp2IL.Core` as a library: `Analyze()` on
a method returns its ISIL with the calls and the metadata uses already resolved, and it costs 0.09 ms
per method —the client's 366,413 in twenty seconds—. That is what `CodeIndex` does.

### 3.1c IL2CPP folds addresses, and that manufactures evidence

It is worth writing down because nobody sees it coming and it spoils the data without showing itself. When a native
address is resolved to a method, `MethodsByAddress` does not return a method: it returns a **list**.
IL2CPP shares the code of identical bodies and of generic instantiations. Measured in
3.6.10.10: of 261,768 addresses, **24,227 are shared by two or more methods**, and one of them is
shared by 2,319.

Keeping the first of the list is drawing lots, and when the draw falls on a message the
sighting is attributed to the wrong one. Result before fixing it: six messages with their
whole dossier manufactured —`jzd` was only touched by `TMP_FontAsset` and `FontAsset`, `heo` only by
`System.IO.FileStream`, `hgc` only by `Mono.CSharp` classes— and each one also putting false
edges into the graph, which then spread one and two hops.

Fixed with a one-line rule —if the address does not point to a single one, it does not point— the
index's numbers go down and are real: the messages with readable context go from 548 to **524**, and the
ones reaching a method with a readable name from 121 to **102**. Nineteen of those anchors did not
exist.

### 3.2 Il2CppDumper is no use

The client's metadata is **version 39**, newer than what Il2CppDumper 6.7.46 supports
(`ERROR: Metadata file supplied is not a supported version[39]`). **Cpp2IL does read it**, and it is what
MelonLoader uses.

### 3.3 Purely structural matching — LOW CEILING

`protocolbuilder emparejar <old dll> <new dll>` implements:

- **Fingerprints in rounds** (Weisfeiler-Lehman-style refinement): a message's fingerprint is its
  fields, and each round adds the fingerprints of the ones it points to.
- **Seeds**: pairs with a unique fingerprint on both sides.
- **Watering by similarity**: a double bar — being quite similar (≥0.55) *and* more than any other
  (margin ≥0.08).
- **Dragging through the parents**: if a and b are the same message and both have a field 3 pointing
  to another message, those two children are the same.

Real result over 3.6.4.3 → 3.6.10.10:

| algorithm version | matched | ambiguous | no pair |
|---|---|---|---|
| exact fingerprints only | 108 (5.0 %) | 1,348 | 713 |
| + watering by similarity | 214 (9.9 %) | 1,332 | 623 |
| + dragging through parents | **245 (11.3 %)** | 1,314 | 610 |

**Why it gets stuck there.** Half the protocol is messages of zero to three fields:

```
message xxx {
  int64 f2 = 2;
}
```

That is identical to four hundred others. The shape **does not carry enough information**. Dragging
through the parents is the right idea —a leaf is identified by who points to it— but it does not
get going because the initial seeds are few: a base of reliable anchors is needed that the
structure alone does not give.

This **is not an implementation bug**: it is the signal's ceiling. Whoever has got furthest with
this problem reached the same conclusion and went looking for the signal elsewhere.

> **Clarification from §2.6.** This 11.3 % was read as «it is many chained patches». It is not: a
> single patch with rotation gives 223–261 pairs, almost the same as this jump that crosses four. The
> 11.3 % is the price of **one** rotation. And that is why splitting the jump into one-patch jumps does
> not fix anything —measured: 12 pairs against 245—.

---

## 4. How Snowbot solves it

The bot's decompiled code, in `scratchpad/snowbot/`. Four pieces:

```
FieldsOrderFromIL   uses Cpp2IL.Core to analyse the machine code and find out which fields
FieldReaderV2       each method touches and IN WHAT ORDER, with its pseudocode

MapperFields        builds a prompt per message: obfuscated fields + type + the pseudocode
                    that uses them + an old unobfuscated version, and sends it to an LLM
                    (DeepSeek / o4-mini) asking for the deobfuscated names.
                    With retries on rate limit and a one-hour timeout: they are thousands of calls.

DofusUnityMapping   once there are names, converts messages from one version to another BY FIELD
                    NAME, ignoring the tags (ProtoDynamicMapper)

MapperTester        validates the result
```

Its own prompt (`PromptLLM.BuildDeobfuscationPrompt`) says so:

> *"Je souhaite désobfusquer les champs obfusqués en me basant exclusivement sur l'analyse du
> pseudoCode Obfusqué et une ancienne version non obfusquée."*

### 4.1 And the small print: they have the unobfuscated client

This document's previous version took from there that «the signal is in the code that uses the
message». It is true for them and **does not carry over to our situation**, and it is worth knowing why
before investing a week in copying them.

In `FieldsOrderFromIL/Program.cs`, as is:

```csharp
bool obfu = true;
...
else
{
    pathGameAssembly = @"C:\Users\quent\Downloads\gameassemblyNonObfu\GameAssembly.dll";
    pathMetadata     = @"C:\Users\quent\Downloads\gameassemblyNonObfu\global-metadata.dat";
}
```

`gameassemblyNonObfu`. The prompt's *«ancienne version non obfusquée»* is not an old version: it is
**a build of the client with the names in place**. What they do is align the pseudocode of
the obfuscated function against that of the same function with names, and the model acts as the matcher.

We have two clients and both are obfuscated. Without that clean side, asking the model to
read pseudocode is asking it to name things out of nothing. That is why our stage 3 does not chase the
pseudocode field by field: it chases the names that slipped past the obfuscator (§2.4), which is
what we do have.

Other public projects in the same field:

- [RuinedYourLife/dofus-deobfs](https://github.com/RuinedYourLife/dofus-deobfs) — maps obfuscated
  protos against clear ones, in Go. Input: `.proto`s taken out with Il2CppDumper + protodec.
- [LuaxY/dofus-unity-protocol-builder](https://github.com/LuaxY/dofus-unity-protocol-builder) — the
  catalogue with real names (`Com.Ankama.Dofus.Server.Game.Protocol...`). **Out of date**.
- [Xpl0itR/protodec](https://github.com/Xpl0itR/protodec) — derives `.proto`s from IL2CPP assemblies.
  It is what our `ProtocolBuilder` does, and that is why it is not needed.

---

## 5. Our advantage, which they do not have

Three things:

1. **We do not need the 2,169 messages.** Snowbot is a generic bot and needs them all. The
   emulator names **303 opcodes** and of those **254 are protocol messages** —the rest are
   literals that never reach the wire—. Mapping two hundred and fifty is a bounded and
   verifiable problem; two thousand, not. They are counted one by one, with file and line, in
   `datos/opcodes_emulador_3.6.10.10.tsv`.
2. **We know what a good part of those do.** `jsd` takes an actor off, `kti` hands out chat, `jru`
   loads a map: **99 with a name and meaning** in `datos/anclas_3.6.10.10.tsv`, and 292 with a measured direction
   and shape. Against the real game, not deduced. They are verification anchors and, on the way, the
   examples with which stage 4's model is calibrated.
3. **The library of labelled captures** (`C:\Jondo 3.6.10.10\Wireshark captures from real
   game\`), with named scenes: "salir del mapa", "usar zaap", "otro personaje saliendo del
   mapa". It pins opcodes to specific moments.

None of the public projects has 2 or 3.

---

## 6. Proposed architecture

### 6.1 The five stages

```
┌─ 1. EXTRACT ─────────────────────────────────────────────────────────┐
│  Cpp2IL (MelonLoader already carries it) -> cpp2il_out               │
│  ProtocolBuilder proto            -> protocolo_<version>.proto       │
│  DONE. Seconds.                                                      │
└──────────────────────────────────────────────────────────────────────┘
┌─ 2. ANCHOR (structure) ──────────────────────────────────────────────┐
│  ProtocolBuilder emparejar        -> ~11 % with high confidence       │
│  They are the BIG messages, which are the costliest by hand.         │
│  DONE. Improvable with enum anchors.                                 │
└──────────────────────────────────────────────────────────────────────┘
┌─ 3. READ THE CODE ───────────────────────────────────────────────────┐
│  protocolbuilder indexar <client folder>                             │
│  Lifts the 366,413 methods with Cpp2IL.Core and notes, per message:  │
│    · who touches it, by signature, by call, by type or by address   │
│    · the names that slipped past the obfuscator in those classes    │
│    · the surrounding text strings and the neighbouring messages      │
│  DONE. 20 seconds. 24.2 % of the messages with readable context,     │
│  and 94 of the 254 the emulator uses.                                │
└──────────────────────────────────────────────────────────────────────┘
┌─ 4. DECIDE (the LLM) ────────────────────────────────────────────────┐
│  protocolbuilder expediente / preguntar / evaluar                    │
│  One dossier per message: shape + whose field it is + the code +     │
│  what was measured in the captures + the messages handled next to it.│
│  MANDATORY output: name + confidence + what it is based on.          │
│  DONE and measured blind: see §6.3.                                  │
└──────────────────────────────────────────────────────────────────────┘
┌─ 5. VERIFY (what nobody else can) ───────────────────────────────────┐
│  protocolbuilder evaluar <anchors> <proposals>                       │
│  · Against what is measured: scores any proposals table against the │
│    99 named anchors, and breaks the hit rate down BY CONFIDENCE,     │
│    which is what says whether it can be trusted. DONE.               │
│  · Against the captures: if the proposal says "this comes out when   │
│    crossing an edge", it has to appear in the edge-crossing capture. │
│  · Against the emulator: the 254 we implement have known semantics   │
│    and behaviour that can be checked with the fake client.           │
│  · Against the two-client bench (tools/two_on_a_map.py).             │
│  The last three, TO DO; the pieces exist.                            │
└──────────────────────────────────────────────────────────────────────┘
```

### 6.2 Non-negotiable principles

- **Nothing is accepted without evidence.** An LLM proposal without a confidence and without what-it-is-based-on does
  not go into the table. It is the difference between a mapping and an expensive hallucination.
- **Four confidence levels**, which is what the model declares and what `evaluar` breaks down:
  *sure* (`segura`: the evidence says it almost in so many words), *likely* (`probable`: several signals pointing the same
  way and none against), *possible* (`posible`: it fits, but two or three others would fit) and *none* (`ninguna`). How many
  there are of each is published, along with each level's hit rate: without that, confidence is decoration.
  Measured in §6.3: from *likely* up it can be trusted, *possible* is drawing lots.
- **The human decides the doubtful ones.** That is why an interface is needed (§7).
- **The mapping is a versioned file**: `mapeo_<old>_a_<new>.txt`, in the repo, reviewable in a
  diff.

### 6.3 How often stage 4 gets it right, measured blind

The cheap test with a known answer that §9 asked for, done. The 99 messages whose name is known are taken,
each one's **own anchor** is covered —in the dossier and in the list of
examples, only its own— and they are answered without looking at `docs/opcodes.md` or the anchors table. Checked
in the transcripts: none of the ten answerers opened those files.

It was run **three times**. The first two sweeps with the index that still attributed messages to classes
that do not touch them (§3.1c); the third with the corrected index, which is the one in the repo:

```
                sweep 1     sweep 2     sweep 3
index           with bug    with bug    corrected
no name               69          69          68
give a name           30          30          31
right                 11          11          14        (45.2 %)  ·  over the 99: 14.1 %

  sure              4 of  5     4 of  5     4 of  4     (100 %)
  likely            4 of  7     3 of  8     3 of  6
  possible          3 of 18     4 of 17     7 of 21
```

The bar was tightened three times, and all three because it was over-measuring:

1. Forgiving one word from three upwards gave 56.7 %, counting
   `AppearanceSlotSetRequestMessage` and `AppearanceSlotSetResultMessage` as the same message.
2. Requiring the short name to be entirely inside the long one gave 43.3 %, and still let `TitleSelect`
   through as a hit for `TitleSelectRequestMessage`.
3. Forgiving only what was not a «role» —request, result, success…— gave 40.0 %, and let
   `AuthenticationTicketMessage` through for `AuthenticationTicketAcceptedMessage`. They are `kqz` and `kra`, two
   different opcodes from the anchors table itself.

The one that remains: **the same words, not one more**, forgiving the order, the plurals and the
trailing «Message». Forgiving what is left over forces keeping by hand a list of which words are
filler, and that list has to grow every time an «Accepted», an «End» or a «Storage» shows up.
With equality there is no list to keep. It rejects legitimate synonyms —Teleport versus Zaap— and therefore
it measures low, which is how one has to measure.

What matters is not the 45.2 %: it is that **the confidence is calibrated and stable**. From the three
sweeps:

- **Fixing the index did move the needle**: from 11 of 30 to 14 of 31. With thirty samples that is
  a hint, not proof, but the mechanism is understood —nineteen manufactured anchors disappeared—.
- **Noting in the dossier how many messages each class touches did NOT move anything**: it was the only thing
  that changed between sweep 1 and 2, and they came out identical. It is better to write that down than to claim the
  fix.
- **«Sure» does not fail in any of the three.**
- **63 of the 69 silences match** between the first two. It does not go quiet at random: it goes quiet on
  the same messages.

That turns the output into something usable with a simple rule: the sure goes in, the likely
goes in with review, the possible is a hint for a person to look at, and the two thirds
that stay quiet dirty nothing.

And what stays quiet stays quiet for good reasons. `hid` is an `int32` in field 1 and nothing else;
no amount of model is going to get from there that it is the title the character is wearing. That
only a capture says.

The ones it gets wrong are instructive. In the first two sweeps, `itg` came out `PresetsMessage` instead of
`ShortcutBarContentMessage` and `lyt` the same instead of `OutfitsListMessage`: both times the
culprit is the same context —class `eqq`, which keeps `PresetListEventWhenCharacterInfo`—
stuck to two different messages. In the second sweep the dossier already warned that `eqq` touches
76 messages, and `itg` kept coming out `PresetsMessage` with «likely» confidence. Warning is not enough:
the hint has to be **discounted**, not noted.

And there is a failure that repeats in all three and is not fixed with more code: `jrw` comes out
`GameMapMovementMessage` when it is `GameMapMovementRequestMessage`, and `iwo` comes out
`InteractiveUseWithParamRequestMessage` when it is `InteractiveUseRequestMessage`. In both cases
the message is right and the variant is not: the request instead of the reply, the version with a parameter instead of
the plain one. The shape does not say who is speaking. **The direction is told by the captures and only the
captures**, and it is in the anchors table for the 292 seen going by; the dossier already
shows it when it has it, and that is why the anchored messages do not fail like that.

**A warning about these 99.** They are the best documented messages there are, and that is why they are the
ones that can be measured; they are also the ones with the most context. The 45.2 % is what the pipeline gives
on its best material, not what it is going to give on the other 2,070.

### 6.4 Consequence for the emulator — start NOW

We have `"jsd"` hard-coded all over the code. **On patch day, having the mapping is of no
use if three hundred literals have to be edited by hand.**

An intermediate layer is needed:

```csharp
ConnectionProtocol.Push(Op.ActorLeft, ...)     // instead of Push("jsd", ...)
```

with one table per version that resolves `Op.ActorLeft → "jsd"`. It is a mechanical refactor, it can be done
bit by bit, and it turns "porting the emulator to the next patch" into changing one file. **This can
—and should— be done before anything else.**

---

## 7. The interface

A WinForms window with the look of the rest (it reuses `LauncherTheme`, `LauncherPanel`, `LauncherButton`
from `Jondo.Unity.Contract`). Four areas:

```
┌───────────────────────────────────────────────────────────────────────────┐
│  JONDO — DEOBFUSCATOR                                    [3.6.4.3 ▾]      │
│                                                          [3.6.10.10 ▾]    │
├──────────────────┬────────────────────────────────────────────────────────┤
│ MESSAGES         │  DETAIL                                                │
│                  │                                                        │
│ ▸ sure      245  │   old: hez                new: les        92 %         │
│ ▸ likely    610  │   ┌──────────────────┬──────────────────┐              │
│ ▸ by hand 1,314  │   │ hex  f1          │ leq  f1          │              │
│ ▸ new       ...  │   │ int32 f2         │ int32 f2         │              │
│                  │   │ string f3        │ string f3        │              │
│ [search...]      │   │ int32 f4         │ int32 f4         │              │
│                  │   └──────────────────┴──────────────────┘              │
│ hez → les    92% │                                                        │
│ hee → jlm    61% │   WHAT IT IS BASED ON                                  │
│ hhs → mah    54% │   · identical shape, 4 fields, same types              │
│ ...              │   · the parent hdw is already paired with jkq (fld 7)  │
│                  │   · appears in «usar zaap» after the iwo [see capture] │
│                  │                                                        │
│                  │   [ACCEPT]  [REJECT]  [FIND ANOTHER]                   │
├──────────────────┴────────────────────────────────────────────────────────┤
│  1,235 of 2,169 resolved   ·   of the 104 the emulator uses: 98           │
│  [EXTRACT]  [MATCH]  [ASK THE LLM]  [VERIFY]  [EXPORT]                    │
└───────────────────────────────────────────────────────────────────────────┘
```

What makes it useful and not an ornament:

- **The bottom bar counts what matters**: not "1,235 of 2,169", but **"of the 104 the
  emulator uses, 98"**. That is the number that decides whether the emulator starts with the new patch.
- **"What it is based on"** with the evidence for each proposal, and the button to open the capture where
  it appears.
- **Accept / reject** one by one, because the doubtful ones are decided by a person.
- The buttons at the bottom are the five stages, in order, and they can be repeated separately.

---

## 8. How the repo stands

```
Jondo.Unity.ProtocolBuilder/          the project, in the solution
  Program.cs                          the commands
  AssemblyReader.cs                   reads assemblies without running them (MetadataLoadContext)
  ProtoWriter.cs                      rebuilds the .proto from the classes
  Matcher.cs                          fingerprints, watering and dragging
  Shuffle.cs                          shuffles names to measure the ceiling
  DescriptorExtractor.cs              the dead path of the serialised descriptor (§3.1)
  ClientReader.cs        NEW          opens the real client with Cpp2IL.Core
  CodeIndex.cs           NEW          stage 3: which code touches each message
  Dossier.cs             NEW          stage 4: a message's dossier
  Llm.cs                 NEW          the model, with cache, retries and limit
  Cytrus.cs              NEW          downloads an old client from the CDN, only what is needed
  Dumper.cs              NEW          rebuilds the cpp2il_out a downloaded client does not carry
  Relay.cs               NEW          walks the chain patch by patch and measures each jump (§2.6)

datos/protocolo_3.6.10.10.proto       2,169 messages with numbers and types
datos/protocolo_conexion_3.6.10.10.proto
datos/protocolo_3.6.4.3.proto         the old version
datos/mapeo_3.6.4.3_a_3.6.10.10.txt   245 pairs + ambiguous + no pair
datos/indice_3.6.10.10.json      NEW   stage 3 already run (1.4 MB)
datos/anclas_3.6.10.10.tsv       NEW   what is known about each opcode, and where from
datos/opcodes_emulador_3.6.10.10.tsv  NEW   the ones the emulator uses, with file and line
datos/propuestas_ciegas_3.6.10.10.tsv NEW   the measurement of §6.3

scratchpad/snowbot/                   Snowbot's decompiled code (outside the repo)
```

Commands:

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- proto <dll> <output.proto>
dotnet run --project Jondo.Unity.ProtocolBuilder -- emparejar <old dll> <new dll> <output.txt>
dotnet run --project Jondo.Unity.ProtocolBuilder -- probar <dll> [emulator opcodes.tsv]
dotnet run --project Jondo.Unity.ProtocolBuilder -- mirar <dll> [type]
```

The clients of other versions, and the measurement of §2.6:

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- bajar --lista
dotnet run --project Jondo.Unity.ProtocolBuilder -- bajar 3.6.4.3 3.6.10.10 clientes
dotnet run --project Jondo.Unity.ProtocolBuilder -- cadena clientes datos/opcodes_emulador_3.6.10.10.tsv
```

`bajar` is 183 MB per version and a few seconds; `cadena` takes a few minutes the first time because
it rebuilds each client's `cpp2il_out`, and seconds afterwards because it is already written.

The new pipeline, from start to finish:

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- indexar "C:\Jondo 3.6.10.10\Cliente 3.6.10.10" datos/indice_3.6.10.10.json
```

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- expediente <dll> datos/indice_3.6.10.10.json datos/anclas_3.6.10.10.tsv jss
```

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- preguntar <dll> datos/indice_3.6.10.10.json datos/anclas_3.6.10.10.tsv datos/propuestas.tsv --evaluar
```

`preguntar` needs `JONDO_LLM_KEY` or `ANTHROPIC_API_KEY` in the environment; the model and the address
come from `JONDO_LLM_MODEL` and `JONDO_LLM_URL`, so pointing it at another provider touches no code. Without
a key, `expediente --todos` dumps the 2,169 dossiers to answer them some other way, and
`evaluar` scores the table wherever it comes from.

---

## 9. Where to go on

Points 2 and 3 of the previous list are already there: stage 3 runs in twenty seconds and stage 4
is measured blind (§6.3). What is left, in order of value:

0. **Look first at whether it has rotated**, which is free and comes out right almost half the time (§2.6). The
   two sets of names are compared: if all 2,169 of the old one are in the new one, the mapping is the
   identity and there is nothing to solve, neither with the matcher nor with the model. It happened in 3 of the 7
   patches measured. Today the application does not check it: it starts the whole matching anyway, and in
   those cases it spends a minute rebuilding something it already knew. It is half an hour of work and it takes
   the whole problem out of the way almost half the patches.
1. **The `Op.` layer in the emulator** (§6.4). It is still the first thing and the only thing that has to be
   done BEFORE the patch. It depends on nothing else, and without it the mapping is of no use: there are 496
   three-letter literals spread over 303 different opcodes in `Jondo.Unity.Launcher`, and
   editing them by hand on patch day is not a plan.
2. **Put the anchors into the matcher as seeds, but for what they really give.** It is
   tested in a separate experiment: K true pairs are injected into `Matcher.Match` and it is measured.
   **There is no cascade**: each anchor contributes between 0.04 and 0.27 new pairs depending on the drift, that is,
   eight hundred anchors do not fix the coverage problem. What they do do, and a lot, is get things right:
   with a 40 % drift, the WRONG matches drop from 94 to 35. The reason is that
   dragging through the parents is already saturated —the missing seeds are not few, they are the ones that do not
   exist— but a false pair drags another one behind it, and the anchors cut those chains. It is worth
   it for precision, not for coverage, and the injection point is `Matcher.cs`, right after
   `var signB = Signatures(b, rounds);`.
3. **Discount the shared context, do not note it.** Noting it is already done —the dossier says
   «eqq (toca a 76 mensajes)»— and it is measured that **it does not help**: `itg` kept coming out
   `PresetsMessage` with «likely» confidence. What is missing is for `CodeIndex` itself to stop
   offering as context the classes that touch half the protocol, or to put them last and
   marked. A simple threshold —out with the ones over ten— is the first thing to try, and it is
   measured by repeating the sweep of §6.3.
4. **The `map<>` fields never count.** `ProtoWriter.Describe` writes them as
   `map<string, hqu>` with the rotated name inside, and neither `Matcher.Kind` nor `Matcher.Similar` know how to
   read that: they are 41 fields in 25 messages of 3.6.10.10 that today add no similarity even when they match.
   It is the cheapest fix left in the matcher.
5. **Anchors through enums**, with adjusted expectations: 496 of the 550 enums are
   simply `0..N-1`, so their fingerprint by values says no more than how many values they have.
   Pairing them by that alone gives **13 deterministic anchors**, not two hundred.
6. **Make `proto` and `emparejar` complain about the wrong assembly.** Pointed at
   `MelonLoader/Il2CppAssemblies/Il2Cpp*.dll` instead of `cpp2il_out/` they return zero messages and
   carry on printing «0 mensajes» and «NaN %» as if nothing happened. The good ones are the ones in
   `cpp2il_out`.
5. The interface (§7), when there are proposals to review for real.

And what never changes: **keep capturing**. Of the 99 messages of §6.3, the 69 the model
stays quiet about stay quiet because their shape says nothing —`hid` is an `int32` and that is all—. No
model and no analysis of the binary fixes that: what fixes it is having recorded the moment it happened.
