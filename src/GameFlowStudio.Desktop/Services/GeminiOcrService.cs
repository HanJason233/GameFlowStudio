using System.IO;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace GameFlowStudio.Desktop.Services;

/// <summary>通过 Gemini generateContent 接口识别桌面截图中的文字。</summary>
public sealed class GeminiOcrService(
    HttpClient httpClient,
    GeminiOcrOptions options,
    ISettingsStorageService settingsStorage,
    ILogger<GeminiOcrService> logger) : IOcrService, ITargetAwareOcrService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) =>
        RecognizeCoreAsync(frame, options.Prompt, cancellationToken);

    public Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, string targetText, CancellationToken cancellationToken) =>
        RecognizeCoreAsync(frame, options.Prompt, cancellationToken);

    private async Task<OcrRecognitionResult> RecognizeCoreAsync(
        CapturedFrame frame,
        string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!frame.IsValid()) throw new ArgumentException("OCR 截图区域无效。", nameof(frame));
        var settings = await settingsStorage.LoadAsync(cancellationToken).ConfigureAwait(false);
        var apiKey = string.IsNullOrWhiteSpace(settings.GeminiApiKey) ? options.ApiKey : settings.GeminiApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("未配置 Gemini OCR API 密钥，请在全局设置中填写，或设置环境变量 GEMINI_API_KEY。");

        var endpoint = BuildEndpoint();
        var image = ConvertFrameToPng(frame);
        var requestBody = new GeminiGenerateContentRequest
        {
            Contents =
            [
                new GeminiContent
                {
                    Role = "user",
                    Parts =
                    [
                        new GeminiPart { Text = prompt },
                        new GeminiPart
                        {
                            InlineData = new GeminiInlineData
                            {
                                MimeType = "image/png",
                                Data = Convert.ToBase64String(image)
                            }
                        }
                    ]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig { Temperature = 0, ResponseMimeType = "text/plain" }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TrimForLog(responseBody);
            logger.LogWarning("Gemini OCR 请求失败，HTTP {StatusCode}: {Detail}", (int)response.StatusCode, detail);
            throw new InvalidOperationException($"Gemini OCR 请求失败（HTTP {(int)response.StatusCode}）：{detail}");
        }

        var text = ReadText(responseBody);
        logger.LogDebug("Gemini OCR 返回 {Length} 个字符。", text.Length);
        return new OcrRecognitionResult(text);
    }

    private Uri BuildEndpoint()
    {
        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Gemini OCR API 地址必须是有效的 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(options.Model)) throw new InvalidOperationException("Gemini OCR 模型不能为空。");
        if (string.IsNullOrWhiteSpace(options.EndpointPath)) throw new InvalidOperationException("Gemini OCR 接口路径不能为空。");

        var path = options.EndpointPath.Replace("{model}", Uri.EscapeDataString(options.Model), StringComparison.Ordinal);
        var builder = new UriBuilder(baseUri)
        {
            Path = $"{baseUri.AbsolutePath.TrimEnd('/')}/{path.TrimStart('/')}",
            Query = string.Empty
        };
        return builder.Uri;
    }

    private static byte[] ConvertFrameToPng(CapturedFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra32, frame.Width * 4);
        bitmap.Freeze();
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string ReadText(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var text = document.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")
                .EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToArray();
            if (text.Length == 0) throw new InvalidOperationException("Gemini OCR 返回了空文本。");
            return string.Join("\n", text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Gemini OCR 返回了无法解析的响应。", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException("Gemini OCR 响应中缺少识别结果。", exception);
        }
        catch (IndexOutOfRangeException exception)
        {
            throw new InvalidOperationException("Gemini OCR 响应中没有候选结果。", exception);
        }
    }

    private static string TrimForLog(string value) =>
        string.IsNullOrWhiteSpace(value) ? "无响应内容" : value.Length <= 500 ? value : value[..500] + "…";
}

internal static class CapturedFrameExtensions
{
    public static bool IsValid(this CapturedFrame frame) =>
        frame.Width > 0 && frame.Height > 0 && frame.Bgra32.Length >= frame.Width * frame.Height * 4;
}

internal sealed class GeminiGenerateContentRequest
{
    public required GeminiContent[] Contents { get; init; }
    public GeminiGenerationConfig? GenerationConfig { get; init; }
}

internal sealed class GeminiContent
{
    public string? Role { get; init; }
    public required GeminiPart[] Parts { get; init; }
}

internal sealed class GeminiPart
{
    public string? Text { get; init; }

    [JsonPropertyName("inline_data")]
    public GeminiInlineData? InlineData { get; init; }
}

internal sealed class GeminiInlineData
{
    [JsonPropertyName("mime_type")]
    public required string MimeType { get; init; }
    public required string Data { get; init; }
}

internal sealed class GeminiGenerationConfig
{
    public double Temperature { get; init; }
    public string? ResponseMimeType { get; init; }
}
