using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace XRaySystem.Infrastructure.Persistence.Migrations;

[DbContext(typeof(XRayDbContext))]
public sealed class XRayDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "8.0.0");

        modelBuilder.Entity("XRaySystem.Domain.Entities.XRayMeasurement", entity =>
        {
            entity.Property<Guid>("AcquisitionId").HasColumnType("TEXT");
            entity.Property<Guid>("Id").HasColumnType("TEXT");
            entity.Property<string>("ImagePath").HasColumnType("TEXT");
            entity.Property<bool>("IsSimulator").HasColumnType("INTEGER");
            entity.Property<int>("Operation").HasColumnType("INTEGER");
            entity.Property<int>("Profile").HasColumnType("INTEGER");
            entity.Property<string>("RawImagePath").HasColumnType("TEXT");
            entity.Property<int?>("SnapFrameMode").HasColumnType("INTEGER");
            entity.Property<int>("SourceHeight").HasColumnType("INTEGER");
            entity.Property<string>("SourceMarker").IsRequired().HasColumnType("TEXT");
            entity.Property<int>("SourceWidth").HasColumnType("INTEGER");
            entity.Property<DateTime>("Timestamp").HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.ToTable("Measurements");

            entity.OwnsOne("XRaySystem.Domain.ValueObjects.SensorReadings", "Readings", owned =>
            {
                owned.Property<Guid>("XRayMeasurementId").HasColumnType("TEXT");
                owned.Property<double>("ExposureTime")
                    .HasColumnType("REAL").HasColumnName("Readings_ExposureTime");
                owned.Property<int>("ExposureTimeProvenance")
                    .HasColumnType("INTEGER").HasColumnName("Readings_ExposureTimeProvenance");
                owned.Property<double>("TubeCurrent")
                    .HasColumnType("REAL").HasColumnName("Readings_TubeCurrent");
                owned.Property<int>("TubeCurrentProvenance")
                    .HasColumnType("INTEGER").HasColumnName("Readings_TubeCurrentProvenance");
                owned.Property<double>("Voltage")
                    .HasColumnType("REAL").HasColumnName("Readings_Voltage");
                owned.Property<int>("VoltageProvenance")
                    .HasColumnType("INTEGER").HasColumnName("Readings_VoltageProvenance");
                owned.Property<double>("XRayIntensity")
                    .HasColumnType("REAL").HasColumnName("Readings_XRayIntensity");
                owned.HasKey("XRayMeasurementId");
                owned.ToTable("Measurements");
                owned.WithOwner().HasForeignKey("XRayMeasurementId");
            });

            entity.Navigation("Readings").IsRequired();

            entity.OwnsOne("XRaySystem.Domain.ValueObjects.AcquisitionParameters", "RequestedParameters", owned =>
            {
                owned.Property<Guid>("XRayMeasurementId").HasColumnType("TEXT");
                owned.Property<double>("ExposureTimeMs")
                    .HasColumnType("REAL").HasColumnName("RequestedParameters_ExposureTimeMs");
                owned.Property<double>("PowerW")
                    .HasColumnType("REAL").HasColumnName("RequestedParameters_PowerW");
                owned.Property<double>("VoltageKv")
                    .HasColumnType("REAL").HasColumnName("RequestedParameters_VoltageKv");
                owned.HasKey("XRayMeasurementId");
                owned.ToTable("Measurements");
                owned.WithOwner().HasForeignKey("XRayMeasurementId");
            });

            entity.Navigation("RequestedParameters").IsRequired();
        });
#pragma warning restore 612, 618
    }
}
