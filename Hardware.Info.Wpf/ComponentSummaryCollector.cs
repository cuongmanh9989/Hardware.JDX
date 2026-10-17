using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace Hardware.Info.Wpf
{
    /// <summary>Một dòng của bảng "Tổng hợp linh kiện".</summary>
    public sealed class SummaryRow
    {
        public SummaryRow(string type, string vendor, string product, string year, string spec, string serial, string identifier, string config)
        {
            Type = type; Vendor = vendor; Product = product; Year = year; Spec = spec; Serial = serial; Identifier = identifier; Config = config;
        }

        public string Type { get; }
        public string Vendor { get; }
        public string Product { get; }
        public string Year { get; }
        /// <summary>Cấu hình: một đặc điểm đặc trưng nhất của linh kiện (bộ nhớ, kích thước màn hình...).</summary>
        public string Spec { get; }
        public string Serial { get; }
        public string Identifier { get; }
        public string Config { get; }
    }

    /// <summary>Kết quả tổng hợp: các dòng đã sắp xếp và ghi chú lỗi của các nguồn không đọc được.</summary>
    public sealed class SummaryResult
    {
        public SummaryResult(List<SummaryRow> rows, List<string> notes) { Rows = rows; Notes = notes; }

        public List<SummaryRow> Rows { get; }
        public List<string> Notes { get; }
    }

    /// <summary>
    /// Tổng hợp thông tin tối thiểu cho từng linh kiện theo một khuôn thống nhất:
    /// định danh (S/N, ID), hãng, dòng sản phẩm, năm sản xuất, cấu hình đặc trưng và nguồn dữ liệu.
    /// Dữ liệu lấy từ <see cref="IHardwareInfo"/> (đã làm mới) cộng thêm EDID màn hình, mã hãng RAM/USB và S/N USB.
    /// </summary>
    public static class ComponentSummaryCollector
    {
        /// <summary>Giá trị hiển thị khi thiết bị không công bố năm sản xuất.</summary>
        private const string YearNotPublished = "N/A";

        /// <summary>Giá trị hiển thị khi không có dữ liệu.</summary>
        private const string NoData = "N/A";

        /// <summary>Định danh NVMe dạng 8 nhóm hex 4 ký tự phân cách bằng "_", vd "0000_..._3594.".</summary>
        private static readonly Regex NvmeIdentifier = new Regex(@"^([0-9A-F]{4}_){7}[0-9A-F]{4}\.?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Một linh kiện cần hiển thị.</summary>
        private sealed class Component
        {
            public string Type = string.Empty;
            public string Product = string.Empty;
            public string Vendor = string.Empty;
            public string Year = YearNotPublished;

            /// <summary>Đặc điểm đặc trưng nhất (cột "Cấu hình"); rỗng nếu không có.</summary>
            public string Spec = string.Empty;
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
            // Chuỗi toàn số 0 / toàn ký tự lặp (vd "00000000", "FFFFFFFF") là S/N chưa ghi
            if (text.Length >= 4 && text.Distinct().Count() == 1 && (text[0] == '0' || text[0] == 'F' || text[0] == 'f')) return false;
            string[] placeholders = { "Default string", "To be filled by O.E.M.", "None", "N/A", "Not Specified", "System Serial Number", "0", "00000000" };
            return !placeholders.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Dựng bảng "Tổng hợp linh kiện" từ dữ liệu đã làm mới trong <paramref name="hw"/>,
        /// sắp xếp theo loại linh kiện → hãng sản xuất → dòng sản phẩm.
        /// Lỗi ở một nguồn phụ (EDID, USB) chỉ làm thiếu dữ liệu đó, không làm hỏng cả bảng.
        /// </summary>
        public static SummaryResult BuildRows(IHardwareInfo hw)
        {
            var components = new List<Component>();
            var notes = new List<string>();

            Safe(notes, "Máy tính", () => AddComputer(hw, components));
            Safe(notes, "CPU", () => AddCpu(hw, components));
            // S/N RAM theo SMBIOS thường là 00000000; thử đọc thẳng từ chip SPD (cần Admin + PawnIO)
            SpdReadResult spd;
            try
            {
                spd = SpdSerialReader.Read();
            }
            catch (Exception ex)
            {
                // Read() đã tự bắt lỗi; đây là lớp bảo vệ cuối để việc đọc SPD không bao giờ làm hỏng cả bảng
                spd = new SpdReadResult(new List<SpdModule>(), "S/N RAM: lỗi khi đọc SPD (" + ex.GetBaseException().Message + ")");
            }
            if (spd.Note.Length > 0) notes.Add(spd.Note);
            Safe(notes, "RAM", () => AddMemory(hw, components, spd));
            Safe(notes, "Ổ đĩa", () => AddDrives(hw, components));
            Safe(notes, "GPU", () => AddGpu(hw, components));
            Safe(notes, "Màn hình", () => AddMonitors(components));
            Safe(notes, "Bàn phím/Chuột", () => AddInputDevices(hw, components));
            Safe(notes, "Pin", () => AddBattery(hw, components));
            Safe(notes, "Âm thanh", () => AddSound(components));

            // OrderBy là sắp xếp ổn định: cùng khóa thì giữ thứ tự thu thập
            List<SummaryRow> rows = components
                .OrderBy(c => c.Type, StringComparer.CurrentCulture)
                .ThenBy(c => c.Vendor, StringComparer.CurrentCulture)
                .ThenBy(c => c.Product, StringComparer.CurrentCulture)
                .Select(ToRow)
                .ToList();

            return new SummaryResult(rows, notes);
        }

        /// <summary>Chạy một bước thu thập; lỗi được ghi vào ghi chú thay vì làm hỏng toàn bộ.</summary>
        private static void Safe(List<string> notes, string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { notes.Add($"{name}: {ex.GetBaseException().Message}"); }
        }

        /// <summary>Chuyển một linh kiện thành một dòng của bảng.</summary>
        private static SummaryRow ToRow(Component c)
        {
            // Nhãn bắt đầu bằng "S/N" (S/N, S/N USB, S/N dạng số) vào cột S/N; còn lại (UUID, MAC, VID:PID...) vào cột ID
            List<KeyValuePair<string, string>> serials = c.Identifiers.Where(i => i.Key.StartsWith("S/N", StringComparison.Ordinal)).ToList();
            List<KeyValuePair<string, string>> ids = c.Identifiers.Except(serials).ToList();

            return new SummaryRow(
                c.Type,
                c.Vendor.Length > 0 ? c.Vendor : NoData,
                c.Product.Length > 0 ? c.Product : NoData,
                c.Year,
                c.Spec.Length > 0 ? c.Spec : NoData,
                // Chỉ một S/N thì hiện giá trị trần; nhiều S/N thì kèm nhãn để phân biệt
                serials.Count == 1 ? serials[0].Value : JoinPairs(serials, NoData),
                JoinPairs(ids, NoData),
                JoinPairs(c.Config, NoData));
        }

        private static string JoinPairs(List<KeyValuePair<string, string>> items, string emptyText) =>
            items.Count == 0 ? emptyText : string.Join(Environment.NewLine, items.Select(i => i.Key + ": " + i.Value));

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
                if (IsMeaningful(cs.Version.Trim())) c.Spec = "Phiên bản " + cs.Version.Trim();
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
                if (IsMeaningful(bios.Version.Trim())) c.Spec = "Phiên bản " + bios.Version.Trim();
                c.Cfg("Ngày phát hành", FormatWmiDate(bios.ReleaseDate));
                // Ngày phát hành dạng yyyyMMdd... → năm
                if (bios.ReleaseDate.Length >= 4 && int.TryParse(bios.ReleaseDate.Substring(0, 4), out int year) && year > 1980)
                    c.Year = year.ToString(CultureInfo.InvariantCulture);
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
                c.Spec = $"{cpu.NumberOfCores} nhân / {cpu.NumberOfLogicalProcessors} luồng, {(cpu.MaxClockSpeed / 1000d).ToString("0.##", CultureInfo.InvariantCulture)} GHz";
                c.Cfg("Số nhân / luồng", $"{cpu.NumberOfCores} / {cpu.NumberOfLogicalProcessors}");
                c.Cfg("Xung nhịp tối đa", cpu.MaxClockSpeed + " MHz");
                c.Cfg("Cache L2", cpu.L2CacheSize > 0 ? HardwareTreeBuilder.FormatBytes(cpu.L2CacheSize) : string.Empty);
                c.Cfg("Cache L3", cpu.L3CacheSize > 0 ? HardwareTreeBuilder.FormatBytes(cpu.L3CacheSize) : string.Empty);
                c.Cfg("Công suất (TDP)", "Chưa đọc được (cần cảm biến, ngoài phạm vi giai đoạn 1)");
                list.Add(c);
            }
        }

        private static void AddMemory(IHardwareInfo hw, List<Component> list, SpdReadResult spd)
        {
            // Các thanh SPD chưa ghép với thanh WMI nào; ghép theo part number, lần lượt theo thứ tự khe
            var unmatched = new List<SpdModule>(spd.Modules);

            foreach (Memory m in hw.MemoryList)
            {
                var c = new Component { Type = "RAM", Product = m.PartNumber.Trim(), Vendor = VendorTables.JedecVendor(m.Manufacturer), Source = "Win32_PhysicalMemory (SMBIOS)" };

                string serial = m.SerialNumber;
                if (!IsMeaningful(serial.Trim()))
                {
                    SpdModule? match = unmatched.FirstOrDefault(s => s.Serial.Length > 0 && string.Equals(s.PartNumber, m.PartNumber.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        unmatched.Remove(match);
                        serial = match.Serial;
                        c.Source = "Win32_PhysicalMemory + SPD (S/N đọc từ chip SPD)";
                    }
                }
                c.Id("S/N", serial);
                c.Id("Part number", m.PartNumber);
                c.Id("Mã hãng gốc", m.Manufacturer);
                c.Id("Vị trí khe", m.BankLabel);
                c.Cfg("Dung lượng", FormatGb(m.Capacity));
                uint running = m.ConfiguredClockSpeed > 0 ? m.ConfiguredClockSpeed : m.Speed;
                c.Spec = string.Join(" ", new[] { FormatGb(m.Capacity), m.MemoryType.ToString(), running > 0 ? running + " MHz" : string.Empty }.Where(t => t.Length > 0));
                c.Cfg("Tốc độ đang chạy", running > 0 ? running + " MHz" : string.Empty);

                // Tốc độ định mức (XMP/EXPO) suy ra từ mã linh kiện, vd "F4-3200C16-16GVK" → 3200 MT/s
                Match rated = Regex.Match(m.PartNumber, @"(?:^|[-_ ])(\d{4})C\d{1,2}", RegexOptions.IgnoreCase);
                if (rated.Success && uint.TryParse(rated.Groups[1].Value, out uint ratedSpeed) && ratedSpeed > running)
                    c.Cfg("Định mức theo mã linh kiện", ratedSpeed + " MT/s (BIOS chưa bật XMP/EXPO nên đang chạy thấp hơn)");
                c.Cfg("Loại", m.MemoryType.ToString());
                c.Cfg("Kiểu thanh", m.FormFactor.ToString());
                list.Add(c);
            }
        }

        private static void AddDrives(IHardwareInfo hw, List<Component> list)
        {
            foreach (Drive d in hw.DriveList)
            {
                if (IsVirtualName(d.Model)) continue;

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
                c.Spec = FormatGb(d.Size);
                c.Cfg("Dung lượng", FormatGb(d.Size));
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
                if (IsVirtualName(v.Name)) continue;

                var c = new Component { Type = "GPU", Product = v.Name, Vendor = v.Manufacturer, Source = "Win32_VideoController" };
                c.Id("Bộ xử lý", v.VideoProcessor);
                c.Spec = FormatGb(v.AdapterRAM);
                c.Cfg("Bộ nhớ (AdapterRAM)", FormatGb(v.AdapterRAM));
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
                // Màn hình đã rút / từng kết nối chỉ còn dấu vết trong registry: bỏ qua
                if (!e.IsPresent) continue;

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
                    c.Year = e.Year.ToString(CultureInfo.InvariantCulture);

                c.Spec = string.Join(", ", new[]
                {
                    e.DiagonalInches > 0 ? $"{e.DiagonalInches:0.#}\"" : string.Empty,
                    e.NativeWidth > 0 ? $"{e.NativeWidth} x {e.NativeHeight}" : string.Empty
                }.Where(t => t.Length > 0));

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
                // Thiết bị không có VID:PID (touchpad/PS2/ACPI) dùng PNPDeviceID làm khóa để không chặn nhầm bàn phím
                string key = m.VendorId.Length > 0 ? m.VendorId + ":" + m.ProductId : m.PNPDeviceID;
                if (!mouseKeys.Add(key)) continue;

                list.Add(BuildInput("Chuột", m.ProductName, m.Name, m.VendorId, m.ProductId, m.PNPDeviceID, usbSerials,
                    m.NumberOfButtons > 0 ? m.NumberOfButtons.ToString(CultureInfo.InvariantCulture) : string.Empty, "Win32_PointingDevice + Plug and Play"));
            }

            var keyboardKeys = new HashSet<string>();
            foreach (Keyboard k in hw.KeyboardList)
            {
                if (IsRemoteDesktop(k.PNPDeviceID)) continue;
                string key = k.VendorId.Length > 0 ? k.VendorId + ":" + k.ProductId : k.PNPDeviceID;
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

            string serial = Hardware.Info.Windows.PnpDeviceInfo.GetUsbSerial(pnpId);
            if (serial.Length == 0 && vid.Length > 0 && usbSerials.TryGetValue(vid + ":" + pid, out string? fallbackSerial))
                serial = fallbackSerial;
            c.Id("S/N USB", serial);
            c.Id("VID:PID", vid.Length > 0 ? vid + ":" + pid : string.Empty);
            c.Id("PNPDeviceID", pnpId);

            c.Cfg(type == "Chuột" ? "Số nút (theo Windows)" : "Số phím chức năng", buttonsOrKeys);
            if (buttonsOrKeys.Length > 0)
                c.Spec = buttonsOrKeys + (type == "Chuột" ? " nút" : " phím chức năng");
            if (type == "Chuột")
                c.Cfg("Độ nhạy (DPI)", "Không đọc được (nằm trong giao thức riêng của hãng, Windows không cung cấp)");
            return c;
        }

        private static bool IsRemoteDesktop(string pnpId) => pnpId.StartsWith("TERMINPUT_BUS", StringComparison.OrdinalIgnoreCase);

        private static void AddBattery(IHardwareInfo hw, List<Component> list)
        {
            foreach (Battery b in hw.BatteryList)
            {
                var c = new Component { Type = "Pin", Product = "Pin hệ thống", Source = "Win32_Battery" };
                c.Spec = b.DesignCapacity + " mWh";
                c.Cfg("Dung lượng thiết kế", b.DesignCapacity + " mWh");
                c.Cfg("Dung lượng đầy hiện tại", b.FullChargeCapacity + " mWh");
                c.Cfg("S/N, hãng, số chu kỳ", "Chưa đọc được (cần giai đoạn 2, chưa có pin để kiểm thử)");
                list.Add(c);
            }
        }

        /// <summary>Tên chứa dấu hiệu thiết bị ảo (phần mềm giả lập, máy ảo, màn hình ảo...).</summary>
        private static bool IsVirtualName(string name) =>
            Regex.IsMatch(name ?? string.Empty, @"virtual|vmware|hyper-v|\bTAP-|remote (display|audio)|basic display", RegexOptions.IgnoreCase);

        /// <summary>Thiết bị âm thanh vật lý đang hoạt động; bỏ thiết bị ảo (ROOT\, SWD\), của Microsoft và thiết bị lỗi/ngắt.</summary>
        private static void AddSound(List<Component> list)
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, Manufacturer, Description, Status, PNPDeviceID FROM Win32_SoundDevice");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    string name = Convert.ToString(mo["Name"], CultureInfo.InvariantCulture) ?? string.Empty;
                    string vendor = Convert.ToString(mo["Manufacturer"], CultureInfo.InvariantCulture) ?? string.Empty;
                    string description = Convert.ToString(mo["Description"], CultureInfo.InvariantCulture) ?? string.Empty;
                    string status = Convert.ToString(mo["Status"], CultureInfo.InvariantCulture) ?? string.Empty;
                    string pnp = Convert.ToString(mo["PNPDeviceID"], CultureInfo.InvariantCulture) ?? string.Empty;

                    if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pnp.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(vendor, "Microsoft", StringComparison.OrdinalIgnoreCase) || IsVirtualName(name)) continue;

                    var c = new Component { Type = "Âm thanh", Product = name, Vendor = vendor, Source = "Win32_SoundDevice (đã loại thiết bị ảo)" };
                    c.Id("PNPDeviceID", pnp);
                    c.Cfg("Mô tả", description == name ? string.Empty : description);
                    list.Add(c);
                }
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

        /// <summary>Đổi số byte sang GB (1 GB = 1024^3 byte, như Windows hiển thị), tối đa 1 chữ số thập phân; 0 thì để rỗng.</summary>
        private static string FormatGb(double bytes) =>
            bytes > 0 ? (bytes / (1024d * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB" : string.Empty;

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
