namespace RechercheLogement.Core.Model;

/// <summary>Libellés français des pages web et de l'historique.</summary>
public static class Labels
{
    public static string Of(PropertyStatus s) => s switch
    {
        PropertyStatus.None => "Aucun statut",
        PropertyStatus.Seen => "Vue",
        PropertyStatus.Rejected => "Pas intéressé",
        PropertyStatus.ToContact => "À contacter",
        _ => s.ToString(),
    };

    public static string Of(ContactStage s) => s switch
    {
        ContactStage.Pending => "À contacter",
        ContactStage.Contacted => "Agence contactée",
        ContactStage.VisitScheduled => "Visite prévue",
        ContactStage.Visited => "Visitée",
        ContactStage.ApplicationSent => "Dossier envoyé",
        ContactStage.Accepted => "Dossier accepté",
        ContactStage.Declined => "Refusé",
        _ => s.ToString(),
    };

    public static string Site(string site) => site switch
    {
        "bienici" => "Bien'ici",
        "seloger" => "SeLoger",
        "leboncoin" => "Leboncoin",
        "pap" => "PAP",
        "logicimmo" => "Logic-Immo",
        _ => site,
    };
}
