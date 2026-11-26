namespace UniversalPresence.Infrastructure;

/// <summary>
/// Escribe un archivo de texto de forma atómica: el contenido va primero a un temporal
/// junto al destino y después se mueve encima. Un cierre forzado o un corte a mitad de
/// guardado nunca puede dejar una caché o una cola truncadas.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";

        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Se intenta limpiar el temporal para no dejar basura; el error real sube.
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // best effort
            }

            throw;
        }
    }
}
