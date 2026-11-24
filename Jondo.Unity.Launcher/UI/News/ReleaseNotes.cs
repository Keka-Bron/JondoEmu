using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Launcher.UI.News
{
    /// <summary>A piece of a line: plain, bold or code.</summary>
    internal sealed record NewsSpan(string Text, bool Bold = false, bool Code = false);

    internal enum NewsBlockKind { Heading, Bullet, Paragraph }

    /// <summary>
    /// One block of a release's notes: a heading (level 1 to 3), a bullet (level 0 for the
    /// outer list, 1 for a nested one) or a paragraph.
    /// </summary>
    internal sealed record NewsBlock(NewsBlockKind Kind, int Level, IReadOnlyList<NewsSpan> Spans)
    {
        public string PlainText
        {
            get
            {
                var text = new StringBuilder();
                foreach (var span in Spans) text.Append(span.Text);
                return text.ToString();
            }
        }
    }

    /// <summary>
    /// The small part of GitHub's Markdown a release's notes are written in, turned into blocks
    /// the launcher draws with its own type: headings, bullets, paragraphs, bold and code.
    /// </summary>
    /// <remarks>
    /// Not a Markdown engine: the notes are ours and keep to these shapes. Anything else
    /// (a table, an image, raw HTML) comes out as its plain text, never as markup.
    /// Links keep their words and lose the address; the release page is one button away.
    /// </remarks>
    internal static class ReleaseNotes
    {
        private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Numbered = new(@"^(\s*)\d+[.)]\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Link = new(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
        private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex Tag = new(@"</?[A-Za-z][^>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.CultureInvariant);

        public static IReadOnlyList<NewsBlock> Parse(string? markdown)
        {
            var blocks = new List<NewsBlock>();
            if (string.IsNullOrWhiteSpace(markdown)) return blocks;

            string text = Comment.Replace(markdown.Replace("\r\n", "\n").Replace('\r', '\n'), "");
            var paragraph = new StringBuilder();
            bool inFence = false;

            void EndParagraph()
            {
                if (paragraph.Length == 0) return;
                blocks.Add(new NewsBlock(NewsBlockKind.Paragraph, 0, Inline(paragraph.ToString())));
                paragraph.Clear();
            }

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd();

                // A fenced block is shown as its lines, each one code.
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    EndParagraph();
                    inFence = !inFence;
                    continue;
                }
                if (inFence)
                {
                    if (line.Length > 0)
                        blocks.Add(new NewsBlock(NewsBlockKind.Paragraph, 0, new[] { new NewsSpan(line.Trim(), Code: true) }));
                    continue;
                }

                if (line.Trim().Length == 0 || Rule.IsMatch(line))
                {
                    EndParagraph();
                    continue;
                }

                var heading = Heading.Match(line.TrimStart());
                if (heading.Success)
                {
                    EndParagraph();
                    int level = Math.Min(3, heading.Groups[1].Value.Length);
                    string words = heading.Groups[2].Value.TrimEnd('#', ' ');
                    blocks.Add(new NewsBlock(NewsBlockKind.Heading, level, Inline(words)));
                    continue;
                }

                var bullet = Bullet.Match(line);
                if (!bullet.Success) bullet = Numbered.Match(line);
                if (bullet.Success)
                {
                    EndParagraph();
                    int indent = bullet.Groups[1].Value.Replace("\t", "    ").Length;
                    blocks.Add(new NewsBlock(NewsBlockKind.Bullet, indent >= 2 ? 1 : 0, Inline(bullet.Groups[2].Value)));
                    continue;
                }

                // A line that carries on a bullet (indented, right after it) belongs to it.
                if (paragraph.Length == 0 && blocks.Count > 0 && blocks[^1].Kind == NewsBlockKind.Bullet
                    && raw.Length > 0 && char.IsWhiteSpace(raw[0]))
                {
                    var last = blocks[^1];
                    var spans = new List<NewsSpan>(last.Spans) { new(" ") };
                    spans.AddRange(Inline(line.Trim()));
                    blocks[^1] = last with { Spans = spans };
                    continue;
                }

                string quote = line.TrimStart();
                if (quote.StartsWith('>')) quote = quote.TrimStart('>', ' ');
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(quote.Trim());
            }

            EndParagraph();
            return blocks;
        }

        /// <summary>Bold and code inside a line; links keep their words, tags go.</summary>
        internal static IReadOnlyList<NewsSpan> Inline(string text)
        {
            text = Link.Replace(text, m => m.Groups[1].Value);
            text = Tag.Replace(text, "");
            text = System.Net.WebUtility.HtmlDecode(text);

            var spans = new List<NewsSpan>();
            var plain = new StringBuilder();

            void Flush()
            {
                if (plain.Length == 0) return;
                spans.Add(new NewsSpan(plain.ToString()));
                plain.Clear();
            }

            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        Flush();
                        spans.Add(new NewsSpan(text.Substring(i + 1, end - i - 1), Code: true));
                        i = end + 1;
                        continue;
                    }
                }
                if ((text[i] == '*' || text[i] == '_') && i + 1 < text.Length && text[i + 1] == text[i])
                {
                    string mark = text.Substring(i, 2);
                    int end = text.IndexOf(mark, i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        Flush();
                        spans.Add(new NewsSpan(text.Substring(i + 2, end - i - 2), Bold: true));
                        i = end + 2;
                        continue;
                    }
                }
                plain.Append(text[i]);
                i++;
            }

            Flush();
            return spans;
        }
    }
}
