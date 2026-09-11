using System.IO;

namespace MarkdownEditor2022.UnitTests
{
    [TestClass]
    public class HtmlGenerationWorkflowTests
    {
        [DataRow("guide", "guide.html")]
        [DataRow("guide.md#setup", "guide.md#setup")]
        [DataRow("guide.md?mode=full", "guide.md?mode=full")]
        [DataRow("https://example.test/guide", "https://example.test/guide")]
        [TestMethod]
        public void RewriteRelativeExtensionlessAnchorHrefs_PreservesOrAddsHtml(string href, string expected)
        {
            string html = $"<p><a href=\"{href}\">Guide</a></p>";

            string result = HtmlGenerationService.RewriteRelativeExtensionlessAnchorHrefs(html);

            StringAssert.Contains(result, $"href=\"{expected}\"");
        }

        [TestMethod]
        public void BuildHtmlDocument_RendersSavedMarkdownWithTitleAndContent()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MarkdownEditor2022Tests", System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string markdownFile = Path.Combine(directory, "guide.md");

            try
            {
                File.WriteAllText(markdownFile, "# Release Guide\n\nSee [setup](setup).\n");

                string html = HtmlGenerationService.BuildHtmlDocument(markdownFile);

                StringAssert.Contains(html, "Release Guide");
                StringAssert.Contains(html, "href=\"setup.html\"");
                StringAssert.Contains(html, "<h1");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }
}
