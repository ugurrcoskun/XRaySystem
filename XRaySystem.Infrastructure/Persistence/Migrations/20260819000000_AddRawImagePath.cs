using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace XRaySystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(XRayDbContext))]
[Migration("20260819000000_AddRawImagePath")]
public sealed class AddRawImagePath : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "RawImagePath",
            table: "Measurements",
            type: "TEXT",
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "RawImagePath",
            table: "Measurements");
}
