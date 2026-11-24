using System.Reflection;
using Autofac;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Sattleme.Bitcoin.Domain;
using Smartstore.Core.Common;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Core.Stores;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Engine;

namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>
/// A shop's database: Smartstore's SmartDbContext on SQLite, through Smartstore's own SQLite provider
/// (Smartstore.Data.Sqlite's SqliteDbFactory, as a shop installed with SQLite runs it), with every
/// table of Smartstore's model, then the module's tables made by the module's own migration (run by
/// FluentMigrator, as Smartstore runs a module's migrations). The store "Sunrise Bakery" (1), its
/// language English (1) and the customer 7 are in it. Each request of a test has its own
/// SmartDbContext and its own connection, as each request of a shop: the module's conditional
/// updates meet as they meet on a shop's database.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    /// <summary>The customer of the orders.</summary>
    public const int CustomerId = 7;

    /// <summary>The store.</summary>
    public const int StoreId = 1;

    /// <summary>The store's name.</summary>
    public const string StoreName = "Sunrise Bakery";

    private static readonly Lazy<string> _template = new(MakeTemplate, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly string _directory;

    /// <summary>A new database, as the template is (a copy of it).</summary>
    public TestDatabase()
    {
        _directory = NewDirectory();
        var path = Path.Combine(_directory, "shop.db");
        File.Copy(_template.Value, path);
        ConnectionString = ConnectionStringOf(path);
    }

    /// <summary>The database's connection string.</summary>
    public string ConnectionString { get; }

    /// <summary>A request's context: Smartstore's model and the module's.</summary>
    public SmartDbContext NewContext()
    {
        return new SmartDbContext(Options(ConnectionString, withModule: true));
    }

    /// <summary>The module's invoice records, oldest first, as stored now.</summary>
    public List<SattlemeInvoice> Invoices()
    {
        using var db = NewContext();
        return [.. db.SattlemeInvoices().AsNoTracking().OrderBy(invoice => invoice.Id)];
    }

    /// <summary>The module's order records, as stored now.</summary>
    public List<SattlemeOrder> Records()
    {
        using var db = NewContext();
        return [.. db.SattlemeOrders().AsNoTracking().OrderBy(order => order.Id)];
    }

    /// <summary>Changes the stored invoice record <paramref name="id"/>.</summary>
    public void Change(int id, Action<SattlemeInvoice> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var db = NewContext();
        change(db.SattlemeInvoices().Single(invoice => invoice.Id == id));
        db.SaveChanges();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The options of a SmartDbContext as Smartstore's CoreStarter makes them (UseDbFactory, the
    /// model assemblies Smartstore.Core and its provider's), with the module's model assembly as the
    /// module's Startup adds it.
    /// </summary>
    internal static DbContextOptions<SmartDbContext> Options(string connectionString, bool withModule)
    {
        var factory = SmartstoreData.Factory;
        var builder = new DbContextOptionsBuilder<SmartDbContext>();
        builder.UseDbFactory(factory, connectionString, options =>
        {
            options.AddModelAssemblies([typeof(SmartDbContext).Assembly, factory.GetType().Assembly]);
            if (withModule)
            {
                options.AddModelAssembly(typeof(SattlemeOrder).Assembly);
            }
        });
        return builder.Options;
    }

    private static string ConnectionStringOf(string path)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sattleme-smartstore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The template every test's database copies: made once per run.</summary>
    private static string MakeTemplate()
    {
        var directory = NewDirectory();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        };
        var path = Path.Combine(directory, "template.db");
        var connectionString = ConnectionStringOf(path);

        // Smartstore's tables, from its model (without the module's).
        using (var db = new SmartDbContext(Options(connectionString, withModule: false)))
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        // The module's tables, from its migration.
        RunModuleMigration(connectionString);

        using (var db = new SmartDbContext(Options(connectionString, withModule: true)))
        {
            db.Languages.Add(new Language { Id = 1, Name = "English", LanguageCulture = "en-US", UniqueSeoCode = "en", Published = true, DisplayOrder = 1 });
            db.Currencies.Add(new Currency { Id = 1, Name = "Franc", CurrencyCode = "CHF", Rate = 1, DisplayLocale = "de-CH", Published = true, CreatedOnUtc = DateTime.UtcNow, UpdatedOnUtc = DateTime.UtcNow });
            db.Stores.Add(new Store { Id = StoreId, Name = StoreName, Url = "https://bakery.example/", Hosts = "bakery.example", DisplayOrder = 1, DefaultCurrencyId = 1, PrimaryExchangeRateCurrencyId = 1 });
            db.Customers.Add(new Customer { Id = CustomerId, CustomerGuid = Guid.NewGuid(), Active = true, CreatedOnUtc = DateTime.UtcNow, LastActivityDateUtc = DateTime.UtcNow });
            db.Addresses.Add(new Address { Id = 1, FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com", City = "Lausanne", Address1 = "Rue 1", ZipPostalCode = "1000", CreatedOnUtc = DateTime.UtcNow });
            db.SaveChanges();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>
    /// Runs the module's migration (Migrations/…_Initial.cs) with FluentMigrator's SQLite processor,
    /// as Smartstore's DbMigrator runs it (Smartstore.Core's DbServiceCollectionExtensions.AddDbMigrator
    /// configures FluentMigrator with AddSQLite for a SQLite shop).
    /// </summary>
    internal static void RunModuleMigration(string connectionString)
    {
        var migrationType = typeof(SattlemeOrder).Assembly.GetTypes()
            .Single(type => typeof(FluentMigrator.IMigration).IsAssignableFrom(type) && !type.IsAbstract);
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddSQLite()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(TestDatabase).Assembly).For.Migrations())
            .BuildServiceProvider(false);
        using var scope = services.CreateScope();
        var migrationRunner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        migrationRunner.Up((FluentMigrator.IMigration)Activator.CreateInstance(migrationType, nonPublic: true)!);
    }
}

/// <summary>
/// What Smartstore's data layer needs once per process to build a SmartDbContext outside a running
/// shop: its data settings (DataSettings.Instance: the SQLite provider), as a shop that was
/// installed with SQLite has them, and an engine (EngineContext.Current) whose containers are
/// empty (no configuration overrides the database's defaults, and no hook of another module runs)
/// and whose tenant has a folder of its own.
/// Smartstore's own tests set both likewise (Smartstore.Test.Common, Smartstore.Core.Tests).
/// </summary>
internal static class SmartstoreData
{
    private static readonly Lazy<DbFactory> _factory = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Smartstore's SQLite provider.</summary>
    public static DbFactory Factory => _factory.Value;

    private static DbFactory Load()
    {
        var factoryType = Type.GetType("Smartstore.Data.Sqlite.SqliteDbFactory, Smartstore.Data.Sqlite", throwOnError: true)!;
        var factory = (DbFactory)Activator.CreateInstance(factoryType, nonPublic: true)!;
        var settings = (DataSettings)Activator.CreateInstance(typeof(DataSettings), nonPublic: true)!;
        typeof(DataSettings).GetProperty(nameof(DataSettings.DbFactory))!.SetValue(settings, factory);
        settings.ConnectionString = "Data Source=:memory:";
        typeof(DataSettings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);

        var container = new ContainerBuilder().Build();
        // The tenant's folder, where Smartstore keeps which modules are installed (ModularState).
        var tenant = Path.Combine(Path.GetTempPath(), "sattleme-smartstore-tenant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tenant);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(tenant, true);
            }
            catch (IOException)
            {
            }
        };
        var application = new Mock<IApplicationContext>();
        application.Setup(app => app.Services).Returns(container);
        application.Setup(app => app.TenantRoot).Returns(new Smartstore.IO.LocalFileSystem(tenant));
        var scopes = new Mock<ILifetimeScopeAccessor>();
        scopes.SetupProperty(accessor => accessor.LifetimeScope, null);
        var engine = new Mock<IEngine>();
        engine.Setup(e => e.Application).Returns(application.Object);
        engine.SetupProperty(e => e.Scope, new ScopedServiceContainer(scopes.Object, new HttpContextAccessor(), container));
        EngineContext.Replace(engine.Object);
        return factory;
    }
}
