using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalPresence.Discord;

/// <summary>Payload sent to Discord's SET_ACTIVITY command.</summary>
public sealed class DiscordActivity
{
    /// <summary>0 Playing, 2 Listening, 3 Watching, 5 Competing.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; } = 2;

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>
    /// Which field the member list shows next to "Listening to":
    /// 0 = NAME (application name), 1 = STATE, 2 = DETAILS.
    /// </summary>
    [JsonPropertyName("status_display_type")]
    public int? StatusDisplayType { get; set; }

    [JsonPropertyName("timestamps")]
    public DiscordTimestamps? Timestamps { get; set; }

    [JsonPropertyName("assets")]
    public DiscordAssets? Assets { get; set; }

    /// <summary>
    /// Botones del perfil (máximo 2). Discord exige label + url https. Son la vía por la
    /// que alguien que ve la actividad puede llegar a la app.
    /// </summary>
    [JsonPropertyName("buttons")]
    public List<DiscordButton>? Buttons { get; set; }

    /// <summary>
    /// Cuándo se construyó la actividad. No se envía a Discord: el servicio lo usa para
    /// desplazar los timestamps hasta el instante real del envío cuando hay que esperar
    /// (límite de actualizaciones), y así el reloj no queda por detrás del audio.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset BuiltAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Anclaje congelado de pausa (el par se calcula hacia el futuro para que la barra
    /// se quede en 0:00). Solo esos timestamps se desplazan al esperar por el límite de
    /// Discord; los de una pista en reproducción avanzan solos y desplazarlos retrasaría
    /// la barra justo por el tiempo de espera.
    /// </summary>
    [JsonIgnore]
    public bool FrozenTimestamps { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, DiscordJson.Options);
}

public sealed class DiscordButton
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class DiscordTimestamps
{
    [JsonPropertyName("start")]
    public long? Start { get; set; }

    [JsonPropertyName("end")]
    public long? End { get; set; }
}

public sealed class DiscordAssets
{
    [JsonPropertyName("large_image")]
    public string? LargeImage { get; set; }

    [JsonPropertyName("large_text")]
    public string? LargeText { get; set; }

    [JsonPropertyName("small_image")]
    public string? SmallImage { get; set; }

    [JsonPropertyName("small_text")]
    public string? SmallText { get; set; }
}

public static class DiscordJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public enum DiscordConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disabled,
}
