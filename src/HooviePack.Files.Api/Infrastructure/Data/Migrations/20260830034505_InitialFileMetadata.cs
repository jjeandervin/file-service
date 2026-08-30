using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HooviePack.Files.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialFileMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "files");

            migrationBuilder.CreateTable(
                name: "Files",
                schema: "files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeclaredSize = table.Column<long>(type: "bigint", nullable: false),
                    ActualSize = table.Column<long>(type: "bigint", nullable: true),
                    UploadTokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    LegacySourcePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Files_LegacySourcePath",
                schema: "files",
                table: "Files",
                column: "LegacySourcePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_StorageKey",
                schema: "files",
                table: "Files",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Files",
                schema: "files");
        }
    }
}
