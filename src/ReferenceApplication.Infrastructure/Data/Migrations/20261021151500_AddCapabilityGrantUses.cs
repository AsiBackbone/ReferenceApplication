using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReferenceApplication.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCapabilityGrantUses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AsiBackboneCapabilityGrantUses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastUsedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RetainUntilUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneCapabilityGrantUses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneCapabilityGrantUses_Issuer_TokenId",
                table: "AsiBackboneCapabilityGrantUses",
                columns: new[] { "Issuer", "TokenId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneCapabilityGrantUses_RetainUntilUtc",
                table: "AsiBackboneCapabilityGrantUses",
                column: "RetainUntilUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsiBackboneCapabilityGrantUses");
        }
    }
}
