using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using Il2CppCore.DataCenter;
using Il2CppCore.DataCenter.Metadata.Item;
using Il2CppCore.DataCenter.Metadata.Monster;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Tooltips.Builder;
using MelonLoader;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The client's own monster group tooltip with two blocks more under its monsters: the
    /// achievements beating the group can still earn, and what it drops, each item with its chance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is the server's (Managers.GroupPreview), asked through the control API's
    /// <c>monsters/tooltip</c> with the session's own token. The tooltip
    /// does not know the group's id, only what is in it -- the builder's group information, a
    /// leader and its underlings, each a template, a grade and a level -- so that is what is sent,
    /// and the answer is kept for a while by composition: hovering a group again shows it at once.
    /// </para>
    /// <para>
    /// The obfuscated names are 3.6.10.11's: <c>ein</c> is the group's static information
    /// (<c>dnmt</c> its leader, <c>dnmu</c> the others) and <c>fl</c> one monster of it
    /// (<c>dbjj</c> template, <c>dbjk</c> grade), as its factory <c>fl.mux(id, grade, level, look)</c> takes them.
    /// </para>
    /// </remarks>
    public static class MonsterGroupTooltip
    {
        private const string SectionName = "JondoGroupPreview";
        private const float Cell = 48f;
        private const int PerRow = 5;
        private static readonly TimeSpan Keep = TimeSpan.FromSeconds(30);

        private static readonly ConcurrentQueue<Action> _toUnity = new ConcurrentQueue<Action>();
        private static readonly Dictionary<string, (DateTime At, Answer Answer)> _answers = new Dictionary<string, (DateTime, Answer)>();
        private static readonly HashSet<string> _asking = new HashSet<string>();
        private static string _token;
        private static bool _off;
        private static bool _described;

        private sealed class Answer
        {
            public int Prospecting;
            public List<(int Id, int Points, int Monster)> Achievements = new List<(int, int, int)>();
            public List<(int Item, double Percent)> Drops = new List<(int, double)>();
        }

        private static string Token
            => _token ??= (Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN")
                           ?? Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ?? "").Trim();

        /// <summary>What the server answered, put on the tooltip on Unity's thread.</summary>
        public static void Tick()
        {
            while (_toUnity.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Group tooltip: {ex.Message}"); }
            }
            try { Wheel(); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Group tooltip, wheel: {ex.Message}"); }
        }

        /// <summary>
        /// The wheel while a group's tooltip is on screen scrolls its achievements: the pointer is
        /// on the monsters, not on the tooltip, so the list would never get the wheel's event itself.
        /// </summary>
        private static void Wheel()
        {
            var scroll = _shown;
            if (scroll != null && scroll.panel == null) scroll = _shown = null;
            _holding = scroll != null && Room(scroll) > 0f;
            HookTheWheel();

            float delta = _wheel;
            _wheel = 0f;
            if (!_holding || delta == 0f) return;

            // One achievement a notch, whatever the wheel reports for one: 120 on some devices, 1
            // on others -- at 120 a notch, a real wheel crept a pixel at a time.
            float notches = Math.Sign(delta) * Math.Max(1f, Math.Abs(delta) / 120f);
            var offset = scroll.scrollOffset;
            offset.y = Math.Clamp(offset.y - notches * PixelsPerNotch, 0f, Room(scroll));
            scroll.scrollOffset = offset;
        }

        private static float Room(ScrollView scroll)
        {
            float room = scroll.contentContainer.layout.height - scroll.contentViewport.layout.height;
            return float.IsNaN(room) ? 0f : room;
        }

        /// <summary>Whether a tooltip's list takes the wheel now, and what the wheel gave it since the last frame.</summary>
        private static bool _holding;
        private static float _wheel;
        private static bool _hooked;

        /// <summary>
        /// The wheel taken from the map while the tooltip's list scrolls. The map's zoom reads the
        /// mouse itself, through no action that could be turned off: right after the input system's
        /// update, before any script reads it, the scroll is kept for the list and wiped from the
        /// mouse's state.
        /// </summary>
        private static void HookTheWheel()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                UnityEngine.InputSystem.InputSystem.add_onAfterUpdate(
                    Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(TakeTheWheel)));
            }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Group tooltip: the wheel stays the map's: {ex.Message}"); }
        }

        private static unsafe void TakeTheWheel()
        {
            if (!_holding) return;
            try
            {
                var control = UnityEngine.InputSystem.Mouse.current?.scroll;
                if (control == null) return;
                float y = control.ReadValue().y;
                if (y == 0f) return;
                _wheel += y;
                float* scroll = (float*)((byte*)control.currentStatePtr + control.stateBlock.byteOffset);
                scroll[0] = 0f;
                scroll[1] = 0f;
            }
            catch { }
        }

        // ─── The words ───────────────────────────────────────────────────────────────────

        private static Dictionary<string, string> L(string es, string en, string fr, string pt, string de)
            => new Dictionary<string, string> { ["es"] = es, ["en"] = en, ["fr"] = fr, ["pt"] = pt, ["de"] = de };

        private static readonly Dictionary<string, Dictionary<string, string>> Texts = new Dictionary<string, Dictionary<string, string>>
        {
            ["achievements"] = L("LOGROS POR DESBLOQUEAR", "ACHIEVEMENTS TO UNLOCK", "SUCCÈS À DÉBLOQUER", "CONQUISTAS A DESBLOQUEAR", "FREIZUSCHALTENDE ERFOLGE"),
            ["loot"] = L("BOTÍN", "LOOT", "BUTINS", "ESPÓLIOS", "BEUTE"),
            ["footer"] = L("Probabilidades con {0} de prospección, sin retos.",
                           "Chances with {0} prospecting, without challenges.",
                           "Chances avec {0} de prospection, hors challenges.",
                           "Chances com {0} de prospecção, sem desafios.",
                           "Chancen mit {0} Beutesuche, ohne Herausforderungen."),
            ["loading"] = L("Cargando el botín…", "Loading the loot…", "Chargement des butins…", "Carregando os espólios…", "Beute wird geladen…"),
        };

        private static string T(string key, params object[] args)
        {
            var byLanguage = Texts[key];
            string text = byLanguage.TryGetValue(KoliseoUi.Language, out string own) ? own : byLanguage["en"];
            return args.Length == 0 ? text : string.Format(text, args);
        }

        /// <summary>
        /// Three significant figures, as the bestiary writes them: 100, 18, 12,8, 6,39, 0,006. The
        /// comma by hand: the client runs without cultures, every one of them is the invariant.
        /// </summary>
        internal static string Percent(double percent, string language)
        {
            string text = percent >= 99.995 ? "100"
                        : percent < 0.001 ? "<0.001"
                        : percent.ToString("G3", CultureInfo.InvariantCulture);
            if (language != "en") text = text.Replace('.', ',');
            return text + " %";
        }

        // ─── The tooltip ─────────────────────────────────────────────────────────────────

        /// <summary>The tooltip's monsters laid out: our blocks under them, now or when the server answers.</summary>
        public static void Decorate(WorldRpMonstersGroupTooltipBuilder builder)
        {
            if (_off || builder == null || Token.Length == 0) return;

            // Found by name under the builder's root, not through its m_binding: the binding is an
            // Il2Cpp struct behind a generic, and reading a field of it froze the client.
            var root = builder.root;
            var list = root == null ? null : UQueryExtensions.Q(root, "ctr_monsterList", (string)null);
            var parent = list?.parent;
            if (parent == null) return;

            var group = GroupOf(builder.m_monstersInfo);
            if (group.Count == 0) return;
            string key = string.Join(";", group.Select(m => $"{m.Monster},{m.Grade}"));
            if (!_described)
            {
                _described = true;
                MelonLogger.Msg($"[JondoFix] Group tooltip: the first group read is {key}.");
            }

            UQueryExtensions.Q(parent, SectionName, (string)null)?.RemoveFromHierarchy();
            var section = new VisualElement { name = SectionName };
            section.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column);
            section.style.paddingLeft = new StyleLength(10f);
            section.style.paddingRight = new StyleLength(10f);
            section.style.paddingBottom = new StyleLength(8f);
            section.pickingMode = PickingMode.Ignore;
            parent.Insert(parent.IndexOf(list) + 1, section);

            if (_answers.TryGetValue(key, out var kept) && DateTime.UtcNow - kept.At < Keep)
            {
                Fill(section, kept.Answer);
                return;
            }

            section.Add(Line(T("loading"), 13f, Grey));
            Ask(key, group, section);
        }

        /// <summary>The group's leader and underlings, as (template, grade).</summary>
        private static List<(int Monster, int Grade)> GroupOf(Il2Cpp.ein info)
        {
            var group = new List<(int, int)>();
            if (info == null) return group;
            void Add(Il2Cpp.fl monster)
            {
                if (monster != null && monster.dbjj > 0) group.Add((monster.dbjj, monster.dbjk));
            }
            Add(info.dnmt);
            var others = info.dnmu;
            if (others != null)
                for (int i = 0; i < others.Count; i++) Add(others[i]);
            return group;
        }

        private static void Ask(string key, List<(int Monster, int Grade)> group, VisualElement section)
        {
            // Unity's thread only, as the answer's end below: the same group hovered again while
            // it is asked waits for the same answer.
            Waiting(key, section);
            if (!_asking.Add(key)) return;

            var request = new Dictionary<string, object>
            {
                ["token"] = Token,
                ["monsters"] = group.Select(m => new[] { m.Monster, m.Grade }).ToArray(),
            };
            Task.Run(async () =>
            {
                var (code, body) = await AdminItemsUi.PostAsync("monsters/tooltip", request);
                Answer answer = null;
                if (code == 200)
                {
                    try { answer = Read(body); }
                    catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Group tooltip: unreadable answer: {ex.Message}"); }
                }
                else if (code == 401 || code == 404)
                {
                    // No session the server knows, or a server without the route: no more asking.
                    _off = true;
                    MelonLogger.Warning($"[JondoFix] Group tooltip: the server says {code}; the loot stays hidden.");
                }

                _toUnity.Enqueue(() =>
                {
                    _asking.Remove(key);
                    if (answer != null) _answers[key] = (DateTime.UtcNow, answer);
                    if (!_waiting.TryGetValue(key, out var sections)) return;
                    _waiting.Remove(key);
                    foreach (var waiting in sections)
                    {
                        if (waiting.panel == null) continue;
                        waiting.Clear();
                        if (answer != null) Fill(waiting, answer);
                    }
                });
            });
        }

        /// <summary>The tooltips waiting for a composition's answer: the same group hovered twice meanwhile.</summary>
        private static readonly Dictionary<string, List<VisualElement>> _waiting = new Dictionary<string, List<VisualElement>>();

        private static void Waiting(string key, VisualElement section)
        {
            if (!_waiting.TryGetValue(key, out var list)) _waiting[key] = list = new List<VisualElement>();
            list.Add(section);
        }

        private static Answer Read(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var answer = new Answer { Prospecting = root.TryGetProperty("prospecting", out var pp) ? pp.GetInt32() : 100 };
            if (root.TryGetProperty("achievements", out var achievements))
                foreach (var a in achievements.EnumerateArray())
                    answer.Achievements.Add((a.GetProperty("id").GetInt32(), a.GetProperty("points").GetInt32(), a.GetProperty("monster").GetInt32()));
            if (root.TryGetProperty("drops", out var drops))
                foreach (var d in drops.EnumerateArray())
                    answer.Drops.Add((d.GetProperty("item").GetInt32(), d.GetProperty("percent").GetDouble()));
            return answer;
        }

        // ─── The blocks ──────────────────────────────────────────────────────────────────

        private static readonly UnityEngine.Color Grey = new UnityEngine.Color(0.75f, 0.75f, 0.75f, 1f);
        private static readonly UnityEngine.Color Header = new UnityEngine.Color(0.62f, 0.8f, 0.35f, 1f);
        private static readonly UnityEngine.Color AchievementRow = new UnityEngine.Color(0.36f, 0.55f, 0.2f, 0.55f);
        private static readonly UnityEngine.Color Badge = new UnityEngine.Color(0f, 0f, 0f, 0.55f);

        /// <summary>
        /// The tallest the achievements get before they scroll -- three of them -- and the width of
        /// the blocks: five cells and the scroll's bar.
        /// </summary>
        private const float MostHeight = 3 * RowHeight;
        private const float BlockWidth = PerRow * (Cell + 3f) + 14f;
        private const float PixelsPerNotch = RowHeight;

        /// <summary>One achievement: its framed icon, its padding and the gap under it.</summary>
        private const float RowHeight = 36f + 10f + 4f;

        /// <summary>The achievements of the tooltip on screen, which the wheel scrolls (<see cref="Wheel"/>).</summary>
        private static ScrollView _shown;

        private static void Fill(VisualElement section, Answer answer)
        {
            string language = KoliseoUi.Language;
            _shown = null;

            // Only the achievements scroll: the loot stays in sight under them. A scroll of a set
            // width: the tooltip is as wide as its content, and a scroll left to its content's width
            // is measured before it.
            if (answer.Achievements.Count > 0)
            {
                section.Add(Title(T("achievements")));
                var scroll = new ScrollView(ScrollViewMode.Vertical);
                scroll.style.width = new StyleLength(BlockWidth);
                scroll.style.maxHeight = new StyleLength(MostHeight);
                scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                scroll.pickingMode = PickingMode.Ignore;
                foreach (var (id, points, monster) in answer.Achievements)
                    scroll.Add(AchievementLine(id, points, monster));
                section.Add(scroll);
                _shown = scroll;
            }

            if (answer.Drops.Count > 0)
            {
                section.Add(Title($"{T("loot")}  ·  {answer.Drops.Count}"));
                // Rows cut here rather than flex-wrap: the tooltip is as wide as its content, and
                // a wrapping grid was measured as one row -- the second went down over the footer.
                var grid = new VisualElement();
                grid.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column);
                grid.style.marginTop = new StyleLength(4f);
                grid.pickingMode = PickingMode.Ignore;
                VisualElement row = null;
                for (int i = 0; i < answer.Drops.Count; i++)
                {
                    if (i % PerRow == 0)
                    {
                        row = new VisualElement();
                        row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
                        row.style.flexShrink = new StyleFloat(0f);
                        row.pickingMode = PickingMode.Ignore;
                        grid.Add(row);
                    }
                    var (item, percent) = answer.Drops[i];
                    row.Add(DropCell(item, percent, language));
                }
                grid.style.width = new StyleLength(BlockWidth);
                section.Add(grid);

                var footer = Line(T("footer", answer.Prospecting), 12f, Grey);
                footer.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
                footer.style.width = new StyleLength(BlockWidth);
                footer.style.marginTop = new StyleLength(4f);
                section.Add(footer);
            }
        }

        private static VisualElement Title(string text)
        {
            var title = Line(text, 13f, Header);
            title.style.unityFontStyleAndWeight = new StyleEnum<UnityEngine.FontStyle>(UnityEngine.FontStyle.Bold);
            title.style.marginTop = new StyleLength(8f);
            title.style.marginBottom = new StyleLength(2f);
            return title;
        }

        private static DofusLabel Line(string text, float size, UnityEngine.Color color)
        {
            var label = new DofusLabel();
            label.text = text;
            label.style.fontSize = new StyleLength(size);
            label.style.color = new StyleColor(color);
            label.pickingMode = PickingMode.Ignore;
            return label;
        }

        private static readonly UnityEngine.Color Gold = new UnityEngine.Color(0.86f, 0.66f, 0.22f, 1f);
        private static readonly UnityEngine.Color Frame = new UnityEngine.Color(0.1f, 0.1f, 0.16f, 1f);

        /// <summary>
        /// One achievement as the client's achievements window draws its card: the icon in a gold
        /// frame, the name in bold, and on the right the points with the window's own trophy.
        /// </summary>
        private static VisualElement AchievementLine(int id, int points, int monster)
        {
            var row = new VisualElement();
            row.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            row.style.alignItems = new StyleEnum<Align>(Align.Center);
            row.style.backgroundColor = new StyleColor(AchievementRow);
            Round(row, 4f);
            row.style.marginBottom = new StyleLength(4f);
            row.style.paddingLeft = new StyleLength(6f);
            row.style.paddingRight = new StyleLength(8f);
            row.style.paddingTop = new StyleLength(5f);
            row.style.paddingBottom = new StyleLength(5f);
            row.pickingMode = PickingMode.Ignore;

            // The achievement's own icon, as its window draws it; the monster's where there is none.
            string name = $"#{id}";
            int icon = 0;
            try
            {
                var achievement = DataCenterModule.achievementsDataRoot?.GetAchievementById(id);
                name = achievement?.name ?? name;
                icon = achievement?.iconId ?? 0;
            }
            catch { }
            var frame = new VisualElement();
            frame.style.width = new StyleLength(36f);
            frame.style.height = new StyleLength(36f);
            frame.style.flexShrink = new StyleFloat(0f);
            frame.style.backgroundColor = new StyleColor(Frame);
            frame.style.borderTopWidth = new StyleFloat(2f);
            frame.style.borderBottomWidth = new StyleFloat(2f);
            frame.style.borderLeftWidth = new StyleFloat(2f);
            frame.style.borderRightWidth = new StyleFloat(2f);
            frame.style.borderTopColor = new StyleColor(Gold);
            frame.style.borderBottomColor = new StyleColor(Gold);
            frame.style.borderLeftColor = new StyleColor(Gold);
            frame.style.borderRightColor = new StyleColor(Gold);
            Round(frame, 4f);
            frame.pickingMode = PickingMode.Ignore;
            var icons = AchievementIcons();
            if (icon > 0 && icons != null) frame.Add(Icon(icons, icon, 32f));
            else
            {
                int portrait = 0;
                try { portrait = MonsterData.GetById(monster)?.adminSelectionIconId ?? 0; } catch { }
                frame.Add(Icon(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Monster, portrait, 32f));
            }
            row.Add(frame);

            var text = new VisualElement();
            text.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column);
            text.style.flexGrow = new StyleFloat(1f);
            text.style.flexShrink = new StyleFloat(1f);
            text.style.marginLeft = new StyleLength(8f);
            text.pickingMode = PickingMode.Ignore;

            var label = Line(name, 13f, UnityEngine.Color.white);
            label.style.unityFontStyleAndWeight = new StyleEnum<UnityEngine.FontStyle>(UnityEngine.FontStyle.Bold);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            text.Add(label);

            // The points and the client's own trophy, on the right as a badge.
            var score = new VisualElement();
            score.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            score.style.alignItems = new StyleEnum<Align>(Align.Center);
            score.style.flexShrink = new StyleFloat(0f);
            score.style.marginLeft = new StyleLength(6f);
            score.style.paddingLeft = new StyleLength(6f);
            score.style.paddingRight = new StyleLength(4f);
            score.style.paddingTop = new StyleLength(1f);
            score.style.paddingBottom = new StyleLength(1f);
            score.style.backgroundColor = new StyleColor(Badge);
            Round(score, 4f);
            score.pickingMode = PickingMode.Ignore;
            var number = Line(points.ToString(CultureInfo.InvariantCulture), 13f, UnityEngine.Color.white);
            number.style.unityFontStyleAndWeight = new StyleEnum<UnityEngine.FontStyle>(UnityEngine.FontStyle.Bold);
            score.Add(number);
            try
            {
                var trophy = new Il2CppCore.UILogic.Components.Figma.DofusIcon();
                trophy.icon = Il2CppCore.UILogic.Components.Figma.FigmaIcons.success;
                trophy.style.unityBackgroundScaleMode = new StyleEnum<UnityEngine.ScaleMode>(UnityEngine.ScaleMode.ScaleToFit);
                // Held at its size: in a row it shrank to a sliver of itself.
                trophy.style.width = new StyleLength(15f);
                trophy.style.height = new StyleLength(15f);
                trophy.style.minWidth = new StyleLength(15f);
                trophy.style.minHeight = new StyleLength(15f);
                trophy.style.flexShrink = new StyleFloat(0f);
                trophy.style.marginLeft = new StyleLength(3f);
                trophy.style.color = new StyleColor(UnityEngine.Color.white);
                trophy.pickingMode = PickingMode.Ignore;
                score.Add(trophy);
            }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Group tooltip: no trophy: {ex.Message}"); }
            row.Add(text);
            row.Add(score);
            return row;
        }

        private static void Round(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = new StyleLength(radius);
            element.style.borderTopRightRadius = new StyleLength(radius);
            element.style.borderBottomLeftRadius = new StyleLength(radius);
            element.style.borderBottomRightRadius = new StyleLength(radius);
        }

        private static VisualElement DropCell(int item, double percent, string language)
        {
            var cell = new VisualElement();
            cell.style.width = new StyleLength(Cell);
            cell.style.flexShrink = new StyleFloat(0f);
            cell.style.marginRight = new StyleLength(3f);
            cell.style.marginBottom = new StyleLength(3f);
            cell.style.alignItems = new StyleEnum<Align>(Align.Center);
            cell.pickingMode = PickingMode.Ignore;

            var chance = Line(Percent(percent, language), 11f, UnityEngine.Color.white);
            chance.style.backgroundColor = new StyleColor(Badge);
            chance.style.paddingLeft = new StyleLength(2f);
            chance.style.paddingRight = new StyleLength(2f);
            chance.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleCenter);
            cell.Add(chance);

            int icon = 0;
            try { icon = ItemData.GetItemById(item)?.iconId ?? 0; } catch { }
            cell.Add(Icon(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType.Item, icon, Cell - 6f));
            return cell;
        }

        private static readonly Dictionary<Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType,
            Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType> _iconTypes =
            new Dictionary<Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType, Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType>();

        private static bool _icons = true;

        /// <summary>Where the administration picker keeps that kind's icons.</summary>
        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType IconsOf(
            Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType kind)
        {
            try
            {
                if (!_iconTypes.TryGetValue(kind, out var type))
                    _iconTypes[kind] = type = Il2CppCore.UILogic.Admin.AdminSelectItemUI.FindIconAddressableEntryType(kind);
                return type;
            }
            catch { return null; }
        }

        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType _achievementIcons;
        private static bool _achievementIconsSought;

        /// <summary>
        /// Where the achievements window keeps its icons («achievements/{0}.png», Picto/UI). The client
        /// lists every kind of icon as a static of one obfuscated class (3.6.10.11's <c>rd</c>): the one
        /// whose path is the achievements' is taken by its path, not by its obfuscated name.
        /// </summary>
        private static Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType AchievementIcons()
        {
            if (_achievementIconsSought) return _achievementIcons;
            _achievementIconsSought = true;
            try
            {
                foreach (var property in typeof(Il2Cpp.rd).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (property.PropertyType != typeof(Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType)) continue;
                    var type = property.GetValue(null) as Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType;
                    if (type?.pathPrefix == null || !type.pathPrefix.StartsWith("achievements/", StringComparison.OrdinalIgnoreCase)) continue;
                    _achievementIcons = type;
                    MelonLogger.Msg($"[JondoFix] Group tooltip: achievement icons are at {type.pathPrefix}*{type.extension}.");
                    break;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Group tooltip: no achievement icons: {ex.Message}"); }
            return _achievementIcons;
        }

        /// <summary>A picture in a tile of the client's own, where the administration picker keeps that kind's icons.</summary>
        private static VisualElement Icon(Il2CppCore.UILogic.Admin.AdminSelectItemUI.ResourceType kind, int icon, float size)
            => Icon(IconsOf(kind), icon, size);

        private static VisualElement Icon(Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntryType type, int icon, float size)
        {
            var place = new VisualElement();
            place.style.width = new StyleLength(size);
            place.style.height = new StyleLength(size);
            place.style.flexShrink = new StyleFloat(0f);
            place.pickingMode = PickingMode.Ignore;
            if (!_icons || icon <= 0 || type == null) return place;
            try
            {
                var tile = new Il2CppCore.UILogic.Components.Slots.Tile();
                tile.imgAddress = new Il2CppAnkama.AddressableUtilities.Runtime.AddressableEntry(type, icon);
                tile.pickingMode = PickingMode.Ignore;
                tile.style.width = new StyleLength(size);
                tile.style.height = new StyleLength(size);
                place.Add(tile);
            }
            catch (Exception ex)
            {
                _icons = false;
                MelonLogger.Warning($"[JondoFix] Group tooltip: no icons, the client's tile would not take one: {ex.Message}");
            }
            return place;
        }
    }

    /// <summary>The group tooltip laid out by the client: our blocks under its monsters.</summary>
    [HarmonyPatch(typeof(WorldRpMonstersGroupTooltipBuilder), nameof(WorldRpMonstersGroupTooltipBuilder.SetupViewWithContent))]
    public static class MonsterGroupTooltipPatch
    {
        public static void Postfix(WorldRpMonstersGroupTooltipBuilder __instance)
        {
            try { MonsterGroupTooltip.Decorate(__instance); }
            catch (Exception ex) { MelonLogger.Warning($"[JondoFix] Group tooltip: {ex.Message}"); }
        }
    }
}
