using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Web.WebView2.Core;

namespace AutomationPlatform.Presentation.Services;

public sealed record PeerPdfSection(string Heading, string[] Paragraphs);

/// <summary>Prints plain-text assignment content without navigating the learner's course WebView.</summary>
public static class PeerPdfDocumentRenderer
{
    private const int MaximumDocumentCharacters = 200_000;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(90);
    private static readonly IntPtr MessageOnlyWindow = new(-3); // HWND_MESSAGE: cannot become visible.

    public static string BuildHtml(string title, IReadOnlyList<PeerPdfSection> sections, double fontSize = 11)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(sections);
        if (!double.IsFinite(fontSize) || fontSize is < 9.5 or > 13) throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (title.Length > 500 || sections.Count is < 1 or > 80)
            throw new ArgumentException("The PDF title or section count is outside the supported limit.");

        var body = new StringBuilder();
        body.Append("<main><h1>").Append(Encode(title)).Append("</h1>");
        var characterCount = title.Length;
        foreach (PeerPdfSection section in sections)
        {
            if (section is null || section.Heading is null || section.Heading.Length > 500
                || section.Paragraphs is null || section.Paragraphs.Length is < 1 or > 100)
                throw new ArgumentException("Each PDF section must contain a heading and paragraphs.");

            characterCount += section.Heading.Length;
            body.Append("<section>");
            if (!string.IsNullOrWhiteSpace(section.Heading))
                body.Append("<h2>").Append(Encode(section.Heading)).Append("</h2>");
            foreach (string paragraph in section.Paragraphs)
            {
                if (string.IsNullOrWhiteSpace(paragraph) || paragraph.Length > 16_000)
                    throw new ArgumentException("A PDF paragraph is empty or exceeds the supported limit.");
                characterCount += paragraph.Length;
                if (characterCount > MaximumDocumentCharacters)
                    throw new ArgumentException("The PDF content exceeds the supported document size.");
                body.Append("<p>").Append(Encode(paragraph)).Append("</p>");
            }
            body.Append("</section>");
        }
        body.Append("</main>");

        // Only text enters this template. No model-generated markup, scripts, links, fonts or images.
        string html = """
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src 'none'; font-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
            <meta name="referrer" content="no-referrer">
            <title>
            """ + Encode(title) + """
            </title>
            <style>
            @page { size: A4 portrait; margin: 20mm; }
            * { box-sizing: border-box; }
            html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
            body { margin: 0; color: #202b3b; background: #fff; font: 11pt/1.55 "Segoe UI", Arial, sans-serif; }
            main { width: 100%; }
            h1 { margin: 0 0 20pt; padding-bottom: 12pt; border-bottom: 1.5pt solid #294b70; font-size: 21pt; line-height: 1.22; font-weight: 650; }
            h2 { margin: 17pt 0 7pt; color: #294b70; font-size: 13pt; line-height: 1.3; font-weight: 650; }
            h1, h2 { break-after: avoid; page-break-after: avoid; overflow-wrap: anywhere; }
            p { margin: 0 0 9pt; white-space: pre-wrap; overflow-wrap: anywhere; orphans: 3; widows: 3; }
            section:first-of-type h2 { margin-top: 0; }
            </style></head><body>
            """ + body + "</body></html>";
        return html.Replace("font: 11pt/1.55", "font: " + fontSize.ToString("0.##", CultureInfo.InvariantCulture) + "pt/1.55", StringComparison.Ordinal);
    }

    /// <remarks>
    /// Invoke on the WebView environment's STA/UI thread. The parent handle is accepted for the
    /// caller's existing window contract; HWND_MESSAGE is deliberately used instead so printing
    /// neither creates a visible window nor changes the course window's children or navigation.
    /// The caller owns assignment/cache identity; this method never overwrites an existing PDF.
    /// </remarks>
    public static async Task RenderAsync(CoreWebView2Environment environment, IntPtr parentWindow,
        string outputPath, string title, IReadOnlyList<PeerPdfSection> sections, CancellationToken ct,
        int? minimumPages = null, int? maximumPages = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ct.ThrowIfCancellationRequested();
        if (minimumPages is < 1 or > 30 || maximumPages is < 1 or > 30 || minimumPages > maximumPages)
            throw new ArgumentOutOfRangeException(nameof(minimumPages));
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || SynchronizationContext.Current is null)
            throw new InvalidOperationException("PDF rendering must run on the WebView UI thread.");
        if (!Path.IsPathFullyQualified(outputPath) || !string.Equals(Path.GetExtension(outputPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The PDF output must be an absolute .pdf path.", nameof(outputPath));

        _ = parentWindow;
        string html = BuildHtml(title, sections);
        string finalPath = Path.GetFullPath(outputPath);
        if (File.Exists(finalPath))
            throw new IOException("An existing PDF cannot be overwritten. Reuse or explicitly invalidate its cache first.");
        string parentDirectory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(parentDirectory);
        string temporaryPath = Path.Combine(parentDirectory, $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.partial.pdf");
        using (new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RenderTimeout);

        CoreWebView2Controller? controller = null;
        Task<CoreWebView2Controller>? creation = null;
        Task<bool>? printing = null;
        bool committed = false;
        try
        {
            CoreWebView2ControllerOptions options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "AcosePeerPdfRenderer";
            options.IsInPrivateModeEnabled = true;
            creation = environment.CreateCoreWebView2ControllerAsync(MessageOnlyWindow, options);
            controller = await creation.WaitAsync(deadline.Token);
            controller.IsVisible = false;
            controller.Bounds = new Rectangle(0, 0, 794, 1123);
            CoreWebView2 view = controller.CoreWebView2;
            view.Settings.IsScriptEnabled = false;
            view.Settings.IsWebMessageEnabled = false;
            view.Settings.AreHostObjectsAllowed = false;
            view.Settings.AreDevToolsEnabled = false;
            view.Settings.AreDefaultContextMenusEnabled = false;
            view.Settings.AreDefaultScriptDialogsEnabled = false;
            view.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // Defense in depth: even if the template changes later, rendering cannot fetch a URL.
            view.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            view.WebResourceRequested += (_, e) =>
                e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Length: 0");
            string ownedHtmlUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            view.NavigationStarting += (_, e) =>
            {
                // Some WebView2 runtimes expose NavigateToString as this exact data URI.
                // Never permit arbitrary data documents or a network URL.
                e.Cancel = !string.Equals(e.Uri, "about:blank", StringComparison.Ordinal) &&
                    !string.Equals(e.Uri, ownedHtmlUri, StringComparison.Ordinal);
            };
            view.FrameNavigationStarting += (_, e) => e.Cancel = true;
            view.NewWindowRequested += (_, e) => e.Handled = true;
            view.DownloadStarting += (_, e) => e.Cancel = true;
            view.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;

            CoreWebView2PrintSettings settings = environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.PageWidth = 210d / 25.4d;
            settings.PageHeight = 297d / 25.4d;
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 20d / 25.4d;
            settings.ScaleFactor = 1;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintSelectionOnly = false;
            settings.ShouldPrintHeaderAndFooter = false;
            double fontSize = 11;
            for (int layoutAttempt = 0; ; layoutAttempt++)
            {
                html = BuildHtml(title, sections, fontSize);
                ownedHtmlUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object? sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (e.IsSuccess) loaded.TrySetResult();
                    else loaded.TrySetException(new IOException($"The PDF layout did not load ({e.WebErrorStatus})."));
                }
                view.NavigationCompleted += OnLoaded;
                try { view.NavigateToString(html); await loaded.Task.WaitAsync(deadline.Token); }
                finally { view.NavigationCompleted -= OnLoaded; }
                string layout = await view.ExecuteScriptAsync("document.fonts.status === 'loaded' && document.body.getBoundingClientRect().height > 0")
                    .WaitAsync(deadline.Token);
                if (!string.Equals(layout, "true", StringComparison.Ordinal))
                    throw new IOException("The PDF text or fonts are not ready for printing.");
                printing = view.PrintToPdfAsync(temporaryPath, settings);
                if (!await printing.WaitAsync(deadline.Token))
                    throw new IOException("WebView could not finish writing the PDF.");
                deadline.Token.ThrowIfCancellationRequested();
                ValidatePdf(temporaryPath);
                if (minimumPages is null && maximumPages is null) break;
                int pages = CountRenderedPages(temporaryPath);
                if ((minimumPages is null || pages >= minimumPages) && (maximumPages is null || pages <= maximumPages)) break;
                if (layoutAttempt >= 2)
                    throw new InvalidDataException($"PDF có {pages} trang, chưa khớp yêu cầu {minimumPages?.ToString() ?? "1"}–{maximumPages?.ToString() ?? "30"} trang; bản nháp được giữ lại.");
                int target = minimumPages.HasValue && pages < minimumPages ? minimumPages.Value : maximumPages!.Value;
                double adjusted = Math.Clamp(Math.Round(fontSize * Math.Sqrt(target / (double)pages) * (pages < target ? 1.02 : .98), 2), 9.5, 13);
                if (Math.Abs(adjusted - fontSize) < .01) throw new InvalidDataException("Nội dung không khớp số trang ở cỡ chữ dễ đọc; cần chỉnh độ dài bản thảo.");
                fontSize = adjusted;
            }
            File.Move(temporaryPath, finalPath, overwrite: false);
            committed = true;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("PDF rendering exceeded its 90-second deadline.", ex);
        }
        finally
        {
            if (controller is not null) CloseController(controller);
            else if (creation is not null) _ = CloseLateControllerAsync(creation);
            if (!committed)
            {
                DeleteOwnedTemporaryFile(temporaryPath);
                // A canceled wait does not cancel the native print. Observe its completion and
                // remove only our unique temporary file if it appears after controller.Close().
                if (printing is not null) _ = CleanLatePrintAsync(printing, temporaryPath);
            }
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n').Replace('\u2011', '-').Replace('\u2013', '-').Replace('\u2014', '-'));

    // Only used on our own freshly printed Chromium PDF, not as a general PDF parser.
    public static int CountRenderedPages(string path)
    {
        if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("Rendered PDF is too large.");
        string content = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        int pages = Regex.Matches(content, @"/Type\s*/Page(?=[\s/>])", RegexOptions.CultureInvariant).Count;
        if (pages is < 1 or > 500) throw new InvalidDataException("Cannot verify the rendered PDF page count.");
        return pages;
    }

    private static void ValidatePdf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 1_024) throw new IOException("The rendered PDF is empty or incomplete.");
        Span<byte> signature = stackalloc byte[5];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual("%PDF-"u8)) throw new IOException("The rendered file is not a PDF.");
        stream.Seek(-Math.Min(1_024, stream.Length), SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        if (!reader.ReadToEnd().Contains("%%EOF", StringComparison.Ordinal))
            throw new IOException("The rendered PDF did not finish writing.");
    }

    private static async Task CloseLateControllerAsync(Task<CoreWebView2Controller> pending)
    {
        try { CloseController(await pending); }
        catch { /* Native creation may itself have failed after cancellation. */ }
    }

    private static void CloseController(CoreWebView2Controller controller)
    {
        try { controller.Close(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

    private static async Task CleanLatePrintAsync(Task<bool> pending, string path)
    {
        try { await pending; }
        catch { /* The native print can fail when its controller is closed. */ }
        finally { DeleteOwnedTemporaryFile(path); }
    }

    private static void DeleteOwnedTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
