namespace Musilib.Domain.Enums;

/// <summary>Origine d'une playlist importée.</summary>
public enum PlaylistSource
{
    YouTube,
    Demo
}

/// <summary>Disponibilité de la vidéo source.</summary>
public enum SourceAvailability
{
    Available,
    Deleted,
    Private,
    Unavailable
}

/// <summary>État de la correspondance Spotify d'un morceau.</summary>
public enum MatchStatus
{
    /// <summary>Aucune recherche effectuée.</summary>
    Pending,
    /// <summary>Une seule correspondance fiable a été trouvée (score élevé et non ambigu).</summary>
    AutoMatched,
    /// <summary>Plusieurs résultats plausibles ou score intermédiaire : validation manuelle requise.</summary>
    Ambiguous,
    /// <summary>Aucun résultat pertinent.</summary>
    NoMatch,
    /// <summary>Correspondance choisie ou confirmée manuellement par l'utilisateur.</summary>
    Confirmed,
    /// <summary>La recherche a échoué (erreur réseau, quota...).</summary>
    Error
}

public enum JobType
{
    Import,
    Match,
    Export,
    Transfer
}

public enum JobStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled
}

public enum JobItemStatus
{
    Pending,
    Completed,
    Skipped,
    Failed
}

public enum HistoryKind
{
    Import,
    Match,
    Export,
    Transfer,
    Edit,
    Connection,
    System
}

public enum ExportOrganization
{
    None,
    Artist,
    ArtistAlbum,
    Album
}

public enum ConflictResolution
{
    /// <summary>Ignore le morceau si un fichier existe déjà à la destination.</summary>
    Skip,
    /// <summary>Remplace le fichier existant.</summary>
    Overwrite,
    /// <summary>Conserve les deux fichiers en suffixant le nouveau nom ("(2)", "(3)"...).</summary>
    KeepBoth
}

public enum MetadataOrigin
{
    Source,
    Spotify,
    Manual
}
