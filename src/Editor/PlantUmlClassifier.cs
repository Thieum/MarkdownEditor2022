using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace MarkdownEditor2022
{
    [Export(typeof(IClassifierProvider))]
    [ContentType(Constants.LanguageName)]
    internal sealed class PlantUmlCommentClassifierProvider : IClassifierProvider
    {
        [Import]
        internal IClassificationTypeRegistryService ClassificationRegistry { get; set; }

        public IClassifier GetClassifier(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new PlantUmlCommentClassifier(buffer, ClassificationRegistry));
    }

    internal sealed class PlantUmlCommentClassifier : IClassifier
    {
        private readonly bool _isPlantUml;
        private readonly IClassificationType _commentType;

        internal PlantUmlCommentClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _commentType = registry.GetClassificationType(PredefinedClassificationTypeNames.Comment);
            _isPlantUml = buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) &&
                DiagramSupport.IsPlantUmlFile(document.FilePath);
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged { add { } remove { } }

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            List<ClassificationSpan> result = [];
            if (!_isPlantUml) return result;
            foreach ((int start, int length) in FindComments(span.Snapshot.GetText()))
            {
                SnapshotSpan? intersection = new SnapshotSpan(span.Snapshot, start, length).Intersection(span);
                if (intersection.HasValue && intersection.Value.Length > 0)
                    result.Add(new ClassificationSpan(intersection.Value, _commentType));
            }
            return result;
        }

        internal static IEnumerable<(int start, int length)> FindComments(string source)
        {
            bool quoted = false;
            for (int index = 0; index < source.Length; index++)
            {
                char current = source[index];
                if (current is '\r' or '\n') quoted = false;
                else if (quoted && current == '\\') index++;
                else if (current == '"') quoted = !quoted;
                else if (!quoted && (current == '\'' ||
                    (current == '/' && index + 1 < source.Length && source[index + 1] == '\'')))
                {
                    int start = index;
                    if (current == '/')
                    {
                        int end = source.IndexOf("'/", index + 2, StringComparison.Ordinal);
                        index = end < 0 ? source.Length : end + 2;
                    }
                    else
                    {
                        while (index < source.Length && source[index] is not '\r' and not '\n') index++;
                    }
                    yield return (start, index - start);
                    index--;
                }
            }
        }
    }
}
