# Localized command replies

Administrator chat commands answer in the language selected by the launcher: Spanish, English or
French. The command words stay the same (`.level`, `.item`, `.teleport`, and so on); usage,
validation, permission and result messages are localized.

## How the language reaches a game session

The launcher already sends its two-letter language code when it requests a client launch. Dofus
then presents that language during authentication. The connection server stores the normalized
code (`es`, `en` or `fr`) in the same single-use ticket that carries the account and server ids.

When the second socket redeems the ticket, `GameSession.BindAccount` copies the language to that
session's `SessionState`. It is deliberately per-session: two connected accounts can use different
languages without changing one another or reading desktop preferences on the server machine.

Unknown and empty language codes fall back to Spanish, which preserves the behavior of existing
clients. The former empty-language fallback to French in `ClientLaunchRegistry` is corrected to
Spanish as well.

## Catalogue scope

`Jondo.Unity.Server/Handlers/CommandTexts.cs` contains the three catalogues. It covers all
player-facing replies produced by `CommandHandler`:

- command usage, unknown-command, permission and failure messages;
- kamas, level, spell and size results;
- teleport, relative-map and shop results;
- item and item-set validation and creation;
- unknown-packet summaries and row labels.

Server-console diagnostics are not translated. They are operational logs rather than text shown to
a player, and keeping them stable makes searches and support instructions predictable.

## Adding or changing a command

Add every new player-facing format under the same key in the `Es`, `En` and `Fr` dictionaries, then
call `CommandTexts.Get` through the `T` helper in `CommandHandler`. Keep dynamic values in numbered
placeholders so each language can reorder the sentence.

The startup regression guard loads the catalogue and rejects a build whose languages have different
keys or placeholder sets. This turns a missing translation into an explicit startup failure instead
of silently mixing languages for one command.
