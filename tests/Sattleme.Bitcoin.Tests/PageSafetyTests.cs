using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Data.Sqlite;
using Moq;
using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;
using SmartstoreSattleme.Net.Codecrete.QrCodeGenerator;
using Match = System.Text.RegularExpressions.Match;

namespace Sattleme.Bitcoin.Tests;

/// <summary>
/// What the pages are made of: the QR code drawn on the server from numbers alone, the views
/// encoding what they show and loading no script but the module's file; and the tables of the
/// migration matching the records.
/// </summary>
[TestFixture]
public sealed partial class PageSafetyTests
{
    private const string Uri = "bitcoin:bc1pmzfrwwndsqmk5yh69yjr5lfgfg4ev8c0tsc06e?amount=0.000245&lightning=lnbc245u1fake";

    [Test]
    public void Draws_the_QR_code_as_an_inline_SVG_of_the_payment_URI()
    {
        var svg = QrSvg.Of(Uri, "QR code")!;
        var document = XDocument.Parse(svg);
        var code = QrCode.EncodeText(Uri, QrCode.Ecc.Medium);
        var size = code.Size + (2 * QrSvg.QuietZone);

        Assert.That(document.Root!.Name.LocalName, Is.EqualTo("svg"));
        Assert.That((string?)document.Root.Attribute("viewBox"), Is.EqualTo($"0 0 {size} {size}"));
        Assert.That((string?)document.Root.Attribute("role"), Is.EqualTo("img"));
        // Every dark module, and nothing else, is covered by the path's runs.
        var path = (string)document.Root.Elements().Single(element => element.Name.LocalName == "path").Attribute("d")!;
        var dark = new HashSet<(int, int)>();
        foreach (Match run in Runs().Matches(path))
        {
            var (x, y, width) = (int.Parse(run.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(run.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(run.Groups[3].Value, CultureInfo.InvariantCulture));
            for (var i = 0; i < width; i++)
            {
                Assert.That(dark.Add((x + i - QrSvg.QuietZone, y - QrSvg.QuietZone)), Is.True);
            }
        }
        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
            {
                Assert.That(dark.Contains((x, y)), Is.EqualTo(code.GetModule(x, y)), $"({x}, {y})");
            }
        }
    }

    [GeneratedRegex(@"M(\d+) (\d+)h(\d+)v1h-\3z")]
    private static partial Regex Runs();

    [Test]
    public void The_QR_code_holds_nothing_but_numbers_and_its_label_encoded()
    {
        var svg = QrSvg.Of(Uri, "<script>alert(\"x\")</script> & co")!;

        Assert.That(svg, Does.Not.Contain("<script"));
        Assert.That(svg, Does.Contain("aria-label=\"&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; co\""));
        Assert.That(svg, Does.Not.Contain(Uri), "the URI is drawn, never written");
        Assert.That(Regex.Replace(svg, "aria-label=\"[^\"]*\"", ""), Does.Match("^[<>a-zA-Z0-9 =\"/:.#\\-]*$"));
    }

    [Test]
    public void A_text_too_long_for_a_QR_code_gets_none()
    {
        Assert.That(QrSvg.Of(new string('a', 5000), "QR code"), Is.Null);
    }

    private static string View(params string[] path)
    {
        return File.ReadAllText(Path.Combine([Repository.Module, "Views", .. path]));
    }

    [Test]
    public void The_payment_page_loads_its_script_from_the_module_and_has_none_inline()
    {
        var page = View("SattlemePayment", "Pay.cshtml");

        Assert.That(page, Does.Not.Contain("<script"));
        Assert.That(page, Does.Contain("Assets.AppendScriptFiles(SattlemeDefaults.Content + \"pay.js\")"));
        Assert.That(page, Does.Contain("Assets.AddMetaRobots(content: \"noindex, nofollow\")"));
        Assert.That(page, Does.Contain("<noscript>").And.Contain("Model.CheckUrl"), "a button to check again without JavaScript");
        Assert.That(page, Does.Contain("Layout = \"_Layout\""), "in the shop's design");
        Assert.That(SattlemeDefaults.Content, Is.EqualTo("~/Modules/Sattleme.Bitcoin/"), "served by the shop from the module's wwwroot: no other host");
        var script = File.ReadAllText(Path.Combine(Repository.Module, "wwwroot", "pay.js"));
        Assert.That(script, Does.Not.Contain("http://").And.Not.Contain("https://").And.Not.Contain("innerHTML").And.Not.Contain("eval("));
    }

    [Test]
    public void The_views_encode_what_they_show()
    {
        // Smartstore's T() gives a LocalizedString, which writes itself HTML-encoded (LocalizedString.WriteTo).
        foreach (var file in Directory.GetFiles(Path.Combine(Repository.Module, "Views"), "*.cshtml", SearchOption.AllDirectories))
        {
            var view = File.ReadAllText(file);
            var raw = Regex.Matches(view, @"Html\.Raw\(([^)]*)\)").Select(match => match.Groups[1].Value).ToList();
            Assert.That(raw, Is.Empty.Or.EqualTo(new[] { "Model.QrSvg" }), file);
            Assert.That(view, Does.Not.Contain("<script"), file);
        }
        using var written = new StringWriter();
        new Smartstore.Core.Localization.LocalizedString("<b>&</b>").WriteTo(written, System.Text.Encodings.Web.HtmlEncoder.Default);
        Assert.That(written.ToString(), Is.EqualTo("&lt;b&gt;&amp;&lt;/b&gt;"));
    }

    /// <summary>What ASP.NET Core's form tag helper writes in a form of the module's views: its antiforgery token or not.</summary>
    private static bool WritesAntiforgeryToken(string form)
    {
        var attributes = new TagHelperAttributeList();
        foreach (Match attribute in Regex.Matches(form, "([a-z-]+)=\"([^\"]*)\""))
        {
            attributes.Add(attribute.Groups[1].Value, attribute.Groups[2].Value);
        }
        var generator = new Mock<IHtmlGenerator>();
        generator.Setup(g => g.GenerateAntiforgery(It.IsAny<ViewContext>())).Returns(new HtmlString("<input name=\"__RequestVerificationToken\" />"));
        generator.Setup(g => g.GenerateForm(It.IsAny<ViewContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<object>()))
            .Returns(new TagBuilder("form"));
        var helper = new FormTagHelper(generator.Object)
        {
            ViewContext = new ViewContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
            Method = attributes.TryGetAttribute("method", out var method) ? (string)method.Value : null,
            Action = attributes.TryGetAttribute("asp-action", out var action) ? (string)action.Value : null,
            Antiforgery = attributes.TryGetAttribute("asp-antiforgery", out var antiforgery) ? bool.Parse((string)antiforgery.Value) : null,
        };
        var html = attributes.Where(attribute => !attribute.Name.StartsWith("asp-", StringComparison.Ordinal));
        var output = new TagHelperOutput("form", [.. html], (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        helper.Process(new TagHelperContext([.. attributes], new Dictionary<object, object>(), "form"), output);
        return output.PostContent.GetContent().Contains("__RequestVerificationToken", StringComparison.Ordinal);
    }

    [Test]
    public void Every_form_that_posts_carries_its_antiforgery_token()
    {
        // The new invoice's action and the configuration's validate it (ValidateAntiForgeryToken,
        // Smartstore's AdminController): a form without it is refused (400).
        Assert.That(WritesAntiforgeryToken("<form method=\"post\" action=\"/elsewhere\">"), Is.False, "ASP.NET Core writes none for a form of its own action");
        var forms = Directory.GetFiles(Path.Combine(Repository.Module, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "<form [^>]*>").Select(match => (File: Path.GetFileName(file), Form: match.Value)))
            .ToList();
        Assert.That(forms.Select(form => form.File), Is.EquivalentTo(new[] { "Pay.cshtml", "Configure.cshtml" }));
        foreach (var (file, form) in forms)
        {
            Assert.That(WritesAntiforgeryToken(form.Replace("@Model.NewInvoiceUrl", "/sattleme/invoice/x", StringComparison.Ordinal)), Is.True, $"{file}: {form}");
        }
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static List<(string Name, bool Unique)> Indexes(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{table}\")";
        using var reader = command.ExecuteReader();
        var indexes = new List<(string, bool)>();
        while (reader.Read())
        {
            if (!reader.GetString(1).StartsWith("sqlite_", StringComparison.Ordinal))
            {
                indexes.Add((reader.GetString(1), reader.GetInt64(2) == 1));
            }
        }
        return indexes;
    }

    private static IEnumerable<string> Fields<TEntity>()
    {
        return typeof(TEntity).GetProperties()
            .Where(property => property.CanWrite && property.GetSetMethod() is not null && property.Name != "LazyLoader")
            .Select(property => property.Name);
    }

    [Test]
    public void The_migration_s_tables_hold_every_field_of_the_records_with_their_indexes()
    {
        // The tests' database has the module's tables from its migration (TestDatabase), run again
        // here: a migration that finds its tables changes nothing.
        using var database = new TestDatabase();
        TestDatabase.RunModuleMigration(database.ConnectionString);
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        Assert.That(Columns(connection, "SattlemeOrder"), Is.EquivalentTo(Fields<SattlemeOrder>()));
        Assert.That(Columns(connection, "SattlemeInvoice"), Is.EquivalentTo(Fields<SattlemeInvoice>()));
        Assert.That(Indexes(connection, "SattlemeOrder"), Is.EquivalentTo(new[] { ("IX_SattlemeOrder_OrderId", true), ("IX_SattlemeOrder_CustomerId", false) }));
        Assert.That(Indexes(connection, "SattlemeInvoice"), Is.EquivalentTo(new[]
        {
            ("IX_SattlemeInvoice_OrderId", false), ("IX_SattlemeInvoice_Reference", true), ("IX_SattlemeInvoice_Final_NextCheckOnUtc", false),
        }));
    }
}
