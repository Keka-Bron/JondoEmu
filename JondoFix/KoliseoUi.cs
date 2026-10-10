using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppCore.UILogic.Bindings;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using Il2CppCore.UILogic.Party;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The Koliseo window with Jondo's fourth card, the JondoBots: room for the four cards, the
    /// card's own name and description, and its rules in a window of the client's own.
    /// </summary>
    /// <remarks>
    /// Measured on the client's own UXML (uielements_assets_all.bundle): the window
    /// (PvpArenaBase, a WindowFigma) is 1,328 wide; the fights tab pads 24 on each side; the
    /// cards sit in ctr_choices, a row with space-between, each with flex 1, and a card
    /// (PvpModeItem) is laid out for 416. Three cards take 1,248 of the 1,280 inside; four took
    /// the same room between them and came out squeezed. Four at their width want 4 × 416, the
    /// gaps and the padding: a window of 1,760, still inside the 1,920 the interface is laid out
    /// against.
    /// </remarks>
    public static class KoliseoUi
    {
        /// <summary>The window's width as the client lays it out, and the one four cards want.</summary>
        public const float DesignWidth = 1328f;
        public const float FourCardsWidth = 1760f;

        /// <summary>The language the client plays in: the launcher's --langCode, else what the texts gave away.</summary>
        public static string Language
        {
            get
            {
                if (_language != null) return _language;
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (string.Equals(args[i], "--langCode", StringComparison.OrdinalIgnoreCase))
                    {
                        _language = args[i + 1].Trim().ToLowerInvariant();
                        return _language;
                    }
                }
                return JondoLanguage.Current ?? JondoLanguage.Fallback;
            }
        }

        private static string _language;

        private static string Text(Dictionary<string, string> byLanguage)
            => byLanguage.TryGetValue(Language, out string text) ? text
             : byLanguage.TryGetValue("en", out string english) ? english : byLanguage.Values.First();

        // ─── The card ────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string> Title = new Dictionary<string, string>
        {
            ["es"] = "JondoBots Mortales",
            ["en"] = "JondoBots of Doom",
            ["fr"] = "JondoBots de l'Apocalypse",
            ["pt"] = "JondoBots Mortais",
            ["de"] = "Tödliche JondoBots",
        };

        private static readonly Dictionary<string, string> Subtitle = new Dictionary<string, string>
        {
            ["es"] = "1 contra 1 contra un bot de nivel 200",
            ["en"] = "1 vs 1 against a level-200 bot",
            ["fr"] = "1 contre 1 face à un bot de niveau 200",
            ["pt"] = "1 contra 1 contra um bot de nível 200",
            ["de"] = "1 gegen 1 gegen einen Bot der Stufe 200",
        };

        /// <summary>The event card's texts, over what the client wrote there.</summary>
        public static void NameTheCard(VisualElement card)
        {
            if (card == null) return;
            SetText(card, "lbl_event_title", Text(Title));
            SetText(card, "lbl_event", Text(Subtitle));
            SetText(card, "lbl_mode", "1 VS 1");
        }

        private static void SetText(VisualElement root, string name, string text)
        {
            var element = UQueryExtensions.Q(root, name, (string)null);
            var label = element?.TryCast<TextElement>();
            if (label != null) label.text = text;
        }

        // ─── The window's width ──────────────────────────────────────────────────────────

        /// <summary>
        /// Four cards shown: the window as wide as they want, kept where its centre was. Fewer: the
        /// client's own width again.
        /// </summary>
        public static void MakeRoom(PvpArenaFightsBinding binding)
        {
            if (binding == null) return;
            var cards = new[] { binding.btn_1v1Mode, binding.btn_2v2Mode, binding.btn_3v3Mode, binding.btn_eventMode };
            int shown = cards.Count(Shown);
            var window = WindowOf(binding.ctr_pvpArenaFights);
            if (!_described)
            {
                _described = true;
                MelonLogger.Msg($"[JondoFix] Koliseo: {shown} card(s) shown; above the cards: {Chain(binding.ctr_pvpArenaFights)}.");
            }
            if (window == null)
            {
                MelonLogger.Warning("[JondoFix] Koliseo: no window above the cards to widen.");
                return;
            }

            float wanted = shown >= 4 ? FourCardsWidth : DesignWidth;
            // What was set here last, if anything: LoadMode can come again before the window is
            // laid out anew, and the laid-out width would then still be the old one.
            var inlineWidth = window.style.width;
            float current = inlineWidth.keyword == StyleKeyword.Undefined ? inlineWidth.value.value : window.resolvedStyle.width;
            if (float.IsNaN(current) || current <= 0) current = DesignWidth;
            if (Math.Abs(current - wanted) < 1f) return;

            var inlineLeft = window.style.left;
            float left = inlineLeft.keyword == StyleKeyword.Undefined ? inlineLeft.value.value : window.resolvedStyle.left;
            window.style.width = new StyleLength(wanted);
            if (!float.IsNaN(left)) window.style.left = new StyleLength(left - (wanted - current) / 2f);
            MelonLogger.Msg($"[JondoFix] Koliseo: {shown} cards, the window goes from {current} to {wanted}.");
        }

        private static bool Shown(VisualElement card)
        {
            if (card == null) return false;
            var display = card.style.display;
            return display.keyword != StyleKeyword.Undefined || display.value != DisplayStyle.None;
        }

        private static bool _described;

        /// <summary>
        /// The Koliseo window itself: the first WindowFigma above the cards -- the UXML names it
        /// PvpArenaBase, but that name is not what it carries once instantiated.
        /// </summary>
        private static VisualElement WindowOf(VisualElement inside)
        {
            for (var element = inside; element != null; element = element.parent)
            {
                if (element.name == "PvpArenaBase" || element.TryCast<WindowFigma>() != null) return element;
            }
            return null;
        }

        /// <summary>Every element above this one, with its type, name and width, for the log.</summary>
        private static string Chain(VisualElement from)
        {
            var parts = new List<string>();
            for (var element = from; element != null && parts.Count < 12; element = element.parent)
                parts.Add($"{element.GetType().Name}#{element.name} {element.resolvedStyle.width:0}");
            return string.Join(" < ", parts);
        }

        // ─── The rules ───────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string> RulesTitle = new Dictionary<string, string>
        {
            ["es"] = "Reglas de los JondoBots Mortales",
            ["en"] = "JondoBots of Doom: the rules",
            ["fr"] = "JondoBots de l'Apocalypse : les règles",
            ["pt"] = "JondoBots Mortais: as regras",
            ["de"] = "Tödliche JondoBots: die Regeln",
        };

        private static readonly Dictionary<string, string[]> Rules = new Dictionary<string, string[]>
        {
            ["es"] = new[]
            {
                "Un combate de 1 contra 1 contra un JondoBot, un bot de nivel 200 que controla el propio servidor. Se sortea su clase entre todas, sin repetir ninguna de las últimas ocho que te tocaron.",
                "Lleva un equipo de nivel 200 optimizado: 12 PA, 6 PM, 1500 en cada elemento, 6666 puntos de vida, +6 de alcance, 30 % de golpes críticos, 20 % de resistencia en todo y +3 invocaciones. De cada pareja de hechizos usa una de las dos variantes, al azar.",
                "Cómo entrar: elige esta tarjeta y pulsa el botón de buscar combate. La partida sale al instante y tienes un minuto para aceptarla; si la dejas caducar recibes la sanción del koliseo. Mientras esperas, el botón de abandonar la cola la cancela sin castigo.",
                "Cómo juega: lee cada hechizo como lo aplica el combate —a quién toca, en qué casillas y qué le hace— y elige la mejor jugada: remata al que tiene poca vida, pega donde menos resistes, lanza sus mejoras antes de golpear, deja sus invocaciones hacia ti y, si juega a distancia, acaba el turno fuera de tu línea de visión y sin quedarse pegado a ti.",
                "Recompensas: una victoria paga como un combate de koliseo (experiencia, kamas, kolichas y vitorichas). No cuenta para el ladder: tu puntuación y tu liga no se mueven.",
            },
            ["en"] = new[]
            {
                "A 1 vs 1 fight against a JondoBot, a level-200 bot played by the server itself. Its class is drawn among all of them, never one of the last eight you faced.",
                "It wears an optimized level-200 set: 12 AP, 6 MP, 1500 in every element, 6666 life points, +6 range, 30 % critical hits, 20 % resistance everywhere and +3 summons. Of each pair of spells it uses one of the two variants, at random.",
                "How to join: select this card and press the button to find a fight. The match comes at once and you have a minute to accept it; letting it run out earns the Koliseo's sanction. While you wait, the button to leave the queue cancels it with no penalty.",
                "How it plays: it reads every spell the way the fight applies it — whom it reaches, on which cells and what it does to them — and picks the best move: it finishes whoever is low, strikes where you resist least, casts its boosts before hitting, puts its summons down toward you and, when it fights at range, ends its turn out of your sight and not stuck next to you.",
                "Rewards: a win pays like a Koliseo fight (experience, kamas, kolichas and vitorichas). It does not count for the ladder: your rating and your league stay as they are.",
            },
        };

        private static readonly Dictionary<string, string> Close = new Dictionary<string, string>
        {
            ["es"] = "Cerrar", ["en"] = "Close", ["fr"] = "Fermer", ["pt"] = "Fechar", ["de"] = "Schließen",
        };

        private static WindowFigma _open;

        /// <summary>
        /// The JondoBots' rules, in a window of the client's own -- WindowFigma, its frame, its
        /// title bar and its close button, the same the Koliseo window is -- over the Koliseo.
        /// </summary>
        public static void ShowRules(VisualElement from)
        {
            CloseRules();
            var layer = WindowOf(from)?.parent ?? from?.panel?.visualTree;
            if (layer == null) return;

            var window = new WindowFigma(true, Text(RulesTitle), "", true);
            window.showCloseButton = true;
            window.name = "JondoBotsRules";
            window.style.position = new StyleEnum<Position>(Position.Absolute);
            const float width = 820f, height = 640f;
            window.style.width = new StyleLength(width);
            window.style.height = new StyleLength(height);
            float layerWidth = layer.resolvedStyle.width, layerHeight = layer.resolvedStyle.height;
            window.style.left = new StyleLength(float.IsNaN(layerWidth) ? 550f : (layerWidth - width) / 2f);
            window.style.top = new StyleLength(float.IsNaN(layerHeight) ? 220f : (layerHeight - height) / 2f);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = new StyleFloat(1f);
            scroll.style.paddingLeft = new StyleLength(28f);
            scroll.style.paddingRight = new StyleLength(28f);
            scroll.style.paddingTop = new StyleLength(20f);
            string[] paragraphs = Rules.TryGetValue(Language, out var own) ? own : Rules["en"];
            // ONE label, the paragraphs a blank line apart. One label each, every paragraph was
            // laid out a line or two shorter than it was drawn -- with the one-line class
            // textShort_largeRegular and with the client's own paragraph class, textLong_largeRegular,
            // alike -- and the next one went down over its last lines. The lines of one text are
            // laid out together and cannot cross.
            var label = new DofusLabel();
            label.text = string.Join("\n\n", paragraphs);
            label.AddToClassList("textLong_largeRegular");
            label.AddToClassList("textColor_white_white100");
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            label.style.flexShrink = new StyleFloat(0f);
            scroll.Add(label);
            window.Add(scroll);

            var footer = new VisualElement();
            footer.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            footer.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
            footer.style.paddingBottom = new StyleLength(20f);
            footer.style.paddingTop = new StyleLength(8f);
            var closeButton = new DofusButtonCustom(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(CloseRules)),
                                                    Text(Close), AdminItemsUi.NoIcon());
            footer.Add(closeButton);
            window.Add(footer);

            layer.Add(window);
            window.BringToFront();
            var headerClose = window.header?.rightButton;
            if (headerClose != null) headerClose.add_Clicked(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(CloseRules)));
            _open = window;
        }

        public static void CloseRules()
        {
            try { _open?.RemoveFromHierarchy(); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Closing the JondoBots' rules: {ex.Message}"); }
            _open = null;
        }
    }

    /// <summary>The modes have come: room for the four cards, and the card's texts again.</summary>
    [HarmonyPatch(typeof(PvpArenaFightsUi), nameof(PvpArenaFightsUi.LoadMode))]
    public static class KoliseoLoadModePatch
    {
        public static void Postfix(PvpArenaFightsUi __instance)
        {
            try { KoliseoUi.MakeRoom(__instance.binding); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Koliseo width: {ex.Message}"); }
            KoliseoTextsAgain.Apply(__instance);
        }
    }

    /// <summary>
    /// The card's texts put back after whatever else of the client writes on the cards -- the
    /// titles, a mode's update, the ranks that come after the modes -- so that ours stay.
    /// </summary>
    [HarmonyPatch]
    public static class KoliseoTextsAgain
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PvpArenaFightsUi), nameof(PvpArenaFightsUi.UpdateTitle));
            yield return AccessTools.Method(typeof(PvpArenaFightsUi), nameof(PvpArenaFightsUi.UpdateUIMode));
            yield return AccessTools.Method(typeof(PvpArenaFightsUi), nameof(PvpArenaFightsUi.OnArenaUpdateRank));
        }

        public static void Postfix(PvpArenaFightsUi __instance) => Apply(__instance);

        public static void Apply(PvpArenaFightsUi ui)
        {
            try { KoliseoUi.NameTheCard(ui?.binding?.btn_eventMode); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Koliseo card: {ex.Message}"); }
            // And the room, from here too: whichever of these the client runs, the cards get it.
            try { KoliseoUi.MakeRoom(ui?.binding); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Koliseo width: {ex.Message}"); }
        }
    }

    /// <summary>The event card drawn: its texts are the JondoBots'.</summary>
    [HarmonyPatch(typeof(PvpArenaFightsUi), nameof(PvpArenaFightsUi.UpdateEventMode))]
    public static class KoliseoEventCardPatch
    {
        public static void Postfix(PvpArenaFightsUi __instance, VisualElement buttonMode)
        {
            try { KoliseoUi.NameTheCard(buttonMode); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Koliseo card: {ex.Message}"); }
            try { KoliseoUi.MakeRoom(__instance?.binding); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Koliseo width: {ex.Message}"); }
        }
    }

    /// <summary>
    /// The event card's "view the rules": the client opens guide article 472, which this client
    /// does not have and falls back on the Abono's. The JondoBots' rules instead.
    /// </summary>
    [HarmonyPatch(typeof(PvpArenaFightsUi), nameof(PvpArenaFightsUi._UpdateEventMode_b__47_0))]
    public static class KoliseoRulesPatch
    {
        public static bool Prefix(PvpArenaFightsUi __instance)
        {
            try
            {
                KoliseoUi.ShowRules(__instance.binding?.ctr_pvpArenaFights);
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[JondoFix] JondoBots' rules: {ex.Message}");
                return true;
            }
        }
    }
}
