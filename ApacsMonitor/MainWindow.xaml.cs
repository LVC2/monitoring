using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ApacsMonitor.Models;
using ApacsMonitor.Services;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace ApacsMonitor;

public partial class MainWindow : Window
{
    private const int PageSize = 50;
    private readonly ConfigurationService _configuration = new();
    private readonly SqlService _sql = new();
    private readonly ObservableCollection<EventRecord> _events = new();
    private readonly DispatcherTimer _timer;
    private AppConfiguration _config = new();
    private string _period = "today";
    private int _visibleCount = PageSize;
    private bool _initializingLanguage = true;
    private bool _isRefreshing;
    private bool _compactView;
    private DebugLogService _log = new(false);

    public MainWindow()
    {
        InitializeComponent();
        _period = "today";
        EmployeeCards.ItemsSource = _events;
        CompactEventsList.ItemsSource = _events;
        UpdatePeriodButtons();
        _initializingLanguage = false;
        _timer = new DispatcherTimer();
        _timer.Tick += async (_, _) => await RefreshEventsAsync(false);
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _period = "today";

        try
        {
            _config = _configuration.Load();
            _log = new DebugLogService(_config.Debug);
            _log.Info($"Application started. Debug={_config.Debug}, ConfigPath={GetConfigPath()}");
            _log.Info($"Database settings: Server={_config.Database.Server}, Database={_config.Database.Database}, Authentication={_config.Database.Authentication}, User={_config.Database.UserName}");
        }
        catch (Exception ex)
        {
            _log.Error("Configuration load failed.", ex);
            throw;
        }

        _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config.RefreshSeconds));
        SetLanguage(_config.Language);
        _period = "today";
        UpdatePeriodButtons();
        _sql.Configure(_config.Database, _config.Password);
        _log.Info("SQL service configured.");
        UpdateViewMode();
        await ConnectFromConfigAsync();

        _period = "today";
        UpdatePeriodButtons();
        ApplyFilters(false);
    }

    private async Task ConnectFromConfigAsync()
    {
        SetConnectionStatus("Подключение...", "Connecting...", "Bağlanıyor...", "#F59E0B");
        _log.Info("Testing SQL connection...");

        try
        {
            await _sql.TestConnectionAsync();
            _log.Info("SQL connection successful.");
            SetConnectionStatus("Подключено к БД", "Connected to database", "Veritabanına bağlandı", "#16A34A");
            _timer.Start();
            await RefreshEventsAsync(true);
        }
        catch (SqlException ex)
        {
            _timer.Stop();
            _log.Error("SQL connection failed.", ex);
            SetConnectionStatus("Нет подключения к БД", "Database disconnected", "Veritabanı bağlantısı yok", "#DC2626");
            LastUpdateText.Text = FormatSqlException(ex);
        }
        catch (Exception ex)
        {
            _timer.Stop();
            _log.Error("Unexpected connection error.", ex);
            SetConnectionStatus("Нет подключения к БД", "Database disconnected", "Veritabanı bağlantısı yok", "#DC2626");
            LastUpdateText.Text = ex.ToString();
        }
    }

    private async Task RefreshEventsAsync(bool resetPagination)
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;

        try
        {
            _log.Info("Reading access events...");
            var events = await _sql.GetRecentEventsAsync(5000);
            _log.Info($"Access events loaded: {events.Count}.");
            _events.Clear();
            foreach (var item in events)
                _events.Add(item);

            if (resetPagination)
                _visibleCount = PageSize;

            SetConnectionStatus("Подключено к БД", "Connected to database", "Veritabanına bağlandı", "#16A34A");
            ApplyFilters(false);
            LastUpdateText.Text = $"{T("Обновлено", "Updated", "Güncellendi")}: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _log.Error("Access journal read failed.", ex);
            SetConnectionStatus("Ошибка чтения журнала", "Journal read error", "Günlük okuma hatası", "#DC2626");
            LastUpdateText.Text = ex.Message;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private string GetConfigPath() =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(MainWindow).Assembly.Location) ?? AppContext.BaseDirectory,
            "appsettings.json");

    private static string FormatSqlException(SqlException ex)
    {
        var details = new List<string>
        {
            ex.Message,
            $"SQL error: {ex.Number}"
        };

        foreach (SqlError error in ex.Errors)
        {
            var line = $"[{error.Number}] {error.Message}";
            if (!details.Contains(line))
                details.Add(line);
        }

        if (ex.InnerException is not null)
            details.Add($"Inner: {ex.InnerException}");

        return string.Join(Environment.NewLine, details);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshEventsAsync(true);
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        _compactView = !_compactView;
        UpdateViewMode();
    }

    private void UpdateViewMode()
    {
        CardsScrollViewer.Visibility = _compactView ? Visibility.Collapsed : Visibility.Visible;
        CompactListBorder.Visibility = _compactView ? Visibility.Visible : Visibility.Collapsed;
        ViewModeButton.Content = _compactView
            ? T("▦  Карточки", "▦  Cards", "▦  Kartlar")
            : T("☷  Кратко", "☷  Compact", "☷  Kısa");
    }

    private async void WorkTimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EventRecord employeeEvent })
            return;

        try
        {
            var now = DateTime.Now;
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var events = await _sql.GetWorkTimeEventsAsync(monthStart.AddDays(-1), now.AddSeconds(1));
            var summaries = WorkTimeCalculator.Calculate(events, now);
            var summary = summaries.FirstOrDefault(x =>
                string.Equals(x.FullName, employeeEvent.FullName, StringComparison.CurrentCultureIgnoreCase));

            if (summary is null)
            {
                MessageBox.Show(
                    T("Не удалось рассчитать время на работе для сотрудника.", "Unable to calculate work time for this employee.", "Bu çalışan için çalışma süresi hesaplanamadı."),
                    T("Время сотрудника", "Employee work time", "Çalışanın çalışma süresi"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var message = string.Join(Environment.NewLine, new[]
            {
                summary.FullName,
                string.IsNullOrWhiteSpace(summary.CardNumber) ? "" : $"{T("Карта", "Card", "Kart")}: {summary.CardNumber}",
                "",
                $"{T("Сегодня", "Today", "Bugün")}: {summary.TodayText}",
                $"{T("Неделя", "Week", "Hafta")}: {summary.WeekText}",
                $"{T("Месяц", "Month", "Ay")}: {summary.MonthText}"
            });

            MessageBox.Show(
                message,
                T("Время сотрудника", "Employee work time", "Çalışanın çalışma süresi"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Error("Work time calculation failed.", ex);
            MessageBox.Show(
                ex.Message,
                T("Ошибка расчёта времени", "Work time calculation error", "Çalışma süresi hesaplama hatası"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetConnectionStatus(string ru, string en, string tr, string color)
    {
        ConnectionIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        ConnectionStatusText.Text = T(ru, en, tr);
    }

    private List<EventRecord> GetFilteredEvents()
    {
        var today = DateTime.Today;
        DateTime from;
        DateTime to = today.AddDays(1);

        switch (_period)
        {
            case "today":
                from = today;
                break;
            case "week":
                from = today.AddDays(-6);
                break;
            case "month":
                from = today.AddMonths(-1).Date;
                break;
            case "custom":
                from = FromDatePicker.SelectedDate?.Date ?? today;
                to = (ToDatePicker.SelectedDate?.Date ?? today).AddDays(1);
                break;
            default:
                from = DateTime.MinValue;
                to = DateTime.MaxValue;
                break;
        }

        IEnumerable<EventRecord> query = _events.Where(x => x.RealTime >= from && x.RealTime < to);

        var search = SearchTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x =>
                x.FullName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                x.CardNumber.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Location.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                x.Direction.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                x.ReaderName.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        query = _config.DisplayMode switch
        {
            "post" => query.Where(x => x.Location.StartsWith("Турникет", StringComparison.CurrentCultureIgnoreCase)),
            "canteen" => query.Where(x => x.Location.Equals("Столовая", StringComparison.CurrentCultureIgnoreCase)),
            _ => query
        };

        return query.OrderByDescending(x => x.RealTime).ToList();
    }

    private void ApplyFilters(bool resetPagination = false)
    {
        if (resetPagination)
            _visibleCount = PageSize;

        var result = GetFilteredEvents();
        var visibleItems = result.Take(_visibleCount).ToList();
        EmployeeCards.ItemsSource = visibleItems;
        CompactEventsList.ItemsSource = visibleItems;
        EventCountText.Text = $"{T("Проходов", "Access events", "Geçişler")}: {Math.Min(_visibleCount, result.Count)} / {result.Count}";
        ShowMoreButton.Visibility = _visibleCount < result.Count ? Visibility.Visible : Visibility.Collapsed;
        UpdateSearchHint();
    }

    private void UpdateSearchHint()
    {
        SearchHint.Visibility = string.IsNullOrWhiteSpace(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowMoreButton_Click(object sender, RoutedEventArgs e)
    {
        _visibleCount += PageSize;
        ApplyFilters(false);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters(false);
    }

    private void AllButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("all");
    private void TodayButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("today");
    private void WeekButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("week");
    private void MonthButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("month");

    private void PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        _period = "custom";
        PeriodPanel.Visibility = Visibility.Visible;
        UpdatePeriodButtons();
        ApplyFilters(false);
    }

    private void CustomDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_period == "custom")
            ApplyFilters(false);
    }

    private void SelectPeriod(string period)
    {
        _period = period;
        PeriodPanel.Visibility = Visibility.Collapsed;
        UpdatePeriodButtons();
        ApplyFilters(false);
    }

    private void UpdatePeriodButtons()
    {
        AllButton.Style = FindResource(_period == "all" ? "ActiveFilterButton" : "FilterButton") as Style;
        TodayButton.Style = FindResource(_period == "today" ? "ActiveFilterButton" : "FilterButton") as Style;
        WeekButton.Style = FindResource(_period == "week" ? "ActiveFilterButton" : "FilterButton") as Style;
        MonthButton.Style = FindResource(_period == "month" ? "ActiveFilterButton" : "FilterButton") as Style;
        PeriodButton.Style = FindResource(_period == "custom" ? "ActiveFilterButton" : "FilterButton") as Style;
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var rows = GetFilteredEvents();
        if (rows.Count == 0)
        {
            MessageBox.Show(T("Нет данных для выгрузки.", "There is no data to export.", "Dışa aktarılacak veri yok."),
                T("Выгрузка", "Export", "Dışa aktarma"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"apacs_access_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("Доступ");
            sheet.Cell(1, 1).Value = T("Время", "Time", "Saat");
            sheet.Cell(1, 2).Value = T("Сотрудник", "Employee", "Çalışan");
            sheet.Cell(1, 3).Value = T("Карта", "Card", "Kart");
            sheet.Cell(1, 4).Value = T("Место", "Location", "Konum");
            sheet.Cell(1, 5).Value = T("Направление", "Direction", "Yön");
            sheet.Cell(1, 6).Value = T("Считыватель", "Reader", "Okuyucu");

            for (var i = 0; i < rows.Count; i++)
            {
                var row = i + 2;
                var item = rows[i];
                sheet.Cell(row, 1).Value = item.RealTime;
                sheet.Cell(row, 1).Style.DateFormat.Format = "dd.MM.yyyy HH:mm:ss";
                sheet.Cell(row, 2).Value = item.FullName;
                sheet.Cell(row, 3).Value = item.CardNumber;
                sheet.Cell(row, 4).Value = item.Location;
                sheet.Cell(row, 5).Value = item.Direction;
                sheet.Cell(row, 6).Value = item.ReaderName;
            }

            sheet.Row(1).Style.Font.Bold = true;
            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents();
            workbook.SaveAs(dialog.FileName);
        }
        catch (Exception ex)
        {
            _log.Error("Excel export failed.", ex);
            MessageBox.Show(ex.Message, T("Ошибка выгрузки", "Export error", "Dışa aktarma hatası"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingLanguage || LanguageComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string language)
            return;

        _config = new AppConfiguration
        {
            Database = _config.Database,
            Password = _config.Password,
            RefreshSeconds = _config.RefreshSeconds,
            DisplayMode = _config.DisplayMode,
            Language = language.ToLowerInvariant(),
            Debug = _config.Debug
        };

        SetLanguage(_config.Language);
    }

    private void SetLanguage(string language)
    {
        _initializingLanguage = true;
        LanguageComboBox.SelectedIndex = language switch
        {
            "en" => 1,
            "tr" => 2,
            _ => 0
        };
        _initializingLanguage = false;

        Title = T("APACS Monitor — Журнал доступа", "APACS Monitor — Access Journal", "APACS Monitor — Erişim Günlüğü");
        SubtitleText.Text = T("Журнал доступа сотрудников", "Employee access journal", "Çalışan erişim günlüğü");
        SearchHint.Text = T("Поиск по ФИО или номеру карты", "Search by name or card number", "Ad veya kart numarasına göre ara");
        AllButton.Content = T("Все", "All", "Tümü");
        TodayButton.Content = T("Сегодня", "Today", "Bugün");
        WeekButton.Content = T("Неделя", "Week", "Hafta");
        MonthButton.Content = T("Месяц", "Month", "Ay");
        PeriodButton.Content = T("Период", "Period", "Dönem");
        RefreshButton.Content = T("↻  Обновить", "↻  Refresh", "↻  Yenile");
        ExportButton.Content = T("⇩  Выгрузить Excel", "⇩  Export Excel", "⇩  Excel'e aktar");
        ShowMoreButton.Content = T("Показать ещё 50", "Show 50 more", "50 daha göster");
        FromText.Text = T("От", "From", "Başlangıç");
        ToText.Text = T("До", "To", "Bitiş");
        UpdateViewMode();
        UpdatePeriodButtons();
        ApplyFilters(false);
    }

    private string T(string ru, string en, string tr)
    {
        var language = _config.Language?.ToLowerInvariant() ?? "ru";
        return language switch
        {
            "en" => en,
            "tr" => tr,
            _ => ru
        };
    }
}
