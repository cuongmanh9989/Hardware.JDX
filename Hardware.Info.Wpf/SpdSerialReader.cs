using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;
using RAMSPDToolkit.SPD.Interop.Shared;
using RAMSPDToolkit.Windows.Driver;
using RAMSPDToolkit.Windows.Driver.Implementations;

namespace Hardware.Info.Wpf
{
    /// <summary>Một thanh RAM đọc được từ chip SPD.</summary>
    public sealed class SpdModule
    {
        public SpdModule(string partNumber, string serial) { PartNumber = partNumber; Serial = serial; }

        /// <summary>Part number trong SPD (đã Trim), dùng để ghép với dữ liệu WMI.</summary>
        public string PartNumber { get; }

        /// <summary>S/N 4 byte dạng hex (vd "A1B2C3D4"), thứ tự theo địa chỉ khe SPD.</summary>
        public string Serial { get; }
    }

    /// <summary>Kết quả đọc SPD: danh sách thanh RAM và lý do nếu không đọc được.</summary>
    public sealed class SpdReadResult
    {
        public SpdReadResult(List<SpdModule> modules, string note) { Modules = modules; Note = note; }

        public List<SpdModule> Modules { get; }

        /// <summary>Rỗng nếu đọc bình thường; còn lại là lý do không đọc được (hiện ở thanh trạng thái).</summary>
        public string Note { get; }
    }

    /// <summary>
    /// Đọc S/N thanh RAM từ chip SPD qua SMBus bằng driver PawnIO (đã ký số, do người dùng cài riêng).
    /// Chỉ ĐỌC: không ghi vào SPD. Không dùng WinRing0 (driver có lỗ hổng bảo mật đã công bố).
    /// Cần quyền Admin và PawnIO đã cài; thiếu một trong hai thì trả về ghi chú và để S/N là N/A.
    /// </summary>
    public static class SpdSerialReader
    {
        private const string PawnIoDll = @"C:\Program Files\PawnIO\PawnIOLib.dll";

        public static SpdReadResult Read()
        {
            if (!IsAdministrator())
                return Fail("S/N RAM: cần chạy với quyền Admin để đọc SPD");
            if (!File.Exists(PawnIoDll))
                return Fail("S/N RAM: chưa cài driver PawnIO (https://pawnio.eu) nên không đọc được SPD");

            bool loaded = false;
            try
            {
                if (!DriverManager.LoadDriver(DriverImplementation.PawnIO))
                    return Fail("S/N RAM: không nạp được driver PawnIO");
                loaded = true;

                SMBusManager.DetectSMBuses();

                var modules = new List<SpdModule>();
                foreach (SMBusInterface bus in SMBusManager.RegisteredSMBuses)
                {
                    for (byte address = SPDConstants.SPD_BEGIN; address <= SPDConstants.SPD_END; address++)
                    {
                        var detector = new SPDDetector(bus, address);
                        if (!detector.IsValid || !(detector.Accessor is SPDAccessor accessor)) continue;

                        string serial = FormatSerial(accessor.ModuleSerialNumber());
                        string part = (accessor.ModulePartNumber() ?? string.Empty).Trim();
                        modules.Add(new SpdModule(part, serial));
                    }
                }

                return modules.Count > 0
                    ? new SpdReadResult(modules, string.Empty)
                    : Fail("S/N RAM: driver đã nạp nhưng không thấy thanh RAM nào qua SMBus (BIOS/chipset có thể chặn truy cập SPD)");
            }
            catch (Exception ex)
            {
                return Fail("S/N RAM: lỗi khi đọc SPD (" + ex.GetBaseException().Message + ")");
            }
            finally
            {
                if (loaded)
                {
                    try { DriverManager.UnloadDriver(); }
                    catch (Exception) { /* không gỡ được driver: bỏ qua, không ảnh hưởng dữ liệu đã đọc */ }
                }
            }
        }

        private static SpdReadResult Fail(string note) => new SpdReadResult(new List<SpdModule>(), note);

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>S/N SPD → hex in hoa; để trống nếu chưa ghi (toàn 0 hoặc toàn FF).</summary>
        private static string FormatSerial(object? serial)
        {
            string text;
            switch (serial)
            {
                case null: return string.Empty;
                case byte[] bytes: text = string.Concat(bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))); break;
                case IFormattable formattable: text = formattable.ToString("X8", CultureInfo.InvariantCulture); break;
                default: text = serial.ToString() ?? string.Empty; break;
            }

            return text.Length == 0 || text.All(c => c == '0') || text.All(c => c == 'F') ? string.Empty : text;
        }
    }
}
