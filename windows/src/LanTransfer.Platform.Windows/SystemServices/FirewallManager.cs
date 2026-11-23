using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LanTransfer.Platform.Windows.SystemServices;

/// <summary>防火墙检查结果（需求 3.8、设计 C7.3）。</summary>
public enum FirewallStatus
{
    Ok,
    NeedsRepair,
    Unavailable,
}

/// <summary>
/// Windows 防火墙规则管理（设计 C7.3、需求 3.4、3.5、3.8）：
/// 使用 INetFwPolicy2 检查与维护 TCP/UDP 入站允许规则（专用 + 公用，RemoteAddresses = LocalSubnet），
/// 并同时清除系统弹窗自动生成的本程序入站阻止规则（Q10）。
/// </summary>
public sealed class FirewallManager
{
    public const string RuleGroup = "LanTransfer";
    public const string TcpRuleName = "局域网传输（TCP 入站）";
    public const string UdpRuleName = "局域网传输（UDP 发现）";

    private const int NetFwProfilePrivate = 2;
    private const int NetFwProfilePublic = 4;
    private const int TargetProfiles = NetFwProfilePrivate | NetFwProfilePublic;
    private const int NetFwIpProtocolTcp = 6;
    private const int NetFwIpProtocolUdp = 17;
    private const int NetFwRuleDirIn = 1;
    private const int NetFwActionBlock = 0;
    private const int NetFwActionAllow = 1;

    private readonly string _exePath;

    public FirewallManager(string? exePath = null)
    {
        _exePath = Path.GetFullPath(exePath ?? Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
    }

    public FirewallStatus CheckStatus()
    {
        if (string.IsNullOrEmpty(_exePath))
        {
            return FirewallStatus.Unavailable;
        }

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return FirewallStatus.Unavailable;
            }

            dynamic? policy = Activator.CreateInstance(policyType);
            if (policy is null)
            {
                return FirewallStatus.Unavailable;
            }

            bool privateEnabled = policy.FirewallEnabled(NetFwProfilePrivate);
            bool publicEnabled = policy.FirewallEnabled(NetFwProfilePublic);
            int requiredProfiles = (privateEnabled ? NetFwProfilePrivate : 0) | (publicEnabled ? NetFwProfilePublic : 0);
            if (requiredProfiles == 0)
            {
                return FirewallStatus.Ok;
            }

            int tcpAllowProfiles = 0;
            int udpAllowProfiles = 0;

            foreach (dynamic rule in policy.Rules)
            {
                if (!rule.Enabled)
                {
                    continue;
                }

                if ((int)rule.Direction != NetFwRuleDirIn)
                {
                    continue;
                }

                string? app = rule.ApplicationName as string;
                if (string.IsNullOrEmpty(app) || !string.Equals(NormalizePath(app), NormalizePath(_exePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int profiles = (int)rule.Profiles;
                if ((profiles & requiredProfiles) == 0)
                {
                    continue;
                }

                int action = (int)rule.Action;
                int proto = (int)rule.Protocol;
                if (action == NetFwActionBlock && (proto == NetFwIpProtocolTcp || proto == NetFwIpProtocolUdp || proto == 256))
                {
                    return FirewallStatus.NeedsRepair;
                }

                if (action == NetFwActionAllow)
                {
                    if (proto == NetFwIpProtocolTcp || proto == 256)
                    {
                        tcpAllowProfiles |= profiles;
                    }

                    if (proto == NetFwIpProtocolUdp || proto == 256)
                    {
                        udpAllowProfiles |= profiles;
                    }
                }
            }

            bool tcpOk = (tcpAllowProfiles & requiredProfiles) == requiredProfiles;
            bool udpOk = (udpAllowProfiles & requiredProfiles) == requiredProfiles;
            return tcpOk && udpOk ? FirewallStatus.Ok : FirewallStatus.NeedsRepair;
        }
        catch
        {
            return FirewallStatus.Unavailable;
        }
    }

    /// <summary>需要管理员权限：删除本组规则与本程序阻止规则后重新创建允许规则（--repair-firewall）。</summary>
    public bool RepairRules()
    {
        if (string.IsNullOrEmpty(_exePath))
        {
            return false;
        }

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (policyType is null || ruleType is null)
            {
                return false;
            }

            dynamic? policy = Activator.CreateInstance(policyType);
            if (policy is null)
            {
                return false;
            }

            RemoveMatchingRules(policy, removeBlockRules: true);
            AddAllowRule(policy, ruleType, TcpRuleName, NetFwIpProtocolTcp);
            AddAllowRule(policy, ruleType, UdpRuleName, NetFwIpProtocolUdp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>需要管理员权限：卸载时删除本组规则（--remove-firewall）。</summary>
    public bool RemoveRules()
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return false;
            }

            dynamic? policy = Activator.CreateInstance(policyType);
            if (policy is null)
            {
                return false;
            }

            RemoveMatchingRules(policy, removeBlockRules: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以 UAC 提权启动自身执行 --repair-firewall 并等待退出；用户取消返回 false（3.8）。</summary>
    public async Task<bool> LaunchElevatedRepairAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_exePath) || !File.Exists(_exePath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(_exePath, "--repair-firewall")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223 /* ERROR_CANCELLED */)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void RemoveMatchingRules(dynamic policy, bool removeBlockRules)
    {
        var toRemove = new List<string>();
        foreach (dynamic rule in policy.Rules)
        {
            string name = rule.Name as string ?? string.Empty;
            string group = rule.Grouping as string ?? string.Empty;
            string app = rule.ApplicationName as string ?? string.Empty;
            bool sameGroup = string.Equals(group, RuleGroup, StringComparison.OrdinalIgnoreCase);
            bool sameAppBlock = removeBlockRules &&
                (int)rule.Direction == NetFwRuleDirIn &&
                (int)rule.Action == NetFwActionBlock &&
                !string.IsNullOrEmpty(app) &&
                string.Equals(NormalizePath(app), NormalizePath(_exePath), StringComparison.OrdinalIgnoreCase);

            if (sameGroup || sameAppBlock)
            {
                toRemove.Add(name);
            }
        }

        foreach (var name in toRemove)
        {
            try
            {
                policy.Rules.Remove(name);
            }
            catch
            {
            }
        }
    }

    private void AddAllowRule(dynamic policy, Type ruleType, string name, int protocol)
    {
        dynamic rule = Activator.CreateInstance(ruleType)!;
        rule.Name = name;
        rule.Grouping = RuleGroup;
        rule.ApplicationName = _exePath;
        rule.Protocol = protocol;
        rule.Direction = NetFwRuleDirIn;
        rule.Action = NetFwActionAllow;
        rule.Profiles = TargetProfiles;
        rule.RemoteAddresses = "LocalSubnet";
        rule.Enabled = true;
        policy.Rules.Add(rule);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return path;
        }
    }
}
