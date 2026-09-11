using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ApacsMonitor.Models;
using ApacsMonitor.Services;

namespace ApacsMonitor;

public partial class MainWindow : Window
{
    private readonly ConfigurationService _configuration = new();
    private readonly SqlService _sql = new();
    private readonly ObservableCollection<EventRecord> _events = new();
    private readonly DispatcherTimer _timer;

    private DatabaseSettings _settings;
    private string _password = "";
    private bool _connected;

    public MainWindow()
    {
        InitializeComponent();
        EventsGrid.ItemsSource = _events;

        _settings = _configuration.LoadDatabaseSettings();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshEventsAsync();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await OpenConnectionWindowAsync();
    }

    private async Task OpenConnectionWindowAsync()
    {
        var dialog = new ConnectionWindow(_settings, _password);
        dialog.Owner = this;

        if (dialog.ShowDialog() != true)
        {
            ConnectionStatusText.Text = "Подключение не настроено";
            return;
        }

        _settings = dialog.Settings;
        _password = dialog.Password;
        _configuration.SaveDatabaseSettings(_settings);
        _sql.Configure(_settings, _password);

        try
        {
            await _sql.TestConnectionAsync();
            _connected = true;
            ConnectionStatusText.Text = $"Подключено: {_settings.Server} / {_settings.Database}";
            _timer.Start();
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            _connected = false;
            _timer.Stop();
            ConnectionStatusText.Text = "Ошибка подключения";
            MessageBox.Show(this, ex.Message, "Ошибка SQL Server", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshEventsAsync()
    {
        if (!_connected)
            return;

        try
        {
            RefreshButton.IsEnabled = false;
            var events = await _sql.GetRecentEventsAsync(100);

            _events.Clear();
            foreach (var item in events)
                _events.Add(item);

            EventCountText.Text = $"Событий: {_events.Count}";
            LastUpdateText.Text = $"Обновлено: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
            ConnectionStatusText.Text = $"Подключено: {_settings.Server} / {_settings.Database}";
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = "Ошибка чтения событий";
            LastUpdateText.Text = ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshEventsAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        await OpenConnectionWindowAsync();
    }
}
