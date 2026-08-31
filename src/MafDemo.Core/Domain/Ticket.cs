using System.Text.Json;
using System.Text.Json.Serialization;

namespace MafDemo.Core.Domain;

public enum TicketStatus { Open, InProgress, Resolved, Closed }

/// <summary>
/// Durable-workflow round-trip: Microsoft.Agents.AI.DurableTask serializes
/// workflow payloads with JsonStringEnumConverter on one hop (envelope) but
/// deserializes them with a converter-less options set on the next (activity
/// input), so a plain enum dies with "The JSON value could not be converted".
/// This converter writes plain numbers (keeps the FileTicketStore file format
/// unchanged) and accepts both numbers and strings on read.
/// </summary>
[JsonConverter(typeof(NumericJsonEnumConverterFactory<TicketPriority>))]
public enum TicketPriority { Low, Normal, High, Critical }

public record Ticket(Guid Id, string Title, string Description,
    TicketPriority Priority, TicketStatus Status, string? Assignee,
    DateTimeOffset CreatedAt, IReadOnlyList<string> Notes);

/// <summary>Enum converter that writes numbers and reads numbers or names.</summary>
file sealed class NumericJsonEnumConverterFactory<T> : JsonConverterFactory
    where T : struct, Enum
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(T);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        new NumericJsonEnumConverter<T>();
}

file sealed class NumericJsonEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? Enum.Parse<T>(reader.GetString()!)
            : (T)Enum.ToObject(typeof(T), reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(Convert.ToInt64(value));
}
