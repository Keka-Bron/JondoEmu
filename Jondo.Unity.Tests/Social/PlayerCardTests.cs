using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.Social
{
    /// <summary>A player's card, in the shape of «Busqueda grupo/…postularse a otro…»'s lad.</summary>
    public sealed class PlayerCardTests
    {
        private const long Account = 8_900_000_071;

        [Fact]
        public async Task The_card_carries_the_character_and_his_looks()
        {
            Wardrobe.Initialize();
            long character = DatabaseManager.CreateCharacter(Account, 1, "Ficha-Uno", 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 120, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_071, "Ficha-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    await PlayerCardHandler.ShowAsync(pipe.ToClient, ConnectionProtocol.Push("kxh", Pb.New().Var(2, character).Build()));
                    var lad = ProtoMessage.Parse(await pipe.Next("lad")).Fields;
                    var body = ProtoMessage.Parse(lad.Single(f => f.FieldNumber == 2).BytesValue).Fields;
                    Assert.True(DateTime.TryParse(System.Text.Encoding.UTF8.GetString(body.Single(f => f.FieldNumber == 3).BytesValue), out _));
                    var card = ProtoMessage.Parse(body.Single(f => f.FieldNumber == 4).BytesValue).Fields;
                    long Number(int n) => card.Single(f => f.FieldNumber == n).VarIntValue;
                    Assert.Equal(Account, Number(2));
                    Assert.Equal(11, Number(3));
                    Assert.Equal(character, Number(4));
                    Assert.Equal(1, Number(6));       // online: he is the one asking
                    Assert.Equal(120, Number(7));
                    Assert.Equal("Ficha-Uno", System.Text.Encoding.UTF8.GetString(card.Single(f => f.FieldNumber == 11).BytesValue));
                    Assert.Contains(card, f => f.FieldNumber == 8);
                    Assert.Contains(card, f => f.FieldNumber == 14);
                    Assert.DoesNotContain(card, f => f.FieldNumber == 10);   // male
                    Assert.DoesNotContain(card, f => f.FieldNumber == 9);    // no background picked

                    // The background window's kye { 2 background }: the card carries it from then on.
                    await PlayerCardHandler.SetBackgroundAsync(pipe.ToClient, ConnectionProtocol.Push("kye", Pb.New().Var(2, 5).Build()));
                    // ...and is told so, kxr { 2 { 1 background } }, which the window draws.
                    var kxr = ProtoMessage.Parse(await pipe.Next("kxr")).Fields;
                    Assert.Equal(5, ProtoMessage.Parse(kxr.Single(f => f.FieldNumber == 2).BytesValue).Fields.Single(f => f.FieldNumber == 1).VarIntValue);
                    await PlayerCardHandler.ShowAsync(pipe.ToClient, ConnectionProtocol.Push("kxh", Pb.New().Var(2, character).Build()));
                    var again = ProtoMessage.Parse(await pipe.Next("lad")).Fields;
                    var card2 = ProtoMessage.Parse(ProtoMessage.Parse(again.Single(f => f.FieldNumber == 2).BytesValue)
                        .Fields.Single(f => f.FieldNumber == 4).BytesValue).Fields;
                    Assert.Equal(5, card2.Single(f => f.FieldNumber == 9).VarIntValue);

                    // A character that does not exist: nothing.
                    await PlayerCardHandler.ShowAsync(pipe.ToClient, ConnectionProtocol.Push("kxh", Pb.New().Var(2, 1).Build()));
                    await pipe.Quiet();
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }

        /// <summary>
        /// The card's general tab, asked lag { 3 character } with a request id: kzl { 1 kyi { 3 kyr } },
        /// with the jobs at their levels in f2 and nothing for the equipment and ranking tabs but
        /// their own empty branch.
        /// </summary>
        [Fact]
        public async Task The_general_tab_carries_the_jobs()
        {
            Wardrobe.Initialize();
            long character = DatabaseManager.CreateCharacter(Account, 1, "Ficha-Tres", 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 120, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                DatabaseManager.SaveJobExperience(character, 26, JobExperience.Floor(200));
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_073, "Ficha-Tres");
                using (SessionContext.Push(pipe.Session))
                {
                    async Task<List<ProtoField>> Tab(long tab)
                    {
                        var ask = Pb.New().VarIfNotZero(2, tab).Var(3, character);
                        var any = Pb.New().Str(1, ConnectionProtocol.UriPrefix + "lag").Bytes(2, ask.Build());
                        await PlayerCardHandler.TabAsync(pipe.ToClient, Pb.New().Msg(2, Pb.New().Bytes(1, any.Build()).Var(2, 7)).Build());
                        byte[] frame = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer);
                        var answer = ProtoMessage.Parse(ProtoMessage.Parse(frame).Fields.Single(f => f.FieldNumber == 3).BytesValue).Fields;
                        Assert.Equal(7, answer.Single(f => f.FieldNumber == 2).VarIntValue);
                        var inner = ProtoMessage.Parse(answer.Single(f => f.FieldNumber == 1).BytesValue).Fields;
                        Assert.EndsWith("/kzl", System.Text.Encoding.UTF8.GetString(inner.Single(f => f.FieldNumber == 1).BytesValue));
                        var kzl = ProtoMessage.Parse(inner.Single(f => f.FieldNumber == 2).BytesValue).Fields;
                        return ProtoMessage.Parse(kzl.Single(f => f.FieldNumber == 1).BytesValue).Fields;
                    }

                    // Every job in f5, his raised one at its level.
                    var general = ProtoMessage.Parse((await Tab(0)).Single(f => f.FieldNumber == 3).BytesValue).Fields;
                    var jobs = general.Where(f => f.FieldNumber == 5)
                        .Select(f => ProtoMessage.Parse(f.BytesValue).Fields)
                        .ToDictionary(e => e.Single(f => f.FieldNumber == 1).VarIntValue, e => e.Single(f => f.FieldNumber == 2).VarIntValue);
                    Assert.Equal(200, jobs[26]);
                    Assert.DoesNotContain(general, f => f.FieldNumber == 7);

                    // The jobs picked to show (kxs, the whole list each time) come back in f7.
                    await PlayerCardHandler.SetJobsAsync(ConnectionProtocol.Push("kxs", Pb.New().Packed(1, new long[] { 26, 999 }).Build()));
                    general = ProtoMessage.Parse((await Tab(0)).Single(f => f.FieldNumber == 3).BytesValue).Fields;
                    Assert.Equal(new byte[] { 26 }, general.Single(f => f.FieldNumber == 7).BytesValue);

                    // The equipment tab carries its characteristics block (f3) and f4; the ranking
                    // tab is read from its f2 alone.
                    var gear = ProtoMessage.Parse((await Tab(1)).Single(f => f.FieldNumber == 2).BytesValue).Fields;
                    Assert.Contains(gear, f => f.FieldNumber == 3);
                    Assert.Contains(gear, f => f.FieldNumber == 4);
                    var ranks = ProtoMessage.Parse((await Tab(2)).Single(f => f.FieldNumber == 1).BytesValue).Fields;
                    Assert.DoesNotContain(ranks, f => f.FieldNumber == 1);
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }

        /// <summary>
        /// The character sheet's «Mon profil» asks with a request id -- 12 20 { 0A any(kxh), 10 03 }
        /// -- and waits for an answer carrying it: the card comes back in root field 3, with that id.
        /// </summary>
        [Fact]
        public async Task A_card_asked_with_a_request_id_is_answered_with_it()
        {
            Wardrobe.Initialize();
            long character = DatabaseManager.CreateCharacter(Account, 1, "Ficha-Dos", 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 120, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_072, "Ficha-Dos");
                using (SessionContext.Push(pipe.Session))
                {
                    var any = Pb.New().Str(1, ConnectionProtocol.UriPrefix + "kxh").Bytes(2, Pb.New().Var(2, character).Build());
                    byte[] request = Pb.New().Msg(2, Pb.New().Bytes(1, any.Build()).Var(2, 3)).Build();
                    await PlayerCardHandler.ShowAsync(pipe.ToClient, request);

                    byte[] frame = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer);
                    var answer = ProtoMessage.Parse(ProtoMessage.Parse(frame).Fields.Single(f => f.FieldNumber == 3).BytesValue).Fields;
                    Assert.Equal(3, answer.Single(f => f.FieldNumber == 2).VarIntValue);
                    var inner = ProtoMessage.Parse(answer.Single(f => f.FieldNumber == 1).BytesValue).Fields;
                    Assert.EndsWith("/lad", System.Text.Encoding.UTF8.GetString(inner.Single(f => f.FieldNumber == 1).BytesValue));

                    // The card's tabs, packed: General, Equipment, Ranking.
                    var lad = ProtoMessage.Parse(inner.Single(f => f.FieldNumber == 2).BytesValue).Fields;
                    var body = ProtoMessage.Parse(lad.Single(f => f.FieldNumber == 2).BytesValue).Fields;
                    Assert.Equal(new byte[] { 0, 1, 2 }, body.Single(f => f.FieldNumber == 1).BytesValue);
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }
    }
}
