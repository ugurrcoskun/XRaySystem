using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace XRaySystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(XRayDbContext))]
[Migration("20260824000000_AddAcquisitionProvenance")]
public sealed class AddAcquisitionProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("AcquisitionId", "Measurements", "TEXT", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.AddColumn<int>("Operation", "Measurements", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("SnapFrameMode", "Measurements", "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>("SourceWidth", "Measurements", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("SourceHeight", "Measurements", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("SourceMarker", "Measurements", "TEXT", nullable: false, defaultValue: "legacy-history");
        migrationBuilder.AddColumn<double>("RequestedParameters_VoltageKv", "Measurements", "REAL", nullable: false, defaultValue: 0.0);
        migrationBuilder.AddColumn<double>("RequestedParameters_PowerW", "Measurements", "REAL", nullable: false, defaultValue: 0.0);
        migrationBuilder.AddColumn<double>("RequestedParameters_ExposureTimeMs", "Measurements", "REAL", nullable: false, defaultValue: 0.0);
        migrationBuilder.AddColumn<int>("Readings_TubeCurrentProvenance", "Measurements", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("Readings_VoltageProvenance", "Measurements", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("Readings_ExposureTimeProvenance", "Measurements", "INTEGER", nullable: false, defaultValue: 0);

        migrationBuilder.Sql("""
            UPDATE "Measurements"
            SET "AcquisitionId" = "Id",
                "SnapFrameMode" = 0,
                "RequestedParameters_VoltageKv" = "Readings_Voltage",
                "RequestedParameters_PowerW" = "Readings_Voltage" * "Readings_TubeCurrent",
                "RequestedParameters_ExposureTimeMs" = "Readings_ExposureTime";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AcquisitionId", "Measurements");
        migrationBuilder.DropColumn("Operation", "Measurements");
        migrationBuilder.DropColumn("SnapFrameMode", "Measurements");
        migrationBuilder.DropColumn("SourceWidth", "Measurements");
        migrationBuilder.DropColumn("SourceHeight", "Measurements");
        migrationBuilder.DropColumn("SourceMarker", "Measurements");
        migrationBuilder.DropColumn("RequestedParameters_VoltageKv", "Measurements");
        migrationBuilder.DropColumn("RequestedParameters_PowerW", "Measurements");
        migrationBuilder.DropColumn("RequestedParameters_ExposureTimeMs", "Measurements");
        migrationBuilder.DropColumn("Readings_TubeCurrentProvenance", "Measurements");
        migrationBuilder.DropColumn("Readings_VoltageProvenance", "Measurements");
        migrationBuilder.DropColumn("Readings_ExposureTimeProvenance", "Measurements");
    }
}
