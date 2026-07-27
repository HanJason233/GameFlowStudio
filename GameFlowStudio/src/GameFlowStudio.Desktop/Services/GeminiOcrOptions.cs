using Microsoft.Extensions.Configuration;

namespace GameFlowStudio.Desktop.Services;

/// <summary>Gemini OCR 的宿主配置。密钥优先从环境变量读取，避免写入项目文件。</summary>
public sealed class GeminiOcrOptions
{
    public const string DefaultApiBaseUrl = "https://gemini.11145149.xyz/";
    public const string DefaultModel = "gemini-flash-lite-latest";
    public const string DefaultPrompt = "你当前是ocr识别助手，只输出识别到的文字，字母，数字，符号等";
    public const string DefaultEndpointPath = "v1beta/models/{model}:generateContent";

    public string ApiBaseUrl { get; init; } = DefaultApiBaseUrl;
    public string EndpointPath { get; init; } = DefaultEndpointPath;
    public string Model { get; init; } = DefaultModel;
    public string Prompt { get; init; } = DefaultPrompt;
    public string ApiKey { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public static GeminiOcrOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("GeminiOcr");
        var apiKey = FirstNonEmpty(
            section["ApiKey"],
            configuration["GEMINI_API_KEY"],
            configuration["GeminiOcr:ApiKey"]);

        return new GeminiOcrOptions
        {
            ApiBaseUrl = FirstNonEmpty(section["ApiBaseUrl"], DefaultApiBaseUrl),
            EndpointPath = FirstNonEmpty(section["EndpointPath"], DefaultEndpointPath),
            Model = FirstNonEmpty(section["Model"], DefaultModel),
            Prompt = FirstNonEmpty(section["Prompt"], DefaultPrompt),
            ApiKey = apiKey,
            Timeout = ParseTimeout(section["TimeoutSeconds"])
        };
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static TimeSpan ParseTimeout(string? value) =>
        int.TryParse(value, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300))
            : TimeSpan.FromSeconds(30);
}
