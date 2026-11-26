using System.Collections.Specialized;
using Avalonia.Controls;
using SpinnyDisc.ViewModels;

namespace SpinnyDisc.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookLog();
        HookLog();
    }

    private void HookLog()
    {
        if (DataContext is MainViewModel vm)
            vm.LogLines.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogBox.ItemCount > 0)
            LogBox.ScrollIntoView(LogBox.ItemCount - 1);
    }
}
