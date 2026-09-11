using System.Windows;
using System.Windows.Controls;
using ApacsMonitor.Models;
using ApacsMonitor.Services;

namespace ApacsMonitor;

public partial class ConnectionWindow : Window
{
    private readonly SqlService _sql = new();

    public DatabaseSettings Settings { get; private set; }
    public string Password { get; private set; } = "";

    public ConnectionWindow(DatabaseSettings settings, string password)
    {
        InitializeComponent();

        Settings = new DatabaseSettings
        {
            Server = settings.Server,
            Database = settings.Database,
            Authentication = settings.Authentication,
            UserName = settings.UserName
        };
        Password = password;

        ServerTextBox.Text = Settings.Server;
        DatabaseTextBox.Text = Settings.Database;
        UserNameTextBox.Text = Settings.UserName;
        PasswordBox.Password = password;

        AuthenticationComboBox.SelectedIndex =
            string.Equals(Settings.Authentication, "Windows", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private DatabaseSettings ReadSettings()
    {
        var authentication = ((ComboBoxItem)AuthenticationComboBox.SelectedItem).Tag?.ToString() ?? "SqlServer";

        return new DatabaseSettings
        {
            Server = ServerTextBox.Text.Trim(),
            Database = DatabaseTextBox.Text.Trim(),
            Authentication = authentication,
            UserName = UserNameTextBox.Text.Trim()
        };
    }

    private async Task<bool> TestConnectionInternalAsync()
    {
        try
        {
            Settings = ReadSettings();
            Password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(Settings.Server))
                throw new InvalidOperationException("Укажите сервер SQL Server.");

            if (string.IsNullOrWhiteSpace(Settings.Database))
                throw new InvalidOperationException("Укажите базу данных.");

            if (Settings.Authentication == "SqlServer" && string.IsNullOrWhiteSpace(Settings.UserName))
                throw new InvalidOperationException("Укажите пользователя SQL Server.");

            _sql.Configure(Settings, Password);
            await _sql.TestConnectionAsync();

            StatusText.Text = "Подключение успешно.";
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
            return false;
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        await TestConnectionInternalAsync();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (await TestConnectionInternalAsync())
            DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void AuthenticationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UserNameTextBox == null || PasswordBox == null || AuthenticationComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var windows = string.Equals(item.Tag?.ToString(), "Windows", StringComparison.OrdinalIgnoreCase);
        UserNameTextBox.IsEnabled = !windows;
        PasswordBox.IsEnabled = !windows;
    }
}
