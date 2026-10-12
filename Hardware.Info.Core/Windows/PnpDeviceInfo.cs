using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Hardware.Info.Windows
{
    /// <summary>
    /// Đọc tên thật của thiết bị USB/HID (vd "CORSAIR KATAR PRO Gaming Mouse") từ Plug and Play.
    /// WMI Win32_Keyboard / Win32_PointingDevice chỉ trả tên chung ("USB Input Device"), còn tên do thiết bị tự khai báo
    /// nằm ở thuộc tính BusReportedDeviceDesc của thiết bị USB cha. Không cần quyền Administrator.
    /// </summary>
    internal static class PnpDeviceInfo
    {
        /// <summary>Khóa thuộc tính PnP (GUID + số thứ tự) — tương ứng DEVPROPKEY trong Windows SDK.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // DEVPKEY_Device_BusReportedDeviceDesc = {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
        private static DEVPROPKEY BusReportedDeviceDescKey = new DEVPROPKEY
        {
            fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
            pid = 4
        };

        private const uint CR_SUCCESS = 0;

        /// <summary>Số cấp thiết bị cha tối đa được duyệt (HID → giao diện USB → thiết bị USB tổng hợp).</summary>
        private const int MaxParentLevels = 4;

        /// <summary>Tách VID/PID dạng hex 4 ký tự từ PNPDeviceID, vd "HID\VID_1B1C&amp;PID_1B93&amp;MI_00\...".</summary>
        private static readonly Regex VidPidRegex = new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey, out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);

        /// <summary>
        /// Lấy tên do thiết bị tự khai báo, đi ngược lên thiết bị cha cho tới khi gặp giá trị khác rỗng.
        /// Trả về chuỗi rỗng nếu không đọc được (không phải thiết bị USB, thiếu API, v.v.).
        /// </summary>
        /// <param name="pnpDeviceId">PNPDeviceID của thiết bị (từ WMI).</param>
        public static string GetBusReportedDescription(string pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId))
                return string.Empty;

            try
            {
                if (CM_Locate_DevNodeW(out uint devInst, pnpDeviceId, 0) != CR_SUCCESS)
                    return string.Empty;

                for (int level = 0; level < MaxParentLevels; level++)
                {
                    byte[] buffer = new byte[1024];
                    uint size = (uint)buffer.Length;

                    if (CM_Get_DevNode_PropertyW(devInst, ref BusReportedDeviceDescKey, out _, buffer, ref size, 0) == CR_SUCCESS && size > 2)
                    {
                        string text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
                        if (text.Length > 0)
                            return text;
                    }

                    if (CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS)
                        break;

                    devInst = parent;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // Hệ thống không có cfgmgr32: bỏ qua, giữ tên WMI
            }

            return string.Empty;
        }

        /// <summary>Lấy mã nhà sản xuất (VID) và mã sản phẩm (PID) dạng hex từ PNPDeviceID; rỗng nếu không phải thiết bị USB/HID có VID/PID.</summary>
        public static void GetVendorProductId(string pnpDeviceId, out string vendorId, out string productId)
        {
            Match match = VidPidRegex.Match(pnpDeviceId ?? string.Empty);
            vendorId = match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
            productId = match.Success ? match.Groups[2].Value.ToUpperInvariant() : string.Empty;
        }
    }
}
