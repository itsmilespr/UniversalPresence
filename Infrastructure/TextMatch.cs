using System.Globalization;
using System.Text;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Normalización y emparejamiento de texto compartidos.
///
/// Existe porque el mismo criterio lo necesitan tres sitios distintos: la búsqueda de
/// portada (comparar lo que reporta el reproductor con el catálogo), las portadas propias
/// por canción (clave estable para «artista + título») y el autotest. Tenerlo en un solo
/// lugar evita que una corrección (los acentos precompuestos que entrega media_kit, por
/// ejemplo) se aplique en un sitio y no en otro.
/// </summary>
public static class TextMatch
{
    /// <summary>Minúsculas, sin acentos, sin signos y con espacios colapsados.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        string decomposed;
        try
        {
            decomposed = value.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Una etiqueta con Unicode inválido (un surrogate suelto, por ejemplo) no se
            // puede descomponer. Se compara el texto tal cual en lugar de romper la pista.
            decomposed = value;
        }

        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var mapped = StripAccent(character);
            builder.Append(char.IsLetterOrDigit(mapped) ? char.ToLowerInvariant(mapped) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Mapeo explícito de las letras acentuadas. <c>NormalizationForm.FormD</c> no siempre
    /// descompone los caracteres precompuestos según el entorno (algunas integraciones de
    /// SMTC los entregan como un único code point), así que se cubren a mano.
    /// </summary>
    public static char StripAccent(char c) => c switch
    {
        'Á' or 'À' or 'Â' or 'Ã' or 'Ä' or 'Å' => 'A',
        'á' or 'à' or 'â' or 'ã' or 'ä' or 'å' => 'a',
        'É' or 'È' or 'Ê' or 'Ë' => 'E',
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'Í' or 'Ì' or 'Î' or 'Ï' => 'I',
        'í' or 'ì' or 'î' or 'ï' => 'i',
        'Ó' or 'Ò' or 'Ô' or 'Õ' or 'Ö' or 'Ø' => 'O',
        'ó' or 'ò' or 'ô' or 'õ' or 'ö' or 'ø' => 'o',
        'Ú' or 'Ù' or 'Û' or 'Ü' => 'U',
        'ú' or 'ù' or 'û' or 'ü' => 'u',
        'Ñ' => 'N',
        'ñ' => 'n',
        'Ç' => 'C',
        'ç' => 'c',
        'Ý' or 'Ÿ' => 'Y',
        'ý' or 'ÿ' => 'y',
        _ => c,
    };

    /// <summary>
    /// True cuando dos títulos/artistas se refieren a lo mismo. Acepta coincidencia exacta,
    /// contención (ediciones con sufijo) y al menos dos palabras significativas en común.
    /// </summary>
    public static bool Similar(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        // Contención por palabras completas, no por letras: «someone» no debe coincidir
        // con «one», pero «himno remastered» sí con «himno».
        if (string.Equals(a, b, StringComparison.Ordinal)
            || ContainsWords(a, b)
            || ContainsWords(b, a))
        {
            return true;
        }

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .ToHashSet();
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .ToHashSet();

        return tokensA.Intersect(tokensB).Count() >= 2;
    }

    private static bool ContainsWords(string haystack, string needle) =>
        $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);

    /// <summary>
    /// Clave estable para una canción: «artista normalizado + separador + título normalizado».
    /// El separador es un carácter de control, así que no puede aparecer en un nombre real.
    /// </summary>
    public static string TrackKey(string? artist, string? title) =>
        $"{Normalize(artist)}\u001F{Normalize(title)}";

    /// <summary>Clave solo por título (portada válida para cualquier artista).</summary>
    public static string TitleKey(string? title) => Normalize(title);

    /// <summary>Texto legible «Título — Artista» para listas de la interfaz.</summary>
    public static string TrackDisplay(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        return string.IsNullOrWhiteSpace(artist) ? title.Trim() : $"{title.Trim()} — {artist.Trim()}";
    }
}
