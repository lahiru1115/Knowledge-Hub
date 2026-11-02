using System.Net;
using System.Net.Sockets;
using System.Text;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Infrastructure.Email;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>Talks real SMTP to a minimal in-process server, standing in for Mailpit.</summary>
public sealed class SmtpEmailTransportTests
{
    /// <summary>Accepts one connection, speaks just enough SMTP, and keeps what it received.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _session;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Received { get; private set; } = "";
        public string MailFrom { get; private set; } = "";
        public string RcptTo { get; private set; } = "";

        public FakeSmtpServer()
        {
            _listener.Start();
            _session = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            var data = new StringBuilder();
            var inData = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (inData)
                {
                    if (line == ".") { inData = false; Received = data.ToString(); await writer.WriteLineAsync("250 queued"); }
                    else data.AppendLine(line);
                    continue;
                }

                var verb = line.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO": await writer.WriteLineAsync("250 fake"); break;
                    case "MAIL": MailFrom = line; await writer.WriteLineAsync("250 ok"); break;
                    case "RCPT": RcptTo = line; await writer.WriteLineAsync("250 ok"); break;
                    case "DATA": inData = true; await writer.WriteLineAsync("354 go"); break;
                    case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                    default: await writer.WriteLineAsync("250 ok"); break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _session.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        }
    }

    [Fact]
    public async Task Sends_a_plain_text_message_with_the_configured_sender()
    {
        await using var server = new FakeSmtpServer();
        var transport = new SmtpEmailTransport(Options.Create(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            From = "Knowledge Hub <no-reply@example.test>",
        }));

        await transport.SendAsync(new EmailMessage("ada@example.com", "Reset your password", "Open this link: https://example.test/reset?token=abc"), TestContext.Current.CancellationToken);

        Assert.Contains("no-reply@example.test", server.MailFrom, StringComparison.Ordinal);
        Assert.Contains("ada@example.com", server.RcptTo, StringComparison.Ordinal);
        Assert.Contains("Subject: Reset your password", server.Received, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain", server.Received, StringComparison.Ordinal);
        Assert.Contains("https://example.test/reset?token=abc", server.Received, StringComparison.Ordinal);
    }
}
