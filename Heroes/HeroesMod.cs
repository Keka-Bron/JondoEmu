using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine.UIElements;

[assembly: MelonInfo(typeof(Heroes.HeroesMod), "Heroes", "1.0.0", "Jondo")]
[assembly: MelonGame("Ankama", "Dofus")]

namespace Heroes
{
    /// <summary>
    /// The hero mode's window on the client: the account's characters as cards, the one in the
    /// world -- the master -- first, then his heroes, then the others to connect beside him.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is the server's (Jondo's Managers.Heroes): it asks the control API with the
    /// account's own session token, which the launcher hands the client as JONDO_PLAYER_TOKEN.
    /// The portraits are the server's too, drawn from the client's own bones as the launcher
    /// draws its team. The heroes themselves are played in the fights by the client as it is:
    /// their bar and their turns come to it as a summon's do.
    /// </para>
    /// <para>
    /// Opened and closed with Ctrl+Tab. Built from the client's own pieces, as Cazacabezas is.
    /// </para>
    /// </remarks>
    public class HeroesMod : MelonMod
    {
        private const string Api = "http://127.0.0.1:8888/api/";
        private const int Places = 8;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly ConcurrentQueue<Action> _toUnity = new ConcurrentQueue<Action>();

        private static string _token = "";
        private static string _language;

        private static JsonElement? _list;
        private static int _listCode = -1;
        private static bool _polling;
        private static float _nextPoll, _nextScan;

        private static readonly Dictionary<long, UnityEngine.Texture2D> _portraits = new Dictionary<long, UnityEngine.Texture2D>();
        private static readonly HashSet<long> _asked = new HashSet<long>();
        private static UnityEngine.Texture2D _glow;

        private static WindowFigma _window;
        private static VisualElement _grid;
        private static DofusLabel _footer;
        private static DofusLabel _say;

        public override void OnInitializeMelon()
        {
            _token = (Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN")
                      ?? Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ?? "").Trim();
            LoggerInstance.Msg(_token.Length > 0
                ? "[Heroes] Ready: Ctrl+Tab opens the hero mode."
                : "[Heroes] No session token from the launcher: the hero mode stays asleep.");
            if (_token.Length > 0) WatchActionBar();
            if (_token.Length > 0) WatchPortraits();
            if (_token.Length > 0) InstallEscapeGuard();
        }

        public override void OnUpdate()
        {
            if (_token.Length == 0) return;
            while (_toUnity.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[Heroes] {ex.Message}"); }
            }

            long nowTicks = Environment.TickCount64;
            if (_reopenHeroUiTime > 0 && nowTicks >= _reopenHeroUiTime)
            {
                _reopenHeroUiTime = 0;
                if (_openUisToReopen.Count > 0)
                {
                    foreach (var uiType in _openUisToReopen.ToList())
                    {
                        TriggerMenuSlot(uiType);
                    }
                    _openUisToReopen.Clear();
                }
                if (_wasAppearanceOpen)
                {
                    _reopenAppearanceTime = nowTicks + 200;
                }
            }

            if (_reopenAppearanceTime > 0 && nowTicks >= _reopenAppearanceTime)
            {
                _reopenAppearanceTime = 0;
                _wasAppearanceOpen = false;
                ReopenAppearanceWindow();
            }

            // Asked all along, not only with the window open: the group panel shows the life of
            // the group, and the portraits are fetched ahead of the window's first opening.
            float now = UnityEngine.Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                try { PutMenuButton(); } catch { }
                try { KeepPanelInSight(); } catch { }
                try { BorrowServices(); } catch { }
                try { WatchLive(); } catch { }
                try { ReadTuning(); } catch { }
            }
            if (now >= _nextPoll && !_polling)
            {
                _nextPoll = now + (_window != null || _panel != null ? 3f : 8f);
                if (_listCode == 401) _nextPoll = now + 120f;
                Poll();
            }
            try { WatchPreview(); } catch { }
            if (_reframe.Count > 0) { try { Reframe(); } catch { } }
            if (_escapePressed)
            {
                _escapePressed = false;
                CloseTopmost();
            }
            if (ShortcutPressed())
            {
                if (_window != null) Close();
                else Open();
            }
            else if (NextPressed() && !Typing())
            {
                // Pressed while a switch is on its way, the presses are kept: the next switch goes
                // that many further round the group at once, so Tab can be hit as fast as one likes.
                if (_switchBusy && now - _switchSince < 8f) _pendingTabs++;
                else SwitchBy(1, now);
            }
        }

        // A switch asked with Tab on its way -- until the group's list after it is read --, since
        // when, and the presses made meanwhile.
        private static bool _switchBusy;
        private static float _switchSince;
        private static int _pendingTabs;

        /// <summary>Which reading of the list is the latest asked: one asked before a switch is the old group's, and dropped.</summary>
        private static int _listAsked;

        /// <summary>
        /// Tab: the group's next taken in hand -- <paramref name="steps"/> further round it, in its
        /// order, round to the first after the last; whoever is fighting is passed over.
        /// </summary>
        private static void SwitchBy(int steps, float now)
        {
            if (!(_list is JsonElement list) || !Bool(list, "on") || !list.TryGetProperty("characters", out var all)) return;
            var group = all.EnumerateArray().Where(c => Bool(c, "master") || Bool(c, "hero")).ToList();
            int me = group.FindIndex(c => Bool(c, "master"));
            if (group.Count < 2 || me < 0 || Bool(group[me], "inFight")) return;
            steps %= group.Count;
            if (steps == 0) return;
            for (int more = 0; more < group.Count; more++)
            {
                int at = (me + steps + more) % group.Count;
                if (at == me) return;
                var next = group[at];
                if (Bool(next, "inFight")) continue;
                _switchBusy = true;
                _switchSince = now;
                TakeInHand(Long(next, "id"), Str(next, "name"), null);
                return;
            }
        }

        /// <summary>The group's list read after a switch: the presses kept meanwhile are played.</summary>
        private static void SwitchDone()
        {
            _switchBusy = false;
            int kept = _pendingTabs;
            _pendingTabs = 0;
            if (kept > 0) SwitchBy(kept, UnityEngine.Time.unscaledTime);
        }

        // ─── The words ───────────────────────────────────────────────────────────────────

        private static string Language
        {
            get
            {
                if (_language != null) return _language;
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (string.Equals(args[i], "--langCode", StringComparison.OrdinalIgnoreCase))
                        return _language = args[i + 1].Trim().ToLowerInvariant();
                return _language = "es";
            }
        }

        private static Dictionary<string, string> L(string es, string en, string fr)
            => new Dictionary<string, string> { ["es"] = es, ["en"] = en, ["fr"] = fr };

        private static readonly Dictionary<string, Dictionary<string, string>> Texts = new Dictionary<string, Dictionary<string, string>>
        {
            ["title"] = L("Modo Héroe", "Hero mode", "Mode Héros"),
            ["level"] = L("Niv. {0}", "Lvl. {0}", "Niv. {0}"),
            ["connected"] = L("{0} / {1} HÉROES CONECTADOS", "{0} / {1} HEROES CONNECTED", "{0} / {1} HÉROS CONNECTÉS"),
            ["connect"] = L("CONECTAR", "CONNECT", "CONNECTER"),
            ["create"] = L("CREAR UN HÉROE", "CREATE A HERO", "CRÉER UN HÉROS"),
            ["create.title"] = L("Nuevo héroe", "New hero", "Nouveau héros"),
            ["create.window"] = L("Crear un personaje", "Create a character", "Créer un personnage"),
            ["create.face"] = L("ROSTRO", "FACE", "VISAGE"),
            ["create.class"] = L("CLASE", "CLASS", "CLASSE"),
            ["create.sex"] = L("SEXO", "SEX", "SEXE"),
            ["create.male"] = L("Masculino", "Male", "Masculin"),
            ["create.female"] = L("Femenino", "Female", "Féminin"),
            ["create.name"] = L("NOMBRE", "NAME", "NOM"),
            ["create.namePlaceholder"] = L("Nombre del héroe", "The hero's name", "Nom du héros"),
            ["create.random"] = L("AL AZAR", "RANDOM", "AU HASARD"),
            ["create.cancel"] = L("CANCELAR", "CANCEL", "ANNULER"),
            ["create.go"] = L("CREAR", "CREATE", "CRÉER"),
            ["create.working"] = L("Creando…", "Creating…", "Création…"),
            ["create.done"] = L("{0} se une al grupo.", "{0} joins the group.", "{0} rejoint le groupe."),
            ["create.doneAlone"] = L("{0} ha sido creado.", "{0} has been created.", "{0} a été créé."),
            ["create.error.empty"] = L("Escribe un nombre.", "Write a name.", "Écris un nom."),
            ["create.error.name"] = L("Ese nombre no sigue las reglas: una mayúscula, letras, hasta dos guiones.",
                                      "That name breaks the rules: a capital, letters, up to two hyphens.",
                                      "Ce nom ne respecte pas les règles : une majuscule, des lettres, deux tirets au plus."),
            ["create.error.taken"] = L("Ese nombre ya está cogido.", "That name is already taken.", "Ce nom est déjà pris."),
            ["create.error.full"] = L("La cuenta no tiene más sitios de personaje.", "The account has no character slot left.",
                                      "Le compte n'a plus de place de personnage."),
            ["create.error.breed"] = L("Elige una clase.", "Choose a class.", "Choisis une classe."),
            ["disconnect"] = L("DESCONECTAR", "DISCONNECT", "DÉCONNECTER"),
            ["master"] = L("Tú", "You", "Vous"),
            ["busy"] = L("En juego en otro cliente", "Playing on another client", "En jeu sur un autre client"),
            ["fighting"] = L("En combate", "In a fight", "En combat"),
            ["off"] = L("El servidor no tiene activado el modo Héroe.", "The server has the hero mode off.", "Le serveur n'a pas activé le mode Héros."),
            ["world"] = L("Entra en el mundo con un personaje.", "Enter the world with a character.", "Entre dans le monde avec un personnage."),
            ["unreachable"] = L("No se puede hablar con el servidor.", "The server cannot be reached.", "Le serveur est injoignable."),
            ["loading"] = L("Preguntando al servidor...", "Asking the server...", "Demande au serveur..."),
            ["hint"] = L("Tus héroes combaten a tu lado: los juegas tú en sus turnos.", "Your heroes fight at your side: you play them in their turns.",
                         "Vos héros combattent à vos côtés : vous les jouez à leur tour."),
            ["error.full"] = L("Ya tienes todos los héroes que permite el servidor.", "You already have as many heroes as the server allows.",
                               "Vous avez déjà autant de héros que le serveur le permet."),
            ["error.online"] = L("Ese personaje está en juego en otro cliente.", "That character is playing on another client.",
                                 "Ce personnage est en jeu sur un autre client."),
            ["error.fight"] = L("No se puede durante un combate.", "Not during a fight.", "Impossible pendant un combat."),
            ["error.already"] = L("Ese personaje ya es un héroe.", "That character is a hero already.", "Ce personnage est déjà un héros."),
            ["error.off"] = L("El servidor no tiene activado el modo Héroe.", "The server has the hero mode off.", "Le serveur n'a pas activé le mode Héros."),
            ["error.none"] = L("No se puede desconectar ahora.", "It cannot be disconnected now.", "Impossible de le déconnecter maintenant."),
            ["error"] = L("No se ha podido.", "It could not be done.", "Cela n'a pas pu se faire."),
            ["hint.quests"] = L("Compartir misiones", "Share quest progress", "Partager la progression des quêtes"),
            ["quests.behind"] = L("No ha seguido:", "Did not follow:", "N'a pas suivi :"),
            ["quests.line"] = L("• {0}: {1}", "• {0}: {1}", "• {0} : {1}"),
            ["quests.why.elsewhere"] = L("no estaba en tu mapa", "not on your map", "pas sur ta carte"),
            ["quests.why.infight"] = L("estaba en combate", "in a fight", "en combat"),
            ["quests.why.notinthefight"] = L("no ganó ese combate contigo", "not among the winners of that fight", "n'a pas gagné ce combat avec toi"),
            ["quests.why.missingitems"] = L("le faltan {0} × {1}", "{0} × {1} missing", "il manque {0} × {1}"),
            ["quests.why.notshared"] = L("paso que no se comparte (objetos, kamas o viaje)", "step not shared (items, kamas or travel)", "étape non partagée (objets, kamas ou voyage)"),
            ["quests.why.notstarted"] = L("no tiene la misión", "quest not started", "n'a pas la quête"),
            ["quests.why.behind"] = L("no estaba en el mismo paso", "not on the same step", "pas à la même étape"),
            ["quests.why.requirements"] = L("no cumple las condiciones", "conditions not met", "ne remplit pas les conditions"),
            ["alert.title"] = L("Un héroe no ha seguido", "A hero did not follow", "Un héros n'a pas suivi"),
            ["alert.line"] = L("{0} — {1}: {2}", "{0} — {1}: {2}", "{0} — {1} : {2}"),
            ["alert.after"] = L("Su botón de misiones queda en rojo hasta que se ponga al día.",
                                "His quests button stays red until he has caught up.",
                                "Son bouton des quêtes reste rouge tant qu'il n'a pas rattrapé son retard."),
            ["alert.ok"] = L("ENTENDIDO", "GOT IT", "COMPRIS"),
            ["hint.auto"] = L("Pase automático del turno", "Automatic turn pass", "Passage automatique du tour"),
            ["hint.loot"] = L("Destinatario del botín", "Loot recipient", "Destinataire du butin"),
            ["hint.play"] = L("Tomar el control de este personaje", "Take control of this character", "Prendre la main sur ce personnage"),
            ["hint.fold"] = L("Plegar / desplegar el grupo", "Fold / unfold the group", "Replier / déplier le groupe"),
            ["hint.window"] = L("Modo Héroe", "Hero mode", "Mode Héros"),
            ["card.class"] = L("Clase", "Class", "Classe"),
            ["card.level"] = L("Nivel", "Level", "Niveau"),
            ["card.prospecting"] = L("Prospección", "Prospecting", "Prospection"),
            ["card.life"] = L("PV", "HP", "PV"),
            ["card.initiative"] = L("Iniciativa", "Initiative", "Initiative"),
            ["card.wisdom"] = L("Sabiduría", "Wisdom", "Sagesse"),
            ["hint.bag"] = L("Inventario", "Inventory", "Inventaire"),
            ["hint.stats"] = L("Características", "Characteristics", "Caractéristiques"),
            ["hint.spells"] = L("Hechizos", "Spells", "Sorts"),
            ["equipped"] = L("EQUIPADO", "WORN", "ÉQUIPÉ"),
            ["bagged"] = L("BOLSA", "BAG", "SAC"),
            ["nothing"] = L("Nada.", "Nothing.", "Rien."),
            ["spellLevel"] = L("nivel {0}", "level {0}", "niveau {0}"),
            ["stat.level"] = L("Nivel", "Level", "Niveau"),
            ["stat.xp"] = L("Experiencia", "Experience", "Expérience"),
            ["stat.life"] = L("Vida", "Life", "Vie"),
            ["stat.ap"] = L("PA", "AP", "PA"),
            ["stat.mp"] = L("PM", "MP", "PM"),
            ["stat.initiative"] = L("Iniciativa", "Initiative", "Initiative"),
            ["stat.prospecting"] = L("Prospección", "Prospecting", "Prospection"),
            ["stat.wisdom"] = L("Sabiduría", "Wisdom", "Sagesse"),
            ["stat.strength"] = L("Fuerza", "Strength", "Force"),
            ["stat.intelligence"] = L("Inteligencia", "Intelligence", "Intelligence"),
            ["stat.chance"] = L("Suerte", "Chance", "Chance"),
            ["stat.agility"] = L("Agilidad", "Agility", "Agilité"),
            ["stat.vitality"] = L("Vitalidad", "Vitality", "Vitalité"),
            ["stat.range"] = L("Alcance", "Range", "Portée"),
            ["stat.power"] = L("Potencia", "Power", "Puissance"),
            ["stat.critical"] = L("Críticos", "Critical", "Coups critiques"),
            ["stat.summons"] = L("Invocaciones", "Summons", "Invocations"),
            ["stat.resist"] = L("Resistencias", "Resistances", "Résistances"),
            ["stat.resistValues"] = L("Neutral {0}% · Tierra {1}% · Fuego {2}% · Agua {3}% · Aire {4}%",
                                      "Neutral {0}% · Earth {1}% · Fire {2}% · Water {3}% · Air {4}%",
                                      "Neutre {0}% · Terre {1}% · Feu {2}% · Eau {3}% · Air {4}%"),
            ["stat.points"] = L("Puntos por repartir", "Points to spend", "Points à répartir"),
            ["stat.kamas"] = L("Kamas", "Kamas", "Kamas"),
        };

        private static string T(string key, params object[] values)
        {
            if (!Texts.TryGetValue(key, out var byLanguage)) return key;
            string text = byLanguage.TryGetValue(Language, out var own) ? own : byLanguage["en"];
            return values.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, values);
        }

        private static readonly Dictionary<int, string[]> Classes = new Dictionary<int, string[]>
        {
            [1] = new[] { "Feca", "Feca", "Féca" }, [2] = new[] { "Osamodas", "Osamodas", "Osamodas" },
            [3] = new[] { "Anutrof", "Enutrof", "Enutrof" }, [4] = new[] { "Sram", "Sram", "Sram" },
            [5] = new[] { "Xelor", "Xelor", "Xélor" }, [6] = new[] { "Zurcarák", "Ecaflip", "Ecaflip" },
            [7] = new[] { "Aniripsa", "Eniripsa", "Eniripsa" }, [8] = new[] { "Yopuka", "Iop", "Iop" },
            [9] = new[] { "Ocra", "Cra", "Crâ" }, [10] = new[] { "Sadida", "Sadida", "Sadida" },
            [11] = new[] { "Sacrógrito", "Sacrier", "Sacrieur" }, [12] = new[] { "Pandawa", "Pandawa", "Pandawa" },
            [13] = new[] { "Tymador", "Rogue", "Roublard" }, [14] = new[] { "Zobal", "Masqueraider", "Zobal" },
            [15] = new[] { "Steamer", "Foggernaut", "Steamer" }, [16] = new[] { "Selatrop", "Eliotrope", "Eliotrope" },
            [17] = new[] { "Hipermago", "Huppermage", "Huppermage" }, [18] = new[] { "Uginak", "Ouginak", "Ouginak" },
            [20] = new[] { "Forjalanza", "Forgelance", "Forgelance" },
        };

        private static string ClassOf(int breed)
            => Classes.TryGetValue(breed, out var names) ? names[Language == "fr" ? 2 : Language == "en" ? 1 : 0] : "";

        // ─── The server ──────────────────────────────────────────────────────────────────

        private static async Task<(int Code, JsonElement? Body)> AskAsync(string route, Dictionary<string, object> body = null)
        {
            body ??= new Dictionary<string, object>();
            body["token"] = _token;
            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(Api + route, content);
                string text = await response.Content.ReadAsStringAsync();
                JsonElement? parsed = null;
                try { parsed = JsonDocument.Parse(text).RootElement.Clone(); } catch { }
                return ((int)response.StatusCode, parsed);
            }
            catch
            {
                return (0, null);
            }
        }

        private static void Poll()
        {
            _polling = true;
            int asked = ++_listAsked;
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/list");
                _toUnity.Enqueue(() =>
                {
                    // Asked before a later one -- a switch's --: the old group's, dropped.
                    if (asked != _listAsked) return;
                    _polling = false;
                    _listCode = code;
                    _list = code == 200 ? body : null;
                    if (_list is JsonElement l && l.TryGetProperty("characters", out var all))
                        foreach (var one in all.EnumerateArray()) AskPortrait(Long(one, "id"));
                    Render();
                    try { RenderPanel(); } catch (Exception ex) { MelonLogger.Warning($"[Heroes] The group panel: {ex.Message}"); }
                    try { WatchQuestLags(); } catch (Exception ex) { MelonLogger.Warning($"[Heroes] The quest alert: {ex.Message}"); }
                    try { MasterChanged(); } catch (Exception ex) { MelonLogger.Warning($"[Heroes] The inventory's character: {ex.Message}"); }
                    if (_switchBusy && _switchListed) { _switchListed = false; SwitchDone(); }
                });
            });
        }

        /// <summary>True once a switch has answered and the list after it is being read.</summary>
        private static bool _switchListed;

        private static long _masterBefore;
        private static bool _toldInventory;
        private enum HeroUiType
        {
            CharacterSheet,
            Inventory,
            Spells,
            Cosmetics
        }

        private static readonly HashSet<HeroUiType> _openUisToReopen = new HashSet<HeroUiType>();
        private static long _reopenHeroUiTime;
        private static bool _wasAppearanceOpen;
        private static long _reopenAppearanceTime;

        private static void FindOpenFigmaWindows(VisualElement root, List<WindowFigma> list)
        {
            if (root == null) return;
            var wf = root.TryCast<WindowFigma>();
            if (wf != null)
            {
                if (wf.panel != null && wf.resolvedStyle.display != DisplayStyle.None && wf.resolvedStyle.visibility == Visibility.Visible)
                {
                    list.Add(wf);
                }
                return;
            }
            for (int i = 0; i < Kids(root); i++)
            {
                FindOpenFigmaWindows(Kid(root, i), list);
            }
        }

        private static void RecordAndCloseOpenHeroUis()
        {
            _openUisToReopen.Clear();
            _wasAppearanceOpen = false;
            try
            {
                var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
                var openWindows = new List<WindowFigma>();
                for (int i = 0; i < documents.Length; i++)
                {
                    var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                    if (root != null) FindOpenFigmaWindows(root, openWindows);
                }

                foreach (var wf in openWindows)
                {
                    string name = wf.name ?? "";
                    string shortcut = wf.shortcutValue ?? "";
                    HeroUiType? detected = null;

                    if (name.IndexOf("Appearance", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        detected = HeroUiType.Cosmetics;
                        _wasAppearanceOpen = true;
                    }
                    else if (name.IndexOf("StatSheet", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("CharacterSheet", StringComparison.OrdinalIgnoreCase) >= 0 || shortcut == "C")
                        detected = HeroUiType.CharacterSheet;
                    else if (name.IndexOf("Cosmetic", StringComparison.OrdinalIgnoreCase) >= 0 || shortcut == "O")
                        detected = HeroUiType.Cosmetics;
                    else if (name.IndexOf("Equipment", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0 || shortcut == "I")
                        detected = HeroUiType.Inventory;
                    else if (name.IndexOf("PlayerSpell", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Spell", StringComparison.OrdinalIgnoreCase) >= 0 || shortcut == "S")
                        detected = HeroUiType.Spells;

                    if (detected.HasValue)
                    {
                        _openUisToReopen.Add(detected.Value);
                        try
                        {
                            wf.closeButton?.InvokeSimulateSingleClick(null);
                            MelonLogger.Msg($"[Heroes] Auto-refresh: closed open UI {detected.Value} on hero switch.");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] Error detecting open UIs: {ex.Message}");
            }
        }

        private static void ReopenAppearanceWindow()
        {
            try
            {
                var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
                for (int i = 0; i < documents.Length; i++)
                {
                    var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                    if (root == null) continue;
                    var btnApp = root.Q<DofusButtonCustom>("btnAppearance");
                    if (btnApp != null)
                    {
                        btnApp.InvokeSimulateSingleClick(null);
                        MelonLogger.Msg("[Heroes] Auto-refresh: reopened Appearance window on hero switch.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] Error reopening appearance window: {ex.Message}");
            }
        }

        private static bool MatchSlotUi(Il2CppCore.UILogic.Components.Slots.Slot slot, HeroUiType type)
        {
            if (slot == null) return false;
            string tooltip = slot.tooltip ?? "";
            string img = slot.imageUrl ?? "";
            string name = slot.name ?? "";

            switch (type)
            {
                case HeroUiType.CharacterSheet:
                    return tooltip.Contains("(C)") || tooltip.IndexOf("caracter", StringComparison.OrdinalIgnoreCase) >= 0
                           || tooltip.IndexOf("character", StringComparison.OrdinalIgnoreCase) >= 0
                           || img.IndexOf("character", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("character", StringComparison.OrdinalIgnoreCase) >= 0;

                case HeroUiType.Inventory:
                    return tooltip.Contains("(I)") || tooltip.IndexOf("invent", StringComparison.OrdinalIgnoreCase) >= 0
                           || img.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0;

                case HeroUiType.Spells:
                    return tooltip.Contains("(S)") || tooltip.IndexOf("hechizo", StringComparison.OrdinalIgnoreCase) >= 0
                           || tooltip.IndexOf("spell", StringComparison.OrdinalIgnoreCase) >= 0
                           || tooltip.IndexOf("sort", StringComparison.OrdinalIgnoreCase) >= 0
                           || img.IndexOf("spell", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("spell", StringComparison.OrdinalIgnoreCase) >= 0;

                case HeroUiType.Cosmetics:
                    return tooltip.Contains("(O)") || tooltip.IndexOf("cosmetic", StringComparison.OrdinalIgnoreCase) >= 0
                           || tooltip.IndexOf("apariencia", StringComparison.OrdinalIgnoreCase) >= 0
                           || tooltip.IndexOf("atuendo", StringComparison.OrdinalIgnoreCase) >= 0
                           || img.IndexOf("cosmetic", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("cosmetic", StringComparison.OrdinalIgnoreCase) >= 0
                           || img.IndexOf("appearance", StringComparison.OrdinalIgnoreCase) >= 0
                           || name.IndexOf("appearance", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return false;
        }

        private static void TriggerMenuSlot(HeroUiType type)
        {
            try
            {
                var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
                var grids = new List<VisualElement>();
                for (int i = 0; i < documents.Length; i++)
                {
                    var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                    if (root != null) GridsUnder(root, grids);
                }
                grids = grids.Where(g => g.panel != null && g.resolvedStyle.display != DisplayStyle.None
                                         && g.resolvedStyle.visibility == Visibility.Visible && g.worldBound.width > 4f).ToList();
                var main = grids.FirstOrDefault(g => g.name == "gd_btnUis");
                var additional = grids.FirstOrDefault(g => g.name == "gd_additionalBtns");

                var slotList = new List<Il2CppCore.UILogic.Components.Slots.Slot>();
                void CollectSlots(VisualElement grid)
                {
                    if (grid == null) return;
                    for (int i = 0; i < Kids(grid); i++)
                    {
                        var s = Kid(grid, i).TryCast<Il2CppCore.UILogic.Components.Slots.Slot>();
                        if (s != null) slotList.Add(s);
                    }
                }
                CollectSlots(main);
                CollectSlots(additional);

                foreach (var s in slotList)
                {
                    if (MatchSlotUi(s, type))
                    {
                        s.OnClick(null);
                        MelonLogger.Msg($"[Heroes] Auto-refresh: reopened UI {type} via menu slot.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] Error reopening UI {type}: {ex.Message}");
            }
        }

        /// <summary>
        /// The character played has changed (a switch): the game's windows left open through it
        /// -- the inventory -- go on drawing the one played before, their displayer never told of
        /// another. Each of the game's displayers standing in such a window is given the new look.
        /// </summary>
        private static void MasterChanged()
        {
            if (!(_list is JsonElement list) || !list.TryGetProperty("characters", out var all)) return;
            long id = 0;
            string look = "";
            foreach (var c in all.EnumerateArray())
                if (Bool(c, "master")) { id = Long(c, "id"); look = Str(c, "look"); }
            if (id == 0) return;
            bool changed = _masterBefore != 0 && _masterBefore != id;
            _masterBefore = id;
            if (changed)
            {
                RecordAndCloseOpenHeroUis();
                _reopenHeroUiTime = Environment.TickCount64 + 250;
                try
                {
                    Type.GetType("JondoFix.ColorPalettePatch, JondoFix")?.GetMethod("OnCharacterChanged")?.Invoke(null, null);
                }
                catch { }
            }
            if (!changed || look.Length == 0 || _displayers == null || _entities == null) return;

            var found = new List<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer>();
            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            for (int i = 0; i < documents.Length; i++)
            {
                var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                if (root != null) GameDisplayers(root, found);
            }
            Il2CppCore.Rendering.Look.EntityLook entityLook = null;
            foreach (var displayer in found)
            {
                string path = PathOf(displayer);
                bool mine = path.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0
                            || path.IndexOf("equipment", StringComparison.OrdinalIgnoreCase) >= 0
                            || path.IndexOf("storage", StringComparison.OrdinalIgnoreCase) >= 0
                            || path.IndexOf("characterSheet", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!_toldInventory)
                    MelonLogger.Msg($"[Heroes] A displayer of the game's after a switch: {path} -> {(mine ? "given the new look" : "left")}.");
                if (!mine) continue;
                entityLook ??= LookOf(look);
                // Its own framing kept: the giving sets one of its own, and he came out too big.
                bool autosize = displayer.autosize;
                float forced = displayer.forcedSize, ratio = displayer.ratioPositioning;
                // Given a look by the mod already, its own forced size is the one kept then.
                var known = _reframe.FirstOrDefault(r => r.Displayer.Pointer == displayer.Pointer);
                if (known != null) forced = known.Forced;
                IntPtr before = displayer.character != null ? displayer.character.Pointer : IntPtr.Zero;
                // As the window drew its own: the animation it showed and the way it faced. The
                // displayer sizes a character by its animation's data, and SetLookAndDirection's
                // own animation drew him twice the size. (HeroesTuning.cfg's inventoryMode 0 is
                // that way again; inventoryFlags the four yes-or-noes of GenerateEntity, as bits.)
                string animation = displayer.m_lastAnimation;
                var facing = (Il2Cpp.rc)1;
                try { if (displayer.character != null) facing = displayer.character.fhpu; } catch { }
                int mode = (int)Tune("inventoryMode", 1f), flags = (int)Tune("inventoryFlags", 0f);
                if (mode == 0 || string.IsNullOrEmpty(animation))
                {
                    Il2CppCore.UI.UIUtils.pcb(displayer, entityLook, facing, 0, _displayers, _entities);
                    _reframe.RemoveAll(r => r.Displayer.Pointer == displayer.Pointer);
                    _reframe.Add(new Reframed { Displayer = displayer, Forced = forced, Before = before });
                }
                else
                {
                    var playing = new Il2CppCore.Rendering.Entity.Animations.EntityAnimation(
                        animation, facing, true, false, true, false, default(Il2Cpp.yb),
                        default(Il2CppCore.Rendering.Entity.Animations.EntityAnimationPlaybackModeValue), false, false,
                        new Il2CppSystem.Nullable<float>());
                    displayer.GenerateEntity(id, entityLook, playing, (flags & 1) != 0, facing, animation,
                                             (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0);
                }
                MelonLogger.Msg($"[Heroes] The inventory's character given anew: mode {mode}, flags {flags}, animation «{animation}», facing {(int)facing}; " +
                                $"autosize {autosize}, forced {forced}, ratio {ratio}.");
            }
            _toldInventory = true;
            Reframe();
        }

        /// <summary>One of the game's displayers the mod gave a look: its own forced size, and the character the mod's giving made.</summary>
        private sealed class Reframed
        {
            public Il2CppCore.UILogic.EntityDisplay.EntityDisplayer Displayer;
            public float Forced;
            public IntPtr Before, Ours;
        }

        private static readonly List<Reframed> _reframe = new List<Reframed>();

        /// <summary>
        /// The mod's giving of a look draws a character some twice the size the game's own way
        /// draws him at the same forced size -- which the bigger it is the smaller he is drawn --:
        /// while the character shown is the mod's the forced size is that much bigger
        /// (HeroesTuning.cfg's inventorySize), and the game's own back as soon as the game draws
        /// its own again -- a change of gear --, so its own is never drawn small.
        /// </summary>
        private static void Reframe()
        {
            for (int i = _reframe.Count - 1; i >= 0; i--)
            {
                var one = _reframe[i];
                try
                {
                    var displayer = one.Displayer;
                    if (displayer.panel == null) { displayer.forcedSize = one.Forced; _reframe.RemoveAt(i); continue; }
                    var character = displayer.character;
                    float wanted = one.Forced * Tune("inventorySize", 2f);
                    if (one.Ours == IntPtr.Zero)
                    {
                        // The one shown before the giving is not the mod's: its own comes after.
                        if (character != null && character.Pointer != one.Before) one.Ours = character.Pointer;
                    }
                    else if (character == null || character.Pointer != one.Ours)
                    {
                        displayer.forcedSize = one.Forced;
                        displayer.ForceRefreshSize();
                        _reframe.RemoveAt(i);
                        continue;
                    }
                    if (Math.Abs(displayer.forcedSize - wanted) > 0.001f)
                    {
                        displayer.forcedSize = wanted;
                        displayer.ForceRefreshSize();
                    }
                }
                catch { _reframe.RemoveAt(i); }
            }
        }

        /// <summary>Every displayer of the game's own under an element: not the mod's, not in the mod's windows.</summary>
        private static void GameDisplayers(VisualElement element, List<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer> into)
        {
            for (int i = 0; i < Kids(element); i++)
            {
                var child = Kid(element, i);
                string name = child.name ?? "";
                if (name.StartsWith("JondoHeroes")) continue;
                var displayer = child.TryCast<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer>();
                if (displayer != null)
                {
                    if (displayer.panel != null && displayer.resolvedStyle.display != DisplayStyle.None) into.Add(displayer);
                    continue;
                }
                GameDisplayers(child, into);
            }
        }

        /// <summary>The names an element stands under, itself first: where in the game's interface it is.</summary>
        private static string PathOf(VisualElement element)
        {
            var names = new List<string>();
            for (var at = element; at != null && names.Count < 14; at = at.hierarchy.parent)
                if (!string.IsNullOrEmpty(at.name)) names.Add(at.name);
            return string.Join(" < ", names);
        }

        private static void AskPortrait(long id)
        {
            if (!_asked.Add(id)) return;
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/portrait", new Dictionary<string, object> { ["id"] = id });
                if (code != 200 || body is not JsonElement b) return;
                string png = Str(b, "png");
                if (png.Length == 0) return;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(png); } catch { return; }
                _toUnity.Enqueue(() =>
                {
                    var texture = new UnityEngine.Texture2D(2, 2);
                    if (!UnityEngine.ImageConversion.LoadImage(texture, bytes)) return;
                    texture.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset;
                    _portraits[id] = texture;
                    Render();
                    try { RenderPanel(); } catch { }
                });
            });
        }

        private static void Act(string route, long id, Dictionary<string, object> more = null)
        {
            Say("");
            var ask = more ?? new Dictionary<string, object>();
            ask["id"] = id;
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync(route, ask);
                _toUnity.Enqueue(() =>
                {
                    if (code == 0) Say(T("unreachable"));
                    else if (code != 200)
                    {
                        string why = body is JsonElement b ? Str(b, "error") : "";
                        Say(Texts.ContainsKey("error." + why) ? T("error." + why) : T("error"));
                    }
                    Poll();
                });
            });
        }

        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        // ─── The window ──────────────────────────────────────────────────────────────────

        private static void Open()
        {
            try
            {
                var layer = FindLayer();
                if (layer == null) return;
                InstallInputGuard();
                float w = layer.resolvedStyle.width, h = layer.resolvedStyle.height;
                float width = float.IsNaN(w) || w <= 0 ? 1060f : Math.Min(1060f, w - 40f);
                float height = float.IsNaN(h) || h <= 0 ? 780f : Math.Min(780f, h - 40f);

                var window = new WindowFigma(true, T("title") + "   Ctrl + Tab", "", true);
                window.showCloseButton = true;
                window.name = "JondoHeroes";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(width);
                window.style.height = new StyleLength(height);
                window.style.left = new StyleLength(float.IsNaN(w) || w <= 0 ? 150f : Math.Max(0f, (w - width) / 2f));
                window.style.top = new StyleLength(float.IsNaN(h) || h <= 0 ? 60f : Math.Max(0f, (h - height) / 2f));

                var body = new VisualElement();
                body.style.flexGrow = new StyleFloat(1f);
                body.style.paddingLeft = new StyleLength(16f);
                body.style.paddingRight = new StyleLength(16f);
                body.style.paddingTop = new StyleLength(12f);
                body.style.paddingBottom = new StyleLength(4f);
                _grid = new VisualElement();
                _grid.style.flexGrow = new StyleFloat(1f);
                body.Add(_grid);
                _say = Label("", 13f);
                _say.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
                _say.style.color = new StyleColor(Red);
                body.Add(_say);
                window.Add(body);

                var footer = new VisualElement();
                footer.style.height = new StyleLength(34f);
                footer.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
                footer.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.36f, 0.36f, 0.52f, 0.9f));
                _footer = Label("", 13f);
                _footer.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
                _footer.style.color = new StyleColor(new UnityEngine.Color(0.80f, 0.80f, 0.90f, 1f));
                _footer.style.letterSpacing = new StyleLength(2f);
                _footer.style.marginBottom = new StyleLength(0f);
                footer.Add(_footer);
                window.Add(footer);

                layer.Add(window);
                window.BringToFront();
                KeepEvents(window);
                var close = window.header?.rightButton;
                if (close != null) close.add_Clicked(Do(Close));
                _window = window;
                Render();
                _nextPoll = 0f;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] The window could not be opened: {ex}");
                Close();
            }
        }

        private static void Close()
        {
            try { _window?.RemoveFromHierarchy(); } catch { }
            _window = null;
            _creating = _creationBuilt = false;
            _grid = null;
            _footer = null;
            _say = null;
        }

        private static void Say(string text, bool good = false)
        {
            if (_say == null) return;
            _say.text = text;
            _say.style.color = new StyleColor(good ? new UnityEngine.Color(0.56f, 0.93f, 0.56f, 1f) : Red);
        }

        /// <summary>The cards: the master, his heroes, the others of the account, and empty places to eight.</summary>
        private static void Render()
        {
            if (_grid == null) return;
            if (_creating) { RenderCreation(); return; }
            _grid.Clear();
            if (_listCode == -1 && _list == null) { _grid.Add(Label(T("loading"))); return; }
            if (_listCode == 0) { _grid.Add(Label(T("unreachable"))); return; }
            if (_list is not JsonElement list || !list.TryGetProperty("characters", out var characters))
            {
                _grid.Add(Label(T("world")));
                return;
            }
            if (!Bool(list, "on")) { _grid.Add(Label(T("off"))); return; }

            // Drawn again only when something in it changed: taken off and put back, the client's
            // displayers stop moving.
            string key = Shape(list) + "|" + _portraits.Count + "|" + (_entities != null);
            if (key == _windowKey && _grid.childCount > 0) return;
            _windowKey = key;

            int max = Int(list, "max");
            bool roomLeft = Int(list, "connected") < 1 + max;
            bool masterFighting = Bool(list, "inFight");
            var all = characters.EnumerateArray().ToList();
            // The account's free character slots, each an empty place one can make a hero on.
            int canMake = Int(list, "slotsLeft");
            for (int row = 0; row < 2; row++)
            {
                var line = new VisualElement();
                line.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                line.style.flexGrow = new StyleFloat(1f);
                line.style.flexBasis = new StyleLength(0f);
                for (int column = 0; column < 4; column++)
                {
                    int at = row * 4 + column;
                    if (at < all.Count && at < Places) line.Add(Card(all[at], roomLeft, masterFighting));
                    else line.Add(Empty(canMake-- > 0 && !masterFighting));
                }
                _grid.Add(line);
            }
            if (_footer != null) _footer.text = T("connected", Int(list, "connected"), 1 + max);
        }

        // ─── The client's own portraits ──────────────────────────────────────────────────

        // The services the client hands its portraits (EntityPortrait.InitEntityPortrait),
        // borrowed off one of its own -- the party window's, the player's -- or as it hands them.
        private static Il2Cpp.feu _entities;
        private static Il2Cpp.fep _displayers;
        private static Il2Cpp.fer _entityInfos;
        private static Il2Cpp.fdf _menuDependencies;
        private static Il2Cpp.fcg _menus;
        // EntityPortrait.SetEntityLook falls over inside (asynchronously, past any catch) with the
        // services the mod has: the displayer, as the character selection draws its characters.
        private static bool _liveBroken, _toldLive, _portraitFailed = true;
        private static int _borrowScans;

        /// <summary>Each living portrait, by where it stands and whose it is, with what it was made of.</summary>
        private static readonly Dictionary<string, (string Key, VisualElement View)> _live = new Dictionary<string, (string, VisualElement)>();

        /// <summary>The animation a character not in the world sits in.</summary>
        private const string Seated = "AnimEmoteSit_Statique";

        /// <summary>
        /// The services off any portrait of the client's already on screen: the party window's,
        /// the player's own. Tried at every scan until had.
        /// </summary>
        private static void BorrowServices()
        {
            if ((_displayers != null && _entities != null) || _liveBroken) return;
            if (_displayers != null) EntitiesOffDisplayers();
            if (_entities != null) return;
            // The displaying service had, the entities' one is looked for a while longer only.
            if (_displayers != null && ++_borrowScans > 60) return;
            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            for (int i = 0; i < documents.Length && (_displayers == null || _entities == null); i++)
            {
                var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                if (root != null) BorrowUnder(root);
            }
        }

        private static void BorrowUnder(VisualElement element)
        {
            for (int i = 0; i < Kids(element) && (_displayers == null || _entities == null); i++)
            {
                var child = Kid(element, i);
                var portrait = child.TryCast<Il2CppCore.UILogic.Components.Figma.EntityPortrait>();
                if (portrait != null && portrait.m_entityDisplayerService != null && portrait.m_entityService != null)
                {
                    Borrowed(portrait.m_entityService, portrait.m_entityDisplayerService, portrait.m_entityInfoService,
                             portrait.m_playerContextMenuDependenciesService, portrait.m_contextMenuService, "a portrait on screen");
                    continue;
                }
                // The inventory's own character, drawn by a displayer: the displaying service only.
                var displayer = child.TryCast<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer>();
                if (displayer != null && displayer.m_entityDisplayerService != null)
                {
                    Borrowed(null, displayer.m_entityDisplayerService, null, null, null, "a displayer on screen");
                    continue;
                }
                BorrowUnder(child);
            }
        }

        private static bool _toldDisplayers;

        /// <summary>
        /// The entities' service, which makes the character a portrait shows, out of the displaying
        /// one: off a portrait of the client's among the displayers it keeps (fep.bdvz), or off
        /// the service's own object, whose fields may hold it.
        /// </summary>
        private static void EntitiesOffDisplayers()
        {
            try
            {
                var kept = _displayers.bdvz();
                if (kept != null)
                {
                    foreach (var one in kept)
                    {
                        var portrait = one?.TryCast<Il2CppCore.UILogic.Components.Figma.EntityPortrait>();
                        if (portrait?.m_entityService == null) continue;
                        Borrowed(portrait.m_entityService, portrait.m_entityDisplayerService, portrait.m_entityInfoService,
                                 portrait.m_playerContextMenuDependenciesService, portrait.m_contextMenuService, "a kept portrait");
                        return;
                    }
                }

                // The services hold one another: from the displaying one, through what each holds,
                // to one that is the entities' service.
                var path = FindEntities(_displayers.Pointer, out int visited);
                if (_entities != null)
                {
                    MelonLogger.Msg($"[Heroes] The entities' service found through {path} ({visited} object(s) looked at).");
                    return;
                }
                if (!_toldDisplayers)
                {
                    _toldDisplayers = true;
                    MelonLogger.Msg($"[Heroes] No entities' service among {visited} object(s) the displaying service leads to; " +
                                    $"{kept?.Count ?? 0} displayer(s) kept.");
                }
            }
            catch (Exception ex)
            {
                if (!_toldDisplayers) { _toldDisplayers = true; MelonLogger.Warning($"[Heroes] The displayers looked into in vain: {ex.Message}"); }
            }
        }

        /// <summary>A member's card lines out of the latest list, or null when he is not in it.</summary>
        private static List<(string Label, string Value)> CardLines(long id)
        {
            if (!(_list is JsonElement list) || !list.TryGetProperty("characters", out var all)) return null;
            foreach (var c in all.EnumerateArray()) if (Long(c, "id") == id) return CardLines(c);
            return null;
        }

        private static List<(string Label, string Value)> CardLines(JsonElement who) => new List<(string, string)>
        {
            (T("card.class"), ClassOf(Int(who, "breed"))),
            (T("card.level"), Int(who, "level").ToString()),
            (T("card.prospecting"), Int(who, "prospecting").ToString()),
            (T("card.life"), $"{Int(who, "life")} / {Int(who, "maxLife")}"),
            (T("card.initiative"), Int(who, "initiative").ToString()),
            (T("card.wisdom"), Int(who, "wisdom").ToString()),
        };

        /// <summary>Each member's life in the group panel, filled where it stands.</summary>
        private static readonly Dictionary<long, VisualElement> _fills = new Dictionary<long, VisualElement>();

        /// <summary>What the window's cards were last drawn from (Render).</summary>
        private static string _windowKey;

        /// <summary>
        /// What a drawing of the group depends on, the life and the characteristics left out: they
        /// change all along, and every drawing again starts the portraits' animations over.
        /// </summary>
        private static string Shape(JsonElement list)
        {
            var text = new System.Text.StringBuilder();
            text.Append(Int(list, "max")).Append('|').Append(Bool(list, "inFight")).Append('|').Append(Long(list, "lootTo"))
                .Append('|').Append(Int(list, "slotsLeft"));
            if (list.TryGetProperty("characters", out var all))
                foreach (var c in all.EnumerateArray())
                    text.Append(';').Append(Long(c, "id")).Append(',').Append(Str(c, "name")).Append(',').Append(Int(c, "level"))
                        .Append(',').Append(Bool(c, "master")).Append(Bool(c, "hero")).Append(Bool(c, "busy")).Append(Bool(c, "inFight"))
                        .Append(Bool(c, "autoPass")).Append(',').Append(Bool(c, "questSync"))
                        // The quests he stayed behind in: the quests button turns red, and back.
                        .Append(',').Append(c.TryGetProperty("questLag", out var lag) ? lag.GetRawText() : "")
                        .Append(',').Append(Str(c, "look"));
            return text.ToString();
        }

        private static readonly Dictionary<string, Type> _managedTypes = new Dictionary<string, Type>();

        /// <summary>The interop type of a native class, by its name: null when there is none.</summary>
        private static Type ManagedOf(Il2CppSystem.Type native)
        {
            string name = native.FullName ?? native.Name;
            if (_managedTypes.TryGetValue(name, out var known)) return known;
            Type managed = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("Il2Cpp")) continue;
                managed = assembly.GetType(native.Namespace?.Length > 0 ? "Il2Cpp" + native.FullName : "Il2Cpp." + native.Name)
                          ?? assembly.GetType("Il2Cpp." + native.FullName) ?? assembly.GetType(native.FullName);
                if (managed != null) break;
            }
            _managedTypes[name] = managed;
            return managed;
        }

        /// <summary>
        /// Breadth first through what the services hold -- their fields, as the interop shows them
        /// --, four steps at most, a few hundred objects, each once: the first that is the entities'
        /// service is borrowed. The path to it, for the log.
        /// </summary>
        private static string FindEntities(IntPtr start, out int visited)
        {
            visited = 0;
            var wanted = Il2CppType.Of<Il2Cpp.feu>();
            var seen = new HashSet<IntPtr> { start };
            var queue = new Queue<(IntPtr At, string Path, int Depth)>();
            queue.Enqueue((start, "fep", 0));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            while (queue.Count > 0 && visited < 400)
            {
                var (at, path, depth) = queue.Dequeue();
                visited++;
                Il2CppSystem.Type native;
                try { native = new Il2CppSystem.Object(at).GetIl2CppType(); }
                catch { continue; }
                if (wanted.IsAssignableFrom(native))
                {
                    Borrowed(new Il2Cpp.feu(at), null, null, null, null, path);
                    return path + " (" + native.Name + ")";
                }
                if (depth >= 4) continue;
                var managed = ManagedOf(native);
                if (managed == null) continue;
                object real;
                try { real = Activator.CreateInstance(managed, at); }
                catch { continue; }
                foreach (var property in managed.GetProperties(flags))
                {
                    var type = property.PropertyType;
                    if (!typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(type)) continue;
                    if (property.GetIndexParameters().Length > 0 || type.Namespace?.StartsWith("Il2CppSystem") == true) continue;
                    try
                    {
                        if (property.GetValue(real) is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase held
                            && held.Pointer != IntPtr.Zero && seen.Add(held.Pointer))
                            queue.Enqueue((held.Pointer, path + "." + property.Name, depth + 1));
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>Harmony's postfix on EntityPortrait.InitEntityPortrait: the services as the client hands them.</summary>
        public static void OnInitEntityPortrait(Il2Cpp.feu __0, Il2Cpp.fep __1, Il2Cpp.fer __2, Il2Cpp.fdf __3, Il2Cpp.fcg __4)
        {
            if (__0 != null && __1 != null) Borrowed(__0, __1, __2, __3, __4, "InitEntityPortrait");
        }

        /// <summary>The services had, each kept once had: a later source fills in what an earlier one lacked.</summary>
        private static void Borrowed(Il2Cpp.feu entities, Il2Cpp.fep displayers, Il2Cpp.fer infos, Il2Cpp.fdf menuDependencies,
                                     Il2Cpp.fcg menus, string from)
        {
            bool first = _displayers == null;
            if (_entities == null && entities != null)
            {
                MelonLogger.Msg($"[Heroes] The entities' service had (off {from}): the portraits come alive.");
                // Whatever was made without it, made again with it.
                foreach (var made in _live.Values) Release(made.View);
                _live.Clear();
                _toldLive = false;
                _panelKey = null;
                _nextPoll = 0f;
            }
            _entities ??= entities;
            _displayers ??= displayers;
            _entityInfos ??= infos;
            _menuDependencies ??= menuDependencies;
            _menus ??= menus;
            if (!first) return;
            MelonLogger.Msg($"[Heroes] The client's portraits are ours to use (services off {from}; " +
                            $"infos {(infos != null ? "yes" : "no")}, menus {(menus != null ? "yes" : "no")}).");
            _panelKey = null;
            _nextPoll = 0f;
        }

        /// <summary>
        /// Where the services are had: as the client hands them to a portrait of its own, and as it
        /// builds the screens it hands them to -- the character selection first, shown at every way
        /// in, before the world; the inventory, the character sheet, the party window after.
        /// </summary>
        private static void WatchPortraits()
        {
            var harmony = new HarmonyLib.Harmony("jondo.heroes.portraits");
            try
            {
                var method = HarmonyLib.AccessTools.Method(typeof(Il2CppCore.UILogic.Components.Figma.EntityPortrait), "InitEntityPortrait");
                if (method != null) harmony.Patch(method, postfix: new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(OnInitEntityPortrait))));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] InitEntityPortrait not watched: {ex.Message}");
            }
            var takeArgs = new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(OnScreenBuilt)));
            int watched = 0;
            foreach (var screen in new[]
            {
                typeof(Il2CppCore.UILogic.Connection.CharacterSelection),
                typeof(Il2CppCore.UILogic.Inventory.Uis.PlayerInventory.EquipmentUi),
                typeof(Il2CppCore.UILogic.CharacterSheet.UICharacterSheet),
                typeof(Il2CppCore.UILogic.Party.PartyDisplayUi),
                typeof(Il2CppCore.UILogic.Party.PartyMember),
            })
            {
                foreach (var ctor in screen.GetConstructors())
                {
                    if (ctor.GetParameters().Length < 2) continue;
                    try { harmony.Patch(ctor, postfix: takeArgs); watched++; }
                    catch (Exception ex) { MelonLogger.Warning($"[Heroes] {screen.Name} not watched: {ex.Message}"); }
                }
            }
            MelonLogger.Msg($"[Heroes] Watching {watched} screen constructor(s) and InitEntityPortrait for the portraits' services.");
        }

        /// <summary>Harmony's postfix on those screens' constructors: whichever of the services they were handed.</summary>
        public static void OnScreenBuilt(object[] __args, System.Reflection.MethodBase __originalMethod)
        {
            try
            {
                if (__args == null || (_displayers != null && _entityInfos != null && _menus != null && _menuDependencies != null)) return;
                Il2Cpp.feu entities = null; Il2Cpp.fep displayers = null; Il2Cpp.fer infos = null; Il2Cpp.fdf dependencies = null; Il2Cpp.fcg menus = null;
                foreach (var arg in __args)
                {
                    if (arg is Il2Cpp.feu e) entities = e;
                    else if (arg is Il2Cpp.fep d) displayers = d;
                    else if (arg is Il2Cpp.fer i) infos = i;
                    else if (arg is Il2Cpp.fdf m) dependencies = m;
                    else if (arg is Il2Cpp.fcg c) menus = c;
                }
                if (entities != null && displayers != null)
                    Borrowed(entities, displayers, infos, dependencies, menus, __originalMethod?.DeclaringType?.Name ?? "a screen");
            }
            catch { }
        }

        private static Il2CppGoogle.Protobuf.MessageParser<Il2Cpp.lhg> _lookParser;

        /// <summary>
        /// A look as the client draws it, out of the protocol's EntityLook the server sends: read
        /// with its own parser, turned into the rendering's look (vd.qse) and made whole (vf.qsi)
        /// -- 3.6.10's names for them.
        /// </summary>
        private static Il2CppCore.Rendering.Look.EntityLook LookOf(string base64)
        {
            if (_lookParser == null)
            {
                foreach (var property in typeof(Il2Cpp.lhg).GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (property.PropertyType != typeof(Il2CppGoogle.Protobuf.MessageParser<Il2Cpp.lhg>)) continue;
                    _lookParser = property.GetValue(null) as Il2CppGoogle.Protobuf.MessageParser<Il2Cpp.lhg>;
                    if (_lookParser != null) break;
                }
                if (_lookParser == null) throw new InvalidOperationException("no parser for the protocol's EntityLook");
            }
            var protocol = _lookParser.ParseFrom(Convert.FromBase64String(base64));
            var look = Il2Cpp.vd.qse(protocol) ?? throw new InvalidOperationException("vd.qse gave nothing");
            try { look = Il2Cpp.vf.qsi(look) ?? look; } catch { }
            return look;
        }

        /// <summary>
        /// A character drawn alive by the client's own portrait (EntityPortrait), seated or in his
        /// class's rest; kept from one drawing of the window to the next, made again only when his
        /// look or his pose changes. Null when the client's portraits cannot be had: the caller
        /// shows the server's drawing instead.
        /// </summary>
        private static VisualElement LivePortrait(string place, long id, string look, bool seated)
        {
            // Both services or none: without the entities' one the client's displayer refuses (Init).
            if (_liveBroken || _displayers == null || _entities == null || string.IsNullOrEmpty(look)) return null;
            string where = place + ":" + id, key = look + (seated ? "|seated" : "|standing");
            if (_live.TryGetValue(where, out var known))
            {
                if (known.Key == key && known.View != null)
                {
                    known.View.RemoveFromHierarchy();
                    return known.View;
                }
                Release(known.View);
                _live.Remove(where);
            }
            Il2CppCore.Rendering.Look.EntityLook entityLook;
            string animation;
            try
            {
                entityLook = LookOf(look);
                animation = seated ? Seated : (Il2Cpp.so.pwt(entityLook, false) ?? "AnimStatique");
            }
            catch (Exception ex)
            {
                _liveBroken = true;
                MelonLogger.Warning($"[Heroes] A look could not be read for the client's portraits, the drawn ones stay: {ex}");
                return null;
            }

            // The client's portrait first, as its party window draws its members -- with the
            // entities' service only: without it, it stays an empty frame.
            if (!_portraitFailed && _entities != null)
            {
                try
                {
                    var portrait = new Il2CppCore.UILogic.Components.Figma.EntityPortrait();
                    portrait.name = "JondoHeroesLivePortrait";
                    portrait.pickingMode = PickingMode.Ignore;
                    portrait.style.flexGrow = new StyleFloat(1f);
                    portrait.InitEntityPortrait(_entities, _displayers, _entityInfos, _menuDependencies, _menus, false, false);
                    portrait.autoSize = true;
                    portrait.SetEntityLook(entityLook, id, animation, 1, false);
                    _live[where] = (key, portrait);
                    Told($"portrait {id} {animation} (entities {(_entities != null ? "yes" : "no")})");
                    return portrait;
                }
                catch (Exception ex)
                {
                    _portraitFailed = true;
                    MelonLogger.Warning($"[Heroes] EntityPortrait refused ({(_entities != null ? "with" : "without")} the entities' service), trying a displayer: {ex.Message}");
                }
            }

            // Else a displayer of its own, given its look as the client's screens give theirs
            // (UIUtils' SetLookAndDirection): standing in its rest, the pose not ours to choose.
            try
            {
                var displayer = new Il2CppCore.UILogic.EntityDisplay.EntityDisplayer();
                displayer.name = "JondoHeroesLivePortrait";
                displayer.pickingMode = PickingMode.Ignore;
                displayer.style.flexGrow = new StyleFloat(1f);
                if (seated)
                {
                    // Seated, the client sizes him to his smaller pose and he comes out bigger than
                    // the others standing: a smaller box, at the bottom of the picture, as he sits.
                    displayer.style.flexGrow = new StyleFloat(0f);
                    displayer.style.width = new StyleLength(new Length(Tune("seatedBox", 70f), LengthUnit.Percent));
                    displayer.style.height = new StyleLength(new Length(Tune("seatedBox", 70f), LengthUnit.Percent));
                    displayer.style.alignSelf = new StyleEnum<Align>(Align.Center);
                    displayer.style.marginTop = new StyleLength(StyleKeyword.Auto);
                }
                // As the character selection draws its characters, which works with no more than
                // the displaying service the mod has.
                try { displayer.Init(_displayers, _entities); }
                catch (Exception ex) { MelonLogger.Warning($"[Heroes] EntityDisplayer.Init refused, its look given all the same: {ex.Message}"); }
                displayer.autosize = true;
                // Put back in a window drawn again, it is given its turn again: taken off, the
                // displaying service had stopped moving it.
                displayer.RegisterCallback<AttachToPanelEvent>(DelegateSupport.ConvertDelegate<EventCallback<AttachToPanelEvent>>(
                    new Action<AttachToPanelEvent>(_ => Revive(displayer))), TrickleDown.NoTrickleDown);
                // Its picture sized to its box as the box gets its size: made before the group panel
                // was laid out, it stayed tiny.
                displayer.RegisterCallback<GeometryChangedEvent>(DelegateSupport.ConvertDelegate<EventCallback<GeometryChangedEvent>>(
                    new Action<GeometryChangedEvent>(e =>
                    {
                        if (Math.Abs(e.oldRect.width - e.newRect.width) < 1f && Math.Abs(e.oldRect.height - e.newRect.height) < 1f) return;
                        try { displayer.ForceRefreshSize(); } catch { }
                    })), TrickleDown.NoTrickleDown);
                if (!seated || !Seat(displayer, id, entityLook))
                    Il2CppCore.UI.UIUtils.pcb(displayer, entityLook, (Il2Cpp.rc)1, 0, _displayers, _entities);
                _live[where] = (key, displayer);
                Told($"displayer {id} {(seated && !_seatFailed ? "seated" : "standing")}");
                return displayer;
            }
            catch (Exception ex)
            {
                _liveBroken = true;
                MelonLogger.Warning($"[Heroes] The client's displayer refused too, the drawn portraits stay: {ex}");
                return null;
            }
        }

        private static bool _seatFailed;

        /// <summary>
        /// A character made seated: the displayer's own GenerateEntity with the seated animation,
        /// looping, facing the viewer. False when it refuses: he is given his look standing instead.
        /// </summary>
        private static bool Seat(Il2CppCore.UILogic.EntityDisplay.EntityDisplayer displayer, long id,
                                 Il2CppCore.Rendering.Look.EntityLook look)
        {
            if (_seatFailed) return false;
            try
            {
                displayer.Init(_displayers, _entities);
                var animation = new Il2CppCore.Rendering.Entity.Animations.EntityAnimation(
                    Seated, (Il2Cpp.rc)1, true, false, true, false, default(Il2Cpp.yb),
                    default(Il2CppCore.Rendering.Entity.Animations.EntityAnimationPlaybackModeValue), false, false,
                    new Il2CppSystem.Nullable<float>());
                displayer.GenerateEntity(id, look, animation, false, (Il2Cpp.rc)1, Seated, false, false, false);
                return true;
            }
            catch (Exception ex)
            {
                _seatFailed = true;
                MelonLogger.Warning($"[Heroes] The seated pose refused, standing instead: {ex.Message}");
                return false;
            }
        }

        /// <summary>A displayer put back on screen: in the displaying service's keeping again, moving.</summary>
        private static void Revive(Il2CppCore.UILogic.EntityDisplay.EntityDisplayer displayer)
        {
            try
            {
                // Its animation carried on where it was: not refreshed, which starts it over.
                _displayers?.bdvx(displayer.Cast<Il2CppCore.UILogic.EntityDisplay.IEntityDisplayer>());
                displayer.SetPause(false);
            }
            catch (Exception ex)
            {
                if (_toldRevive) return;
                _toldRevive = true;
                MelonLogger.Warning($"[Heroes] A displayer could not be given its turn again: {ex.Message}");
            }
        }

        private static bool _toldRevive;

        /// <summary>Whether a live portrait has its character made, and draws him.</summary>
        private static bool HasCharacter(VisualElement view)
        {
            try
            {
                var displayer = view.TryCast<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer>();
                if (displayer != null) return displayer.character != null;
                var portrait = view.TryCast<Il2CppCore.UILogic.Components.Figma.EntityPortrait>();
                return portrait != null && portrait.hasCharacter;
            }
            catch { return false; }
        }

        private static bool _toldAlive;
        private static int _liveWaits;

        /// <summary>
        /// Every scan: a live portrait whose character has come takes the drawn one's place --
        /// the drawn one off the element under it --, logged the first time.
        /// </summary>
        private static void WatchLive()
        {
            if (_live.Count > 0 && !_toldAlive && ++_liveWaits == 10)
            {
                var first = _live.Values.First().View;
                var displayer = first?.TryCast<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer>();
                MelonLogger.Msg($"[Heroes] {_live.Count} live portrait(s), none with a character after 10 s: " +
                                $"{first?.GetIl2CppType().Name}, attached {(first?.panel != null ? "yes" : "no")}, " +
                                $"size {first?.resolvedStyle.width}x{first?.resolvedStyle.height}" +
                                (displayer != null ? $", look {(displayer.displayerLook != null ? "yes" : "no")}, texture {(displayer.m_renderTexture != null ? "yes" : "no")}" : ""));
            }
            foreach (var made in _live.Values)
            {
                var view = made.View;
                var under = view?.hierarchy.parent;
                if (under == null || !HasCharacter(view)) continue;
                under.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                var frame = under.hierarchy.parent;
                if (frame != null) frame.style.opacity = new StyleFloat(1f);
                if (_toldAlive) continue;
                _toldAlive = true;
                MelonLogger.Msg($"[Heroes] A live portrait has its character: {view.GetIl2CppType().Name}.");
            }
        }

        private static void Told(string what)
        {
            if (_toldLive) return;
            _toldLive = true;
            MelonLogger.Msg($"[Heroes] A live portrait made: {what}.");
        }

        private static void Release(VisualElement view)
        {
            try
            {
                view?.TryCast<Il2CppCore.UILogic.Components.Figma.EntityPortrait>()?.Release();
                view?.RemoveFromHierarchy();
            }
            catch { }
        }

        // ─── Making a hero ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether the window shows the making of a hero instead of its cards -- built once, not
        /// again at every refresh of the list, which would take the keyboard from the name.
        /// </summary>
        private static bool _creating, _creationBuilt, _creationBusy;
        private static int _newBreed = 8, _newSex;
        private static string _newName = "";

        // ─── The game's own creation screen ─────────────────────────────────────────────

        private const string CreationTemplate = "Assets/UI/Auth/characterCreation.uxml";
        private static WindowFigma _creationWindow;
        private static bool _toldCreationTree;

        /// <summary>
        /// The game's own character creation screen: its template (characterCreation.uxml) in a
        /// window over the world, the game's CharacterCreation made on it (NativeCreation), or
        /// bound by hand when that cannot be; the mod's own form when the template cannot be had.
        /// </summary>
        private static void OpenNativeCreation()
        {
            if (_creationWindow != null && _creationWindow.panel != null) { _creationWindow.BringToFront(); return; }
            try
            {
                var layer = FindLayer();
                if (layer == null) return;
                var settings = Il2CppCore.UILogic.Components.DofusUISettings.GetSettings();
                var asset = settings?.InstantiateTemplate(CreationTemplate);
                if (asset == null)
                {
                    MelonLogger.Warning($"[Heroes] The creation screen's template ({CreationTemplate}) could not be had: the mod's form instead.");
                    StartCreation();
                    return;
                }
                float w = layer.resolvedStyle.width, h = layer.resolvedStyle.height;
                float width = float.IsNaN(w) || w <= 0 ? 1600f : w * 0.92f;
                float height = float.IsNaN(h) || h <= 0 ? 900f : h * 0.92f;
                var window = new WindowFigma(true, T("create.window"), "", true);
                window.showCloseButton = true;
                window.name = "JondoHeroesCreation";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(width);
                window.style.height = new StyleLength(height);
                window.style.left = new StyleLength(float.IsNaN(w) || w <= 0 ? 40f : (w - width) / 2f);
                window.style.top = new StyleLength(float.IsNaN(h) || h <= 0 ? 30f : (h - height) / 2f);

                var tree = asset.CloneTree();
                tree.name = "JondoHeroesCreationTree";
                tree.style.flexGrow = new StyleFloat(1f);
                window.Add(tree);
                layer.Add(window);
                window.BringToFront();
                KeepEvents(window);
                var close = window.header?.rightButton;
                if (close != null) close.add_Clicked(Do(CloseNativeCreation));
                _creationWindow = window;
                // The game's own screen whole when its services can be had; bound by hand else.
                if (!NativeCreation(tree)) BindCreation(tree);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] The creation screen could not be opened, the mod's form instead: {ex}");
                CloseNativeCreation();
                StartCreation();
            }
        }

        // ─── The game's own CharacterCreation, whole ─────────────────────────────────────

        private static Il2CppCore.UILogic.Auth.CharacterCreation _native;
        private static Dictionary<Type, object> _services;
        private static bool _nativeBroken, _nativeHooked;

        /// <summary>
        /// The services the game's creation screen is built with, found as the entities' one was:
        /// breadth first from the two the mod has, through the fields the services keep one another
        /// in -- fields only, a property's getter being code of the game's run blind. Null with what
        /// is missing logged when any is not reached.
        /// </summary>
        private static Dictionary<Type, object> FindServices(Type[] wanted)
        {
            var found = new Dictionary<Type, object>();
            var natives = wanted.ToDictionary(w => w, w => Il2CppType.From(w));
            var seen = new HashSet<IntPtr>();
            var queue = new Queue<(IntPtr At, int Depth)>();
            foreach (var start in new[] { _displayers.Pointer, _entities.Pointer })
                if (seen.Add(start)) queue.Enqueue((start, 0));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            int visited = 0;
            while (queue.Count > 0 && visited < 8000 && found.Count < wanted.Length)
            {
                var (at, depth) = queue.Dequeue();
                visited++;
                Il2CppSystem.Type native;
                try { native = new Il2CppSystem.Object(at).GetIl2CppType(); }
                catch { continue; }
                foreach (var want in wanted)
                {
                    if (found.ContainsKey(want) || !natives[want].IsAssignableFrom(native)) continue;
                    try { found[want] = Activator.CreateInstance(want, at); } catch { }
                }
                if (depth >= 6) continue;
                var managed = ManagedOf(native);
                if (managed == null || managed.Namespace?.StartsWith("Il2CppSystem") == true || managed.Namespace?.StartsWith("UnityEngine") == true) continue;
                object real;
                try { real = Activator.CreateInstance(managed, at); }
                catch { continue; }
                foreach (var property in managed.GetProperties(flags))
                {
                    var type = property.PropertyType;
                    if (!typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(type)) continue;
                    if (type.Namespace?.StartsWith("Il2CppSystem") == true || type.Namespace?.StartsWith("UnityEngine") == true) continue;
                    if (property.GetIndexParameters().Length > 0 || property.GetMethod == null || property.GetMethod.IsStatic) continue;
                    if (managed.GetField("NativeFieldInfoPtr_" + property.Name, flags) == null) continue;
                    try
                    {
                        if (property.GetValue(real) is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase held
                            && held.Pointer != IntPtr.Zero && seen.Add(held.Pointer))
                            queue.Enqueue((held.Pointer, depth + 1));
                    }
                    catch { }
                }
            }
            var missing = wanted.Where(w => !found.ContainsKey(w)).Select(w => w.Name).ToList();
            MelonLogger.Msg($"[Heroes] The creation screen's services: {found.Count} of {wanted.Length} found among {visited} object(s)" +
                            (missing.Count > 0 ? $"; missing {string.Join(", ", missing)}." : "."));
            return missing.Count > 0 ? null : found;
        }

        /// <summary>
        /// The game's own creation screen made on the template's tree, as the character selection
        /// makes it: everything in it is the game's -- the breeds, the roles, the heads, the colours,
        /// the attitudes, the spells, the character on his platform. Only its end is the mod's: its
        /// JOUER makes a hero (OnNativeCreate), and its name's die asks the server (OnNativeName).
        /// False when it cannot be made: the screen is bound by hand instead.
        /// </summary>
        private static bool NativeCreation(VisualElement tree)
        {
            if (_nativeBroken || _displayers == null || _entities == null) return false;
            try
            {
                _services ??= FindServices(new[]
                {
                    typeof(Il2Cpp.faa), typeof(Il2Cpp.fbz), typeof(Il2Cpp.ewt), typeof(Il2Cpp.fbr), typeof(Il2Cpp.fdj),
                    typeof(Il2Cpp.exd), typeof(Il2Cpp.fct), typeof(Il2Cpp.fca), typeof(Il2Cpp.eyy),
                });
                if (_services == null) { _nativeBroken = true; return false; }
                HookNative();
                // A creation from nothing has no start message: one, even empty, opened the screen
                // as a character's modification («ces changements sont définitifs»). The other
                // ways kept to try with the game running (HeroesTuning.cfg's creationMode: 1 to 4).
                int mode = (int)Tune("creationMode", 1f);
                var start = mode == 0 ? null : new Il2Cpp.cgk(new Il2CppSystem.Collections.Generic.List<Il2Cpp.dsj>(),
                    new Il2CppSystem.Collections.Generic.List<Il2Cpp.dsj>(), null, mode == 2 || mode == 4, mode == 3 || mode == 4);
                MelonLogger.Msg($"[Heroes] The game's own creation screen, start message {(mode == 0 ? "none" : "mode " + mode)}.");
                _makingNative = true;
                try
                {
                    _native = new Il2CppCore.UILogic.Auth.CharacterCreation(tree,
                        (Il2Cpp.faa)_services[typeof(Il2Cpp.faa)], _entities, _displayers,
                        (Il2Cpp.fbz)_services[typeof(Il2Cpp.fbz)], (Il2Cpp.ewt)_services[typeof(Il2Cpp.ewt)],
                        (Il2Cpp.fbr)_services[typeof(Il2Cpp.fbr)], start, (Il2Cpp.fdj)_services[typeof(Il2Cpp.fdj)],
                        (Il2Cpp.exd)_services[typeof(Il2Cpp.exd)], (Il2Cpp.fct)_services[typeof(Il2Cpp.fct)],
                        (Il2Cpp.fca)_services[typeof(Il2Cpp.fca)], (Il2Cpp.eyy)_services[typeof(Il2Cpp.eyy)]);
                }
                finally
                {
                    _makingNative = false;
                    _noPopupUntil = UnityEngine.Time.unscaledTime + 1.5f;
                }
                bool ready = _native.m_initialized;
                if (!ready)
                {
                    try { _native.OnEnable(); } catch (Exception ex) { MelonLogger.Warning($"[Heroes] CharacterCreation.OnEnable: {ex.Message}"); }
                }
                MelonLogger.Msg($"[Heroes] The game's own creation screen made: initialized {ready} at once, {_native.m_initialized} after enabling; " +
                                $"breed {_native.m_breedId}, {(_native.m_breeds != null ? _native.m_breeds.Count : -1)} breed(s).");
                foreach (string away in new[] { "parentChangeServer", "btnLeave" }) Show(UQueryExtensions.Q(tree, away, (string)null), false);
                var play = UQueryExtensions.Q(tree, "btn_play", (string)null)?.TryCast<Il2CppCore.UILogic.Components.Figma.PlayButton>();
                if (play != null) play.text = T("create.go");
                return true;
            }
            catch (Exception ex)
            {
                // Not for good: another start message may do, tried at the next opening.
                _native = null;
                MelonLogger.Warning($"[Heroes] The game's own creation screen could not be made, bound by hand instead: {ex.Message}");
                return false;
            }
        }

        private static void HookNative()
        {
            if (_nativeHooked) return;
            _nativeHooked = true;
            var harmony = new HarmonyLib.Harmony("jondo.heroes.creation");
            var type = typeof(Il2CppCore.UILogic.Auth.CharacterCreation);
            harmony.Patch(HarmonyLib.AccessTools.Method(type, "CreateCharacter"),
                          prefix: new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(OnNativeCreate))));
            harmony.Patch(HarmonyLib.AccessTools.Method(type, "RequestNameGeneration"),
                          prefix: new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(OnNativeName))));
            try
            {
                harmony.Patch(HarmonyLib.AccessTools.Method(type, "ModifyCharacterSetUp"),
                              prefix: new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(OnNativeModify))));
            }
            catch (Exception ex) { MelonLogger.Warning($"[Heroes] ModifyCharacterSetUp not watched: {ex.Message}"); }
            try
            {
                // The popups' service (fob), its showing of one (bdir).
                harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Il2Cpp.fob), "bdir"),
                              prefix: new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(OnPopup))));
            }
            catch (Exception ex) { MelonLogger.Warning($"[Heroes] The popups not watched: {ex.Message}"); }
        }

        private static bool IsOurs(Il2CppCore.UILogic.Auth.CharacterCreation screen)
            => _native != null && screen != null && screen.Pointer == _native.Pointer && _creationWindow != null;

        /// <summary>
        /// Harmony's prefix on CharacterCreation.CreateCharacter: on the mod's screen the character
        /// is made a hero by the server (heroes/create) out of what the screen holds -- his name,
        /// his breed, his sex, his head, his colours --; the selection's own screen goes its way.
        /// </summary>
        public static bool OnNativeCreate(Il2CppCore.UILogic.Auth.CharacterCreation __instance)
        {
            if (!IsOurs(__instance)) return true;
            try
            {
                if (_cBusy) return false;
                string name = (__instance.m_name ?? "").Trim();
                if (name.Length == 0)
                    name = (UQueryExtensions.Q(_creationWindow, "input_name", (string)null)?.TryCast<Il2CppCore.UILogic.Components.Figma.TextInput>()?.text ?? "").Trim();
                var ask = new Dictionary<string, object>
                {
                    ["name"] = name, ["breed"] = __instance.m_breedId, ["sex"] = __instance.m_gender, ["head"] = (int)__instance.m_head,
                };
                var colors = __instance.m_colors;
                for (int i = 0; colors != null && i < colors.Count && i < 6; i++) ask["c" + (i + 1)] = colors[i];
                MelonLogger.Msg($"[Heroes] The creation screen asks for {name}: breed {__instance.m_breedId}, sex {__instance.m_gender}, head {__instance.m_head}, " +
                                $"colours {(colors == null ? "none" : string.Join(",", Enumerable.Range(0, Math.Min(6, colors.Count)).Select(i => colors[i])))}.");
                if (name.Length == 0) { NativeError(T("create.error.empty")); return false; }
                _cBusy = true;
                NativeError("");
                Task.Run(async () =>
                {
                    var (code, body) = await AskAsync("heroes/create", ask);
                    _toUnity.Enqueue(() =>
                    {
                        _cBusy = false;
                        if (code == 200 && body is JsonElement done)
                        {
                            CloseNativeCreation();
                            Say(T(Bool(done, "connected") ? "create.done" : "create.doneAlone", name), good: true);
                            _windowKey = null;
                            Poll();
                            return;
                        }
                        string why = body is JsonElement b ? Str(b, "error") : "";
                        NativeError(code == 0 ? T("unreachable") : Texts.ContainsKey("create.error." + why) ? T("create.error." + why) : T("error"));
                    });
                });
            }
            catch (Exception ex) { MelonLogger.Warning($"[Heroes] The creation screen's JOUER: {ex}"); }
            return false;
        }

        /// <summary>True while the mod's creation screen is being made: its instance is not known yet.</summary>
        private static bool _makingNative;

        /// <summary>Until when the creation screen's opening warning is kept from showing.</summary>
        private static float _noPopupUntil;

        /// <summary>
        /// Harmony's prefix on the popups' service's showing: the creation screen, made in the
        /// world, warns on opening that it «modifies the character, for good» -- it makes one, and
        /// the warning is not shown. Only as the screen opens: any other popup is the game's own.
        /// </summary>
        public static bool OnPopup(ref int __result)
        {
            if (!_makingNative && UnityEngine.Time.unscaledTime > _noPopupUntil) return true;
            MelonLogger.Msg("[Heroes] The creation screen's opening warning not shown.");
            __result = 0;
            return false;
        }

        /// <summary>
        /// Harmony's prefix on CharacterCreation.ModifyCharacterSetUp: whatever start message is
        /// given, the screen sets itself up as a character's modification («ces changements sont
        /// définitifs»). The mod's own is set up as a first creation instead.
        /// </summary>
        public static bool OnNativeModify(Il2CppCore.UILogic.Auth.CharacterCreation __instance)
        {
            if (!_makingNative && !IsOurs(__instance)) return true;
            MelonLogger.Msg("[Heroes] The creation screen was setting up a modification: a first creation instead.");
            try { __instance.FirstCreationSetUp(); }
            catch (Exception ex) { MelonLogger.Warning($"[Heroes] FirstCreationSetUp: {ex.Message}"); }
            return false;
        }

        /// <summary>Harmony's prefix on CharacterCreation.RequestNameGeneration: on the mod's screen the server draws the name.</summary>
        public static bool OnNativeName(Il2CppCore.UILogic.Auth.CharacterCreation __instance)
        {
            if (!IsOurs(__instance)) return true;
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/name");
                _toUnity.Enqueue(() =>
                {
                    if (code != 200 || body is not JsonElement b || _creationWindow == null) return;
                    UQueryExtensions.Q(_creationWindow, "input_name", (string)null)?.TryCast<Il2CppCore.UILogic.Components.Figma.TextInput>()?.SetValue(Str(b, "name"));
                });
            });
            return false;
        }

        private static void NativeError(string text) => NameError(_creationWindow, text);

        /// <summary>
        /// Why the creation screen made nobody, under its name as the game says it: the name's own
        /// TextInput turned to its error style with the text (SetToErrorStyle), back to normal with
        /// none. Keka-Bron/JondoEmu#215: its label found by name and shown by hand stayed hidden --
        /// the TextInput shows it only in its error style --, and a name refused, taken or empty
        /// left the screen as it was, «nothing happens». In the heroes window's line when the
        /// screen has no such input.
        /// </summary>
        private static void NameError(VisualElement root, string text)
        {
            var input = root == null ? null
                : UQueryExtensions.Q(root, "input_name", (string)null)?.TryCast<Il2CppCore.UILogic.Components.Figma.TextInput>();
            try
            {
                if (input != null && !string.IsNullOrEmpty(text))
                {
                    // Its error label is in its infos, which the screen's name field keeps hidden.
                    input.displayInfos = true;
                    input.SetToErrorStyle(text);
                    return;
                }
                if (input != null)
                {
                    input.isInError = false;
                    input.UpdateViewTextInputStyle();
                    return;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[Heroes] The creation screen's error not shown on its name: {ex.Message}"); }
            if (!string.IsNullOrEmpty(text)) Say(text);
        }

        private static void CloseNativeCreation()
        {
            // Its hooks let go (OnDisable); not Disable, which is the selection's way back.
            try { if (_native != null) _native.OnDisable(); } catch { }
            _native = null;
            try { _creationWindow?.RemoveFromHierarchy(); } catch { }
            _creationWindow = null;
            _cTree = null;
            _cLook = null;
        }

        // The screen as it is filled: its tree, the breeds and heads the game's data has, what is chosen.
        private static VisualElement _cTree;
        private static readonly List<(int Id, string Name, string Description, int Complexity)> _cBreeds = new List<(int, string, string, int)>();
        private static readonly List<(int Id, string Asset)> _cHeads = new List<(int, string)>();
        private static readonly List<Il2CppCore.UILogic.Components.Slots.Slot> _cBreedSlots = new List<Il2CppCore.UILogic.Components.Slots.Slot>();
        private static readonly List<Il2CppCore.UILogic.Components.Slots.Slot> _cHeadSlots = new List<Il2CppCore.UILogic.Components.Slots.Slot>();
        private static int _cBreed = 8, _cSex, _cHead, _cDirection = 1;
        private static bool _cBusy;
        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType _breedImages, _headImages;

        private static T Part<T>(string name) where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            var found = _cTree == null ? null : UQueryExtensions.Q(_cTree, name, (string)null);
            return found?.TryCast<T>();
        }

        private static VisualElement Part(string name) => _cTree == null ? null : UQueryExtensions.Q(_cTree, name, (string)null);

        private static void Show(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = new StyleEnum<DisplayStyle>(shown ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private static void OnClick(VisualElement element, Action action)
        {
            if (element == null) return;
            element.RegisterCallback<ClickEvent>(DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(new Action<ClickEvent>(_ =>
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[Heroes] {ex.Message}"); }
            })), TrickleDown.NoTrickleDown);
        }

        private static void SetText(string name, string text)
        {
            var label = Part<TextElement>(name);
            if (label != null) label.text = text;
        }

        /// <summary>
        /// The screen filled from the game's own data, as its own screen fills it: the breeds
        /// (DataCenter's breeds, their images out of the game's addressables), the sex, the heads
        /// for them, the character on his platform, a name, and the button that makes him. What the
        /// hero mode does not use yet -- the server's choice, the attitudes, the colours, the spells
        /// -- put away.
        /// </summary>
        private static void BindCreation(VisualElement tree)
        {
            _cTree = tree;
            _cBusy = false;
            _toldFraming = false;
            foreach (string away in new[] { "parentChangeServer", "btnLeave", "bloc_attitude", "btn_showSpells", "btn_mechanics",
                                            "ctr_colors", "lbl_corps", "ctr_corps", "tg_showEquipment", "lbl_helpToChoose",
                                            "window_spells", "window_mechanics" })
                Show(Part(away), false);
            var role = Part("tg_role_damageDealer");
            Show(role?.hierarchy.parent, false);

            SetText("lbl_breedTitle", T("create.class"));
            SetText("lbl_visage", T("create.face"));
            SetText("lbl_playerNameTitle", T("create.namePlaceholder"));
            var play = Part<Il2CppCore.UILogic.Components.Figma.PlayButton>("btn_play");
            if (play != null)
            {
                play.text = T("create.go");
                play.add_Clicked(Do(CreateFromScreen));
            }
            ChooseImageTypes();
            LoadBreeds();
            BindBreedSlots();
            OnClick(Part("btn_previous"), () => StepBreed(-1));
            OnClick(Part("btn_next"), () => StepBreed(1));
            OnClick(Part("btn_rotateLeft"), () => { _cDirection = (_cDirection + 7) % 8; RefreshPreview(); });
            OnClick(Part("btn_rotateRight"), () => { _cDirection = (_cDirection + 1) % 8; RefreshPreview(); });

            var gender = Part<Il2CppCore.UILogic.Components.Figma.Switch>("switch_gender");
            if (gender != null)
            {
                gender.SetValueWithoutNotify(_cSex == 0);
                gender.add_clicked(Do(() =>
                {
                    _cSex = gender.isChecked ? 0 : 1;
                    _cHead = 0;
                    RefreshBreed();
                }));
            }

            var input = Part<Il2CppCore.UILogic.Components.Figma.TextInput>("input_name");
            if (input != null) input.placeholderText = T("create.namePlaceholder");
            OnClick(Part("btn_generateName"), AskScreenName);
            Show(Part("textInput_button"), false);
            if (!_cBreeds.Any(b => b.Id == _cBreed) && _cBreeds.Count > 0) _cBreed = _cBreeds[0].Id;
            RefreshBreed();
            AskScreenName();
        }

        /// <summary>
        /// Which of the game's addressable kinds hold the breeds' images and the heads': 3.6.10 keeps
        /// them on rd, nameless; their path prefixes tell. All of them logged once.
        /// </summary>
        private static void ChooseImageTypes()
        {
            if (_breedImages != null && _headImages != null && _symbolImages != null) return;
            var seen = new List<string>();
            foreach (var property in typeof(Il2Cpp.rd).GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
            {
                if (property.PropertyType != typeof(Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType)) continue;
                Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType kind;
                try { kind = (Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType)property.GetValue(null); }
                catch { continue; }
                string prefix = kind?.pathPrefix ?? "";
                seen.Add(prefix);
                // The breeds' portraits by breed and sex, the heads by their asset (3.6.10's log).
                if (prefix == "classes/heads/small/Head_{0}{1}") _breedImages = kind;
                else if (prefix == "cosmetics/{0}") _headImages = kind;
                else if (prefix == "classes/symbol_{0}") _symbolImages = kind;
            }
            MelonLogger.Msg($"[Heroes] The game's addressable kinds: {string.Join(" | ", seen)}; breeds {(_breedImages?.pathPrefix ?? "none")}, heads {(_headImages?.pathPrefix ?? "none")}.");
        }

        /// <summary>The breeds the hero mode knows, out of the game's data, in its own order.</summary>
        private static void LoadBreeds()
        {
            _cBreeds.Clear();
            try
            {
                var all = Il2CppCore.DataCenter.DataCenterModule.breedsDataRoot.GetObjects();
                var found = new List<(int Id, string Name, string Description, int Complexity, int Order)>();
                foreach (var breed in all)
                {
                    if (breed == null || !Classes.ContainsKey(breed.id)) continue;
                    found.Add((breed.id, breed.shortName ?? ClassOf(breed.id), breed.description ?? "", breed.complexity, breed.sortIndex));
                }
                foreach (var b in found.OrderBy(b => b.Order)) _cBreeds.Add((b.Id, b.Name, b.Description, b.Complexity));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] The game's breeds could not be read, the mod's own instead: {ex.Message}");
            }
            if (_cBreeds.Count == 0)
                foreach (int id in Classes.Keys.OrderBy(k => k)) _cBreeds.Add((id, ClassOf(id), "", 0));
        }

        private static void BindBreedSlots()
        {
            _cBreedSlots.Clear();
            var container = Part("ctr_breeds");
            if (container == null) return;
            var slots = new List<Il2CppCore.UILogic.Components.Slots.Slot>();
            Il2CppCore.UILogic.Components.Slots.Slot random = null;
            for (int i = 0; i < Kids(container); i++)
            {
                var slot = Kid(container, i).TryCast<Il2CppCore.UILogic.Components.Slots.Slot>();
                if (slot == null) continue;
                if (slot.name == "btn_randomBreed") random = slot;
                else slots.Add(slot);
            }
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (i >= _cBreeds.Count) { Show(slot, false); continue; }
                int id = _cBreeds[i].Id;
                slot.isFilled = true;
                OnClick(slot, () => { _cBreed = id; _cHead = 0; RefreshBreed(); });
                _cBreedSlots.Add(slot);
            }
            if (random != null)
            {
                try
                {
                    random.iconValue = Il2CppCore.UILogic.Components.Figma.FigmaIcons.random;
                    random.isFilled = false;
                }
                catch (Exception ex) { MelonLogger.Warning($"[Heroes] The random breed's icon: {ex.Message}"); }
            }
            if (random != null)
                OnClick(random, () =>
                {
                    _cBreed = _cBreeds[new Random().Next(_cBreeds.Count)].Id;
                    _cHead = 0;
                    RefreshBreed();
                });
        }

        private static void StepBreed(int step)
        {
            int at = _cBreeds.FindIndex(b => b.Id == _cBreed);
            if (at < 0 || _cBreeds.Count == 0) return;
            _cBreed = _cBreeds[(at + step + _cBreeds.Count) % _cBreeds.Count].Id;
            _cHead = 0;
            RefreshBreed();
        }

        /// <summary>Everything that follows the breed and the sex: the slots' images and selection, the texts, the heads, the character.</summary>
        private static void RefreshBreed()
        {
            if (_cTree == null) return;
            for (int i = 0; i < _cBreedSlots.Count && i < _cBreeds.Count; i++)
            {
                var slot = _cBreedSlots[i];
                slot.isSelected = _cBreeds[i].Id == _cBreed;
                if (_breedImages != null && slot.tile != null)
                    try { slot.tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(_breedImages, _cBreeds[i].Id, _cSex); } catch { }
            }
            var breed = _cBreeds.FirstOrDefault(b => b.Id == _cBreed);
            BreedSymbol();
            SetText("lbl_breed", (breed.Name ?? ClassOf(_cBreed)).ToUpperInvariant());
            SetText("lbl_breedInfo", breed.Description ?? "");
            SetText("lbl_archetype", ClassOf(_cBreed));
            for (int i = 1; i <= 5; i++) Show(Part("icon_complexity_" + i), i <= Math.Max(1, breed.Complexity));
            RefreshHeads();
            RefreshPreview();
        }

        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType _symbolImages;

        /// <summary>
        /// The breed's symbol over its name: the game's classes/symbol_{0} image, shown by a tile of
        /// its own laid over the template's, whose own picture is the template's breed's.
        /// </summary>
        private static void BreedSymbol()
        {
            var place = Part("tx_breedSymbol");
            if (place == null || _symbolImages == null) return;
            try
            {
                var tile = Part<Il2CppCore.UILogic.Components.Slots.Tile>("JondoHeroesBreedSymbol");
                if (tile == null)
                {
                    tile = new Il2CppCore.UILogic.Components.Slots.Tile();
                    tile.name = "JondoHeroesBreedSymbol";
                    tile.pickingMode = PickingMode.Ignore;
                    tile.style.position = new StyleEnum<Position>(Position.Absolute);
                    tile.style.left = tile.style.top = new StyleLength(0f);
                    tile.style.width = tile.style.height = new StyleLength(new Length(100f, LengthUnit.Percent));
                    place.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                    place.Add(tile);
                }
                tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(_symbolImages, _cBreed);
            }
            catch (Exception ex) { MelonLogger.Warning($"[Heroes] The breed's symbol: {ex.Message}"); }
        }

        /// <summary>The heads the game offers at creation for the breed and the sex, in its order.</summary>
        private static void RefreshHeads()
        {
            _cHeads.Clear();
            try
            {
                var all = Il2CppCore.DataCenter.DataCenterModule.headsDataRoot.GetObjects();
                var found = new List<(int Id, string Asset, int Order)>();
                foreach (var head in all)
                    if (head != null && head.breed == _cBreed && head.gender == _cSex && head.availableAtCreation)
                        found.Add((head.id, head.assetId ?? "", head.order));
                foreach (var h in found.OrderBy(h => h.Order)) _cHeads.Add((h.Id, h.Asset));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] The game's heads could not be read: {ex.Message}");
            }
            if (_cHead == 0 && _cHeads.Count > 0) _cHead = _cHeads[0].Id;

            var container = Part("ctr_heads");
            if (container == null) return;
            if (_cHeadSlots.Count == 0 || _cHeadSlots[0].panel == null)
            {
                _cHeadSlots.Clear();
                for (int i = 0; i < Kids(container); i++)
                {
                    var slot = Kid(container, i).TryCast<Il2CppCore.UILogic.Components.Slots.Slot>();
                    if (slot == null) continue;
                    int at = _cHeadSlots.Count;
                    OnClick(slot, () => { if (at < _cHeads.Count) { _cHead = _cHeads[at].Id; RefreshHeadSelection(); RefreshPreview(); } });
                    _cHeadSlots.Add(slot);
                }
            }
            for (int i = 0; i < _cHeadSlots.Count; i++)
            {
                var slot = _cHeadSlots[i];
                bool shown = i < _cHeads.Count;
                Show(slot, shown);
                if (!shown) continue;
                slot.isFilled = true;
                if (_headImages != null && slot.tile != null)
                    try { slot.tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(_headImages, _cHeads[i].Asset, false); } catch { }
            }
            RefreshHeadSelection();
        }

        private static void RefreshHeadSelection()
        {
            for (int i = 0; i < _cHeadSlots.Count && i < _cHeads.Count; i++) _cHeadSlots[i].isSelected = _cHeads[i].Id == _cHead;
        }

        /// <summary>The character on the platform: the look the server makes of the choice, drawn by the screen's own displayer.</summary>
        private static void RefreshPreview()
        {
            var displayer = Part<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer>("ed_chara");
            if (displayer == null || _displayers == null || _entities == null) return;
            int breed = _cBreed, sex = _cSex, head = _cHead, direction = _cDirection;
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/preview", new Dictionary<string, object> { ["breed"] = breed, ["sex"] = sex, ["head"] = head });
                _toUnity.Enqueue(() =>
                {
                    if (code != 200 || body is not JsonElement b || _cTree == null) return;
                    if (breed != _cBreed || sex != _cSex || head != _cHead) return;
                    try
                    {
                        _cLook = LookOf(Str(b, "look"));
                        _cDrawn = 0;
                        DrawPreview(displayer, direction);
                    }
                    catch (Exception ex) { MelonLogger.Warning($"[Heroes] The creation's character could not be drawn: {ex.Message}"); }
                });
            });
        }

        // The look on the platform, how many times it was given, and when it is given again.
        private static Il2CppCore.Rendering.Look.EntityLook _cLook;
        private static int _cDrawn;
        private static float _cNextDraw;
        private static bool _toldFraming, _cFramed, _toldFreeze;

        /// <summary>
        /// The platform's character's forced size: the bigger it is the smaller he is drawn (0.32
        /// drew him over twice the template's 0.75). 1.8 for the native screen's look in this window.
        /// </summary>
        private static float PreviewSize => Tune("previewSize", 1.8f);

        // ─── Tuning without a restart ────────────────────────────────────────────────────

        private static readonly Dictionary<string, float> _tuning = new Dictionary<string, float>();
        private static DateTime _tuningRead;

        private static string TuningFile => System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "HeroesTuning.cfg");

        /// <summary>
        /// A number the mod draws with, out of UserData\HeroesTuning.cfg («name = value» a line)
        /// when it is there, else its default: read again whenever the file changes, so a size is
        /// set right with the game running. Code itself is only changed by a restart.
        /// </summary>
        private static float Tune(string name, float byDefault)
            => _tuning.TryGetValue(name, out float value) ? value : byDefault;

        /// <summary>Every scan: the file read again when it changed, and what is on screen drawn again with it.</summary>
        private static void ReadTuning()
        {
            string file = TuningFile;
            if (!System.IO.File.Exists(file)) return;
            var written = System.IO.File.GetLastWriteTimeUtc(file);
            if (written == _tuningRead) return;
            _tuningRead = written;
            _tuning.Clear();
            foreach (string line in System.IO.File.ReadAllLines(file))
            {
                string[] parts = line.Split('=');
                if (parts.Length == 2 && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                    _tuning[parts[0].Trim()] = value;
            }
            MelonLogger.Msg($"[Heroes] Tuning read: {string.Join(", ", _tuning.Select(t => t.Key + " = " + t.Value.ToString(CultureInfo.InvariantCulture)))}");
            // Drawn again with it: the creation's character framed, the portraits made anew.
            _cFramed = false;
            foreach (var made in _live.Values) Release(made.View);
            _live.Clear();
            _windowKey = null;
            _panelKey = null;
            _nextPoll = 0f;
        }

        /// <summary>
        /// The look given to the platform's displayer, its framing (Frame) set after: the giving
        /// sets its own. A breed's first look only starts its loading and shows nothing; it is
        /// given again (WatchPreview) until its character is there.
        /// </summary>
        private static void DrawPreview(Il2CppCore.UILogic.EntityDisplay.EntityDisplayer displayer, int direction)
        {
            if (_cLook == null) return;
            _cDrawn++;
            _cFramed = false;
            _cNextDraw = UnityEngine.Time.unscaledTime + 0.5f;
            try { displayer.autosize = false; } catch { }
            try { displayer.DestroyCharacterIfNeeded(); } catch { }
            Il2CppCore.UI.UIUtils.pcb(displayer, _cLook, (Il2Cpp.rc)direction, 0, _displayers, _entities);
            Frame(displayer);
        }

        /// <summary>
        /// The platform's displayer made to fit the window: the template's box is 1200 pixels, the
        /// native full screen's, and the character drawn to it filled the window. The box itself is
        /// shrunk around where the feet stand -- its ratio down it -- so he stands on the platform,
        /// the size the native screen shows him. It is given to the displaying service too, which
        /// moves the displayers it keeps: left out, the character stopped after a few seconds.
        /// </summary>
        private static void Frame(Il2CppCore.UILogic.EntityDisplay.EntityDisplayer displayer)
        {
            try
            {
                // Not fitted to the box, as the native screen has it: fitted, he was not drawn at all.
                // His size is the displayer's forced size, not its box's: the box only cuts him off
                // (shrunk, it cut his head). 0.75 in the template drew him some 2.4 times the
                // native screen's size in this window; the box is left the client's own.
                displayer.autosize = false;
                displayer.forcedSize = PreviewSize;
                displayer.style.scale = new StyleScale(StyleKeyword.Null);
                displayer.style.transformOrigin = new StyleTransformOrigin(StyleKeyword.Null);
                displayer.ForceRefreshSize();
                _displayers?.bdvx(displayer.Cast<Il2CppCore.UILogic.EntityDisplay.IEntityDisplayer>());
                displayer.SetPause(false);
            }
            catch (Exception ex) { MelonLogger.Warning($"[Heroes] The creation's character not framed: {ex.Message}"); }
        }

        /// <summary>Every frame while the creation screen is open: the platform's character given again until he is there.</summary>
        private static void WatchPreview()
        {
            if (_cTree == null || _cLook == null) return;
            var displayer = Part<Il2CppCore.UILogic.EntityDisplay.EntityDisplayer>("ed_chara");
            if (displayer == null) return;
            if (displayer.character != null)
            {
                // Kept moving: whatever pauses or freezes it is undone.
                try
                {
                    bool frozen = displayer.freezeUpdate, paused = displayer.m_isPaused;
                    if (frozen) displayer.freezeUpdate = false;
                    if (paused) displayer.SetPause(false);
                    if ((frozen || paused) && !_toldFreeze)
                    {
                        _toldFreeze = true;
                        MelonLogger.Msg($"[Heroes] The creation's character was {(frozen ? "frozen" : "")}{(frozen && paused ? " and " : "")}{(paused ? "paused" : "")}: set moving again.");
                    }
                }
                catch { }
                if (_cFramed) return;
                _cFramed = true;
                Frame(displayer);
                if (!_toldFraming)
                {
                    _toldFraming = true;
                    MelonLogger.Msg($"[Heroes] The creation's character: autosize {displayer.autosize}, scale {displayer.scale}, " +
                                    $"style scale {displayer.resolvedStyle.scale.value.x}, forced {displayer.forcedSize}, ratio {displayer.ratioPositioning}, " +
                                    $"box {displayer.worldBound}, layout {displayer.layout}, texture {(displayer.m_renderTexture != null ? displayer.m_renderTexture.width + "x" + displayer.m_renderTexture.height : "none")}, " +
                                    $"after {_cDrawn} giving(s).");
                }
                return;
            }
            if (_cDrawn >= 6 || UnityEngine.Time.unscaledTime < _cNextDraw) return;
            DrawPreview(displayer, _cDirection);
        }

        private static void AskScreenName()
        {
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/name");
                _toUnity.Enqueue(() =>
                {
                    if (code != 200 || body is not JsonElement b || _cTree == null) return;
                    Part<Il2CppCore.UILogic.Components.Figma.TextInput>("input_name")?.SetValue(Str(b, "name"));
                });
            });
        }

        private static void ScreenError(string text) => NameError(_cTree, text);

        /// <summary>The screen's CRÉER: the hero made by the server on the creation screen's rules and connected beside his master.</summary>
        private static void CreateFromScreen()
        {
            if (_cBusy || _cTree == null) return;
            string name = (Part<Il2CppCore.UILogic.Components.Figma.TextInput>("input_name")?.text ?? "").Trim();
            if (name.Length == 0) { ScreenError(T("create.error.empty")); return; }
            _cBusy = true;
            ScreenError("");
            var ask = new Dictionary<string, object> { ["name"] = name, ["breed"] = _cBreed, ["sex"] = _cSex, ["head"] = _cHead };
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/create", ask);
                _toUnity.Enqueue(() =>
                {
                    _cBusy = false;
                    if (code == 200 && body is JsonElement done)
                    {
                        MelonLogger.Msg($"[Heroes] {name} made from the creation screen.");
                        CloseNativeCreation();
                        Say(T(Bool(done, "connected") ? "create.done" : "create.doneAlone", name), good: true);
                        _windowKey = null;
                        Poll();
                        return;
                    }
                    string why = body is JsonElement b ? Str(b, "error") : "";
                    ScreenError(code == 0 ? T("unreachable") : Texts.ContainsKey("create.error." + why) ? T("create.error." + why) : T("error"));
                });
            });
        }

        /// <summary>Every named part of a tree, its type and its depth, a line each: the creation screen's map.</summary>
        private static void Parts(VisualElement element, int depth, System.Text.StringBuilder into)
        {
            if (into.Length > 30000 || depth > 14) return;
            if (!string.IsNullOrEmpty(element.name))
                into.Append(new string(' ', depth)).Append(element.GetIl2CppType().Name).Append(" #").Append(element.name)
                    .Append(" (").Append(Kids(element)).Append(")\n");
            for (int i = 0; i < Kids(element); i++) Parts(Kid(element, i), depth + 1, into);
        }

        private static void StartCreation()
        {
            _windowKey = null;
            _creating = true;
            _creationBuilt = false;
            _newName = "";
            Say("");
            Render();
            AskName();
        }

        private static void StopCreation()
        {
            _windowKey = null;
            _creating = false;
            _creationBuilt = false;
            _creationBusy = false;
            Render();
        }

        /// <summary>
        /// The mod's own form, a name, a class and a sex: the server makes the character with
        /// its class's colours, on the creation screen's rules, and connects it as a hero at once.
        /// </summary>
        private static void RenderCreation()
        {
            if (_creationBuilt) return;
            _creationBuilt = true;
            _grid.Clear();
            var box = new VisualElement();
            box.style.flexGrow = new StyleFloat(1f);
            box.style.paddingLeft = box.style.paddingRight = new StyleLength(40f);
            box.style.paddingTop = new StyleLength(10f);

            var title = Label(T("create.title"), 20f, true);
            title.style.marginBottom = new StyleLength(14f);
            box.Add(title);

            box.Add(Caption(T("create.class")));
            var classes = new VisualElement();
            classes.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            classes.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            classes.style.marginBottom = new StyleLength(14f);
            foreach (int breed in Classes.Keys.OrderBy(k => k))
            {
                int which = breed;
                var choice = Button(Do(() => { _newBreed = which; _creationBuilt = false; RenderCreation(); }),
                                    ClassOf(breed), breed == _newBreed, true);
                choice.style.width = new StyleLength(130f);
                choice.style.marginRight = choice.style.marginBottom = new StyleLength(6f);
                classes.Add(choice);
            }
            box.Add(classes);

            box.Add(Caption(T("create.sex")));
            var sexes = new VisualElement();
            sexes.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            sexes.style.marginBottom = new StyleLength(14f);
            for (int sex = 0; sex < 2; sex++)
            {
                int which = sex;
                var choice = Button(Do(() => { _newSex = which; _creationBuilt = false; RenderCreation(); }),
                                    T(sex == 0 ? "create.male" : "create.female"), sex == _newSex, true);
                choice.style.width = new StyleLength(130f);
                choice.style.marginRight = new StyleLength(6f);
                sexes.Add(choice);
            }
            box.Add(sexes);

            box.Add(Caption(T("create.name")));
            var naming = new VisualElement();
            naming.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            naming.style.alignItems = new StyleEnum<Align>(Align.Center);
            naming.style.marginBottom = new StyleLength(18f);
            var field = new Il2CppCore.UILogic.Components.DofusTextField();
            field.name = "JondoHeroesNewName";
            field.maxLength = 20;
            field.placeholder = T("create.namePlaceholder");
            field.text = _newName;
            field.style.width = new StyleLength(280f);
            field.style.marginRight = new StyleLength(8f);
            field.RegisterValueChangedCallback(DelegateSupport.ConvertDelegate<EventCallback<ChangeEvent<string>>>(
                new Action<ChangeEvent<string>>(e => _newName = e.newValue ?? "")));
            naming.Add(field);
            naming.Add(Button(Do(AskName), T("create.random"), false, true));
            box.Add(naming);

            var actions = new VisualElement();
            actions.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            var cancel = Button(Do(StopCreation), T("create.cancel"), false, !_creationBusy);
            cancel.style.marginRight = new StyleLength(8f);
            actions.Add(cancel);
            actions.Add(Button(Do(Create), T(_creationBusy ? "create.working" : "create.go"), true, !_creationBusy));
            box.Add(actions);
            _grid.Add(box);
        }

        private static DofusLabel Caption(string text)
        {
            var caption = Label(text, 12f, true);
            caption.style.color = new StyleColor(new UnityEngine.Color(0.66f, 0.68f, 0.78f, 1f));
            caption.style.letterSpacing = new StyleLength(1.5f);
            caption.style.marginBottom = new StyleLength(6f);
            return caption;
        }

        /// <summary>A name the server draws, one nobody has, into the field.</summary>
        private static void AskName()
        {
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/name");
                _toUnity.Enqueue(() =>
                {
                    if (code != 200 || body is not JsonElement b || !_creating) return;
                    _newName = Str(b, "name");
                    var fields = new List<VisualElement>();
                    if (_grid != null) Named(_grid, "JondoHeroesNewName", fields);
                    foreach (var one in fields) one.TryCast<Il2CppCore.UILogic.Components.DofusTextField>()?.SetValueWithoutNotify(_newName);
                });
            });
        }

        private static void Create()
        {
            if (_creationBusy) return;
            string name = (_newName ?? "").Trim();
            if (name.Length == 0) { Say(T("create.error.empty")); return; }
            _creationBusy = true;
            _creationBuilt = false;
            Say("");
            RenderCreation();
            var ask = new Dictionary<string, object> { ["name"] = name, ["breed"] = _newBreed, ["sex"] = _newSex };
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/create", ask);
                _toUnity.Enqueue(() =>
                {
                    _creationBusy = false;
                    if (code == 200 && body is JsonElement done)
                    {
                        MelonLogger.Msg($"[Heroes] {name} made from the window.");
                        StopCreation();
                        Say(T(Bool(done, "connected") ? "create.done" : "create.doneAlone", name), good: true);
                        Poll();
                        return;
                    }
                    string why = body is JsonElement b ? Str(b, "error") : "";
                    _creationBuilt = false;
                    RenderCreation();
                    Say(code == 0 ? T("unreachable") : Texts.ContainsKey("create.error." + why) ? T("create.error." + why) : T("error"));
                });
            });
        }

        private static VisualElement Place()
        {
            var place = new VisualElement();
            place.style.flexGrow = new StyleFloat(1f);
            place.style.flexBasis = new StyleLength(0f);
            place.style.marginLeft = new StyleLength(6f);
            place.style.marginRight = new StyleLength(6f);
            place.style.marginTop = new StyleLength(6f);
            place.style.marginBottom = new StyleLength(6f);
            return place;
        }

        /// <summary>An empty place; with <paramref name="canMake"/>, one a hero can be made on: a «+» and its button.</summary>
        private static VisualElement Empty(bool canMake = false)
        {
            var place = Place();
            place.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.13f, 0.14f, 0.22f, 0.55f));
            Round(place, 3f);
            if (!canMake) return place;
            place.style.alignItems = new StyleEnum<Align>(Align.Center);
            place.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            var plus = Label("+", 48f, true);
            plus.style.color = new StyleColor(new UnityEngine.Color(0.55f, 0.57f, 0.70f, 1f));
            plus.style.marginBottom = new StyleLength(8f);
            place.Add(plus);
            place.Add(Button(Do(OpenNativeCreation), T("create"), true, true));
            return place;
        }

        private static VisualElement Card(JsonElement who, bool roomLeft, bool masterFighting)
        {
            long id = Long(who, "id");
            bool master = Bool(who, "master"), hero = Bool(who, "hero"), busy = Bool(who, "busy");
            bool connected = master || hero;

            var card = Place();
            card.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.11f, 0.11f, 0.17f, 0.95f));
            card.style.borderTopWidth = card.style.borderBottomWidth = card.style.borderLeftWidth = card.style.borderRightWidth = new StyleFloat(2f);
            var edge = master ? new UnityEngine.Color(0.85f, 0.70f, 0.30f, 1f)
                     : hero ? new UnityEngine.Color(0.40f, 0.40f, 0.52f, 1f)
                     : new UnityEngine.Color(0.22f, 0.22f, 0.30f, 1f);
            card.style.borderTopColor = card.style.borderBottomColor = card.style.borderLeftColor = card.style.borderRightColor = new StyleColor(edge);
            Round(card, 3f);

            // The head: the name and the level, a crown for the master, a light for the connected.
            var head = new VisualElement();
            head.style.height = new StyleLength(50f);
            head.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            head.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.24f, 0.24f, 0.32f, 1f));
            var name = Label(Str(who, "name"), 15f, true);
            name.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            name.style.marginBottom = new StyleLength(0f);
            if (master) name.style.color = new StyleColor(Gold);
            head.Add(name);
            var level = Label(T("level", Int(who, "level")), 12f);
            level.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            level.style.color = new StyleColor(new UnityEngine.Color(0.70f, 0.70f, 0.78f, 1f));
            level.style.marginBottom = new StyleLength(0f);
            head.Add(level);
            if (master) head.Add(Crown());
            head.Add(Light(connected));
            card.Add(head);

            // The portrait, on its glow.
            var frame = new VisualElement();
            frame.style.flexGrow = new StyleFloat(1f);
            frame.style.marginLeft = frame.style.marginRight = frame.style.marginTop = new StyleLength(8f);
            frame.style.backgroundImage = new StyleBackground(Glow());
            frame.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Cover));
            var picture = new VisualElement();
            picture.style.flexGrow = new StyleFloat(1f);
            picture.style.marginTop = picture.style.marginBottom = new StyleLength(10f);
            // The client's own portrait, alive: seated when he is not in the world, standing when
            // he is. The server's still drawing when the client's cannot be had.
            // The drawn portrait under it until the live one has its character (WatchLive).
            var live = LivePortrait("card", id, Str(who, "look"), seated: !connected);
            bool alive = live != null && HasCharacter(live);
            if (_portraits.TryGetValue(id, out var portrait))
            {
                if (!alive) picture.style.backgroundImage = new StyleBackground(portrait);
                picture.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
            }
            else AskPortrait(id);
            if (live != null) picture.Add(live);
            frame.Add(picture);
            if (!connected && !alive) frame.style.opacity = new StyleFloat(0.45f);
            card.Add(frame);

            // What can be done with him.
            var foot = new VisualElement();
            foot.style.height = new StyleLength(44f);
            foot.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            foot.style.alignItems = new StyleEnum<Align>(Align.Center);
            foot.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            // A hero's bag, sheet and spells, to look at.
            if (hero)
            {
                string heroName = Str(who, "name");
                for (int view = 0; view < 3; view++)
                {
                    int which = view;
                    var icon = Toggle(Do(() => OpenSheet(id, heroName, which)), Glyph(3 + which), false);
                    icon.style.width = new StyleLength(28f);
                    icon.style.marginRight = new StyleLength(4f);
                    foot.Add(Hint(icon, which == 0 ? "hint.bag" : which == 1 ? "hint.stats" : "hint.spells"));
                }
            }
            if (master) foot.Add(Note(T("master")));
            else if (Bool(who, "inFight")) foot.Add(Note(T("fighting")));
            else if (hero) foot.Add(Button(Do(() => Act("heroes/disconnect", id)), T("disconnect"), false, !masterFighting));
            else if (busy) foot.Add(Note(T("busy")));
            else foot.Add(Button(Do(() => Act("heroes/connect", id)), T("connect"), true, roomLeft && !masterFighting));
            card.Add(foot);
            return card;
        }

        // ─── The menus' button ───────────────────────────────────────────────────────────

        private static UnityEngine.Texture2D _icon;
        private static string _toldMenu;

        /// <summary>
        /// A Heroes button in the MENUS window's grid (BannerMenuBinding's gd_btnUis), in the free
        /// place after its last button, the size of the others; placed again every scan, as the
        /// window is drawn again or resized, and put away when no place is free.
        /// </summary>
        /// <remarks>
        /// Laid over the grid, not into one of its rows: the grid is a scroll view as wide as its
        /// rows, and a button put after a row's last slot was there all along, clipped out of sight.
        /// </remarks>
        private static void PutMenuButton()
        {
            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            var grids = new List<VisualElement>();
            for (int i = 0; i < documents.Length; i++)
            {
                var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                if (root != null) GridsUnder(root, grids);
            }
            grids = grids.Where(g => g.panel != null && g.resolvedStyle.display != DisplayStyle.None
                                     && g.resolvedStyle.visibility == Visibility.Visible && g.worldBound.width > 4f).ToList();
            var main = grids.FirstOrDefault(g => g.name == "gd_btnUis");
            if (main == null) return;

            // The menu's own grids: its grid of buttons first, then whatever holds the buttons it
            // has no room for -- the «+»'s, the list it pops up --, as the game puts its own there.
            var candidates = new List<VisualElement> { main };
            candidates.AddRange(grids.Where(g => g != main && OfTheMenu(g)));
            string key = string.Join(" | ", grids.Select(g => $"{g.name}{(candidates.Contains(g) ? "*" : "")}"));
            if (key != _toldGrids)
            {
                _toldGrids = key;
                MelonLogger.Msg($"[Heroes] Slot grids shown: {key}");
            }

            VisualElement host = null, model = null, other = null;
            (float, float, UnityEngine.Vector2) place = default;
            foreach (var grid in candidates)
            {
                var found = FreePlace(grid, out var m, out var o);
                if (found == null) continue;
                host = grid; model = m; other = o; place = found.Value;
                break;
            }

            // No free slot in sight: hidden, as the game's own buttons folded behind its «+» are.
            if (host == null)
            {
                if (_menuButton != null) _menuButton.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
                return;
            }

            if (_menuButton == null || _menuButton.panel == null) _menuButton = MenuButton(host);
            else if (_menuButton.hierarchy.parent != host)
            {
                host.hierarchy.Add(_menuButton);
                _dressedFor = default;
            }
            var (x, y, slot) = place;
            _menuButton.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
            _menuButton.style.left = new StyleLength(x);
            _menuButton.style.top = new StyleLength(y);
            _menuButton.style.width = new StyleLength(slot.x);
            _menuButton.style.height = new StyleLength(slot.y);
            if (_dressedFor != slot)
            {
                _dressedFor = slot;
                Dress(_menuButton, model, other, slot);
            }
        }

        private static VisualElement _menuButton;
        private static string _toldGrids;

        /// <summary>Every slot grid of the client's under an element.</summary>
        private static void GridsUnder(VisualElement element, List<VisualElement> into)
        {
            for (int i = 0; i < Kids(element); i++)
            {
                var child = Kid(element, i);
                if (child.TryCast<Il2CppCore.UILogic.Components.Slots.SlotGrid>() != null) into.Add(child);
                else GridsUnder(child, into);
            }
        }

        /// <summary>
        /// Whether a grid belongs to the MENUS window: under it, or named as its buttons' grids are
        /// (gd_btnUis, gd_additionalBtns), or under something named for the banner -- the list the
        /// «+» pops up may stand on a layer of its own.
        /// </summary>
        private static bool OfTheMenu(VisualElement grid)
        {
            if (grid.name != null && grid.name.IndexOf("btn", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (var up = grid.hierarchy.parent; up != null; up = up.hierarchy.parent)
                if (up.name != null && (up.name.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) >= 0
                                        || up.name.IndexOf("moreBtn", StringComparison.OrdinalIgnoreCase) >= 0
                                        || up.name.IndexOf("additional", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            return false;
        }

        /// <summary>The slot size the grid's button was last dressed for.</summary>
        private static UnityEngine.Vector2 _dressedFor;

        /// <summary>
        /// The game's own look laid into the button, layer by layer off one of its slots: what two
        /// slots both show -- the frame, its border, its back -- is copied, and the picture they
        /// differ in, which is each one's icon, is ours (<see cref="Figure"/>).
        /// </summary>
        private static void Dress(VisualElement button, VisualElement model, VisualElement other, UnityEngine.Vector2 size)
        {
            button.Clear();
            bool icon = false;
            if (model != null) Layers(model, other, model.worldBound, button, ref icon);
            if (!icon)
            {
                var picture = Layer(size.x * 0.14f, size.y * 0.14f, size.x * 0.72f, size.y * 0.72f);
                picture.style.backgroundImage = new StyleBackground(Figure());
                picture.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
                button.Add(picture);
            }

            // The light the pointer turns on: the frame's shape, a little paler.
            float radius = 0f;
            for (int i = 0; i < button.childCount && radius <= 0f; i++)
                radius = button[i].style.borderTopLeftRadius.value.value;
            var light = Layer(0f, 0f, size.x, size.y);
            light.name = "JondoHeroesLight";
            light.style.backgroundColor = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.16f));
            light.style.borderTopLeftRadius = light.style.borderTopRightRadius =
                light.style.borderBottomLeftRadius = light.style.borderBottomRightRadius = new StyleLength(radius > 0f ? radius : 4f);
            light.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            button.Add(light);
        }

        /// <summary>The button lit or not, as the pointer is on it or not.</summary>
        private static void Lit(VisualElement button, bool on)
        {
            try
            {
                var light = new List<VisualElement>();
                Named(button, "JondoHeroesLight", light);
                foreach (var one in light) one.style.display = new StyleEnum<DisplayStyle>(on ? DisplayStyle.Flex : DisplayStyle.None);
            }
            catch { }
        }

        private static void Layers(VisualElement a, VisualElement b, UnityEngine.Rect origin, VisualElement into, ref bool icon)
        {
            var s = a.resolvedStyle;
            if (s.display == DisplayStyle.None || s.visibility != Visibility.Visible || s.opacity < 0.02f) return;
            // Its words -- a shortcut's letter, a count -- are its own, not the frame's.
            if (a.TryCast<TextElement>() != null) return;
            var r = a.worldBound;
            if (r.width >= 1f && r.height >= 1f)
            {
                var layer = Layer(r.x - origin.x, r.y - origin.y, r.width, r.height);
                layer.style.backgroundColor = new StyleColor(s.backgroundColor);
                layer.style.borderTopWidth = new StyleFloat(s.borderTopWidth);
                layer.style.borderBottomWidth = new StyleFloat(s.borderBottomWidth);
                layer.style.borderLeftWidth = new StyleFloat(s.borderLeftWidth);
                layer.style.borderRightWidth = new StyleFloat(s.borderRightWidth);
                layer.style.borderTopColor = new StyleColor(s.borderTopColor);
                layer.style.borderBottomColor = new StyleColor(s.borderBottomColor);
                layer.style.borderLeftColor = new StyleColor(s.borderLeftColor);
                layer.style.borderRightColor = new StyleColor(s.borderRightColor);
                layer.style.borderTopLeftRadius = new StyleLength(s.borderTopLeftRadius);
                layer.style.borderTopRightRadius = new StyleLength(s.borderTopRightRadius);
                layer.style.borderBottomLeftRadius = new StyleLength(s.borderBottomLeftRadius);
                layer.style.borderBottomRightRadius = new StyleLength(s.borderBottomRightRadius);
                layer.style.opacity = new StyleFloat(s.opacity);
                var picture = s.backgroundImage;
                if (HasPicture(picture))
                {
                    if (b != null && SamePicture(picture, b.resolvedStyle.backgroundImage))
                    {
                        layer.style.backgroundImage = new StyleBackground(picture);
                        layer.style.unityBackgroundImageTintColor = new StyleColor(s.unityBackgroundImageTintColor);
                        try { layer.style.backgroundSize = new StyleBackgroundSize(s.backgroundSize); } catch { }
                        try { layer.style.unitySliceLeft = new StyleInt(s.unitySliceLeft); layer.style.unitySliceRight = new StyleInt(s.unitySliceRight);
                              layer.style.unitySliceTop = new StyleInt(s.unitySliceTop); layer.style.unitySliceBottom = new StyleInt(s.unitySliceBottom); } catch { }
                    }
                    else if (!icon)
                    {
                        icon = true;
                        layer.style.backgroundImage = new StyleBackground(Figure());
                        layer.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
                    }
                }
                into.Add(layer);
            }
            for (int i = 0; i < Kids(a); i++)
                Layers(Kid(a, i), b != null && i < Kids(b) ? Kid(b, i) : null, origin, into, ref icon);
        }

        private static VisualElement Layer(float left, float top, float width, float height)
        {
            var layer = new VisualElement();
            layer.pickingMode = PickingMode.Ignore;
            layer.style.position = new StyleEnum<Position>(Position.Absolute);
            layer.style.left = new StyleLength(left);
            layer.style.top = new StyleLength(top);
            layer.style.width = new StyleLength(width);
            layer.style.height = new StyleLength(height);
            return layer;
        }

        private static bool HasPicture(Background picture)
            => picture.texture != null || picture.sprite != null || picture.vectorImage != null || picture.renderTexture != null;

        private static bool SamePicture(Background a, Background b)
        {
            static IntPtr At(UnityEngine.Object o) => o == null ? IntPtr.Zero : o.Pointer;
            return At(a.texture) == At(b.texture) && At(a.sprite) == At(b.sprite)
                   && At(a.vectorImage) == At(b.vectorImage) && At(a.renderTexture) == At(b.renderTexture);
        }

        /// <summary>
        /// Opens or closes the window at the press, before the grid's own handling -- a slot's
        /// drag -- swallows the click a Clickable waits for.
        /// </summary>
        private static void Pressed(VisualElement button)
            => button.RegisterCallback<PointerDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                new Action<PointerDownEvent>(e =>
                {
                    try { e.StopImmediatePropagation(); } catch { }
                    if (_window != null) Close(); else Open();
                })), TrickleDown.TrickleDown);

        /// <summary>
        /// The grid's first empty slot after its last button -- left, top and size, the grid's own
        /// --, or null when there is none, or when it is in a row out of sight. The grid fills its
        /// last row with empty slots; a button's slot shows more than an empty one does.
        /// </summary>
        /// <remarks>
        /// The window made narrow, the grid shows its first rows and a «+» that unfolds the rest
        /// upwards: an empty slot in a row folded away is out of the scroll view's sight, and the
        /// button with it, as the game's own buttons in that row are.
        /// </remarks>
        private static (float, float, UnityEngine.Vector2)? FreePlace(VisualElement grid, out VisualElement model, out VisualElement other)
        {
            model = other = null;
            var slots = new List<VisualElement>();
            SlotsUnder(grid, slots);
            slots = slots.Where(s => s.resolvedStyle.display != DisplayStyle.None && s.resolvedStyle.visibility == Visibility.Visible
                                     && s.worldBound.width > 4f)
                         .OrderBy(s => Math.Round(s.worldBound.y)).ThenBy(s => s.worldBound.x).ToList();
            if (slots.Count < 2) return null;
            var weight = slots.ToDictionary(s => s, Shows);
            var sorted = weight.Values.OrderBy(v => v).ToList();
            int usual = sorted[sorted.Count / 2];
            var full = slots.Where(s => weight[s] >= usual).ToList();
            if (full.Count < 2) return null;
            int lastFull = slots.IndexOf(full[^1]);
            var free = slots.Skip(lastFull + 1).FirstOrDefault(s => weight[s] < usual);

            var viewports = new List<VisualElement>();
            Named(grid, "unity-content-viewport", viewports);
            var sight = viewports.Count > 0 ? viewports[0].worldBound : grid.worldBound;
            bool seen = free != null && sight.Contains(free.worldBound.center);
            string told = $"{grid.name}: {slots.Count} slot(s), {full.Count} showing a button, " +
                          $"{(free == null ? "none free" : seen ? "one free in sight" : "one free out of sight")}";
            if (told != _toldMenu)
            {
                _toldMenu = told;
                MelonLogger.Msg($"[Heroes] MENUS {told}; weights {string.Join(",", sorted)}.");
            }
            if (!seen) return null;

            // The look is copied off two plain slots -- no warning dot on them, which shows more.
            var plain = full.Where(s => weight[s] == usual).Take(2).ToList();
            model = plain.Count > 0 ? plain[0] : full[0];
            other = plain.Count > 1 ? plain[1] : full[1];

            var at = free.worldBound;
            var origin = grid.worldBound;
            return (at.x - origin.x, at.y - origin.y, new UnityEngine.Vector2(at.width, at.height));
        }

        /// <summary>Every slot of the client's under an element, ours left out.</summary>
        private static void SlotsUnder(VisualElement element, List<VisualElement> into)
        {
            for (int i = 0; i < Kids(element); i++)
            {
                var child = Kid(element, i);
                if (child.name == "JondoHeroesMenuButton") continue;
                if (child.TryCast<Il2CppCore.UILogic.Components.Slots.Slot>() != null) into.Add(child);
                else SlotsUnder(child, into);
            }
        }

        /// <summary>How much a slot shows: the elements in it that are shown, and the pictures among them.</summary>
        private static int Shows(VisualElement element)
        {
            if (element.resolvedStyle.display == DisplayStyle.None || element.resolvedStyle.visibility != Visibility.Visible) return 0;
            int count = 1;
            try
            {
                var picture = element.resolvedStyle.backgroundImage;
                if (picture.texture != null || picture.sprite != null || picture.vectorImage != null || picture.renderTexture != null) count++;
            }
            catch { }
            for (int i = 0; i < Kids(element); i++) count += Shows(Kid(element, i));
            return count;
        }

        /// <summary>The button itself, laid over the grid: its place is set by <see cref="PutMenuButton"/>.</summary>
        private static VisualElement MenuButton(VisualElement grid)
        {
            var button = new VisualElement();
            button.name = "JondoHeroesMenuButton";
            button.style.position = new StyleEnum<Position>(Position.Absolute);
            // Dressed once placed, at the size it takes (Dress).
            _dressedFor = default;
            Pressed(button);
            Hint(button, "hint.window");
            // Lit under the pointer, as the game's own buttons are (the light laid by Dress).
            button.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
                new Action<PointerEnterEvent>(_ => Lit(button, true))), TrickleDown.NoTrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
                new Action<PointerLeaveEvent>(_ => Lit(button, false))), TrickleDown.NoTrickleDown);
            grid.hierarchy.Add(button);
            return button;
        }

        /// <summary>Every element under <paramref name="element"/> with that name, the deepest first.</summary>
        private static void Named(VisualElement element, string name, List<VisualElement> into)
        {
            for (int i = 0; i < Kids(element); i++) Named(Kid(element, i), name, into);
            if (element.name == name) into.Add(element);
        }

        // An element's own children, its hierarchy's: a virtual grid's rows are there, and not
        // under its content container, which is what childCount and [i] read.
        private static int Kids(VisualElement element) => element.hierarchy.childCount;
        private static VisualElement Kid(VisualElement element, int i) => element.hierarchy.ElementAt(i);

        private static void Describe(VisualElement element, int depth, System.Text.StringBuilder into)
        {
            if (depth > 6 || into.Length > 6000) return;
            into.Append(new string(' ', depth * 2)).Append(element.GetIl2CppType().Name).Append(" «").Append(element.name)
                .Append("» ").Append(Kids(element)).Append(" child(ren) ")
                .Append(element.resolvedStyle.width).Append('x').Append(element.resolvedStyle.height).Append('\n');
            for (int i = 0; i < Kids(element) && i < 4; i++) Describe(Kid(element, i), depth + 1, into);
        }

        private static UnityEngine.Texture2D _figure;

        /// <summary>
        /// The MENUS button's picture: the two heroes and the crown alone, coloured as the game's
        /// own pictures are, for the frame copied off its slots (<see cref="Dress"/>).
        /// </summary>
        private static UnityEngine.Texture2D Figure() => _figure ??= Draw(framed: false);

        /// <summary>
        /// The button's icon, drawn here: a gold square like the bar's others, two heroes' busts
        /// side by side and a crown on the one in front. Three by three samples a pixel.
        /// </summary>
        private static UnityEngine.Texture2D Icon() => _icon ??= Draw(framed: true);

        private static UnityEngine.Texture2D Draw(bool framed)
        {
            const int size = 64;
            var outline = new UnityEngine.Color(0.36f, 0.17f, 0.04f, 1f);
            var texture = new UnityEngine.Texture2D(size, size, UnityEngine.TextureFormat.RGBA32, false);
            for (int py = 0; py < size; py++)
                for (int px = 0; px < size; px++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                        {
                            float x = px + (sx + 0.5f) / 3f, y = py + (sy + 0.5f) / 3f;
                            var c = framed ? IconAt(x, y, outline) : FigureAt(x, y);
                            r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                        }
                    var color = a > 0 ? new UnityEngine.Color(r / a, g / a, b / a, a / 9f) : new UnityEngine.Color(0, 0, 0, 0);
                    texture.SetPixel(px, size - 1 - py, color);
                }
            texture.Apply();
            texture.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            texture.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset;
            return texture;
        }

        /// <summary>
        /// The two heroes with no square round them, y going down: the one behind blue, the one in
        /// front warm, the crown gold, all edged dark as the game's pictures are.
        /// </summary>
        private static UnityEngine.Color FigureAt(float x, float y)
        {
            var outline = new UnityEngine.Color(0.17f, 0.10f, 0.06f, 1f);
            var color = new UnityEngine.Color(0, 0, 0, 0);
            if (y > 58f) return color;
            color = Bust(x, y, 22f, 24f, 8f, color, new UnityEngine.Color(0.55f, 0.78f, 0.98f, 1f), outline);
            color = Bust(x, y, 39f, 29f, 9f, color, new UnityEngine.Color(1f, 0.74f, 0.40f, 1f), outline);
            if (InCrown(x, y, 39f, 14f)) color = new UnityEngine.Color(1f, 0.86f, 0.20f, 1f);
            else if (InCrown(x, y, 39f, 14f, 1.6f)) color = outline;
            // The bottom cut straight and edged, as the shoulders end.
            if (color.a > 0f && y > 55.6f) color = outline;
            return color;
        }

        /// <summary>The icon at a point, y going down: the last shape that holds it wins.</summary>
        private static UnityEngine.Color IconAt(float x, float y, UnityEngine.Color outline)
        {
            var color = new UnityEngine.Color(0, 0, 0, 0);
            // The square, gold at the top and orange at the bottom, with its dark edge.
            float box = RoundBox(x - 32f, y - 32f, 29f, 29f, 11f);
            if (box > 0f) return color;
            color = box > -2.2f ? outline : UnityEngine.Color.Lerp(new UnityEngine.Color(1f, 0.80f, 0.32f, 1f), new UnityEngine.Color(0.90f, 0.42f, 0.08f, 1f), y / 64f);
            // The hero behind, a little paler, and the one in front.
            color = Bust(x, y, 23f, 25f, 7.5f, color, new UnityEngine.Color(0.98f, 0.88f, 0.70f, 1f), outline);
            color = Bust(x, y, 38f, 29f, 8.5f, color, UnityEngine.Color.white, outline);
            // His crown: a band and three points over his head.
            if (InCrown(x, y, 38f, 16f)) color = new UnityEngine.Color(1f, 0.88f, 0.22f, 1f);
            else if (InCrown(x, y, 38f, 16f, 1.4f)) color = outline;
            // Nothing may spill over the square's edge.
            return box > -2.2f ? outline : color;
        }

        private static UnityEngine.Color Bust(float x, float y, float cx, float headY, float headR,
                                              UnityEngine.Color under, UnityEngine.Color fill, UnityEngine.Color outline)
        {
            float head = (float)Math.Sqrt((x - cx) * (x - cx) + (y - headY) * (y - headY)) - headR;
            float rx = headR * 1.75f, ry = headR * 1.30f, sy = headY + headR * 2.55f;
            float dx = (x - cx) / rx, dy = (y - sy) / ry;
            float shoulders = ((float)Math.Sqrt(dx * dx + dy * dy) - 1f) * Math.Min(rx, ry);
            if (y > sy) shoulders = Math.Abs(x - cx) - rx;   // straight down to the square's edge
            float d = Math.Min(head, shoulders);
            if (d < -1.6f) return fill;
            if (d < 0.6f) return outline;
            return under;
        }

        private static bool InCrown(float x, float y, float cx, float top, float grow = 0f)
        {
            float left = cx - 7f - grow, right = cx + 7f + grow, bottom = top + 7f + grow, band = top + 3.5f;
            if (x < left || x > right || y > bottom || y < top - grow) return false;
            if (y >= band) return true;
            // Three points: their tips at the edges and in the middle.
            float t = (x - left) / (right - left);
            float tri = Math.Abs(((t * 2f) % 1f) * 2f - 1f);
            float tipY = top - grow + (1f - tri) * (band - top + grow);
            return y >= tipY;
        }

        private static float RoundBox(float x, float y, float hw, float hh, float r)
        {
            float qx = Math.Abs(x) - hw + r, qy = Math.Abs(y) - hh + r;
            float ox = Math.Max(qx, 0f), oy = Math.Max(qy, 0f);
            return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0f) - r;
        }

        // ─── The group panel ─────────────────────────────────────────────────────────────

        private static VisualElement _panel;
        private static VisualElement _members;
        private static bool _folded;
        private static string _panelKey;

        /// <summary>
        /// The group at the side of the screen while any hero is connected: each one's portrait and
        /// life, a crown for the master, and for each the automatic pass of his turns (heroes) and
        /// whether the group's items go to him.
        /// </summary>
        private static void RenderPanel()
        {
            var group = new List<JsonElement>();
            long lootTo = 0;
            if (_list is JsonElement list && Bool(list, "on") && list.TryGetProperty("characters", out var all))
            {
                lootTo = Long(list, "lootTo");
                group = all.EnumerateArray().Where(c => Bool(c, "master") || Bool(c, "hero")).ToList();
            }
            SpellBarLines(group.Count >= 2);
            if (group.Count < 2)
            {
                if (_panel != null) { try { _panel.RemoveFromHierarchy(); } catch { } _panel = null; }
                _panelKey = null;
                return;
            }
            // Drawn again only when something in it changed: a hint under the pointer stays.
            // Life left out: it changes all along, and is put in the gauges where they stand.
            string key = Shape(_list.Value) + ":" +
                         string.Join(",", group.Select(g => _portraits.ContainsKey(Long(g, "id")) ? 1 : 0)) + "|" + (_entities != null);
            if (_panel != null && _panel.panel != null && key == _panelKey)
            {
                foreach (var who in group)
                {
                    if (!_fills.TryGetValue(Long(who, "id"), out var gaugeFill)) continue;
                    int now = Int(who, "life"), top = Int(who, "maxLife");
                    gaugeFill.style.height = new StyleLength(new Length(top > 0 ? Math.Clamp(100f * now / top, 0f, 100f) : 0f, LengthUnit.Percent));
                }
                return;
            }
            _panelKey = key;
            HideHint();
            if (_panel == null || _panel.panel == null)
            {
                var layer = FindLayer();
                if (layer == null) return;
                InstallInputGuard();
                _panel = new VisualElement();
                _panel.name = "JondoHeroesGroup";
                _panel.style.position = new StyleEnum<Position>(Position.Absolute);
                _panel.style.right = new StyleLength(8f);
                _panel.style.top = new StyleLength(170f);
                _panel.style.width = new StyleLength(112f);
                _panel.style.paddingLeft = _panel.style.paddingRight = _panel.style.paddingBottom = new StyleLength(4f);
                _panel.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.16f, 0.16f, 0.27f, 0.92f));
                _panel.style.borderTopWidth = _panel.style.borderBottomWidth = _panel.style.borderLeftWidth = _panel.style.borderRightWidth = new StyleFloat(1f);
                var line = new UnityEngine.Color(0.40f, 0.40f, 0.55f, 1f);
                _panel.style.borderTopColor = _panel.style.borderBottomColor = _panel.style.borderLeftColor = _panel.style.borderRightColor = new StyleColor(line);
                Round(_panel, 3f);

                var bar = new VisualElement();
                bar.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                bar.style.justifyContent = new StyleEnum<Justify>(Justify.SpaceBetween);
                bar.style.height = new StyleLength(24f);
                bar.style.alignItems = new StyleEnum<Align>(Align.Center);
                bar.Add(Hint(Tool(Do(() => { _folded = !_folded; if (_members != null) _members.style.display = new StyleEnum<DisplayStyle>(_folded ? DisplayStyle.None : DisplayStyle.Flex); }), "—"), "hint.fold"));
                // The window's own icon, the MENUS button's: the game's font has no «≡».
                var opener = Tool(Do(() => { if (_window != null) Close(); else Open(); }), "");
                opener.style.backgroundImage = new StyleBackground(Icon());
                opener.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
                opener.style.backgroundColor = new StyleColor(new UnityEngine.Color(0, 0, 0, 0));
                bar.Add(Hint(opener, "hint.window"));
                _panel.Add(bar);
                _members = new VisualElement();
                _members.style.display = new StyleEnum<DisplayStyle>(_folded ? DisplayStyle.None : DisplayStyle.Flex);
                _panel.Add(_members);
                KeepEvents(_panel);
                layer.Add(_panel);
                Draggable(_panel, layer);
                PanelPrefs();
                if (_panelX.Value >= 0f && _panelY.Value >= 0f) PlacePanel(layer, _panelX.Value, _panelY.Value);
            }
            _members.Clear();
            foreach (var who in group) _members.Add(Member(who, lootTo));
        }

        // ─── Moving the group panel ──────────────────────────────────────────────────────

        private static MelonPreferences_Entry<float> _panelX, _panelY;
        private static bool _dragging;
        private static UnityEngine.Vector2 _dragFrom, _panelFrom;
        private static VisualElement _dragLayer;

        /// <summary>Where the player left the panel, kept from one session to the next (UserData\MelonPreferences.cfg).</summary>
        private static void PanelPrefs()
        {
            if (_panelX != null) return;
            var category = MelonPreferences.CreateCategory("Heroes");
            _panelX = category.CreateEntry("PanelX", -1f);
            _panelY = category.CreateEntry("PanelY", -1f);
        }

        /// <summary>The name of the panel's buttons: a press on one is the button's, not a drag.</summary>
        private const string Pressable = "JondoHeroesPressable";

        /// <summary>
        /// The panel taken anywhere but on its buttons -- its top bar, its frame, a portrait --
        /// and carried with the pointer; the moves and the release are read on the layer, so a
        /// quick pointer leaving the panel does not drop it. A double click puts it back where it
        /// starts, against the right edge.
        /// </summary>
        private static void Draggable(VisualElement panel, VisualElement layer)
        {
            panel.RegisterCallback<PointerDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                new Action<PointerDownEvent>(e =>
                {
                    if (e.target == null || _panel == null) return;
                    for (var on = e.target.TryCast<VisualElement>(); on != null && on.Pointer != panel.Pointer; on = on.hierarchy.parent)
                        if (on.name == Pressable) return;
                    if (e.clickCount >= 2)
                    {
                        _dragging = false;
                        PanelPrefs();
                        _panelX.Value = _panelY.Value = -1f;
                        MelonPreferences.Save();
                        _panel.style.left = new StyleLength(StyleKeyword.Auto);
                        _panel.style.right = new StyleLength(8f);
                        _panel.style.top = new StyleLength(170f);
                        return;
                    }
                    _panel.BringToFront();
                    _dragging = true;
                    _dragFrom = new UnityEngine.Vector2(e.position.x, e.position.y);
                    var at = _panel.worldBound;
                    var origin = layer.worldBound;
                    _panelFrom = new UnityEngine.Vector2(at.x - origin.x, at.y - origin.y);
                })), TrickleDown.NoTrickleDown);
            if (_dragLayer != null && _dragLayer.Pointer == layer.Pointer) return;
            _dragLayer = layer;
            layer.RegisterCallback<PointerMoveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerMoveEvent>>(
                new Action<PointerMoveEvent>(e =>
                {
                    if (!_dragging) return;
                    PlacePanel(layer, _panelFrom.x + e.position.x - _dragFrom.x, _panelFrom.y + e.position.y - _dragFrom.y);
                })), TrickleDown.TrickleDown);
            layer.RegisterCallback<PointerUpEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerUpEvent>>(
                new Action<PointerUpEvent>(_ =>
                {
                    if (!_dragging) return;
                    _dragging = false;
                    if (_panel == null) return;
                    PanelPrefs();
                    _panelX.Value = _panel.resolvedStyle.left;
                    _panelY.Value = _panel.resolvedStyle.top;
                    MelonPreferences.Save();
                })), TrickleDown.TrickleDown);
        }

        /// <summary>
        /// The panel moved back inside the screen when the game's window is made smaller: a place
        /// kept from a bigger one could leave it out of reach.
        /// </summary>
        private static void KeepPanelInSight()
        {
            if (_panel == null || _panel.panel == null || _dragging) return;
            if (_panel.style.left.keyword == StyleKeyword.Auto) return;
            var layer = _panel.hierarchy.parent;
            if (layer == null) return;
            float x = _panel.resolvedStyle.left, y = _panel.resolvedStyle.top;
            PlacePanel(layer, x, y);
        }

        /// <summary>The panel at a place on the layer, kept inside it.</summary>
        private static void PlacePanel(VisualElement layer, float x, float y)
        {
            if (_panel == null) return;
            float width = layer.resolvedStyle.width, height = layer.resolvedStyle.height;
            float w = _panel.resolvedStyle.width, h = _panel.resolvedStyle.height;
            if (!float.IsNaN(width) && !float.IsNaN(w)) x = Math.Clamp(x, 0f, Math.Max(0f, width - w));
            if (!float.IsNaN(height)) y = Math.Clamp(y, 0f, Math.Max(0f, height - (float.IsNaN(h) || h <= 0f ? 30f : h)));
            _panel.style.right = new StyleLength(StyleKeyword.Auto);
            _panel.style.left = new StyleLength(x);
            _panel.style.top = new StyleLength(y);
        }

        private static VisualElement Member(JsonElement who, long lootTo)
        {
            long id = Long(who, "id");
            bool master = Bool(who, "master");
            var row = new VisualElement();
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            row.style.height = new StyleLength(96f);
            row.style.marginTop = new StyleLength(4f);

            // His life, upright, filled from the bottom.
            int life = Int(who, "life"), max = Int(who, "maxLife");
            var gauge = new VisualElement();
            gauge.style.width = new StyleLength(7f);
            gauge.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.10f, 0.08f, 0.12f, 1f));
            gauge.style.justifyContent = new StyleEnum<Justify>(Justify.FlexEnd);
            Round(gauge, 2f);
            var fill = new VisualElement();
            fill.style.height = new StyleLength(new Length(max > 0 ? Math.Clamp(100f * life / max, 0f, 100f) : 0f, LengthUnit.Percent));
            fill.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.92f, 0.45f, 0.45f, 1f));
            Round(fill, 2f);
            gauge.Add(fill);
            _fills[id] = fill;
            row.Add(gauge);

            var picture = new VisualElement();
            picture.style.flexGrow = new StyleFloat(1f);
            picture.style.marginLeft = new StyleLength(3f);
            var live = LivePortrait("panel", id, Str(who, "look"), seated: false);
            if (_portraits.TryGetValue(id, out var portrait))
            {
                if (live == null || !HasCharacter(live)) picture.style.backgroundImage = new StyleBackground(portrait);
                picture.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
            }
            if (live != null)
            {
                // The panel's picture is narrow: fitted to its width, the character came out tiny.
                // A box as wide as it is high, centred, its sides cut off by the picture, and the
                // client sizes him to the height.
                picture.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
                live.style.position = new StyleEnum<Position>(Position.Absolute);
                live.style.top = new StyleLength(0f);
                live.style.height = new StyleLength(new Length(100f, LengthUnit.Percent));
                float box = Tune("panelBox", 96f);
                live.style.width = new StyleLength(box);
                live.style.left = new StyleLength(new Length(50f, LengthUnit.Percent));
                live.style.marginLeft = new StyleLength(-box / 2f);
                picture.Add(live);
            }
            if (master) picture.Add(Crown());
            // On the portrait, what the game's party window says of a member, as it writes it: the
            // name above, then a label and its value a line.
            // Read from the latest list as the pointer comes: the panel is not drawn again for life.
            string title = Str(who, "name").ToUpperInvariant();
            HintCard(picture, title, () => CardLines(id) ?? CardLines(who));
            row.Add(picture);

            var tools = new VisualElement();
            tools.style.width = new StyleLength(38f);
            tools.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            if (!master)
            {
                bool auto = Bool(who, "autoPass");
                tools.Add(Hint(Toggle(Do(() => Act("heroes/autopass", id, new Dictionary<string, object> { ["on"] = auto ? 0 : 1 })), Glyph(0), auto, height: 20f), "hint.auto"));
                bool questSync = Bool(who, "questSync");
                var questButton = Toggle(Do(() => Act("heroes/quests", id, new Dictionary<string, object> { ["on"] = questSync ? 0 : 1 })), Glyph(6), questSync, height: 20f);
                // Red while he is behind his master in a quest: which, and why, on hovering it.
                var lags = QuestLagsOf(who);
                if (lags.Count > 0)
                {
                    questButton.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.72f, 0.16f, 0.14f, 1f));
                    tools.Add(ButtonHint(questButton, () => QuestLagHint(lags), ""));
                }
                else tools.Add(Hint(questButton, "hint.quests"));
            }
            tools.Add(Hint(Toggle(Do(() => Act("heroes/loot", id)), Glyph(1), lootTo == id, height: 20f), "hint.loot"));
            if (!master && !Bool(who, "inFight"))
            {
                string name = Str(who, "name");
                VisualElement play = null;
                play = Toggle(Do(() => TakeInHand(id, name, play)), Glyph(2), false, height: 20f);
                tools.Add(Hint(play, "hint.play"));
            }
            row.Add(tools);
            return row;
        }

        // ─── Taking a hero in hand ───────────────────────────────────────────────────────

        /// <summary>
        /// Plays a hero from here on, at once: the client becomes his, the one it was a hero of
        /// his, the group around him.
        /// </summary>
        /// <remarks>
        /// One request, and the server does the switch in the same connection (HeroSwitch) -- it
        /// sends the hero's selection and his state, and the map's actors anew; the client is the
        /// hero from then on, with no character list in between.
        /// </remarks>
        private static void TakeInHand(long id, string name, VisualElement from)
        {
            HideHint();
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("heroes/switch", new Dictionary<string, object> { ["id"] = id });
                _toUnity.Enqueue(() =>
                {
                    if (code == 200)
                    {
                        MelonLogger.Msg($"[Heroes] {name} taken in hand.");
                        // The group as it is now read at once: Tab's next press goes by it.
                        _switchListed = true;
                        Poll();
                        return;
                    }
                    _switchBusy = false;
                    _pendingTabs = 0;
                    string why = body is JsonElement b ? Str(b, "error") : "";
                    MelonLogger.Warning($"[Heroes] {name} could not be taken in hand: {code} {why}");
                    if (from != null) ShowHint(from, code == 0 ? T("unreachable") : why == "fight" ? T("error.fight") : T("error"));
                });
            });
        }

        // ─── The spell bar's pages for a hero ────────────────────────────────────────────

        /// <summary>Whether heroes are connected: the spell bar's pages turn for them.</summary>
        private static bool _heroesOn;

        /// <summary>Inside RefreshPageIndex for the main bar, heroes connected: the bar is not «a slave's» there.</summary>
        private static int _pagingDepth;

        private static void SpellBarLines(bool heroes) => _heroesOn = heroes;

        /// <summary>
        /// The client does not turn the pages of the bar while it shows a controlled entity's spells
        /// in a fight (UIActionBar, Cpp2IL of 3.6.10.11): the main bar on its spells, when fkg.bgbl()
        /// and m_updateUiFromSlaves, has get_m_currentPageIndex answer 0, set_m_currentPageIndex do
        /// nothing, and RefreshPageIndex hide the arrows (its last page 0). Made for a summon's few
        /// spells; a hero has a whole class's, and those past the first page could not be reached.
        /// While heroes are connected the page is the bar's own spells page, read and written as for
        /// one's own character, and the arrows are reckoned so -- GetItemSlotIndexInGrid already
        /// places slot «page × lines × 12» on it.
        /// </summary>
        private static void WatchActionBar()
        {
            var harmony = new HarmonyLib.Harmony("jondo.heroes.actionbar");
            var bar = typeof(Il2CppCore.UILogic.GameUICore.UIActionBar);
            void Patch(string name, string prefix = null, string postfix = null)
            {
                try
                {
                    var method = HarmonyLib.AccessTools.Method(bar, name);
                    if (method == null) { MelonLogger.Warning($"[Heroes] UIActionBar.{name} not found."); return; }
                    harmony.Patch(method,
                        prefix: prefix == null ? null : new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(prefix)),
                        postfix: postfix == null ? null : new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(postfix)));
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Heroes] UIActionBar.{name} not watched: {ex.Message}");
                }
            }
            Patch("get_m_currentPageIndex", prefix: nameof(HeroPageRead));
            Patch("set_m_currentPageIndex", prefix: nameof(HeroPageWritten));
            Patch("RefreshPageIndex", prefix: nameof(PageArrowsIn), postfix: nameof(PageArrowsOut));
            Patch("get_m_updateUiFromSlaves", prefix: nameof(NotASlavesBar));
        }

        /// <summary>The main bar, on its spells, while heroes are connected: the one whose pages are theirs to turn.</summary>
        private static bool HeroPaging(Il2CppCore.UILogic.GameUICore.UIActionBar bar)
        {
            try { return _heroesOn && bar != null && bar.m_id == 0 && Convert.ToInt32(bar.m_currentGridEnum) == 1; }
            catch { return false; }
        }

        public static bool HeroPageRead(Il2CppCore.UILogic.GameUICore.UIActionBar __instance, ref int __result)
        {
            if (!HeroPaging(__instance)) return true;
            __result = __instance.m_pageSpellsIndex;
            return false;
        }

        public static bool HeroPageWritten(Il2CppCore.UILogic.GameUICore.UIActionBar __instance, int value)
        {
            if (!HeroPaging(__instance)) return true;
            __instance.m_pageSpellsIndex = value;
            return false;
        }

        public static void PageArrowsIn(Il2CppCore.UILogic.GameUICore.UIActionBar __instance)
        {
            if (HeroPaging(__instance)) _pagingDepth++;
        }

        public static void PageArrowsOut(Il2CppCore.UILogic.GameUICore.UIActionBar __instance)
        {
            if (_pagingDepth > 0 && HeroPaging(__instance)) _pagingDepth--;
        }

        public static bool NotASlavesBar(ref bool __result)
        {
            if (_pagingDepth <= 0) return true;
            __result = false;
            return false;
        }

        private static VisualElement Toggle(Il2CppSystem.Action click, UnityEngine.Texture2D glyph, bool on, float height = 24f)
        {
            var button = new VisualElement();
            button.name = Pressable;
            button.style.height = new StyleLength(height);
            button.style.marginTop = button.style.marginBottom = new StyleLength(2f);
            button.style.backgroundColor = new StyleColor(on ? new UnityEngine.Color(0.45f, 0.62f, 0.12f, 1f) : new UnityEngine.Color(0.28f, 0.28f, 0.40f, 1f));
            Round(button, 3f);
            var picture = new VisualElement();
            picture.pickingMode = PickingMode.Ignore;
            picture.style.flexGrow = new StyleFloat(1f);
            picture.style.marginTop = picture.style.marginBottom = new StyleLength(2f);
            picture.style.backgroundImage = new StyleBackground(glyph);
            picture.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
            if (!on) picture.style.opacity = new StyleFloat(0.75f);
            button.Add(picture);
            button.AddManipulator(new Clickable(click).Cast<IManipulator>());
            Lively(button);
            return button;
        }

        // ─── A hero's sheet: his bag, his characteristics, his spells ────────────────────

        private static WindowFigma _sheet;
        private static ScrollView _sheetPage;

        /// <summary>A window of its own for one of a hero's three sheets (0 bag, 1 characteristics, 2 spells).</summary>
        private static void OpenSheet(long id, string name, int view)
        {
            try
            {
                CloseSheet();
                var layer = FindLayer();
                if (layer == null) return;
                float w = layer.resolvedStyle.width, h = layer.resolvedStyle.height;
                const float width = 560f;
                float height = float.IsNaN(h) || h <= 0 ? 560f : Math.Min(560f, h - 60f);
                var window = new WindowFigma(true, name + " · " + T(view == 0 ? "hint.bag" : view == 1 ? "hint.stats" : "hint.spells"), "", true);
                window.showCloseButton = true;
                window.name = "JondoHeroesSheet";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(width);
                window.style.height = new StyleLength(height);
                window.style.left = new StyleLength(float.IsNaN(w) || w <= 0 ? 300f : Math.Max(0f, (w - width) / 2f + 60f));
                window.style.top = new StyleLength(float.IsNaN(h) || h <= 0 ? 100f : Math.Max(0f, (h - height) / 2f + 30f));
                _sheetPage = new ScrollView(ScrollViewMode.Vertical);
                _sheetPage.style.flexGrow = new StyleFloat(1f);
                _sheetPage.style.paddingLeft = _sheetPage.style.paddingRight = new StyleLength(18f);
                _sheetPage.style.paddingTop = new StyleLength(12f);
                _sheetPage.Add(Label(T("loading")));
                window.Add(_sheetPage);
                layer.Add(window);
                window.BringToFront();
                KeepEvents(window);
                var close = window.header?.rightButton;
                if (close != null) close.add_Clicked(Do(CloseSheet));
                _sheet = window;
                Task.Run(async () =>
                {
                    var (code, body) = await AskAsync("heroes/sheet", new Dictionary<string, object> { ["id"] = id });
                    _toUnity.Enqueue(() => ShowSheet(view, code, body));
                });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] The sheet could not be opened: {ex}");
                CloseSheet();
            }
        }

        private static void CloseSheet()
        {
            try { _sheet?.RemoveFromHierarchy(); } catch { }
            _sheet = null;
            _sheetPage = null;
        }

        private static void ShowSheet(int view, int code, JsonElement? body)
        {
            if (_sheetPage == null) return;
            _sheetPage.Clear();
            if (code != 200 || body is not JsonElement s) { _sheetPage.Add(Label(code == 0 ? T("unreachable") : T("error"))); return; }
            try
            {
                if (view == 1) ShowCharacteristics(s);
                else if (view == 2) ShowSpells(s);
                else ShowBag(s);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] The sheet could not be drawn: {ex}");
                _sheetPage.Add(Label(T("error")));
            }
        }

        /// <summary>
        /// A number with its thousands apart, as the client writes them. By hand: the game runs
        /// with no culture data, and asking for «fr-FR» throws.
        /// </summary>
        private static string Num(long value)
        {
            string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) text.Append(Language == "en" ? ',' : ' ');
                text.Append(digits[i]);
            }
            return (value < 0 ? "-" : "") + text;
        }

        /// <summary>A box of the sheets: a dark panel with its title, as the client's windows group things.</summary>
        private static VisualElement Section(string title)
        {
            var box = new VisualElement();
            box.style.marginBottom = new StyleLength(10f);
            box.style.paddingLeft = box.style.paddingRight = new StyleLength(10f);
            box.style.paddingTop = box.style.paddingBottom = new StyleLength(8f);
            box.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.13f, 0.13f, 0.20f, 0.95f));
            Round(box, 4f);
            if (!string.IsNullOrEmpty(title))
            {
                var head = Label(title, 12f, true);
                head.style.color = new StyleColor(new UnityEngine.Color(0.75f, 0.75f, 0.85f, 1f));
                head.style.letterSpacing = new StyleLength(1f);
                box.Add(head);
            }
            _sheetPage.Add(box);
            return box;
        }

        /// <summary>The characteristics as the client's sheet lays them out: experience, points, then the rest by kind.</summary>
        private static void ShowCharacteristics(JsonElement s)
        {
            long xp = Long(s, "xp"), floor = Long(s, "xpFloor"), next = Long(s, "xpNext");
            var top = Section("");
            var who = Label(Str(s, "name"), 16f, true);
            who.style.marginBottom = new StyleLength(0f);
            top.Add(who);
            top.Add(Note(ClassOf(Int(s, "breed")).ToUpperInvariant() + "  ·  " + T("level", Int(s, "level"))));
            var xpLine = Line(null, T("stat.xp"), next > floor ? $"{Num(xp - floor)} / {Num(next - floor)}" : Num(xp));
            xpLine.style.marginTop = new StyleLength(8f);
            top.Add(xpLine);
            top.Add(Gauge(next > floor ? (float)(xp - floor) / (next - floor) : 1f, new UnityEngine.Color(0.85f, 0.85f, 0.25f, 1f)));
            top.Add(Line(null, T("stat.life"), Num(Int(s, "life")) + " / " + Num(Int(s, "maxLife"))));
            top.Add(Gauge(Int(s, "maxLife") > 0 ? (float)Int(s, "life") / Int(s, "maxLife") : 0f, new UnityEngine.Color(0.90f, 0.40f, 0.40f, 1f)));

            var points = Section("");
            var row = new VisualElement();
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            row.style.justifyContent = new StyleEnum<Justify>(Justify.SpaceAround);
            foreach (var (name, value, color) in new[]
                     {
                         (T("stat.ap"), Int(s, "ap"), new UnityEngine.Color(0.45f, 0.75f, 1f, 1f)),
                         (T("stat.mp"), Int(s, "mp"), new UnityEngine.Color(0.45f, 0.85f, 0.45f, 1f)),
                         ("PO", Int(s, "range"), new UnityEngine.Color(0.40f, 0.85f, 0.80f, 1f)),
                     })
            {
                var one = Label(name + "  " + Num(value), 16f, true);
                one.style.color = new StyleColor(color);
                one.style.marginBottom = new StyleLength(0f);
                row.Add(one);
            }
            points.Add(row);

            var elements = Section(T("hint.stats").ToUpperInvariant());
            foreach (var (name, key, color) in new[]
                     {
                         (T("stat.vitality"), "vitality", new UnityEngine.Color(0.95f, 0.45f, 0.45f, 1f)),
                         (T("stat.wisdom"), "wisdom", new UnityEngine.Color(0.75f, 0.55f, 0.95f, 1f)),
                         (T("stat.strength"), "strength", new UnityEngine.Color(0.80f, 0.60f, 0.35f, 1f)),
                         (T("stat.intelligence"), "intelligence", new UnityEngine.Color(1f, 0.55f, 0.25f, 1f)),
                         (T("stat.chance"), "chance", new UnityEngine.Color(0.40f, 0.70f, 1f, 1f)),
                         (T("stat.agility"), "agility", new UnityEngine.Color(0.45f, 0.85f, 0.45f, 1f)),
                     })
            {
                var line = Line(Dot(color), name, Num(Int(s, key)));
                elements.Add(line);
            }

            var more = Section("");
            foreach (var (name, key) in new[]
                     {
                         (T("stat.initiative"), "initiative"), (T("stat.prospecting"), "prospecting"), (T("stat.power"), "power"),
                         (T("stat.critical"), "critical"), (T("stat.summons"), "summons"),
                     })
                more.Add(Line(null, name, Num(Int(s, key))));
            var resist = s.TryGetProperty("resist", out var r) ? r.EnumerateArray().Select(v => v.GetInt32()).ToArray() : new int[5];
            if (resist.Length == 5) more.Add(Note(T("stat.resistValues", resist[0], resist[1], resist[2], resist[3], resist[4])));

            var foot = Section("");
            foot.Add(Line(null, T("stat.points"), Num(Int(s, "points"))));
            foot.Add(Line(null, T("stat.kamas"), Num(Long(s, "kamas"))));
        }

        /// <summary>The spells as the client's spell book shows them: two columns of cards, each its icon and its name.</summary>
        private static void ShowSpells(JsonElement s)
        {
            var spells = s.TryGetProperty("spells", out var list) ? list.EnumerateArray().ToList() : new List<JsonElement>();
            if (spells.Count == 0) { _sheetPage.Add(Label(T("nothing"))); return; }
            var grid = new VisualElement();
            grid.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            grid.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            foreach (var spell in spells)
            {
                int spellId = Int(spell, "id");
                var (spellName, icon) = SpellOf(spellId);
                var card = new VisualElement();
                card.style.width = new StyleLength(new Length(48f, LengthUnit.Percent));
                card.style.marginRight = new StyleLength(new Length(2f, LengthUnit.Percent));
                card.style.marginBottom = new StyleLength(6f);
                card.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                card.style.alignItems = new StyleEnum<Align>(Align.Center);
                card.style.paddingLeft = card.style.paddingRight = new StyleLength(4f);
                card.style.height = new StyleLength(38f);
                card.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.17f, 0.18f, 0.29f, 1f));
                card.style.borderTopWidth = card.style.borderBottomWidth = card.style.borderLeftWidth = card.style.borderRightWidth = new StyleFloat(1f);
                var edge = new UnityEngine.Color(0.32f, 0.33f, 0.48f, 1f);
                card.style.borderTopColor = card.style.borderBottomColor = card.style.borderLeftColor = card.style.borderRightColor = new StyleColor(edge);
                Round(card, 4f);
                card.Add(IconTile(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Spell, icon, 30f));
                var name = Label(spellName ?? ("#" + spellId), 12f, true);
                name.style.marginBottom = new StyleLength(0f);
                name.style.flexShrink = new StyleFloat(1f);
                card.Add(name);
                grid.Add(card);
            }
            _sheetPage.Add(grid);
        }

        /// <summary>The bag as the client's inventory shows it: what he wears, then a grid of tiles with their quantities.</summary>
        private static void ShowBag(JsonElement s)
        {
            var items = s.TryGetProperty("items", out var all) ? all.EnumerateArray().ToList() : new List<JsonElement>();
            const int Bag = 63;
            foreach (var (title, part) in new[]
                     {
                         (T("equipped"), items.Where(i => Int(i, "position") != Bag).OrderBy(i => Int(i, "position")).ToList()),
                         (T("bagged"), items.Where(i => Int(i, "position") == Bag).ToList()),
                     })
            {
                var box = Section(title);
                if (part.Count == 0) { box.Add(Note(T("nothing"))); continue; }
                var grid = new VisualElement();
                grid.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                grid.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
                foreach (var item in part)
                {
                    int gid = Int(item, "gid"), quantity = Int(item, "quantity");
                    var (itemName, icon) = ItemOf(gid);
                    var cell = new VisualElement();
                    cell.style.width = cell.style.height = new StyleLength(54f);
                    cell.style.marginRight = cell.style.marginBottom = new StyleLength(5f);
                    cell.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.16f, 0.17f, 0.27f, 1f));
                    cell.style.borderTopWidth = cell.style.borderBottomWidth = cell.style.borderLeftWidth = cell.style.borderRightWidth = new StyleFloat(1f);
                    var edge = new UnityEngine.Color(0.30f, 0.31f, 0.45f, 1f);
                    cell.style.borderTopColor = cell.style.borderBottomColor = cell.style.borderLeftColor = cell.style.borderRightColor = new StyleColor(edge);
                    cell.style.alignItems = new StyleEnum<Align>(Align.Center);
                    cell.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
                    Round(cell, 3f);
                    var tile = IconTile(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Item, icon, 44f);
                    tile.style.marginRight = new StyleLength(0f);
                    cell.Add(tile);
                    if (quantity > 1)
                    {
                        var count = Label(Num(quantity), 11f, true);
                        count.pickingMode = PickingMode.Ignore;
                        count.style.position = new StyleEnum<Position>(Position.Absolute);
                        count.style.left = new StyleLength(3f);
                        count.style.top = new StyleLength(1f);
                        count.style.marginBottom = new StyleLength(0f);
                        cell.Add(count);
                    }
                    string named = itemName ?? ("#" + gid);
                    HintText(cell, () => quantity > 1 ? $"{named} x{Num(quantity)}" : named);
                    grid.Add(cell);
                }
                box.Add(grid);
            }
            var purse = Section("");
            purse.Add(Line(null, T("stat.kamas"), Num(Long(s, "kamas"))));
        }

        private static VisualElement Gauge(float share, UnityEngine.Color color)
        {
            var bar = new VisualElement();
            bar.style.height = new StyleLength(6f);
            bar.style.marginBottom = new StyleLength(6f);
            bar.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.08f, 0.08f, 0.12f, 1f));
            Round(bar, 2f);
            var fill = new VisualElement();
            fill.style.width = new StyleLength(new Length(Math.Clamp(share, 0f, 1f) * 100f, LengthUnit.Percent));
            fill.style.flexGrow = new StyleFloat(0f);
            fill.style.height = new StyleLength(new Length(100f, LengthUnit.Percent));
            fill.style.backgroundColor = new StyleColor(color);
            Round(fill, 2f);
            bar.Add(fill);
            return bar;
        }

        private static VisualElement Dot(UnityEngine.Color color)
        {
            var dot = new VisualElement();
            dot.style.width = dot.style.height = new StyleLength(12f);
            dot.style.marginRight = new StyleLength(10f);
            dot.style.backgroundColor = new StyleColor(color);
            Round(dot, 6f);
            return dot;
        }

        /// <summary>A line of a sheet: an icon or none, a name, a value on the right.</summary>
        private static VisualElement Line(VisualElement icon, string name, string value)
        {
            var row = new VisualElement();
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            row.style.alignItems = new StyleEnum<Align>(Align.Center);
            row.style.minHeight = new StyleLength(26f);
            row.style.marginBottom = new StyleLength(2f);
            if (icon != null) row.Add(icon);
            var left = Label(name, 13f);
            left.style.flexGrow = new StyleFloat(1f);
            left.style.marginBottom = new StyleLength(0f);
            row.Add(left);
            var right = Label(value, 13f, true);
            right.style.marginBottom = new StyleLength(0f);
            row.Add(right);
            return row;
        }

        private static (string Name, int Icon) SpellOf(int id)
        {
            try
            {
                var data = Il2CppCore.DataCenter.DataCenterModule.spellsDataRoot?.GetSpellById(id);
                return data == null ? (null, 0) : (data.name, data.iconId);
            }
            catch { return (null, 0); }
        }

        private static (string Name, int Icon) ItemOf(int gid)
        {
            try
            {
                var data = Il2CppCore.DataCenter.DataCenterModule.itemsDataRoot?.GetItemById(gid);
                return data == null ? (null, 0) : (data.name, data.iconId);
            }
            catch { return (null, 0); }
        }

        private static readonly Dictionary<Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType, Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType> _iconTypes =
            new Dictionary<Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType, Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType>();
        private static bool _noIcons;

        /// <summary>An item's or a spell's picture in a tile of the client's own, as JondoFix's item window draws them.</summary>
        private static VisualElement IconTile(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType kind, int iconId, float size = 28f)
        {
            var place = new VisualElement();
            place.style.width = place.style.height = new StyleLength(size);
            place.style.marginRight = new StyleLength(8f);
            place.style.flexShrink = new StyleFloat(0f);
            place.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
            place.pickingMode = PickingMode.Ignore;
            if (_noIcons || iconId <= 0) return place;
            try
            {
                if (!_iconTypes.TryGetValue(kind, out var type))
                    _iconTypes[kind] = type = Il2CppCore.UILogic.Admin.AdminSelectItemUI.FindIconAddressableEntryType(kind);
                var tile = new Il2CppCore.UILogic.Components.Slots.Tile();
                tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(type, iconId);
                tile.pickingMode = PickingMode.Ignore;
                tile.style.width = tile.style.height = new StyleLength(size);
                place.Add(tile);
            }
            catch (Exception ex)
            {
                _noIcons = true;
                MelonLogger.Warning($"[Heroes] No icons in the sheets: {ex.Message}");
            }
            return place;
        }

        // ─── The hints ───────────────────────────────────────────────────────────────────

        private static VisualElement _hint;

        /// <summary>A line saying what the button does, above it while the pointer is on it, its key beside.</summary>
        private static VisualElement Hint(VisualElement target, string key) => ButtonHint(target, () => T(key), KeysOf(key));

        /// <summary>The same with any text, worked out as the pointer comes.</summary>
        private static VisualElement HintText(VisualElement target, Func<string> text)
        {
            try
            {
                target.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
                    new Action<PointerEnterEvent>(_ => ShowHint(target, text()))), TrickleDown.NoTrickleDown);
                target.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
                    new Action<PointerLeaveEvent>(_ => HideHint())), TrickleDown.NoTrickleDown);
            }
            catch { }
            return target;
        }

        private static void ShowHint(VisualElement target, string text)
        {
            try
            {
                var layer = FindLayer();
                if (layer == null) return;
                if (_hint == null || _hint.panel == null)
                {
                    _hint = new VisualElement();
                    _hint.name = "JondoHeroesHint";
                    _hint.pickingMode = PickingMode.Ignore;
                    _hint.style.position = new StyleEnum<Position>(Position.Absolute);
                    _hint.style.paddingLeft = _hint.style.paddingRight = new StyleLength(10f);
                    _hint.style.paddingTop = _hint.style.paddingBottom = new StyleLength(5f);
                    // The game's hints: a dark slate box, rounded, with no edge drawn.
                    _hint.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.12f, 0.12f, 0.17f, 0.96f));
                    Round(_hint, 5f);
                    _hint.RegisterCallback<GeometryChangedEvent>(DelegateSupport.ConvertDelegate<EventCallback<GeometryChangedEvent>>(
                        new Action<GeometryChangedEvent>(_ => { try { PlaceAbove(); } catch { } })), TrickleDown.NoTrickleDown);
                    layer.Add(_hint);
                }
                // A label a line: one label with line breaks in it is measured one line high, and
                // the rest spilled out of the box.
                _hint.Clear();
                string[] lines = (text ?? "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = Label(lines[i], 12f, i == 0 && lines.Length > 1);
                    line.pickingMode = PickingMode.Ignore;
                    line.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                    line.style.marginBottom = new StyleLength(i == 0 && lines.Length > 1 ? 3f : 0f);
                    _hint.Add(line);
                }
                // On its left: the group panel stands against the screen's right edge. What the
                // buttons' hints set (ShowButtonHint) undone.
                var at = target.worldBound;
                float width = layer.resolvedStyle.width;
                _hintAbove = null;
                _hint.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column);
                _hint.style.alignItems = new StyleEnum<Align>(Align.FlexStart);
                _hint.style.left = new StyleLength(StyleKeyword.Auto);
                _hint.style.bottom = new StyleLength(StyleKeyword.Auto);
                _hint.style.translate = new StyleTranslate(StyleKeyword.Null);
                _hint.style.right = new StyleLength(Math.Max(0f, (float.IsNaN(width) ? 0f : width) - at.xMin + 6f));
                _hint.style.top = new StyleLength(at.yMin);
                _hint.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
                _hint.BringToFront();
            }
            catch { }
        }

        /// <summary>
        /// A member's card, as the game's party window writes it on its portraits: the name in
        /// capitals, pale, then «label : value» a line, the value white and bold.
        /// </summary>
        private static void HintCard(VisualElement target, string title, Func<List<(string Label, string Value)>> lines)
        {
            try
            {
                target.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
                    new Action<PointerEnterEvent>(_ => ShowCard(target, title, lines()))), TrickleDown.NoTrickleDown);
                target.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
                    new Action<PointerLeaveEvent>(_ => HideHint())), TrickleDown.NoTrickleDown);
            }
            catch { }
        }

        private static void ShowCard(VisualElement target, string title, List<(string Label, string Value)> lines)
        {
            ShowHint(target, "");
            try
            {
                if (_hint == null || _hint.panel == null) return;
                _hint.Clear();
                var pale = new UnityEngine.Color(0.66f, 0.68f, 0.78f, 1f);
                var head = Label(title, 12f);
                head.pickingMode = PickingMode.Ignore;
                head.style.color = new StyleColor(pale);
                head.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                head.style.marginBottom = new StyleLength(4f);
                head.style.alignSelf = new StyleEnum<Align>(Align.FlexStart);
                head.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleLeft);
                _hint.Add(head);
                foreach (var (label, value) in lines)
                {
                    var row = new VisualElement();
                    row.pickingMode = PickingMode.Ignore;
                    row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                    row.style.marginBottom = new StyleLength(1f);
                    // A space that does not break: a label's last plain space is trimmed off.
                    var name = Label(label + " : ", 13f);
                    name.pickingMode = PickingMode.Ignore;
                    name.style.color = new StyleColor(pale);
                    name.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                    name.style.marginBottom = new StyleLength(0f);
                    var it = Label(value, 13f, true);
                    it.pickingMode = PickingMode.Ignore;
                    it.style.color = new StyleColor(UnityEngine.Color.white);
                    it.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                    it.style.marginBottom = new StyleLength(0f);
                    row.Add(name);
                    row.Add(it);
                    _hint.Add(row);
                }
            }
            catch { }
        }

        /// <summary>The keys a button's hint shows beside its words, as the game's do: none for most.</summary>
        private static string KeysOf(string key) => key switch
        {
            "hint.window" => "Ctrl + Tab",
            "hint.play" => "Tab",
            _ => null,
        };

        /// <summary>The quests a hero stayed behind his master in, as the server's list names them.</summary>
        private static List<(int Quest, string Reason, int Item, int Count)> QuestLagsOf(JsonElement who)
        {
            var lags = new List<(int, string, int, int)>();
            if (!who.TryGetProperty("questLag", out var all) || all.ValueKind != JsonValueKind.Array) return lags;
            foreach (var lag in all.EnumerateArray())
                lags.Add((Int(lag, "quest"), Str(lag, "reason"), Int(lag, "item"), Int(lag, "count")));
            return lags;
        }

        /// <summary>The red quests button's hint: each quest, by the client's own name, and why.</summary>
        private static string QuestLagHint(List<(int Quest, string Reason, int Item, int Count)> lags)
        {
            var text = new System.Text.StringBuilder(T("hint.quests")).Append('\n').Append(T("quests.behind"));
            foreach (var (quest, reason, item, count) in lags)
            {
                string why = reason == "missingitems"
                    ? T("quests.why.missingitems", count, ItemOf(item).Name ?? "#" + item)
                    : T("quests.why." + reason);
                text.Append('\n').Append(T("quests.line", QuestNameOf(quest), why));
            }
            return text.ToString();
        }

        /// <summary>The heroes' quest lags already told in the alert, so that each is told once.</summary>
        private static readonly HashSet<string> _lagsTold = new HashSet<string>();

        private static VisualElement _alert;

        /// <summary>
        /// A hero who stayed behind his master in a quest, told in a window to close -- hard to miss,
        /// as asked in Keka-Bron/JondoEmu#206 --, once per lag: his quests button stays red after it.
        /// A lag gone and come back again is told again.
        /// </summary>
        private static void WatchQuestLags()
        {
            var now = new HashSet<string>();
            var fresh = new List<string>();
            if (_list is JsonElement list && list.TryGetProperty("characters", out var all))
            {
                foreach (var who in all.EnumerateArray())
                {
                    if (!Bool(who, "hero")) continue;
                    foreach (var (quest, reason, item, count) in QuestLagsOf(who))
                    {
                        string key = $"{Long(who, "id")}:{quest}:{reason}:{item}:{count}";
                        now.Add(key);
                        if (_lagsTold.Contains(key)) continue;
                        string why = reason == "missingitems"
                            ? T("quests.why.missingitems", count, ItemOf(item).Name ?? "#" + item)
                            : T("quests.why." + reason);
                        fresh.Add(T("alert.line", Str(who, "name"), QuestNameOf(quest), why));
                    }
                }
            }
            _lagsTold.IntersectWith(now);
            _lagsTold.UnionWith(now);
            if (fresh.Count > 0) ShowQuestAlert(fresh);
        }

        private static void ShowQuestAlert(List<string> lines)
        {
            var layer = FindLayer();
            if (layer == null) return;
            InstallInputGuard();
            // One window: what comes while it is open is added to it.
            if (_alert == null || _alert.panel == null)
            {
                float w = layer.resolvedStyle.width, h = layer.resolvedStyle.height;
                const float width = 520f;
                var window = new WindowFigma(true, T("alert.title"), "", true);
                window.showCloseButton = true;
                window.name = "JondoHeroesQuestAlert";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(width);
                window.style.left = new StyleLength(float.IsNaN(w) || w <= 0 ? 300f : Math.Max(0f, (w - width) / 2f));
                window.style.top = new StyleLength(float.IsNaN(h) || h <= 0 ? 200f : Math.Max(0f, h / 2f - 140f));

                var body = new VisualElement();
                body.name = "lines";
                body.style.paddingLeft = body.style.paddingRight = new StyleLength(18f);
                body.style.paddingTop = new StyleLength(14f);
                window.Add(body);

                var after = Note(T("alert.after"));
                after.style.paddingLeft = after.style.paddingRight = new StyleLength(18f);
                after.style.marginTop = new StyleLength(6f);
                window.Add(after);

                var footer = new VisualElement();
                footer.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                footer.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
                footer.style.paddingTop = footer.style.paddingBottom = new StyleLength(12f);
                footer.Add(Button(Do(CloseQuestAlert), T("alert.ok"), strong: true, enabled: true));
                window.Add(footer);

                layer.Add(window);
                KeepEvents(window);
                var close = window.header?.rightButton;
                if (close != null) close.add_Clicked(Do(CloseQuestAlert));
                _alert = window;
            }
            var into = UQueryExtensions.Q(_alert, "lines", (string)null);
            foreach (string line in lines)
            {
                var label = Label(line, 13f);
                label.style.color = new StyleColor(Red);
                into?.Add(label);
            }
            _alert.BringToFront();
        }

        private static void CloseQuestAlert()
        {
            try { _alert?.RemoveFromHierarchy(); } catch { }
            _alert = null;
        }

        private static string QuestNameOf(int quest)
        {
            try
            {
                string name = Il2CppCore.DataCenter.DataCenterModule.questsDataRoot?.GetQuestById(quest)?.name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return "#" + quest;
        }

        /// <summary>
        /// A button's hint as the game writes its buttons': the words, then the key in a box of its
        /// own when it has one, above the button and centred on it.
        /// </summary>
        private static VisualElement ButtonHint(VisualElement target, Func<string> text, string keys)
        {
            try
            {
                target.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
                    new Action<PointerEnterEvent>(_ => ShowButtonHint(target, text(), keys))), TrickleDown.NoTrickleDown);
                target.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
                    new Action<PointerLeaveEvent>(_ => HideHint())), TrickleDown.NoTrickleDown);
            }
            catch { }
            return target;
        }

        private static void ShowButtonHint(VisualElement target, string text, string keys)
        {
            ShowHint(target, text);
            try
            {
                if (_hint == null || _hint.panel == null) return;
                // The words and the key's box side by side -- but a hint of several lines keeps its
                // lines one under the other (ShowHint): side by side they ran into one, off the screen.
                bool lines = (text ?? "").Contains('\n');
                _hint.style.flexDirection = new StyleEnum<FlexDirection>(lines ? FlexDirection.Column : FlexDirection.Row);
                _hint.style.alignItems = new StyleEnum<Align>(lines ? Align.FlexStart : Align.Center);
                if (!string.IsNullOrEmpty(keys))
                {
                    var key = new VisualElement();
                    key.pickingMode = PickingMode.Ignore;
                    key.style.marginLeft = new StyleLength(8f);
                    key.style.paddingLeft = key.style.paddingRight = new StyleLength(5f);
                    key.style.paddingTop = key.style.paddingBottom = new StyleLength(1f);
                    key.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.30f, 0.30f, 0.42f, 1f));
                    Round(key, 3f);
                    var words = Label(keys, 11f);
                    words.pickingMode = PickingMode.Ignore;
                    words.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
                    words.style.marginBottom = new StyleLength(0f);
                    key.Add(words);
                    _hint.Add(key);
                }

                // Above the button, its middle on the button's -- placed once its size is known.
                _hintAbove = target.worldBound;
                PlaceAbove();
            }
            catch { }
        }

        /// <summary>The button a button's hint stands above, until another hint is shown.</summary>
        private static UnityEngine.Rect? _hintAbove;

        /// <summary>
        /// A button's hint above it and centred on it, kept inside the screen: pushed left off the
        /// right edge -- the group panel stands against it --, below the button when there is no
        /// room above. Done again as the hint's size comes (its GeometryChangedEvent).
        /// </summary>
        private static void PlaceAbove()
        {
            if (_hintAbove is not UnityEngine.Rect at || _hint == null) return;
            var layer = _hint.hierarchy.parent;
            if (layer == null) return;
            float w = _hint.resolvedStyle.width, h = _hint.resolvedStyle.height;
            if (float.IsNaN(w) || float.IsNaN(h)) { w = 0f; h = 0f; }
            var origin = layer.worldBound;
            float lw = layer.resolvedStyle.width;
            float x = at.center.x - origin.x - w / 2f;
            if (!float.IsNaN(lw)) x = Math.Min(x, lw - w - 4f);
            x = Math.Max(4f, x);
            float y = at.yMin - origin.y - h - 4f;
            if (y < 4f) y = at.yMax - origin.y + 4f;
            _hint.style.translate = new StyleTranslate(StyleKeyword.Null);
            _hint.style.right = new StyleLength(StyleKeyword.Auto);
            _hint.style.bottom = new StyleLength(StyleKeyword.Auto);
            _hint.style.left = new StyleLength(x);
            _hint.style.top = new StyleLength(y);
        }

        private static void HideHint()
        {
            try { if (_hint != null) _hint.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None); } catch { }
        }

        // ─── The panel's glyphs: a sword, a sack, a «play» ───────────────────────────────

        private static readonly UnityEngine.Texture2D[] _glyphs = new UnityEngine.Texture2D[7];

        /// <summary>
        /// A glyph, drawn here white with a dark edge: 0 the automatic pass (a sword), 1 the loot
        /// recipient (a tied sack), 2 taking him in hand (a «play»), 3 his bag (a chest), 4 his
        /// characteristics (three bars), 5 his spells (a star), 6 quest sharing (a checklist).
        /// </summary>
        private static UnityEngine.Texture2D Glyph(int which)
        {
            if (_glyphs[which] != null) return _glyphs[which];
            const int size = 32;
            var edge = new UnityEngine.Color(0.16f, 0.09f, 0.05f, 1f);
            var texture = new UnityEngine.Texture2D(size, size, UnityEngine.TextureFormat.RGBA32, false);
            for (int py = 0; py < size; py++)
                for (int px = 0; px < size; px++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                        {
                            float x = px + (sx + 0.5f) / 3f, y = py + (sy + 0.5f) / 3f;
                            float d = which switch
                            {
                                0 => Sword(x, y), 1 => Sack(x, y), 2 => Play(x, y),
                                3 => Chest(x, y), 4 => Bars(x, y), 5 => Star(x, y), _ => Box(x, y, 16f, 16f, 10f, 12f),
                            };
                            UnityEngine.Color c;
                            if ((which == 1 && Knot(x, y)) || (which == 3 && d < -1.1f && ChestLines(x, y))
                                || (which == 6 && QuestLines(x, y))) c = edge;
                            else if (d < -1.1f) c = UnityEngine.Color.white;
                            else if (d < 0.5f) c = edge;
                            else continue;
                            r += c.r; g += c.g; b += c.b; a += 1f;
                        }
                    texture.SetPixel(px, size - 1 - py, a > 0 ? new UnityEngine.Color(r / a, g / a, b / a, a / 9f) : new UnityEngine.Color(0, 0, 0, 0));
                }
            texture.Apply();
            texture.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            texture.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset;
            return _glyphs[which] = texture;
        }

        private static bool QuestLines(float x, float y)
            => Box(x, y, 16f, 10f, 6f, 1f) < 0f
               || Capsule(x, y, 11f, 17f, 14f, 21f, 1f) < 0f
               || Capsule(x, y, 14f, 21f, 21f, 15f, 1f) < 0f;

        private static float Circle(float x, float y, float cx, float cy, float r)
            => (float)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

        private static float Capsule(float x, float y, float ax, float ay, float bx, float by, float r)
        {
            float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            float h = Math.Clamp((px * dx + py * dy) / (dx * dx + dy * dy), 0f, 1f);
            float qx = px - dx * h, qy = py - dy * h;
            return (float)Math.Sqrt(qx * qx + qy * qy) - r;
        }

        private static float Ellipse(float x, float y, float cx, float cy, float rx, float ry)
        {
            float dx = (x - cx) / rx, dy = (y - cy) / ry;
            return ((float)Math.Sqrt(dx * dx + dy * dy) - 1f) * Math.Min(rx, ry);
        }

        private static float Box(float x, float y, float cx, float cy, float hw, float hh)
        {
            float qx = Math.Abs(x - cx) - hw, qy = Math.Abs(y - cy) - hh;
            float ox = Math.Max(qx, 0f), oy = Math.Max(qy, 0f);
            return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0f);
        }

        /// <summary>A sword, point up and right: blade, guard, grip and pommel.</summary>
        private static float Sword(float x, float y)
        {
            float d = Capsule(x, y, 11.5f, 20.5f, 25f, 7f, 2.7f);
            d = Math.Min(d, Capsule(x, y, 6.5f, 16.5f, 15.5f, 25.5f, 2.3f));
            d = Math.Min(d, Capsule(x, y, 11f, 21f, 6.5f, 25.5f, 2.2f));
            return Math.Min(d, Circle(x, y, 5.5f, 26.5f, 3f));
        }

        /// <summary>A purse: a round body, its neck, its mouth flared.</summary>
        private static float Sack(float x, float y)
        {
            float d = Ellipse(x, y, 16f, 21.5f, 10.5f, 8.5f);
            d = Math.Min(d, Box(x, y, 16f, 12.8f, 3.4f, 2.4f));
            return Math.Min(d, Ellipse(x, y, 16f, 8.3f, 6.5f, 3.2f));
        }

        /// <summary>The tie round the purse's neck, in the edge's colour.</summary>
        private static bool Knot(float x, float y) => Capsule(x, y, 12f, 13.2f, 20f, 13.2f, 1.1f) < 0f;

        /// <summary>A chest: its body and its lid, a little rounded.</summary>
        private static float Chest(float x, float y)
            => Math.Min(Box(x, y, 16f, 20f, 11f, 7f) - 1f, Box(x, y, 16f, 10.5f, 11f, 3.5f) - 1.5f);

        /// <summary>The chest's lid line and its lock, in the edge's colour.</summary>
        private static bool ChestLines(float x, float y)
            => Math.Abs(y - 14f) < 0.8f || Box(x, y, 16f, 15f, 2f, 2.6f) < 0f;

        /// <summary>Three bars side by side, of three heights: the characteristics.</summary>
        private static float Bars(float x, float y)
        {
            float d = Box(x, y, 8.5f, 21.5f, 2.6f, 5.5f) - 0.6f;
            d = Math.Min(d, Box(x, y, 16f, 16.5f, 2.6f, 10.5f) - 0.6f);
            return Math.Min(d, Box(x, y, 23.5f, 19f, 2.6f, 8f) - 0.6f);
        }

        /// <summary>A four-pointed star, its points long and thin: the spells.</summary>
        private static float Star(float x, float y)
        {
            float dx = Math.Abs(x - 16f), dy = Math.Abs(y - 16f);
            // Within each quarter, the star's edge runs from a point's tip to the waist between them.
            float d = Math.Min(Capsule(dx, dy, 0f, 0f, 0f, 13f, 2.2f), Capsule(dx, dy, 0f, 0f, 13f, 0f, 2.2f));
            float waist = ((dx + dy) - 9f) * 0.75f;
            return Math.Min(d, waist);
        }

        /// <summary>A «play» triangle pointing right, its corners rounded.</summary>
        private static float Play(float x, float y)
        {
            float[] vx = { 10f, 10f, 25f }, vy = { 7f, 25f, 16f };
            float best = float.MaxValue;
            int positive = 0, negative = 0;
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                float ex = vx[j] - vx[i], ey = vy[j] - vy[i], wx = x - vx[i], wy = y - vy[i];
                float h = Math.Clamp((wx * ex + wy * ey) / (ex * ex + ey * ey), 0f, 1f);
                float qx = wx - ex * h, qy = wy - ey * h;
                best = Math.Min(best, qx * qx + qy * qy);
                if (ex * wy - ey * wx >= 0) positive++; else negative++;
            }
            float d = (float)Math.Sqrt(best);
            return (positive == 3 || negative == 3 ? -d : d) - 1.2f;
        }

        private static VisualElement Tool(Il2CppSystem.Action click, string text)
        {
            var tool = new VisualElement();
            tool.name = Pressable;
            tool.style.width = new StyleLength(24f);
            tool.style.height = new StyleLength(20f);
            tool.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            tool.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.30f, 0.30f, 0.44f, 1f));
            Round(tool, 3f);
            var label = Label(text, 12f, true);
            label.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            label.style.marginBottom = new StyleLength(0f);
            tool.Add(label);
            tool.AddManipulator(new Clickable(click).Cast<IManipulator>());
            Lively(tool);
            return tool;
        }

        private static VisualElement Crown()
        {
            var crown = new VisualElement();
            crown.style.position = new StyleEnum<Position>(Position.Absolute);
            crown.style.left = new StyleLength(10f);
            crown.style.top = new StyleLength(10f);
            crown.style.width = new StyleLength(14f);
            crown.style.height = new StyleLength(10f);
            crown.style.backgroundColor = new StyleColor(Gold);
            crown.style.borderBottomLeftRadius = crown.style.borderBottomRightRadius = new StyleLength(2f);
            crown.style.borderTopLeftRadius = crown.style.borderTopRightRadius = new StyleLength(5f);
            return crown;
        }

        private static VisualElement Light(bool on)
        {
            var light = new VisualElement();
            light.style.position = new StyleEnum<Position>(Position.Absolute);
            light.style.right = new StyleLength(12f);
            light.style.top = new StyleLength(12f);
            light.style.width = light.style.height = new StyleLength(8f);
            light.style.backgroundColor = new StyleColor(on ? new UnityEngine.Color(0.80f, 0.95f, 0.10f, 1f) : new UnityEngine.Color(0.90f, 0.25f, 0.22f, 1f));
            Round(light, 4f);
            return light;
        }

        /// <summary>
        /// A button lit under the pointer and darker while pressed, as the game's own are: its
        /// colour as it stands when the pointer comes, put back when it goes.
        /// </summary>
        private static void Lively(VisualElement button)
        {
            UnityEngine.Color rest = default;
            bool over = false;
            void Paint(float towardsWhite, float towardsBlack)
            {
                var c = UnityEngine.Color.Lerp(UnityEngine.Color.Lerp(rest, UnityEngine.Color.white, towardsWhite), UnityEngine.Color.black, towardsBlack);
                c.a = rest.a;
                button.style.backgroundColor = new StyleColor(c);
            }
            try
            {
                button.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
                    new Action<PointerEnterEvent>(_ =>
                    {
                        if (!over) rest = button.resolvedStyle.backgroundColor;
                        over = true;
                        Paint(0.22f, 0f);
                    })), TrickleDown.NoTrickleDown);
                button.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
                    new Action<PointerLeaveEvent>(_ =>
                    {
                        if (over) button.style.backgroundColor = new StyleColor(rest);
                        over = false;
                    })), TrickleDown.NoTrickleDown);
                button.RegisterCallback<PointerDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                    new Action<PointerDownEvent>(_ => { if (over) Paint(0f, 0.18f); })), TrickleDown.TrickleDown);
                button.RegisterCallback<PointerUpEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerUpEvent>>(
                    new Action<PointerUpEvent>(_ => { if (over) Paint(0.22f, 0f); })), TrickleDown.TrickleDown);
            }
            catch { }
        }

        /// <summary>The glow behind a portrait: green in the middle, the night's blue on the edges, drawn once.</summary>
        private static UnityEngine.Texture2D Glow()
        {
            if (_glow != null) return _glow;
            const int size = 64;
            var texture = new UnityEngine.Texture2D(size, size);
            var middle = new UnityEngine.Color(0.36f, 0.70f, 0.52f, 1f);
            var edge = new UnityEngine.Color(0.24f, 0.30f, 0.42f, 1f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - size / 2f) / (size / 2f), dy = (y - size * 0.45f) / (size / 2f);
                    float t = Math.Min(1f, (float)Math.Sqrt(dx * dx * 0.8f + dy * dy));
                    texture.SetPixel(x, y, UnityEngine.Color.Lerp(middle, edge, t * t));
                }
            texture.Apply();
            texture.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            texture.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset;
            return _glow = texture;
        }

        // ─── The client's pieces ─────────────────────────────────────────────────────────

        private static readonly UnityEngine.Color Gold = new UnityEngine.Color(1f, 0.82f, 0.35f, 1f);
        private static readonly UnityEngine.Color Red = new UnityEngine.Color(1f, 0.45f, 0.40f, 1f);

        private static void Round(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius = new StyleLength(radius);
            element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = new StyleLength(radius);
        }

        private static DofusLabel Label(string text, float size = 14f, bool bold = false)
        {
            var label = new DofusLabel();
            label.text = text;
            label.AddToClassList("textShort_largeRegular");
            label.AddToClassList("textColor_white_white100");
            label.style.fontSize = new StyleLength(size);
            if (bold) label.style.unityFontStyleAndWeight = new StyleEnum<UnityEngine.FontStyle>(UnityEngine.FontStyle.Bold);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            label.style.marginBottom = new StyleLength(4f);
            return label;
        }

        private static DofusLabel Note(string text)
        {
            var note = Label(text, 12f);
            note.style.color = new StyleColor(new UnityEngine.Color(0.70f, 0.70f, 0.78f, 1f));
            note.style.marginBottom = new StyleLength(0f);
            return note;
        }

        private static VisualElement Button(Il2CppSystem.Action click, string text, bool strong, bool enabled)
        {
            var button = new VisualElement();
            button.style.paddingLeft = button.style.paddingRight = new StyleLength(16f);
            button.style.paddingTop = button.style.paddingBottom = new StyleLength(6f);
            button.style.backgroundColor = new StyleColor(strong
                ? new UnityEngine.Color(0.45f, 0.62f, 0.12f, 1f)
                : new UnityEngine.Color(0.36f, 0.36f, 0.48f, 1f));
            Round(button, 4f);
            var label = Label(text, 12f, true);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            label.style.marginBottom = new StyleLength(0f);
            button.Add(label);
            if (enabled)
            {
                button.AddManipulator(new Clickable(click).Cast<IManipulator>());
                Lively(button);
            }
            else button.style.opacity = new StyleFloat(0.4f);
            return button;
        }

        private static Il2CppSystem.Action Do(Action action)
            => DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[Heroes] {ex.Message}"); }
            }));

        private static bool ShortcutPressed()
        {
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                return keyboard != null && keyboard.tabKey.wasPressedThisFrame
                       && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Tab alone, neither Ctrl -- the window's -- nor Shift nor Alt held with it.</summary>
        private static bool NextPressed()
        {
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                return keyboard != null && keyboard.tabKey.wasPressedThisFrame
                       && !keyboard.ctrlKey.isPressed && !keyboard.shiftKey.isPressed && !keyboard.altKey.isPressed;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Whether a text field has the keyboard -- the chat, a search --: Tab is its own there.</summary>
        private static bool Typing()
        {
            try
            {
                var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
                for (int i = 0; i < documents.Length; i++)
                {
                    var focused = documents[i]?.TryCast<UIDocument>()?.rootVisualElement?.panel?.focusController?.focusedElement;
                    if (focused == null) continue;
                    string type = focused.GetIl2CppType().Name;
                    if (type.Contains("Text") || type.Contains("Input")) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>The layer the client opens its own windows on (RootHigh), else the top document's root.</summary>
        private static VisualElement FindLayer()
        {
            VisualElement high = null, topRoot = null;
            float topOrder = float.MinValue;
            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            for (int i = 0; i < documents.Length; i++)
            {
                var document = documents[i]?.TryCast<UIDocument>();
                var root = document?.rootVisualElement;
                if (root == null) continue;
                if (high == null)
                {
                    var named = UQueryExtensions.Q(root, "RootHigh", (string)null);
                    if (named != null && named.panel != null) high = named;
                }
                float width = root.resolvedStyle.width;
                if (!float.IsNaN(width) && width > 0 && document.sortingOrder >= topOrder)
                {
                    topOrder = document.sortingOrder;
                    topRoot = root;
                }
            }
            return high ?? topRoot;
        }

        private static void KeepEvents(VisualElement window)
        {
            try { window.RegisterCallback<PointerDownEvent>(Stop<PointerDownEvent>(), TrickleDown.NoTrickleDown); } catch { }
            try { window.RegisterCallback<PointerUpEvent>(Stop<PointerUpEvent>(), TrickleDown.NoTrickleDown); } catch { }
            try { window.RegisterCallback<MouseDownEvent>(Stop<MouseDownEvent>(), TrickleDown.NoTrickleDown); } catch { }
            try { window.RegisterCallback<MouseUpEvent>(Stop<MouseUpEvent>(), TrickleDown.NoTrickleDown); } catch { }
            try { window.RegisterCallback<ClickEvent>(Stop<ClickEvent>(), TrickleDown.NoTrickleDown); } catch { }
            try { window.RegisterCallback<WheelEvent>(Stop<WheelEvent>(), TrickleDown.NoTrickleDown); } catch { }
        }

        private static EventCallback<T> Stop<T>() where T : EventBase<T>, new()
            => DelegateSupport.ConvertDelegate<EventCallback<T>>(new Action<T>(e =>
            {
                try { e.StopPropagation(); } catch { }
            }));

        // ─── The mouse is the window's ───────────────────────────────────────────────────

        private static bool _guardInstalled;

        /// <summary>Whether the pointer is on the window or on the group panel this frame.</summary>
        public static bool PointerOver => Over(_window) || Over(_panel) || Over(_sheet) || Over(_creationWindow);

        private static bool Over(VisualElement element)
        {
            if (element == null) return false;
            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                var panel = element.panel;
                if (mouse == null || panel == null) return false;
                var at = mouse.position.ReadValue();
                var onPanel = RuntimePanelUtils.ScreenToPanel(panel, new UnityEngine.Vector2(at.x, UnityEngine.Screen.height - at.y));
                return element.worldBound.Contains(onPanel);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A click on the window is not a click on the map: while the pointer is on it the game's
        /// InputUtility says «over the interface» and reports no button, as Cazacabezas does.
        /// </summary>
        private static void InstallInputGuard()
        {
            if (_guardInstalled) return;
            _guardInstalled = true;
            var harmony = new HarmonyLib.Harmony("jondo.heroes.input");
            var overUi = new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(SayOverUi)));
            var noButton = new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(SayNoButton)));
            var type = typeof(Il2CppAnkama.Utilities.InputUtility);
            void Patch(string name, HarmonyLib.HarmonyMethod postfix)
            {
                try
                {
                    var method = HarmonyLib.AccessTools.Method(type, name);
                    if (method != null) harmony.Patch(method, postfix: postfix);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Heroes] InputUtility.{name} not guarded: {ex.Message}");
                }
            }
            Patch("get_isPointerOverUI", overUi);
            foreach (string name in new[] { "GetPointerDown", "IsPointerDown", "GetPointerUp", "GetSecondaryDown", "IsSecondaryDown",
                                            "GetSecondaryUp", "GetTertiaryDown", "IsTertiaryDown", "GetTertiaryUp" })
                Patch(name, noButton);
        }

        // ─── Escape is the window's ──────────────────────────────────────────────────────

        /// <summary>An Escape the windows took this frame, for <see cref="OnUpdate"/> to close the top one.</summary>
        private static volatile bool _escapePressed;

        /// <summary>Whether Escape was down in the keyboard's last event: a press is its going down.</summary>
        private static bool _escapeDown;

        /// <summary>Whether one of the mod's windows is open, for Escape to close it.</summary>
        private static bool AnyWindowOpen => _window != null || _sheet != null || _creationWindow != null || _alert != null;

        /// <summary>
        /// Escape closes the mod's window on top, as it closes the game's own (dagostin, 2026-11-21).
        /// The windows are none of the game's: Escape went past them to the game, which opened its
        /// pause menu over them and left them open whatever was pressed. While one is open the
        /// keyboard's events that hold Escape down are taken out of the Input System before the game
        /// reads them (InputEventPtr.handled), and the mod closes its window itself. Measured with
        /// the MCP's client on 2026-11-21: Escape opened the pause menu over the window, now it closes it.
        /// </summary>
        private static void InstallEscapeGuard()
        {
            try
            {
                var listener = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UnityEngine.InputSystem.LowLevel.InputEventPtr, UnityEngine.InputSystem.InputDevice>>(
                    new Action<UnityEngine.InputSystem.LowLevel.InputEventPtr, UnityEngine.InputSystem.InputDevice>(OnInputEvent));
                UnityEngine.InputSystem.InputSystem.onEvent += listener;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] Escape not guarded: {ex.Message}");
            }
        }

        private static readonly int StateEvent = FourCC("STAT"), KeyboardState = FourCC("KEYS");

        private static int FourCC(string s) => (s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3];

        /// <summary>
        /// One of the Input System's events: a keyboard's state with Escape down is the windows'.
        /// </summary>
        /// <remarks>
        /// What the converted delegate is handed is not the InputEventPtr but where it is kept: read
        /// as it came, the event's type was garbage («must be a StateEvent ... but is a 0"}d»), and
        /// once dereferenced it is the «STAT» the keyboard sent. The event is read raw, as JondoFix's
        /// TestInput writes it: the header, «KEYS» at 20 and one bit a key from 24; «handled» is the
        /// high bit of the event's id at 16 (InputEvent.kHandledMask), which keeps it from the game.
        /// </remarks>
        private static unsafe void OnInputEvent(UnityEngine.InputSystem.LowLevel.InputEventPtr input, UnityEngine.InputSystem.InputDevice device)
        {
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard == null || device == null || device.deviceId != keyboard.deviceId) return;
                IntPtr held = (IntPtr)input.ToPointer();
                if (held == IntPtr.Zero) return;
                IntPtr e = Marshal.ReadIntPtr(held);
                if (e == IntPtr.Zero || Marshal.ReadInt32(e) != StateEvent || Marshal.ReadInt32(e, 20) != KeyboardState) return;
                int key = (int)UnityEngine.InputSystem.Key.Escape;
                bool down = (Marshal.ReadByte(e, 24 + key / 8) & (1 << (key % 8))) != 0;
                bool pressed = down && !_escapeDown;
                _escapeDown = down;
                if (!down || !AnyWindowOpen) return;
                Marshal.WriteInt32(e, 16, Marshal.ReadInt32(e, 16) | unchecked((int)0x80000000));
                if (pressed) _escapePressed = true;
            }
            catch { }
        }

        /// <summary>The mod's window on top closed: the creation screen, a hero's sheet, the quest alert, the window.</summary>
        private static void CloseTopmost()
        {
            if (_creationWindow != null) CloseNativeCreation();
            else if (_sheet != null) CloseSheet();
            else if (_alert != null) CloseQuestAlert();
            else if (_window != null)
            {
                if (_creating) StopCreation();
                else Close();
            }
        }

        public static void SayOverUi(ref bool __result)
        {
            if (PointerOver) __result = true;
        }

        public static void SayNoButton(ref bool __result)
        {
            if (PointerOver) __result = false;
        }
    }
}
