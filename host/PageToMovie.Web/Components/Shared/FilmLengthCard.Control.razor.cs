using Microsoft.AspNetCore.Components;

namespace PageToMovie.Web.Components;

public partial class FilmLengthCard_Control
{
    [CascadingParameter] public FilmLengthCard Host { get; set; } = default;
}
