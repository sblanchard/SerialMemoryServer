using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SerialMemory.McpClient.Config;

/// <summary>
/// Persistent configuration storage with encrypted API key.
/// Stored at /data/config.json inside Docker container.
/// </summary>
public sealed class ConfigStore(ILogger<ConfigStore> logger, string? configPath = null)
{
    private const string DefaultConfigPath = "/data/config.json";
    private const string DefaultServerUrl = "https://serialmemory.serialcoder.com";

    private readonly string _configPath = configPath ?? DefaultConfigPath;
    private readonly byte[] _encryptionKey = DeriveEncryptionKey();
    private McpClientConfig? _config;

    public McpClientConfig? Config => _config;
    public bool IsConfigured => _config != null && !string.IsNullOrEmpty(_config.EncryptedApiKey);
    public string ServerUrl => _config?.ServerUrl ?? DefaultServerUrl;
    public string WebSocketUrl => ServerUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/mcp";

    public string GetApiKey()
    {
        if (_config?.EncryptedApiKey == null)
            throw new InvalidOperationException("Configuration not loaded or API key missing");
        return DecryptApiKey(_config.EncryptedApiKey);
    }

    public async Task<bool> LoadAsync()
    {
        if (!File.Exists(_configPath))
        {
            logger.LogInformation("Config file not found at {Path}", _configPath);
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            _config = JsonSerializer.Deserialize<McpClientConfig>(json);

            if (_config == null || string.IsNullOrEmpty(_config.EncryptedApiKey))
            {
                logger.LogWarning("Config file exists but is invalid");
                return false;
            }

            // Validate decryption works
            _ = GetApiKey();
            logger.LogInformation("Configuration loaded successfully");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load configuration");
            return false;
        }
    }

    public async Task SaveAsync(string apiKey, string? serverUrl = null)
    {
        EnsureDataDirectory();

        _config = new McpClientConfig
        {
            ServerUrl = serverUrl ?? DefaultServerUrl,
            EncryptedApiKey = EncryptApiKey(apiKey),
            CreatedAt = DateTimeOffset.UtcNow,
            Version = "1.0"
        };

        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await File.WriteAllTextAsync(_configPath, json);
        logger.LogInformation("Configuration saved to {Path}", _configPath);
    }

    public async Task<bool> RunInteractiveSetupAsync()
    {
        if (!IsInteractiveTerminal())
        {
            WriteSetupError();
            return false;
        }

        WriteSetupBanner();

        // Prompt for API key
        Console.Error.Write("  Enter your SerialMemory API Key: ");
        var apiKey = ReadSecureInput();
        Console.Error.WriteLine();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("  ERROR: API key is required.");
            return false;
        }

        // Prompt for server URL
        Console.Error.WriteLine();
        Console.Error.Write($"  Server URL [{DefaultServerUrl}]: ");
        var serverUrl = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(serverUrl))
            serverUrl = DefaultServerUrl;

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
        {
            Console.Error.WriteLine("  ERROR: Invalid URL format.");
            return false;
        }

        try
        {
            await SaveAsync(apiKey, serverUrl);
            WriteSetupSuccess();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ERROR: Failed to save configuration: {ex.Message}");
            return false;
        }
    }

    private void EnsureDataDirectory()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static bool IsInteractiveTerminal()
    {
        try
        {
            _ = Console.WindowWidth;
            return !Console.IsInputRedirected;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteSetupBanner()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.Error.WriteLine("║         SerialMemory MCP Bridge - First Time Setup           ║");
        Console.Error.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        Console.Error.WriteLine();
    }

    private static void WriteSetupSuccess()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("  ✓ Configuration saved successfully!");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  The container can now run non-interactively.");
        Console.Error.WriteLine("  MCP bridge listening on port 4317.");
        Console.Error.WriteLine();
    }

    private static void WriteSetupError()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.Error.WriteLine("  ERROR: Interactive terminal required on first run");
        Console.Error.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Run with: docker run -it -v serialmemory_config:/data serialcoder/serialmemory-mcp");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  After setup, the container can run non-interactively.");
        Console.Error.WriteLine();
    }

    private static string ReadSecureInput()
    {
        var input = new StringBuilder();

        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            if (keyInfo.Key == ConsoleKey.Enter)
                break;

            if (keyInfo.Key == ConsoleKey.Backspace && input.Length > 0)
            {
                input.Remove(input.Length - 1, 1);
                Console.Error.Write("\b \b");
            }
            else if (!char.IsControl(keyInfo.KeyChar))
            {
                input.Append(keyInfo.KeyChar);
                Console.Error.Write("*");
            }
        }

        return input.ToString();
    }

    private static byte[] DeriveEncryptionKey()
    {
        var machineId = Environment.MachineName +
                       Environment.GetEnvironmentVariable("HOSTNAME") +
                       "serialmemory-mcp-bridge-v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(machineId));
    }

    private string EncryptApiKey(string apiKey)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(apiKey);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[aes.IV.Length + cipherBytes.Length];
        aes.IV.CopyTo(result, 0);
        cipherBytes.CopyTo(result, aes.IV.Length);

        return Convert.ToBase64String(result);
    }

    private string DecryptApiKey(string encryptedApiKey)
    {
        var data = Convert.FromBase64String(encryptedApiKey);

        using var aes = Aes.Create();
        aes.Key = _encryptionKey;

        var iv = new byte[16];
        var cipher = new byte[data.Length - 16];
        Array.Copy(data, 0, iv, 0, 16);
        Array.Copy(data, 16, cipher, 0, cipher.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}

/// <summary>
/// Configuration model persisted to /data/config.json.
/// </summary>
public sealed class McpClientConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("encryptedApiKey")]
    public string EncryptedApiKey { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";
}
