using System.Net.NetworkInformation;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Server.Process;

/// <summary>/device 端点（移植自 bean/Device.java + Nano.java）：返回本机设备 JSON 供局域网发现/投屏。</summary>
public class DeviceProcess : IProcess
{
    public bool IsRequest(ServerRequest req) => req.Path.StartsWith("/device");

    public Task<ServerResponse> Handle(ServerRequest req) => Task.FromResult(ServerResponse.Ok(GetDeviceJson()));

    /// <summary>设备 JSON：ip 字段含 http:// 前缀与端口；type 0=电视（Windows 端作为投屏接收端，按规格走 /action?do=cast 协议）。</summary>
    public static string GetDeviceJson() => JsonUtil.Serialize(new Device
    {
        Uuid = GetUuid(),
        Name = Environment.MachineName,
        Ip = LocalServer.Instance.GetAddressLan(""),
        Type = 0,
        Serial = GetUuid()[..8],
        Eth = GetMac(NetworkInterfaceType.Ethernet),
        Wlan = GetMac(NetworkInterfaceType.Wireless80211),
        Time = Stores.Now(),
    });

    /// <summary>持久化设备标识（等价 AndroidID，首次生成后固定）。</summary>
    static string GetUuid()
    {
        var uuid = Setting.GetString("device_uuid");
        if (string.IsNullOrEmpty(uuid)) { uuid = Guid.NewGuid().ToString("N"); Setting.Put("device_uuid", uuid); }
        return uuid;
    }

    static string GetMac(NetworkInterfaceType type)
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.NetworkInterfaceType == type && ni.OperationalStatus == OperationalStatus.Up)
                    return string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("x2")));
        }
        catch { }
        return "";
    }
}
