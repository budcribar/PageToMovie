namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Marks feature implementations that may request inference from a configured model.
/// Model-backed operations should delegate transport and recovery to ModelExecution.
/// </summary>
public static class NamespaceMarker
{
    public static Type MarkerType => typeof(NamespaceMarker);
}
