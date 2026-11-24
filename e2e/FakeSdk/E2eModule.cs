using Smartstore.Engine.Modularity;

namespace Sattleme.E2e;

/// <summary>The test-only module: installed, it replaces the payment module's SDK (Startup).</summary>
internal sealed class E2eModule : ModuleBase
{
}
