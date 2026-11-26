using System.Text.Json;

namespace UniversalPresence.Media;

/// <summary>
/// Deezer no usa un código HTTP para la cuota: contesta 200 con
/// <c>{"error":{"code":4}}</c>. Sin esta comprobación ese cuerpo se leería como «no hay
/// resultados», que es un fallo distinto y permanente.
/// </summary>
internal static class DeezerQuota
{
    private const int QuotaExceededCode = 4;

    /// <summary>Escrutinio de errores de Deezer; <c>code</c> viene como número o como texto.</summary>
    public static bool IsExceeded(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("error", out var error)
        && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code)
        && (code.TryGetInt32(out var number)
            ? number == QuotaExceededCode
            : code.GetString() == QuotaExceededCode.ToString());
}
