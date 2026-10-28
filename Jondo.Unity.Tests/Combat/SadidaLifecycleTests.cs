using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

// Regression scenarios from the Sadida 3.6.10.10 captures. Use the actual spell rows
// and summon starting spells: testing a displayed root row misses the lifecycle.
[Collection("MapManager")]
public class SadidaLifecycleTests
{
    private static Fighter Person(long id, int team, int cell) => new()
    {
        Id = id, TeamId = team, CellId = cell, Level = 200, Breed = 10,
        MaxHP = 5000, CurrentHP = 2500, MaxAP = 20, CurrentAP = 20, MaxMP = 6, CurrentMP = 6,
        Otras = { [26] = 20 },
    };

    private static (FightInstance Fight, Fighter Sadida, Fighter Enemy) Scene(int cell = 300, int enemyCell = 314, long firstId = 100)
    {
        var fight = new FightInstance(1, 1) { Reglas = FightRules.Desafio };
        var sadida = Person(firstId, 0, cell);
        var enemy = Person(firstId + 100, 1, enemyCell);
        fight.AddPlayer(sadida); fight.AddOpponent(enemy);
        fight.StartFight();
        return (fight, sadida, enemy);
    }

    private static Task Cast(FightInstance fight, Fighter caster, int spell, Fighter target, int grade = 1, int cell = -1)
        => FightHandler.AplicarEfectosAsync(null, fight, caster, spell, grade, target,
            EffectEngine.AlLanzar, cell < 0 ? target.CellId : cell);

    private static async Task Initialize(FightInstance fight, Fighter caster, int spell, int grade)
    {
        foreach (var initial in ClassPassives.ForFight(10, new[] { (spell, grade) }))
            await Cast(fight, caster, initial.Spell, caster, initial.Grade);
    }

    private static async Task Advance(FightInstance fight, int round)
    {
        while (fight.RoundNumber < round) fight.NextTurn();
        await FightHandler.ApplyDuePendingAsync(null, fight);
    }

    private static Fighter SummonOf(FightInstance fight, int template)
        => Assert.Single(fight.Todos, f => f.IsAlive && f.MonsterId == template);

    [Fact]
    public async Task Arbre_waits_one_round_before_becoming_leafy()
    {
        var (fight, sadida, _) = Scene();
        await Cast(fight, sadida, 13519, sadida, cell: 330);
        var tree = SummonOf(fight, 5894);
        int birthHP = tree.MaxHP;
        Assert.DoesNotContain(256, tree.Buffs.Estados);
        Assert.Contains(tree.Buffs.Puestos, b => b.Pendiente && b.EffectId == 792 && b.EmpiezaEnRonda == 2);
        await FightHandler.ApplyDuePendingAsync(null, fight);
        Assert.DoesNotContain(256, tree.Buffs.Estados);
        await Advance(fight, 2);
        Assert.Contains(256, tree.Buffs.Estados);
        Assert.Equal(birthHP * 2, tree.MaxHP);
        Assert.DoesNotContain(tree.Buffs.Puestos, b => b.Pendiente && b.HechizoOrigen == 14700);
    }

    [Fact]
    public async Task Arbre_feuillu_is_mature_at_birth()
    {
        var (fight, sadida, _) = Scene();
        await Cast(fight, sadida, 13560, sadida, cell: 330);
        Assert.Contains(256, SummonOf(fight, 5894).Buffs.Estados);
    }

    private static byte[] At(byte[] payload, params int[] path)
    {
        foreach (int field in path)
            payload = ProtoMessage.Parse(payload).Fields.Single(f => f.FieldNumber == field).BytesValue;
        return payload;
    }

    private static bool Action(byte[] payload, int action)
        => ProtoMessage.Parse(payload).Fields.Any(f => f.FieldNumber == 14 && f.VarIntValue == action);

    private static void Look(byte[] payload, int bones, int scale)
    {
        var fields = ProtoMessage.Parse(payload).Fields;
        Assert.Equal(bones, fields.Single(f => f.FieldNumber == 3).VarIntValue);
        Assert.Equal(new byte[] { (byte)scale }, fields.Single(f => f.FieldNumber == 5).BytesValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tree_growth_sends_the_captured_look_to_both_players(bool useLarme)
    {
        var (fight, sadida, enemy) = Scene(enemyCell: 500, firstId: 8_510_000);
        await using var ownerWire = await PortalTests.Wire.Open(sadida.Id);
        await using var opponentWire = await PortalTests.Wire.Open(enemy.Id);
        await Cast(fight, sadida, 13519, sadida, cell: 330);
        var tree = SummonOf(fight, 5894);
        var birth = await ownerWire.Drain();
        await opponentWire.Drain();
        var summon = Assert.Single(birth, f => f.Op == "jwe" && Action(f.Payload, 181));
        Look(At(summon.Payload, 1, 1, 1, 3), 3163, 60); // Arbre packet 15.
        Assert.DoesNotContain(birth, f => f.Op == "jwe" && Action(f.Payload, 149));
        Assert.DoesNotContain(256, tree.Buffs.Estados);
        Look(FightHandler.CurrentFightLook(fight, tree), 3163, 60);

        if (useLarme) await Cast(fight, sadida, 13528, tree);
        else await Advance(fight, 2);

        Assert.Contains(256, tree.Buffs.Estados);
        Assert.DoesNotContain(tree.Buffs.Puestos, b => b.Pendiente && b.HechizoOrigen == 14700);
        foreach (var frames in new[] { await ownerWire.Drain(), await opponentWire.Drain() })
        {
            var change = Assert.Single(frames, f => f.Op == "jwe" && Action(f.Payload, 149));
            Look(At(change.Payload, 26, 3), 3164, 70); // Arbre packet 58; Larme packet 57.
        }
        Look(FightHandler.CurrentFightLook(fight, tree), 3164, 70);
        if (useLarme)
        {
            await Advance(fight, 2);
            Assert.DoesNotContain(await ownerWire.Drain(), f => f.Op == "jwe" && Action(f.Payload, 149));
        }
    }

    [Fact]
    public async Task The_leafy_variant_sends_its_mature_look_immediately()
    {
        var (fight, sadida, _) = Scene(enemyCell: 500, firstId: 8_520_000);
        await using var wire = await PortalTests.Wire.Open(sadida.Id);
        await Cast(fight, sadida, 13560, sadida, cell: 330);
        var tree = SummonOf(fight, 5894);
        var frames = await wire.Drain();
        var change = Assert.Single(frames, f => f.Op == "jwe" && Action(f.Payload, 149));
        Look(At(change.Payload, 26, 3), 3164, 70);
        Assert.Contains(256, tree.Buffs.Estados);
        Look(FightHandler.CurrentFightLook(fight, tree), 3164, 70);
        Assert.Equal(0, SpellTable.GradeFor(13560, 114));
        Assert.Equal(1, SpellTable.GradeFor(13560, 115));
    }

    [Theory]
    [InlineData(13519, false)]
    [InlineData(13528, true)]
    [InlineData(13560, true)]
    [InlineData(14700, true)]
    public async Task Ronce_only_hits_enemies_around_a_leafy_tree(int maturation, bool relays)
    {
        var (fight, sadida, enemy) = Scene(enemyCell: 344);
        await Cast(fight, sadida, maturation == 13560 ? 13560 : 13519, sadida, cell: 330);
        var tree = SummonOf(fight, 5894);
        if (maturation == 13528) await Cast(fight, sadida, 13528, tree);
        if (maturation == 14700) await Advance(fight, 2);
        int before = enemy.CurrentHP;
        await Cast(fight, sadida, 13516, tree);
        Assert.Equal(relays, enemy.CurrentHP < before);
    }

    [Fact]
    public async Task Arbre_de_vie_keeps_its_special_form_for_two_rounds()
    {
        var (fight, sadida, _) = Scene();
        await Cast(fight, sadida, 13534, sadida, cell: 330);
        var tree = SummonOf(fight, 5894);
        // ArbreDeVie packet 18 explicitly applies 256 at birth; 5179 and look 1171
        // cover it until round +2. Both percentage bonuses in packets 18/21 are +630.
        Assert.Contains(256, tree.Buffs.Estados);
        Assert.Contains(5179, tree.Buffs.Estados);
        Assert.Equal(1890, tree.MaxHP);
        Assert.Contains(tree.Buffs.Puestos, b => b.Pendiente && b.HechizoOrigen == 13599 && b.EmpiezaEnRonda == 3);
        await Advance(fight, 2);
        Assert.Contains(5179, tree.Buffs.Estados);
        await Advance(fight, 3);
        tree.Buffs.Barrer(3);
        Assert.DoesNotContain(5179, tree.Buffs.Estados);
        Assert.Contains(256, tree.Buffs.Estados);
    }

    [Theory]
    [InlineData(13519)]
    [InlineData(13560)]
    public async Task The_seventh_tree_removes_the_oldest(int spell)
    {
        var (fight, sadida, _) = Scene();
        Fighter oldest = null;
        for (int i = 0; i < 7; i++)
        {
            await Cast(fight, sadida, spell, sadida, cell: 330 + i);
            oldest ??= SummonOf(fight, 5894);
            if (i < 6) await Advance(fight, fight.RoundNumber + 1);
        }
        Assert.Equal(6, fight.Todos.Count(f => f.IsAlive && f.MonsterId == 5894));
        Assert.False(oldest.IsAlive);
    }

    [Theory]
    [InlineData(13530, 5901)]
    [InlineData(13566, 5900)]
    public async Task Controlled_trees_survive_two_rounds_then_return_to_a_leafy_tree(int spell, int template)
    {
        var (fight, sadida, _) = Scene();
        await Cast(fight, sadida, 13560, sadida, cell: 330);
        var original = SummonOf(fight, 5894);
        await Cast(fight, sadida, spell, original);
        var controlled = SummonOf(fight, template);
        Assert.False(original.IsAlive);
        Assert.Contains(controlled.Buffs.Puestos, b => b.Pendiente && b.EffectId == 405 && b.EmpiezaEnRonda == 3);
        await Advance(fight, 2);
        Assert.True(controlled.IsAlive);
        await Advance(fight, 3);
        Assert.False(controlled.IsAlive);
        var returned = SummonOf(fight, 5894);
        Assert.Equal(330, returned.CellId);
        Assert.Equal(sadida.Id, returned.Invocador);
        Assert.Contains(256, returned.Buffs.Estados);
    }

    [Theory]
    [InlineData(13515, 5896, 1)]
    [InlineData(13515, 5896, 2)]
    [InlineData(13526, 5895, 1)]
    [InlineData(13522, 5898, 1)]
    [InlineData(13523, 5897, 1)]
    [InlineData(13520, 5899, 1)]
    public async Task Transmuted_dolls_play_for_three_rounds_before_returning_to_a_tree(int spell, int template, int grade)
    {
        var (fight, sadida, _) = Scene();
        await Initialize(fight, sadida, spell, grade);
        await Cast(fight, sadida, spell, sadida, grade, 330);
        var doll = SummonOf(fight, template);
        Assert.Contains(doll.Buffs.Puestos, b => b.Pendiente && b.HechizoOrigen == 13549
            && b.NivelOrigen == 3 && b.EffectId == 405 && b.EmpiezaEnRonda == 4);
        await Advance(fight, 2);
        Assert.True(doll.IsAlive);
        await Advance(fight, 3);
        Assert.True(doll.IsAlive);
        await Advance(fight, 4);
        Assert.False(doll.IsAlive);
        var tree = SummonOf(fight, 5894);
        Assert.Equal(330, tree.CellId);
        Assert.Equal(sadida.Id, tree.Invocador);
        Assert.DoesNotContain(256, tree.Buffs.Estados);
        await Advance(fight, 5);
        Assert.Contains(256, tree.Buffs.Estados);
    }

    [Fact]
    public async Task Sacrifiee_grows_in_two_stages_instead_of_at_birth()
    {
        var (fight, sadida, _) = Scene();
        var doll = await FightHandler.SummonAsync(null, fight, sadida, 5898, 2, 330);
        int birthHP = doll.MaxHP;
        Assert.Contains(5418, doll.Buffs.Estados);
        Assert.DoesNotContain(5419, doll.Buffs.Estados);
        Assert.DoesNotContain(5420, doll.Buffs.Estados);
        Assert.Equal(630, birthHP);
        await Advance(fight, 2);
        Assert.DoesNotContain(5418, doll.Buffs.Estados);
        Assert.Contains(5419, doll.Buffs.Estados);
        Assert.DoesNotContain(5420, doll.Buffs.Estados);
        Assert.Equal(birthHP + birthHP / 2, doll.MaxHP);
        await Advance(fight, 3);
        Assert.DoesNotContain(5419, doll.Buffs.Estados);
        Assert.Contains(5420, doll.Buffs.Estados);
        Assert.Equal(birthHP * 5 / 2, doll.MaxHP);
    }

    [Fact]
    public async Task Miasmes_fires_on_the_poisoned_enemys_turn_even_after_he_moves()
    {
        var (fight, sadida, enemy) = Scene();
        await Cast(fight, sadida, 13562, sadida, 2);
        Assert.Contains(263, enemy.Buffs.Estados);
        Assert.Contains(enemy.Buffs.ActiveSpells, h => h.Hechizo == 13580 && h.Filas != null);
        Assert.DoesNotContain(sadida.Buffs.ActiveSpells, h => h.Hechizo == 13580);
        int before = enemy.CurrentHP;
        await FightHandler.EngancheAsync(null, fight, sadida, EffectEngine.AlAcabarElTurno);
        Assert.Equal(before, enemy.CurrentHP);
        enemy.MoverA(400);
        var lateArrival = Person(201, 1, 315);
        fight.AddOpponent(lateArrival);
        await FightHandler.EngancheAsync(null, fight, enemy, EffectEngine.AlAcabarElTurno);
        Assert.True(enemy.CurrentHP < before);
        Assert.Equal(2500, lateArrival.CurrentHP);
    }

    [Theory]
    [InlineData(259, 286, 272)]
    [InlineData(230, 273, 244)]
    public async Task Rempotage_moves_the_doll_then_plants_on_its_original_cell(int ownerCell, int dollCell, int expected)
    {
        var (fight, sadida, _) = Scene(ownerCell, 500);
        var doll = await FightHandler.SummonAsync(null, fight, sadida, 5895, 1, dollCell);
        doll.CurrentHP = doll.MaxHP / 2;
        int before = doll.CurrentHP;
        await Cast(fight, sadida, 13565, doll);
        Assert.Equal(expected, doll.CellId);
        Assert.True(doll.CurrentHP > before);
        Assert.Equal(dollCell, SummonOf(fight, 5894).CellId);
    }

    [Fact]
    public async Task Rempotage_skips_an_occupied_cell_between_the_owner_and_doll()
    {
        var (fight, sadida, _) = Scene(230, 244);
        var doll = await FightHandler.SummonAsync(null, fight, sadida, 5895, 1, 273);
        await Cast(fight, sadida, 13565, doll);
        Assert.Equal(259, doll.CellId);
        Assert.Equal(273, SummonOf(fight, 5894).CellId);
    }

    [Fact]
    public async Task Miasmes_overlapping_anchors_do_not_duplicate_the_poison()
    {
        var (fight, sadida, enemy) = Scene();
        await Cast(fight, sadida, 13560, sadida, cell: 329);
        await Cast(fight, sadida, 13562, sadida, 2);
        Assert.Single(enemy.Buffs.ActiveSpells, h => h.Hechizo == 13580);
        int before = enemy.CurrentHP;
        await FightHandler.EngancheAsync(null, fight, enemy, EffectEngine.AlAcabarElTurno);
        Assert.InRange(before - enemy.CurrentHP, 12, 14);
    }

    [Fact]
    public async Task Rempotage_does_not_teleport_beyond_the_doll_when_the_diagonal_is_blocked()
    {
        var (fight, sadida, _) = Scene(300, 301);
        var doll = await FightHandler.SummonAsync(null, fight, sadida, 5895, 1, 302);
        await Cast(fight, doll, 13581, sadida);
        Assert.Equal(302, doll.CellId);
    }

    [Fact]
    public async Task A_delayed_replacement_alone_also_waits_and_returns_to_the_original_cell()
    {
        var (fight, sadida, _) = Scene();
        var doll = Person(-3, 0, 330);
        doll.IsMonster = true; doll.MonsterId = 5895; doll.Invocador = sadida.Id;
        fight.Invocar(doll, sadida);
        var row = SpellEffects.De(13549, 3).Single(r => r.EffectId == 405);
        await FightHandler.AplicarEfectosAsync(null, fight, sadida, 13549, 3, doll,
            EffectEngine.AlLanzar, doll.CellId, tirada: new[] { row });
        Assert.True(doll.IsAlive);
        await Advance(fight, 3);
        Assert.True(doll.IsAlive);
        doll.MoverA(344);
        await Advance(fight, 4);
        Assert.False(doll.IsAlive);
        Assert.Equal(344, SummonOf(fight, 5894).CellId);
    }

    [Theory]
    [InlineData(13516, 2)]
    [InlineData(13577, 4)]
    public async Task Infection_still_expires_at_caster_turn_start_with_its_original_duration(int spell, int expiry)
    {
        var (fight, sadida, enemy) = Scene();
        await Cast(fight, sadida, 29615, enemy);
        await Cast(fight, sadida, spell, enemy);
        Assert.Contains(263, enemy.Buffs.Estados);
        // Several casts can carry the same state; it remains until the last row expires.
        Assert.Equal(expiry, enemy.Buffs.Puestos.Where(b => b.Estado == 263).Max(b => b.CaducaEnRonda));
        await FightHandler.EngancheAsync(null, fight, sadida, EffectEngine.AlAcabarElTurno);
        Assert.Contains(263, enemy.Buffs.Estados);
        enemy.Buffs.Barrer(expiry - 1, b => b.Quien == sadida.Id);
        Assert.Contains(263, enemy.Buffs.Estados);
        enemy.Buffs.Barrer(expiry, b => b.Quien == sadida.Id);
        Assert.DoesNotContain(263, enemy.Buffs.Estados);
    }

    [Theory]
    [InlineData(13515, 4007)]
    [InlineData(13526, 4008)]
    [InlineData(13522, 4009)]
    [InlineData(13523, 4010)]
    [InlineData(13520, 4011)]
    public async Task Transmutation_initialization_uses_an_existing_rank(int spell, int state)
    {
        var (fight, sadida, _) = Scene();
        await Initialize(fight, sadida, spell, 3);
        Assert.Contains(state, sadida.Buffs.Estados);
    }

    [Theory]
    [InlineData(5894)]
    [InlineData(5900)]
    [InlineData(5901)]
    public void Alternative_starred_templates_are_OR_but_state_conditions_still_apply(int template)
    {
        var tree = new Fighter { IsMonster = true, MonsterId = template };
        const string mask = "C,*F5894,*F5900,*F5901,*e5387";
        Assert.True(EffectEngine.CasterQualifies(tree, mask));
        tree.Buffs.PonerEstado(5387);
        Assert.False(EffectEngine.CasterQualifies(tree, mask));
        Assert.False(EffectEngine.CasterQualifies(new Fighter { IsMonster = true, MonsterId = 5895 }, mask));
    }
}
