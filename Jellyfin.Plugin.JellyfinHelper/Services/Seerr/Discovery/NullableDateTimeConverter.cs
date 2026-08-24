using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Custom JSON converter for <c>DateTime?</c> that gracefully handles
///     empty strings returned by TMDb/Seerr for missing date fields.
///     Without this converter, System.Text.Json throws <see cref="JsonException"/>
///     when encountering an empty string (<c>""</c>) for a nullable DateTime property,
///     which causes the entire discovery page response to be discarded.
/// </summary>
internal sealed class NullableDateTimeConverter : JsonConverter<DateTime?>
{
    /// <inheritdoc />
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // DateTimeStyles.RoundtripKind preserves the Kind marker from the wire:
            //   "...Z"       -> Kind=Utc
            //   "...+02:00"  -> Kind=Local (or Utc if the offset is zero)
            //   plain "YYYY-MM-DD" -> Kind=Unspecified
            // Without this flag, DateTime.TryParse silently converts UTC input to Local time,
            // which shifts .Ticks by the machine's UTC offset and breaks downstream code that
            // compares against DateTime.UtcNow or persists timestamps.
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }

            // Unrecognized format - treat as missing rather than throwing
            return null;
        }

        // Unexpected token type - consume the token to satisfy the JsonConverter contract
        reader.Skip();
        return null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}