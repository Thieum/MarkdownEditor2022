using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EnvDTE;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using MarkdownEditor2022.Services;

namespace MarkdownEditor2022
{
    internal static class HtmlGenerationService
    {
        private const string _htmlTemplateFileName = "md-template.html";
        private const string _htmlExtension = ".html";
        private static readonly object _generationLock = new();
        private static readonly Dictionary<string, object> _generations = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex _anchorHrefRegex = new(
            @"(?<prefix><a\b[^>]*\bhref\s*=\s*"")(?<url>[^""]+)(?<suffix>"")",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool HtmlGenerationEnabled(string markdownFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string htmlFile = GetHtmlFileName(markdownFile);
            if (!File.Exists(htmlFile))
            {
                return false;
            }

            // Only consider generation "enabled" if the HTML file is nested
            // under the markdown item in the project (i.e., this extension
            // created it). A pre-existing, unlinked HTML file on disk should
            // not be treated as managed by this extension.
            DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            ProjectItem htmlItem = dte?.Solution?.FindProjectItem(htmlFile);
            if (htmlItem == null)
            {
                return false;
            }

            try
            {
                string dependentUpon = htmlItem.Properties?.Item("DependentUpon")?.Value as string;
                return string.Equals(dependentUpon, Path.GetFileName(markdownFile), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Property may not exist in some project types.
                return false;
            }
        }

        public static async Task GenerateAndNestHtmlFileAsync(string markdownFile)
        {
            if (string.IsNullOrWhiteSpace(markdownFile))
            {
                throw new ArgumentException("Markdown file path cannot be null or empty.", nameof(markdownFile));
            }

            string generationPath = Path.GetFullPath(markdownFile);
            object generation = BeginGeneration(generationPath);
            try
            {
                string htmlFile = GetHtmlFileName(markdownFile);
                string html = await Task.Run(() => BuildHtmlDocument(markdownFile));
                if (PlantUmlExportService.ContainsDiagrams(html))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    html = await PlantUmlExportService.RenderAsync(html, ThemeHelper.GetMermaidTheme());
                }
                await Task.Run(() => WriteCurrentGeneration(generationPath, generation, htmlFile, html));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                lock (_generationLock) { EnsureCurrentGeneration(generationPath, generation); }
                AddNestedHtmlFile(markdownFile, htmlFile);
            }
            finally { EndGeneration(generationPath, generation); }
        }

        internal static object BeginGeneration(string path)
        {
            object generation = new();
            lock (_generationLock) { _generations[path] = generation; }
            return generation;
        }

        internal static void WriteCurrentGeneration(string path, object generation, string outputFile, string html)
        {
            // Rendering can span several saves. An older SVG must never replace a newer export.
            lock (_generationLock)
            {
                EnsureCurrentGeneration(path, generation);
                File.WriteAllText(outputFile, html, new UTF8Encoding(true));
            }
        }

        internal static void EndGeneration(string path, object generation)
        {
            lock (_generationLock)
            {
                if (_generations.TryGetValue(path, out object current) && ReferenceEquals(current, generation))
                    _generations.Remove(path);
            }
        }

        internal static void CancelGeneration(string path)
        {
            lock (_generationLock) { _generations.Remove(Path.GetFullPath(path)); }
        }

        private static void EnsureCurrentGeneration(string path, object generation)
        {
            if (!_generations.TryGetValue(path, out object current) || !ReferenceEquals(current, generation))
                throw new OperationCanceledException("HTML generation was superseded by a newer save or disabled.");
        }

        public static void DisableHtmlGeneration(string markdownFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(markdownFile))
            {
                throw new ArgumentException("Markdown file path cannot be null or empty.", nameof(markdownFile));
            }

            string htmlFile = GetHtmlFileName(markdownFile);
            CancelGeneration(markdownFile);
            DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;

            ProjectItem htmlItem = dte?.Solution?.FindProjectItem(htmlFile);
            htmlItem?.Remove();

            if (File.Exists(htmlFile))
            {
                File.Delete(htmlFile);
            }
        }

        public static string GetSelectedMarkdownFilePath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.SelectedItems == null || dte.SelectedItems.Count != 1)
            {
                return null;
            }

            SelectedItem selectedItem = dte.SelectedItems.Item(1);
            ProjectItem projectItem = selectedItem?.ProjectItem;

            if (projectItem == null || projectItem.FileCount < 1)
            {
                return null;
            }

            string filePath = projectItem.FileNames[1];
            return IsMarkdownFile(filePath) ? filePath : null;
        }

        public static bool IsMarkdownFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string extension = Path.GetExtension(filePath);
            return extension.Equals(Constants.FileExtensionMd, StringComparison.OrdinalIgnoreCase)
                || extension.Equals(Constants.FileExtensionRmd, StringComparison.OrdinalIgnoreCase)
                || extension.Equals(Constants.FileExtensionMermaid, StringComparison.OrdinalIgnoreCase)
                || extension.Equals(Constants.FileExtensionMmd, StringComparison.OrdinalIgnoreCase)
                || DiagramSupport.IsPlantUmlFile(filePath);
        }

        public static string GetHtmlFileName(string markdownFile)
        {
            if (string.IsNullOrWhiteSpace(markdownFile))
            {
                throw new ArgumentException("Markdown file path cannot be null or empty.", nameof(markdownFile));
            }

            return Path.ChangeExtension(markdownFile, _htmlExtension);
        }

        internal static string BuildHtmlDocument(string markdownFile)
        {
            if (string.IsNullOrWhiteSpace(markdownFile))
            {
                throw new ArgumentException("Markdown file path cannot be null or empty.", nameof(markdownFile));
            }

            string markdown = File.ReadAllText(markdownFile);
            string content;
            string title;
            if (DiagramSupport.IsPlantUmlFile(markdownFile))
            {
                content = DiagramSupport.RenderStandalone(markdownFile, markdown);
                title = Path.GetFileNameWithoutExtension(markdownFile);
            }
            else
            {
                MarkdownDocument document = Markdown.Parse(markdown, Document.PipelineToGenerateHtml);
                content = DiagramSupport.NormalizePlantUml(document.ToHtml(Document.PipelineToGenerateHtml).Replace("\n", Environment.NewLine));
                title = GetTitle(markdownFile, document);
            }
            content = RewriteRelativeExtensionlessAnchorHrefs(content);

            return CreateFromHtmlTemplate(markdownFile, title, content);
        }

        internal static string RewriteRelativeExtensionlessAnchorHrefs(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return html;
            }

            return _anchorHrefRegex.Replace(html, match =>
            {
                string href = match.Groups["url"].Value;
                string rewritten = RewriteHrefIfNeeded(href);
                if (string.Equals(href, rewritten, StringComparison.Ordinal))
                {
                    return match.Value;
                }

                return string.Concat(match.Groups["prefix"].Value, rewritten, match.Groups["suffix"].Value);
            });
        }

        private static string RewriteHrefIfNeeded(string href)
        {
            if (string.IsNullOrEmpty(href) ||
                href.StartsWith("#", StringComparison.Ordinal) ||
                href.StartsWith("/", StringComparison.Ordinal) ||
                href.IndexOf(':') >= 0)
            {
                return href;
            }

            int splitIndex = href.IndexOfAny(new[] { '?', '#' });
            string pathPart = splitIndex >= 0 ? href.Substring(0, splitIndex) : href;
            string suffix = splitIndex >= 0 ? href.Substring(splitIndex) : string.Empty;

            if (string.IsNullOrEmpty(pathPart) ||
                pathPart.EndsWith("/", StringComparison.Ordinal) ||
                pathPart.EndsWith("\\", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(Path.GetExtension(pathPart)))
            {
                return href;
            }

            return pathPart + _htmlExtension + suffix;
        }

        internal static string CreateFromHtmlTemplate(string markdownFile, string title, string htmlContent)
        {
            if (string.IsNullOrWhiteSpace(markdownFile))
            {
                throw new ArgumentException("Markdown file path cannot be null or empty.", nameof(markdownFile));
            }

            if (title == null)
            {
                throw new ArgumentNullException(nameof(title));
            }

            if (htmlContent == null)
            {
                throw new ArgumentNullException(nameof(htmlContent));
            }

            try
            {
                string templateFile = GetHtmlTemplateFile(markdownFile);
                string template = File.ReadAllText(templateFile);

                if (template.IndexOf("[content]", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return htmlContent;
                }

                return template
                    .Replace("[title]", title)
                    .Replace("[content]", htmlContent);
            }
            catch
            {
                return htmlContent;
            }
        }

        private static string GetTitle(string markdownFile, MarkdownDocument document)
        {
            HeadingBlock firstHeading = document.Descendants().OfType<HeadingBlock>().FirstOrDefault();
            if (firstHeading?.Inline == null)
            {
                return Path.GetFileNameWithoutExtension(markdownFile);
            }

            using StringWriter stringWriter = new();

            try
            {
                HtmlRenderer renderer = new(stringWriter)
                {
                    EnableHtmlForInline = false
                };

                renderer.Render(firstHeading.Inline);
                stringWriter.Flush();

                string title = stringWriter.ToString();
                return string.IsNullOrWhiteSpace(title)
                    ? Path.GetFileNameWithoutExtension(markdownFile)
                    : title;
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(markdownFile);
            }
        }

        private static string GetHtmlTemplateFile(string markdownFile)
        {
            string folder = Path.GetDirectoryName(markdownFile);
            string templatePath = FindFileRecursively(folder, _htmlTemplateFileName);

            if (!string.IsNullOrWhiteSpace(templatePath))
            {
                return templatePath;
            }

            string profileTemplate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _htmlTemplateFileName);
            if (File.Exists(profileTemplate))
            {
                return profileTemplate;
            }

            string extensionFolder = Path.GetDirectoryName(typeof(MarkdownEditor2022Package).Assembly.Location);
            return Path.Combine(extensionFolder, "Margin", _htmlTemplateFileName);
        }

        private static string FindFileRecursively(string folder, string fileName)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            DirectoryInfo dir = new(folder);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static void AddNestedHtmlFile(string markdownFile, string htmlFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DTE dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            ProjectItem markdownItem = dte?.Solution?.FindProjectItem(markdownFile);

            if (markdownItem == null)
            {
                return;
            }

            ProjectItem htmlItem = dte?.Solution?.FindProjectItem(htmlFile);
            if (htmlItem == null)
            {
                try
                {
                    htmlItem = markdownItem.ProjectItems?.AddFromFile(htmlFile);
                }
                catch
                {
                    htmlItem = null;
                }
            }

            if (htmlItem == null)
            {
                return;
            }

            try
            {
                if (htmlItem.Properties != null)
                {
                    htmlItem.Properties.Item("DependentUpon").Value = Path.GetFileName(markdownFile);
                }
            }
            catch
            {
                // Some project types may not support this property.
            }
        }
    }
}
