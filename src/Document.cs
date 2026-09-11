using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownEditor2022.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;

namespace MarkdownEditor2022
{
    public class Document : IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly SemaphoreSlim _parseSemaphore = new(1, 1);
        private CancellationTokenSource _parseCts = new();
        private readonly CancellationTokenSource _disposalTokenSource = new();
        private readonly TaskCompletionSource<bool> _initialParseCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _parseCancellationLock = new();
        private bool _isDisposed;
        private string _lastParsedText;
        private int _lastParsedVersion;

        public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)  // Must be BEFORE UseAdvancedExtensions to override default
            .UseAdvancedExtensions()
            .UseTocToken()  // Support for [[_TOC_]] Azure DevOps wiki syntax
            .UsePragmaLines()
            .UsePreciseSourceLocation()
            .UseYamlFrontMatter()
            .UseEmojiAndSmiley(enableSmileys: false)
            .Build();

        public static MarkdownPipeline PipelineToGenerateHtml { get; } = new MarkdownPipelineBuilder()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)  // Must be BEFORE UseAdvancedExtensions to override default
            .UseAdvancedExtensions()
            .UseTocToken()  // Support for [[_TOC_]] Azure DevOps wiki syntax
            .UseYamlFrontMatter()
            .UseEmojiAndSmiley(enableSmileys: false)
            .Build();

        // Compiled regex for better performance
        // Converts Azure DevOps triple-colon syntax to standard fenced code blocks
        // Handles both ":::mermaid" and "::: mermaid" (with space) formats
        // Uses a MatchEvaluator to exclude markdown alerts like "::: note"
        private static readonly Regex _colonFixRegex = new(@"^(:::) ?(\w*)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly HashSet<string> _alertKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "note", "tip", "important", "caution", "warning"
        };

        public Document(ITextBuffer buffer)
        {
            _buffer = buffer;
            _buffer.Changed += BufferChanged;
            FileName = buffer.GetFileName();

            ParseAsync().FireAndForget();
            AdvancedOptions.Saved += AdvancedOptionsSaved;
        }

        public MarkdownDocument Markdown { get; private set; }

        public string FileName { get; }

        public bool IsParsing { get; private set; }

        public DocumentAnalysis Analysis { get; private set; }

        /// <summary>
        /// Waits for the initial parse to complete. Returns immediately if already parsed.
        /// </summary>
        public Task WaitForInitialParseAsync(CancellationToken cancellationToken = default)
        {
            if (Markdown != null)
            {
                return Task.CompletedTask;
            }

            return _initialParseCompletionSource.Task.WithCancellation(cancellationToken);
        }

        private void BufferChanged(object sender, TextContentChangedEventArgs e)
        {
            ParseAsync().FireAndForget();
        }

        private async Task ParseAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            // Cancel any in-flight parse (we will ignore its result if already running).
            // Keep cancelled sources undisposed until their parse has exited to avoid racing
            // token access during rapid edits or disposal.
            CancellationTokenSource localCts;
            CancellationToken localToken;
            lock (_parseCancellationLock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _parseCts?.Cancel();
                localCts = new CancellationTokenSource();
                _parseCts = localCts;
                localToken = localCts.Token;
            }

            // Use semaphore to prevent multiple concurrent parsing operations.
            // Always wait for the semaphore so parse requests are queued instead of dropped,
            // which can otherwise leave initial parsing incomplete.
            try
            {
                using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _disposalTokenSource.Token,
                    localToken);
                await _parseSemaphore.WaitAsync(waitCts.Token);
            }
            catch (OperationCanceledException)
            {
                ReleaseParseCancellationSource(localCts);
                return;
            }

            bool semaphoreAcquired = false;

            try
            {
                semaphoreAcquired = true;
                IsParsing = true;
                bool success = false;

                try
                {
                    await TaskScheduler.Default; // move to a background thread

                    if (localToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Capture the snapshot and its version after switching threads so the
                    // version corresponds to the exact text being parsed.
                    ITextSnapshot snapshot = _buffer.CurrentSnapshot;
                    int snapshotVersion = snapshot.Version.VersionNumber;
                    string text = snapshot.GetText();

                    // Skip parsing if text hasn't changed based on snapshot version & content
                    if (snapshotVersion == _lastParsedVersion && string.Equals(text, _lastParsedText, System.StringComparison.Ordinal))
                    {
                        success = true; // treat as success so consumers can continue
                        return;
                    }

                    // This fixes this bug: https://github.com/madskristensen/MarkdownEditor2022/issues/128
                    // Also supports ::: mermaid syntax (with space): https://github.com/madskristensen/MarkdownEditor2022/issues/170
                    text = _colonFixRegex.Replace(text, ColonFixEvaluator);

                    MarkdownDocument md = Markdig.Markdown.Parse(text, Pipeline);

                    if (localToken.IsCancellationRequested)
                    {
                        return; // abandon
                    }

                    // Build analysis (single pass over descendants)
                    DocumentAnalysis analysis = BuildAnalysis(md);

                    // Only publish results if the snapshot hasn't advanced further
                    if (_buffer.CurrentSnapshot.Version.VersionNumber != snapshotVersion)
                    {
                        return; // stale result
                    }

                    Markdown = md;
                    Analysis = analysis;
                    _lastParsedText = text;
                    _lastParsedVersion = snapshotVersion;
                    success = true;
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
                finally
                {
                    IsParsing = false;

                    if (!localToken.IsCancellationRequested)
                    {
                        // Complete the initial wait even when parsing fails; consumers can inspect Markdown.
                        _initialParseCompletionSource.TrySetResult(true);

                        if (success)
                        {
                            Parsed?.Invoke(this);
                        }
                    }
                }
            }
            finally
            {
                if (semaphoreAcquired)
                {
                    _parseSemaphore.Release();
                }

                ReleaseParseCancellationSource(localCts);
            }
        }

        private void ReleaseParseCancellationSource(CancellationTokenSource localCts)
        {
            lock (_parseCancellationLock)
            {
                if (ReferenceEquals(_parseCts, localCts))
                {
                    _parseCts = null;
                }
            }

            localCts.Dispose();
        }

        private static DocumentAnalysis BuildAnalysis(MarkdownDocument md)
        {
            List<HeadingBlock> headings = [];
            List<HtmlBlock> htmlComments = [];
            List<Table> tables = [];

            foreach (MarkdownObject obj in md.Descendants())
            {
                if (obj is HeadingBlock hb)
                {
                    headings.Add(hb);
                }
                else if (obj is HtmlBlock hb2 && hb2.Type == HtmlBlockType.Comment)
                {
                    htmlComments.Add(hb2);
                }
                else if (obj is Table table)
                {
                    tables.Add(table);
                }
            }

            return new DocumentAnalysis(headings, htmlComments, tables);
        }

        /// <summary>
        /// Converts Azure DevOps triple-colon syntax to standard fenced code blocks.
        /// Preserves alert syntax (note, tip, important, caution, warning).
        /// </summary>
        private static string ColonFixEvaluator(Match match)
        {
            string keyword = match.Groups[2].Value;

            // If keyword is an alert type, preserve original text
            if (_alertKeywords.Contains(keyword))
            {
                return match.Value;
            }

            // Convert ::: or ::: <keyword> to ``` or ```<keyword>
            return "```" + keyword;
        }

        private void AdvancedOptionsSaved(AdvancedOptions obj)
        {
            ParseAsync().FireAndForget();
        }

        public void Dispose()
        {
            lock (_parseCancellationLock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _parseCts?.Cancel();
            }

            _buffer.Changed -= BufferChanged;
            AdvancedOptions.Saved -= AdvancedOptionsSaved;
            _disposalTokenSource.Cancel();
            // The source may still be observed by an in-flight WaitAsync continuation.
            // Let it be reclaimed with the document instead of disposing it during teardown.
            _initialParseCompletionSource.TrySetCanceled();
        }

        public event Action<Document> Parsed;
    }
}
