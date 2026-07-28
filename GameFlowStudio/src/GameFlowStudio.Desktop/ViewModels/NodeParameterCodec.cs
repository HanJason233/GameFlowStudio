using System.Globalization;
using System.Text.Json;
using GameFlowStudio.Contracts.Workflows;

namespace GameFlowStudio.Desktop.ViewModels;

internal static class NodeParameterCodec
{
    public static bool TryParse(
        IReadOnlyList<NodeParameterItem> items,
        out Dictionary<string, JsonElement> parameters,
        out string error)
    {
        parameters = new(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!TryParseValue(item, out var value))
            {
                error = $"参数“{item.Name}”格式无效，请检查后再应用。";
                return false;
            }

            parameters[item.Name] = value;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseValue(NodeParameterItem item, out JsonElement value)
    {
        switch (item.Kind)
        {
            case JsonValueKind.Number when IsDecimalParameter(item.Name) && double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue):
                value = ProjectJson.Element(decimalValue);
                return true;
            case JsonValueKind.Number when int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue):
                value = ProjectJson.Element(integerValue);
                return true;
            case JsonValueKind.True or JsonValueKind.False when bool.TryParse(item.Value, out var booleanValue):
                value = ProjectJson.Element(booleanValue);
                return true;
            case JsonValueKind.Array or JsonValueKind.Object:
                try
                {
                    value = ParseJson(item.Value);
                    return true;
                }
                catch (JsonException)
                {
                    break;
                }
            default:
                value = ProjectJson.Element(item.Value);
                return true;
        }

        value = default;
        return false;
    }

    private static bool IsDecimalParameter(string name) => name is "minMatchRatio" or "matchThreshold";

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
