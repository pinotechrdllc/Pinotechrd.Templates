using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Probe.WithEndpoint;

public sealed record CreateOrder([property: Required] string Customer, [property: Range(1, 10)] int Quantity);

// Calls AddValidation and maps one endpoint that binds a type with data annotations.
public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services) => services.AddValidation();

    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders", (CreateOrder order) => Results.Ok(order));
        return endpoints;
    }
}
