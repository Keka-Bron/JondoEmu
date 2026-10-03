using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppCore.DataCenter;
using Il2CppCore.DataCenter.Metadata.Effect.Instance;
using Il2CppCore.DataCenter.Metadata.Item;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The administrator's item window: look through the client's own catalogue, pick who gets
    /// it, and give it with the top of every range or with a roll. F10 opens and closes it.
    /// </summary>
    /// <remarks>
    /// A window of the client's own -- WindowFigma, its buttons and its labels, as the JondoBots'
    /// rules are (<see cref="KoliseoUi.ShowRules"/>) -- over the catalogue the client already
    /// carries, so nothing about the items is asked of the server.
    ///
    /// Giving is the server's: this posts to its control API (/api/personaje) with the token the
    /// launcher handed this client, and the server checks token and role on every request. What
    /// is shown here decides nothing; <see cref="JondoFixMod.IsJondoAdministrator"/> only keeps
    /// the key from opening a window that would be refused anyway.
    ///
    /// The fields are read every frame instead of listened to: one less delegate to carry across
    /// to Il2Cpp, and the list is only rebuilt once the text has stopped changing.
    /// </remarks>
    public static class AdminItemsUi
    {
        private const float Width = 1240f, Height = 860f, ListWidth = 620f;

        /// <summary>How many rows a page of the list shows.</summary>
        private const int PageSize = 40;

        /// <summary>How long the search text has to stay still before the list is rebuilt, in seconds.</summary>
        private const float SearchPause = 0.25f;

        private sealed class Entry
        {
            public int Id;
            public string Name = "";
            public string Search = "";
            public int Level;
            public string Type = "";
            public int TypeId;
            public int Category;
            public int IconId;
        }

        private static readonly ConcurrentQueue<Action> _toUnity = new ConcurrentQueue<Action>();
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private static List<Entry> _catalogue;
        private static WindowFigma _window;
        private static TextInput _search, _quantity;
        private static VisualElement _rows, _detail, _targets, _categories, _types;
        private static DofusLabel _status, _targetLabel, _pageLabel;

        /// <summary>What the list is narrowed to: a category and a type of item, -1 for all of them.</summary>
        private static int _category = -1, _typeId = -1;

        /// <summary>Everything the search and the filters leave, and which page of it is shown.</summary>
        private static List<Entry> _found = new List<Entry>();
        private static int _page;
        private static Entry _selected;

        /// <summary>The connected character the item goes to; empty for one's own.</summary>
        private static string _target = "";

        private static string _shownSearch;
        private static string _pendingSearch;
        private static float _pendingSince;
        private static bool _layersLogged;
        private static float _openedAt;
        private static bool _reported;

        // ─── Texts ───────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, Dictionary<string, string>> Texts =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["title"] = L("Jondo · Dar objeto", "Jondo · Give item", "Jondo · Donner un objet"),
                ["search"] = L("Nombre, tipo, id o niveles 190-200", "Name, type, id or levels 190-200",
                               "Nom, type, id ou niveaux 190-200"),
                ["quantity"] = L("Cantidad", "Quantity", "Quantité"),
                ["max"] = L("Dar · características MÁXIMAS", "Give · MAX stats", "Donner · stats MAX"),
                ["random"] = L("Dar · características ALEATORIAS", "Give · RANDOM stats", "Donner · stats ALÉATOIRES"),
                ["to"] = L("Para: {0}", "To: {0}", "Pour : {0}"),
                ["me"] = L("mi personaje", "my character", "mon personnage"),
                ["refresh"] = L("Actualizar conectados", "Refresh connected", "Actualiser les connectés"),
                ["pick"] = L("Elige un objeto de la lista.", "Pick an item from the list.", "Choisissez un objet dans la liste."),
                ["level"] = L("Nivel {0} · {1}", "Level {0} · {1}", "Niveau {0} · {1}"),
                ["page"] = L("Página {0} de {1} · {2} objetos", "Page {0} of {1} · {2} items", "Page {0} sur {1} · {2} objets"),
                ["previous"] = L("< Anterior", "< Previous", "< Précédent"),
                ["next"] = L("Siguiente >", "Next >", "Suivant >"),
                ["all"] = L("Todo", "All", "Tout"),
                ["filter.category"] = L("1 · Categoría", "1 · Category", "1 · Catégorie"),
                ["filter.type"] = L("2 · Tipo de {0} ({1})", "2 · Type of {0} ({1})", "2 · Type de {0} ({1})"),
                ["category.0"] = L("Equipamiento", "Equipment", "Équipement"),
                ["category.1"] = L("Consumibles", "Consumables", "Consommables"),
                ["category.2"] = L("Recursos", "Resources", "Ressources"),
                ["category.3"] = L("Misión", "Quest", "Quête"),
                ["category.5"] = L("Apariencia", "Cosmetics", "Cosmétiques"),
                ["category.4"] = L("Otros", "Other", "Autres"),
                ["loading"] = L("El catálogo todavía no está cargado.", "The catalogue is not loaded yet.",
                                "Le catalogue n'est pas encore chargé."),
                ["sending"] = L("Enviando…", "Sending…", "Envoi…"),
                ["given"] = L("{0} x{1} entregado a {2} ({3}).", "{0} x{1} given to {2} ({3}).",
                              "{0} x{1} donné à {2} ({3})."),
                ["mode.max"] = L("máximas", "max", "max"),
                ["mode.random"] = L("aleatorias", "random", "aléatoires"),
                ["no_token"] = L("Sin token: arranca el cliente desde el lanzador con una cuenta de administrador.",
                                 "No token: start the client from the launcher with an administrator account.",
                                 "Pas de jeton : lancez le client depuis le lanceur avec un compte administrateur."),
                ["error.personaje-desconectado"] = L("Ese personaje no está conectado.", "That character is not connected.",
                                                     "Ce personnage n'est pas connecté."),
                ["error.personaje-en-combate"] = L("Ese personaje está en combate.", "That character is in a fight.",
                                                   "Ce personnage est en combat."),
                ["error.personaje-ocupado"] = L("Ese personaje está ocupado; prueba otra vez.", "That character is busy; try again.",
                                                "Ce personnage est occupé ; réessayez."),
                ["error.rol"] = L("Esta cuenta no es de administrador.", "This account is not an administrator.",
                                  "Ce compte n'est pas administrateur."),
                ["error.sesion"] = L("La sesión ha caducado: vuelve a entrar desde el lanzador.",
                                     "The session has expired: sign in again from the launcher.",
                                     "La session a expiré : reconnectez-vous depuis le lanceur."),
                ["error.cantidad-excesiva"] = L("Demasiados objetos con tirada de una vez (máximo 100).",
                                                "Too many rolled items at once (100 at most).",
                                                "Trop d'objets à jets en une fois (100 au plus)."),
                ["error.objeto-desconocido"] = L("El servidor no conoce ese objeto.", "The server does not know that item.",
                                                 "Le serveur ne connaît pas cet objet."),
                ["error.other"] = L("El servidor ha dicho que no: {0}", "The server refused: {0}", "Le serveur a refusé : {0}"),
                ["error.unreachable"] = L("El servidor no contesta: {0}", "The server does not answer: {0}",
                                          "Le serveur ne répond pas : {0}"),
            };

        private static Dictionary<string, string> L(string es, string en, string fr)
            => new Dictionary<string, string> { ["es"] = es, ["en"] = en, ["fr"] = fr };

        private static string T(string key, params object[] values)
        {
            if (!Texts.TryGetValue(key, out var byLanguage)) return key;
            string text = byLanguage.TryGetValue(KoliseoUi.Language ?? "en", out string own) ? own : byLanguage["en"];
            return values.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, values);
        }

        // ─── Every frame ─────────────────────────────────────────────────────────────────

        /// <summary>From the mod's OnUpdate: the key, what the server answered, and the search.</summary>
        public static void Tick()
        {
            while (_toUnity.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Item window: {ex.Message}"); }
            }

            if (F10Pressed())
            {
                if (_window != null) Close();
                else Open();
            }

            if (_window == null || _search == null) return;

            // Once it has been laid out: where it ended up, for the log.
            if (!_reported && UnityEngine.Time.unscaledTime - _openedAt > 0.5f)
            {
                _reported = true;
                var bound = _window.worldBound;
                MelonLogger.Msg($"[JondoFix] Item window: laid out at {bound.x:0},{bound.y:0} {bound.width:0}x{bound.height:0}, " +
                                $"{(OnScreen(_window) ? "on screen" : "NOT on screen")}: {Chain(_window)}.");
            }

            string typed = _search.text ?? "";
            if (typed != _pendingSearch)
            {
                _pendingSearch = typed;
                _pendingSince = UnityEngine.Time.unscaledTime;
            }
            if (typed != _shownSearch && UnityEngine.Time.unscaledTime - _pendingSince >= SearchPause)
                ShowRows(typed);
        }

        private static bool F10Pressed()
        {
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                return keyboard != null && keyboard.f10Key.wasPressedThisFrame;
            }
            catch
            {
                return false;
            }
        }

        // ─── The window ──────────────────────────────────────────────────────────────────

        public static void Open()
        {
            try
            {
                if (!EnsureCatalogue())
                {
                    MelonLogger.Msg("[JondoFix] Item window: the item catalogue is not loaded yet.");
                    return;
                }

                InstallInputGuard();

                var layer = FindLayer();
                if (layer == null)
                {
                    MelonLogger.Warning("[JondoFix] Item window: no interface layer to open on.");
                    return;
                }

                var window = new WindowFigma(true, T("title"), "", true);
                window.showCloseButton = true;
                window.name = "JondoAdminItems";
                window.style.position = new StyleEnum<Position>(Position.Absolute);
                window.style.width = new StyleLength(Width);
                window.style.height = new StyleLength(Height);
                float layerWidth = layer.resolvedStyle.width, layerHeight = layer.resolvedStyle.height;
                window.style.left = new StyleLength(float.IsNaN(layerWidth) || layerWidth <= 0 ? 370f : (layerWidth - Width) / 2f);
                window.style.top = new StyleLength(float.IsNaN(layerHeight) || layerHeight <= 0 ? 180f : (layerHeight - Height) / 2f);

                var body = Row();
                body.style.flexGrow = new StyleFloat(1f);
                body.style.paddingLeft = new StyleLength(20f);
                body.style.paddingRight = new StyleLength(20f);
                body.style.paddingTop = new StyleLength(14f);
                body.style.paddingBottom = new StyleLength(16f);
                body.Add(BuildList());
                body.Add(BuildSide());
                window.Add(body);

                layer.Add(window);
                window.BringToFront();
                KeepEvents(window);
                var headerClose = window.header?.rightButton;
                if (headerClose != null) headerClose.add_Clicked(Do(Close));
                _window = window;
                _openedAt = UnityEngine.Time.unscaledTime;
                _reported = false;

                _shownSearch = null;
                _pendingSearch = "";
                ShowCategories();
                ShowTypes();
                ShowRows("");
                ShowDetail();
                ShowTarget();
                RefreshTargets();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Item window could not be opened: {ex}");
                Close();
            }
        }

        public static void Close()
        {
            try { _window?.RemoveFromHierarchy(); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Closing the item window: {ex.Message}"); }
            _window = null;
            _search = null;
            _quantity = null;
            _rows = null;
            _categories = null;
            _types = null;
            _typesBox = null;
            _typesHeading = null;
            _pageLabel = null;
            _detail = null;
            _targets = null;
            _status = null;
            _targetLabel = null;
        }

        private static VisualElement BuildList()
        {
            var column = new VisualElement();
            column.style.width = new StyleLength(ListWidth);
            column.style.marginRight = new StyleLength(20f);
            column.style.flexShrink = new StyleFloat(0f);

            _search = new TextInput();
            _search.placeholderText = T("search");
            _search.style.marginBottom = new StyleLength(8f);
            column.Add(_search);

            _categories = Row();
            _categories.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            _categories.style.flexShrink = new StyleFloat(0f);
            column.Add(_categories);

            // The types of the category picked, in a box of their own, set in and tinted so that
            // they read as belonging under the category. Equipment alone has some thirty: they scroll.
            _typesBox = new VisualElement();
            _typesBox.style.flexShrink = new StyleFloat(0f);
            _typesBox.style.marginLeft = new StyleLength(18f);
            _typesBox.style.marginTop = new StyleLength(2f);
            _typesBox.style.marginBottom = new StyleLength(8f);
            _typesBox.style.paddingLeft = new StyleLength(10f);
            _typesBox.style.paddingTop = new StyleLength(6f);
            _typesBox.style.paddingBottom = new StyleLength(4f);
            _typesBox.style.backgroundColor = new StyleColor(new UnityEngine.Color(0f, 0f, 0f, 0.28f));
            _typesBox.style.borderLeftWidth = new StyleFloat(3f);
            _typesBox.style.borderLeftColor = new StyleColor(RowPicked);
            _typesHeading = Label("", true);
            _typesHeading.style.marginBottom = new StyleLength(4f);
            _typesBox.Add(_typesHeading);
            var types = new ScrollView(ScrollViewMode.Vertical);
            types.style.maxHeight = new StyleLength(104f);
            _types = Row();
            _types.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            types.Add(_types);
            _typesBox.Add(types);
            column.Add(_typesBox);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = new StyleFloat(1f);
            _rows = scroll;
            column.Add(scroll);

            var pager = Row();
            pager.style.alignItems = new StyleEnum<Align>(Align.Center);
            pager.style.justifyContent = new StyleEnum<Justify>(Justify.SpaceBetween);
            pager.style.flexShrink = new StyleFloat(0f);
            pager.style.marginTop = new StyleLength(8f);
            pager.Add(Button(Do(() => { _page--; ShowPage(); }), T("previous"), null));
            _pageLabel = Label("");
            pager.Add(_pageLabel);
            pager.Add(Button(Do(() => { _page++; ShowPage(); }), T("next"), null));
            column.Add(pager);
            return column;
        }

        private static VisualElement BuildSide()
        {
            var column = new VisualElement();
            column.style.flexGrow = new StyleFloat(1f);

            var detail = new ScrollView(ScrollViewMode.Vertical);
            detail.style.flexGrow = new StyleFloat(1f);
            detail.style.marginBottom = new StyleLength(10f);
            _detail = detail;
            column.Add(detail);

            _targetLabel = Label("", true);
            column.Add(_targetLabel);

            _targets = Row();
            _targets.style.flexWrap = new StyleEnum<Wrap>(Wrap.Wrap);
            _targets.style.marginBottom = new StyleLength(10f);
            column.Add(_targets);

            var quantityRow = Row();
            quantityRow.style.alignItems = new StyleEnum<Align>(Align.Center);
            quantityRow.style.marginBottom = new StyleLength(10f);
            var quantityLabel = Label(T("quantity"));
            quantityLabel.style.marginRight = new StyleLength(12f);
            quantityRow.Add(quantityLabel);
            _quantity = new TextInput();
            _quantity.isNumbersOnly = true;
            _quantity.style.width = new StyleLength(140f);
            _quantity.SetValueWithoutNotify("1");
            quantityRow.Add(_quantity);
            column.Add(quantityRow);

            var buttons = Row();
            var max = Button(Do(() => Give(false)), T("max"), null, true);
            max.style.marginRight = new StyleLength(12f);
            buttons.Add(max);
            buttons.Add(Button(Do(() => Give(true)), T("random"), null, true));
            column.Add(buttons);

            _status = Label("");
            _status.style.marginTop = new StyleLength(12f);
            column.Add(_status);
            return column;
        }

        /// <summary>
        /// A button of this window's own: light text on a tint, or on gold for the ones that act.
        /// </summary>
        /// <remarks>
        /// They were the client's (DofusButtonCustom) and are not any more. Its styles are made for
        /// its own pages: the text came out dark on this dark window, it put its colour back over
        /// whatever was set, and its width was that of its size class and not of its text.
        /// </remarks>
        private static VisualElement Button(Il2CppSystem.Action click, string text, object noImage, bool strong = false)
        {
            var button = new VisualElement();
            button.style.flexShrink = new StyleFloat(0f);
            button.style.paddingLeft = new StyleLength(16f);
            button.style.paddingRight = new StyleLength(16f);
            button.style.paddingTop = new StyleLength(8f);
            button.style.paddingBottom = new StyleLength(8f);
            button.style.marginRight = new StyleLength(8f);
            button.style.marginBottom = new StyleLength(6f);
            button.style.backgroundColor = new StyleColor(strong ? ChipPicked : ChipLarge);
            button.style.borderTopLeftRadius = new StyleLength(6f);
            button.style.borderTopRightRadius = new StyleLength(6f);
            button.style.borderBottomLeftRadius = new StyleLength(6f);
            button.style.borderBottomRightRadius = new StyleLength(6f);

            var label = Label(text, true);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            label.style.color = new StyleColor(strong ? UnityEngine.Color.white : LightText);
            button.Add(label);

            button.AddManipulator(new Clickable(click).Cast<IManipulator>());
            return button;
        }

        private static readonly UnityEngine.Color LightText = new UnityEngine.Color(0.93f, 0.90f, 0.82f, 1f);

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
            // The one text class the rules window is known to draw with; bold is set by hand.
            label.AddToClassList("textShort_largeRegular");
            label.AddToClassList("textColor_white_white100");
            if (bold) label.style.unityFontStyleAndWeight = new StyleEnum<UnityEngine.FontStyle>(UnityEngine.FontStyle.Bold);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            return label;
        }

        private static Il2CppSystem.Action Do(Action action)
            => DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Item window: {ex.Message}"); }
            }));

        private static void Say(string text)
        {
            if (_status != null) _status.text = text;
        }

        // ─── Where it opens ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The layer the client's own windows live on: the parent of a WindowFigma that is really
        /// on screen, else the root of the interface document. A window that exists but sits under
        /// something hidden is no guide -- the client keeps closed windows around, and opening
        /// beside one of those is opening where nothing is drawn.
        /// </summary>
        private static VisualElement FindLayer()
        {
            VisualElement beside = null, topRoot = null, high = null;
            float topOrder = float.MinValue;

            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            for (int i = 0; i < documents.Length; i++)
            {
                var document = documents[i]?.TryCast<UIDocument>();
                var root = document?.rootVisualElement;
                if (root == null) continue;

                if (!_layersLogged)
                {
                    MelonLogger.Msg($"[JondoFix] Item window: document {document.name}, order {document.sortingOrder:0.#}:");
                    Describe(root, 0);
                }

                var windows = new List<VisualElement>();
                Windows(root, 0, windows);
                foreach (var window in windows)
                {
                    var parent = window.parent;
                    if (beside == null && parent != null && OnScreen(parent) && parent.resolvedStyle.width >= Width)
                        beside = parent;
                }

                // The layer the client opens its own windows on. The root of the document is above
                // every layer and shows the window just as well, but the game does not count what is
                // out there as interface: a click on it is a click on the map too.
                if (high == null)
                {
                    var named = Named(root, "RootHigh", 0);
                    if (named != null && OnScreen(named)) high = named;
                }

                float width = root.resolvedStyle.width;
                if (!float.IsNaN(width) && width > 0 && document.sortingOrder >= topOrder)
                {
                    topOrder = document.sortingOrder;
                    topRoot = root;
                }
            }

            _layersLogged = true;
            var chosen = beside ?? high ?? topRoot;
            MelonLogger.Msg($"[JondoFix] Item window: opening {(beside != null ? "beside a client window" : high != null ? "on the client's window layer" : "on the document's root")}: {Chain(chosen)}.");
            return chosen;
        }

        private static VisualElement Named(VisualElement from, string name, int depth)
        {
            if (from == null || depth > 3) return null;
            int count = from.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = from.ElementAt(i);
                if (child == null) continue;
                if (child.name == name) return child;
                var deeper = Named(child, name, depth + 1);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>
        /// A press, a release, a click or the wheel on the window ends at the window: whatever
        /// listens further up for a click on the map does not hear it.
        /// </summary>
        private static void KeepEvents(VisualElement window)
        {
            int kept = 0;
            try { window.RegisterCallback<PointerDownEvent>(Stop<PointerDownEvent>(), TrickleDown.NoTrickleDown); kept++; } catch { }
            try { window.RegisterCallback<PointerUpEvent>(Stop<PointerUpEvent>(), TrickleDown.NoTrickleDown); kept++; } catch { }
            try { window.RegisterCallback<MouseDownEvent>(Stop<MouseDownEvent>(), TrickleDown.NoTrickleDown); kept++; } catch { }
            try { window.RegisterCallback<MouseUpEvent>(Stop<MouseUpEvent>(), TrickleDown.NoTrickleDown); kept++; } catch { }
            try { window.RegisterCallback<ClickEvent>(Stop<ClickEvent>(), TrickleDown.NoTrickleDown); kept++; } catch { }
            try { window.RegisterCallback<WheelEvent>(Stop<WheelEvent>(), TrickleDown.NoTrickleDown); kept++; } catch { }
            if (!_keptLogged)
            {
                _keptLogged = true;
                MelonLogger.Msg($"[JondoFix] Item window: {kept} of 6 kinds of pointer event kept on the window.");
            }
        }

        private static bool _keptLogged;

        private static EventCallback<T> Stop<T>() where T : EventBase<T>, new()
            => DelegateSupport.ConvertDelegate<EventCallback<T>>(new Action<T>(e =>
            {
                try { e.StopPropagation(); } catch { }
            }));

        /// <summary>Whether the element and everything above it is drawn.</summary>
        private static bool OnScreen(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
            {
                var style = e.resolvedStyle;
                if (style.display == DisplayStyle.None || style.visibility == Visibility.Hidden || style.opacity <= 0f)
                    return false;
            }
            return element != null && element.panel != null;
        }

        private static void Windows(VisualElement from, int depth, List<VisualElement> found)
        {
            if (from == null || depth > 12) return;
            int count = from.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = from.ElementAt(i);
                if (child == null || child.name == "JondoAdminItems") continue;
                if (child.TryCast<WindowFigma>() != null) found.Add(child);
                else Windows(child, depth + 1, found);
            }
        }

        private static string One(VisualElement e)
        {
            var style = e.resolvedStyle;
            return $"{e.GetIl2CppType().Name}#{e.name} {style.width:0}x{style.height:0}" +
                   (style.display == DisplayStyle.None ? " hidden" : "") +
                   (style.opacity <= 0f ? " transparent" : "");
        }

        private static string Chain(VisualElement from)
        {
            var parts = new List<string>();
            for (var e = from; e != null && parts.Count < 12; e = e.parent) parts.Add(One(e));
            return string.Join(" < ", parts);
        }

        /// <summary>The top of the interface tree, for the log: what there is to open on.</summary>
        private static void Describe(VisualElement from, int depth)
        {
            if (from == null || depth > 3) return;
            MelonLogger.Msg($"[JondoFix]   {new string(' ', depth * 2)}{One(from)} ({from.childCount} children)");
            int count = Math.Min(from.childCount, 40);
            for (int i = 0; i < count; i++) Describe(from.ElementAt(i), depth + 1);
        }

        // ─── The catalogue ───────────────────────────────────────────────────────────────

        private static bool EnsureCatalogue()
        {
            if (_catalogue != null) return true;

            var items = DataCenterModule.itemsDataRoot?.GetObjects();
            if (items == null || items.Count == 0) return false;

            var types = new Dictionary<int, string>();
            var catalogue = new List<Entry>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                ItemData item = items[i];
                if (item == null) continue;
                try
                {
                    string name = item.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    int typeId = item.typeId;
                    if (!types.TryGetValue(typeId, out string type))
                    {
                        try { type = item.GetItemType()?.name ?? ""; }
                        catch { type = ""; }
                        types[typeId] = type;
                    }

                    catalogue.Add(new Entry
                    {
                        Id = item.id,
                        Name = name,
                        Level = item.level,
                        Type = type,
                        TypeId = typeId,
                        Category = CategoryOf(item),
                        IconId = item.iconId,
                        Search = Fold(name + " " + type + " " + item.id.ToString(CultureInfo.InvariantCulture)),
                    });
                }
                catch { }
            }

            catalogue.Sort((a, b) => a.Level != b.Level
                ? a.Level.CompareTo(b.Level)
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            _catalogue = catalogue;
            MelonLogger.Msg($"[JondoFix] Item window: {catalogue.Count} items in the catalogue.");
            return true;
        }

        /// <summary>Lower case and without accents: "Gélano" is found by "gelano".</summary>
        private static string Fold(string text)
        {
            var folded = new StringBuilder(text.Length);
            foreach (char c in text.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    folded.Append(char.ToLowerInvariant(c));
            }
            return folded.ToString();
        }

        /// <summary>The six categories the filter offers; anything else of the client's counts as "other".</summary>
        private static readonly int[] Categories = { 0, 1, 2, 3, 5, 4 };

        private static int CategoryOf(ItemData item)
        {
            try
            {
                int category = (int)item.category;
                return category >= 0 && category <= 5 ? category : 4;
            }
            catch { return 4; }
        }

        /// <summary>
        /// Every word has to be in the name, the type or the id; a word like 190-200 is a range of
        /// levels instead. On top of that, the category and the type picked.
        /// </summary>
        private static List<Entry> Matching(string typed)
        {
            var words = new List<string>();
            int minLevel = int.MinValue, maxLevel = int.MaxValue;
            foreach (string word in Fold(typed ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int dash = word.IndexOf('-');
                if (dash > 0 && int.TryParse(word.Substring(0, dash), out int from)
                             && int.TryParse(word.Substring(dash + 1), out int to))
                {
                    minLevel = Math.Min(from, to);
                    maxLevel = Math.Max(from, to);
                }
                else words.Add(word);
            }

            var found = new List<Entry>();
            foreach (var entry in _catalogue)
            {
                if (_category >= 0 && entry.Category != _category) continue;
                if (_typeId >= 0 && entry.TypeId != _typeId) continue;
                if (entry.Level < minLevel || entry.Level > maxLevel) continue;
                bool all = true;
                foreach (string word in words)
                {
                    if (entry.Search.IndexOf(word, StringComparison.Ordinal) < 0) { all = false; break; }
                }
                if (all) found.Add(entry);
            }
            return found;
        }

        /// <summary>The list again from the search and the filters, back on its first page.</summary>
        private static void ShowRows(string typed)
        {
            _shownSearch = typed;
            if (_rows == null || _catalogue == null) return;

            _found = Matching(typed);
            _page = 0;
            ShowPage();
        }

        private static void ShowPage()
        {
            if (_rows == null) return;

            int pages = Math.Max(1, (_found.Count + PageSize - 1) / PageSize);
            _page = Math.Clamp(_page, 0, pages - 1);
            if (_pageLabel != null) _pageLabel.text = T("page", _page + 1, pages, _found.Count);

            _rows.Clear();
            int end = Math.Min(_found.Count, (_page + 1) * PageSize);
            for (int i = _page * PageSize; i < end; i++) _rows.Add(ItemRow(_found[i]));
        }

        /// <summary>One line of the list: the whole name, however long, and the click that picks it.</summary>
        private static VisualElement ItemRow(Entry entry)
        {
            var row = new VisualElement();
            row.style.paddingLeft = new StyleLength(10f);
            row.style.paddingRight = new StyleLength(10f);
            row.style.paddingTop = new StyleLength(5f);
            row.style.paddingBottom = new StyleLength(5f);
            row.style.marginBottom = new StyleLength(3f);
            row.style.backgroundColor = new StyleColor(entry == _selected ? RowPicked : RowPlain);
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            row.style.alignItems = new StyleEnum<Align>(Align.Center);
            var icon = IconOf(entry);
            if (icon != null) row.Add(icon);
            var name = Label($"{entry.Name}  ·  {entry.Level}  ·  {entry.Type}");
            name.style.flexShrink = new StyleFloat(1f);
            row.Add(name);
            var pick = Do(() => { _selected = entry; ShowDetail(); ShowPage(); });
            if (_plainRows)
            {
                try
                {
                    row.AddManipulator(new Clickable(pick).Cast<IManipulator>());
                    return row;
                }
                catch (Exception ex)
                {
                    // A line that cannot be clicked is no list: buttons from here on.
                    _plainRows = false;
                    MelonLogger.Warning($"[JondoFix] Item window: rows as buttons, a plain row takes no click: {ex.Message}");
                }
            }

            var button = Button(pick, $"{entry.Name}  ·  {entry.Level}  ·  {entry.Type}", null);
            button.style.marginRight = new StyleLength(0f);
            return button;
        }

        private static bool _plainRows = true;

        // ─── The icons ───────────────────────────────────────────────────────────────────

        private const float IconSize = 44f;
        private static bool _icons = true;
        private static bool _iconTypeRead;
        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType _iconType;

        /// <summary>
        /// The item's picture, in a tile of the client's own: where its icons live is asked of the
        /// client's administration picker, which draws the same list. Null, and no more icons for
        /// the session, the first time the client will not have it.
        /// </summary>
        private static VisualElement IconOf(Entry entry)
        {
            if (!_icons || entry.IconId <= 0) return _icons ? IconPlace(null) : null;
            try
            {
                if (!_iconTypeRead)
                {
                    _iconType = Il2CppCore.UILogic.Admin.AdminSelectItemUI.FindIconAddressableEntryType(
                        Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Item);
                    _iconTypeRead = true;
                    MelonLogger.Msg($"[JondoFix] Item window: item icons are at {_iconType.pathPrefix}*{_iconType.extension}.");
                }

                var tile = new Il2CppCore.UILogic.Components.Slots.Tile();
                tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(_iconType, entry.IconId);
                tile.pickingMode = PickingMode.Ignore;
                return IconPlace(tile);
            }
            catch (Exception ex)
            {
                _icons = false;
                MelonLogger.Warning($"[JondoFix] Item window: no icons, the client's tile would not take one: {ex.Message}");
                return null;
            }
        }

        /// <summary>The square an icon sits in, the same whether or not the item has one, so the names line up.</summary>
        private static VisualElement IconPlace(VisualElement icon)
        {
            var place = new VisualElement();
            place.style.width = new StyleLength(IconSize);
            place.style.height = new StyleLength(IconSize);
            place.style.marginRight = new StyleLength(10f);
            place.style.flexShrink = new StyleFloat(0f);
            place.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
            place.pickingMode = PickingMode.Ignore;
            if (icon != null)
            {
                icon.style.width = new StyleLength(IconSize);
                icon.style.height = new StyleLength(IconSize);
                place.Add(icon);
            }
            return place;
        }

        private static readonly UnityEngine.Color RowPlain = new UnityEngine.Color(1f, 1f, 1f, 0.06f);
        private static readonly UnityEngine.Color RowPicked = new UnityEngine.Color(0.85f, 0.65f, 0.2f, 0.45f);

        // ─── The filters ─────────────────────────────────────────────────────────────────

        private static string Chip(string text, bool on) => on ? "[ " + text + " ]" : text;

        /// <summary>
        /// The first level: what kind of thing. Large buttons under their own heading, the one
        /// picked in the client's main style and the others in its second.
        /// </summary>
        private static void ShowCategories()
        {
            if (_categories == null) return;
            _categories.Clear();

            var heading = Label(T("filter.category"), true);
            heading.style.width = new StyleLength(new Length(100f, LengthUnit.Percent));
            heading.style.marginBottom = new StyleLength(4f);
            _categories.Add(heading);

            _categories.Add(CategoryButton(-1, T("all")));
            foreach (int category in Categories) _categories.Add(CategoryButton(category, T("category." + category)));
        }

        private static VisualElement CategoryButton(int category, string text)
        {
            bool on = _category == category || (category < 0 && _category < 0);
            return FilterButton(text, on, true, () => PickCategory(category));
        }

        /// <summary>
        /// A filter button drawn here rather than borrowed: light text on a tint, gold when it is
        /// the one picked. The client's second and third button styles write dark, for its light
        /// pages, and put their colour back whatever is set on them.
        /// </summary>
        private static VisualElement FilterButton(string text, bool on, bool large, Action click)
        {
            var button = new VisualElement();
            button.style.flexShrink = new StyleFloat(0f);
            button.style.paddingLeft = new StyleLength(large ? 14f : 9f);
            button.style.paddingRight = new StyleLength(large ? 14f : 9f);
            button.style.paddingTop = new StyleLength(large ? 7f : 3f);
            button.style.paddingBottom = new StyleLength(large ? 7f : 3f);
            button.style.marginRight = new StyleLength(6f);
            button.style.marginBottom = new StyleLength(6f);
            button.style.backgroundColor = new StyleColor(on ? ChipPicked : large ? ChipLarge : ChipSmall);
            float radius = large ? 6f : 4f;
            button.style.borderTopLeftRadius = new StyleLength(radius);
            button.style.borderTopRightRadius = new StyleLength(radius);
            button.style.borderBottomLeftRadius = new StyleLength(radius);
            button.style.borderBottomRightRadius = new StyleLength(radius);

            var label = Label(text, large || on);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            label.style.color = new StyleColor(on ? UnityEngine.Color.white : LightText);
            if (!large) label.style.fontSize = new StyleLength(15f);
            button.Add(label);

            button.AddManipulator(new Clickable(Do(click)).Cast<IManipulator>());
            return button;
        }

        private static readonly UnityEngine.Color ChipLarge = new UnityEngine.Color(1f, 1f, 1f, 0.16f);
        private static readonly UnityEngine.Color ChipSmall = new UnityEngine.Color(1f, 1f, 1f, 0.09f);
        private static readonly UnityEngine.Color ChipPicked = new UnityEngine.Color(0.80f, 0.58f, 0.14f, 0.95f);

        private static void PickCategory(int category)
        {
            _category = category;
            _typeId = -1;
            ShowCategories();
            ShowTypes();
            ShowRows(_search?.text ?? "");
        }

        /// <summary>
        /// The second level: which type inside the category picked -- wings, ceremonial weapons...
        /// Small buttons in the client's third style, in a box of their own set in from the
        /// categories and named after the one they belong to. Nothing while every category shows.
        /// </summary>
        private static void ShowTypes()
        {
            if (_types == null) return;
            _types.Clear();

            bool any = _category >= 0 && _catalogue != null;
            if (_typesBox != null) _typesBox.style.display = new StyleEnum<DisplayStyle>(any ? DisplayStyle.Flex : DisplayStyle.None);
            if (!any) return;

            var types = new Dictionary<int, string>();
            foreach (var entry in _catalogue)
            {
                if (entry.Category == _category && entry.Type.Length > 0) types[entry.TypeId] = entry.Type;
            }

            if (_typesHeading != null) _typesHeading.text = T("filter.type", T("category." + _category), types.Count);

            _types.Add(TypeButton(-1, T("all")));
            foreach (var type in types.OrderBy(t => t.Value, StringComparer.CurrentCultureIgnoreCase))
                _types.Add(TypeButton(type.Key, type.Value));
        }

        private static VisualElement TypeButton(int typeId, string text)
        {
            bool on = _typeId == typeId || (typeId < 0 && _typeId < 0);
            return FilterButton(text, on, false, () => PickType(typeId));
        }

        private static VisualElement _typesBox;
        private static DofusLabel _typesHeading;

        private static void PickType(int typeId)
        {
            _typeId = typeId;
            ShowTypes();
            ShowRows(_search?.text ?? "");
        }

        // ─── The mouse is the window's ───────────────────────────────────────────────────

        private static int _overFrame = -1;
        private static bool _over;
        private static bool _guardInstalled;

        /// <summary>Whether the pointer is on the window this frame.</summary>
        public static bool PointerOver
        {
            get
            {
                if (_window == null) return false;
                int frame = UnityEngine.Time.frameCount;
                if (frame == _overFrame) return _over;
                _overFrame = frame;
                _over = false;
                try
                {
                    var mouse = UnityEngine.InputSystem.Mouse.current;
                    var panel = _window.panel;
                    if (mouse == null || panel == null) return false;
                    var at = mouse.position.ReadValue();
                    var onPanel = RuntimePanelUtils.ScreenToPanel(panel, new UnityEngine.Vector2(at.x, UnityEngine.Screen.height - at.y));
                    _over = _window.worldBound.Contains(onPanel);
                }
                catch { }
                return _over;
            }
        }

        /// <summary>
        /// Keeps a click on the window from also being a click on the map. The game reads the
        /// mouse through InputUtility and asks it whether the pointer is over the interface; this
        /// window is not one of the client's, so nobody answers yes for it. While the pointer is on
        /// it, InputUtility says "over the interface" and reports no button.
        /// </summary>
        /// <remarks>
        /// Patched by hand and one by one, not with attributes: an attribute on a method this
        /// client does not have takes every other patch of the mod down with it at load.
        /// </remarks>
        private static void InstallInputGuard()
        {
            if (_guardInstalled) return;
            _guardInstalled = true;

            var harmony = new HarmonyLib.Harmony("jondo.adminitems.input");
            var overUi = new HarmonyLib.HarmonyMethod(typeof(AdminItemsUi).GetMethod(nameof(SayOverUi)));
            var noButton = new HarmonyLib.HarmonyMethod(typeof(AdminItemsUi).GetMethod(nameof(SayNoButton)));
            var patched = new List<string>();
            var type = typeof(Il2CppAnkama.Utilities.InputUtility);

            void Patch(string name, HarmonyLib.HarmonyMethod postfix)
            {
                try
                {
                    var method = HarmonyLib.AccessTools.Method(type, name);
                    if (method == null) return;
                    harmony.Patch(method, postfix: postfix);
                    patched.Add(name);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[JondoFix] Item window: InputUtility.{name} not guarded: {ex.Message}");
                }
            }

            Patch("get_isPointerOverUI", overUi);
            foreach (string name in new[] { "GetPointerDown", "IsPointerDown", "GetPointerUp", "GetSecondaryDown", "IsSecondaryDown",
                                            "GetSecondaryUp", "GetTertiaryDown", "IsTertiaryDown", "GetTertiaryUp" })
                Patch(name, noButton);
            MelonLogger.Msg($"[JondoFix] Item window: mouse guarded on {patched.Count} of 10 InputUtility methods ({string.Join(", ", patched)}).");
        }

        private static bool _overUiAsked, _buttonAsked;

        public static void SayOverUi(ref bool __result)
        {
            if (!PointerOver) return;
            __result = true;
            if (_overUiAsked) return;
            _overUiAsked = true;
            MelonLogger.Msg("[JondoFix] Item window: the game asked whether the pointer is over the interface, and was told yes.");
        }

        public static void SayNoButton(ref bool __result)
        {
            if (!PointerOver) return;
            bool was = __result;
            __result = false;
            if (!was || _buttonAsked) return;
            _buttonAsked = true;
            MelonLogger.Msg("[JondoFix] Item window: a mouse button over the window was kept from the game.");
        }

        // ─── The item picked ─────────────────────────────────────────────────────────────

        private static void ShowDetail()
        {
            if (_detail == null) return;
            _detail.Clear();

            if (_selected == null)
            {
                _detail.Add(Label(T("pick")));
                return;
            }

            var head = Row();
            head.style.alignItems = new StyleEnum<Align>(Align.Center);
            head.style.marginBottom = new StyleLength(4f);
            var picture = IconOf(_selected);
            if (picture != null) head.Add(picture);
            var title = Label(_selected.Name, true);
            title.style.flexShrink = new StyleFloat(1f);
            head.Add(title);
            _detail.Add(head);
            var level = Label(T("level", _selected.Level, _selected.Type));
            level.style.marginBottom = new StyleLength(10f);
            _detail.Add(level);

            foreach (string line in EffectsOf(_selected.Id))
                _detail.Add(Label(line));
        }

        /// <summary>The item's lines as the client words them, each with its range.</summary>
        private static List<string> EffectsOf(int gid)
        {
            var lines = new List<string>();
            try
            {
                var effects = ItemData.GetItemById(gid)?.possibleEffects;
                if (effects == null) return lines;

                for (int i = 0; i < effects.Count; i++)
                {
                    var effect = effects[i];
                    if (effect == null) continue;

                    string text = "";
                    try { text = effect.description ?? ""; } catch { }
                    if (text.Length == 0)
                    {
                        var dice = effect.TryCast<EffectInstanceDice>();
                        text = dice == null
                            ? $"#{(int)effect.effectId}"
                            : dice.diceSide > dice.diceNum
                                ? $"#{(int)effect.effectId}: {dice.diceNum} - {dice.diceSide}"
                                : $"#{(int)effect.effectId}: {dice.diceNum}";
                    }
                    lines.Add(text);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] Item window: the effects of {gid}: {ex.Message}");
            }
            return lines;
        }

        // ─── Who gets it ─────────────────────────────────────────────────────────────────

        private static void ShowTarget()
        {
            if (_targetLabel != null)
                _targetLabel.text = T("to", _target.Length == 0 ? T("me") : _target);
        }

        private static List<(string Name, int Level, bool Own)> _connected = new List<(string Name, int Level, bool Own)>();

        private static void ShowTargets(List<(string Name, int Level, bool Own)> connected)
        {
            if (_targets == null) return;
            _targets.Clear();
            _connected = connected;

            foreach (var character in connected)
            {
                string name = character.Name;
                bool own = character.Own;
                bool picked = own ? _target.Length == 0 : _target == name;
                var pick = Button(Do(() => { _target = own ? "" : name; ShowTarget(); ShowTargets(_connected); }),
                                                 $"{name} ({character.Level})", null, picked);
                pick.style.marginRight = new StyleLength(8f);
                pick.style.marginBottom = new StyleLength(6f);
                _targets.Add(pick);
            }
            _targets.Add(Button(Do(RefreshTargets), T("refresh"), null));

            // Whoever was picked and has since left: back to one's own, rather than a give that
            // is going to be refused.
            if (_target.Length > 0 && !connected.Any(c => c.Name == _target))
            {
                _target = "";
                ShowTarget();
            }
        }

        private static void RefreshTargets()
        {
            string token = Token();
            if (token.Length == 0)
            {
                Say(T("no_token"));
                return;
            }

            Task.Run(async () =>
            {
                var (code, body) = await PostAsync("conectados", new Dictionary<string, object> { ["token"] = token });
                var connected = new List<(string, int, bool)>();
                if (code == 200)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        foreach (var one in doc.RootElement.GetProperty("conectados").EnumerateArray())
                        {
                            connected.Add((one.GetProperty("nombre").GetString() ?? "",
                                           one.GetProperty("nivel").GetInt32(),
                                           one.GetProperty("propio").GetBoolean()));
                        }
                    }
                    catch { }
                }
                _toUnity.Enqueue(() =>
                {
                    ShowTargets(connected);
                    if (code != 200) Say(Refusal(code, body));
                });
            });
        }

        // ─── Giving ──────────────────────────────────────────────────────────────────────

        private static void Give(bool random)
        {
            if (_selected == null)
            {
                Say(T("pick"));
                return;
            }

            string token = Token();
            if (token.Length == 0)
            {
                Say(T("no_token"));
                return;
            }

            if (!int.TryParse((_quantity?.text ?? "1").Trim(), out int quantity) || quantity < 1) quantity = 1;

            var item = _selected;
            string target = _target;
            var request = new Dictionary<string, object>
            {
                ["token"] = token,
                ["personaje"] = target,
                ["objeto"] = item.Id,
                ["cantidad"] = quantity,
                ["modo"] = random ? "aleatorio" : "max",
            };

            Say(T("sending"));
            Task.Run(async () =>
            {
                var (code, body) = await PostAsync("personaje", request);
                string said;
                if (code == 200)
                {
                    string who = target;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        who = doc.RootElement.GetProperty("personaje").GetString() ?? target;
                    }
                    catch { }
                    said = T("given", item.Name, quantity, who, T(random ? "mode.random" : "mode.max"));
                }
                else said = Refusal(code, body);

                _toUnity.Enqueue(() => Say(said));
            });
        }

        private static string Token()
            => (Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ?? "").Trim();

        /// <summary>A request to the server's control API. Code 0 when it could not be reached.</summary>
        private static async Task<(int Code, string Body)> PostAsync(string route, Dictionary<string, object> body)
        {
            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync("http://127.0.0.1:8888/api/" + route, content);
                return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                return (0, ex.Message);
            }
        }

        private static string Refusal(int code, string body)
        {
            if (code == 0) return T("error.unreachable", body);

            string reason = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error)) reason = error.GetString() ?? body;
            }
            catch { }
            return Texts.ContainsKey("error." + reason) ? T("error." + reason) : T("error.other", reason);
        }
    }
}
