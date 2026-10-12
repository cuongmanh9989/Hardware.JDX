using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Tổng hợp thông tin tối thiểu cho từng linh kiện theo một khuôn thống nhất:
    /// định danh (S/N, ID), hãng, dòng sản phẩm, năm sản xuất, cấu hình đặc trưng và nguồn dữ liệu.
    /// Dữ liệu lấy từ <see cref="IHardwareInfo"/> (đã làm mới) cộng thêm EDID màn hình, mã hãng RAM/USB và S/N USB.
    /// </summary>
    public static class ComponentSummaryCollector
    {
        /// <summary>Giá trị hiển thị khi thiết bị không công bố năm sản xuất.</summary>
        private const string YearNotPublished = "Không có (thiết bị không công bố)";

        /// <summary>Giá trị hiển thị khi không có dữ liệu.</summary>
        private const string NoData = "Không có";

        /// <summary>Định danh NVMe dạng 8 nhóm hex 4 ký tự phân cách bằng "_", vd "0000_..._3594.".</summary>
        private static readonly Regex NvmeIdentifier = new Regex(@"^([0-9A-F]{4}_){7}[0-9A-F]{4}\.?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Một linh kiện cần hiển thị.</summary>
        private sealed class Component
        {
            public string Type = string.Empty;
            public string Product = string.Empty;
            public string Vendor = string.Empty;
            public string Year = YearNotPublished;
            public string Source = string.Empty;
            public readonly List<KeyValuePair<string, string>> Identifiers = new List<KeyValuePair<string, string>>();
            public readonly List<KeyValuePair<string, string>> Config = new List<KeyValuePair<string, string>>();

            /// <summary>Thêm định danh; bỏ qua giá trị rỗng hoặc giá trị mặc định vô nghĩa của nhà sản xuất.</summary>
            public void Id(string label, string? value)
            {
                string text = (value ?? string.Empty).Trim();
                if (IsMeaningful(text)) Identifiers.Add(new KeyValuePair<string, string>(label, text));
            }

            /// <summary>Thêm thông số cấu hình; bỏ qua giá trị rỗng.</summary>
            public void Cfg(string label, string? value)
            {
                string text = (value ?? string.Empty).Trim();
                if (text.Length > 0) Config.Add(new KeyValuePair<string, string>(label, text));
            }
        }

        /// <summary>Giá trị không mang thông tin (rỗng hoặc chuỗi mặc định của BIOS OEM).</summary>
        private static bool IsMeaningful(string text)
        {
            if (text.Length == 0) return false;
            string[] placeholders = { "Default string", "To be filled by O.E.M.", "None", "N/A", "Not Specified", "System Serial Number", "0", "00000000" };
            return !placeholders.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Dựng cây "Tổng hợp linh kiện" từ dữ liệu đã làm mới trong <paramref name="hw"/>.
        /// Lỗi ở một nguồn phụ (EDID, USB) chỉ làm thiếu dữ liệu đó, không làm hỏng cả cây.
        /// </summary>
        public static TreeNode BuildTree(IHardwareInfo hw)
        {
            var components = new List<Component>();
            var notes = new List<string>();

            Safe(notes, "Máy tính", () => AddComputer(hw, components));
            Safe(notes, "CPU", () => AddCpu(hw, components));
            Safe(notes, "RAM", () => AddMemory(hw, components));
            Safe(notes, "Ổ đĩa", () => AddDrives(hw, components));
            Safe(notes, "GPU", () => AddGpu(hw, components));
            Safe(notes, "Màn hình", () => AddMonitors(components));
            Safe(notes, "Bàn phím/Chuột", () => AddInputDevices(hw, components));
            Safe(notes, "Card mạng", () => AddNetwork(hw, components));
            Safe(notes, "Pin", () => AddBattery(hw, components));
            Safe(notes, "Máy in", () => AddSimple(hw.PrinterList.Select(p => (p.Name, string.Empty, p.Description)), "Máy in", components, "Win32_Printer"));
            Safe(notes, "Âm thanh", () => AddSimple(hw.SoundDeviceList.Select(s => (s.Name, s.Manufacturer, s.Description)), "Âm thanh", components, "Win32_SoundDevice"));

            const string rootTitle = "Tổng hợp linh kiện (chuẩn hóa)";
            var root = new TreeNode { Icon = "📋", Header = rootTitle, Path = rootTitle };

            // Gom theo loại, giữ thứ tự xuất hiện
            foreach (IGrouping<string, Component> group in components.GroupBy(c => c.Type))
            {
                var groupNode = new TreeNode { Header = group.Key, Value = $"({group.Count()})", Path = rootTitle + "/" + group.Key };
                int index = 1;
                foreach (Component component in group)
                    groupNode.Children.Add(ToNode(component, groupNode.Path, index++));
                root.Children.Add(groupNode);
            }

            if (notes.Count > 0)
            {
                var noteNode = new TreeNode { Header = "Lưu ý khi đọc dữ liệu", Path = rootTitle + "/notes" };
                foreach (string note in notes)
                    noteNode.Children.Add(new TreeNode { Header = note, Path = noteNode.Path + "/" + note });
                root.Children.Add(noteNode);
            }

            return root;
        }

        /// <summary>Chạy một bước thu thập; lỗi được ghi vào ghi chú thay vì làm hỏng toàn bộ.</summary>
        private static void Safe(List<string> notes, string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { notes.Add($"{name}: {ex.GetBaseException().Message}"); }
        }

        /// <summary>Chuyển một linh kiện thành nút cây theo khuôn thống nhất.</summary>
        private static TreeNode ToNode(Component c, string parentPath, int index)
        {
            // Tiêu đề: bỏ hãng nếu chưa tra được (câu dài) hoặc tên sản phẩm đã bắt đầu bằng tên hãng
            bool showVendor = c.Vendor.Length > 0 && !c.Vendor.StartsWith("Chưa tra được", StringComparison.Ordinal)
                              && !c.Product.StartsWith(c.Vendor, StringComparison.OrdinalIgnoreCase);
            string title = showVendor ? $"{c.Vendor} {c.Product}".Trim() : c.Product;
            var node = new TreeNode { Header = title.Length > 0 ? title : c.Type, Path = $"{parentPath}/#{index}" };

            node.Children.Add(Leaf(node, "Hãng sản xuất", c.Vendor.Length > 0 ? c.Vendor : NoData));
            node.Children.Add(Leaf(node, "Dòng sản phẩm", c.Product.Length > 0 ? c.Product : NoData));
            node.Children.Add(Leaf(node, "Năm sản xuất", c.Year));
            node.Children.Add(Group(node, "Định danh (S/N, ID)", c.Identifiers, "Không có (thiết bị/BIOS không khai báo)"));
            node.Children.Add(Group(node, "Cấu hình đặc trưng", c.Config, NoData));
            node.Children.Add(Leaf(node, "Nguồn dữ liệu", c.Source));

            return node;
        }

        private static TreeNode Leaf(TreeNode parent, string header, string value) =>
            new TreeNode { Header = header, Value = value, Path = parent.Path + "/" + header };

        private static TreeNode Group(TreeNode parent, string header, List<KeyValuePair<string, string>> items, string emptyText)
        {
            var group = new TreeNode { Header = header, Path = parent.Path + "/" + header };
            if (items.Count == 0)
                group.Value = emptyText;
            foreach (KeyValuePair<string, string> item in items)
                group.Children.Add(Leaf(group, item.Key, item.Value));
            return group;
        }

        // ---------------------------------------------------------------- Từng loại linh kiện

        private static void AddComputer(IHardwareInfo hw, List<Component> list)
        {
            foreach (ComputerSystem cs in hw.ComputerSystemList)
            {
                var c = new Component { Type = "Máy tính", Product = cs.Name, Vendor = cs.Vendor, Source = "Win32_ComputerSystemProduct" };
                c.Id("S/N", cs.IdentifyingNumber);
                c.Id("UUID", cs.UUID);
                c.Id("SKU", cs.SKUNumber);
                c.Cfg("Phiên bản", cs.Version);
                list.Add(c);
            }

            foreach (Motherboard mb in hw.MotherboardList)
            {
                var c = new Component { Type = "Mainboard", Product = mb.Product, Vendor = mb.Manufacturer, Source = "Win32_BaseBoard" };
                c.Id("S/N", mb.SerialNumber);
                list.Add(c);
            }

            foreach (BIOS bios in hw.BiosList)
            {
                var c = new Component { Type = "BIOS", Product = bios.Name, Vendor = bios.Manufacturer, Source = "Win32_BIOS (năm = ngày phát hành BIOS)" };
                c.Id("S/N", bios.SerialNumber);
                c.Cfg("Phiên bản", bios.Version);
                c.Cfg("Ngày phát hành", FormatWmiDate(bios.ReleaseDate));
                // Ngày phát hành dạng yyyyMMdd... → năm
                if (bios.ReleaseDate.Length >= 4 && int.TryParse(bios.ReleaseDate.Substring(0, 4), out int year) && year > 1980)
                    c.Year = year + " (năm phát hành BIOS, không phải năm sản xuất mainboard)";
                list.Add(c);
            }
        }

        private static void AddCpu(IHardwareInfo hw, List<Component> list)
        {
            foreach (CPU cpu in hw.CpuList)
            {
                var c = new Component { Type = "CPU", Product = cpu.Name, Vendor = CpuVendor(cpu.Manufacturer), Source = "Win32_Processor" };
                c.Id("ProcessorId", cpu.ProcessorId);
                c.Id("Socket", cpu.SocketDesignation);
                c.Cfg("Số nhân / luồng", $"{cpu.NumberOfCores} / {cpu.NumberOfLogicalProcessors}");
                c.Cfg("Xung nhịp tối đa", cpu.MaxClockSpeed + " MHz");
                c.Cfg("Cache L2", cpu.L2CacheSize > 0 ? HardwareTreeBuilder.FormatBytes(cpu.L2CacheSize) : string.Empty);
                c.Cfg("Cache L3", cpu.L3CacheSize > 0 ? HardwareTreeBuilder.FormatBytes(cpu.L3CacheSize) : string.Empty);
                c.Cfg("Công suất (TDP)", "Chưa đọc được (cần cảm biến, ngoài phạm vi giai đoạn 1)");
                list.Add(c);
            }
        }

        private static void AddMemory(IHardwareInfo hw, List<Component> list)
        {
            foreach (Memory m in hw.MemoryList)
            {
                var c = new Component { Type = "RAM", Product = m.PartNumber.Trim(), Vendor = VendorTables.JedecVendor(m.Manufacturer), Source = "Win32_PhysicalMemory (SMBIOS)" };
                c.Id("S/N", m.SerialNumber);
                c.Id("Part number", m.PartNumber);
                c.Id("Mã hãng gốc", m.Manufacturer);
                c.Id("Vị trí khe", m.BankLabel);
                c.Cfg("Dung lượng", HardwareTreeBuilder.FormatBytes(m.Capacity));
                c.Cfg("Tốc độ", m.Speed + " MHz");
                c.Cfg("Loại", m.MemoryType.ToString());
                c.Cfg("Kiểu thanh", m.FormFactor.ToString());
                list.Add(c);
            }
        }

        private static void AddDrives(IHardwareInfo hw, List<Component> list)
        {
            foreach (Drive d in hw.DriveList)
            {
                // Hãng "(Standard disk drives)" là tên driver, không phải hãng thật
                string vendor = d.Manufacturer.StartsWith("(Standard", StringComparison.OrdinalIgnoreCase) ? string.Empty : d.Manufacturer;
                var c = new Component { Type = "Ổ đĩa", Product = d.Model, Vendor = vendor, Source = "Win32_DiskDrive" };

                string serial = d.SerialNumber.Trim();
                if (NvmeIdentifier.IsMatch(serial))
                {
                    // Windows báo định danh NVMe (NGUID/EUI-64) thay vì S/N in trên nhãn
                    c.Id("Định danh NVMe (NGUID/EUI-64)", serial.TrimEnd('.').Replace("_", string.Empty));
                }
                else
                {
                    c.Id("S/N", serial);
                }

                c.Id("Firmware", d.FirmwareRevision);
                c.Cfg("Dung lượng", HardwareTreeBuilder.FormatBytes(d.Size));
                c.Cfg("Loại phương tiện", d.MediaType);
                c.Cfg("Số phân vùng", d.PartitionList.Count.ToString(CultureInfo.InvariantCulture));
                c.Cfg("Sức khỏe SMART / giờ chạy / TBW", "Chưa đọc được (cần quyền Admin + driver, ngoài phạm vi giai đoạn 1)");
                list.Add(c);
            }
        }

        private static void AddGpu(IHardwareInfo hw, List<Component> list)
        {
            foreach (VideoController v in hw.VideoControllerList)
            {
                // Bỏ card ảo của Remote Desktop / Basic Display
                if (v.Name.IndexOf("Remote Display", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.Name.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                var c = new Component { Type = "GPU", Product = v.Name, Vendor = v.Manufacturer, Source = "Win32_VideoController" };
                c.Id("Bộ xử lý", v.VideoProcessor);
                c.Cfg("Bộ nhớ (AdapterRAM)", v.AdapterRAM > 0 ? HardwareTreeBuilder.FormatBytes(v.AdapterRAM) : string.Empty);
                c.Cfg("Phiên bản driver", v.DriverVersion);
                c.Cfg("Ngày driver", FormatWmiDate(v.DriverDate));
                c.Cfg("Độ phân giải hiện tại", v.CurrentHorizontalResolution > 0 ? $"{v.CurrentHorizontalResolution} x {v.CurrentVerticalResolution} @ {v.CurrentRefreshRate} Hz" : string.Empty);
                list.Add(c);
            }
        }

        private static void AddMonitors(List<Component> list)
        {
            foreach (EdidInfo e in EdidReader.ReadAll())
            {
                var c = new Component
                {
                    Type = "Màn hình",
                    Product = e.ModelName.Length > 0 ? e.ModelName : "Mã sản phẩm 0x" + e.ProductCode.ToString("X4", CultureInfo.InvariantCulture),
                    Vendor = VendorTables.EdidVendor(e.VendorCode),
                    Source = "EDID (registry, không cần Admin)"
                };

                c.Id("S/N", e.SerialString);
                if (e.SerialNumeric != 0) c.Id("S/N dạng số", e.SerialNumeric.ToString(CultureInfo.InvariantCulture));
                c.Id("Mã hãng EDID", e.VendorCode);
                c.Id("Mã sản phẩm", "0x" + e.ProductCode.ToString("X4", CultureInfo.InvariantCulture));
                c.Id("DeviceID", e.DeviceId);

                if (e.Year >= 1990)
                    c.Year = e.Week > 0 ? $"{e.Year} (tuần {e.Week})" : e.Year.ToString(CultureInfo.InvariantCulture);

                if (e.DiagonalInches > 0)
                    c.Cfg("Kích thước", $"{e.DiagonalInches:0.#}\" ({e.WidthCm} x {e.HeightCm} cm)");
                if (e.NativeWidth > 0)
                    c.Cfg("Độ phân giải gốc", $"{e.NativeWidth} x {e.NativeHeight}");
                c.Cfg("Trạng thái", e.IsPresent ? "Đang hiện diện" : "Đã từng kết nối (hiện Windows không liệt kê, ví dụ khi dùng Remote Desktop hoặc đã rút)");
                c.Cfg("Phiên bản EDID", e.EdidVersion);
                c.Cfg("Độ sáng", "Chưa đọc được (cần DDC/CI, ngoài phạm vi giai đoạn 1)");
                list.Add(c);
            }
        }

        private static void AddInputDevices(IHardwareInfo hw, List<Component> list)
        {
            Dictionary<string, string> usbSerials = ReadUsbSerials();

            // Chuột trước, để loại giao diện "bàn phím" ảo của chính con chuột đó (phím macro)
            var mouseKeys = new HashSet<string>();
            foreach (Mouse m in hw.MouseList)
            {
                if (IsRemoteDesktop(m.PNPDeviceID)) continue;
                string key = m.VendorId + ":" + m.ProductId;
                if (!mouseKeys.Add(key)) continue;

                list.Add(BuildInput("Chuột", m.ProductName, m.Name, m.VendorId, m.ProductId, m.PNPDeviceID, usbSerials,
                    m.NumberOfButtons > 0 ? m.NumberOfButtons.ToString(CultureInfo.InvariantCulture) : string.Empty, "Win32_PointingDevice + Plug and Play"));
            }

            var keyboardKeys = new HashSet<string>();
            foreach (Keyboard k in hw.KeyboardList)
            {
                if (IsRemoteDesktop(k.PNPDeviceID)) continue;
                string key = k.VendorId + ":" + k.ProductId;
                if (mouseKeys.Contains(key) || !keyboardKeys.Add(key)) continue;

                list.Add(BuildInput("Bàn phím", k.ProductName, k.Name, k.VendorId, k.ProductId, k.PNPDeviceID, usbSerials,
                    k.NumberOfFunctionKeys > 0 ? k.NumberOfFunctionKeys.ToString(CultureInfo.InvariantCulture) : string.Empty, "Win32_Keyboard + Plug and Play"));
            }
        }

        private static Component BuildInput(string type, string productName, string fallbackName, string vid, string pid, string pnpId,
            Dictionary<string, string> usbSerials, string buttonsOrKeys, string source)
        {
            var c = new Component
            {
                Type = type,
                Product = productName.Length > 0 ? productName : fallbackName,
                Vendor = vid.Length > 0 ? VendorTables.UsbVendor(vid) : string.Empty,
                Source = source
            };

            if (vid.Length > 0 && c.Vendor.Length == 0)
                c.Vendor = "Chưa tra được (VID " + vid + ")";

            if (vid.Length > 0 && usbSerials.TryGetValue(vid + ":" + pid, out string? serial))
                c.Id("S/N USB", serial);
            c.Id("VID:PID", vid.Length > 0 ? vid + ":" + pid : string.Empty);
            c.Id("PNPDeviceID", pnpId);

            c.Cfg(type == "Chuột" ? "Số nút (theo Windows)" : "Số phím chức năng", buttonsOrKeys);
            if (type == "Chuột")
                c.Cfg("Độ nhạy (DPI)", "Không đọc được (nằm trong giao thức riêng của hãng, Windows không cung cấp)");
            return c;
        }

        private static bool IsRemoteDesktop(string pnpId) => pnpId.StartsWith("TERMINPUT_BUS", StringComparison.OrdinalIgnoreCase);

        private static void AddNetwork(IHardwareInfo hw, List<Component> list)
        {
            foreach (NetworkAdapter n in hw.NetworkAdapterList)
            {
                var c = new Component { Type = "Card mạng", Product = n.Name, Vendor = n.Manufacturer, Source = "Win32_NetworkAdapter" };
                c.Id("MAC", n.MACAddress);
                c.Cfg("Tốc độ liên kết", n.Speed > 0 && n.Speed != long.MaxValue ? HardwareTreeBuilder.FormatBytes(n.Speed / 8d) + "/s" : string.Empty);
                c.Cfg("Loại", n.AdapterType);
                list.Add(c);
            }
        }

        private static void AddBattery(IHardwareInfo hw, List<Component> list)
        {
            foreach (Battery b in hw.BatteryList)
            {
                var c = new Component { Type = "Pin", Product = "Pin hệ thống", Source = "Win32_Battery" };
                c.Cfg("Dung lượng thiết kế", b.DesignCapacity + " mWh");
                c.Cfg("Dung lượng đầy hiện tại", b.FullChargeCapacity + " mWh");
                c.Cfg("S/N, hãng, số chu kỳ", "Chưa đọc được (cần giai đoạn 2, chưa có pin để kiểm thử)");
                list.Add(c);
            }
        }

        private static void AddSimple(IEnumerable<(string Name, string Vendor, string Description)> items, string type, List<Component> list, string source)
        {
            foreach ((string name, string vendor, string description) in items)
            {
                var c = new Component { Type = type, Product = name, Vendor = vendor, Source = source };
                c.Cfg("Mô tả", description == name ? string.Empty : description);
                list.Add(c);
            }
        }

        // ---------------------------------------------------------------- Hỗ trợ

        /// <summary>
        /// S/N của thiết bị USB, lấy từ phần cuối DeviceID "USB\VID_xxxx&amp;PID_xxxx\&lt;S/N&gt;".
        /// Windows tự sinh chuỗi có dấu '&amp;' cho thiết bị không có S/N, nên loại bỏ các chuỗi đó.
        /// </summary>
        private static Dictionary<string, string> ReadUsbSerials()
        {
            var result = new Dictionary<string, string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\cimv2", @"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\VID_%'");
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        string id = Convert.ToString(mo["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty;
                        Match match = Regex.Match(id, @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\([^&\\]+)$", RegexOptions.IgnoreCase);
                        if (match.Success)
                            result[match.Groups[1].Value.ToUpperInvariant() + ":" + match.Groups[2].Value.ToUpperInvariant()] = match.Groups[3].Value;
                    }
                }
            }
            catch (Exception)
            {
                // Không đọc được S/N USB: bỏ qua, các trường khác vẫn hiển thị
            }
            return result;
        }

        /// <summary>Đổi ngày WMI "20241021000000.000000+000" sang "2024-10-21".</summary>
        private static string FormatWmiDate(string wmiDate) =>
            wmiDate.Length >= 8 && int.TryParse(wmiDate.Substring(0, 8), out _) ? $"{wmiDate.Substring(0, 4)}-{wmiDate.Substring(4, 2)}-{wmiDate.Substring(6, 2)}" : wmiDate;

        /// <summary>Chuẩn hóa tên hãng CPU từ chuỗi vendor của CPUID.</summary>
        private static string CpuVendor(string manufacturer)
        {
            switch (manufacturer)
            {
                case "GenuineIntel": return "Intel";
                case "AuthenticAMD": return "AMD";
                default: return manufacturer;
            }
        }
    }
}
