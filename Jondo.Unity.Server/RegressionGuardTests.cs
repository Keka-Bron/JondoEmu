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
            AssertZaapDiscoveryFiltering();
            AssertCharacterCombatBases();
            AssertShieldCombatSemantics();
            AssertEquipmentBonusesAndMountSlot();
            AssertSpellBarDragMoves();
            AssertHousePurchaseContextIsolation();
            AssertHousePurchaseSafetyRules();
            AssertSocketWritesAreSerialized();
            AssertProfessionCatalog();
            AssertRelativeMapLookup();
            AssertInteractiveRegistry();
            AssertWorldTransitionArrivalSafety();
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

        private static void AssertZaapDiscoveryFiltering()
        {
            byte[] capturedKnownList = Network.ConnectionProtocol.Push(
                Jondo.Unity.Protocol.Op.Hjk,
                Network.ConnectionProtocol.BuildDiscoveredZaaps(new[] { 154010371L }));
            if (!Network.WorldEntry.ShouldSkip(capturedKnownList))
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Captured-account hjk would leak into world entry.");
            }

            if (Managers.ZaapDiscovery.IsDiscoverableMap(DatabaseManager.StartingMap))
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Fresh-character creation map would auto-unlock a zaap.");
            }

            var discoverable = Managers.Interactives.Waypoints.FirstOrDefault(waypoint =>
                Managers.ZaapDiscovery.IsDiscoverableMap(waypoint.MapId));
            if (discoverable == null)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] No evidence-backed ordinary zaap can be discovered.");
            }

            var empty = Handlers.ZaapTravelHandler.OrdinaryDestinations(
                discoverable.MapId, Array.Empty<long>());
            var one = Handlers.ZaapTravelHandler.OrdinaryDestinations(
                discoverable.MapId, new[] { discoverable.MapId });
            var duplicate = Handlers.ZaapTravelHandler.OrdinaryDestinations(
                discoverable.MapId, new[] { discoverable.MapId, discoverable.MapId });

            if (empty.Count != 0 || one.Count != 1 || duplicate.Count != 1 ||
                one[0].MapId != discoverable.MapId || one[0].Cost != 0)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Zaap destinations are not strictly filtered by " +
                    "character-owned discovery state.");
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

        private static void AssertShieldCombatSemantics()
        {
            // Effect 1020 is a percentage of the caster's level. Keep an odd level here so a
            // change from integer-floor semantics cannot pass unnoticed.
            if (Managers.EffectEngine.PuntosDeEscudo(50, 133) != 66 ||
                Managers.EffectEngine.PuntosDeEscudo(0, 133) != 0 ||
                Managers.EffectEngine.PuntosDeEscudo(50, 0) != 0)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Effect 1020 no longer uses floor(percent * caster level).");
            }

            var buffs = new Jondo.Unity.World.Fights.Buffs();
            int siguiente = 0;
            Func<int> numero = () => ++siguiente;
            var primero = buffs.Poner(new Jondo.Unity.World.Fights.Buff
            {
                EffectId = Jondo.Unity.World.Fights.Buffs.EscudoPorNivel,
                EffectUid = 1,
                Cuanto = 66,
                HechizoOrigen = 14676,
                Quien = 10,
                CaducaEnRonda = 3,
            }, numero);
            var refrescado = buffs.Poner(new Jondo.Unity.World.Fights.Buff
            {
                EffectId = Jondo.Unity.World.Fights.Buffs.EscudoPorNivel,
                EffectUid = 1,
                Cuanto = 67,
                HechizoOrigen = 14676,
                Quien = 10,
                CaducaEnRonda = 4,
            }, numero);
            var segundo = buffs.Poner(new Jondo.Unity.World.Fights.Buff
            {
                EffectId = Jondo.Unity.World.Fights.Buffs.EscudoPorNivel,
                EffectUid = 2,
                Cuanto = 30,
                HechizoOrigen = 99999,
                Quien = 10,
                CaducaEnRonda = 4,
            }, numero);

            int absorbido = buffs.AbsorberEscudo(80, ronda: 1);
            if (!ReferenceEquals(primero, refrescado) || siguiente != 2 ||
                primero.Numero != 1 || segundo.Numero != 2 ||
                absorbido != 80 || primero.Cuanto != 0 || segundo.Cuanto != 17 ||
                buffs.EscudoDisponible(1) != 17 || buffs.Puestos.Count != 2)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Shields do not refresh, stack or absorb oldest-first.");
            }

            // Installed 3.6.10.10 native receiver fmc::bgmn reads jvp as: optional f1 shield
            // loss, f2 victim, f3 life loss, f4 element, f5 permanent/erosion loss.
            var damage = Network.ProtoMessage.Parse(Network.FightProtocol.BuildDamage(
                author: 10, efecto: 96, victim: 20, amount: 123, elemento: 3,
                shieldLoss: 45, permanentDamage: 12));
            var detailField = damage.Fields.SingleOrDefault(field =>
                field.FieldNumber == 40 && field.WireType == 2);
            var detail = detailField == null ? null : Network.ProtoMessage.Parse(detailField.BytesValue);
            if (detail == null ||
                detail.Fields.SingleOrDefault(field => field.FieldNumber == 1)?.VarIntValue != 45 ||
                detail.Fields.SingleOrDefault(field => field.FieldNumber == 2)?.VarIntValue != 20 ||
                detail.Fields.SingleOrDefault(field => field.FieldNumber == 3)?.VarIntValue != 123 ||
                detail.Fields.SingleOrDefault(field => field.FieldNumber == 4)?.VarIntValue != 3 ||
                detail.Fields.SingleOrDefault(field => field.FieldNumber == 5)?.VarIntValue != 12)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] jvp shield/life/element/permanent wire fields drifted.");
            }

            var withoutOptional = Network.ProtoMessage.Parse(Network.FightProtocol.BuildDamage(
                author: 10, efecto: 96, victim: 20, amount: 1, elemento: 3));
            var withoutDetailField = withoutOptional.Fields.Single(field =>
                field.FieldNumber == 40 && field.WireType == 2);
            var withoutDetail = Network.ProtoMessage.Parse(withoutDetailField.BytesValue);
            if (withoutDetail.Fields.Any(field => field.FieldNumber == 1 || field.FieldNumber == 5))
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Empty optional jvp shield/permanent fields were serialized.");
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

                // The client item-move schema uses int32 ids.  Existing seeded rows can have
                // 64-bit ids, so the low wire id must still resolve back to the saved item.
                const long persistedUid = 13825560052L;
                const long wireUid = 940658164L;
                session.State.EquipmentItems[persistedUid] = new Managers.Equipment.Item
                {
                    Uid = persistedUid, Template = 123, Position = Managers.Equipment.Bag,
                };
                if (Managers.Equipment.WireUid(persistedUid) != wireUid ||
                    Managers.Equipment.ByWireUid(wireUid)?.Uid != persistedUid)
                {
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] A persisted item id no longer resolves from the int32 client wire id.");
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

            var reviewedNpcSpawns = new[]
            {
                (Source: 20, Npc: 2907, Map: 153881600L, Cell: 384, Direction: 3),
                (Source: 21, Npc: 2936, Map: 152835072L, Cell: 262, Direction: 3),
                (Source: 43, Npc: 2892, Map: 154010883L, Cell: 357, Direction: 3),
                (Source: 44, Npc: 2885, Map: 153357316L, Cell: 205, Direction: 3),
            };
            bool exactNpcSpawns = Managers.IncarnamServerContent.NpcSpawns.Count == reviewedNpcSpawns.Length &&
                reviewedNpcSpawns.All(expectedSpawn =>
                    Managers.IncarnamServerContent.NpcSpawns.Any(actualSpawn =>
                        actualSpawn.SourceRecordId == expectedSpawn.Source &&
                        actualSpawn.NpcId == expectedSpawn.Npc &&
                        actualSpawn.MapId == expectedSpawn.Map &&
                        actualSpawn.CellId == expectedSpawn.Cell &&
                        actualSpawn.Orientation == expectedSpawn.Direction));
            bool inventedGanymed = Managers.IncarnamServerContent.NpcSpawns.Any(spawn =>
                spawn.SourceRecordId == 42 ||
                (spawn.NpcId == 7581 && spawn.MapId == 153880835 && spawn.CellId == 215));
            if (Managers.IncarnamServerContent.Workshops.Count != 30 ||
                !exactNpcSpawns || inventedGanymed)
            {
                throw new InvalidDataException(
                    "[RegressionGuard FAILED] Expected 30 reviewed Incarnam workshop bindings " +
                    "and exactly 4 source/client-compatible NPC placements; source row 42 must " +
                    "remain rejected instead of being substituted with Ganymed.");
            }

            var farmerStations = Managers.IncarnamServerContent.Workshops
                .Where(station => station.MapId == 153354242)
                .ToList();
            var farmerMill = farmerStations.SingleOrDefault(station =>
                station.Element.Id == 489524 && station.Element.Cell == 285 &&
                station.Element.Gfx == 32991 && station.InteractiveTypeId == 22 &&
                station.SkillId == 27 && station.RecipeCount == 70);
            if (farmerStations.Count != 4 || farmerMill == null)
            {
                throw new InvalidDataException(
                    "[RegressionGuard FAILED] Incarnam farmer workshop bindings are incomplete.");
            }

            foreach (var station in Managers.IncarnamServerContent.Workshops)
                expected.Add((station.MapId, station.Element.Id));

            // The visible Incarnam waypoint is an inline map bounding-box target.  The pinned
            // bundle and official iwo capture identify element 538795 on cell 304 with the normal
            // zaap graphic 301199.  Element 490237/gfx 3212 is unrelated animated scenery.
            var incarnamZaap = Managers.Interactives.ZaapElements(154010371);
            if (incarnamZaap.Count != 1 || incarnamZaap[0].Id != 538795 ||
                incarnamZaap[0].Cell != 304 || incarnamZaap[0].Gfx != 301199)
            {
                throw new InvalidDataException(
                    "[RegressionGuard FAILED] Incarnam zaap is not bound to map element " +
                    "538795 (cell 304, gfx 301199).");
            }

            // The native lev writer/parser (and the official capture retained in the migration
            // notes) put disabled actions in repeated f3, enabled actions in repeated f4, the
            // element id in f5 and the type in f6. The extracted proto was shifted by the optional
            // f2 Has-property; trusting it made the portal visible but deliberately non-clickable.
            var wireCharacter = new DatabaseManager.DbCharacter
            {
                Id = 1,
                Name = "InteractiveWireGuard",
                Breed = 9,
                Sex = 0,
                Level = 1,
                ServerId = DatabaseManager.DefaultServerId,
            };

            // MapInfoUI resolves this exact field through the installed client's SubAreasDataRoot.
            // A legacy 444 -> 20663 rewrite made every Atelier transition feed it an id that does
            // not exist, so the UI threw before updating the coordinates and minimap marker.
            foreach (var expectedSubArea in new[]
            {
                (MapId: 154010371L, SubAreaId: 450L),
                (MapId: 154010372L, SubAreaId: 444L),
                (MapId: 153354242L, SubAreaId: 444L),
            })
            {
                var mapInfo = MapManager.GetMapInfo(expectedSubArea.MapId);
                var mapJss = Network.ProtoMessage.Parse(Network.ConnectionProtocol.BuildMapActors(
                    expectedSubArea.MapId, wireCharacter, cell: 300, facing: 1, accountId: 1));
                long wireSubArea = mapJss.Fields.SingleOrDefault(field =>
                    field.FieldNumber == 6 && field.WireType == 0)?.VarIntValue ?? 0;
                if (mapInfo?.SubAreaId != expectedSubArea.SubAreaId ||
                    wireSubArea != expectedSubArea.SubAreaId)
                {
                    throw new InvalidDataException(
                        $"[RegressionGuard FAILED] Map {expectedSubArea.MapId} must send pinned " +
                        $"subarea {expectedSubArea.SubAreaId}, got {wireSubArea}.");
                }
            }

            var incarnamJss = Network.ProtoMessage.Parse(Network.ConnectionProtocol.BuildMapActors(
                154010371, wireCharacter, cell: 300, facing: 1, accountId: 1));
            var declaration = incarnamJss.Fields
                .Where(field => field.FieldNumber == 11 && field.WireType == 2)
                .Select(field => Network.ProtoMessage.Parse(field.BytesValue))
                .SingleOrDefault(message => message.Fields.Any(field =>
                    field.FieldNumber == 5 && field.WireType == 0 &&
                    field.VarIntValue == incarnamZaap[0].Id));
            var state = incarnamJss.Fields
                .Where(field => field.FieldNumber == 15 && field.WireType == 2)
                .Select(field => Network.ProtoMessage.Parse(field.BytesValue))
                .SingleOrDefault(message => message.Fields.Any(field =>
                    field.FieldNumber == 3 && field.WireType == 0 &&
                    field.VarIntValue == incarnamZaap[0].Id));
            var enabledAction = declaration?.Fields
                .Where(field => field.FieldNumber == 4 && field.WireType == 2)
                .Select(field => Network.ProtoMessage.Parse(field.BytesValue))
                .SingleOrDefault();

            if (declaration == null ||
                declaration.Fields.Any(field => field.FieldNumber == 3 && field.WireType == 2) ||
                declaration.Fields.SingleOrDefault(field => field.FieldNumber == 6 && field.WireType == 0)
                    ?.VarIntValue != Managers.Interactives.ZaapType ||
                enabledAction == null ||
                enabledAction.Fields.SingleOrDefault(field => field.FieldNumber == 1 && field.WireType == 0)
                    ?.VarIntValue != Managers.Interactives.SkillInstanceOf(incarnamZaap[0].Id) ||
                enabledAction.Fields.SingleOrDefault(field => field.FieldNumber == 2 && field.WireType == 0)
                    ?.VarIntValue != Managers.Interactives.UseSkill ||
                state == null ||
                state.Fields.SingleOrDefault(field => field.FieldNumber == 1 && field.WireType == 0)
                    ?.VarIntValue != 1 ||
                state.Fields.SingleOrDefault(field => field.FieldNumber == 2 && field.WireType == 0)
                    ?.VarIntValue != incarnamZaap[0].Cell)
            {
                throw new InvalidDataException(
                    "[RegressionGuard FAILED] Incarnam jss does not match the native lev/ldf " +
                    "interactive wire shape (enabled actions f4, element f5, state f15).");
            }

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

        private static void AssertWorldTransitionArrivalSafety()
        {
            // Installed 3.6.10.10 client data maps this Route des Ames entrance to reciprocal
            // source 411. That is the authored exit trigger, so arriving on it regresses into an
            // immediate return outdoors on the first movement request.
            const long interiorMapId = 153357316;
            const int reciprocalDoorCell = 411;
            if (!Handlers.WorldInteractiveTransitionHandler.TryNearestSafeWalkable(
                    interiorMapId, reciprocalDoorCell, 2, out int arrival) ||
                Jondo.Unity.World.Maps.MapGeometry.Distance(arrival, reciprocalDoorCell) < 2)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Interior transition arrival is still inside the exit trigger radius.");
            }

            // The Incarnam farmer workshop's exit drawing is anchored at 424, but the live client
            // walks the actor to 411 for the exit action. Oven use ends at 299. Only the reviewed
            // action endpoint may return to the observed, walkable pre-entry cell 288.
            if (!Managers.WorldInteractiveReturns.TryResolveDeclaredExit(
                    153354242, 411, out var atelierReturn) ||
                atelierReturn.ReturnMapId != 154010372 ||
                atelierReturn.ReturnCellId != 288 ||
                atelierReturn.EntryElementId != 489326 ||
                Managers.WorldInteractiveReturns.TryResolveDeclaredExit(
                    153354242, 299, out _) ||
                Managers.WorldInteractiveReturns.TryResolveDeclaredExit(
                    153354242, 285, out _) ||
                Managers.WorldInteractiveReturns.TryResolveDeclaredExit(
                    153354242, 258, out _))
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Farmer workshop return is not bound to its exact action cell.");
            }

            // The graph itself still records the authored reciprocal anchor. Generic graph-backed
            // returns require that exact cell and normalize the outdoor source to a walkable cell.
            if (!Managers.WorldInteractiveTransitions.TryResolveReciprocalReturn(
                    153354242, 424, out long returnMap, out int returnCell,
                    out int entryElement) ||
                returnMap != 154010372 || returnCell != 273 || entryElement != 489326 ||
                Managers.WorldInteractiveTransitions.TryResolveReciprocalReturn(
                    153354242, 285, out _, out _, out _) ||
                Managers.WorldInteractiveTransitions.TryResolveReciprocalReturn(
                    153354242, 258, out _, out _, out _) ||
                Managers.WorldInteractiveTransitions.TryResolveReciprocalReturn(
                    153354242, 300, out _, out _, out _))
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Farmer workshop graph return is ambiguous or unsafe.");
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
