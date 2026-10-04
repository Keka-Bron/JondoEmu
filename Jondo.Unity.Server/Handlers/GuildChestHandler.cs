using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The guild chest: opening it, changing tab, and whether a move is allowed. The moves
    /// themselves are any storage's: see <see cref="StorageHandler"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on "Interactivos varios/entrar en banco bonta-abrir cofre gremio-usarlo-abrir cofre
    /// personal del banco.pcapng", frame numbers being positions in <c>hilo.tramas</c>:
    ///
    ///   26  C iwo { f1: 28538, f2: 524415 }          the chest, skill instance and element
    ///   27  S iwn { f1: 1, f2: 524415, f4: 184, f5: character }
    ///   28  S ivl { the tabs }
    ///   29  S kbk { f1: 22, f2: 1, f3: 100 }         the window, on tab 1
    ///   30  S iwb {}                                  tab 1, empty
    ///   31  S jlo { f1: "Sacri-Master" }             who has it open
    ///   32  S jlq { f1: "Sacri-Master" }
    ///   38  C jll { f2: 2 }                           tab 2, please
    ///   39  S lqn { f1: 1, f2: 654 }                  "No tienes el derecho de consultar el cofre de gremio."
    ///   48  C kcr { f1: 1, f2: 534451715 }  → itd, ium, iun
    ///   61  C kcr { f1: -1, f2: 537272712 } → iua, itc, iun
    ///   68  C kla → 69 S khd { f3: 11 }
    ///
    /// The hpp and hpq of frames 33-34 are left out: they come after using a home potion too
    /// ("Movimiento/utilizar pocima de hogar"), so they belong to something else.
    /// </para>
    /// <para>
    /// Inferred, and said where it is done: the refusals other than 654 (put in, take out, no
    /// guild, that type of item -- the client's own sentences 655, 656, 659, 661), what a granted
    /// tab change answers (the tab's content), and who else is told the list of viewers.
    /// </para>
    /// </remarks>
    public static class GuildChestHandler
    {
        /// <summary>The chest was used: open it on the first tab, if the character may look at it.</summary>
        public static async Task OpenAsync(NetworkStream? stream, int elementId, int skillId)
        {
            long character = SessionContext.State.CharacterId;
            await StorageHandler.SendAsync(stream, Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, character));

            var guild = GuildStore.GuildOf(character);
            if (guild == null)
            {
                await RefuseAsync(stream, GuildChests.NoGuild);
                return;
            }

            var tab = GuildChests.TabOf(guild.Id, 1)!;
            if (!GuildChests.RightsOf(character).Contains(tab.ConsultRight))
            {
                await RefuseAsync(stream, GuildChests.NoRightToLook);
                return;
            }

            var window = new StorageHandler.Window
            {
                Kind = StorageHandler.Kind.GuildChest,
                Place = StorageStacks.GuildChest(guild.Id, tab.Index),
                MapId = SessionContext.State.MapId,
                ElementId = elementId,
                GuildId = guild.Id,
                Tab = tab.Index,
            };

            await StorageHandler.SendAsync(stream, Op.Ivl, StorageProtocol.BuildGuildChestTabs(GuildChests.TabsOfGuild(guild.Id)));
            await StorageHandler.SendAsync(stream, Op.Kbk, StorageProtocol.BuildGuildChestOpened(tab.Index));
            await StorageHandler.OpenAsync(stream, window, null);

            await StorageHandler.SendAsync(stream, Op.Jlo, StorageProtocol.BuildGuildChestViewers(ViewersOf(window)));
            await StorageHandler.SendAsync(stream, Op.Jlq, StorageProtocol.BuildGuildChestViewer(NameOf(SessionContext.Current)));
            await ViewersChangedAsync(window, SessionContext.Current.Id);

            Console.WriteLine($"[Guild chest] {character} opened the chest of guild {guild.Id}, tab {tab.Index}.");
        }

        /// <summary>
        /// jll { f2: tab }: another tab. Refused with 654 without the right to look at it,
        /// frames 38-39; granted, its content (inference). False when no guild chest is open.
        /// </summary>
        public static async Task<bool> SelectTabAsync(NetworkStream? stream, byte[] payload)
        {
            var window = StorageHandler.Current;
            if (window == null || window.Kind != StorageHandler.Kind.GuildChest) return false;

            byte[]? jll = ConnectionProtocol.ReadPayload(payload, Op.Jll);
            if (jll == null) return true;

            int index = 0;
            foreach (var field in ProtoMessage.Parse(jll).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) index = (int)field.VarIntValue;
            }

            var rights = GuildChests.RightsOf(SessionContext.State.CharacterId);
            var tab = GuildChests.Tabs.Count >= index && index >= 1 ? GuildChests.Tabs[index - 1] : null;
            if (tab == null || GuildChests.TabOf(window.GuildId, index) == null || !rights.Contains(tab.ConsultRight))
            {
                await RefuseAsync(stream, GuildChests.NoRightToLook);
                return true;
            }

            window.Tab = tab.Index;
            window.Place = StorageStacks.GuildChest(window.GuildId, tab.Index);
            await StorageHandler.SendAsync(stream, Op.Iwb, ConnectionProtocol.BuildStorageContent(StorageStacks.ItemsOf(window.Place)));
            return true;
        }

        /// <summary>
        /// Whether this move may happen: the right to put in or take out of the tab open, the item
        /// type the tab takes, and still being in that guild. Refused with the client's sentence.
        /// </summary>
        internal static async Task<bool> MayMoveAsync(NetworkStream? stream, StorageHandler.Window window, bool deposit, long uid)
        {
            long character = SessionContext.State.CharacterId;
            var member = GuildStore.MemberOf(character);
            var tab = GuildChests.TabOf(window.GuildId, window.Tab);
            if (member == null || member.GuildId != window.GuildId || tab == null)
            {
                await RefuseAsync(stream, GuildChests.NoGuild);
                return false;
            }

            var rights = GuildChests.RightsOf(character);
            if (!rights.Contains(deposit ? tab.DepositRight : tab.WithdrawRight))
            {
                await RefuseAsync(stream, deposit ? GuildChests.NoRightToPut : GuildChests.NoRightToTake);
                return false;
            }

            if (deposit)
            {
                var item = HavenBagStore.FromInventory(character, uid);
                int type = item == null ? 0 : ItemTypeOf(item.Gid);
                if (type != 0 && !tab.ItemTypes.Contains(type))
                {
                    await RefuseAsync(stream, GuildChests.TypeRefused);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Everybody else looking at the same guild chest gets the list of who is (jlo). Nobody
        /// else was there in the capture, so to whom and when is an inference.
        /// </summary>
        internal static async Task ViewersChangedAsync(StorageHandler.Window window, Guid except)
        {
            var names = ViewersOf(window);
            byte[]? frame = null;
            foreach (var session in SessionRegistry.InWorld())
            {
                if (session.Id == except) continue;
                var theirs = session.State.Storage;
                if (theirs == null || theirs.Kind != StorageHandler.Kind.GuildChest || theirs.GuildId != window.GuildId) continue;
                if (theirs.MapId != session.State.MapId) continue;

                frame ??= ConnectionProtocol.Push(Op.Jlo, StorageProtocol.BuildGuildChestViewers(names));
                try
                {
                    await session.SendAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Guild chest] {session.CharacterId} could not be told who is looking: {ex.Message}");
                }
            }
        }

        /// <summary>The names of the characters who have this guild's chest open, this one included.</summary>
        private static List<string> ViewersOf(StorageHandler.Window window)
        {
            var names = new List<string>();
            foreach (var session in SessionRegistry.InWorld())
            {
                var theirs = session.State.Storage;
                if (theirs == null || theirs.Kind != StorageHandler.Kind.GuildChest || theirs.GuildId != window.GuildId) continue;
                if (theirs.MapId != session.State.MapId) continue;
                names.Add(NameOf(session));
            }

            string mine = NameOf(SessionContext.Current);
            if (SessionContext.State.Storage == window && !names.Contains(mine)) names.Insert(0, mine);
            return names;
        }

        private static string NameOf(GameSession session)
        {
            if (session.State.CharacterName is { Length: > 0 } name) return name;
            return DatabaseManager.GetCharacterById(session.CharacterId)?.Name ?? "";
        }

        private static Task RefuseAsync(NetworkStream? stream, int message)
            => StorageHandler.SendAsync(stream, Op.Lqn,
                ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, message));

        private static int ItemTypeOf(int gid)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Type FROM ItemTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", gid);
                return command.ExecuteScalar() is long type ? (int)type : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
