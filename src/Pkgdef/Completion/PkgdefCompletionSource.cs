﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Operations;
using Intel = Microsoft.VisualStudio.Language.Intellisense;

namespace MadsKristensen.ExtensibilityTools.Pkgdef
{
    class PkgdefCompletionSource : ICompletionSource
    {
        private ITextBuffer _buffer;
        private bool _disposed = false;
        private IClassifier _classifier;
        private ITextStructureNavigatorSelectorService _navigator;
        private ImageSource _defaultGlyph;
        private ImageSource _snippetGlyph;
        private IGlyphService _glyphService;

        public PkgdefCompletionSource(ITextBuffer buffer, IClassifierAggregatorService classifier, ITextStructureNavigatorSelectorService navigator, IGlyphService glyphService)
        {
            _buffer = buffer;
            _classifier = classifier.GetClassifier(buffer);
            _navigator = navigator;
            _glyphService = glyphService;
            _defaultGlyph = glyphService.GetGlyph(StandardGlyphGroup.GlyphGroupProperty, StandardGlyphItem.GlyphItemPublic);
            _snippetGlyph = glyphService.GetGlyph(StandardGlyphGroup.GlyphCSharpExpansion, StandardGlyphItem.GlyphItemPublic);
        }

        public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
        {
            if (_disposed)
                return;

            ITextSnapshot snapshot = _buffer.CurrentSnapshot;
            var triggerPoint = session.GetTriggerPoint(snapshot);

            if (triggerPoint == null)
                return;

            SnapshotSpan extent = FindTokenSpanAtPosition(session).GetSpan(snapshot);
            var line = triggerPoint.Value.GetContainingLine().Extent;

            var spans = _classifier.GetClassificationSpans(line);

            List<Intel.Completion> list = new List<Completion>();
            int position = session.TextView.Caret.Position.BufferPosition;

            foreach (var span in spans)
            {
                if (span.ClassificationType.IsOfType(PredefinedClassificationTypeNames.SymbolDefinition) && span.Span.GetText().StartsWith("$"))
                {
                    if (!span.Span.Contains(extent.Start))
                        continue;

                    extent = span.Span;

                    foreach (var key in CompletionItem.Items)
                        list.Add(CreateCompletion(key.Name, "$" + key.Name + "$", _defaultGlyph, key.Description));
                }
                else if (extent.GetText().StartsWith("$"))
                {
                    if (!span.Span.Contains(extent.Start))
                        continue;

                    extent = new SnapshotSpan(snapshot, extent.Start, 1);

                    foreach (var key in CompletionItem.Items)
                        list.Add(CreateCompletion(key.Name, "$" + key.Name + "$", _defaultGlyph, key.Description));
                }
                else if (span.ClassificationType.IsOfType(PkgdefClassificationTypes.Guid))
                {
                    if (!span.Span.Contains(extent.Start))
                        continue;

                    extent = span.Span;

                    list.Add(CreateCompletion("<New GUID>", "{" + Guid.NewGuid() + "}", _defaultGlyph, "Creates a new GUID"));

                    AddAllGuids(snapshot, list);
                }
                else if (position > 0 && snapshot.GetText(position - 1, 1) == "{")
                {
                    if (!span.Span.Contains(position - 1))
                        continue;

                    int length = 1;

                    if (snapshot.Length > position + 1 && snapshot.GetText(position, 1) == "}")
                        length = 2;

                    extent = new SnapshotSpan(snapshot, position - 1, length);

                    list.Add(CreateCompletion("<New GUID>", "{" + Guid.NewGuid() + "}", _defaultGlyph, "Creates a new GUID"));

                    AddAllGuids(snapshot, list);
                }
            }

            if (spans.Count == 0 && extent.GetText() == "?")
            {
                HandleSnippets(list);
            }

            if (list.Count > 0)
            {
                var applicableTo = snapshot.CreateTrackingSpan(extent, SpanTrackingMode.EdgeInclusive);
                completionSets.Add(new CompletionSet("All", "All", applicableTo, list, Enumerable.Empty<Intel.Completion>()));
            }
        }

        private void HandleSnippets(List<Intel.Completion> list)
        {
            string assembly = Assembly.GetExecutingAssembly().Location;
            string folder = Path.GetDirectoryName(assembly).ToLowerInvariant();
            string snippetDir = Path.Combine(folder, "Pkgdef\\Completion\\Snippets");

            foreach (string snippet in Directory.EnumerateFiles(snippetDir, "*.txt"))
            {
                string name = Path.GetFileNameWithoutExtension(snippet);
                string insertion = File.ReadAllText(snippet);
                list.Add(CreateCompletion(name, insertion, _snippetGlyph));
            }
        }

        private void AddAllGuids(ITextSnapshot snapshot, List<Intel.Completion> list)
        {
            var guidSpans = _classifier.GetClassificationSpans(new SnapshotSpan(snapshot, 0, snapshot.Length)).Where(g => g.ClassificationType.IsOfType(PkgdefClassificationTypes.Guid));
            Dictionary<string, Tuple<string, ImageSource>> dic = new Dictionary<string, Tuple<string, ImageSource>>();
            var unknown = _glyphService.GetGlyph(StandardGlyphGroup.GlyphGroupModule, StandardGlyphItem.TotalGlyphItems);

            foreach (var cspan in guidSpans)
            {
                string guid = cspan.Span.GetText();
                ITextSnapshotLine line = snapshot.GetLineFromPosition(cspan.Span.Start.Position);
                string lineText = line.GetText();
                Guid check;

                if (!dic.ContainsKey(guid) && Guid.TryParse(guid, out check))
                {
                    dic[guid] = null;
                }

                if (dic[guid] == null || !dic[guid].Item1.Contains("\\"))
                {
                    string before = lineText.Substring(0, lineText.IndexOf(guid)).Trim();
                    string text = before;

                    Match entryKey = Regex.Match(text, "\"(?<key>[^\"]+)\"");
                    if (entryKey.Success)
                    {
                        text = entryKey.Value + "=";
                    }

                    dic[guid] = Tuple.Create(text + Environment.NewLine + "Line: " + line.LineNumber, unknown);
                }
            }

            List<Completion> entries = new List<Completion>();

            foreach (string guid in dic.Keys)
            {
                entries.Add(CreateCompletion(guid, guid, dic[guid].Item2, dic[guid].Item1));
            }

            list.AddRange(entries.OrderBy(e => e.DisplayText));
        }

        private Completion CreateCompletion(string name, string insertion, ImageSource glyph = null, string description = null)
        {
            if (glyph == null)
                glyph = _defaultGlyph;

            return new Completion(name, insertion, description, glyph, null);
        }

        private ITrackingSpan FindTokenSpanAtPosition(ICompletionSession session)
        {
            SnapshotPoint currentPoint = (session.TextView.Caret.Position.BufferPosition) - 1;
            ITextStructureNavigator navigator = _navigator.GetTextStructureNavigator(_buffer);
            TextExtent extent = navigator.GetExtentOfWord(currentPoint);
            return currentPoint.Snapshot.CreateTrackingSpan(extent.Span, SpanTrackingMode.EdgeInclusive);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}