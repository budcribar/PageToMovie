using System.Globalization;
using PageToMovie.Fountain;

namespace PageToMovie.ScreenplayEditor.Models;

/// <summary>
/// Renders a <see cref="ScreenplayModel"/> as an industry-standard-formatted screenplay PDF.
/// Uses the PDF spec's built-in Base-14 "Courier" font (fixed-pitch metrics defined by the
/// spec itself), so no font file needs to be embedded, downloaded, or resolved at run time —
/// this keeps generation fully client-side and Blazor WASM-safe.
/// </summary>
public static class PdfFormatter
{
    private const double PageWidth = 612;   // 8.5in
    private const double PageHeight = 792;  // 11in
    private const double MarginTop = 72;    // 1in
    private const double MarginBottom = 72; // 1in
    private const double ContentLeft = 108;  // 1.5in from left edge
    private const double ContentRight = 540; // 1in from right edge
    private const double FontSize = 12;
    private const double LineHeight = 12;    // 6 lines per inch, standard screenplay spacing
    private const double CharWidth = FontSize * 0.6; // Courier is fixed-pitch: 600/1000 em

    private const double CharacterIndent = 266;    // 3.7in from left edge
    private const double ParentheticalIndent = 223; // 3.1in from left edge
    private const double DialogueIndent = 180;      // 2.5in from left edge
    private const double DialogueRight = 432;       // 6.0in from left edge

    private const double PageNumberY = PageHeight - 54; // 0.75in from top edge

    private const int MaxCharsAction = (int)((ContentRight - ContentLeft) / CharWidth);
    private const int MaxCharsDialogue = (int)((DialogueRight - DialogueIndent) / CharWidth);
    private const int MaxCharsParenthetical = (int)((DialogueRight - ParentheticalIndent) / CharWidth);
    private const int MaxCharsCharacter = (int)((DialogueRight - CharacterIndent) / CharWidth);

    public static byte[] ToPdfBytes(this ScreenplayModel model)
    {
        var pages = LayoutPages(model);
        return BuildPdfBytes(pages);
    }

    private static string Norm(string? s) => FountainLexer.NormalizeTypographicPunctuation(s ?? "");

    private static List<List<PdfTextLine>> LayoutPages(ScreenplayModel model)
    {
        var pages = new List<List<PdfTextLine>>();
        var currentPage = new List<PdfTextLine>();
        double cursorY = 0;

        void NewPage()
        {
            currentPage = new List<PdfTextLine>();
            pages.Add(currentPage);
            cursorY = PageHeight - MarginTop;
            if (pages.Count > 1)
            {
                var label = pages.Count + ".";
                currentPage.Add(new PdfTextLine(ContentRight - label.Length * CharWidth, PageNumberY, label));
            }
        }

        void EnsureRoom()
        {
            if (currentPage == null || cursorY - LineHeight < MarginBottom)
            {
                NewPage();
            }
        }

        void EmitLine(double x, string text)
        {
            EnsureRoom();
            cursorY -= LineHeight;
            currentPage.Add(new PdfTextLine(x, cursorY, text));
        }

        void EmitBlank()
        {
            EnsureRoom();
            cursorY -= LineHeight;
        }

        NewPage();

        var firstScene = true;
        foreach (var scene in model.Scenes)
        {
            if (!firstScene) EmitBlank();
            firstScene = false;

            foreach (var line in WrapText(BuildSceneHeading(scene), MaxCharsAction))
            {
                EmitLine(ContentLeft, line);
            }
            EmitBlank();

            foreach (var beat in scene.Beats)
            {
                EmitBeat(beat, EmitLine, EmitBlank);
            }
        }

        return pages;
    }

    private static void EmitBeat(ScreenplayBeat beat, Action<double, string> emitLine, Action emitBlank)
    {
        switch (beat.BeatType)
        {
            case BeatType.Action:
                if (string.IsNullOrWhiteSpace(beat.ActionText)) return;
                foreach (var paragraph in Norm(beat.ActionText).Replace("\r\n", "\n").Split('\n'))
                {
                    foreach (var line in WrapText(paragraph, MaxCharsAction))
                    {
                        emitLine(ContentLeft, line);
                    }
                }
                emitBlank();
                return;

            case BeatType.Dialogue:
                EmitDialogue(beat, emitLine, emitBlank);
                return;

            case BeatType.Transition:
                if (string.IsNullOrWhiteSpace(beat.TransitionText)) return;
                foreach (var line in WrapText(Norm(beat.TransitionText).Trim().ToUpperInvariant(), MaxCharsAction))
                {
                    emitLine(ContentRight - line.Length * CharWidth, line);
                }
                emitBlank();
                return;

            case BeatType.Centered:
                if (string.IsNullOrWhiteSpace(beat.ActionText)) return;
                foreach (var line in WrapText(Norm(beat.ActionText).Trim('>', '<', ' '), MaxCharsAction))
                {
                    var x = ContentLeft + ((ContentRight - ContentLeft) - line.Length * CharWidth) / 2;
                    emitLine(x, line);
                }
                emitBlank();
                return;

            case BeatType.Note:
                // Fountain notes ([[ ... ]]) are production annotations, never rendered into the
                // formatted screenplay — same rule the Fountain spec itself uses.
                return;
        }
    }

    private static void EmitDialogue(ScreenplayBeat beat, Action<double, string> emitLine, Action emitBlank)
    {
        var wroteAnything = EmitSpeakerCue(beat, emitLine);
        if (EmitParentheticals(beat, emitLine)) wroteAnything = true;
        if (EmitSpokenLines(beat, emitLine)) wroteAnything = true;
        if (wroteAnything) emitBlank();
    }

    private static bool EmitSpeakerCue(ScreenplayBeat beat, Action<double, string> emitLine)
    {
        if (string.IsNullOrWhiteSpace(beat.Speaker)) return false;
        var cue = Norm(beat.Speaker).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(beat.Extension))
            cue += $" ({Norm(beat.Extension).Trim('(', ')').ToUpperInvariant()})";
        foreach (var line in WrapText(cue, MaxCharsCharacter))
            emitLine(CharacterIndent, line);
        return true;
    }

    private static bool EmitParentheticals(ScreenplayBeat beat, Action<double, string> emitLine)
    {
        if (string.IsNullOrWhiteSpace(beat.Parenthetical)) return false;
        foreach (var raw in Norm(beat.Parenthetical).Split('\n'))
            EmitOneParenthetical(raw, emitLine);
        return true;
    }

    private static void EmitOneParenthetical(string raw, Action<double, string> emitLine)
    {
        var paren = raw.Trim();
        if (paren.Length == 0) return;
        if (!paren.StartsWith('(')) paren = "(" + paren;
        if (!paren.EndsWith(')')) paren += ")";
        foreach (var line in WrapText(paren, MaxCharsParenthetical))
            emitLine(ParentheticalIndent, line);
    }

    private static bool EmitSpokenLines(ScreenplayBeat beat, Action<double, string> emitLine)
    {
        if (string.IsNullOrWhiteSpace(beat.SpokenText)) return false;
        foreach (var segment in Norm(beat.SpokenText).Replace("\r\n", "\n").Split('\n'))
        {
            foreach (var line in WrapText(segment, MaxCharsDialogue))
                emitLine(DialogueIndent, line);
        }
        return true;
    }

    private static string BuildSceneHeading(ScreenplayScene scene)
    {
        var heading = !string.IsNullOrWhiteSpace(scene.SceneTitle)
            ? scene.SceneTitle.TrimStart('.')
            : $"{scene.Environment} {scene.Location} - {scene.TimeOfDay}".Trim(' ', '-');
        return Norm(heading).ToUpperInvariant();
    }

    private static List<string> WrapText(string text, int maxChars)
    {
        var lines = new List<string>();
        if (maxChars < 1) maxChars = 1;
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }

        var current = new System.Text.StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var remaining = word;
            while (remaining.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                lines.Add(remaining[..maxChars]);
                remaining = remaining[maxChars..];
            }

            if (current.Length == 0)
            {
                current.Append(remaining);
            }
            else if (current.Length + 1 + remaining.Length <= maxChars)
            {
                current.Append(' ').Append(remaining);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(remaining);
            }
        }
        lines.Add(current.ToString());
        return lines;
    }

    private readonly record struct PdfTextLine(double X, double Y, string Text);

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static byte[] BuildPdfBytes(List<List<PdfTextLine>> pages)
    {
        if (pages.Count == 0) pages.Add(new List<PdfTextLine>());
        var pageCount = pages.Count;

        const int catalogObj = 1;
        const int pagesObj = 2;
        const int fontObj = 3;
        const int firstPageObj = 4;
        var firstContentObj = firstPageObj + pageCount;
        var objectCount = firstContentObj + pageCount - 1;

        var offsets = new int[objectCount + 1];
        var buf = new PdfByteBuffer();

        buf.Ascii("%PDF-1.4\n");

        void BeginObj(int num)
        {
            offsets[num] = buf.Position;
            buf.Ascii($"{num} 0 obj\n");
        }
        void EndObj() => buf.Ascii("endobj\n");

        BeginObj(catalogObj);
        buf.Ascii($"<< /Type /Catalog /Pages {pagesObj} 0 R >>\n");
        EndObj();

        BeginObj(pagesObj);
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{firstPageObj + i} 0 R"));
        buf.Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\n");
        EndObj();

        BeginObj(fontObj);
        buf.Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>\n");
        EndObj();

        var contentBytes = new byte[pageCount][];
        for (var i = 0; i < pageCount; i++)
        {
            var cbuf = new PdfByteBuffer();
            cbuf.Ascii("BT\n/F1 12 Tf\n");
            foreach (var line in pages[i])
            {
                cbuf.Ascii($"1 0 0 1 {Num(line.X)} {Num(line.Y)} Tm\n");
                cbuf.PdfLiteralString(line.Text);
                cbuf.Ascii(" Tj\n");
            }
            cbuf.Ascii("ET\n");
            contentBytes[i] = cbuf.ToArray();
        }

        for (var i = 0; i < pageCount; i++)
        {
            BeginObj(firstContentObj + i);
            buf.Ascii($"<< /Length {contentBytes[i].Length} >>\nstream\n");
            buf.Raw(contentBytes[i]);
            buf.Ascii("\nendstream\n");
            EndObj();
        }

        for (var i = 0; i < pageCount; i++)
        {
            BeginObj(firstPageObj + i);
            buf.Ascii($"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] " +
                      $"/Resources << /Font << /F1 {fontObj} 0 R >> >> /Contents {firstContentObj + i} 0 R >>\n");
            EndObj();
        }

        var xrefStart = buf.Position;
        buf.Ascii($"xref\n0 {objectCount + 1}\n");
        buf.Ascii("0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
        {
            buf.Ascii(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }
        buf.Ascii($"trailer\n<< /Size {objectCount + 1} /Root {catalogObj} 0 R >>\nstartxref\n{xrefStart}\n%%EOF");

        return buf.ToArray();
    }

    private sealed class PdfByteBuffer
    {
        private readonly List<byte> _bytes = new();
        public int Position => _bytes.Count;

        public void Ascii(string s)
        {
            foreach (var c in s) _bytes.Add((byte)c);
        }

        public void Raw(byte b) => _bytes.Add(b);
        public void Raw(byte[] bytes) => _bytes.AddRange(bytes);

        public void PdfLiteralString(string text)
        {
            _bytes.Add((byte)'(');
            foreach (var ch in text)
            {
                var b = ch <= 0xFF ? (byte)ch : (byte)'?';
                if (b is (byte)'(' or (byte)')' or (byte)'\\')
                {
                    _bytes.Add((byte)'\\');
                }
                _bytes.Add(b);
            }
            _bytes.Add((byte)')');
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
