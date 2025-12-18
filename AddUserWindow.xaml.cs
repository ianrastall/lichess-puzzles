using System.Windows;

namespace Lichess_Puzzles;

public partial class AddUserWindow : Window
{
    public string UserName { get; private set; } = "";

    public AddUserWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        UserName = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(UserName))
        {
            MessageBox.Show("Please enter a name for the profile.", "Name required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
