using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TapoP100_Controller;

internal sealed class TapoP100Controller : IDisposable
{
    private const string SessionCookieName = "TP_SESSIONID";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _ipAddress;
    private readonly string _username;
    private readonly string _password;
    private readonly string _terminalUuid = Guid.NewGuid().ToString();
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Action<string>? _log;

    private string? _latestSessionCookie;
    private bool _disposed;

    public TapoP100Controller(
        string ipAddress,
        string username,
        string password,
        Action<string>? log = null)
    {
        _ipAddress = ipAddress;
        _username = username;
        _password = password;
        _log = log;

        HttpClientHandler handler = new()
        {
            UseCookies = false
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public Task TurnOnAsync(CancellationToken cancellationToken) => SetPowerStateAsync(true, cancellationToken);

    public Task TurnOffAsync(CancellationToken cancellationToken) => SetPowerStateAsync(false, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _lock.Dispose();
    }

    private async Task SetPowerStateAsync(bool powerOn, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latestSessionCookie = null;

            const string variant = "KLAP v2";
            string baseUri = $"http://{_ipAddress}";

            _log?.Invoke($"{variant} handshake1 request: {baseUri}");

            byte[] localSeed = RandomNumberGenerator.GetBytes(16);
            byte[] authHash = GenerateKlapAuthHashV2(_username, _password);

            byte[] handshake1Response = await PostBytesAsync(
                BuildKlapUri(baseUri, "handshake1"),
                localSeed,
                null,
                cancellationToken);

            string sessionCookie = ExtractSessionCookie();

            if (handshake1Response.Length < 48)
            {
                throw new InvalidOperationException($"Unexpected handshake1 length: {handshake1Response.Length}");
            }

            byte[] remoteSeed = handshake1Response[..16];
            byte[] serverHash = handshake1Response[16..48];
            byte[] expectedServerHash = Sha256(localSeed, remoteSeed, authHash);

            if (!serverHash.SequenceEqual(expectedServerHash))
            {
                throw new InvalidOperationException("handshake1 hash mismatch");
            }

            _log?.Invoke($"{variant} handshake1 success: {baseUri}");
            _log?.Invoke($"{variant} handshake2 request: {baseUri}");

            byte[] handshake2Payload = Sha256(remoteSeed, localSeed, authHash);

            await PostBytesAsync(
                BuildKlapUri(baseUri, "handshake2"),
                handshake2Payload,
                sessionCookie,
                cancellationToken);

            _log?.Invoke($"{variant} handshake2 success: {baseUri}");

            KlapSession session = new(localSeed, remoteSeed, authHash);
            string requestJson = JsonSerializer.Serialize(
                new
                {
                    method = "set_device_info",
                    @params = new
                    {
                        device_on = powerOn
                    },
                    terminalUUID = _terminalUuid
                },
                JsonOptions);

            (byte[] encryptedPayload, int seq) = session.Encrypt(requestJson);
            _log?.Invoke($"{variant} power request: {(powerOn ? "ON" : "OFF")} / seq={seq} / {baseUri}");

            byte[] encryptedResponse = await PostBytesAsync(
                BuildKlapRequestUri(baseUri, seq),
                encryptedPayload,
                sessionCookie,
                cancellationToken);

            string responseJson = session.Decrypt(encryptedResponse);
            using JsonDocument document = JsonDocument.Parse(responseJson);
            ThrowIfDeviceReturnedError(document.RootElement);

            _log?.Invoke($"Tapo command completed: {(powerOn ? "ON" : "OFF")} / {variant} / {baseUri}");
        }
        finally
        {
            if (!_disposed)
            {
                _lock.Release();
            }
        }
    }

    private async Task<byte[]> PostBytesAsync(
        string requestUri,
        byte[] payload,
        string? sessionCookie,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, requestUri)
        {
            Content = new ByteArrayContent(payload)
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        ApplyCookie(request, sessionCookie);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        CacheSessionCookie(response);
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string responseText = TryDecodeResponseBody(responseBytes);
            throw new InvalidOperationException(
                $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) from {requestUri}: {responseText}");
        }

        return responseBytes;
    }

    private static void ApplyCookie(HttpRequestMessage request, string? sessionCookie)
    {
        if (!string.IsNullOrWhiteSpace(sessionCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        }
    }

    private void CacheSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return;
        }

        foreach (string header in values)
        {
            string firstSegment = header.Split(';', 2)[0];
            if (firstSegment.StartsWith($"{SessionCookieName}=", StringComparison.OrdinalIgnoreCase))
            {
                _latestSessionCookie = firstSegment;
                break;
            }
        }
    }

    private string ExtractSessionCookie()
    {
        if (string.IsNullOrWhiteSpace(_latestSessionCookie))
        {
            throw new InvalidOperationException("The Tapo plug did not return a session cookie.");
        }

        return _latestSessionCookie;
    }

    private static byte[] GenerateKlapAuthHashV2(string username, string password)
    {
        byte[] userSha1 = SHA1.HashData(Encoding.UTF8.GetBytes(username));
        byte[] passSha1 = SHA1.HashData(Encoding.UTF8.GetBytes(password));
        return Sha256(userSha1, passSha1);
    }

    private static byte[] Sha256(params byte[][] parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] part in parts)
        {
            hash.AppendData(part);
        }

        return hash.GetHashAndReset();
    }

    private static byte[] Combine(params byte[][] parts)
    {
        int totalLength = parts.Sum(static p => p.Length);
        byte[] buffer = new byte[totalLength];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, buffer, offset, part.Length);
            offset += part.Length;
        }

        return buffer;
    }

    private static string TryDecodeResponseBody(byte[] responseBytes)
    {
        if (responseBytes.Length == 0)
        {
            return "<empty>";
        }

        try
        {
            return Encoding.UTF8.GetString(responseBytes);
        }
        catch
        {
            return Convert.ToHexString(responseBytes);
        }
    }

    private static void ThrowIfDeviceReturnedError(JsonElement element)
    {
        if (!element.TryGetProperty("error_code", out JsonElement errorCodeElement))
        {
            return;
        }

        int errorCode = errorCodeElement.GetInt32();
        if (errorCode == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Tapo device returned error_code={errorCode}: {element.GetRawText()}");
    }

    private static string BuildKlapUri(string baseUri, string action)
    {
        return $"{baseUri}/app/{action}";
    }

    private static string BuildKlapRequestUri(string baseUri, int seq)
    {
        return $"{baseUri}/app/request?seq={seq}";
    }

    private sealed class KlapSession
    {
        private readonly byte[] _key;
        private readonly byte[] _ivPrefix;
        private readonly byte[] _signatureKey;
        private int _sequence;
        private byte[]? _lastIv;

        public KlapSession(byte[] localSeed, byte[] remoteSeed, byte[] userHash)
        {
            _key = Sha256(Encoding.ASCII.GetBytes("lsk"), localSeed, remoteSeed, userHash)[..16];

            byte[] fullIv = Sha256(Encoding.ASCII.GetBytes("iv"), localSeed, remoteSeed, userHash);
            _ivPrefix = fullIv[..12];
            _sequence = BinaryPrimitives.ReadInt32BigEndian(fullIv.AsSpan(fullIv.Length - 4, 4));

            _signatureKey = Sha256(Encoding.ASCII.GetBytes("ldk"), localSeed, remoteSeed, userHash)[..28];
        }

        public (byte[] Payload, int Sequence) Encrypt(string message)
        {
            _sequence++;
            byte[] iv = BuildIv(_sequence);
            _lastIv = iv;

            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = _key;
            aes.IV = iv;

            byte[] plaintext = Encoding.UTF8.GetBytes(message);
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            byte[] ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

            byte[] seqBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(seqBytes, _sequence);
            byte[] signature = Sha256(_signatureKey, seqBytes, ciphertext);

            return (Combine(signature, ciphertext), _sequence);
        }

        public string Decrypt(byte[] payload)
        {
            if (_lastIv is null)
            {
                throw new InvalidOperationException("KLAP decrypt called before encrypt.");
            }

            if (payload.Length < 33)
            {
                throw new InvalidOperationException($"Unexpected KLAP response length: {payload.Length}");
            }

            byte[] ciphertext = payload[32..];

            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = _key;
            aes.IV = _lastIv;

            using ICryptoTransform decryptor = aes.CreateDecryptor();
            byte[] plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            return Encoding.UTF8.GetString(plaintext);
        }

        private byte[] BuildIv(int sequence)
        {
            byte[] iv = new byte[16];
            Buffer.BlockCopy(_ivPrefix, 0, iv, 0, _ivPrefix.Length);
            BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(12, 4), sequence);
            return iv;
        }
    }
}
