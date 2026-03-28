using System.Security.Cryptography;
using System.Text;
using GameController.FBServiceExt.Application.Abstractions.Security;
using GameController.FBServiceExt.Application.Options;
using Microsoft.Extensions.Options;

namespace GameController.FBServiceExt.Infrastructure.Security;

public sealed class MetaWebhookSignatureValidator : IWebhookSignatureValidator
{
    private readonly IOptionsMonitor<MetaWebhookOptions> _optionsMonitor;

    public MetaWebhookSignatureValidator(IOptionsMonitor<MetaWebhookOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    public bool IsValid(string requestBody, string? signatureHeader)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.RequireSignatureValidation)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(options.AppSecret))
        {
            return false;
        }

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedHex = signatureHeader[prefix.Length..].Trim();
        if (expectedHex.Length == 0)
        {
            return false;
        }

        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(expectedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var bodyBytes = Encoding.UTF8.GetBytes(requestBody);
        var secretBytes = Encoding.UTF8.GetBytes(options.AppSecret);
        using var hmac = new HMACSHA256(secretBytes);
        var computedHash = hmac.ComputeHash(bodyBytes);

        return CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);
    }
}
