using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Cửa sổ chính: hiển thị thông tin phần cứng dạng cây, làm mới từng nhóm ở luồng nền để UI không bị treo.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Nguồn dữ liệu phần cứng từ thư viện Hardware.Info.</summary>
        private readonly IHardwareInfo _hardwareInfo = new HardwareInfo();

        /// <summary>Các nút gốc đang hiển thị trên TreeView.</summary>
        private readonly ObservableCollection<TreeNode> _roots = new ObservableCollection<TreeNode>();

        /// <summary>Đang làm mới hay không (chặn bấm trùng).</summary>
        private bool _isBusy;

        /// <summary>Các dòng của bảng "Tổng hợp linh kiện".</summary>
        private readonly ObservableCollection<SummaryRow> _summaryRows = new ObservableCollection<SummaryRow>();

        public MainWindow()
        {
            InitializeComponent();
            ConfigureAdminButton();
            HardwareTree.ItemsSource = _roots;
            SummaryGrid.ItemsSource = _summaryRows;
            CollectionViewSource.GetDefaultView(_summaryRows).Filter = MatchesFilters;
            Loaded += async (_, __) =>
            {
                // async void: lỗi lọt ra đây sẽ làm sập app, nên bắt hết
                try
                {
                    await RefreshAsync(firstLoad: true);
                    await StartPublishingAsync();
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Lỗi khởi động: " + ex.GetBaseException().Message;
                }
            };
        }

        /// <summary>Hiện thông báo ở thanh trạng thái (dùng cho trình xử lý lỗi toàn cục).</summary>
        public void ShowStatus(string message) => StatusText.Text = message;

        /// <summary>Đang gửi Kafka hay không (chặn gửi chồng nhau giữa nút bấm và bộ hẹn giờ).</summary>
        private bool _isPublishing;

        /// <summary>Cấu hình gửi Kafka; null nếu appsettings.json thiếu hoặc sai (khi đó chỉ hiển thị, không gửi).</summary>
        private DeviceInfoPublisherOptions? _publishOptions;

        private DispatcherTimer? _publishTimer;

        /// <summary>Gửi ngay một lần sau lần đọc đầu tiên, rồi mỗi IntervalMinutes phút làm mới dữ liệu và gửi lại.</summary>
        private async Task StartPublishingAsync()
        {
            _publishOptions = DeviceInfoPublisherOptions.Load(out string error);
            if (_publishOptions == null)
            {
                StatusText.Text = "Không gửi Kafka: " + error;
                return;
            }

            await PublishNowAsync();

            _publishTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_publishOptions.IntervalMinutes) };
            _publishTimer.Tick += async (_, __) =>
            {
                try
                {
                    // Đang làm mới thủ công hoặc đang gửi thì bỏ lượt này, lượt sau sẽ gửi
                    if (_isBusy || _isPublishing || _publishOptions == null) return;

                    // CPU đang bận (vd đang chơi game) thì hoãn tối đa MaxDeferMinutes để nhường tài nguyên
                    await LowPriority.WaitForIdleAsync(_publishOptions.MaxCpuPercent, TimeSpan.FromMinutes(_publishOptions.MaxDeferMinutes), CancellationToken.None);

                    await RefreshAsync(firstLoad: false);
                    await PublishNowAsync();
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Lỗi chu kỳ gửi Kafka: " + ex.GetBaseException().Message;
                }
            };
            _publishTimer.Start();
        }

        /// <summary>Gửi thủ công một bản tin với dữ liệu đang hiển thị; kết quả hiện ở thanh trạng thái.</summary>
        private async void PublishButton_Click(object sender, RoutedEventArgs e)
        {
            // Cấu hình chưa nạp được lúc khởi động (hoặc đã sửa file): nạp lại để thử
            string error = string.Empty;
            _publishOptions ??= DeviceInfoPublisherOptions.Load(out error);
            if (_publishOptions == null)
            {
                StatusText.Text = "Không gửi Kafka: " + error;
                return;
            }
            if (_summaryRows.Count == 0)
            {
                StatusText.Text = "Chưa có dữ liệu linh kiện để gửi (đợi đọc phần cứng xong)";
                return;
            }

            PublishButton.IsEnabled = false;
            try { await PublishNowAsync(); }
            catch (Exception ex) { StatusText.Text = "Gửi Kafka lỗi: " + ex.GetBaseException().Message; }
            finally { PublishButton.IsEnabled = true; }
        }

        private async Task PublishNowAsync()
        {
            if (_publishOptions == null || _summaryRows.Count == 0 || _isPublishing) return;

            _isPublishing = true;
            try
            {
                StatusText.Text = "Đang gửi Kafka...";
                PublishResult result = await DeviceInfoPublisher.PublishAsync(_publishOptions, _summaryRows.ToList());
                StatusText.Text = result.Message;
            }
            finally
            {
                _isPublishing = false;
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try { await RefreshAsync(firstLoad: false); }
            catch (Exception ex) { StatusText.Text = "Lỗi làm mới: " + ex.GetBaseException().Message; }
        }

        /// <summary>Hiển thị trạng thái quyền hiện tại trên nút Admin.</summary>
        private void ConfigureAdminButton()
        {
            if (IsAdministrator())
            {
                AdminButton.Content = "✔ Đang chạy quyền Admin";
                AdminButton.IsEnabled = false;
            }
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Khởi động lại app với quyền Admin (hiện hộp thoại UAC); nếu người dùng từ chối thì giữ nguyên app hiện tại.</summary>
        private void AdminButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được đường dẫn file chạy."),
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Application.Current.Shutdown();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                StatusText.Text = "Đã hủy yêu cầu quyền Admin";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Không khởi động lại được với quyền Admin: " + ex.Message;
            }
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (TreeNode root in _roots) root.SetExpandedRecursive(true);
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (TreeNode root in _roots) root.SetExpandedRecursive(false);
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            if (ViewTabs.SelectedItem == SummaryTab)
            {
                // Dạng TSV để dán thẳng vào Excel; ô nhiều dòng được bọc trong dấu nháy kép
                sb.AppendLine("Loại linh kiện\tHãng sản xuất\tDòng sản phẩm\tNăm sản xuất\tCấu hình\tS/N hoặc IMEI\tID định danh\tCấu hình chi tiết");
                foreach (SummaryRow r in CollectionViewSource.GetDefaultView(_summaryRows).Cast<SummaryRow>())
                    sb.AppendLine(string.Join("\t", new[] { r.Type, r.Vendor, r.Product, r.Year, r.Spec, r.Serial, r.Identifier, r.Config }.Select(TsvCell)));
            }
            else
            {
                foreach (TreeNode root in _roots) root.AppendText(sb, 0);
            }
            Clipboard.SetText(sb.ToString());
            StatusText.Text = "Đã sao chép thông tin vào clipboard";
        }

        /// <summary>Chuỗi lọc đang nhập ở từng cột, khóa theo tên thuộc tính của <see cref="SummaryRow"/>.</summary>
        private readonly Dictionary<string, string> _filters = new Dictionary<string, string>();

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var box = (TextBox)sender;
            _filters[(string)box.Tag] = box.Text.Trim();
            CollectionViewSource.GetDefaultView(_summaryRows).Refresh();
        }

        /// <summary>Các giá trị đang bị bỏ chọn ở bộ lọc dạng danh sách, khóa theo tên thuộc tính.</summary>
        private readonly Dictionary<string, HashSet<string>> _excluded = new Dictionary<string, HashSet<string>>();

        private static string GetCell(SummaryRow row, string key)
        {
            switch (key)
            {
                case "Type": return row.Type;
                case "Vendor": return row.Vendor;
                case "Product": return row.Product;
                case "Year": return row.Year;
                case "Spec": return row.Spec;
                case "Serial": return row.Serial;
                case "Identifier": return row.Identifier;
                default: return row.Config;
            }
        }

        /// <summary>Dòng được giữ lại khi thỏa mọi chuỗi lọc và không thuộc giá trị đã bỏ chọn.</summary>
        private bool MatchesFilters(object item)
        {
            var row = (SummaryRow)item;
            foreach (KeyValuePair<string, string> filter in _filters)
            {
                if (filter.Value.Length == 0) continue;
                if (GetCell(row, filter.Key).IndexOf(filter.Value, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
            }
            foreach (KeyValuePair<string, HashSet<string>> excluded in _excluded)
            {
                if (excluded.Value.Contains(GetCell(row, excluded.Key))) return false;
            }
            return true;
        }

        /// <summary>
        /// Mở danh sách giá trị của cột (kiểu Excel): mỗi giá trị kèm số dòng, bỏ chọn để ẩn các dòng đó.
        /// Danh sách dựng lại mỗi lần mở nên luôn khớp dữ liệu mới nhất.
        /// </summary>
        private void ValueFilterButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            string key = (string)button.Tag;
            _excluded.TryGetValue(key, out HashSet<string>? current);

            var groups = _summaryRows.GroupBy(r => GetCell(r, key)).OrderBy(g => g.Key, StringComparer.CurrentCulture).ToList();

            var panel = new StackPanel { Margin = new Thickness(8) };
            var all = new CheckBox { IsThreeState = true, Content = CountContent("Chọn tất cả", _summaryRows.Count, true), Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(all);

            var items = new List<(CheckBox Box, string Value)>();
            foreach (IGrouping<string, SummaryRow> group in groups)
            {
                var box = new CheckBox
                {
                    IsChecked = current == null || !current.Contains(group.Key),
                    Content = CountContent(group.Key.Replace(Environment.NewLine, " | "), group.Count(), false),
                    Margin = new Thickness(0, 2, 0, 2)
                };
                items.Add((box, group.Key));
                panel.Children.Add(box);
            }

            void SyncAll()
            {
                bool anyOn = items.Any(i => i.Box.IsChecked == true);
                bool anyOff = items.Any(i => i.Box.IsChecked != true);
                all.IsChecked = anyOn && anyOff ? (bool?)null : anyOn;
            }

            void Apply()
            {
                var unchecked_ = new HashSet<string>(items.Where(i => i.Box.IsChecked != true).Select(i => i.Value));
                if (unchecked_.Count == 0) _excluded.Remove(key); else _excluded[key] = unchecked_;
                button.Foreground = unchecked_.Count == 0 ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xB2));
                button.FontWeight = unchecked_.Count == 0 ? FontWeights.Normal : FontWeights.Bold;
                CollectionViewSource.GetDefaultView(_summaryRows).Refresh();
                SyncAll();
            }

            // Bấm "Chọn tất cả": còn mục chưa chọn thì chọn hết, ngược lại bỏ chọn hết
            all.Click += (_, __) =>
            {
                bool target = items.Any(i => i.Box.IsChecked != true);
                foreach ((CheckBox box, string _) in items) box.IsChecked = target;
                Apply();
            };
            foreach ((CheckBox box, string _) in items)
                box.Click += (_, __) => Apply();
            SyncAll();

            new Popup
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                IsOpen = true,
                Child = new Border
                {
                    Background = SystemColors.WindowBrush,
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    MinWidth = 260,
                    Child = new ScrollViewer { MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel }
                }
            };
        }

        /// <summary>Nội dung một mục danh sách: giá trị bên trái, số lượng (xám) bên phải.</summary>
        private static UIElement CountContent(string text, int count, bool bold)
        {
            var grid = new Grid { MinWidth = 230 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = text,
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = text,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            });
            var number = new TextBlock { Text = count.ToString(), Foreground = Brushes.Gray, Margin = new Thickness(16, 0, 0, 0) };
            Grid.SetColumn(number, 1);
            grid.Children.Add(number);
            return grid;
        }

        private static string TsvCell(string text) =>
            text.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

        /// <summary>
        /// Làm mới tuần tự từng nhóm ở luồng nền; mỗi nhóm xong thì cập nhật ngay lên UI.
        /// Giữ nguyên trạng thái mở/đóng của các nút theo đường dẫn.
        /// </summary>
        private async Task RefreshAsync(bool firstLoad)
        {
            if (_isBusy) return;
            _isBusy = true;
            RefreshButton.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.Value = 0;

            // Lưu các đường dẫn đang mở để khôi phục sau khi làm mới
            var expanded = new HashSet<string>();
            foreach (TreeNode root in _roots) CollectExpanded(root, expanded);

            bool live = LiveCountersCheck.IsChecked == true;
            var categories = HardwareTreeBuilder.CreateCategories(_hardwareInfo, live);

            try
            {
                for (int i = 0; i < categories.Count; i++)
                {
                    HardwareTreeBuilder.Category category = categories[i];
                    StatusText.Text = $"Đang đọc: {category.Title} ({i + 1}/{categories.Count})...";

                    // Đọc phần cứng và dựng nút ở luồng nền
                    TreeNode node = await Task.Run(() =>
                    {
                        using IDisposable lowPriority = LowPriority.ScopeThread();
                        try
                        {
                            category.Refresh();
                            return HardwareTreeBuilder.BuildCategoryNode(category, category.GetData());
                        }
                        catch (Exception ex)
                        {
                            var error = new TreeNode { Icon = category.Icon, Header = category.Title, Path = category.Title };
                            error.Children.Add(new TreeNode { Header = "Lỗi", Value = ex.GetBaseException().Message, Path = category.Title + "/error" });
                            return error;
                        }
                    });

                    // Cập nhật UI: lần đầu mở sẵn nhóm cấp 1, các lần sau khôi phục trạng thái cũ
                    if (firstLoad) node.IsExpanded = true;
                    else RestoreExpanded(node, expanded);

                    if (i < _roots.Count) _roots[i] = node;
                    else _roots.Add(node);

                    Progress.Value = (i + 1d) / (categories.Count + 1);
                }

                // Tổng hợp sau cùng vì cần dữ liệu đã làm mới của các nhóm trên
                StatusText.Text = "Đang tổng hợp linh kiện...";
                SummaryResult summary = await Task.Run(() =>
                {
                    using IDisposable lowPriority = LowPriority.ScopeThread();
                    return ComponentSummaryCollector.BuildRows(_hardwareInfo);
                });
                _summaryRows.Clear();
                foreach (SummaryRow row in summary.Rows) _summaryRows.Add(row);
                Progress.Value = 1;

                StatusText.Text = $"Hoàn tất lúc {DateTime.Now:HH:mm:ss}";
                if (summary.Notes.Count > 0)
                    StatusText.Text += " — Lưu ý: " + string.Join("; ", summary.Notes);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Lỗi: " + ex.GetBaseException().Message;
            }
            finally
            {
                Progress.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                _isBusy = false;
            }
        }

        /// <summary>Thu thập đường dẫn các nút đang mở.</summary>
        private static void CollectExpanded(TreeNode node, HashSet<string> set)
        {
            if (node.IsExpanded) set.Add(node.Path);
            foreach (TreeNode child in node.Children) CollectExpanded(child, set);
        }

        /// <summary>Mở lại các nút từng mở trước đó.</summary>
        private static void RestoreExpanded(TreeNode node, HashSet<string> set)
        {
            node.IsExpanded = set.Contains(node.Path);
            foreach (TreeNode child in node.Children) RestoreExpanded(child, set);
        }
    }
}
