using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;

namespace Hardware.Info.Wpf
{
    /// <summary>Quạt/thiết bị làm mát theo WMI Win32_Fan.</summary>
    public class FanInfo
    {
        /// <summary>Tên thiết bị làm mát.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Trạng thái (OK, Degraded...).</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Có làm mát chủ động (quạt) hay không.</summary>
        public string ActiveCooling { get; set; } = string.Empty;

        /// <summary>Quạt có chạy tốc độ thay đổi hay không (thường BIOS không khai báo).</summary>
        public string VariableSpeed { get; set; } = string.Empty;

        /// <summary>Tốc độ mong muốn (RPM) do BIOS khai báo; thường để trống.</summary>
        public string DesiredSpeedRpm { get; set; } = string.Empty;
    }

    /// <summary>Cảm biến nhiệt theo WMI Win32_TemperatureProbe.</summary>
    public class TemperatureProbeInfo
    {
        /// <summary>Tên cảm biến.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Mô tả cảm biến.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Nhiệt độ hiện tại (°C); để trống nếu BIOS không cung cấp giá trị.</summary>
        public string CurrentTemperatureCelsius { get; set; } = string.Empty;
    }

    /// <summary>Vùng nhiệt ACPI theo WMI MSAcpi_ThermalZoneTemperature.</summary>
    public class ThermalZoneInfo
    {
        /// <summary>Tên vùng nhiệt.</summary>
        public string InstanceName { get; set; } = string.Empty;

        /// <summary>Nhiệt độ hiện tại (°C).</summary>
        public string CurrentTemperatureCelsius { get; set; } = string.Empty;

        /// <summary>Ngưỡng nhiệt tới hạn (°C).</summary>
        public string CriticalTripPointCelsius { get; set; } = string.Empty;
    }

    /// <summary>Kết quả đọc quạt và nhiệt độ.</summary>
    public class FanTemperatureInfo
    {
        /// <summary>Ghi chú giới hạn của WMI.</summary>
        public string Note { get; set; } = "Windows/WMI chỉ cho biết có quạt và trạng thái; tốc độ quạt (RPM) thật chỉ đọc được qua driver của hãng hoặc phần mềm chuyên dụng.";

        /// <summary>Danh sách quạt / thiết bị làm mát.</summary>
        public List<FanInfo> FanList { get; } = new List<FanInfo>();

        /// <summary>Danh sách cảm biến nhiệt.</summary>
        public List<TemperatureProbeInfo> TemperatureProbeList { get; } = new List<TemperatureProbeInfo>();

        /// <summary>Danh sách vùng nhiệt ACPI.</summary>
        public List<ThermalZoneInfo> ThermalZoneList { get; } = new List<ThermalZoneInfo>();

        /// <summary>Lỗi gặp phải khi đọc từng nguồn (vd bị từ chối quyền), để người dùng biết vì sao trống.</summary>
        public List<string> ErrorList { get; } = new List<string>();
    }

    /// <summary>Đọc thông tin quạt và nhiệt độ qua WMI (không cần driver, không cần quyền Administrator cho Win32_Fan).</summary>
    public static class FanTemperatureReader
    {
        public static FanTemperatureInfo Read()
        {
            var info = new FanTemperatureInfo();

            Query(info, @"root\cimv2", "SELECT Name, Status, ActiveCooling, VariableSpeed, DesiredSpeed FROM Win32_Fan", mo =>
                info.FanList.Add(new FanInfo
                {
                    Name = Text(mo["Name"]),
                    Status = Text(mo["Status"]),
                    ActiveCooling = Text(mo["ActiveCooling"]),
                    VariableSpeed = Text(mo["VariableSpeed"]),
                    DesiredSpeedRpm = Text(mo["DesiredSpeed"])
                }));

            Query(info, @"root\cimv2", "SELECT Name, Description, CurrentReading FROM Win32_TemperatureProbe", mo =>
                info.TemperatureProbeList.Add(new TemperatureProbeInfo
                {
                    Name = Text(mo["Name"]),
                    Description = Text(mo["Description"]),
                    // CurrentReading tính bằng phần mười độ C
                    CurrentTemperatureCelsius = mo["CurrentReading"] is int reading ? (reading / 10.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty
                }));

            Query(info, @"root\wmi", "SELECT InstanceName, CurrentTemperature, CriticalTripPoint FROM MSAcpi_ThermalZoneTemperature", mo =>
                info.ThermalZoneList.Add(new ThermalZoneInfo
                {
                    InstanceName = Text(mo["InstanceName"]),
                    CurrentTemperatureCelsius = TenthsKelvinToCelsius(mo["CurrentTemperature"]),
                    CriticalTripPointCelsius = TenthsKelvinToCelsius(mo["CriticalTripPoint"])
                }));

            return info;
        }

        /// <summary>Chạy một truy vấn WMI; lỗi của một nguồn không làm hỏng các nguồn khác.</summary>
        private static void Query(FanTemperatureInfo info, string scope, string query, Action<ManagementBaseObject> map)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, query);
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    using (mo) map(mo);
                }
            }
            catch (Exception ex)
            {
                info.ErrorList.Add($"{query.Substring(query.IndexOf("FROM", StringComparison.Ordinal) + 5)}: {ex.GetBaseException().Message}");
            }
        }

        private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        /// <summary>Đổi phần mười Kelvin (đơn vị của ACPI) sang °C.</summary>
        private static string TenthsKelvinToCelsius(object? value)
        {
            if (value == null) return string.Empty;
            double kelvin = Convert.ToDouble(value, CultureInfo.InvariantCulture) / 10.0;
            return (kelvin - 273.15).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
