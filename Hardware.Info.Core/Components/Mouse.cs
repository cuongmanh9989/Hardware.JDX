using System;

// https://docs.microsoft.com/en-us/windows/win32/cimwin32prov/win32-pointingdevice

namespace Hardware.Info
{
    /// <summary>
    /// WMI class: Win32_PointingDevice
    /// </summary>
    public class Mouse
    {
        /// <summary>
        /// Short description of the object.
        /// </summary>
        public string Caption { get; set; } = string.Empty;

        /// <summary>
        /// Description of the object.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Name of the processor's manufacturer.
        /// </summary>
        public string Manufacturer { get; set; } = string.Empty;

        /// <summary>
        /// Label by which the object is known.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Number of buttons on the pointing device.
        /// </summary>
        public byte NumberOfButtons { get; set; }

        /// <summary>
        /// Windows Plug and Play device ID (Win32_PointingDevice.PNPDeviceID).
        /// </summary>
        public string PNPDeviceID { get; set; } = string.Empty;

        /// <summary>
        /// Real product name reported by the USB device itself (for example "CORSAIR KATAR PRO Gaming Mouse"), empty if unavailable. Windows only.
        /// </summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>
        /// USB vendor ID (VID) in hex, empty if unavailable. Windows only.
        /// </summary>
        public string VendorId { get; set; } = string.Empty;

        /// <summary>
        /// USB product ID (PID) in hex, empty if unavailable. Windows only.
        /// </summary>
        public string ProductId { get; set; } = string.Empty;

        /// <summary>
        /// Write all property values to a string
        /// </summary>
        /// <returns>Each property on a new line</returns>
        public override string ToString()
        {
            return
                "Caption: " + Caption + Environment.NewLine +
                "Description: " + Description + Environment.NewLine +
                "Manufacturer: " + Manufacturer + Environment.NewLine +
                "Name: " + Name + Environment.NewLine +
                "NumberOfButtons: " + NumberOfButtons + Environment.NewLine +
                "PNPDeviceID: " + PNPDeviceID + Environment.NewLine +
                "ProductName: " + ProductName + Environment.NewLine +
                "VendorId: " + VendorId + Environment.NewLine +
                "ProductId: " + ProductId + Environment.NewLine;
        }
    }
}
