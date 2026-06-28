using System.Windows;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDataObject = System.Windows.DataObject;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace PullWatch;

public static class TextBoxInput
{
    public static readonly DependencyProperty AcceptsNonNegativeIntegerOnlyProperty =
        DependencyProperty.RegisterAttached(
            "AcceptsNonNegativeIntegerOnly",
            typeof(bool),
            typeof(TextBoxInput),
            new PropertyMetadata(false, OnAcceptsNonNegativeIntegerOnlyChanged)
        );

    public static readonly DependencyProperty FallbackTextWhenEmptyProperty =
        DependencyProperty.RegisterAttached(
            "FallbackTextWhenEmpty",
            typeof(string),
            typeof(TextBoxInput),
            new PropertyMetadata(null, OnFallbackTextWhenEmptyChanged)
        );

    public static bool GetAcceptsNonNegativeIntegerOnly(DependencyObject dependencyObject)
    {
        return (bool)dependencyObject.GetValue(AcceptsNonNegativeIntegerOnlyProperty);
    }

    public static void SetAcceptsNonNegativeIntegerOnly(
        DependencyObject dependencyObject,
        bool value
    )
    {
        dependencyObject.SetValue(AcceptsNonNegativeIntegerOnlyProperty, value);
    }

    public static string? GetFallbackTextWhenEmpty(DependencyObject dependencyObject)
    {
        return (string?)dependencyObject.GetValue(FallbackTextWhenEmptyProperty);
    }

    public static void SetFallbackTextWhenEmpty(DependencyObject dependencyObject, string? value)
    {
        dependencyObject.SetValue(FallbackTextWhenEmptyProperty, value);
    }

    private static void OnAcceptsNonNegativeIntegerOnlyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs
    )
    {
        if (dependencyObject is not WpfTextBox textBox)
        {
            return;
        }

        if ((bool)eventArgs.OldValue)
        {
            textBox.PreviewTextInput -= OnPreviewTextInput;
            WpfDataObject.RemovePastingHandler(textBox, OnPaste);
        }

        if ((bool)eventArgs.NewValue)
        {
            textBox.PreviewTextInput += OnPreviewTextInput;
            WpfDataObject.AddPastingHandler(textBox, OnPaste);
        }
    }

    private static void OnFallbackTextWhenEmptyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs
    )
    {
        if (dependencyObject is not WpfTextBox textBox)
        {
            return;
        }

        if (eventArgs.OldValue is not null)
        {
            textBox.LostFocus -= OnLostFocus;
        }

        if (eventArgs.NewValue is not null)
        {
            textBox.LostFocus += OnLostFocus;
        }
    }

    private static void OnPreviewTextInput(
        object sender,
        System.Windows.Input.TextCompositionEventArgs eventArgs
    )
    {
        eventArgs.Handled = !ContainsOnlyDigits(eventArgs.Text);
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs eventArgs)
    {
        if (!eventArgs.DataObject.GetDataPresent(WpfDataFormats.Text))
        {
            eventArgs.CancelCommand();
            return;
        }

        if (
            eventArgs.DataObject.GetData(WpfDataFormats.Text) is not string text
            || !ContainsOnlyDigits(text)
        )
        {
            eventArgs.CancelCommand();
        }
    }

    private static void OnLostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (
            sender is not WpfTextBox textBox
            || !string.IsNullOrWhiteSpace(textBox.Text)
            || GetFallbackTextWhenEmpty(textBox) is not { } fallbackText
        )
        {
            return;
        }

        textBox.Text = fallbackText;
        textBox.GetBindingExpression(WpfTextBox.TextProperty)?.UpdateSource();
    }

    private static bool ContainsOnlyDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
