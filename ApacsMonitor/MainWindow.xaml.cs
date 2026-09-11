using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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
    private AppConfiguration _config = new();
    private string _period = "today";
    private bool _initializingLanguage;

    public MainWindow()
    {
        InitializeComponent();
        EmployeeCards.ItemsSource = _events;
        _timer = new DispatcherTimer();
        _timer.Tick += async (_, _) => await RefreshEventsAsync();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _config = _configuration.Load();
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config.RefreshSeconds));
        SetLanguage(_config.Language);
        _sql.Configure(_config.Database, _config.Password);
        await ConnectFromConfigAsync();
    }

    private async Task ConnectFromConfigAsync()
    {
        try
        {
            ConnectionStatusText.Text = T("Подключение...", "Connecting...", "Bağlanıyor...");
            await _sql.TestConnectionAsync();
            ConnectionStatusText.Text = T("Подключено", "Connected", "Bağlandı");
            _timer.Start();
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            _timer.Stop();
            ConnectionStatusText.Text = T("Ошибка подключения", "Connection error", "Bağlantı hatası");
            LastUpdateText.Text = ex.Message;
        }
    }

    private async Task RefreshEventsAsync()
    {
        try
        {
            var events = await _sql.GetRecentEventsAsync(1000);
            _events.Clear();
            foreach (var item in events) _events.Add(item);
            ApplyFilters();
            LastUpdateText.Text = $"{T("Обновлено", "Updated", "Güncellendi")}: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = T("Ошибка чтения", "Read error", "Okuma hatası");
            LastUpdateText.Text = ex.Message;
        }
    }

    private void ApplyFilters()
    {
        var today = DateTime.Today;
        DateTime from;
        DateTime to = today.AddDays(1);
        switch (_period)
        {
            case "week": from = today.AddDays(-6); break;
            case "month": from = today.AddMonths(-1).Date; break;
            case "custom":
                from = FromDatePicker.SelectedDate?.Date ?? today;
                to = (ToDatePicker.SelectedDate?.Date ?? today).AddDays(1);
                break;
            default: from = today; break;
        }

        IEnumerable<EventRecord> query = _events.Where(x => x.RealTime >= from && x.RealTime < to);

        // DisplayMode is deliberately controlled only from appsettings.json.
        // These names are only a temporary compatibility filter for the current
        // diagnostic system-event source. Real employee events will use a proper
        // location/access-point field once the access-event table is mapped.
        query = _config.DisplayMode switch
        {
            "post" => query.Where(x => IsPostEvent(x.InitObjectName)),
            "canteen" => query.Where(x => IsCanteenEvent(x.InitObjectName)),
            _ => query
        };

        var search = SearchTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.InitObjectName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || x.InitObjectId0.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.InitObjectId1.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));

        var result = query.OrderByDescending(x => x.RealTime).ToList();
        EmployeeCards.ItemsSource = result;
        EventCountText.Text = $"{T("Событий", "Events", "Olaylar")}: {result.Count}";
    }

    private static bool IsPostEvent(string name) =>
        name.Contains("Post", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Turn", StringComparison.OrdinalIgnoreCase);

    private static bool IsCanteenEvent(string name) =>
        name.Contains("Canteen", StringComparison.OrdinalIgnoreCase);

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();
    private void TodayButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("today");
    private void WeekButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("week");
    private void MonthButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("month");

    private void PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        _period = "custom";
        PeriodPanel.Visibility = Visibility.Visible;
        UpdatePeriodButtons();
        ApplyFilters();
    }

    private void CustomDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_period == "custom") ApplyFilters();
    }

    private void SelectPeriod(string period)
    {
        _period = period;
        PeriodPanel.Visibility = Visibility.Collapsed;
        UpdatePeriodButtons();
        ApplyFilters();
    }

    private void UpdatePeriodButtons()
    {
        TodayButton.Style = FindResource(_period == "today" ? "ActiveFilterButton" : "FilterButton") as Style;
        WeekButton.Style = FindResource(_period == "week" ? "ActiveFilterButton" : "FilterButton") as Style;
        MonthButton.Style = FindResource(_period == "month" ? "ActiveFilterButton" : "FilterButton") as Style;
        PeriodButton.Style = FindResource(_period == "custom" ? "ActiveFilterButton" : "FilterButton") as Style;
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingLanguage || LanguageComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string language) return;
        _config = new AppConfiguration
        {
            Database = _config.Database,
            Password = _config.Password,
            RefreshSeconds = _config.RefreshSeconds,
            DisplayMode = _config.DisplayMode,
            Language = language
        };
        SetLanguage(language);
    }

    private void SetLanguage(string language)
    {
        _initializingLanguage = true;
        LanguageComboBox.SelectedIndex = language switch { "en" => 1, "tr" => 2, _ => 0 };
        _initializingLanguage = false;
        Title = T("APACS Monitor — Журнал доступа", "APACS Monitor — Access Journal", "APACS Monitor — Erişim Günlüğü");
        SubtitleText.Text = T("Журнал доступа сотрудников", "Employee access journal", "Çalışan erişim günlüğü");
        SearchHint.Text = T("Поиск по ФИО или номеру карты", "Search by name or card number", "Ad veya kart numarasına göre ara");
        TodayButton.Content = T("Сегодня", "Today", "Bugün");
        WeekButton.Content = T("Неделя", "Week", "Hafta");
        MonthButton.Content = T("Месяц", "Month", "Ay");
        PeriodButton.Content = T("Период", "Period", "Dönem");
        FromText.Text = T("От", "From", "Başlangıç");
        ToText.Text = T("До", "To", "Bitiş");
        ApplyFilters();
    }

    private string T(string ru, string en, string tr)
    {
        var language = _config.Language?.ToLowerInvariant() ?? "ru";
        return language switch { "en" => en, "tr" => tr, _ => ru };
    }
}
