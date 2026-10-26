using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CratePilot.Core
{
    // Coordinates are physical pixels relative to the captured client area.
    public sealed class DetectedWord
    {
        public string Text { get; set; }
        public Rectangle Bounds { get; set; }
        public DetectedWord() { Text = String.Empty; }
        public DetectedWord(string text, Rectangle bounds) { Text = text ?? String.Empty; Bounds = bounds; }
    }

    public enum CrateGrade { Unknown = 0, Low = 1, Mid = 2, High = 3 }

    public enum ScreenKind { Unknown, MidGradeMenu, TapPrompt, Collection, Ambiguous, CrateMenu = MidGradeMenu }

    public sealed class StockWordEvidence
    {
        public string Text { get; }
        public Rectangle Bounds { get; }
        public StockWordEvidence(string text, Rectangle bounds) { Text = text ?? String.Empty; Bounds = bounds; }
    }

    public sealed class ScreenObservation
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public IReadOnlyList<DetectedWord> Words { get; set; } = new List<DetectedWord>();
        public ScreenKind Kind { get; set; }
        public CrateGrade Grade { get; set; }
        public string CrateTitle { get; set; }
        public bool IsCrateMenu => Grade != CrateGrade.Unknown;
        public bool IsMidGradeMenu { get { return IsCrateMenu; } set { if (value && Grade == CrateGrade.Unknown) Grade = CrateGrade.Mid; } }
        public int? Remaining { get; set; }
        public Rectangle? RemainingBounds { get; set; }
        // Exact OCR tokens that established Remaining; never infer them again from proximity.
        public IReadOnlyList<StockWordEvidence> RemainingWords { get; set; } = new StockWordEvidence[0];
        public Rectangle? TitleBounds { get; set; }
        public Rectangle? OpenButton { get; set; }
        public Rectangle? TapPrompt { get; set; }
        public Rectangle? NextButton { get; set; }
        public Rectangle? ClaimButton { get; set; }
        public bool HasAmbiguousActions { get; set; }
        public ulong VisualSignature { get; set; }
        public string Text { get; set; } = String.Empty;
    }

    // No native calls, clock, OCR service, or mutable global state: suitable for fixtures.
    public static class RecognitionClassifier
    {
        private static readonly Regex RemainingPattern = new Regex(@"\b(\d{1,6})\s*REMAINING\b|\bREMAINING\s*(\d{1,6})\b", RegexOptions.Compiled);
        private static readonly Regex TapCuePattern = new Regex(@"\bSPAM\b|\bTAP\s+TO\b", RegexOptions.Compiled);

        public static ScreenObservation Analyze(IEnumerable<DetectedWord> words, int width, int height)
        {
            if (words == null) throw new ArgumentNullException(nameof(words));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            var valid = words.Where(w => w != null && !String.IsNullOrWhiteSpace(w.Text) &&
                w.Bounds.Width > 0 && w.Bounds.Height > 0 && w.Bounds.Left >= 0 && w.Bounds.Top >= 0 &&
                (long)w.Bounds.Left + w.Bounds.Width <= width && (long)w.Bounds.Top + w.Bounds.Height <= height).ToList();
            var groups = BuildGroups(valid);
            var observation = new ScreenObservation
            {
                Width = width, Height = height, Words = valid.AsReadOnly(),
                Text = String.Join("\n", groups.Select(g => g.Text))
            };

            var titles = new List<Tuple<CrateGrade, string, Rectangle>>();
            foreach (var r in FindPhrases(groups, "LOW GRADE CRATE"))
                titles.Add(Tuple.Create(CrateGrade.Low, "LOW GRADE CRATE", r));
            foreach (var r in FindPhrases(groups, "MID GRADE CRATE"))
                titles.Add(Tuple.Create(CrateGrade.Mid, "MID GRADE CRATE", r));
            foreach (var r in FindPhrases(groups, "HIGH GRADE CRATE"))
                titles.Add(Tuple.Create(CrateGrade.High, "HIGH GRADE CRATE", r));

            if (titles.Count == 0)
            {
                foreach (var g in groups)
                {
                    if (Regex.IsMatch(g.Text, @"\bLOW\b.*?\b(GRADE|CRATE)\b"))
                        titles.Add(Tuple.Create(CrateGrade.Low, g.Text, g.Bounds));
                    else if (Regex.IsMatch(g.Text, @"\bMID\b.*?\b(GRADE|CRATE)\b"))
                        titles.Add(Tuple.Create(CrateGrade.Mid, g.Text, g.Bounds));
                    else if (Regex.IsMatch(g.Text, @"\bHIGH\b.*?\b(GRADE|CRATE)\b"))
                        titles.Add(Tuple.Create(CrateGrade.High, g.Text, g.Bounds));
                    else if (Regex.IsMatch(g.Text, @"\b(GRADE\s+CRATE|CRATE)\b") && !g.Text.Contains("OPEN"))
                        titles.Add(Tuple.Create(CrateGrade.Mid, g.Text, g.Bounds));
                }
            }

            if (titles.Count >= 1)
            {
                observation.Grade = titles[0].Item1;
                observation.CrateTitle = titles[0].Item2;
                observation.TitleBounds = titles[0].Item3;
            }

            var prompts = FindPhrases(groups, "SPAM TAP TO OPEN").ToList();
            if (prompts.Count == 0) prompts = FindPhrases(groups, "TAP TO OPEN").ToList();
            if (prompts.Count == 0) prompts = FindPhrases(groups, "SPAM TAP").ToList();
            if (prompts.Count == 0)
            {
                var anyTap = groups.FirstOrDefault(g => Regex.IsMatch(g.Text, @"\b(SPAM|TAP)\b") && !g.Text.Contains("REMAINING") && !g.Text.Contains("GRADE"));
                if (anyTap != null) prompts.Add(anyTap.Bounds);
            }
            if (prompts.Count > 0) observation.TapPrompt = prompts[0];
            bool hasTapCue = prompts.Count > 0 || groups.Any(g => TapCuePattern.IsMatch(g.Text));

            var remaining = new List<Tuple<int, TextGroup>>();
            foreach (var group in groups)
            {
                var match = RemainingPattern.Match(group.Text);
                int value;
                if (match.Success && Int32.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, out value))
                    remaining.Add(Tuple.Create(value, group));
            }
            // Support stacked number + REMAINING
            var numGroups = groups.Where(g => Regex.IsMatch(g.Text, @"^\d{1,6}$")).ToList();
            var remGroups = groups.Where(g => g.Text.Contains("REMAINING")).ToList();
            foreach (var ng in numGroups)
            {
                int val;
                if (!Int32.TryParse(ng.Text, out val)) continue;
                var matchRem = remGroups.FirstOrDefault(rg =>
                    Math.Abs(CenterX(ng.Bounds) - CenterX(rg.Bounds)) <= Math.Max(ng.Bounds.Width, rg.Bounds.Width) * 1.5 &&
                    Math.Abs(CenterY(ng.Bounds) - CenterY(rg.Bounds)) <= Math.Max(ng.Bounds.Height, rg.Bounds.Height) * 3);
                if (matchRem != null && !remaining.Any(r => r.Item1 == val))
                {
                    var combined = new TextGroup
                    {
                        Text = val + " REMAINING",
                        Bounds = Rectangle.Union(ng.Bounds, matchRem.Bounds)
                    };
                    combined.Words.AddRange(ng.Words);
                    combined.Words.AddRange(matchRem.Words);
                    remaining.Add(Tuple.Create(val, combined));
                }
            }
            if (observation.TitleBounds.HasValue)
            {
                var title = observation.TitleBounds.Value;
                var nearby = remaining.Where(r => Math.Abs(CenterX(r.Item2.Bounds) - CenterX(title)) < width * .40 &&
                    Math.Abs(CenterY(r.Item2.Bounds) - CenterY(title)) < height * .55).ToList();
                if (nearby.Count == 1)
                {
                    observation.Remaining = nearby[0].Item1;
                    observation.RemainingBounds = nearby[0].Item2.Bounds;
                    observation.RemainingWords = nearby[0].Item2.Words
                        .Select(word => new StockWordEvidence(word.Text, word.Bounds)).ToList().AsReadOnly();
                }
                else if (nearby.Count > 1) observation.HasAmbiguousActions = true;
            }

            var open = ActionLabels(groups, "OPEN", width, height).Where(r => !observation.TapPrompt.HasValue ||
                !IntersectsExpanded(r, observation.TapPrompt.Value, r.Height)).ToList();
            if (observation.TitleBounds.HasValue && !hasTapCue)
            {
                var title = observation.TitleBounds.Value;
                var filtered = open.Where(r => r.Top >= title.Top && Math.Abs(CenterX(r) - CenterX(title)) < width * .45).ToList();
                if (filtered.Count > 0)
                    observation.OpenButton = filtered.OrderBy(r => Math.Abs(CenterX(r) - CenterX(title))).First();
                else if (open.Count > 0)
                    observation.OpenButton = open.OrderBy(r => Math.Abs(CenterX(r) - CenterX(title))).First();
                else
                {
                    // Fallback when open text has low contrast or gradient: in TDS crate menu, OPEN is centered below title
                    int btnWidth = Math.Max(120, title.Width * 2 / 3);
                    int btnHeight = Math.Max(40, (int)(title.Height * 1.5));
                    int btnX = (int)CenterX(title) - btnWidth / 2;
                    int btnY = Math.Min(height - btnHeight - 20, Math.Max(title.Bottom + 120, (int)(height * 0.70)));
                    observation.OpenButton = new Rectangle(btnX, btnY, btnWidth, btnHeight);
                }
            }
            else if (open.Count > 0)
            {
                observation.OpenButton = open[0];
            }
            if (!observation.OpenButton.HasValue && !hasTapCue)
            {
                var anyOpen = groups.FirstOrDefault(g => Regex.IsMatch(g.Text, @"\b([O0DQ]PEN|ABRIR)\b") &&
                    !Regex.IsMatch(g.Text, @"\b(ALL|BUY|ROBUX|SHOP)\b") && g.Bounds.Top >= height * 0.20);
                if (anyOpen != null)
                {
                    observation.OpenButton = anyOpen.Bounds;
                }
                else if (groups.Any(g => g.Text.Contains("REMAINING")))
                {
                    observation.Grade = CrateGrade.Mid;
                    int btnWidth = 160, btnHeight = 45;
                    int btnX = width / 2 - btnWidth / 2;
                    int btnY = Math.Min(height - 100, (int)(height * 0.72));
                    observation.OpenButton = new Rectangle(btnX, btnY, btnWidth, btnHeight);
                }
            }
            var next = ActionLabels(groups, "NEXT", width, height).ToList();
            var claim = ActionLabels(groups, "CLAIM", width, height).ToList();
            if (next.Count > 0) observation.NextButton = next[0];
            if (claim.Count > 0) observation.ClaimButton = claim[0];
            // OCR may wrap OPEN / ALL or BUY / OPEN into separate text rows.
            // Only spatially associated cues block an action; distant HUD text does not.
            if (groups.Any(group => (group.Text == "OPEN" || group.Text == "NEXT" || group.Text == "CLAIM") &&
                HasUnsafeCueNear(group.Bounds, groups))) observation.HasAmbiguousActions = true;

            if ((observation.IsMidGradeMenu && (observation.TapPrompt.HasValue || observation.NextButton.HasValue || observation.ClaimButton.HasValue)) ||
                (observation.TapPrompt.HasValue && (observation.NextButton.HasValue || observation.ClaimButton.HasValue)))
                observation.HasAmbiguousActions = true;
            if (observation.HasAmbiguousActions)
                observation.Kind = ScreenKind.Ambiguous;
            else if (observation.IsMidGradeMenu) observation.Kind = ScreenKind.MidGradeMenu;
            else if (observation.TapPrompt.HasValue) observation.Kind = ScreenKind.TapPrompt;
            else if (observation.NextButton.HasValue || observation.ClaimButton.HasValue) observation.Kind = ScreenKind.Collection;
            else observation.Kind = ScreenKind.Unknown;
            return observation;
        }

        public static string Normalize(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            var builder = new StringBuilder(text.Length);
            foreach (char c in text.ToUpperInvariant())
                builder.Append(Char.IsLetterOrDigit(c) ? c : ' ');
            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }

        private sealed class TextGroup
        {
            internal string Text;
            internal Rectangle Bounds;
            internal readonly List<DetectedWord> Words = new List<DetectedWord>();
        }

        private static List<TextGroup> BuildGroups(List<DetectedWord> words)
        {
            var rows = new List<List<DetectedWord>>();
            foreach (var word in words.OrderBy(w => CenterY(w.Bounds)).ThenBy(w => w.Bounds.Left))
            {
                var row = rows.FirstOrDefault(r => Math.Abs(CenterY(r[0].Bounds) - CenterY(word.Bounds)) <=
                    Math.Max(r[0].Bounds.Height, word.Bounds.Height) * .55);
                if (row == null) { row = new List<DetectedWord>(); rows.Add(row); }
                row.Add(word);
            }
            var groups = new List<TextGroup>();
            foreach (var row in rows)
            {
                TextGroup current = null;
                foreach (var word in row.OrderBy(w => w.Bounds.Left))
                {
                    string text = Normalize(word.Text);
                    if (text.Length == 0) continue;
                    if (current == null || word.Bounds.Left - current.Bounds.Right > Math.Max(current.Bounds.Height, word.Bounds.Height) * 3)
                    {
                        current = new TextGroup { Text = text, Bounds = word.Bounds };
                        groups.Add(current);
                    }
                    else
                    {
                        current.Text += " " + text;
                        current.Bounds = Rectangle.Union(current.Bounds, word.Bounds);
                    }
                    current.Words.Add(new DetectedWord(word.Text, word.Bounds));
                }
            }
            return groups;
        }

        private static IEnumerable<Rectangle> FindPhrases(List<TextGroup> groups, string phrase)
        {
            foreach (var group in groups)
            {
                if (group.Text == phrase) { yield return group.Bounds; continue; }
                // The game can wrap a title/prompt over two nearby centered lines.
                if (!phrase.StartsWith(group.Text + " ", StringComparison.Ordinal)) continue;
                foreach (var next in groups)
                {
                    if (next == group || next.Bounds.Top < group.Bounds.Bottom - group.Bounds.Height / 3 ||
                        next.Bounds.Top - group.Bounds.Bottom > Math.Max(group.Bounds.Height, next.Bounds.Height) * 2 ||
                        Math.Abs(CenterX(group.Bounds) - CenterX(next.Bounds)) > Math.Max(group.Bounds.Width, next.Bounds.Width) * .65)
                        continue;
                    if (group.Text + " " + next.Text == phrase) yield return Rectangle.Union(group.Bounds, next.Bounds);
                }
            }
        }

        private static IEnumerable<Rectangle> ActionLabels(List<TextGroup> groups, string label, int width, int height)
        {
            foreach (var group in groups)
            {
                var r = group.Bounds;
                string text = group.Text;
                bool isMatch = false;
                if (label == "OPEN")
                {
                    isMatch = Regex.IsMatch(text, @"\b([O0DQ]PEN|ABRIR)\b") && !Regex.IsMatch(text, @"\b(ALL|BUY|ROBUX|SHOP)\b");
                }
                else if (label == "NEXT")
                {
                    isMatch = Regex.IsMatch(text, @"\bNEXT\b");
                }
                else if (label == "CLAIM")
                {
                    isMatch = Regex.IsMatch(text, @"\b(CLAIM|COLLECT|EQUIP|CONTINUE)\b") || text == "OK";
                }
                else
                {
                    isMatch = text == label;
                }

                if (isMatch && r.Top >= height * 0.15 && r.Width < width * .7 && r.Height < height * .3 &&
                    r.Width >= 5 && r.Height >= 5)
                    yield return r;
            }
        }

        private static bool HasUnsafeCueNear(Rectangle label, List<TextGroup> groups)
        {
            foreach (var cue in groups.SelectMany(group => group.Words))
            {
                string text = Normalize(cue.Text);
                bool bulk = Regex.IsMatch(text, @"\bALL\b");
                bool purchase = Regex.IsMatch(text, @"\b(BUY|PURCHASE|SHOP|ROBUX|COST|PRICE)\b");
                if (!bulk && !purchase) continue;
                int height = Math.Max(label.Height, cue.Bounds.Height);
                int horizontalGap = Math.Max(0, Math.Max(label.Left - cue.Bounds.Right, cue.Bounds.Left - label.Right));
                int verticalGap = Math.Max(0, Math.Max(label.Top - cue.Bounds.Bottom, cue.Bounds.Top - label.Bottom));
                bool sameRow = Math.Abs(CenterY(label) - CenterY(cue.Bounds)) <= height * .65 &&
                    horizontalGap <= Math.Max(height * 3, label.Width);
                bool wrapped = Math.Abs(CenterX(label) - CenterX(cue.Bounds)) <=
                    Math.Max(height, Math.Min(label.Width, cue.Bounds.Width) * .5) && verticalGap <= height * 1.25;
                if (sameRow || wrapped) return true;
            }
            return false;
        }

        private static bool IntersectsExpanded(Rectangle a, Rectangle b, int margin)
        {
            b.Inflate(margin, margin);
            return a.IntersectsWith(b);
        }
        private static double CenterX(Rectangle r) { return r.Left + r.Width * .5; }
        private static double CenterY(Rectangle r) { return r.Top + r.Height * .5; }
    }
}
