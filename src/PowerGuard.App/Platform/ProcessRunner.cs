using System.Diagnostics;
using System.Text;
using PowerGuard.Core;

namespace PowerGuard.App.Platform;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            // schtasks 的控制台输出使用 OEM 代码页
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardOutputEncoding = oem; psi.StandardErrorEncoding = oem;
            }
            catch { }
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return new(-1, "", "", "Process.Start 返回 null");
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
                return new(-1, "", "", $"{Path.GetFileName(fileName)} 超时");
            }
            return new(p.ExitCode, await outTask, await errTask);
        }
        catch (Exception ex) { return new(-1, "", "", ex.Message); }
    }
}
