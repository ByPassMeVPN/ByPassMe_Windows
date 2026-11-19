using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace ByPassMe.TunnelHelper;

internal sealed class TunnelWorker : BackgroundService
{
    public const string PipeName = "ByPassMeTunnel";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var server = CreatePipeServer();

            try
            {
                await server.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    private static async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line))
        {
            await writer.WriteLineAsync("ERR|empty command");
            return;
        }

        var reply = line switch
        {
            "PING" => "OK",
            "STOP" => RunStop(),
            _ when line.StartsWith("START|", StringComparison.Ordinal) => RunStart(line["START|".Length..]),
            _ => "ERR|unknown command"
        };

        await writer.WriteLineAsync(reply);
    }

    private static string RunStart(string configPath)
    {
        configPath = configPath.Trim().Trim('"');
        return WireGuardRunner.StartTunnel(configPath) ? "OK" : "ERR|start failed";
    }

    private static string RunStop()
    {
        WireGuardRunner.StopTunnel();
        return "OK";
    }
}
