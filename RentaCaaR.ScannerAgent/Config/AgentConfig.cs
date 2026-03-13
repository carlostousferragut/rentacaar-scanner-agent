using Microsoft.Win32;

namespace RentaCaaR.ScannerAgent.Config;

public class AgentConfig
{
    private const string RegistryPath = @"SOFTWARE\RentaCaaR\ScannerAgent";
    public static readonly string AgentVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public string? AgentId         { get; private set; }
    public string? Secret          { get; private set; }
    public string? BackendUrl      { get; private set; }
    public string? OrgName         { get; private set; }
    public string? OfficeName      { get; private set; }
    public string? DefaultScannerId { get; private set; }
    public bool IsRegistered       => !string.IsNullOrEmpty(AgentId) && !string.IsNullOrEmpty(Secret);

    public AgentConfig()
    {
        Load();
    }

    private void Load()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        if (key == null) return;
        AgentId          = key.GetValue("AgentId")          as string;
        Secret           = key.GetValue("Secret")           as string;
        BackendUrl       = key.GetValue("BackendUrl")        as string;
        OrgName          = key.GetValue("OrgName")           as string;
        OfficeName       = key.GetValue("OfficeName")        as string;
        DefaultScannerId = key.GetValue("DefaultScannerId")  as string;
    }

    public void Save(string agentId, string secret, string backendUrl, string orgName, string officeName)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        key.SetValue("AgentId",    agentId);
        key.SetValue("Secret",     secret);
        key.SetValue("BackendUrl", backendUrl);
        key.SetValue("OrgName",    orgName);
        key.SetValue("OfficeName", officeName);
        AgentId    = agentId;
        Secret     = secret;
        BackendUrl = backendUrl;
        OrgName    = orgName;
        OfficeName = officeName;
    }

    public void SaveDefaultScanner(string scannerId)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        key.SetValue("DefaultScannerId", scannerId);
        DefaultScannerId = scannerId;
    }

    public void ClearCredentials()
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
        key?.DeleteValue("AgentId", throwOnMissingValue: false);
        key?.DeleteValue("Secret", throwOnMissingValue: false);
        AgentId = null;
        Secret = null;
    }
}
