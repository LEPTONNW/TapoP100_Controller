using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TapoP100_Controller;

internal sealed class P100ApiServer : IDisposable
{
    private readonly int _port;
    private readonly Func<ApiHttpRequest, Task<ApiCommandResult>> _handler;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private bool _disposed;

    public P100ApiServer(int port, Func<ApiHttpRequest, Task<ApiCommandResult>> handler, Action<string>? log = null)
    {
        _port = port;
        _handler = handler;
        _log = log;
    }

    public void Start()
    {
        if (_disposed || _listener is not null)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _listener is not null)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log?.Invoke($"HTTP API stopped: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

        try
        {
            string? requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                await WriteResponseAsync(stream, 400, "잘못된 요청", cancellationToken);
                return;
            }

            string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteResponseAsync(stream, 400, "잘못된 요청", cancellationToken);
                return;
            }

            string method = parts[0];
            string path = parts[1];
            int contentLength = 0;

            while (true)
            {
                string? headerLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(headerLine))
                {
                    break;
                }

                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(headerLine["Content-Length:".Length..].Trim(), out contentLength);
                }
            }

            string body = string.Empty;
            if (contentLength > 0)
            {
                char[] buffer = new char[contentLength];
                int offset = 0;
                while (offset < contentLength)
                {
                    int read = await reader.ReadAsync(buffer.AsMemory(offset, contentLength - offset), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                body = new string(buffer, 0, offset);
            }

            ApiCommandRequest? payload = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                payload = JsonSerializer.Deserialize<ApiCommandRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            _log?.Invoke($"HTTP API request: {method} {path}");

            ApiCommandResult result = await _handler(new ApiHttpRequest(method, path, payload));
            await WriteResponseAsync(stream, result.Success ? 200 : 500, result.Message, cancellationToken);
        }
        catch (JsonException ex)
        {
            await WriteResponseAsync(stream, 400, $"JSON 파싱 실패: {ex.Message}", cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(stream, 500, $"실패: {ex.Message}", cancellationToken);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string message, CancellationToken cancellationToken)
    {
        string reasonPhrase = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            405 => "Method Not Allowed",
            _ => "Internal Server Error"
        };

        byte[] body = Encoding.UTF8.GetBytes(message);
        string headers =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
