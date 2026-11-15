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
            if (_token.Length > 0) WatchMainMenu();
        }

        public override void OnUpdate()
        {
            if (_token.Length == 0) return;
            while (_toUnity.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[Heroes] {ex.Message}"); }
            }

            // Asked all along, not only with the window open: the group panel shows the life of
            // the group, and the portraits are fetched ahead of the window's first opening.
            float now = UnityEngine.Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                try { PutMenuButton(); } catch { }
            }
            if (now >= _nextPoll && !_polling)
            {
                _nextPoll = now + (_window != null || _panel != null ? 3f : 8f);
                if (_listCode == 401) _nextPoll = now + 120f;
                Poll();
            }
            if (ShortcutPressed())
            {
                if (_window != null) Close();
                else Open();
            }
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
            ["auto"] = L("AUTO", "AUTO", "AUTO"),
            ["loot"] = L("BOTÍN", "LOOT", "BUTIN"),
            ["play"] = L("JUGAR", "PLAY", "JOUER"),
            ["menu"] = L("Abre una vez el menú (Esc) y vuelve a probar.", "Open the menu once (Esc) and try again.",
                         "Ouvre une fois le menu (Échap) et réessaie."),
        };

        private static string T(string key, params object[] values)
        {
            if (!Texts.TryGetValue(key, out var byLanguage)) return key;
            string text = byLanguage.TryGetValue(Language, out var own) ? own : byLanguage["en"];
            return values.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, values);
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
                var (code, body) = await AskAsync("heroes/list");
                _toUnity.Enqueue(() =>
                {
                    _polling = false;
                    _listCode = code;
                    _list = code == 200 ? body : null;
                    if (_list is JsonElement l && l.TryGetProperty("characters", out var all))
                        foreach (var one in all.EnumerateArray()) AskPortrait(Long(one, "id"));
                    Render();
                    try { RenderPanel(); } catch (Exception ex) { MelonLogger.Warning($"[Heroes] The group panel: {ex.Message}"); }
                });
            });
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
            _grid = null;
            _footer = null;
            _say = null;
        }

        private static void Say(string text)
        {
            if (_say != null) _say.text = text;
        }

        /// <summary>The cards: the master, his heroes, the others of the account, and empty places to eight.</summary>
        private static void Render()
        {
            if (_grid == null) return;
            _grid.Clear();
            if (_listCode == -1 && _list == null) { _grid.Add(Label(T("loading"))); return; }
            if (_listCode == 0) { _grid.Add(Label(T("unreachable"))); return; }
            if (_list is not JsonElement list || !list.TryGetProperty("characters", out var characters))
            {
                _grid.Add(Label(T("world")));
                return;
            }
            if (!Bool(list, "on")) { _grid.Add(Label(T("off"))); return; }

            int max = Int(list, "max");
            bool roomLeft = Int(list, "connected") < 1 + max;
            bool masterFighting = Bool(list, "inFight");
            var all = characters.EnumerateArray().ToList();
            for (int row = 0; row < 2; row++)
            {
                var line = new VisualElement();
                line.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                line.style.flexGrow = new StyleFloat(1f);
                line.style.flexBasis = new StyleLength(0f);
                for (int column = 0; column < 4; column++)
                {
                    int at = row * 4 + column;
                    line.Add(at < all.Count && at < Places ? Card(all[at], roomLeft, masterFighting) : Empty());
                }
                _grid.Add(line);
            }
            if (_footer != null) _footer.text = T("connected", Int(list, "connected"), 1 + max);
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

        private static VisualElement Empty()
        {
            var place = Place();
            place.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.13f, 0.14f, 0.22f, 0.55f));
            Round(place, 3f);
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
            if (_portraits.TryGetValue(id, out var portrait))
            {
                picture.style.backgroundImage = new StyleBackground(portrait);
                picture.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
            }
            else AskPortrait(id);
            frame.Add(picture);
            if (!connected) frame.style.opacity = new StyleFloat(0.45f);
            card.Add(frame);

            // What can be done with him.
            var foot = new VisualElement();
            foot.style.height = new StyleLength(44f);
            foot.style.alignItems = new StyleEnum<Align>(Align.Center);
            foot.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
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
        private static bool _toldMenu;

        /// <summary>
        /// A Heroes button at the end of the MENUS bar's grid (BannerMenuBinding's gd_btnUis), the
        /// size of its neighbours, put back whenever the client draws the bar again.
        /// </summary>
        private static void PutMenuButton()
        {
            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            for (int i = 0; i < documents.Length; i++)
            {
                var root = documents[i]?.TryCast<UIDocument>()?.rootVisualElement;
                var grid = root == null ? null : UQueryExtensions.Q(root, "gd_btnUis", (string)null);
                if (grid == null || grid.panel == null) continue;
                if (UQueryExtensions.Q(grid, "JondoHeroesMenuButton", (string)null) != null) return;
                // The grid is a virtual one: its slots are in rows it makes from its data, not its
                // own children. The button goes into the last row that holds slots, after them.
                VisualElement row = null;
                LastRowOfSlots(grid, ref row);
                if (row == null) return;
                VisualElement model = row[row.childCount - 1];
                float w = model.resolvedStyle.width, h = model.resolvedStyle.height;
                if (float.IsNaN(w) || w <= 0) w = 32f;
                if (float.IsNaN(h) || h <= 0) h = 32f;
                if (!_toldMenu)
                {
                    _toldMenu = true;
                    MelonLogger.Msg($"[Heroes] MENUS bar: a row of {row.childCount} slot(s) of {w}x{h}.");
                }

                var button = new VisualElement();
                button.name = "JondoHeroesMenuButton";
                button.style.width = new StyleLength(w);
                button.style.height = new StyleLength(h);
                if (model != null)
                {
                    button.style.marginLeft = new StyleLength(model.resolvedStyle.marginLeft);
                    button.style.marginRight = new StyleLength(model.resolvedStyle.marginRight);
                    button.style.marginTop = new StyleLength(model.resolvedStyle.marginTop);
                    button.style.marginBottom = new StyleLength(model.resolvedStyle.marginBottom);
                }
                var picture = new VisualElement();
                picture.pickingMode = PickingMode.Ignore;
                picture.style.flexGrow = new StyleFloat(1f);
                picture.style.marginLeft = picture.style.marginRight = picture.style.marginTop = picture.style.marginBottom = new StyleLength(2f);
                picture.style.backgroundImage = new StyleBackground(Icon());
                picture.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
                button.Add(picture);
                button.AddManipulator(new Clickable(Do(() => { if (_window != null) Close(); else Open(); })).Cast<IManipulator>());
                row.Add(button);
                return;
            }
        }

        /// <summary>The last element under <paramref name="element"/> whose children are the client's slots.</summary>
        private static void LastRowOfSlots(VisualElement element, ref VisualElement row)
        {
            for (int i = 0; i < element.childCount; i++)
            {
                var child = element[i];
                if (child.TryCast<Il2CppCore.UILogic.Components.Slots.Slot>() != null)
                {
                    row = element;
                    continue;
                }
                LastRowOfSlots(child, ref row);
            }
        }

        /// <summary>
        /// The button's icon, drawn here: a gold square like the bar's others, two heroes' busts
        /// side by side and a crown on the one in front. Three by three samples a pixel.
        /// </summary>
        private static UnityEngine.Texture2D Icon()
        {
            if (_icon != null) return _icon;
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
                            var c = IconAt(x, y, outline);
                            r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                        }
                    var color = a > 0 ? new UnityEngine.Color(r / a, g / a, b / a, a / 9f) : new UnityEngine.Color(0, 0, 0, 0);
                    texture.SetPixel(px, size - 1 - py, color);
                }
            texture.Apply();
            texture.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            texture.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset;
            return _icon = texture;
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
            if (group.Count < 2)
            {
                if (_panel != null) { try { _panel.RemoveFromHierarchy(); } catch { } _panel = null; }
                return;
            }
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
                bar.Add(Tool(Do(() => { _folded = !_folded; if (_members != null) _members.style.display = new StyleEnum<DisplayStyle>(_folded ? DisplayStyle.None : DisplayStyle.Flex); }), "—"));
                // The window's own icon, the MENUS button's: the game's font has no «≡».
                var opener = Tool(Do(() => { if (_window != null) Close(); else Open(); }), "");
                opener.style.backgroundImage = new StyleBackground(Icon());
                opener.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
                opener.style.backgroundColor = new StyleColor(new UnityEngine.Color(0, 0, 0, 0));
                bar.Add(opener);
                _panel.Add(bar);
                _members = new VisualElement();
                _members.style.display = new StyleEnum<DisplayStyle>(_folded ? DisplayStyle.None : DisplayStyle.Flex);
                _panel.Add(_members);
                KeepEvents(_panel);
                layer.Add(_panel);
            }
            _members.Clear();
            foreach (var who in group) _members.Add(Member(who, lootTo));
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
            row.Add(gauge);

            var picture = new VisualElement();
            picture.style.flexGrow = new StyleFloat(1f);
            picture.style.marginLeft = new StyleLength(3f);
            if (_portraits.TryGetValue(id, out var portrait))
            {
                picture.style.backgroundImage = new StyleBackground(portrait);
                picture.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
            }
            if (master) picture.Add(Crown());
            row.Add(picture);

            var tools = new VisualElement();
            tools.style.width = new StyleLength(38f);
            tools.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            if (!master)
            {
                bool auto = Bool(who, "autoPass");
                tools.Add(Toggle(Do(() => Act("heroes/autopass", id, new Dictionary<string, object> { ["on"] = auto ? 0 : 1 })), T("auto"), auto));
            }
            tools.Add(Toggle(Do(() => Act("heroes/loot", id)), T("loot"), lootTo == id));
            if (!master && !Bool(who, "inFight"))
            {
                string name = Str(who, "name");
                tools.Add(Toggle(Do(() => TakeInHand(name)), T("play"), false));
            }
            row.Add(tools);
            return row;
        }

        // ─── Taking a hero in hand ───────────────────────────────────────────────────────

        /// <summary>The client's main menu, kept as it comes up: its «change character» is what takes a hero in hand.</summary>
        private static Il2CppCore.UILogic.GameUICore.MainMenu _mainMenu;
        private static bool _menuWatched;

        /// <summary>
        /// Plays a hero from here on: the client changes to his character through its own main
        /// menu's «change character», and the server gives the group back to him on his way in --
        /// the one who led it is a hero of his then (Heroes.Regroup).
        /// </summary>
        private static void TakeInHand(string name)
        {
            if (_mainMenu == null)
            {
                Say(T("menu"));
                MelonLogger.Msg("[Heroes] No main menu kept yet: it is opened once with Esc.");
                return;
            }
            try
            {
                _mainMenu.InitCharactersList();
                if (_mainMenu.m_charsNames == null || !_mainMenu.m_charsNames.Contains(name))
                {
                    MelonLogger.Warning($"[Heroes] {name} is not in the main menu's characters.");
                    return;
                }
                _mainMenu.OnCharacterSelection(name);
                _mainMenu.OnConfirmCharacterSelection();
                MelonLogger.Msg($"[Heroes] Taking {name} in hand.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Heroes] {name} could not be taken in hand: {ex.Message}");
            }
        }

        private static void WatchMainMenu()
        {
            if (_menuWatched) return;
            _menuWatched = true;
            var harmony = new HarmonyLib.Harmony("jondo.heroes.mainmenu");
            var keep = new HarmonyLib.HarmonyMethod(typeof(HeroesMod).GetMethod(nameof(KeepMainMenu)));
            foreach (string name in new[] { "OnEnable", "Load", "InitCharactersList" })
            {
                try
                {
                    var method = HarmonyLib.AccessTools.Method(typeof(Il2CppCore.UILogic.GameUICore.MainMenu), name);
                    if (method != null) harmony.Patch(method, postfix: keep);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[Heroes] MainMenu.{name} not watched: {ex.Message}");
                }
            }
        }

        public static void KeepMainMenu(Il2CppCore.UILogic.GameUICore.MainMenu __instance)
        {
            if (__instance != null) _mainMenu = __instance;
        }

        private static VisualElement Toggle(Il2CppSystem.Action click, string text, bool on)
        {
            var button = new VisualElement();
            button.style.height = new StyleLength(22f);
            button.style.marginTop = button.style.marginBottom = new StyleLength(2f);
            button.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            button.style.backgroundColor = new StyleColor(on ? new UnityEngine.Color(0.45f, 0.62f, 0.12f, 1f) : new UnityEngine.Color(0.28f, 0.28f, 0.40f, 1f));
            Round(button, 3f);
            var label = Label(text, 9f, true);
            label.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            label.style.marginBottom = new StyleLength(0f);
            label.style.color = new StyleColor(on ? UnityEngine.Color.white : new UnityEngine.Color(0.75f, 0.75f, 0.85f, 1f));
            button.Add(label);
            button.AddManipulator(new Clickable(click).Cast<IManipulator>());
            return button;
        }

        private static VisualElement Tool(Il2CppSystem.Action click, string text)
        {
            var tool = new VisualElement();
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
            light.style.backgroundColor = new StyleColor(on ? new UnityEngine.Color(0.80f, 0.95f, 0.10f, 1f) : new UnityEngine.Color(0.35f, 0.35f, 0.42f, 1f));
            Round(light, 4f);
            return light;
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
            if (enabled) button.AddManipulator(new Clickable(click).Cast<IManipulator>());
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
        public static bool PointerOver => Over(_window) || Over(_panel);

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
