using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Controls;

public sealed class TagDescriptionDisplay : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(TagDescriptionDisplay),
        new PropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty TagDefinitionsProperty = DependencyProperty.Register(
        nameof(TagDefinitions),
        typeof(IReadOnlyList<TagDefinition>),
        typeof(TagDescriptionDisplay),
        new PropertyMetadata(null, OnDisplayPropertyChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public IReadOnlyList<TagDefinition>? TagDefinitions
    {
        get => (IReadOnlyList<TagDefinition>?)GetValue(TagDefinitionsProperty);
        set => SetValue(TagDefinitionsProperty, value);
    }

    private static void OnDisplayPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        ((TagDescriptionDisplay)sender).Rebuild();
    }

    private void Rebuild()
    {
        Inlines.Clear();
        var source = SourceText ?? string.Empty;
        if (source.Length == 0)
        {
            Inlines.Add(new Run("—"));
            return;
        }

        var brushes = TagVisuals.CreateBrushes(TagDefinitions);
        var position = 0;
        foreach (var token in TagParser.FindTokens(source))
        {
            if (token.Start > position)
            {
                Inlines.Add(new Run(TextWrapOpportunityFormatter.AddInvisibleBreaks(
                    source.Substring(position, token.Start - position))));
            }

            Inlines.Add(new Run(TextWrapOpportunityFormatter.AddInvisibleBreaks(token.Name))
            {
                FontWeight = FontWeights.Bold,
                Foreground = TagVisuals.Resolve(brushes, token.Name),
            });
            position = token.Start + token.Length;
        }

        if (position < source.Length)
        {
            Inlines.Add(new Run(TextWrapOpportunityFormatter.AddInvisibleBreaks(source.Substring(position))));
        }
    }
}
