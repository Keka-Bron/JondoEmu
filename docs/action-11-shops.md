# NPC shops using action 11

The 18 November 2026 Fandom review (`jondo-wiki-action11-v2.json`) covered 40 NPCs. The 30 documentary catalogues in [shops.json](../content/npcs/shops.json) have 675 imported offers. Each catalogue records its Fandom NPC page as `source`. A positive `currency` identifies the token's item template. Zero explicitly selects kamas. Prices in these catalogues are per seller and per item.

NPC IDs and action 11, item and currency IDs, and the native binding effects were checked against the branch's `datos/world.zip`. None of the 30 NPCs had a catalogue in the measured, authored or legacy token shops. Currencies, prices, and source order come from the report. Placements and dialogues retain their existing data. Fandom is documentary evidence for the offer and its criteria, with client data used to resolve IDs.

| Currency | Item ID | Catalogues | Offers |
|---|---:|---:|---:|
| Wantoken | 13052 | 9 | 243 |
| Kamas | 0 | 5 | 12 |
| Almoken | 13330 | 1 | 70 |
| Ice Kama | 11196 | 1 | 33 |
| Nugget | 14635 | 3 | 80 |
| Kamoken | 32387 | 1 | 9 |
| Orichor | 15206 | 5 | 52 |
| Pearl of the Depths | 17863 | 1 | 9 |
| D'vine Tingy | 26264 | 1 | 16 |
| Expedition Badge | 27504 | 1 | 18 |
| Loyaloken | 30529 | 1 | 126 |
| Recompensoken | 30853 | 1 | 7 |

## Criteria and bindings

- Quest, item, title and alteration conditions use the existing player criteria at catalogue opening and purchase. The two Water Clock Sand alterations (893 and 896) have the same name, icon and description in the client data. Ess Jeesy's sand requires both variants to be absent, as well as the item itself. The Icebreaker certificate keeps the source's alternative between absence of the title and absence of the certificate.
- Harvey Terran's 77 offers use the days calculated from the stored account subscription by the veteran reward system. For example, the ceremonial Groom Hat is item 15777 and requires 1,350 full days. This uses Jondo's subscription model, based on the recorded subscription date and its one-year term. The shop thresholds are independent of the veteran rewards sent as gifts.
- Jykel's Klonebawks STG-4 through STG-18 are the 15 separate templates 26265–26279. Pauly Gees's seven Dragobons keep their template effects 2892, including their existing alteration IDs and handling on use.
- The imported offers with binding have a native marker in the item template. Nine use 981 (character link), 114 use 983 with no duration (indefinite account link), and 133 use 983 with a 180-day duration. Purchases keep the native effect and stamp the dated variant at purchase. An indefinite account link is sent as the protocol's account marker 982. The expiry is stored with the inventory effects and used by server-side transfers. Character-bound items stay out of the account bank. Both types of bound item stay out of player trades, markets, NPC buyback and shared storage while the restriction applies.

## Excluded rows

- [Pink Tada (3253)](https://dofuswiki.fandom.com/wiki/Pink_Tada) — Amber of the Depths has no resolved item template.
- [Loya Loken (7529)](https://dofuswiki.fandom.com/wiki/Loya_Loken) — Cursed One Costume, Cursed One Mane, Cursed One Shield, Cursed One Veil, Mongrelamus's Curse, “The Blade”, Goultard the Cursed Ceremonial Set and Mongrosity have no resolved item template.
- The report had no verified catalogue for 1230, 1367, 1378, 3816, 5643, 5678, 6363, 6405, 6442 and 6851. Their data is left as it was.

## In-game check

1. Open Tokageko (6831) on map 191105026. Check that all 18 offers show Expedition Badges (27504). The price distribution is 13 at 10 badges, two at 15, and one each at 20, 30 and 60.
2. Try buying with fewer badges than required, then with enough badges. Confirm the correct deduction, one item received and the same kamas balance. Reconnect and check the same exchange date, 180 days after purchase. Try to list or trade the item while the link is active.
3. At Niki Minawa, confirm that Mam Pla Sauce costs 2,000 kamas and appears after completing quest 2205. At Harvey Terran, check an offer at a subscription-day boundary.
