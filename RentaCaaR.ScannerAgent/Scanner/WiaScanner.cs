using System.Drawing;
using System.Drawing.Imaging;

namespace RentaCaaR.ScannerAgent.Scanner;

public record ScannerInfo(string Id, string Name);

public class WiaScanner
{
    private const string DeviceManagerProgId = "WIA.DeviceManager";
    private const int ScannerDeviceType = 1;

    public List<ScannerInfo> GetScanners()
    {
        var managerType = Type.GetTypeFromProgID(DeviceManagerProgId)
            ?? throw new NotSupportedException("WIA no está disponible en este sistema");
        dynamic manager = Activator.CreateInstance(managerType)!;

        var result = new List<ScannerInfo>();
        foreach (dynamic info in manager.DeviceInfos)
        {
            try
            {
                if ((int)info.Type == ScannerDeviceType)
                {
                    string id = (string)info.DeviceID;
                    string name = GetPropertyValue(info.Properties, "Name") ?? id;
                    result.Add(new ScannerInfo(id, name));
                }
            }
            catch { /* skip unavailable devices */ }
        }
        return result;
    }

    public byte[] Scan(string deviceId, int dpi = 300)
    {
        var managerType = Type.GetTypeFromProgID(DeviceManagerProgId)!;
        dynamic manager = Activator.CreateInstance(managerType)!;

        dynamic? deviceInfo = null;
        foreach (dynamic info in manager.DeviceInfos)
        {
            if ((string)info.DeviceID == deviceId) { deviceInfo = info; break; }
        }
        if (deviceInfo == null)
            throw new Exception($"Escáner {deviceId} no encontrado");

        dynamic device = deviceInfo.Connect();
        dynamic item = device.Items[1];

        // WIA property IDs
        SetProperty(item.Properties, 6146, 2);    // WIA_IPS_CUR_INTENT: color
        SetProperty(item.Properties, 6147, dpi);  // WIA_IPS_XRES
        SetProperty(item.Properties, 6148, dpi);  // WIA_IPS_YRES

        // Transfer as JPEG
        const string jpegFormatGuid = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
        dynamic imageFile = item.Transfer(jpegFormatGuid);
        return (byte[])imageFile.FileData.BinaryData;
    }

    private static void SetProperty(dynamic properties, int propertyId, int value)
    {
        foreach (dynamic prop in properties)
        {
            try
            {
                if ((int)prop.PropertyID == propertyId)
                {
                    prop.set_Value(value);
                    return;
                }
            }
            catch { }
        }
    }

    private static string? GetPropertyValue(dynamic properties, string name)
    {
        foreach (dynamic prop in properties)
        {
            try { if ((string)prop.Name == name) return (string)prop.get_Value(); }
            catch { }
        }
        return null;
    }
}
