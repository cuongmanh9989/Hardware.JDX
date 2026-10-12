using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

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

        public MainWindow()
        {
            InitializeComponent();
            HardwareTree.ItemsSource = _roots;
            Loaded += async (_, __) => await RefreshAsync(firstLoad: true);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync(firstLoad: false);

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
            foreach (TreeNode root in _roots) root.AppendText(sb, 0);
            Clipboard.SetText(sb.ToString());
            StatusText.Text = "Đã sao chép toàn bộ thông tin vào clipboard";
        }

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

                    Progress.Value = (i + 1d) / categories.Count;
                }

                StatusText.Text = $"Hoàn tất lúc {DateTime.Now:HH:mm:ss}";
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
