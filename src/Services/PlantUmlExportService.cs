using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownEditor2022.Services
{
    internal static class PlantUmlExportService
    {
        internal static bool ContainsDiagrams(string html) =>
            html.IndexOf("class=\"plantuml\"", StringComparison.Ordinal) >= 0;

        internal static async Task<string> RenderAsync(string html, string theme)
        {
            if (!ContainsDiagrams(html))
            {
                return html;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Browser.EnsureNativeDllSearchPath();
            WebView2 view = new();
            Window window = new()
            {
                Width = 1, Height = 1, Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                Content = view, Opacity = 0
            };
            try
            {
                window.Show();
                Task<CoreWebView2Environment> environmentTask = Browser.GetOrCreateWebView2EnvironmentAsync();
                await PdfExportService.WaitForCompletionAsync(environmentTask, TimeSpan.FromSeconds(30),
                    "Timed out creating the PlantUML export browser.");
                CoreWebView2Environment environment = await environmentTask;
                await PdfExportService.WaitForCompletionAsync(view.EnsureCoreWebView2Async(environment),
                    TimeSpan.FromSeconds(30), "Timed out initializing PlantUML export rendering.");
                string folder = Path.GetDirectoryName(typeof(PlantUmlExportService).Assembly.Location);
                view.CoreWebView2.SetVirtualHostNameToFolderMapping("markdown-editor-host", folder,
                    CoreWebView2HostResourceAccessKind.Allow);
                return await RenderInBrowserAsync(view.CoreWebView2, html, theme);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                view.Dispose();
                window.Close();
            }
        }

        internal static async Task<string> RenderInBrowserAsync(CoreWebView2 browser, string html, string theme)
        {
            TaskCompletionSource<bool> navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigation(object sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (args.IsSuccess) navigation.TrySetResult(true);
                else navigation.TrySetException(new InvalidOperationException("Failed to load PlantUML export renderer: " + args.WebErrorStatus));
            }
            void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
            {
                string message = args.TryGetWebMessageAsString();
                if (message.StartsWith("plantumlExportComplete:", StringComparison.Ordinal))
                    rendered.TrySetResult(message.Substring("plantumlExportComplete:".Length));
                else if (message.StartsWith("plantumlExportError:", StringComparison.Ordinal))
                    rendered.TrySetException(new InvalidOperationException(message.Substring("plantumlExportError:".Length)));
            }
            browser.NavigationCompleted += OnNavigation;
            browser.WebMessageReceived += OnMessage;
            try
            {
                browser.NavigateToString("<!doctype html><html><head><meta charset=\"utf-8\">" +
                    "<script src=\"http://markdown-editor-host/margin/plantuml-preview.js\"></script></head><body></body></html>");
                await PdfExportService.WaitForCompletionAsync(navigation.Task, TimeSpan.FromSeconds(30),
                    "Timed out loading PlantUML export renderer.");
                // Parse the user's template in an inert document; its scripts must not run in the renderer.
                string script = "(async function(){try{" +
                    $"var doc=new DOMParser().parseFromString(\"{Browser.EscapeForJavaScript(html)}\",'text/html');" +
                    $"await MarkdownPlantUml.render(Array.from(doc.querySelectorAll('.plantuml')), \"{Browser.EscapeForJavaScript(theme)}\");" +
                    "window.chrome.webview.postMessage('plantumlExportComplete:<!doctype html>\\n'+doc.documentElement.outerHTML);" +
                    "}catch(error){window.chrome.webview.postMessage('plantumlExportError:'+error.message);}})();";
                await browser.ExecuteScriptAsync(script);
                await PdfExportService.WaitForCompletionAsync(rendered.Task, TimeSpan.FromSeconds(30),
                    "Timed out rendering PlantUML export diagrams.");
                return await rendered.Task;
            }
            finally
            {
                browser.NavigationCompleted -= OnNavigation;
                browser.WebMessageReceived -= OnMessage;
            }
        }
    }
}
