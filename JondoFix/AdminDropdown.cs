using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// A drop-down list for the administrator's window: a box with what is picked and an arrow,
    /// and under it, once clicked, the list to pick from, scrolling when it is long.
    /// </summary>
    /// <remarks>
    /// Drawn here rather than the client's Dropdown: that one is fed through generic Il2Cpp
    /// delegates (Action&lt;ListElementBase&gt;) that cannot be tried without a round in the game
    /// each time, and a picker that silently does nothing is worse than a plain one. Its box is
    /// drawn like the client's text fields, so it sits with them.
    /// </remarks>
    internal static class AdminDropdown
    {
        private static readonly UnityEngine.Color Box = new UnityEngine.Color(0.05f, 0.05f, 0.09f, 0.85f);
        private static readonly UnityEngine.Color Border = new UnityEngine.Color(1f, 1f, 1f, 0.22f);
        private static readonly UnityEngine.Color ListBack = new UnityEngine.Color(0.09f, 0.09f, 0.14f, 0.98f);

        /// <summary>
        /// The drop-down: <paramref name="items"/> as (key, text), the one whose key is
        /// <paramref name="selected"/> shown in the box, and <paramref name="picked"/> called with
        /// the key of the one clicked. <paramref name="empty"/> stands in the box when nothing is.
        /// </summary>
        public static VisualElement Make(IReadOnlyList<(string Key, string Text)> items, string selected,
                                         Action<string> picked, string empty, float width = 380f)
        {
            var root = new VisualElement();
            root.style.width = new StyleLength(width);
            root.style.flexShrink = new StyleFloat(0f);

            var box = AdminItemsUi.Row();
            box.style.alignItems = new StyleEnum<Align>(Align.Center);
            box.style.justifyContent = new StyleEnum<Justify>(Justify.SpaceBetween);
            box.style.backgroundColor = new StyleColor(Box);
            Edge(box, Border, 1f);
            Round(box, 4f);
            box.style.paddingLeft = new StyleLength(12f);
            box.style.paddingRight = new StyleLength(12f);
            box.style.paddingTop = new StyleLength(7f);
            box.style.paddingBottom = new StyleLength(7f);

            string shown = empty;
            foreach (var (key, text) in items) if (key == selected) shown = text;
            var label = AdminItemsUi.Label(shown);
            label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
            label.style.flexShrink = new StyleFloat(1f);
            label.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
            box.Add(label);
            var arrow = Arrow();
            arrow.style.marginLeft = new StyleLength(10f);
            box.Add(arrow);
            root.Add(box);

            var list = new ScrollView(ScrollViewMode.Vertical);
            list.style.maxHeight = new StyleLength(260f);
            list.style.backgroundColor = new StyleColor(ListBack);
            Edge(list, Border, 1f);
            Round(list, 4f);
            list.style.marginTop = new StyleLength(2f);
            list.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
            foreach (var (key, text) in items)
            {
                string k = key, t = text;
                var line = AdminItemsUi.Label(t, k == selected);
                line.style.paddingLeft = new StyleLength(12f);
                line.style.paddingTop = new StyleLength(6f);
                line.style.paddingBottom = new StyleLength(6f);
                if (k == selected) line.style.backgroundColor = new StyleColor(AdminItemsUi.RowPicked);
                line.AddManipulator(new Clickable(AdminItemsUi.Do(() =>
                {
                    label.text = t;
                    list.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
                    picked(k);
                })).Cast<IManipulator>());
                list.Add(line);
            }
            root.Add(list);

            box.AddManipulator(new Clickable(AdminItemsUi.Do(() =>
            {
                bool open = list.resolvedStyle.display == DisplayStyle.Flex;
                list.style.display = new StyleEnum<DisplayStyle>(open ? DisplayStyle.None : DisplayStyle.Flex);
            })).Cast<IManipulator>());
            return root;
        }

        /// <summary>
        /// A small arrow pointing down, drawn as bars narrowing by a pixel each side: the client's
        /// font has no arrow glyph, and one missing draws as an empty box.
        /// </summary>
        private static VisualElement Arrow()
        {
            var arrow = new VisualElement();
            arrow.style.alignItems = new StyleEnum<Align>(Align.Center);
            arrow.style.flexShrink = new StyleFloat(0f);
            arrow.pickingMode = PickingMode.Ignore;
            for (float width = 11f; width > 0f; width -= 2f)
            {
                var bar = new VisualElement();
                bar.style.width = new StyleLength(width);
                bar.style.height = new StyleLength(1.2f);
                bar.style.backgroundColor = new StyleColor(new UnityEngine.Color(1f, 1f, 1f, 0.8f));
                bar.pickingMode = PickingMode.Ignore;
                arrow.Add(bar);
            }
            return arrow;
        }

        internal static void Edge(VisualElement e, UnityEngine.Color color, float width)
        {
            e.style.borderTopWidth = new StyleFloat(width);
            e.style.borderBottomWidth = new StyleFloat(width);
            e.style.borderLeftWidth = new StyleFloat(width);
            e.style.borderRightWidth = new StyleFloat(width);
            e.style.borderTopColor = new StyleColor(color);
            e.style.borderBottomColor = new StyleColor(color);
            e.style.borderLeftColor = new StyleColor(color);
            e.style.borderRightColor = new StyleColor(color);
        }

        internal static void Round(VisualElement e, float radius)
        {
            e.style.borderTopLeftRadius = new StyleLength(radius);
            e.style.borderTopRightRadius = new StyleLength(radius);
            e.style.borderBottomLeftRadius = new StyleLength(radius);
            e.style.borderBottomRightRadius = new StyleLength(radius);
        }
    }
}
