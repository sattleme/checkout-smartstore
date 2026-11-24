using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Sattleme.Bitcoin.Services;
using Smartstore.Engine;
using Smartstore.Engine.Builders;

namespace Sattleme.E2e;

/// <summary>
/// Registers the fake after the payment module's own services, so that it is the one resolved.
/// The payment module's SDK (Sattleme.Bitcoin.Sdk) lives in that module's own load context
/// (Smartstore's ModuleAssemblyLoadContext): this module, which implements the SDK's types, takes
/// that very assembly, never a copy of its own, through the default context's Resolving event.
/// </summary>
internal sealed class Startup : StarterBase
{
    private const string Sdk = "Sattleme.Bitcoin.Sdk";

    [ThreadStatic]
    private static bool _resolving;

    public Startup()
    {
        RunAfter("Sattleme.Bitcoin.Startup");
    }

    public override int Order => (int)StarterOrdering.Default + 1;

    public override void ConfigureServices(IServiceCollection services, IApplicationContext appContext)
    {
        AssemblyLoadContext.Default.Resolving += ResolveSdk;
        Register(services);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Register(IServiceCollection services)
    {
        services.AddSingleton<E2eGateway>();
        services.AddSingleton<ISattlemeGateway>(provider => provider.GetRequiredService<E2eGateway>());
    }

    private static Assembly? ResolveSdk(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name != Sdk || _resolving)
        {
            return null;
        }
        var loaded = AssemblyLoadContext.All.SelectMany(each => each.Assemblies).FirstOrDefault(assembly => assembly.GetName().Name == Sdk);
        if (loaded is not null)
        {
            return loaded;
        }
        // Not loaded yet: the payment module's own assembly asks for it, which Smartstore resolves
        // into that module's context.
        _resolving = true;
        try
        {
            _ = typeof(ISattlemeClient).GetMethods().Select(method => method.ReturnType.GetGenericArguments().FirstOrDefault()).ToList();
        }
        finally
        {
            _resolving = false;
        }
        return AssemblyLoadContext.All.SelectMany(each => each.Assemblies).FirstOrDefault(assembly => assembly.GetName().Name == Sdk);
    }
}
