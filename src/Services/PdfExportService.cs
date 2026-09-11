using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownEditor2022.Services
{
    internal static class PdfExportService
    {
        /// <summary>
        /// Converts the given Markdown file to PDF by rendering its HTML in a hidden WebView2 instance
        /// and using the browser's built-in print-to-PDF capability.
        /// </summary>
        public static async Task ExportToPdfAsync(string markdownFile, string outputPdfPath)
        {
            if (string.IsNullOrWhiteSpace(markdownFile))
            {
                throw new ArgumentException("Markdown file path cannot be null or empty.", nameof(markdownFile));
            }

            if (string.IsNullOrWhiteSpace(outputPdfPath))
            {
                throw new ArgumentException("Output PDF path cannot be null or empty.", nameof(outputPdfPath));
            }

            string tempHtmlFile = null;
            Window hiddenWindow = null;

            try
            {
                // Build the HTML document from the markdown file
                string html = HtmlGenerationService.BuildHtmlDocument(markdownFile);
                html = AddRenderingAssets(html);

                // Write the HTML next to the markdown file
                // resources (images, stylesheets, …) correctly via the same base directory.
                string markdownDir = Path.GetDirectoryName(markdownFile);
                tempHtmlFile = Path.Combine(markdownDir, $".export-{Guid.NewGuid():N}.html");
                File.WriteAllText(tempHtmlFile, html, new System.Text.UTF8Encoding(true));

                // All WebView2 interaction must happen on the UI thread
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                Browser.EnsureNativeDllSearchPath();
                CoreWebView2Environment environment = await Browser.GetOrCreateWebView2EnvironmentAsync();

                // Create an off-screen 1×1 window — needed because WebView2 requires a visual tree
                hiddenWindow = new Window
                {
                    Width = 1,
                    Height = 1,
                    Left = -30000,
                    Top = -30000,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Title = string.Empty,
                    Opacity = 0
                };

                WebView2 webView = new WebView2();
                hiddenWindow.Content = webView;
                hiddenWindow.Show();

                await webView.EnsureCoreWebView2Async(environment);

                // Navigate to the temp HTML file and wait for navigation to complete
                TaskCompletionSource<bool> navigationCompleted = new TaskCompletionSource<bool>();

                void OnNavigationCompleted(object s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    webView.NavigationCompleted -= OnNavigationCompleted;
                    navigationCompleted.TrySetResult(e.IsSuccess);
                }

                webView.NavigationCompleted += OnNavigationCompleted;
                webView.CoreWebView2.Navigate(new Uri(tempHtmlFile).AbsoluteUri);

                bool navSuccess = await navigationCompleted.Task;
                if (!navSuccess)
                {
                    throw new InvalidOperationException("Failed to load the HTML content for PDF export.");
                }

                await WaitForRenderingAsync(webView.CoreWebView2);

                CoreWebView2PrintSettings printSettings = webView.CoreWebView2.Environment.CreatePrintSettings();
                printSettings.ShouldPrintBackgrounds = true;
                printSettings.ShouldPrintHeaderAndFooter = false;
                bool printSuccess = await webView.CoreWebView2.PrintToPdfAsync(outputPdfPath, printSettings);
                if (!printSuccess)
                {
                    throw new InvalidOperationException($"PDF export failed. The browser could not write to: {outputPdfPath}");
                }
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                hiddenWindow?.Close();

                if (tempHtmlFile != null && File.Exists(tempHtmlFile))
                {
                    try { File.Delete(tempHtmlFile); } catch { /* best-effort cleanup */ }
                }
            }
        }

        private static string AddRenderingAssets(string html)
        {
            string margin = Path.Combine(Path.GetDirectoryName(typeof(MarkdownEditor2022Package).Assembly.Location), "Margin");
            StringBuilder assets = new();
            assets.Append("<style>");
            assets.Append(ReadAsset(margin, "prism.css"));
            assets.Append("</style><script>window.MathJax={tex:{packages:{'[+]':['color']}},loader:{load:['[tex]/color']}};</script>");

            foreach (string file in new[] { "prism.js", "mermaid.min.js", "mathjax.js" })
            {
                string source = ReadAsset(margin, file);
                if (!string.IsNullOrEmpty(source))
                {
                    assets.Append("<script>").Append(source.Replace("</script", "<\\/script")).Append("</script>");
                }
            }

            assets.Append("<script>(async function(){try{if(window.Prism)Prism.highlightAll();if(window.mermaid){mermaid.initialize({securityLevel:'loose'});await Promise.resolve(mermaid.init(undefined,document.querySelectorAll('.mermaid')));}if(window.MathJax&&MathJax.typesetPromise){await MathJax.typesetPromise();}}catch(e){}window.__markdownEditorReady=true;})();</script>");
            return html.Replace("</head>", assets.ToString() + "</head>");
        }

        private static string ReadAsset(string folder, string fileName)
        {
            string path = Path.Combine(folder ?? string.Empty, fileName);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        private static async Task WaitForRenderingAsync(CoreWebView2 webView)
        {
            for (int i = 0; i < 100; i++)
            {
                string ready = await webView.ExecuteScriptAsync(
                    "document.readyState === 'complete' && window.__markdownEditorReady === true");
                if (string.Equals(ready, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("Timed out waiting for PDF preview rendering to complete.");
        }
    }
}
