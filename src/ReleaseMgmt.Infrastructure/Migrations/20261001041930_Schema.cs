using Microsoft.EntityFrameworkCore.Migrations;
using ReleaseMgmt.Infrastructure.Persistence;

#nullable disable

namespace ReleaseMgmt.Infrastructure.Migrations
{
    /// <summary>Creates the 49 tables and 23 indexes by executing db/schema.sql's DDL verbatim (the contract, CLAUDE.md rule 1).
    /// The EF model (ModelMap) is asserted against the result by SchemaContractTests. Forward-only.</summary>
    public partial class Schema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in SchemaSql.TablesAndIndexes()) migrationBuilder.Sql(statement);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Migrations are forward-only; restore from backup (D3).");
    }
}
