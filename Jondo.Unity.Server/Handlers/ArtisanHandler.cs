using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The artisans' directory: who crafts what, for how much, and where they are.
    /// </summary>
    /// <remarks>
    /// ─── Measured in four captures (Oficios/) ──────────────────────────────────────────────
    ///
    ///   settings  C irl { f1 { f3: job, f4: free, f5: minimum level } }, one per job when the
    ///             jobs window opens and one per change
    ///             S isd { f1 (repeated) { f3: job, f4: free, f5: minimum level } }, every job, every time
    ///   listing   C kef { f2: [job] }   S iro { f1 { f1: job, f2: listed } }. The same request
    ///             lists the farmer in one capture and takes him off in another: a toggle
    ///   the book  C iwo                 S iwn, kfj { f2: [the workshop's jobs] }
    ///             (the farmers' book [28], the magi's the six magus jobs)
    ///   a job     C isr { f2: job }     S isf { f1 (repeated) entry }, then isv as artisans come
    ///             and go and isq { f1: who, f2: job } when one leaves the list
    ///   close     C kla                 S khd { f3: 11 }
    ///
    /// An entry is { f1 { f2: job, f3: minimum level, f4: free, f5: job level }, f2 { f1 { f1: 1 },
    /// f2: name, f3: breed, f4: id, f5: sex, f7 { f1: map } } }. Only connected characters appear,
    /// the one asking included: "verme" in the capture that lists itself and then looks.
    ///
    /// The settings a character never touched are the ones the capture's untouched jobs show:
    /// free, and a minimum level of 1.
    /// </remarks>
    public static class ArtisanHandler
    {
        /// <summary>One job's settings as an artisan.</summary>
        public readonly record struct Setting(int MinLevel, bool Free, bool Listed);

        public static readonly Setting Default = new Setting(1, true, false);

        /// <summary>
        /// The jobs the directory knows: the base one, and every job with a recipe, a resource or a
        /// magus table. That leaves out exactly the two the real isd leaves out, Pergamago (75) and
        /// Bestiólogo (78), whose skills have none of the three -- and keeps the six magi, whose
        /// skills have no recipe but are smithmagic.
        /// </summary>
        public static IReadOnlyList<int> Jobs()
            => JobManager.All
                .Where(j => IsPractised(j.Id, SkillManager.ForJob(j.Id), skill => RecipeManager.ForSkill(skill).Count))
                .Select(j => j.Id).OrderBy(id => id).ToList();

        /// <summary>Whether a job is one that can be practised: base, or a skill that gathers, crafts or mages.</summary>
        internal static bool IsPractised(int jobId, IEnumerable<SkillDefinition> skills, Func<int, int> recipesOf)
            => jobId == WorkshopHandler.BaseJob
               || skills.Any(s => s.IsGathering || s.IsForgemagus || recipesOf(s.Id) > 0);

        public static Setting SettingOf(SessionState state, int job)
            => state.CrafterSettings.TryGetValue(job, out var setting) ? setting : Default;

        // ─── Settings ───────────────────────────────────────────────────────────────────────

        /// <summary>isd: every job's settings.</summary>
        public static byte[] BuildSettings(SessionState state, IEnumerable<int>? jobs = null)
        {
            var isd = Pb.New();
            foreach (int job in jobs ?? Jobs())
            {
                var s = SettingOf(state, job);
                int minLevel = job == WorkshopHandler.BaseJob ? 0 : s.MinLevel;
                isd.Msg(1, Pb.New().Var(3, job).VarIfNotZero(4, s.Free ? 1 : 0).VarIfNotZero(5, minLevel));
            }
            return isd.Build();
        }

        /// <summary>irl: one job's settings changed.</summary>
        public static async Task SettingsAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? irl = ConnectionProtocol.ReadPayload(payload, Op.Irl);
            if (irl == null) return;
            var state = SessionContext.State;
            foreach (var f in ProtoMessage.Parse(irl).Fields)
            {
                if (f.FieldNumber != 1 || f.WireType != 2) continue;
                int job = 0, minLevel = 0;
                bool free = false;
                foreach (var g in ProtoMessage.Parse(f.BytesValue).Fields)
                {
                    if (g.WireType != 0) continue;
                    if (g.FieldNumber == 3) job = (int)g.VarIntValue;
                    else if (g.FieldNumber == 4) free = g.VarIntValue != 0;
                    else if (g.FieldNumber == 5) minLevel = (int)Math.Clamp(g.VarIntValue, 0, 200);
                }
                if (job == 0 || !JobManager.TryGet(job, out _)) continue;

                var before = SettingOf(state, job);
                var now = before with { MinLevel = minLevel, Free = free };
                if (now == before) continue;
                state.CrafterSettings[job] = now;
                DatabaseManager.SaveCrafterSetting(state.CharacterId, job, now);
                if (now.Listed) await AnnounceAsync(SessionContext.Current, job, listed: true);
            }
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Isd, BuildSettings(state)));
        }

        // ─── Listing ────────────────────────────────────────────────────────────────────────

        /// <summary>iro: whether each of these jobs is in the public list.</summary>
        public static byte[] BuildListing(IEnumerable<(int Job, bool Listed)> jobs)
        {
            var iro = Pb.New();
            foreach (var (job, listed) in jobs) iro.Msg(1, Pb.New().Var(1, job).VarIfNotZero(2, listed ? 1 : 0));
            return iro.Build();
        }

        /// <summary>kef: in or out of the public list, job by job.</summary>
        public static async Task ToggleListingAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kef = ConnectionProtocol.ReadPayload(payload, Op.Kef);
            if (kef == null) return;
            var me = SessionContext.Current;
            var toggled = new List<(int Job, bool Listed)>();
            foreach (int job in PackedOf(kef, 2))
            {
                if (!JobManager.TryGet(job, out _)) continue;
                var now = SettingOf(me.State, job) with { Listed = !SettingOf(me.State, job).Listed };
                me.State.CrafterSettings[job] = now;
                DatabaseManager.SaveCrafterSetting(me.CharacterId, job, now);
                toggled.Add((job, now.Listed));
            }
            if (toggled.Count == 0) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Iro, BuildListing(toggled)));
            foreach (var (job, listed) in toggled) await AnnounceAsync(me, job, listed);
            Console.WriteLine($"[Artisans] {me.State.CharacterName}: " +
                              string.Join(", ", toggled.Select(t => $"job {t.Job} {(t.Listed ? "listed" : "off the list")}")) + ".");
        }

        /// <summary>
        /// Tells whoever has this job's list open: isv when the artisan is (still) in it, isq when
        /// they left it.
        /// </summary>
        private static async Task AnnounceAsync(GameSession artisan, int job, bool listed)
        {
            byte[] message = listed
                ? ConnectionProtocol.Push(Op.Isv, Pb.New().Msg(1, Entry(artisan, job)).Build())
                : ConnectionProtocol.Push(Op.Isq, Pb.New().Var(1, artisan.CharacterId).Var(2, job).Build());
            foreach (var reader in SessionRegistry.InWorld().Where(s => s.State.DirectoryJob == job))
                await reader.SendAsync(message);
        }

        /// <summary>An artisan leaves the game: gone from every list someone is reading.</summary>
        public static async Task LeftAsync(GameSession artisan)
        {
            foreach (var (job, setting) in artisan.State.CrafterSettings.ToList())
            {
                if (!setting.Listed) continue;
                byte[] isq = ConnectionProtocol.Push(Op.Isq, Pb.New().Var(1, artisan.CharacterId).Var(2, job).Build());
                foreach (var reader in SessionRegistry.InWorld().Where(s => s.State.DirectoryJob == job && s != artisan))
                    await reader.SendAsync(isq);
            }
        }

        // ─── The book and the list ──────────────────────────────────────────────────────────

        /// <summary>The book of a workshop: its window, with the workshop's jobs.</summary>
        public static async Task OpenBookAsync(NetworkStream stream, int elementId, int skillId)
        {
            var state = SessionContext.State;
            var jobs = Workshops.BookJobsOn(state.MapId);
            state.Workshop = new WorkshopHandler.Bench { ElementId = elementId, SkillId = skillId, Book = true };
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, state.CharacterId)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kfj, BuildBook(jobs)));
            Console.WriteLine($"[Artisans] Book {elementId} opened: jobs {string.Join(", ", jobs)}.");
        }

        /// <summary>kfj: the book's window, with the workshop's jobs packed in f2.</summary>
        public static byte[] BuildBook(IEnumerable<int> jobs) => Pb.New().Packed(2, jobs.Select(j => (long)j)).Build();

        /// <summary>isr: one job's artisans.</summary>
        public static async Task ListAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? isr = ConnectionProtocol.ReadPayload(payload, Op.Isr);
            if (isr == null) return;
            int job = 0;
            foreach (var f in ProtoMessage.Parse(isr).Fields)
                if (f.FieldNumber == 2 && f.WireType == 0) job = (int)f.VarIntValue;
            if (job == 0) return;

            SessionContext.State.DirectoryJob = job;
            var artisans = SessionRegistry.InWorld()
                .Where(s => s.CharacterId != 0 && SettingOf(s.State, job).Listed).ToList();
            var isf = Pb.New();
            foreach (var artisan in artisans) isf.Msg(1, Entry(artisan, job));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Isf, isf.Build()));
            Console.WriteLine($"[Artisans] Job {job}: {artisans.Count} artisan(s) listed.");
        }

        /// <summary>One artisan of the list: the job as they practise it, and who and where they are.</summary>
        public static Pb Entry(GameSession artisan, int job)
        {
            var state = artisan.State;
            var s = SettingOf(state, job);
            return Pb.New()
                .Msg(1, Pb.New().Var(2, job).VarIfNotZero(3, s.MinLevel).VarIfNotZero(4, s.Free ? 1 : 0)
                                .VarIfNotZero(5, state.JobLevel(job)))
                .Msg(2, Pb.New()
                    .Msg(1, Pb.New().Var(1, OnlineStatus))
                    .Str(2, state.CharacterName)
                    .Var(3, state.Breed)
                    .Var(4, state.CharacterId)
                    .VarIfNotZero(5, state.Sex)
                    .Msg(7, Pb.New().Var(1, state.MapId)));
        }

        /// <summary>The f2.f1.f1 of every captured entry: 1.</summary>
        private const int OnlineStatus = 1;

        private static IEnumerable<int> PackedOf(byte[] body, int field)
        {
            foreach (var f in ProtoMessage.Parse(body).Fields)
            {
                if (f.FieldNumber != field) continue;
                if (f.WireType == 0) { yield return (int)f.VarIntValue; continue; }
                if (f.WireType != 2) continue;
                byte[] packed = f.BytesValue;
                int i = 0;
                while (i < packed.Length)
                {
                    long v = 0; int shift = 0;
                    while (i < packed.Length)
                    {
                        byte b = packed[i++];
                        v |= (long)(b & 0x7f) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    yield return (int)v;
                }
            }
        }
    }
}
