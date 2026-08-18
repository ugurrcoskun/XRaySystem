using Microsoft.Maui.Controls;
using XRaySystem.Mobile.ViewModels;

namespace XRaySystem.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private bool? _isWideLayout;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0) return;

        var useWideLayout = width >= 900;
        if (_isWideLayout == useWideLayout) return;
        _isWideLayout = useWideLayout;

        Workspace.ColumnDefinitions.Clear();
        if (useWideLayout)
        {
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            Grid.SetRow(ControlPanel, 0);
            Grid.SetColumn(ControlPanel, 0);
            Grid.SetRow(ViewerPanel, 0);
            Grid.SetColumn(ViewerPanel, 1);
        }
        else
        {
            Workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            Grid.SetRow(ViewerPanel, 0);
            Grid.SetColumn(ViewerPanel, 0);
            Grid.SetRow(ControlPanel, 1);
            Grid.SetColumn(ControlPanel, 0);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }
}
