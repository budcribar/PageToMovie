using PageToMovie.Core.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PageToMovie.Adaptation;

namespace PageToMovie.Engine;

/// <summary>
/// Prepare book source for Stage 1: PDF text (PdfPig), page render, optional Grok vision OCR.
/// Writes source/book_full.txt + extract_meta.json.
/// </summary>
public sealed class BookPrepareService
{
    private const string BookImagesFolder = "book_images";
    private const string EmbeddedKind = "embedded";
    private const string RelevanceKey = "relevance";
    private const string GrokVisionTranscribeAction = "grok_vision_transcribe";
    private const string GrokVisionEngine = "grok_vision";
    private const string RenderedPageRelevance = "rendered_page";

    private readonly ProjectStore _projects;
    private readonly IVisionClient _vision;
    private readonly ILogger<BookPrepareService> _log;

    private static readonly Regex HtmlEntryExtRegex = new(@"\.(xhtml|html|htm)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex HtmlTagsRegex = new(@"<[^>]+>", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex WhitespaceNormalizeRegex = new(@"\s+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ImageFileExtRegex = new(@"\.(png|jpe?g|webp)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex EmbeddedPageNumRegex = new(@"embedded_p(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex RenderedPageNumRegex = new(@"page_(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    public BookPrepareService(
        ProjectStore projects,
        IVisionClient vision,
        IOptions<PageToMovieOptions> opts,
        ILogger<BookPrepareService> log)
    {
        _projects = projects;
        _vision = vision;
        _log = log;
    }

    public async Task<BookPrepareResult> PrepareAsync(
        string projectId,
        bool forceExtract = true,
        bool forceVision = false,
        bool autoVision = true,
        string? visionModel = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        {
            var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            visionModel = string.IsNullOrWhiteSpace(visionModel)
                ? ProjectModelSelection.RequireVision(cfg, "Book prepare")
                : ProjectModelSelection.RequireExplicit(visionModel, ModelCapability.Vision, "Book prepare");
        }

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var source = Path.Combine(projectDir, "source");
        Directory.CreateDirectory(source);
        var bookTxt = Path.Combine(source, "book_full.txt");
        var imgDir = Path.Combine(source, BookImagesFolder);
        Directory.CreateDirectory(imgDir);

        var result = new BookPrepareResult { ProjectId = projectId, Ok = false };
        var hasXai = _vision.IsConfigured;
        result.HasXaiKey = hasXai;

        onProgress?.Invoke("Looking for PDF / EPUB / book_full.txt…");
        var pdf = FindPdf(source);
        var epub = pdf is null ? FindEpub(source) : null;
        result.PdfName = pdf is not null ? Path.GetFileName(pdf) : (epub is not null ? Path.GetFileName(epub) : null);

        BookTextAnalysis analysis;
        string engine;

        if (epub is not null && (forceExtract || !File.Exists(bookTxt)))
        {
            onProgress?.Invoke($"Extracting text and images from {Path.GetFileName(epub)} (EPUB)…");
            var (epubRawText, epubImgRows, epubPages) = await ExtractTextAndImagesEpubAsync(epub, imgDir, source, ct).ConfigureAwait(false);
            var text = GutenbergCleaner.StripHeaderAndFooter(epubRawText);
            engine = "epub";
            analysis = BookTextAnalyzer.Analyze(text, epubPages);
            analysis.TextEngine = engine;
            result.ImagesExtracted = epubImgRows.Count;

            await File.WriteAllTextAsync(bookTxt, text + "\n", ct).ConfigureAwait(false);
            if (epubImgRows.Count > 0)
                await WriteManifestAsync(source, imgDir, epubImgRows, epubPages, ct).ConfigureAwait(false);
            else
                await EnsureManifestFromDiskAsync(source, imgDir, epubPages, ct).ConfigureAwait(false);
        }
        else if (pdf is not null && (forceExtract || !File.Exists(bookTxt)))
        {
            onProgress?.Invoke($"Extracting text from {Path.GetFileName(pdf)} (PdfPig)…");
            var (rawExtractText, pageCount) = ExtractTextPdfPig(pdf);
            var text = GutenbergCleaner.StripHeaderAndFooter(rawExtractText);
            engine = "pdfpig";
            analysis = BookTextAnalyzer.Analyze(text, pageCount);
            analysis.TextEngine = engine;

            onProgress?.Invoke("Extracting embedded images…");
            var imageRows = await ExtractEmbeddedImagesAsync(pdf, imgDir, source, ct).ConfigureAwait(false);
            result.ImagesExtracted = imageRows.Count;

            // Fallback: render full pages when embeds are sparse (vision needs plates)
            if (imageRows.Count < Math.Max(1, pageCount / 2))
            {
                onProgress?.Invoke("Rendering page images (PDFtoImage) for vision plates…");
#pragma warning disable CA1416 // PDFtoImage is desktop/mobile OS only (Windows/Linux/macOS)
                var (rendered, renderError) = RenderPdfPages(pdf, imgDir, source, pageCount, analysis);
#pragma warning restore CA1416
                if (rendered.Count > 0)
                {
                    // Prefer rendered pages as cover; keep embeds too
                    imageRows = rendered.Concat(imageRows).ToList();
                    result.ImagesExtracted = imageRows.Count;
                    onProgress?.Invoke($"Rendered {rendered.Count} page plate(s)");
                }
                else if (!string.IsNullOrWhiteSpace(renderError))
                {
                    _log.LogWarning("PDF page render produced 0 images: {Error}", renderError);
                    onProgress?.Invoke($"Page render failed: {renderError}");
                    result.Notes.Add($"PDF page render failed: {renderError}");
                }
                else
                {
                    onProgress?.Invoke("Page render produced 0 images (no error detail).");
                }
            }

            // Picture books without plates cannot OCR — fail loudly instead of a vague later error.
            var looksPicture = analysis.BookKind == BookKind.PictureBook || analysis.TextDensity == TextDensity.Sparse;
            if (looksPicture && imageRows.Count == 0)
            {
                throw new InvalidOperationException(
                    "Could not extract or render page images from this PDF (needed for picture-book OCR). " +
                    "On Linux/Docker install fontconfig + freetype and ensure libpdfium/libSkiaSharp native " +
                    "assets are in the publish output (runtimes/*/native). " +
                    "Local: confirm PDFtoImage works; Railway: check deploy logs for DllNotFoundException.");
            }

            if (imageRows.Count > 0)
                await WriteManifestAsync(source, imgDir, imageRows, pageCount, ct).ConfigureAwait(false);
            else
                await EnsureManifestFromDiskAsync(source, imgDir, pageCount, ct).ConfigureAwait(false);

            // New inventory invalidates prior character plate sort; Stage1/attach re-sorts into scenes.json
            try
            {
                _projects.ClearCharacterPlatesSorted(projectId);
                onProgress?.Invoke("Cleared character_plates sorted flag (book images refreshed)");
            }
            catch { /* non-fatal */ }

            await File.WriteAllTextAsync(bookTxt, text + "\n", ct);
            result.Pages = pageCount;
            result.TextEngine = engine;
            onProgress?.Invoke(
                $"Extract: pages={pageCount} words={analysis.TextWords} quality={analysis.TextQuality} images={imageRows.Count}");
        }
        else if (File.Exists(bookTxt))
        {
            onProgress?.Invoke("Using existing book_full.txt…");
            var text = await File.ReadAllTextAsync(bookTxt, ct);
            // TXT uploads and older projects may still hold a Gutenberg license block on disk.
            if (GutenbergCleaner.HasGutenbergHeader(text))
            {
                onProgress?.Invoke("Stripping Project Gutenberg header/footer from book_full.txt…");
                text = GutenbergCleaner.StripHeaderAndFooter(text);
                text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";
                await File.WriteAllTextAsync(bookTxt, text, ct).ConfigureAwait(false);
            }
            analysis = BookTextAnalyzer.Analyze(text);
            analysis.TextEngine = "existing_book_full";
            result.TextEngine = analysis.TextEngine;
            result.Pages = analysis.Pages;
        }
        else
        {
            throw new InvalidOperationException(
                $"No PDF and no book_full.txt under {source}. Upload a PDF first.");
        }

        var pageImages = await CollectPageImagesAsync(source, ct).ConfigureAwait(false);
        result.PageImageCount = pageImages.Count;
        var strategy = DecideStrategy(analysis, pageImages.Count > 0, hasXai);
        if (forceVision && pageImages.Count > 0 && hasXai)
        {
            strategy = new BookStrategy
            {
                Action = GrokVisionTranscribeAction,
                Reason = "Forced Grok vision transcription.",
                ReadyForStage1 = false,
                NeedsUser = false,
            };
        }
        if (!autoVision && strategy.Action == GrokVisionTranscribeAction)
        {
            strategy = new BookStrategy
            {
                Action = "vision_skipped",
                Reason = "Auto vision disabled; keeping extract text (may be garbled).",
                ReadyForStage1 = analysis.TextQuality == TextQuality.Good,
                NeedsUser = analysis.TextQuality != TextQuality.Good,
            };
        }

        result.Strategy = strategy.Action;
        result.StrategyReason = strategy.Reason;
        onProgress?.Invoke($"Strategy: {strategy.Action} — {strategy.Reason}");

        if (strategy.Action == GrokVisionTranscribeAction)
        {
            if (!hasXai)
                throw new InvalidOperationException("XAI_API_KEY required for Grok vision OCR.");
            if (pageImages.Count == 0)
                throw new InvalidOperationException(
                    "No page images for vision. Re-extract PDF (embedded images) first.");

            // Backup prior text
            if (File.Exists(bookTxt))
            {
                var bak = bookTxt + $".bak_pre_vision_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(bookTxt, bak, overwrite: true);
                onProgress?.Invoke($"Backed up book_full.txt → {Path.GetFileName(bak)}");
            }

            var sb = new StringBuilder();
            var failed = 0;
            for (var i = 0; i < pageImages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (page, path) = pageImages[i];
                onProgress?.Invoke($"Vision OCR page {page} ({i + 1}/{pageImages.Count})…");
                try
                {
                    var pageText = await _vision.TranscribePageAsync(path, page, visionModel, ct);
                    if (string.IsNullOrWhiteSpace(pageText))
                        pageText = "(illustration only)";
                    sb.AppendLine($"--- PAGE {page} ---");
                    sb.AppendLine(pageText.Trim());
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogWarning(ex, "Vision failed page {Page}", page);
                    onProgress?.Invoke($"Page {page} vision failed: {ex.Message}");
                    sb.AppendLine($"--- PAGE {page} ---");
                    sb.AppendLine("(illustration only)");
                    sb.AppendLine();
                }
            }

            await File.WriteAllTextAsync(bookTxt, sb.ToString(), ct);
            analysis = BookTextAnalyzer.Analyze(sb.ToString(), pageImages.Count);
            analysis.TextEngine = GrokVisionEngine;
            result.TextEngine = GrokVisionEngine;
            result.VisionPages = pageImages.Count;
            result.VisionFailedPages = failed;
            onProgress?.Invoke(
                $"Vision done: {pageImages.Count - failed}/{pageImages.Count} pages, quality={analysis.TextQuality}");
        }

        // Natural film minutes from Adaptation façade (A3.3) — not Engine-local math.
        // AnalyzeBook / EstimateNaturalRuntime are pure density; FilmRuntime only persists.
        var bookForNatural = File.Exists(bookTxt)
            ? await File.ReadAllTextAsync(bookTxt, ct).ConfigureAwait(false)
            : "";
        var adaptation = new AdaptationService();
        var analysisFacade = adaptation.AnalyzeBook(bookForNatural);
        var naturalMinutes = analysisFacade.SuggestedTotalMinutes > 0
            ? analysisFacade.SuggestedTotalMinutes
            : adaptation.EstimateNaturalRuntime(bookForNatural).NaturalMinutes;
        if (naturalMinutes > 0)
            analysis.SuggestedTotalMinutes = naturalMinutes;

        result.TextQuality = analysis.TextQuality.ToApiString();
        result.GarbageScore = analysis.GarbageScore;
        result.TextWords = analysis.TextWords;
        result.BookKind = analysis.BookKind.ToApiString();
        result.SuggestedTotalMinutes = analysis.SuggestedTotalMinutes;
        result.SuggestedChunkPages = analysis.SuggestedChunkPages;
        result.Notes = analysis.Notes.ToList();

        // Vision success is stage-1-ready; strategies that need user/key are not;
        // clean embedded text uses analyzer flags.
        if (result.TextEngine == GrokVisionEngine)
        {
            var failed = result.VisionFailedPages;
            var total = Math.Max(result.VisionPages, 1);
            result.ReadyForStage1 = failed < total;
            if (result.ReadyForStage1)
                analysis.ReadyForStage1 = true;
        }
        else if (strategy.NeedsUser ||
                 strategy.Action is "need_xai_for_vision" or "manual_or_ocr" or "vision_skipped")
        {
            result.ReadyForStage1 = false;
        }
        else
        {
            result.ReadyForStage1 = analysis.ReadyForStage1 && analysis.GarbageScore < 0.45;
        }

        result.Ok = true;

        await WriteExtractMetaAsync(source, result, analysis, strategy, visionModel, ct).ConfigureAwait(false);
        onProgress?.Invoke(
            result.ReadyForStage1
                ? $"Book ready for Stage 1 (~{result.SuggestedTotalMinutes} min)"
                : "Book needs attention before Stage 1");
        // Stage-end package history (text only; video ignored by project git).
        _projects.TriggerAutoGitCommit(projectId, ProjectStageCommits.BookPrepared);
        return result;
    }

    private static BookStrategy DecideStrategy(BookTextAnalysis analysis, bool hasImages, bool hasXai)
    {
        var quality = analysis.TextQuality;
        var density = analysis.TextDensity;
        var kind = analysis.BookKind;
        var words = analysis.TextWords;
        var garbage = analysis.GarbageScore;

        var picture = kind == BookKind.PictureBook || density == TextDensity.Sparse;
        var textClearlyClean = quality == TextQuality.Good && garbage < 0.2 && words >= 80 && density != TextDensity.Sparse;

        if (picture && hasImages && hasXai && !textClearlyClean)
        {
            return new BookStrategy
            {
                Action = GrokVisionTranscribeAction,
                Reason =
                    $"Picture book / sparse text (quality={quality}, garbage={garbage:0.00}). " +
                    "Rebuilding book_full.txt with Grok vision from page images.",
                ReadyForStage1 = false,
            };
        }

        if (picture && hasImages && !hasXai && !textClearlyClean)
        {
            return new BookStrategy
            {
                Action = "need_xai_for_vision",
                Reason =
                    "Picture book images ready, but embedded PDF text is unreliable. " +
                    "Set XAI_API_KEY and re-run prepare.",
                ReadyForStage1 = false,
                NeedsUser = true,
            };
        }

        if (quality == TextQuality.Good && garbage < 0.25)
        {
            return new BookStrategy
            {
                Action = "use_embedded_text",
                Reason = "Text looks clean enough for Stage 1.",
                ReadyForStage1 = true,
            };
        }

        var needsBetter = quality is TextQuality.Poor or TextQuality.Empty || garbage >= 0.25;
        if (!needsBetter)
        {
            return new BookStrategy
            {
                Action = "use_embedded_text",
                Reason = $"Text quality '{quality.ToApiString()}' is acceptable for Stage 1.",
                ReadyForStage1 = true,
            };
        }

        if (hasImages && hasXai)
        {
            return new BookStrategy
            {
                Action = GrokVisionTranscribeAction,
                Reason = $"Text quality is '{quality}'. Rebuilding with Grok vision.",
                ReadyForStage1 = false,
            };
        }

        if (hasImages && !hasXai)
        {
            return new BookStrategy
            {
                Action = "need_xai_for_vision",
                Reason = $"Text quality is '{quality}'. Set XAI_API_KEY for vision OCR.",
                ReadyForStage1 = false,
                NeedsUser = true,
            };
        }

        return new BookStrategy
        {
            Action = "manual_or_ocr",
            Reason = "Poor text and no page images. Upload PDF with images or paste book_full.txt.",
            ReadyForStage1 = false,
            NeedsUser = true,
        };
    }

    private static string? FindEpub(string sourceDir)
    {
        if (!Directory.Exists(sourceDir)) return null;
        var cands = new DirectoryInfo(sourceDir).EnumerateFiles()
            .Where(f => f.Extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cands.Count == 0) return null;
        return cands.OrderByDescending(p => p.Length).Select(p => p.FullName).First();
    }

    private static string? FindPdf(string sourceDir)
    {
        if (!Directory.Exists(sourceDir)) return null;
        var cands = new DirectoryInfo(sourceDir).EnumerateFiles()
            .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cands.Count == 0) return null;
        return cands
            .OrderBy(p => p.Name.Contains("nick", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(p => p.Length)
            .Select(p => p.FullName)
            .First();
    }

    private static async Task<(string Text, List<Dictionary<string, object?>> ImageRows, int PageCount)> ExtractTextAndImagesEpubAsync(
        string epubPath,
        string imgDir,
        string sourceDir,
        CancellationToken ct = default)
    {
        var textParts = new List<string>();
        var imageRows = new List<Dictionary<string, object?>>();
        int pageIndex = 0;

        using var fs = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);

        // 1. Extract image files
        var imageEntries = archive.Entries
            .Where(e => CommonRegex.IsMatch(e.FullName, @"\.(png|jpe?g|webp)$", RegexOptions.IgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int imgIndex = 0;
        foreach (var entry in imageEntries)
        {
            ct.ThrowIfCancellationRequested();
            imgIndex++;
            try
            {
                var ext = Path.GetExtension(entry.FullName).TrimStart('.').ToLowerInvariant();
                var name = $"embedded_epub_x{imgIndex:D3}.{ext}";
                var fullPath = Path.Combine(imgDir, name);

                await using (var stream = entry.Open())
                await using (var outStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await stream.CopyToAsync(outStream, ct).ConfigureAwait(false);
                }

                var rel = Path.GetRelativePath(sourceDir, fullPath).Replace('\\', '/');
                imageRows.Add(new Dictionary<string, object?>
                {
                    ["kind"] = EmbeddedKind,
                    ["page"] = imgIndex,
                    ["path"] = rel.StartsWith(BookImagesFolder) ? rel : $"book_images/{name}",
                    [RelevanceKey] = imgIndex == 1 ? "cover" : "embedded_figure",
                });
            }
            catch { /* ignore bad images */ }
        }

        // 2. Extract story text from HTML/XHTML entries
        var htmlEntries = archive.Entries
            .Where(e => HtmlEntryExtRegex.IsMatch(e.FullName) &&
                        !e.Name.Contains("toc", StringComparison.OrdinalIgnoreCase) &&
                        !e.Name.Contains("nav", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in htmlEntries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                var html = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var rawText = HtmlTagsRegex.Replace(html, " ");
                var clean = System.Net.WebUtility.HtmlDecode(rawText);
                clean = WhitespaceNormalizeRegex.Replace(clean, " ").Trim();
                if (clean.Length > 50)
                {
                    pageIndex++;
                    textParts.Add($"--- PAGE {pageIndex} ---\n{clean}");
                }
            }
            catch { /* ignore */ }
        }

        var fullText = string.Join("\n\n", textParts);
        return (fullText, imageRows, Math.Max(1, pageIndex));
    }

    private static (string Text, int PageCount) ExtractTextPdfPig(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);
        var parts = new List<string>();
        var n = 0;
        foreach (var page in doc.GetPages())
        {
            n++;
            var t = (page.Text ?? "").Trim();
            parts.Add($"--- PAGE {n} ---\n{t}");
        }
        return (string.Join("\n\n", parts), n);
    }

    private static async Task<List<Dictionary<string, object?>>> ExtractEmbeddedImagesAsync(
        string pdfPath,
        string imgDir,
        string sourceDir,
        CancellationToken ct = default)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            var pageIndex = 0;
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                pageIndex++;
                var imgIndex = 0;
                foreach (var image in page.GetImages())
                {
                    imgIndex++;
                    try
                    {
                        byte[]? pngBytes = null;
                        if (image.TryGetPng(out var png) && png is { Length: >= 256 })
                            pngBytes = png;
                        else if (image.TryGetBytesAsMemory(out var mem) && mem.Length >= 256)
                            pngBytes = mem.ToArray();
                        if (pngBytes is null)
                            continue;

                        // Skip tiny icons
                        var w = image.WidthInSamples;
                        var h = image.HeightInSamples;
                        if (w < 64 || h < 64)
                            continue;

                        var ext = pngBytes.Length >= 2 && pngBytes[0] == 0xFF && pngBytes[1] == 0xD8
                            ? "jpg"
                            : "png";
                        var name = $"embedded_p{pageIndex:D3}_x{imgIndex}.{ext}";
                        var full = Path.Combine(imgDir, name);
                        await File.WriteAllBytesAsync(full, pngBytes, ct);
                        var rel = Path.GetRelativePath(sourceDir, full).Replace('\\', '/');
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["kind"] = EmbeddedKind,
                            ["page"] = pageIndex,
                            ["path"] = rel.StartsWith(BookImagesFolder) ? rel : $"book_images/{name}",
                            ["width"] = w,
                            ["height"] = h,
                            [RelevanceKey] = "embedded_figure",
                        });
                    }
                    catch
                    {
                        // skip bad images
                    }
                }
            }
        }
        catch
        {
            // non-fatal
        }

        return rows;
    }

    /// <summary>
    /// Render PDF pages to PNG via PDFtoImage (PDFium). Used when embeds are missing
    /// so Grok vision has plates to OCR.
    /// Returns rows plus a human-readable error when rendering fails (e.g. missing libpdfium on Linux).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static (List<Dictionary<string, object?>> Rows, string? Error) RenderPdfPages(
        string pdfPath,
        string imgDir,
        string sourceDir,
        int pageCount,
        BookTextAnalysis analysis)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            // Sparse picture books: render all pages; denser books: cover + sparse
            var renderAll = analysis.BookKind == BookKind.PictureBook || pageCount <= 40;
            var pdfBytes = File.ReadAllBytes(pdfPath);
            using var ms = new MemoryStream(pdfBytes);
            var options = new PDFtoImage.RenderOptions(Dpi: 150);
            var index = 0;
            var pageErrors = new List<string>();
            foreach (var bitmap in PDFtoImage.Conversion.ToImages(ms, options: options))
            {
                index++;
                try
                {
                    if (!renderAll && index > 1)
                    {
                        // Keep cover + every Nth for longer books
                        if (index % Math.Max(2, pageCount / 20) != 0 && index != pageCount)
                        {
                            bitmap.Dispose();
                            continue;
                        }
                    }

                    var name = $"page_{index:D3}_render.png";
                    var full = Path.Combine(imgDir, name);
                    using (var fs = File.OpenWrite(full))
                        bitmap.Encode(fs, SkiaSharp.SKEncodedImageFormat.Png, 90);
                    bitmap.Dispose();

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = RenderedPageRelevance,
                        ["page"] = index,
                        ["path"] = $"book_images/{name}",
                        [RelevanceKey] = index == 1 ? "cover" : RenderedPageRelevance,
                    });
                }
                catch (Exception pageEx)
                {
                    // Skip only this page — never abort the whole render loop.
                    try { bitmap.Dispose(); } catch { /* ignore */ }
                    if (pageErrors.Count < 8)
                    {
                        pageErrors.Add(
                            $"page {index}: {pageEx.GetType().Name}: {pageEx.Message}");
                    }
                }
            }

            if (rows.Count == 0 && index == 0)
                return (rows, "PDFtoImage returned no page bitmaps (empty or unreadable PDF).");
            if (rows.Count == 0 && pageErrors.Count > 0)
                return (rows, "All page renders failed. " + string.Join("; ", pageErrors));
            // Partial success: ignore per-page noise (caller uses row count).
        }
        catch (DllNotFoundException ex)
        {
            return (rows,
                $"Native library missing for PDF render ({ex.Message}). " +
                "Install fontconfig/freetype and ensure runtimes/*/native/libpdfium + libSkiaSharp ship in the image.");
        }
        catch (Exception ex)
        {
            // Outer failure (e.g. mid-enumeration): keep any pages already rendered.
            if (rows.Count > 0)
                return (rows, null);
            return (rows, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (rows, null);
    }

    private static async Task WriteManifestAsync(
        string sourceDir,
        string imgDir,
        List<Dictionary<string, object?>> rows,
        int pages,
        CancellationToken ct = default)
    {
        var man = new Dictionary<string, object?>
        {
            ["schema_version"] = "book_images.v1",
            ["pages"] = pages,
            ["images"] = rows,
            ["updated_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        var path = Path.Combine(imgDir, "manifest.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(man, JsonDefaults.Indented) + "\n",
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuild manifest from files already on disk when PdfPig could not pull new embeds.
    /// </summary>
    private static async Task EnsureManifestFromDiskAsync(
        string sourceDir,
        string imgDir,
        int pages,
        CancellationToken ct = default)
    {
        var path = Path.Combine(imgDir, "manifest.json");
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("images", out var imgs) &&
                    imgs.ValueKind == JsonValueKind.Array &&
                    imgs.GetArrayLength() > 0)
                    return; // keep existing inventory
            }
            catch { /* rebuild below */ }
        }

        if (!Directory.Exists(imgDir)) return;
        var rows = new List<Dictionary<string, object?>>();
        foreach (var fi in new DirectoryInfo(imgDir).EnumerateFiles()
                     .Where(f => ImageFileExtRegex.IsMatch(f.Name))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = fi.Name;
            var f = fi.FullName;
            var m = EmbeddedPageNumRegex.Match(name);
            var kind = EmbeddedKind;
            int page = 0;
            if (m.Success)
                int.TryParse(m.Groups[1].Value, out page);
            else
            {
                m = RenderedPageNumRegex.Match(name);
                if (m.Success)
                {
                    kind = RenderedPageRelevance;
                    int.TryParse(m.Groups[1].Value, out page);
                }
            }
            if (page <= 0) continue;
            rows.Add(new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["page"] = page,
                ["path"] = $"book_images/{name}",
                [RelevanceKey] = kind == EmbeddedKind ? "embedded_figure" : RenderedPageRelevance,
            });
        }
        if (rows.Count == 0) return;
        await WriteManifestAsync(
            sourceDir,
            imgDir,
            rows,
            pages > 0 ? pages : rows.Max(r => (int)r["page"]!),
            ct).ConfigureAwait(false);
    }

    private static async Task<List<(int Page, string Path)>> CollectPageImagesAsync(
        string sourceDir,
        CancellationToken ct = default)
    {
        var imgDir = Path.Combine(sourceDir, BookImagesFolder);
        var byPage = new Dictionary<int, (string? Emb, string? Ren)>();
        var manPath = Path.Combine(imgDir, "manifest.json");
        if (File.Exists(manPath))
        {
            try
            {
                await using var stream = File.OpenRead(manPath);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("images", out var imgs) &&
                    imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in imgs.EnumerateArray())
                    {
                        var page = row.TryGetProperty("page", out var p) && p.TryGetInt32(out var pn) ? pn : 0;
                        if (page <= 0) continue;
                        var rel = row.TryGetProperty("path", out var pr) ? pr.GetString() ?? "" : "";
                        var full = Path.IsPathRooted(rel)
                            ? rel
                            : Path.Combine(sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(full))
                            full = Path.Combine(imgDir, Path.GetFileName(rel));
                        if (!File.Exists(full)) continue;
                        var kind = row.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                        byPage.TryGetValue(page, out var slot);
                        if (kind == EmbeddedKind)
                            slot.Emb = full;
                        else
                            slot.Ren ??= full;
                        byPage[page] = slot;
                    }
                }
            }
            catch { /* fall through */ }
        }

        if (byPage.Count == 0 && Directory.Exists(imgDir))
        {
            foreach (var fi in new DirectoryInfo(imgDir).EnumerateFiles())
            {
                var name = fi.Name;
                var f = fi.FullName;
                var m = EmbeddedPageNumRegex.Match(name);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var page))
                {
                    byPage.TryGetValue(page, out var slot);
                    slot.Emb = f;
                    byPage[page] = slot;
                    continue;
                }
                m = RenderedPageNumRegex.Match(name);
                if (m.Success && int.TryParse(m.Groups[1].Value, out page))
                {
                    byPage.TryGetValue(page, out var slot);
                    slot.Ren ??= f;
                    byPage[page] = slot;
                }
            }
        }

        // Prefer full-page renders for vision OCR; fall back to embeds
        return byPage
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value.Ren ?? kv.Value.Emb!))
            .Where(t => !string.IsNullOrEmpty(t.Item2) && File.Exists(t.Item2))
            .ToList()!;
    }

    private static async Task WriteExtractMetaAsync(
        string sourceDir,
        BookPrepareResult result,
        BookTextAnalysis analysis,
        BookStrategy strategy,
        string? visionModel,
        CancellationToken ct = default)
    {
        var meta = new Dictionary<string, object?>
        {
            ["schema_version"] = "extract_meta.v1",
            ["prepared_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["pdf"] = result.PdfName,
            ["text_engine"] = result.TextEngine,
            ["pages"] = result.Pages,
            ["text_chars"] = analysis.TextChars,
            ["text_words"] = analysis.TextWords,
            ["text_quality"] = analysis.TextQuality.ToApiString(),
            ["book_kind"] = analysis.BookKind.ToApiString(),
            // Initial film medium from import analysis (refined at screenplay adaptation).
            ["visual_medium"] = analysis.BookKind == BookKind.PictureBook
                ? "illustrated_picture_book"
                : "photoreal_live_action",
            ["render_style_lock"] = analysis.BookKind == BookKind.PictureBook
                ? "STYLE LOCK: stylized animated children's picture-book look for ALL on-screen cast (animals and humans share the same medium) -- not photoreal, not live-action"
                : "STYLE LOCK: photoreal live-action continuity portrait — naturalistic face and wardrobe. NOT cartoon, NOT illustration, NOT anime",
            ["medium_source"] = "import_extract_meta",
            // natural/target/mode filled by FilmRuntime.ApplyNaturalToMetaDictionary below
            ["suggested_chunk_pages"] = analysis.SuggestedChunkPages,
            ["strategy"] = new Dictionary<string, object?>
            {
                ["action"] = strategy.Action,
                ["reason"] = strategy.Reason,
                ["ready_for_stage1"] = result.ReadyForStage1,
                ["needs_user"] = strategy.NeedsUser,
            },
            ["ready_for_stage1"] = result.ReadyForStage1,
            ["has_page_images"] = result.PageImageCount > 0,
            ["page_image_count"] = result.PageImageCount,
            ["auto_prepared"] = true,
            ["notes"] = analysis.Notes,
            ["analysis"] = new Dictionary<string, object?>
            {
                ["pages"] = analysis.Pages,
                ["text_chars"] = analysis.TextChars,
                ["text_words"] = analysis.TextWords,
                ["letter_ratio"] = analysis.LetterRatio,
                ["empty_page_ratio"] = analysis.EmptyPageRatio,
                ["sparse_page_ratio"] = analysis.SparsePageRatio,
                ["garbage_score"] = analysis.GarbageScore,
                ["text_quality"] = analysis.TextQuality.ToApiString(),
                ["text_density"] = analysis.TextDensity.ToApiString(),
                ["book_kind"] = analysis.BookKind.ToApiString(),
                ["ready_for_stage1"] = analysis.ReadyForStage1,
                ["suggested_total_minutes"] = analysis.SuggestedTotalMinutes,
                ["suggested_chunk_pages"] = analysis.SuggestedChunkPages,
                ["notes"] = analysis.Notes,
                ["text_source"] = analysis.TextEngine,
            },
            ["vision"] = result.TextEngine == GrokVisionEngine
                ? new Dictionary<string, object?>
                {
                    ["ran"] = true,
                    ["model"] = visionModel,
                    ["failed_pages"] = result.VisionFailedPages,
                }
                : null,
        };
        // Natural + default target from Adaptation minutes; FilmRuntime only fills storage keys.
        FilmRuntime.ApplyNaturalToMetaDictionary(meta, analysis.SuggestedTotalMinutes);
        var path = Path.Combine(sourceDir, "extract_meta.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(meta, JsonDefaults.Indented) + "\n",
            ct).ConfigureAwait(false);
    }

    private sealed class BookStrategy
    {
        public string Action { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool ReadyForStage1 { get; set; }
        public bool NeedsUser { get; set; }
    }
}

public sealed class BookPrepareResult
{
    public bool Ok { get; set; }
    public string ProjectId { get; set; } = "";
    public string? PdfName { get; set; }
    public bool HasXaiKey { get; set; }
    public int Pages { get; set; }
    public int TextWords { get; set; }
    public string? TextQuality { get; set; }
    public double GarbageScore { get; set; }
    public string? BookKind { get; set; }
    public string? TextEngine { get; set; }
    public string? Strategy { get; set; }
    public string? StrategyReason { get; set; }
    public bool ReadyForStage1 { get; set; }
    public int SuggestedTotalMinutes { get; set; }
    public int SuggestedChunkPages { get; set; }
    public int PageImageCount { get; set; }
    public int ImagesExtracted { get; set; }
    public int VisionPages { get; set; }
    public int VisionFailedPages { get; set; }
    public List<string> Notes { get; set; } = new();
}
