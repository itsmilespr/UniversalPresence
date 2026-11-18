namespace UniversalPresence.Infrastructure;

/// <summary>
/// Caducidad de las imágenes que la app sube a Litterbox.
///
/// Litterbox borra el archivo a las 72 h, así que una URL no puede reutilizarse para
/// siempre: si se guarda durante días, la portada y el icono del reproductor se rompen sin
/// que nada los sustituya. Estas constantes fijan una vida útil conservadora y el momento
/// de revalidar antes de seguir usando una URL.
/// </summary>
public static class RemoteAssetPolicy
{
    /// <summary>Vida real del archivo en el host (72 h en Litterbox).</summary>
    public static readonly TimeSpan HostLifetime = TimeSpan.FromHours(72);

    /// <summary>A partir de esta edad se comprueba la URL con un HEAD antes de confiar en ella.</summary>
    public static readonly TimeSpan RevalidateAfter = TimeSpan.FromHours(24);

    /// <summary>Una URL más antigua no se usa: se sube una nueva antes de que caduque.</summary>
    public static readonly TimeSpan MaxReuseAge = TimeSpan.FromHours(60);

    /// <summary>
    /// TTL de la entrada en disco. Es mayor que <see cref="MaxReuseAge"/> para que la URL
    /// antigua siga disponible como último recurso mientras se sube la nueva.
    /// </summary>
    public static TimeSpan EntryTtl => HostLifetime + TimeSpan.FromHours(12);
}
