using FluentMigrator;
using Sattleme.Bitcoin.Domain;
using Smartstore.Core.Data.Migrations;

namespace Sattleme.Bitcoin.Migrations;

/// <summary>
/// The module's two tables (Smartstore's migrations: FluentMigrator, run when the application starts
/// with the module loaded, before its installation). Uninstalling the module keeps them, as
/// Smartstore's guidelines ask ("Getting started with modules": no data deleted on uninstall): a
/// reinstalled module finds its invoices again.
/// </summary>
[MigrationVersion("2026-10-27 10:00:00", "Sattleme.Bitcoin: Initial")]
internal sealed class Initial : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        if (!Schema.Table("SattlemeOrder").Exists())
        {
            Create.Table("SattlemeOrder")
                .WithIdColumn()
                .WithColumn(nameof(SattlemeOrder.OrderId)).AsInt32().NotNullable()
                    .Unique("IX_SattlemeOrder_OrderId")
                .WithColumn(nameof(SattlemeOrder.OrderGuid)).AsGuid().NotNullable()
                .WithColumn(nameof(SattlemeOrder.StoreId)).AsInt32().NotNullable()
                .WithColumn(nameof(SattlemeOrder.CustomerId)).AsInt32().NotNullable()
                    .Indexed("IX_SattlemeOrder_CustomerId")
                .WithColumn(nameof(SattlemeOrder.PageToken)).AsString(64).NotNullable()
                .WithColumn(nameof(SattlemeOrder.LockId)).AsString(64).Nullable()
                .WithColumn(nameof(SattlemeOrder.LockedUntilUtc)).AsDateTime2().Nullable()
                .WithColumn(nameof(SattlemeOrder.CreatedOnUtc)).AsDateTime2().NotNullable();
        }

        if (!Schema.Table("SattlemeInvoice").Exists())
        {
            Create.Table("SattlemeInvoice")
                .WithIdColumn()
                .WithColumn(nameof(SattlemeInvoice.OrderId)).AsInt32().NotNullable()
                    .Indexed("IX_SattlemeInvoice_OrderId")
                .WithColumn(nameof(SattlemeInvoice.Reference)).AsString(64).NotNullable()
                    .Unique("IX_SattlemeInvoice_Reference")
                .WithColumn(nameof(SattlemeInvoice.PriceAmount)).AsString(40).NotNullable()
                .WithColumn(nameof(SattlemeInvoice.PriceCurrency)).AsString(3).NotNullable()
                .WithColumn(nameof(SattlemeInvoice.InvoiceJson)).AsMaxString().Nullable()
                .WithColumn(nameof(SattlemeInvoice.ExpiresAt)).AsInt64().NotNullable()
                .WithColumn(nameof(SattlemeInvoice.StatusJson)).AsMaxString().Nullable()
                .WithColumn(nameof(SattlemeInvoice.State)).AsString(20).Nullable()
                .WithColumn(nameof(SattlemeInvoice.Received)).AsInt64().NotNullable()
                .WithColumn(nameof(SattlemeInvoice.Final)).AsBoolean().NotNullable()
                .WithColumn(nameof(SattlemeInvoice.CompletionClaimedOnUtc)).AsDateTime2().Nullable()
                .WithColumn(nameof(SattlemeInvoice.CreatedOnUtc)).AsDateTime2().NotNullable()
                .WithColumn(nameof(SattlemeInvoice.CheckedOnUtc)).AsDateTime2().Nullable()
                .WithColumn(nameof(SattlemeInvoice.NextCheckOnUtc)).AsDateTime2().Nullable()
                .WithColumn(nameof(SattlemeInvoice.LeaseId)).AsString(64).Nullable()
                .WithColumn(nameof(SattlemeInvoice.LeaseUntilUtc)).AsDateTime2().Nullable()
                .WithColumn(nameof(SattlemeInvoice.Error)).AsString(40).NotNullable()
                .WithColumn(nameof(SattlemeInvoice.Errors)).AsInt32().NotNullable()
                .WithColumn(nameof(SattlemeInvoice.Shredded)).AsBoolean().NotNullable();

            Create.Index("IX_SattlemeInvoice_Final_NextCheckOnUtc")
                .OnTable("SattlemeInvoice")
                .OnColumn(nameof(SattlemeInvoice.Final)).Ascending()
                .OnColumn(nameof(SattlemeInvoice.NextCheckOnUtc)).Ascending();
        }
    }

    /// <inheritdoc/>
    public override void Down()
    {
        // As Smartstore's own modules: no down initial migration, the invoices stay.
    }
}
