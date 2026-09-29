using Microsoft.EntityFrameworkCore.Migrations;
using ReleaseMgmt.Infrastructure.Persistence;

#nullable disable

namespace ReleaseMgmt.Infrastructure.Migrations
{
    /// <summary>Applies all 42 triggers from db/schema.sql verbatim and in file order (same-event triggers fire newest-first,
    /// so order decides which rule's error a caller sees). A migration that rebuilds a table must re-apply that table's triggers;
    /// SchemaContractTests fails if any trigger name is missing.</summary>
    public partial class Triggers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in SchemaSql.Triggers()) migrationBuilder.Sql(statement);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Migrations are forward-only; restore from backup (D3).");
    }
}
