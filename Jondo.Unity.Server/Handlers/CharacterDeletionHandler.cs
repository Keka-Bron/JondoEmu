using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Deleting a character, and making the client believe it.
    /// </summary>
    /// <remarks>
    /// Measured end to end in "crear personaje - borrar personaje.pcapng", which records one
    /// deletion. The client asks three times and the server answers in three groups:
    ///
    /// <code>
    ///   C-&gt;S  kwa  10928cb8bf8802                       f2: the character id
    ///   C-&gt;S  kvu  08&lt;id&gt; 1220 "9cdab917cc3d5a4a33..."  f1: the id, f2: 32 hex characters
    ///   C-&gt;S  kvh  (empty)
    ///
    ///   S-&gt;C  kvn  0a06 "Gone-1"                        f1: the name of what is gone
    ///   S-&gt;C  kqp kqp kqp  kvi  jtg                     the list again
    ///   S-&gt;C  kvm  (empty)
    /// </code>
    ///
    /// Three requests, three answers, and getting that split wrong is visible: with all three
    /// replies hung off the kvu, clicking the bin did nothing at all, because the confirmation
    /// popup is waiting for the kvn that answers the kwa.
    ///
    /// <code>
    ///   kwa  -&gt;  kvn                    the name, and the popup opens
    ///   kvu  -&gt;  kqp kqp kqp  kvi  jtg  deleted, and here is the list again
    ///   kvh  -&gt;  kvm                    creation eligibility after the popup closes
    /// </code>
    ///
    /// The list goes back framed rather than as a bare kvi, for the same reason it does after a
    /// creation: the client replaces what it holds only when the whole set arrives. Sending the
    /// kvi alone after a creation is what once put the PREVIOUS character into the world.
    ///
    /// About those 32 hex characters. In the game a deletion is confirmed by typing something --
    /// the character's name, or the secret answer on the account -- and the client sends it hashed.
    /// This server stores no secret, so there is nothing to compare it against and it is not
    /// checked. What protects a character is that the id has to belong to the account on this
    /// session, which is enforced inside the transaction in
    /// <see cref="DatabaseManager.DeleteCharacter"/> rather than here. That is a real difference
    /// from the real server and it is written down: someone who can already log into the account
    /// can delete its characters without knowing the answer.
    /// </remarks>
    public static class CharacterDeletionHandler
    {
        /// <summary>
        /// The kwa: the player clicked the bin. Answers with the name, which opens the popup.
        /// </summary>
        /// <remarks>
        /// Nothing is deleted here and nothing is remembered either. The kvu that follows carries
        /// the id again, so there is no half-finished deletion held between the two, and no state
        /// for another socket to walk into.
        /// </remarks>
        public static async Task<bool> AskAsync(GameSession session, PrepareCharacterDeletionRequest request)
        {
            long characterId = request.CharacterId;
            long accountId = session.AccountId;
            if (characterId <= 0 || accountId <= 0)
            {
                Console.WriteLine($"[Characters] Deletion request without an id or without an account " +
                                  $"(character {characterId}, account {accountId}).");
                return false;
            }

            // Straight off the list of the account itself, so a character that is not on it has no
            // name to give back and the popup never opens.
            var owned = DatabaseManager.GetCharactersByAccountId(accountId, session.ServerId);
            var target = owned.FirstOrDefault(character => character.Id == characterId);

            if (target == null)
            {
                Console.WriteLine($"[Characters] Character {characterId} does not belong to account {accountId}.");
                return false;
            }

            await session.SendAsync(new CharacterDeletionPrepareEvent(target.Name, SecretQuestion: "", NeedSecretAnswer: false));

            Console.WriteLine($"[Characters] Deletion confirmation for {target.Name} ({characterId}).");
            return true;
        }

        /// <summary>The client is done with the popup. Send the existing successful creation-eligibility result.</summary>
        public static Task CloseAsync(GameSession session)
            => session.SendAsync(new CanCharacterBeCreatedResultEvent(CharacterCreationResult.CreateOk));

        /// <summary>
        /// The kvu: confirmed. Deletes, then sends the list as it now stands.
        /// </summary>
        public static async Task<bool> DeleteAsync(GameSession session, CharacterDeletionRequest request)
        {
            using var context = SessionContext.Push(session);
            long accountId = session.AccountId;
            int serverId = session.ServerId;
            long characterId = request.CharacterId;

            if (characterId <= 0)
            {
                Console.WriteLine("[Characters] The deletion carried no character id.");
                return false;
            }

            // Same guard as the selection, and for the same reason: the id comes off the wire. A
            // socket that has not presented its ticket arrives here with account zero, and zero
            // must delete nothing rather than fall through to a lookup that ignores the account.
            if (accountId <= 0)
            {
                Console.WriteLine($"[Characters] Deletion without a resolved account (character " +
                                  $"{characterId}). The ticket has not been presented.");
                return false;
            }

            // Out of his alliance and his guild first, as if he left them -- a leader's rank to the
            // next in line, an alliance gone with its last member --: the deletion keeps their rows,
            // and he stayed a member nobody could see. Before it, while his name and his account
            // are still there for the guild's log and its houses; and only for one of this account.
            if (DatabaseManager.CharacterBelongsToAccount(characterId, accountId))
            {
                string leaving = DatabaseManager.GetCharacterById(characterId)?.Name ?? "";
                await AllianceHandler.CharacterGoneAsync(characterId, leaving);
                await GuildHandler.CharacterGoneAsync(characterId);
            }

            string name = DatabaseManager.DeleteCharacter(characterId, accountId);
            if (name.Length > 0) PlayerPolicies.CharacterDeleted(characterId);
            if (name.Length == 0)
            {
                Console.WriteLine($"[Characters] Character {characterId} was not deleted.");
                return false;
            }

            var characters = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
            await CharacterListService.Live.SendAsync(session, characters);

            Console.WriteLine($"[Characters] Deleted {name} ({characterId}). " +
                              $"{characters.Count} left on server {serverId}.");
            return true;
        }
    }
}
