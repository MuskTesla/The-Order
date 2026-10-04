using System.Globalization;
using TheOrder.Application;

namespace TheOrder.Presentation;

public sealed class PersonalDraft : ObservableState
{
    public GameDetails Source { get; }
    private string name, status, rating, notes, categories, tags;
    private bool favorite;
    public PersonalDraft(GameDetails source)
    {
        Source = source; var p = source.Personal;
        name = p.Name; status = p.Status; rating = p.Rating?.ToString(CultureInfo.CurrentCulture) ?? "";
        notes = p.Notes; categories = string.Join(", ", p.Categories); tags = string.Join(", ", p.Tags); favorite = p.Favorite;
    }
    public string Name { get => name; set { if (Set(ref name, value)) Changed(nameof(IsDirty)); } }
    public string Status { get => status; set { if (Set(ref status, value)) Changed(nameof(IsDirty)); } }
    public string Rating { get => rating; set { if (Set(ref rating, value)) Changed(nameof(IsDirty)); } }
    public string Notes { get => notes; set { if (Set(ref notes, value)) Changed(nameof(IsDirty)); } }
    public string Categories { get => categories; set { if (Set(ref categories, value)) Changed(nameof(IsDirty)); } }
    public string Tags { get => tags; set { if (Set(ref tags, value)) Changed(nameof(IsDirty)); } }
    public bool Favorite { get => favorite; set { if (Set(ref favorite, value)) Changed(nameof(IsDirty)); } }
    public bool IsDirty => Name != Source.Personal.Name || Status != Source.Personal.Status || Rating != (Source.Personal.Rating?.ToString(CultureInfo.CurrentCulture) ?? "")
        || Notes != Source.Personal.Notes || Categories != string.Join(", ", Source.Personal.Categories) || Tags != string.Join(", ", Source.Personal.Tags) || Favorite != Source.Personal.Favorite;
    public PersonalInput Input => new(Name, Status, Rating, Favorite, Notes, Categories, Tags);
}
