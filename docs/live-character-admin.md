# Live character administration

The control API can change an online character's base characteristics and kamas without restarting
the server or making the player reconnect. It is intended for local administration tools that need
the same immediate feedback as an in-game command.

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
  "kamas": 50000
}
```

Every value after `personaje` is optional. Characteristic values are clamped to the range from zero
to `Int32.MaxValue`; kamas cannot be negative. The response contains the complete set of resulting
base values, not only the fields that changed.

The caller is authenticated through the same launcher token and database role check used by the
other control routes. The HAAPI listener is bound to loopback, and the request log redacts the token.
The target character must be connected and outside a fight.

## What changes immediately

The server serializes the operation with the target session's normal packet processing, then:

1. changes the in-memory base values;
2. saves the character through `DatabaseManager.SaveCurrentCharacter()`;
3. sends refreshed characteristics, kamas and pod capacity to that character.

No client restart, server restart or direct database edit is required.

The endpoint deliberately does not change character level. A level change also has to reconcile
experience, characteristic capital, spell points, known spell grades and the level-up packet; the
existing administrator `.level` command owns that complete transition.

## Errors

| HTTP status | Error | Meaning |
|---|---|---|
| 400 | `sin-cambios` | No supported numeric field was supplied. |
| 401 | `sesion` | The launcher token is absent or expired. |
| 403 | `rol` | The token belongs to an account below administrator. |
| 404 | `personaje-desconectado` | No connected character matches the supplied name. |
| 405 | `metodo` | The endpoint was called with a method other than `POST`. |
| 409 | `personaje-en-combate` | Live base-stat changes are blocked during a fight. |

Implementation: `Jondo.Unity.Server/Network/ControlApi.cs`. Online-character lookup and session
serialization come from `SessionRegistry` and `GameSession.UnoCadaVez`.
