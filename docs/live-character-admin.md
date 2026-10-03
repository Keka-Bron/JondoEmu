# Live character administration

The control API can change an online character without restarting the server or making the player
reconnect. It can update base characteristics, kamas and level, grant an item, grant and equip a
mount, or teleport the character. It is intended for local administration tools that need the same
immediate feedback as an in-game command.

## Request

Send a `POST` request to `http://127.0.0.1:8888/api/personaje`. The JSON body must carry the token
of an administrator account (role **5**), the online character name and at least one value to
change:

```json
{
  "token": "<administrator launcher token>",
  "personaje": "<online character>",
  "vitalidad": 1000,
  "sabiduria": 500,
  "fuerza": 250,
  "inteligencia": 250,
  "suerte": 250,
  "agilidad": 250,
  "kamas": 50000,
  "nivel": 200,
  "mapa": 88212759,
  "celda": 321,
  "objeto": 1234,
  "cantidad": 3,
  "modo": "aleatorio",
  "montura": 5678
}
```

Every value after `personaje` is optional. `personaje` itself may be left out or empty: the
character is then the caller's own, the one its account has in the world. `modo` goes with `objeto`:
`"max"` (the default) gives the top of every range, `"aleatorio"` or `"random"` rolls each
characteristic in its range the way a craft does; at most 100 rolled items per request. Characteristic values are clamped from zero to
10,000,000 and kamas cannot be negative. Item quantities are limited to 1,000,000. `celda` is
optional but only valid with `mapa`; the nearest walkable cell is used. `montura` must identify a
rideable item template. The response contains the complete resulting character state and the UIDs
of newly granted objects.

The caller is authenticated through the same launcher token and database role check used by the
other control routes. The HAAPI listener is bound to loopback, and the request log redacts the token.
The target character must be connected and outside a fight.

## What changes immediately

The server validates every requested operation first, serializes it with the target session's
normal packet processing, then performs the requested subset:

1. changes and persists base values, kamas and the complete level/experience/capital transition;
2. refreshes characteristics, kamas, pods, known spells and shortcut bars;
3. creates requested items with their template effects and pushes them into the inventory;
4. equips a requested mount through the normal equipment path, including appearance refresh;
5. teleports through the normal map-change path and announces departure to nearby sessions.

No client restart, server restart or direct database edit is required.

To change an account role, use `POST /api/rol` with the same administrator token:

```json
{ "token": "<administrator launcher token>", "cuenta": "login", "rol": 5 }
```

The role is clamped to the supported range documented in `docs/role.md`.

## Who is connected

`POST /api/conectados` with the same administrator token answers the characters in the world:

```json
{ "conectados": [ { "nombre": "Keka", "nivel": 200, "propio": true } ] }
```

`propio` marks the caller's own character.

## The item window in the client

JondoFix opens a window on **F10** for an administrator: the client's own item catalogue with a
search (name, type, id, or a level range such as `190-200`), the lines of the item picked, the
connected characters to give it to, a quantity, and two buttons — maximum characteristics or
rolled ones. It is these two routes and nothing else: the launcher hands the client its account's
token in `JONDO_CONTROL_TOKEN` (administrators only), the mod posts it, and the server checks
token and role on every request. The control API is on loopback, so the window works when the
server runs on the same machine as the client. Implementation: `JondoFix/AdminItemsUi.cs`.

## Errors

| HTTP status | Error | Meaning |
|---|---|---|
| 400 | `sin-cambios` | No supported numeric field was supplied. |
| 400 | `modo-invalido` | `modo` is not `max`, `aleatorio` or `random`. |
| 400 | `modo-sin-objeto` | A rolled mode was asked for with no `objeto`. |
| 400 | `cantidad-excesiva` | More than 100 items that roll, in rolled mode. |
| 400 | `campo-invalido-*` | A supported field was supplied with a non-numeric value. |
| 400 | `objeto-desconocido` | The requested item template does not exist. |
| 400 | `montura-invalida` | The requested template is not a rideable mount. |
| 400 | `mapa-desconocido` | The destination is absent from the world map catalogue. |
| 401 | `sesion` | The launcher token is absent or expired. |
| 403 | `rol` | The token belongs to an account below administrator. |
| 404 | `personaje-desconectado` | No connected character matches the supplied name. |
| 405 | `metodo` | The endpoint was called with a method other than `POST`. |
| 409 | `personaje-en-combate` | Live base-stat changes are blocked during a fight. |

Implementation: `Jondo.Unity.Server/Network/ControlApi.cs`. Online-character lookup and session
serialization come from `SessionRegistry` and `GameSession.UnoCadaVez`.
