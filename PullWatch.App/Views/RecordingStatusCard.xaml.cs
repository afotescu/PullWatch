using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PullWatch;

public partial class RecordingStatusCard : UserControl
{
    public static readonly DependencyProperty ViewDiagnosticsCommandProperty =
        DependencyProperty.Register(
            nameof(ViewDiagnosticsCommand),
            typeof(ICommand),
            typeof(RecordingStatusCard),
            new PropertyMetadata(null)
        );

    public RecordingStatusCard()
    {
        InitializeComponent();
    }

    public ICommand? ViewDiagnosticsCommand
    {
        get => (ICommand?)GetValue(ViewDiagnosticsCommandProperty);
        set => SetValue(ViewDiagnosticsCommandProperty, value);
    }
}
