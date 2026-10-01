#!/usr/bin/env python3
"""After `dotnet ef migrations add Schema` and `... add Triggers`, replace the scaffolded Up/Down bodies so the
migrations execute db/schema.sql verbatim (see docs/QUESTIONS.md Q-001). Usage: python3 tools/patch_migrations.py"""
import re, glob, pathlib
d = pathlib.Path(__file__).resolve().parent.parent / "src/ReleaseMgmt.Infrastructure/Migrations"
NS = "ReleaseMgmt.Infrastructure.Migrations"
BODY = {
 "Schema": ("Creates the 48 tables and 21 indexes by executing db/schema.sql's DDL verbatim (the contract, CLAUDE.md rule 1).\n    /// The EF model (ModelMap) is asserted against the result by SchemaContractTests. Forward-only.", "SchemaSql.TablesAndIndexes()"),
 "Triggers": ("Applies all 42 triggers from db/schema.sql verbatim and in file order (same-event triggers fire newest-first,\n    /// so order decides which rule's error a caller sees). A migration that rebuilds a table must re-apply that table's triggers;\n    /// SchemaContractTests fails if any trigger name is missing.", "SchemaSql.Triggers()"),
}
for name, (doc, src) in BODY.items():
    f = glob.glob(str(d / f"*_{name}.cs"))
    f = [x for x in f if not x.endswith(".Designer.cs")][0]
    pathlib.Path(f).write_text(f'''using Microsoft.EntityFrameworkCore.Migrations;
using ReleaseMgmt.Infrastructure.Persistence;

#nullable disable

namespace {NS}
{{
    /// <summary>{doc}</summary>
    public partial class {name} : Migration
    {{
        protected override void Up(MigrationBuilder migrationBuilder)
        {{
            foreach (var statement in {src}) migrationBuilder.Sql(statement);
        }}

        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Migrations are forward-only; restore from backup (D3).");
    }}
}}
''')
print("patched", list(BODY))
