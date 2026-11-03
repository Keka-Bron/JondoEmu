# The cities' war and the head hunters

Bonta against Brakmar in the field: aggressions, honour and its ten grades, dishonour, and the
*Cazacabezas* — hunt orders on a player of the other city, paid in chapeuvepés.

There are no captures of any of it. The rules come from the client itself (its texts, its alignment
tab, its end-of-fight screen, the map flags), from Ankama's devlogs on the alignment rework, and from
how the old Dofus 1.29 ran its *traques* and its pévétons. Where a number is this server's own
choice, it says so.

---

## Who may attack whom

The client offers *Attack* (`hph` without its `f2`) on a player when its own rule holds
(`PlayerMenuMaker.AllowAggression`), and the server checks the same rule (`Alignments.MayAggress`):

- the attacker has his PvP on;
- the target is sworn to the other city and shows a grade, which only PvP on gives;
- the target is no more than fifty levels above the attacker, both counted up to 200.

And the map has to allow it: bit 2 of the map's flags (`m_flags`), which 8,296 of the 15,360 maps
carry — none of Astrub, its fields or the cities' streets. Elsewhere the client hears
*requirements not met*.

An aggression inside a running conquest belongs to the conquest (`ConquestHandler`) and gives no
honour.

## Honour, grades, dishonour

`Managers/Honour.cs` settles a finished aggression. The numbers are this server's own, chosen to keep
the 1.29 spirit: fights between equals pay, crushing the weak shames.

| | |
|:---|:---|
| Grades | ten, from honour floors 0, 500, 1,500, 3,000, 5,000, 7,500, 10,000, 12,500, 15,000, 17,500; at most 20,000 honour |
| Base win | 100 honour to each winner |
| Strength | × the losers' levels over the winners' levels (each up to 200), at most × 2 |
| Numbers | × losers over winners, between × 0.5 and × 2 |
| Grade gap | × (1 + 0.1 per grade the loser has over the winner), between × 0.5 and × 2 |
| Clamp | 10 to 300 per winner |
| Losers | lose six tenths of the winners' average gain |
| Same loser within an hour | nothing, and the client's own *no honour lately* line |
| Humiliation | attackers who beat someone under half their level get 1 to 3 dishonour and no honour |
| Dishonour | each even win washes one point away instead of paying honour; while any is left, no hunt orders |

The end screen's PvP block (`lmc` inside `jyg`, the old `FightResultPvpData` order: grade, floor,
ceiling, honour, delta) shows it on the grade's bar, and the client's info messages say it in words:
74 honour gained, 76 honour lost, 75 and 77 dishonour, 81 lost in defeat, 84 humiliation, 85
dishonour washed, 155 not this time. The sheet's honour block is
`{ f1 honour, f2 status, f3 ceiling, f4 floor }`; a new grade redraws the wings.

## The Cazacabezas

`Managers/HeadHunts.cs` holds the rules, `Network/ControlApiHeadHunts.cs` answers the mod, and the mod
(`Cazacabezas/`) only shows and asks.

**Taking an order.** From level 50, sworn, PvP on and no dishonour. The board lists the enemies within
reach: the other city, PvP on, level 30 or more, from 20 levels below the hunter to 50 above, seen
in the last two weeks; the online ones first. Three hunters at most per prey, one order per hunter,
three days per order. Giving one up costs ten minutes; failing one costs an hour.

**The hunt.** It opens when hunter and prey are both in the world, for 15 minutes; when it closes the
prey rests 30 minutes before it can open again (both minutes are server settings). While it is open:

- the prey cannot fold his wings — the client's own *cannot hide your alignment in an aggression
  zone* (317) — and is told so;
- the hunter is told where the prey was seen, at once and every 45 seconds, with a map link;
- the mod draws a strip on top of the screen: the prey and the time left for the hunter, a warning for
  the prey;
- it closes early if the prey leaves the world.

**The end.** An aggression between the two while the hunt is open, whoever attacked:

- the hunter wins — the bounty in chapeuvepés and 50 honour (message 80);
- the prey wins — one chapeuvepé and 50 honour (message 180), and the hunter is locked out for an
  hour.

**The bounty** is `2 + (grade + 1) / 2 + min(streak / 2, 5) + 1 if the prey is not below the hunter`:
3 to 13. The streak is the prey's aggressions won in a row.

**Chapeuvepés** are kept per character and spent with the two Sacres, in the militias: Tomás Sacre
in Bonta's, Ema Sacre in Brakmar's. Their prices follow the 1.29 pévéton merchant — characteristic
scrolls 2, 3, 5 or 16 by size, Alicha 1 or 5, and the gems.

## The mod

`Cazacabezas/` is a MelonLoader mod built like JondoFix's windows, out of the client's own pieces
(`WindowFigma`, `DofusTabGroup`, `DofusLabel`). It opens with **F7** or the button it puts beside the
honour bar of the alignment tab (`pb_honour`), and asks the control API on `8888` with the account's
own session, which the launcher passes as `JONDO_PLAYER_TOKEN` to every client:

| Route | Answers |
|:---|:---|
| `POST /api/headhunts/status` | the character's record, his order and whether he is hunted |
| `POST /api/headhunts/board` | the prey he may choose |
| `POST /api/headhunts/take` `{ target }` | `ok`, or why not: `off`, `neutral`, `pvp`, `level`, `dishonour`, `locked`, `busy`, `target`, `full` |
| `POST /api/headhunts/abandon` | gives his order up |

All four need the character in the world (`409 world` otherwise). The launcher copies the mod into
the client's `Mods/` with JondoFix.

## The JondoBots at war

With *Simulate players* on, three bots of every five swear to a city — Bonta the even numbers,
Brakmar the odd. From level 20 they wear their wings (with the sheet's switch, `hnu`) and come in on
the front: the 40 maps of the war ground nearest to Astrub's start, the war ground being the area
next door with the most maps that allow an aggression (the Cania Plains). There they aggress an enemy of their
size — three fifths of their level at least, so they never earn dishonour — and nobody twice within
five minutes, walk towards the nearest enemy within eight maps, and from level 50 take hunt orders
through the same routes the mod uses and chase their prey map by map.
