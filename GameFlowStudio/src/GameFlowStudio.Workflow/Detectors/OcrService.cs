using GameFlowStudio.Contracts.Results;
using GameFlowStudio.Contracts.Services;

namespace GameFlowStudio.Workflow.Detectors;

/// <summary>
/// 默认 OCR 服务。核心库不绑定具体 OCR 引擎，宿主可注册云端或其它实现覆盖此服务。
/// </summary>
public sealed class UnsupportedOcrService : IOcrService
{
    public Task<OcrRecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("当前未配置 OCR 引擎，请在宿主中注册 IOcrService。");
    }
}
