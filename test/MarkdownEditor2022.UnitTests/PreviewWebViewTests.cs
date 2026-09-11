using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownEditor2022.UnitTests
{
    [TestClass]
    public class PreviewWebViewTests
    {
        private const string Sample = "<p id=\"stable\">Paragraph</p><pre><code class=\"language-javascript\">const value = 1;</code></pre><pre class=\"mermaid\">graph TD; A-->B;</pre><p class=\"math\">\\(x+1\\)</p>";
        private const string Container = "document.getElementById('___markdown-content___')";
        private const string MathCount = "Array.from(MathJax.startup.document.math).length";

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        [Timeout(90000)]
        public Task InitialLazyFeatures_WaitForMathStartup_AndRetainUnchangedNodes() => RunAsync(async page =>
        {
            await page.NavigateAsync(Sample);
            await page.AssertScriptAsync("!window.Prism && !window.mermaid && !window.MathJax",
                "Feature libraries must be loaded lazily by the real preview script.");
            await page.ScriptAsync(@"
                window.MathJax = { startup: { typeset: false, ready: function () {
                    window.__mathStartupWaiting = true;
                    window.__releaseMathStartup = function () { MathJax.startup.defaultReady(); };
                } } };");

            Stopwatch initial = Stopwatch.StartNew();
            await page.InitializeAsync(1);
            await page.UntilAsync("window.__mathStartupWaiting === true", "MathJax startup hook");
            await page.AssertScriptAsync("typeof MathJax.typesetPromise !== 'function'",
                "This test must exercise MathJax's delayed API installation, not an already ready instance.");
            await page.AssertScriptAsync("!!document.querySelector('code .token') && !!document.querySelector('.mermaid svg')",
                "Real Prism and Mermaid must render before the deliberately delayed MathJax startup.");
            Assert.IsFalse(page.HasCompletion(1), "Initialization acknowledged before MathJax startup finished.");
            await page.ScriptAsync("__releaseMathStartup()");
            await page.CompleteAsync(1);
            TestContext.WriteLine($"Initial Prism/Mermaid/MathJax render (including controlled startup delay): {initial.ElapsedMilliseconds} ms");
            await page.AssertScriptAsync("!!document.querySelector('.math mjx-container')", "Real MathJax did not typeset the initial math.");
            await page.AssertScriptAsync("['prism.js','mermaid.min.js','mathjax.js','color.js'].every(name => " +
                "performance.getEntriesByType('resource').some(entry => entry.name.endsWith('/' + name)))",
                "The actual locally mapped feature libraries, including the MathJax color extension, must load.");

            await page.ScriptAsync($"window.__retained = Array.from({Container}.children)");
            await page.UpdateAndCompleteAsync(Sample + "<p>Small edit</p>", 2);
            await page.AssertScriptAsync($"__retained.every((node, index) => node === {Container}.children[index])",
                "Unchanged paragraph, highlighted code, diagram and math blocks must retain DOM identity.");
            await page.AssertScriptAsync(MathCount + " === 1", "Retaining math must not register duplicate MathItems.");
            page.AssertNoErrors();
        });

        [TestMethod]
        [Timeout(90000)]
        public Task RepeatedMathReplacementAndRemoval_KeepMathItemsBounded() => RunAsync(async page =>
        {
            const string plain = "<p id=\"stable\">Paragraph</p>";
            await page.NavigateAsync(plain);
            await page.InitializeAsync(1);
            await page.CompleteAsync(1);
            int id = 2;
            for (int cycle = 0; cycle < 8; cycle++)
            {
                await page.UpdateAndCompleteAsync(plain + $"<p class=\"math\">\\(x+{cycle}\\)</p>", id++);
                await page.AssertScriptAsync("!!document.querySelector('.math mjx-container')", "Added math was not typeset.");
                int added = await page.NumberAsync(MathCount);
                Assert.AreEqual(1, added, $"MathItems leaked after addition {cycle}.");
                await page.UpdateAndCompleteAsync(plain, id++);
                int removed = await page.NumberAsync(MathCount);
                Assert.AreEqual(0, removed, $"MathItems must be cleared even when the new document has no math ({cycle}).");
                TestContext.WriteLine($"Math cycle {cycle}: registered after add={added}, after remove={removed}");
            }

            string document = Sample + string.Concat(Enumerable.Range(0, 150).Select(i => $"<p>Document paragraph {i}</p>")) +
                string.Concat(Enumerable.Range(0, 4).Select(i => $"<p class=\"math\">\\(y+{i}\\)</p>"));
            Stopwatch render = Stopwatch.StartNew();
            await page.UpdateAndCompleteAsync(document, id++);
            TestContext.WriteLine($"Document render (150 paragraphs, Prism, Mermaid, five math expressions): {render.ElapsedMilliseconds} ms");
            for (int edit = 0; edit < 5; edit++)
            {
                render.Restart();
                await page.UpdateAndCompleteAsync(document + $"<p>Warm small edit {edit}</p>", id++);
                int count = await page.NumberAsync(MathCount);
                TestContext.WriteLine($"Warm small edit {edit}: {render.ElapsedMilliseconds} ms; registered MathItems={count}");
                Assert.AreEqual(5, count, "Warm edits must not accumulate MathItems.");
            }
            page.AssertNoErrors();
        });

        [TestMethod]
        [Timeout(90000)]
        public Task SlowRender_BurstAndUndo_ConvergeWithoutWaitingForSupersededIds() => RunAsync(async page =>
        {
            await page.NavigateAsync(Sample);
            await page.InitializeAsync(1);
            await page.CompleteAsync(1);
            await page.ScriptAsync(@"
                window.__stable = document.getElementById('stable');
                window.__diagramCalls = 0;
                const originalRun = mermaid.run.bind(mermaid);
                mermaid.run = function (options) {
                    if (++window.__diagramCalls !== 1) return originalRun(options);
                    window.__renderBlocked = true;
                    return new Promise(resolve => { window.__releaseRender = resolve; })
                        .then(() => originalRun(options));
                };");
            await page.UpdateAsync(Sample.Replace("A-->B", "A-->Slow").Replace("x+1", "x+2"), 2);
            await page.UntilAsync("window.__renderBlocked === true", "controlled Mermaid render");
            using (CancellationTokenSource canceledHostWait = new())
            {
                Task waiter = page.CompleteAsync(2, canceledHostWait.Token);
                canceledHostWait.Cancel();
                try
                {
                    await waiter;
                    Assert.Fail("The simulated canceled host waiter unexpectedly completed.");
                }
                catch (OperationCanceledException)
                {
                }
            }
            Assert.IsFalse(page.HasCompletion(2), "Mermaid's blocked request must not be acknowledged early.");

            for (int id = 3; id < 10; id++)
                await page.UpdateAsync(Sample.Replace("A-->B", $"A-->Burst{id}").Replace("x+1", $"x+{id}"), id);
            // Undo to the original document while the earlier render is still in flight.
            await page.UpdateAsync(Sample, 10);
            Assert.IsFalse(page.HasCompletion(10), "Queued work must not acknowledge before actual rendering.");
            for (int id = 3; id < 10; id++)
                Assert.IsFalse(page.HasCompletion(id), "A queued, superseded request must not block the final waiter.");
            await page.ScriptAsync("__releaseRender()");
            await page.CompleteAsync(10);

            await page.AssertScriptAsync("document.getElementById('stable') === __stable", "Burst edits replaced an unchanged block.");
            await page.AssertScriptAsync("!!document.querySelector('code .token') && !!document.querySelector('.mermaid svg') && " +
                "!!document.querySelector('.math mjx-container')", "The final queued document must finish all real feature rendering.");
            await page.AssertScriptAsync($"{Container}.children.length === 4 && !{Container}.textContent.includes('Slow') && " +
                $"!{Container}.textContent.includes('Burst') && document.querySelector('.mermaid svg').textContent.includes('B')",
                "A stale render overwrote the final undo.");
            Assert.AreEqual(2, await page.NumberAsync("__diagramCalls"), "Only the in-flight and final coalesced diagrams should render.");
            Assert.AreEqual(1, await page.NumberAsync(MathCount), "Slow render convergence leaked stale math.");
            await page.UpdateAndCompleteAsync(Sample + "<p>Still responsive</p>", 11);
            page.AssertNoErrors();
        });

        private async Task RunAsync(Func<PreviewPage, Task> test)
        {
            TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(65));
            Thread thread = new(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    PreviewPage page = new(deadline.Token);
                    Exception? failure = null;
                    try
                    {
                        await page.OpenAsync();
                        await test(page);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    finally
                    {
                        try { await page.CloseAsync(); }
                        catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
                        if (failure == null) finished.TrySetResult(true);
                        else finished.TrySetException(failure);
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    }
                }));
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = nameof(PreviewWebViewTests)
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(82))) != finished.Task)
            {
                deadline.Cancel();
                Assert.Fail("WebView2 integration operation exceeded 82 seconds; the background STA did not finish cleanup.");
            }
            try { await finished.Task; }
            finally
            {
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(2)), "WebView2 STA dispatcher did not shut down.");
            }
        }

        private sealed class PreviewPage
        {
            private readonly CancellationToken _deadline;
            private readonly List<string> _messages = [];
            private readonly string _userData = Path.Combine(AppContext.BaseDirectory, ".webview-tests", Guid.NewGuid().ToString("N"));
            private readonly TaskCompletionSource<bool> _browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private WebView2? _view;
            private Window? _window;
            private CoreWebView2Environment? _environment;
            private bool _browserStarted;

            internal PreviewPage(CancellationToken deadline) => _deadline = deadline;

            internal async Task OpenAsync()
            {
                foreach (string asset in new[] { "preview-content.js", "prism.js", "mermaid.min.js", "mathjax.js", "color.js" })
                    Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "Margin", asset)), $"Missing copied WebView2 test asset: Margin\\{asset}");
                try
                {
                    CoreWebView2Environment.GetAvailableBrowserVersionString();
                }
                catch (WebView2RuntimeNotFoundException error)
                {
                    throw new AssertFailedException("Real preview tests require the Microsoft Edge WebView2 Evergreen Runtime. Install it on this Windows test machine.", error);
                }
                Directory.CreateDirectory(_userData);
                _view = new WebView2();
                _window = new Window
                {
                    Width = 900, Height = 700, Left = -20000, Top = -20000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    Content = _view
                };
                _window.Show();
                _environment = await WithinAsync(
                    CoreWebView2Environment.CreateAsync(userDataFolder: _userData), "create isolated WebView2 environment", 25);
                _environment.BrowserProcessExited += (_, _) => _browserExited.TrySetResult(true);
                await WithinAsync(_view.EnsureCoreWebView2Async(_environment), "initialize WebView2", 25);
                _browserStarted = true;
                _view.CoreWebView2.SetVirtualHostNameToFolderMapping("markdown-editor-host",
                    AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
                _view.CoreWebView2.WebMessageReceived += (_, args) => _messages.Add(args.TryGetWebMessageAsString());
            }

            internal async Task NavigateAsync(string html)
            {
                TaskCompletionSource<bool> navigated = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNavigation(object? sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    if (args.IsSuccess) navigated.TrySetResult(true);
                    else navigated.TrySetException(new AssertFailedException($"Preview navigation failed: {args.WebErrorStatus}"));
                }
                _view!.CoreWebView2.NavigationCompleted += OnNavigation;
                try
                {
                    _view.NavigateToString("<!doctype html><html><head><meta charset=\"utf-8\"></head><body>" +
                        "<div id=\"___markdown-content___\">" + html + "</div>" +
                        "<script src=\"http://markdown-editor-host/margin/preview-content.js\"></script></body></html>");
                    await WithinAsync(navigated.Task, "navigate to preview");
                    await AssertScriptAsync("typeof __initializeMarkdownPreview === 'function' && typeof __updateMarkdownPreview === 'function'",
                        "The native preview-content.js entry points were not installed.");
                }
                finally { _view.CoreWebView2.NavigationCompleted -= OnNavigation; }
            }

            internal Task InitializeAsync(int id) => AssertScriptAsync($"__initializeMarkdownPreview('default', {id}) === true", "Initialize must immediately accept the request.");

            internal Task UpdateAsync(string html, int id)
            {
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
                return AssertScriptAsync($"__updateMarkdownPreview(new TextDecoder().decode(Uint8Array.from(atob('{encoded}'), c => c.charCodeAt(0))), 'default', {id}) === true",
                    "Update must immediately accept the request.");
            }

            internal async Task UpdateAndCompleteAsync(string html, int id)
            {
                await UpdateAsync(html, id);
                await CompleteAsync(id);
            }

            internal bool HasCompletion(int id) => _messages.Contains("previewComplete:" + id);

            internal async Task CompleteAsync(int id, CancellationToken cancellation = default)
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                while (!HasCompletion(id))
                {
                    cancellation.ThrowIfCancellationRequested();
                    _deadline.ThrowIfCancellationRequested();
                    if (_messages.Contains("previewFailed:" + id))
                        Assert.Fail($"Preview request {id} failed: {string.Join("; ", _messages)}");
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(25))
                        Assert.Fail($"Preview request {id} timed out. Messages: {string.Join("; ", _messages)}");
                    await Task.Delay(20, cancellation);
                }
            }

            internal Task<string> ScriptAsync(string script) =>
                WithinAsync(_view!.ExecuteScriptAsync(script), "execute preview script", 10);

            internal async Task AssertScriptAsync(string script, string message) =>
                Assert.AreEqual("true", await ScriptAsync(script), message);

            internal async Task<int> NumberAsync(string script) =>
                int.Parse(await ScriptAsync(script), CultureInfo.InvariantCulture);

            internal async Task UntilAsync(string condition, string description)
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                while (await ScriptAsync(condition) != "true")
                {
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(25))
                        Assert.Fail($"Timed out waiting for {description}. Messages: {string.Join("; ", _messages)}");
                    await Task.Delay(20, _deadline);
                }
            }

            internal void AssertNoErrors() =>
                Assert.IsFalse(_messages.Any(message => message.StartsWith("previewError:", StringComparison.Ordinal) ||
                    message.StartsWith("previewFailed:", StringComparison.Ordinal)), string.Join("; ", _messages));

            private async Task WithinAsync(Task operation, string description, int seconds = 20)
            {
                if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(seconds), _deadline)) != operation)
                {
                    _deadline.ThrowIfCancellationRequested();
                    throw new TimeoutException($"Timed out trying to {description}.");
                }
                await operation;
            }

            private async Task<T> WithinAsync<T>(Task<T> operation, string description, int seconds = 20)
            {
                await WithinAsync((Task)operation, description, seconds);
                return await operation;
            }

            internal async Task CloseAsync()
            {
                try
                {
                    // Initialization may time out after launching a process but before returning a controller.
                    if (_environment != null)
                        _browserStarted |= _environment.GetProcessInfos().Count != 0;
                }
                finally
                {
                    try { _view?.Dispose(); }
                    finally { _window?.Close(); }
                }
                if (_browserStarted &&
                    await Task.WhenAny(_browserExited.Task, Task.Delay(TimeSpan.FromSeconds(10))) != _browserExited.Task)
                    throw new TimeoutException($"The isolated WebView2 browser did not exit; its profile was left intact at {_userData}.");
                Stopwatch cleanup = Stopwatch.StartNew();
                while (Directory.Exists(_userData))
                {
                    try { Directory.Delete(_userData, recursive: true); }
                    catch (IOException) when (cleanup.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(100); }
                    catch (UnauthorizedAccessException) when (cleanup.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(100); }
                }
            }
        }
    }
}
