namespace UniversalPresence.Localization;

/// <summary>
/// Texto que se publica en Discord (estado, álbum y botones). Es lo primero que ve alguien
/// que abre el perfil, así que va en el idioma configurado y por defecto en inglés.
/// </summary>
public sealed record PresenceText(
    string Paused,
    string Playing,
    string InAlbum,
    string Download,
    string HowItWorks,
    string UnknownPlayer,
    string Player)
{
    /// <summary>Inglés: el valor por defecto y el respaldo de cualquier idioma desconocido.</summary>
    public static readonly PresenceText English = new(
        "Paused",
        "Now playing",
        "From {0}",
        "Download",
        "How it works",
        "Unknown player",
        "Player");

    public static PresenceText For(string? language)
    {
        var code = (language ?? "").Trim().ToLowerInvariant();

        // zh-TW / zh-HK usan caracteres tradicionales; el resto de zh, simplificados.
        if (code.StartsWith("zh", StringComparison.Ordinal))
        {
            return code.Contains("tw", StringComparison.Ordinal)
                || code.Contains("hk", StringComparison.Ordinal)
                || code.Contains("hant", StringComparison.Ordinal)
                ? TraditionalChinese
                : SimplifiedChinese;
        }

        var primary = code.Split('-', '_')[0];

        return primary switch
        {
            "es" => new("Pausado", "Reproduciendo", "De {0}", "Descargar", "Cómo funciona",
                "Reproductor desconocido", "Reproductor"),
            "pt" => new("Pausado", "Reproduzindo", "De {0}", "Baixar", "Como funciona",
                "Reprodutor desconhecido", "Reprodutor"),
            "fr" => new("En pause", "Lecture en cours", "Extrait de {0}", "Télécharger",
                "Comment ça marche", "Lecteur inconnu", "Lecteur"),
            "de" => new("Pausiert", "Wird wiedergegeben", "Aus {0}", "Herunterladen",
                "So funktioniert's", "Unbekannter Player", "Player"),
            "it" => new("In pausa", "In riproduzione", "Da {0}", "Scarica", "Come funziona",
                "Lettore sconosciuto", "Lettore"),
            "nl" => new("Gepauzeerd", "Wordt afgespeeld", "Uit {0}", "Downloaden",
                "Hoe het werkt", "Onbekende speler", "Speler"),
            "pl" => new("Wstrzymano", "Odtwarzanie", "Z albumu {0}", "Pobierz", "Jak to działa",
                "Nieznany odtwarzacz", "Odtwarzacz"),
            "ru" => new("Пауза", "Сейчас играет", "Из альбома {0}", "Скачать",
                "Как это работает", "Неизвестный плеер", "Плеер"),
            "uk" => new("Призупинено", "Зараз відтворюється", "З альбому {0}", "Завантажити",
                "Як це працює", "Невідомий програвач", "Програвач"),
            "tr" => new("Duraklatıldı", "Şimdi çalıyor", "{0} albümünden", "İndir",
                "Nasıl çalışır", "Bilinmeyen oynatıcı", "Oynatıcı"),
            "ja" => new("一時停止中", "再生中", "「{0}」より", "ダウンロード", "使い方",
                "不明なプレーヤー", "プレーヤー"),
            "ko" => new("일시정지됨", "재생 중", "{0} 수록", "다운로드", "사용 방법",
                "알 수 없는 플레이어", "플레이어"),
            "id" => new("Dijeda", "Sedang diputar", "Dari {0}", "Unduh", "Cara kerja",
                "Pemutar tidak dikenal", "Pemutar"),
            "sv" => new("Pausad", "Spelas nu", "Från {0}", "Ladda ner", "Hur det fungerar",
                "Okänd spelare", "Spelare"),
            "cs" => new("Pozastaveno", "Přehrává se", "Z alba {0}", "Stáhnout", "Jak to funguje",
                "Neznámý přehrávač", "Přehrávač"),
            _ => English,
        };
    }

    private static readonly PresenceText SimplifiedChinese = new(
        "已暂停", "正在播放", "来自《{0}》", "下载", "工作原理", "未知播放器", "播放器");

    private static readonly PresenceText TraditionalChinese = new(
        "已暫停", "正在播放", "來自《{0}》", "下載", "運作方式", "未知播放器", "播放器");
}
