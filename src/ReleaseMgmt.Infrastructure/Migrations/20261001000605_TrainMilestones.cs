using Microsoft.EntityFrameworkCore.Migrations;
using ReleaseMgmt.Infrastructure.Persistence;

#nullable disable

namespace ReleaseMgmt.Infrastructure.Migrations
{
    /// <summary>Adds TrainMilestones and IX_Milestones_Train to a database created before them (Q-0840). The statements are db/schema.sql's DDL verbatim with
    /// IF NOT EXISTS added, so on a fresh database (where migration Schema already ran the whole of schema.sql) this is a no-op, and on an existing one it creates
    /// exactly the contract text (SQLite stores the statement without IF NOT EXISTS; SchemaContractTests compares it). No triggers are involved. Forward-only.</summary>
    public partial class TrainMilestones : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in SchemaSql.IfNotExists("TrainMilestones", "IX_Milestones_Train")) migrationBuilder.Sql(statement);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Migrations are forward-only; restore from backup (D3).");
    }
}
