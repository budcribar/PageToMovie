using System.Text.Json;
using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

[JsonConverter(typeof(StudioPathJsonConverter))]
public enum StudioPath
{
    Full = 0,
    SimpleVoice = 1
}

public sealed class StudioPathJsonConverter : JsonConverter<StudioPath>
{
    public override StudioPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            return ProjectStudioPaths.Normalize(str);
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out var val) && Enum.IsDefined(typeof(StudioPath), val))
                return (StudioPath)val;
        }
        return StudioPath.Full;
    }

    public override void Write(Utf8JsonWriter writer, StudioPath value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ProjectStudioPaths.ToSerializedString(value));
    }
}
