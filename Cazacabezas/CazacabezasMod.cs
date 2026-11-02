using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine.UIElements;

[assembly: MelonInfo(typeof(Cazacabezas.CazacabezasMod), "Cazacabezas", "1.0.0", "Jondo")]
[assembly: MelonGame("Ankama", "Dofus")]

namespace Cazacabezas
{
    /// <summary>
    /// The head hunters' window on the client: the hunter's record, his order and the board of prey
    /// of the other city, and a tracker on screen while a hunt is open -- his own, or one on him.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything it shows and does is the server's (Jondo's HeadHunts): it asks the control API with
    /// the account's own session token, which the launcher hands the client as JONDO_PLAYER_TOKEN,
    /// and the server answers for the account's character in the world. The mod decides nothing.
    /// </para>
    /// <para>
    /// It opens from a button it puts in the character sheet's alignment tab, beside the honour bar,
    /// or with F7. Built from the client's own pieces, as JondoFix's windows are: a WindowFigma, its
    /// tabs, its labels.
    /// </para>
    /// </remarks>
    public class CazacabezasMod : MelonMod
    {
        private const string Api = "http://127.0.0.1:8888/api/";
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly ConcurrentQueue<Action> _toUnity = new ConcurrentQueue<Action>();

        private static string _token = "";
        private static string _language;

        /// <summary>The server's last answer about this character, and when it came.</summary>
        private static JsonElement? _status;
        private static float _statusAt;
        private static List<JsonElement> _board = new List<JsonElement>();
        private static bool _polling;
        private static float _nextPoll, _nextScan;

        private static WindowFigma _window;
        private static VisualElement _page;
        private static DofusLabel _say;
        private static int _tab;
        private static VisualElement _tracker;
        private static DofusLabel _trackerText;

        public override void OnInitializeMelon()
        {
            _token = (Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN")
                      ?? Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ?? "").Trim();
            LoggerInstance.Msg(_token.Length > 0
                ? "[Cazacabezas] Ready: F7 or the alignment tab's button opens the head hunters."
                : "[Cazacabezas] No session token from the launcher: the head hunters stay asleep.");
        }

        public override void OnUpdate()
        {
            if (_token.Length == 0) return;
            while (_toUnity.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[Cazacabezas] {ex.Message}"); }
            }

            float now = UnityEngine.Time.unscaledTime;
            if (now >= _nextPoll && !_polling)
            {
                _nextPoll = now + (_window != null || HuntOn ? 2f : 6f);
                Poll();
            }
            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                try { PutButton(); } catch { }
            }
            if (F7Pressed())
            {
                if (_window != null) Close();
                else Open();
            }
            try { ShowTracker(); } catch { }
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
            ["title"] = L("Cazacabezas", "Head hunters", "Chasseurs de têtes"),
            ["button"] = L("CAZACABEZAS", "HEAD HUNTERS", "CHASSEURS DE TÊTES"),
            ["tab.mine"] = L("Mi caza", "My hunt", "Ma traque"),
            ["tab.board"] = L("Tablón", "Board", "Tableau"),
            ["off"] = L("El servidor no tiene activados los cazacabezas.", "The server has the head hunters off.", "Le serveur n'a pas activé les chasseurs de têtes."),
            ["world"] = L("Entra en el mundo con un personaje.", "Enter the world with a character.", "Entre dans le monde avec un personnage."),
            ["unreachable"] = L("No se puede hablar con el servidor.", "The server cannot be reached.", "Le serveur est injoignable."),
            ["loading"] = L("Preguntando al servidor...", "Asking the server...", "Demande au serveur..."),
            ["record"] = L("Grado {0} · {1} de honor · {2} de deshonor", "Grade {0} · {1} honour · {2} dishonour", "Grade {0} · {1} d'honneur · {2} de déshonneur"),
            ["purse"] = L("{0} chapeuvepés — cámbialos con Tomás Sacre (Bonta) o Ema Sacre (Brakmar), en las milicias.",
                          "{0} chapeuvepés — trade them with Tomás Sacre (Bonta) or Ema Sacre (Brakmar), at the militias.",
                          "{0} pévétons — échange-les avec Thomas Sacre (Bonta) ou Emma Sacre (Brâkmar), dans les milices."),
            ["tally"] = L("Cazas: {0} · Supervivencias: {1} · Fallidas: {2} · Racha: {3}", "Kills: {0} · Survived: {1} · Failed: {2} · Streak: {3}",
                          "Traques réussies : {0} · Survies : {1} · Échouées : {2} · Série : {3}"),
            ["cannot.neutral"] = L("Para cazar hay que jurar lealtad a Bonta o a Brakmar.", "To hunt you must swear to Bonta or Brakmar.", "Pour traquer il faut jurer fidélité à Bonta ou Brâkmar."),
            ["cannot.pvp"] = L("Activa tu modo JcJ para poder cazar.", "Switch your PvP on to hunt.", "Active ton mode JcJ pour traquer."),
            ["cannot.level"] = L("Hace falta nivel 50 para cazar cabezas.", "Head hunting needs level 50.", "La traque demande le niveau 50."),
            ["cannot.dishonour"] = L("Tu deshonor no te permite cazar: límpialo ganando agresiones equilibradas.", "Your dishonour bars you from hunting: clean it by winning balanced aggressions.",
                                     "Ton déshonneur t'interdit la traque : lave-le en gagnant des agressions équilibrées."),
            ["cannot.locked"] = L("Debes esperar {0} para otra orden.", "You must wait {0} for another order.", "Tu dois attendre {0} pour un autre ordre."),
            ["cannot.busy"] = L("Ya tienes una orden de caza.", "You already have a hunt order.", "Tu as déjà un ordre de traque."),
            ["cannot.target"] = L("Esa presa ya no está en el tablón.", "That prey is no longer on the board.", "Cette proie n'est plus au tableau."),
            ["cannot.full"] = L("Esa presa ya tiene tres cazadores.", "That prey already has three hunters.", "Cette proie a déjà trois chasseurs."),
            ["cannot.off"] = L("El servidor no tiene activados los cazacabezas.", "The server has the head hunters off.", "Le serveur n'a pas activé les chasseurs de têtes."),
            ["noorder"] = L("No tienes ninguna orden de caza. Elige una presa en el tablón.", "You have no hunt order. Pick a prey on the board.", "Tu n'as aucun ordre de traque. Choisis une proie au tableau."),
            ["prey"] = L("Presa: {0}, {1} nivel {2}, grado {3}", "Prey: {0}, {1} level {2}, grade {3}", "Proie : {0}, {1} niveau {2}, grade {3}"),
            ["open"] = L("¡CAZA ABIERTA! Quedan {0}.", "HUNT OPEN! {0} left.", "TRAQUE OUVERTE ! Il reste {0}."),
            ["seen"] = L("Vista por última vez en [{0},{1}].", "Last seen at [{0},{1}].", "Vue pour la dernière fois en [{0},{1}]."),
            ["rest"] = L("La presa descansa: la caza podrá reabrirse en {0}.", "The prey rests: the hunt may open again in {0}.", "La proie se repose : la traque pourra rouvrir dans {0}."),
            ["offline"] = L("La presa no está conectada. La caza se abrirá cuando entre en el mundo.", "The prey is not online. The hunt opens when they enter the world.",
                            "La proie n'est pas connectée. La traque s'ouvrira quand elle entrera dans le monde."),
            ["waiting"] = L("La presa está conectada: la caza se abrirá en unos segundos.", "The prey is online: the hunt opens in a few seconds.", "La proie est connectée : la traque s'ouvre dans quelques secondes."),
            ["bounty"] = L("Recompensa: {0} chapeuvepés · La orden caduca en {1}.", "Bounty: {0} chapeuvepés · The order runs out in {1}.", "Prime : {0} pévétons · L'ordre expire dans {1}."),
            ["abandon"] = L("Abandonar la orden", "Give the order up", "Abandonner l'ordre"),
            ["abandoned"] = L("Orden abandonada.", "Order given up.", "Ordre abandonné."),
            ["hunted"] = L("¡Te están cazando! Durante la caza no puedes ocultar tus alas.", "You are being hunted! During the hunt you cannot fold your wings.",
                           "On te traque ! Pendant la traque tu ne peux pas replier tes ailes."),
            ["huntedby"] = L("Hay {0} cazacabezas tras tu cabeza.", "{0} head hunter(s) are after your head.", "{0} chasseur(s) de têtes sont après ta tête."),
            ["refresh"] = L("Actualizar", "Refresh", "Actualiser"),
            ["empty"] = L("No hay presas a tu alcance: enemigos con el modo JcJ activo, de nivel 30 o más, desde 20 niveles por debajo del tuyo hasta 50 por encima.",
                          "No prey within reach: enemies with PvP on, level 30 or more, from 20 levels below yours to 50 above.",
                          "Aucune proie à ta portée : ennemis en mode JcJ, niveau 30 ou plus, de 20 niveaux sous le tien à 50 au-dessus."),
            ["row"] = L("{0} · {1} {2} · grado {3}", "{0} · {1} {2} · grade {3}", "{0} · {1} {2} · grade {3}"),
            ["rowpay"] = L("{0} chapeuvepés · cazadores {1}/3", "{0} chapeuvepés · hunters {1}/3", "{0} pévétons · chasseurs {1}/3"),
            ["online"] = L("conectado", "online", "connecté"),
            ["take"] = L("Cazar", "Hunt", "Traquer"),
            ["taken"] = L("¡Orden de caza aceptada!", "Hunt order taken!", "Ordre de traque accepté !"),
            ["tracker.hunter"] = L("Caza: {0} · {1} · [{2},{3}]", "Hunt: {0} · {1} · [{2},{3}]", "Traque : {0} · {1} · [{2},{3}]"),
            ["tracker.prey"] = L("¡Te cazan! ({0})", "You are hunted! ({0})", "On te traque ! ({0})"),
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

        private static string Clock(int seconds)
        {
            if (seconds >= 3600) return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
            return $"{seconds / 60}:{seconds % 60:00}";
        }

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
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("headhunts/status");
                _toUnity.Enqueue(() =>
                {
                    _polling = false;
                    _status = code == 200 ? body : null;
                    _statusAt = UnityEngine.Time.unscaledTime;
                    // A session the server does not know, or no server: asked again much later, not
                    // every few seconds into its log.
                    if (code == 401 || code == 0) _nextPoll = _statusAt + (code == 401 ? 120f : 20f);
                    if (_window != null && _tab == 0) ShowMine(code);
                });
            });
        }

        private static bool HuntOn
            => _status is JsonElement s && (Bool(s, "hunted") || (s.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Object && Bool(o, "open")));

        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        /// <summary>The seconds of a countdown the server gave, less the time since it gave it.</summary>
        private static int Since(int seconds) => Math.Max(0, seconds - (int)(UnityEngine.Time.unscaledTime - _statusAt));

        // ─── The window ──────────────────────────────────────────────────────────────────

        private static void Open()
        {
            try
            {
                var layer = FindLayer();
                if (layer == null) return;
                InstallInputGuard();
                var window = new WindowFigma(true, T("title"), "", true);
                window.showCloseButton = true;
                window.name = "JondoCazacabezas";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(780f);
                window.style.height = new StyleLength(560f);
                float w = layer.resolvedStyle.width, h = layer.resolvedStyle.height;
                window.style.left = new StyleLength(float.IsNaN(w) || w <= 0 ? 400f : Math.Max(0f, (w - 780f) / 2f));
                window.style.top = new StyleLength(float.IsNaN(h) || h <= 0 ? 160f : Math.Max(0f, (h - 560f) / 2f));

                var tabs = new DofusTabGroup();
                tabs.AddToClassList("SubHeader");
                try { tabs.size = DofusTab.TabSize.large; } catch { }
                tabs.Add(new DofusTab(Do(() => ShowTab(0)), T("tab.mine"), NoIcon()));
                tabs.Add(new DofusTab(Do(() => ShowTab(1)), T("tab.board"), NoIcon()));
                window.Add(tabs);

                _page = new ScrollView(ScrollViewMode.Vertical);
                _page.style.flexGrow = new StyleFloat(1f);
                _page.style.paddingLeft = new StyleLength(24f);
                _page.style.paddingRight = new StyleLength(24f);
                _page.style.paddingTop = new StyleLength(16f);
                window.Add(_page);

                layer.Add(window);
                window.BringToFront();
                KeepEvents(window);
                var close = window.header?.rightButton;
                if (close != null) close.add_Clicked(Do(Close));
                _window = window;
                ShowTab(_tab);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Cazacabezas] The window could not be opened: {ex}");
                Close();
            }
        }

        private static void Close()
        {
            try { _window?.RemoveFromHierarchy(); } catch { }
            _window = null;
            _page = null;
            _say = null;
        }

        private static void ShowTab(int tab)
        {
            _tab = tab;
            if (_page == null) return;
            _page.Clear();
            if (tab == 0)
            {
                ShowMine(_status != null ? 200 : -1);
                Poll();
            }
            else
            {
                _page.Add(Label(T("loading")));
                Task.Run(async () =>
                {
                    var (code, body) = await AskAsync("headhunts/board");
                    _toUnity.Enqueue(() => ShowBoard(code, body));
                });
            }
        }

        /// <summary>The hunter's own page: his record, his purse, his order -- or why he cannot hunt.</summary>
        private static void ShowMine(int code)
        {
            if (_page == null || _tab != 0) return;
            _page.Clear();
            if (code == 0) { _page.Add(Label(T("unreachable"))); return; }
            if (code == -1) { _page.Add(Label(T("loading"))); return; }
            if (_status is not JsonElement s) { _page.Add(Label(T("world"))); return; }
            if (!Bool(s, "on")) { _page.Add(Label(T("off"))); return; }

            _page.Add(Label(Str(s, "character"), true));
            _page.Add(Label(T("record", Int(s, "grade"), Int(s, "honour"), Int(s, "dishonour"))));
            _page.Add(Label(T("purse", Int(s, "chapeuvepes"))));
            _page.Add(Label(T("tally", Int(s, "kills"), Int(s, "survivals"), Int(s, "failures"), Int(s, "streak"))));

            if (Int(s, "huntedBy") > 0)
            {
                var warning = Label(Bool(s, "hunted") ? T("hunted") : T("huntedby", Int(s, "huntedBy")), true);
                warning.style.color = new StyleColor(Red);
                warning.style.marginTop = new StyleLength(12f);
                _page.Add(warning);
            }

            var gap = Label("");
            gap.style.height = new StyleLength(12f);
            _page.Add(gap);

            if (s.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Object)
            {
                _page.Add(Label(T("prey", Str(order, "name"), ClassOf(Int(order, "breed")), Int(order, "level"), Int(order, "grade")), true));
                if (Bool(order, "open"))
                {
                    var open = Label(T("open", Clock(Since(Int(order, "openSeconds")))), true);
                    open.style.color = new StyleColor(Gold);
                    _page.Add(open);
                    if (order.TryGetProperty("seenX", out var x) && x.ValueKind == JsonValueKind.Number)
                        _page.Add(Label(T("seen", x.GetInt32(), Int(order, "seenY"))));
                }
                else if (Int(order, "restSeconds") > 0) _page.Add(Label(T("rest", Clock(Since(Int(order, "restSeconds"))))));
                else _page.Add(Label(Bool(order, "online") ? T("waiting") : T("offline")));
                _page.Add(Label(T("bounty", Int(order, "bounty"), Clock(Since(Int(order, "expiresSeconds"))))));
                _page.Add(Button(Do(Abandon), T("abandon"), false));
            }
            else
            {
                string cannot = Str(s, "cannot");
                _page.Add(Label(cannot.Length > 0 ? Reason(cannot, Int(s, "lockedSeconds")) : T("noorder")));
            }
            _say = Label("");
            _page.Add(_say);
        }

        private static string Reason(string why, int lockedSeconds)
            => why == "locked" ? T("cannot.locked", Clock(lockedSeconds)) : T("cannot." + why);

        /// <summary>The board: the prey within reach, the online first, each with its bounty and a button to hunt it.</summary>
        private static void ShowBoard(int code, JsonElement? body)
        {
            if (_page == null || _tab != 1) return;
            _page.Clear();
            var top = Row();
            top.Add(Button(Do(() => ShowTab(1)), T("refresh"), false));
            _page.Add(top);
            _say = Label("");
            _page.Add(_say);
            if (code == 0) { _page.Add(Label(T("unreachable"))); return; }
            if (code != 200 || body is not JsonElement b || !b.TryGetProperty("prey", out var prey)) { _page.Add(Label(T("world"))); return; }
            if (_status is JsonElement s && Str(s, "cannot").Length > 0) _page.Add(Label(Reason(Str(s, "cannot"), Int(s, "lockedSeconds"))));
            _board = prey.EnumerateArray().ToList();
            if (_board.Count == 0) { _page.Add(Label(T("empty"))); return; }
            foreach (var one in _board)
            {
                var row = Row();
                row.style.alignItems = new StyleEnum<Align>(Align.Center);
                row.style.marginBottom = new StyleLength(8f);
                var who = new VisualElement();
                who.style.flexGrow = new StyleFloat(1f);
                var name = Label(T("row", Str(one, "name"), ClassOf(Int(one, "breed")), Int(one, "level"), Int(one, "grade")) +
                                 (Bool(one, "online") ? " · " + T("online") : ""), true);
                if (Bool(one, "online")) name.style.color = new StyleColor(Green);
                who.Add(name);
                who.Add(Label(T("rowpay", Int(one, "bounty"), Int(one, "hunters"))));
                row.Add(who);
                long id = Long(one, "id");
                row.Add(Button(Do(() => Take(id)), T("take"), true));
                _page.Add(row);
            }
        }

        private static void Take(long target)
        {
            Task.Run(async () =>
            {
                var (code, body) = await AskAsync("headhunts/take", new Dictionary<string, object> { ["target"] = target });
                _toUnity.Enqueue(() =>
                {
                    if (code == 200)
                    {
                        Say(T("taken"));
                        Poll();
                        ShowTab(0);
                    }
                    else Say(code == 0 ? T("unreachable") : Reason(body is JsonElement b ? Str(b, "error") : "", 0));
                });
            });
        }

        private static void Abandon()
        {
            Task.Run(async () =>
            {
                var (code, _) = await AskAsync("headhunts/abandon");
                _toUnity.Enqueue(() =>
                {
                    Say(code == 200 ? T("abandoned") : T("unreachable"));
                    Poll();
                });
            });
        }

        private static void Say(string text)
        {
            if (_say != null) _say.text = text;
        }

        // ─── The tracker ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// A strip at the top of the screen while a hunt is open: the prey, the time left and where
        /// it was seen, for the hunter; a warning, for the prey.
        /// </summary>
        private static void ShowTracker()
        {
            string text = null;
            if (_status is JsonElement s)
            {
                if (s.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Object && Bool(o, "open"))
                {
                    string x = o.TryGetProperty("seenX", out var sx) && sx.ValueKind == JsonValueKind.Number ? sx.GetInt32().ToString(CultureInfo.InvariantCulture) : "?";
                    string y = o.TryGetProperty("seenY", out var sy) && sy.ValueKind == JsonValueKind.Number ? sy.GetInt32().ToString(CultureInfo.InvariantCulture) : "?";
                    text = T("tracker.hunter", Str(o, "name"), Clock(Since(Int(o, "openSeconds"))), x, y);
                }
                else if (Bool(s, "hunted"))
                {
                    text = T("tracker.prey", Int(s, "huntedBy"));
                }
            }

            if (text == null)
            {
                if (_tracker != null) { try { _tracker.RemoveFromHierarchy(); } catch { } _tracker = null; }
                return;
            }
            if (_tracker == null || _tracker.panel == null)
            {
                var layer = FindLayer();
                if (layer == null) return;
                // A row across the top of the screen, its box in the middle: the clicks go through.
                _tracker = new VisualElement();
                _tracker.name = "JondoCazacabezasTracker";
                _tracker.pickingMode = PickingMode.Ignore;
                _tracker.style.position = new StyleEnum<Position>(Position.Absolute);
                _tracker.style.top = new StyleLength(12f);
                _tracker.style.left = new StyleLength(0f);
                _tracker.style.right = new StyleLength(0f);
                _tracker.style.alignItems = new StyleEnum<Align>(Align.Center);
                var box = new VisualElement();
                box.pickingMode = PickingMode.Ignore;
                box.style.paddingLeft = new StyleLength(18f);
                box.style.paddingRight = new StyleLength(18f);
                box.style.paddingTop = new StyleLength(6f);
                box.style.paddingBottom = new StyleLength(6f);
                box.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.35f, 0.04f, 0.04f, 0.82f));
                box.style.borderTopLeftRadius = new StyleLength(8f);
                box.style.borderTopRightRadius = new StyleLength(8f);
                box.style.borderBottomLeftRadius = new StyleLength(8f);
                box.style.borderBottomRightRadius = new StyleLength(8f);
                _trackerText = Label("", true);
                _trackerText.pickingMode = PickingMode.Ignore;
                _trackerText.style.marginBottom = new StyleLength(0f);
                box.Add(_trackerText);
                _tracker.Add(box);
                layer.Add(_tracker);
            }
            if (_trackerText != null && _trackerText.text != text) _trackerText.text = text;
        }

        // ─── The alignment tab's button ──────────────────────────────────────────────────

        /// <summary>
        /// The button beside the honour bar of the character sheet's alignment tab, put there
        /// whenever the tab is drawn and the button is not (pb_honour, AlignmentTabBinding's).
        /// </summary>
        private static void PutButton()
        {
            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            for (int i = 0; i < documents.Length; i++)
            {
                var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                var bar = root == null ? null : UQueryExtensions.Q(root, "pb_honour", (string)null);
                if (bar == null || bar.parent == null || bar.panel == null) continue;
                var place = bar.parent;
                if (UQueryExtensions.Q(place, "JondoCazacabezasButton", (string)null) != null) return;
                var button = Button(Do(Open), T("button"), true);
                button.name = "JondoCazacabezasButton";
                button.style.marginTop = new StyleLength(8f);
                button.style.alignSelf = new StyleEnum<Align>(Align.FlexStart);
                place.Add(button);
                return;
            }
        }

        // ─── The client's pieces ─────────────────────────────────────────────────────────

        private static readonly UnityEngine.Color LightText = new UnityEngine.Color(0.93f, 0.90f, 0.82f, 1f);
        private static readonly UnityEngine.Color Gold = new UnityEngine.Color(1f, 0.82f, 0.35f, 1f);
        private static readonly UnityEngine.Color Red = new UnityEngine.Color(1f, 0.45f, 0.40f, 1f);
        private static readonly UnityEngine.Color Green = new UnityEngine.Color(0.55f, 0.90f, 0.45f, 1f);
        private static readonly UnityEngine.Color Plain = new UnityEngine.Color(0.30f, 0.20f, 0.12f, 0.95f);
        private static readonly UnityEngine.Color Strong = new UnityEngine.Color(0.55f, 0.12f, 0.08f, 0.95f);

        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry NoIcon()
            => new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry();

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            return row;
        }

        private static DofusLabel Label(string text, bool bold = false)
        {
            var label = new DofusLabel();
            label.text = text;
            label.AddToClassList("textShort_largeRegular");
            label.AddToClassList("textColor_white_white100");
            if (bold) label.style.unityFontStyleAndWeight = new StyleEnum<UnityEngine.FontStyle>(UnityEngine.FontStyle.Bold);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            label.style.marginBottom = new StyleLength(4f);
            return label;
        }

        private static VisualElement Button(Il2CppSystem.Action click, string text, bool strong)
        {
            var button = new VisualElement();
            button.style.flexShrink = new StyleFloat(0f);
            button.style.paddingLeft = new StyleLength(16f);
            button.style.paddingRight = new StyleLength(16f);
            button.style.paddingTop = new StyleLength(8f);
            button.style.paddingBottom = new StyleLength(8f);
            button.style.marginTop = new StyleLength(6f);
            button.style.marginRight = new StyleLength(8f);
            button.style.backgroundColor = new StyleColor(strong ? Strong : Plain);
            button.style.borderTopLeftRadius = new StyleLength(6f);
            button.style.borderTopRightRadius = new StyleLength(6f);
            button.style.borderBottomLeftRadius = new StyleLength(6f);
            button.style.borderBottomRightRadius = new StyleLength(6f);
            var label = Label(text, true);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            label.style.color = new StyleColor(strong ? UnityEngine.Color.white : LightText);
            label.style.marginBottom = new StyleLength(0f);
            button.Add(label);
            button.AddManipulator(new Clickable(click).Cast<IManipulator>());
            return button;
        }

        private static Il2CppSystem.Action Do(Action action)
            => DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[Cazacabezas] {ex.Message}"); }
            }));

        private static bool F7Pressed()
        {
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                return keyboard != null && keyboard.f7Key.wasPressedThisFrame;
            }
            catch
            {
                return false;
            }
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

        /// <summary>Whether the pointer is on the window this frame.</summary>
        public static bool PointerOver
        {
            get
            {
                if (_window == null) return false;
                try
                {
                    var mouse = UnityEngine.InputSystem.Mouse.current;
                    var panel = _window.panel;
                    if (mouse == null || panel == null) return false;
                    var at = mouse.position.ReadValue();
                    var onPanel = RuntimePanelUtils.ScreenToPanel(panel, new UnityEngine.Vector2(at.x, UnityEngine.Screen.height - at.y));
                    return _window.worldBound.Contains(onPanel);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// A click on the window is not a click on the map: while the pointer is on it the game's
        /// InputUtility says «over the interface» and reports no button, as JondoFix's windows do.
        /// </summary>
        private static void InstallInputGuard()
        {
            if (_guardInstalled) return;
            _guardInstalled = true;
            var harmony = new HarmonyLib.Harmony("jondo.cazacabezas.input");
            var overUi = new HarmonyLib.HarmonyMethod(typeof(CazacabezasMod).GetMethod(nameof(SayOverUi)));
            var noButton = new HarmonyLib.HarmonyMethod(typeof(CazacabezasMod).GetMethod(nameof(SayNoButton)));
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
                    MelonLogger.Warning($"[Cazacabezas] InputUtility.{name} not guarded: {ex.Message}");
                }
            }
            Patch("get_isPointerOverUI", overUi);
            foreach (string name in new[] { "GetPointerDown", "IsPointerDown", "GetPointerUp", "GetSecondaryDown", "IsSecondaryDown",
                                            "GetSecondaryUp", "GetTertiaryDown", "IsTertiaryDown", "GetTertiaryUp" })
                Patch(name, noButton);
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
