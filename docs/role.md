# Account roles

Jondo now uses the complete Giny-compatible role scale from **1 to 5**. The numeric role is stored
in `Accounts.Role`, and higher roles inherit the permissions of every lower role.

| Value | Constant | Meaning |
|---:|---|---|
| 1 | `Roles.Jugador` | Normal player; also the default for a newly created account. |
| 2 | `Roles.Moderador` | Moderator; chat moderation and world movement for player support. |
| 3 | `Roles.GameMasterPadawan` | Game Master Padawan; first support and event tools. |
| 4 | `Roles.GameMaster` | Game Master; character-affecting tools such as level, kamas or appearance. |
| 5 | `Roles.Administrador` | Administrator; server administration and commands reserved to the highest role. |

Authorization is cumulative and is checked as `accountRole >= requiredRole`. The authoritative
check always runs on the server. Hiding or showing an administration control in the launcher or
client is only a convenience and is not a security boundary.

## Why the former 1-to-4 scale was wrong

The former Jondo definition omitted `GameMasterPadawan`. It therefore used:

```text
1 Player, 2 Moderator, 3 Game Master, 4 Administrator
```

Giny and the Dofus account-right criteria use five distinct levels:

```text
1 Player, 2 Moderator, 3 Game Master Padawan, 4 Game Master, 5 Administrator
```

Stopping at 4 did more than rename a value. It made Jondo's role 4 mean administrator while the
same numeric value in Giny-aware data or conditions means Game Master. Conditions that correctly
required role 5 could consequently reject a Jondo administrator, while code treating role 4 as an
administrator could grant administrative access to a real Game Master. The missing intermediate
role also made direct comparisons with Giny configuration unreliable.

## Correction and database migration

`Roles.cs` now defines all five values. Game Master moved from 3 to 4, Administrator moved from 4
to 5, and role 3 is reserved for Game Master Padawan.

Existing databases require special handling because their old role 4 rows represented
administrators. At server startup, migration `roles-giny-1-to-5` changes those existing rows from
4 to `Roles.Administrador` (5) exactly once and records completion in `JondoMigrations`.

The one-time marker is essential. Repeating `UPDATE Accounts SET Role = 5 WHERE Role = 4` at every
startup would incorrectly promote every Game Master created after the correction. Once the
migration has run, role 4 remains available for its correct meaning.

Configured server-owner accounts are also promoted through the `Roles.Administrador` constant,
not a literal number. This keeps that rule synchronized with the central role definition.

## Current command thresholds

The chat-command permission table currently assigns:

- role 2: `.teleport`;
- role 4: `.kamas`, `.level`, `.size`, `.shop`;
- role 5: `.relative`, `.item`, `.itemset`.

`.relative` explicitly requires Administrator. `.item` and `.itemset` use the handler's secure
default: every registered command absent from the permission table requires Administrator.

The control API also uses these shared constants, and database role updates are clamped to the
valid range 1 through 5.

## Implementation references

- Role values, names and cumulative comparison: `Jondo.Unity.Contract/Roles.cs`
- One-time migration and account-role persistence: `Jondo.Unity.Server/DatabaseManager.cs`
- Chat-command requirements: `Jondo.Unity.Server/Handlers/CommandHandler.cs`
- Server control API requirements: `Jondo.Unity.Server/Network/ControlApi.cs`
- Launcher-side administrator display check: `Jondo.Unity.Launcher/LauncherService.cs`
