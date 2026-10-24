using Avalonia.Controls;
using Avalonia.Interactivity;
using BayouLex.App.ViewModels;

namespace BayouLex.App.Views;

public partial class LibraryManagerWindow : Window
{
    public LibraryManagerWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            // Don't leave a half-installed pack behind by closing mid-install.
            if (DataContext is LibraryManagerViewModel { IsWorking: true })
            {
                e.Cancel = true;
            }
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
