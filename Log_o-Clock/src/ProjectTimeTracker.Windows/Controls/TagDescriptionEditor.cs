using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Controls;

public sealed class TagDescriptionEditor : RichTextBox
{
    private static readonly DependencyProperty IsTagProperty = DependencyProperty.RegisterAttached(
        "IsTag",
        typeof(bool),
        typeof(TagDescriptionEditor),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private IReadOnlyDictionary<string, Brush> _tagBrushes = new Dictionary<string, Brush>();
    private bool _formatting;

    public TagDescriptionEditor()
    {
        Document = CreateDocument();
        TextChanged += (_, _) => FormatCurrentText();
    }

    public string Text
    {
        get => ReadSemanticText();
        set => SetSemanticText(value ?? string.Empty);
    }

    public void Clear() => SetSemanticText(string.Empty);

    public void SetTagDefinitions(IEnumerable<TagDefinition> tags)
    {
        _tagBrushes = TagVisuals.CreateBrushes(tags);
        SetSemanticText(ReadSemanticText());
    }

    private static FlowDocument CreateDocument()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        return document;
    }

    private void FormatCurrentText()
    {
        if (_formatting)
        {
            return;
        }

        var visibleCaretOffset = GetVisibleCaretOffset();
        SetSemanticText(ReadSemanticText(), visibleCaretOffset);
    }

    private void SetSemanticText(string value, int? visibleCaretOffset = null)
    {
        _formatting = true;
        try
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var tokens = TagParser.FindTokens(value);
            var position = 0;
            foreach (var token in tokens)
            {
                AddPlainText(paragraph, value.Substring(position, token.Start - position));
                var run = new Run(token.Name)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = TagVisuals.Resolve(_tagBrushes, token.Name),
                };
                run.SetValue(IsTagProperty, true);
                paragraph.Inlines.Add(run);
                position = token.Start + token.Length;
            }

            AddPlainText(paragraph, value.Substring(position));
            Document.Blocks.Clear();
            Document.Blocks.Add(paragraph);
            CaretPosition = FindVisiblePosition(paragraph, visibleCaretOffset ?? int.MaxValue);
        }
        finally
        {
            _formatting = false;
        }
    }

    private static void AddPlainText(Paragraph paragraph, string text)
    {
        var segments = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length > 0)
            {
                paragraph.Inlines.Add(new Run(segments[index]));
            }

            if (index < segments.Length - 1)
            {
                paragraph.Inlines.Add(new LineBreak());
            }
        }
    }

    private string ReadSemanticText()
    {
        var text = new StringBuilder();
        var tagActive = false;
        foreach (var block in Document.Blocks)
        {
            if (block is not Paragraph paragraph)
            {
                continue;
            }

            AppendInlines(paragraph.Inlines, text, ref tagActive);
        }

        return text.ToString();
    }

    private static void AppendInlines(InlineCollection inlines, StringBuilder text, ref bool tagActive)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    var isTag = (bool)run.GetValue(IsTagProperty);
                    if (isTag && !tagActive && run.Text.Length > 0)
                    {
                        text.Append('#');
                    }

                    text.Append(run.Text);
                    tagActive = isTag && run.Text.Length > 0;
                    break;
                case LineBreak:
                    text.Append('\n');
                    tagActive = false;
                    break;
                case Span span:
                    AppendInlines(span.Inlines, text, ref tagActive);
                    break;
            }
        }
    }

    private int GetVisibleCaretOffset()
    {
        var value = new TextRange(Document.ContentStart, CaretPosition).Text;
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Length;
    }

    private static TextPointer FindVisiblePosition(Paragraph paragraph, int offset)
    {
        foreach (var inline in paragraph.Inlines)
        {
            if (inline is Run run)
            {
                if (offset <= run.Text.Length)
                {
                    return run.ContentStart.GetPositionAtOffset(Math.Max(0, offset), LogicalDirection.Forward)
                        ?? run.ContentEnd;
                }

                offset -= run.Text.Length;
            }
            else if (inline is LineBreak)
            {
                if (offset <= 1)
                {
                    return inline.ContentEnd.GetInsertionPosition(LogicalDirection.Forward) ?? inline.ContentEnd;
                }

                offset--;
            }
        }

        return paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) ?? paragraph.ContentEnd;
    }
}
