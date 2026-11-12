using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// What a chat line's links stand for (kti f8, ktp), for the links the client sends short and
    /// draws only from what the server adds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A monster group shared from its menu goes as «{monsterGroup,-1021015}», its id alone
    /// (HyperlinkShowMonsterGroup.CreateLinkForNetwork). The client that receives the line turns it
    /// into the full link from the ktp's f2, one kte per group (hbr.izr): f1 the leader's monster,
    /// f2 the group, f3 its waves, f4 the world map, f5 where it is { f1 x, f2 y }, f6 the scale mode,
    /// f7 its infos. Sent as it came, the line showed «{monsterGroup,-1021015}».
    /// </para>
    /// <para>
    /// The link reads «Leader [x,y]» (a.fu, «{0} [{1},{2}]», read in the running client), the name
    /// of the f1's monster and the f5. The f7 is what its tooltip lists -- level, experience and
    /// each monster --, the text the client makes of a group (RoleplayEntitiesService.xzk) and reads
    /// back (xzl): «grade x monster x level» for each, joined by «|». Taken in the running client
    /// from a group of three Chakrobats and two Ronronchons: «3x982x6|3x982x6|2x982x5|1x4105x6|1x4105x6».
    /// Empty, the link was drawn but its tooltip showed nothing.
    /// </para>
    /// </remarks>
    public static class ChatLinks
    {
        private static readonly Regex MonsterGroup = new(@"\{monsterGroup,(-?\d+)\}", RegexOptions.Compiled);

        /// <summary>The world map a map of the Twelve is drawn on, as the map links say it (HeadHunts).</summary>
        private const int WorldOfTheTwelve = 1;

        /// <summary>
        /// The ktp for the monster groups <paramref name="text"/> links, those of the map the speaker
        /// stands on; null when it links none that is there.
        /// </summary>
        public static Pb? Of(string text, long mapId)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("{monsterGroup,")) return null;
            var map = MapManager.GetMapInfo(mapId);
            if (map == null) return null;

            var groups = MobSpawnManager.GetMobsForMap(mapId);
            var ktp = Pb.New();
            bool any = false;
            var seen = new HashSet<long>();
            foreach (Match match in MonsterGroup.Matches(text))
            {
                if (!long.TryParse(match.Groups[1].Value, out long id) || !seen.Add(id)) continue;
                var group = groups.Find(g => g.MobId == id);
                if (group == null || group.Members.Count == 0) continue;
                ktp.Msg(2, MonsterGroupEntry(group, map));
                any = true;
            }
            return any ? ktp : null;
        }

        /// <summary>One group's kte: its leader, its id, where it stands and what it is made of.</summary>
        internal static Pb MonsterGroupEntry(MobSpawnManager.MobGroup group, MapInfo map)
            => Pb.New()
                .Var(1, group.Members[0].Monster.Id)
                .Var(2, group.MobId)
                .Var(4, WorldOfTheTwelve)
                .Msg(5, Pb.New().Var(1, map.PosX).Var(2, map.PosY))
                .Str(7, GroupInfos(group));

        /// <summary>The group as its tooltip reads it: «grade x monster x level» for each, joined by «|».</summary>
        internal static string GroupInfos(MobSpawnManager.MobGroup group)
            => string.Join("|", group.Members.Select(m =>
                $"{ConnectionProtocol.GradeOf(m)}x{m.Monster.Id}x{ConnectionProtocol.LevelOf(m)}"));
    }
}
