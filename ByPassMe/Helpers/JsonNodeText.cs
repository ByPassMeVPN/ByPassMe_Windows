using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ByPassMe.Helpers;

/// <summary>
/// JsonNode → строка. Нельзя вызывать ToJsonString(new JsonSerializerOptions { WriteIndented = false }):
/// в .NET 8 это падает с «TypeInfoResolver … before being marked as read-only»
/// (так оборвалось подключение 1.1.50 после старта xray).
/// </summary>
public static class JsonNodeText
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
    };

    public static string Write(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            node.WriteTo(writer, Options);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
