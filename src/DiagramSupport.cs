using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace MarkdownEditor2022
{
    internal static class DiagramSupport
    {
        private static readonly Regex _plantUmlClass = new(
            "class=\"language-(?:plantuml|puml)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsPlantUmlFile(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty);
            return extension.Equals(Constants.FileExtensionPuml, StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(Constants.FileExtensionPlantUml, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsStandaloneDiagram(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty);
            return IsPlantUmlFile(path) ||
                extension.Equals(Constants.FileExtensionMermaid, StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(Constants.FileExtensionMmd, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizePlantUml(string html) =>
            _plantUmlClass.Replace(html, "class=\"plantuml\"");

        internal static string RenderStandalone(string path, string source) =>
            $"<pre class=\"{(IsPlantUmlFile(path) ? "plantuml" : "mermaid")}\">{WebUtility.HtmlEncode(source)}</pre>";
    }
}
