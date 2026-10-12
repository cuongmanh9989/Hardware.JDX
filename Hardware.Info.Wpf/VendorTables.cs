using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Bảng tra tên hãng từ mã định danh (EDID PNP ID, USB VID, JEDEC). Chỉ chứa các mã phổ biến đã biết chắc;
    /// mã không có trong bảng sẽ được hiển thị nguyên mã gốc thay vì đoán.
    /// </summary>
    public static class VendorTables
    {
        /// <summary>Mã PNP 3 ký tự trong EDID của màn hình → tên hãng.</summary>
        private static readonly Dictionary<string, string> EdidVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEL"] = "Dell", ["SAM"] = "Samsung", ["LEN"] = "Lenovo", ["GSM"] = "LG Electronics", ["ACI"] = "ASUS", ["AUS"] = "ASUS",
            ["AOC"] = "AOC", ["BNQ"] = "BenQ", ["HWP"] = "HP", ["PHL"] = "Philips", ["MSI"] = "MSI", ["ACR"] = "Acer",
            ["VSC"] = "ViewSonic", ["SNY"] = "Sony", ["NEC"] = "NEC", ["IVM"] = "Iiyama", ["GBT"] = "Gigabyte", ["MEI"] = "Panasonic",
            ["APP"] = "Apple", ["HSD"] = "Hannspree", ["LGD"] = "LG Display", ["BOE"] = "BOE", ["AUO"] = "AU Optronics", ["CMN"] = "Chimei Innolux",
            ["SHP"] = "Sharp", ["TSB"] = "Toshiba", ["EIZ"] = "EIZO", ["HKC"] = "HKC",
        };

        /// <summary>Mã nhà sản xuất USB (VID, hex 4 ký tự) → tên hãng.</summary>
        private static readonly Dictionary<string, string> UsbVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1B1C"] = "Corsair", ["046D"] = "Logitech", ["045E"] = "Microsoft", ["1532"] = "Razer", ["1038"] = "SteelSeries",
            ["0951"] = "Kingston (HyperX)", ["04D9"] = "Holtek", ["093A"] = "PixArt", ["258A"] = "SINO WEALTH", ["0B05"] = "ASUS",
            ["413C"] = "Dell", ["03F0"] = "HP", ["17EF"] = "Lenovo", ["05AC"] = "Apple", ["04F2"] = "Chicony", ["046A"] = "Cherry",
            ["1A2C"] = "China Resource Semico (Semico)", ["0C45"] = "Microdia", ["04CA"] = "Lite-On", ["0461"] = "Primax", ["1C4F"] = "SiGma Micro",
            ["3151"] = "YICHIP", ["248A"] = "Maxxter / Telink", ["30FA"] = "INSTANT", ["0458"] = "KYE Systems (Genius)", ["2516"] = "Cooler Master",
            ["1EA7"] = "SHARKOON", ["320F"] = "Evision (Redragon)", ["24AE"] = "Rapoo",
        };

        /// <summary>Mã JEDEC (byte bank + byte ID, in từ Windows) → tên hãng bộ nhớ. Chỉ gồm các mã bank 1 và Crucial đã biết chắc.</summary>
        private static readonly Dictionary<string, string> JedecVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["802C"] = "Micron", ["80AD"] = "SK hynix", ["80CE"] = "Samsung", ["859B"] = "Crucial",
        };

        /// <summary>Tên hãng từ mã PNP của EDID; không có trong bảng thì trả về chính mã đó.</summary>
        public static string EdidVendor(string pnpId) => EdidVendors.TryGetValue(pnpId, out string? name) ? name : pnpId;

        /// <summary>Tên hãng từ VID USB; trả về chuỗi rỗng nếu chưa tra được.</summary>
        public static string UsbVendor(string vendorId) => UsbVendors.TryGetValue(vendorId, out string? name) ? name : string.Empty;

        /// <summary>
        /// Giải mã mã hãng RAM do Windows trả về (vd "0x0B2C", "802C"). Nếu không tra được, hiển thị mã gốc kèm bank/ID JEDEC.
        /// </summary>
        public static string JedecVendor(string manufacturer)
        {
            string hex = (manufacturer ?? string.Empty).Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);

            // Không phải mã hex 4 ký tự → đã là tên hãng, giữ nguyên
            if (hex.Length != 4 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
                return manufacturer ?? string.Empty;

            if (JedecVendors.TryGetValue(hex, out string? name))
                return name;

            int bank = ((value >> 8) & 0x7F) + 1;
            int id = value & 0x7F;
            return $"Chưa tra được (mã JEDEC 0x{hex.ToUpperInvariant()}: bank {bank}, ID 0x{id:X2})";
        }
    }
}
