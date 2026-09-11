using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ApacsMonitor.Models;
using ApacsMonitor.Services;
using ClosedXML.Excel;
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

    public MainWindow()
    {
        InitializeComponent();
        _initializingLanguage = false;
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
        SetConnectionStatus("Подключение...", "Connecting...", "Bağlanıyor...", "#F59E0B");

        try
        {
            await _sql.TestConnectionAsync();
            SetConnectionStatus("Подключено к БД", "Connected to database", "Veritabanına bağlandı", "#16A34A");
            _timer.Start();
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            _timer.Stop();
            SetConnectionStatus("Нет подключения к БД", "Database disconnected", "Veritabanı bağlantısı yok", "#DC2626");
            LastUpdateText.Text = ex.Message;
        }
    }

    private async Task RefreshEventsAsync()
    {
        try
        {
            var events = await _sql.GetRecentEventsAsync(5000);
            _events.Clear();
            foreach (var item in events)
                _events.Add(item);

            SetConnectionStatus("Подключено к БД", "Connected to database", "Veritabanına bağlandı", "#16A34A");
            ApplyFilters();
            LastUpdateText.Text = $"{T("Обновлено", "Updated", "Güncellendi")}: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _timer.Stop();
            SetConnectionStatus("Ошибка чтения журнала", "Journal read error", "Günlük okuma hatası", "#DC2626");
            LastUpdateText.Text = ex.Message;
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
            "post" => query.Where(x => x.Location.Equals("Проходная", StringComparison.CurrentCultureIgnoreCase)),
            "canteen" => query.Where(x => x.Location.Equals("Столовая", StringComparison.CurrentCultureIgnoreCase)),
            _ => query
        };

        return query.OrderByDescending(x => x.RealTime).ToList();
    }

    private void ApplyFilters(bool resetPagination = true)
    {
        if (resetPagination)
            _visibleCount = PageSize;

        var result = GetFilteredEvents();
        EmployeeCards.ItemsSource = result.Take(_visibleCount).ToList();
        EventCountText.Text = $"{T("Проходов", "Access events", "Geçişler")}: {Math.Min(_visibleCount, result.Count)} / {result.Count}";
        ShowMoreButton.Visibility = _visibleCount < result.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMoreButton_Click(object sender, RoutedEventArgs e)
    {
        _visibleCount += PageSize;
        ApplyFilters(false);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();
    private void AllButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("all");
    private void TodayButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("today");
    private void WeekButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("week");
    private void MonthButton_Click(object sender, RoutedEventArgs e) => SelectPeriod("month");

    private void PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        _period = "custom";
        _visibleCount = PageSize;
        PeriodPanel.Visibility = Visibility.Visible;
        UpdatePeriodButtons();
        ApplyFilters(false);
    }

    private void CustomDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_period == "custom")
            ApplyFilters();
    }

    private void SelectPeriod(string period)
    {
        _period = period;
        _visibleCount = PageSize;
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
            Language = language.ToLowerInvariant()
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
        ExportButton.Content = T("⇩  Выгрузить Excel", "⇩  Export Excel", "⇩  Excel'e aktar");
        ShowMoreButton.Content = T("Показать ещё 50", "Show 50 more", "50 daha göster");
        FromText.Text = T("От", "From", "Başlangıç");
        ToText.Text = T("До", "To", "Bitiş");
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
