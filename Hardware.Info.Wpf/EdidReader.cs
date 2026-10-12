using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace Hardware.Info.Wpf
{
    /// <summary>Thông tin màn hình đọc từ EDID.</summary>
    public class EdidInfo
    {
        /// <summary>DeviceID của màn hình trong Plug and Play.</summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>Màn hình đang hiện diện (đang được Windows liệt kê) hay chỉ là bản ghi của màn hình đã từng kết nối.</summary>
        public bool IsPresent { get; set; }

        /// <summary>Mã hãng 3 ký tự (PNP ID), vd "DEL".</summary>
        public string VendorCode { get; set; } = string.Empty;

        /// <summary>Tên model do màn hình khai báo (vd "D22e-20").</summary>
        public string ModelName { get; set; } = string.Empty;

        /// <summary>Số serial dạng chuỗi (descriptor 0xFF); rỗng nếu màn hình không khai báo.</summary>
        public string SerialString { get; set; } = string.Empty;

        /// <summary>Số serial dạng số 32-bit; 0 nếu không khai báo.</summary>
        public uint SerialNumeric { get; set; }

        /// <summary>Mã sản phẩm 16-bit.</summary>
        public int ProductCode { get; set; }

        /// <summary>Năm sản xuất (hoặc năm model nếu tuần = 255); 0 nếu không hợp lệ.</summary>
        public int Year { get; set; }

        /// <summary>Tuần sản xuất (1-54); 0 nếu không khai báo.</summary>
        public int Week { get; set; }

        /// <summary>Chiều rộng hiển thị (cm); 0 nếu không khai báo.</summary>
        public int WidthCm { get; set; }

        /// <summary>Chiều cao hiển thị (cm); 0 nếu không khai báo.</summary>
        public int HeightCm { get; set; }

        /// <summary>Độ phân giải gốc rộng (pixel); 0 nếu không đọc được.</summary>
        public int NativeWidth { get; set; }

        /// <summary>Độ phân giải gốc cao (pixel); 0 nếu không đọc được.</summary>
        public int NativeHeight { get; set; }

        /// <summary>Phiên bản EDID, vd "1.4".</summary>
        public string EdidVersion { get; set; } = string.Empty;

        /// <summary>Đường chéo màn hình (inch), tính từ kích thước vật lý; 0 nếu không có.</summary>
        public double DiagonalInches => WidthCm > 0 && HeightCm > 0 ? Math.Sqrt(WidthCm * WidthCm + HeightCm * HeightCm) / 2.54 : 0;
    }

    /// <summary>
    /// Đọc EDID của các màn hình đang cắm từ registry (HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\...).
    /// Không cần quyền Administrator, khác với WMI WmiMonitorID (root\wmi) bị từ chối khi không có Admin.
    /// </summary>
    public static class EdidReader
    {
        /// <summary>
        /// Đọc EDID của mọi màn hình có lưu trong registry, đánh dấu màn hình nào đang hiện diện.
        /// Registry còn lưu cả màn hình đã rút ra, và khi dùng Remote Desktop thì màn hình thật không được liệt kê là đang hiện diện.
        /// </summary>
        public static List<EdidInfo> ReadAll()
        {
            var result = new List<EdidInfo>();
            HashSet<string> present = ReadPresentMonitorIds();

            using RegistryKey? display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (display == null) return result;

            foreach (string modelKeyName in display.GetSubKeyNames())
            {
                using RegistryKey? modelKey = display.OpenSubKey(modelKeyName);
                if (modelKey == null) continue;

                foreach (string instanceName in modelKey.GetSubKeyNames())
                {
                    using RegistryKey? parameters = modelKey.OpenSubKey(instanceName + @"\Device Parameters");
                    if (parameters?.GetValue("EDID") is byte[] edid && TryParse(edid, out EdidInfo? info))
                    {
                        info.DeviceId = @"DISPLAY\" + modelKeyName + @"\" + instanceName;
                        info.IsPresent = present.Contains(info.DeviceId);
                        result.Add(info);
                    }
                }
            }

            // Màn hình đang hiện diện lên trước
            return result.OrderByDescending(e => e.IsPresent).ToList();
        }

        /// <summary>DeviceID các màn hình đang hiện diện theo Plug and Play (không phân biệt hoa thường).</summary>
        private static HashSet<string> ReadPresentMonitorIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass='Monitor'");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                    ids.Add(Convert.ToString(mo["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty);
            }
            return ids;
        }

        /// <summary>Phân tích khối EDID 128 byte đầu tiên.</summary>
        private static bool TryParse(byte[] e, out EdidInfo info)
        {
            info = new EdidInfo();

            // Header cố định 00 FF FF FF FF FF FF 00
            if (e.Length < 128 || e[0] != 0x00 || e[7] != 0x00)
                return false;
            for (int i = 1; i <= 6; i++)
                if (e[i] != 0xFF) return false;

            // Mã hãng: 3 ký tự, mỗi ký tự 5 bit (1 = 'A'), lưu big-endian ở byte 8-9
            int id = (e[8] << 8) | e[9];
            info.VendorCode = new string(new[]
            {
                (char)('@' + ((id >> 10) & 0x1F)),
                (char)('@' + ((id >> 5) & 0x1F)),
                (char)('@' + (id & 0x1F)),
            });

            info.ProductCode = e[10] | (e[11] << 8);
            info.SerialNumeric = BitConverter.ToUInt32(e, 12);
            info.Week = e[16] == 0xFF ? 0 : e[16];
            info.Year = e[17] >= 1 ? 1990 + e[17] : 0;
            info.EdidVersion = e[18] + "." + e[19];
            info.WidthCm = e[21];
            info.HeightCm = e[22];

            // 4 khối 18 byte từ byte 54: khối timing (pixel clock != 0) hoặc descriptor văn bản (đầu 00 00 00 <tag>)
            for (int offset = 54; offset <= 108; offset += 18)
            {
                if (e[offset] != 0 || e[offset + 1] != 0)
                {
                    // Khối timing đầu tiên là độ phân giải gốc
                    if (info.NativeWidth == 0)
                    {
                        info.NativeWidth = e[offset + 2] | ((e[offset + 4] & 0xF0) << 4);
                        info.NativeHeight = e[offset + 5] | ((e[offset + 7] & 0xF0) << 4);
                    }
                    continue;
                }

                byte tag = e[offset + 3];
                if (tag != 0xFC && tag != 0xFF) continue;

                string text = Encoding.ASCII.GetString(e, offset + 5, 13).Split('\n')[0].Trim();
                if (tag == 0xFC) info.ModelName = text;
                else info.SerialString = text;
            }

            return true;
        }
    }
}
