using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher
{
    public static class RegressionGuardTests
    {
        private static readonly string[] ForbiddenLiterals = new string[]
        {
            "670668947750",
            "-20003",
            "\"Fortellon\""
        };

        public static void Run()
        {
            // The connection phase messages are always checked, in deployment too: that is where
            // structural bugs used to slip through, and all the client shows there is a blank
            // screen with no error at all.
            Network.ConnectionProtocolSelfTest.Run();
            Network.ClientLaunchRegistry.AssertTwoClientsAreIsolated();
            Network.ClientLaunchRegistry.AssertEightClientLimit();
            AssertPerSessionPlayerCaches();
            AssertSocketWritesAreSerialized();
            AssertProfessionCatalog();
            AssertRelativeMapLookup();
            AssertInteractiveRegistry();

            // OJO: esta parte no llega a correr nunca. Subir tres carpetas desde donde está el
            // binario y volver a bajar a "Jondo.Unity.Launcher" no da con el código fuente en
            // ninguna de las dos formas de ejecutarlo, ni en bin\<config>\<tfw>\ ni en el
            // despliegue, así que siempre se sale por el return de abajo. Se deja igual que
            // estaba —no es parte de ordenar las carpetas— pero conviene saberlo: si se corrige
            // la ruta, la comprobación salta con lo que ya hay escrito hoy.
            //
            // Antes esto usaba Assembly.Location, que con el .exe de un solo fichero devuelve
            // cadena vacía; AppContext.BaseDirectory da lo mismo que daba antes y sin aviso.
            string launcherDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string projectDir = Path.Combine(launcherDir, "..", "..", "..", "Jondo.Unity.Launcher");

            if (!Directory.Exists(projectDir)) return;

            var csFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("BasePayloads.cs") && !f.Contains("TransitionPayloads.cs") && !f.Contains("RegressionGuardTests.cs"))
                .ToList();

            foreach (var file in csFiles)
            {
                string text = File.ReadAllText(file);
                foreach (var literal in ForbiddenLiterals)
                {
                    if (text.Contains(literal))
                    {
                        throw new InvalidOperationException($"[RegressionGuard FAILED] File '{Path.GetFileName(file)}' contains forbidden literal string '{literal}'!");
                    }
                }
            }

            Console.WriteLine("[RegressionGuard] ✅ All CS files passed literal guard test. Zero forbidden capture literals found.");
        }

        private static void AssertPerSessionPlayerCaches()
        {
            var first = Network.GameSession.SinSocket();
            var second = Network.GameSession.SinSocket();

            first.State.EquipmentItems[101] = new Managers.Equipment.Item { Uid = 101 };
            first.State.ChosenSpells[1] = 1001;
            first.State.SpellBar[0] = 1001;
            first.State.OpenNpcShopId = 11;

            second.State.EquipmentItems[202] = new Managers.Equipment.Item { Uid = 202 };
            second.State.ChosenSpells[1] = 2002;
            second.State.SpellBar[0] = 2002;
            second.State.OpenNpcShopId = 22;

            using (Network.SessionContext.Push(first))
            {
                if (Managers.Equipment.ByUid(101) == null || Managers.Equipment.ByUid(202) != null ||
                    Managers.SpellChoices.Chosen[1] != 1001 || Managers.SpellChoices.Bar[0] != 1001 ||
                    first.State.OpenNpcShopId != 11)
                {
                    throw new InvalidOperationException("[RegressionGuard FAILED] First player cache leaked across sessions.");
                }
            }

            using (Network.SessionContext.Push(second))
            {
                if (Managers.Equipment.ByUid(202) == null || Managers.Equipment.ByUid(101) != null ||
                    Managers.SpellChoices.Chosen[1] != 2002 || Managers.SpellChoices.Bar[0] != 2002 ||
                    second.State.OpenNpcShopId != 22)
                {
                    throw new InvalidOperationException("[RegressionGuard FAILED] Second player cache leaked across sessions.");
                }
            }
        }

        private static void AssertSocketWritesAreSerialized()
        {
            var stream = new OverlapDetectingStream();
            Task.WhenAll(
                Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, new byte[] { 1, 2, 3 }),
                Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, new byte[] { 4, 5, 6 }),
                Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, new byte[] { 7, 8, 9 }))
                .GetAwaiter().GetResult();

            if (stream.OverlapDetected)
                throw new InvalidOperationException("[RegressionGuard FAILED] Packet writes overlapped on one socket.");
        }

        private static void AssertInteractiveRegistry()
        {
            var expected = new HashSet<(long MapId, int ElementId)>();
            foreach (long mapId in Managers.Interactives.MapIds)
            {
                foreach (var zaap in Managers.Interactives.ZaapElements(mapId))
                    expected.Add((mapId, zaap.Id));

                var chest = Managers.Merkasako.ChestOf(mapId);
                if (chest.Id != 0) expected.Add((mapId, chest.Id));

                var lottery = Managers.Lottery.Of(mapId);
                if (lottery.Id != 0) expected.Add((mapId, lottery.Id));

                foreach (var interactive in Managers.InteractiveRegistry.OnMap(mapId))
                {
                    foreach (var action in interactive.Actions)
                    {
                        if (!Managers.InteractiveRegistry.TryResolveUse(
                                mapId, interactive.Element.Id, action.SkillInstanceId,
                                out var resolved, out var resolvedAction) ||
                            !ReferenceEquals(interactive, resolved) ||
                            !ReferenceEquals(action, resolvedAction))
                        {
                            throw new InvalidOperationException(
                                "[RegressionGuard FAILED] Interactive registry cannot resolve its own declaration.");
                        }
                    }

                    if (Managers.InteractiveRegistry.TryResolveUse(
                            mapId, interactive.Element.Id, int.MaxValue, out _, out _))
                    {
                        throw new InvalidOperationException(
                            "[RegressionGuard FAILED] Interactive registry accepted a mismatched skill instance.");
                    }
                }
            }

            if (Managers.InteractiveRegistry.Count != expected.Count)
            {
                throw new InvalidOperationException(
                    $"[RegressionGuard FAILED] Expected {expected.Count} interactives, got " +
                    $"{Managers.InteractiveRegistry.Count}.");
            }
        }

        private static void AssertProfessionCatalog()
        {
            if (Managers.JobManager.Count == 0 || Managers.SkillManager.Count == 0 ||
                Managers.RecipeManager.Count == 0)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Profession catalogue is empty.");

            foreach (var skill in Managers.SkillManager.All)
            {
                if (!Managers.JobManager.TryGet(skill.ParentJobId, out _))
                    throw new InvalidOperationException(
                        $"[RegressionGuard FAILED] Skill {skill.Id} references missing job {skill.ParentJobId}.");
            }

            foreach (var recipe in Managers.RecipeManager.All)
            {
                if (!Managers.JobManager.TryGet(recipe.JobId, out _) ||
                    !Managers.SkillManager.TryGet(recipe.SkillId, out _) ||
                    recipe.Ingredients.Count == 0 ||
                    recipe.Ingredients.Any(i => i.ItemId <= 0 || i.Quantity <= 0))
                    throw new InvalidOperationException(
                        $"[RegressionGuard FAILED] Recipe {recipe.ResultId} is inconsistent.");
            }

            var gathering = Managers.SkillManager.All.FirstOrDefault(s => s.IsGathering);
            if (gathering == null ||
                !Handlers.GatheringHandler.TryResolve(gathering.Id, out _, out _, out _))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Gathering handler cannot resolve a gathering skill.");

            var craft = Managers.RecipeManager.All.First();
            if (!Handlers.CraftHandler.TryResolve(craft.SkillId, out _, out _, out var recipes, out _) ||
                !Handlers.CraftHandler.TryResolveRecipe(craft.SkillId, craft.ResultId, out _, out _) ||
                recipes.Count == 0)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Craft handler cannot resolve a known recipe.");
        }

        private static void AssertRelativeMapLookup()
        {
            var group = MapManager.Maps.Values
                .Where(m => m.MapId > 0)
                .GroupBy(m => (m.PosX, m.PosY))
                .FirstOrDefault(g => g.Count() > 1);
            if (group == null) return;

            var ordered = group.OrderBy(m => m.MapId).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var match = Managers.MapLookup.NextRelative(ordered[i].MapId);
                long expected = ordered[(i + 1) % ordered.Count].MapId;
                if (match == null || match.Map.MapId != expected ||
                    match.Candidates != ordered.Count ||
                    match.Wrapped != (i == ordered.Count - 1))
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] Relative map cycle is not stable.");
            }
        }

        private sealed class OverlapDetectingStream : Stream
        {
            private int _activeWrites;
            public bool OverlapDetected { get; private set; }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async Task WriteAsync(byte[] buffer, int offset, int count,
                                                  CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _activeWrites) > 1) OverlapDetected = true;
                try
                {
                    await Task.Delay(10, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeWrites);
                }
            }
        }
    }
}
