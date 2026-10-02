using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YEEYEEYEE.Core;

namespace YEEYEEYEE.Host;

public sealed record CallbackSignatureOptions
{
    public byte[] Secret { get; init; } = Array.Empty<byte>();
    public TimeSpan AllowedClockSkew { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class CallbackReplayException : Exception
{
    public CallbackReplayException(string message) : base(message) { }
}

public sealed class HmacCallbackVerifier
{
    private readonly byte[] secret;
    private readonly TimeSpan allowedSkew;
    private readonly ConcurrentDictionary<string, long> nonces = new(StringComparer.Ordinal);

    public HmacCallbackVerifier(CallbackSignatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Secret.Length < 16) throw new ArgumentException("回调密钥至少需要 16 字节", nameof(options));
        if (options.AllowedClockSkew <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        secret = options.Secret.ToArray();
        allowedSkew = options.AllowedClockSkew;
    }

    public void Verify(string timestampHeader, string nonceHeader, string signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset? now = null)
    {
        if (!long.TryParse(timestampHeader, out var unixSeconds)) throw new CallbackReplayException("回调时间戳无效");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var current = now ?? DateTimeOffset.UtcNow;
        if ((current - timestamp).Duration() > allowedSkew) throw new CallbackReplayException("回调时间戳超出允许窗口");
        if (string.IsNullOrWhiteSpace(nonceHeader)) throw new CallbackReplayException("回调 nonce 缺失");
        var canonical = Encoding.UTF8.GetBytes($"{timestampHeader}.{Convert.ToBase64String(rawBody.ToArray())}");
        var expected = Convert.ToHexString(HMACSHA256.HashData(secret, canonical));
        var provided = signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? signatureHeader[7..] : signatureHeader;
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var providedBytes = Encoding.ASCII.GetBytes(provided);
        if (providedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes)) throw new CryptographicException("回调签名无效");
        if (!nonces.TryAdd(nonceHeader, unixSeconds)) throw new CallbackReplayException("回调已被处理");
        foreach (var item in nonces.Where(item => current.ToUnixTimeSeconds() - item.Value > (long)allowedSkew.TotalSeconds)) nonces.TryRemove(item.Key, out _);
    }
}

public sealed class SignedExternalCallbackHandler
{
    private readonly HmacCallbackVerifier verifier;
    private readonly ExternalTaskCallbackReceiver receiver;

    public SignedExternalCallbackHandler(HmacCallbackVerifier verifier, ExternalTaskCallbackReceiver receiver)
    {
        this.verifier = verifier;
        this.receiver = receiver;
    }

    public bool Handle(Guid userId, string timestamp, string nonce, string signature, ReadOnlyMemory<byte> rawBody)
    {
        verifier.Verify(timestamp, nonce, signature, rawBody.Span);
        var update = JsonSerializer.Deserialize<ExternalTaskUpdate>(
                rawBody.Span,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                })
            ?? throw new JsonException("回调内容为空");
        return receiver.Receive(userId, update);
    }
}
