using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// What the client shows, read from its own interface for the test harness: the texts on
    /// screen and where they are, the elements under a point, the elements of a kind. The harness
    /// used to read the window's pictures with OCR and to click places measured by hand; the
    /// interface tree says the same thing exactly, whatever the window's size.
    ///
    /// Every rectangle is in the window's pixels, (0, 0) top left: the same coordinates the
    /// harness clicks with. Only TestInput calls this, so it only runs in a client started for
    /// the harness.
    /// </summary>
    internal static class Interface
    {
        private const int Most = 600;
        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        private sealed class Found
        {
            public string t { get; set; }
            public int x { get; set; }
            public int y { get; set; }
            public int w { get; set; }
            public int h { get; set; }
            public string n { get; set; }
            public string k { get; set; }
            /// <summary>Set only by a search of hidden elements too: true for one that is not shown.</summary>
            public bool? hidden { get; set; }
        }

        private static readonly JsonSerializerOptions Quiet = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private static IEnumerable<VisualElement> Roots()
        {
            var documents = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIDocument>());
            for (int i = 0; i < documents.Length; i++)
            {
                var document = documents[i]?.TryCast<UIDocument>();
                var root = document?.rootVisualElement;
                if (root != null && root.panel != null) yield return root;
            }
        }

        private static bool Shown(VisualElement e)
        {
            var style = e.resolvedStyle;
            return style.display != DisplayStyle.None && style.visibility != Visibility.Hidden && style.opacity > 0f;
        }

        /// <summary>A panel rectangle in window pixels: the panel's scale found from two corners of the screen.</summary>
        private static Rect OnScreen(IPanel panel, Rect bound)
        {
            var a = RuntimePanelUtils.ScreenToPanel(panel, Vector2.zero);
            var b = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(Screen.width, Screen.height));
            float sx = Math.Abs(b.x - a.x) < 0.001f ? 1f : Screen.width / (b.x - a.x);
            float sy = Math.Abs(b.y - a.y) < 0.001f ? 1f : Screen.height / (b.y - a.y);
            return new Rect((bound.x - a.x) * sx, (bound.y - a.y) * sy, bound.width * sx, bound.height * sy);
        }

        private static Found Describe(VisualElement e, string text)
        {
            var r = OnScreen(e.panel, e.worldBound);
            return new Found
            {
                t = text,
                x = (int)Math.Round(r.x), y = (int)Math.Round(r.y), w = (int)Math.Round(r.width), h = (int)Math.Round(r.height),
                n = e.name ?? "",
                k = e.GetIl2CppType().Name,
            };
        }

        private static bool Visible(Found f) => f.w > 0 && f.h > 0 && f.x + f.w > 0 && f.y + f.h > 0 && f.x < Screen.width && f.y < Screen.height;

        private static string TextOf(VisualElement e)
        {
            var text = e.TryCast<TextElement>()?.text;
            return string.IsNullOrWhiteSpace(text) ? null : Tags.Replace(text, "").Trim();
        }

        private static void Walk(VisualElement from, int depth, Action<VisualElement> each, bool all = false)
        {
            if (from == null || depth > 60 || !all && !Shown(from)) return;
            each(from);
            int count = from.hierarchy.childCount;
            for (int i = 0; i < count; i++) Walk(from.hierarchy.ElementAt(i), depth + 1, each, all);
        }

        /// <summary>Whether an element and every parent of it are shown.</summary>
        private static bool ShownAll(VisualElement e)
        {
            for (var at = e; at != null; at = at.parent)
                if (!Shown(at)) return false;
            return true;
        }

        /// <summary>Every text shown, or those containing the filter (case and accents aside).</summary>
        public static string Texts(string filter)
        {
            string wanted = Plain(filter);
            var found = new List<Found>();
            foreach (var root in Roots())
                Walk(root, 0, e =>
                {
                    if (found.Count >= Most) return;
                    string text = TextOf(e);
                    if (text == null || wanted.Length > 0 && !Plain(text).Contains(wanted)) return;
                    var f = Describe(e, text);
                    if (Visible(f)) found.Add(f);
                });
            return JsonSerializer.Serialize(found, Quiet);
        }

        /// <summary>
        /// The elements whose name or kind contains the query, with the texts inside them. With
        /// <paramref name="all"/>, the hidden ones too, marked so -- the label a field keeps hidden
        /// until its error style shows it, which a search of what is shown never finds.
        /// </summary>
        public static string Find(string query, bool all = false)
        {
            string wanted = Plain(query);
            var found = new List<Found>();
            foreach (var root in Roots())
                Walk(root, 0, e =>
                {
                    if (found.Count >= Most || wanted.Length == 0) return;
                    if (!Plain(e.name ?? "").Contains(wanted) && !Plain(e.GetIl2CppType().Name).Contains(wanted)) return;
                    var texts = new List<string>();
                    Walk(e, 0, inner => { if (texts.Count < 80 && TextOf(inner) is string t) texts.Add(t); }, all);
                    var f = Describe(e, string.Join(" / ", texts));
                    if (all)
                    {
                        if (!ShownAll(e) || !Visible(f)) f.hidden = true;
                        found.Add(f);
                    }
                    else if (Visible(f)) found.Add(f);
                }, all);
            return JsonSerializer.Serialize(found, Quiet);
        }

        /// <summary>
        /// Writes a text into the field named <paramref name="name"/> -- the element itself or the
        /// first text field inside it --, as typing it would end: its value set and its change
        /// told, so the screen's own listener takes it (the creation screen's name, the chat's
        /// line). Typed key by key, the client's fields dropped what came in the background.
        /// </summary>
        public static string SetText(string name, string text, bool submit = false)
        {
            VisualElement target = null, hiddenOne = null;
            foreach (var root in Roots())
                Walk(root, 0, e =>
                {
                    if (target != null || !string.Equals(e.name, name, StringComparison.Ordinal)) return;
                    if (ShownAll(e)) target = e;
                    else hiddenOne ??= e;
                }, all: true);
            target ??= hiddenOne;
            if (target == null) throw new InvalidOperationException($"no element named {name}");

            // The game's own field keeps its text apart from the text field inside it, and its
            // screen reads that one: the chat sends it, the creation screen names with it. Its
            // SetValue sets both and tells its listeners, as typing would.
            Il2CppCore.UILogic.Components.Figma.TextInput input = null;
            for (var at = target; at != null && input == null; at = at.parent)
                input = at.TryCast<Il2CppCore.UILogic.Components.Figma.TextInput>();
            if (input == null)
                Walk(target, 0, e => { if (input == null) input = e.TryCast<Il2CppCore.UILogic.Components.Figma.TextInput>(); }, all: true);
            TextField field = target.TryCast<TextField>();
            if (field == null)
                Walk(target, 0, e => { if (field == null) field = e.TryCast<TextField>(); }, all: true);
            if (input != null)
            {
                input.Focus();
                input.SetValue(text);
                string how = submit ? Submit(input, field) : "";
                return JsonSerializer.Serialize(new { name, kind = "TextInput", value = input.text, submitted = how.Length > 0 ? how : null }, Quiet);
            }

            if (field == null) throw new InvalidOperationException($"{name} ({target.GetIl2CppType().Name}) holds no text field");
            field.Focus();
            field.value = text;
            string sent = submit ? Submit(null, field) : "";
            return JsonSerializer.Serialize(new { name, kind = field.GetIl2CppType().Name, value = field.value, submitted = sent.Length > 0 ? sent : null }, Quiet);
        }

        /// <summary>
        /// The field validated as Enter would: a Return key sent to the text field itself, where the
        /// game's own field listens for it (its onValidate is set only, as a listener on that key) --
        /// the key the harness's keyboard sends does not reach a field in a client in the background.
        /// </summary>
        private static string Submit(Il2CppCore.UILogic.Components.Figma.TextInput input, TextField field)
        {
            // Where the screen listens differs: the key down or up, on the text field or on the game's
            // field around it, or the navigation's submit. Each is tried until the field empties --
            // what a sent chat line does --; a field that does not empty gets them all.
            string Text() => input != null ? input.text : field?.value;
            string before = Text();
            var tries = new List<(string Name, Func<EventBase> Make, VisualElement To)>();
            foreach (var to in new VisualElement[] { field, input })
            {
                if (to == null) continue;
                string where = to == (VisualElement)field ? "field" : "input";
                tries.Add(("keydown@" + where, () => KeyDownEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None), to));
                tries.Add(("keyup@" + where, () => KeyUpEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None), to));
                tries.Add(("submit@" + where, () => NavigationSubmitEvent.GetPooled(), to));
            }
            var sent = new List<string>();
            foreach (var (name, make, to) in tries)
            {
                var e = make();
                e.target = to.Cast<IEventHandler>();
                to.SendEvent(e);
                e.Dispose();
                sent.Add(name);
                if (!string.IsNullOrEmpty(before) && string.IsNullOrEmpty(Text())) return name;
            }
            return string.Join("+", sent) + " (still holds its text)";
        }

        /// <summary>The element under a window point and its parents, innermost first.</summary>
        public static string Pick(int x, int y)
        {
            var found = new List<Found>();
            foreach (var root in Roots())
            {
                var panel = root.panel;
                var at = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(x, y));
                var picked = panel.Pick(at);
                for (var e = picked; e != null && found.Count < 40; e = e.parent)
                    found.Add(Describe(e, TextOf(e) ?? ""));
                if (found.Count > 0) break;
            }
            return JsonSerializer.Serialize(found, Quiet);
        }

        private const string Accented = "áàâäãåéèêëíìîïóòôöõúùûüñçý";
        private const string Bare = "aaaaaaeeeeiiiiooooouuuuncy";

        /// <summary>
        /// Lower case without accents, to compare what a person reads. By hand: the mod's runtime
        /// has no globalisation data, and Normalize leaves the accents where they are.
        /// </summary>
        private static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var plain = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
            {
                int i = Accented.IndexOf(c);
                plain.Append(i >= 0 ? Bare[i] : c);
            }
            return plain.ToString();
        }
    }
}
