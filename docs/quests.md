# Quests

How a quest is taken, how it progresses and how it ends. Everything here comes from measuring the 401
Wireshark captures and the client dump; what is not measured is said not to be.

---

## 1. The six opcodes

```
S->C  ief  {1: quest}                                    starts
C->S  ieo  {2: quest}                     ->  S->C  idu  the step and its objectives
C->S  idw  {1: quest, 2: objective}                      the client takes an objective as done
S->C  idz  {1: quest, 2: step}                           the server validates the step
C->S  iec  {1: quest}                                    asks about one of its quests
```

The directions are checked **by ports**, not only by the envelope's root field.

### Why it is known they are quests and not something else

This repository's documents filed `ieo`/`idu` as the interactive elements pair
and `idz`/`idw` as «connection extras». It was an old assumption, of the same kind as the one that gave
quest names to `lry`, `isf`, `lol` and `izu`, which **do not appear in any of the three quest
captures**.

What settles it is not that the numbers «look like» quest ids —a small number looks like one by
chance— but that **they match each other**:

| | |
|---|---|
| `idu` frames in the 401 captures | 448 |
| …whose step really belongs to the quest they name | **448 of 448** |
| Objectives inside them | 1,479 |
| …that really belong to that step | **1,479 of 1,479** |
| Coherent `idz` frames | 21 of 21 |
| Coherent `idw` frames | 16 of 16 |

448 (quest, step) pairs taken from a wrong reading of the format do not come out coherent.

### The field that means the opposite of what it seems

In each objective of an `idu`, field 4 is 1 or absent. The obvious thing would be for 1 to be «done». It is the
other way round. Following step 2249 through the tutorial capture:

```
[(9655, 1)]                                        9655 is what has to be done
[(9655, ·), (9656, 1)]                             9655 done, now 9656
[(9655, ·), (9656, ·), (9657..9661, 1)]            both done, five more
```

**The mark leaves the objective that is met.** And several can be pending at once, so
a step cannot be modelled as a pointer into a list.

`Jondo.Unity.Tests/Protocol/QuestProtocolTests.cs` compares byte for byte what this
server builds with those frames.

### A difference on purpose

Ankama's server sends a **growing prefix** of the step's objectives: 3183 declares
four and the capture shows two, then four. This one sends them all at once, because which ones
Ankama considers «revealed» is not in any data we have, and showing too much is less bad than
hiding an objective that is needed.

---

## 2. How it is taken

The hook is the dialog, and it is in the client's own data: **a quest step declares the
NPC line it is handed out with**.

```
quest.startPosition  ->  npcId + mapId     who hands it out and where
step.dialogId        ->  line id           the exact line it is given with
```

**1,260 of the 2,225 steps** carry `dialogId`, and **all 1,260 resolve to real text**. Of those,
1,177 belong to a quest that starts at an NPC with a name and a map.

The capture `Misiones\hablar con NPC y aceptar una mision` shows the whole chain: the client opens
the dialog on map 212863492, the server takes it down to line 50071, the player picks
answer 66788, and **then** `ief {2432}` comes out. Quest 2432 says it is handed out by NPC 6617 on
map 212863492 and its only step declares `dialogId 50071`. Three independent numbers, a single
story.

**After picking the answer, not on reaching the line.** That is the capture's order and it is the
one the engine copies.

### Which of the answers is the one that accepts

**It cannot be taken from the captures.** The answer that gave the quest carried an extra field, but
that field shows up in **184 of the 429 captured answers** and almost none of them are quest ones: it is not a
quest mark.

The tree says it, with `startsQuest`. Before that, any answer of the line started the
quest —«No, gracias» too—, which is what still happens on lines without a written tree. See
section 7.

---

## 3. The start condition

Ankama writes it as one string per quest. The grammar is measured over all 1,976:

```
condition := term | condition '&' condition | condition '|' condition | '(' condition ')'
term      := OP CMP VALUE (',' VALUE)*
OP        := two letters           29 distinct
CMP       := '=' | '!' | '>' | '<'
```

Three things easy to get wrong, and all three checked:

- **«Not equal» is a bare `!`, never `!=`.** `Qa!496` is «quest 496 is not in progress». There is not
  a single `!=` in the whole file, and treating the `!` as noise would invert 236 conditions.
- **There are parentheses and they nest up to three levels.** 170 quests use them.
- **Precedence does not matter in practice.** 168 conditions mix `&` and `|` and every mix
  goes in parentheses. `&` binds tighter, as in C, which is the reading that agrees with all 168.

And two Ankama oddities to read without choking: **`E` as a fifth comparator** (2 uses,
`POE14271` and `POE11563`) and **a value with a letter**, `PJ>a,199` (1 use).

### What it knows how to judge and what not

Its own six: `PL` level, `Qf` quest finished, `Qa` quest in progress, `Qc` finished as well,
`Qo` objective met, `Pm` current map. And what the server knows of the character and the world:
`Ad` today's Almanax (its 390 offering quests), `OA` an achievement, `PG` the class (the class
quests: «The Tymador Job» is `PG=13`), `PJ`/`Pj` a job's level, `PO` an item held, `QF`, `SC` the
server's type, `PZ` the subscription, `BT` (the client's AlwaysTrueCriterion), `Ps`, `Pa`, `Pr` the
alignment's side, level and rank, `WE` a world event going on (Osavora's season or Gargandias's weekend),
`DD`, `DH` and `DM` the days, hours and minutes since an item was received (its «Recibido el»), `SH`
the server's hour (Paris) and `HA` an alteration carried. `Sc` is the client's StaticCriterionCriterion,
which only the real server judges and no data describes; its values were read off the quests each
guards and Dofus pour les noobs (`SeasonalEvents`): most are content that is simply on -- 703 and 702
Frigost, 835 and 822 wanted posters, 940 the Ankama building, 842 the Crocabulia line... -- and five
are the yearly events, on only while they run: 967 Halouine (two weeks at the end of October), 982 Sain
Ballotin (the week of 14 February), 945 the island of Pwâk (four weeks before May), 979 Vulkania
(July and August) and 953 the Ripate's feast (two weeks around 19 September).
Together they cover **every** term of 1,964 of the 1,976 conditions; the rest are the tournament
server's (`ST`) and three loose ones (`Sv`, `Pz`, `Nf`).

`Qc` is read as «finished» because of what appears next to it: `(Qa=890|Qc=890)` is «890 is in progress
or already done». `Qo` carries objective ids, 116 of 116.

What it does not understand —the seasons (`Sc`), a few server flags— **it lets
through and says so**. Rejecting it would leave quests out of anyone's reach, which is a worse answer
than offering them early. The terms it does understand are still required.

The alignment's side is the character's own: neutral until Chantal Inea has him swear to a city, so
the Bontarian and Brakmarian quests (`Ps=1`, `Ps=2`) are for those who have. Its level and rank are
the quests' own (`Alignments.Progression`), read off the catalogue's conditions:

- **The level** (`Pa`) is each side's chain of a hundred quests, each asking for the level it raises:
  «Entraînement avec Tarche» asks `Ps=1&Pa=0`, «L'exorciste» `Ps=1&Pa=99`. Finishing the one that asks
  `Pa=N` leaves the level at N + 1. The sheet shows it in its alignment block's f1, measured at 100 on a
  level 200 Bontarian.
- **The rank** (`Pr`, the client's AlignmentRanks) is the orders' «Apprentissage» quests: each asks for
  the rank it leaves behind and the next one for the rank it gives (433 «Disciple de Ménalt» asks
  `Pr=1`, 109 «Écuyer» `Pr=2&Qf=433`, so 433 gives 2); the last of an order gives the rank after its
  own. A sworn character with no order is his side's Néophyte, 1 or 18. The client is told it in
  `hod` f1, on entering the world and when it changes; the capture's was its capturer's 17,
  «Maître des Illusions», and every character used to get it.
- **Leaving the city** with Morry Gann forgets both, as he warns: «¡Perderás todo lo que has dedicado a
  la milicia de tu ciudad!». His fee goes by the level, ten levels a step (inferred).

Osavora's weekly quests ask for their season and a week since their marker: «Una pequeña comezón» is
`PL>179&Qf=2515&(((WE=230|WE=255)&(DD>7,31582|PO!31582))|...)`, the Repos (world event 230) and not
having received its marker (31582) in the last seven days. `DD` is the one operator whose first number
is not the key: the days come first, the item second.

### The chain

990 quests require another one before, and there is the Astrub chain as it is:

```
quest 56  Ps=1&Pa=1&PL>29&Qf=55
quest 57  Ps=1&Pa=2&PL>29&Qf=56
quest 58  Ps=1&Pa=3&PL>29&Qf=57
```

---

## 4. How an objective is met

There are 18 types. The engine closes them in two ways:

**The client says so** (`idw`). Type 0 ones are free text —5,670 of the 15,547— and ask for clicking
something in the interface, which the server never hears about. It is believed, and the risk is bounded in
`QuestLog.Tick`: it only accepts an objective **of the step the character is really on**, so
the worst a lying client can do is finish a quest it already has, in the order in
which that quest is written.

**The server counts it** (end of fight). Three types name a monster, and in all three
`parameter0` is the monster and `parameter1` how many:

| Type | What | How many |
|---|---|---|
| 6 | defeat N in a single fight | 776 of 788 with a real monster |
| 14 | defeat N, accumulating across fights | 143 of 143 |
| 16 | defeat N on a specific map, in one fight | 88 of 88 |

Summons do not count. They are on the opposing side with `IsMonster` set, and this project already
tripped over that twice: a summoning monster was paying kamas for creatures it made
itself.

---

## 5. What is stored

```sql
CREATE TABLE CharacterQuests (
    CharacterId INTEGER NOT NULL,
    QuestId     INTEGER NOT NULL,
    StepId      INTEGER NOT NULL DEFAULT 0,
    Objectives  TEXT    NOT NULL DEFAULT '',
    Completed   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (CharacterId, QuestId)
);
```

`Objectives` is two halves split by a bar: the objectives already done, and then the ones half
way with their count. `18390,18391|18392:3`.

It is written **at the moment of the change**, not on leaving. On this server there is no periodic save and
`SaveCurrentCharacter` only writes the `Characters` row, so whatever waits for logout is
lost in an ugly shutdown — and losing an afternoon's quest is worse than losing a few kamas.

---

## 6. Someone else's journal

The block the server formerly replayed on entering the world carried **261 `idu` frames**: the whole quest
journal of the captured account. Everyone who entered saw a stranger's
quests, and since there has been an engine they also contradicted what the server believes.

The replay and its skip list are gone. `Quests.SendJournalAsync` sends the journal
of the character connecting from that character's own stored state.

---

## 7. The dialog trees

Without a tree, the server only knows how to send **the first line the NPC's template declares**, with
all its answers at once. Snori Nairb offers its thirty-nine at the same time, none of them lead
anywhere, and the line where the quest is handed out is never reached.

Of the 1,260 steps handed out by talking:

| | |
|---|---|
| Reachable without a tree (the line is the template's first) | **21** |
| The line exists in the template but is not the first | 64 |
| **The line is not even in the template** | **1,092** |

### Where they come from: two sources, and the second is better

There are two ways of recovering the «which answer goes on which line» pairing, which is the only thing
that never came out of Ankama's server.

#### a) The dofuspourlesnoobs guides — it worked for Astrub

They write the player's answers in French, in French quotation marks. It works because of two things
measured, not assumed:

- **Within one NPC, 98.3% of its lines are identified by their text.** Across the whole game
  it is only 70% —«Hasta luego.» is said by hundreds— but a conversation is with one NPC.
- **The answer ids do not have to be Ankama's.** 36.4% of an NPC's answers
  share text with another of its own, and it does not matter: the server sends the ids and the client returns the one
  it was given. Any one with the right text will do as long as the tree is coherent with itself.

`tools/dialogue_from_guide.py` pairs line → id. `tools/build_dialogue_trees.py` builds the tree
and writes it. The hand-made ones carry `_byHand` and are not overwritten.

**Beware of the website's cache:** it was caught serving another quest's page under the same
URL, two responses of the same size and different content, **and the swap depends on the
user-agent** — with bare curl one quest comes out and with a Chrome user-agent another. A
response was even seen whose `<title>` was the right one and whose body heading was another quest's, so
comparing titles is not enough: the body heading has to be looked at. The underlying protection is that
the answers are paired against the NPC that gives the quest: if the page is another one, none match.

#### b) The client's own conversation — it was needed for Incarnam

**For Incarnam the guide is no good.** The 24 pages were downloaded and each one was read twice: all
24 exist, but **21 do not print a single answer option** —they are narrative prose, «Parlez à
Berb Nhin», «Ramenez les Orties»— and of the three that do, two attribute the answers to an NPC
other than the one giving the quest. It is not an extraction failure: the raw HTML was searched for French
quotation marks, `<i>`, `<em>` and `font-style:italic`.

The good source was at home. The client carries:

| | |
|---|---|
| the lines the NPC's template declares | with their text |
| **all** the answers that NPC can give | with their text |
| **the text of the line each step names** | even if the template does not declare it |

That is, everything but the pairing. And the texts answer each other in plain French: line
20877 says Corporal Mynerve is waiting at the top of the tower, and answer 25045 says
«Accepter d'être mis à l'épreuve et se diriger vers l'escalier». That can be read and written.

`tools/npc_conversation.py <npc>` dumps that, and `--category 19` does it for all the ones who hand out
quests of a category. `tools/merge_authored_trees.py` checks the written tree against the
template and the catalogue before letting it in, because writing by hand gains in fidelity and loses
the one thing a generator gives: not being able to misread. It checks that the answer belongs to the
NPC, that it is not repeated, that every `next` lands on a line of the tree, that the line is real, that the
handing out is where the step says, that no answer hides itself, that there is an exit left,
and that placed + discarded add up to all of the NPC's.

These trees are stored with `_byHand`, like the ones written with the editor.

### What each answer is for

| | |
|---|---|
| `startsQuest` | this answer **gives** the quest |
| `quest` | only offered with that quest in progress |
| `step` | and only on that step |
| `afterQuest` | only once finished |

`startsQuest` is separate from `quest` and has to be: marking as «of the quest» the answer that
**starts** it would hide it until having it, and then nobody could take it.

Before this, **any** answer of the line gave the quest, so «No, gracias» did too.

## 8. The green mark over the NPC

It is the `iom` opcode:

```
1 { 2 (repeated) { 2: <packed quest ids>, 4: actor }, 3: map }
```

The **294 numbers** the 380 captured frames carry are quest ids, all 294. And it can be seen
going out: in the tutorial an actor arrives with `[2511]` and later the same actor arrives with the list
empty, which is exactly when it is taken. **235 of the 380 are empty** — that is how the mark is erased.

It is sent on arriving at a map and again on taking a quest. **Every NPC on the map is named**,
also the ones that have nothing: leaving one out says nothing about it and the client would keep painting what
it had last time.

### What can be taken is marked, not what the catalogue promises

The catalogue names a giver in **1,958** quest/NPC pairs. Of those, only **70** can be
handed out with a conversation this server knows how to have: 43 because there is a written tree that gives
them, and 27 because the line the step names turns out to be the template's opening one. Marking the
other 1,888 would put a green mark over almost everybody that **never goes out**, however much
the player talks, because that line is never reached.

So the mark comes from `QuestsOfferedBy`, not from the catalogue. And it goes both ways: the tree
can also offer a quest the catalogue assigns to nobody —there are **155 without a giver**, and
«Mort au rat !» is one of them even though the innkeeper Grobid declares the answer «Dire que vous avez
vu l'affiche placardée dehors», which is exactly the poster it starts with.

Watch out for a consequence that bites: **writing a tree takes away from the NPC the quests you do not
mark in it.** Without a tree any answer of the line the step names will do; as soon as there is
a tree, `NpcHandler` stops asking the catalogue and only gives what carries `startsQuest`. A tree
that takes the handing-out line without marking anything on it leaves a flawless conversation that
hands out nothing. `AuthoredDialoguesTests` watches over it.

## 9. The rewards

Experience and kamas are **multipliers** —2, or 1.2, or 0.035— and the base they
multiply is in the client's code, which shows it before giving the reward: Core's class `lg`,
the same formula for quests and achievements, with the step's optimal level and its duration. See
`docs/achievements.md` §4 and `RewardFormula`. Measured: tutorial quest 1629 pays 141 at level 2
in the capture, and that is what it pays here (with the 5 % bonus that character had).

Only the reward of the character's **level band** is paid: 4,555 of the 6,707 carry
`levelMin`/`levelMax`, and the Almanax offerings declare ten, from 9-29 to 190-200. They used to be
paid all together. The reward's attitudes are taught with `khi`.

## 10. The Almanax

INFERRED entirely: no capture sets foot in the sanctuary. Each of the 376 days of the client's calendar
leads to an ordinary quest, «Ofrenda para …», with the condition `PL>19&Ad=<day>`. `Ad` is
answered by `Managers.Almanax`: today's entry. All 376 are given by Ontoral Zo (NPC 1625) and
no step names a line of his, so the rule for NPCs without a tree applies: his
opening conversation hands out today's. Once a day.

Which day it is today: each date of the year is named by two entries, the month's saint (only that day) and Bryss
(31 to 34 days, «se encargará de reemplazar lo irremplazable»), and six days a year a moveable feast
with its year. The most precise one wins: the date with a year, and otherwise the entry that names the fewest days.

Today's saint stands in the temple, alone, in the room of its day (`Managers.AlmanaxTemple`). The
season is the one the offering's prayer names («Reza ante el altar de verano»): spring from 20 March,
summer from 21 June, autumn from 22 September, winter from 21 December. Each season has four rooms,
three alike and one drawn darker; the 80 saints whose objective names a map stand in summer's three,
a month each from the 21st (101453313 July, 101453315 to 20 August, 101453317 to 20 September), and
the other seasons' rooms are read the same way. The sanctuary's door (element 476222) leads to the
hall of today's season -- the one of the four halls whose door of that season is drawn otherwise --;
there that door leads to today's room and the other three to their dark rooms; every room's way out
back to today's hall. The cell the saint stands on is ours (302): no capture enters the temple.

Every type of the days' bonuses is applied, to everybody: they hang on the date, not on having made
the offering. The client gives only their numbers; each is named after the day whose description
says what it does, and so are their conditions (an area, a subarea, a monster or its race, a job, a
skill, an item worn or obtained, the dungeons, the anomalies' zones; `Almanax.BonusType`, `Fits`).
The last two named: 26, «la cantidad de carne que consiguen los cazadores aumenta un 125%», on the
quantity of each meat named, and 34, «un bonus del 75% a las posibilidades de conseguir botín en los
Sueños». The 21 days with no bonus are events of their own (repeatable quests, treasure chests,
surprise gifts in the loot).

**«El año del dragón» (quest 954) and the Dolmanax.** Ontoral hands it over with the offering, once.
While it is in hand each offering made gives a page of the calendar (item 13345) -- «Ontoral Zo te
dará una página de calendario cada día que pase» --, and the 365 pages brought to him finish it and
give the Dolmanax (13344): «Toma este dolmanax, este auténtico huevo de dragón». The quest, the page
and the 365 are read off the data (the one quest of his that is not an offering and asks for an item
by the hundreds); the step's reward in the data carries only experience, so the page and the
Dolmanax are handed over by `Quests` when an offering or the quest finishes. The achievements for
100, 200 and 300 pages (`PO>13345,99`...) and for the quest (`Qf=954`) follow by themselves.

## 11. What is missing

- **Editing quests.** Studio shows them; it does not write them.
- **The escorting objectives.** Type 10, 26 of them: their parameter is a text, not who to escort.

Not missing, though it looked so: **`repeatLimit`** is 1 in all 1,976 quests, and every `QF` of the quests'
and achievements' conditions is `QF>quest,0` -- finished at least once --, so no count of how many times
a quest was done is ever asked for.
