using System;
using System.Diagnostics;

namespace UnlockServer
{
    /// <summary>
    /// 网络控制端口的防火墙规则。只在开启网络控制时放行专用网络入站，
    /// 程序清单本身要求管理员权限，无需二次提权。
    /// </summary>
    public static class NetworkAccessHelper
    {
        public const string RuleName = "UnlockServer Network Control";

        public static bool EnsurePortRule(int port)
        {
            RemovePortRule();
            if (port <= 0 || port > 65535) return false;
            return RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
                            $"protocol=TCP localport={port} profile=private");
        }

        public static bool RemovePortRule()
        {
            return RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
        }

        private static bool RunNetsh(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return false;
                    process.WaitForExit(15000);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"配置防火墙规则失败: {ex.Message}");
                return false;
            }
        }
    }
}
