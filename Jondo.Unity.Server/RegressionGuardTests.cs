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
            Network.ClientLaunchRegistry.AssertTokenScopes();
            AssertPerSessionPlayerCaches();
            AssertCharacterSlotAllowance();
            AssertFreshCharacterBaseline();
            AssertCharacterCombatBases();
            AssertEquipmentBonusesAndMountSlot();
            AssertSpellBarDragMoves();
            AssertHousePurchaseContextIsolation();
            AssertHousePurchaseSafetyRules();
            AssertSocketWritesAreSerialized();
            AssertProfessionCatalog();
            AssertRelativeMapLookup();
            AssertInteractiveRegistry();
            AssertPublicControlBoundary();

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

        private static void AssertHousePurchaseContextIsolation()
        {
            var first = Network.GameSession.SinSocket();
            var second = Network.GameSession.SinSocket();
            first.State.MapId = 100;
            second.State.MapId = 100;
            first.State.PendingHousePurchase = new PendingHousePurchaseContext
            {
                HouseId = 7,
                MapId = 100,
                ElementId = 8,
                ExpectedPrice = 9,
                AccountId = 10,
                CharacterId = 11,
            };

            if (second.State.PendingHousePurchase != null)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] House purchase context leaked between sessions.");

            first.State.MapId = 101;
            if (first.State.PendingHousePurchase != null)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] A map change did not clear the pending house offer.");
        }

        private static void AssertHousePurchaseSafetyRules()
        {
            var firstHand = new Managers.HouseDefinition { Price = 1_000_000 };
            var secondHand = new Managers.HouseDefinition
            {
                OwnerAccountId = 99,
                Listed = true,
                Price = 2_000_000,
            };
            if (!Managers.HouseManager.CanPurchaseFirstHand(firstHand, accountId: 10) ||
                Managers.HouseManager.CanPurchaseFirstHand(secondHand, accountId: 10))
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] An owned house could bypass the unsupported " +
                    "second-hand payout boundary.");
            }

            const int door = 100;
            if (!Managers.HouseManager.IsWithinInteractionRange(door, door) ||
                !Managers.HouseManager.IsWithinInteractionRange(door + 1, door) ||
                !Managers.HouseManager.IsWithinInteractionRange(door + 14, door) ||
                Managers.HouseManager.IsWithinInteractionRange(door + 2, door) ||
                Managers.HouseManager.IsWithinInteractionRange(-1, door))
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] House interaction proximity accepts a remote or " +
                    "rejects an adjacent roleplay cell.");
            }
        }

        private static void AssertPublicControlBoundary()
        {
            var loopback = System.Net.IPAddress.Loopback;
            var remote = System.Net.IPAddress.Parse("10.20.30.40");

            if (!Network.ServerBinding.MayUseControlApi(loopback, publicMode: true,
                                                        allowInsecureRemoteControl: false) ||
                Network.ServerBinding.MayUseControlApi(remote, publicMode: true,
                                                       allowInsecureRemoteControl: false) ||
                !Network.ServerBinding.MayUseControlApi(remote, publicMode: true,
                                                        allowInsecureRemoteControl: true))
            {
                throw new InvalidOperationException("Public launcher-control access boundary regressed.");
            }

            string forwarded = Network.ServerBinding.ControlClientAddress(
                loopback, "203.0.113.9, 127.0.0.1");
            string spoofed = Network.ServerBinding.ControlClientAddress(remote, "203.0.113.9");
            if (forwarded != "203.0.113.9" || spoofed != remote.ToString())
            {
                throw new InvalidOperationException("Trusted proxy address handling regressed.");
            }
        }

        private static void AssertCharacterSlotAllowance()
        {
            int limit = Network.ConnectionProtocol.MaxCharactersPerServer;
            if (limit <= 1 || !Handlers.CharacterCreationHandler.HasAvailableCharacterSlot(0) ||
                !Handlers.CharacterCreationHandler.HasAvailableCharacterSlot(limit - 1) ||
                Handlers.CharacterCreationHandler.HasAvailableCharacterSlot(limit) ||
                Handlers.CharacterCreationHandler.HasAvailableCharacterSlot(-1))
            {
                throw new InvalidOperationException("[RegressionGuard FAILED] Character creation slot allowance is invalid.");
            }
        }

        private static void AssertFreshCharacterBaseline()
        {
            if (Handlers.CharacterCreationHandler.StartingMap != DatabaseManager.StartingMap ||
                Handlers.CharacterCreationHandler.StartingCell != DatabaseManager.StartingCell ||
                Handlers.CharacterCreationHandler.StartingLevel != 1 ||
                Handlers.CharacterCreationHandler.StartingKamas != 0 ||
                Handlers.CharacterCreationHandler.StartingStat != 0)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Fresh characters no longer start as clean Incarnam tutorial characters.");
            }
        }

        private static void AssertCharacterCombatBases()
        {
            var session = Network.GameSession.SinSocket();
            session.State.BaseActionPoints = 8;
            session.State.BaseMovementPoints = 5;
            session.State.CharacterLevel = 1;

            using (Network.SessionContext.Push(session))
            {
                if (Handlers.StatsHandler.PlayerBaseAp != 8 || Handlers.StatsHandler.PlayerBaseMp != 5 ||
                    Handlers.StatsHandler.GetPlayerMaxAp() != 8 || Handlers.StatsHandler.GetPlayerMaxMp() != 5)
                {
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] The character's saved PA/PM bases are not used by its stat sheet.");
                }

                session.State.CharacterLevel = Handlers.StatsHandler.LevelForSeventhAp;
                if (Handlers.StatsHandler.GetPlayerMaxAp() != 9)
                {
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] The level-based PA bonus no longer layers over the saved base.");
                }
            }
        }

        private static void AssertEquipmentBonusesAndMountSlot()
        {
            var session = Network.GameSession.SinSocket();
            using (Network.SessionContext.Push(session))
            {
                session.State.EquipmentItems[1] = new Managers.Equipment.Item
                {
                    Uid = 1, Template = 100, Position = 6,
                };
                session.State.EquipmentItems[1].Effects.Add(
                    new Managers.Equipment.ItemEffect(118, 42, 0, 0));

                session.State.EquipmentItems[2] = new Managers.Equipment.Item
                {
                    Uid = 2, Template = 19291, Position = 8,
                };

                var bonuses = Managers.Equipment.Bonuses();
                if (!bonuses.TryGetValue(Network.ConnectionProtocol.Stat.Strength, out long strength) ||
                    strength != 42 || Managers.Equipment.Worn != 2)
                {
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] Worn item effects or mount slot no longer participate in equipment state.");
                }
            }
        }

        private static void AssertSpellBarDragMoves()
        {
            var session = Network.GameSession.SinSocket();
            session.State.SpellBar[6] = 6006;
            session.State.SpellBar[9] = 9009;

            using (Network.SessionContext.Push(session))
            {
                if (!Managers.SpellChoices.MoveBarSlot(6, 9) ||
                    session.State.SpellBar[6] != 9009 || session.State.SpellBar[9] != 6006 ||
                    !session.State.SpellBarInitialized)
                {
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] Dragging onto an occupied spell shortcut did not swap slots.");
                }

                if (!Managers.SpellChoices.MoveBarSlot(6, 7) ||
                    session.State.SpellBar.ContainsKey(6) || session.State.SpellBar[7] != 9009)
                {
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] Dragging onto an empty spell shortcut did not move its slot.");
                }
            }
        }

        private static void AssertInteractiveRegistry()
        {
            // ImportOfficialTemplates upserts every one of the 261 pinned client models.  The
            // table may also contain administrator-defined models, which are legitimate and
            // must not turn a successful import into a startup failure.
            if (Managers.HouseManager.TemplateCount < 261)
            {
                throw new InvalidDataException(
                    $"[RegressionGuard FAILED] Expected at least 261 client house templates, got " +
                    $"{Managers.HouseManager.TemplateCount}.");
            }

            var expected = new HashSet<(long MapId, int ElementId)>();
            foreach (long mapId in Managers.Interactives.MapIds)
            {
                foreach (var zaap in Managers.Interactives.ZaapElements(mapId))
                    expected.Add((mapId, zaap.Id));

                var chest = Managers.Merkasako.ChestOf(mapId);
                if (chest.Id != 0) expected.Add((mapId, chest.Id));

                var lottery = Managers.Lottery.Of(mapId);
                if (lottery.Id != 0) expected.Add((mapId, lottery.Id));

                foreach (var zaapi in Managers.Zaapis.ElementsOn(mapId))
                    expected.Add((mapId, zaapi.Id));

                foreach (var bin in Managers.Bins.On(mapId))
                    expected.Add((mapId, bin.Id));

                foreach (var door in Managers.Houses.On(mapId))
                {
                    expected.Add((mapId, door.ElementId));
                    if (!Managers.HouseManager.TryGetByDoor(mapId, door.ElementId, out var house) ||
                        house == null || house.InteriorMapId != door.InteriorMapId ||
                        house.HouseTypeId <= 0 || house.Price <= 0 ||
                        !Managers.HouseManager.TryGetTemplate(house.HouseTypeId, out _))
                    {
                        throw new InvalidDataException(
                            $"[RegressionGuard FAILED] House door {mapId}/{door.ElementId} has no " +
                            "matching priced persistent instance with a valid client model.");
                    }
                }

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

            foreach (long interior in Managers.Houses.Interiors)
            {
                if (Managers.Houses.TryGetExit(interior, out var exit))
                    expected.Add((interior, exit.ElementId));

                foreach (var interactive in Managers.InteractiveRegistry.OnMap(interior))
                {
                    foreach (var action in interactive.Actions)
                    {
                        if (!Managers.InteractiveRegistry.TryResolveUse(
                                interior, interactive.Element.Id, action.SkillInstanceId,
                                out var resolved, out var resolvedAction) ||
                            !ReferenceEquals(interactive, resolved) ||
                            !ReferenceEquals(action, resolvedAction))
                        {
                            throw new InvalidOperationException(
                                "[RegressionGuard FAILED] House exit cannot resolve its declaration.");
                        }
                    }
                }
            }

            int graphRoutesClaimedBySpecializedHandlers = 0;
            foreach (var route in Managers.WorldInteractiveTransitions.All)
            {
                var live = Managers.Interactives.ByElementId(route.MapId, route.Element.Id);
                if (live.Id != route.Element.Id || live.Cell != route.Element.Cell ||
                    live.Gfx != route.Element.Gfx || route.Sources.Count == 0 ||
                    !Managers.WorldInteractiveTransitions.TryGet(
                        route.MapId, route.Element.Id, out var resolvedRoute) ||
                    !ReferenceEquals(route, resolvedRoute))
                {
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] World-graph transition is not joined to its " +
                        "exact live map element.");
                }

                if (!expected.Add((route.MapId, route.Element.Id)))
                    graphRoutesClaimedBySpecializedHandlers++;
            }

            if (Managers.InteractiveRegistry.WorldTransitionCount !=
                    Managers.WorldInteractiveTransitions.Count -
                    graphRoutesClaimedBySpecializedHandlers ||
                Managers.InteractiveRegistry.SkippedClaimedWorldTransitionCount !=
                    graphRoutesClaimedBySpecializedHandlers)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] World-graph transitions replaced or bypassed a " +
                    "specialized interactive handler.");
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
            // Los dumps de dofusdude son OPCIONALES a propósito: quien no los haya puesto en
            // datos/JsonFromDofusDude/ arranca sin oficios —el propio importador lo permite y lo
            // dice en el arranque—, así que aquí no hay catálogo que comprobar y no es ningún
            // fallo. Comprobarlo igual y reventar el arranque era lo que mataba al servidor de
            // entrada: la guardia exigía como obligatorios los datos que su propia importación
            // declara opcionales.
            //
            // Con los dumps puestos, todo lo de abajo se exige igual: ahí un catálogo vacío o
            // incoherente sí es una regresión de verdad.
            if (!File.Exists(Paths.SkillsJson) || !File.Exists(Paths.RecipesJson))
            {
                Console.WriteLine("[RegressionGuard] Sin dumps de oficios en datos/JsonFromDofusDude; " +
                                  "se omite la comprobación del catálogo (arranque sin oficios).");
                return;
            }

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
