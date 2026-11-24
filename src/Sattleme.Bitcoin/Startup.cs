using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sattleme.Bitcoin.Filters;
using Sattleme.Bitcoin.Services;
using Smartstore.Core.Data;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Engine;
using Smartstore.Engine.Builders;
using Smartstore.Web.Controllers;

namespace Sattleme.Bitcoin;

/// <summary>
/// The module's services: its tables in Smartstore's database context, the SDK's clients (one per
/// integration key, for the application), the services of each request, and the filter that shows the
/// Bitcoin payment on the customer's order page.
/// </summary>
internal sealed class Startup : StarterBase
{
    /// <inheritdoc/>
    public override void ConfigureServices(IServiceCollection services, IApplicationContext appContext)
    {
        services.AddTransient<IDbContextConfigurationSource<SmartDbContext>, SmartDbContextConfigurer>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISattlemeGateway, SattlemeGateway>();
        services.AddScoped<SattlemeStore>();
        services.AddScoped<IntegrationKeys>();
        services.AddScoped<OrderNotes>();
        services.AddScoped<InvoiceMaker>();
        services.AddScoped<PaymentChecker>();
        services.AddScoped<PersonalDataShredder>();
        services.AddScoped<CustomerMessages>();
        services.AddScoped<PaymentPages>();
        services.AddScoped<PersonalDataExport>();

        services.Configure<MvcOptions>(options =>
        {
            options.Filters.AddEndpointFilter<OrderDetailsFilter, PublicController>()
                .ForController("Order")
                .ForAction("Details")
                .WhenNonAjax();
        });
    }

    /// <summary>Adds the module's entities to Smartstore's database context.</summary>
    private sealed class SmartDbContextConfigurer : IDbContextConfigurationSource<SmartDbContext>
    {
        public void Configure(IServiceProvider services, DbContextOptionsBuilder builder)
        {
            builder.UseDbFactory(factory => factory.AddModelAssembly(GetType().Assembly));
        }
    }
}
