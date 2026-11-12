using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The test harness's routes for playing quests out of a fight: where the character stands
    /// and what is around him (the NPCs, their quest marks, the interactives), a conversation
    /// walked reply by reply as the client walks it (iov, ioy, kla), an interactive used (iwo),
    /// the quests he holds; and, to set a quest's scene, a teleport, an item handed over and a
    /// quest started, finished or forgotten.
    /// </summary>
    /// <remarks>
    /// Every action goes through the handler the client's packet goes to, inside the character's
    /// session: what is judged is what a player gets. The texts come back in Spanish, from the
    /// client's own keys -- an NPC says what its template's message and reply keys say, in every
    /// language the client carries.
    /// </remarks>
    public static partial class ControlApi
    {
        /// <summary>A translation key's Spanish text, empty when there is none.</summary>
        private static string TextOf(long key)
        {
            if (key <= 0) return "";
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
                command.Parameters.AddWithValue("$key", key.ToString(CultureInfo.InvariantCulture));
                return command.ExecuteScalar() as string ?? "";
            }
            catch { return ""; }
        }

        /// <summary>The conversation as it stands: the NPC, the question put and the replies offered, with their texts.</summary>
        private static object DialogueNow(GameSession session, List<string>? lines = null)
        {
            var state = session.State;
            var template = state.OpenDialogueNpcId != 0 ? Npcs.TemplateOf(state.OpenDialogueNpcId) : null;
            string Message(long id)
            {
                if (template == null) return "";
                int at = Array.IndexOf(template.Messages, id);
                return at >= 0 && at < template.MessageTexts.Length ? TextOf(template.MessageTexts[at]) : "";
            }
            string Reply(long id)
            {
                if (template == null) return "";
                int at = Array.IndexOf(template.Replies, id);
                return at >= 0 && at < template.ReplyTexts.Length ? TextOf(template.ReplyTexts[at]) : "";
            }
            bool open = state.OpenDialogueNpcId != 0;
            return new
            {
                open,
                npc = state.OpenDialogueNpcId,
                question = open ? state.LastNpcQuestion : 0,
                text = open ? Message(state.LastNpcQuestion) : "",
                replies = open ? state.LastNpcReplies.Select(r => new { id = r, text = Reply(r) }).ToList() : null,
                lines = lines?.Where(l => l.Contains("[Quest") || l.Contains("[NPC") || l.Contains("[Dialog") || l.Contains("quest"))
                              .Select(l => l.Length > 220 ? l[..220] : l).ToList(),
            };
        }

        /// <summary>A quest's run as the log holds it: its step, and each objective of the step, done or not.</summary>
        private static object RunOf(World.Quests.QuestLog log, World.Quests.QuestRun run)
        {
            var book = Quests.Book;
            var step = book?.Step(run.StepId);
            return new
            {
                quest = run.QuestId,
                finished = run.Finished,
                step = run.StepId,
                objectives = step?.Objectives.Select(o => new
                {
                    id = o.Id, type = o.TypeId, done = run.Done.Contains(o.Id),
                    counted = run.Counted.TryGetValue(o.Id, out int n) ? n : 0,
                    npc = o.NpcId, map = o.MapId, parameters = o.Parameters,
                }).ToList(),
            };
        }

        /// <summary>
        /// Where the character stands and what is around him: the map's NPCs (template, actor id,
        /// cell, the quests each offers or is on right now), the players, and the interactives with
        /// the skills they answer.
        /// </summary>
        private static Respuesta TestWorld()
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            var state = session.State;
            long map = state.MapId;
            object? npcs = null, interactives = null;
            RunInSession(session, _ =>
            {
                // What the character sees: a placement whose quest terms do not hold is not there for him.
                npcs = Npcs.OnMap(map).Select(n => new
                {
                    npc = n.NpcId, actor = n.ContextualId, cell = n.Cell, orientation = n.Orientation,
                    offers = Quests.OfferedRightNowBy(n.NpcId, map),
                    onQuests = Quests.InProgressWith(n.NpcId, map),
                }).ToList();
                interactives = InteractiveRegistry.OnMap(map).Select(i => new
                {
                    element = i.Element.Id, cell = i.Element.Cell, gfx = i.Element.Gfx, type = i.Type,
                    skills = i.Actions.Select(a => new { skill = a.SkillId, instance = a.SkillInstanceId, kind = a.Kind.ToString() }).ToList(),
                }).ToList();
                return System.Threading.Tasks.Task.CompletedTask;
            });
            return Bien(new
            {
                map, cell = state.CellId, subArea = MapManager.GetMapInfo(map)?.SubAreaId ?? 0,
                level = state.CharacterLevel,
                npcs, interactives,
            });
        }

        /// <summary>{ npc | actor, action? }: an NPC clicked (iov, action 3 by default: talk), and the conversation it opens.</summary>
        private static Respuesta TestTalk(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            long map = session.State.MapId;
            long actor = Numero(cuerpo, "actor");
            int npcId = (int)Numero(cuerpo, "npc");
            if (actor == 0)
            {
                var spawn = Npcs.Of(map).FirstOrDefault(n => n.NpcId == npcId);
                if (spawn == null) return Mal(404, $"npc {npcId} is not on map {map}");
                actor = spawn.ContextualId;
            }
            long action = Numero(cuerpo, "action");
            if (action == 0) action = Npcs.Talk;
            var lines = RunInSession(session, stream => NpcHandler.InteractAsync(stream,
                ClientFrame(Op.Iov, Pb.New().Var(1, action).Var(2, map).Var(3, actor).Build())));
            return Bien(DialogueNow(session, lines));
        }

        /// <summary>{ reply }: a reply chosen (ioy), and where the conversation went.</summary>
        private static Respuesta TestReply(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            long reply = Numero(cuerpo, "reply");
            var lines = RunInSession(session, stream => NpcHandler.ReplyAsync(stream,
                ClientFrame(Op.Ioy, Pb.New().Var(1, reply).Build())));
            return Bien(DialogueNow(session, lines));
        }

        /// <summary>The conversation's X (kla).</summary>
        private static Respuesta TestCloseDialogue()
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            // The X closes whatever is open, as the client's kla does (GameNodeProxy): a workshop too,
            // whose closing is what marks a quest's «Fabricar … y cerrar la interfaz».
            var lines = RunInSession(session, stream => WorkshopHandler.IsOpen
                ? WorkshopHandler.CloseAsync(stream)
                : NpcHandler.CloseAsync(stream));
            return Bien(DialogueNow(session, lines));
        }

        /// <summary>
        /// { item }: an item of the bag used as its double click uses it (iuu with its uid), found by
        /// template: a quest item whose use is an objective, the sacred urn of «Leyenda del otoño».
        /// </summary>
        private static Respuesta TestUseItem(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            int template = (int)Numero(cuerpo, "item");
            long uid = 0;
            var lines = RunInSession(session, async stream =>
            {
                uid = Managers.Equipment.All.FirstOrDefault(i => i.Template == template && i.Quantity > 0)?.Uid ?? 0;
                if (uid != 0)
                    await ItemUseHandler.UseAsync(stream, ClientFrame(Op.Iuu, Pb.New().Var(3, uid).Build()));
            });
            if (uid == 0) return Mal(404, $"item {template} not carried");
            return Bien(new { item = template, uid, lines = lines.Select(l => l.Length > 220 ? l[..220] : l).ToList() });
        }

        /// <summary>
        /// { item }: how many of the template the slot's character carries, whether one is worn, and
        /// the uid of the last placed -- what a play script checks between two steps, from the
        /// session's own inventory.
        /// </summary>
        private static Respuesta TestCarried(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            int template = (int)Numero(cuerpo, "item");
            int count = 0, worn = 0;
            long uid = 0;
            int[] positions = Array.Empty<int>();
            RunInSession(session, _ =>
            {
                var mine = Managers.Equipment.All.Where(i => i.Template == template && i.Quantity > 0).ToList();
                count = mine.Sum(i => i.Quantity);
                worn = Managers.Equipment.HowManyWorn(template);
                uid = mine.OrderByDescending(i => i.Position).FirstOrDefault()?.Uid ?? 0;
                positions = mine.Select(i => i.Position).ToArray();
                return Task.CompletedTask;
            });
            return Bien(new { item = template, count, worn, uid, positions });
        }

        /// <summary>
        /// { text }: one of the administrator's chat commands as the character typed it -- «.oficio 26
        /// 80» puts the alchemist at level 80 for a quest that asks for it (Des fleurs épineuses).
        /// </summary>
        private static Respuesta TestCommand(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            string text = Texto(cuerpo, "text");
            bool handled = false;
            var lines = RunInSession(session, async stream =>
            {
                handled = await CommandHandler.TryHandleAsync(stream, text, 0, session.AccountId);
            });
            return Bien(new { handled, lines = lines.Select(l => l.Length > 220 ? l[..220] : l).ToList() });
        }

        /// <summary>{ element, skill }: an interactive used (iwo), with the skill instance the map gives it when none is named.</summary>
        private static Respuesta TestUse(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            long map = session.State.MapId;
            int element = (int)Numero(cuerpo, "element");
            int skill = (int)Numero(cuerpo, "skill");
            if (skill == 0)
                skill = InteractiveRegistry.OnMap(map).FirstOrDefault(i => i.Element.Id == element)?.Actions.FirstOrDefault()?.SkillInstanceId ?? 0;
            // A quest's element travels in the character's own jss, not in the registry
            // (ConnectionProtocol.AddQuestElements), with the instance every element has.
            if (skill == 0) skill = Interactives.SkillInstanceOf(element);
            var lines = RunInSession(session, stream => InteractiveActionHandler.UseAsync(stream,
                ClientFrame(Op.Iwo, Pb.New().Var(1, skill).Var(2, element).Build())));
            return Bien(new { element, skill, dialogue = DialogueNow(session), lines = lines.Select(l => l.Length > 220 ? l[..220] : l).ToList() });
        }

        /// <summary>{ map }: the character taken there as a teleport takes him, out of a fight.</summary>
        private static Respuesta TestGoto(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            long map = Numero(cuerpo, "map");
            if (MapManager.GetMapInfo(map) == null) return Mal(404, $"map {map}");
            if (FightHandler.FightOf(session.State.CharacterId) != null) return Mal(409, "in a fight");
            int landed = -1;
            int cell = cuerpo.Contains("\"cell\"") ? (int)Numero(cuerpo, "cell") : -1;
            var lines = RunInSession(session, async stream => landed = cell >= 0
                ? await TeleportHandler.ToMapAsync(stream, map, cell)
                : await TeleportHandler.ToMapAsync(stream, map));
            return Bien(new { map = session.State.MapId, cell = session.State.CellId, landed, lines = lines.Where(l => l.Contains("[Quest")).ToList() });
        }

        /// <summary>
        /// { item, quantity }: an item handed to the character as the administrator hands one; a
        /// negative quantity takes it back instead, a follower out of his slot too -- what a quest
        /// closed by hand leaves behind.
        /// </summary>
        private static Respuesta TestGive(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            int item = (int)Numero(cuerpo, "item");
            int asked = (int)Numero(cuerpo, "quantity");
            if (asked < 0)
            {
                bool taken = false;
                RunInSession(session, async stream => taken = await Managers.QuestFollowers.TakeAsync(stream, item, -asked));
                return taken ? Bien(new { item, quantity = asked }) : Mal(422, "not carried");
            }
            int quantity = Math.Max(1, asked);
            if (!DatabaseManager.TryGetItemTemplateEffects(item, out _)) return Mal(404, $"item {item}");
            bool given = false;
            RunInSession(session, async stream => given = await CommandHandler.GrantItemAsync(stream, item, quantity) != null);
            return given ? Bien(new { item, quantity }) : Mal(422, "not given");
        }

        /// <summary>
        /// The authored content of the quests read again with the server running: the NPCs'
        /// placements and conversations, the objectives' bindings and criteria, the fights replies
        /// start, the gathering and fight drops, the hand-written loot groups, the readable signs and the passages. The
        /// clients stay connected; what a map
        /// shows changes on entering it again.
        /// </summary>
        private static Respuesta TestReload()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Managers.NpcDialogues.Reload();
            Managers.Npcs.Initialize();
            Managers.DungeonDoor.Initialize();
            Managers.Bankers.Initialize();
            Managers.Quests.ReloadContent();
            Managers.NpcFights.Forget();
            Handlers.NpcBarterHandler.Forget();
            Managers.MapStagings.Forget();
            Managers.PlateGates.Forget();
            Managers.MeasuredArrivals.Forget();
            Managers.HarvestDrops.Forget();
            Managers.QuestFightDrops.Forget();
            Managers.LootBoxes.Forget();
            Managers.DungeonExitRooms.Forget();
            Managers.Readables.Forget();
            Managers.TeleportManager.Initialize();
            // The passages are declared to the client from here: a door written since the start was
            // reloaded above and still answered «Unknown use» until this was built again.
            Managers.InteractiveRegistry.Initialize();
            Console.WriteLine($"[Content] Reloaded in {watch.ElapsedMilliseconds} ms.");
            return Bien(new { reloaded = true, ms = watch.ElapsedMilliseconds });
        }

        /// <summary>The character's quests: those in hand with their step and objectives, and those finished.</summary>
        private static Respuesta TestQuests()
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            var log = session.State.Quests;
            if (log == null) return Mal(409, "no quest log");
            return Bien(new
            {
                doing = log.Runs.Values.Where(r => !r.Finished).Select(r => RunOf(log, r)).ToList(),
                finished = log.Runs.Values.Where(r => r.Finished).Select(r => r.QuestId).OrderBy(q => q).ToList(),
            });
        }

        /// <summary>
        /// { quest, op }: a quest's scene set -- "start" it as an NPC hands it over, "finish" it
        /// whole (a quest another one asks for), "forget" it to play it again.
        /// </summary>
        private static Respuesta TestQuest(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            int quest = (int)Numero(cuerpo, "quest");
            string op = Texto(cuerpo, "op");
            var log = session.State.Quests;
            if (log == null || Quests.Book?.Of(quest) == null) return Mal(404, $"quest {quest}");
            var lines = new List<string>();
            switch (op)
            {
                case "start":
                    lines = RunInSession(session, stream => Quests.StartAsync(stream, quest));
                    break;
                case "finish":
                    lines = RunInSession(session, async stream =>
                    {
                        if (!log.Active(quest) && !log.Finished(quest)) await Quests.StartAsync(stream, quest);
                        for (int guard = 0; guard < 40 && log.Active(quest); guard++) log.FinishStep(quest);
                        Quests.SaveAsItStands(quest);
                        await Quests.SendJournalAsync(stream);
                    });
                    break;
                case "forget":
                    lines = RunInSession(session, async stream =>
                    {
                        Quests.Forget(quest);
                        await Quests.SendJournalAsync(stream);
                    });
                    break;
                default:
                    return Mal(400, "op: start, finish or forget");
            }
            var run = log.Run(quest);
            return Bien(new { quest, op, run = run == null ? null : RunOf(log, run), lines = lines.Where(l => l.Contains("[Quest")).ToList() });
        }
    }
}
