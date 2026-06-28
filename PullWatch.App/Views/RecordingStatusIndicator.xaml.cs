using System.Windows;

namespace PullWatch;

public partial class RecordingStatusIndicator : UserControl
{
    public static readonly DependencyProperty HealthProperty = DependencyProperty.Register(
        nameof(Health),
        typeof(RecordingStatusHealth),
        typeof(RecordingStatusIndicator),
        new PropertyMetadata(RecordingStatusHealth.Idle)
    );

    public static readonly DependencyProperty IsPulseActiveProperty = DependencyProperty.Register(
        nameof(IsPulseActive),
        typeof(bool),
        typeof(RecordingStatusIndicator),
        new PropertyMetadata(false)
    );

    public static readonly DependencyProperty IndicatorStrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(IndicatorStrokeThickness),
            typeof(double),
            typeof(RecordingStatusIndicator),
            new PropertyMetadata(0d)
        );

    public RecordingStatusIndicator()
    {
        InitializeComponent();
    }

    public RecordingStatusHealth Health
    {
        get => (RecordingStatusHealth)GetValue(HealthProperty);
        set => SetValue(HealthProperty, value);
    }

    public bool IsPulseActive
    {
        get => (bool)GetValue(IsPulseActiveProperty);
        set => SetValue(IsPulseActiveProperty, value);
    }

    public double IndicatorStrokeThickness
    {
        get => (double)GetValue(IndicatorStrokeThicknessProperty);
        set => SetValue(IndicatorStrokeThicknessProperty, value);
    }
}
