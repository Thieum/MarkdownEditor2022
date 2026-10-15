using System.IO;

namespace MarkdownEditor2022.UnitTests
{
    [TestClass]
    public sealed class PlantUmlTests
    {
        [TestMethod]
        public async Task HtmlGenerationService_DelayedOlderGenerationCannotOverwriteNewerOutput()
        {
            using GenerationFixture fixture = new();
            object oldGeneration = HtmlGenerationService.BeginGeneration(fixture.SourcePath);
            TaskCompletionSource<bool> renderFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task oldWriter = Task.Run(async () =>
            {
                await renderFinished.Task;
                Assert.ThrowsExactly<OperationCanceledException>(() => HtmlGenerationService.WriteCurrentGeneration(
                    fixture.SourcePath, oldGeneration, fixture.OutputPath, "<svg>older diagram</svg>"));
            });

            try
            {
                object newGeneration = HtmlGenerationService.BeginGeneration(fixture.SourcePath);
                HtmlGenerationService.WriteCurrentGeneration(
                    fixture.SourcePath, newGeneration, fixture.OutputPath, "<svg>newer diagram</svg>");
                Assert.AreEqual("<svg>newer diagram</svg>", File.ReadAllText(fixture.OutputPath));
            }
            finally
            {
                renderFinished.SetResult(true);
            }

            await oldWriter;
            Assert.AreEqual("<svg>newer diagram</svg>", File.ReadAllText(fixture.OutputPath));
        }

        [TestMethod]
        public void HtmlGenerationService_EndingOldGenerationPreservesNewerGeneration()
        {
            using GenerationFixture fixture = new();
            object oldGeneration = HtmlGenerationService.BeginGeneration(fixture.SourcePath);
            object newGeneration = HtmlGenerationService.BeginGeneration(fixture.SourcePath);

            HtmlGenerationService.EndGeneration(fixture.SourcePath, oldGeneration);
            HtmlGenerationService.WriteCurrentGeneration(
                fixture.SourcePath, newGeneration, fixture.OutputPath, "<svg>current diagram</svg>");

            Assert.AreEqual("<svg>current diagram</svg>", File.ReadAllText(fixture.OutputPath));
            Assert.ThrowsExactly<OperationCanceledException>(() => HtmlGenerationService.WriteCurrentGeneration(
                fixture.SourcePath, oldGeneration, fixture.OutputPath, "<svg>stale diagram</svg>"));
            Assert.AreEqual("<svg>current diagram</svg>", File.ReadAllText(fixture.OutputPath));
        }

        [TestMethod]
        public void HtmlGenerationService_EndingCurrentGenerationInvalidatesToken()
        {
            using GenerationFixture fixture = new();
            object generation = HtmlGenerationService.BeginGeneration(fixture.SourcePath);
            HtmlGenerationService.WriteCurrentGeneration(
                fixture.SourcePath, generation, fixture.OutputPath, "<svg>completed diagram</svg>");

            HtmlGenerationService.EndGeneration(fixture.SourcePath, generation);

            Assert.ThrowsExactly<OperationCanceledException>(() => HtmlGenerationService.WriteCurrentGeneration(
                fixture.SourcePath, generation, fixture.OutputPath, "<svg>unexpected second write</svg>"));
            Assert.AreEqual("<svg>completed diagram</svg>", File.ReadAllText(fixture.OutputPath));
        }

        [TestMethod]
        public void HtmlGenerationService_CancelGenerationPreservesPriorOutputAndBlocksWriter()
        {
            using GenerationFixture fixture = new();
            File.WriteAllText(fixture.OutputPath, "<svg>previous saved diagram</svg>");
            object generation = HtmlGenerationService.BeginGeneration(fixture.SourcePath);

            HtmlGenerationService.CancelGeneration(fixture.SourcePath);

            Assert.AreEqual("<svg>previous saved diagram</svg>", File.ReadAllText(fixture.OutputPath));
            Assert.ThrowsExactly<OperationCanceledException>(() => HtmlGenerationService.WriteCurrentGeneration(
                fixture.SourcePath, generation, fixture.OutputPath, "<svg>cancelled render</svg>"));
            Assert.AreEqual("<svg>previous saved diagram</svg>", File.ReadAllText(fixture.OutputPath));
        }

        [TestMethod]
        public void HtmlGenerationService_GenerationIdentityIsCaseInsensitive()
        {
            using GenerationFixture fixture = new();
            string upperPath = fixture.SourcePath.ToUpperInvariant();
            string lowerPath = fixture.SourcePath.ToLowerInvariant();
            object oldGeneration = HtmlGenerationService.BeginGeneration(lowerPath);
            object currentGeneration = HtmlGenerationService.BeginGeneration(upperPath);
            File.WriteAllText(fixture.OutputPath, "<svg>prior diagram</svg>");

            Assert.ThrowsExactly<OperationCanceledException>(() => HtmlGenerationService.WriteCurrentGeneration(
                lowerPath, oldGeneration, fixture.OutputPath, "<svg>old casing diagram</svg>"));
            Assert.AreEqual("<svg>prior diagram</svg>", File.ReadAllText(fixture.OutputPath));
            HtmlGenerationService.WriteCurrentGeneration(
                lowerPath, currentGeneration, fixture.OutputPath, "<svg>current casing diagram</svg>");
            Assert.AreEqual("<svg>current casing diagram</svg>", File.ReadAllText(fixture.OutputPath));

            HtmlGenerationService.EndGeneration(lowerPath, currentGeneration);
            Assert.ThrowsExactly<OperationCanceledException>(() => HtmlGenerationService.WriteCurrentGeneration(
                upperPath, currentGeneration, fixture.OutputPath, "<svg>ended casing diagram</svg>"));
            Assert.AreEqual("<svg>current casing diagram</svg>", File.ReadAllText(fixture.OutputPath));

            object cancelledGeneration = HtmlGenerationService.BeginGeneration(upperPath);
            HtmlGenerationService.CancelGeneration(lowerPath);
            Assert.ThrowsExactly<OperationCanceledException>(() => HtmlGenerationService.WriteCurrentGeneration(
                upperPath, cancelledGeneration, fixture.OutputPath, "<svg>cancelled casing diagram</svg>"));
            Assert.AreEqual("<svg>current casing diagram</svg>", File.ReadAllText(fixture.OutputPath));
        }

        [TestMethod]
        public void HtmlGenerationService_IndependentFilesDoNotSupersedeEachOther()
        {
            using GenerationFixture fixture = new();
            object first = HtmlGenerationService.BeginGeneration(fixture.SourcePath);
            object second = HtmlGenerationService.BeginGeneration(fixture.SecondSourcePath);
            HtmlGenerationService.WriteCurrentGeneration(
                fixture.SourcePath, first, fixture.OutputPath, "<svg>first file</svg>");
            HtmlGenerationService.WriteCurrentGeneration(
                fixture.SecondSourcePath, second, fixture.SecondOutputPath, "<svg>second file</svg>");
            Assert.AreEqual("<svg>first file</svg>", File.ReadAllText(fixture.OutputPath));
            Assert.AreEqual("<svg>second file</svg>", File.ReadAllText(fixture.SecondOutputPath));

            HtmlGenerationService.EndGeneration(fixture.SourcePath, first);
            HtmlGenerationService.WriteCurrentGeneration(
                fixture.SecondSourcePath, second, fixture.SecondOutputPath, "<svg>second file revised</svg>");
            Assert.AreEqual("<svg>first file</svg>", File.ReadAllText(fixture.OutputPath));
            Assert.AreEqual("<svg>second file revised</svg>", File.ReadAllText(fixture.SecondOutputPath));
        }

        private sealed class GenerationFixture : IDisposable
        {
            private readonly string _directory = Path.Combine(
                Path.GetTempPath(), "MarkdownEditor2022PlantUmlGenerationTests", Guid.NewGuid().ToString("N"));

            internal string SourcePath => Path.Combine(_directory, "first-diagram.puml");
            internal string OutputPath => Path.Combine(_directory, "first-diagram.html");
            internal string SecondSourcePath => Path.Combine(_directory, "second-diagram.puml");
            internal string SecondOutputPath => Path.Combine(_directory, "second-diagram.html");

            internal GenerationFixture()
            {
                Directory.CreateDirectory(_directory);
            }

            public void Dispose()
            {
                HtmlGenerationService.CancelGeneration(SourcePath);
                HtmlGenerationService.CancelGeneration(SecondSourcePath);
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, true);
                }
            }
        }

        [TestMethod]
        public void Browser_RenderHtmlDocument_PlantUmlAliasesPreserveAndEncodeExplicitSource()
        {
            const string source = "Alice -> Bob: <node> & \"quoted\"";
            foreach (string language in new[] { "plantuml", "puml" })
            {
                string html = Browser.RenderHtmlDocument(Markdig.Markdown.Parse(
                    $"```{language}\n{source}\n```",
                    Document.Pipeline));

                StringAssert.Contains(html, "class=\"plantuml\">");
                StringAssert.Contains(html, "Alice -&gt; Bob: &lt;node&gt; &amp; &quot;quoted&quot;");
                Assert.IsFalse(html.Contains("language-" + language));
                Assert.IsFalse(html.Contains("@startuml"));
                Assert.IsFalse(html.Contains("@enduml"));
            }
        }

        [TestMethod]
        public void Browser_RenderHtmlDocument_MixedDiagramAndCodeLanguagesRemainStable()
        {
            Markdig.Syntax.MarkdownDocument markdown = Markdig.Markdown.Parse(
                "```plantuml\nAlice -> Bob\n```\n\n```mermaid\ngraph TD; A-->B;\n```\n\n```js\nconst value = 1;\n```",
                Document.Pipeline);

            string first = Browser.RenderHtmlDocument(markdown);
            string second = Browser.RenderHtmlDocument(markdown);

            StringAssert.Contains(first, "class=\"plantuml\">Alice -&gt; Bob");
            StringAssert.Contains(first, "class=\"mermaid\"");
            StringAssert.Contains(first, "class=\"language-javascript\"");
            Assert.AreEqual(first, second);
        }

        [TestMethod]
        public void DiagramSupport_IsPlantUmlFile_RecognizesPlantUmlExtensionsCaseInsensitively()
        {
            foreach (string path in new[] { "diagram.puml", "diagram.PUML", "diagram.plantuml", "diagram.PLANTUML" })
            {
                Assert.IsTrue(DiagramSupport.IsPlantUmlFile(path), path);
                Assert.IsTrue(DiagramSupport.IsStandaloneDiagram(path), path);
            }

            foreach (string path in new[] { "diagram.md", "diagram.puml.txt", "diagramplantuml", "" })
            {
                Assert.IsFalse(DiagramSupport.IsPlantUmlFile(path), path);
                Assert.IsFalse(DiagramSupport.IsStandaloneDiagram(path), path);
            }

            foreach (string path in new[] { "diagram.mmd", "diagram.MMD", "diagram.mermaid", "diagram.MERMAID" })
            {
                Assert.IsFalse(DiagramSupport.IsPlantUmlFile(path), path);
                Assert.IsTrue(DiagramSupport.IsStandaloneDiagram(path), path);
            }

            Assert.IsFalse(DiagramSupport.IsPlantUmlFile(null!));
            Assert.IsFalse(DiagramSupport.IsStandaloneDiagram(null!));
        }

        [TestMethod]
        public void DiagramSupport_RenderStandalone_EncodesSourceAndSelectsPlantUmlClass()
        {
            string html = DiagramSupport.RenderStandalone(
                "architecture.PUML",
                "@startuml\nAlice -> Bob: <node> & \"quoted\"\n@enduml");

            Assert.AreEqual(
                "<pre class=\"plantuml\">@startuml\nAlice -&gt; Bob: &lt;node&gt; &amp; &quot;quoted&quot;\n@enduml</pre>",
                html);
        }

        [TestMethod]
        public void DiagramSupport_RenderStandalone_EmptySourcePreservesDiagramDiscriminator()
        {
            Assert.AreEqual("<pre class=\"plantuml\"></pre>", DiagramSupport.RenderStandalone("empty.plantuml", ""));
            Assert.AreEqual("<pre class=\"mermaid\"></pre>", DiagramSupport.RenderStandalone("empty.mmd", ""));
        }

        [TestMethod]
        public void PlantUmlCommentClassifier_FindComments_FindsSingleLineComments()
        {
            string source = "Alice -> Bob ' first\nnote ' second\n";
            var expected = new[]
            {
                (source.IndexOf("' first", StringComparison.Ordinal), "' first".Length),
                (source.IndexOf("' second", StringComparison.Ordinal), "' second".Length)
            };

            CollectionAssert.AreEqual(expected, PlantUmlCommentClassifier.FindComments(source).ToArray());
        }

        [TestMethod]
        public void PlantUmlCommentClassifier_FindComments_FindsMultilineComments()
        {
            string source = "before\n/' block\nstill block '/ after\n";
            int start = source.IndexOf("/'", StringComparison.Ordinal);
            int end = source.IndexOf("'/", start + 2, StringComparison.Ordinal) + 2;
            var comments = PlantUmlCommentClassifier.FindComments(source).ToArray();

            CollectionAssert.AreEqual(new[] { (start, end - start) }, comments);
            Assert.AreEqual("/' block\nstill block '/", source.Substring(comments[0].start, comments[0].length));
            Assert.AreEqual(" after\n", source.Substring(comments[0].start + comments[0].length));
        }

        [TestMethod]
        public void PlantUmlCommentClassifier_FindComments_IgnoresQuotedAndEscapedCommentMarkers()
        {
            string source = "Alice \"say \\\"'still quoted'\\\" /' also quoted '/\" -> Bob\nnote ' real\n";
            int start = source.IndexOf("' real", StringComparison.Ordinal);
            var comments = PlantUmlCommentClassifier.FindComments(source).ToArray();

            CollectionAssert.AreEqual(new[] { (start, "' real".Length) }, comments);
            Assert.AreEqual("' real", source.Substring(comments[0].start, comments[0].length));
        }

        [TestMethod]
        public void PlantUmlCommentClassifier_FindComments_QuoteStateResetsAtLineBoundaries()
        {
            const string source = "\"unfinished quote\r\n' first\r' second\nAlice -> Bob";
            CollectionAssert.AreEqual(
                new[] { (19, 7), (27, 8) },
                PlantUmlCommentClassifier.FindComments(source).ToArray());
            Assert.AreEqual(0, PlantUmlCommentClassifier.FindComments("").Count());
            Assert.AreEqual(0, PlantUmlCommentClassifier.FindComments("Alice -> Bob: \"no ' comment\"").Count());
        }

        [TestMethod]
        public void PlantUmlCommentClassifier_FindComments_UnterminatedCommentsExtendToLineOrEnd()
        {
            string lineSource = "Alice -> Bob ' unfinished";
            var lineComments = PlantUmlCommentClassifier.FindComments(lineSource).ToArray();
            CollectionAssert.AreEqual(
                new[] { (lineSource.IndexOf("'", StringComparison.Ordinal), lineSource.Length - lineSource.IndexOf("'", StringComparison.Ordinal)) },
                lineComments);

            string blockSource = "before /' unfinished block";
            var blockComments = PlantUmlCommentClassifier.FindComments(blockSource).ToArray();
            int blockStart = blockSource.IndexOf("/'", StringComparison.Ordinal);
            CollectionAssert.AreEqual(new[] { (blockStart, blockSource.Length - blockStart) }, blockComments);
            Assert.AreEqual("/' unfinished block", blockSource.Substring(blockComments[0].start, blockComments[0].length));
        }

        [TestMethod]
        public void HtmlGenerationService_BuildHtmlDocument_MarkdownPlantUmlPreservesTitleAndLinks()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MarkdownEditor2022PlantUmlTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string markdownFile = Path.Combine(directory, "design.md");

            try
            {
                File.WriteAllText(Path.Combine(directory, "md-template.html"), "<title>[title]</title><main>[content]</main>");
                File.WriteAllText(markdownFile,
                    "# System Design\n\n```plantuml\nAlice -> Bob: <node> & \"quoted\"\n```\n\n[Next](next)\n\n[Spec](spec.md)\n");

                string html = HtmlGenerationService.BuildHtmlDocument(markdownFile);

                StringAssert.Contains(html, "<title>System Design</title>");
                StringAssert.Contains(html, "<code class=\"plantuml\">");
                StringAssert.Contains(html, "Alice -&gt; Bob: &lt;node&gt; &amp; &quot;quoted&quot;");
                StringAssert.Contains(html, "href=\"next.html\"");
                StringAssert.Contains(html, "href=\"spec.md\"");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [TestMethod]
        public void HtmlGenerationService_BuildHtmlDocument_StandalonePlantUmlUsesFileTitleAndEncodesSource()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MarkdownEditor2022PlantUmlTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string plantUmlFile = Path.Combine(directory, "architecture.PLANTUML");

            try
            {
                File.WriteAllText(Path.Combine(directory, "md-template.html"), "<title>[title]</title><main>[content]</main>");
                File.WriteAllText(plantUmlFile, "@startuml\nAlice -> Bob: <node> & \"quoted\"\n@enduml");

                string html = HtmlGenerationService.BuildHtmlDocument(plantUmlFile);

                StringAssert.Contains(html, "<title>architecture</title>");
                StringAssert.Contains(html, "<pre class=\"plantuml\">@startuml");
                StringAssert.Contains(html, "Alice -&gt; Bob: &lt;node&gt; &amp; &quot;quoted&quot;");
                StringAssert.Contains(html, "@enduml</pre>");
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
