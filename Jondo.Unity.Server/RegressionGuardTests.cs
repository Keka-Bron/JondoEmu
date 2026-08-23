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

                // Los zaapis y las papeleras se reconocen por su gráfico y son decenas, así que van
                // en bloque. Sin esto la cuenta no cuadra y el servidor no arranca — que es lo que
                // pasó al añadirlos: el guardia los vio antes que nadie.
                foreach (var zaapi in Managers.Zaapis.ElementsOn(mapId))
                    expected.Add((mapId, zaapi.Id));

                foreach (var bin in Managers.Bins.On(mapId))
                    expected.Add((mapId, bin.Id));

                foreach (var door in Managers.Houses.On(mapId))
                    expected.Add((mapId, door.ElementId));

                foreach (var teleport in Managers.TeleportManager.On(mapId))
                {
                    expected.Add((mapId, teleport.ElementId));
                    if (Managers.Houses.TryGetDoor(mapId, teleport.ElementId, out _))
                        throw new InvalidOperationException(
                            "[RegressionGuard FAILED] A generic teleport overlaps a house door.");
                }

                foreach (var resource in Managers.Resources.On(mapId))
                    expected.Add((mapId, resource.ElementId));

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

            // Los interiores de casa no estan en Interactives.MapIds -son mapas sin elementos
            // en el volcado del cliente- asi que se cuentan aparte.
            foreach (long interior in Managers.Houses.Interiors)
            {
                if (Managers.Houses.TryGetExit(interior, out var exit))
                    expected.Add((interior, exit.ElementId));
            }

            // Invariant Giny : chaque Teleport/ElementId/USE114 est exposé au clic et la même
            // route est retrouvable sur sa cellule pour le déclenchement de fin de mouvement/JQI.
            foreach (var teleport in Managers.TeleportManager.All)
            {
                if (!Managers.InteractiveRegistry.TryResolveUse(
                        teleport.SourceMapId, teleport.ElementId,
                        Managers.Interactives.SkillInstanceOf(teleport.ElementId),
                        out var interactive, out var action) ||
                    interactive.Element.Id != teleport.ElementId ||
                    interactive.Element.Cell != teleport.SourceCellId ||
                    interactive.Element.Gfx != teleport.GfxId ||
                    interactive.Type != Managers.TeleportManager.GenericTeleportType ||
                    teleport.InteractiveType != Managers.TeleportManager.GenericTeleportType ||
                    action.Kind != Managers.InteractiveActionKind.Teleport ||
                    action.SkillId != 114 || teleport.SkillId != 114 ||
                    !Managers.TeleportManager.TryGetCellTrigger(
                        teleport.SourceMapId, teleport.SourceCellId, out var cellRoute) ||
                    !ReferenceEquals(teleport, cellRoute))
                {
                    throw new InvalidOperationException(
                        $"[RegressionGuard FAILED] Teleport ElementId {teleport.SourceMapId}/" +
                        $"{teleport.ElementId} is not a clickable f11/f15 interactive.");
                }
            }

            if (!Managers.TeleportManager.TryGet(191106048, 515837, out var astrub) ||
                astrub.DestinationMapId != 192416776 || astrub.DestinationCellId != 534)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Astrub/Temple teleport route is missing or changed.");
            }

            if (!Managers.TeleportManager.TryGet(188746247, 515801, out var jewellerEntrance) ||
                jewellerEntrance.DestinationMapId != 192937990 ||
                jewellerEntrance.DestinationCellId != 400 ||
                !Managers.TeleportManager.TryGet(192937990, 515742, out var jewellerExit) ||
                jewellerExit.DestinationMapId != 188746247 ||
                jewellerExit.SourceCellId != 414 || jewellerExit.DestinationCellId != 358 ||
                !Managers.TeleportManager.TryGetCellTrigger(192937990, 414, out var cellExit) ||
                !ReferenceEquals(jewellerExit, cellExit) ||
                !Managers.InteractiveRegistry.TryResolveUse(
                    192937990, 515742, Managers.Interactives.SkillInstanceOf(515742),
                    out var jewellerInteractive, out var jewellerAction) ||
                jewellerInteractive.Element.Cell != 414 || jewellerInteractive.Element.Gfx != 3520 ||
                jewellerInteractive.Type != Managers.TeleportManager.GenericTeleportType ||
                jewellerAction.Kind != Managers.InteractiveActionKind.Teleport ||
                jewellerAction.SkillId != 114)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Astrub jeweller workshop round trip is missing or changed.");
            }

            // L'escalier gfx 62018 suit exactement la même route générique que le soleil : clic
            // par ElementId et fin de mouvement/JQI par SourceCellId, tous deux avec USE114.
            if (!Managers.TeleportManager.TryGet(192940038, 515691, out var stairExit) ||
                stairExit.SourceCellId != 327 || stairExit.DestinationMapId != 188744711 ||
                stairExit.DestinationCellId != 427 || stairExit.GfxId != 62018 ||
                stairExit.InteractiveType != Managers.TeleportManager.GenericTeleportType ||
                stairExit.SkillId != 114 ||
                !Managers.TeleportManager.TryGetCellTrigger(192940038, 327, out var stairCellRoute) ||
                !ReferenceEquals(stairExit, stairCellRoute) ||
                !Managers.InteractiveRegistry.TryResolveUse(
                    192940038, 515691, Managers.Interactives.SkillInstanceOf(515691),
                    out var stairInteractive, out var stairAction) ||
                stairInteractive.Element.Gfx != 62018 || stairInteractive.Element.Cell != 327 ||
                stairInteractive.Type != Managers.TeleportManager.GenericTeleportType ||
                stairAction.Kind != Managers.InteractiveActionKind.Teleport ||
                stairAction.SkillId != 114)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Stair 192940038/515691 must be a clickable teleport.");
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

            // El catalogo tiene que traer habilidades de recoleccion y el mundo tiene que tener
            // recursos con esas habilidades. Antes esto llamaba a GatheringHandler.TryResolve,
            // que solo cruzaba tablas en memoria y no mandaba un byte; ahora el handler recolecta
            // de verdad y lo que hay que comprobar es que haya algo que recolectar.
            var gathering = Managers.SkillManager.All.FirstOrDefault(s => s.IsGathering);
            if (gathering == null)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] El catalogo no trae ninguna habilidad de recoleccion.");

            if (Managers.Resources.Count == 0)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] No hay ningun recurso recolectable en el mundo.");

            // Y la cantidad tiene que respetar lo medido: un oficio al tope sobre el recurso mas
            // facil da veinte, y uno recien empezado da cuatro.
            if (Handlers.GatheringHandler.Ceiling(200, 1) != 20 ||
                Handlers.GatheringHandler.Ceiling(200, 80) != 13 ||
                Handlers.GatheringHandler.Ceiling(1, 1) != 4)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La cantidad recolectada no cuadra con las capturas.");

            // Y la curva de experiencia, con los tres puntos que dan las capturas.
            if (Managers.JobExperience.Floor(2) != 20 || Managers.JobExperience.Floor(3) != 60 ||
                Managers.JobExperience.Floor(200) != 398000 ||
                Managers.JobExperience.LevelOf(20) != 2 || Managers.JobExperience.LevelOf(19) != 1)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La curva de experiencia de oficio no cuadra.");

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
