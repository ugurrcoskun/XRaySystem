using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace XRaySystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(XRayDbContext))]
[Migration("20260818000000_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Measurements",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                ImagePath = table.Column<string>(type: "TEXT", nullable: true),
                Profile = table.Column<int>(type: "INTEGER", nullable: false),
                IsSimulator = table.Column<bool>(type: "INTEGER", nullable: false),
                Readings_TubeCurrent = table.Column<double>(type: "REAL", nullable: false),
                Readings_Voltage = table.Column<double>(type: "REAL", nullable: false),
                Readings_XRayIntensity = table.Column<double>(type: "REAL", nullable: false),
                Readings_ExposureTime = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Measurements", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Measurements");
}
