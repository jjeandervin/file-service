using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileService.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BucketName",
                schema: "files",
                table: "Files",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                schema: "files",
                table: "Files",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "hooviepack");

            migrationBuilder.CreateIndex(
                name: "IX_Files_ClientId_Status_CreatedAt",
                schema: "files",
                table: "Files",
                columns: new[] { "ClientId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_ClientId_Status_CreatedAt",
                schema: "files",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "BucketName",
                schema: "files",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ClientId",
                schema: "files",
                table: "Files");
        }
    }
}
