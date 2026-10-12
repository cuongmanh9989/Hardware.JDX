using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Đọc dữ liệu từ <see cref="IHardwareInfo"/> và dựng thành cây <see cref="TreeNode"/> bằng reflection,
    /// nên mọi thuộc tính (kể cả danh sách lồng nhau như CpuCoreList, PartitionList) đều được hiển thị.
    /// </summary>
    public static class HardwareTreeBuilder
    {
        /// <summary>Độ sâu tối đa khi duyệt đối tượng, tránh vòng lặp tham chiếu.</summary>
        private const int MaxDepth = 8;

        /// <summary>Tên thuộc tính mang ý nghĩa dung lượng tính bằng byte (để hiển thị thêm KB/MB/GB).</summary>
        private static readonly Regex ByteSizeName = new Regex("(Size|Space|Capacity|RAM|Total|Available|Bytes)", RegexOptions.Compiled);

        /// <summary>Tên thuộc tính ưu tiên dùng làm tiêu đề cho một phần tử trong danh sách.</summary>
        private static readonly string[] TitleProperties = { "Name", "Caption", "Description", "Model", "Manufacturer" };

        /// <summary>
        /// Một nhóm phần cứng cấp 1: biểu tượng, tên, hàm làm mới dữ liệu và hàm lấy dữ liệu sau khi làm mới.
        /// </summary>
        public sealed record Category(string Icon, string Title, Action Refresh, Func<object?> GetData);

        /// <summary>Danh sách nhóm theo thứ tự hiển thị (CPU, RAM, ổ đĩa, GPU trước).</summary>
        public static IReadOnlyList<Category> CreateCategories(IHardwareInfo hw, bool includeLiveCounters)
        {
            return new List<Category>
            {
                new Category("🖥", "Hệ điều hành", hw.RefreshOperatingSystem, () => hw.OperatingSystem),
                new Category("⚙", "CPU", () => hw.RefreshCPUList(includePercentProcessorTime: includeLiveCounters), () => hw.CpuList),
                new Category("🧠", "RAM - Trạng thái bộ nhớ", hw.RefreshMemoryStatus, () => hw.MemoryStatus),
                new Category("🧠", "RAM - Thanh nhớ", hw.RefreshMemoryList, () => hw.MemoryList),
                new Category("💾", "Ổ đĩa", hw.RefreshDriveList, () => hw.DriveList),
                new Category("🎮", "GPU - Card đồ họa", () => hw.RefreshVideoControllerList(), () => hw.VideoControllerList),
                new Category("🧩", "Mainboard", hw.RefreshMotherboardList, () => hw.MotherboardList),
                new Category("🔧", "BIOS", hw.RefreshBIOSList, () => hw.BiosList),
                new Category("🏷", "Hệ thống máy tính", hw.RefreshComputerSystemList, () => hw.ComputerSystemList),
                new Category("🖵", "Màn hình", hw.RefreshMonitorList, () => hw.MonitorList),
                new Category("🌐", "Card mạng", () => hw.RefreshNetworkAdapterList(includeBytesPerSec: includeLiveCounters, includeNetworkAdapterConfiguration: true), () => hw.NetworkAdapterList),
                new Category("🔋", "Pin", hw.RefreshBatteryList, () => hw.BatteryList),
                new Category("⌨", "Bàn phím", hw.RefreshKeyboardList, () => hw.KeyboardList),
                new Category("🖱", "Chuột", hw.RefreshMouseList, () => hw.MouseList),
                new Category("🖨", "Máy in", hw.RefreshPrinterList, () => hw.PrinterList),
                new Category("🔊", "Âm thanh", hw.RefreshSoundDeviceList, () => hw.SoundDeviceList),
            };
        }

        /// <summary>Dựng nút cấp 1 cho một nhóm từ dữ liệu đã làm mới.</summary>
        public static TreeNode BuildCategoryNode(Category category, object? data)
        {
            var root = new TreeNode { Icon = category.Icon, Header = category.Title, Path = category.Title };

            if (data is IEnumerable list && !(data is string))
                AddListItems(root, list, 1);
            else if (data != null)
                AddProperties(root, data, 1);

            if (root.Children.Count == 0)
                root.Children.Add(new TreeNode { Header = "(không có dữ liệu)", Path = root.Path + "/-" });

            return root;
        }

        /// <summary>Thêm từng phần tử của một danh sách làm nút con.</summary>
        private static void AddListItems(TreeNode parent, IEnumerable items, int depth)
        {
            int index = 1;
            foreach (object? item in items)
            {
                if (item == null)
                    continue;

                if (IsSimple(item))
                {
                    parent.Children.Add(new TreeNode { Header = $"[{index}]", Value = FormatSimple(item, string.Empty), Path = $"{parent.Path}/[{index}]" });
                }
                else
                {
                    var node = new TreeNode { Header = $"{GetItemTitle(item)} #{index}", Path = $"{parent.Path}/#{index}" };
                    AddProperties(node, item, depth + 1);
                    parent.Children.Add(node);
                }

                index++;
            }
        }

        /// <summary>Thêm mọi thuộc tính công khai của một đối tượng làm nút con.</summary>
        private static void AddProperties(TreeNode parent, object obj, int depth)
        {
            if (depth > MaxDepth)
                return;

            bool isBattery = obj.GetType().Name == "Battery";

            foreach (PropertyInfo property in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                    continue;

                object? value;
                try { value = property.GetValue(obj); }
                catch (Exception ex) { value = $"(lỗi đọc: {ex.GetBaseException().Message})"; }

                string path = $"{parent.Path}/{property.Name}";

                if (value is IEnumerable list && !(value is string))
                {
                    var group = new TreeNode { Header = property.Name, Path = path };
                    AddListItems(group, list, depth + 1);
                    group.Value = $"({group.Children.Count})";
                    parent.Children.Add(group);
                }
                else if (value == null || IsSimple(value))
                {
                    string? sizeHint = !isBattery && ByteSizeName.IsMatch(property.Name) ? property.Name : null;
                    parent.Children.Add(new TreeNode { Header = property.Name, Value = FormatSimple(value, sizeHint ?? string.Empty), Path = path });
                }
                else
                {
                    var group = new TreeNode { Header = property.Name, Path = path };
                    AddProperties(group, value, depth + 1);
                    parent.Children.Add(group);
                }
            }
        }

        /// <summary>Giá trị "đơn giản" hiển thị trực tiếp bằng ToString (số, chuỗi, enum, ngày, IP...).</summary>
        private static bool IsSimple(object value)
        {
            Type type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal
                   || value is DateTime || value is DateTimeOffset || value is TimeSpan || value is Guid
                   || value is System.Net.IPAddress;
        }

        /// <summary>Định dạng giá trị; với số nguyên lớn có tên dạng dung lượng thì kèm theo đơn vị dễ đọc.</summary>
        private static string FormatSimple(object? value, string sizeHintName)
        {
            if (value == null)
                return "(trống)";

            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

            if (sizeHintName.Length > 0 && (value is ulong || value is long || value is uint || value is int))
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (number >= 1024)
                    return $"{text}  ({FormatBytes(number)})";
            }

            return text.Length == 0 ? "(trống)" : text;
        }

        /// <summary>Đổi số byte sang KB/MB/GB/TB.</summary>
        private static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }
            return $"{bytes:0.##} {units[unit]}";
        }

        /// <summary>Lấy tiêu đề dễ nhận biết cho một phần tử (Name/Caption/...), mặc định là tên kiểu.</summary>
        private static string GetItemTitle(object item)
        {
            Type type = item.GetType();
            foreach (string name in TitleProperties)
            {
                PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.PropertyType == typeof(string) && property.GetValue(item) is string text && !string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
            return type.Name;
        }
    }
}
