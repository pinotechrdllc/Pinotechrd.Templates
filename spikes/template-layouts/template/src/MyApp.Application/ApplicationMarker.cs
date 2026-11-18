using MyApp.Domain;

namespace MyApp.Application;

public static class ApplicationMarker
{
    public const string Layer = "application+" + DomainMarker.Layer;
}
