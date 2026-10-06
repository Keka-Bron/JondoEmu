# Achievements

Earning them, seeing them in the window and claiming them. The data comes from the client dump and the protocol
from the captures; what is not measured is said not to be.

---

## 1. What an achievement is

```
achievement  name, category, level, points, objectives, rewards
objective    a criterion. ALL of them have to hold.
reward       items with their quantity, experience and kamas as RATIOS, ornaments,
             titles, emotes, spells, guild points — and its own criterion,
             because an achievement can pay differently to different people.
```

The criteria are written in **the same language** as a quest's start condition,
parentheses and `!` included, so the same evaluator reads them.

| | |
|---|---|
| Achievements | **2,780** |
| Objectives | **8,946** |
| Rewards | **6,394** |
| Categories | 134 |
| Objectives the client names and does not describe | **322 achievements**, 272 tied |
| Achievements with all their objectives in terms this engine judges | **2,172** |

---

## 2. The protocol

Everything comes from the captures, and where the captures are silent, from the client itself: Core's class `epj`
(whose only readable name is `DelayedClearAchievements`) receives exactly these messages and hands them
to the achievements window with names the obfuscator did not touch — `OnAchievementList`,
`OnAchievementFinished`, `OnAchievementRewardSuccess`, `OnAchievementDetailedList`,
`OnAchievementAlmostFinishedDetailedList`, `OnAchievementDetails`. Class `epf` builds the
five requests.

```
S->C  mft  {1 (rep): {2: character, 3: achievement}}            the list, on entering the world
S->C  mfu  {1: {1: CHARACTER level, 2: character, 3: achievement}}   earned
C->S  mga  {1: achievement}  or {1: -1}                         «pay me»
S->C  mfs  {2: 1, 4: achievement}                               paid

C->S  mfe  (empty)          -> S->C  mgb  the ones closest to being earned
C->S  mfp  (empty)          -> S->C  mfx  (root 3, empty)
C->S  mff  {1: category}    -> S->C  mfo  the whole category, with each objective's progress
C->S  mfm  {1: achievement} -> S->C  mfg  one achievement     INFERRED: no capture
```

**`mfs` does not go with `mfu`.** In the nine captured frames it always arrives after an `mga` and the
sheet the claim changed. It used to be sent on earning; no longer.

**The window**, in `Chats\usando todos los chats`, frames 103 to 108: `mfe`, `mfp` and
`mff {40}` go up and `mgb`, `mfx` and `mfo` come down in that order. Each request is answered with the reply that
holds the same position. The `mfo` of category 40 lists its **23 achievements, all of them**, earned or not,
the pending ones first. Each objective carries `f1` the objective, `f2` out of how much, and `f4` how far it has got
**even at zero** (`20 00`) while it is not done; a done one carries no `f4`. «Ez>1,99» travels as 91
out of 100: the maximum is the threshold plus one. The `mgb` is six started achievements from six categories; how
the real server picks those six is not in the capture, and here they are the six most advanced.

**The list on entering** carries no level in any of the 5,198 entries of the seven captured `mft`,
because those accounts had everything claimed. An achievement earned and not claimed is sent the way `mfu` sends it, with the
level in `f1`: INFERRED, it is the only thing that tells the two apart in that message.

**The claim**, measured in the tutorial and in the five captured `mga`, per achievement:

```
ivf {kamas}, lqn 45 «has ganado N kamas»     if it pays kamas
kua {level}                                   if it levels up
kub (the sheet), kuf {1: experience gained}
iua ...                                       the items
khi {1: attitude}                             the attitudes
mfs {2: 1, 4: achievement}
```

`kuf` carries the experience gained: in twelve frames its `f1` is exactly what the experience
of the previous `kub` went up by (545 on the Pandala route, 109 on the rats one). After a fight it travels empty.

---

## 3. What makes them progress

The catalogue files each achievement under everything its objectives mention (`(operator, key)`), and
each thing that happens asks only about its own:

| Happens | Looks at | Operators |
|---|---|---|
| Finishing a quest | the ones waiting for that quest | `Qf` `Qc` `QF` `QQ` |
| Reaching a map | the area, if new; the level, if it changed; the bag; the map | `Xs` `PL` `PO` `Pm` |
| Winning a fight | each defeated monster; with a challenge met, again; the challenges | `EM` `Ef` `EH` |
| Crafting | what was crafted and the jobs | `Xc` `Xj` |
| Levelling a job | the jobs | `Xj` |
| Entering the world | everything, once | |

`Xs`, `Xj` and `Xc` are our keys, not the client's: the achievements that use them are the ones the client
does not describe. The fight's part is set aside and looked at on returning to the map, so as not to put an `mfu` in
the middle of the end of the fight, where no capture has it.

The counters nothing else keeps are kept in `CharacterAchievementCounters` (monsters defeated,
defeated with a challenge, areas visited, items crafted, and the day of the last Almanax offering).

### The 322 the client does not describe

Their objectives are not in the client's table: the server judges them on its own. What they ask for
is in the achievement's own name and description, and `tools/extract_achievement_links.py` reads it:

| | | |
|---|---|---|
| explore an area | 224 | the achievement is named after the subarea. **Measured**: the 17 the captures earn trigger on entering the subarea of that name |
| defeat a monster | 18 | the dopples |
| finish a quest | 13 | «Terminar la misión: …», by name |
| reach a level | 11 | «Alcanzar el nivel N» |
| level N in M jobs | 5 | |
| craft | 1 | «Fabricar 1 objeto»; the tutorial earns it right after the ring |

50 are left untied, and they are not earned: the Temporis levels, breeding mounts, eating sweets, breaking a
miaumiau, opening a gift, «Criptas» (no area is called that), the feca dopple (two monsters with
that name).

### The rule the other way round from quests

In a quest's start condition, **what cannot be judged is let through**. In an achievement
it is **the other way round: what cannot be judged is NOT granted.** But it is judged with three answers, so
an unknown term that an `|` has already made irrelevant blocks nothing: `SC=0|(SC=5&ST!7)` is
true on a classic server whatever `ST` is.

`SC` is the server type from the client's table (`server_game_types`): 0 «Clásico», 5
«Temporis». This server is classic, so the Temporis achievements are not granted.

### The cascade

`OA` is «achievement obtained» and the most common operator: 2,157 objectives. 8520 «Con bases sólidas» is
`(OA=8518)` and `(OA=8519)`. Earning one can earn others in the same move; so can the points
ones (`Oa>999`). It is walked breadth-first with a cap.

---

## 4. What is paid

**Experience and kamas, with the client's own formula.** The client shows the reward
before giving it, so the formula is in its code: Core's class `lg`, `nza` (experience) and
`nzd` (kamas), the same for quests and achievements. Disassembled, with the constants read from
GameAssembly.dll:

```
fixed(L)  = L · trunc((100 + 2L)²) / 20 · duration · ratio
L ≤ A     experience = fixed(L)
L > A     experience = 0.3 · fixed(A) + 0.7 · fixed(min(L, trunc(1.5 · A)))
then      · (1 + bonus/100), · limit
kamas     = trunc(level² + 20·level − 20) · ratio
```

L is the character's level (capped at 200), A the achievement's or the step's optimum, the duration 1 in an
achievement. Against the nine claims in the captures it comes out **exact**, with a 5 % bonus those
characters had (and 110 % the rats one) — the bonus belongs to the character and is not modelled here. Two
rounding differences of the server against the client: it adds the two halves before truncating, and
it multiplies the bonus with more precision than a float. With the client's rounding it would come out 26 and 544
where the captures say 27 and 545.

**Rewards with a condition.** 1,060 say `Ob!<the achievement itself>`: it cannot be «does not have it»,
because whoever claims has it, so it is read as «has not been paid yet» (INFERRED; in Ankama it is
the account, here the character). It is judged before marking it claimed. `PO!10207` is «does not have that
item». What cannot be judged is not paid.

**Items**, to the bag. **Attitudes**, learnt with `khi`. **Titles and ornaments** are
noted down and nothing is sent: every character already has the 539 and the 167 offered. **Guild
points**, no.

---

## 5. What is stored

```sql
CharacterAchievements        (CharacterId, AchievementId, Claimed)
CharacterAchievementCounters (CharacterId, Kind, Key, Count)
```

The server creates them at startup, and so does whoever reads or writes them, because a database freshly taken out of
`datos/world.zip` does not carry them.
